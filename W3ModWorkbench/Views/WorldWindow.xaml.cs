using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace W3ModWorkbench;

/// <summary>
/// A world (.w2w) as a map from above: its terrain (shaded relief, its colour map or the textures painted), its layers'
/// entities as dots in their groups' colours, its terrain tiles, occlusion grid and simplex areas, and what's under
/// the mouse (where, how high, which textures, which tile). The layers are a tree as REDkit's world editor lists them,
/// each group and layer shown or hidden; a click on the map picks an entity, with its layer and template. REDkit's
/// worlds find their layers by folder; the game's cooked worlds keep the tree inside them.
/// </summary>
public partial class WorldWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private WorldFile? _world;
    private WorldTerrainPictures? _terrain;
    private LayerGroup? _layers;
    private readonly List<LayerNode> _roots = [];
    private readonly Dictionary<LayerInfo, List<MapDot>> _dots = [];
    // what each layer's sector data places (the meshes, decals and lights cooking moved out of its entities)
    private readonly Dictionary<LayerInfo, List<SectorObject>> _sectors = [];
    private readonly Dictionary<LayerInfo, LayerNode> _layerNodes = [];
    private List<string> _areaFiles = [];
    private string? _diskRoot;
    private bool _diskRootFound;
    private readonly CancellationTokenSource _closing = new();
    private int _entities;
    private List<MapShape> _areaShapes = [], _navShapes = [];
    private System.Windows.Media.Imaging.BitmapSource? _navPicture;
    private bool _navLoaded, _navVisible;
    private System.Windows.Media.Imaging.BitmapSource? _gameMap;
    private FoliageLook? _foliage;
    private bool _gameMapByDefault;
    private bool _gameMapLoaded, _foliageLoaded;
    private List<MapMarker>? _pins, _waypoints;

    /// <summary>A group or a layer in the tree, shown on the map or not.</summary>
    private sealed class LayerNode : INotifyPropertyChanged
    {
        private bool _shown = true, _expanded;
        private readonly Action _changed;
        public LayerGroup? Group { get; }
        public LayerInfo? Layer { get; }
        public int Colour { get; }
        public string Title { get; }
        public ObservableCollection<LayerNode> Children { get; } = [];
        public Brush Swatch { get; }
        public string CountText { get => _count; set { _count = value; Changed(nameof(CountText)); } }
        private string _count = "";

        public LayerNode(LayerGroup? group, LayerInfo? layer, int colour, Action changed)
        {
            Group = group;
            Layer = layer;
            Colour = colour;
            _changed = changed;
            Title = group?.Name ?? layer!.ShortName;
            var swatch = new SolidColorBrush(WorldMap.ColorOf(colour));
            swatch.Freeze();
            Swatch = swatch;
        }

        public bool Shown
        {
            get => _shown;
            set
            {
                Set(value);
                _changed();
            }
        }

        /// <summary>Shows or hides it and everything under it, without redrawing.</summary>
        public void Set(bool shown)
        {
            _shown = shown;
            Changed(nameof(Shown));
            foreach (var child in Children) child.Set(shown);
        }

        public bool Expanded { get => _expanded; set { _expanded = value; Changed(nameof(Expanded)); } }

        public IEnumerable<LayerInfo> Layers() => Layer is { } l ? [l] : Children.SelectMany(c => c.Layers());

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A fact about the world for the World tab: what, its value, and a file it links to.</summary>
    private sealed record Fact(string Label, string Value, string? Link)
    {
        public bool HasLink => Link is not null;
    }

    private WorldWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        MakeModBtn.Show(path, origin, readData);
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = L.T("WorldWindow.World2", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        LookBox.ItemsSource = new[] { L.T("WorldWindow.Relief"), L.T("WorldWindow.ColourMap"), L.T("WorldWindow.Textures"), L.T("WorldWindow.GameMap") };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            KeepCamera3D();
            _closing.Cancel();
            GpuMap.Dispose();
            View3D.Dispose();
        };
    }

    public static bool CanShow(string name) => WorldFile.IsWorld(name);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new WorldWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new WorldWindow(file, file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    /// <summary>Whether the world was opened from a file on disk (REDkit's depot, a mod's folder) rather than the game.</summary>
    private bool OnDisk => _origin is not null && File.Exists(_origin) && WorldFile.IsWorld(_origin);

    /// <summary>
    /// A file of the world by its game path: from the project or the game, else, for a world opened from disk, from
    /// the depot it's in (the folder above the world that the path is relative to).
    /// </summary>
    private byte[]? Read(string path)
    {
        if (OnDisk && Disk(path) is { } file) return File.ReadAllBytes(file);
        try { return _locator.ReadFile(path); }
        catch (IOException) { return null; }
    }

    private string? Disk(string path)
    {
        if (!_diskRootFound)
        {
            lock (_closing)
            {
                if (!_diskRootFound)
                {
                    for (var dir = new DirectoryInfo(Path.GetDirectoryName(_origin!)!); dir is not null; dir = dir.Parent)
                        if (File.Exists(Path.Combine(dir.FullName, path)))
                        {
                            _diskRoot = dir.FullName;
                            break;
                        }
                    _diskRootFound = _diskRoot is not null;
                }
            }
        }
        return _diskRoot is { } root && File.Exists(Path.Combine(root, path)) ? Path.Combine(root, path) : null;
    }

    /// <summary>The world's game path: as opened, or, from disk, relative to its depot.</summary>
    private string GamePath => OnDisk && _diskRoot is { } root ? Path.GetRelativePath(root, _origin!) : _path;

    private async Task LoadAsync()
    {
        try
        {
            var read = _readData;
            _world = await Task.Run(() => WorldFile.Read(read(), _path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = L.T("WorldWindow.CouldNotRead", ex.Message);
            return;
        }
        var world = _world;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        FileProperties.Show(PropertyRow.Of(world.World, world.File));
        _ = LoadRedkitAsync();
        if (world.Terrain is { } t)
        {
            Map.SetTileGrid(t.TilesPerEdge, t.Corner, t.Size);
            await Dispatcher.InvokeAsync(() => Map.FitTo(t.Corner, t.Corner + new Vector2(t.Size)), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        Map.SetOcclusion(world.Occlusion);
        var token = _closing.Token;
        try
        {
            // the terrain, the layer tree and the areas, then every layer's entities
            await LoadTerrainAsync(token);
            await LoadLayerTreeAsync(token);
            ShowFacts();
            await LoadEntitiesAsync(token);
            ShowFacts();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadTerrainAsync(CancellationToken token)
    {
        if (_world!.Terrain is not { } terrain)
        {
            LookBox.IsEnabled = false;
            return;
        }
        // a world opened from disk finds its depot by its first tile
        if (OnDisk && terrain.Tiles.Cast<string?>().FirstOrDefault(p => p is not null) is { } first) await Task.Run(() => Disk(first), token);
        int total = terrain.Tiles.Cast<string?>().Count(p => p is not null);
        var progress = new Progress<int>(n => MapProgress.Text = L.T("WorldWindow.ReadingTerrain", n, total));
        _terrain = await Task.Run(() => WorldTerrainPictures.Load(terrain, Read, progress, token), token);
        // the game's own map first, the relief shown while it's read and kept when the game has none
        Map.SetTerrain(_terrain.Picture(TerrainLook.Relief), terrain.Corner, terrain.Size);
        Map.FitWorld();
        _gameMapByDefault = true;
        LookBox.SelectedIndex = 3;
    }

    private async Task LoadLayerTreeAsync(CancellationToken token)
    {
        var world = _world!;
        string folder = GamePath.Contains('\\') ? Path.GetDirectoryName(GamePath)! : world.Folder;
        (_layers, _areaFiles) = await Task.Run(() =>
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var areas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = folder + "\\";
            foreach (string p in _locator.AllPaths(".w2l")) if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) paths.Add(p);
            foreach (string p in _locator.AllPaths(".w3simplex")) if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) areas.Add(p);
            if (OnDisk && _diskRoot is { } root && Directory.Exists(Path.Combine(root, folder)))
            {
                foreach (string f in Directory.EnumerateFiles(Path.Combine(root, folder), "*.w2l", SearchOption.AllDirectories)) paths.Add(Path.GetRelativePath(root, f));
                foreach (string f in Directory.EnumerateFiles(Path.Combine(root, folder), "*.w3simplex", SearchOption.AllDirectories)) areas.Add(Path.GetRelativePath(root, f));
            }
            var tree = world.EmbeddedLayers ?? LayerGroup.FromPaths(folder, paths);
            // layers a DLC adds to the world from its own folders (dlc\bob\levels\prolog_village, REDkit's dlc\bob\data\levels\…),
            // in a DLC group as REDkit's world editor shows them
            string worldName = Path.GetFileName(folder);
            string marker = $"\\levels\\{worldName}\\";
            var dlcLayers = _locator.AllPaths(".w2l").Where(p => p.StartsWith("dlc\\", StringComparison.OrdinalIgnoreCase) && p.Contains(marker, StringComparison.OrdinalIgnoreCase)).ToList();
            if (OnDisk && _diskRoot is { } dlcRoot && Directory.Exists(Path.Combine(dlcRoot, "dlc")))
                foreach (var dlc in Directory.EnumerateDirectories(Path.Combine(dlcRoot, "dlc")))
                    foreach (var levels in new[] { Path.Combine(dlc, "levels", worldName), Path.Combine(dlc, "data", "levels", worldName) })
                        if (Directory.Exists(levels))
                            dlcLayers.AddRange(Directory.EnumerateFiles(levels, "*.w2l", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dlcRoot, f)));
            var known = tree.AllLayers().Select(l => l.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byDlc = dlcLayers.Where(p => !known.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(p => p.Split('\\')[1], StringComparer.OrdinalIgnoreCase).ToList();
            if (byDlc.Count > 0)
            {
                var dlcGroup = new LayerGroup { Name = "DLC", Path = "dlc" };
                foreach (var g in byDlc.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string first = g.First();
                    string dlcFolder = first[..(first.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length - 1)];
                    var one = LayerGroup.FromPaths(dlcFolder, g);
                    var named = new LayerGroup { Name = g.Key, Path = dlcFolder };
                    named.Groups.AddRange(one.Groups);
                    named.Layers.AddRange(one.Layers);
                    dlcGroup.Groups.Add(named);
                }
                tree.Groups.Insert(0, dlcGroup);
            }
            return (tree, areas.Order(StringComparer.OrdinalIgnoreCase).ToList());
        }, token);
        _roots.Clear();
        _layerNodes.Clear();
        int colour = 0;
        LayerNode Node(LayerGroup group, int c)
        {
            var node = new LayerNode(group, null, c, ShowDots);
            foreach (var child in group.Groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)) node.Children.Add(Node(child, c));
            foreach (var layer in group.Layers.OrderBy(l => l.ShortName, StringComparer.OrdinalIgnoreCase))
            {
                var leaf = new LayerNode(null, layer, c, ShowDots);
                _layerNodes[layer] = leaf;
                node.Children.Add(leaf);
            }
            node.CountText = L.T("Count.Layer", node.Layers().Count());
            return node;
        }
        // the DLC group first, as REDkit lists it, then the world's own groups by name
        foreach (var group in _layers.Groups.OrderBy(g => g.Path != "dlc").ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)) _roots.Add(Node(group, colour++));
        foreach (var layer in _layers.Layers.OrderBy(l => l.ShortName, StringComparer.OrdinalIgnoreCase))
        {
            var leaf = new LayerNode(null, layer, colour, ShowDots);
            _layerNodes[layer] = leaf;
            _roots.Add(leaf);
        }
        ShowTree();
        LayersTab.Header = L.T("WorldWindow.Layers2", _layerNodes.Count);
        LayerNote.Text = world.EmbeddedLayers is not null ? L.T("WorldWindow.TreeInWorld") : L.T("WorldWindow.TreeFromFolders");
        AreasBox.ItemsSource = new[] { L.T("WorldWindow.NoAreas") }.Concat(_areaFiles.Select(Path.GetFileNameWithoutExtension)).ToList();
        AreasBox.SelectedIndex = 0;
        AreasBox.IsEnabled = _areaFiles.Count > 0;
    }

    /// <summary>Reads every layer's entities in the background, putting them on the map as they come.</summary>
    private async Task LoadEntitiesAsync(CancellationToken token)
    {
        var layers = _layerNodes.Keys.ToList();
        var groupOf = _layerNodes.ToDictionary(p => p.Key, p => p.Value.Colour);
        int done = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var found = new System.Collections.Concurrent.ConcurrentQueue<(LayerInfo Layer, List<MapDot> Dots, List<SectorObject> Sector)>();
        var reading = Task.Run(() => Parallel.ForEach(layers, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 8) }, layer =>
        {
            try
            {
                if (Read(layer.Path) is { } data)
                {
                    var (entities, sector) = WorldFile.Layer(data);
                    found.Enqueue((layer, entities.Select(e => new MapDot(e, layer, groupOf[layer])).ToList(), sector));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            Interlocked.Increment(ref done);
        }), token);
        while (!reading.IsCompleted)
        {
            await Task.WhenAny(reading, Task.Delay(400, token));
            Take();
            MapProgress.Text = L.T("WorldWindow.ReadingLayers", done, layers.Count);
        }
        await reading;
        Take();
        MapProgress.Visibility = Visibility.Collapsed;
        _layersRead = true;
        // the 3D view, if it's already up, gets the water levels and meshes of the layers read since
        if (View3D.HasTerrain)
        {
            SetWater3D();
            _ = SetProbe3DAsync();
            if (_meshesWaiting) _ = ShowMeshes3DAsync();
        }
        foreach (var node in _roots) Count(node);
        ShowEntities();
        ShowShapes();

        void Take()
        {
            bool any = false;
            while (found.TryDequeue(out var item))
            {
                _dots[item.Layer] = item.Dots;
                if (item.Sector.Count > 0) _sectors[item.Layer] = item.Sector;
                _entities += item.Dots.Count;
                any = true;
            }
            if (any) ShowDots();
        }
    }

    /// <summary>Each group's count of layers and entities, once they're read.</summary>
    private int Count(LayerNode node)
    {
        if (node.Layer is { } layer)
        {
            int n = _dots.TryGetValue(layer, out var d) ? d.Count : 0;
            node.CountText = n.ToString(CultureInfo.CurrentCulture);
            return n;
        }
        int sum = node.Children.Sum(Count);
        node.CountText = L.T("WorldWindow.LayersEntities", L.T("Count.Layer", node.Layers().Count()), L.T("Count.Entity", sum));
        return sum;
    }

    private void ShowDots()
    {
        if (EntitiesCheck.IsChecked != true)
        {
            Map.SetDots([]);
            return;
        }
        Map.SetDots(_dots.Where(p => _layerNodes.TryGetValue(p.Key, out var node) && node.Shown).SelectMany(p => p.Value).ToList());
    }

    private void ShowTree()
    {
        string filter = FilterBox.Text.Trim();
        if (filter.Length == 0)
        {
            LayerTree.ItemsSource = _roots;
            return;
        }
        // the layers whose names or paths have the letters, in a flat list
        LayerTree.ItemsSource = _layerNodes.Values.Where(n => n.Layer!.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Layer!.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The World tab: the terrain, occlusion, layers, and every file the world names, each opened by a click.</summary>
    private void ShowFacts()
    {
        var world = _world!;
        var facts = new List<Fact>();
        if (world.Terrain is { } t)
        {
            facts.Add(new(L.T("WorldWindow.Terrain"), L.T("WorldWindow.TerrainFact", t.Size, t.TilesPerEdge, t.TileSize, t.TileResolution, t.Lowest, t.Highest), null));
            facts.Add(new(L.T("WorldWindow.TerrainCorner"), $"{t.Corner.X:0}, {t.Corner.Y:0}", null));
            if (t.Material is { } m) facts.Add(new(L.T("WorldWindow.TerrainMaterial"), m, m));
            if (_terrain is { } p) facts.Add(new(L.T("WorldWindow.TilesRead"), L.T("WorldWindow.TilesReadFact", p.TilesRead, t.Tiles.Cast<string?>().Count(x => x is not null), p.Side), null));
        }
        else facts.Add(new(L.T("WorldWindow.Terrain"), L.T("WorldWindow.NoTerrain"), null));
        if (world.Occlusion is { } o) facts.Add(new(L.T("WorldWindow.Occlusion"), L.T("WorldWindow.OcclusionFact", o.Side, o.TileSize, o.Tiles.Count), null));
        facts.Add(new(L.T("WorldWindow.Layers"), L.T("WorldWindow.LayersFact", _layerNodes.Count, _roots.Count(r => r.Group is not null), _entities), null));
        foreach (string area in _areaFiles) facts.Add(new(L.T("WorldWindow.Areas"), area, area));
        // every file the world's own properties name, environment and sounds included
        void Links(RedObject o, string prefix)
        {
            foreach (var (name, _, value) in o.Properties)
            {
                if (value is RedHandle h && world.File.PathOf(h) is { Length: > 0 } path) facts.Add(new(prefix + name, path, path));
                else if (value is RedObject inner && inner.Export is null) Links(inner, prefix + name + " › ");
                else if (value is string s && s.Contains('\\') && name.EndsWith("Path", StringComparison.OrdinalIgnoreCase)) facts.Add(new(prefix + name, s, null));
            }
        }
        Links(world.World, "");
        FactsList.ItemsSource = facts;
        // a world opened from disk is its own source: say so, not its path twice
        InfoText.Text = L.T("WorldWindow.Info", _path, string.Equals(_path, _source, StringComparison.OrdinalIgnoreCase) ? L.T("WorldWindow.OnDisk") : _source, world.File.Version, world.Terrain is { } tt ? L.T("WorldWindow.InfoTerrain", tt.Size, tt.TilesPerEdge) : L.T("WorldWindow.NoTerrain"),
            L.T("Count.Layer", _layerNodes.Count), L.T("Count.Entity", _entities));
    }

    /// <summary>
    /// Every entity read, as REDkit's Entities Browser lists them, those whose name, class, layer or template has the
    /// filter's letters (at most a few thousand, as a list of tens of thousands helps no one).
    /// </summary>
    private void ShowEntities()
    {
        string filter = EntityFilterBox.Text.Trim();
        var all = _dots.Values.SelectMany(d => d);
        var shown = (filter.Length == 0 ? all : all.Where(d => d.Entity.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || d.Entity.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase) || d.Layer.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (d.Entity.Template?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase).Take(5000).ToList();
        EntityGrid.ItemsSource = shown;
        EntitiesTab.Header = L.T("WorldWindow.Entities2", _entities);
    }

    private void EntityFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        EntityFilterPlaceholder.Visibility = EntityFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowEntities();
    }

    private void EntityGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntityGrid.SelectedItem is not MapDot dot) return;
        Map.Picked = dot;
        Map_DotPicked(Map, dot);
        var at = new Vector2(dot.Entity.Position.X, dot.Entity.Position.Y);
        Map.FitTo(at - new Vector2(60), at + new Vector2(60));
    }

    private void LookBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_terrain is not { } p || LookBox.SelectedIndex < 0) return;
        if (LookBox.SelectedIndex == 3)
        {
            _ = ShowGameMapAsync(p.Terrain);
            return;
        }
        _gameMapByDefault = false;
        var look = (TerrainLook)LookBox.SelectedIndex;
        Map.SetOverlay("gamemap", null, default, 0);
        Map.SetTerrain(p.Picture(look), p.Terrain.Corner, p.Terrain.Size);
    }

    private async void AreasBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AreasBox.SelectedIndex <= 0 || AreasBox.SelectedIndex > _areaFiles.Count)
        {
            _areaShapes = [];
            ShowShapes();
            return;
        }
        string path = _areaFiles[AreasBox.SelectedIndex - 1];
        var terrain = _world?.Terrain;
        try
        {
            var shapes = await Task.Run(() =>
            {
                var tree = SimplexTreeFile.Read(Read(path) ?? throw new FileNotFoundException(path));
                var cells = terrain is { } t ? tree.Cells(t.Corner, t.Corner + new Vector2(t.Size)) : tree.Cells(tree.Reach);
                return cells.Where(c => c.Area >= 0).Select(c => new MapShape(c.Outline, SimplexMap.ColorOf(c.Area), true, true)).ToList();
            });
            _areaShapes = shapes;
            ShowShapes();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            await ShowMessage(L.T("Common.CouldNotRead"), $"{path}\n\n{ex.Message}", MessageKind.Error);
        }
    }

    /// <summary>
    /// The world's navigation, read the first time it's asked for: its terrain tiles' cells (blocked ground red, ground a
    /// navmesh takes over blue) as a picture over the map, and each navmesh's border.
    /// </summary>
    private async void NavigationCheck_Click(object sender, RoutedEventArgs e)
    {
        if (NavigationCheck.IsChecked != true)
        {
            Map.SetOverlay("navigation", null, default, 0);
            _navVisible = false;
            ShowShapes();
            return;
        }
        if (!_navLoaded && _world?.Terrain is { } terrain)
        {
            _navLoaded = true;
            string folder = (GamePath.Contains('\\') ? Path.GetDirectoryName(GamePath)! : _world.Folder) + @"\navi_cooked";
            MapProgress.Visibility = Visibility.Visible;
            MapProgress.Text = L.T("WorldWindow.ReadingNavigation");
            (_navPicture, _navShapes) = await Task.Run(() => LoadNavigation(terrain, folder));
            MapProgress.Visibility = Visibility.Collapsed;
            NavigationCheck.ToolTip = L.T("WorldWindow.NavigationRead", _navShapes.Count);
        }
        if (_world?.Terrain is { } t) Map.SetOverlay("navigation", _navPicture, t.Corner, t.Size);
        _navVisible = true;
        ShowShapes();
    }

    private (System.Windows.Media.Imaging.BitmapSource? Picture, List<MapShape> Shapes) LoadNavigation(WorldTerrain terrain, string folder)
    {
        int n = terrain.TilesPerEdge, cells = 256, step = Math.Max(1, cells * n / 2048), side = cells * n / step;
        var pixels = new byte[side * side * 4];
        bool any = false;
        Parallel.For(0, n * n, i =>
        {
            int col = i % n, row = i / n;
            try
            {
                if (Read($@"{folder}\terrain_{col}x{row}.navtile") is not { } data) return;
                var tile = NavTileFile.Read(data);
                int per = tile.Resolution / step;
                for (int y = 0; y < per; y++)
                    for (int x = 0; x < per; x++)
                    {
                        var cell = tile.Cell(x * step, y * step);
                        if (cell == NavCell.Walkable) continue;
                        int o = (((n - 1 - row) * per + (per - 1 - y)) * side + col * per + x) * 4;
                        (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) = cell == NavCell.Blocked ? ((byte)40, (byte)40, (byte)150, (byte)110) : ((byte)200, (byte)120, (byte)40, (byte)110);
                    }
                any = true;
            }
            catch (InvalidDataException) { }
        });
        System.Windows.Media.Imaging.BitmapSource? picture = null;
        if (any)
        {
            picture = System.Windows.Media.Imaging.BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
            picture.Freeze();
        }
        // each navmesh's border: the edges with no neighbour, or the top bit set
        var shapes = new List<MapShape>();
        var meshes = _locator.AllPaths(".navmesh").Where(p => p.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (OnDisk && _diskRoot is { } root && Directory.Exists(Path.Combine(root, folder)))
            meshes.AddRange(Directory.EnumerateFiles(Path.Combine(root, folder), "*.navmesh").Select(f => Path.GetRelativePath(root, f)));
        var edge = Color.FromRgb(0x5A, 0xC8, 0xFA);
        foreach (string path in meshes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Read(path) is not { } data) continue;
                var mesh = NavMeshFile.Read(data);
                for (int i = 0; i < mesh.Triangles.Count; i++)
                {
                    var (a, b, c) = mesh.Triangles[i];
                    var (na, nb, nc) = mesh.Neighbours[i];
                    int[] corners = [a, b, c, a];
                    int[] across = [na, nb, nc];
                    for (int k = 0; k < 3; k++)
                        if (across[k] == 0xFFFF || (across[k] & 0x8000) != 0)
                        {
                            var p0 = mesh.Vertices[corners[k]];
                            var p1 = mesh.Vertices[corners[k + 1]];
                            shapes.Add(new MapShape([new Vector2(p0.X, p0.Y), new Vector2(p1.X, p1.Y)], edge, false, false));
                        }
                }
            }
            catch (InvalidDataException) { }
        }
        return (picture, shapes);
    }

    /// <summary>Every outline drawn over the map: a simplex tree's areas, the navmeshes' borders, the area components'.</summary>
    private void ShowShapes()
    {
        var shapes = new List<MapShape>(_areaShapes);
        if (_navVisible) shapes.AddRange(_navShapes);
        if (OutlinesCheck.IsChecked == true)
            foreach (var dot in _dots.Where(p => _layerNodes.TryGetValue(p.Key, out var node) && node.Shown).SelectMany(p => p.Value))
                foreach (var c in dot.Entity.Components)
                    if (c.Outline is { } outline) shapes.Add(new MapShape(outline, OutlineColor(c.ClassName), true, dot == Map.Picked, dot == Map.Picked));
        Map.SetShapes(shapes);
    }

    /// <summary>An area component's colour, by what it's for.</summary>
    private static Color OutlineColor(string className) => className switch
    {
        _ when className.Contains("Trigger", StringComparison.Ordinal) => Color.FromRgb(0xF2, 0xC9, 0x4C),
        _ when className.Contains("Sound", StringComparison.Ordinal) => Color.FromRgb(0x4C, 0xC0, 0xE0),
        _ when className.Contains("Navmesh", StringComparison.Ordinal) => Color.FromRgb(0x5A, 0xC8, 0xFA),
        _ when className.Contains("Environment", StringComparison.Ordinal) => Color.FromRgb(0xB0, 0x7C, 0xF2),
        _ when className.Contains("Scene", StringComparison.Ordinal) => Color.FromRgb(0xF2, 0x7A, 0xB0),
        _ when className.Contains("Denied", StringComparison.Ordinal) => Color.FromRgb(0xE5, 0x48, 0x48),
        _ when className.Contains("Water", StringComparison.Ordinal) => Color.FromRgb(0x3F, 0x8F, 0xE0),
        _ => Color.FromRgb(0xC8, 0xC8, 0xC8),
    };

    /// <summary>
    /// The game's own minimap under the entities, read the first time it's asked for: a picture for each terrain tile
    /// (tileCOLUMNxROW, rows from the south, in the world's minimaps folder), from the game's texture cache, or REDkit's
    /// pictures of them.
    /// </summary>
    private async Task ShowGameMapAsync(WorldTerrain terrain)
    {
        if (!_gameMapLoaded)
        {
            _gameMapLoaded = true;
            string folder = (_world!.World["minimapsPath"] as string ?? @"gameplay\gui_new\minimaps\" + Path.GetFileName(_world.Folder)).TrimEnd('\\');
            MapProgress.Visibility = Visibility.Visible;
            MapProgress.Text = L.T("WorldWindow.ReadingGameMap");
            _gameMap = await Task.Run(() =>
            {
                int n = terrain.TilesPerEdge, size = Math.Clamp(4096 / n, 32, 256), side = n * size;
                var pixels = new byte[side * side * 4];
                int found = 0;
                Parallel.For(0, n * n, i =>
                {
                    int col = i % n, row = i / n;
                    string path = $@"{folder}\tile{col}x{row}.jpg";
                    var picture = MeshTextures.Load(_locator, path, size) ?? RedkitPicture(path);
                    if (picture is null || picture.PixelWidth == 0) return;
                    var scaled = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                        new System.Windows.Media.Imaging.TransformedBitmap(picture, new ScaleTransform((double)size / picture.PixelWidth, (double)size / picture.PixelHeight)),
                        PixelFormats.Bgra32, null, 0);
                    var tile = new byte[size * size * 4];
                    scaled.CopyPixels(new Int32Rect(0, 0, Math.Min(size, scaled.PixelWidth), Math.Min(size, scaled.PixelHeight)), tile, size * 4, 0);
                    for (int y = 0; y < size; y++)
                        Array.Copy(tile, y * size * 4, pixels, (((n - 1 - row) * size + y) * side + col * size) * 4, size * 4);
                    Interlocked.Increment(ref found);
                });
                if (found == 0) return null;
                // the game's map covers only the walkable middle: the rest painted from the terrain to match it
                if (_terrain is { } painted)
                {
                    string cells = GamePath.Contains('\\') ? Path.GetDirectoryName(GamePath)! : _world!.Folder;
                    var trees = (_foliage ?? LoadFoliage(terrain, cells)).Trees.Select(p => (p.At, p.Reach)).ToList();
                    GameMapFill.Complete(pixels, side, painted, TerrainArray("diffuse"), trees);
                }
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
                bitmap.Freeze();
                return bitmap;
            });
            MapProgress.Visibility = Visibility.Collapsed;
        }
        if (LookBox.SelectedIndex != 3) return;
        if (_gameMap is null)
        {
            // only said when asked for: a world opened without one just stays on its relief
            if (!_gameMapByDefault) await ShowMessage(null, L.T("WorldWindow.NoGameMap"), MessageKind.Info);
            _gameMapByDefault = false;
            LookBox.SelectedIndex = 0;
            return;
        }
        _gameMapByDefault = false;
        // over the relief, which shows where the game has no minimap (past the playable middle)
        if (_terrain is { } pictures) Map.SetTerrain(pictures.Picture(TerrainLook.Relief), terrain.Corner, terrain.Size);
        Map.SetOverlay("gamemap", _gameMap, terrain.Corner, terrain.Size, under: true);
    }

    /// <summary>REDkit's own picture of a game path (its depot keeps the hub map and minimap as .jpg files), or null.</summary>
    private static System.Windows.Media.Imaging.BitmapSource? RedkitPicture(string path)
    {
        if (Core.Projects.FileSafety.RedkitFile(path) is not { } file) return null;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException) { return null; }
    }

    /// <summary>
    /// The world's foliage, read the first time it's asked for: from afar, every tree a disc and the grass a wash, each in
    /// its own colour; zoomed in, every plant drawn from above from its SpeedTree file, turned and sized as placed.
    /// </summary>
    private async void FoliageCheck_Click(object sender, RoutedEventArgs e)
    {
        var terrain = _world?.Terrain;
        if (FoliageCheck.IsChecked != true || terrain is null)
        {
            Map.SetOverlay("grass", null, default, 0);
            Map.SetOverlay("trees", null, default, 0);
            Map.SetPlants(null, null);
            return;
        }
        if (!_foliageLoaded)
        {
            _foliageLoaded = true;
            string folder = GamePath.Contains('\\') ? Path.GetDirectoryName(GamePath)! : _world!.Folder;
            MapProgress.Visibility = Visibility.Visible;
            MapProgress.Text = L.T("WorldWindow.ReadingFoliage");
            _foliage = await Task.Run(() => LoadFoliage(terrain, folder));
            MapProgress.Visibility = Visibility.Collapsed;
            FoliageCheck.ToolTip = L.T("WorldWindow.FoliageRead", _foliage.Plants, _foliage.Kinds, _foliage.Drawn);
        }
        if (_foliage is not { } look) return;
        // each picture from afar gives way to the plants' own as the map zooms in past them
        Map.SetOverlay("grass", look.GrassPicture, terrain.Corner, terrain.Size, WorldMap.GrassScale);
        Map.SetOverlay("trees", look.TreesPicture, terrain.Corner, terrain.Size, WorldMap.TreeScale);
        Map.SetPlants(look.Trees, look.Grass);
    }

    /// <summary>The trees, bushes and grass in 3D, read off the UI thread while the ground is already shown.</summary>
    private async Task ShowPlants3DAsync()
    {
        string folder = GamePath.Contains('\\') ? Path.GetDirectoryName(GamePath)! : _world!.Folder;
        MapProgress.Visibility = Visibility.Visible;
        MapProgress.Text = L.T("WorldWindow.ReadingPlants3D");
        var world = _world!;
        var (kinds, grass) = await Task.Run(() =>
        {
            var grassSetup = LoadGrass(world, out var brushPlants);
            return (LoadPlants3D(folder, brushPlants), grassSetup);
        });
        MapProgress.Visibility = Visibility.Collapsed;
        View3D.SetPlants(kinds);
        View3D.SetGrass(grass);
        View3D.TranslucencyCurve = TranslucencyOf(world);
        View3D.Gtao = GtaoOf(world);
        await ShowMeshes3DAsync();
    }

    /// <summary>The water shapes the layers read so far have, for the 3D view: each sets the water's level inside it.</summary>
    private void SetWater3D() => View3D.SetWater(_dots.Values.Where(_ => _layers3D).SelectMany(d => d).SelectMany(d => d.Entity.Components)
        .Where(c => c is { ClassName: "CWaterComponent", Outline: { Count: >= 3 } }).Select(c => (c.Outline!, c.Height)), _waterSurfaces);

    /// <summary>
    /// The world's global environment probe (its contribution -1, else the one with the largest area) for the 3D view's
    /// ambient: its cooked albedo, normals and depth from the texture cache, where it was taken and its ambient colour and
    /// intensity. A world without one keeps the view's own capture.
    /// </summary>
    private async Task SetProbe3DAsync()
    {
        if (!_probes3D) return;
        var probes = _dots.Values.SelectMany(d => d).SelectMany(d => d.Entity.Components).Where(c => c.Probe is not null).ToList();
        var chosen = probes.FirstOrDefault(c => c.Probe!.Contribution == -1)
            ?? probes.OrderByDescending(c => c.Place.M11 * c.Place.M22 * c.Place.M33).FirstOrDefault();
        if (chosen?.Probe is not { } probe) return;
        var locator = _locator;
        var faces = await Task.Run(() =>
        {
            byte[]? Pixels(uint hash)
            {
                if (locator.FindTexture(hash) is not { Archive: Core.Textures.TextureCacheArchive cache } entry) return null;
                using var stream = new FileStream(cache.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Core.Textures.TextureCacheArchive.ReadPixels(stream, entry.Info);
            }
            try { return (Albedo: Pixels(probe.Hashes[0]), Normals: Pixels(probe.Hashes[1]), Depth: Pixels(probe.Hashes[3])); }
            catch (Exception e) when (e is IOException or InvalidDataException) { return (null, null, null); }
        });
        if (faces is { Albedo.Length: 128 * 128 * 6 * 4, Normals.Length: 128 * 128 * 6 * 4, Depth.Length: 128 * 128 * 6 * 2 })
            View3D.SetCookedProbe(probe.Origin, probe.AmbientColour, probe.AmbientIntensity, faces.Albedo, faces.Normals, faces.Depth);
    }

    // whether the 3D view's camera has been put back where it was left
    private bool _cameraRestored;

    private bool RestoreCamera3D(string kept)
    {
        var parts = kept.Split('|');
        if (!View3D.PasteCamera(kept)) return false;
        if (parts.Length > 2 && float.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float speed)) View3D.Speed = speed;
        return true;
    }

    /// <summary>The 3D camera kept for the world, to come back to (only once it's been shown).</summary>
    private void KeepCamera3D()
    {
        if (!_cameraRestored) return;
        App.Settings.WorldCameras[_path.ToLowerInvariant()] = View3D.CameraText + "|" + View3D.Speed.ToString("0.###", CultureInfo.InvariantCulture);
        App.Settings.Save();
    }

    // whether the 3D view takes what the layers place (meshes, paths, water shapes, the probes): off, it's the world's
    // terrain, foliage, water and sky alone, as REDkit shows a world with its layers unloaded
    private bool _layers3D = true;

    // whether the 3D view takes its ambient from the world's own environment probe (in the layers too, kept apart from
    // the rest, as REDkit can load its env_global_pbr group alone)
    private bool _probes3D = true;

    // the water meshes' triangles in the world (rivers drawn with the game's water shaders), for the water's level
    private List<(Vector3 A, Vector3 B, Vector3 C)> _waterSurfaces = [];

    // whether every layer's been read; whether the 3D view's meshes wait for that
    private bool _layersRead, _meshesWaiting;

    /// <summary>
    /// The layers' meshes in 3D: every mesh the layers' components draw (those read so far), read with each material's
    /// colour picture and look as the mesh viewer reads them, and where each copy stands.
    /// </summary>
    private async Task ShowMeshes3DAsync()
    {
        // once every layer's been read, so none are missing
        _meshesWaiting = !_layersRead;
        if (_meshesWaiting || !_layers3D) return;
        var locator = _locator;
        MapProgress.Visibility = Visibility.Visible;
        MapProgress.Text = L.T("WorldWindow.ReadingMeshes3D");
        var entities = _dots.Values.SelectMany(d => d).Select(d => d.Entity).ToList();
        static bool IsProxy(string mesh) => mesh.Contains(@"	extures_entity_proxy", StringComparison.OrdinalIgnoreCase);
        // meshes, and cloth (banners, washing, awnings) in the shape it has before it's simulated
        // (a character's cloth, a monster's rags, needs its skeleton: left out)
        // and what breaks (fences, crates), whole as it stands before
        static bool IsMesh(LayerComponent c) => c.Resource is { } r && (r.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase)
                || Core.Meshes.ClothFile.IsCloth(r) && !r.StartsWith(@"characters\", StringComparison.OrdinalIgnoreCase) || r.EndsWith(".reddest", StringComparison.OrdinalIgnoreCase))
            && LayerComponent.Drawable(c.DrawableFlags).Visible;
        // an entity made from a template whose meshes the layer doesn't stream itself (a windmill, a fence, a door): the
        // template's meshes (its default appearance's, those not skinned to a skeleton), each placed on the entity; its
        // stand-in for far off (an entity proxy) then left out
        var fromTemplates = await Task.Run(() =>
        {
            var byTemplate = new Dictionary<string, List<(string Mesh, Matrix4x4 Placed, bool Shadow, (ColorShift?, ColorShift?)? Coloring)>?>(StringComparer.OrdinalIgnoreCase);
            var found = new List<(string Mesh, Matrix4x4 Place, float AutoHide, bool Shadow, (ColorShift?, ColorShift?)? Coloring)>();
            var replaced = new HashSet<LayerEntity>();
            foreach (var entity in entities)
            {
                if (entity.Template is not { } template || entity.Components.Any(c => IsMesh(c) && !IsProxy(c.Resource!))) continue;
                if (!byTemplate.TryGetValue(template, out var meshes))
                {
                    meshes = null;
                    try
                    {
                        if (locator.ReadFile(template) is { } data)
                        {
                            var file = Core.Entities.EntityFile.Read(data);
                            file.FindOrigins(locator.ReadFile);
                            // each mesh with the colour entry its appearance gives its component, as the game dyes it
                            meshes = [.. Core.Entities.EntityFile.MeshesOf(file.Look(file.DefaultAppearance, locator.ReadFile))
                                .Select(m => (m.Mesh, m.Placed, m.Component, Drawn: LayerComponent.Drawable(m.Component.Object["drawableFlags"]?.ToString())))
                                .Where(m => !m.Component.Skinned && m.Drawn.Visible)
                                .Select(m => (m.Mesh, m.Placed, m.Drawn.CastsShadow, LookTextures.Coloring(file, file.DefaultAppearance?.Name, m.Component.Name)))];
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException) { }
                    byTemplate[template] = meshes;
                }
                if (meshes is not { Count: > 0 }) continue;
                var place = entity.Object["transform"] is byte[] raw ? Core.Entities.EntityFile.Transform(raw) : Matrix4x4.CreateTranslation(entity.Position);
                foreach (var (mesh, placed, shadow, coloring) in meshes) found.Add((mesh, placed * place, 0, shadow, coloring));
                replaced.Add(entity);
            }
            return (found, replaced);
        });
        var fromEntities = entities.SelectMany(e => e.Components.Where(c => IsMesh(c) && !(fromTemplates.replaced.Contains(e) && IsProxy(c.Resource!)))
                .Select(c => (Mesh: c.Resource!, c.Place, c.AutoHide, Shadow: LayerComponent.Drawable(c.DrawableFlags).CastsShadow, Coloring: ((ColorShift?, ColorShift?)?)null))).ToList();
        // the layers' sector data: most of a cooked world's buildings, walls and props, named by their paths' hashes;
        // those an entity also draws where it stands left to the entity
        var sectors = _sectors.Values.SelectMany(s => s).Where(o => o.Kind is SectorObjectKind.Mesh or SectorObjectKind.RigidBody && o.Visible && o.Resource != 0).ToList();
        var fromSectors = await Task.Run(() =>
        {
            var wanted = sectors.Select(o => o.Resource).ToHashSet();
            var paths = new Dictionary<ulong, string>();
            foreach (var extension in new[] { ".w2mesh", ".reddest", ".redcloth" })
                foreach (var path in locator.AllPaths(extension))
                    if (SectorData.PathHash(path) is var hash && wanted.Contains(hash)) paths.TryAdd(hash, path);
            var already = fromEntities.Select(e => (e.Mesh.ToLowerInvariant(), Round(e.Place.Translation))).ToHashSet();
            static (int, int, int) Round(Vector3 v) => ((int)MathF.Round(v.X * 20), (int)MathF.Round(v.Y * 20), (int)MathF.Round(v.Z * 20));
            return sectors.Where(o => paths.ContainsKey(o.Resource) && !already.Contains((paths[o.Resource].ToLowerInvariant(), Round(o.Place.Translation))))
                .Select(o => (Mesh: paths[o.Resource], o.Place, AutoHide: 0f, Shadow: o.CastsShadow, Coloring: ((ColorShift?, ColorShift?)?)null)).ToList();
        });
        var placed = fromEntities.Concat(fromSectors)
            .Concat(fromTemplates.found)
            .GroupBy(c => (Mesh: c.Mesh.ToLowerInvariant(), c.Shadow)).ToList();
        if (placed.Count == 0)
        {
            MapProgress.Visibility = Visibility.Collapsed;
            return;
        }
        var textures = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<System.Windows.Media.Imaging.BitmapSource?>>(StringComparer.OrdinalIgnoreCase);
        Task<System.Windows.Media.Imaging.BitmapSource?> Texture(string path) =>
            // up to 2048 across, the game's own blocks sent to the graphics card as they're stored (only a 512 copy kept in
            // memory): at 512 the buildings, fences and props were a quarter of their size and looked blurred close up
            textures.GetOrAdd(path, p => Task.Run(() => MeshTextures.LoadWithAlpha(locator, p, 2048)));
        var kinds = new System.Collections.Concurrent.ConcurrentBag<MeshKind3D>();
        var waterSurfaces = new List<(Vector3, Vector3, Vector3)>();
        await Parallel.ForEachAsync(placed, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 8) }, async (group, token) =>
        {
            string path = group.First().Mesh;
            try
            {
                if (locator.ReadFile(path) is not { } data) return;
                var archive = locator.Find(path)?.Archive;
                var mesh = Core.Meshes.ClothFile.IsCloth(path) ? Core.Meshes.ClothFile.Read(data, () => locator.ReadCollision(path))
                    : Core.Meshes.MeshFile.Read(data, n => locator.ReadBuffer(path, n, archive));
                var materials = new List<(System.Windows.Media.Imaging.BitmapSource?, MaterialLook?, MeshUse)>();
                for (int m = 0; m < mesh.Materials.Count; m++)
                {
                    try
                    {
                        // volumes (weather, interiors) aren't seen; see-through surfaces (puddles, glass) aren't drawn yet;
                        // the merged shadow meshes only cast the sun's shadow
                        string shader = Core.Meshes.MeshMaterial.Resolve(mesh.File, mesh.Materials[m], locator.ReadFile).Shader.ToLowerInvariant();
                        // effect surfaces aren't drawn as solid ones: glowing planes, burnt patches, cobwebs, water and waterfalls
                        if (shader.StartsWith("water_"))
                        {
                            // a river's surface: the water drawn over it at its height, as the water shapes are
                            var triangles = new List<(Vector3, Vector3, Vector3)>();
                            foreach (var chunk in mesh.Chunks.Where(c => c.Material == m && c.InLod(0) && c.RenderMask != 0))
                                foreach (var place in group.Select(c => c.Place))
                                    for (int t = 0; t + 2 < chunk.Indices.Length; t += 3)
                                        triangles.Add((Vector3.Transform(chunk.Positions[chunk.Indices[t]], place), Vector3.Transform(chunk.Positions[chunk.Indices[t + 1]], place),
                                            Vector3.Transform(chunk.Positions[chunk.Indices[t + 2]], place)));
                            lock (waterSurfaces) waterSurfaces.AddRange(triangles);
                            materials.Add((null, null, MeshUse.Hidden));
                            continue;
                        }
                        if (shader is "volume" or "glow_plane_shader" or "web" || shader.StartsWith("transparent") || shader.StartsWith("m_"))
                        { materials.Add((null, null, MeshUse.Hidden)); continue; }
                        if (shader is "mergedmesh") { materials.Add((null, null, MeshUse.ShadowOnly)); continue; }
                        // dyed as its entity's colour entry says, or else undyed (the dyeable parts' raw red and blue are a mask, not a colour)
                        var coloring = group.Select(c => c.Coloring).FirstOrDefault(c => c is not null) ?? (StageTextures.Undyed, StageTextures.Undyed);
                        var (picture, look, leaveOut) = await LookTextures.ForMaterial(mesh, m, locator, Texture, coloring);
                        if (picture is null && !leaveOut)
                        {
                            // a material whose colour isn't a plain Diffuse (pbr_vert_blend's DiffuseArray, for one): its first
                            // colour texture, and its normal map likewise
                            var textures = Core.Meshes.MeshMaterial.Resolve(mesh.File, mesh.Materials[m], locator.ReadFile).Textures;
                            if (textures.FirstOrDefault(t => t.Parameter.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)) is { } colour)
                                picture = await Texture(colour.Path);
                            if (look?.Normal is null && textures.FirstOrDefault(t => t.Parameter.Contains("Normal", StringComparison.OrdinalIgnoreCase)) is { } normal
                                && await Texture(normal.Path) is { } normalPicture)
                                look = new MaterialLook { Normal = normalPicture, Constants = look?.Constants ?? W3Constants.Plain(true) };
                        }
                        materials.Add((picture, look, leaveOut ? MeshUse.Hidden : MeshUse.Drawn));
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException) { materials.Add((null, null, MeshUse.Drawn)); }
                }
                kinds.Add(new MeshKind3D(path, mesh, materials, group.Max(c => c.AutoHide), group.Select(c => c.Place).ToList(), group.Key.Shadow));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        });
        // the stripes (paths and roads over the ground): their triangles into the world, batched by their textures
        var stripes = await Task.Run(() => entities.SelectMany(e => e.Components).Where(c => c.Stripe is not null)
            .GroupBy(c => (Diffuse: c.Resource?.ToLowerInvariant(), Normal: c.Stripe!.Normal?.ToLowerInvariant()))
            .Select(group =>
            {
                var vertices = new List<StripeVertex3D>();
                var indices = new List<int>();
                foreach (var c in group)
                {
                    int first = vertices.Count;
                    foreach (var (position, uv, alpha, along) in c.Stripe!.Read())
                        vertices.Add(new StripeVertex3D
                        {
                            Position = Vector3.Transform(position, c.Place), Uv = uv, Alpha = alpha,
                            Along = Vector3.Normalize(Vector3.TransformNormal(along, c.Place) + new Vector3(1e-6f, 0, 0)),
                        });
                    int count = vertices.Count - first;
                    foreach (int i in c.Stripe.Triangles()) indices.Add(first + Math.Min(i, Math.Max(count - 1, 0)));
                }
                var diffuse = group.Key.Diffuse is { } d ? MeshTextures.LoadWithAlpha(locator, d, 2048) : null;
                var normal = group.Key.Normal is { } n ? MeshTextures.LoadWithAlpha(locator, n, 2048) : null;
                return new StripeBatch3D(diffuse, normal, [.. vertices], [.. indices]);
            }).ToList());
        MapProgress.Visibility = Visibility.Collapsed;
        View3D.SetMeshes([.. kinds.OrderBy(k => k.Path, StringComparer.OrdinalIgnoreCase)]);
        View3D.SetStripes(stripes);
        if (waterSurfaces.Count > 0)
        {
            _waterSurfaces = waterSurfaces;
            SetWater3D();
        }
    }

    /// <summary>
    /// The world's GTAO settings: radius, final power, grass's power, trees' power. The world files store the first two
    /// with their names clipped ("dius", "nalValuePower") and grass's as "GrassPower", so both spellings are read.
    /// </summary>
    private static Vector4? GtaoOf(WorldFile world)
    {
        if (((world.World["environmentParameters"] as RedObject)?["renderSettings"] as RedObject)?["gtaoSettings"] is not RedObject g) return null;
        float Value(float otherwise, params string[] names) => names.Select(n => g[n]).OfType<float>().Cast<float?>().FirstOrDefault() ?? otherwise;
        return new Vector4(Value(1, "radius", "dius"), Value(1, "finalValuePower", "nalValuePower"), Value(1, "grassPower", "GrassPower"),
            Value(1, "treesPower", "TreesPower"));
    }

    /// <summary>The world's remaster translucency settings (each a curve; its first key), 1 for one it doesn't keep.</summary>
    private static Vector4 TranslucencyOf(WorldFile world)
    {
        var settings = (world.World["environmentParameters"] as RedObject)?["translucencyRemasterSettings"] as RedObject;
        float Value(string name) => (settings?[name] as RedObject)?.Array("dataCurveValues").OfType<RedObject>().FirstOrDefault()?.Float("value", 1) ?? 1;
        return new Vector4(Value("translucencyCurveTightness"), Value("translucencyHighlightDimness"), Value("translucencyNormalMaskPower"),
            Value("translucencyScatteringSaturation"));
    }

    /// <summary>
    /// Every kind of plant the world's foliage cells place, with its SpeedTree file, its pictures (each LOD's and its
    /// billboard atlas, cutouts in their alpha) and where it grows; kinds whose file can't be read are left out.
    /// </summary>
    /// <summary>
    /// The terrain's grass: each texture slot's vegetation brush (the clip map's grassBrushes) and its plants, and the
    /// foliage scene's grass mask; <paramref name="plants"/> gets the brushes' plant files, to load with the rest.
    /// </summary>
    private GrassSetup? LoadGrass(WorldFile world, out List<string> plants)
    {
        plants = [];
        try
        {
            var file = world.File;
            List<object?>? brushes = null;
            string? maskPath = null;
            for (int i = 0; i < file.Exports.Count; i++)
            {
                var o = file.Object(i);
                if (file.Exports[i].ClassName == "CClipMap") brushes = o.Array("grassBrushes");
                if (file.Exports[i].ClassName == "CFoliageScene" && o["grassMask"] is RedHandle mh) maskPath = file.PathOf(mh);
            }
            if (brushes is null || maskPath is null || Read(maskPath) is not { } maskData) return null;
            var maskFile = Cr2wFile.Read(maskData);
            var maskObject = maskFile.Object(0);
            int res = (int)(maskObject["maskRes"] is uint r ? r : 0);
            var tail = maskObject.Tail;
            if (res <= 0 || tail.Length * 8 < res * res) return null;
            var mask = new bool[res * res];
            for (int b = 0; b < mask.Length; b++) mask[b] = (tail[b >> 3] & (1 << (b & 7))) != 0;
            var entries = new List<GrassEntry>();
            for (int slot = 0; slot < brushes.Count; slot++)
            {
                if (brushes[slot] is not RedHandle bh || file.PathOf(bh) is not { } brushPath || Read(brushPath) is not { } brushData) continue;
                var brush = Cr2wFile.Read(brushData);
                for (int i = 0; i < brush.Exports.Count; i++)
                {
                    if (brush.Exports[i].ClassName != "CVegetationBrushEntry") continue;
                    var e = brush.Object(i);
                    if (e["resource"] is not RedHandle th || brush.PathOf(th) is not { } tree || Read(tree) is not { } treeData) continue;
                    var srt = SpeedTreeFile.Read(treeData);
                    float footprint = MathF.Max(MathF.Max(-srt.Min.X, srt.Max.X), MathF.Max(-srt.Min.Y, srt.Max.Y));
                    entries.Add(new GrassEntry(slot, tree, e.Float("size", 1), e.Float("sizeVar", 0), e.Float("radiusScale", 1), footprint));
                    plants.Add(tree);
                }
            }
            return entries.Count > 0 ? new GrassSetup(entries, mask, res) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return null; }
    }

    private List<PlantKind3D> LoadPlants3D(string folder, IReadOnlyList<string>? extraGrass = null)
    {
        var paths = _locator.AllPaths(".flyr").Where(p => p.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (OnDisk && _diskRoot is { } root && Directory.Exists(Path.Combine(root, folder)))
            paths.AddRange(Directory.EnumerateFiles(Path.Combine(root, folder), "*.flyr", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)));
        var groups = new System.Collections.Concurrent.ConcurrentBag<FoliageGroup>();
        Parallel.ForEach(paths.Distinct(StringComparer.OrdinalIgnoreCase), path =>
        {
            try
            {
                foreach (var group in FoliageFile.Read(Read(path) ?? []).Groups) groups.Add(group);
            }
            catch (InvalidDataException) { }
        });
        // the terrain's grass plants too, placed by the view as it grows the grass
        foreach (var tree in extraGrass ?? []) groups.Add(new FoliageGroup(tree, true, []));
        var pictures = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Windows.Media.Imaging.BitmapSource?>(StringComparer.OrdinalIgnoreCase);
        var kinds = new System.Collections.Concurrent.ConcurrentBag<PlantKind3D>();
        Parallel.ForEach(groups.GroupBy(g => (g.Tree.ToLowerInvariant(), g.Grass)), kind =>
        {
            var first = kind.First();
            try
            {
                if (Read(first.Tree) is not { } data) return;
                var tree = SpeedTreeFile.Read(data);
                var mine = new Dictionary<string, System.Windows.Media.Imaging.BitmapSource?>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in tree.Materials.Select(m => m.Diffuse).Concat(tree.Materials.Select(m => m.Specular)).Concat(tree.Materials.Select(m => m.Normal))
                    .Append(tree.BillboardMaterial?.Diffuse ?? "")
                    .Append(tree.BillboardMaterial?.Normal ?? "").Where(n => n.Length > 0).Distinct())
                {
                    string path = SpeedTreeFile.TexturePath(first.Tree, name);
                    // grass's pictures up to 2048 (the game's own size for its atlases, 1024 by 2048): an atlas holds dozens of
                    // blades and plumes a few texels wide each, which turned into broad, jagged cards at half the size
                    int size = (first.Grass || tree.Materials.Any(m => m.Grass)) && tree.Materials.Any(m => m.Diffuse == name) ? 2048 : 1024;
                    mine[path] = pictures.GetOrAdd(path, t => MeshTextures.LoadWithAlpha(_locator, t, size));
                }
                kinds.Add(new PlantKind3D(first.Tree, tree, first.Grass, mine, kind.SelectMany(g => g.Instances).ToList()));
            }
            catch (InvalidDataException) { }
        });
        return kinds.OrderBy(k => k.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The world's foliage as the map draws it: pictures for afar, the plants for close up, and how many there are.</summary>
    private sealed record FoliageLook(System.Windows.Media.Imaging.BitmapSource? GrassPicture, System.Windows.Media.Imaging.BitmapSource? TreesPicture,
        List<MapPlant> Trees, List<MapPlant> Grass, int Plants, int Kinds, int Drawn);

    private FoliageLook LoadFoliage(WorldTerrain terrain, string folder)
    {
        var paths = _locator.AllPaths(".flyr").Where(p => p.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (OnDisk && _diskRoot is { } root && Directory.Exists(Path.Combine(root, folder)))
            paths.AddRange(Directory.EnumerateFiles(Path.Combine(root, folder), "*.flyr", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)));
        var cells = new System.Collections.Concurrent.ConcurrentBag<FoliageFile>();
        Parallel.ForEach(paths.Distinct(StringComparer.OrdinalIgnoreCase), path =>
        {
            try { cells.Add(FoliageFile.Read(Read(path) ?? [])); }
            catch (InvalidDataException) { }
        });

        // each kind of plant drawn from above once, its pictures read once for every kind that shares them
        var kinds = cells.SelectMany(c => c.Groups).Select(g => (g.Tree, g.Grass)).Distinct().ToList();
        var textures = new System.Collections.Concurrent.ConcurrentDictionary<string, SpritePixels?>(StringComparer.OrdinalIgnoreCase);
        var sprites = new System.Collections.Concurrent.ConcurrentDictionary<(string, bool), TreeSprite?>();
        Parallel.ForEach(kinds, kind =>
        {
            TreeSprite? sprite = null;
            try
            {
                if (Read(kind.Tree) is { } data)
                    sprite = TreeSprites.Render(SpeedTreeFile.Read(data), m => m.Diffuse.Length == 0 ? null
                        : textures.GetOrAdd(SpeedTreeFile.TexturePath(kind.Tree, m.Diffuse), t => SpritePixels.Of(MeshTextures.LoadWithAlpha(_locator, t, 512))),
                        kind.Grass ? 48 : 128, kind.Grass);
            }
            catch (InvalidDataException) { }
            sprites[kind] = sprite;
        });

        const int side = 2048;
        float perMetre = side / terrain.Size;
        var grass = new byte[side * side * 4];
        var trees = new byte[side * side * 4];
        var treeList = new List<MapPlant>();
        var grassList = new List<MapPlant>();
        int plants = 0;
        foreach (var cell in cells)
            foreach (var group in cell.Groups)
            {
                var sprite = sprites.GetValueOrDefault((group.Tree, group.Grass));
                var colour = sprite?.Colour ?? (group.Grass ? Color.FromRgb(60, 120, 40) : Color.FromRgb(25, 60, 22));
                foreach (var plant in group.Instances)
                {
                    float fx = (plant.Position.X - terrain.Corner.X) * perMetre, fy = side - 1 - (plant.Position.Y - terrain.Corner.Y) * perMetre;
                    int x = (int)fx, y = (int)fy;
                    if (x < 0 || y < 0 || x >= side || y >= side) continue;
                    plants++;
                    if (sprite is not null)
                        (group.Grass ? grassList : treeList).Add(new MapPlant(new Vector2(plant.Position.X, plant.Position.Y), plant.Yaw,
                            sprite.Radius * plant.Scale, sprite.Picture));
                    int o = (y * side + x) * 4;
                    if (group.Grass)
                    {
                        // grass adds up to a wash of its colour where it's thick
                        (grass[o], grass[o + 1], grass[o + 2]) = (colour.B, colour.G, colour.R);
                        grass[o + 3] = (byte)Math.Min(170, grass[o + 3] + 14);
                        continue;
                    }
                    // a tree: a disc of its colour most of the way across its crown, its edge softened
                    float r = Math.Max(0.7f, (sprite?.Radius ?? 3) * 0.7f * plant.Scale * perMetre);
                    int reach = (int)Math.Ceiling(r);
                    for (int dy = -reach; dy <= reach; dy++)
                        for (int dx = -reach; dx <= reach; dx++)
                        {
                            int px = x + dx, py = y + dy;
                            if (px < 0 || py < 0 || px >= side || py >= side) continue;
                            float d = MathF.Sqrt((px + 0.5f - fx) * (px + 0.5f - fx) + (py + 0.5f - fy) * (py + 0.5f - fy));
                            byte alpha = (byte)(Math.Clamp(r + 0.5f - d, 0, 1) * 220);
                            int q = (py * side + px) * 4;
                            if (alpha <= trees[q + 3]) continue;
                            (trees[q], trees[q + 1], trees[q + 2], trees[q + 3]) = (colour.B, colour.G, colour.R, alpha);
                        }
                }
            }

        // premultiplied, as WPF draws Pbgra32 pictures smoothly
        static System.Windows.Media.Imaging.BitmapSource? Picture(byte[] pixels, bool any)
        {
            if (!any) return null;
            for (int i = 0; i < pixels.Length; i += 4)
                for (int c = 0; c < 3; c++) pixels[i + c] = (byte)(pixels[i + c] * pixels[i + 3] / 255);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(side, side, 96, 96, PixelFormats.Pbgra32, null, pixels, side * 4);
            bitmap.Freeze();
            return bitmap;
        }
        bool anyGrass = cells.Any(c => c.Groups.Any(g => g.Grass)), anyTrees = cells.Any(c => c.Groups.Any(g => !g.Grass));
        return new FoliageLook(Picture(grass, anyGrass), Picture(trees, anyTrees), treeList, grassList, plants, kinds.Count,
            sprites.Count(p => p.Value is not null));
    }

    /// <summary>The hub map's pins (game\hub_pins\WORLD.w2em and .w2qm) and the cooked waypoints, as markers.</summary>
    private async void Markers_Click(object sender, RoutedEventArgs e)
    {
        string name = Path.GetFileNameWithoutExtension(GamePath);
        string folder = GamePath.Contains('\\') ? Path.GetDirectoryName(GamePath)! : _world!.Folder;
        if (PinsCheck.IsChecked == true && _pins is null)
            _pins = await Task.Run(() =>
            {
                var markers = new List<MapMarker>();
                foreach (string ext in new[] { ".w2em", ".w2qm" })
                    try
                    {
                        if (Read($@"game\hub_pins\{name}{ext}") is { } data)
                            markers.AddRange(MapPins.Read(data).Select(p => new MapMarker(new Vector2(p.Position.X, p.Position.Y),
                                p.Quest ? Color.FromRgb(0xF2, 0xC9, 0x4C) : Color.FromRgb(0xF2, 0xF2, 0xF2), p.Quest ? p.Name : $"{p.Name} ({p.Kind})", p.Radius, true)));
                    }
                    catch (InvalidDataException) { }
                return markers;
            });
        if (WaypointsCheck.IsChecked == true && _waypoints is null)
            _waypoints = await Task.Run(() =>
            {
                try
                {
                    return Read($@"{folder}\navi_cooked\{name}.redwpset") is { } data
                        ? Waypoints.Read(data).Select(w => new MapMarker(new Vector2(w.Position.X, w.Position.Y), Color.FromRgb(0x7C, 0xE0, 0x9A), null, 0, false)).ToList()
                        : [];
                }
                catch (InvalidDataException) { return new List<MapMarker>(); }
            });
        var all = new List<MapMarker>();
        if (WaypointsCheck.IsChecked == true && _waypoints is not null) all.AddRange(_waypoints);
        if (PinsCheck.IsChecked == true && _pins is not null) all.AddRange(_pins);
        Map.SetMarkers(all);
        PinsCheck.ToolTip = _pins is null ? L.T("WorldWindow.PinsTip") : L.T("WorldWindow.PinsRead", _pins.Count);
        WaypointsCheck.ToolTip = _waypoints is null ? L.T("WorldWindow.WaypointsTip") : L.T("WorldWindow.WaypointsRead", _waypoints.Count);
    }

    /// <summary>
    /// The world in 3D in the map's place: its terrain from what the map read, the camera over the middle of the map's
    /// view, looking north; or back to the map.
    /// </summary>
    private async void View3DToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = View3DToggle.IsChecked == true;
        if (on && _terrain is null)
        {
            View3DToggle.IsChecked = false;
            await ShowMessage(null, L.T("WorldWindow.NoTerrain3D"), MessageKind.Info);
            return;
        }
        if (on)
        {
            if (!View3D.HasTerrain)
            {
                MapProgress.Visibility = Visibility.Visible;
                MapProgress.Text = L.T("WorldWindow.ReadingTerrainTextures");
                var world = _world!;
                var (diffuse, normals, lighting, foam) = await Task.Run(() => (TerrainArray("diffuse"), TerrainArray("normal"),
                    WorldLighting.Of(world.File, world.World, Read), CachedTexture(@"environment\water\global_ocean\ocean_control.texarray")));
                MapProgress.Visibility = Visibility.Collapsed;
                View3D.ShowTerrain(_terrain!, diffuse, normals);
                View3D.SetLighting(lighting);
                ShowWeathers(lighting);
                await ShowBalanceMaps(lighting);
                View3D.LightingChanged -= View3D_LightingChanged;
                View3D.LightingChanged += View3D_LightingChanged;
                View3D.SetFoam(foam);
                // the water shapes the layers read so far have: each sets the water's level inside it
                SetWater3D();
                _ = SetProbe3DAsync();
                View3DToggle.ToolTip = View3D.Painted ? L.T("WorldWindow.View3DTip") : L.T("WorldWindow.View3DNoTextures");
                _ = ShowPlants3DAsync();
            }
            // the first time, where the camera was when the world was last left (as REDkit keeps it), else over the map's middle
            if (!(_cameraRestored is false && App.Settings.WorldCameras.TryGetValue(_path.ToLowerInvariant(), out var kept) && RestoreCamera3D(kept)))
            {
                var content = Map.Content();
                View3D.LookAt(new Vector2((float)((Map.ActualWidth / 2 - content.Offset.X) / content.Scale), (float)(-(Map.ActualHeight / 2 - content.Offset.Y) / content.Scale)));
            }
            _cameraRestored = true;
            View3D.Focus();
        }
        else
        {
            KeepCamera3D();
            HoverText.Text = L.T("WorldWindow.MouseHint");
        }
        // the map's own switches put away while the 3D view shows
        DayPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on && View3D.PlayingDay) PlayDay(false);
        foreach (UIElement control in new UIElement[] { LookBox, AreasBox, EntitiesCheck, FoliageCheck, OutlinesCheck, PinsCheck, WaypointsCheck,
            NavigationCheck, TilesCheck, OcclusionCheck, GpuCheck, FitButton })
            control.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        View3D.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Map.Visibility = on || GpuCheck.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        GpuMap.Visibility = !on && GpuCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One of the textures arrays the terrain is painted with, as the texture cache keeps it: the terrain material's
    /// parameter of that name (a CMaterialParameterTextureArray naming a .texarray). Null when it can't be found.
    /// </summary>
    private (Core.Textures.TextureInfo Info, byte[] Pixels)? TerrainArray(string parameter)
    {
        try
        {
            if (_world?.Terrain?.Material is not { } material || Read(material) is not { } data) return null;
            var file = Cr2wFile.Read(data);
            for (int i = 0; i < file.Exports.Count; i++)
            {
                if (file.Exports[i].ClassName != "CMaterialParameterTextureArray") continue;
                var p = file.Object(i);
                if (!string.Equals(p["parameterName"] as string, parameter, StringComparison.OrdinalIgnoreCase)) continue;
                return p["texture"] is RedHandle h && file.PathOf(h) is { } path ? CachedTexture(path) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return null;
    }

    private bool _settingHour;

    private WorldLighting? _lighting;
    private bool _fillingWeathers;

    /// <summary>The world's weather conditions in the weather box, the one blended in now picked (or none).</summary>
    private void ShowWeathers(WorldLighting lighting)
    {
        _lighting = lighting;
        _fillingWeathers = true;
        WeatherBox.Items.Clear();
        WeatherBox.Items.Add(new ComboBoxItem { Content = L.T("WorldWindow.NoWeather"), Tag = null });
        foreach (var weather in lighting.Weathers) WeatherBox.Items.Add(new ComboBoxItem { Content = weather.Name, Tag = weather.Name });
        WeatherBox.SelectedIndex = lighting.Weather is { } w ? 1 + lighting.Weathers.ToList().FindIndex(x => x.Name == w) : 0;
        WeatherBox.Visibility = lighting.Weathers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _fillingWeathers = false;
    }

    /// <summary>The colour lookups the environment's grading blends in, read off the UI thread.</summary>
    private async Task ShowBalanceMaps(WorldLighting lighting)
    {
        var (first, second) = lighting.BalanceMaps;
        var maps = await Task.Run(() => (first is null ? null : CachedTexture(first), second is null ? null : CachedTexture(second)));
        View3D.SetBalanceMaps(maps.Item1, maps.Item2);
    }

    private void WeatherBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingWeathers || WeatherBox.SelectedItem is not ComboBoxItem item) return;
        // as REDkit's Force weather preset: the condition blends in over its blend time
        View3D.ChangeWeather(item.Tag as string, instantly: false);
    }

    private (string?, string?) _balanceMapsShown;

    /// <summary>The weather box and the balance maps following the lighting as it changes (a weather blending in, a preset forced).</summary>
    private async void View3D_LightingChanged(object? sender, EventArgs e)
    {
        if (View3D.Lighting is not { } lighting) return;
        _lighting = lighting;
        _fillingWeathers = true;
        WeatherBox.SelectedItem = WeatherBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == lighting.Weather) ?? WeatherBox.Items[0];
        _fillingWeathers = false;
        if (lighting.BalanceMaps != _balanceMapsShown)
        {
            _balanceMapsShown = lighting.BalanceMaps;
            await ShowBalanceMaps(lighting);
        }
    }

    private WorldEnvironmentWindow? _environmentWindow;

    /// <summary>REDkit's World Environment tool for the 3D view, one at a time.</summary>
    private void EnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_environmentWindow is { IsLoaded: true })
        {
            _environmentWindow.Activate();
            return;
        }
        _environmentWindow = new WorldEnvironmentWindow(View3D, _locator, Read, this);
        _environmentWindow.Closed += (_, _) => _environmentWindow = null;
        _environmentWindow.Show();
    }

    private void HourSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // it's first set while the window is still being made
        if (HourText is null || View3D is null) return;
        HourText.Text = HourName(HourSlider.Value);
        if (!_settingHour) View3D.Hour = (float)HourSlider.Value;
    }

    private void PlayDayToggle_Click(object sender, RoutedEventArgs e) => PlayDay(PlayDayToggle.IsChecked == true);

    /// <summary>The day going by on its own in 3D (an hour every two seconds), the slider following it, or stopped.</summary>
    private void PlayDay(bool on)
    {
        View3D.PlayingDay = on;
        PlayDayToggle.IsChecked = on;
        PlayDayIcon.Text = on ? "\uE769" : "\uE768";
    }

    private void View3D_HourChanged(object? sender, float hour)
    {
        _settingHour = true;
        HourSlider.Value = hour;
        _settingHour = false;
    }

    /// <summary>An hour of the day as a clock shows it.</summary>
    private static string HourName(double hour)
    {
        int minutes = (int)Math.Round(hour * 60) % (24 * 60);
        return new DateTime(1, 1, 1, minutes / 60, minutes % 60, 0).ToString("t", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>A texture as the game's texture caches keep it, its stored pixels; null when no cache has it.</summary>
    private (Core.Textures.TextureInfo Info, byte[] Pixels)? CachedTexture(string path)
    {
        if (_locator.FindTexture(path) is not { } entry) return null;
        try
        {
            using var stream = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return (entry.Info, Core.Textures.TextureCacheArchive.ReadPixels(stream, entry.Info));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    private void View3D_CameraMoved(object? sender, (Vector3 Eye, float Heading, float Speed) camera) =>
        HoverText.Text = L.T("WorldWindow.Camera3D", camera.Eye.X, camera.Eye.Y, camera.Eye.Z, camera.Heading, camera.Speed);

    /// <summary>The test of drawing the map with the graphics card: shown in the 2D map's place with what it shows, or put away.</summary>
    private void GpuCheck_Click(object sender, RoutedEventArgs e)
    {
        bool gpu = GpuCheck.IsChecked == true;
        if (gpu)
        {
            GpuMap.BackBrush = Map.BackBrush;
            GpuMap.ShowFrom(Map);
            GpuCheck.ToolTip = L.T("WorldWindow.GpuRead", GpuMap.PlantCount);
        }
        GpuMap.Visibility = gpu ? Visibility.Visible : Visibility.Collapsed;
        Map.Visibility = gpu ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        Map.ShowTiles = TilesCheck.IsChecked == true;
        Map.ShowOcclusion = OcclusionCheck.IsChecked == true;
        ShowDots();
        ShowShapes();
    }

    private void FitButton_Click(object sender, RoutedEventArgs e) => Map.FitWorld();

    private void Map_PointerMoved(object? sender, Vector2? point)
    {
        if (point is not { } p)
        {
            HoverText.Text = L.T("WorldWindow.MouseHint");
            return;
        }
        string text = $"X {p.X:0.0}   Y {p.Y:0.0}";
        if (_terrain?.At(p) is { } at)
        {
            var t = _terrain.Terrain;
            int col = (int)((p.X - t.Corner.X) / t.TileSize), row = (int)((p.Y - t.Corner.Y) / t.TileSize);
            text += "   ·   " + L.T("WorldWindow.HoverTerrain", at.Metres, TerrainLevel.Background(at.Control), TerrainLevel.Overlay(at.Control),
                TerrainLevel.SlopeThreshold(at.Control), $"tile_{row}_x_{col}");
        }
        HoverText.Text = text;
    }

    private void Map_DotPicked(object? sender, MapDot? dot)
    {
        if (dot is null)
        {
            PickedPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var e = dot.Entity;
        PickedTitle.Text = $"{(dot.Title.Length > 0 ? dot.Title : L.T("WorldWindow.Unnamed"))}   ·   {e.ClassName}";
        PickedDetail.Text = L.T("WorldWindow.PickedDetail", dot.Layer.Path, $"{e.Position.X:0.00}, {e.Position.Y:0.00}, {e.Position.Z:0.00}", e.Yaw * 180 / MathF.PI,
            e.Template ?? L.T("WorldWindow.NoTemplate"));
        OpenTemplateButton.IsEnabled = e.Template is not null;
        PickedComponents.ItemsSource = e.Components;
        PickedComponents.Visibility = e.Components.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PickedPanel.Visibility = Visibility.Visible;
        // the picked entity's areas filled in, so it's clear which outlines are its own
        if (OutlinesCheck.IsChecked == true) ShowShapes();
    }

    private void OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (Map.Picked?.Entity.Template is { } template) _ = DataViewers.OpenLinkAsync(template, _locator, this, this);
    }

    private void OpenPickedLayer_Click(object sender, RoutedEventArgs e)
    {
        if (Map.Picked is { } dot) OpenLayer(dot.Layer.Path);
    }

    /// <summary>A layer's file in the properties viewer, every entity in it listed.</summary>
    private void OpenLayer(string path)
    {
        if (Read(path) is { } data) PropertiesWindow.Open(path, _source, data, _locator, this);
    }

    private LayerNode? Selected => LayerTree.SelectedItem as LayerNode;

    private void LayerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Selected is not { } node)
        {
            Map.Highlight(null);
            return;
        }
        var layers = node.Layers().ToHashSet();
        Map.Highlight(layers);
        var dots = layers.SelectMany(l => _dots.TryGetValue(l, out var d) ? d : []).ToList();
        if (dots.Count > 0) Map.FitToDots(dots);
    }

    private void ShowOnly_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } node) return;
        foreach (var root in _roots) root.Set(false);
        node.Set(true);
        ShowDots();
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _roots) root.Set(true);
        ShowDots();
    }

    private void OpenLayer_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Layer is { } layer) OpenLayer(layer.Path);
    }

    private void CopyLayerPath_Click(object sender, RoutedEventArgs e)
    {
        string? path = Selected?.Layer?.Path ?? Selected?.Group?.Path;
        if (path is null) return;
        try { Clipboard.SetText(path); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTree();
    }

    private void FactLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Hyperlink)?.Tag is string path) _ = DataViewers.OpenLinkAsync(path, _locator, this, this);
    }

    /// <summary>Shows REDkit's own copy's properties when there is one, and says where they come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit?.First("CGameWorld") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, L.T("WorldWindow.World3"));
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Tabs.SelectedItem = LayersTab;
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.F)
        {
            Map.FitWorld();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
