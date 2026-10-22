using System.Globalization;
using Vector2 = System.Numerics.Vector2;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// A simplex tree's map, seen from above with north up: each area filled in its own colour and numbered, the rest of
/// the map cut by the tree's lines, a grid in metres, and the world's edge when it's known. Drag to move around, use
/// the wheel to zoom where the mouse is, click a piece to pick its area. The split picked is drawn across the map.
/// </summary>
public sealed class SimplexMap : FrameworkElement
{
    private SimplexTreeFile? _tree;
    private List<SimplexCell> _cells = [];
    private (Vector2 Min, Vector2 Max)? _world;

    // what's shown: metres to pixels, and where the map's middle is on screen
    private double _scale = 1;
    private Vector _offset;
    private Point _lastMouse;
    private bool _dragging, _moved;
    private Vector2? _hover;
    private bool _showAllLines = true, _showNumbers = true;

    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty LineBrushProperty = Brush(nameof(LineBrush));
    public static readonly DependencyProperty GridBrushProperty = Brush(nameof(GridBrush));
    public static readonly DependencyProperty AccentBrushProperty = Brush(nameof(AccentBrush));
    public static readonly DependencyProperty BackBrushProperty = Brush(nameof(BackBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(SimplexMap), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush BackBrush { get => (Brush)GetValue(BackBrushProperty); set => SetValue(BackBrushProperty, value); }

    /// <summary>The area picked (-1 for none), filled more strongly.</summary>
    public int SelectedArea { get; private set; } = -1;

    /// <summary>The split drawn across the map, or -1.</summary>
    public int SelectedSplit { get; private set; } = -1;

    /// <summary>Whether the lines that end in no area are drawn too.</summary>
    public bool ShowAllLines { get => _showAllLines; set { _showAllLines = value; InvalidateVisual(); } }

    /// <summary>Whether each area's pieces are numbered with its id.</summary>
    public bool ShowNumbers { get => _showNumbers; set { _showNumbers = value; InvalidateVisual(); } }

    /// <summary>Raised as the mouse moves over the map, with where it is in metres, or null when it leaves.</summary>
    public event EventHandler<Vector2?>? PointerMoved;

    /// <summary>Raised when a piece of the map is clicked, with where.</summary>
    public event EventHandler<Vector2>? MapClicked;

    public SimplexMap()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(GridBrushProperty, "DividerStrokeColorDefaultBrush");
        SetResourceReference(AccentBrushProperty, "AccentFillColorDefaultBrush");
        SetResourceReference(BackBrushProperty, "SolidBackgroundFillColorBaseBrush");
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, e) => { if (e.PreviousSize.Width == 0) FitToView(); };
    }

    /// <summary>Shows a tree, over the world's ground from <paramref name="world"/> (its corner and far corner) when known.</summary>
    public void Show(SimplexTreeFile tree, (Vector2 Min, Vector2 Max)? world)
    {
        _tree = tree;
        _world = world;
        // cut at the world's ground when it's known: past it there's nothing to be in an area
        _cells = world is { } w ? tree.Cells(w.Min, w.Max) : tree.Cells(tree.Reach);
        SelectedArea = -1;
        SelectedSplit = -1;
        FitToView();
    }

    /// <summary>The pieces of the map, as cut.</summary>
    public IReadOnlyList<SimplexCell> Cells => _cells;

    public void Select(int area, int split)
    {
        SelectedArea = area;
        SelectedSplit = split;
        InvalidateVisual();
    }

    /// <summary>A colour for an area, the same each time: its own hue, spread round the wheel.</summary>
    public static Color ColorOf(int area)
    {
        if (area < 0) return Colors.Transparent;
        double hue = (area * 137.508 + 8) % 360;
        return FromHsv(hue, 0.62, 0.86);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>Frames what matters: the areas, else the world, else the whole map.</summary>
    public void FitToView()
    {
        var named = _cells.Where(c => c.Area >= 0).SelectMany(c => c.Outline).ToList();
        if (named.Count > 0) FitTo(named.Aggregate(new Vector2(float.MaxValue), Vector2.Min), named.Aggregate(new Vector2(float.MinValue), Vector2.Max));
        else if (_world is { } w) FitTo(w.Min, w.Max);
        else FitTo(new Vector2(-1024), new Vector2(1024));
    }

    /// <summary>Frames the whole world when it's known.</summary>
    public void FitWorld()
    {
        if (_world is { } w) FitTo(w.Min, w.Max);
        else FitToView();
    }

    public void FitTo(Vector2 min, Vector2 max)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var size = Vector2.Max(max - min, new Vector2(10));
        _scale = Math.Min(ActualWidth / size.X, ActualHeight / size.Y) * 0.9;
        var middle = (min + max) / 2;
        _offset = new Vector(ActualWidth / 2 - middle.X * _scale, ActualHeight / 2 + middle.Y * _scale);
        InvalidateVisual();
    }

    private Point ToScreen(Vector2 p) => new(p.X * _scale + _offset.X, -p.Y * _scale + _offset.Y);

    private Vector2 ToMap(Point p) => new((float)((p.X - _offset.X) / _scale), (float)(-(p.Y - _offset.Y) / _scale));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawGrid(dc, dpi);
        if (_tree is null) return;

