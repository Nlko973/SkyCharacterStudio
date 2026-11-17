using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using SkyModel.Core.Resources;

namespace SkyCharacterStudio;

/// <summary>Loads standalone game icons or extracts an ImageRegion from a UI atlas.</summary>
internal static class GameIconLoader
{
    private sealed record Region(string Atlas, double U0, double V0, double U1, double V1);
    private static readonly Dictionary<string, Region> Regions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static BitmapSource? Load(string sourcePath, string iconName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(iconName)) return null;
        if (SkyResourceResolver.FindTextureFile(sourcePath, iconName) is { } direct
            && GameTextureLoader.Load(direct) is BitmapSource directBitmap) return directBitmap;
        var region = FindRegion(sourcePath, iconName);
        if (region is null) return null;
        var atlasPath = SkyResourceResolver.FindTextureFile(sourcePath, region.Atlas);
        if (atlasPath is null || GameTextureLoader.Load(atlasPath) is not BitmapSource atlas) return null;
        var left = Math.Clamp((int)Math.Floor(region.U0 * atlas.PixelWidth), 0, atlas.PixelWidth - 1);
        var top = Math.Clamp((int)Math.Floor((1 - region.V1) * atlas.PixelHeight), 0, atlas.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(region.U1 * atlas.PixelWidth), left + 1, atlas.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((1 - region.V0) * atlas.PixelHeight), top + 1, atlas.PixelHeight);
        var crop = new CroppedBitmap(atlas, new Int32Rect(left, top, right - left, bottom - top));
        crop.Freeze();
        return crop;
    }

    public static string? FindEmoteIcon(string animationPackPath, string clipName)
    {
        var resources = FindResourcesDirectory(animationPackPath);
        var path = resources is null ? null : Path.Combine(resources, "CollectibleDefs.json");
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var clip = Normalize(clipName);
            string? bestIcon = null;
            var bestLength = 0;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("icon", out var iconValue)) continue;
                var icon = iconValue.GetString();
                if (string.IsNullOrWhiteSpace(icon)) continue;
                foreach (var keyName in new[] { "params", "name" })
                {
                    if (!entry.TryGetProperty(keyName, out var keyValue)) continue;
                    var key = Normalize(keyValue.GetString() ?? string.Empty).Replace("emote", string.Empty, StringComparison.OrdinalIgnoreCase);
                    if (key.Length < 4 || !clip.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.Length > bestLength) { bestLength = key.Length; bestIcon = icon; }
                }
            }
            return bestIcon;
        }
        catch { return null; }
    }

    public static BitmapSource? LoadForClip(string animationPackPath, string clipName)
    {
        var icon = FindEmoteIcon(animationPackPath, clipName);
        return icon is null ? null : Load(animationPackPath, icon);
    }

    private static Region? FindRegion(string sourcePath, string iconName)
    {
        lock (Gate) if (Regions.TryGetValue(iconName, out var cached)) return cached;
        var resources = FindResourcesDirectory(sourcePath);
        var atlasFile = resources is null ? null : Path.Combine(resources, "UIPackedAtlas.lua");
        if (atlasFile is null || !File.Exists(atlasFile)) return null;
        try
        {
            var text = File.ReadAllText(atlasFile);
            var pattern = "resource\\s+\"ImageRegion\"\\s+\"" + Regex.Escape(iconName)
                + "\"\\s*\\{\\s*image\\s*=\\s*\"([^\"]+)\"\\s*,\\s*uv\\s*=\\s*\\{([^}]+)\\}";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!match.Success) return null;
            var values = match.Groups[2].Value.Split(',').Select(ParseNumber).ToArray();
            if (values.Length < 4 || values.Any(value => value is null)) return null;
            var region = new Region(match.Groups[1].Value, values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value);
            lock (Gate) Regions[iconName] = region;
            return region;
        }
        catch { return null; }
    }

    private static double? ParseNumber(string value)
    {
        var pieces = value.Trim().Split('/');
        if (pieces.Length == 1 && double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        if (pieces.Length == 2
            && double.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0) return numerator / denominator;
        return null;
    }

    private static string? FindResourcesDirectory(string sourcePath)
    {
        var current = Directory.Exists(sourcePath) ? sourcePath : Path.GetDirectoryName(sourcePath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            var resources = Path.Combine(current, "Data", "Resources");
            if (Directory.Exists(resources)) return resources;
            resources = Path.Combine(current, "Resources");
            if (Directory.Exists(resources)) return resources;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private static string Normalize(string value) => Regex.Replace(value, "[^A-Za-z0-9]", "").ToLowerInvariant();
}
