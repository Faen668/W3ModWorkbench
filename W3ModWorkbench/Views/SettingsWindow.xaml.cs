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
using W3ModWorkbench.Localisation;

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
    private string _appLanguage;
    private readonly bool _loaded;

    /// <summary>The section shown last, to open on again while the app runs.</summary>
    private static int _lastPage;

    private static readonly double[] FontSizes = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];
    private static readonly int[] TabWidths = [2, 3, 4, 6, 8];

    /// <summary>The sections: their English name (what <see cref="ShowPage"/> takes), the key of the name shown, icon and page.</summary>
    private readonly List<(string Name, string Key, string Icon, FrameworkElement Page)> _pages;

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
        [ // loc-ignore-start: the English names are what ShowPage takes; the names shown are the keys
            ("General", "Settings.Nav.General", "", GeneralPage),
            ("Appearance", "Settings.Nav.Appearance", "", AppearancePage),
            ("Game and tools", "Settings.Nav.Game", "", GamePage),
            ("Language", "Settings.Nav.Language", "", LanguagePage),
            ("Scripts and editors", "Settings.Nav.Scripts", "", ScriptsPage),
            ("Viewers", "Settings.Nav.Viewers", "", ViewersPage),
            ("Privacy and online", "Settings.Nav.Privacy", "", PrivacyPage),
        ]; // loc-ignore-end
        foreach (var (_, key, icon, _) in _pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Width = 22, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Bind(TextBlock.TextProperty, key));
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
        RecipesBox.Text = settings.Cr2wRecipesFolder;
        RecipesHint.Text = L.T("Settings.General.RecipesHint", Core.Cr2w.Cr2wRecipe.DefaultFolder);

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
        _appLanguage = settings.AppLanguage;
        AddAppLanguage("", L.T("Settings.Language.AppWindows"), L.T("Settings.Language.AppWindowsTip"));
        foreach (var language in Loc.Available) AddAppLanguage(language.Tag, language.Name, language.EnglishName);
        UpdateAppLanguageStatus();
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
        FontSizeBox.ItemsSource = FontSizes.Select(s => L.T("Settings.Scripts.FontSizeItem", s)).ToList();
        FontSizeBox.SelectedIndex = Nearest(FontSizes, settings.EditorFontSize);
        TabWidthBox.ItemsSource = TabWidths.Select(w => L.T("Settings.Scripts.TabWidthItem", w)).ToList();
        TabWidthBox.SelectedIndex = Nearest(TabWidths.Select(w => (double)w).ToArray(), settings.EditorTabWidth);
        CheckAsYouTypeCheck.IsChecked = settings.CheckScriptsAsYouType;
        (settings.ParameterHints switch { "Off" => HintsOff, "Always" => HintsAlways, _ => HintsHeld }).IsChecked = true;
        DimUnusedCheck.IsChecked = settings.DimUnusedNames;
        (settings.FormatUseTabs ? IndentTabs : IndentSpaces).IsChecked = true;
        (settings.FormatCompactColon ? ColonCompact : ColonSpaced).IsChecked = true;
        (settings.FormatBraceOnOwnLine ? BraceOwnLine : BraceSameLine).IsChecked = true;
        (settings.FormatAnnotations switch { "OwnLine" => AnnotationsOwnLine, "SameLine" => AnnotationsSameLine, _ => AnnotationsAsWritten }).IsChecked = true;
        (settings.FormatParenSpacing switch { "Spaced" => ParensSpaced, "None" => ParensNone, _ => ParensAsWritten }).IsChecked = true;
        KeepAlignmentCheck.IsChecked = settings.FormatKeepAlignment;
        AlignColonsCheck.IsChecked = settings.FormatAlignFieldColons;
        FormatOnSaveCheck.IsChecked = settings.FormatOnSave;

        // Viewers
        RedkitPropsCheck.IsChecked = settings.PreferRedkitProperties;
        RedkitPropsStatus.Text = FileSafety.RedkitInstalled ? "" : L.T("Settings.Viewers.RedkitMissing");
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

    /// <summary>Opens on a section by its English name: Settings links in the app and the guide go straight to theirs.</summary>
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
                Tag = ThemeName(preset.Name),
                Content = Swatch(preset, dark),
                IsChecked = preset.Name == _preset,
                ToolTip = preset.Accent is null ? L.T("Settings.Appearance.ThemeWindowsTip") : L.T("Settings.Appearance.ThemeSchoolTip", ThemeName(preset.Name)),
            };
            button.Checked += (_, _) => Preset_Checked(preset);
            PresetPanel.Children.Add(button);
        }
    }

    /// <summary>A theme's name as shown: the witcher schools' names, translated where a language has its own.</summary>
    private static string ThemeName(string name) => name switch
    {
        "Windows" => L.T("Settings.Appearance.ThemeWindows"),
        "Wolf" => L.T("Settings.Appearance.ThemeWolf"),
        "Cat" => L.T("Settings.Appearance.ThemeCat"),
        "Griffin" => L.T("Settings.Appearance.ThemeGriffin"),
        "Bear" => L.T("Settings.Appearance.ThemeBear"),
        "Viper" => L.T("Settings.Appearance.ThemeViper"),
        "Manticore" => L.T("Settings.Appearance.ThemeManticore"),
        "Graphite" => L.T("Settings.Appearance.ThemeGraphite"),
        _ => name,
    };

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

    /// <summary>The sample's code, the same in every language.</summary>
    private static readonly string[] SampleLines =
    [ // loc-ignore-start
        "@wrapMethod(CR4Player) function OnSpawned(data : SEntitySpawnData)",
        "{",
        "    wrappedMethod(data); // keep the original",
        "    theGame.GetGuiManager().ShowNotification(\"Toss a coin\", 3.0);",
        "}",
    ]; // loc-ignore-end

    // ---------------------------------------------------------------- general

    private string DefaultProjects => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "W3 Mod Workbench");

    private void UpdateProjectsStatus()
    {
        string folder = ProjectsBox.Text.Trim();
        ProjectsStatus.Text = folder.Length == 0
            ? L.T("Settings.General.ProjectsEmpty", DefaultProjects)
            : Directory.Exists(folder) ? L.T("Settings.General.ProjectsHere")
            : L.T("Settings.General.ProjectsMissing");
    }

    private void UpdateRecentStatus()
    {
        int count = _settings.RecentPaths.Count + _settings.RecentProjects.Count + _settings.AssetRecent.Count;
        RecentStatus.Text = count == 0 ? L.T("Settings.General.RecentNone") : L.T("Settings.General.RecentCount", count);
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
        if (Pick(L.T("Settings.General.ProjectsPick"), ProjectsBox.Text.Trim() is { Length: > 0 } now ? now : DefaultProjects) is { } folder) ProjectsBox.Text = folder;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (Pick(L.T("Settings.General.OutputPick"), OutputBox.Text) is { } folder) OutputBox.Text = folder;
    }

    private void BrowseRecipes_Click(object sender, RoutedEventArgs e)
    {
        if (Pick(L.T("Settings.General.RecipesPick"), Core.Cr2w.Cr2wRecipe.Folder(RecipesBox.Text)) is { } folder) RecipesBox.Text = folder;
    }

    // ---------------------------------------------------------------- game and tools

    private void UpdateGameStatus()
    {
        string folder = GameFolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            string? found = GameLocator.Find();
            SetStatus(GameFolderIcon, GameFolderStatus, found is null ? Status.Info : Status.Good,
                found is null ? L.T("Settings.Game.GameNotFound") : L.T("Settings.Game.GameFoundUsed", found));
        }
        else if (GameLocator.IsGameFolder(folder)) SetStatus(GameFolderIcon, GameFolderStatus, Status.Good, L.T("Settings.Game.GameFound"));
        else SetStatus(GameFolderIcon, GameFolderStatus, Status.Warning, L.T("Settings.Game.GameWrongFolder"));
    }

    private void UpdateDocumentsStatus()
    {
        string folder = DocumentsBox.Text.Trim();
        string used = folder.Length == 0 ? GameDocuments.Default : folder;
        string[] known = ["input.settings", "user.settings", "dx12user.settings", "mods.settings"];
        var has = Directory.Exists(used) ? known.Where(f => File.Exists(Path.Combine(used, f))).ToList() : [];
        if (has.Count > 0)
            SetStatus(DocumentsIcon, DocumentsStatus, Status.Good, (folder.Length == 0 ? L.T("Settings.Game.DocumentsUsing", used) : "") + L.T("Settings.Game.DocumentsHas", string.Join(", ", has)));
        else if (Directory.Exists(used))
            SetStatus(DocumentsIcon, DocumentsStatus, Status.Info, (folder.Length == 0 ? L.T("Settings.Game.DocumentsUsing", used) : "")
                + L.T("Settings.Game.DocumentsNoSettings"));
        else
            SetStatus(DocumentsIcon, DocumentsStatus, folder.Length == 0 ? Status.Info : Status.Warning, folder.Length == 0
                ? L.T("Settings.Game.DocumentsNotYet", used)
                : L.T("Settings.Game.DocumentsMissing"));
    }

    private void BrowseDocuments_Click(object sender, RoutedEventArgs e)
    {
        if (Pick(L.T("Settings.Game.DocumentsPick"), DocumentsBox.Text.Trim() is { Length: > 0 } now ? now : GameDocuments.Default) is { } folder)
            DocumentsBox.Text = folder;
    }

    private void DefaultDocuments_Click(object sender, RoutedEventArgs e) => DocumentsBox.Text = "";

    private void UpdateMergerStatus()
    {
        string path = MergerBox.Text.Trim().Trim('"');
        if (path.Length == 0) SetStatus(MergerIcon, MergerStatus, Status.Info, L.T("Settings.Game.MergerNotSet"));
        else if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) SetStatus(MergerIcon, MergerStatus, Status.Good, L.T("Settings.Game.MergerFound", Path.GetFileName(path)));
        else SetStatus(MergerIcon, MergerStatus, Status.Warning, L.T("Settings.Game.MergerWrongFile"));
    }

    private void DetectMerger_Click(object sender, RoutedEventArgs e)
    {
        if (ScriptMerger.Find(GameFolderBox.Text.Trim().Trim('"')) is { } found) MergerBox.Text = found;
        else
            _ = ShowMessage(L.T("Settings.Game.MergerMissingTitle"), L.T("Settings.Game.MergerMissing"), MessageKind.Info);
    }

    private void BrowseMerger_Click(object sender, RoutedEventArgs e)
    {
        string current = MergerBox.Text.Trim().Trim('"');
        var dialog = new OpenFileDialog
        {
            Title = L.T("Settings.Game.MergerPick"),
            Filter = L.T("Common.ProgramsFilter"),
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
                ? L.T("Settings.Game.WccNotSetBuiltIn")
                : L.T("Settings.Game.WccNotSetNoScripts"));
        else if (ScriptCompiler.IsWccLite(path))
            SetStatus(WccIcon, WccStatus, Status.Good, L.T("Settings.Game.WccFound"));
        else
            SetStatus(WccIcon, WccStatus, Status.Warning, File.Exists(path) && Path.GetFileName(path).Equals(ScriptCompiler.ExeName, StringComparison.OrdinalIgnoreCase)
                ? L.T("Settings.Game.WccNoScripts")
                : L.T("Settings.Game.WccWrongFile"));

        bool wcc = path.Length > 0 && ScriptCompiler.IsWccLite(path);
        CompilerStatus.Text = CompilerBuiltIn.IsChecked == true || !wcc
            ? builtIn ? L.T("Settings.Scripts.WillUseBuiltIn") : L.T("Settings.Scripts.WillUseNeither")
            : L.T("Settings.Scripts.WillUseWcc");
    }

    private void DetectWcc_Click(object sender, RoutedEventArgs e)
    {
        if (ScriptCompiler.Find() is { } found)
            WccBox.Text = found;
        else
            _ = ShowMessage(L.T("Settings.Game.WccMissingTitle"), L.T("Settings.Game.WccMissing"), MessageKind.Info);
    }

    private void BrowseWcc_Click(object sender, RoutedEventArgs e)
    {
        string current = WccBox.Text.Trim().Trim('"');
        var dialog = new OpenFileDialog
        {
            Title = L.T("Settings.Game.WccPick"),
            Filter = L.T("Settings.Game.WccFilter"),
            InitialDirectory = File.Exists(current) ? Path.GetDirectoryName(current) : "",
        };
        if (dialog.ShowDialog(this) == true) WccBox.Text = dialog.FileName;
    }

    private void DetectGame_Click(object sender, RoutedEventArgs e)
    {
        if (GameLocator.Find() is { } found)
            GameFolderBox.Text = found;
        else
            _ = ShowMessage(L.T("Settings.Game.GameMissingTitle"), L.T("Settings.Game.GameMissing"), MessageKind.Info);
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        if (Pick(L.T("Settings.Game.GamePick"), GameFolderBox.Text) is { } folder) GameFolderBox.Text = folder;
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

    /// <summary>A language's name as its speakers write it, for the list: the same in every language of the app.</summary>
    private static string LanguageName(string handle) => handle switch // loc-ignore-start
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
    }; // loc-ignore-end

    /// <summary>A choice in the app's language list: "" for the Windows setting, else a language's tag.</summary>
    private void AddAppLanguage(string tag, string name, string tip)
    {
        var button = new RadioButton
        {
            Style = (Style)FindResource("Segment"), GroupName = "AppLanguage", Content = name,
            IsChecked = tag.Equals(_appLanguage, StringComparison.OrdinalIgnoreCase), Margin = new Thickness(0, 0, 4, 4), Tag = null,
            ToolTip = tip,
        };
        button.Checked += (_, _) =>
        {
            _appLanguage = tag;
            UpdateAppLanguageStatus();
        };
        AppLanguagePanel.Children.Add(button);
    }

    /// <summary>The language the app will be in, and for the Windows setting, which language Windows shows.</summary>
    private void UpdateAppLanguageStatus()
    {
        string tag = Loc.Resolve(_appLanguage);
        string name = Loc.Available.FirstOrDefault(l => l.Tag == tag)?.Name ?? tag;
        AppLanguageStatus.Text = _appLanguage.Length == 0
            ? L.T("Settings.Language.AppStatusWindows", System.Globalization.CultureInfo.InstalledUICulture.NativeName, name)
            : L.T("Settings.Language.AppStatus", name);
    }

    /// <summary>Whether the game installed has the language's text and voices.</summary>
    private void UpdateLanguageStatus()
    {
        string? game = GameFolderBox.Text.Trim() is { Length: > 0 } set && GameLocator.IsGameFolder(set) ? set : GameLocator.Find();
        if (game is null)
        {
            LanguageStatus.Text = L.T("Settings.Language.GameNotFound");
            return;
        }
        string content = Path.Combine(game, "content", "content0");
        bool text = File.Exists(Path.Combine(content, $"{_language}.w3strings"));
        bool voices = File.Exists(Path.Combine(content, $"{_language}pc.w3speech"));
        string name = LanguageName(_language);
        LanguageStatus.Text = (text, voices) switch
        {
            (true, true) => L.T("Settings.Language.HasAll", name),
            (true, false) => L.T("Settings.Language.HasTextOnly", name),
            (false, true) => L.T("Settings.Language.HasVoicesOnly", name),
            _ => L.T("Settings.Language.HasNone", name),
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
        EditorSample.Text = $"function OnSpawned(data : SEntitySpawnData)\n{{\n{tab}if (IsAlive())\n{tab}{{\n{tab}{tab}Heal(10);\n{tab}}}\n}}"; // loc-ignore: code
        UpdateFormatSample();
    }

    private void Format_Changed(object sender, RoutedEventArgs e)
    {
        if (_loaded) UpdateFormatSample();
    }

    /// <summary>The formatter's choices as picked, before they're saved.</summary>
    private Core.Scripts.Formatting.FormatOptions PickedFormat() => new(
        UseTabs: IndentTabs.IsChecked == true, IndentSize: TabWidths[Math.Max(0, TabWidthBox.SelectedIndex)],
        CompactColon: ColonCompact.IsChecked == true, BraceOnOwnLine: BraceOwnLine.IsChecked == true,
        AlignFieldColons: AlignColonsCheck.IsChecked == true, KeepAlignment: KeepAlignmentCheck.IsChecked == true,
        Annotations: AnnotationsOwnLine.IsChecked == true ? Core.Scripts.Formatting.AnnotationPlacement.OwnLine
            : AnnotationsSameLine.IsChecked == true ? Core.Scripts.Formatting.AnnotationPlacement.SameLine : Core.Scripts.Formatting.AnnotationPlacement.AsWritten,
        ParenSpacing: ParensSpaced.IsChecked == true ? Core.Scripts.Formatting.ParenSpacing.Spaced
            : ParensNone.IsChecked == true ? Core.Scripts.Formatting.ParenSpacing.None : Core.Scripts.Formatting.ParenSpacing.AsWritten);

    // loc-ignore-start: a script, formatted as the choices say, to show what they do
    private const string FormatSampleCode =
        "class MyGuard extends CNewNPC\n{\n\tprivate var health:float;\n\tprivate var maxHealth    :    float;\n\tprivate var name : string;\n\n"
        + "\t@addMethod(CR4Player) function Greet( who : string ) {\n\t\tif(health>0) ShowHello(who,1);\n\t}\n}";
    // loc-ignore-end

    /// <summary>A small script laid out as the choices picked say, with a tab shown as the space it takes.</summary>
    private void UpdateFormatSample()
    {
        if (TabWidthBox.SelectedIndex < 0) return;
        var result = Core.Scripts.Formatting.ScriptFormatter.Format(FormatSampleCode, PickedFormat());
        FormatSample.Text = result.Text.Replace("\t", new string(' ', TabWidths[TabWidthBox.SelectedIndex]));
    }

    // ---------------------------------------------------------------- privacy

    private void UpdateEabStatus()
    {
        bool on = _settings.EasyAssetBrowserOn;
        SetStatus(EabIcon, EabStatus, on ? Status.Good : Status.Info, on
            ? DateTime.TryParse(_settings.EasyAssetBrowserAgreed, out var when) ? L.T("Settings.Privacy.EabOnSince", when) : L.T("Settings.Privacy.EabOn")
            : L.T("Settings.Privacy.EabOff"));
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
        _settings.Cr2wRecipesFolder = RecipesBox.Text.Trim();
        _settings.GameFolder = GameFolderBox.Text.Trim();
        GameScripts.GameFolder = _settings.GameFolder;
        _settings.GameDocumentsFolder = DocumentsBox.Text.Trim();
        GameDocuments.Override = _settings.GameDocumentsFolder;
        _settings.WccLitePath = WccBox.Text.Trim().Trim('"');
        _settings.ScriptMergerPath = MergerBox.Text.Trim().Trim('"');
        _settings.GameLanguage = _language;
        _settings.AppLanguage = _appLanguage;
        _settings.PreferBuiltInCompiler = CompilerBuiltIn.IsChecked == true;
        _settings.PreviewOpenIn = OpenInWindows.IsChecked == true ? "Windows" : "Tabs";
        _settings.PreviewSplit = SplitDown.IsChecked == true ? "Down" : "Right";
        _settings.PeekDefinitions = PeekCheck.IsChecked == true;
        _settings.SuggestNames = CompleteCheck.IsChecked == true;
        _settings.CheckScriptsAsYouType = CheckAsYouTypeCheck.IsChecked == true;
        _settings.ParameterHints = HintsOff.IsChecked == true ? "Off" : HintsAlways.IsChecked == true ? "Always" : "Held";
        _settings.DimUnusedNames = DimUnusedCheck.IsChecked == true;
        var format = PickedFormat();
        _settings.FormatUseTabs = format.UseTabs;
        _settings.FormatCompactColon = format.CompactColon;
        _settings.FormatBraceOnOwnLine = format.BraceOnOwnLine;
        _settings.FormatAlignFieldColons = format.AlignFieldColons;
        _settings.FormatKeepAlignment = format.KeepAlignment;
        _settings.FormatAnnotations = format.Annotations.ToString();
        _settings.FormatParenSpacing = format.ParenSpacing.ToString();
        _settings.FormatOnSave = FormatOnSaveCheck.IsChecked == true;
        _settings.EditorFontSize = FontSizes[Math.Max(0, FontSizeBox.SelectedIndex)];
        _settings.EditorTabWidth = TabWidths[Math.Max(0, TabWidthBox.SelectedIndex)];
        _settings.PreferRedkitProperties = RedkitPropsCheck.IsChecked == true;
        _settings.TextureDetail = Detail512.IsChecked == true ? 512 : Detail2048.IsChecked == true ? 2048 : DetailFull.IsChecked == true ? 0 : 1024;
        _settings.Save();
        Loc.Instance.Apply(Loc.Resolve(_settings.AppLanguage));
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
            : System.Text.Encoding.UTF8.GetBytes(L.T("Settings.Privacy.NoticesMissing") + "\n\n" // loc-ignore-start: the projects' own names
                + "WitcherScript grammar: witcherscript-vscode by webspam (MIT), https://github.com/webspam/witcherscript-vscode\n"
                + "Built-in compiler: a port of the compiler webspam wrote for Aelto's tw3-cahirb (MIT), https://github.com/Aelto/tw3-cahirb/pull/1\n"
                + "Code editor: AvalonEdit by AlphaSierraPapa for the SharpDevelop Team (MIT), https://github.com/icsharpcode/AvalonEdit\n"); // loc-ignore-end
        PreviewWindow.ShowAlone(new PreviewItem("THIRD-PARTY-NOTICES.md", "W3 Mod Workbench", text), owner);
    }

    /// <summary>Shows PRIVACY.md from the app folder, or a short built-in summary if it is missing.</summary>
    public static void ShowPrivacy(Window owner)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "PRIVACY.md");
        byte[] text = File.Exists(path)
            ? File.ReadAllBytes(path)
            : System.Text.Encoding.UTF8.GetBytes(L.T("Settings.Privacy.PrivacyMissing") + "\n\n" + L.T("Settings.Privacy.PrivacySummary") + "\n");
        PreviewWindow.ShowAlone(new PreviewItem("PRIVACY.md", "W3 Mod Workbench", text), owner);
    }

    /// <summary>Shows the LICENSE file shipped next to the exe, or the built-in copy if it is missing.</summary>
    public static void ShowLicence(Window owner)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        byte[] text = File.Exists(path) ? File.ReadAllBytes(path) : System.Text.Encoding.UTF8.GetBytes(LicenceText);
        PreviewWindow.ShowAlone(new PreviewItem("LICENSE", "W3 Mod Workbench", text), owner);
    }

    /// <summary>The licence is the same in every language: its English is what's legally given.</summary>
    private const string LicenceText = // loc-ignore-start
        """
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
        """; // loc-ignore-end

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            App.ApplyTheme(_originalTheme, _originalPreset);
        base.OnClosed(e);
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

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
