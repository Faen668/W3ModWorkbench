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
/// turned to stand up the way it does in the game.
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
    private MeshFile? _mesh;
    private int _lod;
    private bool _wireframe;
    private bool _textured = true;
    private readonly Dictionary<int, BitmapSource?> _textures = [];

    // the camera turns around this point, at this distance, at these angles (degrees)
    private Point3D _target;
    private double _distance = 5, _yaw = 35, _pitch = 20;
    private Point _lastMouse;
    private bool _orbiting, _panning;

    public MeshView()
    {
        Background = Brushes.Transparent; // takes the mouse everywhere, not just over the mesh
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

    public void Show(MeshFile? mesh)
    {
        _mesh = mesh;
        _lod = 0;
        _hidden.Clear();
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
    public void SetTexture(int material, BitmapSource? texture)
    {
        _textures[material] = texture;
        _materials.Remove(material);
        foreach (var (index, model) in _chunks)
            if (_mesh!.Chunks[index].Material == material) model.Material = model.BackMaterial = MaterialFor(material);
    }

    // ---------------------------------------------------------------- building the scene

    private void Rebuild()
    {
        _models.Children.Clear();
        _chunks.Clear();
        if (_mesh is not null)
            foreach (var chunk in Drawn(_mesh, _lod))
            {
                if (_hidden.Contains(chunk.Index)) continue;
                var material = MaterialFor(chunk.Material);
                // both sides drawn, as many of the game's cloth and foliage chunks are single sheets
                var model = new GeometryModel3D(Geometry(chunk), material) { BackMaterial = material };
                _chunks[chunk.Index] = model;
                _models.Children.Add(model);
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
        _wires.Children.Clear();
        if (!_wireframe || _mesh is null) return;
        var drawn = Drawn(_mesh, _lod).Where(c => !_hidden.Contains(c.Index)).ToList();
        if (drawn.Count == 0) return;
        double width = Math.Max(1e-4, Radius() * 0.0012);
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        foreach (var chunk in drawn)
        {
            var edges = new HashSet<long>();
            for (int i = 0; i + 2 < chunk.Indices.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = chunk.Indices[i + k], b = chunk.Indices[i + (k + 1) % 3];
                    if (a == b || !edges.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a)) continue;
                    AddEdge(positions, indices, ToView(chunk.Positions[a]), ToView(chunk.Positions[b]), width);
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
        if (_mesh is null) return (new Point3D(-1, -1, -1), new Point3D(1, 1, 1));
        var chunks = Drawn(_mesh, _lod).Where(c => !_hidden.Contains(c.Index) && c.Positions.Length > 0).ToList();
        if (chunks.Count == 0) chunks = _mesh.Chunks.Where(c => c.Positions.Length > 0).ToList();
        if (chunks.Count == 0) return (new Point3D(-1, -1, -1), new Point3D(1, 1, 1));
        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        foreach (var chunk in chunks)
            foreach (var p in chunk.Positions)
            {
                min = System.Numerics.Vector3.Min(min, p);
                max = System.Numerics.Vector3.Max(max, p);
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
        _lastMouse = e.GetPosition(this);
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
        _orbiting = _panning = false;
        ReleaseMouseCapture();
        e.Handled = true; // a right-drag isn't a right-click
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1 / 0.85), Radius() * 0.02, Radius() * 100);
        UpdateCamera();
        e.Handled = true;
    }
}
