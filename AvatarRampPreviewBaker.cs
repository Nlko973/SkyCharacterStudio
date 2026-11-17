using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyModel.Core.Models;

namespace SkyCharacterStudio;

/// <summary>
/// Approximate Avatar ramp preview for the non-shader viewport. Rasterizes the
/// UV0 surface while sampling the game's dye mask through its independent UV1.
/// This avoids showing a ramp atlas as if it were a normal diffuse texture.
/// </summary>
internal static class AvatarRampPreviewBaker
{
    private const int Size = 256;

    public static BitmapSource? TryBake(WardrobeEntry item, MeshData mesh, BitmapSource mask)
    {
        if (!item.Definition.Shader.Equals("Avatar", StringComparison.OrdinalIgnoreCase)
            || mesh.Uv0.Count != mesh.Vertices.Count || mesh.Uv1.Count != mesh.Vertices.Count
            || mesh.Indices.Count == 0) return null;

        var maskPixels = new FormatConvertedBitmap(mask, PixelFormats.Bgra32, null, 0);
        var maskData = new byte[checked(maskPixels.PixelWidth * maskPixels.PixelHeight * 4)];
        maskPixels.CopyPixels(maskData, maskPixels.PixelWidth * 4, 0);
        var primary = ResolveColor(item.PrimaryDyeColorDefinition, item.Definition.PrimaryDyeHsv);
        var secondary = ResolveColor(item.SecondaryDyeColorDefinition, item.Definition.SecondaryDyeHsv);
        var baseColor = Color.FromRgb(230, 226, 218);
        var pixels = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
        {
            pixels[i * 4] = baseColor.B;
            pixels[i * 4 + 1] = baseColor.G;
            pixels[i * 4 + 2] = baseColor.R;
            pixels[i * 4 + 3] = 255;
        }

        for (var triangle = 0; triangle < mesh.Indices.Count; triangle++)
        {
            var (ia, ib, ic) = mesh.Indices[triangle];
            if (!Valid(ia) || !Valid(ib) || !Valid(ic)) continue;
            var a = mesh.Uv0[ia]; var b = mesh.Uv0[ib]; var c = mesh.Uv0[ic];
            var ma = mesh.Uv1[ia]; var mb = mesh.Uv1[ib]; var mc = mesh.Uv1[ic];
            var ax = a.U * (Size - 1); var ay = (1 - a.V) * (Size - 1);
            var bx = b.U * (Size - 1); var by = (1 - b.V) * (Size - 1);
            var cx = c.U * (Size - 1); var cy = (1 - c.V) * (Size - 1);
            var area = Edge(ax, ay, bx, by, cx, cy);
            if (Math.Abs(area) < 1e-5f) continue;
            var minX = Math.Clamp((int)MathF.Floor(Math.Min(ax, Math.Min(bx, cx))), 0, Size - 1);
            var maxX = Math.Clamp((int)MathF.Ceiling(Math.Max(ax, Math.Max(bx, cx))), 0, Size - 1);
            var minY = Math.Clamp((int)MathF.Floor(Math.Min(ay, Math.Min(by, cy))), 0, Size - 1);
            var maxY = Math.Clamp((int)MathF.Ceiling(Math.Max(ay, Math.Max(by, cy))), 0, Size - 1);
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var wa = Edge(bx, by, cx, cy, x + .5f, y + .5f) / area;
                var wb = Edge(cx, cy, ax, ay, x + .5f, y + .5f) / area;
                var wc = 1 - wa - wb;
                if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f) continue;
                var u = wa * ma.U + wb * mb.U + wc * mc.U;
                var v = wa * ma.V + wb * mb.V + wc * mc.V;
                var mx = Math.Clamp((int)(Wrap(u) * maskPixels.PixelWidth), 0, maskPixels.PixelWidth - 1);
                var my = Math.Clamp((int)((1 - Wrap(v)) * maskPixels.PixelHeight), 0, maskPixels.PixelHeight - 1);
                var mo = (my * maskPixels.PixelWidth + mx) * 4;
                var r = maskData[mo + 2] / 255f;
                var g = maskData[mo + 1] / 255f;
                var color = Mix(Mix(baseColor, primary, r), secondary, g);
                var offset = (y * Size + x) * 4;
                pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R;
            }
        }

        var result = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        result.Freeze();
        return result;

        bool Valid(int index) => index >= 0 && index < mesh.Uv0.Count && index < mesh.Uv1.Count;
    }

    private static Color ResolveColor(DyeColorDefinition? dye, float[]? hsv)
    {
        if (dye is { Name: not "none", Hsv.Length: >= 3 })
            return GameColorMath.FromHsv(dye.Hsv[0], dye.Hsv[1] / 100d, dye.Hsv[2] / 100d);
        return hsv is { Length: >= 3 } ? GameColorMath.FromHsvData(hsv) : Colors.White;
    }

    private static Color Mix(Color a, Color b, float amount) => Color.FromRgb(
        (byte)Math.Clamp((int)Math.Round(a.R + (b.R - a.R) * amount), 0, 255),
        (byte)Math.Clamp((int)Math.Round(a.G + (b.G - a.G) * amount), 0, 255),
        (byte)Math.Clamp((int)Math.Round(a.B + (b.B - a.B) * amount), 0, 255));

    private static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
        (px - ax) * (by - ay) - (py - ay) * (bx - ax);

    private static float Wrap(float value) => value - MathF.Floor(value);
}
