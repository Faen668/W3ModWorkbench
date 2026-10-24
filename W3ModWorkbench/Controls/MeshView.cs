using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using W3ModWorkbench.Core.Meshes;
using Media3D = System.Windows.Media.Media3D;
using Point3D = System.Windows.Media.Media3D.Point3D;
using Vector3D = System.Windows.Media.Media3D.Vector3D;
using Vector3 = System.Numerics.Vector3;
using HxMesh = HelixToolkit.SharpDX.MeshGeometry3D;
using HxLines = HelixToolkit.SharpDX.LineGeometry3D;
using HxCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using Color4 = HelixToolkit.Maths.Color4;

namespace W3ModWorkbench;

/// <summary>
/// Shows a mesh in 3D, lit from where you look. Drag with the left button to turn around it, with the right or middle
/// button (or Shift and the left) to move it, and use the wheel to come closer; double-click fits it to the view.
/// Each chunk is drawn with its material's texture, or plain grey when there's none. The game is Z-up, so a mesh is
/// turned to stand up the way it does in the game. It can also show several meshes, each placed where it goes, the
/// way an entity puts its meshes together (<see cref="ShowParts"/>).
/// <para>
/// Drawn with Direct3D 11 through Helix Toolkit, lit a pixel at a time: a material's normal map gives a surface its
/// creases and wrinkles, a little specular its sheen, and hair is blended by its texture's alpha.
/// </para>
/// </summary>
public sealed partial class MeshView : Border
{
    // one graphics device and set of shaders for every view
    private static IEffectsManager? _effects;
    private static IEffectsManager Effects => _effects ??= Made(new DefaultEffectsManager());

    /// <summary>The shaders, with the view's own pass added (alpha-tested hair, skin lit through).</summary>
    private static IEffectsManager Made(IEffectsManager effects)
    {
        W3Shading.Install(effects);
        return effects;
    }

    private static bool _recovering;

    /// <summary>Raised once the shared graphics device has been made again, for every view to draw again.</summary>
    private static event EventHandler? DeviceRecovered;

