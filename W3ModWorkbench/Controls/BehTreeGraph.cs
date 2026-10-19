using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.BehTrees;

namespace W3ModWorkbench;

/// <summary>
/// A behaviour tree drawn top down: the root at the top, each node's children in a row under it in the order the tree
/// tries them, a curve from each node to each child. Nodes are coloured by what they do and show their priority. Drag
/// to move around, use the wheel to zoom, click a node to select it, double-click a node to fold its branch away or
/// open it again, and double-click the background to see it all.
/// </summary>
public sealed class BehTreeGraph : FrameworkElement
{
    private const double NodeWidth = 210, NodeHeight = 50, ColumnGap = 22, RowGap = 44;

    private BehTreeNode? _root;
    private readonly Dictionary<BehTreeNode, Rect> _boxes = [];
    private readonly HashSet<BehTreeNode> _folded = [];
    private HashSet<BehTreeNode> _matches = [];
    private BehTreeNode? _selected, _hover;

    // what's shown: graph units to pixels, and where the graph's origin is on screen
    private double _scale = 1;
    private Vector _offset;
    private Point _lastMouse;
    private bool _dragging, _moved;

    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty LineBrushProperty = Brush(nameof(LineBrush));
    public static readonly DependencyProperty NodeBrushProperty = Brush(nameof(NodeBrush));
    public static readonly DependencyProperty AccentBrushProperty = Brush(nameof(AccentBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(BehTreeGraph), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush NodeBrush { get => (Brush)GetValue(NodeBrushProperty); set => SetValue(NodeBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>Raised when a node is clicked, with it, or null for a click on nothing.</summary>
    public event EventHandler<BehTreeNode?>? NodeClicked;

    public BehTreeGraph()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(NodeBrushProperty, "SubtleFillColorSecondaryBrush");
        SetResourceReference(AccentBrushProperty, "AccentFillColorDefaultBrush");
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, e) => { if (e.PreviousSize.Width == 0) FitToView(); };
    }

    public BehTreeNode? Selected
    {
        get => _selected;
        set { _selected = value; InvalidateVisual(); }
    }

    /// <summary>The colour of each kind of node, also used for the legend.</summary>
    public static Color ColorOf(BehTreeNodeKind kind) => kind switch
    {
        BehTreeNodeKind.Composite => Color.FromRgb(0x2E, 0xB6, 0x7D),
        BehTreeNodeKind.Decorator => Color.FromRgb(0xD9, 0xA7, 0x3A),
        BehTreeNodeKind.Condition => Color.FromRgb(0xB5, 0x7B, 0xE0),
        BehTreeNodeKind.Script => Color.FromRgb(0x4C, 0x9A, 0xFF),
        BehTreeNodeKind.Action => Color.FromRgb(0xF2, 0x7A, 0x4A),
        BehTreeNodeKind.Subtree => Color.FromRgb(0xE0, 0x6C, 0x9F),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    public void Show(BehTreeNode? root)
    {
        _root = root;
        _folded.Clear();
        _matches = [];
        _selected = _hover = null;
        Layout();
        FitToView();
    }

    /// <summary>Marks the nodes a search found, and opens any folded branch they're in.</summary>
    public void Highlight(IEnumerable<BehTreeNode> nodes)
    {
        _matches = [.. nodes];
        if (_matches.Count > 0 && _root is not null && Unfold(_root, _matches)) Layout();
        InvalidateVisual();
    }

    /// <summary>Opens every folded branch with one of the nodes in it; whether any was opened.</summary>
    private bool Unfold(BehTreeNode node, HashSet<BehTreeNode> wanted)
    {
        bool changed = false;
        foreach (var child in node.Children)
        {
            changed |= Unfold(child, wanted);
            if (_folded.Contains(node) && child.All().Any(wanted.Contains)) changed |= _folded.Remove(node);
        }
        return changed;
    }

    /// <summary>Folds every branch below a depth, or opens them all with none.</summary>
    public void FoldBelow(int? depth)
    {
        _folded.Clear();
        if (depth is int d && _root is not null) Fold(_root, 0, d);
        Layout();
        FitToView();
    }

    private void Fold(BehTreeNode node, int depth, int below)
    {
        if (node.Children.Count == 0) return;
        if (depth >= below) _folded.Add(node);
        else foreach (var child in node.Children) Fold(child, depth + 1, below);
    }

    /// <summary>
    /// Where each shown node goes: a row for each depth, the nodes with nothing shown under them side by side in the
    /// order they're reached, and each other node over the middle of its children.
    /// </summary>
    private void Layout()
    {
        _boxes.Clear();
        if (_root is null) return;
        double next = 0;
        double Place(BehTreeNode node, int depth)
        {
            double x;
            IReadOnlyList<BehTreeNode> children = _folded.Contains(node) ? [] : node.Children;
            if (children.Count == 0)
            {
                x = next;
                next += NodeWidth + ColumnGap;
            }
            else
            {
                var xs = children.Select(c => Place(c, depth + 1)).ToList();
                x = (xs[0] + xs[^1]) / 2;
            }
            _boxes[node] = new Rect(x, depth * (NodeHeight + RowGap), NodeWidth, NodeHeight);
            return x;
        }
        Place(_root, 0);
    }

    // smaller than this the names can't be read, so a big tree is shown from its root at this size
    private const double SmallestFit = 0.7;

    /// <summary>Fits the tree to the view, or, when that would make it too small to read, shows its top from the root.</summary>
    public void FitToView()
    {
        if (_boxes.Count == 0 || ActualWidth < 10 || ActualHeight < 10 || _root is null)
        {
            InvalidateVisual();
            return;
        }
        var all = _boxes.Values.Aggregate(Rect.Union);
        all.Inflate(30, 30);
        double fit = Math.Min(ActualWidth / all.Width, ActualHeight / all.Height);
        _scale = Math.Clamp(fit, SmallestFit, 1.3);
        if (fit >= SmallestFit)
            _offset = new Vector(ActualWidth / 2 - (all.X + all.Width / 2) * _scale, ActualHeight / 2 - (all.Y + all.Height / 2) * _scale);
        else
        {
            var root = _boxes[_root];
            _offset = new Vector(ActualWidth / 2 - (root.X + root.Width / 2) * _scale, 30 - root.Y * _scale);
        }
        InvalidateVisual();
    }

    private Rect OnScreen(Rect box) => new(box.X * _scale + _offset.X, box.Y * _scale + _offset.Y, box.Width * _scale, box.Height * _scale);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // so the whole of it takes the mouse
        if (_root is null) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = TextElement.GetFontFamily(this);
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var plain = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var view = new Rect(0, 0, ActualWidth, ActualHeight);

        // the lines first, so the boxes sit on them: from a node down to a bar over its children, then down to each, as
        // an organisation chart has them, which stays readable when the children are spread far apart
        var line = new Pen(LineBrush, Math.Max(0.6, 1.2 * _scale));
        line.Freeze();
        foreach (var (node, box) in _boxes)
        {
            if (_folded.Contains(node) || node.Children.Count == 0) continue;
            var from = OnScreen(box);
            var tops = node.Children.Where(_boxes.ContainsKey).Select(c => OnScreen(_boxes[c])).ToList();
            if (tops.Count == 0) continue;
            double bar = from.Bottom + (tops[0].Top - from.Bottom) / 2;
            double left = Math.Min(from.X + from.Width / 2, tops.Min(t => t.X + t.Width / 2));
            double right = Math.Max(from.X + from.Width / 2, tops.Max(t => t.X + t.Width / 2));
            if (!view.IntersectsWith(new Rect(left, from.Bottom, Math.Max(1, right - left), Math.Max(1, tops[0].Top - from.Bottom)))) continue;
            dc.DrawLine(line, new Point(from.X + from.Width / 2, from.Bottom), new Point(from.X + from.Width / 2, bar));
            if (tops.Count > 1 || Math.Abs(tops[0].X - from.X) > 0.5) dc.DrawLine(line, new Point(left, bar), new Point(right, bar));
            foreach (var to in tops) dc.DrawLine(line, new Point(to.X + to.Width / 2, bar), new Point(to.X + to.Width / 2, to.Top));
        }

        foreach (var (node, rect) in _boxes)
        {
            var box = OnScreen(rect);
            if (!view.IntersectsWith(box)) continue;
            var color = new SolidColorBrush(ColorOf(node.Kind));
            color.Freeze();
            bool selected = ReferenceEquals(node, _selected), match = _matches.Contains(node);
            var border = new Pen(selected || match ? AccentBrush : ReferenceEquals(node, _hover) ? TextBrush : color, selected ? 2.6 : match ? 2 : 1.1);
            dc.DrawRoundedRectangle(NodeBrush, border, box, 6 * _scale, 6 * _scale);
            // the kind's colour as a band along the top
            dc.DrawRectangle(color, null, new Rect(box.X + 6 * _scale, box.Y, Math.Max(1, box.Width - 12 * _scale), Math.Max(1, 3 * _scale)));
            if (_scale >= 0.3)
            {
                double right = node.Priority is { } ? 34 : 10;
                var title = new FormattedText(node.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 12 * _scale, TextBrush, dip)
                { MaxTextWidth = Math.Max(1, box.Width - (10 + right) * _scale), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(title, new Point(box.X + 10 * _scale, box.Y + 7 * _scale));
                string detail = node.Detail.Length > 0 ? node.Detail : node.Comment ?? Kind(node.Kind);
                var second = new FormattedText(detail, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 10.5 * _scale, LineBrush, dip)
                { MaxTextWidth = Math.Max(1, box.Width - 20 * _scale), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(second, new Point(box.X + 10 * _scale, box.Y + 26 * _scale));
                if (node.Priority is { } priority)
                {
                    var tag = new FormattedText(priority, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 9.5 * _scale, LineBrush, dip)
                    { MaxTextWidth = 60 * _scale, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(tag, new Point(box.Right - tag.Width - 8 * _scale, box.Y + 8 * _scale));
                }
            }
            // a folded branch says how much is folded under it
            if (_folded.Contains(node))
            {
                int hidden = node.All().Count() - 1;
                var count = new FormattedText($"+{hidden:N0}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 10 * _scale, TextBrush, dip);
                var pill = new Rect(box.X + box.Width / 2 - count.Width / 2 - 6 * _scale, box.Bottom - 2 * _scale, count.Width + 12 * _scale, count.Height + 2 * _scale);
                dc.DrawRoundedRectangle(color, null, pill, pill.Height / 2, pill.Height / 2);
                dc.DrawText(count, new Point(pill.X + 6 * _scale, pill.Y + 1 * _scale));
            }
        }
    }

    /// <summary>A kind of node in words, for a node with nothing more to say on its second line.</summary>
    public static string Kind(BehTreeNodeKind kind) => kind switch
    {
        BehTreeNodeKind.Composite => "Composite",
        BehTreeNodeKind.Decorator => "Decorator",
        BehTreeNodeKind.Condition => "Condition",
        BehTreeNodeKind.Script => "Script task",
        BehTreeNodeKind.Action => "Action",
        BehTreeNodeKind.Subtree => "Subtree",
        _ => "A note in the tree",
    };

    private BehTreeNode? NodeAt(Point p)
    {
        foreach (var (node, box) in _boxes)
            if (OnScreen(box).Contains(p)) return node;
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            if (NodeAt(e.GetPosition(this)) is { Children.Count: > 0 } node) ToggleFold(node);
            else FitToView();
            return;
        }
        _lastMouse = e.GetPosition(this);
        _dragging = CaptureMouse();
        _moved = false;
        e.Handled = true;
    }

    /// <summary>Folds a node's branch away, or opens it, keeping the node where it is on screen.</summary>
    public void ToggleFold(BehTreeNode node)
    {
        var before = _boxes.TryGetValue(node, out var was) ? OnScreen(was) : default;
        if (!_folded.Remove(node)) _folded.Add(node);
        Layout();
        if (_boxes.TryGetValue(node, out var now))
        {
            var after = OnScreen(now);
            _offset += new Vector(before.X - after.X, before.Y - after.Y);
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var now = e.GetPosition(this);
        if (_dragging)
        {
            var delta = now - _lastMouse;
            if (!_moved && delta.Length < 3) return;
            _moved = true;
            _offset += delta;
            _lastMouse = now;
            InvalidateVisual();
            return;
        }
        var at = NodeAt(now);
        if (ReferenceEquals(at, _hover)) return;
        _hover = at;
        Cursor = at is null ? null : Cursors.Hand;
        ToolTip = at is null ? null : $"{at.Title}\n{at.Object.ClassName}" + (at.Task is { } task ? $"\ntask {task.ClassName}" : "")
            + (at.Priority is { } p ? $"\npriority {p}" : "") + (at.Comment is { } c ? $"\n{c}" : "")
            + (at.Children.Count > 0 ? $"\n{at.Children.Count} under it; double-click to fold or open" : "");
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_moved) return;
        var at = NodeAt(e.GetPosition(this));
        Selected = at;
        NodeClicked?.Invoke(this, at);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // zoom about the point under the mouse
        var at = e.GetPosition(this);
        double before = _scale;
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.05, 3);
        _offset = (Vector)at - ((Vector)at - _offset) * (_scale / before);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Scrolls a node into the middle of the view, opening any branch it's folded in, and selects it.</summary>
    public void Reveal(BehTreeNode node)
    {
        if (_root is not null && Unfold(_root, [node])) Layout();
        if (!_boxes.TryGetValue(node, out var rect)) return;
        if (_scale < SmallestFit) _scale = 1;
        var box = OnScreen(rect);
        _offset += new Vector(ActualWidth / 2 - (box.X + box.Width / 2), ActualHeight / 2 - (box.Y + box.Height / 2));
        Selected = node;
    }
}
