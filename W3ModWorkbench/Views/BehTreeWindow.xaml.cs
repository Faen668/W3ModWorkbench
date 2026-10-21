using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.BehTrees;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Shows a behaviour tree (.w2behtree), the AI an NPC runs, as a graph from its root down, each node coloured by what
/// it does, and every property of the node picked (and of the script task it runs) beside it. Nodes can be found by
/// name, class, task or the variable they test, and branches folded away.
/// </summary>
public partial class BehTreeWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private BehTreeFile? _tree;
    // where the tree was opened from (its archive, or the file on disk), and REDkit's copy of it to take properties from
    private readonly string? _origin;
    private Cr2wFile? _redkit;
    private BehTreeNode? _shown;
    private List<BehTreeNode> _matches = [];
    private int _match = -1;

    private BehTreeWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        _origin = origin;
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        Title = $"{Path.GetFileName(path)} - Behaviour tree";
        InfoText.Text = $"{path}   ·   {source}";
        Legend.ItemsSource = Enum.GetValues<BehTreeNodeKind>().Select(k =>
        {
            var brush = new SolidColorBrush(BehTreeGraph.ColorOf(k));
            brush.Freeze();
            return new { Name = BehTreeGraph.Kind(k), Brush = brush };
        }).ToList();
        FoldBox.ItemsSource = new[] { "Open every branch" }.Concat(Enumerable.Range(1, 8).Select(d => $"Fold below level {d}")).ToList();
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Whether a file's name says it's a behaviour tree this window can show.</summary>
    public static bool CanShow(string name) => BehTreeFile.IsBehTree(name);

    /// <summary>Opens a behaviour tree in a window of its own.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new BehTreeWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    /// <summary>Opens a behaviour tree file on disk.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new BehTreeWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        try
        {
            _tree = await Task.Run(() => BehTreeFile.Read(_readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The behaviour tree could not be read.\n\n" + ex.Message;
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        if (_tree.Root is null)
        {
            NodeTitle.Text = "This tree is empty: it has no root node.";
            return;
        }
        Graph.Show(_tree.Root);
        _ = LoadRedkitAsync();
        FoldBox.SelectedIndex = 0;
        var tasks = _tree.Root.All().Count(n => n.Task is not null);
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {_tree.File.Version}, {Format.Count(_tree.NodeCount, "node")}, {Format.Count(tasks, "script task")}";
        ShowNode(_tree.Root);
        Graph.Selected = _tree.Root;
    }

    // ---------------------------------------------------------------- the node picked

    private void Graph_NodeClicked(object? sender, BehTreeNode? node)
    {
        if (node is not null) ShowNode(node);
    }

    /// <summary>Shows a node's properties beside the tree, without the children the graph already shows, then its task's.</summary>
    private void ShowNode(BehTreeNode node)
    {
        _shown = node;
        NodeTitle.Text = node.Title;
        var said = new List<string> { BehTreeGraph.Kind(node.Kind), node.Object.ClassName };
        if (node.Task is { } task) said.Add($"runs {task.ClassName}");
        if (node.Priority is { } priority) said.Add($"priority {priority}");
        if (node.Children.Count > 0) said.Add(Format.Count(node.Children.Count, "child", "children"));
        if (node.Detail.Length > 0 && node.Task is null) said.Add(node.Detail);
        NodeDetail.Text = string.Join("  ·  ", said);
        NodeComment.Text = node.Comment ?? "";
        NodeComment.Visibility = node.Comment is null ? Visibility.Collapsed : Visibility.Visible;
        var rows = Rows(node.Object, [.. node.LinkNames, "taskOrigin"]);
        if (node.Task is { } t)
        {
            // the task is a script class, which the engine's own list of classes hasn't got, so it's headed here
            rows.Add(new PropertyRow($"Task: {t.ClassName}", "", "", 0, null, heading: true));
            rows.AddRange(Rows(t, []));
        }
        Properties.Show(rows);
    }

    /// <summary>An object's properties: from REDkit's copy of the tree when it has the same object, otherwise from the tree opened.</summary>
    private List<PropertyRow> Rows(RedObject o, string[] leaveOut) =>
        _redkit is { } redkit && RedkitSource.Match(redkit, o, _tree!.File) is { } own
            ? PropertyRow.Of(own, redkit, leaveOut: leaveOut)
            : PropertyRow.Of(o, _tree!.File, leaveOut: leaveOut);

    /// <summary>Reads REDkit's copy of the tree, for one of the game's own, and says above the properties where they come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        _redkit = from.Redkit;
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, "behaviour tree");
        if (_shown is { } node) ShowNode(node);
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    /// <summary>A file a property refers to: another tree opens here; anything else has its path copied.</summary>
    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    // ---------------------------------------------------------------- finding and folding

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        string text = SearchBox.Text.Trim();
        _matches = text.Length < 2 || _tree?.Root is null ? [] : _tree.Root.All().Where(n => Matches(n, text)).ToList();
        _match = -1;
        Graph.Highlight(_matches);
        MatchText.Text = text.Length < 2 ? "" : _matches.Count == 0 ? "none" : Format.Count(_matches.Count, "node");
        if (_matches.Count > 0) NextMatch();
    }

    private static bool Matches(BehTreeNode node, string text) =>
        node.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || node.Detail.Contains(text, StringComparison.OrdinalIgnoreCase)
        || node.Object.ClassName.Contains(text, StringComparison.OrdinalIgnoreCase) || node.Task?.ClassName.Contains(text, StringComparison.OrdinalIgnoreCase) == true
        || node.Comment?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _matches.Count == 0) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift) _match = (_match - 2 + _matches.Count * 2) % _matches.Count;
        NextMatch();
        e.Handled = true;
    }

    /// <summary>Moves to the next node the search found, and shows it.</summary>
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
        if (_tree?.Root is null || FoldBox.SelectedIndex < 0) return;
        Graph.FoldBelow(FoldBox.SelectedIndex == 0 ? null : FoldBox.SelectedIndex);
        if (_matches.Count > 0) Graph.Highlight(_matches);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Graph.FitToView();

    private void Copy(string? text, string? message = null)
    {
        if (string.IsNullOrEmpty(text)) return;
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