    /// <summary>
    /// Makes the views' graphics device again after it failed, as Helix's maintainer advises for a lost device: what's
    /// no longer needed is let go first, then the device and its shaders are made anew, with the view's own passes.
    /// </summary>
    private static void RecoverDevice()
    {
        if (_recovering || Application.Current?.Dispatcher is not { } dispatcher) return;
        _recovering = true;
        dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(300);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (Effects is EffectsManager manager)
                {
                    manager.Reinitialize();
                    W3Shading.Install(manager);
                }
                DeviceRecovered?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is SharpDX.SharpDXException or InvalidOperationException or OutOfMemoryException)
            {
                // left as it is; the next failure tries again
            }
            finally
            {
                _recovering = false;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Whether hair is drawn by the view's own pass, alpha tested, rather than blended in Helix's transparent pass.</summary>
    private static bool AlphaTested => Effects is not null && W3Shading.Problem is null;

    private readonly Viewport3DX _viewport;
    private readonly HxCamera _camera = new() { FieldOfView = 45, NearPlaneDistance = 0.01, FarPlaneDistance = 10000 };
    private readonly DirectionalLight3D _headlight = new() { Color = Warm(200), Direction = new Vector3D(0, 0, -1) };

    /// <summary>The main light's colour at a brightness: a little warm, as daylight and candlelight are, so skin doesn't read grey.</summary>
    private static Color Warm(int level) => Color.FromRgb((byte)Math.Min(255, level * 1.06), (byte)level, (byte)(level * 0.9));
    private readonly GroupModel3D _lights = new();
    private readonly GroupModel3D _models = new();
    private readonly GroupModel3D _floor = new();
    private bool _showFloor;
    // what's drawn over the meshes to see how they're made: their box, normals, bones and collision
    private readonly GroupModel3D _debug = new();
    private readonly GroupModel3D _skeleton = new();
    private readonly GroupModel3D _marks = new();
    private bool _showBounds, _showNormals, _showBones, _vertexColors;
    private IReadOnlyList<(Vector3[] Vertices, int[] Triangles)> _collision = [];
    private readonly Dictionary<int, MeshGeometryModel3D> _chunks = [];
    // every chunk built since the meshes were last built whole, drawn or not: a mesh coming back to a LOD it was drawn at
    // takes its chunks from here rather than building them again
    private readonly Dictionary<int, MeshGeometryModel3D> _built = [];
    // hair's soft edges: the same cards again, blended, by the chunk they go with (see Fringe)
    private readonly Dictionary<int, MeshGeometryModel3D> _fringes = [];
    private readonly Dictionary<int, Material> _fringeMaterials = [];
    private readonly Dictionary<Element3D, int> _chunkOf = [];
    private readonly Dictionary<int, Material> _materials = [];
    private readonly HashSet<int> _hidden = [];
    private List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> _parts = [];
    private HashSet<int> _highlighted = [];
    private Func<int, MeshChunk, Vector3[]?>? _posed;
    // each chunk's vertices in the pose shown, kept for the bounds and the normals drawn
    private readonly Dictionary<(int Part, MeshChunk Chunk), Vector3[]> _posedPositions = [];
    private MeshFile? _mesh => _parts.Count > 0 ? _parts[0].Mesh : null;
    private int _lod;
    private bool _wireframe;
    private bool _textured = true;
    private readonly Dictionary<int, BitmapSource?> _textures = [];
    private readonly Dictionary<int, MaterialLook?> _looks = [];

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
        _viewport = new Viewport3DX
        {
            EffectsManager = Effects,
            Camera = _camera,
            BackgroundColor = Color.FromRgb(32, 32, 32),
            ShowViewCube = false,
            ShowCoordinateSystem = false,
            // the mouse is this control's: it turns the camera itself, as it always has
            IsHitTestVisible = false,
            // smoothing edges takes samples of each pixel, which a big view filled with a mesh pays for most
            MSAA = App.Settings.Antialiasing switch { 0 => MSAALevel.Disable, 4 => MSAALevel.Four, _ => MSAALevel.Two },
            // hair is alpha tested by the view's own pass; should that pass be missing, it's blended without depth between
            // its cards (sorted card by card, a nearer card's see-through parts hid those behind and a beard showed squares)
            OITRenderMode = OITRenderType.SinglePassWeighted,
            // shadows from the light behind the camera: under a chin, a hat's brim, an arm across the body
            IsShadowMappingEnabled = true,
            // ambient occlusion from the depth of what's drawn: the light all round darkened in creases and corners
            EnableSSAO = true,
            SSAOSamplingRadius = 0.15,
            SSAOIntensity = 1.2,
        };
        // first, as the shadow map is made from the first directional light there is
        _viewport.Items.Add(_headlight);
        _viewport.Items.Add(new ShadowMap3D { Resolution = new Size(4096, 4096), Bias = 0.003, Intensity = 0.85, AutoCoverCompleteScene = true, IsSceneDynamic = true });
        _viewport.Items.Add(_lights);
        _viewport.Items.Add(_floor);
        _viewport.Items.Add(_models);
        _viewport.Items.Add(_debug);
        _viewport.Items.Add(_skeleton);
        _viewport.Items.Add(_marks);
        _viewport.Items.Add(_particles);
        // the frame rate panel over the picture's corner, when Settings has it on
        Child = new Grid { Children = { _viewport, _statsPanel } };
        Loaded += (_, _) => HookStats(true);
        Unloaded += (_, _) => HookStats(false);
        // the view is drawn at a size set here, not the control's, so a drag that resizes it every frame (a splitter)
        // doesn't make Direct3D new surfaces for each size: those go only now and then, and dozens a second ran a
        // scene's graphics memory out (an out-of-memory error, or a white window). Meanwhile its picture is stretched
        _viewport.HorizontalAlignment = HorizontalAlignment.Left;
        _viewport.VerticalAlignment = VerticalAlignment.Top;
        _resizeSettled = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _resizeSettled.Tick += (_, _) => SettleSize();
        // the window's see-through backdrop doesn't show through Direct3D's picture, so it's cleared to the theme's
        // own background, following the theme as it changes
        SetResourceReference(ThemeBackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        // the environment's cube map, read once for every view; drawn again when it's there
        W3Environment.EnsureLoaded();
        W3Environment.Changed += (_, _) => _viewport.InvalidateRender();
        // the graphics device can fail under the view (out of memory, a driver reset, the computer waking from sleep):
        // the view says so here rather than the app stopping, and the device is made again for every view
        _viewport.RenderExceptionOccurred += (_, e) =>
        {
            e.Handled = true;
            RecoverDevice();
        };
        DeviceRecovered += (_, _) => _viewport.InvalidateRender();
    }

    // ---------------------------------------------------------------- the frame rate panel

    private readonly FrameStats _stats = new();
    private readonly TextBlock _statsText = new() { FontFamily = FrameStats.Font, FontSize = 11.5, Foreground = FrameStats.Ink };
    private readonly System.Windows.Controls.Border _statsPanel = new()
    {
        Background = FrameStats.Plate, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    private System.Windows.Threading.DispatcherTimer? _statsTimer;
    private bool _statsHooked;

    /// <summary>Counts the frames Helix draws, and shows the panel while the view is on screen and Settings has it on.</summary>
    private void HookStats(bool on)
    {
        if (on == _statsHooked) return;
        _statsHooked = on;
        _statsPanel.Child ??= _statsText;
        if (on) FrameStats.EnabledChanged += ShowStats;
        else FrameStats.EnabledChanged -= ShowStats;
        FollowRenderHost();
        ShowStats();
    }

    private HelixToolkit.SharpDX.IRenderHost? _countedHost;

    /// <summary>Counts the frames of the view's renderer: there may be none yet when the view is loaded, and a recovered device brings a new one.</summary>
    private void FollowRenderHost()
    {
        var host = _statsHooked ? _viewport.RenderHost : null;
        if (ReferenceEquals(host, _countedHost)) return;
        if (_countedHost is not null) _countedHost.Rendered -= NoteFrame;
        _countedHost = host;
        if (host is not null) host.Rendered += NoteFrame;
    }

    private void NoteFrame(object? sender, EventArgs e)
    {
        if (FrameStats.Enabled) _stats.Frame();
    }

    private void ShowStats()
    {
        bool show = _statsHooked && FrameStats.Enabled;
        _statsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _statsTimer is null)
        {
            _statsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (_, _) =>
            {
                FollowRenderHost();
                _statsText.Text = _stats.Text();
            };
        }
        if (show)
        {
            _statsText.Text = _stats.Text();
            _statsTimer!.Start();
        }
        else _statsTimer?.Stop();
    }

    private static readonly DependencyProperty ThemeBackgroundProperty = DependencyProperty.Register(nameof(ThemeBackgroundProperty), typeof(Brush), typeof(MeshView),
        new PropertyMetadata(null, (view, e) =>
        {
            var meshView = (MeshView)view;
            var color = e.NewValue is SolidColorBrush brush ? brush.Color : Color.FromRgb(32, 32, 32);
            meshView._viewport.BackgroundColor = color;
            // and the same colour behind it: where Direct3D's picture isn't quite opaque, WPF shows what's under it
            meshView.Background = new SolidColorBrush(color);
        }));

    /// <summary>The mesh shown, or null for none.</summary>
    public MeshFile? Mesh => _mesh;

    /// <summary>The LOD shown: 0 is the most detailed.</summary>
    public int Lod
    {
        get => _lod;
        set { _lod = value; Rebuild(); }
    }

    private bool _autoLod;
    private int[] _partLods = [];

    /// <summary>
    /// Whether each mesh is drawn at the LOD the game would draw it at from where the camera is (its LOD distances),
    /// rather than at <see cref="Lod"/>: for a scene's crowd, so actors far off cost less.
    /// </summary>
    public bool AutoLod
    {
        get => _autoLod;
        set
        {
            _autoLod = value;
            _partLods = [];
            Rebuild();
        }
    }

    /// <summary>
    /// The LOD each mesh would be drawn at from where the camera is now: the last whose distance it's past (a LOD's
    /// distance of 0 past the first means it isn't picked by distance), with a little give either side of each distance
    /// so a mesh right on one doesn't flick between two. Worked out each time the camera moves, so a camera cut shows
    /// the new shot's LODs in its first frame; the meshes whose LOD changed, or null for none.
    /// </summary>
    private List<int>? PickLods()
    {
        if (!_autoLod || _parts.Count == 0) return null;
        var eye = Numeric(_camera.Position);
        // where each mesh is: posed, a vertex of it as it's posed; else the middle of its box, placed
        var anchors = new Dictionary<int, Vector3>();
        foreach (var ((part, _), positions) in _posedPositions)
            if (positions.Length > 0) anchors.TryAdd(part, positions[0]);
        bool fresh = _partLods.Length != _parts.Count;
        var lods = new int[_parts.Count];
        List<int>? changed = null;
        for (int p = 0; p < _parts.Count; p++)
        {
            var (mesh, place) = _parts[p];
            var at = anchors.TryGetValue(p, out var anchor) ? anchor : Vector3.Transform((mesh.BoundsMin + mesh.BoundsMax) / 2, place);
            float distance = Vector3.Distance(eye, ToView(at));
            int LodAt(float scale)
            {
                int lod = 0;
                for (int i = 1; i < mesh.LodDistances.Count && i < mesh.LodCount; i++)
                    if (mesh.LodDistances[i] > 0 && distance >= mesh.LodDistances[i] * scale) lod = i;
                return lod;
            }
            int was = fresh ? -1 : _partLods[p];
            // kept while it's within 5% of the distance it changes at
            lods[p] = was >= LodAt(1.05f) && was <= LodAt(0.95f) ? was : LodAt(1);
            if (!fresh && lods[p] != was) (changed ??= []).Add(p);
        }
        _partLods = lods;
        return fresh ? [.. Enumerable.Range(0, _parts.Count)] : changed;
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

    /// <summary>Whether the meshes are shown in their vertex colours instead of their textures.</summary>
    public bool VertexColors { get => _vertexColors; set { _vertexColors = value; Rebuild(); } }

    /// <summary>Collision shapes to draw over the meshes, each its vertices and triangles in the mesh's space; none to draw none.</summary>
    public void ShowCollision(IReadOnlyList<(Vector3[] Vertices, int[] Triangles)> shapes)
    {
        _collision = shapes;
        RebuildDebug();
    }

    /// <summary>Whether the triangles' edges are drawn over the mesh.</summary>
    public bool Wireframe
    {
        get => _wireframe;
        set
        {
            _wireframe = value;
            foreach (var model in _chunks.Values) model.RenderWireframe = value;
        }
    }

    /// <summary>Whether textures are drawn, or every chunk in plain grey to see the shape.</summary>
    public bool Textured
    {
        get => _textured;
        set { _textured = value; _materials.Clear(); _fringeMaterials.Clear(); Rebuild(); }
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
    public void ShowParts(IReadOnlyList<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts) => ShowParts(parts, []);

    /// <summary>
    /// Shows meshes with their textures already read: each (part, material) drawn with its picture and look, or
    /// left out. Built once with all of them, rather than again for every texture put on after.
    /// </summary>
    public void ShowParts(IReadOnlyList<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts,
        IEnumerable<(int Part, int Material, BitmapSource? Picture, MaterialLook? Look, bool LeaveOut)> textures)
    {
        _poseVersion++;
        _parts = [.. parts];
        _highlighted = [];
        _posed = null;
        _posedPositions.Clear();
        _lod = 0;
        _hidden.Clear();
        _leftOut.Clear();
        _textures.Clear();
        _looks.Clear();
        _materials.Clear();
        _fringeMaterials.Clear();
        _sceneSphere = null;
        foreach (var (part, material, picture, look, leaveOut) in textures)
        {
            if (leaveOut) _leftOut.Add(Key(part, material));
            else
            {
                if (picture is not null) _textures[Key(part, material)] = picture;
                if (look is not null) _looks[Key(part, material)] = look;
            }
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

    /// <summary>Puts a picture (and what the material is beside it) on every chunk drawn with a material, or plain grey for null.</summary>
    public void SetTexture(int material, BitmapSource? texture, MaterialLook? look = null) => SetTexture(0, material, texture, look);

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

    /// <summary>Puts a picture and its look on the chunks of one of the meshes shown together that are drawn with a material.</summary>
    public void SetTexture(int part, int material, BitmapSource? texture, MaterialLook? look = null)
    {
        int key = Key(part, material);
        _textures[key] = texture;
        _looks[key] = look;
        _materials.Remove(key);
        _fringeMaterials.Remove(key);
        foreach (var (index, model) in _chunks)
            if (index >> 16 == part && part < _parts.Count && _parts[part].Mesh.Chunks[index & 0xFFFF].Material == material)
            {
                model.Material = MaterialFor(key, _highlighted.Contains(part));
                if (look?.Constants.Glow.Y > 0 && model.Geometry is HxMesh { Colors: null or { Count: 0 } } geometry)
                    geometry.Colors = VertexColorsOf(_parts[part].Mesh.Chunks[index & 0xFFFF]);
                model.IsTransparent = SeeThrough(key);
                model.IsThrowingShadow = !_vertexColors && !HasAlpha(key);
                Fringe(index, model, key);
            }
    }

    /// <summary>
    /// Marks some of the meshes shown together with a glow, to show where a part of an entity is; how many triangles
    /// they have at the LOD shown.
    /// </summary>
    public int Highlight(IEnumerable<int> parts)
    {
        _highlighted = [.. parts.Where(p => p >= 0 && p < _parts.Count)];
        foreach (var (key, model) in _chunks)
            model.Material = MaterialFor(Key(key >> 16, _parts[key >> 16].Mesh.Chunks[key & 0xFFFF].Material), _highlighted.Contains(key >> 16));
        return _highlighted.Sum(p => DrawnAt(p).Sum(c => c.Triangles));
    }

    /// <summary>Raised when one of the meshes shown together is clicked (not dragged), with its number, or -1 for none.</summary>
    public event EventHandler<int>? PartClicked;

    /// <summary>
    /// Shows the meshes in a pose: <paramref name="posed"/> gives a chunk's vertices where they are in it (by its mesh's
    /// number), or null to leave it as it was made. Null shows them all as they were made again.
    /// </summary>
    public void Pose(Func<int, MeshChunk, Vector3[]?>? posed)
    {
        _poseVersion++;
        _posed = posed;
        _posedPositions.Clear();
        _sceneMoved = true;
        foreach (var (key, model) in _chunks)
        {
            var chunk = _parts[key >> 16].Mesh.Chunks[key & 0xFFFF];
            var positions = posed?.Invoke(key >> 16, chunk);
            if (positions is not null) _posedPositions[(key >> 16, chunk)] = positions;
            if (model.Geometry is HxMesh geometry) PutPose(geometry, chunk, positions is null ? null : Bent(chunk, positions));
        }
    }

    // bumped by each pose and each new set of meshes, so a pose worked out in the background for an older one is dropped
    private int _poseVersion;

    /// <summary>
    /// Poses the meshes as <see cref="Pose"/> does, but works it out on the thread pool, the chunks in parallel: the
    /// pose itself (<paramref name="makePose"/> is called there, so the bones are placed off the UI thread too), each
    /// chunk's bent vertices and their normals, so the UI thread only has to hand them to the graphics card. A big cast
    /// (a battle's dozen actors) would otherwise keep the UI thread so busy it couldn't answer a click.
    /// </summary>
    public async Task PoseAsync(Func<Func<int, MeshChunk, Vector3[]?>?> makePose)
    {
        int version = ++_poseVersion;
        var parts = _parts;
        var keys = _chunks.Keys.ToList();
        var made = await Task.Run(() =>
        {
            var posed = makePose();
            var results = new (int Key, Vector3[]? Positions, Surface? Bent)[keys.Count];
            Parallel.For(0, keys.Count, i =>
            {
                int key = keys[i];
                var chunk = parts[key >> 16].Mesh.Chunks[key & 0xFFFF];
                var positions = posed?.Invoke(key >> 16, chunk);
                results[i] = (key, positions, positions is null ? null : Bent(chunk, positions));
            });
            return (Posed: posed, Results: results);
        });
        if (version != _poseVersion || !ReferenceEquals(parts, _parts)) return;
        _posed = made.Posed;
        _posedPositions.Clear();
        _sceneMoved = true;
        var done = new HashSet<int>();
        foreach (var (key, positions, bent) in made.Results)
        {
            var chunk = parts[key >> 16].Mesh.Chunks[key & 0xFFFF];
            if (positions is not null) _posedPositions[(key >> 16, chunk)] = positions;
            if (_chunks.TryGetValue(key, out var model) && model.Geometry is HxMesh geometry) PutPose(geometry, chunk, bent);
            done.Add(key);
        }
        // chunks the view was built with again meanwhile (another LOD, as the camera came closer) are posed here too, or
        // they'd keep the pose they were built in
        foreach (var (key, model) in _chunks)
        {
            if (done.Contains(key) || model.Geometry is not HxMesh geometry) continue;
            var chunk = parts[key >> 16].Mesh.Chunks[key & 0xFFFF];
            var positions = made.Posed?.Invoke(key >> 16, chunk);
            if (positions is not null) _posedPositions[(key >> 16, chunk)] = positions;
            PutPose(geometry, chunk, positions is null ? null : Bent(chunk, positions));
        }
    }

    /// <summary>A chunk's surface in the view's space: its vertices, their normals, and the tangents its normal map is laid along.</summary>
    private sealed record Surface(Vector3Collection Positions, Vector3Collection Normals, Vector3Collection Tangents, Vector3Collection BiTangents)
    {
        /// <summary>
        /// The surface of a chunk with its normals (in the game's space, as many as its vertices) and the tangents the
        /// mesh keeps, each turned as far as its normal has turned from where it was made, so a bent arm's normal map
        /// bends with it and a head and body that meet keep the same tangents along the seam. A mesh that keeps none
        /// has them worked out from its UVs.
        /// </summary>
        public static Surface Of(MeshChunk chunk, Vector3Collection positions, Vector3[] normals)
        {
            var viewNormals = new Vector3Collection(normals.Length);
            foreach (var n in normals) viewNormals.Add(ToView(n));
            if (chunk.Tangents.Length != normals.Length || chunk.Normals.Length != normals.Length)
            {
                var made = Geometry(chunk);
                HelixToolkit.Geometry.MeshBuilder.ComputeTangents(positions, viewNormals, made.TextureCoordinates!, made.TriangleIndices!, out var computed, out var bicomputed);
                return new Surface(positions, viewNormals, new Vector3Collection(computed), new Vector3Collection(bicomputed));
            }
            var tangents = new Vector3Collection(normals.Length);
            var bitangents = new Vector3Collection(normals.Length);
            for (int v = 0; v < normals.Length; v++)
            {
                var (from, to) = (chunk.Normals[v], normals[v]);
                var stored = chunk.Tangents[v];
                var tangent = new Vector3(stored.X, stored.Y, stored.Z);
                // the shortest turn from the normal as made to the normal now, applied to the tangent
                var axis = Vector3.Cross(from, to);
                float cos = Vector3.Dot(from, to);
                if (cos > -0.999f) tangent = tangent * cos + Vector3.Cross(axis, tangent) + axis * (Vector3.Dot(axis, tangent) / (1 + cos));
                tangent -= to * Vector3.Dot(to, tangent);
                tangent = tangent.LengthSquared() > 1e-12f ? Vector3.Normalize(tangent) : Vector3.UnitX;
                tangents.Add(ToView(tangent));
                bitangents.Add(ToView(Vector3.Cross(to, tangent) * stored.W));
            }
            return new Surface(positions, viewNormals, tangents, bitangents);
        }
    }

    /// <summary>
    /// A chunk bent into a pose, in the view's space: its vertices, normals worked out from its bent triangles (each
    /// vertex's the sum of its faces', by their size) and tangents to go with them. Made off the UI thread.
    /// </summary>
    private static Surface Bent(MeshChunk chunk, Vector3[] positions)
    {
        var points = new Vector3Collection(positions.Length);
        foreach (var v in positions) points.Add(ToView(v));
        var sums = new Vector3[positions.Length];
        var indices = chunk.Indices;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            // facing out as the mesh's own normals do (checked against them: the same on all but a few vertices)
            int a = indices[i], b = indices[i + 2], c = indices[i + 1];
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
            var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            sums[a] += face;
            sums[b] += face;
            sums[c] += face;
        }
        for (int v = 0; v < sums.Length; v++) sums[v] = sums[v].LengthSquared() > 1e-20f ? Vector3.Normalize(sums[v]) : Vector3.UnitZ;
        return Surface.Of(chunk, points, sums);
    }

    /// <summary>Hands a chunk's model its vertices in a pose, or as it was made for null.</summary>
    private static void PutPose(HxMesh geometry, MeshChunk chunk, Surface? bent)
    {
        var surface = bent ?? MadeSurface(chunk);
        if (ReferenceEquals(geometry.Positions, surface.Positions)) return;
        geometry.Positions = surface.Positions;
        geometry.Normals = surface.Normals;
        geometry.Tangents = surface.Tangents;
        geometry.BiTangents = surface.BiTangents;
        geometry.UpdateVertices();
        geometry.UpdateBounds();
    }

    /// <summary>The most LODs any of the meshes shown has.</summary>
    public int LodCount => _parts.Count == 0 ? 1 : _parts.Max(p => p.Mesh.LodCount);

    /// <summary>How big the meshes shown are, in metres: across, deep and high, as they were made.</summary>
    public Vector3 Size()
    {
        var (min, max) = Bounds();
        return new Vector3((float)(max.X - min.X), (float)(max.Z - min.Z), (float)(max.Y - min.Y));
    }

    /// <summary>How much is drawn at the LOD shown: triangles, vertices and chunks.</summary>
    public (int Triangles, int Vertices, int Chunks) Stats()
    {
        var chunks = _parts.SelectMany((part, p) => DrawnAt(p).Where(c => Shown(p, c))).ToList();
        return (chunks.Sum(c => c.Triangles), chunks.Sum(c => c.Positions.Length), chunks.Count);
    }

    /// <summary>The mesh under a point of the view, or -1: the nearest chunk under it, as the graphics card drew it.</summary>
    private int PartAt(Point point)
    {
        var at = TranslatePoint(point, _viewport);
        return _viewport.FindNearest(at, out _, out _, out var model, out _) && model is not null && _chunkOf.TryGetValue(model, out int key) ? key >> 16 : -1;
    }

    // chunks, materials and textures are kept by the mesh they're in and their number in it
    private static int Key(int part, int index) => part << 16 | index;

    /// <summary>The chunks of a mesh drawn at the LOD picked, or its least detailed when it has fewer.</summary>
    private IEnumerable<MeshChunk> DrawnAt(MeshFile mesh) => Drawn(mesh, Math.Min(_lod, Math.Max(0, mesh.LodCount - 1)));

    /// <summary>The chunks one of the meshes shown is drawn with: at the LOD picked, or the one its distance gives with <see cref="AutoLod"/>.</summary>
    private IEnumerable<MeshChunk> DrawnAt(int part)
    {
        var mesh = _parts[part].Mesh;
        int lod = _autoLod && part < _partLods.Length ? _partLods[part] : _lod;
        return Drawn(mesh, Math.Min(lod, Math.Max(0, mesh.LodCount - 1)));
    }

    /// <summary>A placement in the game's Z-up space as one in the view's Y-up space.</summary>
    private static Media3D.Matrix3D ToView(System.Numerics.Matrix4x4 m)
    {
        // the view's axes are the game's X, Z and -Y: S^-1 · M · S, with S taking (x, y, z) to (x, z, -y)
        var s = new Media3D.Matrix3D(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var inverse = s;
        inverse.Invert();
        var game = new Media3D.Matrix3D(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
        return inverse * game * s;
    }

    private static Vector3 ToView(Vector3 p) => new(p.X, p.Z, -p.Y);

    private static Point3D ToViewPoint(Vector3 p) => new(p.X, p.Z, -p.Y);

    private static Vector3 Numeric(Point3D p) => new((float)p.X, (float)p.Y, (float)p.Z);

    // ---------------------------------------------------------------- building the scene

    private void Rebuild()
    {
        if (_autoLod && _partLods.Length != _parts.Count) PickLods();
        _models.Children.Clear();
        _chunks.Clear();
        _built.Clear();
        _chunkOf.Clear();
        _fringes.Clear();
        var posed = PoseAhead(Enumerable.Range(0, _parts.Count));
        for (int p = 0; p < _parts.Count; p++) AddPart(p, posed);
        // with LODs picked by distance, every LOD's chunks are put in the scene now, hidden, so the graphics card has
        // them before a camera cut asks for them
        if (_autoLod)
            for (int p = 0; p < _parts.Count; p++)
            {
                var mesh = _parts[p].Mesh;
                foreach (var chunk in Enumerable.Range(0, mesh.LodCount).SelectMany(lod => Drawn(mesh, lod)).Distinct())
                {
                    int key = Key(p, chunk.Index);
                    if (_chunks.ContainsKey(key) || !Shown(p, chunk)) continue;
                    var model = Build(p, chunk, null);
                    Fringe(key, model, Key(p, chunk.Material));
                    Hide(key, model);
                }
            }
        RebuildFloor();
        RebuildDebug();
    }

    /// <summary>
    /// Draws the meshes whose LOD changed at their new LOD: only theirs, hiding the chunks of the LOD they were drawn at
    /// and showing the new LOD's (built once, kept), posed as the others are, rather than building the whole scene
    /// again, which stalled a cutscene for a moment at each camera cut.
    /// </summary>
    private void SwitchLods(IEnumerable<int> parts)
    {
        var list = parts.ToList();
        var posed = PoseAhead(list);
        foreach (int p in list)
        {
            var wanted = DrawnAt(p).Where(c => Shown(p, c)).Select(c => Key(p, c.Index)).ToHashSet();
            foreach (var key in _chunks.Keys.Where(k => k >> 16 == p && !wanted.Contains(k)).ToList())
            {
                Hide(key, _chunks[key]);
                _chunks.Remove(key);
            }
            foreach (var entry in _posedPositions.Keys.Where(k => k.Part == p).ToList()) _posedPositions.Remove(entry);
            AddPart(p, posed);
        }
    }

    /// <summary>Takes a chunk out of the picture, and its soft edges with it, leaving both in the scene.</summary>
    private void Hide(int key, MeshGeometryModel3D model)
    {
        model.IsRendering = false;
        model.IsHitTestVisible = false;
        if (_fringes.TryGetValue(key, out var fringe)) fringe.IsRendering = false;
    }

    /// <summary>
    /// The chunks some of the meshes are drawn with, bent into the pose shown: worked out on all the cores at once, as
    /// <see cref="PoseAsync"/> does, so a camera cut that changes the LOD of a whole cast doesn't hold the frame while
    /// each is bent in turn.
    /// </summary>
    private Dictionary<int, (Vector3[] Positions, Surface Bent)> PoseAhead(IEnumerable<int> parts)
    {
        var found = new System.Collections.Concurrent.ConcurrentDictionary<int, (Vector3[], Surface)>();
        if (_posed is not { } posed) return [];
        var chunks = parts.SelectMany(p => DrawnAt(p).Where(c => Shown(p, c)).Select(c => (Part: p, Chunk: c))).ToList();
        Parallel.ForEach(chunks, item =>
        {
            if (posed(item.Part, item.Chunk) is { } positions)
                found[Key(item.Part, item.Chunk.Index)] = (positions, Bent(item.Chunk, positions));
        });
        return new(found);
    }

    /// <summary>
    /// Shows one of the meshes' chunks at the LOD it's drawn at, each built (or taken from those built before) and posed,
    /// with the poses <see cref="PoseAhead"/> worked out.
    /// </summary>
    private void AddPart(int p, Dictionary<int, (Vector3[] Positions, Surface Bent)> posed)
    {
        foreach (var chunk in DrawnAt(p))
        {
            if (!Shown(p, chunk)) continue;
            int key = Key(p, chunk.Index);
            int material = Key(p, chunk.Material);
            var (positions, bent) = posed.TryGetValue(key, out var pose) ? pose : default;
            if (positions is not null) _posedPositions[(p, chunk)] = positions;
            if (_chunks.ContainsKey(key)) continue;
            if (_built.TryGetValue(key, out var model))
            {
                // as it is now: its pose, and whatever was changed on the drawn chunks while it wasn't drawn
                if (model.Geometry is HxMesh kept) PutPose(kept, chunk, bent);
                model.Material = _vertexColors ? VertexColorMaterial : MaterialFor(material, _highlighted.Contains(p));
                model.IsTransparent = !_vertexColors && SeeThrough(material);
                model.IsThrowingShadow = !_vertexColors && !HasAlpha(material);
                model.RenderWireframe = _wireframe;
                model.IsRendering = true;
                model.IsHitTestVisible = true;
            }
            else model = Build(p, chunk, bent);
            _chunks[key] = model;
            Fringe(key, model, material);
        }
    }

    /// <summary>Builds a chunk's model, in a pose or as it was made, and puts it in the scene.</summary>
    private MeshGeometryModel3D Build(int p, MeshChunk chunk, Surface? bent)
    {
        int key = Key(p, chunk.Index);
        int material = Key(p, chunk.Material);
        var place = _parts[p].Place;
        var made = Geometry(chunk);
        // each chunk its own geometry, sharing what a pose doesn't change, so each can be posed apart
        var surface = bent ?? MadeSurface(chunk);
        var geometry = new HxMesh
        {
            Positions = surface.Positions, Normals = surface.Normals, Tangents = surface.Tangents, BiTangents = surface.BiTangents,
            TextureCoordinates = made.TextureCoordinates, TriangleIndices = made.TriangleIndices,
        };
        // the vertex colours for the view's colours, or for a material that's coloured by them (hair)
        if (_vertexColors || _looks.GetValueOrDefault(material)?.Constants.Glow.Y > 0) geometry.Colors = VertexColorsOf(chunk);
        var model = new MeshGeometryModel3D
        {
            Geometry = geometry,
            Material = _vertexColors ? VertexColorMaterial : MaterialFor(material, _highlighted.Contains(p)),
            // both sides drawn, as many of the game's cloth and foliage chunks are single sheets
            CullMode = SharpDX.Direct3D11.CullMode.None,
            // a see-through texture (hair) is blended over what's behind it, drawn after the solid ones
            IsTransparent = !_vertexColors && SeeThrough(material),
            RenderWireframe = _wireframe,
            WireframeColor = Color.FromRgb(225, 230, 235),
            // posed, a chunk is far from where it was made, so it's never left out for being off screen there
            EnableViewFrustumCheck = false,
            // hair's cards would shadow as the solid sheets they are, as the shadow map doesn't test their alpha
            IsThrowingShadow = !_vertexColors && !HasAlpha(material),
        };
        if (!place.IsIdentity)
        {
            var transform = new Media3D.MatrixTransform3D(ToView(place));
            transform.Freeze();
            model.Transform = transform;
        }
        _built[key] = model;
        _chunkOf[model] = key;
        _models.Children.Add(model);
        return model;
    }

    /// <summary>
    /// Hair as the game draws it, in two passes: solid where its texture's alpha is over half (the view's pass, alpha
    /// tested, writing depth), then the same cards blended over that, so the thin ends of strands and sparse stubble
    /// fade out softly instead of being cut off. The second is a model of its own sharing the first's geometry, so a pose
    /// moves both. Only with the view's pass; without it, hair is blended whole.
    /// </summary>
    private void Fringe(int chunk, MeshGeometryModel3D model, int material)
    {
        bool wanted = AlphaTested && !_vertexColors && HasAlpha(material);
        if (!wanted)
        {
            if (_fringes.Remove(chunk, out var gone)) _models.Children.Remove(gone);
            return;
        }
        if (_fringes.TryGetValue(chunk, out var fringe)) fringe.IsRendering = true;
        else
        {
            fringe = new MeshGeometryModel3D
            {
                Geometry = model.Geometry,
                CullMode = SharpDX.Direct3D11.CullMode.None,
                IsTransparent = true,
                IsThrowingShadow = false,
                IsHitTestVisible = false,
                EnableViewFrustumCheck = false,
            };
            if (model.Transform is { } transform) fringe.Transform = transform;
            _fringes[chunk] = fringe;
            _models.Children.Add(fringe);
        }
        fringe.Material = FringeMaterial(material);
    }

    /// <summary>The blended pass's material for a hair material: the same picture and look, drawn in the view's transparent pass.</summary>
    private Material FringeMaterial(int material)
    {
        if (_fringeMaterials.TryGetValue(material, out var known)) return known;
        var picture = _textures.GetValueOrDefault(material);
        var look = _looks.GetValueOrDefault(material);
        var made = new PhongMaterial(new W3MaterialCore { Look = look, Soft = true })
        {
            DiffuseColor = new Color4(1, 1, 1, 1),
            AmbientColor = new Color4(0, 0, 0, 1),
            DiffuseMap = picture is null ? null : GpuTextures.Of(picture),
            NormalMap = look?.Normal is null ? null : GpuTextures.Of(look.Normal),
        };
        return _fringeMaterials[material] = made;
    }

    /// <summary>Whether a material is drawn in the transparent pass: glass, or hair when the view's own pass isn't there to test its alpha.</summary>
    private bool SeeThrough(int material) => _textured && _looks.GetValueOrDefault(material)?.Transparent == true || !AlphaTested && HasAlpha(material);

    /// <summary>Whether a material's picture has alpha: hair, eyelashes and the like.</summary>
    private bool HasAlpha(int material) =>
        _textured && _textures.GetValueOrDefault(material) is { } texture && (texture.Format == PixelFormats.Pbgra32 || texture.Format == PixelFormats.Bgra32)
        && _looks.GetValueOrDefault(material)?.Kind is null or SurfaceKind.Hair;

    /// <summary>
    /// The debug drawing over the meshes: the box they fit in (blue), each vertex's normal (green), the bones of a
    /// skinned mesh where they were bound (yellow crosses), and the collision shapes' edges (orange).
    /// </summary>
    private void RebuildDebug()
    {
        _debug.Children.Clear();
        if (_parts.Count == 0) return;
        double radius = Radius();
        if (_showBounds)
        {
            var (a, b) = Bounds();
            var c = new Point3D[8];
            for (int i = 0; i < 8; i++) c[i] = new Point3D((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z);
            var edges = new List<(Vector3, Vector3)>();
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) edges.Add((Numeric(c[i]), Numeric(c[i | bit])));
            AddLines(_debug, edges, Color.FromRgb(0x4C, 0x9A, 0xFF), 1.5);
        }
        var drawn = _parts.SelectMany((part, p) => DrawnAt(p).Where(c => Shown(p, c)).Select(c => (Part: p, Chunk: c, part.Place))).ToList();
        if (_showNormals)
        {
            int total = drawn.Sum(d => d.Chunk.Positions.Length);
            int every = Math.Max(1, total / 60000); // a big mesh has some of them drawn, so the view stays quick
            float length = (float)(radius * 0.04);
            var lines = new List<(Vector3, Vector3)>();
            foreach (var (part, chunk, place) in drawn)
            {
                var at = _posedPositions.GetValueOrDefault((part, chunk)) ?? chunk.Positions;
                for (int v = 0; v < at.Length && v < chunk.Normals.Length; v += every)
                {
                    var from = place.IsIdentity ? at[v] : Vector3.Transform(at[v], place);
                    var normal = place.IsIdentity ? chunk.Normals[v] : Vector3.TransformNormal(chunk.Normals[v], place);
                    lines.Add((ToView(from), ToView(from + normal * length)));
                }
            }
            AddLines(_debug, lines, Color.FromRgb(0x2E, 0xB6, 0x7D), 1);
        }
        if (_showBones)
        {
            float size = (float)(radius * 0.02);
            var lines = new List<(Vector3, Vector3)>();
            foreach (var (mesh, place) in _parts)
                foreach (var inverse in mesh.BoneInverseBinds)
                    if (System.Numerics.Matrix4x4.Invert(inverse, out var bound))
                    {
                        var joint = ToView(Vector3.Transform(bound.Translation, place));
                        lines.Add((joint - Vector3.UnitX * size, joint + Vector3.UnitX * size));
                        lines.Add((joint - Vector3.UnitY * size, joint + Vector3.UnitY * size));
                        lines.Add((joint - Vector3.UnitZ * size, joint + Vector3.UnitZ * size));
                    }
            AddLines(_debug, lines, Color.FromRgb(0xF2, 0xC9, 0x4C), 2);
        }
        if (_collision.Count > 0)
        {
            var lines = new List<(Vector3, Vector3)>();
            var place = _parts[0].Place;
            foreach (var (vertices, triangles) in _collision)
            {
                var seen = new HashSet<long>();
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = triangles[i + k], b = triangles[i + (k + 1) % 3];
                        if (a >= vertices.Length || b >= vertices.Length || !seen.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a)) continue;
                        lines.Add((ToView(Vector3.Transform(vertices[a], place)), ToView(Vector3.Transform(vertices[b], place))));
                    }
            }
            AddLines(_debug, lines, Color.FromRgb(0xF2, 0x7A, 0x4A), 1.5);
        }
    }

    /// <summary>
    /// A skeleton drawn over everything, in the game's space: a line from each bone to its parent, the one picked in
    /// the accent colour, and, as asked, each bone's axes (X red, Y green, Z blue, <paramref name="axisLength"/> long),
    /// its name beside it, and a path (where a bone goes over an animation). Null bones clear it.
    /// </summary>
    public void ShowSkeleton(System.Numerics.Matrix4x4[]? bones, IReadOnlyList<short>? parents, int selected = -1, float axisLength = 0,
        IReadOnlyList<string>? names = null, IReadOnlyList<Vector3>? path = null)
    {
        _skeleton.Children.Clear();
        if (bones is not null && parents is not null)
        {
            var lines = new List<(Vector3, Vector3)>();
            var helpers = new List<(Vector3, Vector3)>();
            var picked = new List<(Vector3, Vector3)>();
            var joints = new List<(Vector3, Vector3)>();
            float joint = 0.006f;
            for (int i = 0; i < bones.Length && i < parents.Count; i++)
            {
                var at = ToView(bones[i].Translation);
                int parent = parents[i];
                // a bone hung from the root (an IK target, the trajectory) is a helper, not the body: its line is faint
                if (parent >= 0 && parent < bones.Length)
                    (i == selected ? picked : parents[parent] < 0 ? helpers : lines).Add((ToView(bones[parent].Translation), at));
                var marks = i == selected ? picked : joints;
                marks.Add((at - Vector3.UnitX * joint, at + Vector3.UnitX * joint));
                marks.Add((at - Vector3.UnitY * joint, at + Vector3.UnitY * joint));
                marks.Add((at - Vector3.UnitZ * joint, at + Vector3.UnitZ * joint));
            }
            AddLines(_skeleton, helpers, Color.FromRgb(0x6A, 0x6A, 0x70), 1, onTop: true);
            AddLines(_skeleton, lines, Color.FromRgb(0xE8, 0xE4, 0xD8), 1.6, onTop: true);
            AddLines(_skeleton, joints, Color.FromRgb(0xF2, 0xC9, 0x4C), 2, onTop: true);
            if (axisLength > 0)
                for (int axis = 0; axis < 3; axis++)
                {
                    var axisLines = new List<(Vector3, Vector3)>();
                    var unit = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                    foreach (var bone in bones)
                        axisLines.Add((ToView(bone.Translation), ToView(Vector3.Transform(unit * axisLength, bone))));
                    AddLines(_skeleton, axisLines, axis == 0 ? Color.FromRgb(0xE5, 0x48, 0x48) : axis == 1 ? Color.FromRgb(0x4C, 0xC0, 0x5A) : Color.FromRgb(0x4C, 0x8A, 0xFF), 1.2, onTop: true);
                }
            AddLines(_skeleton, picked, Color.FromRgb(0xC7, 0x7D, 0xFF), 3, onTop: true);
            if (names is not null)
            {
                var text = new BillboardText3D();
                for (int i = 0; i < bones.Length && i < names.Count; i++)
                    text.TextInfo.Add(new TextInfo(names[i], ToView(bones[i].Translation))
                    {
                        Foreground = i == selected ? new Color4(0.78f, 0.49f, 1f, 1) : new Color4(0.95f, 0.93f, 0.88f, 1),
                        Background = new Color4(0, 0, 0, 0.45f),
                        Scale = 0.55f,
                    });
                _skeleton.Children.Add(new BillboardTextModel3D { Geometry = text, FixedSize = true, IsHitTestVisible = false, DepthBias = -100000000 });
            }
        }
        if (path is { Count: > 1 })
        {
            var trail = new List<(Vector3, Vector3)>();
            for (int i = 1; i < path.Count; i++) trail.Add((ToView(path[i - 1]), ToView(path[i])));
            AddLines(_skeleton, trail, Color.FromRgb(0x3F, 0xD0, 0xE0), 2.2, onTop: true);
        }
        _viewport.InvalidateRender();
    }

    /// <summary>
    /// Marks drawn over everything, in the game's space, in place of the last ones: lines, each in its colour and so
    /// many pixels thick, and labels beside points (a dialog set's slots and cameras). Empty lists clear them.
    /// </summary>
    public void ShowMarks(IReadOnlyList<(Vector3 A, Vector3 B, Color Color, double Thickness)> lines, IReadOnlyList<(string Text, Vector3 At, Color Color)> labels)
    {
        _marks.Children.Clear();
        foreach (var group in lines.GroupBy(l => (l.Color, l.Thickness)))
            AddLines(_marks, group.Select(l => (ToView(l.A), ToView(l.B))).ToList(), group.Key.Color, group.Key.Thickness, onTop: true);
        if (labels.Count > 0)
        {
            var text = new BillboardText3D();
            foreach (var (label, at, color) in labels)
                text.TextInfo.Add(new TextInfo(label, ToView(at))
                {
                    Foreground = new Color4(color.R / 255f, color.G / 255f, color.B / 255f, 1),
                    Background = new Color4(0, 0, 0, 0.55f),
                    Scale = 0.6f,
                });
            _marks.Children.Add(new BillboardTextModel3D { Geometry = text, FixedSize = true, IsHitTestVisible = false, DepthBias = -100000000 });
        }
        _viewport.InvalidateRender();
    }

    /// <summary>Lines in 3D, in the view's space, in one colour whatever the light, so many pixels thick.</summary>
    private static void AddLines(GroupModel3D into, List<(Vector3 A, Vector3 B)> lines, Color color, double thickness, bool onTop = false)
    {
        if (lines.Count == 0) return;
        var positions = new Vector3Collection(lines.Count * 2);
        var indices = new IntCollection(lines.Count * 2);
        foreach (var (a, b) in lines)
        {
            indices.Add(positions.Count);
            positions.Add(a);
            indices.Add(positions.Count);
            positions.Add(b);
        }
        into.Children.Add(new LineGeometryModel3D
        {
            Geometry = new HxLines { Positions = positions, Indices = indices },
            Color = color,
            Thickness = thickness,
            EnableViewFrustumCheck = false,
            IsHitTestVisible = false,
            // pulled nearly to the camera, to be seen through the body round it
            DepthBias = onTop ? -100000000 : 0,
        });
    }

    // the ground to draw the grid over, in the game's space, when it's given rather than worked out from the meshes
    private (Vector3 Min, Vector3 Max, float Ground)? _floorArea;

    /// <summary>
    /// The area the floor grid covers, in the game's Z-up space, and the height of the ground: for a scene whose meshes
    /// move about (a cutscene's actors), so the grid is under everywhere they go. Null works it out from the meshes.
    /// </summary>
    public void SetFloorArea(Vector3 min, Vector3 max, float ground)
    {
        _floorArea = (min, max, ground);
        RebuildFloor();
    }

    /// <summary>
    /// A grid on the ground the meshes stand on (the lowest point of them as they were made, which for a character is
    /// its feet), reaching out past them, its lines at a round spacing for their size and every fifth one brighter.
    /// </summary>
    private void RebuildFloor()
    {
        _floor.Children.Clear();
        _floorLines = [];
        if (!_showFloor || _parts.Count == 0) return;
        Point3D min, max;
        double size;
        if (_floorArea is { } area)
        {
            // the grid a little wider than the area, on the ground
            (min, max) = (ToViewPoint(new Vector3(area.Min.X, area.Min.Y, area.Ground)), ToViewPoint(new Vector3(area.Max.X, area.Max.Y, area.Ground)));
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
        double half = lines * step;
        float y = (float)min.Y;
        double cx = Math.Round((min.X + max.X) / 2 / step) * step, cz = Math.Round((min.Z + max.Z) / 2 / step) * step;
        var minor = new List<(Vector3, Vector3)>();
        var major = new List<(Vector3, Vector3)>();
        for (int k = -lines; k <= lines; k++)
        {
            var into = k % 5 == 0 ? major : minor;
            float x = (float)(cx + k * step), z = (float)(cz + k * step);
            into.Add((new Vector3((float)(cx - half), y, z), new Vector3((float)(cx + half), y, z)));
            into.Add((new Vector3(x, y, (float)(cz - half)), new Vector3(x, y, (float)(cz + half))));
        }
        AddLines(_floor, minor, Color.FromRgb(72, 72, 82), 0.6);
        AddLines(_floor, major, Color.FromRgb(112, 112, 126), 1);
        _floorLines = [.. _floor.Children.OfType<LineGeometryModel3D>().Zip(new[] { minor, major }.Where(l => l.Count > 0))];
        ClipFloor();
    }

    // the floor's lines as laid out, each kept with what draws it, to be cut to what's in front of the camera
    private List<(LineGeometryModel3D Model, List<(Vector3 A, Vector3 B)> Lines)> _floorLines = [];

    /// <summary>
    /// Cuts the floor's lines off just in front of the camera. A line drawn with an end behind the camera is put in the
    /// wrong place, and wildly so as the camera moves the least bit: a camera low over a wide grid (a cutscene's) saw
    /// the lines running towards it jump about every frame.
    /// </summary>
    private void ClipFloor()
    {
        if (_floorLines.Count == 0) return;
        var look = _camera.LookDirection;
        look.Normalize();
        var (eye, forward) = (new Vector3((float)_camera.Position.X, (float)_camera.Position.Y, (float)_camera.Position.Z), new Vector3((float)look.X, (float)look.Y, (float)look.Z));
        // a little past the near plane, so what's kept is drawn
        float near = (float)_camera.NearPlaneDistance * 1.5f;
        foreach (var (model, lines) in _floorLines)
        {
            var positions = new Vector3Collection(lines.Count * 2);
            var indices = new IntCollection(lines.Count * 2);
            foreach (var (a, b) in lines)
            {
                float da = Vector3.Dot(a - eye, forward), db = Vector3.Dot(b - eye, forward);
                if (da < near && db < near) continue;
                var (from, to) = (a, b);
                if (da < near) from = a + (b - a) * ((near - da) / (db - da));
                else if (db < near) to = a + (b - a) * ((near - da) / (db - da));
                indices.Add(positions.Count);
                positions.Add(from);
                indices.Add(positions.Count);
                positions.Add(to);
            }
            model.Geometry = new HxLines { Positions = positions, Indices = indices };
        }
    }

    /// <summary>A chunk's vertex colours, blended across each triangle from its corners; white for a chunk without them.</summary>
    private static Color4Collection VertexColorsOf(MeshChunk chunk)
    {
        var colors = new Color4Collection(chunk.Positions.Length);
        for (int v = 0; v < chunk.Positions.Length; v++)
            colors.Add(v * 3 + 2 < chunk.Colors.Length
                ? new Color4(chunk.Colors[v * 3] / 255f, chunk.Colors[v * 3 + 1] / 255f, chunk.Colors[v * 3 + 2] / 255f, 1)
                : new Color4(1, 1, 1, 1));
        return colors;
    }

    private static readonly Material VertexColorMaterial = new PhongMaterial
    {
        DiffuseColor = new Color4(1, 1, 1, 1),
        AmbientColor = new Color4(0, 0, 0, 1),
        VertexColorBlendingFactor = 1,
    };

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshChunk, HxMesh> Geometries = [];

    /// <summary>A chunk as the view wants it, made once: Y up instead of Z, and triangles wound the other way round.</summary>
    private static HxMesh Geometry(MeshChunk chunk) => Geometries.GetValue(chunk, c =>
    {
        var positions = new Vector3Collection(c.Positions.Length);
        var normals = new Vector3Collection(c.Positions.Length);
        var uvs = new Vector2Collection(c.Positions.Length);
        foreach (var p in c.Positions) positions.Add(ToView(p));
        for (int v = 0; v < c.Positions.Length; v++) normals.Add(v < c.Normals.Length ? ToView(c.Normals[v]) : Vector3.UnitY);
        for (int v = 0; v < c.Positions.Length; v++) uvs.Add(v < c.Uvs.Length ? c.Uvs[v] : default);
        var indices = new IntCollection(c.Indices.Length);
        for (int i = 0; i + 2 < c.Indices.Length; i += 3)
        {
            indices.Add(c.Indices[i]);
            indices.Add(c.Indices[i + 2]);
            indices.Add(c.Indices[i + 1]);
        }
        return new HxMesh { Positions = positions, Normals = normals, TextureCoordinates = uvs, TriangleIndices = indices };
    });

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MeshChunk, Surface> MadeSurfaces = [];

    /// <summary>A chunk's surface as it was made, with its tangents, worked out once.</summary>
    private static Surface MadeSurface(MeshChunk chunk) => MadeSurfaces.GetValue(chunk, c =>
    {
        var normals = new Vector3[c.Positions.Length];
        for (int v = 0; v < normals.Length; v++) normals[v] = v < c.Normals.Length ? c.Normals[v] : Vector3.UnitZ;
        return Surface.Of(c, Geometry(c).Positions!, normals);
    });

    /// <summary>
    /// A material's look: its picture and what the game's material says beside it (normal map, masks, detail maps, its
    /// numbers), drawn by the view's own shader (see <see cref="W3Shading"/>); plain grey without a picture; with a blue
    /// glow when it's highlighted.
    /// </summary>
    private Material MaterialFor(int material, bool highlighted)
    {
        int key = highlighted ? material | int.MinValue : material;
        if (_materials.TryGetValue(key, out var known)) return known;
        var picture = _textured ? _textures.GetValueOrDefault(material) : null;
        var look = _textured ? _looks.GetValueOrDefault(material) : null;
        var made = new PhongMaterial(new W3MaterialCore { Look = look })
        {
            DiffuseColor = picture is null ? new Color4(0.67f, 0.67f, 0.67f, 1) : new Color4(1, 1, 1, 1),
            AmbientColor = new Color4(0, 0, 0, 1),
            RenderShadowMap = true,
            DiffuseMap = picture is null ? null : GpuTextures.Of(picture),
            // Helix's own normal map slot too, so it hands the shader the tangents
            NormalMap = look?.Normal is null ? null : GpuTextures.Of(look.Normal),
            EmissiveColor = highlighted ? new Color4(0.12f, 0.24f, 0.4f, 1) : new Color4(0, 0, 0, 0),
        };
        return _materials[key] = made;
    }

    // ---------------------------------------------------------------- camera

    private (Point3D Min, Point3D Max) Bounds()
    {
        if (_parts.Count == 0) return (new Point3D(-1, -1, -1), new Point3D(1, 1, 1));
        var chunks = _parts.SelectMany((part, i) => DrawnAt(i).Where(c => Shown(i, c) && c.Positions.Length > 0).Select(c => (Part: i, Chunk: c, part.Place))).ToList();
        if (chunks.Count == 0) chunks = _parts.SelectMany((part, i) => part.Mesh.Chunks.Where(c => c.Positions.Length > 0).Select(c => (Part: i, Chunk: c, part.Place))).ToList();
        if (chunks.Count == 0) return (new Point3D(-1, -1, -1), new Point3D(1, 1, 1));
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        // where each chunk is in the pose shown, when it's posed, as a cutscene's actors stand away from where they were made
        foreach (var (part, chunk, place) in chunks)
            foreach (var p in _posedPositions.TryGetValue((part, chunk), out var posed) ? posed : chunk.Positions)
            {
                var at = place.IsIdentity ? p : Vector3.Transform(p, place);
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
        var a = ToViewPoint(min);
        var b = ToViewPoint(max);
        return (new Point3D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Point3D(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
    }

    private double Radius()
    {
        var (min, max) = Bounds();
        return Math.Max(0.01, (max - min).Length / 2);
    }

    /// <summary>The view's width over its height.</summary>
    private double Aspect => ActualWidth > 0 && ActualHeight > 0 ? ActualWidth / ActualHeight : 1;

    /// <summary>An angle across the width as the angle up the height, for the view's shape (Helix's is up the height).</summary>
    private double Upright(double across) => 2 * Math.Atan(Math.Tan(across * Math.PI / 360) / Aspect) * 180 / Math.PI;

    /// <summary>Points the camera at the middle of what's shown, from far enough to see all of it.</summary>
    public void FitToView()
    {
        var (min, max) = Bounds();
        _target = min + (max - min) / 2;
        double radius = Math.Max(0.01, (max - min).Length / 2);
        // far enough to fit it in the narrower of the view's two angles: across the width, or the height when it's wider than tall
        double across = FieldOfView, high = Upright(across);
        _distance = radius / Math.Sin((_fieldOfView is null ? across : Math.Min(across, high)) * Math.PI / 360) * 1.1;
        _camera.NearPlaneDistance = radius * 0.01;
        _camera.FarPlaneDistance = radius * 200;
        UpdateCamera();
    }

    /// <summary>Points the camera at the middle of a box in the game's Z-up space, from far enough to see all of it.</summary>
    public void FitTo(Vector3 min, Vector3 max)
    {
        var (a, b) = (ToViewPoint(min), ToViewPoint(max));
        var (low, high) = (new Point3D(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)), new Point3D(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));
        _target = low + (high - low) / 2;
        double radius = Math.Max(0.5, (high - low).Length / 2);
        // back far enough that every corner of the box is inside the picture as the camera looks now: a sphere round the
        // box would sit much further back in a wide, short view
        double across = Math.Tan(FieldOfView * Math.PI / 360), up = across / Aspect;
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

    /// <summary>Turns the camera round what it looks at by some degrees, as a turntable turns (a material's preview spinning).</summary>
    public void Orbit(double degrees)
    {
        _yaw = (_yaw + degrees) % 360;
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
    public void LookThrough(Vector3 eye, Vector3 forward, Vector3 up, double fieldOfView, double? nearPlane = null)
    {
        _lookingThrough = (ToViewPoint(eye), new Vector3D(forward.X, forward.Z, -forward.Y), new Vector3D(up.X, up.Z, -up.Y), fieldOfView);
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
    /// Lights of the scene's own to add to the view (a scene's campfire or key light), in place of the last ones, given
    /// as WPF's lights in the view's space. While there are any, the light from behind the camera is dimmed, so they show.
    /// </summary>
    public void SetLights(IReadOnlyList<Media3D.Light> lights)
    {
        if (lights.Count == 0 && _lights.Children.Count == 0) return;
        _lights.Children.Clear();
        foreach (var light in lights)
            if (LightOf(light) is { } made) _lights.Children.Add(made);
        _headlight.Color = Warm(lights.Count > 0 ? 120 : 200);
    }

    /// <summary>A WPF light as the view's: a point or spot light with its reach and fall-off, a directional or ambient one.</summary>
    private static Light3D? LightOf(Media3D.Light light) => light switch
    {
        Media3D.SpotLight spot => new SpotLight3D
        {
            Color = spot.Color,
            Position = spot.Position,
            Direction = spot.Direction,
            Range = spot.Range,
            Attenuation = new Vector3D(spot.ConstantAttenuation, spot.LinearAttenuation, spot.QuadraticAttenuation),
            OuterAngle = spot.OuterConeAngle,
            InnerAngle = spot.InnerConeAngle,
        },
        Media3D.PointLight point => new PointLight3D
        {
            Color = point.Color,
            Position = point.Position,
            Range = point.Range,
            Attenuation = new Vector3D(point.ConstantAttenuation, point.LinearAttenuation, point.QuadraticAttenuation),
        },
        Media3D.DirectionalLight directional => new DirectionalLight3D { Color = directional.Color, Direction = directional.Direction },
        Media3D.AmbientLight ambient => new AmbientLight3D { Color = ambient.Color },
        _ => null,
    };

    /// <summary>The light, from behind the camera and a little above and to the side, turned round by <see cref="LightAngle"/>.</summary>
    private Vector3D LightFrom(Vector3D look)
    {
        var direction = look + new Vector3D(0.2, -0.3, 0);
        double a = _lightAngle * Math.PI / 180;
        return new Vector3D(direction.X * Math.Cos(a) - direction.Z * Math.Sin(a), direction.Y, direction.X * Math.Sin(a) + direction.Z * Math.Cos(a));
    }

    private void UpdateCamera()
    {
        // the meshes at the LODs the camera's distance gives, switched before the frame is drawn
        if (_autoLod && PickLods() is { Count: > 0 } changed) SwitchLods(changed);
        if (_lookingThrough is { } through)
        {
            _camera.Position = through.Eye;
            _camera.LookDirection = through.Forward;
            _camera.UpDirection = through.Up;
            // the game's angle is up the height, as the view's is
            _camera.FieldOfView = Math.Min(170, through.FieldOfView);
            _headlight.Direction = LightFrom(through.Forward);
            ClipFloor();
            return;
        }
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        // the game's front is -Y, which is +Z here
        var direction = new Vector3D(-Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
        _camera.Position = _target - direction * _distance;
        _camera.LookDirection = direction;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.FieldOfView = Upright(FieldOfView);
        _headlight.Direction = LightFrom(direction);
        ClipFloor();
    }

    private readonly System.Windows.Threading.DispatcherTimer _resizeSettled;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateCamera();
        // a view that was hidden (no size yet) takes its size at once
        if (double.IsNaN(_viewport.Width) || _viewport.Width <= 1 || _viewport.Height <= 1)
        {
            SettleSize();
            return;
        }
        // stretched to the new size until the resizing stops for a moment
        _viewport.RenderTransform = new ScaleTransform(sizeInfo.NewSize.Width / _viewport.Width, sizeInfo.NewSize.Height / _viewport.Height);
        _resizeSettled.Stop();
        _resizeSettled.Start();
    }

    /// <summary>Draws the view at the control's size again, once resizing has stopped.</summary>
    private void SettleSize()
    {
        _resizeSettled.Stop();
        _viewport.Width = Math.Max(1, ActualWidth);
        _viewport.Height = Math.Max(1, ActualHeight);
        _viewport.RenderTransform = Transform.Identity;
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
