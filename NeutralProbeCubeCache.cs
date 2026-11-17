using System.Runtime.CompilerServices;
using HelixToolkit.SharpDX;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace SkyCharacterStudio;

/// <summary>Provides a neutral fallback probe for standalone editor previews.</summary>
internal static class NeutralProbeCubeCache
{
    private static readonly ConditionalWeakTable<SharpDX.Direct3D11.Device, ShaderResourceView> Cache = new();

    public static ShaderResourceView Get(SharpDX.Direct3D11.Device device) => Cache.GetValue(device, Create);

    private static ShaderResourceView Create(SharpDX.Direct3D11.Device device)
    {
        using var data = new DataStream(24, true, true);
        for (var face = 0; face < 6; face++)
        {
            data.Write((byte)128);
            data.Write((byte)128);
            data.Write((byte)128);
            data.Write(byte.MaxValue);
        }

        var description = new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 6,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.TextureCube,
        };
        var initialData = Enumerable.Range(0, 6)
            .Select(face => new DataBox(IntPtr.Add(data.DataPointer, face * 4), 4, 4))
            .ToArray();
        using var texture = new Texture2D(device, description, initialData);
        return new ShaderResourceView(device, texture);
    }
}
