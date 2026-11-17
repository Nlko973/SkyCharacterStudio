using System.IO;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyModel.Core.Indexing;
using SkyModel.Core.Models;
using SkyModel.Core.Parsing;
using SkyModel.Core.Resources;

namespace SkyCharacterStudio;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public const string DefaultInstall = @"D:\Games\Steam\steamapps\common\Sky Children of the Light";
    private readonly List<MeshCatalogEntry> _allMeshes = [];
    private readonly List<WardrobeEntry> _allItems = [];
    private List<WardrobeEntry> _filteredItems = [];
    private string _searchText = string.Empty;
    private string _selectedCategory = "All";
    private string _selectedSubcategory = "All";
    private string _selectedSeason = "All";
    private string _selectedLanguage = "Русский";
    private string _selectedTheme = "Aurora";
    private bool _useGameItemIcons;
    private string _installPath = DefaultInstall;
    private string _statusText = "Ready";
    private string _catalogSummary = "Choose a game folder to begin";
    private string _selectedName = "—";
    private string _detailsText = "Select a game item to inspect its metadata.";
    private string _ownedButtonText = "Add selected to My items";
    private string _ownedSummary = "My items: 0";
    private string _shaderStatus = "Game Avatar shader sources are not loaded";
    private string _favoriteButtonText = "Добавить в избранное";
    private string _outfitName = "Новый образ";
    private string _builtInShoesButtonText = "Mark as outfit with shoes";
    private IReadOnlyList<DyeColorSwatch> _selectedDyeColors = [];
    private int _pageIndex;
    private int _thumbnailRevision;
    private const int PageSize = 48;
    private WardrobeEntry? _selectedItem;
    private SavedOutfitEntry? _selectedSavedOutfit;
    private IReadOnlyList<OutfitPreviewPart> _sceneParts = [];
    private string _sceneMode = "OUTFIT PREVIEW";
    private int _sceneRevision;
    private readonly Dictionary<string, WardrobeEntry> _equippedByType = new(StringComparer.OrdinalIgnoreCase);
    private WardrobeEntry? _maskBeforeChibi;
    private bool _chibiMaskActive;
    private readonly HashSet<string> _ownedItemNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteItemNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _integratedShoesItemNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string OwnedItemsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "owned-items.json");
    private static readonly string IntegratedShoesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "outfits-with-shoes.json");
    private static readonly string LanguagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "language.txt");
    private static readonly string ThemePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "theme.txt");
    private static readonly string ItemIconModePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "item-icon-mode.txt");
    private static readonly string InstallPathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "game-path.txt");
    private static readonly string FavoritesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "favorites.json");
    private static readonly string SavedOutfitsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "saved-outfits.json");
    private static readonly string SavedOutfitImagesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyCharacterStudio", "saved-outfits");

    public MainViewModel()
    {
        try
        {
            if (File.Exists(LanguagePath))
            {
                var saved = File.ReadAllText(LanguagePath).Trim();
                if (saved is "Русский" or "English") _selectedLanguage = saved;
            }
        }
        catch (IOException) { }
        try
        {
            var savedTheme = File.Exists(ThemePath) ? File.ReadAllText(ThemePath).Trim() : string.Empty;
            if (ThemeOptions.Contains(savedTheme)) _selectedTheme = savedTheme;
        }
        catch (IOException) { }
        try { _useGameItemIcons = File.Exists(ItemIconModePath) && File.ReadAllText(ItemIconModePath).Trim() == "game"; }
        catch (IOException) { }
        try
        {
            var savedPath = File.Exists(InstallPathFile) ? File.ReadAllText(InstallPathFile).Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(savedPath)) _installPath = savedPath;
        }
        catch (IOException) { }
        RefreshCategoryLabels();
        RefreshSubcategories();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<WardrobeEntry> WardrobeItems { get; } = [];
    public ObservableCollection<WardrobeEntry> EquippedItems { get; } = [];
    public ObservableCollection<SavedOutfitEntry> SavedOutfits { get; } = [];
    public ObservableCollection<string> Categories { get; } = ["All"];
    public ObservableCollection<string> Subcategories { get; } = ["All"];
    public ObservableCollection<string> Seasons { get; } = ["All"];
    public ObservableCollection<string> Languages { get; } = ["Русский", "English"];
    public ObservableCollection<string> ThemeOptions { get; } = ["Aurora", "Ocean", "Rose Nebula", "AMOLED", "Black", "Neon Black", "Neon Purple", "Purple Black", "Neon Purple Black", "White", "Sunset", "Forest", "Sky Blue"];
    public string InstallPath { get => _installPath; set => Set(ref _installPath, value); }
    public bool UseGameItemIcons
    {
        get => _useGameItemIcons;
        set
        {
            if (!Set(ref _useGameItemIcons, value)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ItemIconModePath)!);
                File.WriteAllText(ItemIconModePath, value ? "game" : "model");
            }
            catch (IOException) { }
            _ = RefreshItemIconsAsync();
        }
    }

    private async Task RefreshItemIconsAsync()
    {
        var entries = WardrobeItems.Concat(EquippedItems).Distinct().ToArray();
        var results = await Task.Run(() => entries.Select(item => (Item: item, Image: LoadItemIcon(item))).ToArray());
        foreach (var (item, image) in results) item.SetIconImage(image);
    }

    private ImageSource? LoadItemIcon(WardrobeEntry item)
    {
        if (_useGameItemIcons && item.MeshPath is not null && !string.IsNullOrWhiteSpace(item.Definition.Icon))
        {
            var path = SkyResourceResolver.FindTextureFile(item.MeshPath, item.Definition.Icon);
            if (path is not null && GameTextureLoader.Load(path) is BitmapSource icon) return icon;
        }
        return item.MeshPath is null ? null : MeshThumbnailProvider.GetThumbnail(item);
    }
    public void SaveInstallPath()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InstallPathFile)!);
        File.WriteAllText(InstallPathFile, InstallPath);
    }

    public string ImportedSceneBackground { get; private set; } = string.Empty;
    public string ImportedPhotoFolder { get; private set; } = string.Empty;
    public double[] ImportedLightingValues { get; private set; } = [];

    public void ExportProfile(string filePath, string sceneBackground, string photoFolder, double[] lightingValues)
    {
        var bundle = new ProfileTransfer(_selectedLanguage, _selectedTheme, _installPath, sceneBackground, photoFolder, lightingValues,
            _ownedItemNames.OrderBy(x => x).ToArray(), _favoriteItemNames.OrderBy(x => x).ToArray(),
            _integratedShoesItemNames.OrderBy(x => x).ToArray(),
            SavedOutfits.Select(look => new SavedLookTransfer(look.Name, look.ItemNames.ToArray(), look.DyeChoices)).ToArray());
        File.WriteAllText(filePath, JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task ImportProfileAsync(string filePath)
    {
        var bundle = JsonSerializer.Deserialize<ProfileTransfer>(File.ReadAllText(filePath))
            ?? throw new InvalidDataException("The profile file is empty or invalid.");
        if (Languages.Contains(bundle.Language)) SelectedLanguage = bundle.Language;
        if (ThemeOptions.Contains(bundle.Theme)) SelectedTheme = bundle.Theme;
        InstallPath = bundle.InstallPath ?? DefaultInstall;
        ImportedSceneBackground = bundle.SceneBackground ?? string.Empty;
        ImportedPhotoFolder = bundle.PhotoFolder ?? string.Empty;
        ImportedLightingValues = bundle.LightingValues ?? [];
        SaveInstallPath();
        _ownedItemNames.Clear(); _ownedItemNames.UnionWith(bundle.OwnedItems ?? []); PersistOwnedItems();
        _favoriteItemNames.Clear(); _favoriteItemNames.UnionWith(bundle.Favorites ?? []);
        File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(_favoriteItemNames.OrderBy(x => x).ToArray()));
        _integratedShoesItemNames.Clear(); _integratedShoesItemNames.UnionWith(bundle.OutfitsWithShoes ?? []);
        File.WriteAllText(IntegratedShoesPath, JsonSerializer.Serialize(_integratedShoesItemNames.OrderBy(x => x).ToArray()));
        SavedOutfits.Clear();
        foreach (var look in bundle.Looks ?? [])
            SavedOutfits.Add(CreateSavedLookEntry(look.Name, look.ItemNames ?? [], string.Empty, look.DyeChoices));
        PersistSavedOutfits();
        RefreshOwnedControls(); ApplyFilter();
        await Task.CompletedTask;
    }

    public void ExportLook(string filePath, SavedOutfitEntry look) =>
        File.WriteAllText(filePath, JsonSerializer.Serialize(new SavedLookTransfer(look.Name, look.ItemNames.ToArray(), look.DyeChoices), new JsonSerializerOptions { WriteIndented = true }));

    public void ImportLook(string filePath)
    {
        var look = JsonSerializer.Deserialize<SavedLookTransfer>(File.ReadAllText(filePath))
            ?? throw new InvalidDataException("The look file is empty or invalid.");
        SavedOutfits.Add(CreateSavedLookEntry(look.Name, look.ItemNames ?? [], string.Empty, look.DyeChoices));
        SelectedSavedOutfit = SavedOutfits[^1];
        PersistSavedOutfits();
        StatusText = $"Imported look: {look.Name}";
    }
    public IReadOnlyDictionary<string, string> UiText => _selectedLanguage == "Русский" ? RussianUiText : EnglishUiText;
    private static readonly IReadOnlyDictionary<string, string> EnglishUiText = new Dictionary<string, string>
    {
        ["language"] = "LANGUAGE", ["theme"] = "THEME", ["wardrobe"] = "WARDROBE CATALOG", ["scan"] = "Scan game files", ["search"] = "Search item, mesh, or season",
        ["view"] = "View selected item only", ["owned_add"] = "Add selected to My items", ["owned_remove"] = "Remove selected from My items",
        ["shoes_add"] = "Mark as outfit with shoes", ["shoes_remove"] = "Remove from Outfits with shoes",
        ["favorite_add"] = "Add selected to favorites", ["favorite_remove"] = "Remove selected from favorites",
        ["inspector"] = "INSPECTOR", ["collection"] = "MY COLLECTION", ["random"] = "Random outfit from My items",
        ["saved_outfits"] = "SAVED LOOKS", ["save_outfit"] = "Save current look", ["wear_saved"] = "Wear selected look", ["load_saved"] = "Load look", ["delete_saved"] = "Delete look",
        ["current_outfit"] = "YOUR LOOK", ["definition"] = "GAME ITEM DEFINITION",
        ["dyes"] = "GAME DYE COLORS · OPTIONS", ["dye_help"] = "Each item dyes specific areas. Choices are saved; the preview keeps its original colors until those masks are decoded.",
        ["dye_primary"] = "Primary color", ["dye_pattern"] = "Pattern", ["dye_variants"] = "Dye combinations",
        ["dye_primary_blend"] = "Primary blend", ["dye_pattern_blend"] = "Pattern blend",
        ["scene_hint"] = "HSV-colored 3D preview with outfit transforms and skeletal animation; exact game dye masks and lighting are still being matched.",
        ["poses_animations"] = "POSES & ANIMATIONS", ["animation_label"] = "Emotions and animations", ["animation_help"] = "Choose an animation or emote pack; scrub a frame to hold its pose.", ["play"] = "▶  Play", ["pause"] = "Ⅱ  Pause", ["frame"] = "Frame",
        ["orbit"] = "Drag to orbit · Wheel to zoom", ["readonly"] = "GAME FILES · READ ONLY",
        ["all"] = "All", ["favorites"] = "Favorites", ["pants"] = "Pants", ["masks"] = "Masks", ["hair"] = "Hair", ["capes"] = "Capes", ["items"] = "Items",
        ["outfits"] = "Outfits", ["shoes"] = "Shoes", ["outfits_shoes"] = "Outfits with shoes", ["face"] = "Face accessories", ["necklaces"] = "Necklaces",
        ["hair_accessories"] = "Hair accessories", ["head_accessories"] = "Head accessories", ["held"] = "Held props", ["furniture"] = "Furniture", ["placeable"] = "Placeable props",
        ["animation_search"] = "Search animation, emote, or pose", ["static_poses"] = "STATIC POSES", ["pose_stand"] = "Stand", ["pose_sit"] = "Sit", ["pose_squat"] = "Sit 2", ["pose_lie"] = "Lie down",
        ["chibi_mode"] = "Chibi mask", ["chibi_help"] = "Equip Sky's Chibi mask. Character proportions follow the game when its rig morph is supported."
    };
    private static readonly IReadOnlyDictionary<string, string> RussianUiText = new Dictionary<string, string>
    {
        ["dye_help"] = "Выбор красок сохранится. Превью не меняет исходные цвета, пока не восстановлены маски окрашиваемых областей.",
        ["dye_primary"] = "\u041e\u0441\u043d\u043e\u0432\u043d\u043e\u0439 \u0446\u0432\u0435\u0442", ["dye_pattern"] = "\u0423\u0437\u043e\u0440", ["dye_variants"] = "\u0421\u043e\u0447\u0435\u0442\u0430\u043d\u0438\u044f \u0446\u0432\u0435\u0442\u043e\u0432",
        ["dye_primary_blend"] = "\u0421\u043c\u0435\u0448\u0435\u043d\u0438\u0435 \u043e\u0441\u043d\u043e\u0432\u043d\u043e\u0433\u043e", ["dye_pattern_blend"] = "\u0421\u043c\u0435\u0448\u0435\u043d\u0438\u0435 \u0443\u0437\u043e\u0440\u0430",
        ["scene_hint"] = "Цветной 3D-предпросмотр со смещениями предметов и скелетной анимацией; игровые маски покраски и освещение ещё уточняются.",
        ["poses_animations"] = "ПОЗЫ И АНИМАЦИИ", ["animation_label"] = "Анимация персонажа", ["animation_help"] = "Выберите кадр, чтобы зафиксировать позу.", ["play"] = "▶  Старт", ["pause"] = "Ⅱ  Пауза", ["frame"] = "Кадр",
        ["animation_search"] = "\u041f\u043e\u0438\u0441\u043a \u044d\u043c\u043e\u0446\u0438\u0438, \u0430\u043d\u0438\u043c\u0430\u0446\u0438\u0438 \u0438\u043b\u0438 \u043f\u043e\u0437\u044b", ["static_poses"] = "\u0421\u0422\u0410\u0422\u0418\u0427\u041d\u042b\u0415 \u041f\u041e\u0417\u044b", ["pose_stand"] = "\u0421\u0442\u043e\u044f\u0442\u044c", ["pose_sit"] = "\u0421\u0438\u0434\u0435\u0442\u044c", ["pose_squat"] = "\u0421\u0438\u0434\u0435\u0442\u044c 2", ["pose_lie"] = "\u041b\u0435\u0436\u0430\u0442\u044c",
        ["chibi_mode"] = "\u0427\u0438\u0431\u0438-\u043c\u0430\u0441\u043a\u0430", ["chibi_help"] = "\u041d\u0430\u0434\u0435\u0442\u044c \u0447\u0438\u0431\u0438-\u043c\u0430\u0441\u043a\u0443 Sky; \u043f\u0440\u043e\u043f\u043e\u0440\u0446\u0438\u0438 \u0442\u0435\u043b\u0430 \u0432 \u044d\u0442\u043e\u043c \u043f\u0440\u0435\u0432\u044c\u044e \u043f\u043e\u043a\u0430 \u043f\u0440\u0438\u0431\u043b\u0438\u0436\u0451\u043d\u044b.",
        ["language"] = "ЯЗЫК", ["theme"] = "ТЕМА", ["wardrobe"] = "КАТАЛОГ ГАРДЕРОБА", ["scan"] = "Сканировать файлы игры", ["search"] = "Поиск предмета, модели или сезона",
        ["view"] = "Показать только выбранный предмет", ["owned_add"] = "Добавить в «Мои предметы»", ["owned_remove"] = "Убрать из «Моих предметов»",
        ["shoes_add"] = "Отметить как костюм с обувью", ["shoes_remove"] = "Убрать из костюмов с обувью",
        ["favorite_add"] = "Добавить в избранное", ["favorite_remove"] = "Убрать из избранного",
        ["inspector"] = "СВЕДЕНИЯ", ["collection"] = "МОЯ КОЛЛЕКЦИЯ", ["random"] = "Случайный образ из моих предметов",
        ["saved_outfits"] = "СОХРАНЁННЫЕ ОБРАЗЫ", ["save_outfit"] = "Сохранить текущий образ", ["wear_saved"] = "Надеть выбранный образ", ["load_saved"] = "Загрузить образ", ["delete_saved"] = "Удалить образ",
        ["current_outfit"] = "ВАШ ОБРАЗ", ["definition"] = "ДАННЫЕ ИГРОВОГО ПРЕДМЕТА",
        ["dyes"] = "ЦВЕТА ИГРЫ · ВАРИАНТЫ",
        ["orbit"] = "Перетаскивание — вращение · Колесо — масштаб", ["readonly"] = "ФАЙЛЫ ИГРЫ · ТОЛЬКО ЧТЕНИЕ",
        ["all"] = "Все", ["favorites"] = "Избранное", ["pants"] = "Одежда", ["masks"] = "Маски", ["hair"] = "Волосы", ["capes"] = "Накидки", ["items"] = "Предметы",
        ["outfits"] = "Костюмы", ["shoes"] = "Обувь", ["outfits_shoes"] = "Костюмы со встроенной обувью", ["face"] = "Аксессуары для лица", ["necklaces"] = "Ожерелья",
        ["hair_accessories"] = "Аксессуары для волос", ["head_accessories"] = "Аксессуары для головы", ["held"] = "Предметы в руках", ["furniture"] = "Мебель", ["placeable"] = "Размещаемые предметы"
    };
    public IReadOnlyList<DyeColorDefinition> DyeColors { get; private set; } = [];
    public IReadOnlyList<OutfitPreviewPart> SceneParts { get => _sceneParts; private set => Set(ref _sceneParts, value); }
    public string SceneMode { get => _sceneMode; private set => Set(ref _sceneMode, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ShaderStatus { get => _shaderStatus; set => Set(ref _shaderStatus, value); }
    public string CatalogSummary { get => _catalogSummary; private set => Set(ref _catalogSummary, value); }
    public string SelectedName { get => _selectedName; private set => Set(ref _selectedName, value); }
    public string DetailsText { get => _detailsText; private set => Set(ref _detailsText, value); }
    public string OwnedButtonText { get => _ownedButtonText; private set => Set(ref _ownedButtonText, value); }
    public string OwnedSummary { get => _ownedSummary; private set => Set(ref _ownedSummary, value); }
    public string FavoriteButtonText { get => _favoriteButtonText; private set => Set(ref _favoriteButtonText, value); }
    public string OutfitName { get => _outfitName; set => Set(ref _outfitName, value); }
    public SavedOutfitEntry? SelectedSavedOutfit
    {
        get => _selectedSavedOutfit;
        set
        {
            if (Set(ref _selectedSavedOutfit, value)) OnPropertyChanged(nameof(CanWearSavedOutfit));
        }
    }
    public bool CanWearSavedOutfit => SelectedSavedOutfit is not null;
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!ThemeOptions.Contains(value) || !Set(ref _selectedTheme, value)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)!);
                File.WriteAllText(ThemePath, value);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public string BuiltInShoesButtonText { get => _builtInShoesButtonText; private set => Set(ref _builtInShoesButtonText, value); }
    public bool CanMarkBuiltInShoes => SelectedItem is { } entry
        && entry.Definition.Type.Equals("body", StringComparison.OrdinalIgnoreCase)
        && !entry.Definition.IsOnesie;
    public bool CanEquipSelected => SelectedItem?.CanEquip == true;
    public bool CanViewSelected => SelectedItem?.MeshPath is not null;
    public Visibility SubcategoryVisibility => _selectedCategory is "Capes" or "Favorites" ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<DyeColorSwatch> SelectedDyeColors { get => _selectedDyeColors; private set => Set(ref _selectedDyeColors, value); }
    public string PageIndicator => _filteredItems.Count == 0 ? "0 / 0" : $"{_pageIndex + 1} / {(_filteredItems.Count + PageSize - 1) / PageSize}";

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplyFilter(); }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is not ("Русский" or "English") || !Set(ref _selectedLanguage, value)) return;
            RefreshCategoryLabels();
            RefreshSubcategories(preserveSelection: true);
            OnPropertyChanged(nameof(UiText));
            RefreshOwnedControls();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LanguagePath)!);
                File.WriteAllText(LanguagePath, _selectedLanguage);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public string SelectedCategory
    {
        get => LocalizeOption(_selectedCategory);
        set
        {
            var canonical = CanonicalOption(value);
            if (!Set(ref _selectedCategory, canonical)) return;
            RefreshSubcategories();
            OnPropertyChanged(nameof(SubcategoryVisibility));
            ApplyFilter();
        }
    }

    public string SelectedSubcategory
    {
        get => LocalizeOption(_selectedSubcategory);
        set { if (Set(ref _selectedSubcategory, CanonicalOption(value))) ApplyFilter(); }
    }

    public string SelectedSeason
    {
        get => _selectedSeason;
        set { if (Set(ref _selectedSeason, value ?? "All")) ApplyFilter(); }
    }

    public WardrobeEntry? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            RefreshOwnedControls();
            OnPropertyChanged(nameof(CanEquipSelected));
            OnPropertyChanged(nameof(CanViewSelected));
            if (value is null)
            {
                SelectedName = "—";
                DetailsText = "Select a game item to inspect its metadata.";
                if (_equippedByType.Count > 0)
                    _ = BuildOutfitAsync();
                else
                    SceneParts = [];
            }
            else
            {
                _ = SelectItemAsync(value);
            }
        }
    }

    public async Task ScanAsync(string path, bool quiet = false)
    {
        StatusText = "Scanning game resources…";
        CatalogSummary = "Indexing game catalog…";
        try
        {
            var result = await Task.Run(() => SkyInstallScanner.Scan(path));
            var newMeshes = result.Meshes;
            var catalog = await Task.Run(() => GameCatalog.Load(result.InstallRoot, newMeshes));

            // Only replace the active catalog after every source file has been read.
            // A failed rescan must not turn a working wardrobe into an empty one.
            _allMeshes.Clear();
            _allMeshes.AddRange(newMeshes);
            _allItems.Clear();
            _allItems.AddRange(catalog.Items);
            WardrobeItems.Clear();
            EquippedItems.Clear();
            _equippedByType.Clear();
            SelectedItem = null;
            SceneParts = [];
            SelectedName = "—";
            DetailsText = "Mesh metadata will appear here.";
            LoadOwnedItems();
            LoadFavoriteItems();
            LoadIntegratedShoesTags();
            LoadSavedOutfits();
            DyeColors = catalog.Dyes;
            var noDyeBrush = new SolidColorBrush(Colors.Transparent);
            noDyeBrush.Freeze();
            var choices = new[] { new DyeColorChoice("none", "No dye", noDyeBrush) }
                .Concat(DyeColors
                .Where(dye => dye.Name != "none" && string.IsNullOrWhiteSpace(dye.SecondaryDye))
                .OrderBy(dye => dye.Id)
                .Select(dye => new DyeColorChoice(dye.Name, System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(dye.Name), CreateSwatch(dye).Brush)))
                .ToArray();
            foreach (var item in _allItems)
                item.ConfigureDyes(choices, choices, DyeColors);
            Categories.Clear();
            Categories.Add("All");
            Seasons.Clear();
            Seasons.Add("All");
            foreach (var season in _allItems.Select(item => item.Definition.Season.Trim())
                         .Where(season => !string.IsNullOrWhiteSpace(season))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(season => season, StringComparer.OrdinalIgnoreCase))
                Seasons.Add(season);
            if (!Seasons.Contains(_selectedSeason)) SelectedSeason = "All";
            RefreshCategoryLabels();
            RefreshSubcategories();
            SelectedCategory = "All";
            ApplyFilter();
            LoadDefaultOutfit();
            await BuildOutfitAsync();
            var linked = _allItems.Count(item => item.MeshPath is not null);
            if (SceneParts.Count > 0)
                StatusText = $"Loaded {_allItems.Count:N0} catalog items · {linked:N0} mesh links · {DyeColors.Count:N0} dye colors";
        }
        catch (Exception ex)
        {
            if (_allItems.Count > 0)
            {
                ApplyFilter();
                CatalogSummary = $"Kept previous catalog · {_allItems.Count:N0} items · scan failed";
            }
            else
            {
                CatalogSummary = "Game installation could not be read";
            }
            StatusText = quiet
                ? $"Automatic scan failed: {ex.Message}"
                : ex.Message;
            if (!quiet) throw;
        }
    }

    private async Task LoadItemAsync(WardrobeEntry entry, bool showOnly = false)
    {
        var revision = Interlocked.Increment(ref _sceneRevision);
        SelectedName = entry.DisplayName;
        var def = entry.Definition;
        SelectedDyeColors = (def.DyeablePrimary || def.DyeableSecondary)
            ? DyeColors.Where(color => string.IsNullOrWhiteSpace(color.SecondaryDye)).Select(CreateSwatch).ToArray()
            : [];
        DetailsText = $"Category       {entry.MainCategory} / {GetSubcategory(entry)}\nMesh           {entry.Mesh}\nGame icon key  {def.Icon}\nSeason         {def.Season}\nShader         {def.Shader}\nDiffuse        {def.DiffuseTexture}\nMask           {def.MaskTexture}\nAttributes     {def.AttributeTexture}\nPrimary dye    {YesNo(def.DyeablePrimary)}\nSecondary dye  {YesNo(def.DyeableSecondary)}\nBase HSV       {FormatHsv(def.BaseHsv)}\nDefault HSV 1  {FormatHsv(def.PrimaryDyeHsv)}\nDefault HSV 2  {FormatHsv(def.SecondaryDyeHsv)}\n\nGame item key: {def.Name}\n{entry.PreviewColorDetails}\n\nComplete game definition\n{JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = true })}";
        if (entry.MeshPath is null)
        {
            SceneParts = [];
            StatusText = $"No matching mesh file for {entry.Name}";
            return;
        }

        StatusText = $"Loading {entry.Name}…";
        try
        {
            var data = await Task.Run(() => TgcMeshReader.ReadMesh(entry.MeshPath));
            if (!ReferenceEquals(SelectedItem, entry) || revision != _sceneRevision) return;
            DetailsText += $"\n\nVertices   {data.Vertices.Count:N0}\nTriangles  {data.Indices.Count:N0}\nUVs        {data.Uv0.Count:N0}\nWeights    {data.BoneWeights.Count:N0}\nBones      {data.EmbeddedSkeleton?.Count ?? 0:N0}\n\n{Path.GetRelativePath(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(entry.MeshPath)!)!, ".."), entry.MeshPath)}";
            if (showOnly)
            {
                SceneMode = "ITEM PREVIEW · STATIC";
                SceneParts = [new OutfitPreviewPart(entry, data)];
                StatusText = $"Loaded {entry.Name} · static geometry preview";
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(SelectedItem, entry) || revision != _sceneRevision) return;
            SceneParts = [];
            StatusText = $"Could not decode {entry.Name}";
            DetailsText = ex.Message;
        }
    }

    private async Task SelectItemAsync(WardrobeEntry entry)
    {
        await LoadItemAsync(entry);
        if (!ReferenceEquals(SelectedItem, entry) || !entry.CanEquip || entry.MeshPath is null) return;
        var slotItem = GetPrimarySlotItem(entry);
        if (slotItem.Mesh.Equals("Outfit_None", StringComparison.OrdinalIgnoreCase))
            _equippedByType.Remove(entry.Definition.Type);
        else
            _equippedByType[entry.Definition.Type] = slotItem;
        RefreshEquippedItems();
        await BuildOutfitAsync();
    }

    public async Task ViewSelectedOnlyAsync()
    {
        if (SelectedItem is not { MeshPath: not null } entry) return;
        await LoadItemAsync(entry, showOnly: true);
    }

    public async Task EquipSelectedAsync()
    {
        if (SelectedItem is not { CanEquip: true } item) return;
        var type = item.Definition.Type;
        if (item.Mesh.Equals("Outfit_None", StringComparison.OrdinalIgnoreCase))
            _equippedByType.Remove(type);
        else
            _equippedByType[type] = item;
        RefreshEquippedItems();
        await BuildOutfitAsync();
    }

    public async Task UnequipAsync(WardrobeEntry item)
    {
        var slot = _equippedByType.FirstOrDefault(pair => ReferenceEquals(pair.Value, item));
        if (slot.Key is null) return;
        _equippedByType.Remove(slot.Key);
        if (item.Name.Equals("CharSkyKid_Mask_BasicQ", StringComparison.OrdinalIgnoreCase))
            _chibiMaskActive = false;
        RefreshEquippedItems();
        await BuildOutfitAsync();
    }

    public async Task<bool> SetChibiMaskAsync(bool enabled)
    {
        if (_chibiMaskActive == enabled) return true;
        var chibiMask = _allItems.FirstOrDefault(item =>
            item.Name.Equals("CharSkyKid_Mask_BasicQ", StringComparison.OrdinalIgnoreCase)
            && item.CanEquip);
        if (enabled && chibiMask is null)
        {
            StatusText = "The game's Chibi mask mesh was not found. Scan the selected Sky folder first.";
            return false;
        }

        if (enabled)
        {
            _equippedByType.TryGetValue("mask", out _maskBeforeChibi);
            _equippedByType["mask"] = chibiMask!;
        }
        else if (_maskBeforeChibi is not null)
        {
            _equippedByType["mask"] = _maskBeforeChibi;
            _maskBeforeChibi = null;
        }
        else
        {
            _equippedByType.Remove("mask");
        }

        _chibiMaskActive = enabled;
        RefreshEquippedItems();
        await BuildOutfitAsync();
        return true;
    }

    public void ToggleSelectedOwned()
    {
        if (SelectedItem is not { } item) return;
        if (!_ownedItemNames.Add(item.Name)) _ownedItemNames.Remove(item.Name);
        if (!PersistOwnedItems()) return;
        RefreshOwnedControls();
        StatusText = _ownedItemNames.Contains(item.Name)
            ? $"Added {item.DisplayName} to My items"
            : $"Removed {item.DisplayName} from My items";
    }

    public void ToggleSelectedFavorite()
    {
        if (SelectedItem is not { } item) return;
        if (!_favoriteItemNames.Add(item.Name)) _favoriteItemNames.Remove(item.Name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FavoritesPath)!);
            File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(_favoriteItemNames.OrderBy(name => name).ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Could not save favorites";
            return;
        }
        RefreshOwnedControls();
        ApplyFilter();
        StatusText = _favoriteItemNames.Contains(item.Name)
            ? $"Added {item.DisplayName} to favorites"
            : $"Removed {item.DisplayName} from favorites";
    }

    public void ToggleSelectedBuiltInShoesTag()
    {
        if (SelectedItem is not { } entry || !entry.Definition.Type.Equals("body", StringComparison.OrdinalIgnoreCase)
            || entry.Definition.IsOnesie) return;
        var selectedName = SelectedItem.Name;
        if (!_integratedShoesItemNames.Add(selectedName)) _integratedShoesItemNames.Remove(selectedName);
        var isTagged = _integratedShoesItemNames.Contains(selectedName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IntegratedShoesPath)!);
            File.WriteAllText(IntegratedShoesPath, JsonSerializer.Serialize(_integratedShoesItemNames.OrderBy(name => name).ToArray()));
            RefreshOwnedControls();
            ApplyFilter();
            StatusText = isTagged
                ? "Added item to Outfits with shoes"
                : "Removed item from Outfits with shoes";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Could not save outfit shoe tags";
        }
    }

    public async Task GenerateRandomOwnedOutfitAsync()
    {
        var random = new Random();
        var candidates = _allItems
            .Where(item => item.CanEquip && _ownedItemNames.Contains(item.Name) && item.MeshPath is not null
                && !item.Mesh.Equals("Outfit_None", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Definition.Type, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ElementAt(random.Next(group.Count())))
            .ToList();

        if (candidates.Count == 0)
        {
            StatusText = "Mark items as owned before generating a random outfit";
            return;
        }

        _equippedByType.Clear();
        foreach (var item in candidates) _equippedByType[item.Definition.Type] = item;
        RefreshEquippedItems();
        await BuildOutfitAsync();
        StatusText = $"Random outfit built from {candidates.Count} owned item categories";
    }

    public async Task SaveCurrentOutfitAsync()
    {
        var items = _equippedByType.Values.SelectMany(GetSlotParts)
            .Where(item => item.MeshPath is not null).DistinctBy(item => item.Name).ToArray();
        if (items.Length == 0)
        {
            StatusText = "Equip items before saving a look";
            return;
        }

        StatusText = "Creating saved look preview…";
        var preview = await Task.Run(() => MeshThumbnailProvider.GetOutfitThumbnail(items));
        if (preview is null)
        {
            StatusText = "Could not create look preview";
            return;
        }

        var name = string.IsNullOrWhiteSpace(OutfitName) ? $"Look {SavedOutfits.Count + 1}" : OutfitName.Trim();
        var previewPath = Path.Combine(SavedOutfitImagesPath, $"{Guid.NewGuid():N}.png");
        try
        {
            Directory.CreateDirectory(SavedOutfitImagesPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var file = File.Create(previewPath)) encoder.Save(file);
            var itemNames = items.Select(item => item.Name).ToArray();
            var dyeChoices = items.ToDictionary(item => item.Name, item => new SavedDyeChoices(
                item.SelectedPrimaryMainDye?.Name, item.SelectedPrimaryBlendDye?.Name,
                item.SelectedSecondaryMainDye?.Name, item.SelectedSecondaryBlendDye?.Name), StringComparer.OrdinalIgnoreCase);
            var saved = new SavedOutfitEntry(name, itemNames, previewPath, preview, dyeChoices);
            SavedOutfits.Add(saved);
            SelectedSavedOutfit = saved;
            PersistSavedOutfits();
            StatusText = $"Saved look: {name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save look: {ex.Message}";
        }
    }

    public async Task WearSavedOutfitAsync(SavedOutfitEntry? requested = null)
    {
        var saved = requested ?? SelectedSavedOutfit;
        if (saved is null) return;
        _equippedByType.Clear();
        foreach (var name in saved.ItemNames)
        {
            var item = _allItems.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item is null || !item.CanEquip || item.MeshPath is null) continue;
            if (saved.DyeChoices?.TryGetValue(item.Name, out var dyes) == true)
            {
                item.SelectedPrimaryMainDye = item.PrimaryDyeChoices.FirstOrDefault(choice => choice.Name.Equals(dyes.PrimaryMain, StringComparison.OrdinalIgnoreCase));
                item.SelectedPrimaryBlendDye = item.PrimaryDyeChoices.FirstOrDefault(choice => choice.Name.Equals(dyes.PrimaryBlend, StringComparison.OrdinalIgnoreCase));
                item.SelectedSecondaryMainDye = item.SecondaryDyeChoices.FirstOrDefault(choice => choice.Name.Equals(dyes.SecondaryMain, StringComparison.OrdinalIgnoreCase));
                item.SelectedSecondaryBlendDye = item.SecondaryDyeChoices.FirstOrDefault(choice => choice.Name.Equals(dyes.SecondaryBlend, StringComparison.OrdinalIgnoreCase));
            }
            var primary = GetPrimarySlotItem(item);
            _equippedByType[primary.Definition.Type] = primary;
        }
        RefreshEquippedItems();
        await BuildOutfitAsync();
        StatusText = $"Loaded look: {saved.Name}";
    }

    public void DeleteSavedOutfit(SavedOutfitEntry saved)
    {
        if (!SavedOutfits.Remove(saved)) return;
        if (ReferenceEquals(SelectedSavedOutfit, saved)) SelectedSavedOutfit = null;

        try
        {
            var imageRoot = Path.GetFullPath(SavedOutfitImagesPath) + Path.DirectorySeparatorChar;
            var imagePath = Path.GetFullPath(saved.PreviewPath);
            if (imagePath.StartsWith(imageRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(imagePath))
                File.Delete(imagePath);
            PersistSavedOutfits();
            StatusText = $"Deleted saved look: {saved.Name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText = $"Could not delete saved look: {ex.Message}";
        }
    }

    private void LoadSavedOutfits()
    {
        SavedOutfits.Clear();
        SelectedSavedOutfit = null;
        try
        {
            if (!File.Exists(SavedOutfitsPath)) return;
            var saved = JsonSerializer.Deserialize<SavedOutfitFile[]>(File.ReadAllText(SavedOutfitsPath)) ?? [];
            foreach (var entry in saved)
                SavedOutfits.Add(CreateSavedLookEntry(entry.Name, entry.ItemNames, entry.PreviewPath, entry.DyeChoices));
            PersistSavedOutfits();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            StatusText = "Could not load saved looks";
        }
    }

    private SavedOutfitEntry CreateSavedLookEntry(string name, IReadOnlyList<string> itemNames, string? existingPreviewPath, IReadOnlyDictionary<string, SavedDyeChoices>? dyeChoices = null)
    {
        var previewPath = existingPreviewPath ?? string.Empty;
        var preview = !string.IsNullOrWhiteSpace(previewPath) ? LoadPreviewImage(previewPath) : null;
        if (preview is not null) return new SavedOutfitEntry(name, itemNames, previewPath, preview, dyeChoices);
        var items = itemNames.Select(itemName => _allItems.FirstOrDefault(item => item.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase)))
            .OfType<WardrobeEntry>().Where(item => item.MeshPath is not null).ToArray();
        if (items.Length == 0) return new SavedOutfitEntry(name, itemNames, string.Empty, null, dyeChoices);
        var generatedPreview = MeshThumbnailProvider.GetOutfitThumbnail(items);
        if (generatedPreview is null) return new SavedOutfitEntry(name, itemNames, string.Empty, null, dyeChoices);
        try
        {
            Directory.CreateDirectory(SavedOutfitImagesPath);
            previewPath = Path.Combine(SavedOutfitImagesPath, $"{Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(generatedPreview));
            using var output = File.Create(previewPath); encoder.Save(output);
        }
        catch (IOException) { previewPath = string.Empty; }
        return new SavedOutfitEntry(name, itemNames, previewPath, generatedPreview, dyeChoices);
    }

    private void PersistSavedOutfits()
    {
        var records = SavedOutfits.Select(entry => new SavedOutfitFile(entry.Name, entry.ItemNames.ToArray(), entry.PreviewPath, entry.DyeChoices)).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(SavedOutfitsPath)!);
        File.WriteAllText(SavedOutfitsPath, JsonSerializer.Serialize(records));
    }

    private static ImageSource? LoadPreviewImage(string path)
    {
        if (!File.Exists(path)) return null;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void LoadOwnedItems()
    {
        _ownedItemNames.Clear();
        try
        {
            if (!File.Exists(OwnedItemsPath)) return;
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(OwnedItemsPath)) ?? [];
            var availableNames = _allItems.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in saved.Where(availableNames.Contains)) _ownedItemNames.Add(name);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            StatusText = "Could not load saved My items list";
        }
        finally { RefreshOwnedControls(); }
    }

    private void LoadFavoriteItems()
    {
        _favoriteItemNames.Clear();
        try
        {
            if (!File.Exists(FavoritesPath)) return;
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FavoritesPath)) ?? [];
            var available = _allItems.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in saved.Where(available.Contains)) _favoriteItemNames.Add(name);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            StatusText = "Could not load favorites";
        }
        finally { RefreshOwnedControls(); }
    }

    private void LoadIntegratedShoesTags()
    {
        _integratedShoesItemNames.Clear();
        try
        {
            if (!File.Exists(IntegratedShoesPath)) return;
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(IntegratedShoesPath)) ?? [];
            var bodyNames = _allItems.Where(item => item.Definition.Type.Equals("body", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in saved.Where(bodyNames.Contains)) _integratedShoesItemNames.Add(name);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            StatusText = "Could not load outfit shoe tags";
        }
        finally { RefreshOwnedControls(); }
    }

    private bool PersistOwnedItems()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OwnedItemsPath)!);
            File.WriteAllText(OwnedItemsPath, JsonSerializer.Serialize(_ownedItemNames.OrderBy(name => name).ToArray()));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Could not save My items list";
            return false;
        }
    }

    private void RefreshOwnedControls()
    {
        foreach (var entry in _allItems)
        {
            entry.SetOwned(_ownedItemNames.Contains(entry.Name));
            entry.SetFavorite(_favoriteItemNames.Contains(entry.Name));
        }
        OwnedSummary = $"My items: {_ownedItemNames.Count:N0}";
        OwnedButtonText = SelectedItem is { } item && _ownedItemNames.Contains(item.Name)
            ? UiText["owned_remove"] : UiText["owned_add"];
        FavoriteButtonText = SelectedItem is { } favorite && _favoriteItemNames.Contains(favorite.Name)
            ? UiText["favorite_remove"] : UiText["favorite_add"];
        BuiltInShoesButtonText = SelectedItem is { } selected && _integratedShoesItemNames.Contains(selected.Name)
            ? UiText["shoes_remove"] : UiText["shoes_add"];
        OnPropertyChanged(nameof(CanMarkBuiltInShoes));
    }

    private void LoadDefaultOutfit()
    {
        foreach (var item in _allItems.Where(item => item.Definition.IsDefault && item.MeshPath is not null &&
                     !item.Mesh.Equals("Outfit_None", StringComparison.OrdinalIgnoreCase)))
        {
            _equippedByType.TryAdd(item.Definition.Type, item);
        }
        RefreshEquippedItems();
    }

    private void RefreshEquippedItems()
    {
        EquippedItems.Clear();
        var equipped = _equippedByType.Values.ToHashSet();
        foreach (var entry in _allItems) entry.SetEquipped(equipped.Contains(entry));
        foreach (var item in _equippedByType.Values.OrderBy(item => item.EquippedSlotOrder).ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase))
            EquippedItems.Add(item);
        _ = LoadEquippedThumbnailsAsync(EquippedItems.ToArray());
    }

    private async Task LoadEquippedThumbnailsAsync(IReadOnlyList<WardrobeEntry> entries)
    {
        var thumbnails = await Task.Run(() => entries
            .Where(item => item.IconImage is null && item.MeshPath is not null)
            .Select(item => (Item: item, Image: LoadItemIcon(item)))
            .ToArray());
        foreach (var (item, image) in thumbnails)
            if (image is not null) item.SetIconImage(image);
    }

    private async Task BuildOutfitAsync()
    {
        var revision = Interlocked.Increment(ref _sceneRevision);
        var equipped = _equippedByType.Values.SelectMany(GetSlotParts).Where(item => item.MeshPath is not null).ToList();
        SceneMode = "OUTFIT PREVIEW · STATIC";
        StatusText = $"Building outfit from {equipped.Count} equipped items…";
        try
        {
            var parts = await Task.Run(() => equipped.Select(item =>
                new OutfitPreviewPart(item, TgcMeshReader.ReadMesh(item.MeshPath!))).ToList());
            if (revision != _sceneRevision) return;
            SceneParts = parts;
            StatusText = $"Outfit preview · {parts.Count} static game meshes";
        }
        catch (Exception ex)
        {
            if (revision != _sceneRevision) return;
            SceneParts = [];
            StatusText = $"Could not build outfit: {ex.Message}";
        }
    }

    private WardrobeEntry GetPrimarySlotItem(WardrobeEntry item)
    {
        if (!item.Mesh.EndsWith("Strokes", StringComparison.OrdinalIgnoreCase)) return item;
        var baseMesh = item.Mesh[..^"Strokes".Length];
        return _allItems.FirstOrDefault(candidate => candidate.Definition.Type.Equals(item.Definition.Type, StringComparison.OrdinalIgnoreCase)
            && candidate.Mesh.Equals(baseMesh, StringComparison.OrdinalIgnoreCase)) ?? item;
    }

    private IEnumerable<WardrobeEntry> GetSlotParts(WardrobeEntry item)
    {
        yield return item;
        var companionMesh = item.Mesh + "Strokes";
        var companion = _allItems.FirstOrDefault(candidate => candidate.Definition.Type.Equals(item.Definition.Type, StringComparison.OrdinalIgnoreCase)
            && candidate.Mesh.Equals(companionMesh, StringComparison.OrdinalIgnoreCase) && candidate.MeshPath is not null);
        if (companion is not null) yield return companion;
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var filtered = _allItems.Where(item => (_selectedCategory is "All" or "Favorites"
                || item.MainCategory.Equals(_selectedCategory, StringComparison.OrdinalIgnoreCase))
            && (_selectedCategory != "Favorites" || _favoriteItemNames.Contains(item.Name))
            && (_selectedSubcategory == "All" || GetSubcategory(item).Equals(_selectedSubcategory, StringComparison.OrdinalIgnoreCase))
            && (_selectedSeason == "All" || item.Definition.Season.Equals(_selectedSeason, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrEmpty(query))
            filtered = filtered.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Mesh.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Definition.Season.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.SearchMetadata.Contains(query, StringComparison.OrdinalIgnoreCase));
        _filteredItems = filtered.ToList();
        if (SelectedItem is not null && !_filteredItems.Contains(SelectedItem))
            SelectedItem = null;
        _pageIndex = 0;
        UpdateVisiblePage();
    }

    public void ChangePage(int offset)
    {
        var pageCount = Math.Max(1, (_filteredItems.Count + PageSize - 1) / PageSize);
        _pageIndex = Math.Clamp(_pageIndex + offset, 0, pageCount - 1);
        UpdateVisiblePage();
    }

    private void UpdateVisiblePage()
    {
        WardrobeItems.Clear();
        var visibleItems = _filteredItems.Skip(_pageIndex * PageSize).Take(PageSize).ToArray();
        foreach (var entry in visibleItems) WardrobeItems.Add(entry);
        CatalogSummary = _allItems.Count == 0 ? "Choose a game folder to begin" : $"{_filteredItems.Count:N0} matches of {_allItems.Count:N0} · page {PageIndicator}";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PageIndicator)));
        _ = LoadPageThumbnailsAsync(visibleItems, Interlocked.Increment(ref _thumbnailRevision));
    }

    private string GetSubcategory(WardrobeEntry item) =>
        item.Definition.Type.Equals("body", StringComparison.OrdinalIgnoreCase)
            && (item.Definition.IsOnesie || _integratedShoesItemNames.Contains(item.Name))
            ? "Outfits with shoes"
            : item.Subcategory;

    private void RefreshSubcategories(bool preserveSelection = false)
    {
        var selectedBeforeRefresh = _selectedSubcategory;
        var values = _selectedCategory switch
        {
            "Pants" => new[] { "All", "Outfits", "Shoes", "Outfits with shoes" },
            "Masks" => new[] { "All", "Masks", "Face accessories", "Necklaces" },
            "Hair" => new[] { "All", "Hair", "Hair accessories", "Head accessories" },
            "Items" => new[] { "All", "Held props", "Furniture", "Placeable props" },
            "Favorites" => new[] { "All" },
            "All" => new[] { "All", "Outfits", "Shoes", "Outfits with shoes", "Masks", "Face accessories", "Necklaces", "Hair", "Hair accessories", "Head accessories", "Held props", "Furniture", "Placeable props" },
            _ => new[] { "All" },
        };
        if (preserveSelection && Subcategories.Count == values.Length)
        {
            for (var index = 0; index < values.Length; index++) Subcategories[index] = LocalizeOption(values[index]);
            _selectedSubcategory = selectedBeforeRefresh;
            OnPropertyChanged(nameof(SelectedSubcategory));
            return;
        }
        Subcategories.Clear();
        foreach (var value in values) Subcategories.Add(LocalizeOption(value));
        if (!_selectedSubcategory.Equals("All", StringComparison.Ordinal))
        {
            _selectedSubcategory = "All";
            OnPropertyChanged(nameof(SelectedSubcategory));
        }
    }

    private void RefreshCategoryLabels()
    {
        var selectedBeforeRefresh = _selectedCategory;
        var values = new[] { "All", "Favorites", "Pants", "Masks", "Hair", "Capes", "Items" };
        if (Categories.Count == values.Length)
        {
            for (var index = 0; index < values.Length; index++) Categories[index] = LocalizeOption(values[index]);
        }
        else
        {
            Categories.Clear();
            foreach (var value in values) Categories.Add(LocalizeOption(value));
        }
        _selectedCategory = selectedBeforeRefresh;
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(SelectedSubcategory));
    }

    private string LocalizeOption(string option)
    {
        var key = option switch
        {
            "All" => "all", "Favorites" => "favorites", "Pants" => "pants", "Masks" => "masks", "Hair" => "hair", "Capes" => "capes", "Items" => "items",
            "Outfits" => "outfits", "Shoes" => "shoes", "Outfits with shoes" => "outfits_shoes", "Face accessories" => "face", "Necklaces" => "necklaces",
            "Hair accessories" => "hair_accessories", "Head accessories" => "head_accessories", "Held props" => "held", "Furniture" => "furniture", "Placeable props" => "placeable",
            _ => string.Empty
        };
        return key.Length == 0 ? option : UiText[key];
    }

    private string CanonicalOption(string? option)
    {
        if (string.IsNullOrWhiteSpace(option)) return "All";
        var canonicalValues = new[] { "All", "Favorites", "Pants", "Masks", "Hair", "Capes", "Items", "Outfits", "Shoes", "Outfits with shoes", "Face accessories", "Necklaces", "Hair accessories", "Head accessories", "Held props", "Furniture", "Placeable props" };
        return canonicalValues.FirstOrDefault(value => value.Equals(option, StringComparison.OrdinalIgnoreCase)
            || LocalizeOption(value).Equals(option, StringComparison.OrdinalIgnoreCase)) ?? option;
    }

    private async Task LoadPageThumbnailsAsync(IReadOnlyList<WardrobeEntry> entries, int revision)
    {
        var thumbnails = await Task.Run(() => entries
            .Where(item => item.MeshPath is not null)
            .Select(item => (Item: item, Image: LoadItemIcon(item)))
            .ToArray());
        if (revision != _thumbnailRevision) return;
        foreach (var (item, image) in thumbnails) item.SetIconImage(image);
    }

    private static string YesNo(bool value) => value ? "available in item data" : "not declared";

    private static string FormatHsv(float[]? values) => values is { Length: > 0 }
        ? string.Join(", ", values.Select(value => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))
        : "item default";

    private static DyeColorSwatch CreateSwatch(DyeColorDefinition dye)
    {
        var hsv = dye.Hsv;
        var color = hsv.Length >= 3 ? GameColorMath.FromHsv(hsv[0], hsv[1] / 100d, hsv[2] / 100d) : Colors.Transparent;
        var details = $"{dye.Name} · HSV {string.Join(", ", hsv)}\nPrimary: {dye.PrimaryDye}\nSecondary: {dye.SecondaryDye}";
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return new DyeColorSwatch(dye.Name, details, brush);
    }

    private sealed record SavedOutfitFile(string Name, string[] ItemNames, string PreviewPath, IReadOnlyDictionary<string, SavedDyeChoices>? DyeChoices = null);
    private sealed record SavedLookTransfer(string Name, string[]? ItemNames, IReadOnlyDictionary<string, SavedDyeChoices>? DyeChoices = null);
    private sealed record ProfileTransfer(string Language, string Theme, string InstallPath, string SceneBackground, string PhotoFolder, double[]? LightingValues, string[]? OwnedItems, string[]? Favorites, string[]? OutfitsWithShoes, SavedLookTransfer[]? Looks);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
