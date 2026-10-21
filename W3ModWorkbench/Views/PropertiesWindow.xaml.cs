using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// One object in a CR2W file, for the list beside its properties: its class, its name when it has one, how deep it sits,
/// and the objects under it, which open and close as a tree's do.
/// </summary>
public sealed class PropertiesObjectItem(int index, string className, string detail, int depth, PropertiesObjectItem? parent) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index => index;
    public string ClassName => className;
    public string Detail => detail;
    public int Depth => depth;
    public PropertiesObjectItem? Parent => parent;
    public List<PropertiesObjectItem> Children { get; } = [];

    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public FontWeight Weight => Depth == 0 ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpanderVisibility => Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SpacerVisibility => Children.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    public string ToolTip => Children.Count > 0 ? L.T("PropertiesWindow.Under", ClassName, L.T("Count.Object", Children.Count)) : ClassName;

    private bool _isExpanded = true;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
        }
    }

    public string Glyph => IsExpanded ? "\uE70D" : "\uE76C";

    /// <summary>Whether its class or name has the text searched for.</summary>
    public bool Matches(string text) =>
        ClassName.Contains(text, StringComparison.OrdinalIgnoreCase) || Detail.Contains(text, StringComparison.OrdinalIgnoreCase);
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
    // the file's objects at the top of the tree, and all of them in order
    private List<PropertiesObjectItem> _roots = [];
    private List<PropertiesObjectItem> _all = [];

    private PropertiesWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin, string? diskPath)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        _diskPath = diskPath;
        Title = L.T("PropertiesWindow.Properties", Path.GetFileName(path));
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

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

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
            BusyText.Text = L.T("PropertiesWindow.FileCouldNotRead", ex.Message);
            return;
        }
        _redkit = from.Redkit;
        BusyText.Visibility = Visibility.Collapsed;
        var file = _file;
        if (file.Exports.Count == 0)
        {
            BusyText.Text = L.T("PropertiesWindow.FileHasNoObjects");
            BusyText.Visibility = Visibility.Visible;
            return;
        }
        string what = FileKinds.Of(path)?.Name.ToLowerInvariant() ?? "file";
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, what);
        (_roots, _all) = Objects(file);
        ShowObjects();
        ObjectsTitle.Text = L.T("PropertiesWindow.Objects2", _all.Count);
        HeadingText.Text = $"{Path.GetFileName(path)}  ·  {file.Exports[0].ClassName}";
        InfoText.Text = L.T("PropertiesWindow.CR2WUsed", path, _source, file.Version, L.T("Count.Object", file.Exports.Count), L.T("Count.File", file.Imports.Count));
        ObjectList.SelectedIndex = 0;
    }

    /// <summary>The file's objects, each under the one it belongs to, in the order the file keeps them: those at the top, and all of them.</summary>
    private static (List<PropertiesObjectItem> Roots, List<PropertiesObjectItem> All) Objects(Cr2wFile file)
    {
        var children = file.Exports.Where(x => x.Parent >= 0 && x.Parent < file.Exports.Count).ToLookup(x => x.Parent);
        var roots = new List<PropertiesObjectItem>();
        var all = new List<PropertiesObjectItem>(file.Exports.Count);
        var placed = new HashSet<int>();
        void Add(Cr2wExport export, PropertiesObjectItem? parent)
        {
            if (!placed.Add(export.Index)) return;
            var item = new PropertiesObjectItem(export.Index, export.ClassName, ObjectName(file, export.Index), parent is null ? 0 : parent.Depth + 1, parent);
            (parent?.Children ?? roots).Add(item);
            all.Add(item);
            foreach (var child in children[export.Index]) Add(child, item);
        }
        foreach (var export in file.Exports.Where(x => x.Parent < 0 || x.Parent >= file.Exports.Count)) Add(export, null);
        // any left over (a parent that points round in a circle) at the top
        foreach (var export in file.Exports) Add(export, null);
        return (roots, all);
    }

    /// <summary>
    /// Lists the objects as the tree stands: those under a closed one left out, or with a search, only those whose
    /// class or name has it and the objects they're under, all open. The one picked stays picked when it's still shown.
    /// </summary>
    private void ShowObjects()
    {
        var picked = ObjectList.SelectedItem as PropertiesObjectItem;
        string text = ObjectSearch.Text.Trim();
        var shown = new List<PropertiesObjectItem>();
        if (text.Length == 0)
        {
            void Walk(PropertiesObjectItem item)
            {
                shown.Add(item);
                if (item.IsExpanded) foreach (var child in item.Children) Walk(child);
            }
            foreach (var root in _roots) Walk(root);
        }
        else
        {
            // a match, and every object above one
            var keep = new HashSet<PropertiesObjectItem>();
            foreach (var item in _all.Where(i => i.Matches(text)))
                for (var up = item; up is not null && keep.Add(up); up = up.Parent) { }
            shown.AddRange(_all.Where(keep.Contains));
        }
        ObjectList.ItemsSource = shown;
        ObjectsTitle.Text = text.Length == 0 ? L.T("PropertiesWindow.Objects2", _all.Count) : L.T("PropertiesWindow.Objects3", _all.Count(i => i.Matches(text)), _all.Count);
        if (picked is not null && shown.Contains(picked)) ObjectList.SelectedItem = picked;
    }

    private void Expander_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PropertiesObjectItem item) return;
        item.IsExpanded = !item.IsExpanded;
        ShowObjects();
        e.Handled = true;
    }

    private void SetExpanded(bool open)
    {
        foreach (var item in _all) item.IsExpanded = open;
        // the resource stays open, so its objects show
        if (!open) foreach (var root in _roots) root.IsExpanded = true;
        ShowObjects();
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetExpanded(true);

    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetExpanded(false);

    private void ObjectSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ObjectSearchPlaceholder.Visibility = ObjectSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowObjects();
    }

    /// <summary>Right opens the object picked, Left closes it (or goes up to the one it's under), as in a tree.</summary>
    private void ObjectList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ObjectList.SelectedItem is not PropertiesObjectItem item || ObjectSearch.Text.Trim().Length > 0) return;
        if (e.Key == Key.Right && item.Children.Count > 0 && !item.IsExpanded)
        {
            item.IsExpanded = true;
            ShowObjects();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            if (item.Children.Count > 0 && item.IsExpanded)
            {
                item.IsExpanded = false;
                ShowObjects();
            }
            else if (item.Parent is { } parent)
            {
                ObjectList.SelectedItem = parent;
                ObjectList.ScrollIntoView(parent);
            }
            (ObjectList.ItemContainerGenerator.ContainerFromItem(ObjectList.SelectedItem) as UIElement)?.Focus();
            e.Handled = true;
        }
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
            Properties.Show([new PropertyRow(L.T("PropertiesWindow.ObjectCouldNotRead"), "", ex.Message, 0, null)]);
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
            _ = Message.Show(null, message ?? L.T("PropertiesWindow.Copied", text), MessageKind.Info, primary: message is null ? null : L.T("Common.OK"), autoClose: message is null);
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
