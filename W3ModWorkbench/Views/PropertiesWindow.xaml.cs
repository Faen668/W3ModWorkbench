using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>One object in a CR2W file, for the list beside its properties: its class, its name when it has one, and how deep it sits.</summary>
public sealed record PropertiesObjectItem(int Index, string ClassName, string Detail, int Depth)
{
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public FontWeight Weight => Depth == 0 ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Any CR2W resource's properties, as REDkit opens every resource in a property window even when it has no editor for
/// it: a layer, a grass mask, a terrain tile, a font, a particle system, a quest. Every object in the file is listed,
/// under the one it belongs to, and the one picked has its properties beside it, the resource first. One of the game's
/// own files shows them from REDkit's source file when REDkit has it, as cooking leaves most of them out.
/// </summary>
public partial class PropertiesWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    // where the file was opened from (its archive, or the file on disk): it says whether it's one of the game's own
    private readonly string? _origin;
    private readonly string? _diskPath;
    private byte[]? _data;
    private Cr2wFile? _file;
    private Cr2wFile? _redkit;

    private PropertiesWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin, string? diskPath)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        _diskPath = diskPath;
        Title = $"{Path.GetFileName(path)} - Properties";
        HeadingText.Text = Path.GetFileName(path);
        KindText.Text = FileKinds.Of(path) is { } kind ? $"{kind.Name}  ·  {path}" : path;
        InfoText.Text = $"{path}   ·   {source}";
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Whether a file's name says it's a CR2W resource, which this window can show.</summary>
    public static bool CanShow(string name) => Cr2wJsonBatch.LooksLikeCr2w(name);

    /// <summary>Opens a CR2W file's properties. <paramref name="archive"/> is where it came from, which says whether it's one of the game's own.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new PropertiesWindow(path, source, () => data, locator, archive?.FilePath, null).Standalone(owner).Show();

    /// <summary>Opens a CR2W file on disk: one in a project, one extracted from a bundle, or one of REDkit's own.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new PropertiesWindow(Core.Projects.FileSafety.GamePathOf(file) ?? Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file, file)
            .Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        var (path, origin) = (_path, _origin);
        PropertyOrigin from;
        try
        {
            (_data, _file, from) = await Task.Run(() =>
            {
                var data = _readData();
                var file = Cr2wFile.Read(data);
                return (data, file, RedkitSource.For(path, origin));
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The file could not be read.\n\n" + ex.Message;
            return;
        }
        _redkit = from.Redkit;
        BusyText.Visibility = Visibility.Collapsed;
        var file = _file;
        if (file.Exports.Count == 0)
        {
            BusyText.Text = "This file has no objects in it.";
            BusyText.Visibility = Visibility.Visible;
            return;
        }
        string what = FileKinds.Of(path)?.Name.ToLowerInvariant() ?? "file";
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, what);
        var items = Objects(file);
        ObjectList.ItemsSource = items;
        ObjectsTitle.Text = $"Objects ({items.Count:N0})";
        HeadingText.Text = $"{Path.GetFileName(path)}  ·  {file.Exports[0].ClassName}";
        InfoText.Text = $"{path}   ·   {_source}   ·   CR2W {file.Version}, {Format.Count(file.Exports.Count, "object")}, {Format.Count(file.Imports.Count, "file")} used";
        ObjectList.SelectedIndex = 0;
    }

    /// <summary>The file's objects, each under the one it belongs to, in the order the file keeps them.</summary>
    private static List<PropertiesObjectItem> Objects(Cr2wFile file)
    {
        var children = file.Exports.Where(x => x.Parent >= 0 && x.Parent < file.Exports.Count).ToLookup(x => x.Parent);
        var items = new List<PropertiesObjectItem>(file.Exports.Count);
        var placed = new HashSet<int>();
        void Add(Cr2wExport export, int depth)
        {
            if (!placed.Add(export.Index)) return;
            items.Add(new PropertiesObjectItem(export.Index, export.ClassName, ObjectName(file, export.Index), depth));
            foreach (var child in children[export.Index]) Add(child, depth + 1);
        }
        foreach (var export in file.Exports.Where(x => x.Parent < 0 || x.Parent >= file.Exports.Count)) Add(export, 0);
        // any left over (a parent that points round in a circle) at the top
        foreach (var export in file.Exports) Add(export, 0);
        return items;
    }

    /// <summary>What an object is called, when it has a name worth showing.</summary>
    private static string ObjectName(Cr2wFile file, int index)
    {
        try
        {
            var o = file.Object(index);
            foreach (string key in new[] { "name", "componentName", "layerName", "debugName" })
                if (o[key] is string n && n.Length > 0 && n != "None") return n;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
        }
        return "";
    }

    private void ObjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_file is null || ObjectList.SelectedItem is not PropertiesObjectItem item) return;
        ObjectTitle.Text = item.Detail.Length > 0 ? $"{item.ClassName}  ·  {item.Detail}" : item.ClassName;
        try
        {
            var o = _file.Object(item.Index);
            Properties.Show(_redkit is { } redkit && RedkitSource.Match(redkit, o, _file) is { } own ? PropertyRow.Of(own, redkit) : PropertyRow.Of(o, _file));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            Properties.Show([new PropertyRow("This object could not be read", "", ex.Message, 0, null)]);
        }
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = ViewerLinks.OpenAsync(this, Message, _locator, path);

    private void Json_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        JsonWindow.OpenCr2w(Path.GetFileName(_path), _source, _data, this, _diskPath is null ? _path : null, _diskPath);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => Copy(_path);

    private void Copy(string text, string? message = null)
    {
        try
        {
            Clipboard.SetText(text);
            _ = Message.Show(null, message ?? $"Copied {text}", MessageKind.Info, primary: message is null ? null : "OK", autoClose: message is null);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            PropertySearch.Focus();
            PropertySearch.SelectAll();
            e.Handled = true;
        }
    }
}
