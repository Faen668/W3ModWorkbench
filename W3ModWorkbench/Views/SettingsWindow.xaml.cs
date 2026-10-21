using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.LoadOrder;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Highlighting;

namespace W3ModWorkbench;

/// <summary>
/// Edits <see cref="App.Settings"/>, a section at a time: General, Appearance, Game and tools, Language, Scripts and
/// editors, Viewers, and Privacy. The theme previews live; Cancel puts it back. Only choices the app would otherwise
/// make for the user are here; what a window toggles for the moment stays in that window.
/// </summary>
public partial class SettingsWindow : Window, IMessageHost
{
    private readonly Settings _settings;
    private readonly string _originalTheme;
    private readonly string _originalPreset;
    private string _preset;
    private string _language;
    private readonly bool _loaded;

    /// <summary>The section shown last, to open on again while the app runs.</summary>
    private static int _lastPage;

    private static readonly double[] FontSizes = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];
    private static readonly int[] TabWidths = [2, 3, 4, 6, 8];

    /// <summary>The sections: name, icon, page and the words a search would use.</summary>
    private readonly List<(string Name, string Icon, FrameworkElement Page)> _pages;

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        _originalTheme = settings.Theme;
        _originalPreset = settings.ThemePreset;
        _preset = Themes.Find(settings.ThemePreset).Name;
        _language = (W3Strings.Language(settings.GameLanguage) ?? W3Strings.Languages[0]).Handle;
        Sample.SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

        _pages =
        [
            ("General", "", GeneralPage),
            ("Appearance", "", AppearancePage),
            ("Game and tools", "", GamePage),
            ("Language", "", LanguagePage),
            ("Scripts and editors", "", ScriptsPage),
            ("Viewers", "", ViewersPage),
            ("Privacy and online", "", PrivacyPage),
        ];
        foreach (var (name, icon, _) in _pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Width = 22, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = name, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Nav.Items.Add(new ListBoxItem { Content = row });
        }

        // General
        ProjectsBox.Text = settings.ProjectsFolder;
        ProjectsBox.TextChanged += (_, _) => UpdateProjectsStatus();
        UpdateProjectsStatus();
        ReopenProjectCheck.IsChecked = settings.ReopenLastProject;
        UpdateRecentStatus();
        OutputBox.Text = settings.OutputFolder;
        KeepStructureCheck.IsChecked = settings.KeepStructure;
        OpenWhenDoneCheck.IsChecked = settings.OpenWhenDone;
        AskCopyCheck.IsChecked = settings.AskWhichCopy;

        // Appearance
        (settings.Theme switch { "Light" => ThemeLight, "Dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        UpdateAppearance();

        // Game and tools
        GameFolderBox.Text = settings.GameFolder;
        GameFolderBox.TextChanged += (_, _) => { UpdateGameStatus(); UpdateWccStatus(); UpdateLanguageStatus(); };
        UpdateGameStatus();
        DocumentsBox.Text = settings.GameDocumentsFolder;
        DocumentsBox.TextChanged += (_, _) => UpdateDocumentsStatus();
        UpdateDocumentsStatus();
        WccBox.Text = settings.WccLitePath;
        WccBox.TextChanged += (_, _) => UpdateWccStatus();
        UpdateWccStatus();
        MergerBox.Text = settings.ScriptMergerPath;
        MergerBox.TextChanged += (_, _) => UpdateMergerStatus();
        UpdateMergerStatus();

        // Language
        foreach (var language in W3Strings.Languages)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "Language", Content = LanguageName(language.Handle),
                IsChecked = language.Handle == _language, Margin = new Thickness(0, 0, 4, 4), Tag = null,
                ToolTip = language.Handle,
            };
            button.Checked += (_, _) =>
            {
                _language = language.Handle;
                UpdateLanguageStatus();
            };
            LanguagePanel.Children.Add(button);
        }
        UpdateLanguageStatus();

        // Scripts and editors
        (settings.PreferBuiltInCompiler ? CompilerBuiltIn : CompilerAuto).IsChecked = true;
        CompilerAuto.Checked += (_, _) => UpdateWccStatus();
        CompilerBuiltIn.Checked += (_, _) => UpdateWccStatus();
        (settings.PreviewOpenIn == "Windows" ? OpenInWindows : OpenInTabs).IsChecked = true;
        (settings.PreviewSplit == "Down" ? SplitDown : SplitRight).IsChecked = true;
        PeekCheck.IsChecked = settings.PeekDefinitions;
        CompleteCheck.IsChecked = settings.SuggestNames;
        FontSizeBox.ItemsSource = FontSizes.Select(s => $"{s:0} pt").ToList();
        FontSizeBox.SelectedIndex = Nearest(FontSizes, settings.EditorFontSize);
        TabWidthBox.ItemsSource = TabWidths.Select(w => $"{w} spaces").ToList();
        TabWidthBox.SelectedIndex = Nearest(TabWidths.Select(w => (double)w).ToArray(), settings.EditorTabWidth);

        // Viewers
        RedkitPropsCheck.IsChecked = settings.PreferRedkitProperties;
        RedkitPropsStatus.Text = FileSafety.RedkitInstalled ? "" : "REDkit isn't installed or found, so the viewers show the game's cooked files either way.";
        RedkitPropsStatus.Visibility = RedkitPropsStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        (settings.TextureDetail switch { 512 => Detail512, 2048 => Detail2048, 0 => DetailFull, _ => Detail1024 }).IsChecked = true;

        // Privacy
        UpdateEabStatus();

        _loaded = true;
        UpdateEditorSample();
        Nav.SelectedIndex = _lastPage;
    }

    /// <summary>The index of the value nearest to <paramref name="value"/>.</summary>
    private static int Nearest(double[] values, double value) =>
        Array.IndexOf(values, values.MinBy(v => Math.Abs(v - value)));

    // ---------------------------------------------------------------- the sections

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0) return;
        _lastPage = Nav.SelectedIndex;
        for (int i = 0; i < _pages.Count; i++) _pages[i].Page.Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroller.ScrollToTop();
    }

    /// <summary>Opens on a section by its name: Settings links in the app and the guide go straight to theirs.</summary>
    public void ShowPage(string name)
    {
        int index = _pages.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Nav.SelectedIndex = index;
    }

    private void Help_Click(object sender, RoutedEventArgs e) => Help.HelpWindow.ShowHelpFor(this);

    // ---------------------------------------------------------------- appearance

    private string SelectedTheme => ThemeLight.IsChecked == true ? "Light" : ThemeDark.IsChecked == true ? "Dark" : "System";

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        ApplyAppearance();
    }

    private void Preset_Checked(ThemePreset preset)
    {
        if (!_loaded || _preset == preset.Name) return;
        _preset = preset.Name;
        ApplyAppearance();
    }

    /// <summary>
    /// Applies the chosen theme here and in every window. Restyling the window and rebuilding the swatches
    /// jumps the page back to the top, so the scroll position is put back once the new layout is done.
    /// </summary>
    private void ApplyAppearance()
    {
        double offset = SettingsScroller.VerticalOffset;
        App.ApplyTheme(SelectedTheme, _preset);
        UpdateAppearance();
        SettingsScroller.ScrollToVerticalOffset(offset);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SettingsScroller.ScrollToVerticalOffset(offset));
    }

    /// <summary>The theme swatches and the sample's code, in the colours of the mode chosen.</summary>
    private void UpdateAppearance()
    {
        bool dark = Themes.IsDark(SelectedTheme);
        SampleCode.Content = DefinitionPeek.Code(SampleLines, dark ? WitcherScriptColorizer.Palette.Dark : WitcherScriptColorizer.Palette.Light);

        PresetPanel.Children.Clear();
        foreach (var preset in Themes.Presets)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "Preset",
                Tag = preset.Name,
                Content = Swatch(preset, dark),
                IsChecked = preset.Name == _preset,
                ToolTip = preset.Accent is null ? "Your Windows accent colour and window background" : $"{preset.Name} school colours",
            };
            button.Checked += (_, _) => Preset_Checked(preset);
            PresetPanel.Children.Add(button);
        }
    }

    /// <summary>A small picture of a theme: its background, with a bar in its accent colour and a plain one.</summary>
    private static Border Swatch(ThemePreset preset, bool dark)
    {
        var background = preset.Background(dark) ?? (dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        var accent = preset.Accent is { } a ? Themes.FillShade(a, dark)
            : dark ? SystemColors.AccentColorLight2 : SystemColors.AccentColorDark1;
        var plain = dark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1E, 0, 0, 0);

        var face = new Grid();
        face.Children.Add(new Border
        {
            Width = 46, Height = 9, CornerRadius = new CornerRadius(4.5), Background = new SolidColorBrush(accent),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 15, 12, 0),
        });
        face.Children.Add(new Border
        {
            Width = 58, Height = 9, CornerRadius = new CornerRadius(4.5), Background = new SolidColorBrush(plain),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 15),
        });
        return new Border
        {
            Width = 108,
            Height = 62,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(background),
            BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0)),
            Child = face,
        };
    }

    private static readonly string[] SampleLines =
    [
        "@wrapMethod(CR4Player) function OnSpawned(data : SEntitySpawnData)",
        "{",
        "    wrappedMethod(data); // keep the original",
        "    theGame.GetGuiManager().ShowNotification(\"Toss a coin\", 3.0);",
        "}",
    ];

    // ---------------------------------------------------------------- general

    private string DefaultProjects => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "W3 Mod Workbench");

    private void UpdateProjectsStatus()
    {
        string folder = ProjectsBox.Text.Trim();
        ProjectsStatus.Text = folder.Length == 0
            ? $"Empty: {DefaultProjects}. New Project still lets you put one anywhere."
            : Directory.Exists(folder) ? "New Project starts here, and still lets you put one anywhere."
            : "That folder doesn't exist yet. It's made when the first project goes in it.";
    }

    private void UpdateRecentStatus()
    {
        int count = _settings.RecentPaths.Count + _settings.RecentProjects.Count + _settings.AssetRecent.Count;
        RecentStatus.Text = count == 0 ? "Nothing in them." : $"{count:N0} remembered: recent files, projects and Asset Browser files.";
        ClearRecentButton.IsEnabled = count > 0;
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        // straight away, as it's clearing what the app remembers rather than a choice to save
        _settings.RecentPaths.Clear();
        _settings.RecentProjects.Clear();
        _settings.AssetRecent.Clear();
        _settings.Save();
        UpdateRecentStatus();
    }

    private void BrowseProjects_Click(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose where new projects go", ProjectsBox.Text.Trim() is { Length: > 0 } now ? now : DefaultProjects) is { } folder) ProjectsBox.Text = folder;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose the default output folder", OutputBox.Text) is { } folder) OutputBox.Text = folder;
    }

    // ---------------------------------------------------------------- game and tools

    private void UpdateGameStatus()
    {
        string folder = GameFolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            string? found = GameLocator.Find();
            SetStatus(GameFolderIcon, GameFolderStatus, found is null ? Status.Info : Status.Good,
                found is null ? "Not set, and the game wasn't found in your Steam or GOG libraries. Set it to read the game's files and scripts."
                    : $"Not set, so the one found is used: {found}");
        }
        else if (GameLocator.IsGameFolder(folder)) SetStatus(GameFolderIcon, GameFolderStatus, Status.Good, "Found the game's files and scripts.");
        else SetStatus(GameFolderIcon, GameFolderStatus, Status.Warning, "This isn't The Witcher 3 folder. Choose the one that has the bin and content folders.");
    }

    private void UpdateDocumentsStatus()
    {
        string folder = DocumentsBox.Text.Trim();
        string used = folder.Length == 0 ? GameDocuments.Default : folder;
        string[] known = ["input.settings", "user.settings", "dx12user.settings", "mods.settings"];
        var has = Directory.Exists(used) ? known.Where(f => File.Exists(Path.Combine(used, f))).ToList() : [];
        if (has.Count > 0)
            SetStatus(DocumentsIcon, DocumentsStatus, Status.Good, (folder.Length == 0 ? $"Using {used}. " : "") + $"It has {string.Join(", ", has)}.");
        else if (Directory.Exists(used))
            SetStatus(DocumentsIcon, DocumentsStatus, Status.Info, (folder.Length == 0 ? $"Using {used}. " : "")
                + "The game hasn't written its settings there yet: start the game once, or choose where it writes them.");
        else
            SetStatus(DocumentsIcon, DocumentsStatus, folder.Length == 0 ? Status.Info : Status.Warning, folder.Length == 0
                ? $"{used} doesn't exist yet: the game makes it the first time it runs."
                : "That folder doesn't exist.");
    }

    private void BrowseDocuments_Click(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose the folder the game keeps its settings in", DocumentsBox.Text.Trim() is { Length: > 0 } now ? now : GameDocuments.Default) is { } folder)
            DocumentsBox.Text = folder;
    }

    private void DefaultDocuments_Click(object sender, RoutedEventArgs e) => DocumentsBox.Text = "";

    private void UpdateMergerStatus()
    {
        string path = MergerBox.Text.Trim().Trim('"');
        if (path.Length == 0) SetStatus(MergerIcon, MergerStatus, Status.Info, "Not set. The load order analyser still works; it just can't open the merger for you.");
        else if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) SetStatus(MergerIcon, MergerStatus, Status.Good, $"Found {Path.GetFileName(path)}.");
        else SetStatus(MergerIcon, MergerStatus, Status.Warning, "That isn't a program. Choose the merger's .exe.");
    }

    private void DetectMerger_Click(object sender, RoutedEventArgs e)
    {
        if (ScriptMerger.Find(GameFolderBox.Text.Trim().Trim('"')) is { } found) MergerBox.Text = found;
        else
            _ = ShowMessage("Script Merger wasn't found",
                "It isn't in the game folder, Documents or Downloads. Use the folder button to pick its .exe yourself.", MessageKind.Info);
    }

    private void BrowseMerger_Click(object sender, RoutedEventArgs e)
    {
        string current = MergerBox.Text.Trim().Trim('"');
        var dialog = new OpenFileDialog
        {
            Title = "Choose Script Merger",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = File.Exists(current) ? Path.GetDirectoryName(current) : "",
        };
        if (dialog.ShowDialog(this) == true) MergerBox.Text = dialog.FileName;
    }

    private void UpdateWccStatus()
    {
        string path = WccBox.Text.Trim().Trim('"');
        bool builtIn = CompilerChoice.Pick("", GameFolderBox.Text.Trim(), preferBuiltIn: true) is not null;
        if (path.Length == 0)
            SetStatus(WccIcon, WccStatus, Status.Info, builtIn
                ? "Not set. Compile uses the built-in compiler, which reads the game's scripts."
                : "Not set, and the game's scripts weren't found, so Compile is off. Scripts can still be edited and saved.");
        else if (ScriptCompiler.IsWccLite(path))
            SetStatus(WccIcon, WccStatus, Status.Good, "Found wcc_lite and REDkit's game scripts.");
        else
            SetStatus(WccIcon, WccStatus, Status.Warning, File.Exists(path) && Path.GetFileName(path).Equals(ScriptCompiler.ExeName, StringComparison.OrdinalIgnoreCase)
                ? @"That's wcc_lite, but REDkit's r4data\scripts folder isn't two folders up from it, where it's expected."
                : @"Choose wcc_lite.exe in REDkit's bin\x64_RedKit folder.");

        bool wcc = path.Length > 0 && ScriptCompiler.IsWccLite(path);
        CompilerStatus.Text = CompilerBuiltIn.IsChecked == true || !wcc
            ? builtIn ? "Compile will use the built-in compiler." : "Neither compiler can run: the game's scripts weren't found. Set the game folder under Game and tools."
            : "Compile will use wcc_lite.";
    }

    private void DetectWcc_Click(object sender, RoutedEventArgs e)
    {
        if (ScriptCompiler.Find() is { } found)
            WccBox.Text = found;
        else
            _ = ShowMessage("REDkit wasn't found",
                "It isn't in your Steam or GOG libraries. Use the folder button to pick wcc_lite.exe yourself.", MessageKind.Info);
    }

    private void BrowseWcc_Click(object sender, RoutedEventArgs e)
    {
        string current = WccBox.Text.Trim().Trim('"');
        var dialog = new OpenFileDialog
        {
            Title = "Choose REDkit's wcc_lite.exe",
            Filter = "wcc_lite (wcc_lite.exe)|wcc_lite.exe|Programs (*.exe)|*.exe",
            InitialDirectory = File.Exists(current) ? Path.GetDirectoryName(current) : "",
        };
        if (dialog.ShowDialog(this) == true) WccBox.Text = dialog.FileName;
    }

    private void DetectGame_Click(object sender, RoutedEventArgs e)
    {
        if (GameLocator.Find() is { } found)
            GameFolderBox.Text = found;
        else
            _ = ShowMessage("The Witcher 3 wasn't found",
                "It isn't in your Steam or GOG libraries. Use the folder button to pick it yourself.", MessageKind.Info);
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose The Witcher 3 game folder", GameFolderBox.Text) is { } folder) GameFolderBox.Text = folder;
    }

    private string? Pick(string title, string current)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(current) ? current : "" };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private enum Status { Info, Good, Warning }

    private static void SetStatus(TextBlock icon, TextBlock text, Status status, string message)
    {
        icon.Text = status switch { Status.Good => "", Status.Warning => "", _ => "" };
        if (status == Status.Info) icon.ClearValue(ForegroundProperty);
        else icon.SetResourceReference(ForegroundProperty, status == Status.Good ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        text.Text = message;
    }

    // ---------------------------------------------------------------- language

    /// <summary>A language's name as its speakers write it, for the list.</summary>
    private static string LanguageName(string handle) => handle switch
    {
        "en" => "English",
        "pl" => "Polski",
        "de" => "Deutsch",
        "fr" => "Français",
        "it" => "Italiano",
        "es" => "Español",
        "cz" => "Čeština",
        "hu" => "Magyar",
        "ru" => "Русский",
        "jp" => "日本語",
        "zh" => "中文",
        "ar" => "العربية",
        "br" => "Português (Brasil)",
        "esmx" => "Español (México)",
        "kr" => "한국어",
        "tr" => "Türkçe",
        _ => handle,
    };

    /// <summary>Whether the game installed has the language's text and voices.</summary>
    private void UpdateLanguageStatus()
    {
        string? game = GameFolderBox.Text.Trim() is { Length: > 0 } set && GameLocator.IsGameFolder(set) ? set : GameLocator.Find();
        if (game is null)
        {
            LanguageStatus.Text = "The game wasn't found, so it can't be said which languages it has.";
            return;
        }
        string content = Path.Combine(game, "content", "content0");
        bool text = File.Exists(Path.Combine(content, $"{_language}.w3strings"));
        bool voices = File.Exists(Path.Combine(content, $"{_language}pc.w3speech"));
        string name = LanguageName(_language);
        LanguageStatus.Text = (text, voices) switch
        {
            (true, true) => $"Your game has {name} text and voices.",
            (true, false) => $"Your game has {name} text, and no {name} voices: scenes use the English voices.",
            (false, true) => $"Your game has {name} voices but not its text: lines show in English.",
            _ => $"Your game hasn't {name} installed: lines and voices show in English. Steam and GOG install other languages from the game's properties.",
        };
    }

    // ---------------------------------------------------------------- scripts and editors

    private void EditorLook_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) UpdateEditorSample();
    }

    /// <summary>A few lines of code in the font size and tab width picked, with a tab shown as the space it takes.</summary>
    private void UpdateEditorSample()
    {
        if (FontSizeBox.SelectedIndex < 0 || TabWidthBox.SelectedIndex < 0) return;
        EditorSample.FontSize = FontSizes[FontSizeBox.SelectedIndex];
        string tab = new(' ', TabWidths[TabWidthBox.SelectedIndex]);
        EditorSample.Text = $"function OnSpawned(data : SEntitySpawnData)\n{{\n{tab}if (IsAlive())\n{tab}{{\n{tab}{tab}Heal(10);\n{tab}}}\n}}";
    }

    // ---------------------------------------------------------------- privacy

    private void UpdateEabStatus()
    {
        bool on = _settings.EasyAssetBrowserOn;
        string since = DateTime.TryParse(_settings.EasyAssetBrowserAgreed, out var when) ? $" since {when:d MMMM yyyy}" : "";
        SetStatus(EabIcon, EabStatus, on ? Status.Good : Status.Info, on
            ? $"On{since}: the Asset Browser shows its categories and tags."
            : "Off: the app doesn't go online at all.");
        EabOnButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        EabOffButton.Visibility = EabForgetButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // these take effect at once, as turning it on is agreed to in a window of its own

    private void EabOn_Click(object sender, RoutedEventArgs e)
    {
        if (EabConsentWindow.Ask(this)) AssetBrowserWindow.SettingsChanged();
        UpdateEabStatus();
    }

    private void EabOff_Click(object sender, RoutedEventArgs e) => TurnEabOff(deleteLists: false);

    private void EabForget_Click(object sender, RoutedEventArgs e) => TurnEabOff(deleteLists: true);

    private void TurnEabOff(bool deleteLists)
    {
        _settings.EasyAssetBrowserOn = false;
        _settings.Save();
        EasyAssetBrowser.Forget(deleteLists);
        AssetBrowserWindow.SettingsChanged();
        UpdateEabStatus();
    }

    private void Privacy_Click(object sender, RoutedEventArgs e) => ShowPrivacy(this);
    private void Licence_Click(object sender, RoutedEventArgs e) => ShowLicence(this);
    private void Notices_Click(object sender, RoutedEventArgs e) => ShowNotices(this);

    // ---------------------------------------------------------------- saving

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = SelectedTheme;
        _settings.ThemePreset = _preset;
        _settings.ProjectsFolder = ProjectsBox.Text.Trim();
        _settings.ReopenLastProject = ReopenProjectCheck.IsChecked == true;
        _settings.OutputFolder = OutputBox.Text.Trim();
        _settings.KeepStructure = KeepStructureCheck.IsChecked == true;
        _settings.OpenWhenDone = OpenWhenDoneCheck.IsChecked == true;
        _settings.AskWhichCopy = AskCopyCheck.IsChecked == true;
        _settings.GameFolder = GameFolderBox.Text.Trim();
        GameScripts.GameFolder = _settings.GameFolder;
        _settings.GameDocumentsFolder = DocumentsBox.Text.Trim();
        GameDocuments.Override = _settings.GameDocumentsFolder;
        _settings.WccLitePath = WccBox.Text.Trim().Trim('"');
        _settings.ScriptMergerPath = MergerBox.Text.Trim().Trim('"');
        _settings.GameLanguage = _language;
        _settings.PreferBuiltInCompiler = CompilerBuiltIn.IsChecked == true;
        _settings.PreviewOpenIn = OpenInWindows.IsChecked == true ? "Windows" : "Tabs";
        _settings.PreviewSplit = SplitDown.IsChecked == true ? "Down" : "Right";
        _settings.PeekDefinitions = PeekCheck.IsChecked == true;
        _settings.SuggestNames = CompleteCheck.IsChecked == true;
        _settings.EditorFontSize = FontSizes[Math.Max(0, FontSizeBox.SelectedIndex)];
        _settings.EditorTabWidth = TabWidths[Math.Max(0, TabWidthBox.SelectedIndex)];
        _settings.PreferRedkitProperties = RedkitPropsCheck.IsChecked == true;
        _settings.TextureDetail = Detail512.IsChecked == true ? 512 : Detail2048.IsChecked == true ? 2048 : DetailFull.IsChecked == true ? 0 : 1024;
        _settings.Save();
        PreviewWindow.ApplySettings();
        EditorLook.ApplyAll();
        AssetBrowserWindow.SettingsChanged();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Shows THIRD-PARTY-NOTICES.md from the app folder, or a short built-in list if it is missing.</summary>
    public static void ShowNotices(Window owner)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        byte[] text = File.Exists(path)
            ? File.ReadAllBytes(path)
            : System.Text.Encoding.UTF8.GetBytes("THIRD-PARTY-NOTICES.md is missing from the app folder.\n\n"
                + "WitcherScript grammar: witcherscript-vscode by webspam (MIT), https://github.com/webspam/witcherscript-vscode\n"
                + "Built-in compiler: a port of the compiler webspam wrote for Aelto's tw3-cahirb (MIT), https://github.com/Aelto/tw3-cahirb/pull/1\n"
                + "Code editor: AvalonEdit by AlphaSierraPapa for the SharpDevelop Team (MIT), https://github.com/icsharpcode/AvalonEdit\n");
        PreviewWindow.ShowAlone(new PreviewItem("THIRD-PARTY-NOTICES.md", "W3 Mod Workbench", text), owner);
    }

    /// <summary>Shows PRIVACY.md from the app folder, or a short built-in summary if it is missing.</summary>
    public static void ShowPrivacy(Window owner)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "PRIVACY.md");
        byte[] text = File.Exists(path)
            ? File.ReadAllBytes(path)
            : System.Text.Encoding.UTF8.GetBytes("PRIVACY.md is missing from the app folder.\n\n"
                + "W3 Mod Workbench collects no information about you. It works offline and goes online only for EasyAssetBrowser tags, "
                + "which are off until you turn them on: it then downloads their lists from GitHub, which sees your IP address as with any download.\n");
        PreviewWindow.ShowAlone(new PreviewItem("PRIVACY.md", "W3 Mod Workbench", text), owner);
    }

    /// <summary>Shows the LICENSE file shipped next to the exe, or the built-in copy if it is missing.</summary>
    public static void ShowLicence(Window owner)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        byte[] text = File.Exists(path) ? File.ReadAllBytes(path) : System.Text.Encoding.UTF8.GetBytes(LicenceText);
        PreviewWindow.ShowAlone(new PreviewItem("LICENSE", "W3 Mod Workbench", text), owner);
    }

    private const string LicenceText = """
        MIT License

        Copyright (c) 2026 Faen90

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            App.ApplyTheme(_originalTheme, _originalPreset);
        base.OnClosed(e);
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return; // Esc closes the message, not the window
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
