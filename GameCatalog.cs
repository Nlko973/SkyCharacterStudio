using System.IO;
using System.Numerics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using SkyModel.Core.Models;
using SkyModel.Core.Resources;

namespace SkyCharacterStudio;

public sealed record OutfitDefinition
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("mesh")] public string Mesh { get; init; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("icon")] public string Icon { get; init; } = string.Empty;
    [JsonPropertyName("season")] public string Season { get; init; } = string.Empty;
    [JsonPropertyName("outfit_event")] public string OutfitEvent { get; init; } = string.Empty;
    [JsonPropertyName("shader")] public string Shader { get; init; } = string.Empty;
    [JsonPropertyName("spiritShader")] public string SpiritShader { get; init; } = string.Empty;
    [JsonPropertyName("diffuseTex")] public string DiffuseTexture { get; init; } = string.Empty;
    [JsonPropertyName("normTex")] public string NormalTexture { get; init; } = string.Empty;
    [JsonPropertyName("maskTex")] public string MaskTexture { get; init; } = string.Empty;
    [JsonPropertyName("attribTex")] public string AttributeTexture { get; init; } = string.Empty;
    [JsonPropertyName("inCloset")] public bool InCloset { get; init; }
    [JsonPropertyName("isDefault")] public bool IsDefault { get; init; }
    [JsonPropertyName("dyeable_primary")] public bool DyeablePrimary { get; init; }
    [JsonPropertyName("dyeable_secondary")] public bool DyeableSecondary { get; init; }
    [JsonPropertyName("primary_dye_hsv")] public float[]? PrimaryDyeHsv { get; init; }
    [JsonPropertyName("secondary_dye_hsv")] public float[]? SecondaryDyeHsv { get; init; }
    [JsonPropertyName("base_hsv")] public float[]? BaseHsv { get; init; }
    [JsonPropertyName("icon_hsv")] public float[]? IconHsv { get; init; }
    [JsonPropertyName("color_override")] public bool ColorOverride { get; init; }
    [JsonPropertyName("glitter_intensity")] public float? GlitterIntensity { get; init; }
    [JsonPropertyName("glitter_scale")] public float? GlitterScale { get; init; }
    [JsonPropertyName("glitter_size")] public float? GlitterSize { get; init; }
    [JsonPropertyName("shader_params")] public ShaderParameterBlock? ShaderParams { get; init; }
    [JsonPropertyName("isOnesie")] public bool IsOnesie { get; init; }
    [JsonPropertyName("hairOffset")] public float[]? HairOffset { get; init; }
    [JsonPropertyName("hairScale")] public float[]? HairScale { get; init; }
    [JsonPropertyName("maskOffset")] public float[]? MaskOffset { get; init; }
    [JsonPropertyName("maskScale")] public float[]? MaskScale { get; init; }
    [JsonPropertyName("propOffset")] public float[]? PropOffset { get; init; }
    [JsonPropertyName("propScale")] public float[]? PropScale { get; init; }
    [JsonPropertyName("idleAnim")] public string? IdleAnim { get; init; }
    [JsonPropertyName("takeOutAnimSeq")] public string? TakeOutAnimSequence { get; init; }
    [JsonPropertyName("putBackAnimSeq")] public string? PutBackAnimSequence { get; init; }
    [JsonPropertyName("fastPlayAnimSeq")] public string? FastPlayAnimSequence { get; init; }
    [JsonPropertyName("handHoldRunning")] public bool HandHoldRunning { get; init; }
    [JsonPropertyName("neckOffset")] public float[]? NeckOffset { get; init; }
    [JsonPropertyName("neckScale")] public float[]? NeckScale { get; init; }
    [JsonPropertyName("wingOffset")] public float[]? WingOffset { get; init; }
    [JsonPropertyName("wingScale")] public float[]? WingScale { get; init; }
    [JsonPropertyName("footYOffset")] public float? FootYOffset { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record ShaderParameterBlock
{
    [JsonPropertyName("floats")] public IReadOnlyList<ShaderFloatParameter> Floats { get; init; } = [];
}

public sealed record ShaderFloatParameter
{
    [JsonPropertyName("shader_param_name")] public string Name { get; init; } = string.Empty;
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [JsonPropertyName("value")] public float Value { get; init; }
}

public sealed record DyeColorDefinition
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("hsv")] public int[] Hsv { get; init; } = [];
    [JsonPropertyName("primary_dye")] public string PrimaryDye { get; init; } = string.Empty;
    [JsonPropertyName("secondary_dye")] public string SecondaryDye { get; init; } = string.Empty;
}

