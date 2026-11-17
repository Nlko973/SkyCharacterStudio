using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using SkyModel.Core.Resources;

namespace SkyCharacterStudio;

/// <summary>Decodes Sky texture assets through the editor's texture decoder for Helix's D3D texture pool.</summary>
internal static class GameAvatarTextureLoader
{
    private static readonly ConcurrentDictionary<(string Path, bool IsColor), TextureModel> Cache = new();
    private static readonly TextureModel BlackTexture = new([new Color4(0, 0, 0, 0)], 1, 1);
    private static readonly TextureModel FlatNormalTexture = new([new Color4(0.5f, 0.5f, 1, 1)], 1, 1);
    private static readonly TextureModel NeutralLightTexture = new([new Color4(0.5f, 0.5f, 0.5f, 1)], 1, 1);
    private static readonly TextureModel NeutralDepthTexture = new([new Color4(1, 1, 1, 1)], 1, 1);

    public static IReadOnlyDictionary<string, TextureModel> LoadForItem(WardrobeEntry item)
    {
        var result = new Dictionary<string, TextureModel>(StringComparer.OrdinalIgnoreCase);
        if (item.MeshPath is null) return result;

        var material = SkyResourceResolver.ResolveMaterial(item.MeshPath);
        var diffuse = Resolve(item, item.Definition.DiffuseTexture, material?.DiffuseTex);
        var diffuse2 = Resolve(item, item.Definition.MaskTexture, material?.MaskTex, material?.Diffuse2Tex);
        var normal = Resolve(item, item.Definition.NormalTexture, material?.NormTex);
        var attrib = Resolve(item, item.Definition.AttributeTexture, material?.AttribTex);
        var noise = Resolve(item, "Noise2Ch");
        var capeIntegrity = Resolve(item, "CapeIntegrity");

        // The game samples diffuse/albedo in sRGB and the GPU converts it to
        // linear before lighting. Mask, normal, and packed attribute maps are
        // numeric data and must keep their original channel values.
        Add(result, "u_diffuse1Tex", diffuse, isColor: true);
        // Avatar SPIR-V samples these slots even when the outfit has no dye mask
        // or normal map. Keep the D3D binding valid with neutral data instead of
        // leaving an unbound resource whose sample is driver-dependent.
        Add(result, "u_diffuse2Tex", diffuse2, BlackTexture);
        Add(result, "u_normTex", normal, FlatNormalTexture);
        Add(result, "u_attribTex", attrib);
        Add(result, "u_noise", noise);
        Add(result, "u_capeIntegrityNoise", capeIntegrity);
        Add(result, "u_lightPRTex", null, NeutralLightTexture);
        Add(result, "u_lightCSTex", null, NeutralLightTexture);
        // Replaced by the live opaque scene color during Helix's weighted
        // transparency pass. A defined fallback avoids sampling an unbound SRV.
        Add(result, "u_opaqueDepth", null, NeutralDepthTexture);
        return result;
    }

    private static string? Resolve(WardrobeEntry item, params string?[] names)
    {
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var path = SkyResourceResolver.FindTextureFile(item.MeshPath!, name);
            if (path is not null) return path;
        }
        return null;
    }

    private static void Add(
        IDictionary<string, TextureModel> target,
        string name,
        string? path,
        TextureModel? fallback = null,
        bool isColor = false)
    {
        if (path is not null)
        {
            var key = (Path.GetFullPath(path), isColor);
            target[name] = Cache.GetOrAdd(key, static cacheKey => LoadTexture(cacheKey.Path, cacheKey.IsColor));
            return;
        }
        if (fallback is not null) target[name] = fallback;
    }

    private static TextureModel LoadTexture(string path, bool isColor)
    {
        var source = GameTextureLoader.Load(path) as BitmapSource
            ?? throw new InvalidDataException($"Sky texture could not be decoded: {Path.GetFileName(path)}");
        var bgra = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = checked(bgra.PixelWidth * 4);
        var pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        var colors = new Color4[bgra.PixelWidth * bgra.PixelHeight];
        for (var i = 0; i < colors.Length; i++)
        {
            var offset = i * 4;
            var r = pixels[offset + 2] / 255f;
            var g = pixels[offset + 1] / 255f;
            var b = pixels[offset] / 255f;
            if (isColor)
            {
                r = GameColorMath.SrgbToLinear(r);
                g = GameColorMath.SrgbToLinear(g);
                b = GameColorMath.SrgbToLinear(b);
            }
            colors[i] = new Color4(r, g, b, pixels[offset + 3] / 255f);
        }
        return new TextureModel(colors, bgra.PixelWidth, bgra.PixelHeight);
    }
}
