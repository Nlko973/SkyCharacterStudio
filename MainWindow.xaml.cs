using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SkyModel.Core.Models;
using SkyModel.Core.Parsing;
using SkyModel.Core.Resources;
using System.Numerics;

namespace SkyCharacterStudio;

internal sealed record AnimationPackOption(string Name, string Path)
{
    public string DisplayName
    {
        get
        {
            const string emotePrefix = "CharKidAnimEmote_";
            if (Name.StartsWith(emotePrefix, StringComparison.OrdinalIgnoreCase))
                return $"Emotes · {Name[emotePrefix.Length..]}";
            const string animationPrefix = "CharKidAnim";
            return Name.StartsWith(animationPrefix, StringComparison.OrdinalIgnoreCase)
                ? $"Animations · {Name[animationPrefix.Length..]}"
                : Name;
        }
    }
}
internal sealed record AnimationSegmentOption(string Name, string Glyph, BitmapSource? Icon, SkyAnimationClip Clip);
internal sealed record BackgroundPresetOption(string Key, string Russian, string English)
{
    public string Label => English;
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private Point _lastMouse;
    private bool _orbiting;
    private bool _chibiMode;
    private static readonly string SceneBackgroundPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyCharacterStudio", "scene-background.txt");
    private static readonly string PhotoFolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyCharacterStudio", "photo-folder.txt");
    private string _photoFolder = string.Empty;
    private string _customSceneBackgroundHex = "#00FF00";
    private Color _sceneBackgroundColor = (Color)ColorConverter.ConvertFromString("#191C27");
    private double _yaw = -0.7;
    private double _pitch = 0.15;
    private double _distance = 5;
    private Point3D _target;
    private readonly Dictionary<TranslateTransform, double> _scrollingTextures = [];
    private readonly DateTime _animationEpoch = DateTime.UtcNow;
    private readonly System.Windows.Threading.DispatcherTimer _animationTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private int _shaderLoadRevision;
    private GameAvatarEffectsManager? _gameEffectsManager;
    private readonly HashSet<WardrobeEntry> _dyeObservedItems = [];
    private readonly List<GameAvatarMaterialCore> _avatarMaterials = [];
    private readonly List<AnimatedMeshBinding> _animatedMeshes = [];
    private readonly List<AnimatedPropBinding> _animatedProps = [];
    private List<AnimationSegmentOption> _animationSegments = [];
    private SkyAnimationClip? _activeAnimation;
    private bool _isPosePlaying;
    private bool _loadingAnimationPacks;
    private int _animationLoadRevision;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        ApplyTheme(_viewModel.SelectedTheme);
        LoadSceneBackground();
        LoadPhotoFolder();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        GameShaderViewport.RenderExceptionOccurred += (_, args) =>
            Dispatcher.BeginInvoke(() =>
            {
                GameShaderViewport.Visibility = Visibility.Collapsed;
                Viewport.Visibility = Visibility.Visible;
                _viewModel.ShaderStatus = $"Direct3D Avatar render failed; showing the textured preview instead: {args.Exception.Message}";
            });
        CameraChanged();
        _animationTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - _animationEpoch).TotalSeconds;
            var delta = (float)_animationTimer.Interval.TotalSeconds;
            foreach (var (transform, speed) in _scrollingTextures)
                transform.X = (elapsed * speed) % 1.0;
            if (GameShaderViewport.Visibility == Visibility.Visible)
                foreach (var material in _avatarMaterials)
                    material.UpdateAnimatedValues((float)elapsed, delta);
            if (_isPosePlaying && _activeAnimation is { FrameCount: > 1 })
            {
                var next = (int)AnimationFrameSlider.Value + 1;
                if (next >= _activeAnimation.FrameCount) next = 0;
                AnimationFrameSlider.Value = next;
            }
        };
        _animationTimer.Start();
        Closed += (_, _) => _animationTimer.Stop();
        Closed += (_, _) => _gameEffectsManager?.Dispose();
        Loaded += async (_, _) => await ScanAsync(_viewModel.InstallPath, quiet: true);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Sky: Children of the Light install folder", InitialDirectory = Directory.Exists(_viewModel.InstallPath) ? _viewModel.InstallPath : MainViewModel.DefaultInstall };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.InstallPath = dialog.FolderName;
            _viewModel.SaveInstallPath();
            await ScanAsync(dialog.FolderName);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Window? settingsWindow = null;
        StackPanel? settingsContent = null;
        var language = new ComboBox { ItemsSource = _viewModel.Languages, SelectedItem = _viewModel.SelectedLanguage, MinWidth = 220, Margin = new Thickness(0, 6, 0, 14) };
        var theme = new ComboBox { ItemsSource = _viewModel.ThemeOptions, SelectedItem = _viewModel.SelectedTheme, MinWidth = 220, Margin = new Thickness(0, 6, 0, 14) };
        theme.SelectionChanged += (_, _) => { if (theme.SelectedItem is string value) _viewModel.SelectedTheme = value; };
        var itemIcons = new CheckBox { IsChecked = _viewModel.UseGameItemIcons, Tag = "itemIcons", Margin = new Thickness(0, 2, 0, 12), Foreground = (Brush)Application.Current.Resources["TextPrimary"] };
        itemIcons.Checked += (_, _) => _viewModel.UseGameItemIcons = true;
        itemIcons.Unchecked += (_, _) => _viewModel.UseGameItemIcons = false;
        var background = new ComboBox
        {
            ItemsSource = new[]
            {
                new BackgroundPresetOption("Theme panel", "\u0426\u0432\u0435\u0442 \u0442\u0435\u043c\u044b", "Theme panel"),
                new BackgroundPresetOption("Chroma key green", "\u0425\u0440\u043e\u043c\u0430\u043a\u0435\u0439: \u0437\u0435\u043b\u0451\u043d\u044b\u0439", "Chroma key green"),
                new BackgroundPresetOption("Chroma key blue", "\u0425\u0440\u043e\u043c\u0430\u043a\u0435\u0439: \u0441\u0438\u043d\u0438\u0439", "Chroma key blue"),
                new BackgroundPresetOption("Black", "\u0427\u0451\u0440\u043d\u044b\u0439", "Black"),
                new BackgroundPresetOption("White", "\u0411\u0435\u043b\u044b\u0439", "White"),
                new BackgroundPresetOption("Custom color", "\u0421\u0432\u043e\u0439 \u0446\u0432\u0435\u0442", "Custom color"),
            }, DisplayMemberPath = _viewModel.SelectedLanguage == "English" ? nameof(BackgroundPresetOption.English) : nameof(BackgroundPresetOption.Russian),
            MinWidth = 220, Margin = new Thickness(0, 6, 0, 5)
        };
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is string value) _viewModel.SelectedLanguage = value;
            if (settingsWindow is not null && settingsContent is not null) ApplySettingsLanguage(settingsWindow, settingsContent, background);
        };
        background.SelectedItem = background.Items.Cast<BackgroundPresetOption>().First(option => option.Key == GetSceneBackgroundPreset());
        var backgroundHex = new TextBox { Text = _customSceneBackgroundHex, Margin = new Thickness(0, 3, 0, 12), MinWidth = 130, MaxWidth = 150, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Hex color, for example #00FF00" };
        background.SelectionChanged += (_, _) => ApplySceneBackgroundPreset((background.SelectedItem as BackgroundPresetOption)?.Key, backgroundHex.Text);
        backgroundHex.TextChanged += (_, _) =>
        {
            if ((background.SelectedItem as BackgroundPresetOption)?.Key == "Custom color") ApplySceneBackgroundPreset("Custom color", backgroundHex.Text);
        };
        backgroundHex.Text = _customSceneBackgroundHex;
        var path = new TextBox { Text = _viewModel.InstallPath, Margin = new Thickness(0, 6, 0, 7), MinWidth = 360 };
        var chooseFolder = new Button { Content = "Choose game folder…", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left };
        chooseFolder.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Select Sky: Children of the Light install folder", InitialDirectory = Directory.Exists(path.Text) ? path.Text : MainViewModel.DefaultInstall };
            if (picker.ShowDialog(this) == true) path.Text = picker.FolderName;
        };
        var scan = new Button { Content = "Save path and scan", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var photoFolder = new TextBlock { Tag = "photoFolderValue", Text = string.IsNullOrWhiteSpace(_photoFolder) ? "Choose on first photo" : _photoFolder, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 7) };
        var changePhotoFolder = new Button { Content = "Change photo folder…", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left };
        changePhotoFolder.Click += (_, _) => { ChangePhotoFolder_Click(this, new RoutedEventArgs()); photoFolder.Text = string.IsNullOrWhiteSpace(_photoFolder) ? "Choose on first photo" : _photoFolder; if (settingsWindow is not null && settingsContent is not null) ApplySettingsLanguage(settingsWindow, settingsContent, background); };
        var exportProfile = new Button { Content = "Export settings and all looks…", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        exportProfile.Click += (_, _) => ExportProfile_Click();
        var importProfile = new Button { Content = "Import settings and all looks…", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        importProfile.Click += async (_, _) => await ImportProfile_ClickAsync();
        var importLook = new Button { Content = "Import one look…", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        importLook.Click += (_, _) => ImportLook_Click();
        settingsWindow = new Window
        {
            Title = "Settings", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 620, Height = 690, MinWidth = 560, MinHeight = 590, ResizeMode = ResizeMode.CanResize,
            Background = (Brush)Application.Current.Resources["Canvas"], Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            Content = new Border
            {
                Padding = new Thickness(22), Margin = new Thickness(10), CornerRadius = new CornerRadius(17),
                Background = (Brush)Application.Current.Resources["Panel"], BorderBrush = (Brush)Application.Current.Resources["PanelRaised"], BorderThickness = new Thickness(1),
                Child = null
            }
        };
        settingsContent = CreateSettingsContent(language, theme, background, backgroundHex, itemIcons, path, chooseFolder, scan, photoFolder, changePhotoFolder, exportProfile, importProfile, importLook);
        ((Border)settingsWindow.Content).Child = settingsContent;
        ApplySettingsLanguage(settingsWindow, settingsContent, background);
        scan.Click += async (_, _) =>
        {
            _viewModel.InstallPath = path.Text.Trim();
            try { _viewModel.SaveInstallPath(); await ScanAsync(_viewModel.InstallPath); settingsWindow.Close(); }
            catch (Exception ex) { MessageBox.Show(settingsWindow, ex.Message, "Unable to scan game files", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        settingsWindow.ShowDialog();
    }

    private static StackPanel CreateSettingsContent(ComboBox language, ComboBox theme, ComboBox background, TextBox backgroundHex, CheckBox itemIcons, TextBox path, Button chooseFolder, Button scan, TextBlock photoFolder, Button changePhotoFolder, Button exportProfile, Button importProfile, Button importLook)
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Tag = "title", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["TextPrimary"] });
        root.Children.Add(new TextBlock { Tag = "subtitle", FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextMuted"], Margin = new Thickness(0, 3, 0, 16) });
        var tabs = new TabControl { Style = (Style)Application.Current.Resources["SettingsTabs"], ItemContainerStyle = (Style)Application.Current.Resources["SettingsTabItem"] };
        StackPanel Page() => new() { Margin = new Thickness(7), MinWidth = 460 };
        void Label(StackPanel page, string value) => page.Children.Add(new TextBlock { Tag = value, Foreground = (Brush)Application.Current.Resources["TextMuted"], FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        var appearance = Page();
        Label(appearance, "language"); appearance.Children.Add(language);
        Label(appearance, "theme"); appearance.Children.Add(theme);
        Label(appearance, "itemDisplay"); appearance.Children.Add(itemIcons);
        Label(appearance, "background"); appearance.Children.Add(background); appearance.Children.Add(backgroundHex);
        Label(appearance, "photoFolderLabel"); appearance.Children.Add(photoFolder); changePhotoFolder.Tag = "changePhoto"; appearance.Children.Add(changePhotoFolder);
        tabs.Items.Add(new TabItem { Header = "◐  Appearance", Content = appearance });

        var game = Page();
        Label(game, "gamePath"); chooseFolder.Tag = "chooseGame"; scan.Tag = "scanGame"; game.Children.Add(path); game.Children.Add(chooseFolder); game.Children.Add(scan);
        tabs.Items.Add(new TabItem { Header = "◈  Game files", Content = game });

        var profile = Page();
        profile.Children.Add(new TextBlock { Tag = "profileHeading", FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["Accent"] });
        profile.Children.Add(new TextBlock { Tag = "profileDescription", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 10), Foreground = (Brush)Application.Current.Resources["TextMuted"] });
        profile.Children.Add(exportProfile); profile.Children.Add(importProfile); profile.Children.Add(importLook);
        profile.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["PanelRaised"], Margin = new Thickness(0, 18, 0, 12) });
        profile.Children.Add(new TextBlock { Tag = "aboutHeading", FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.Resources["Accent"] });
        profile.Children.Add(new TextBlock { Tag = "aboutText", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 3), Foreground = (Brush)Application.Current.Resources["TextMuted"] });
        var github = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("◉  GitHub"))
        {
            NavigateUri = new Uri("https://github.com/Nlko973"),
        };
        github.RequestNavigate += (_, args) => Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
        var githubLine = new TextBlock();
        githubLine.Inlines.Add(github);
        profile.Children.Add(githubLine);
        tabs.Items.Add(new TabItem { Header = "⇄  Profile & About", Content = profile });
        root.Children.Add(tabs);
        return root;
    }

    private void ApplySettingsLanguage(Window window, StackPanel content, ComboBox background)
    {
        var en = _viewModel.SelectedLanguage == "English";
        window.Title = en ? "Settings" : "\u041d\u0430\u0441\u0442\u0440\u0443\u043a\u0438";
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var ru = new Dictionary<string, string>
        {
            ["title"]="\u041d\u0410\u0421\u0422\u0420\u041e\u0419\u041a\u0418", ["subtitle"]="\u041d\u0430\u0441\u0442\u0440\u043e\u0439\u0442\u0435 \u0440\u0435\u0434\u0430\u043a\u0442\u043e\u0440 \u0438 \u0443\u043f\u0440\u0430\u0432\u043b\u044f\u0439\u0442\u0435 \u043f\u0440\u043e\u0444\u0438\u043b\u0435\u043c Sky",
            ["language"]="\u042f\u0437\u044b\u043a", ["theme"]="\u0422\u0435\u043c\u0430", ["background"]="\u0424\u043e\u043d \u0441\u0446\u0435\u043d\u044b", ["photoFolderLabel"]="\u041f\u0430\u043f\u043a\u0430 \u0441\u043d\u0438\u043c\u043a\u043e\u0432 \u0441\u0446\u0435\u043d\u044b", ["photoFolderValue"]=string.IsNullOrWhiteSpace(_photoFolder) ? "\u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u043f\u0430\u043f\u043a\u0443 \u043f\u0440\u0438 \u043f\u0435\u0440\u0432\u043e\u043c \u0441\u043d\u0438\u043c\u043a\u0435" : _photoFolder,
            ["gamePath"]="\u041f\u0430\u043f\u043a\u0430 Sky: Children of the Light", ["profileHeading"]="\u041f\u0420\u041e\u0424\u0418\u041b\u042c \u0418 \u041e\u0411\u0420\u0410\u0417\u042b",
            ["profileDescription"]="\u042d\u043a\u0441\u043f\u043e\u0440\u0442 \u0438 \u0438\u043c\u043f\u043e\u0440\u0442 \u043d\u0430\u0441\u0442\u0440\u043e\u0435\u043a, \u043a\u043e\u043b\u043b\u0435\u043a\u0446\u0438\u0439 \u0438 \u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d\u043d\u044b\u0445 \u043e\u0431\u0440\u0430\u0437\u043e\u0432. \u041e\u0431\u0440\u0430\u0437 \u043c\u043e\u0436\u043d\u043e \u043f\u0435\u0440\u0435\u043d\u043e\u0441\u0438\u0442\u044c \u043e\u0442\u0434\u0435\u043b\u044c\u043d\u043e.",
            ["aboutHeading"]="\u041e \u041f\u0420\u041e\u0413\u0420\u0410\u041c\u041c\u0415", ["aboutText"]=$"Sky Character Studio\n\u0420\u0435\u0434\u0430\u043a\u0442\u043e\u0440 \u043f\u0435\u0440\u0441\u043e\u043d\u0430\u0436\u0430 \u0438 \u043e\u0434\u0435\u0436\u0434\u044b \u0434\u043b\u044f Sky: Children of the Light.\n\u0412\u0435\u0440\u0441\u0438\u044f {version}\n\u0420\u0430\u0437\u0440\u0430\u0431\u043e\u0442\u0447\u0438\u043a: Niko",
            ["changePhoto"]="\u0418\u0437\u043c\u0435\u043d\u0438\u0442\u044c \u043f\u0430\u043f\u043a\u0443 \u0441\u043d\u0438\u043c\u043a\u043e\u0432...", ["chooseGame"]="\u0412\u044b\u0431\u0440\u0430\u0442\u044c \u043f\u0430\u043f\u043a\u0443 \u0438\u0433\u0440\u044b...", ["scanGame"]="\u0421\u043e\u0445\u0440\u0430\u043d\u0438\u0442\u044c \u043f\u0443\u0442\u044c \u0438 \u0441\u043a\u0430\u043d\u0438\u0440\u043e\u0432\u0430\u0442\u044c",
            ["exportProfile"]="\u042d\u043a\u0441\u043f\u043e\u0440\u0442 \u043d\u0430\u0441\u0442\u0440\u043e\u0435\u043a \u0438 \u0432\u0441\u0435\u0445 \u043e\u0431\u0440\u0430\u0437\u043e\u0432...", ["importProfile"]="\u0418\u043c\u043f\u043e\u0440\u0442 \u043d\u0430\u0441\u0442\u0440\u043e\u0435\u043a \u0438 \u0432\u0441\u0435\u0445 \u043e\u0431\u0440\u0430\u0437\u043e\u0432...", ["importLook"]="\u0418\u043c\u043f\u043e\u0440\u0442 \u043e\u0434\u043d\u043e\u0433\u043e \u043e\u0431\u0440\u0430\u0437\u0430..."
        };
        var enText = new Dictionary<string, string>
        {
            ["title"]="SETTINGS", ["subtitle"]="Personalize the editor and manage your Sky profile", ["language"]="Language", ["theme"]="Theme", ["background"]="Scene background", ["photoFolderLabel"]="\u041f\u0430\u043f\u043a\u0430 \u0441\u043d\u0438\u043c\u043a\u043e\u0432 \u0441\u0446\u0435\u043d\u044b", ["photoFolderValue"]=string.IsNullOrWhiteSpace(_photoFolder) ? "\u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u043f\u0430\u043f\u043a\u0443 \u043f\u0440\u0438 \u043f\u0435\u0440\u0432\u043e\u043c \u0441\u043d\u0438\u043c\u043a\u0435" : _photoFolder, ["gamePath"]="Sky: Children of the Light location", ["profileHeading"]="PROFILE & LOOKS", ["profileDescription"]="Export or import application settings, collections, and saved looks. A single look can also be transferred on its own.", ["aboutHeading"]="ABOUT", ["aboutText"]=$"Sky Character Studio\nCharacter and outfit editor for Sky: Children of the Light.\nVersion {version}\nDeveloper: Niko", ["changePhoto"]="Change photo folder...", ["chooseGame"]="Choose game folder...", ["scanGame"]="Save path and scan", ["exportProfile"]="Export settings and all looks...", ["importProfile"]="Import settings and all looks...", ["importLook"]="Import one look..."
        };
        var labels = en ? enText : ru;
        foreach (var element in Descendants(content))
        {
            if (element.Tag is "itemDisplay")
            {
                if (element is TextBlock displayLabel) displayLabel.Text = en ? "Item display" : "Отображение предметов";
                continue;
            }
            if (element.Tag is "itemIcons" && element is CheckBox gameIcons)
            {
                gameIcons.Content = en ? "Use game icons instead of 3D previews" : "Игровые иконки вместо 3D-моделей";
                continue;
            }
            if (element.Tag is not string key || !labels.TryGetValue(key, out var label)) continue;
            if (element is TextBlock textBlock) textBlock.Text = label;
            else if (element is Button button) button.Content = label;
        }
        var tabItems = Descendants(content).OfType<TabItem>().ToArray();
        if (tabItems.Length >= 3)
        {
            tabItems[0].Header = en ? "◌  Appearance" : "\u25cc  \u0412\u043d\u0435\u0448\u043d\u0438\u0439 \u0432\u0438\u0434";
            tabItems[1].Header = en ? "◈  Game files" : "\u25c8  \u0424\u0430\u0439\u043b\u044b \u0438\u0433\u0440\u044b";
            tabItems[2].Header = en ? "↔  Profile & About" : "\u2194  \u041f\u0440\u043e\u0444\u0438\u043b\u044c \u0438 \u043e \u043f\u0440\u043e\u0433\u0440\u0430\u043c\u043c\u0435";
        }
        background.DisplayMemberPath = en ? nameof(BackgroundPresetOption.English) : nameof(BackgroundPresetOption.Russian);
        background.Items.Refresh();
    }
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
    {
        IEnumerable<DependencyObject> children = parent switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            ItemsControl items => items.Items.OfType<DependencyObject>(),
            ContentControl control when control.Content is DependencyObject content => [content],
            Decorator decorator when decorator.Child is not null => [decorator.Child],
            _ => Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(parent)).Select(index => VisualTreeHelper.GetChild(parent, index)),
        };
        foreach (var child in children)
        {
            if (child is FrameworkElement element) yield return element;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private void LoadSceneBackground()
    {
        try
        {
            if (File.Exists(SceneBackgroundPath))
            {
                var saved = File.ReadAllText(SceneBackgroundPath).Trim();
                if (ColorConverter.ConvertFromString(saved) is Color color)
                {
                    ApplySceneBackground(color);
                    _customSceneBackgroundHex = saved;
                    return;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        ApplySceneBackground((Color)ColorConverter.ConvertFromString("#191C27"));
    }

    private void ApplySceneBackgroundPreset(string? preset, string customHex)
    {
        var hex = preset switch
        {
            "Theme panel" => GetThemePanelColor().ToString(),
            "Chroma key green" => "#00FF00",
            "Chroma key blue" => "#0000FF",
            "Black" => "#000000",
            "White" => "#FFFFFF",
            "Custom color" => customHex,
            _ => null,
        };
        if (hex is null || ColorConverter.ConvertFromString(hex) is not Color color) return;
        if (preset == "Custom color") _customSceneBackgroundHex = hex;
        ApplySceneBackground(color);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SceneBackgroundPath)!);
            File.WriteAllText(SceneBackgroundPath, color.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ApplySceneBackground(Color color)
    {
        _sceneBackgroundColor = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        SceneBorder.Background = brush;
        SceneLayer.Background = brush;
        GameShaderViewport.BackgroundColor = color;
    }

    private string GetSceneBackgroundPreset()
    {
        if (_sceneBackgroundColor == GetThemePanelColor()) return "Theme panel";
        if (_sceneBackgroundColor == Color.FromRgb(0, 255, 0)) return "Chroma key green";
        if (_sceneBackgroundColor == Color.FromRgb(0, 0, 255)) return "Chroma key blue";
        if (_sceneBackgroundColor == Colors.Black) return "Black";
        if (_sceneBackgroundColor == Colors.White) return "White";
        return "Custom color";
    }

    private static Color GetThemePanelColor() => Application.Current.Resources["Panel"] is SolidColorBrush brush
        ? brush.Color
        : Color.FromRgb(25, 28, 39);

    private async void SaveScenePhoto_Click(object sender, RoutedEventArgs e)
    {
        var savePath = string.Empty;
        if (string.IsNullOrWhiteSpace(_photoFolder) || !Directory.Exists(_photoFolder))
        {
            var firstPhotoDialog = new SaveFileDialog { Title = "Choose a folder for scene photos", Filter = "PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true, FileName = "SkyCharacterStudio.png" };
            if (firstPhotoDialog.ShowDialog(this) != true) return;
            _photoFolder = Path.GetDirectoryName(firstPhotoDialog.FileName)!;
            savePath = firstPhotoDialog.FileName;
            PersistPhotoFolder();
        }
        else
        {
            savePath = Path.Combine(_photoFolder, $"SkyCharacterStudio_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        }
        try
        {
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); });
            var usingGameRenderer = GameShaderViewport.Visibility == Visibility.Visible;
            FrameworkElement source = usingGameRenderer ? GameShaderViewport : SceneLayer;
            if (source.ActualWidth < 1 || source.ActualHeight < 1) throw new InvalidOperationException("The scene is not ready to capture yet.");
            if (_viewModel.SceneParts.Count == 0) throw new InvalidOperationException("Load a character or item into the scene before saving a photo.");
            BitmapSource bitmap;
            if (usingGameRenderer)
                bitmap = HelixToolkit.Wpf.SharpDX.ViewportExtensions.RenderBitmap(GameShaderViewport,
                    (int)Math.Ceiling(source.ActualWidth * 3), (int)Math.Ceiling(source.ActualHeight * 3))
                    ?? throw new InvalidOperationException("The game renderer could not capture the scene.");
            else
            {
                var rendered = new RenderTargetBitmap((int)Math.Ceiling(source.ActualWidth * 3), (int)Math.Ceiling(source.ActualHeight * 3), 288, 288, PixelFormats.Pbgra32);
                var overlays = new FrameworkElement[] { SceneModeBadge, SceneActions, SceneFooterHint, EmptyState };
                var visibility = overlays.Select(element => element.Visibility).ToArray();
                try
                {
                    foreach (var overlay in overlays) overlay.Visibility = Visibility.Collapsed;
                    rendered.Render(SceneLayer);
                }
                finally
                {
                    for (var i = 0; i < overlays.Length; i++) overlays[i].Visibility = visibility[i];
                }
                bitmap = rendered;
            }
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var output = File.Create(savePath);
            encoder.Save(output);
            _viewModel.ShaderStatus = $"High-resolution scene photo saved: {savePath}";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save scene photo", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void LoadPhotoFolder()
    {
        try { if (File.Exists(PhotoFolderPath)) _photoFolder = File.ReadAllText(PhotoFolderPath).Trim(); }
        catch (IOException) { }
    }

    private void PersistPhotoFolder()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PhotoFolderPath)!);
            File.WriteAllText(PhotoFolderPath, _photoFolder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ChangePhotoFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose scene photo folder", InitialDirectory = Directory.Exists(_photoFolder) ? _photoFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
        if (picker.ShowDialog(this) != true) return;
        _photoFolder = picker.FolderName;
        PersistPhotoFolder();
    }

    private void ExportProfile_Click()
    {
        var dialog = new SaveFileDialog { Title = "Export Sky Character Studio profile", Filter = "Sky Studio profile (*.skyprofile.json)|*.skyprofile.json|JSON file (*.json)|*.json", DefaultExt = ".skyprofile.json", AddExtension = true, FileName = "SkyCharacterStudio.skyprofile.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _viewModel.ExportProfile(dialog.FileName, _sceneBackgroundColor.ToString(), _photoFolder,
                [AmbientIntensitySlider.Value, KeyIntensitySlider.Value, FillIntensitySlider.Value, LightAzimuthSlider.Value, LightElevationSlider.Value]);
            _viewModel.ShaderStatus = "Profile and saved looks exported.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not export profile", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task ImportProfile_ClickAsync()
    {
        var dialog = new OpenFileDialog { Title = "Import Sky Character Studio profile", Filter = "Sky Studio profile (*.skyprofile.json;*.json)|*.skyprofile.json;*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _viewModel.ImportProfileAsync(dialog.FileName);
            _photoFolder = _viewModel.ImportedPhotoFolder;
            PersistPhotoFolder();
            if (_viewModel.ImportedLightingValues.Length >= 5)
            {
                AmbientIntensitySlider.Value = _viewModel.ImportedLightingValues[0];
                KeyIntensitySlider.Value = _viewModel.ImportedLightingValues[1];
                FillIntensitySlider.Value = _viewModel.ImportedLightingValues[2];
                LightAzimuthSlider.Value = _viewModel.ImportedLightingValues[3];
                LightElevationSlider.Value = _viewModel.ImportedLightingValues[4];
            }
            if (ColorConverter.ConvertFromString(_viewModel.ImportedSceneBackground) is Color color)
            {
                ApplySceneBackground(color);
                Directory.CreateDirectory(Path.GetDirectoryName(SceneBackgroundPath)!);
                File.WriteAllText(SceneBackgroundPath, color.ToString());
            }
            if (Directory.Exists(_viewModel.InstallPath)) await ScanAsync(_viewModel.InstallPath);
            _viewModel.ShaderStatus = "Profile settings, collections, and saved looks imported.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not import profile", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ImportLook_Click()
    {
        var dialog = new OpenFileDialog { Title = "Import one saved look", Filter = "Sky look (*.skylook.json)|*.skylook.json|JSON file (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try { _viewModel.ImportLook(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not import look", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ExportLook_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SavedOutfitEntry look }) return;
        var dialog = new SaveFileDialog { Title = "Export saved look", Filter = "Sky look (*.skylook.json)|*.skylook.json|JSON file (*.json)|*.json", DefaultExt = ".skylook.json", AddExtension = true, FileName = string.Concat(look.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".skylook.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { _viewModel.ExportLook(dialog.FileName, look); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not export look", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task ScanAsync(string path, bool quiet = false)
    {
        try
        {
            await _viewModel.ScanAsync(path, quiet);
            EmptyState.Visibility = _viewModel.SceneParts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadAnimationPacks(path);
        }
        catch (Exception ex)
        {
            if (!quiet)
                MessageBox.Show(this, ex.Message, "Unable to scan game files", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Equip_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.EquipSelectedAsync();
    }

    private async void UnequipItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WardrobeEntry item })
        {
            await _viewModel.UnequipAsync(item);
            if (item.Name.Equals("CharSkyKid_Mask_BasicQ", StringComparison.OrdinalIgnoreCase))
            {
                _chibiMode = false;
                ChibiModeToggle.IsChecked = false;
                _ = PrepareSelectedGameShaderAsync();
            }
        }
    }

    private async void ChibiMode_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox toggle) return;
        if (_viewModel.SceneParts.Count == 0)
        {
            toggle.IsChecked = false;
            _viewModel.ShaderStatus = "Load the character scene before enabling Chibi mode.";
            return;
        }
        _chibiMode = toggle.IsChecked == true;
        if (!await _viewModel.SetChibiMaskAsync(_chibiMode))
        {
            _chibiMode = false;
            toggle.IsChecked = false;
            return;
        }
        if (_viewModel.SceneParts.Count > 0) ShowMeshes(_viewModel.SceneParts);
        if (_chibiMode)
            _viewModel.ShaderStatus = "Chibi preview uses Sky's test_chibi scale (0.8); the game's height rig is still being matched.";
        _ = PrepareSelectedGameShaderAsync();
    }

    private async void ViewSelected_Click(object sender, RoutedEventArgs e) => await _viewModel.ViewSelectedOnlyAsync();

    private void ToggleOwned_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleSelectedOwned();

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleSelectedFavorite();

    private void TagBuiltInShoes_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleSelectedBuiltInShoesTag();

    private async void RandomOutfit_Click(object sender, RoutedEventArgs e) => await _viewModel.GenerateRandomOwnedOutfitAsync();

    private async void SaveOutfit_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveCurrentOutfitAsync();

    private async void LoadSavedLook_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SavedOutfitEntry saved })
            await _viewModel.WearSavedOutfitAsync(saved);
    }

    private void DeleteSavedLook_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SavedOutfitEntry saved })
            _viewModel.DeleteSavedOutfit(saved);
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e) => _viewModel.ChangePage(-1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => _viewModel.ChangePage(1);

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTheme)) ApplyTheme(_viewModel.SelectedTheme);
        if (e.PropertyName == nameof(MainViewModel.SelectedLanguage)) UpdateAnimationControlText();
        if (e.PropertyName == nameof(MainViewModel.SelectedItem))
        {
            ObserveDyeItems(_viewModel.SceneParts.Select(part => part.Item).Append(_viewModel.SelectedItem));
            _ = PrepareSelectedGameShaderAsync();
        }
        if (e.PropertyName == nameof(MainViewModel.SceneParts))
        {
            if (_viewModel.SceneParts.Count > 0)
            {
                ShowMeshes(_viewModel.SceneParts);
                EmptyState.Visibility = Visibility.Collapsed;
                ObserveDyeItems(_viewModel.SceneParts.Select(part => part.Item));
                _ = PrepareSelectedGameShaderAsync();
            }
            else
            {
                MeshVisual.Content = null;
                EmptyState.Visibility = Visibility.Visible;
                ObserveDyeItems(_viewModel.SelectedItem is null ? [] : [_viewModel.SelectedItem]);
                _ = PrepareSelectedGameShaderAsync();
            }
        }
    }

    private void SelectedItemDyeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WardrobeEntry.SelectedPrimaryMainDye)
            or nameof(WardrobeEntry.SelectedPrimaryBlendDye)
            or nameof(WardrobeEntry.SelectedSecondaryMainDye)
            or nameof(WardrobeEntry.SelectedSecondaryBlendDye))
        {
            if (_viewModel.SceneParts.Count > 0) ShowMeshes(_viewModel.SceneParts);
            _ = RefreshDyePreviewAsync();
        }
    }

    private async Task RefreshDyePreviewAsync()
    {
        await PrepareSelectedGameShaderAsync();
        if (Viewport.Visibility == Visibility.Visible)
            _viewModel.ShaderStatus = "Dye choices are saved. The preview applies game mask channels only for AvatarCham items with matching diffuse/mask UVs; unsupported materials keep their original colors.";
    }

    private void LoadAnimationPacks(string installPath)
    {
        _loadingAnimationPacks = true;
        _activeAnimation = null;
        _isPosePlaying = false;
        UpdateAnimationControlText();
        AnimationPlayButton.IsEnabled = false;
        AnimationFrameSlider.IsEnabled = false;
        UpdateAnimationControlText();
        AnimationSegmentBox.ItemsSource = null;
        AnimationSegmentBox.IsEnabled = false;
        _animationSegments = [];
        AnimationSearchBox.Clear();
        try
        {
            var assets = Path.Combine(installPath, "data", "assets");
            var options = Directory.Exists(assets)
                ? Directory.EnumerateFiles(assets, "*.animpack", SearchOption.AllDirectories)
                    .Where(file => Path.GetFileNameWithoutExtension(file).StartsWith("CharKidAnim", StringComparison.OrdinalIgnoreCase))
                    .Select(file => new AnimationPackOption(Path.GetFileNameWithoutExtension(file), file))
                    .OrderByDescending(option => option.Name.Equals("CharKidAnimPlayerAct", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            AnimationPackBox.ItemsSource = options;
            AnimationPackBox.SelectedItem = options.FirstOrDefault(option => option.Name.Equals("CharKidAnimPlayerAct", StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault();
            AnimationPackBox.IsEnabled = options.Count > 0;
            if (options.Count == 0) _viewModel.ShaderStatus = "No CharKidAnim .animpack files found under the selected game folder.";
        }
        catch (Exception ex)
        {
            _viewModel.ShaderStatus = $"Could not list Sky animation packs: {ex.Message}";
        }
        finally { _loadingAnimationPacks = false; }
        if (AnimationPackBox.SelectedItem is AnimationPackOption selected) _ = LoadAnimationPackAsync(selected);
    }

    private async void AnimationPack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAnimationPacks || AnimationPackBox.SelectedItem is not AnimationPackOption selected) return;
        await LoadAnimationPackAsync(selected);
    }

    private async Task LoadAnimationPackAsync(AnimationPackOption selected)
    {
        var revision = Interlocked.Increment(ref _animationLoadRevision);
        _activeAnimation = null;
        _isPosePlaying = false;
        _animationSegments = [];
        AnimationPlayButton.IsEnabled = false;
        AnimationFrameSlider.IsEnabled = false;
        AnimationSegmentBox.IsEnabled = false;
        UpdateAnimationControlText();
        _viewModel.ShaderStatus = $"Loading Sky animation pack {selected.Name}…";
        try
        {
            var animationList = Path.Combine(Path.GetDirectoryName(selected.Path)!, "AnimationList.dat");
            var clips = await Task.Run(() => SkyAnimationReader.Read(selected.Path, animationList));
            if (revision != Volatile.Read(ref _animationLoadRevision)) return;
            _animationSegments = await Task.Run(() => clips.Select(clip => new AnimationSegmentOption(
                clip.Name, AnimationGlyph(clip.Name), GameIconLoader.LoadForClip(selected.Path, clip.Name), clip)).ToList());
            AnimationSearchBox.Clear();
            ApplyAnimationSegmentFilter(selectFirst: true);
            AnimationSegmentBox.IsEnabled = AnimationSegmentBox.Items.Count > 0;
            if (_animationSegments.Count == 0) _viewModel.ShaderStatus = $"{selected.Name} contains no readable animation clips.";
        }
        catch (Exception ex)
        {
            if (revision == Volatile.Read(ref _animationLoadRevision))
                _viewModel.ShaderStatus = $"Could not decode {selected.Name}: {ex.Message}";
        }
    }

    private void AnimationSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnimationSegmentBox.SelectedItem is not AnimationSegmentOption selected)
        {
            _activeAnimation = null;
            _isPosePlaying = false;
            RefreshAnimationAvailability();
            return;
        }
        _activeAnimation = selected.Clip;
        _isPosePlaying = false;
        RefreshAnimationAvailability();
        UseAnimatedPreviewViewport();
        AnimationFrameSlider.Maximum = Math.Max(0, selected.Clip.FrameCount - 1);
        AnimationFrameSlider.Value = 0;
        RenderAnimationFrame(0);
        _viewModel.ShaderStatus = $"{selected.Name}: {selected.Clip.Bones.Count} bones · {selected.Clip.FrameCount} frames at 30 FPS.";
    }

    private void AnimationSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyAnimationSegmentFilter();

    private static string AnimationGlyph(string name)
    {
        var value = name.ToLowerInvariant();
        if (value.Contains("dance") || value.Contains("waltz") || value.Contains("groove")) return "♫";
        if (value.Contains("walk") || value.Contains("step")) return "♟";
        if (value.Contains("sit") || value.Contains("rest") || value.Contains("sleep")) return "☾";
        if (value.Contains("write") || value.Contains("draw") || value.Contains("paint")) return "✎";
        if (value.Contains("hug") || value.Contains("heart") || value.Contains("love")) return "♡";
        if (value.Contains("warm") || value.Contains("sun")) return "☀";
        if (value.Contains("pray") || value.Contains("meditat")) return "✧";
        if (value.Contains("wave") || value.Contains("hello")) return "⚑";
        if (value.Contains("fly") || value.Contains("jump")) return "✦";
        return "◉";
    }

    private void ApplyAnimationSegmentFilter(bool selectFirst = false)
    {
        if (AnimationSegmentBox is null) return;
        var selected = AnimationSegmentBox.SelectedItem as AnimationSegmentOption;
        var query = AnimationSearchBox?.Text.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(query)
            ? _animationSegments
            : _animationSegments.Where(option => option.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        AnimationSegmentBox.ItemsSource = filtered;
        var index = selected is null ? -1 : filtered.FindIndex(option => ReferenceEquals(option.Clip, selected.Clip));
        if (index < 0 && selectFirst && filtered.Count > 0) index = 0;
        AnimationSegmentBox.SelectedIndex = index;
        AnimationSegmentBox.IsEnabled = filtered.Count > 0;
        if (filtered.Count == 0)
        {
            _activeAnimation = null;
            _isPosePlaying = false;
            AnimationPlayButton.IsEnabled = false;
            AnimationFrameSlider.IsEnabled = false;
            UpdateAnimationControlText();
        }
    }

    private async void StaticPose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string poseNames }) return;
        var option = AnimationPackBox.ItemsSource is IEnumerable<AnimationPackOption> packs
            ? packs.FirstOrDefault(pack => pack.Name.Equals("CharKidAnimGroundState", StringComparison.OrdinalIgnoreCase))
            : null;
        if (option is null)
        {
            _viewModel.ShaderStatus = "The Sky ground-state pose pack is not available in the selected game folder.";
            return;
        }

        _loadingAnimationPacks = true;
        AnimationPackBox.SelectedItem = option;
        _loadingAnimationPacks = false;
        await LoadAnimationPackAsync(option);

        var candidates = poseNames.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var clip = candidates.Select(candidate => _animationSegments.FirstOrDefault(segment =>
                segment.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(match => match is not null)
            ?? candidates.Select(candidate => _animationSegments.FirstOrDefault(segment =>
                segment.Name.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(match => match is not null);
        if (clip is null)
        {
            _viewModel.ShaderStatus = $"No static ground pose matched: {string.Join(", ", candidates)}.";
            return;
        }

        AnimationSearchBox.Clear();
        AnimationSegmentBox.SelectedItem = clip;
        _isPosePlaying = false;
        if (_activeAnimation is not null)
        {
            AnimationFrameSlider.Value = 0;
            RenderAnimationFrame(0);
            UpdateAnimationControlText();
            _viewModel.ShaderStatus = $"Static pose: {clip.Name}";
        }
    }

    private void AnimationPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAnimation is not { FrameCount: > 1 }) return;
        UseAnimatedPreviewViewport();
        _isPosePlaying = !_isPosePlaying;
        UpdateAnimationControlText();
    }

    private void AnimationFrame_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var frame = (int)Math.Round(e.NewValue);
        AnimationFrameText.Text = $"{_viewModel.UiText["frame"]} {frame}";
        if (_activeAnimation is not null && (_animatedMeshes.Count > 0 || _animatedProps.Count > 0)) RenderAnimationFrame(frame);
    }

    private void UpdateAnimationControlText()
    {
        AnimationPlayButton.Content = _viewModel.UiText[_isPosePlaying ? "pause" : "play"];
        AnimationFrameText.Text = $"{_viewModel.UiText["frame"]} {(int)Math.Round(AnimationFrameSlider.Value)}";
    }

    private void UseAnimatedPreviewViewport()
    {
        if (GameShaderViewport.Visibility != Visibility.Visible) return;
        GameShaderViewport.Visibility = Visibility.Collapsed;
        Viewport.Visibility = Visibility.Visible;
        _viewModel.ShaderStatus = "Using the textured CPU preview so the selected animation can be applied to the full outfit.";
    }

    private bool CanAnimate(SkyAnimationClip? clip)
    {
        if (clip is null) return false;
        var names = clip.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skinnedOutfitPart = _animatedMeshes.Any(binding => binding.Data.EmbeddedSkeleton is { Count: > 0 } skeleton
            && binding.Data.BoneWeights.Any(row => row.Count > 0)
            && skeleton.Any(bone => names.Contains(bone.Name)));
        return skinnedOutfitPart || _animatedProps.Any(prop => IsItemAnimationClip(clip.Name, prop.Definition)
            && (names.Contains("R_hand") || names.Contains("RightHand")));
    }

    private void RefreshAnimationAvailability()
    {
        var enabled = CanAnimate(_activeAnimation);
        AnimationPlayButton.IsEnabled = enabled && _activeAnimation!.FrameCount > 1;
        AnimationFrameSlider.IsEnabled = enabled && _activeAnimation!.FrameCount > 0;
        if (!enabled) _isPosePlaying = false;
        UpdateAnimationControlText();
    }

    private void ObserveDyeItems(IEnumerable<WardrobeEntry?> items)
    {
        var next = items.OfType<WardrobeEntry>().ToHashSet();
        foreach (var item in _dyeObservedItems.Except(next).ToArray()) item.PropertyChanged -= SelectedItemDyeChanged;
        foreach (var item in next.Except(_dyeObservedItems)) item.PropertyChanged += SelectedItemDyeChanged;
        _dyeObservedItems.Clear();
        _dyeObservedItems.UnionWith(next);
    }

    private async Task PrepareSelectedGameShaderAsync()
    {
        if (!ExperimentalShaderRendererApproved())
        {
            GameShaderViewport.Visibility = Visibility.Collapsed;
            Viewport.Visibility = Visibility.Visible;
            _viewModel.ShaderStatus = "Откат: SPIR-V shader renderer не прошёл визуальную проверку; используется прежний текстурный preview.";
            return;
        }

        var revision = Interlocked.Increment(ref _shaderLoadRevision);
        var item = _viewModel.SelectedItem;
        var parts = _viewModel.SceneParts.Where(part => part.Item.MeshPath is not null).ToList();
        if (parts.Count == 0 && item?.MeshPath is not null)
        {
            var meshData = await Task.Run(() => TgcMeshReader.ReadMesh(item.MeshPath));
            if (revision != Volatile.Read(ref _shaderLoadRevision) || !ReferenceEquals(_viewModel.SelectedItem, item)) return;
            parts.Add(new OutfitPreviewPart(item, meshData));
        }
        if (parts.Count == 0)
        {
            GameShaderViewport.Visibility = Visibility.Collapsed;
            Viewport.Visibility = Visibility.Visible;
            _viewModel.ShaderStatus = "Game Avatar shader sources are not loaded";
            return;
        }

        var shaderName = parts.Count > 0 && string.IsNullOrWhiteSpace(parts[0].Item.Definition.Shader) ? "Avatar" : parts.FirstOrDefault()?.Item.Definition.Shader ?? "Avatar";
        _viewModel.ShaderStatus = $"Preparing Sky Avatar shaders for {parts.Count} outfit meshes";
        try
        {
            var programs = await Task.Run(() => parts.Select(part => part.Item).Distinct()
                .Select(partItem => (Item: partItem, Program: GameAvatarShaderCompiler.LoadForMesh(
                    partItem.MeshPath!, string.IsNullOrWhiteSpace(partItem.Definition.Shader) ? "Avatar" : partItem.Definition.Shader)))
                .ToDictionary(pair => pair.Item, pair => pair.Program));
            if (revision != Volatile.Read(ref _shaderLoadRevision)) return;
            var oldManager = _gameEffectsManager;
            _gameEffectsManager = new GameAvatarEffectsManager(programs.Values.DistinctBy(program => program.ShaderName).ToArray());
            GameShaderViewport.EffectsManager = _gameEffectsManager;
            GameShaderMeshVisual.Children.Clear();
            _avatarMaterials.Clear();
            var bufferSummaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingTextureNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dyeUniformNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dyeColorTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bodyBounds = ComputeBodyBounds(parts);
            foreach (var part in parts)
            {
                var partItem = part.Item;
                var shader = programs[partItem];
                var values = BuildAvatarShaderValues(partItem);
                var matrices = BuildAvatarShaderMatrices();
                var textures = GameAvatarTextureLoader.LoadForItem(partItem);
                var buffers = GameAvatarUniformBuffers.Create(shader, values, matrices);
                foreach (var field in shader.UniformBlocks.SelectMany(block => block.Fields))
                {
                    if (field.Name.Contains("Dye", StringComparison.OrdinalIgnoreCase)) dyeUniformNames.Add(field.Name);
                    if (field.Name is "u_primaryDyeColor" or "u_secondaryDyeColor") dyeColorTargets.Add(field.Name);
                }
                foreach (var buffer in buffers) bufferSummaries.Add($"{buffer.Stage} b{buffer.Register} {buffer.Data.Length}B");
                foreach (var name in textures.Keys)
                    textureNames.Add(name is "u_lightPRTex" or "u_lightCSTex" ? $"{name} (neutral editor fallback)" : name);
                if (shader.Bindings.Any(binding => binding.Name.Equals("u_probeCube", StringComparison.OrdinalIgnoreCase)))
                    textureNames.Add("u_probeCube (neutral editor fallback)");
                foreach (var binding in shader.Bindings.Where(binding => binding.ResourceType.Contains("texture", StringComparison.OrdinalIgnoreCase)))
                    if (!textures.ContainsKey(binding.Name) && binding.Name != "u_probeCube") missingTextureNames.Add($"{shader.ShaderName}:{binding.Name}");
                var materialCore = new GameAvatarMaterialCore(shader, values, matrices, textures);
                _avatarMaterials.Add(materialCore);
                var avatarModel = new HelixToolkit.Wpf.SharpDX.MeshGeometryModel3D
                {
                    // The game Avatar shader consumes object-space positions and this
                    // viewport supplies identity u_model. Bake the OutfitDefs attachment
                    // transform into the geometry so SPIR-V and WPF previews agree.
                    Geometry = GameAvatarGeometryAdapter.Create(part.MeshData, BuildPartTransform(part, parts, bodyBounds)),
                    Material = new GameAvatarMaterial(materialCore),
                    IsTransparent = GameAvatarEffectsManager.IsBlendedAvatarShader(shader),
                    IsThrowingShadow = false,
                    EnableViewFrustumCheck = false,
                };
                GameShaderMeshVisual.Children.Add(avatarModel);
            }
            var missingEnvironmentMaps = missingTextureNames.Where(name =>
                name.Contains("u_probeCube", StringComparison.OrdinalIgnoreCase)
                || name.Contains("u_lightPRTex", StringComparison.OrdinalIgnoreCase)
                || name.Contains("u_lightCSTex", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (missingEnvironmentMaps.Length > 0)
                throw new InvalidOperationException($"Required game lighting maps are missing ({string.Join(", ", missingEnvironmentMaps)}); using the opaque textured preview.");
            GameShaderCamera.Position = Camera.Position;
            GameShaderCamera.LookDirection = Camera.LookDirection;
            GameShaderCamera.UpDirection = Camera.UpDirection;
            GameShaderCamera.FieldOfView = Camera.FieldOfView;
            Interlocked.Exchange(ref GameAvatarMaterialVariable.ResourceBindCount, 0);
            Interlocked.Exchange(ref GameAvatarMaterialVariable.DrawCallCount, 0);
            Interlocked.Exchange(ref GameAvatarMaterialVariable.MaterialVariableCount, 0);
            Viewport.Visibility = Visibility.Collapsed;
            GameShaderViewport.Visibility = Visibility.Visible;
            oldManager?.Dispose();
            VerifyShaderFrameAfterRender(revision);
            var dyeStatus = dyeColorTargets.Contains("u_primaryDyeColor") && dyeColorTargets.Contains("u_secondaryDyeColor")
                ? $"Dynamic primary/secondary dye colors are reflected; dye masks use {string.Join(", ", dyeUniformNames)}."
                : $"Sky runtime uses dynamic u_primaryDyeColor/u_secondaryDyeColor parameters; this preview applies dye only through decoded AvatarCham mask channels when diffuse and mask UVs match.";
            _viewModel.ShaderStatus = $"Sky Avatar SPIR-V stages converted to DXBC for {parts.Count} outfit meshes. Reflected buffers: {string.Join(", ", bufferSummaries)}. Bound shader textures: {string.Join(", ", textureNames)}. Unresolved shader textures: {string.Join(", ", missingTextureNames)}. {dyeStatus}";
        }
        catch (Exception ex)
        {
            if (revision != Volatile.Read(ref _shaderLoadRevision)) return;
            GameShaderViewport.Visibility = Visibility.Collapsed;
            Viewport.Visibility = Visibility.Visible;
            _viewModel.ShaderStatus = $"Game shader unavailable: {ex.Message}";
        }
    }

    private static bool ExperimentalShaderRendererApproved() => true;

    private void VerifyShaderFrameAfterRender(int revision)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (revision != Volatile.Read(ref _shaderLoadRevision) || GameShaderViewport.Visibility != Visibility.Visible) return;
            try
            {
                // Successful shader compilation only proves that bytecode is
                // accepted. Check the rendered frame before hiding the known
                // WPF mesh view; a blank D3D frame must never leave an empty scene.
                // Capture the live backbuffer at its arranged size. Resizing the
                // swap chain immediately before inspection can return an empty
                // capture on some DXGI drivers even though the visible viewport
                // is rendering; it also perturbs the user's live scene.
                var frame = HelixToolkit.Wpf.SharpDX.ViewportExtensions.RenderBitmap(GameShaderViewport)
                    ?? throw new InvalidOperationException("The Direct3D viewport returned no frame.");
                var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var stride = bgra.PixelWidth * 4;
                var pixels = new byte[stride * bgra.PixelHeight];
                bgra.CopyPixels(pixels, stride, 0);
                var changedPixels = 0;
                for (var offset = 0; offset + 3 < pixels.Length; offset += 4)
                {
                    var difference = Math.Abs(pixels[offset] - 26) + Math.Abs(pixels[offset + 1] - 18) + Math.Abs(pixels[offset + 2] - 16);
                    if (difference > 36) changedPixels++;
                }
                if (changedPixels >= 300) return;
                var diagnosticDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyCharacterStudio");
                Directory.CreateDirectory(diagnosticDirectory);
                var diagnosticPath = Path.Combine(diagnosticDirectory, "shader-frame-diagnostic.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bgra));
                using (var stream = File.Create(diagnosticPath)) encoder.Save(stream);
                var renderHost = GameShaderViewport.RenderHost;
                var hostState = renderHost is null ? "no render host" : $"rendering={renderHost.IsRendering}, size={renderHost.ActualWidth:0}x{renderHost.ActualHeight:0}";
                var rendererCounts = $"parts {GameShaderMeshVisual.Children.Count}, materials {Volatile.Read(ref GameAvatarMaterialVariable.MaterialVariableCount)}, resource binds {Volatile.Read(ref GameAvatarMaterialVariable.ResourceBindCount)}, draw calls {Volatile.Read(ref GameAvatarMaterialVariable.DrawCallCount)}; {hostState}";
                GameShaderViewport.Visibility = Visibility.Collapsed;
                Viewport.Visibility = Visibility.Visible;
                _viewModel.ShaderStatus = $"Game shader frame had only {changedPixels} non-background pixels ({rendererCounts}); showing the textured 3D preview. GPU capture: {diagnosticPath}";
            }
            catch (Exception ex)
            {
                GameShaderViewport.Visibility = Visibility.Collapsed;
                Viewport.Visibility = Visibility.Visible;
                _viewModel.ShaderStatus = $"Game shader frame check failed; showing the textured 3D preview: {ex.Message}";
            }
        };
        timer.Start();
    }

    private static void ApplyTheme(string theme)
    {
        var palette = theme switch
        {
            "Ocean" => new[] { "#081824", "#102B3A", "#193B4A", "#69D8CF", "#EDF9F7", "#A5C3C3" },
            "Rose Nebula" => new[] { "#1B111C", "#2A1C2D", "#3A263A", "#F29BB5", "#FFF0F4", "#C7AAB9" },
            "AMOLED" or "Black" => new[] { "#000000", "#080808", "#151515", "#B4A2FF", "#F7F7F7", "#969696" },
            "Neon Black" => new[] { "#07070B", "#101018", "#1B1B27", "#39F5D0", "#F4FFF9", "#9AA9A8" },
            "Neon Purple" => new[] { "#10091A", "#1D1030", "#2B1744", "#DA6BFF", "#FFF5FF", "#C5A9CC" },
            "Purple Black" => new[] { "#0D0A12", "#17121F", "#241C30", "#A58BEF", "#F4F0FA", "#A9A2B5" },
            "Neon Purple Black" => new[] { "#05030A", "#10091A", "#1D1230", "#C84DFF", "#FFF7FF", "#BDA8C8" },
            "White" => new[] { "#F1F3F7", "#FFFFFF", "#E5E9F0", "#6751C8", "#202330", "#686D7B" },
            "Sunset" => new[] { "#1D1116", "#2D1B22", "#422632", "#FF9B6B", "#FFF4EC", "#C9A99F" },
            "Forest" => new[] { "#0C1713", "#14231B", "#20342A", "#7DD6A1", "#F0FFF4", "#A6BCAE" },
            "Sky Blue" => new[] { "#0D1724", "#15263A", "#20354D", "#62B9FF", "#F3FAFF", "#A8BCCD" },
            _ => new[] { "#10121A", "#191C27", "#222635", "#9B87F5", "#F1F0F8", "#999DB0" },
        };
        var keys = new[] { "Canvas", "Panel", "PanelRaised", "Accent", "TextPrimary", "TextMuted" };
        for (var i = 0; i < keys.Length; i++)
            Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[i]));
    }

    private void ShowMeshes(IReadOnlyList<OutfitPreviewPart> parts)
    {
        _scrollingTextures.Clear();
        _animatedMeshes.Clear();
        _animatedProps.Clear();
        var bodyBounds = ComputeBodyBounds(parts);
        var group = new Model3DGroup();
        var allPositions = new Point3DCollection();
        foreach (var part in parts)
        {
            var data = part.MeshData;
            var isProp = part.Item.Definition.Type.Equals("prop", StringComparison.OrdinalIgnoreCase)
                || part.Item.Definition.Type.Equals("instrument", StringComparison.OrdinalIgnoreCase);
            var transform = BuildPartTransform(part, parts, bodyBounds);
            var mesh = new MeshGeometry3D();
            foreach (var (x, y, z) in data.Vertices)
            {
                var position = new Point3D(-x, y, -z);
                mesh.Positions.Add(position);
                allPositions.Add(transform.Transform(position));
                var uvIndex = mesh.Positions.Count - 1;
                mesh.TextureCoordinates.Add(data.Uv0.Count > uvIndex
                    ? new Point(data.Uv0[uvIndex].U, data.Uv0[uvIndex].V)
                    : new Point(0, 0));
            }
            foreach (var (a, b, c) in data.Indices)
            {
                if (a < 0 || b < 0 || c < 0 || a >= mesh.Positions.Count || b >= mesh.Positions.Count || c >= mesh.Positions.Count) continue;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c);
            }

            var normals = Enumerable.Range(0, mesh.Positions.Count).Select(_ => new Vector3D()).ToArray();
            for (var i = 0; i + 2 < mesh.TriangleIndices.Count; i += 3)
            {
                var a = mesh.TriangleIndices[i]; var b = mesh.TriangleIndices[i + 1]; var c = mesh.TriangleIndices[i + 2];
                var normal = Vector3D.CrossProduct(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
                normals[a] += normal; normals[b] += normal; normals[c] += normal;
            }
            var normalCollection = new Vector3DCollection(normals.Length);
            foreach (var normal in normals)
            {
                if (normal.LengthSquared > 1e-12) normal.Normalize();
                normalCollection.Add(normal);
            }
            mesh.Normals = normalCollection;

            var material = BuildItemMaterial(part.Item, data);
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material, Transform = transform };
            group.Children.Add(model);
            if (isProp && HasItemAnimation(part.Item.Definition))
                _animatedProps.Add(new AnimatedPropBinding(part.Item.Definition, model, transform));
            if (data.EmbeddedSkeleton is { Count: > 0 } && data.BoneWeights.Count == mesh.Positions.Count)
                _animatedMeshes.Add(new AnimatedMeshBinding(data, mesh));
        }
        MeshVisual.Content = group;
        Frame(allPositions);
        RefreshAnimationAvailability();
        if (_activeAnimation is not null) RenderAnimationFrame((int)AnimationFrameSlider.Value);
    }

    private Rect3D? ComputeBodyBounds(IReadOnlyList<OutfitPreviewPart> parts)
    {
        var bodyParts = parts.Where(part => part.Item.Definition.Type.Equals("body", StringComparison.OrdinalIgnoreCase)).ToList();
        if (bodyParts.Count == 0) return null;
        var bounds = Rect3D.Empty;
        foreach (var part in bodyParts)
        {
            var transform = BuildItemTransform(part.Item.Definition, includeChibi: false);
            foreach (var (x, y, z) in part.MeshData.Vertices)
                bounds.Union(transform.Transform(new Point3D(-x, y, -z)));
        }
        return bounds.IsEmpty ? null : bounds;
    }

    private static Rect3D ComputeTransformedBounds(MeshData data, Transform3D transform)
    {
        var bounds = Rect3D.Empty;
        foreach (var (x, y, z) in data.Vertices)
            bounds.Union(transform.Transform(new Point3D(-x, y, -z)));
        return bounds;
    }

    private Rect3D ComputeMaskEyeBounds(OutfitPreviewPart maskPart, Rect3D? bodyBounds, Transform3D? suppliedTransform = null)
    {
        var transform = suppliedTransform ?? BuildItemTransform(maskPart.Item.Definition, addFaceNudge: false);
        var wholeMaskBounds = ComputeTransformedBounds(maskPart.MeshData, transform);
        if (bodyBounds is not Rect3D body || body.SizeY <= 0) return wholeMaskBounds;
        var eyeBottom = body.Y + body.SizeY * 0.76;
        var eyeTop = body.Y + body.SizeY * 0.94;
        var eyeBounds = Rect3D.Empty;
        foreach (var (x, y, z) in maskPart.MeshData.Vertices)
        {
            var point = transform.Transform(new Point3D(-x, y, -z));
            if (point.Y >= eyeBottom && point.Y <= eyeTop)
                eyeBounds.Union(point);
        }
        return eyeBounds.IsEmpty ? wholeMaskBounds : eyeBounds;
    }

    private Transform3D BuildPartTransform(OutfitPreviewPart part, IReadOnlyList<OutfitPreviewPart> parts, Rect3D? bodyBounds)
    {
        var definition = part.Item.Definition;
        var bodyDefinition = parts.FirstOrDefault(candidate => candidate.Item.Definition.Type.Equals("body", StringComparison.OrdinalIgnoreCase))?.Item.Definition;
        var type = definition.Type;
        var isProp = type.Equals("prop", StringComparison.OrdinalIgnoreCase)
            || type.Equals("instrument", StringComparison.OrdinalIgnoreCase);
        var isFaceAccessory = type.Equals("face", StringComparison.OrdinalIgnoreCase)
            || type.Equals("glasses", StringComparison.OrdinalIgnoreCase);
        var transform = BuildItemTransform(definition, isProp ? part.MeshData : null, bodyBounds, addFaceNudge: !isFaceAccessory, bodyDefinition: bodyDefinition);
        if (!isFaceAccessory) return transform;

        var maskPart = parts.FirstOrDefault(candidate => candidate.Item.Definition.Type.Equals("mask", StringComparison.OrdinalIgnoreCase));
        var maskTransform = maskPart is null ? null : BuildItemTransform(maskPart.Item.Definition, addFaceNudge: false, bodyDefinition: bodyDefinition);
        var faceBodyBounds = bodyBounds;
        if (_chibiMode && bodyBounds is Rect3D unscaledBody)
            faceBodyBounds = new Rect3D(unscaledBody.X, unscaledBody.Y * 0.84, unscaledBody.Z,
                unscaledBody.SizeX, unscaledBody.SizeY * 0.84, unscaledBody.SizeZ);
        var maskEyeBounds = maskPart is null || maskTransform is null
            ? Rect3D.Empty
            : ComputeMaskEyeBounds(maskPart, faceBodyBounds, maskTransform);
        var accessoryBounds = ComputeTransformedBounds(part.MeshData, transform);
        var dx = maskEyeBounds.IsEmpty ? 0 : maskEyeBounds.X + maskEyeBounds.SizeX * 0.5 - (accessoryBounds.X + accessoryBounds.SizeX * 0.5);
        var dy = maskEyeBounds.IsEmpty ? 0 : maskEyeBounds.Y + maskEyeBounds.SizeY * 0.5 - (accessoryBounds.Y + accessoryBounds.SizeY * 0.5);
        var dz = 0.12;
        if (maskPart is not null && maskTransform is not null && !maskEyeBounds.IsEmpty)
        {
            var maskVertices = maskPart.MeshData.Vertices
                .Select(vertex => maskTransform.Transform(new Point3D(-vertex.X, vertex.Y, -vertex.Z)))
                .ToArray();
            var nearestSurfaceOffset = double.PositiveInfinity;
            foreach (var (x, y, z) in part.MeshData.Vertices)
            {
                var point = transform.Transform(new Point3D(-x, y, -z));
                point = new Point3D(point.X + dx, point.Y + dy, point.Z);
                if (TryGetMaskFrontAt(maskVertices, maskPart.MeshData.Indices, point.X, point.Y, out var surfaceZ))
                    nearestSurfaceOffset = Math.Max(nearestSurfaceOffset, surfaceZ + 0.05 - point.Z);
            }
            if (double.IsFinite(nearestSurfaceOffset)) dz = Math.Max(dz, nearestSurfaceOffset);
            else dz = Math.Max(dz, maskEyeBounds.Z + maskEyeBounds.SizeZ + 0.05 - (accessoryBounds.Z + accessoryBounds.SizeZ));
        }
        var aligned = new Transform3DGroup();
        aligned.Children.Add(transform);
        aligned.Children.Add(new TranslateTransform3D(dx, dy, dz));
        return aligned;
    }

    private static bool TryGetMaskFrontAt(IReadOnlyList<Point3D> vertices, IReadOnlyList<(int A, int B, int C)> triangles, double x, double y, out double frontZ)
    {
        frontZ = double.PositiveInfinity;
        var found = false;
        foreach (var (a, b, c) in triangles)
        {
            if (a < 0 || b < 0 || c < 0 || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count) continue;
            var p0 = vertices[a]; var p1 = vertices[b]; var p2 = vertices[c];
            var denominator = (p1.Y - p2.Y) * (p0.X - p2.X) + (p2.X - p1.X) * (p0.Y - p2.Y);
            if (Math.Abs(denominator) < 1e-10) continue;
            var w0 = ((p1.Y - p2.Y) * (x - p2.X) + (p2.X - p1.X) * (y - p2.Y)) / denominator;
            var w1 = ((p2.Y - p0.Y) * (x - p2.X) + (p0.X - p2.X) * (y - p2.Y)) / denominator;
            var w2 = 1 - w0 - w1;
            if (w0 < -1e-5 || w1 < -1e-5 || w2 < -1e-5) continue;
            frontZ = Math.Max(frontZ, w0 * p0.Z + w1 * p1.Z + w2 * p2.Z);
            found = true;
        }
        return found;
    }

    private Vector3D? GetPropStowTranslation(MeshData propMesh, OutfitDefinition definition, Rect3D? bodyBounds, OutfitDefinition? bodyDefinition)
    {
        if (bodyBounds is not Rect3D body || body.IsEmpty || body.SizeY <= 0) return new Vector3D(0, 9.35, 3.0);
        var baseTransform = BuildItemTransform(definition, includeChibi: false, addPropStow: false, bodyDefinition: bodyDefinition);
        var meshBounds = Rect3D.Empty;
        foreach (var (x, y, z) in propMesh.Vertices)
            meshBounds.Union(baseTransform.Transform(new Point3D(-x, y, -z)));
        if (meshBounds.IsEmpty) return new Vector3D(0, 9.35, 3.0);

        // Mesh vertices are converted to the preview's handedness first. The face
        // points toward -Z, so the back is +Z. Put the nearest prop surface behind
        // the body's rear surface instead of placing every prop at one fixed Z.
        var bodyCenterX = body.X + body.SizeX * 0.5;
        var targetY = body.Y + body.SizeY * 0.68;
        // The preview camera looks from +Z: the front of the avatar is the
        // largest Z value, so a back-stowed item must sit behind the body's
        // minimum Z boundary. Previously this used the front boundary and
        // sent props toward the camera.
        var requiredBackZ = body.Z - 0.25;
        return new Vector3D(bodyCenterX - (meshBounds.X + meshBounds.SizeX * 0.5),
            targetY - (meshBounds.Y + meshBounds.SizeY * 0.5), requiredBackZ - (meshBounds.Z + meshBounds.SizeZ));
    }

    private void RenderAnimationFrame(int frameIndex)
    {
        var clip = _activeAnimation;
        if (clip is null || clip.Frames.Count == 0) return;
        frameIndex = Math.Clamp(frameIndex, 0, clip.Frames.Count - 1);
        var pose = clip.Frames[frameIndex];
        var nameToIndex = clip.Bones.Select((bone, index) => (bone.Name, index))
            .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var world = new Matrix4x4[clip.Bones.Count];
        var worldReady = new bool[clip.Bones.Count];
        var worldActive = new bool[clip.Bones.Count];
        Matrix4x4 GetWorld(int index)
        {
            if (worldReady[index]) return world[index];
            if (worldActive[index]) return Matrix4x4.Identity;
            worldActive[index] = true;
            var localPose = pose[index];
            var local = Matrix4x4.CreateScale(localPose.Scale)
                * Matrix4x4.CreateFromQuaternion(localPose.Rotation)
                * Matrix4x4.CreateTranslation(localPose.Translation);
            var parent = clip.Bones[index].ParentIndex;
            world[index] = parent >= 0 && parent < clip.Bones.Count ? local * GetWorld(parent) : local;
            worldActive[index] = false;
            worldReady[index] = true;
            return world[index];
        }
        for (var i = 0; i < clip.Bones.Count; i++) _ = GetWorld(i);

        foreach (var binding in _animatedMeshes)
        {
            var skeleton = binding.Data.EmbeddedSkeleton!;
            var skin = new Matrix4x4[skeleton.Count];
            var mapped = new bool[skeleton.Count];
            for (var i = 0; i < skeleton.Count; i++)
            {
                if (!nameToIndex.TryGetValue(skeleton[i].Name, out var animationBone)) continue;
                var m = skeleton[i].InverseBindMatrix;
                if (m.Length < 16) continue;
                var inverseBind = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                skin[i] = inverseBind * world[animationBone];
                mapped[i] = true;
            }

            var positions = new Point3DCollection(binding.Data.Vertices.Count);
            for (var vertex = 0; vertex < binding.Data.Vertices.Count; vertex++)
            {
                var source = binding.Data.Vertices[vertex];
                var original = new Vector3(source.X, source.Y, source.Z);
                var result = Vector3.Zero;
                var totalWeight = 0f;
                foreach (var influence in binding.Data.BoneWeights[vertex])
                {
                    if ((uint)influence.BoneIndex >= (uint)skin.Length || !mapped[influence.BoneIndex]) continue;
                    result += Vector3.Transform(original, skin[influence.BoneIndex]) * influence.Weight;
                    totalWeight += influence.Weight;
                }
                if (totalWeight > 1e-5f) result /= totalWeight;
                else result = original;
                positions.Add(new Point3D(-result.X, result.Y, -result.Z));
            }
            binding.Geometry.Positions = positions;
            var normals = new Vector3DCollection(positions.Count);
            for (var i = 0; i < positions.Count; i++) normals.Add(new Vector3D());
            var triangles = binding.Geometry.TriangleIndices;
            for (var i = 0; i + 2 < triangles.Count; i += 3)
            {
                var a = triangles[i]; var b = triangles[i + 1]; var c = triangles[i + 2];
                var normal = Vector3D.CrossProduct(positions[b] - positions[a], positions[c] - positions[a]);
                normals[a] += normal; normals[b] += normal; normals[c] += normal;
            }
            for (var i = 0; i < normals.Count; i++) if (normals[i].LengthSquared > 1e-12) normals[i].Normalize();
            binding.Geometry.Normals = normals;

        }

        var handIndex = clip.Bones.Select((bone, index) => (bone.Name, index))
            .Where(pair => pair.Name.Equals("R_hand", StringComparison.OrdinalIgnoreCase)
                || pair.Name.Equals("RightHand", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.index).DefaultIfEmpty(-1).First();
        var handedness = Matrix4x4.CreateScale(-1, 1, -1);
        foreach (var prop in _animatedProps)
        {
            if (!IsItemAnimationClip(clip.Name, prop.Definition) || handIndex < 0)
            {
                prop.Model.Transform = prop.StowedTransform;
                continue;
            }

            var definition = prop.Definition;
            var scale = definition.PropScale is { Length: >= 3 } scaleValues
                ? new Vector3(scaleValues[0], scaleValues[1], scaleValues[2])
                : Vector3.One;
            var offset = definition.PropOffset is { Length: >= 3 } offsetValues
                ? new Vector3(-offsetValues[0] * 0.1f, offsetValues[1] * 0.1f, -offsetValues[2] * 0.1f)
                : Vector3.Zero;
            var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(offset);
            var worldTransform = handedness * local * world[handIndex] * handedness;
            if (_chibiMode)
                worldTransform *= Matrix4x4.CreateScale(1, 0.84f, 1) * Matrix4x4.CreateTranslation(0, -2, 0);
            prop.Model.Transform = new MatrixTransform3D(ToWpfMatrix(worldTransform));
        }
    }

    private static Matrix3D ToWpfMatrix(Matrix4x4 matrix) => new(
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44);

    private sealed record AnimatedMeshBinding(MeshData Data, MeshGeometry3D Geometry);
    private sealed record AnimatedPropBinding(OutfitDefinition Definition, GeometryModel3D Model, Transform3D StowedTransform);

    private static bool HasItemAnimation(OutfitDefinition definition) =>
        !string.IsNullOrWhiteSpace(definition.IdleAnim)
        || !string.IsNullOrWhiteSpace(definition.TakeOutAnimSequence)
        || !string.IsNullOrWhiteSpace(definition.PutBackAnimSequence)
        || !string.IsNullOrWhiteSpace(definition.FastPlayAnimSequence);

    private static bool IsItemAnimationClip(string clipName, OutfitDefinition definition) =>
        new[] { definition.IdleAnim, definition.TakeOutAnimSequence, definition.PutBackAnimSequence, definition.FastPlayAnimSequence }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Any(name => clipName.Contains(name!, StringComparison.OrdinalIgnoreCase));

    private Material BuildItemMaterial(WardrobeEntry item, MeshData meshData)
    {
        var definition = item.Definition;
        var diffuseName = definition.DiffuseTexture;
        var resolvedMaterial = item.MeshPath is null ? null : SkyResourceResolver.ResolveMaterial(item.MeshPath);
        var texturePath = item.MeshPath is null || string.IsNullOrWhiteSpace(diffuseName)
            ? null : SkyResourceResolver.FindTextureFile(item.MeshPath, diffuseName);
        if (texturePath is null && item.MeshPath is not null && !string.IsNullOrWhiteSpace(resolvedMaterial?.DiffuseTex))
        {
            diffuseName = resolvedMaterial.DiffuseTex;
            texturePath = SkyResourceResolver.FindTextureFile(item.MeshPath, diffuseName);
        }
        var texture = texturePath is null ? null : GameTextureLoader.Load(texturePath);
        var diffuseIsRamp = diffuseName?.Contains("Ramp", StringComparison.OrdinalIgnoreCase) == true;
        if (texture is BitmapSource diffuseBitmap && item.MeshPath is not null)
        {
            var maskName = definition.MaskTexture;
            var maskPath = SkyResourceResolver.FindTextureFile(item.MeshPath, maskName ?? string.Empty)
                ?? SkyResourceResolver.FindTextureFile(item.MeshPath, resolvedMaterial?.MaskTex ?? string.Empty)
                ?? SkyResourceResolver.FindTextureFile(item.MeshPath, resolvedMaterial?.Diffuse2Tex ?? string.Empty);
            if (maskPath is not null && GameTextureLoader.Load(maskPath) is BitmapSource maskBitmap)
                texture = GameDyeTextureBaker.TryBake(item, meshData, diffuseBitmap, maskBitmap, diffuseIsRamp) ?? texture;
        }
        Brush baseBrush;
        // Keep only the known fallback cases neutral; ordinary outfit ramps are
        // handled by the active game Avatar shader when its resources are available.
        var unsupportedAvatarRamp = diffuseIsRamp
            && (definition.Type.Equals("hair", StringComparison.OrdinalIgnoreCase)
                || definition.Name.Equals("CharSkyKid_Mask_BasicQ", StringComparison.OrdinalIgnoreCase));
        if (texture is null || unsupportedAvatarRamp)
        {
            var previewColor = GetItemRenderColor(item);
            baseBrush = new SolidColorBrush(Color.FromRgb(previewColor.R, previewColor.G, previewColor.B));
        }
        else
        {
            // Keep the game's authored diffuse/ramp texture intact. The previous
            // flat-color substitute erased the cape patterns and original outfit
            // palette. Do not fake dyeing by tinting the entire diffuse image;
            // dyeing must target the item's actual masked regions.
            ImageSource image = texture;
            var imageBrush = new ImageBrush(image) { Stretch = Stretch.Fill, Opacity = 1 };
            var scrollSpeed = definition.ShaderParams?.Floats
                .FirstOrDefault(parameter => parameter.Name.Equals("u_scrollSpeed", StringComparison.OrdinalIgnoreCase))?.Value ?? 0;
            if (scrollSpeed != 0)
            {
                imageBrush.TileMode = TileMode.Tile;
                imageBrush.Viewport = new Rect(0, 0, 1, 1);
                imageBrush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox;
                var transform = new TranslateTransform();
                imageBrush.RelativeTransform = transform;
                _scrollingTextures[transform] = scrollSpeed;
            }
            baseBrush = imageBrush;
        }

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(baseBrush));
        return material;
    }

    private static Color GetItemRenderColor(WardrobeEntry item)
    {
        var definition = item.Definition;
        if (definition.BaseHsv is { Length: >= 3 }) return GameColorMath.FromHsvData(definition.BaseHsv);
        if (definition.PrimaryDyeHsv is { Length: >= 3 }) return GameColorMath.FromHsvData(definition.PrimaryDyeHsv);

        // These are the neutral, undyed defaults when OutfitDefs has no per-item HSV.
        // The Avatar ramp shader normally supplies these colors; keep the WPF fallback
        // visibly colored instead of painting every ramp-backed mesh gray.
        return definition.Type.ToLowerInvariant() switch
        {
            "hair" => Color.FromRgb(218, 214, 202),
            "hat" or "horn" => Color.FromRgb(72, 48, 36),
            "mask" or "face" => Color.FromRgb(231, 208, 175),
            "body" or "feet" => Color.FromRgb(242, 235, 218),
            "neck" => Color.FromRgb(198, 183, 156),
            "wing" => Color.FromRgb(239, 231, 207),
            "prop" or "instrument" => Color.FromRgb(166, 119, 76),
            _ => Colors.White,
        };
    }

    private Transform3D BuildItemTransform(OutfitDefinition definition, MeshData? propMesh = null, Rect3D? bodyBounds = null, bool includeChibi = true, bool addPropStow = true, bool addFaceNudge = true, OutfitDefinition? bodyDefinition = null)
    {
        var type = definition.Type.ToLowerInvariant();
        var offset = type switch
        {
            "hair" or "hat" => definition.HairOffset,
            "mask" or "face" or "glasses" => definition.MaskOffset,
            "prop" or "instrument" => definition.PropOffset,
            "neck" or "cape" => definition.NeckOffset,
            "wing" => definition.WingOffset,
            _ => null,
        };
        var scale = type switch
        {
            "hair" or "hat" => definition.HairScale,
            "mask" or "face" or "glasses" => definition.MaskScale,
            "prop" or "instrument" => definition.PropScale,
            "neck" or "cape" => definition.NeckScale,
            "wing" => definition.WingScale,
            _ => null,
        };
        // OutfitDefs also stores slot attachment adjustments on some body records.
        // Compose those with the item-local values so changing body variants updates
        // the attached items in the same way as the game's per-outfit data model.
        var bodyOffset = bodyDefinition is null ? null : type switch
        {
            "hair" or "hat" => bodyDefinition.HairOffset,
            "mask" or "face" or "glasses" => bodyDefinition.MaskOffset,
            "prop" or "instrument" => bodyDefinition.PropOffset,
            "neck" or "cape" => bodyDefinition.NeckOffset,
            "wing" => bodyDefinition.WingOffset,
            _ => null,
        };
        var bodyScale = bodyDefinition is null ? null : type switch
        {
            "hair" or "hat" => bodyDefinition.HairScale,
            "mask" or "face" or "glasses" => bodyDefinition.MaskScale,
            "prop" or "instrument" => bodyDefinition.PropScale,
            "neck" or "cape" => bodyDefinition.NeckScale,
            "wing" => bodyDefinition.WingScale,
            _ => null,
        };
        var isUnsetOffset = IsUnsetAttachmentOffset(offset);
        var hasBodyOffset = bodyOffset is { Length: >= 3 } && !IsUnsetAttachmentOffset(bodyOffset);
        if (isUnsetOffset) offset = null;
        if (hasBodyOffset)
        {
            offset = offset is { Length: >= 3 }
                ? [offset[0] + bodyOffset![0], offset[1] + bodyOffset[1], offset[2] + bodyOffset[2]]
                : bodyOffset;
        }
        var group = new Transform3DGroup();
        var combinedScale = scale is { Length: >= 3 } && bodyScale is { Length: >= 3 }
            ? new[] { scale[0] * bodyScale[0], scale[1] * bodyScale[1], scale[2] * bodyScale[2] }
            : scale is { Length: >= 3 } ? scale : bodyScale;
        if (combinedScale is { Length: >= 3 } && combinedScale.Take(3).All(value => value is > 0 and < 10))
            group.Children.Add(new ScaleTransform3D(combinedScale[0], combinedScale[1], combinedScale[2]));
        // Prop meshes are authored around their own origin (usually near (0,0,0)),
        // while the SkyKid body occupies Y≈0..15. OutfitDefs.propOffset is a small
        // per-item adjustment around the attachment point, not a world-space origin.
        // Put closet props on the character's upper back before applying that adjustment.
        // OutfitDefs vectors are authored in the game's handedness; the mesh adapter
        // flips X and Z to match the WPF camera coordinates.
        // OutfitDefs uses [1, 0, 0] / [1, 1, 0] as an unset attachment offset.
        // Treating that sentinel as a real vector shifted most seasonal pendants
        // one whole preview unit to the left.
        if (offset is { Length: >= 3 } && !isUnsetOffset)
        {
            var adjustmentScale = type is "prop" or "instrument" ? 0.1 : 1.0;
            group.Children.Add(new TranslateTransform3D(-offset[0] * adjustmentScale, offset[1] * adjustmentScale, -offset[2] * adjustmentScale));
        }
        if (type == "feet" && definition.FootYOffset is float footYOffset && float.IsFinite(footYOffset))
            group.Children.Add(new TranslateTransform3D(0, footYOffset, 0));
        // The viewer camera faces the avatar from +Z, so face accessories move
        // toward +Z to sit in front of the mask.
        if (addFaceNudge && type is ("face" or "glasses"))
            group.Children.Add(new TranslateTransform3D(0, 0, 0.12));
        if (addPropStow && (type is "prop" or "instrument"))
        {
            var stow = propMesh is null
                ? new Vector3D(0, 9.35, -3.0)
                : GetPropStowTranslation(propMesh, definition, bodyBounds, bodyDefinition) ?? new Vector3D(0, 9.35, -3.0);
            group.Children.Add(new TranslateTransform3D(stow));
        }
        // The game's test_chibi NPC definition records scale 0.8 and height -2.
        // Uniform scale alone hid the intended height change, so this preview
        // applies a visible body/attachment proxy until the rig morph is decoded.
        if (includeChibi && _chibiMode)
        {
            // The game profile gives height=-2.0. Until the exact rig morph is
            // decoded, make that change visible: compress the outfit body in Y,
            // keep the head-sized accessories full scale, and lower attachments
            // onto the shortened torso. This is an explicit interim rig proxy.
            if (type == "body")
                group.Children.Add(new ScaleTransform3D(1, 0.84, 1));
            else if (type is "wing" or "neck" or "cape")
                group.Children.Add(new ScaleTransform3D(1, 0.84, 1));
            else if (type != "feet")
                group.Children.Add(new TranslateTransform3D(0, -2.0, 0));
        }
        return group;
    }

    private static bool IsUnsetAttachmentOffset(float[]? offset) => offset is { Length: >= 3 }
        && Math.Abs(offset[0] - 1) < .0001
        && Math.Abs(offset[2]) < .0001;

    private IReadOnlyDictionary<string, Vector4> BuildAvatarShaderValues(WardrobeEntry item)
    {
        // DyeColorDefs records encode each game's main/blend pair as one HSV
        // result. Sky.exe supplies the resolved RGB colors through dynamic
        // u_primaryDyeColor/u_secondaryDyeColor shader parameters.
        var lighting = GetAvatarLightingValues();
        var values = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["u_diffuseColor"] = Vector4.One,
            ["u_subsurfaceColor"] = new Vector4(0.32f, 0.24f, 0.20f, 1),
            // These reflected color members are what the compiled Avatar shader
            // actually samples for its primary and secondary mask channels.
            ["u_outfitColor0"] = GetShaderDyeColor(item, item.PrimaryDyeColorDefinition, item.Definition.PrimaryDyeHsv),
            ["u_patternColor"] = GetShaderDyeColor(item, item.SecondaryDyeColorDefinition, item.Definition.SecondaryDyeHsv),
            ["u_primaryDyeColor"] = GetDyeColor(item.PrimaryDyeColorDefinition),
            ["u_secondaryDyeColor"] = GetDyeColor(item.SecondaryDyeColorDefinition),
            ["u_primaryDyeApply"] = new Vector4(item.Definition.DyeablePrimary && item.PrimaryDyeColorDefinition is not null && item.PrimaryDyeColorDefinition.Name != "none" ? 1 : 0),
            ["u_secondaryDyeApply"] = new Vector4(item.Definition.DyeableSecondary && item.SecondaryDyeColorDefinition is not null && item.SecondaryDyeColorDefinition.Name != "none" ? 1 : 0),
            ["u_primaryDyeHighlight"] = Vector4.Zero,
            ["u_secondaryDyeHighlight"] = Vector4.Zero,
            ["u_primaryDyeIsDefault"] = new Vector4(item.PrimaryDyeColorDefinition is null || item.PrimaryDyeColorDefinition.Name == "none" ? 1 : 0),
            ["u_secondaryDyeIsDefault"] = new Vector4(item.SecondaryDyeColorDefinition is null || item.SecondaryDyeColorDefinition.Name == "none" ? 1 : 0),
            ["u_sunDir"] = lighting["u_sunDir"],
            ["u_sunColor"] = lighting["u_sunColor"],
            ["u_averageSkyColor"] = lighting["u_averageSkyColor"],
            ["u_averageSkyboxColor"] = lighting["u_averageSkyboxColor"],
            ["u_averageProbeColor"] = lighting["u_averageProbeColor"],
            ["u_probeExposure"] = new Vector4(1),
            ["u_normScale"] = new Vector4(1),
            ["u_normIntensity"] = new Vector4(1),
            ["u_emissionScale"] = new Vector4(1),
            ["u_glitterIntensity"] = new Vector4(item.Definition.GlitterIntensity ?? 0),
            ["u_glitterScale"] = new Vector4(item.Definition.GlitterScale ?? 1),
            ["u_glitterSize"] = new Vector4(item.Definition.GlitterSize ?? 1),
            ["u_debugRenderMode"] = Vector4.Zero,
            ["u_chamScale"] = new Vector4(1),
            ["u_capeIntegrity"] = new Vector4(1),
            ["u_opacity"] = new Vector4(1),
            ["u_time"] = new Vector4((float)(DateTime.UtcNow - _animationEpoch).TotalSeconds),
        };
        if (item.Definition.ShaderParams is { Floats.Count: > 0 } shaderParameters)
            foreach (var parameter in shaderParameters.Floats)
                if (!string.IsNullOrWhiteSpace(parameter.Name) && float.IsFinite(parameter.Value))
                    values[parameter.Name] = new Vector4(parameter.Value);
        return values;
    }

    private IReadOnlyDictionary<string, Vector4> GetAvatarLightingValues()
    {
        var azimuth = LightAzimuthSlider.Value * Math.PI / 180;
        var elevation = LightElevationSlider.Value * Math.PI / 180;
        var horizontal = Math.Cos(elevation);
        var direction = Vector3.Normalize(new Vector3(
            (float)(Math.Sin(azimuth) * horizontal),
            (float)Math.Sin(elevation),
            (float)(Math.Cos(azimuth) * horizontal)));
        var key = (float)KeyIntensitySlider.Value;
        var ambient = (float)AmbientIntensitySlider.Value;
        var fill = (float)FillIntensitySlider.Value;
        var keyColor = new Vector3(1.0f, 0.95f, 0.88f) * key;
        var skyColor = new Vector3(0.32f, 0.36f, 0.46f) * (ambient + fill) * 0.5f;
        // Sky's Avatar fragment shader evaluates ambient irradiance from the
        // six reflected probe SH vectors. Leaving them zero makes ordinary
        // outfits render nearly black even when sun/average colors are set.
        // A uniform environment has only the L0 coefficient; encode it in the
        // same real-SH basis used by the shader (Y00 = 0.2820948).
        var probeL0 = skyColor * (1f / 0.2820948f);
        return new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
        {
            ["u_sunDir"] = new Vector4(direction, 0),
            ["u_sunColor"] = new Vector4(keyColor, 0),
            ["u_averageSkyColor"] = new Vector4(skyColor, 0),
            ["u_averageSkyboxColor"] = new Vector4(skyColor, 0),
            ["u_averageProbeColor"] = new Vector4(skyColor, 1),
            ["u_probeShR0"] = new Vector4(probeL0.X, 0, 0, 0),
            ["u_probeShR1"] = Vector4.Zero,
            ["u_probeShG0"] = new Vector4(probeL0.Y, 0, 0, 0),
            ["u_probeShG1"] = Vector4.Zero,
            ["u_probeShB0"] = new Vector4(probeL0.Z, 0, 0, 0),
            ["u_probeShB1"] = Vector4.Zero,
            ["u_probeShRGB2"] = Vector4.Zero,
        };
    }

    private static Vector4 GetShaderDyeColor(WardrobeEntry item, DyeColorDefinition? dye, float[]? defaultHsv)
    {
        if (dye is { Name: not "none", Hsv.Length: >= 3 }) return GetDyeColor(dye);
        if (defaultHsv is { Length: >= 3 })
        {
            var color = GameColorMath.FromHsvData(defaultHsv);
            return ToLinearShaderColor(color);
        }

        var preview = GetItemRenderColor(item);
        return ToLinearShaderColor(preview);
    }

    private static Vector4 GetDyeColor(DyeColorDefinition? dye)
    {
        if (dye?.Hsv is not { Length: >= 3 } hsv) return Vector4.Zero;
        var rgb = GameColorMath.SrgbToLinear(GameColorMath.ToRgb(hsv[0], hsv[1] / 100d, hsv[2] / 100d));
        return new Vector4(rgb, 1);
    }

    private static Vector4 ToLinearShaderColor(Color color) => new(
        GameColorMath.SrgbToLinear(color.R / 255f),
        GameColorMath.SrgbToLinear(color.G / 255f),
        GameColorMath.SrgbToLinear(color.B / 255f),
        1);


    private IReadOnlyDictionary<string, Matrix4x4> BuildAvatarShaderMatrices()
    {
        var width = Math.Max(1, Viewport.ActualWidth);
        var height = Math.Max(1, Viewport.ActualHeight);
        var eye = new Vector3((float)Camera.Position.X, (float)Camera.Position.Y, (float)Camera.Position.Z);
        var target = new Vector3((float)(Camera.Position.X + Camera.LookDirection.X), (float)(Camera.Position.Y + Camera.LookDirection.Y), (float)(Camera.Position.Z + Camera.LookDirection.Z));
        var up = new Vector3((float)Camera.UpDirection.X, (float)Camera.UpDirection.Y, (float)Camera.UpDirection.Z);
        var view = Matrix4x4.CreateLookAt(eye, target, up);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView((float)(Camera.FieldOfView * Math.PI / 180), (float)(width / height), 0.01f, 10000f);
        var viewProjection = view * projection;
        Matrix4x4.Invert(viewProjection, out var inverseViewProjection);
        return new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            // SPIRV-Cross emits row_major matrices and the game vertex shader
            // multiplies row vectors (mul(position, matrix)). System.Numerics
            // builds the same row-vector matrices, so transposing here moved
            // every vertex outside the clip volume and produced a blank frame.
            ["u_model"] = Matrix4x4.Identity,
            ["u_view"] = view,
            ["u_proj"] = projection,
            ["u_viewProj"] = viewProjection,
            ["u_viewProjInv"] = inverseViewProjection,
        };
    }

    private void Frame(Point3DCollection points)
    {
        if (points.Count == 0) return;
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        var minZ = points.Min(p => p.Z); var maxZ = points.Max(p => p.Z);
        _target = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        _distance = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ) * 2.5;
        if (_distance < 0.05) _distance = 2;
        CameraChanged();
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _orbiting = true; _lastMouse = e.GetPosition(Viewport); Viewport.CaptureMouse();
    }
    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e) { _orbiting = false; Viewport.ReleaseMouseCapture(); }
    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_orbiting) return;
        var p = e.GetPosition(Viewport); var delta = p - _lastMouse; _lastMouse = p;
        _yaw += delta.X * 0.009; _pitch = Math.Clamp(_pitch - delta.Y * 0.009, -1.45, 1.45); CameraChanged();
    }
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e) { _distance = Math.Clamp(_distance * Math.Exp(-e.Delta * 0.001), 0.05, 100000); CameraChanged(); }

    private void Lighting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AmbientSceneLight is null || KeySceneLight is null || FillSceneLight is null
            || AmbientIntensitySlider is null || KeyIntensitySlider is null || FillIntensitySlider is null
            || LightAzimuthSlider is null || LightElevationSlider is null) return;

        AmbientSceneLight.Color = ScaleLight(Color.FromRgb(119, 125, 145), AmbientIntensitySlider.Value);
        KeySceneLight.Color = ScaleLight(Color.FromRgb(255, 244, 232), KeyIntensitySlider.Value);
        FillSceneLight.Color = ScaleLight(Color.FromRgb(119, 119, 187), FillIntensitySlider.Value);

        var azimuth = LightAzimuthSlider.Value * Math.PI / 180;
        var elevation = LightElevationSlider.Value * Math.PI / 180;
        var horizontal = Math.Cos(elevation);
        KeySceneLight.Direction = new Vector3D(-Math.Sin(azimuth) * horizontal, -Math.Sin(elevation), -Math.Cos(azimuth) * horizontal);
        FillSceneLight.Direction = new Vector3D(Math.Sin(azimuth + Math.PI / 2) * horizontal, Math.Sin(elevation * 0.45), Math.Cos(azimuth + Math.PI / 2) * horizontal);
        var avatarLighting = GetAvatarLightingValues();
        foreach (var material in _avatarMaterials) material.UpdateValues(avatarLighting);
    }

    private static Color ScaleLight(Color source, double intensity) => Color.FromRgb(
        (byte)Math.Clamp(source.R * intensity, 0, 255),
        (byte)Math.Clamp(source.G * intensity, 0, 255),
        (byte)Math.Clamp(source.B * intensity, 0, 255));

    private void CameraChanged()
    {
        var cp = Math.Cos(_pitch);
        var direction = new Vector3D(cp * Math.Cos(_yaw), Math.Sin(_pitch), cp * Math.Sin(_yaw));
        Camera.Position = _target - direction * _distance;
        Camera.LookDirection = _target - Camera.Position;
        Camera.UpDirection = new Vector3D(0, 1, 0);
        if (GameShaderCamera is not null)
        {
            GameShaderCamera.Position = Camera.Position;
            GameShaderCamera.LookDirection = Camera.LookDirection;
            GameShaderCamera.UpDirection = Camera.UpDirection;
            if (_avatarMaterials.Count > 0)
            {
                var matrices = BuildAvatarShaderMatrices();
                foreach (var material in _avatarMaterials) material.UpdateMatrices(matrices);
            }
        }
    }
}
