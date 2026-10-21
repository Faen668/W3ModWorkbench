using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.BehTrees;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Shows a file that keeps a tree of objects (a spawn tree, a steering graph) as a graph from its root down, each node
/// coloured by its kind and labelled with why its parent goes to it, every property of the node picked (or of the file
/// itself, with none picked) beside it, and any tables the file keeps beside its tree under it. Nodes can be found by
/// name, class, comment or setting, and branches folded away.
/// </summary>
public partial class TreeFileWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly Func<byte[], TreeDocument> _read;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private TreeDocument? _doc;
    private Cr2wFile? _redkit;
    private BehTreeNode? _shown;
    private List<BehTreeNode> _matches = [];
    // a panel the kind of file adds over the properties (a job tree's preview), told each node picked
    private readonly Func<TreeFileWindow, ITreePreview?>? _extra;
    private ITreePreview? _preview;
    private int _match = -1;

    private TreeFileWindow(string path, string source, Func<byte[]> readData, Func<byte[], TreeDocument> read, string kind, ResourceLocator locator, string? origin,
        Func<TreeFileWindow, ITreePreview?>? extra = null)
    {
        _extra = extra;
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _read = read;
        _locator = locator;
        _origin = origin;
        Title = $"{Path.GetFileName(path)} - {kind}";
        InfoText.Text = $"{path}   ·   {source}";
        BusyText.Text = $"Reading the {kind.ToLowerInvariant()}…";
        FoldBox.ItemsSource = new[] { "Open every branch" }.Concat(Enumerable.Range(1, 8).Select(d => $"Fold below level {d}")).ToList();
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Opens a tree file in a window of its own, reading it with <paramref name="read"/>.</summary>
    public static void Open(string path, string source, byte[] data, Func<byte[], TreeDocument> read, string kind, ResourceLocator locator, Window owner,
        IArchive? archive = null, Func<TreeFileWindow, ITreePreview?>? extra = null) =>
        new TreeFileWindow(path, source, () => data, read, kind, locator, archive?.FilePath, extra).Standalone(owner).Show();

    /// <summary>Opens a tree file on disk.</summary>
    public static void OpenFile(string file, Func<byte[], TreeDocument> read, string kind, ResourceLocator locator, Window owner,
        Func<TreeFileWindow, ITreePreview?>? extra = null) =>
        new TreeFileWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), read, kind, locator, file, extra).Standalone(owner).Show();

    /// <summary>The locator the window reads with, for a preview to read with too.</summary>
    public ResourceLocator Locator => _locator;

    /// <summary>The file shown, once read.</summary>
    public TreeDocument? Document => _doc;

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        try
        {
            _doc = await Task.Run(() => _read(_readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The file could not be read.\n\n" + ex.Message;
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        FileButtonText.Text = _doc.KindName;
        Legend.ItemsSource = _doc.Legend.Select(l =>
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(l.Tint >> 24), (byte)(l.Tint >> 16), (byte)(l.Tint >> 8), (byte)l.Tint));
            brush.Freeze();
            return new { l.Name, Brush = brush };
        }).ToList();
        ShowTables(_doc.Tables);
        if (_extra?.Invoke(this) is { } preview)
        {
            _preview = preview;
            ExtraHost.Content = preview.View;
            ExtraCard.Visibility = ExtraSplitter.Visibility = Visibility.Visible;
            ExtraRow.Height = new GridLength(1.3, GridUnitType.Star);
            ExtraSplitRow.Height = new GridLength(8);
            SideColumn.Width = new GridLength(620);
        }
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {_doc.File.Version}, {Format.Count(_doc.NodeCount, "node")}"
                        + (_doc.Summary.Length > 0 ? $", {_doc.Summary}" : "");
        _ = LoadRedkitAsync();
        if (_doc.Root is null)
        {
            ShowResource();
            NodeDetail.Text = $"This {_doc.KindName.ToLowerInvariant()} is empty: it has no root node.";
            return;
        }
        Graph.Show(_doc.Root);
        FoldBox.SelectedIndex = 0;
        ShowResource();
    }

    /// <summary>Shows the file's tables under the tree, a tab each, or nothing when it has none.</summary>
    private void ShowTables(IReadOnlyList<TreeTable> tables)
    {
        if (tables.Count == 0) return;
        foreach (var table in tables)
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                ItemsSource = table.Rows.Select(r => r.ToList()).ToList(),
                CanUserResizeRows = false,
            };
            for (int c = 0; c < table.Columns.Count; c++)
                grid.Columns.Add(new DataGridTextColumn { Header = table.Columns[c], Binding = new Binding($"[{c}]") });
            Tables.Items.Add(new TabItem { Header = $"{table.Title} ({table.Rows.Count:N0})", Content = grid });
        }
        Tables.SelectedIndex = 0;
        TablesCard.Visibility = TablesSplitter.Visibility = Visibility.Visible;
        TablesSplitRow.Height = new GridLength(8);
        TablesRow.Height = new GridLength(220, GridUnitType.Pixel);
    }

    // ---------------------------------------------------------------- the node picked

    private void Graph_NodeClicked(object? sender, BehTreeNode? node)
    {
        if (node is not null) ShowNode(node);
        else ShowResource();
    }

    /// <summary>Picks a node as clicking it does, for a preview that picks one.</summary>
    public void Pick(BehTreeNode node)
    {
        Graph.Reveal(node);
        ShowNode(node);
    }

    private void FileProperties_Click(object sender, RoutedEventArgs e)
    {
        Graph.Selected = null;
        ShowResource();
    }

    /// <summary>Shows the file's own object, its spawn tree or steering behaviour, with nothing picked.</summary>
    private void ShowResource()
    {
        if (_doc is null) return;
        _shown = null;
        NodeTitle.Text = $"The {_doc.KindName.ToLowerInvariant()}";
        NodeDetail.Text = $"{_doc.Resource.ClassName}  ·  click a node in the tree to see its own";
        NodeComment.Visibility = Visibility.Collapsed;
        Properties.Show(Rows(_doc.Resource, ["rootNode", "root", "jobTreeRootNode"]));
    }

    /// <summary>Shows a node's properties beside the tree, without the children the graph already shows, then its task's or test's.</summary>
    private void ShowNode(BehTreeNode node)
    {
        _shown = node;
        _preview?.Picked(node);
        NodeTitle.Text = node.Title;
        var said = new List<string> { BehTreeGraph.KindOf(node), node.Object.ClassName };
        if (node.Children.Count > 0) said.Add(Format.Count(node.Children.Count, "child", "children"));
        if (node.Detail.Length > 0) said.Add(node.Detail);
        NodeDetail.Text = string.Join("  ·  ", said);
        string? note = node.Comment ?? node.Label;
        NodeComment.Text = note ?? "";
        NodeComment.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        var rows = Rows(node.Object, [.. node.LinkNames]);
        if (node.Task is { } t)
        {
            rows.Add(new PropertyRow($"{BehTreeGraph.KindOf(node)}: {t.ClassName}", "", "", 0, null, heading: true));
            rows.AddRange(Rows(t, []));
        }
        Properties.Show(rows);
    }

    /// <summary>An object's properties: from REDkit's copy of the file when it has the same object, otherwise from the file opened.</summary>
    private List<PropertyRow> Rows(RedObject o, string[] leaveOut) =>
        _redkit is { } redkit && RedkitSource.Match(redkit, o, _doc!.File) is { } own
            ? PropertyRow.Of(own, redkit, leaveOut: leaveOut)
            : PropertyRow.Of(o, _doc!.File, leaveOut: leaveOut);

    /// <summary>Reads REDkit's copy of the file, for one of the game's own, and says above the properties where they come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        _redkit = from.Redkit;
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, _doc?.KindName.ToLowerInvariant() ?? "file");
        if (_shown is { } node) ShowNode(node);
        else ShowResource();
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    // ---------------------------------------------------------------- finding and folding

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        string text = SearchBox.Text.Trim();
        _matches = text.Length < 2 || _doc?.Root is null ? [] : _doc.Root.All().Where(n => Matches(n, text)).ToList();
        _match = -1;
        Graph.Highlight(_matches);
        MatchText.Text = text.Length < 2 ? "" : _matches.Count == 0 ? "none" : Format.Count(_matches.Count, "node");
        if (_matches.Count > 0) NextMatch();
    }

    private static bool Matches(BehTreeNode node, string text) =>
        node.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || node.Detail.Contains(text, StringComparison.OrdinalIgnoreCase)
        || node.Object.ClassName.Contains(text, StringComparison.OrdinalIgnoreCase) || node.Task?.ClassName.Contains(text, StringComparison.OrdinalIgnoreCase) == true
        || node.Comment?.Contains(text, StringComparison.OrdinalIgnoreCase) == true || node.Label?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _matches.Count == 0) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift) _match = (_match - 2 + _matches.Count * 2) % _matches.Count;
        NextMatch();
        e.Handled = true;
    }

    private void NextMatch()
    {
        _match = (_match + 1) % _matches.Count;
        var node = _matches[_match];
        Graph.Reveal(node);
        ShowNode(node);
        MatchText.Text = $"{_match + 1} of {_matches.Count:N0}";
    }

    private void FoldBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_doc?.Root is null || FoldBox.SelectedIndex < 0) return;
        Graph.FoldBelow(FoldBox.SelectedIndex == 0 ? null : FoldBox.SelectedIndex);
        if (_matches.Count > 0) Graph.Highlight(_matches);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Graph.FitToView();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.None: Graph.FitToView(); break;
            default: return;
        }
        e.Handled = true;
    }
}

/// <summary>A panel a kind of tree file adds to the tree viewer (a job tree's animation preview), told each node picked.</summary>
public interface ITreePreview
{
    FrameworkElement View { get; }
    void Picked(BehTreeNode node);
}
