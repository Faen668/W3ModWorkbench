using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using W3BundleExplorer.Core;
using W3BundleExplorer.Core.Scripts;

namespace W3BundleExplorer;

/// <summary>Edits <see cref="App.Settings"/>. The theme previews live; Cancel puts it back.</summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly string _originalTheme;
    private bool _loaded;

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _settings = settings;
        _originalTheme = settings.Theme;

        (settings.Theme switch { "Light" => ThemeLight, "Dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        OutputBox.Text = settings.OutputFolder;
        KeepStructureCheck.IsChecked = settings.KeepStructure;
        OpenWhenDoneCheck.IsChecked = settings.OpenWhenDone;
        GameFolderBox.Text = settings.GameFolder;
        GameFolderBox.TextChanged += (_, _) => UpdateGameStatus();
        UpdateGameStatus();

        var assembly = Assembly.GetExecutingAssembly();
        VersionText.Text = $"Version {assembly.GetName().Version?.ToString(3)}  ·  by Faen90";
        CopyrightText.Text = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright © 2026 Faen90";
        SettingsPathText.Text = $"Settings are saved in {Settings.FilePath}";
        _loaded = true;
    }

    private string SelectedTheme => ThemeLight.IsChecked == true ? "Light" : ThemeDark.IsChecked == true ? "Dark" : "System";

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loaded) App.ApplyTheme(SelectedTheme);
    }

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

    private void DetectGame_Click(object sender, RoutedEventArgs e)
    {
        if (GameLocator.Find() is { } found)
            GameFolderBox.Text = found;
        else
            MessageBox.Show(this, "The Witcher 3 wasn't found in your Steam or GOG libraries. Use the folder button to pick it yourself.",
                "Find game folder", MessageBoxButton.OK, MessageBoxImage.Information);
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
        _settings.OutputFolder = OutputBox.Text.Trim();
        _settings.KeepStructure = KeepStructureCheck.IsChecked == true;
        _settings.OpenWhenDone = OpenWhenDoneCheck.IsChecked == true;
        _settings.GameFolder = GameFolderBox.Text.Trim();
        EnumResolver.GameFolder = _settings.GameFolder;
        _settings.Save();
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
        new PreviewWindow("THIRD-PARTY-NOTICES.md", "W3 Bundle Explorer", text) { Owner = this }.Show();
    }

    /// <summary>Shows the LICENSE file shipped next to the exe, or the built-in copy if it is missing.</summary>
    public static void ShowLicence(Window owner)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        byte[] text = File.Exists(path) ? File.ReadAllBytes(path) : System.Text.Encoding.UTF8.GetBytes(LicenceText);
        new PreviewWindow("LICENSE", "W3 Bundle Explorer", text) { Owner = owner }.Show();
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
            App.ApplyTheme(_originalTheme);
        base.OnClosed(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
