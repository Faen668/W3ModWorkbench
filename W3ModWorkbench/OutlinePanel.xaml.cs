using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Scripts.Navigation;

namespace W3ModWorkbench;

/// <summary>A declaration in the outline: a type with its members under it, or a member or global.</summary>
public sealed class OutlineNode(ScriptSymbol symbol, int end) : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Replaced in place after an edit that keeps the same declarations, so the tree stays as it is.</summary>
    public ScriptSymbol Symbol { get; set; } = symbol;

    /// <summary>Where its body ends, for finding the declaration the caret is in.</summary>
    public int End { get; set; } = end;

    public List<OutlineNode> Children { get; } = [];

    /// <summary>The children the filter lets through.</summary>
    public List<OutlineNode> Shown { get; set; } = [];

    public bool IsType => Symbol.IsType;
    public string Glyph => DefinitionPeek.KindGlyph(Symbol.Kind);

    /// <summary>What a declaration is called in the tree. Annotated ones outside a class say what they change.</summary>
    public string Label => Symbol.Kind switch
    {
        SymbolKind.Function or SymbolKind.Event when Symbol.Annotation is not null && Symbol.Owner is not null => $"{Symbol.Owner}.{Symbol.Name}()",
        SymbolKind.Function or SymbolKind.Event => Symbol.Name + "()",
        SymbolKind.Field when Symbol.Annotation is not null && Symbol.Owner is not null => $"{Symbol.Owner}.{Symbol.Name}",
        SymbolKind.State => $"{Symbol.Owner}.{Symbol.Name}",
        _ => Symbol.Name,
    };

    /// <summary>The dim text after the name: a type, a return type, a base class or an annotation.</summary>
    public string Detail => Symbol.Annotation is { } a ? "@" + a
        : Symbol.Kind == SymbolKind.Field && Symbol.Type is { } t ? ": " + t
        : Symbol.Kind is SymbolKind.Function or SymbolKind.Event && Symbol.Type is { } r ? ": " + r
        : Symbol.Base is { } b ? "extends " + b
        : "";

    public string ToolTip => $"{Symbol.Title}  ·  line {Symbol.Line}";

    /// <summary>Tells the same declaration apart across edits.</summary>
    public string Identity => $"{Symbol.Kind}|{Symbol.Annotation}|{Symbol.Owner}|{Symbol.Name}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The side bar beside a script: everything it declares as a tree, types with their members under them.
/// Clicking a declaration (or Enter) goes to it, and the declaration the caret is in is picked out as it moves.
/// </summary>
public partial class OutlinePanel : UserControl
{
    private List<OutlineNode> _roots = [];
    private List<OutlineNode> _all = [];
    private string _signature = "";

    public OutlinePanel()
    {
        InitializeComponent();
    }

    /// <summary>A declaration was picked, to go to.</summary>
    public event Action<ScriptSymbol>? Picked;

    /// <summary>The close button was pressed.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Shows a file's declarations. When they're the same as before (only their places moved, as after typing
    /// inside a function) the tree stays as it is, open and scrolled where it was.
    /// </summary>
    public void Update(ParsedScript parsed)
    {
        var ends = new Dictionary<ScriptSymbol, int>();
        foreach (var t in parsed.Types) ends[t.Symbol] = t.End;
        foreach (var f in parsed.Functions) ends[f.Symbol] = f.BodyEnd;
        var symbols = parsed.Symbols.Where(s => s.Kind is not (SymbolKind.Parameter or SymbolKind.Local)).OrderBy(s => s.Offset).ToList();

        string signature = string.Join("\n", symbols.Select(s => $"{s.Kind}|{s.Annotation}|{s.Owner}|{s.Name}|{s.Type}|{s.Base}"));
        if (signature == _signature)
        {
            for (int i = 0; i < symbols.Count; i++)
            {
                _all[i].Symbol = symbols[i];
                _all[i].End = ends.GetValueOrDefault(symbols[i], symbols[i].Offset);
            }
            return;
        }

        // keep closed what was closed, by declaration
        var closed = _all.Where(n => !n.IsExpanded).Select(n => n.Identity).ToHashSet();
        _signature = signature;
        _all = [];
        _roots = [];
        var types = parsed.Types.OrderBy(t => t.Start).ToList();
        var byType = new Dictionary<ScriptSymbol, OutlineNode>();
        foreach (var s in symbols)
        {
            var node = new OutlineNode(s, ends.GetValueOrDefault(s, s.Offset));
            if (closed.Contains(node.Identity)) node.IsExpanded = false;
            _all.Add(node);
            if (s.IsType) byType[s] = node;
            // the type it's declared inside, if any: a type's own name sits before its {, so it never contains itself
            var parent = types.LastOrDefault(t => s.Offset > t.Start && s.Offset < t.End);
            if (parent is not null && byType.TryGetValue(parent.Symbol, out var owner)) owner.Children.Add(node);
            else _roots.Add(node);
        }
        ApplyFilter();
    }

