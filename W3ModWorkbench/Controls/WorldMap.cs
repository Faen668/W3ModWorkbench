using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

/// <summary>A marker on a <see cref="WorldMap"/>: a pin or a waypoint, where, its colour, its name and, for an area, its radius in metres.</summary>
public sealed record MapMarker(Vector2 At, Color Color, string? Label, float Radius, bool Diamond);

/// <summary>A plant over the map: where its foot is, its turn (radians), how far it reaches (metres), and its picture from above.</summary>
public sealed record MapPlant(Vector2 At, float Yaw, float Reach, BitmapSource Picture);

/// <summary>
/// A shape drawn over a <see cref="WorldMap"/>: an outline (closed or not) in the world's metres, in a colour; a bold one
/// (the picked entity's) is drawn thicker and filled more strongly.
/// </summary>
public sealed record MapShape(IReadOnlyList<Vector2> Points, Color Color, bool Closed, bool Filled, bool Bold = false);

/// <summary>
/// A world from above, north up: its terrain as a picture (shaded relief, the colour map, or the textures painted),
/// the terrain's tiles and the occlusion grid if asked, shapes laid over it (simplex areas, navmeshes), and its layers'
/// entities as dots in their groups' colours. Drag to move, the wheel to zoom where the mouse is, click to pick the
/// nearest entity. Raises where the mouse is so the window can say what's there.
/// </summary>
public sealed class WorldMap : FrameworkElement
{
    private BitmapSource? _terrain;
    private Rect _terrainRect;
    // pictures laid over the terrain (navigation, foliage), by name, drawn in the order they were first set
    private readonly List<(string Key, BitmapSource Picture, Rect Area, double MaxScale)> _overlays = [];
    // plants drawn as their pictures once zoomed in this far (pixels a metre), trees then grass
    public const double TreeScale = 2.5, GrassScale = 6;
    private PlantCells _trees = new(null), _grass = new(null);
    private IReadOnlyList<MapPlant> _treeList = [], _grassList = [];
    private IReadOnlyList<MapMarker> _markers = [];
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
    public bool ShowTiles { get => _showTiles; set { _showTiles = value; Changed(); } }
    private bool _showTiles;

    /// <summary>Whether the occlusion grid's tiles are drawn.</summary>
    public bool ShowOcclusion { get => _showOcclusion; set { _showOcclusion = value; Changed(); } }
    private bool _showOcclusion;

