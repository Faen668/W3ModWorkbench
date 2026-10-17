using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Highlighting;

namespace W3ModWorkbench;

/// <summary>Edits <see cref="App.Settings"/>. The theme previews live; Cancel puts it back.</summary>
public partial class SettingsWindow : Window, IMessageHost
{
    private readonly Settings _settings;
    private readonly string _originalTheme;
    private readonly string _originalPreset;
    private string _preset;
    private readonly bool _loaded;

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _settings = settings;
        _originalTheme = settings.Theme;
        _originalPreset = settings.ThemePreset;
        _preset = Themes.Find(settings.ThemePreset).Name;
        Sample.SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

        (settings.Theme switch { "Light" => ThemeLight, "Dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        OutputBox.Text = settings.OutputFolder;
        KeepStructureCheck.IsChecked = settings.KeepStructure;
        OpenWhenDoneCheck.IsChecked = settings.OpenWhenDone;
        GameFolderBox.Text = settings.GameFolder;
        (settings.PreviewOpenIn == "Windows" ? OpenInWindows : OpenInTabs).IsChecked = true;
        (settings.PreviewSplit == "Down" ? SplitDown : SplitRight).IsChecked = true;
        PeekCheck.IsChecked = settings.PeekDefinitions;
        GameFolderBox.TextChanged += (_, _) => UpdateGameStatus();
        UpdateGameStatus();
        WccBox.Text = settings.WccLitePath;
        WccBox.TextChanged += (_, _) => UpdateWccStatus();
        UpdateWccStatus();

        var assembly = Assembly.GetExecutingAssembly();
        VersionText.Text = $"Version {assembly.GetName().Version?.ToString(3)}  ·  by Faen90";
        CopyrightText.Text = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright © 2026 Faen90";
        SettingsPathText.Text = $"Settings are saved in {Settings.FilePath}";
        UpdateAppearance();
        _loaded = true;
    }

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

    private void UpdateGameStatus()
    {
        string folder = GameFolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            GameFolderIcon.Text = ""; // info
            GameFolderIcon.ClearValue(ForegroundProperty);
            GameFolderStatus.Text = "Not set. That's fine; it's only needed for scripts opened outside the game folder.";
        }
        else if (GameLocator.IsGameFolder(folder))
        {
            GameFolderIcon.Text = ""; // check mark
            GameFolderIcon.SetResourceReference(ForegroundProperty, "SystemFillColorSuccessBrush");
            GameFolderStatus.Text = "Found the game's scripts. Enum names will be shown.";
        }
        else
        {
            GameFolderIcon.Text = ""; // warning
            GameFolderIcon.SetResourceReference(ForegroundProperty, "SystemFillColorCautionBrush");
            GameFolderStatus.Text = "This isn't The Witcher 3 folder. Choose the one that contains the bin and content folders.";
        }
    }

    private void UpdateWccStatus()
    {
        string path = WccBox.Text.Trim().Trim('"');
        if (path.Length == 0)
        {
            WccIcon.Text = ""; // info
            WccIcon.ClearValue(ForegroundProperty);
            WccStatus.Text = "Not set, so Compile is off. Scripts can still be edited and saved.";
        }
        else if (ScriptCompiler.IsWccLite(path))
        {
            WccIcon.Text = ""; // check mark
            WccIcon.SetResourceReference(ForegroundProperty, "SystemFillColorSuccessBrush");
            WccStatus.Text = "Found wcc_lite and REDkit's game scripts. Script projects can be compiled.";
        }
        else
        {
            WccIcon.Text = ""; // warning
            WccIcon.SetResourceReference(ForegroundProperty, "SystemFillColorCautionBrush");
            WccStatus.Text = File.Exists(path) && Path.GetFileName(path).Equals(ScriptCompiler.ExeName, StringComparison.OrdinalIgnoreCase)
                ? @"That's wcc_lite, but REDkit's r4data\scripts folder isn't two folders up from it, where it's expected."
                : @"Choose wcc_lite.exe in REDkit's bin\x64_RedKit folder.";
        }
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

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose the default output folder", OutputBox.Text) is { } folder) OutputBox.Text = folder;
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = SelectedTheme;
        _settings.ThemePreset = _preset;
        _settings.OutputFolder = OutputBox.Text.Trim();
        _settings.KeepStructure = KeepStructureCheck.IsChecked == true;
        _settings.OpenWhenDone = OpenWhenDoneCheck.IsChecked == true;
        _settings.GameFolder = GameFolderBox.Text.Trim();
        GameScripts.GameFolder = _settings.GameFolder;
        _settings.PreviewOpenIn = OpenInWindows.IsChecked == true ? "Windows" : "Tabs";
        _settings.PreviewSplit = SplitDown.IsChecked == true ? "Down" : "Right";
        _settings.PeekDefinitions = PeekCheck.IsChecked == true;
        _settings.WccLitePath = WccBox.Text.Trim().Trim('"');
        _settings.Save();
        PreviewWindow.ApplySettings();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ViewLicence_Click(object sender, RoutedEventArgs e) => ShowLicence(this);

    private void ViewNotices_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        byte[] text = File.Exists(path)
            ? File.ReadAllBytes(path)
            : System.Text.Encoding.UTF8.GetBytes("THIRD-PARTY-NOTICES.md is missing from the app folder.\n\n"
                + "WitcherScript grammar: witcherscript-vscode by webspam (MIT), https://github.com/webspam/witcherscript-vscode\n"
                + "Code editor: AvalonEdit by AlphaSierraPapa for the SharpDevelop Team (MIT), https://github.com/icsharpcode/AvalonEdit\n");
        PreviewWindow.ShowAlone(new PreviewItem("THIRD-PARTY-NOTICES.md", "W3 Mod Workbench", text), this);
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