public sealed record DyeColorSwatch(string Name, string Details, Brush Brush);
public sealed record DyeColorChoice(string Name, string Label, Brush Brush)
{
    public bool IsNone => Name.Equals("none", StringComparison.OrdinalIgnoreCase);
}
public sealed record SavedDyeChoices(string? PrimaryMain, string? PrimaryBlend, string? SecondaryMain, string? SecondaryBlend);
public sealed record SavedOutfitEntry(string Name, IReadOnlyList<string> ItemNames, string PreviewPath, System.Windows.Media.ImageSource? Thumbnail, IReadOnlyDictionary<string, SavedDyeChoices>? DyeChoices = null);

public sealed record PlaceableDefinition
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("mesh")] public string Mesh { get; init; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; init; } = string.Empty;
    [JsonPropertyName("diffuse1Tex")] public string DiffuseTexture { get; init; } = string.Empty;
    [JsonPropertyName("primary_dye_hsv")] public float[]? PrimaryDyeHsv { get; init; }
    [JsonPropertyName("secondary_dye_hsv")] public float[]? SecondaryDyeHsv { get; init; }
    [JsonPropertyName("base_hsv")] public float[]? BaseHsv { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed class WardrobeEntry(OutfitDefinition definition, string? meshPath, string? mainCategory = null, string? subcategory = null) : INotifyPropertyChanged
{
    private System.Windows.Media.ImageSource? _iconImage;
    private bool _isOwned;
    private bool _isFavorite;
    private bool _isEquipped;
    private IReadOnlyList<DyeColorChoice> _primaryDyeChoices = [];
    private IReadOnlyList<DyeColorChoice> _secondaryDyeChoices = [];
    private DyeColorChoice? _selectedPrimaryMainDye;
    private DyeColorChoice? _selectedPrimaryBlendDye;
    private DyeColorChoice? _selectedSecondaryMainDye;
    private DyeColorChoice? _selectedSecondaryBlendDye;
    private IReadOnlyList<DyeColorDefinition> _dyeDefinitions = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<DyeColorChoice> PrimaryDyeChoices { get => _primaryDyeChoices; private set { _primaryDyeChoices = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryDyeChoices))); } }
    public IReadOnlyList<DyeColorChoice> SecondaryDyeChoices { get => _secondaryDyeChoices; private set { _secondaryDyeChoices = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecondaryDyeChoices))); } }
    public DyeColorChoice? SelectedPrimaryMainDye { get => _selectedPrimaryMainDye; set => SetDyeChoice(ref _selectedPrimaryMainDye, value, nameof(SelectedPrimaryMainDye), nameof(PrimaryDyeColorDefinition)); }
    public DyeColorChoice? SelectedPrimaryBlendDye { get => _selectedPrimaryBlendDye; set => SetDyeChoice(ref _selectedPrimaryBlendDye, value, nameof(SelectedPrimaryBlendDye), nameof(PrimaryDyeColorDefinition)); }
    public DyeColorChoice? SelectedSecondaryMainDye { get => _selectedSecondaryMainDye; set => SetDyeChoice(ref _selectedSecondaryMainDye, value, nameof(SelectedSecondaryMainDye), nameof(SecondaryDyeColorDefinition)); }
    public DyeColorChoice? SelectedSecondaryBlendDye { get => _selectedSecondaryBlendDye; set => SetDyeChoice(ref _selectedSecondaryBlendDye, value, nameof(SelectedSecondaryBlendDye), nameof(SecondaryDyeColorDefinition)); }
    public string SelectedDyePair => $"Primary: {SelectedPrimaryMainDye?.Name ?? "none"} + {SelectedPrimaryBlendDye?.Name ?? "none"}; Secondary: {SelectedSecondaryMainDye?.Name ?? "none"} + {SelectedSecondaryBlendDye?.Name ?? "none"}";
    public DyeColorDefinition? PrimaryDyeColorDefinition => ResolveDyeColor(SelectedPrimaryMainDye, SelectedPrimaryBlendDye);
    public DyeColorDefinition? SecondaryDyeColorDefinition => ResolveDyeColor(SelectedSecondaryMainDye, SelectedSecondaryBlendDye);
    public DyeColorDefinition? SelectedDyeDefinition => PrimaryDyeColorDefinition;
    public void ConfigureDyes(IReadOnlyList<DyeColorChoice> primary, IReadOnlyList<DyeColorChoice> secondary, IReadOnlyList<DyeColorDefinition> allDyes)
    {
        _dyeDefinitions = allDyes;
        PrimaryDyeChoices = primary;
        SecondaryDyeChoices = secondary;
        var noDyePrimary = primary.FirstOrDefault(choice => choice.Name == "none") ?? primary.FirstOrDefault();
        var noDyeSecondary = secondary.FirstOrDefault(choice => choice.Name == "none") ?? secondary.FirstOrDefault();
        SelectedPrimaryMainDye ??= noDyePrimary;
        SelectedPrimaryBlendDye ??= noDyePrimary;
        SelectedSecondaryMainDye ??= noDyeSecondary;
        SelectedSecondaryBlendDye ??= noDyeSecondary;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDyeDefinition)));
    }

    private DyeColorDefinition? ResolveDyeColor(DyeColorChoice? main, DyeColorChoice? blend)
    {
        var mainName = main?.Name ?? "none";
        var blendName = blend?.Name ?? "none";
        if (mainName == "none" && blendName == "none")
            return _dyeDefinitions.FirstOrDefault(dye => dye.Name.Equals("none", StringComparison.OrdinalIgnoreCase));
        if (mainName == "none") return null;
        var definitionBlendName = blendName == "none" ? string.Empty : blendName;
        return _dyeDefinitions.FirstOrDefault(dye =>
            dye.PrimaryDye.Equals(mainName, StringComparison.OrdinalIgnoreCase)
            && dye.SecondaryDye.Equals(definitionBlendName, StringComparison.OrdinalIgnoreCase));
    }

    private void SetDyeChoice(ref DyeColorChoice? field, DyeColorChoice? value, string propertyName, string colorDefinitionProperty)
    {
        if (ReferenceEquals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDyePair)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(colorDefinitionProperty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDyeDefinition)));
    }
    public OutfitDefinition Definition { get; } = definition;
    public string SearchMetadata { get; } = JsonSerializer.Serialize(definition);
    public string? MeshPath { get; } = meshPath;
    public string? MainCategoryOverride { get; } = mainCategory;
    public string? SubcategoryOverride { get; } = subcategory;
    public System.Windows.Media.ImageSource? IconImage
    {
        get => _iconImage;
        private set
        {
            if (ReferenceEquals(_iconImage, value)) return;
            _iconImage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
        }
    }
    internal void SetIconImage(System.Windows.Media.ImageSource? image) => IconImage = image;
    internal void SetOwned(bool value)
    {
        if (_isOwned == value) return;
        _isOwned = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOwned)));
    }
    internal void SetFavorite(bool value)
    {
        if (_isFavorite == value) return;
        _isFavorite = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
    }
    internal void SetEquipped(bool value)
    {
        if (_isEquipped == value) return;
        _isEquipped = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEquipped)));
    }
    public string Name => Definition.Name;
    public string Mesh => Definition.Mesh;
    public bool HasPrimaryDye => Definition.DyeablePrimary;
    public bool HasSecondaryDye => Definition.DyeableSecondary;
    public bool IsDyeable => HasPrimaryDye || HasSecondaryDye;
    public Color PreviewColor => GameColorMath.GetItemPreviewColor(Definition);
    public bool IsOwned => _isOwned;
    public bool IsFavorite => _isFavorite;
    public bool IsEquipped => _isEquipped;
    public bool HasGameColorData => Definition.BaseHsv is { Length: >= 3 }
        || Definition.PrimaryDyeHsv is { Length: >= 3 }
        || Definition.SecondaryDyeHsv is { Length: >= 3 }
        || Definition.IconHsv is { Length: >= 3 };
    // icon_hsv is only for catalog artwork. It must never be used to tint the 3D mesh.
    public bool HasModelColorData => Definition.BaseHsv is { Length: >= 3 }
        || Definition.PrimaryDyeHsv is { Length: >= 3 }
        || Definition.SecondaryDyeHsv is { Length: >= 3 };
    public Color ModelPreviewColor => GameColorMath.GetItemModelPreviewColor(Definition);
    public string PreviewColorDetails => HasGameColorData
        ? $"Game HSV color data: {string.Join(", ", Definition.BaseHsv ?? Definition.PrimaryDyeHsv ?? Definition.SecondaryDyeHsv ?? Definition.IconHsv ?? [])}"
        : "No per-item HSV override in OutfitDefs; neutral thumbnail tint is used.";
    public Brush PreviewColorBrush
    {
        get
        {
            var brush = new SolidColorBrush(PreviewColor);
            brush.Freeze();
            return brush;
        }
    }
    public string MainCategory => MainCategoryOverride ?? Definition.Type switch
    {
        "body" or "feet" => "Pants",
        "mask" or "face" or "glasses" or "neck" => "Masks",
        "hair" or "hat" or "horn" => "Hair",
        "wing" => "Capes",
        "prop" or "furniture" or "placeable" => "Items",
        _ => Definition.Type,
    };
    public string Subcategory => SubcategoryOverride ?? Definition.Type switch
    {
        "body" => "Outfits",
        "feet" => "Shoes",
        "mask" => "Masks",
        "face" or "glasses" => "Face accessories",
        "neck" => "Necklaces",
        "hair" => "Hair",
        "horn" => "Hair accessories",
        "hat" => "Head accessories",
        "wing" => string.Empty,
        "prop" => "Held props",
        _ => Definition.Type,
    };
    public string Category => Subcategory.Length == 0 ? MainCategory : Subcategory;
    public int EquippedSlotOrder => Definition.Type.ToLowerInvariant() switch
    {
        "body" => 0,
        "feet" => 1,
        "wing" => 2,
        "hair" => 3,
        "mask" => 4,
        "neck" => 5,
        "face" or "glasses" => 6,
        "hat" or "horn" => 7,
        "prop" or "instrument" => 8,
        _ => 9,
    };
    public string EquippedSlotLabel => Definition.Type.ToLowerInvariant() switch
    {
        "body" => "Body",
        "feet" => "Shoes",
        "wing" => "Capes",
        "hair" => "Hair",
        "mask" => "Masks",
        "face" or "glasses" => "Face",
        "neck" => "Neck",
        "prop" or "instrument" => "Props",
        "hat" or "horn" => "Hair accessories",
        _ => MainCategory,
    };
    public bool CanEquip => Definition.Type is not ("furniture" or "placeable");

    public string DisplayName
    {
        get
        {
            var value = Name.Replace("CharSkyKid_", string.Empty, StringComparison.Ordinal)
                .Replace("_", " ", StringComparison.Ordinal);
            return value;
        }
    }

    public string Availability => MeshPath is null ? "Mesh unavailable" : Category;
}