    /// <summary>Picks out the innermost declaration the caret is in, without taking the focus from the text.</summary>
    public void Reveal(int offset)
    {
        if (_all.Count == 0) return;
        var node = _all.Where(n => offset >= n.Symbol.Offset && offset <= Math.Max(n.End, n.Symbol.Offset + n.Symbol.Name.Length))
            .OrderByDescending(n => n.Symbol.Offset).FirstOrDefault();
        if (node is null || node.IsSelected) return;
        foreach (var n in _all) if (n.IsSelected && n != node) n.IsSelected = false;
        if (OwnerOf(node) is { IsExpanded: false } parent) parent.IsExpanded = true;
        node.IsSelected = true;
        Dispatcher.BeginInvoke(() => ContainerFor(node)?.BringIntoView(), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private OutlineNode? OwnerOf(OutlineNode node) => _roots.FirstOrDefault(r => r.Children.Contains(node));

    private TreeViewItem? ContainerFor(OutlineNode node)
    {
        if (OwnerOf(node) is { } parent)
            return (Tree.ItemContainerGenerator.ContainerFromItem(parent) as TreeViewItem)?.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
        return Tree.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
    }

    // ---------------------------------------------------------------- filter

    private void ApplyFilter()
    {
        string query = FilterBox.Text.Trim();
        bool Matches(OutlineNode n) => query.Length == 0 || n.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
        var shown = new List<OutlineNode>();
        foreach (var root in _roots)
        {
            root.Shown = Matches(root) ? root.Children : [.. root.Children.Where(Matches)];
            if (Matches(root) || root.Shown.Count > 0) shown.Add(root);
            root.Raise(nameof(OutlineNode.Shown));
            if (query.Length > 0 && root.Shown.Count > 0) root.IsExpanded = true;
        }
        Tree.ItemsSource = shown;
        EmptyText.Text = _all.Count == 0 ? "Nothing declared in this file." : "Nothing matches the filter.";
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FilterBox.Text.Length > 0)
        {
            FilterBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && FirstMatch() is { } match)
        {
            Picked?.Invoke(match.Symbol);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && Tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
        {
            first.Focus();
            e.Handled = true;
        }
    }

    /// <summary>The first declaration, in the order shown, whose name has the filter's text.</summary>
    private OutlineNode? FirstMatch()
    {
        string query = FilterBox.Text.Trim();
        foreach (var root in Tree.Items.OfType<OutlineNode>())
        {
            if (root.Label.Contains(query, StringComparison.OrdinalIgnoreCase)) return root;
            if (root.Shown.FirstOrDefault(n => n.Label.Contains(query, StringComparison.OrdinalIgnoreCase)) is { } member) return member;
        }
        return null;
    }

    // ---------------------------------------------------------------- going to a declaration

    private void Tree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // the expander arrow only opens and closes
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != Tree; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ToggleButton) return;
            if (d is TreeViewItem { DataContext: OutlineNode node })
            {
                Picked?.Invoke(node.Symbol);
                return;
            }
        }
    }

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is OutlineNode node)
        {
            Picked?.Invoke(node.Symbol);
            e.Handled = true;
        }
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in _roots) n.IsExpanded = true;
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in _roots) n.IsExpanded = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
