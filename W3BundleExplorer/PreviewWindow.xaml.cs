using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using W3BundleExplorer.Highlighting;

namespace W3BundleExplorer;

public partial class PreviewWindow : Window
{
    private readonly string _path;
    private readonly byte[] _data;
    private readonly SearchPanel _search;

    public PreviewWindow(string path, string source, byte[] data)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _path = path;
        _data = data;
        string name = Path.GetFileName(path);
        Title = $"{name} - Preview";
        FileNameText.Text = name;
        DetailText.Text = $"{source}  ·  {path}  ·  {Format.Bytes(data.Length)}";

        Editor.Options.IndentationSize = 4; // tab width
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);
        Editor.Text = Decode(data);
        if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
        {
            var selection = new SolidColorBrush(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B));
            selection.Freeze();
            Editor.TextArea.SelectionBrush = selection;
            Editor.TextArea.SelectionBorder = null;
            Editor.TextArea.SelectionForeground = null;
        }

        // WitcherScript is coloured with the grammar from WebSpam's witcherscript-vscode extension
        if (Path.GetExtension(path).Equals(".ws", StringComparison.OrdinalIgnoreCase))
            Editor.TextArea.TextView.LineTransformers.Add(new WitcherScriptColorizer(Editor.Document, IsDarkTheme()));

        _search = SearchPanel.Install(Editor.TextArea);
        if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush a)
        {
            var marker = new SolidColorBrush(Color.FromArgb(0x55, a.Color.R, a.Color.G, a.Color.B));
            marker.Freeze();
            _search.MarkerBrush = marker;
        }
        Loaded += (_, _) => Editor.TextArea.Focus();
    }

    private static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        int skip = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(data, skip, data.Length - skip);
    }

    /// <summary>Whether the window ended up with a dark theme, judged by how bright its text colour is.</summary>
    private bool IsDarkTheme() =>
        TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
        && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(Editor.Text);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string ext = Path.GetExtension(_path);
        var dialog = new SaveFileDialog
        {
            Title = "Save file",
            FileName = Path.GetFileName(_path),
            Filter = ext.Length > 0 ? $"{ext.TrimStart('.').ToUpperInvariant()} file (*{ext})|*{ext}|All files (*.*)|*.*" : "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, _data);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The file could not be saved.\n\n{ex.Message}", "W3 Bundle Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc closes the search bar first, then the window
            if (_search.IsClosed) Close();
            else _search.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }
}