        // the world's ground, as its terrain covers it
        if (_world is { } w)
        {
            var pen = new Pen(TextBrush, 1.2) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, new Rect(ToScreen(new Vector2(w.Min.X, w.Max.Y)), ToScreen(new Vector2(w.Max.X, w.Min.Y))));
        }

        var outline = new Pen(LineBrush, 0.8);
        foreach (var cell in _cells)
        {
            if (cell.Area < 0 && !ShowAllLines) continue;
            Brush? fill = null;
            if (cell.Area >= 0)
            {
                var color = ColorOf(cell.Area);
                color.A = (byte)(cell.Area == SelectedArea ? 200 : SelectedArea >= 0 ? 70 : 140);
                fill = new SolidColorBrush(color);
                fill.Freeze();
            }
            dc.DrawGeometry(fill, cell.Area >= 0 || ShowAllLines ? outline : null, Polygon(cell.Outline));
        }

        // the split picked, across the whole map
        if (SelectedSplit >= 0 && SelectedSplit < _tree.Splits.Count)
        {
            var split = _tree.Splits[SelectedSplit];
            var normal = split.Normal;
            if (normal.LengthSquared() > 1e-9f)
            {
                var along = new Vector2(-normal.Y, normal.X);
                var foot = -normal * split.Offset / normal.LengthSquared();
                float far = 1e5f;
                // (the accent brush follows the theme, so it can't be frozen)
                var pen = new Pen(AccentBrush, 2.4);
                dc.DrawLine(pen, ToScreen(foot - along * far), ToScreen(foot + along * far));
                // an arrow towards its positive side
                var start = ToScreen(foot);
                var tip = ToScreen(foot + Vector2.Normalize(normal) * (float)(36 / _scale));
                dc.DrawLine(pen, start, tip);
                dc.DrawEllipse(AccentBrush, null, tip, 4, 4);
            }
        }

        // each area's pieces numbered, where there's room
        if (ShowNumbers)
            foreach (var cell in _cells.Where(c => c.Area >= 0))
            {
                var pieces = cell.Outline.Select(ToScreen).ToList();
                double width = pieces.Max(p => p.X) - pieces.Min(p => p.X), height = pieces.Max(p => p.Y) - pieces.Min(p => p.Y);
                if (width < 18 || height < 14) continue;
                var text = new FormattedText(cell.Area.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 12 /* loc-ignore */, Brushes.White, dpi);
                var at = ToScreen(cell.Centre);
                var box = new Rect(at.X - text.Width / 2 - 4, at.Y - text.Height / 2 - 1, text.Width + 8, text.Height + 2);
                var back = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
                back.Freeze();
                dc.DrawRoundedRectangle(back, null, box, 3, 3);
                dc.DrawText(text, new Point(box.X + 4, box.Y + 1));
            }

        if (_hover is { } h)
        {
            var text = new FormattedText($"X {h.X:0.0}   Y {h.Y:0.0}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 12, TextBrush, dpi);
            dc.DrawRoundedRectangle(BackBrush, null, new Rect(8, ActualHeight - text.Height - 14, text.Width + 12, text.Height + 6), 4, 4);
            dc.DrawText(text, new Point(14, ActualHeight - text.Height - 11));
        }
    }

    private StreamGeometry Polygon(IReadOnlyList<Vector2> points)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToScreen(points[0]), true, true);
            g.PolyLineTo(points.Skip(1).Select(ToScreen).ToList(), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Lines every so many metres (1, 2 or 5 times a power of ten, about 80 pixels apart), labelled, and the axes.</summary>
    private void DrawGrid(DrawingContext dc, double dpi)
    {
        double step = 80 / _scale, power = Math.Pow(10, Math.Floor(Math.Log10(step)));
        step = step / power < 2 ? 2 * power : step / power < 5 ? 5 * power : 10 * power;
        var min = ToMap(new Point(0, ActualHeight));
        var max = ToMap(new Point(ActualWidth, 0));
        var pen = new Pen(GridBrush, 1);
        var axis = new Pen(LineBrush, 1.2);
        for (double x = Math.Floor(min.X / step) * step; x <= max.X; x += step)
        {
            var at = ToScreen(new Vector2((float)x, 0)).X;
            dc.DrawLine(Math.Abs(x) < step / 2 ? axis : pen, new Point(at, 0), new Point(at, ActualHeight));
            Label(dc, dpi, x, new Point(at + 3, 2));
        }
        for (double y = Math.Floor(min.Y / step) * step; y <= max.Y; y += step)
        {
            var at = ToScreen(new Vector2(0, (float)y)).Y;
            dc.DrawLine(Math.Abs(y) < step / 2 ? axis : pen, new Point(0, at), new Point(ActualWidth, at));
            Label(dc, dpi, y, new Point(3, at + 1));
        }
    }

    private void Label(DrawingContext dc, double dpi, double value, Point at)
    {
        var text = new FormattedText(L.T("SimplexMap.Metres", Math.Round(value, 3)), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LineBrush, dpi); // loc-ignore
        dc.DrawText(text, at);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var at = e.GetPosition(this);
        double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        double scale = Math.Clamp(_scale * factor, 1e-4, 200);
        factor = scale / _scale;
        _offset = new Vector(at.X - (at.X - _offset.X) * factor, at.Y - (at.Y - _offset.Y) * factor);
        _scale = scale;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _lastMouse = e.GetPosition(this);
        _dragging = true;
        _moved = false;
        CaptureMouse();
        if (e.ClickCount == 2)
        {
            FitToView();
            _dragging = false;
            ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            var moved = at - _lastMouse;
            if (moved.Length > 2) _moved = true;
            if (_moved)
            {
                _offset += moved;
                _lastMouse = at;
            }
        }
        _hover = ToMap(at);
        PointerMoved?.Invoke(this, _hover);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        PointerMoved?.Invoke(this, null);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (!_moved) MapClicked?.Invoke(this, ToMap(e.GetPosition(this)));
    }
}
