using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench;

/// <summary>
/// Shows a mesh in 3D, lit from where you look. Drag with the left button to turn around it, with the right or middle
/// button (or Shift and the left) to move it, and use the wheel to come closer; double-click fits it to the view.
/// Each chunk is drawn with its material's texture, or plain grey when there's none. The game is Z-up, so a mesh is
/// turned to stand up the way it does in the game. It can also show several meshes, each placed where it goes, the
/// way an entity puts its meshes together (<see cref="ShowParts"/>).
/// </summary>
public sealed class MeshView : Border
{
    private readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 45, NearPlaneDistance = 0.01, FarPlaneDistance = 10000 };
    private readonly DirectionalLight _headlight = new(Color.FromRgb(200, 200, 200), new Vector3D(0, 0, -1));
    private readonly Model3DGroup _models = new();
    private readonly Model3DGroup _wires = new();
    private readonly Dictionary<int, GeometryModel3D> _chunks = [];
    private readonly Dictionary<int, Material> _materials = [];
    private readonly HashSet<int> _hidden = [];
    private List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> _parts = [];
    private HashSet<int> _highlighted = [];
    private Func<int, MeshChunk, System.Numerics.Vector3[]?>? _posed;
    // each chunk's vertices in the pose shown, kept for the wireframe
    private readonly Dictionary<(int Part, MeshChunk Chunk), System.Numerics.Vector3[]> _posedPositions = [];
    private readonly System.Windows.Threading.DispatcherTimer _wireTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private DateTime _wiresBuilt;
    private MeshFile? _mesh => _parts.Count > 0 ? _parts[0].Mesh : null;
    private int _lod;
    private bool _wireframe;
    private bool _textured = true;
    private readonly Dictionary<int, BitmapSource?> _textures = [];

    // the camera turns around this point, at this distance, at these angles (degrees)
    private Point3D _target;
    private double _distance = 5, _yaw = 35, _pitch = 20;
    private Point _lastMouse, _downAt;
    private bool _orbiting, _panning;

    public MeshView()
    {
        Background = Brushes.Transparent; // takes the mouse everywhere, not just over the mesh
        _wireTimer.Tick += (_, _) => RebuildWires();
        ClipToBounds = true;
        Focusable = true;
        _viewport.Camera = _camera;
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(90, 90, 90)));
        scene.Children.Add(_headlight);
        scene.Children.Add(_models);
        scene.Children.Add(_wires);
        _viewport.Children.Add(new ModelVisual3D { Content = scene });
        Child = _viewport;
        RenderOptions.SetEdgeMode(_viewport, EdgeMode.Unspecified);
    }

    /// <summary>The mesh shown, or null for none.</summary>
    public MeshFile? Mesh => _mesh;

    /// <summary>The LOD shown: 0 is the most detailed.</summary>
    public int Lod
    {
        get => _lod;
        set { _lod = value; Rebuild(); }
    }

    /// <summary>Whether the triangles' edges are drawn over the mesh.</summary>
    public bool Wireframe
    {
        get => _wireframe;
        set { _wireframe = value; RebuildWires(); }
    }

    /// <summary>Whether textures are drawn, or every chunk in plain grey to see the shape.</summary>
    public bool Textured
    {
        get => _textured;
        set { _textured = value; _materials.Clear(); Rebuild(); }
    }

    /// <summary>The chunks a LOD draws: those in it whose render mask says they're seen (not just shadows).</summary>
    public static IEnumerable<MeshChunk> Drawn(MeshFile mesh, int lod)
    {
        var chunks = mesh.Chunks.Where(c => c.InLod(lod) && c.Indices.Length > 0).ToList();
        var seen = chunks.Where(c => (c.RenderMask & 1) != 0).ToList();
        return seen.Count > 0 ? seen : chunks.Where(c => c.RenderMask != 0);
    }

    public void Show(MeshFile? mesh) => ShowParts(mesh is null ? [] : [(mesh, System.Numerics.Matrix4x4.Identity)]);

    /// <summary>Shows meshes together, each moved, turned and scaled by its matrix (in the game's Z-up space).</summary>
    public void ShowParts(IReadOnlyList<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts)
    {
        _parts = [.. parts];
        _highlighted = [];
        _posed = null;
        _posedPositions.Clear();
        _lod = 0;
        _hidden.Clear();
        _leftOut.Clear();
        _textures.Clear();
        _materials.Clear();
        Rebuild();
        FitToView();
    }

    /// <summary>Draws every chunk again, after some were hidden.</summary>
    public void ShowAllChunks()
    {
        if (_hidden.Count == 0) return;
        _hidden.Clear();
        Rebuild();
    }

    public void SetChunkVisible(int chunk, bool visible)
    {
        if (visible ? _hidden.Remove(chunk) : _hidden.Add(chunk)) Rebuild();
    }

    /// <summary>Puts a picture on every chunk drawn with a material, or plain grey for null.</summary>
    public void SetTexture(int material, BitmapSource? texture) => SetTexture(0, material, texture);

    /// <summary>Puts a picture on the chunks of one of the meshes shown together that are drawn with a material.</summary>
    private readonly HashSet<int> _leftOut = [];

    /// <summary>
    /// Leaves out the chunks of one of a mesh's materials, such as the soft shadow drawn over the eyes, which can't be
    /// drawn as the game blends it and would cover what's under it.
    /// </summary>
    public void LeaveOut(int part, int material)
    {
        if (_leftOut.Add(Key(part, material))) Rebuild();
    }

    /// <summary>Whether a chunk is drawn: not hidden in the chunk list (the first mesh's), nor of a material left out.</summary>
    private bool Shown(int part, MeshChunk chunk) => (part > 0 || !_hidden.Contains(chunk.Index)) && !_leftOut.Contains(Key(part, chunk.Material));

    public void SetTexture(int part, int material, BitmapSource? texture)
    {
        int key = Key(part, material);
        _textures[key] = texture;
        _materials.Remove(key);
        foreach (var (index, model) in _chunks)
            if (index >> 16 == part && part < _parts.Count && _parts[part].Mesh.Chunks[index & 0xFFFF].Material == material)
                model.Material = model.BackMaterial = _highlighted.Contains(part) ? new MaterialGroup { Children = { MaterialFor(key), Glow } } : MaterialFor(key);
    }

    /// <summary>
    /// Marks some of the meshes shown together with a glow, to show where a part of an entity is; how many triangles
    /// they have at the LOD shown.
    /// </summary>
    public int Highlight(IEnumerable<int> parts)
    {
        _highlighted = [.. parts.Where(p => p >= 0 && p < _parts.Count)];
        Rebuild();
        return _highlighted.Sum(p => DrawnAt(_parts[p].Mesh).Sum(c => c.Triangles));
    }

    private static readonly Material Glow = Frozen(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(150, 0x4C, 0x9A, 0xFF))));

    private static Material Frozen(Material material)
    {
        material.Freeze();
        return material;
    }

    /// <summary>Raised when one of the meshes shown together is clicked (not dragged), with its number, or -1 for none.</summary>
    public event EventHandler<int>? PartClicked;

    /// <summary>
    /// Shows the meshes in a pose: <paramref name="posed"/> gives a chunk's vertices where they are in it (by its mesh's
    /// number), or null to leave it as it was made. Null shows them all as they were made again.
    /// </summary>
    public void Pose(Func<int, MeshChunk, System.Numerics.Vector3[]?>? posed)
    {
        _posed = posed;
        _posedPositions.Clear();
        foreach (var (key, model) in _chunks)
            model.Geometry = PosedGeometry(key >> 16, _parts[key >> 16].Mesh.Chunks[key & 0xFFFF]);
        if (!_wireframe) return;
        // the wireframe is slow to make, so while it plays it follows a few times a second, and then where it stops
        if (DateTime.UtcNow - _wiresBuilt > TimeSpan.FromMilliseconds(250)) RebuildWires();
        else
        {
            _wireTimer.Stop();
            _wireTimer.Start();
        }
    }

    /// <summary>A chunk's geometry, in the pose shown. Its normals are left for WPF to work out from the bent triangles.</summary>
    private MeshGeometry3D PosedGeometry(int part, MeshChunk chunk)
    {
        var made = Geometry(chunk);
        if (_posed?.Invoke(part, chunk) is not { } positions) return made;
        _posedPositions[(part, chunk)] = positions;
        var points = new Point3DCollection(positions.Length);
        foreach (var v in positions) points.Add(ToView(v));
        var geometry = new MeshGeometry3D { Positions = points, TextureCoordinates = made.TextureCoordinates, TriangleIndices = made.TriangleIndices };
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The most LODs any of the meshes shown has.</summary>
    public int LodCount => _parts.Count == 0 ? 1 : _parts.Max(p => p.Mesh.LodCount);

    /// <summary>How much is drawn at the LOD shown: triangles, vertices and chunks.</summary>
    public (int Triangles, int Vertices, int Chunks) Stats()
    {
        var chunks = _parts.SelectMany((part, p) => DrawnAt(part.Mesh).Where(c => Shown(p, c))).ToList();
        return (chunks.Sum(c => c.Triangles), chunks.Sum(c => c.Positions.Length), chunks.Count);
    }

    /// <summary>
    /// The mesh under a point of the view, or -1: a ray from the camera through the point, into the scene, and the
    /// nearest chunk it meets.
    /// </summary>
    private int PartAt(Point point)
    {
        var at = TranslatePoint(point, _viewport);
        double width = Math.Max(1, _viewport.ActualWidth), height = Math.Max(1, _viewport.ActualHeight);
        var look = _camera.LookDirection;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, _camera.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        // the camera's field of view is across; up and down follow from the view's shape
        double across = Math.Tan(_camera.FieldOfView * Math.PI / 360), down = across * height / width;
        var ray = look + right * ((at.X / width * 2 - 1) * across) + up * ((1 - at.Y / height * 2) * down);
        int found = -1;
        double nearest = double.MaxValue;
        VisualTreeHelper.HitTest(_viewport.Children[0], null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult { ModelHit: GeometryModel3D model } hit && hit.DistanceToRayOrigin < nearest
                && _chunks.FirstOrDefault(c => ReferenceEquals(c.Value, model)) is { Value: not null } chunk)
            {
                nearest = hit.DistanceToRayOrigin;
                found = chunk.Key >> 16;
            }
            return HitTestResultBehavior.Continue;
        }, new RayHitTestParameters(_camera.Position, ray));
        return found;
    }

    // chunks, materials and textures are kept by the mesh they're in and their number in it
    private static int Key(int part, int index) => part << 16 | index;

    /// <summary>The chunks of a mesh drawn at the LOD picked, or its least detailed when it has fewer.</summary>
    private IEnumerable<MeshChunk> DrawnAt(MeshFile mesh) => Drawn(mesh, Math.Min(_lod, Math.Max(0, mesh.LodCount - 1)));

    /// <summary>A placement in the game's Z-up space as one in the view's Y-up space.</summary>
    private static Matrix3D ToView(System.Numerics.Matrix4x4 m)
    {
        // the view's axes are the game's X, Z and -Y: S^-1 · M · S, with S taking (x, y, z) to (x, z, -y)
        var s = new Matrix3D(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var inverse = s;
        inverse.Invert();
        var game = new Matrix3D(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
        return inverse * game * s;
    }

    // ---------------------------------------------------------------- building the scene

    private void Rebuild()
    {
        _models.Children.Clear();
        _chunks.Clear();
        for (int p = 0; p < _parts.Count; p++)
        {
            var (mesh, place) = _parts[p];
            var transform = place.IsIdentity ? null : new MatrixTransform3D(ToView(place));
            foreach (var chunk in DrawnAt(mesh))
            {
                if (!Shown(p, chunk)) continue;
                Material material = MaterialFor(Key(p, chunk.Material));
                if (_highlighted.Contains(p)) material = new MaterialGroup { Children = { material, Glow } };
                // both sides drawn, as many of the game's cloth and foliage chunks are single sheets
                var model = new GeometryModel3D(PosedGeometry(p, chunk), material) { BackMaterial = material };
                if (transform is not null) model.Transform = transform;
                _chunks[Key(p, chunk.Index)] = model;
                _models.Children.Add(model);
            }
        }
        RebuildWires();
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshChunk, MeshGeometry3D> _geometries = [];

    /// <summary>A chunk as WPF wants it: Y up instead of Z, and triangles wound the other way round.</summary>
    private MeshGeometry3D Geometry(MeshChunk chunk) => _geometries.GetValue(chunk, c =>
    {
        var positions = new Point3DCollection(c.Positions.Length);
        var normals = new Vector3DCollection(c.Normals.Length);
        var uvs = new PointCollection(c.Uvs.Length);
        foreach (var p in c.Positions) positions.Add(ToView(p));
        foreach (var n in c.Normals) normals.Add(new Vector3D(n.X, n.Z, -n.Y));
        foreach (var t in c.Uvs) uvs.Add(new Point(t.X, t.Y));
        var indices = new Int32Collection(c.Indices.Length);
        for (int i = 0; i + 2 < c.Indices.Length; i += 3)
        {
            indices.Add(c.Indices[i]);
            indices.Add(c.Indices[i + 2]);
            indices.Add(c.Indices[i + 1]);
        }
        var geometry = new MeshGeometry3D { Positions = positions, Normals = normals, TextureCoordinates = uvs, TriangleIndices = indices };
        geometry.Freeze();
        return geometry;
    });

    private static Point3D ToView(System.Numerics.Vector3 p) => new(p.X, p.Z, -p.Y);

    private Material MaterialFor(int material)
    {
        if (_materials.TryGetValue(material, out var known)) return known;
        Brush brush;
        if (_textured && _textures.GetValueOrDefault(material) is { } picture)
        {
            // absolute units and tiling, so UVs past 1 repeat the texture as the game does instead of stretching it
            var image = new ImageBrush(picture)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
            brush = image;
        }
        else brush = new SolidColorBrush(Color.FromRgb(170, 170, 170));
        brush.Freeze();
        var result = new DiffuseMaterial(brush);
        result.Freeze();
        return _materials[material] = result;
    }

    /// <summary>
    /// The triangles' edges as thin crossed strips. WPF has no lines in 3D, so each edge is two narrow quads at right
    /// angles, which read as a line from any side.
    /// </summary>
    private void RebuildWires()
    {
        _wireTimer.Stop();
        _wiresBuilt = DateTime.UtcNow;
        _wires.Children.Clear();
        if (!_wireframe || _parts.Count == 0) return;
        var drawn = _parts.SelectMany((part, p) => DrawnAt(part.Mesh).Where(c => Shown(p, c))
            .Select(c => (Chunk: c, part.Place, Positions: _posedPositions.GetValueOrDefault((p, c)) ?? c.Positions))).ToList();
        if (drawn.Count == 0) return;
        double width = Math.Max(1e-4, Radius() * 0.0012);
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        foreach (var (chunk, place, at) in drawn)
        {
            var edges = new HashSet<long>();
            for (int i = 0; i + 2 < chunk.Indices.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = chunk.Indices[i + k], b = chunk.Indices[i + (k + 1) % 3];
                    if (a == b || !edges.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a)) continue;
                    AddEdge(positions, indices, ToView(System.Numerics.Vector3.Transform(at[a], place)), ToView(System.Numerics.Vector3.Transform(at[b], place)), width);
                }
        }
        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        geometry.Freeze();
        var brush = new SolidColorBrush(Color.FromRgb(225, 230, 235));
        brush.Freeze();
        var material = new EmissiveMaterial(brush);
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(Brushes.Black));
        group.Children.Add(material);
        group.Freeze();
        _wires.Children.Add(new GeometryModel3D(geometry, group) { BackMaterial = group });
    }

    private static void AddEdge(Point3DCollection positions, Int32Collection indices, Point3D a, Point3D b, double width)
    {
        var along = b - a;
        if (along.LengthSquared < 1e-12) return;
        var side = Vector3D.CrossProduct(along, Math.Abs(along.Y) < 0.9 * along.Length ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0));
        side.Normalize();
        var up = Vector3D.CrossProduct(along, side);
        up.Normalize();
        foreach (var offset in new[] { side * width, up * width })
        {
            int start = positions.Count;
            positions.Add(a - offset);
            positions.Add(a + offset);
            positions.Add(b + offset);
            positions.Add(b - offset);
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }
    }

    // ---------------------------------------------------------------- camera

    private (Point3D Min, Point3D Max) Bounds()
    {
        if (_parts.Count == 0) return (new Point3D(-1, -1, -1), new Point3D(1, 1, 1));
        var chunks = _parts.SelectMany((part, i) => DrawnAt(part.Mesh).Where(c => Shown(i, c) && c.Positions.Length > 0).Select(c => (Chunk: c, part.Place))).ToList();
        if (chunks.Count == 0) chunks = _parts.SelectMany(part => part.Mesh.Chunks.Where(c => c.Positions.Length > 0).Select(c => (Chunk: c, part.Place))).ToList();
        if (chunks.Count == 0) return (new Point3D(-1, -1, -1), new Point3D(1, 1, 1));
        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        foreach (var (chunk, place) in chunks)
            foreach (var p in chunk.Positions)
            {
                var at = place.IsIdentity ? p : System.Numerics.Vector3.Transform(p, place);
                min = System.Numerics.Vector3.Min(min, at);
                max = System.Numerics.Vector3.Max(max, at);
            }
        var a = ToView(min);
        var b = ToView(max);
        return (new Point3D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Point3D(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }

    private double Radius()
    {
        var (min, max) = Bounds();
        return Math.Max(0.01, (max - min).Length / 2);
    }

    /// <summary>Points the camera at the middle of what's shown, from far enough to see all of it.</summary>
    public void FitToView()
    {
        var (min, max) = Bounds();
        _target = min + (max - min) / 2;
        double radius = Math.Max(0.01, (max - min).Length / 2);
        _distance = radius / Math.Sin(_camera.FieldOfView * Math.PI / 360) * 1.1;
        _camera.NearPlaneDistance = radius * 0.01;
        _camera.FarPlaneDistance = radius * 200;
        UpdateCamera();
    }

    /// <summary>Looks from a side: the front (0), the right (90), the back (180), the left (270), or from above when <paramref name="top"/>.</summary>
    public void LookFrom(double yaw, bool top = false)
    {
        _yaw = yaw;
        _pitch = top ? 89 : 0;
        FitToView();
    }

    private void UpdateCamera()
    {
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        // the game's front is -Y, which is +Z here
        var direction = new Vector3D(-Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
        _camera.Position = _target - direction * _distance;
        _camera.LookDirection = direction;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _headlight.Direction = direction + new Vector3D(0.2, -0.3, 0);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            FitToView();
            return;
        }
        _lastMouse = _downAt = e.GetPosition(this);
        _panning = e.ChangedButton is MouseButton.Right or MouseButton.Middle || e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _orbiting = !_panning && e.ChangedButton == MouseButton.Left;
        if (_orbiting || _panning)
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_orbiting && !_panning) return;
        var now = e.GetPosition(this);
        var delta = now - _lastMouse;
        _lastMouse = now;
        if (_orbiting)
        {
            _yaw = (_yaw - delta.X * 0.4) % 360;
            _pitch = Math.Clamp(_pitch + delta.Y * 0.4, -89, 89);
        }
        else
        {
            // moves the point turned around, by as much as the mesh moves under the mouse
            double perPixel = 2 * _distance * Math.Tan(_camera.FieldOfView * Math.PI / 360) / Math.Max(1, ActualHeight);
            var right = Vector3D.CrossProduct(_camera.LookDirection, _camera.UpDirection);
            right.Normalize();
            var up = Vector3D.CrossProduct(right, _camera.LookDirection);
            up.Normalize();
            _target += (-right * delta.X + up * delta.Y) * perPixel;
        }
        UpdateCamera();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_orbiting && !_panning) return;
        bool clicked = _orbiting && (e.GetPosition(this) - _downAt).Length < 4;
        _orbiting = _panning = false;
        ReleaseMouseCapture();
        e.Handled = true; // a right-drag isn't a right-click
        if (clicked && PartClicked is not null) PartClicked(this, PartAt(e.GetPosition(this)));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1 / 0.85), Radius() * 0.02, Radius() * 100);
        UpdateCamera();
        e.Handled = true;
    }
}
