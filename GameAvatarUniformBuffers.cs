using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Render;
using HelixToolkit.SharpDX.Utilities;

namespace SkyCharacterStudio;

/// <summary>
/// Creates zero-initialized, correctly sized per-stage Avatar constant-buffer
/// payloads and writes reflected fields by their .ref offsets. The caller owns
/// the meaning and lifetime of the values; no guessed game lighting is baked
/// into this low-level layout helper.
/// </summary>
internal static class GameAvatarUniformBuffers
{
    public static IReadOnlyList<GameAvatarUniformBuffer> Create(
        GameAvatarShaderProgram program,
        IReadOnlyDictionary<string, Vector4>? values = null,
        IReadOnlyDictionary<string, Matrix4x4>? matrices = null)
    {
        var result = new List<GameAvatarUniformBuffer>();
        foreach (var block in program.UniformBlocks)
        {
            var binding = program.Bindings.FirstOrDefault(item =>
                item.ResourceType.Equals("constant buffer", StringComparison.OrdinalIgnoreCase)
                && item.Stage.Equals(block.Stage, StringComparison.OrdinalIgnoreCase)
                && item.Slot == block.Register);
            if (binding is null)
                throw new InvalidDataException($"No DXBC constant-buffer binding found for {block.Stage} {block.Name} at b{block.Register}.");

            var bytes = new byte[binding.ByteSize];
            foreach (var field in block.Fields)
            {
                if (field.Offset < 0 || field.Offset + field.Size > bytes.Length)
                    throw new InvalidDataException($"Reflected field {block.Stage}.{field.Name} exceeds its DXBC buffer ({field.Offset}+{field.Size}>{bytes.Length}).");
                if (field.Type == 0x23 && matrices is not null && matrices.TryGetValue(field.Name, out var matrix))
                    WriteMatrix(bytes, field, matrix);
                else if (field.Type != 0x23 && values is not null && values.TryGetValue(field.Name, out var value))
                    WriteField(bytes, field, value);
            }
            result.Add(new GameAvatarUniformBuffer(block.Name, block.Stage, block.Register, bytes));
        }
        return result;
    }

    private static void WriteField(byte[] target, GameShaderUniformField field, Vector4 value)
    {
        var components = field.Type switch
        {
            1 or 9 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            _ => throw new ArgumentException($"Field {field.Name} is not a scalar/vector field.", nameof(field)),
        };
        Span<float> vector = stackalloc float[] { value.X, value.Y, value.Z, value.W };
        var byteCount = components * sizeof(float);
        for (var i = 0; i < Math.Max(1, field.ArrayCount); i++)
        {
            var offset = field.Offset + i * byteCount;
            for (var component = 0; component < components; component++)
            {
                var encoded = field.Type == 9
                    ? unchecked((int)Math.Max(0, vector[component]))
                    : BitConverter.SingleToInt32Bits(vector[component]);
                BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset + component * sizeof(float), sizeof(float)), encoded);
            }
        }
    }

    private static void WriteMatrix(byte[] target, GameShaderUniformField field, Matrix4x4 matrix)
    {
        if (field.ArrayCount != 1 || field.Size < 64)
            throw new ArgumentException($"Matrix arrays are not currently supported for {field.Name}.", nameof(field));
        Span<float> values = stackalloc float[]
        {
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        };
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(field.Offset + i * sizeof(float), sizeof(float)), BitConverter.SingleToInt32Bits(values[i]));
    }
}

internal sealed record GameAvatarUniformBuffer(string Name, string Stage, int Register, byte[] Data);

/// <summary>
/// Registers each reflected stage buffer with Helix by the DXBC resource name.
/// ShaderPass binds these pooled buffers from the shader reflection mappings;
/// this object only updates their contents for the current draw.
/// </summary>
internal sealed class GameAvatarConstantBufferSet
{
    private readonly IReadOnlyList<(GameAvatarUniformBuffer Source, ConstantBufferProxy Proxy)> _buffers;
    private readonly object _sync = new();

    public GameAvatarConstantBufferSet(IEffectsManager effectsManager, IReadOnlyList<GameAvatarUniformBuffer> buffers)
    {
        var pool = effectsManager.ConstantBufferPool
            ?? throw new InvalidOperationException("Helix constant-buffer pool is not initialized.");
        var device = effectsManager.Device
            ?? throw new InvalidOperationException("Helix Direct3D device is not initialized.");

        _buffers = buffers.Select(buffer =>
        {
            var proxy = pool.Register(buffer.Name, buffer.Data.Length)
                ?? throw new InvalidOperationException($"Helix could not register constant buffer {buffer.Name}.");
            if (!proxy.Initialized)
                proxy.CreateBuffer(device);
            return (buffer, proxy);
        }).ToArray();
    }

    public void Upload(DeviceContextProxy context)
    {
        lock (_sync)
            foreach (var (source, proxy) in _buffers)
                proxy.UploadDataToBuffer(context, source.Data, source.Data.Length);
    }

    public void Update(IReadOnlyList<GameAvatarUniformBuffer> buffers)
    {
        lock (_sync)
        {
            foreach (var (source, _) in _buffers)
            {
                var updated = buffers.FirstOrDefault(buffer => buffer.Stage == source.Stage && buffer.Register == source.Register && buffer.Name == source.Name);
                if (updated is null || updated.Data.Length != source.Data.Length) continue;
                updated.Data.CopyTo(source.Data, 0);
            }
        }
    }
}
