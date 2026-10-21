using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// An entity's components laid out as the entity editor has them, each a box at its place in the editor's graph,
/// with a curve from each component to the ones attached to it. A component from a template the entity includes says
/// which, as does one from the appearance shown, and each is marked as the editor marks them: I included, A from the
/// appearance, O overridden. Drag to move around, use the wheel to zoom, click a component to select it and
/// double-click to see them all.
/// </summary>
public sealed class EntityGraph : FrameworkElement
{
    private const double NodeHeight = 34;

    private readonly List<(EntityComponent Component, Rect Box)> _nodes = [];
    private EntityLook? _look;
    private EntityComponent? _selected, _hover;

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
        DependencyProperty.Register(name, typeof(Brush), typeof(EntityGraph), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush NodeBrush { get => (Brush)GetValue(NodeBrushProperty); set => SetValue(NodeBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>Raised when a component is clicked, with it, or null for a click on nothing.</summary>
    public event EventHandler<EntityComponent?>? ComponentClicked;

    public EntityGraph()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(NodeBrushProperty, "SubtleFillColorSecondaryBrush");
        SetResourceReference(AccentBrushProperty, "AccentFillColorDefaultBrush");
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, e) => { if (e.PreviousSize.Width == 0) FitToView(); };
    }

    public EntityComponent? Selected
    {
        get => _selected;
        set { _selected = value; InvalidateVisual(); }
    }

    public void Show(EntityLook? look)
    {
        _look = look;
        _selected = _hover = null;
        Layout();
        FitToView();
    }

    /// <summary>
    /// Each component's box, sized to its name, at its place in the graph. Components the editor never placed (all at
    /// the origin, as in some cooked files) are put in a grid instead.
    /// </summary>
    private void Layout()
    {
        _nodes.Clear();
        if (_look is null) return;
        var components = _look.Components;
        bool placed = components.Select(c => (c.GraphX, c.GraphY)).Distinct().Count() > Math.Min(2, components.Count - 1);
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(components.Count)));
        for (int i = 0; i < components.Count; i++)
        {
            var c = components[i];
            double width = Math.Max(90, Math.Max(c.Name.Length, Subtitle(c).Length * 0.85) * 7 + 34);
            var at = placed ? new Point(c.GraphX, c.GraphY) : new Point(i % columns * 220, i / columns * 70);
            _nodes.Add((c, new Rect(at.X, at.Y, width, NodeHeight)));
        }
    }

    // smaller than this the names can't be read, so a big graph is fitted at this and centred on most of it
    private const double SmallestFit = 0.75;

    /// <summary>
    /// Scales and moves the graph so every component fits, or, when that would make them too small to read, shows
    /// the middle of where most of them are.
    /// </summary>
    public void FitToView()
    {
        if (_nodes.Count == 0 || ActualWidth < 10 || ActualHeight < 10)
        {
            InvalidateVisual();
            return;
        }
        var all = _nodes.Select(n => n.Box).Aggregate(Rect.Union);
        all.Inflate(30, 30);
        _scale = Math.Clamp(Math.Min(ActualWidth / all.Width, ActualHeight / all.Height), SmallestFit, 1.5);
        Point middle;
        if (all.Width * _scale <= ActualWidth && all.Height * _scale <= ActualHeight) middle = new Point(all.X + all.Width / 2, all.Y + all.Height / 2);
        else
        {
            // the median of the boxes' middles, which a few far-off components don't pull away
            var xs = _nodes.Select(n => n.Box.X + n.Box.Width / 2).Order().ToList();
            var ys = _nodes.Select(n => n.Box.Y + n.Box.Height / 2).Order().ToList();
            middle = new Point(xs[xs.Count / 2], ys[ys.Count / 2]);
        }
        _offset = new Vector(ActualWidth / 2 - middle.X * _scale, ActualHeight / 2 - middle.Y * _scale);
        InvalidateVisual();
    }

    private Rect OnScreen(Rect box) => new(box.X * _scale + _offset.X, box.Y * _scale + _offset.Y, box.Width * _scale, box.Height * _scale);

    /// <summary>A colour for each kind of component: meshes, skeletons and animation, effects and sound, the rest.</summary>
    private static Color ColorOf(EntityComponent c) => c switch
    {
        { Mesh: not null } => Color.FromRgb(0x4C, 0x9A, 0xFF),
        _ when c.ClassName.Contains("Animated") || c.ClassName.Contains("Skeleton") || c.ClassName.Contains("Mimic") || c.ClassName.Contains("Agent") => Color.FromRgb(0xB5, 0x7B, 0xE0),
        _ when c.ClassName.Contains("Effect") || c.ClassName.Contains("Particle") || c.ClassName.Contains("Light") || c.ClassName.Contains("Sound") => Color.FromRgb(0xF2, 0x99, 0x4A),
        _ when c.ClassName.Contains("Trigger") || c.ClassName.Contains("Interaction") || c.ClassName.Contains("Collider") || c.ClassName.Contains("Physic") => Color.FromRgb(0x2E, 0xB6, 0x7D),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    private static string Short(string className) => className.Length > 1 && className[0] == 'C' && char.IsUpper(className[1]) ? className[1..] : className;

    /// <summary>A component's second line: its class, and the template it comes from without the extension.</summary>
    private static string Subtitle(EntityComponent c) =>
        Short(c.ClassName) + (c.Origin is { } origin ? "  ·  " + origin.Replace(".w2ent", "") : "") + (c.Streamed ? L.T("EntityGraph.Streamed") : "");

    /// <summary>The editor's marks: I included, A from the appearance, O overridden.</summary>
    private static string Badges(EntityComponent c) => (c.Overridden ? "O" : "") + (c.FromAppearance ? "A" : "") + (c.Included ? "I" : "");

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // so the whole of it takes the mouse
        if (_look is null) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var small = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var boxes = _nodes.ToDictionary(n => n.Component, n => OnScreen(n.Box));

        // attachments as curves from the parent's bottom to the child's top; skinned ones dashed
        foreach (var link in _look.Links)
        {
            if (!boxes.TryGetValue(link.Parent, out var from) || !boxes.TryGetValue(link.Child, out var to)) continue;
            var a = new Point(from.X + from.Width / 2, from.Bottom);
            var b = new Point(to.X + to.Width / 2, to.Top);
            double bend = Math.Max(30, Math.Abs(b.Y - a.Y) / 2);
            var curve = new StreamGeometry();
            using (var g = curve.Open())
            {
                g.BeginFigure(a, false, false);
                g.BezierTo(new Point(a.X, a.Y + bend), new Point(b.X, b.Y - bend), b, true, false);
            }
            curve.Freeze();
            var pen = new Pen(LineBrush, ReferenceEquals(link.Child, _selected) || ReferenceEquals(link.Parent, _selected) ? 2.2 : 1.3);
            if (link.Kind != "Hard") pen.DashStyle = DashStyles.Dash;
            dc.DrawGeometry(null, pen, curve);
        }

        foreach (var (component, _) in _nodes)
        {
            var box = boxes[component];
            if (box.Right < 0 || box.Bottom < 0 || box.Left > ActualWidth || box.Top > ActualHeight) continue;
            var color = new SolidColorBrush(ColorOf(component));
            color.Freeze();
            bool selected = ReferenceEquals(component, _selected);
            var border = new Pen(selected ? AccentBrush : ReferenceEquals(component, _hover) ? TextBrush : color, selected ? 2.5 : 1.2);
            dc.DrawRoundedRectangle(NodeBrush, border, box, 5 * _scale, 5 * _scale);
            dc.DrawRectangle(color, null, new Rect(box.X, box.Y + 4 * _scale, 4 * _scale, Math.Max(1, box.Height - 8 * _scale)));
            if (_scale < 0.35) continue; // too small to read
            var name = new FormattedText(component.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12 * _scale, TextBrush, dip)
            { MaxTextWidth = Math.Max(1, box.Width - 16 * _scale), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(name, new Point(box.X + 10 * _scale, box.Y + 3 * _scale));
            var kind = new FormattedText(Subtitle(component), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, small, 10 * _scale, LineBrush, dip)
            { MaxTextWidth = Math.Max(1, box.Width - 16 * _scale), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(kind, new Point(box.X + 10 * _scale, box.Y + 18 * _scale));
            if (Badges(component) is { Length: > 0 } badges)
            {
                var marks = new FormattedText(badges, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 9.5 * _scale, TextBrush, dip);
                var tag = new Rect(box.Right - marks.Width - 8 * _scale, box.Y - 7 * _scale, marks.Width + 6 * _scale, marks.Height + 1);
                dc.DrawRoundedRectangle(NodeBrush, new Pen(color, 1), tag, 3 * _scale, 3 * _scale);
                dc.DrawText(marks, new Point(tag.X + 3 * _scale, tag.Y));
            }
        }
    }

    private EntityComponent? ComponentAt(Point p)
    {
        for (int i = _nodes.Count - 1; i >= 0; i--)
            if (OnScreen(_nodes[i].Box).Contains(p)) return _nodes[i].Component;
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            FitToView();
            return;
        }
        _lastMouse = e.GetPosition(this);
        _dragging = CaptureMouse();
        _moved = false;
        e.Handled = true;
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
        var at = ComponentAt(now);
        if (ReferenceEquals(at, _hover)) return;
        _hover = at;
        Cursor = at is null ? null : Cursors.Hand;
        ToolTip = at is null ? null : $"{at.Name}\n{at.ClassName}" + (at.Origin is { } origin ? L.T("EntityGraph.From", origin) : "") + (at.Mesh is { } mesh ? $"\n{mesh}" : "");
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_moved) return;
        var at = ComponentAt(e.GetPosition(this));
        Selected = at;
        ComponentClicked?.Invoke(this, at);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // zoom about the point under the mouse
        var at = e.GetPosition(this);
        double before = _scale;
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.1, 3);
        _offset = (Vector)at - ((Vector)at - _offset) * (_scale / before);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Scrolls a component into the middle of the view and selects it.</summary>
    public void Reveal(EntityComponent component)
    {
        var node = _nodes.FirstOrDefault(n => ReferenceEquals(n.Component, component));
        if (node.Component is null) return;
        var box = OnScreen(node.Box);
        _offset += new Vector(ActualWidth / 2 - (box.X + box.Width / 2), ActualHeight / 2 - (box.Y + box.Height / 2));
        Selected = component;
    }
}
