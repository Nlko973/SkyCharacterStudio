using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SkyCharacterStudio;

internal sealed record GameShaderUniformField(string Name, int Type, int ArrayCount, int Offset, int Size);

internal sealed record GameShaderUniformBlock(
    string Name,
    string Stage,
    int Register,
    int ByteSize,
    IReadOnlyList<GameShaderUniformField> Fields);

internal sealed record GameShaderBinding(string Name, string Stage, int Slot, string ResourceType, int ByteSize = 0);

/// <summary>Reads the stage-specific constant layouts stored in Sky's .ref file.</summary>
internal static class GameAvatarShaderReflection
{
    private const int MemberStride = 0x24;
    private static readonly string[] TextureResources =
    [
        "u_attribTex", "u_diffuse1Tex", "u_diffuse2Tex", "u_lightCSTex",
        "u_lightPRTex", "u_normTex", "u_probeCube", "u_noise", "u_capeIntegrityNoise",
        "u_opaqueColor", "u_opaqueDepth",
    ];

    public static IReadOnlyList<GameShaderUniformBlock> Read(string reflectionPath)
    {
        var data = File.ReadAllBytes(reflectionPath);
        var blocks = new List<GameShaderUniformBlock>();

        var frameName = FindAscii(data, "PerFrameUniforms").FirstOrDefault(-1);
        if (frameName < 0)
            throw new InvalidDataException("PerFrameUniforms was not found in the Avatar reflection file.");
        var perFrameVertex = ReadBlock(data, "PerFrameUniforms", "Vertex", 2, frameName + 0x20);
        blocks.Add(perFrameVertex);
        // The game uses the same PerFrameUniforms layout in both stages. The
        // old reader only created the vertex b2 buffer, leaving fragment sun,
        // probe SH, and average-light values at zero and making lit Avatar
        // materials render black. The fragment SPIR-V has its own b2 binding,
        // so upload a second stage-specific buffer with the same reflected
        // fields and offsets.
        blocks.Add(perFrameVertex with { Stage = "Fragment" });

        var objectHeaders = FindAscii(data, "PerObjectUniforms")
            .Where(offset => HasUniformBlockHeader(data, offset))
            .ToArray();
        if (objectHeaders.Length < 2)
            throw new InvalidDataException("Expected separate vertex and fragment PerObjectUniforms layouts.");
        blocks.Add(ReadBlock(data, "PerObjectUniforms", "Vertex", 0, objectHeaders[0] + 0x20));
        blocks.Add(ReadBlock(data, "PerObjectUniforms", "Fragment", 0, objectHeaders[1] + 0x20));

        var passName = FindAscii(data, "PerPassUniforms").FirstOrDefault();
        if (passName == 0)
            throw new InvalidDataException("PerPassUniforms was not found in the Avatar reflection file.");
        blocks.Add(ReadBlock(data, "PerPassUniforms", "Fragment", 1, passName + 0x20));

        return blocks;
    }

    public static IReadOnlyDictionary<int, string> ReadTextureSlots(string reflectionPath)
    {
        var data = File.ReadAllBytes(reflectionPath);
        var result = new Dictionary<int, string>();
        foreach (var name in TextureResources)
        {
            var offset = FindAscii(data, name).FirstOrDefault(-1);
            if (offset < 0 || offset + 31 >= data.Length) continue;
            var resourceType = data[offset + 30];
            var slot = data[offset + 31];
            if (resourceType is not (1 or 3)) continue;
            if (result.TryGetValue(slot, out var existing) && !existing.Equals(name, StringComparison.Ordinal))
                throw new InvalidDataException($"Two game textures share reflected slot t{slot}: {existing} and {name}.");
            result[slot] = name;
        }
        return result;
    }

    private static GameShaderUniformBlock ReadBlock(byte[] data, string name, string stage, int register, int memberOffset)
    {
        var fields = new List<GameShaderUniformField>();
        for (var offset = memberOffset; offset + MemberStride <= data.Length; offset += MemberStride)
        {
            if (data[offset + 35] != 0xff) break;
            var encodedType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 28, 2));
            var type = encodedType & 0xff;
            if (type is not (1 or 2 or 3 or 4 or 9 or 0x23)) break;

            var terminator = data.AsSpan(offset, 28).IndexOf((byte)0);
            if (terminator <= 0) break;
            var fieldName = Encoding.ASCII.GetString(data, offset, terminator);
            if (!fieldName.StartsWith("u_", StringComparison.Ordinal)) break;

            var arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 30, 2));
            var byteOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 32, 2));
            var fieldSize = GetTypeSize(type) * Math.Max(1, (int)arrayCount);
            fields.Add(new GameShaderUniformField(fieldName, type, arrayCount, byteOffset, fieldSize));
        }

        if (fields.Count == 0)
            throw new InvalidDataException($"No uniform fields were decoded for {stage} {name}.");
        var byteSize = (fields.Max(field => field.Offset + field.Size) + 15) & ~15;
        return new GameShaderUniformBlock(name, stage, register, byteSize, fields);
    }

    private static int GetTypeSize(int type) => type switch
    {
        1 or 9 => 4,
        2 => 8,
        3 or 4 => 16,
        0x23 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static bool HasUniformBlockHeader(byte[] data, int nameOffset)
    {
        if (nameOffset < 8) return false;
        var header = data.AsSpan(nameOffset - 8, 8);
        return BinaryPrimitives.ReadUInt16LittleEndian(header) == 0x0c
            && BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) == 1
            && header[7] == 0xff;
    }

    private static IEnumerable<int> FindAscii(byte[] data, string value)
    {
        var target = Encoding.ASCII.GetBytes(value);
        for (var offset = 0; offset <= data.Length - target.Length; offset++)
        {
            if (data.AsSpan(offset, target.Length).SequenceEqual(target))
                yield return offset;
        }
    }
}
