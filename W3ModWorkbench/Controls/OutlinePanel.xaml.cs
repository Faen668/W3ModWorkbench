using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using System.IO;
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

    public OutlineNode? Parent { get; set; }

    /// <summary>For an XML element: its tag, and the attribute that names it. Null for a declaration.</summary>
    public string? CustomLabel { get; init; }
    public string? CustomDetail { get; init; }

    /// <summary>The children the filter lets through.</summary>
    public List<OutlineNode> Shown { get; set; } = [];

    public bool IsType => CustomLabel is not null ? Children.Count > 0 : Symbol.IsType;
    public string Glyph => CustomLabel is not null ? (Children.Count > 0 ? "\uE8B7" : "\uE943") : DefinitionPeek.KindGlyph(Symbol.Kind);

    /// <summary>What a declaration is called in the tree. Annotated ones outside a class say what they change.</summary>
    public string Label => CustomLabel ?? Symbol.Kind switch
    {
        SymbolKind.Function or SymbolKind.Event when Symbol.Annotation is not null && Symbol.Owner is not null => $"{Symbol.Owner}.{Symbol.Name}()",
        SymbolKind.Function or SymbolKind.Event => Symbol.Name + "()",
        SymbolKind.Field when Symbol.Annotation is not null && Symbol.Owner is not null => $"{Symbol.Owner}.{Symbol.Name}",
        SymbolKind.State => $"{Symbol.Owner}.{Symbol.Name}",
        _ => Symbol.Name,
    };

    /// <summary>The dim text after the name: a type, a return type, a base class or an annotation.</summary>
    public string Detail => CustomLabel is not null ? CustomDetail ?? ""
        : Symbol.Annotation is { } a ? "@" + a
        : Symbol.Kind == SymbolKind.Field && Symbol.Type is { } t ? ": " + t
        : Symbol.Kind is SymbolKind.Function or SymbolKind.Event && Symbol.Type is { } r ? ": " + r
        : Symbol.Base is { } b ? "extends " + b
        : "";

    public string ToolTip => $"{Symbol.Title}  ·  line {Symbol.Line}";

    /// <summary>Tells the same declaration apart across edits.</summary>
    public string Identity => CustomLabel is not null ? $"xml|{Parent?.Identity}|{CustomLabel}|{CustomDetail}" : $"{Symbol.Kind}|{Symbol.Annotation}|{Symbol.Owner}|{Symbol.Name}";

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
            if (parent is not null && byType.TryGetValue(parent.Symbol, out var owner))
            {
                node.Parent = owner;
                owner.Children.Add(node);
            }
            else _roots.Add(node);
        }
        ApplyFilter();
    }

    private const int MaxXmlNodes = 4000;

    /// <summary>
    /// Shows an XML file's elements as a tree, each with the attribute that names it. When the tree is the same as before
    /// (only places moved) it stays as it is. Text that isn't well-formed keeps what was read up to the error.
    /// </summary>
    public void UpdateXml(string text, ScriptFile file)
    {
        var starts = ScriptSearch.LineStarts(text);
        var roots = new List<OutlineNode>();
        var all = new List<OutlineNode>();
        var stack = new Stack<OutlineNode>();
        int At(IXmlLineInfo li) => starts[Math.Clamp(li.LineNumber - 1, 0, starts.Count - 1)] + Math.Max(0, li.LinePosition - 1);
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var li = (IXmlLineInfo)reader;
            while (all.Count < MaxXmlNodes && reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    int start = Math.Max(0, At(li) - 1); // the <
                    var node = new OutlineNode(new ScriptSymbol(reader.LocalName, SymbolKind.Field, null, null, file, start + 1, li.LineNumber), start)
                    {
                        CustomLabel = reader.Name,
                        CustomDetail = Naming(reader),
                        Parent = stack.Count > 0 ? stack.Peek() : null,
                        IsExpanded = stack.Count < 3,
                    };
                    if (node.Parent is null) roots.Add(node);
                    else node.Parent.Children.Add(node);
                    all.Add(node);
                    if (reader.IsEmptyElement) node.End = Math.Max(start, text.IndexOf('>', start) + 1);
                    else stack.Push(node);
                }
                else if (reader.NodeType == XmlNodeType.EndElement && stack.Count > 0)
                {
                    var node = stack.Pop();
                    node.End = Math.Max(node.End, text.IndexOf('>', At(li)) + 1);
                }
            }
        }
        catch (XmlException)
        {
            // keep what was read before the error
        }
        foreach (var open in stack) open.End = text.Length;
        if (all.Count == 0 && _all.Count > 0) return;
        Show(all, roots);
    }

    /// <summary>
    /// Shows a JSON file's objects and lists as a tree, each by its key (or [n] in a list) with its "$class" or size
    /// beside it. Small ones that fit on a line (a vector, a curve point) are left out, so the tree stays readable.
    /// </summary>
    public void UpdateJson(string text, ScriptFile file)
    {
        var starts = ScriptSearch.LineStarts(text);
        int LineOf(int offset)
        {
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (starts[mid] <= offset) lo = mid; else hi = mid - 1;
            }
            return lo + 1;
        }
        var roots = new List<OutlineNode>();
        var all = new List<OutlineNode>();
        void Add(Highlighting.JsonBlock block, OutlineNode? parent, int depth)
        {
            if (all.Count >= MaxXmlNodes) return;
            // one that opens and closes on one line isn't worth a row
            if (parent is not null && LineOf(block.Open) == LineOf(Math.Max(block.Open, block.End - 1))) return;
            bool keyed = block.Start < text.Length && text[block.Start] == '"';
            var symbol = new ScriptSymbol(keyed ? block.Name : text[block.Open].ToString(), SymbolKind.Field, null, null, file,
                keyed ? block.Start + 1 : block.Open, LineOf(block.Start));
            var node = new OutlineNode(symbol, block.End)
            {
                CustomLabel = block.Name,
                CustomDetail = block.Label ?? (block.IsList ? $"{block.Count} item{(block.Count == 1 ? "" : "s")}" : $"{block.Count} propert{(block.Count == 1 ? "y" : "ies")}"),
                Parent = parent,
                IsExpanded = depth < 3,
            };
            if (parent is null) roots.Add(node);
            else parent.Children.Add(node);
            all.Add(node);
            foreach (var child in block.Children) Add(child, node, depth + 1);
        }
        foreach (var block in Highlighting.JsonScanner.Structure(text, MaxXmlNodes * 4)) Add(block, null, 0);
        if (all.Count == 0 && _all.Count > 0) return;
        Show(all, roots);
    }

    /// <summary>Puts a tree in the panel, or keeps the one there when only the places moved, so it stays as it was.</summary>
    private void Show(List<OutlineNode> all, List<OutlineNode> roots)
    {
        string signature = string.Join("\n", all.Select(n => $"{n.Identity}|{n.Children.Count}"));
        if (signature == _signature && _all.Count == all.Count)
        {
            for (int i = 0; i < all.Count; i++)
            {
                _all[i].Symbol = all[i].Symbol;
                _all[i].End = all[i].End;
            }
            return;
        }
        var closed = _all.Where(n => !n.IsExpanded).Select(n => n.Identity).ToHashSet();
        foreach (var n in all) if (closed.Contains(n.Identity)) n.IsExpanded = false;
        _signature = signature;
        _all = all;
        _roots = roots;
        ApplyFilter();
    }

    /// <summary>The attribute that names an element: name, id or key, else the first one written.</summary>
    private static string? Naming(XmlReader reader)
    {
        foreach (string key in new[] { "name", "id", "key", "tag", "type" })
            if (reader.GetAttribute(key) is { Length: > 0 } value) return key is "name" or "id" ? value : $"{key}=\"{value}\"";
        if (!reader.HasAttributes) return null;
        reader.MoveToFirstAttribute();
        string first = $"{reader.Name}=\"{reader.Value}\"";
        reader.MoveToElement();
        return first;
    }

    /// <summary>Picks out the innermost declaration the caret is in, without taking the focus from the text.</summary>
    public void Reveal(int offset)
    {
        if (_all.Count == 0) return;
        var node = _all.Where(n => offset >= n.Symbol.Offset && offset <= Math.Max(n.End, n.Symbol.Offset + n.Symbol.Name.Length))
            .OrderByDescending(n => n.Symbol.Offset).FirstOrDefault();
        if (node is null || node.IsSelected) return;
        foreach (var n in _all) if (n.IsSelected && n != node) n.IsSelected = false;
        for (var up = node.Parent; up is not null; up = up.Parent) up.IsExpanded = true;
        node.IsSelected = true;
        Dispatcher.BeginInvoke(() => ContainerFor(node)?.BringIntoView(), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private TreeViewItem? ContainerFor(OutlineNode node)
    {
        var chain = new List<OutlineNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Insert(0, n);
        ItemsControl container = Tree;
        foreach (var n in chain)
        {
            if (container.ItemContainerGenerator.ContainerFromItem(n) is not TreeViewItem item) return null;
            container = item;
        }
        return container as TreeViewItem;
    }

    // ---------------------------------------------------------------- filter

    private void ApplyFilter()
    {
        string query = FilterBox.Text.Trim();
        var shown = _roots.Where(r => Filter(r, query, query.Length == 0)).ToList();
        Tree.ItemsSource = shown;
        EmptyText.Text = _all.Count == 0 ? "Nothing declared in this file." : "Nothing matches the filter.";
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Narrows a node's children to the names containing the text, keeping the parents they're in. Everything under a
    /// name that matches is kept. Returns whether the node stays in the tree.
    /// </summary>
    private static bool Filter(OutlineNode node, string query, bool everything)
    {
        bool self = everything || node.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || node.Detail.Contains(query, StringComparison.OrdinalIgnoreCase);
        var kept = node.Children.Where(c => Filter(c, query, self)).ToList();
        node.Shown = self ? node.Children : kept;
        node.Raise(nameof(OutlineNode.Shown));
        if (query.Length > 0 && kept.Count > 0) node.IsExpanded = true;
        return self || kept.Count > 0;
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
        static OutlineNode? Find(IEnumerable<OutlineNode> nodes, string query)
        {
            foreach (var n in nodes)
            {
                if (n.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || n.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)) return n;
                if (Find(n.Shown, query) is { } inner) return inner;
            }
            return null;
        }
        return Find(Tree.Items.OfType<OutlineNode>(), query);
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