    /// <summary>How big an entity's dot is, in pixels.</summary>
    public double DotSize { get => _dotSize; set { _dotSize = value; Changed(); } }
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
        _terrain = Shareable(picture);
        _terrainRect = new Rect(corner.X, corner.Y, size, size);
        Changed();
    }

    /// <summary>
    /// A picture laid over the terrain (navigation, foliage) under a name, covering a square of the world, or null to take
    /// it away; shown only until zoomed in past <paramref name="maxScale"/> pixels a metre, and under the other overlays
    /// when <paramref name="under"/>.
    /// </summary>
    public void SetOverlay(string key, BitmapSource? picture, Vector2 corner, float size, double maxScale = double.MaxValue, bool under = false)
    {
        picture = Shareable(picture);
        int at = _overlays.FindIndex(o => o.Key == key);
        if (picture is null)
        {
            if (at >= 0) _overlays.RemoveAt(at);
        }
        else if (at >= 0) _overlays[at] = (key, picture, new Rect(corner.X, corner.Y, size, size), maxScale);
        else _overlays.Insert(under ? 0 : _overlays.Count, (key, picture, new Rect(corner.X, corner.Y, size, size), maxScale));
        Changed();
    }

    /// <summary>The trees and grass drawn as their pictures from above once zoomed in; null for none.</summary>
    public void SetPlants(IReadOnlyList<MapPlant>? trees, IReadOnlyList<MapPlant>? grass)
    {
        _trees = new PlantCells(trees);
        _grass = new PlantCells(grass);
        (_treeList, _grassList) = (trees ?? [], grass ?? []);
        Changed();
    }

    /// <summary>What the map shows and where it's looking, for drawing it another way (the GPU map test).</summary>
    public record MapContent((BitmapSource Picture, Rect Area)? Terrain, List<(BitmapSource Picture, Rect Area)> Overlays,
        IReadOnlyList<MapPlant> Trees, IReadOnlyList<MapPlant> Grass, List<(Vector2 At, Color Colour)> Dots, IReadOnlyList<MapShape> Shapes,
        double Scale, Vector Offset);

    /// <summary>
    /// What the map shows: the terrain, the pictures over it but the foliage's (the plants themselves are given), the
    /// dots of the layers shown in their colours, the shapes, and the view.
    /// </summary>
    public MapContent Content() => new(
        _terrain is { } terrain ? (terrain, _terrainRect) : null,
        _overlays.Where(o => o.Key is not ("grass" or "trees")).Select(o => (o.Picture, o.Area)).ToList(),
        _treeList, _grassList,
        _dots.Select(d => (new Vector2(d.Entity.Position.X, d.Entity.Position.Y), ColorOf(d.Group))).ToList(),
        _shapes, _scale, _offset);

    /// <summary>A picture the painter's thread can use: frozen, or a frozen copy.</summary>
    private static BitmapSource? Shareable(BitmapSource? picture)
    {
        if (picture is null || picture.IsFrozen) return picture;
        if (!picture.CanFreeze) picture = new WriteableBitmap(picture);
        picture.Freeze();
        return picture;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public void SetMarkers(IReadOnlyList<MapMarker> markers)
    {
        _markers = markers;
        Changed();
    }

    public void SetTileGrid(int tiles, Vector2 corner, float size)
    {
        _tileGrid = (tiles, corner, size);
        Changed();
    }

    public void SetOcclusion(OcclusionGrid? grid)
    {
        _occlusion = grid;
        Changed();
    }

    public void SetDots(IReadOnlyList<MapDot> dots)
    {
        _dots = dots;
        Changed();
    }

    public void SetShapes(IReadOnlyList<MapShape> shapes)
    {
        _shapes = shapes;
        Changed();
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
        Changed();
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
        Moved();
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

    // ---- drawing ----
    // The map is painted on a thread of its own into a snapshot covering the view and a margin around it. Moving and
    // zooming only move and scale that one picture, which stays quick even where WPF draws without the graphics card
    // (over Remote Desktop); once the map settles, or is dragged past the margin, a sharp snapshot is painted again.

    /// <summary>How far past the view the snapshot reaches, as a share of the view's size on each side.</summary>
    private const double Margin = 0.3;

    private BitmapSource? _snapshot, _snapshotLines;
    private double _snapScale = 1;
    private Vector _snapOffset;
    private Size _snapSize;
    private int _version, _paintedVersion = -1;
    private bool _painting, _paintAgain;
    private DispatcherTimer? _settle;
    private static Dispatcher? _painter;

    /// <summary>How long the last snapshot took to paint, in milliseconds.</summary>
    public double LastPaintMilliseconds { get; private set; }

    /// <summary>What's shown has changed: paint it again straight away.</summary>
    private void Changed()
    {
        _version++;
        Paint();
    }

    /// <summary>The map moved or zoomed: show the snapshot moved, and paint a new one once it settles or runs out.</summary>
    private void Moved()
    {
        InvalidateVisual();
        _settle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) =>
        {
            _settle!.Stop();
            if (NeedsPaint()) Paint();
        }, Dispatcher);
        _settle.Stop();
        _settle.Start();
        if (!Covered()) Paint();
    }

    private bool NeedsPaint() => _paintedVersion != _version || _snapScale != _scale || !Covered(Margin / 2);

    /// <summary>Whether the snapshot covers the view, and this much of the margin past it.</summary>
    private bool Covered(double margin = 0)
    {
        if (_snapshot is null) return false;
        double k = _scale / _snapScale;
        var left = new Point(_offset.X - _snapOffset.X * k, _offset.Y - _snapOffset.Y * k);
        double mx = ActualWidth * margin, my = ActualHeight * margin;
        return left.X <= -mx && left.Y <= -my && left.X + _snapSize.Width * k >= ActualWidth + mx && left.Y + _snapSize.Height * k >= ActualHeight + my;
    }

    private void Paint()
    {
        if (_painting)
        {
            _paintAgain = true;
            return;
        }
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _painting = true;
        int version = _version;
        var scene = new Scene(this, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Painter.BeginInvoke(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (ground, lines) = scene.Paint();
            double took = clock.Elapsed.TotalMilliseconds;
            Dispatcher.BeginInvoke(() =>
            {
                _snapshot = ground;
                _snapshotLines = lines;
                _snapScale = scene.Scale;
                _snapOffset = scene.Offset;
                _snapSize = scene.Size;
                _paintedVersion = version;
                LastPaintMilliseconds = took;
                _painting = false;
                InvalidateVisual();
                if (_paintAgain || (!_dragging && NeedsPaint()) || !Covered())
                {
                    _paintAgain = false;
                    if (NeedsPaint()) Paint();
                }
            });
        });
    }

    /// <summary>The thread the map is painted on: one for every map, as painting is quick and one at a time.</summary>
    private static Dispatcher Painter
    {
        get
        {
            if (_painter is { } painter) return painter;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                _painter = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true, Name = "World map painter" }; // loc-ignore: a thread's name
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            return _painter!;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Moved();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackBrush, null, new Rect(RenderSize));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_snapshot is { } snapshot)
        {
            double k = _scale / _snapScale;
            // whole device pixels when not zoomed, so the picture stays sharp
            double x = _offset.X - _snapOffset.X * k, y = _offset.Y - _snapOffset.Y * k;
            if (k == 1) (x, y) = (Math.Round(x * dpi) / dpi, Math.Round(y * dpi) / dpi);
            RenderOptions.SetBitmapScalingMode(this, k == 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
            var area = new Rect(x, y, _snapSize.Width * k, _snapSize.Height * k);
            dc.DrawImage(snapshot, area);
            if (_snapshotLines is { } lines) dc.DrawImage(lines, area);
        }
        if (_picked is { } p)
        {
            var at = ToScreen(p.Entity.Position.X, p.Entity.Position.Y);
            dc.DrawEllipse(null, PickedPen, at, 8, 8);
            float facing = p.Entity.Yaw;
            dc.DrawLine(PickedPen, at, new Point(at.X - Math.Sin(facing) * 14, at.Y - Math.Cos(facing) * 14));
        }
        DrawScale(dc, dpi);
    }

    private static readonly Pen PickedPen = Frozen(new Pen(Brushes.White, 2));

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

    /// <summary>
    /// Everything the map shows, as it is when a snapshot is asked for, painted on the painter's thread: the terrain, the
    /// pictures over it, the plants, the grids, the shapes, the dots and the markers, for the view and the margin round it.
    /// </summary>
    private sealed class Scene
    {
        private readonly BitmapSource? _terrain;
        private readonly Rect _terrainRect;
        private readonly List<(string Key, BitmapSource Picture, Rect Area, double MaxScale)> _overlays;
        private readonly PlantCells _trees, _grass;
        private readonly IReadOnlyList<MapMarker> _markers;
        private readonly IReadOnlyList<MapDot> _dots;
        private readonly IReadOnlyList<MapShape> _shapes;
        private readonly (int Tiles, Vector2 Corner, float Size)? _tileGrid;
        private readonly OcclusionGrid? _occlusion;
        private readonly HashSet<LayerInfo>? _highlighted;
        private readonly double _dotSize, _dpi;

        public double Scale { get; }

        /// <summary>Where the world's origin is in the snapshot.</summary>
        public Vector Offset { get; }

        public Size Size { get; }

        public Scene(WorldMap map, double dpi)
        {
            _terrain = map._terrain;
            _terrainRect = map._terrainRect;
            _overlays = [.. map._overlays];
            _trees = map._trees;
            _grass = map._grass;
            _markers = map._markers;
            _dots = map._dots;
            _shapes = map._shapes;
            _tileGrid = map._tileGrid;
            _occlusion = map._showOcclusion ? map._occlusion : null;
            if (!map._showTiles) _tileGrid = null;
            _highlighted = map._highlighted;
            _dotSize = map._dotSize;
            _dpi = dpi;
            Scale = map._scale;
            double mx = Math.Ceiling(map.ActualWidth * Margin), my = Math.Ceiling(map.ActualHeight * Margin);
            Offset = map._offset + new Vector(mx, my);
            Size = new Size(map.ActualWidth + 2 * mx, map.ActualHeight + 2 * my);
        }

        private Point ToScreen(float x, float y) => new(x * Scale + Offset.X, -y * Scale + Offset.Y);

        private Vector2 ToMap(Point p) => new((float)((p.X - Offset.X) / Scale), (float)(-(p.Y - Offset.Y) / Scale));

        /// <summary>The snapshot: its ground (the terrain, the pictures over it and the plants) and the lines and dots over that.</summary>
        public (BitmapSource Ground, BitmapSource Lines) Paint()
        {
            int width = (int)Math.Ceiling(Size.Width * _dpi), height = (int)Math.Ceiling(Size.Height * _dpi);
            var canvas = new Canvas(width, height, _dpi);
            Rect Area(Rect world) => new(ToScreen((float)world.X, (float)world.Bottom), ToScreen((float)world.Right, (float)world.Y));
            // the terrain's pixels shown as squares once zoomed in, so what's under the mouse is clear
            if (_terrain is not null) canvas.Draw(_terrain, Area(_terrainRect), Scale <= 1.5);
            foreach (var (_, picture, area, maxScale) in _overlays)
                if (Scale <= maxScale) canvas.Draw(picture, Area(area), true);
            // a plant can reach past its own cell
            var from = ToMap(new Point(0, Size.Height)) - new Vector2(40);
            var to = ToMap(new Point(Size.Width, 0)) + new Vector2(40);
            foreach (var cells in new[] { Scale >= GrassScale ? _grass : null, Scale >= TreeScale ? _trees : null })
                if (cells is not null)
                    foreach (var (picture, world) in cells.Pictures(from, to, Scale))
                        canvas.Draw(picture, new Rect(world.X * Scale + Offset.X, world.Y * Scale + Offset.Y, world.Width * Scale, world.Height * Scale), true);
            var ground = canvas.Picture(_dpi);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) Draw(dc);
            var lines = new RenderTargetBitmap(width, height, 96 * _dpi, 96 * _dpi, PixelFormats.Pbgra32);
            lines.Render(visual);
            lines.Freeze();
            return (ground, lines);
        }

        /// <summary>The lines and dots: the grids, the shapes, the entities and the markers.</summary>
        private void Draw(DrawingContext dc)
        {
            if (_occlusion is { } grid)            {
                var pen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(90, 0xF2, 0x7A, 0x4A))), 1));
                float half = grid.TileSize / 2;
                foreach (var (middle, _) in grid.Tiles)
                    dc.DrawRectangle(null, pen, new Rect(ToScreen(middle.X - half, middle.Y + half), ToScreen(middle.X + half, middle.Y - half)));
            }
            if (_tileGrid is { } tg)
            {
                var pen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255))), 1));
                float step = tg.Size / tg.Tiles;
                for (int i = 0; i <= tg.Tiles; i++)
                {
                    dc.DrawLine(pen, ToScreen(tg.Corner.X + i * step, tg.Corner.Y), ToScreen(tg.Corner.X + i * step, tg.Corner.Y + tg.Size));
                    dc.DrawLine(pen, ToScreen(tg.Corner.X, tg.Corner.Y + i * step), ToScreen(tg.Corner.X + tg.Size, tg.Corner.Y + i * step));
                }
            }
            DrawShapes(dc);
            var view = new Rect(-10, -10, Size.Width + 20, Size.Height + 20);
            DrawDots(dc, view);
            DrawMarkers(dc, view);
        }

        /// <summary>The shapes, one geometry for each colour and kind, those off the snapshot left out.</summary>
        private void DrawShapes(DrawingContext dc)
        {
            foreach (var kind in _shapes.GroupBy(s => (s.Color, s.Filled, s.Bold)))
            {
                var geometry = new StreamGeometry();
                bool any = false;
                using (var g = geometry.Open())
                    foreach (var shape in kind)
                    {
                        if (shape.Points.Count < 2) continue;
                        g.BeginFigure(ToScreen(shape.Points[0].X, shape.Points[0].Y), shape.Filled, shape.Closed);
                        g.PolyLineTo(shape.Points.Skip(1).Select(p => ToScreen(p.X, p.Y)).ToList(), true, false);
                        any = true;
                    }
                if (!any) continue;
                geometry.Freeze();
                var (color, filled, bold) = kind.Key;
                var fill = filled ? Frozen(new SolidColorBrush(Color.FromArgb(bold ? (byte)120 : (byte)60, color.R, color.G, color.B))) : null;
                dc.DrawGeometry(fill, Frozen(new Pen(Frozen(new SolidColorBrush(color)), bold ? 2.6 : 1.2)), geometry);
            }
        }

        /// <summary>The dots, one geometry for each group's colour, those of the layers picked on top.</summary>
        private void DrawDots(DrawingContext dc, Rect view)
        {
            double r = _dotSize / 2;
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
                    dc.DrawGeometry(Frozen(new SolidColorBrush(color)), null, geometry);
                }
        }

        /// <summary>Each marker: a diamond for a pin, a dot for a waypoint, a ring for a quest's area, named once zoomed in enough.</summary>
        private void DrawMarkers(DrawingContext dc, Rect view)
        {
            if (_markers.Count == 0) return;
            bool named = Scale > 0.9;
            var back = Frozen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)));
            var outline = Frozen(new Pen(Brushes.Black, 1));
            foreach (var group in _markers.GroupBy(m => m.Color))
            {
                var brush = Frozen(new SolidColorBrush(group.Key));
                var ring = Frozen(new Pen(brush, 1.4));
                foreach (var m in group)
                {
                    var at = ToScreen(m.At.X, m.At.Y);
                    if (!view.Contains(at)) continue;
                    if (m.Radius > 0) dc.DrawEllipse(null, ring, at, m.Radius * Scale, m.Radius * Scale);
                    if (m.Diamond)
                    {
                        var g = new StreamGeometry();
                        using (var c = g.Open())
                        {
                            c.BeginFigure(new Point(at.X, at.Y - 6), true, true);
                            c.PolyLineTo([new Point(at.X + 5, at.Y), new Point(at.X, at.Y + 6), new Point(at.X - 5, at.Y)], true, false);
                        }
                        g.Freeze();
                        dc.DrawGeometry(brush, outline, g);
                    }
                    else dc.DrawEllipse(brush, null, at, 2, 2);
                    if (named && m.Label is { Length: > 0 } label)
                    {
                        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, _dpi); // loc-ignore: the typeface
                        dc.DrawRoundedRectangle(back, null, new Rect(at.X + 8, at.Y - text.Height / 2 - 1, text.Width + 8, text.Height + 2), 3, 3);
                        dc.DrawText(text, new Point(at.X + 12, at.Y - text.Height / 2));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Plants by 64 m cell, small ones first so big crowns cover them. Each cell is put together into one picture of its
    /// own at a resolution near the map's (2 to 16 pixels a metre), the cells wanted painted side by side on every core,
    /// and kept for when the map comes back to them; the snapshot then draws a few dozen pictures, not every plant.
    /// </summary>
    private sealed class PlantCells(IReadOnlyList<MapPlant>? plants)
    {
        private const double Cell = 64;
        private const int MaxPictures = 240;
        private readonly Dictionary<(int X, int Y, int Level), (BitmapSource Picture, Rect Area)?> _pictures = [];
        private readonly LinkedList<(int, int, int)> _recent = new();
        private readonly Dictionary<BitmapSource, Sprite> _sprites = [];

        public Dictionary<(int, int), List<MapPlant>> Cells { get; } = (plants ?? [])
            .GroupBy(p => ((int)Math.Floor(p.At.X / Cell), (int)Math.Floor(p.At.Y / Cell)))
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Reach).ToList());

        /// <summary>The pictures of the cells between two corners of the world, and where each goes, in metres with Y down.</summary>
        public List<(BitmapSource Picture, Rect Area)> Pictures(Vector2 from, Vector2 to, double scale)
        {
            int level = Math.Clamp((int)Math.Pow(2, Math.Ceiling(Math.Log2(scale))), 2, 16);
            var wanted = new List<(int, int, int)>();
            for (int cx = (int)Math.Floor(from.X / Cell); cx <= (int)Math.Floor(to.X / Cell); cx++)
                for (int cy = (int)Math.Floor(from.Y / Cell); cy <= (int)Math.Floor(to.Y / Cell); cy++)
                    if (Cells.ContainsKey((cx, cy))) wanted.Add((cx, cy, level));
            var missing = wanted.Where(k => !_pictures.ContainsKey(k)).ToList();
            if (missing.Count > 0)
            {
                foreach (var plant in missing.SelectMany(k => Cells[(k.Item1, k.Item2)]))
                    if (!_sprites.ContainsKey(plant.Picture)) _sprites[plant.Picture] = new Sprite(plant.Picture);
                var made = new (BitmapSource, Rect)?[missing.Count];
                Parallel.For(0, missing.Count, i => made[i] = Picture(Cells[(missing[i].Item1, missing[i].Item2)], missing[i].Item3));
                for (int i = 0; i < missing.Count; i++) _pictures[missing[i]] = made[i];
            }
            var pictures = new List<(BitmapSource, Rect)>();
            foreach (var key in wanted)
            {
                _recent.Remove(key);
                _recent.AddFirst(key);
                if (_pictures[key] is { } cell) pictures.Add(cell);
            }
            // the pictures least lately wanted let go, so a long look round doesn't keep every cell at every zoom
            while (_recent.Count > MaxPictures)
            {
                _pictures.Remove(_recent.Last!.Value);
                _recent.RemoveLast();
            }
            return pictures;
        }

        /// <summary>A cell's plants put together at so many pixels a metre: each sprite turned, sized and laid over the last.</summary>
        private (BitmapSource, Rect)? Picture(List<MapPlant> plants, int level)
        {
            float minX = plants.Min(p => p.At.X - p.Reach), maxX = plants.Max(p => p.At.X + p.Reach);
            float minY = plants.Min(p => p.At.Y - p.Reach), maxY = plants.Max(p => p.At.Y + p.Reach);
            int width = Math.Max(1, (int)Math.Ceiling((maxX - minX) * level)), height = Math.Max(1, (int)Math.Ceiling((maxY - minY) * level));
            var pixels = new byte[width * height * 4];
            foreach (var plant in plants)
            {
                var sprite = _sprites[plant.Picture];
                // the plant's foot in the picture (Y down), and how far it reaches in pixels
                float cx = (plant.At.X - minX) * level, cy = (maxY - plant.At.Y) * level, reach = plant.Reach * level;
                if (reach < 0.5f) continue;
                var (source, size) = sprite.Level(2 * reach);
                float cos = MathF.Cos(plant.Yaw), sin = MathF.Sin(plant.Yaw), toSprite = size / (2 * reach);
                int x0 = Math.Max(0, (int)(cx - reach)), x1 = Math.Min(width - 1, (int)(cx + reach));
                int y0 = Math.Max(0, (int)(cy - reach)), y1 = Math.Min(height - 1, (int)(cy + reach));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        // back into the sprite: the world's offset (Y up) turned back by the plant's turn
                        float wx = x + 0.5f - cx, wy = cy - (y + 0.5f);
                        float sx = cos * wx + sin * wy, sy = -sin * wx + cos * wy;
                        int u = (int)((sx + reach) * toSprite), v = (int)((reach - sy) * toSprite);
                        if (u < 0 || v < 0 || u >= size || v >= size) continue;
                        int s = (v * size + u) * 4;
                        int a = source[s + 3];
                        if (a == 0) continue;
                        int d = (y * width + x) * 4, keep = 255 - a;
                        // premultiplied, laid over what's there
                        pixels[d] = (byte)(source[s] + pixels[d] * keep / 255);
                        pixels[d + 1] = (byte)(source[s + 1] + pixels[d + 1] * keep / 255);
                        pixels[d + 2] = (byte)(source[s + 2] + pixels[d + 2] * keep / 255);
                        pixels[d + 3] = (byte)(a + pixels[d + 3] * keep / 255);
                    }
            }
            var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            picture.Freeze();
            return (picture, new Rect(minX, -maxY, maxX - minX, maxY - minY));
        }

        /// <summary>A plant's picture as premultiplied pixels, halved again and again, to take one near the size it's drawn.</summary>
        private sealed class Sprite
        {
            private readonly List<(byte[] Pixels, int Size)> _levels = [];

            public Sprite(BitmapSource picture)
            {
                var source = picture.Format == PixelFormats.Pbgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Pbgra32, null, 0);
                int size = Math.Min(source.PixelWidth, source.PixelHeight);
                var pixels = new byte[size * size * 4];
                source.CopyPixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
                _levels.Add((pixels, size));
                while (size >= 8)
                {
                    int half = size / 2;
                    var smaller = new byte[half * half * 4];
                    for (int y = 0; y < half; y++)
                        for (int x = 0; x < half; x++)
                            for (int c = 0; c < 4; c++)
                            {
                                int a = ((2 * y) * size + 2 * x) * 4 + c, b = a + size * 4;
                                smaller[(y * half + x) * 4 + c] = (byte)((pixels[a] + pixels[a + 4] + pixels[b] + pixels[b + 4]) / 4);
                            }
                    _levels.Add((smaller, half));
                    (pixels, size) = (smaller, half);
                }
            }

            /// <summary>The smallest level at least this many pixels across, or the biggest.</summary>
            public (byte[] Pixels, int Size) Level(float across)
            {
                for (int i = _levels.Count - 1; i >= 0; i--)
                    if (_levels[i].Size >= across) return _levels[i];
                return _levels[0];
            }
        }
    }

    /// <summary>
    /// A snapshot's pictures put together without WPF (whose drawing without a graphics card, as over Remote Desktop, is
    /// slow at scaling big pictures): each picture scaled into place from a copy of it halved to near the size it's
    /// shown, smoothed or not, laid over the last, its rows shared out among the cores.
    /// </summary>
    private sealed class Canvas(int width, int height, double pixelsPerUnit)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Pixels { get; } = new byte[width * height * 4];

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, Levels> Copies = [];

        /// <summary>A picture laid over the canvas, into a rectangle given in the snapshot's units.</summary>
        public void Draw(BitmapSource picture, Rect where, bool smooth)
        {
            var levels = Copies.GetValue(picture, p => new Levels(p));
            double left = where.X * pixelsPerUnit, top = where.Y * pixelsPerUnit, across = where.Width * pixelsPerUnit, down = where.Height * pixelsPerUnit;
            if (across < 1 || down < 1) return;
            int x0 = Math.Max(0, (int)Math.Floor(left)), x1 = Math.Min(Width, (int)Math.Ceiling(left + across));
            int y0 = Math.Max(0, (int)Math.Floor(top)), y1 = Math.Min(Height, (int)Math.Ceiling(top + down));
            if (x0 >= x1 || y0 >= y1) return;
            // the copy whose pixels are nearest the canvas's, never smaller
            var (source, sw, sh) = levels.Near(levels.Width / across);
            double sx = sw / across, sy = sh / down;
            var pixels = Pixels;
            int width = Width;
            Parallel.For(y0, y1, y =>
            {
                double v = (y + 0.5 - top) * sy - (smooth ? 0.5 : 0);
                for (int x = x0; x < x1; x++)
                {
                    double u = (x + 0.5 - left) * sx - (smooth ? 0.5 : 0);
                    int b, g, r, a;
                    if (smooth)
                    {
                        int iu = (int)Math.Floor(u), iv = (int)Math.Floor(v);
                        double fu = u - iu, fv = v - iv;
                        int u0 = Math.Clamp(iu, 0, sw - 1), u1 = Math.Clamp(iu + 1, 0, sw - 1), v0 = Math.Clamp(iv, 0, sh - 1), v1 = Math.Clamp(iv + 1, 0, sh - 1);
                        int p00 = (v0 * sw + u0) * 4, p10 = (v0 * sw + u1) * 4, p01 = (v1 * sw + u0) * 4, p11 = (v1 * sw + u1) * 4;
                        double w00 = (1 - fu) * (1 - fv), w10 = fu * (1 - fv), w01 = (1 - fu) * fv, w11 = fu * fv;
                        a = (int)(source[p00 + 3] * w00 + source[p10 + 3] * w10 + source[p01 + 3] * w01 + source[p11 + 3] * w11 + 0.5);
                        if (a == 0) continue;
                        b = (int)(source[p00] * w00 + source[p10] * w10 + source[p01] * w01 + source[p11] * w11 + 0.5);
                        g = (int)(source[p00 + 1] * w00 + source[p10 + 1] * w10 + source[p01 + 1] * w01 + source[p11 + 1] * w11 + 0.5);
                        r = (int)(source[p00 + 2] * w00 + source[p10 + 2] * w10 + source[p01 + 2] * w01 + source[p11 + 2] * w11 + 0.5);
                    }
                    else
                    {
                        int p = (Math.Clamp((int)v, 0, sh - 1) * sw + Math.Clamp((int)u, 0, sw - 1)) * 4;
                        a = source[p + 3];
                        if (a == 0) continue;
                        (b, g, r) = (source[p], source[p + 1], source[p + 2]);
                    }
                    int d = (y * width + x) * 4, keep = 255 - a;
                    // premultiplied, laid over what's there
                    pixels[d] = (byte)(b + pixels[d] * keep / 255);
                    pixels[d + 1] = (byte)(g + pixels[d + 1] * keep / 255);
                    pixels[d + 2] = (byte)(r + pixels[d + 2] * keep / 255);
                    pixels[d + 3] = (byte)(a + pixels[d + 3] * keep / 255);
                }
            });
        }

        public BitmapSource Picture(double dpi)
        {
            var picture = BitmapSource.Create(Width, Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, Pixels, Width * 4);
            picture.Freeze();
            return picture;
        }

        /// <summary>A picture's premultiplied pixels, and copies of it halved again and again, made when first wanted.</summary>
        private sealed class Levels
        {
            private readonly List<(byte[] Pixels, int Width, int Height)> _levels = [];

            public int Width => _levels[0].Width;

            public Levels(BitmapSource picture)
            {
                var source = picture.Format == PixelFormats.Pbgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Pbgra32, null, 0);
                var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
                source.CopyPixels(pixels, source.PixelWidth * 4, 0);
                _levels.Add((pixels, source.PixelWidth, source.PixelHeight));
            }

            /// <summary>The smallest copy with at least one pixel for each canvas pixel (<paramref name="shrink"/> source pixels to one).</summary>
            public (byte[] Pixels, int Width, int Height) Near(double shrink)
            {
                int level = shrink < 2 ? 0 : (int)Math.Floor(Math.Log2(shrink));
                lock (_levels)
                {
                    while (_levels.Count <= level && _levels[^1].Width >= 2 && _levels[^1].Height >= 2) _levels.Add(Half(_levels[^1]));
                    return _levels[Math.Min(level, _levels.Count - 1)];
                }
            }

            private static (byte[], int, int) Half((byte[] Pixels, int Width, int Height) from)
            {
                int w = from.Width / 2, h = from.Height / 2;
                var pixels = new byte[w * h * 4];
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int a = ((2 * y) * from.Width + 2 * x) * 4, b = a + from.Width * 4, d = (y * w + x) * 4;
                        for (int c = 0; c < 4; c++)
                            pixels[d + c] = (byte)((from.Pixels[a + c] + from.Pixels[a + 4 + c] + from.Pixels[b + c] + from.Pixels[b + 4 + c] + 2) / 4);
                    }
                });
                return (pixels, w, h);
            }
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var at = e.GetPosition(this);
        double scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.25 : 1 / 1.25), 0.01, 80);
        double factor = scale / _scale;
        _offset = new Vector(at.X - (at.X - _offset.X) * factor, at.Y - (at.Y - _offset.Y) * factor);
        _scale = scale;
        Moved();
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
                Moved();
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