public static class GameColorMath
{
    public static Color GetItemPreviewColor(OutfitDefinition definition)
    {
        var values = definition.BaseHsv ?? definition.PrimaryDyeHsv ?? definition.SecondaryDyeHsv ?? definition.IconHsv;
        return GetColorFromHsv(values);
    }

    public static Color GetItemModelPreviewColor(OutfitDefinition definition)
    {
        var values = definition.BaseHsv ?? definition.PrimaryDyeHsv ?? definition.SecondaryDyeHsv;
        return GetColorFromHsv(values);
    }

    private static Color GetColorFromHsv(float[]? values)
    {
        if (values is null || values.Length < 3) return Color.FromRgb(190, 187, 205);
        var hue = values[0];
        var saturation = values.Length >= 5 ? values[2] : values[1];
        var value = values.Length >= 5 ? values[4] : values[2];
        return FromHsv(hue, saturation / 100d, value / 100d);
    }

    public static Color FromHsvData(float[]? values) => GetColorFromHsv(values);

    public static Color FromHsv(double hue, double saturation, double value)
    {
        var rgb = ToRgb(hue, saturation, value);
        return Color.FromRgb((byte)Math.Round(rgb.X * 255), (byte)Math.Round(rgb.Y * 255), (byte)Math.Round(rgb.Z * 255));
    }

