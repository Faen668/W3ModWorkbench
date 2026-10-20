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
    private readonly Model3DGroup _lights = new();
    private readonly Model3DGroup _models = new();
    private readonly Model3DGroup _wires = new();
    private readonly Model3DGroup _floor = new();
    private bool _showFloor;
    // what's drawn over the meshes to see how they're made: their box, normals, bones and collision
    private readonly Model3DGroup _debug = new();
    private bool _showBounds, _showNormals, _showBones, _vertexColors;
    private IReadOnlyList<(System.Numerics.Vector3[] Vertices, int[] Triangles)> _collision = [];
    private readonly Dictionary<int, GeometryModel3D> _chunks = [];
    private readonly Dictionary<int, Material> _materials = [];
    private readonly HashSet<int> _hidden = [];
    private List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> _parts = [];
    private HashSet<int> _highlighted = [];
    private Func<int, MeshChunk, System.Numerics.Vector3[]?>? _posed;
    // each chunk's vertices in the pose shown, kept for the wireframe
    private readonly Dictionary<(int Part, MeshChunk Chunk), System.Numerics.Vector3[]> _posedPositions = [];
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
        ClipToBounds = true;
        Focusable = true;
        _viewport.Camera = _camera;
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(90, 90, 90)));
        scene.Children.Add(_headlight);
        scene.Children.Add(_lights);
        scene.Children.Add(_floor);
        scene.Children.Add(_models);
        scene.Children.Add(_wires);
        scene.Children.Add(_debug);
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

    /// <summary>Whether a grid is drawn on the ground under the meshes, so what's shown stands on something.</summary>
    public bool ShowFloor
    {
        get => _showFloor;
        set { _showFloor = value; RebuildFloor(); }
    }

    /// <summary>Whether the box the meshes fit in is drawn.</summary>
    public bool ShowBounds { get => _showBounds; set { _showBounds = value; RebuildDebug(); } }

    /// <summary>Whether each vertex's normal is drawn, as a short line out of the surface.</summary>
    public bool ShowNormals { get => _showNormals; set { _showNormals = value; RebuildDebug(); } }

    /// <summary>Whether a skinned mesh's bones are drawn where they were bound, as small crosses.</summary>
    public bool ShowBones { get => _showBones; set { _showBones = value; RebuildDebug(); } }

    /// <summary>Whether the meshes are shown in their vertex colours (each triangle in its corners' average) instead of their textures.</summary>
    public bool VertexColors { get => _vertexColors; set { _vertexColors = value; Rebuild(); } }

    /// <summary>Collision shapes to draw over the meshes, each its vertices and triangles in the mesh's space; none to draw none.</summary>
    public void ShowCollision(IReadOnlyList<(System.Numerics.Vector3[] Vertices, int[] Triangles)> shapes)
    {
        _collision = shapes;
        RebuildDebug();
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
        _poseVersion++;
        _parts = [.. parts];
        _wireWidth = 0;
        _highlighted = [];
        _posed = null;
        _posedPositions.Clear();
        _lod = 0;
        _hidden.Clear();
        _leftOut.Clear();
        _textures.Clear();
        _materials.Clear();
        _sceneSphere = null;
        Rebuild();
        FitToView();
    }

    /// <summary>
    /// Shows meshes with their textures already read: each (part, material) drawn with its picture or left out. Built
    /// once with all of them, rather than again for every texture put on after.
    /// </summary>
    public void ShowParts(IReadOnlyList<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts,
        IEnumerable<(int Part, int Material, BitmapSource? Picture, bool LeaveOut)> textures)
    {
        _poseVersion++;
        _parts = [.. parts];
        _wireWidth = 0;
        _highlighted = [];
        _posed = null;
        _posedPositions.Clear();
        _lod = 0;
        _hidden.Clear();
        _leftOut.Clear();
        _textures.Clear();
        _materials.Clear();
        _sceneSphere = null;
        foreach (var (part, material, picture, leaveOut) in textures)
        {
            if (leaveOut) _leftOut.Add(Key(part, material));
            else if (picture is not null) _textures[Key(part, material)] = picture;
        }
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
        // a see-through texture (hair cut out by its alpha) is drawn after the solid ones, so what's behind it shows through
        bool seeThrough = texture?.Format == PixelFormats.Pbgra32 || texture?.Format == PixelFormats.Bgra32;
        foreach (var (index, model) in _chunks)
            if (index >> 16 == part && part < _parts.Count && _parts[part].Mesh.Chunks[index & 0xFFFF].Material == material)
            {
                model.Material = model.BackMaterial = _highlighted.Contains(part) ? new MaterialGroup { Children = { MaterialFor(key), Glow } } : MaterialFor(key);
                if (seeThrough && _models.Children.Remove(model)) _models.Children.Add(model);
            }
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
        _poseVersion++;
        _posed = posed;
        _posedPositions.Clear();
        _sceneMoved = true;
        foreach (var (key, model) in _chunks)
            model.Geometry = PosedGeometry(key >> 16, _parts[key >> 16].Mesh.Chunks[key & 0xFFFF]);
        if (_wireframe) RebuildWires();
    }

    // bumped by each pose and each new set of meshes, so a pose worked out in the background for an older one is dropped
    private int _poseVersion;

    /// <summary>
    /// Poses the meshes as <see cref="Pose"/> does, but works it out on the thread pool, the chunks in parallel: the
    /// pose itself (<paramref name="makePose"/> is called there, so the bones are placed off the UI thread too), each
    /// chunk's bent vertices and their normals, as frozen geometry the UI thread only has to put in. A big cast (a
    /// battle's dozen actors) would otherwise keep the UI thread so busy it couldn't answer a click.
    /// </summary>
    public async Task PoseAsync(Func<Func<int, MeshChunk, System.Numerics.Vector3[]?>?> makePose)
    {
        int version = ++_poseVersion;
        var parts = _parts;
        var keys = _chunks.Keys.ToList();
        var made = await Task.Run(() =>
        {
            var posed = makePose();
            var results = new (int Key, MeshGeometry3D Geometry, System.Numerics.Vector3[]? Positions)[keys.Count];
            Parallel.For(0, keys.Count, i =>
            {
                int key = keys[i];
                var chunk = parts[key >> 16].Mesh.Chunks[key & 0xFFFF];
                var positions = posed?.Invoke(key >> 16, chunk);
                results[i] = (key, positions is null ? Geometry(chunk) : Bent(chunk, positions), positions);
            });
            return (Posed: posed, Results: results);
        });
        if (version != _poseVersion || !ReferenceEquals(parts, _parts)) return;
        _posed = made.Posed;
        _posedPositions.Clear();
        _sceneMoved = true;
        foreach (var (key, geometry, positions) in made.Results)
        {
            if (positions is not null) _posedPositions[(key >> 16, parts[key >> 16].Mesh.Chunks[key & 0xFFFF])] = positions;
            if (_chunks.TryGetValue(key, out var model)) model.Geometry = geometry;
        }
        if (_wireframe) RebuildWires();
    }

    /// <summary>
    /// A chunk bent into a pose, with normals worked out from its bent triangles (each vertex's the sum of its faces',
    /// by their size), frozen so it can be made off the UI thread.
    /// </summary>
    private MeshGeometry3D Bent(MeshChunk chunk, System.Numerics.Vector3[] positions)
    {
        var made = Geometry(chunk);
        var points = new Point3DCollection(positions.Length);
        foreach (var v in positions) points.Add(ToView(v));
        var sums = new System.Numerics.Vector3[positions.Length];
        var indices = chunk.Indices;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            // facing out as the mesh's own normals do (checked against them: the same on all but a few vertices)
            int a = indices[i], b = indices[i + 2], c = indices[i + 1];
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
            var face = System.Numerics.Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            sums[a] += face;
            sums[b] += face;
            sums[c] += face;
        }
        var normals = new Vector3DCollection(positions.Length);
        foreach (var n in sums)
        {
            var unit = n.LengthSquared() > 1e-20f ? System.Numerics.Vector3.Normalize(n) : System.Numerics.Vector3.UnitZ;
            normals.Add(new Vector3D(unit.X, unit.Z, -unit.Y));
        }
        var geometry = new MeshGeometry3D { Positions = points, Normals = normals, TextureCoordinates = made.TextureCoordinates, TriangleIndices = made.TriangleIndices };
        geometry.Freeze();
        return geometry;
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
    /// <summary>How big the meshes shown are, in metres: across, deep and high, as they were made.</summary>
    public System.Numerics.Vector3 Size()
    {
        var (min, max) = Bounds();
        return new System.Numerics.Vector3((float)(max.X - min.X), (float)(max.Z - min.Z), (float)(max.Y - min.Y));
    }

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
        var seeThrough = new List<GeometryModel3D>();
        for (int p = 0; p < _parts.Count; p++)
        {
            var (mesh, place) = _parts[p];
            var transform = place.IsIdentity ? null : new MatrixTransform3D(ToView(place));
            foreach (var chunk in DrawnAt(mesh))
            {
                if (!Shown(p, chunk)) continue;
                Material material = MaterialFor(Key(p, chunk.Material));
                if (_highlighted.Contains(p)) material = new MaterialGroup { Children = { material, Glow } };
                var geometry = PosedGeometry(p, chunk);
                if (_vertexColors) (geometry, material) = ColoredGeometry(chunk);
                // both sides drawn, as many of the game's cloth and foliage chunks are single sheets
                var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
                if (transform is not null) model.Transform = transform;
                _chunks[Key(p, chunk.Index)] = model;
                if (_textured && _textures.GetValueOrDefault(Key(p, chunk.Material)) is { } texture
                    && (texture.Format == PixelFormats.Pbgra32 || texture.Format == PixelFormats.Bgra32)) seeThrough.Add(model);
                else _models.Children.Add(model);
            }
        }
        // see-through ones after the solid ones, so what's behind them shows through
        foreach (var model in seeThrough) _models.Children.Add(model);
        RebuildWires();
        RebuildFloor();
        RebuildDebug();
    }

    /// <summary>
    /// The debug drawing over the meshes: the box they fit in (blue), each vertex's normal (green), the bones of a
    /// skinned mesh where they were bound (yellow crosses), and the collision shapes' edges (orange).
    /// </summary>
    private void RebuildDebug()
    {
        _debug.Children.Clear();
        if (_parts.Count == 0) return;
        double radius = Radius();
        double width = Math.Max(1e-4, radius * 0.0015);
        if (_showBounds)
        {
            var (a, b) = Bounds();
            var c = new Point3D[8];
            for (int i = 0; i < 8; i++) c[i] = new Point3D((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
            var edges = new List<(Point3D, Point3D)>();
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) edges.Add((c[i], c[i | bit]));
            AddLines(edges, Color.FromRgb(0x4C, 0x9A, 0xFF), width);
        }
        var drawn = _parts.SelectMany((part, p) => DrawnAt(part.Mesh).Where(c => Shown(p, c)).Select(c => (Part: p, Chunk: c, part.Place))).ToList();
        if (_showNormals)
        {
            int total = drawn.Sum(d => d.Chunk.Positions.Length);
            int every = Math.Max(1, total / 30000); // a big mesh has some of them drawn, so the view stays quick
            double length = radius * 0.04;
            var lines = new List<(Point3D, Point3D)>();
            foreach (var (part, chunk, place) in drawn)
            {
                var at = _posedPositions.GetValueOrDefault((part, chunk)) ?? chunk.Positions;
                for (int v = 0; v < at.Length && v < chunk.Normals.Length; v += every)
                {
                    var from = place.IsIdentity ? at[v] : System.Numerics.Vector3.Transform(at[v], place);
                    var normal = place.IsIdentity ? chunk.Normals[v] : System.Numerics.Vector3.TransformNormal(chunk.Normals[v], place);
                    lines.Add((ToView(from), ToView(from + normal * (float)length)));
                }
            }
            AddLines(lines, Color.FromRgb(0x2E, 0xB6, 0x7D), width * 0.7);
        }
        if (_showBones)
        {
            double size = radius * 0.02;
            var lines = new List<(Point3D, Point3D)>();
            foreach (var (mesh, place) in _parts)
                foreach (var inverse in mesh.BoneInverseBinds)
                    if (System.Numerics.Matrix4x4.Invert(inverse, out var bound))
                    {
                        var joint = ToView(System.Numerics.Vector3.Transform(bound.Translation, place));
                        lines.Add((joint + new Vector3D(-size, 0, 0), joint + new Vector3D(size, 0, 0)));
                        lines.Add((joint + new Vector3D(0, -size, 0), joint + new Vector3D(0, size, 0)));
                        lines.Add((joint + new Vector3D(0, 0, -size), joint + new Vector3D(0, 0, size)));
                    }
            AddLines(lines, Color.FromRgb(0xF2, 0xC9, 0x4C), width * 1.4);
        }
        if (_collision.Count > 0)
        {
            var lines = new List<(Point3D, Point3D)>();
            var place = _parts[0].Place;
            foreach (var (vertices, triangles) in _collision)
            {
                var seen = new HashSet<long>();
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = triangles[i + k], b = triangles[i + (k + 1) % 3];
                        if (a >= vertices.Length || b >= vertices.Length || !seen.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a)) continue;
                        lines.Add((ToView(System.Numerics.Vector3.Transform(vertices[a], place)), ToView(System.Numerics.Vector3.Transform(vertices[b], place))));
                    }
            }
            AddLines(lines, Color.FromRgb(0xF2, 0x7A, 0x4A), width * 1.2);
        }
    }

    /// <summary>Lines in 3D, each as two narrow strips at right angles (WPF has no lines in 3D), in one colour that shows whatever the light.</summary>
    private void AddLines(List<(Point3D A, Point3D B)> lines, Color color, double width)
    {
        if (lines.Count == 0) return;
        var points = new Point3D[lines.Count * 8];
        var indices = new int[lines.Count * 12];
        for (int i = 0; i < lines.Count; i++)
        {
            var (a, b) = lines[i];
            var along = b - a;
            var side = along.LengthSquared < 1e-12 ? new Vector3D(1, 0, 0)
                : Vector3D.CrossProduct(along, Math.Abs(along.Y) < 0.9 * along.Length ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0));
            side.Normalize();
            var up = along.LengthSquared < 1e-12 ? new Vector3D(0, 1, 0) : Vector3D.CrossProduct(along, side);
            up.Normalize();
            side *= width;
            up *= width;
            int at = i * 8;
            (points[at], points[at + 1], points[at + 2], points[at + 3]) = (a - side, a + side, b + side, b - side);
            (points[at + 4], points[at + 5], points[at + 6], points[at + 7]) = (a - up, a + up, b + up, b - up);
            for (int strip = 0; strip < 2; strip++)
            {
                int s0 = at + strip * 4, t = i * 12 + strip * 6;
                (indices[t], indices[t + 1], indices[t + 2], indices[t + 3], indices[t + 4], indices[t + 5]) = (s0, s0 + 1, s0 + 2, s0, s0 + 2, s0 + 3);
            }
        }
        var geometry = new MeshGeometry3D { Positions = new Point3DCollection(points), TriangleIndices = new Int32Collection(indices) };
        geometry.Freeze();
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var material = new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) } };
        material.Freeze();
        _debug.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
    }

    /// <summary>
    /// A grid on the ground the meshes stand on (the lowest point of them as they were made, which for a character is
    /// its feet), reaching out past them, its lines at a round spacing for their size and every fifth one brighter. Each
    /// line is a thin flat strip, as WPF has no lines in 3D.
    /// </summary>
    // the ground to draw the grid over, in the game's space, when it's given rather than worked out from the meshes
    private (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max, float Ground)? _floorArea;

    /// <summary>
    /// The area the floor grid covers, in the game's Z-up space, and the height of the ground: for a scene whose meshes
    /// move about (a cutscene's actors), so the grid is under everywhere they go. Null works it out from the meshes.
    /// </summary>
    public void SetFloorArea(System.Numerics.Vector3 min, System.Numerics.Vector3 max, float ground)
    {
        _floorArea = (min, max, ground);
        RebuildFloor();
    }

    private void RebuildFloor()
    {
        _floor.Children.Clear();
        if (!_showFloor || _parts.Count == 0) return;
        Point3D min, max;
        double size;
        if (_floorArea is { } area)
        {
            // the grid a little wider than the area, on the ground
            (min, max) = (ToView(new System.Numerics.Vector3(area.Min.X, area.Min.Y, area.Ground)), ToView(new System.Numerics.Vector3(area.Max.X, area.Max.Y, area.Ground)));
            (min, max) = (new Point3D(Math.Min(min.X, max.X), area.Ground, Math.Min(min.Z, max.Z)), new Point3D(Math.Max(min.X, max.X), area.Ground, Math.Max(min.Z, max.Z)));
            size = Math.Max(4, Math.Max(max.X - min.X, max.Z - min.Z) + 6);
        }
        else
        {
            (min, max) = Bounds();
            size = Math.Max(2, Math.Max(max.X - min.X, max.Z - min.Z) * 2.5);
        }
        double step = new[] { 0.1, 0.25, 0.5, 1, 2, 5, 10, 25, 50, 100 }.FirstOrDefault(s => size / s <= (_floorArea is null ? 24 : 60), 250);
        int lines = (int)Math.Ceiling(size / 2 / step);
        double half = lines * step, y = min.Y;
        double cx = Math.Round((min.X + max.X) / 2 / step) * step, cz = Math.Round((min.Z + max.Z) / 2 / step) * step;
        double width = Math.Max(0.002, step * 0.012);
        var minor = (Positions: new Point3DCollection(), Indices: new Int32Collection());
        var major = (Positions: new Point3DCollection(), Indices: new Int32Collection());
        static void Strip((Point3DCollection Positions, Int32Collection Indices) to, Point3D a, Point3D b, Point3D c, Point3D d)
        {
            int start = to.Positions.Count;
            to.Positions.Add(a); to.Positions.Add(b); to.Positions.Add(c); to.Positions.Add(d);
            to.Indices.Add(start); to.Indices.Add(start + 1); to.Indices.Add(start + 2);
            to.Indices.Add(start); to.Indices.Add(start + 2); to.Indices.Add(start + 3);
        }
        for (int k = -lines; k <= lines; k++)
        {
            var into = k % 5 == 0 ? major : minor;
            double w = k % 5 == 0 ? width * 1.6 : width, x = cx + k * step, z = cz + k * step;
            Strip(into, new Point3D(cx - half, y, z - w), new Point3D(cx - half, y, z + w), new Point3D(cx + half, y, z + w), new Point3D(cx + half, y, z - w));
            Strip(into, new Point3D(x - w, y, cz - half), new Point3D(x + w, y, cz - half), new Point3D(x + w, y, cz + half), new Point3D(x - w, y, cz + half));
        }
        foreach (var (lines3d, colour) in new[] { (minor, Color.FromRgb(72, 72, 82)), (major, Color.FromRgb(112, 112, 126)) })
        {
            var geometry = new MeshGeometry3D { Positions = lines3d.Positions, TriangleIndices = lines3d.Indices };
            geometry.Freeze();
            var material = new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(new SolidColorBrush(colour)) } };
            material.Freeze();
            _floor.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
        }
    }

    /// <summary>
    /// A chunk in its vertex colours. WPF can't blend colours across a triangle from its corners, so each triangle gets
    /// its own corners and the average of their colours, looked up in a small palette texture; a chunk without vertex
    /// colours is white.
    /// </summary>
    private static (MeshGeometry3D, Material) ColoredGeometry(MeshChunk chunk)
    {
        int triangles = chunk.Indices.Length / 3;
        var palette = new Dictionary<int, int>();
        var colorOf = new int[triangles];
        for (int t = 0; t < triangles; t++)
        {
            int r = 255, g = 255, b = 255;
            if (chunk.Colors.Length > 0)
            {
                (r, g, b) = (0, 0, 0);
                for (int k = 0; k < 3; k++)
                {
                    int v = chunk.Indices[t * 3 + k] * 3;
                    if (v + 2 >= chunk.Colors.Length) continue;
                    (r, g, b) = (r + chunk.Colors[v], g + chunk.Colors[v + 1], b + chunk.Colors[v + 2]);
                }
                (r, g, b) = (r / 3, g / 3, b / 3);
            }
            // five bits a channel keeps the palette small without bands showing
            int key = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
            if (!palette.TryGetValue(key, out int index)) palette[key] = index = palette.Count;
            colorOf[t] = index;
        }
        int side = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(palette.Count)));
        var pixels = new byte[side * side * 4];
        foreach (var (key, index) in palette)
        {
            int at = index * 4;
            pixels[at] = (byte)((key & 31) << 3 | 4);
            pixels[at + 1] = (byte)((key >> 5 & 31) << 3 | 4);
            pixels[at + 2] = (byte)((key >> 10 & 31) << 3 | 4);
            pixels[at + 3] = 255;
        }
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
        bitmap.Freeze();
        var positions = new Point3DCollection(triangles * 3);
        var uvs = new PointCollection(triangles * 3);
        var indices = new Int32Collection(triangles * 3);
        for (int t = 0; t < triangles; t++)
        {
            var uv = new Point((colorOf[t] % side + 0.5) / side, (colorOf[t] / side + 0.5) / side);
            // wound the other way round for WPF, as the textured geometry is
            foreach (int k in new[] { 0, 2, 1 })
            {
                int v = chunk.Indices[t * 3 + k];
                indices.Add(positions.Count);
                positions.Add(v < chunk.Positions.Length ? ToView(chunk.Positions[v]) : default);
                uvs.Add(uv);
            }
        }
        var geometry = new MeshGeometry3D { Positions = positions, TextureCoordinates = uvs, TriangleIndices = indices };
        geometry.Freeze();
        var brush = new ImageBrush(bitmap) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return (geometry, material);
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
        if (!_wireframe || _parts.Count == 0) return;
        var drawn = _parts.SelectMany((part, p) => DrawnAt(part.Mesh).Where(c => Shown(p, c))
            .Select(c => (Chunk: c, part.Place, Positions: _posedPositions.GetValueOrDefault((p, c)) ?? c.Positions, Edges: Edges(c)))).ToList();
        if (drawn.Count == 0) return;
        if (_wireWidth <= 0) _wireWidth = Math.Max(1e-4, Radius() * 0.0012);
        double width = _wireWidth;

        // each edge is 4 points, so every chunk's share of the points is known before any is made
        var firstPoint = new int[drawn.Count];
        int total = 0;
        for (int i = 0; i < drawn.Count; i++) (firstPoint[i], total) = (total, total + drawn[i].Edges.Length / 3 * 4);
        var points = new Point3D[total];
        Parallel.For(0, drawn.Count, i =>
        {
            var (_, place, at, edges) = drawn[i];
            var view = new Point3D[at.Length];
            for (int v = 0; v < at.Length; v++) view[v] = ToView(place.IsIdentity ? at[v] : System.Numerics.Vector3.Transform(at[v], place));
            int to = firstPoint[i];
            for (int e = 0; e + 2 < edges.Length; e += 3, to += 4) Edge(points, to, view[edges[e]], view[edges[e + 1]], view[edges[e + 2]], width);
        });

        // the triangles are the same for every pose of the same edges, so they're made once
        if (_wireIndices is null || _wireIndices.Count != total / 4 * 6)
        {
            var indices = new int[total / 4 * 6];
            for (int at = 0, point = 0; point < total; at += 6, point += 4)
                (indices[at], indices[at + 1], indices[at + 2], indices[at + 3], indices[at + 4], indices[at + 5]) = (point, point + 1, point + 2, point, point + 2, point + 3);
            _wireIndices = new Int32Collection(indices);
            _wireIndices.Freeze();
        }
        var geometry = new MeshGeometry3D { Positions = new Point3DCollection(points), TriangleIndices = _wireIndices };
        geometry.Freeze();
        _wires.Children.Add(new GeometryModel3D(geometry, WireMaterial) { BackMaterial = WireMaterial });
    }

    private double _wireWidth;
    private Int32Collection? _wireIndices;

    private static readonly Material WireMaterial = MakeWireMaterial();

    private static Material MakeWireMaterial()
    {
        var brush = new SolidColorBrush(Color.FromRgb(225, 230, 235));
        brush.Freeze();
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(Brushes.Black));
        group.Children.Add(new EmissiveMaterial(brush));
        group.Freeze();
        return group;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshChunk, int[]> EdgeCache = [];

    /// <summary>
    /// A chunk's edges, each once, as its two vertices' numbers and the third vertex of a triangle it's an edge of (which
    /// gives the surface the line is laid on); found the first time they're wanted.
    /// </summary>
    private static int[] Edges(MeshChunk chunk) => EdgeCache.GetValue(chunk, c =>
    {
        var seen = new HashSet<long>();
        var edges = new List<int>();
        for (int i = 0; i + 2 < c.Indices.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = c.Indices[i + k], b = c.Indices[i + (k + 1) % 3];
                if (a == b || !seen.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a)) continue;
                edges.Add(a);
                edges.Add(b);
                edges.Add(c.Indices[i + (k + 2) % 3]);
            }
        return [.. edges];
    });

    /// <summary>
    /// An edge as a narrow strip, 4 points from <paramref name="at"/>, laid flat on the surface of the triangle with
    /// <paramref name="third"/> as its other corner, so it's seen wherever the surface is; a point-like one is folded to a point.
    /// </summary>
    private static void Edge(Point3D[] points, int at, Point3D a, Point3D b, Point3D third, double width)
    {
        var along = b - a;
        // the game's triangles are wound the other way round from WPF's, so this points out of the surface
        var normal = Vector3D.CrossProduct(third - a, along);
        var side = Vector3D.CrossProduct(normal, along);
        if (along.LengthSquared < 1e-12 || side.LengthSquared < 1e-24)
        {
            for (int i = 0; i < 4; i++) points[at + i] = a;
            return;
        }
        side.Normalize();
        side *= width;
        // lifted a little off the surface, or it flickers in and out of it
        normal.Normalize();
        var lift = normal * (width * 2);
        (points[at], points[at + 1], points[at + 2], points[at + 3]) = (a - side + lift, a + side + lift, b + side + lift, b - side + lift);
    }

    // ---------------------------------------------------------------- camera

    private (Point3D Min, Point3D Max) Bounds()
    {
        if (_parts.Count == 0) return (new Point3D(-1, -1, -1), new Point3D(1, 1, 1));
        var chunks = _parts.SelectMany((part, i) => DrawnAt(part.Mesh).Where(c => Shown(i, c) && c.Positions.Length > 0).Select(c => (Part: i, Chunk: c, part.Place))).ToList();
        if (chunks.Count == 0) chunks = _parts.SelectMany((part, i) => part.Mesh.Chunks.Where(c => c.Positions.Length > 0).Select(c => (Part: i, Chunk: c, part.Place))).ToList();
        if (chunks.Count == 0) return (new Point3D(-1, -1, -1), new Point3D(1, 1, 1));
        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        // where each chunk is in the pose shown, when it's posed, as a cutscene's actors stand away from where they were made
        foreach (var (part, chunk, place) in chunks)
            foreach (var p in _posedPositions.TryGetValue((part, chunk), out var posed) ? posed : chunk.Positions)
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
        // far enough to fit it in the narrower of the view's two angles: across the width, or the height when it's wider than tall
        double aspect = ActualWidth > 0 && ActualHeight > 0 ? ActualWidth / ActualHeight : 1;
        double high = 2 * Math.Atan(Math.Tan(_camera.FieldOfView * Math.PI / 360) / aspect) * 180 / Math.PI;
        _distance = radius / Math.Sin((_fieldOfView is null ? _camera.FieldOfView : Math.Min(_camera.FieldOfView, high)) * Math.PI / 360) * 1.1;
        _camera.NearPlaneDistance = radius * 0.01;
        _camera.FarPlaneDistance = radius * 200;
        UpdateCamera();
    }

    /// <summary>Points the camera at the middle of a box in the game's Z-up space, from far enough to see all of it.</summary>
    public void FitTo(System.Numerics.Vector3 min, System.Numerics.Vector3 max)
    {
        var (a, b) = (ToView(min), ToView(max));
        var (low, high) = (new Point3D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Point3D(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        _target = low + (high - low) / 2;
        double radius = Math.Max(0.5, (high - low).Length / 2);
        double aspect = ActualWidth > 0 && ActualHeight > 0 ? ActualWidth / ActualHeight : 1;
        // back far enough that every corner of the box is inside the picture as the camera looks now: a sphere round the
        // box would sit much further back in a wide, short view
        double across = Math.Tan(FieldOfView * Math.PI / 360), up = across / aspect;
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        var forward = new Vector3D(-Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0));
        if (right.Length < 1e-6) right = new Vector3D(1, 0, 0);
        right.Normalize();
        var above = Vector3D.CrossProduct(right, forward);
        double distance = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            var at = new Point3D((corner & 1) == 0 ? low.X : high.X, (corner & 2) == 0 ? low.Y : high.Y, (corner & 4) == 0 ? low.Z : high.Z) - _target;
            double depth = Vector3D.DotProduct(at, forward);
            distance = Math.Max(distance, depth + Math.Abs(Vector3D.DotProduct(at, right)) / across);
            distance = Math.Max(distance, depth + Math.Abs(Vector3D.DotProduct(at, above)) / up);
        }
        _distance = Math.Max(distance * 1.1, radius * 0.5);
        _camera.NearPlaneDistance = Math.Max(0.01, radius * 0.01);
        _camera.FarPlaneDistance = Math.Max(2000, radius * 200);
        UpdateCamera();
    }

    /// <summary>
    /// The angle the view starts from, before anything is shown: round from -Y (0) and down from level, in degrees.
    /// The game's characters face +Y, so 180 looks them in the face; a prop reads better from the default three-quarter view.
    /// </summary>
    public void StartFrom(double yaw, double pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        UpdateCamera();
    }

    /// <summary>Looks from a side: the front (0), the right (90), the back (180), the left (270), or from above when <paramref name="top"/>.</summary>
    public void LookFrom(double yaw, bool top = false)
    {
        _yaw = yaw;
        _pitch = top ? 89 : 0;
        FitToView();
    }

    // a camera of the game's to look through instead of the one the mouse moves, while it's set
    private (Point3D Eye, Vector3D Forward, Vector3D Up, double FieldOfView)? _lookingThrough;

    /// <summary>Whether the view is looking through a camera given to it rather than the one the mouse moves.</summary>
    public bool IsLookingThrough => _lookingThrough is not null;

    /// <summary>
    /// Looks through a camera of the game's: from a point, along a direction with an up, in the game's Z-up space, with a
    /// field of view up the height, in degrees, as the game's cameras have it (measured against REDkit's picture of the
    /// same shot), so a wider picture shows more at the sides. The mouse doesn't move it; <see cref="StopLookingThrough"/> gives
    /// the view back to the mouse, where it was.
    /// </summary>
    public void LookThrough(System.Numerics.Vector3 eye, System.Numerics.Vector3 forward, System.Numerics.Vector3 up, double fieldOfView, double? nearPlane = null)
    {
        _lookingThrough = (ToView(eye), new Vector3D(forward.X, forward.Z, -forward.Y), new Vector3D(up.X, up.Z, -up.Y), fieldOfView);
        SetThroughPlanes(_lookingThrough.Value.Eye, nearPlane);
        UpdateCamera();
    }

    // where the scene is, kept for the planes through a camera: worked out again at most twice a second as it moves
    private (Point3D Centre, double Radius)? _sceneSphere;
    private bool _sceneMoved;
    private long _sceneMeasuredAt;

    /// <summary>
    /// The near and far planes through a camera, fitted to the scene: the far one just past its far side, the near one
    /// a small part of that. A fixed 5 cm to 2 km is more than the depth buffer can tell apart, and surfaces show
    /// through one another.
    /// </summary>
    private void SetThroughPlanes(Point3D eye, double? nearPlane)
    {
        long now = Environment.TickCount64;
        if (_sceneSphere is null || _sceneMoved && now - _sceneMeasuredAt > 500)
        {
            var (min, max) = Bounds();
            _sceneSphere = (min + (max - min) / 2, Math.Max(1, (max - min).Length / 2));
            _sceneMoved = false;
            _sceneMeasuredAt = now;
        }
        var (centre, radius) = _sceneSphere.Value;
        // a little past the far side, as actors move between measurements
        double far = Math.Max(10, (eye - centre).Length + radius * 1.5);
        _camera.FarPlaneDistance = far;
        // the camera's own near plane where it has one (a cutscene's), cutting away what's too close to the lens as the game does
        _camera.NearPlaneDistance = nearPlane is { } near ? Math.Max(near, far / 20000) : Math.Clamp(far / 4000, 0.02, 0.5);
    }

    /// <summary>Gives the view back to the mouse, looking from where it did before it looked through a camera.</summary>
    public void StopLookingThrough()
    {
        if (_lookingThrough is null) return;
        _lookingThrough = null;
        double radius = Radius();
        _camera.NearPlaneDistance = radius * 0.01;
        _camera.FarPlaneDistance = radius * 200;
        UpdateCamera();
    }

    private double? _fieldOfView;
    private double _lightAngle;

    /// <summary>The free camera's field of view across the width, in degrees, as the game and REDkit give one.</summary>
    public double FieldOfView
    {
        get => _fieldOfView ?? 45;
        set
        {
            _fieldOfView = Math.Clamp(value, 10, 120);
            UpdateCamera();
        }
    }

    /// <summary>Where the light comes from, in degrees round from where the camera looks (0 is from behind the camera).</summary>
    public double LightAngle
    {
        get => _lightAngle;
        set
        {
            _lightAngle = value;
            UpdateCamera();
        }
    }

    /// <summary>
    /// Lights of the scene's own to add to the view (a scene's campfire or key light), in place of the last ones. While
    /// there are any, the light from behind the camera is dimmed, so they show.
    /// </summary>
    public void SetLights(IReadOnlyList<Light> lights)
    {
        if (lights.Count == 0 && _lights.Children.Count == 0) return;
        _lights.Children.Clear();
        foreach (var light in lights) _lights.Children.Add(light);
        byte head = (byte)(lights.Count > 0 ? 120 : 200);
        _headlight.Color = Color.FromRgb(head, head, head);
    }

    /// <summary>The light, from behind the camera and a little above and to the side, turned round by <see cref="LightAngle"/>.</summary>
    private Vector3D LightFrom(Vector3D look)
    {
        var direction = look + new Vector3D(0.2, -0.3, 0);
        double a = _lightAngle * Math.PI / 180;
        return new Vector3D(direction.X * Math.Cos(a) - direction.Z * Math.Sin(a), direction.Y, direction.X * Math.Sin(a) + direction.Z * Math.Cos(a));
    }

    private void UpdateCamera()
    {
        if (_lookingThrough is { } through)
        {
            _camera.Position = through.Eye;
            _camera.LookDirection = through.Forward;
            _camera.UpDirection = through.Up;
            // the game's angle is up the height; WPF's is across the width, so it's worked out for the picture's shape
            double shape = ActualWidth > 0 && ActualHeight > 0 ? ActualWidth / ActualHeight : 16.0 / 9;
            _camera.FieldOfView = Math.Min(170, 2 * Math.Atan(Math.Tan(through.FieldOfView * Math.PI / 360) * shape) * 180 / Math.PI);
            _headlight.Direction = LightFrom(through.Forward);
            return;
        }
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        // the game's front is -Y, which is +Z here
        var direction = new Vector3D(-Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
        _camera.Position = _target - direction * _distance;
        _camera.LookDirection = direction;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.FieldOfView = _fieldOfView ?? 45;
        _headlight.Direction = LightFrom(direction);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateCamera();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_lookingThrough is not null) return; // the camera looked through isn't moved by hand
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
        if (_lookingThrough is not null) return;
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1 / 0.85), Radius() * 0.02, Radius() * 100);
        UpdateCamera();
        e.Handled = true;
    }
}
