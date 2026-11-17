using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyModel.Core.Models;

namespace SkyCharacterStudio;

/// <summary>
/// CPU preview for the verified AvatarCham mask equation. It only runs when the
/// mesh uses the same UV coordinates for diffuse and mask, so a regular WPF
/// ImageBrush samples the same texel region as the game shader.
/// </summary>
internal static class GameDyeTextureBaker
{
    public static BitmapSource? TryBake(WardrobeEntry item, MeshData mesh, BitmapSource diffuse, BitmapSource mask, bool diffuseIsRamp)
    {
        if (!string.Equals(item.Definition.Shader, "AvatarCham", StringComparison.OrdinalIgnoreCase)) return null;
        if (diffuseIsRamp) return null;
        if (item.Definition.DyeablePrimary is false && item.Definition.DyeableSecondary is false) return null;
        if (!HasMatchingUvSets(mesh)) return null;

        var primary = item.Definition.DyeablePrimary ? ColorFor(item.PrimaryDyeColorDefinition) : null;
        var secondary = item.Definition.DyeableSecondary ? ColorFor(item.SecondaryDyeColorDefinition) : null;
        if (primary is null && secondary is null) return null;

        var diffusePixels = ToBgra32(diffuse, out var width, out var height);
        var maskPixels = ToBgra32(mask, out var maskWidth, out var maskHeight);
        if (width <= 0 || height <= 0 || maskWidth <= 0 || maskHeight <= 0) return null;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceOffset = (y * width + x) * 4;
            var maskX = Math.Clamp((int)((x + 0.5f) / width * maskWidth), 0, maskWidth - 1);
            var maskY = Math.Clamp((int)((y + 0.5f) / height * maskHeight), 0, maskHeight - 1);
            var maskOffset = (maskY * maskWidth + maskX) * 4;
            var redWeight = maskPixels[maskOffset + 2] / 255f;
            var greenWeight = maskPixels[maskOffset + 1] / 255f;
            var b = GameColorMath.SrgbToLinear(diffusePixels[sourceOffset] / 255f);
            var g = GameColorMath.SrgbToLinear(diffusePixels[sourceOffset + 1] / 255f);
            var r = GameColorMath.SrgbToLinear(diffusePixels[sourceOffset + 2] / 255f);

            if (primary is { } primaryColor)
            {
                r = Lerp(r, GameColorMath.SrgbToLinear(primaryColor.R / 255f), redWeight);
                g = Lerp(g, GameColorMath.SrgbToLinear(primaryColor.G / 255f), redWeight);
                b = Lerp(b, GameColorMath.SrgbToLinear(primaryColor.B / 255f), redWeight);
            }
            if (secondary is { } secondaryColor)
            {
                r = Lerp(r, GameColorMath.SrgbToLinear(secondaryColor.R / 255f), greenWeight);
                g = Lerp(g, GameColorMath.SrgbToLinear(secondaryColor.G / 255f), greenWeight);
                b = Lerp(b, GameColorMath.SrgbToLinear(secondaryColor.B / 255f), greenWeight);
            }

            diffusePixels[sourceOffset] = GameColorMath.LinearToSrgbByte(b);
            diffusePixels[sourceOffset + 1] = GameColorMath.LinearToSrgbByte(g);
            diffusePixels[sourceOffset + 2] = GameColorMath.LinearToSrgbByte(r);
            // Keep the source alpha byte intact. Texture coverage is independent
            // from the primary/secondary dye mask.
        }

        var baked = BitmapSource.Create(width, height, diffuse.DpiX, diffuse.DpiY,
            PixelFormats.Bgra32, null, diffusePixels, width * 4);
        baked.Freeze();
        return baked;
    }

    private static bool HasMatchingUvSets(MeshData mesh)
    {
        if (mesh.Uv0.Count == 0 || mesh.Uv0.Count != mesh.Uv1.Count || mesh.Uv0.Count != mesh.Vertices.Count) return false;
        for (var i = 0; i < mesh.Uv0.Count; i++)
        {
            var a = mesh.Uv0[i];
            var b = mesh.Uv1[i];
            if (!float.IsFinite(a.U) || !float.IsFinite(a.V) || !float.IsFinite(b.U) || !float.IsFinite(b.V)) return false;
            if (Math.Abs(a.U - b.U) > 0.0005f || Math.Abs(a.V - b.V) > 0.0005f) return false;
        }
        return true;
    }

    private static Color? ColorFor(DyeColorDefinition? dye)
    {
        if (dye is null || dye.Name.Equals("none", StringComparison.OrdinalIgnoreCase) || dye.Hsv is not { Length: >= 3 } hsv) return null;
        return GameColorMath.FromHsv(hsv[0], hsv[1] / 100d, hsv[2] / 100d);
    }

    private static byte[] ToBgra32(BitmapSource source, out int width, out int height)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static float Lerp(float from, float to, float weight) => from + (to - from) * Math.Clamp(weight, 0, 1);
}