    public static Vector3 ToRgb(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x),
        };
        return new Vector3((float)(r + m), (float)(g + m), (float)(b + m));
    }
}

public sealed record OutfitPreviewPart(WardrobeEntry Item, SkyModel.Core.Models.MeshData MeshData);

internal sealed class ScalarOrVectorConverter : JsonConverter<float[]>
{
    public override float[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var scalar = reader.GetSingle();
            return [scalar, scalar, scalar];
        }
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected a number or a 3D vector.");
        var values = new List<float>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            if (reader.TokenType == JsonTokenType.Number) values.Add(reader.GetSingle());
            else throw new JsonException("Expected numeric components in a scale vector.");
        if (values.Count == 1) return [values[0], values[0], values[0]];
        if (values.Count != 3) throw new JsonException("Expected three components in a scale vector.");
        return values.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, float[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var component in value) writer.WriteNumberValue(component);
        writer.WriteEndArray();
    }
}

public static class GameCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ScalarOrVectorConverter());
        return options;
    }
    public static (IReadOnlyList<WardrobeEntry> Items, IReadOnlyList<DyeColorDefinition> Dyes) Load(
        string installRoot, IReadOnlyList<MeshCatalogEntry> meshes)
    {
        var resources = Path.Combine(installRoot, "data", "assets", "initial", "Data", "Resources");
        var outfits = ReadList<OutfitDefinition>(Path.Combine(resources, "OutfitDefs.json"));
        var dyes = ReadList<DyeColorDefinition>(Path.Combine(resources, "DyeColorDefs.json"));
        var placeables = ReadList<PlaceableDefinition>(Path.Combine(resources, "PlaceableDefs.json"));

        var preferredMeshes = meshes.OrderBy(m => MeshPriority(m.RelativePath)).ToList();
        var meshPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in preferredMeshes)
            meshPaths.TryAdd(mesh.Name, mesh.FullPath);
        foreach (var mesh in preferredMeshes)
            meshPaths.TryAdd(SkyResourceResolver.GetResourceMeshName(mesh.Name), mesh.FullPath);
        foreach (var mesh in preferredMeshes)
            meshPaths.TryAdd(SkyResourceResolver.NormalizeMeshName(mesh.Name), mesh.FullPath);

        var items = outfits
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Mesh))
            .Select(item => new WardrobeEntry(item, meshPaths.GetValueOrDefault(item.Mesh)))
            .ToList();
        var furnitureCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Furniture", "Chairs", "Tables", "Beds", "Pillows", "Rugs", "Storage", "LightsLarge", "LightsSmall",
            "WallMounted", "Plants", "Plushies", "SmallDecorations", "MiscellaneousLarge", "MiscellaneousSmall",
            "Cooking", "Counters", "Bathing",
        };
        items.AddRange(placeables
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Mesh))
            .Select(item => new WardrobeEntry(new OutfitDefinition
            {
                Name = item.Name,
                Mesh = item.Mesh,
                Type = furnitureCategories.Contains(item.Category) ? "furniture" : "placeable",
                DiffuseTexture = item.DiffuseTexture,
                BaseHsv = item.BaseHsv ?? item.PrimaryDyeHsv ?? item.SecondaryDyeHsv,
                PrimaryDyeHsv = item.PrimaryDyeHsv,
                SecondaryDyeHsv = item.SecondaryDyeHsv,
                AdditionalData = item.AdditionalData is null
                    ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) { ["category"] = JsonSerializer.SerializeToElement(item.Category) }
                    : new Dictionary<string, JsonElement>(item.AdditionalData, StringComparer.OrdinalIgnoreCase) { ["category"] = JsonSerializer.SerializeToElement(item.Category) },
            }, meshPaths.GetValueOrDefault(item.Mesh), "Items",
                furnitureCategories.Contains(item.Category) ? "Furniture" : "Placeable props")));
        return (items, dyes);
    }

    private static List<T> ReadList<T>(string path)
    {
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? [];
    }

    private static int MeshPriority(string relativePath) => relativePath.StartsWith("initial/Data/Meshes/", StringComparison.OrdinalIgnoreCase)
        ? 0
        : relativePath.StartsWith("meshes/Data/Meshes/", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
}
