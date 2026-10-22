using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.Localisation;
using Vector2 = System.Numerics.Vector2;

namespace W3ModWorkbench;

/// <summary>A dot on a <see cref="WorldMap"/>: an entity of a layer, in its group's colour.</summary>
public sealed record MapDot(LayerEntity Entity, LayerInfo Layer, int Group)
{
    /// <summary>The entity's name, or for one without, its template's.</summary>
    public string Title => Entity.Name.Length > 0 ? Entity.Name : Entity.Template is { } t ? System.IO.Path.GetFileNameWithoutExtension(t) : "";
}

/// <summary>A shape drawn over a <see cref="WorldMap"/>: an outline (closed or not) in the world's metres, in a colour.</summary>
public sealed record MapShape(IReadOnlyList<Vector2> Points, Color Color, bool Closed, bool Filled);

/// <summary>
/// A world from above, north up: its terrain as a picture (shaded relief, the colour map, or the textures painted),
/// the terrain's tiles and the occlusion grid if asked, shapes laid over it (simplex areas, navmeshes), and its layers'
/// entities as dots in their groups' colours. Drag to move, the wheel to zoom where the mouse is, click to pick the
/// nearest entity. Raises where the mouse is so the window can say what's there.
/// </summary>
public sealed class WorldMap : FrameworkElement
{
    private BitmapSource? _terrain, _overlay;
    private Rect _terrainRect, _overlayRect;
    private IReadOnlyList<MapDot> _dots = [];
    private IReadOnlyList<MapShape> _shapes = [];
    private (int Tiles, Vector2 Corner, float Size)? _tileGrid;
    private OcclusionGrid? _occlusion;
    private MapDot? _picked;
    private HashSet<LayerInfo>? _highlighted;
    private double _scale = 0.3;
    private Vector _offset;
    private Point _lastMouse;
    private bool _dragging, _moved;
    private Vector2? _hover;

    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty LineBrushProperty = Brush(nameof(LineBrush));
    public static readonly DependencyProperty BackBrushProperty = Brush(nameof(BackBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(WorldMap), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush BackBrush { get => (Brush)GetValue(BackBrushProperty); set => SetValue(BackBrushProperty, value); }

    /// <summary>Whether the terrain tiles' edges are drawn.</summary>
    public bool ShowTiles { get => _showTiles; set { _showTiles = value; InvalidateVisual(); } }
    private bool _showTiles;

    /// <summary>Whether the occlusion grid's tiles are drawn.</summary>
    public bool ShowOcclusion { get => _showOcclusion; set { _showOcclusion = value; InvalidateVisual(); } }
    private bool _showOcclusion;

    /// <summary>How big an entity's dot is, in pixels.</summary>
    public double DotSize { get => _dotSize; set { _dotSize = value; InvalidateVisual(); } }
    private double _dotSize = 3;

    /// <summary>Raised as the mouse moves over the map, with where it is in metres, or null when it leaves.</summary>
    public event EventHandler<Vector2?>? PointerMoved;

    /// <summary>Raised when the map is clicked, with the dot nearest the click (within a few pixels), or null.</summary>
    public event EventHandler<MapDot?>? DotPicked;

    public WorldMap()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(BackBrushProperty, "SolidBackgroundFillColorBaseBrush");
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>The terrain's picture and the part of the world it covers (its corner, its size), its rows from the north.</summary>
    public void SetTerrain(BitmapSource? picture, Vector2 corner, float size)
    {
        _terrain = picture;
        _terrainRect = new Rect(corner.X, corner.Y, size, size);
        InvalidateVisual();
    }

    /// <summary>A picture laid over the terrain (navigation), covering the same kind of square, or null for none.</summary>
    public void SetOverlay(BitmapSource? picture, Vector2 corner, float size)
    {
        _overlay = picture;
        _overlayRect = new Rect(corner.X, corner.Y, size, size);
        InvalidateVisual();
    }

    public void SetTileGrid(int tiles, Vector2 corner, float size)
    {
        _tileGrid = (tiles, corner, size);
        InvalidateVisual();
    }

    public void SetOcclusion(OcclusionGrid? grid)
    {
        _occlusion = grid;
        InvalidateVisual();
    }

    public void SetDots(IReadOnlyList<MapDot> dots)
    {
        _dots = dots;
        InvalidateVisual();
    }

    public void SetShapes(IReadOnlyList<MapShape> shapes)
    {
        _shapes = shapes;
        InvalidateVisual();
    }

    /// <summary>The dot picked, ringed, or null.</summary>
    public MapDot? Picked
    {
        get => _picked;
        set { _picked = value; InvalidateVisual(); }
    }

    /// <summary>The layers whose dots stand out (the rest dimmed), or null for none.</summary>
    public void Highlight(HashSet<LayerInfo>? layers)
    {
        _highlighted = layers;
        InvalidateVisual();
    }

    /// <summary>A colour for a group of layers, the same each time.</summary>
    public static Color ColorOf(int group) => SimplexMap.ColorOf(group + 3);

    public void FitTo(Vector2 min, Vector2 max)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var size = Vector2.Max(max - min, new Vector2(10));
        _scale = Math.Min(ActualWidth / size.X, ActualHeight / size.Y) * 0.92;
        var middle = (min + max) / 2;
        _offset = new Vector(ActualWidth / 2 - middle.X * _scale, ActualHeight / 2 + middle.Y * _scale);
        InvalidateVisual();
    }

    public void FitWorld()
    {
        if (_terrain is not null) FitTo(new Vector2((float)_terrainRect.X, (float)_terrainRect.Y), new Vector2((float)_terrainRect.Right, (float)_terrainRect.Bottom));
        else if (_dots.Count > 0) FitToDots(_dots);
    }

    public void FitToDots(IEnumerable<MapDot> dots)
    {
        var points = dots.Select(d => new Vector2(d.Entity.Position.X, d.Entity.Position.Y)).ToList();
        if (points.Count == 0) return;
        var min = points.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
        var max = points.Aggregate(new Vector2(float.MinValue), Vector2.Max);
        FitTo(min - new Vector2(20), max + new Vector2(20));
    }

    private Point ToScreen(float x, float y) => new(x * _scale + _offset.X, -y * _scale + _offset.Y);

    private Vector2 ToMap(Point p) => new((float)((p.X - _offset.X) / _scale), (float)(-(p.Y - _offset.Y) / _scale));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackBrush, null, new Rect(RenderSize));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_terrain is not null)
        {
            var a = ToScreen((float)_terrainRect.X, (float)_terrainRect.Bottom);
            var b = ToScreen((float)_terrainRect.Right, (float)_terrainRect.Y);
            RenderOptions.SetBitmapScalingMode(this, _scale > 1.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
            dc.DrawImage(_terrain, new Rect(a, b));
        }
        if (_overlay is not null)
            dc.DrawImage(_overlay, new Rect(ToScreen((float)_overlayRect.X, (float)_overlayRect.Bottom), ToScreen((float)_overlayRect.Right, (float)_overlayRect.Y)));
        if (_showOcclusion && _occlusion is { } grid)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 0xF2, 0x7A, 0x4A)), 1);
            pen.Freeze();
            float half = grid.TileSize / 2;
            foreach (var (middle, _) in grid.Tiles)
                dc.DrawRectangle(null, pen, new Rect(ToScreen(middle.X - half, middle.Y + half), ToScreen(middle.X + half, middle.Y - half)));
        }
        if (_showTiles && _tileGrid is { } tg)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
            pen.Freeze();
            float step = tg.Size / tg.Tiles;
            for (int i = 0; i <= tg.Tiles; i++)
            {
                dc.DrawLine(pen, ToScreen(tg.Corner.X + i * step, tg.Corner.Y), ToScreen(tg.Corner.X + i * step, tg.Corner.Y + tg.Size));
                dc.DrawLine(pen, ToScreen(tg.Corner.X, tg.Corner.Y + i * step), ToScreen(tg.Corner.X + tg.Size, tg.Corner.Y + i * step));
            }
        }
        foreach (var shape in _shapes)
        {
            if (shape.Points.Count < 2) continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(ToScreen(shape.Points[0].X, shape.Points[0].Y), shape.Filled, shape.Closed);
                g.PolyLineTo(shape.Points.Skip(1).Select(p => ToScreen(p.X, p.Y)).ToList(), true, false);
            }
            geometry.Freeze();
            var fill = shape.Filled ? new SolidColorBrush(Color.FromArgb(60, shape.Color.R, shape.Color.G, shape.Color.B)) : null;
            fill?.Freeze();
            var pen = new Pen(new SolidColorBrush(shape.Color), 1.2);
            pen.Freeze();
            dc.DrawGeometry(fill, pen, geometry);
        }

        // the dots, one geometry for each group's colour, those of the layers picked on top
        double r = _dotSize / 2;
        var view = new Rect(-10, -10, ActualWidth + 20, ActualHeight + 20);
        // with layers picked, the rest first and dimmed, then the picked ones over them
        var passes = _highlighted is null ? new[] { (Lit: true, All: true) } : new[] { (Lit: false, All: false), (Lit: true, All: false) };
        foreach (var (lit, all) in passes)
            foreach (var group in _dots.GroupBy(d => d.Group))
            {
                var geometry = new StreamGeometry();
                bool any = false;
                using (var g = geometry.Open())
                    foreach (var dot in group)
                    {
                        if (!all && _highlighted!.Contains(dot.Layer) != lit) continue;
                        var at = ToScreen(dot.Entity.Position.X, dot.Entity.Position.Y);
                        if (!view.Contains(at)) continue;
                        g.BeginFigure(new Point(at.X - r, at.Y - r), true, true);
                        g.PolyLineTo([new Point(at.X + r, at.Y - r), new Point(at.X + r, at.Y + r), new Point(at.X - r, at.Y + r)], false, false);
                        any = true;
                    }
                if (!any) continue;
                geometry.Freeze();
                var color = ColorOf(group.Key);
                if (!lit) color = Color.FromArgb(70, color.R, color.G, color.B);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dc.DrawGeometry(brush, null, geometry);
            }
        if (_picked is { } p)
        {
            var at = ToScreen(p.Entity.Position.X, p.Entity.Position.Y);
            var ring = new Pen(Brushes.White, 2);
            ring.Freeze();
            dc.DrawEllipse(null, ring, at, 8, 8);
            float facing = p.Entity.Yaw;
            dc.DrawLine(ring, at, new Point(at.X - Math.Sin(facing) * 14, at.Y - Math.Cos(facing) * 14));
        }

        DrawScale(dc, dpi);
    }

    /// <summary>A bar a round number of metres long, bottom right.</summary>
    private void DrawScale(DrawingContext dc, double dpi)
    {
        double metres = 120 / _scale, power = Math.Pow(10, Math.Floor(Math.Log10(metres)));
        metres = metres / power < 2 ? power : metres / power < 5 ? 2 * power : 5 * power;
        double width = metres * _scale;
        var text = new FormattedText(L.T("SimplexMap.Metres", metres), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, TextBrush, dpi); // loc-ignore: the typeface
        double x = ActualWidth - width - 16, y = ActualHeight - 18;
        dc.DrawRoundedRectangle(BackBrush, null, new Rect(x - 8, y - text.Height - 8, width + 16, text.Height + 16), 4, 4);
        var pen = new Pen(TextBrush, 2);
        dc.DrawLine(pen, new Point(x, y), new Point(x + width, y));
        dc.DrawText(text, new Point(x + width / 2 - text.Width / 2, y - text.Height - 3));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var at = e.GetPosition(this);
        double scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.25 : 1 / 1.25), 0.01, 80);
        double factor = scale / _scale;
        _offset = new Vector(at.X - (at.X - _offset.X) * factor, at.Y - (at.Y - _offset.Y) * factor);
        _scale = scale;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _lastMouse = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            FitWorld();
            return;
        }
        _dragging = true;
        _moved = false;
        CaptureMouse();
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
                InvalidateVisual();
            }
        }
        _hover = ToMap(at);
        PointerMoved?.Invoke(this, _hover);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        PointerMoved?.Invoke(this, null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_moved) return;
        var at = e.GetPosition(this);
        MapDot? best = null;
        double nearest = 9 * 9;
        foreach (var dot in _dots)
        {
            if (_highlighted is not null && !_highlighted.Contains(dot.Layer)) continue;
            var p = ToScreen(dot.Entity.Position.X, dot.Entity.Position.Y);
            double d = (p - at).LengthSquared;
            if (d < nearest) (best, nearest) = (dot, d);
        }
        _picked = best;
        DotPicked?.Invoke(this, best);
        InvalidateVisual();
    }
}
