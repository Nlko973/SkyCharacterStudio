using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyModel.Core.Parsing;
using SkyModel.Core.Resources;

namespace SkyCharacterStudio;

internal static class MeshThumbnailProvider
{
    private const int Size = 96;
    private static readonly ConcurrentDictionary<string, Lazy<BitmapSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? GetThumbnail(WardrobeEntry item) => Cache.GetOrAdd(
        $"{item.MeshPath}|{item.Definition.DiffuseTexture}|{item.PreviewColor.R:X2}{item.PreviewColor.G:X2}{item.PreviewColor.B:X2}",
        _ => new Lazy<BitmapSource?>(() => Render(item.MeshPath!, item.Definition.DiffuseTexture, item.PreviewColor), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public static BitmapSource? GetOutfitThumbnail(IReadOnlyList<WardrobeEntry> items)
    {
        var key = "outfit:" + string.Join('|', items.Select(item => item.Name));
        return Cache.GetOrAdd(key, _ => new Lazy<BitmapSource?>(() => RenderOutfit(items), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static BitmapSource? RenderOutfit(IReadOnlyList<WardrobeEntry> items)
    {
        try
        {
            var camera = Vector3.Normalize(new Vector3(0.65f, 0.18f, -0.74f));
            var right = Vector3.Normalize(Vector3.Cross(camera, Vector3.UnitY));
            var up = Vector3.Normalize(Vector3.Cross(right, camera));
            var parts = new List<(WardrobeEntry Item, SkyModel.Core.Models.MeshData Mesh, BitmapSource? Texture, byte[]? Pixels, (float X, float Y, float Depth)[] Points)>();
            foreach (var item in items.Where(item => item.MeshPath is not null))
            {
                var mesh = TgcMeshReader.ReadMesh(item.MeshPath!);
                var path = string.IsNullOrWhiteSpace(item.Definition.DiffuseTexture) ? null : SkyResourceResolver.FindTextureFile(item.MeshPath!, item.Definition.DiffuseTexture);
                var texture = path is null ? null : GameTextureLoader.Load(path) as BitmapSource;
                byte[]? texturePixels = null;
                if (texture is not null)
                {
                    texturePixels = new byte[texture.PixelWidth * texture.PixelHeight * 4];
                    texture.CopyPixels(texturePixels, texture.PixelWidth * 4, 0);
                }
                var points = mesh.Vertices.Select(vertex =>
                {
                    var position = new Vector3(-vertex.X, vertex.Y, -vertex.Z);
                    return (Vector3.Dot(position, right), Vector3.Dot(position, up), Vector3.Dot(position, camera));
                }).ToArray();
                parts.Add((item, mesh, texture, texturePixels, points));
            }
            if (parts.Count == 0) return null;

            var minX = parts.SelectMany(part => part.Points).Min(point => point.Item1);
            var maxX = parts.SelectMany(part => part.Points).Max(point => point.Item1);
            var minY = parts.SelectMany(part => part.Points).Min(point => point.Item2);
            var maxY = parts.SelectMany(part => part.Points).Max(point => point.Item2);
            var scale = (Size - 14f) / Math.Max(maxX - minX, maxY - minY);
            if (!float.IsFinite(scale) || scale <= 0) return null;
            var centerX = (minX + maxX) * 0.5f; var centerY = (minY + maxY) * 0.5f;
            var screens = parts.Select(part => part.Points.Select(point => (
                X: (point.Item1 - centerX) * scale + Size * 0.5f,
                Y: Size * 0.5f - (point.Item2 - centerY) * scale,
                point.Item3)).ToArray()).ToArray();
            var pixels = new byte[Size * Size * 4];
            var depthBuffer = Enumerable.Repeat(float.NegativeInfinity, Size * Size).ToArray();
            var light = Vector3.Normalize(new Vector3(-0.4f, 0.7f, -0.6f));
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];
                var screen = screens[partIndex];
                foreach (var (a, b, c) in part.Mesh.Indices)
                {
                    if (a < 0 || b < 0 || c < 0 || a >= screen.Length || b >= screen.Length || c >= screen.Length) continue;
                    var p0 = screen[a]; var p1 = screen[b]; var p2 = screen[c];
                    var area = Edge(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y);
                    if (Math.Abs(area) < 0.001f) continue;
                    var x0 = Math.Clamp((int)MathF.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))), 0, Size - 1);
                    var x1 = Math.Clamp((int)MathF.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))), 0, Size - 1);
                    var y0 = Math.Clamp((int)MathF.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))), 0, Size - 1);
                    var y1 = Math.Clamp((int)MathF.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))), 0, Size - 1);
                    var v0 = ToVector(part.Mesh.Vertices[a]); var v1 = ToVector(part.Mesh.Vertices[b]); var v2 = ToVector(part.Mesh.Vertices[c]);
                    var normal = Vector3.Cross(v1 - v0, v2 - v0);
                    if (normal.LengthSquared() > 1e-10f) normal = Vector3.Normalize(normal);
                    var shade = 0.55f + 0.38f * Math.Abs(Vector3.Dot(normal, light));
                    for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var px = x + 0.5f; var py = y + 0.5f;
                        var w0 = Edge(p1.X, p1.Y, p2.X, p2.Y, px, py) / area;
                        var w1 = Edge(p2.X, p2.Y, p0.X, p0.Y, px, py) / area;
                        var w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        var depth = w0 * p0.Item3 + w1 * p1.Item3 + w2 * p2.Item3;
                        var pixel = y * Size + x;
                        if (depth <= depthBuffer[pixel]) continue;
                        var offset = pixel * 4;
                        var red = part.Item.PreviewColor.R; var green = part.Item.PreviewColor.G; var blue = part.Item.PreviewColor.B; var alpha = (byte)255;
                        if (part.Texture is not null && part.Pixels is not null && part.Mesh.Uv0.Count > Math.Max(a, Math.Max(b, c)))
                        {
                            var uv0 = part.Mesh.Uv0[a]; var uv1 = part.Mesh.Uv0[b]; var uv2 = part.Mesh.Uv0[c];
                            var u = uv0.U * w0 + uv1.U * w1 + uv2.U * w2;
                            var v = uv0.V * w0 + uv1.V * w1 + uv2.V * w2;
                            var tx = (int)(Wrap(u) * part.Texture.PixelWidth) % part.Texture.PixelWidth;
                            var ty = (int)(Wrap(1 - v) * part.Texture.PixelHeight) % part.Texture.PixelHeight;
                            var texel = (ty * part.Texture.PixelWidth + tx) * 4;
                            blue = part.Pixels[texel]; green = part.Pixels[texel + 1]; red = part.Pixels[texel + 2]; alpha = part.Pixels[texel + 3];
                            if (alpha == 0) continue;
                        }
                        depthBuffer[pixel] = depth;
                        pixels[offset] = (byte)Math.Clamp((int)Math.Round(blue * shade), 0, 255);
                        pixels[offset + 1] = (byte)Math.Clamp((int)Math.Round(green * shade), 0, 255);
                        pixels[offset + 2] = (byte)Math.Clamp((int)Math.Round(red * shade), 0, 255);
                        pixels[offset + 3] = alpha;
                    }
                }
            }
            var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static BitmapSource? Render(string meshPath, string diffuseName, Color tint)
    {
        try
        {
            var mesh = TgcMeshReader.ReadMesh(meshPath);
            if (mesh.Vertices.Count == 0 || mesh.Indices.Count == 0) return null;
            var texturePath = string.IsNullOrWhiteSpace(diffuseName) ? null : SkyResourceResolver.FindTextureFile(meshPath, diffuseName);
            var texture = texturePath is null ? null : GameTextureLoader.Load(texturePath) as BitmapSource;
            byte[]? texturePixels = null;
            if (texture is not null)
            {
                texturePixels = new byte[texture.PixelWidth * texture.PixelHeight * 4];
                texture.CopyPixels(texturePixels, texture.PixelWidth * 4, 0);
            }

            var camera = Vector3.Normalize(new Vector3(0.65f, 0.18f, -0.74f));
            var right = Vector3.Normalize(Vector3.Cross(camera, Vector3.UnitY));
            var up = Vector3.Normalize(Vector3.Cross(right, camera));
            var projected = new (float X, float Y, float Depth)[mesh.Vertices.Count];
            for (var i = 0; i < mesh.Vertices.Count; i++)
            {
                var vertex = mesh.Vertices[i];
                var position = new Vector3(-vertex.X, vertex.Y, -vertex.Z);
                projected[i] = (Vector3.Dot(position, right), Vector3.Dot(position, up), Vector3.Dot(position, camera));
            }

            var minX = projected.Min(point => point.X); var maxX = projected.Max(point => point.X);
            var minY = projected.Min(point => point.Y); var maxY = projected.Max(point => point.Y);
            var scale = (Size - 14f) / Math.Max(maxX - minX, maxY - minY);
            if (!float.IsFinite(scale) || scale <= 0) return null;
            var centerX = (minX + maxX) * 0.5f; var centerY = (minY + maxY) * 0.5f;
            var screen = projected.Select(point => (
                X: (point.X - centerX) * scale + Size * 0.5f,
                Y: Size * 0.5f - (point.Y - centerY) * scale,
                point.Depth)).ToArray();

            var pixels = new byte[Size * Size * 4];
            var depthBuffer = Enumerable.Repeat(float.NegativeInfinity, Size * Size).ToArray();
            var light = Vector3.Normalize(new Vector3(-0.4f, 0.7f, -0.6f));
            foreach (var (a, b, c) in mesh.Indices)
            {
                if (a < 0 || b < 0 || c < 0 || a >= screen.Length || b >= screen.Length || c >= screen.Length) continue;
                var p0 = screen[a]; var p1 = screen[b]; var p2 = screen[c];
                var area = Edge(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y);
                if (Math.Abs(area) < 0.001f) continue;
                var x0 = Math.Clamp((int)MathF.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))), 0, Size - 1);
                var x1 = Math.Clamp((int)MathF.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))), 0, Size - 1);
                var y0 = Math.Clamp((int)MathF.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))), 0, Size - 1);
                var y1 = Math.Clamp((int)MathF.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))), 0, Size - 1);
                var v0 = ToVector(mesh.Vertices[a]); var v1 = ToVector(mesh.Vertices[b]); var v2 = ToVector(mesh.Vertices[c]);
                var normal = Vector3.Cross(v1 - v0, v2 - v0);
                if (normal.LengthSquared() > 1e-10f) normal = Vector3.Normalize(normal);
                var shade = 0.55f + 0.38f * Math.Abs(Vector3.Dot(normal, light));
                for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var px = x + 0.5f; var py = y + 0.5f;
                    var w0 = Edge(p1.X, p1.Y, p2.X, p2.Y, px, py) / area;
                    var w1 = Edge(p2.X, p2.Y, p0.X, p0.Y, px, py) / area;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    var depth = w0 * p0.Depth + w1 * p1.Depth + w2 * p2.Depth;
                    var pixel = y * Size + x;
                    if (depth <= depthBuffer[pixel]) continue;
                    depthBuffer[pixel] = depth;
                    var offset = pixel * 4;
                    var red = tint.R; var green = tint.G; var blue = tint.B; var alpha = (byte)255;
                    if (texture is not null && texturePixels is not null && mesh.Uv0.Count > Math.Max(a, Math.Max(b, c)))
                    {
                        var uv0 = mesh.Uv0[a]; var uv1 = mesh.Uv0[b]; var uv2 = mesh.Uv0[c];
                        var u = uv0.U * w0 + uv1.U * w1 + uv2.U * w2;
                        var v = uv0.V * w0 + uv1.V * w1 + uv2.V * w2;
                        var tx = (int)(Wrap(u) * texture.PixelWidth) % texture.PixelWidth;
                        var ty = (int)(Wrap(1 - v) * texture.PixelHeight) % texture.PixelHeight;
                        var texel = (ty * texture.PixelWidth + tx) * 4;
                        blue = texturePixels[texel]; green = texturePixels[texel + 1]; red = texturePixels[texel + 2]; alpha = texturePixels[texel + 3];
                        if (alpha == 0) continue;
                    }
                    pixels[offset] = (byte)Math.Clamp((int)Math.Round(blue * shade), 0, 255);
                    pixels[offset + 1] = (byte)Math.Clamp((int)Math.Round(green * shade), 0, 255);
                    pixels[offset + 2] = (byte)Math.Clamp((int)Math.Round(red * shade), 0, 255);
                    pixels[offset + 3] = alpha;
                }
            }

            var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static Vector3 ToVector((float X, float Y, float Z) point) => new(-point.X, point.Y, -point.Z);

    private static float Wrap(double value) => (float)(value - Math.Floor(value));

    private static float Edge(float ax, float ay, float bx, float by, float px, float py) => (px - ax) * (by - ay) - (py - ay) * (bx - ax);
}
