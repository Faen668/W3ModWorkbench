using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.World;
using Vector2 = System.Numerics.Vector2;

namespace W3ModWorkbench;

/// <summary>
/// A world's minimap as the game draws it: a picture for each terrain tile (tileCOLUMNxROW.jpg, rows from the south,
/// 512 or 1024 pixels each), here put together at up to 4096 pixels across, BGRA with alpha 0 where the game has none,
/// and where each tile's own file is, for <see cref="MinimapDetail"/> to draw them full size once zoomed in.
/// </summary>
public sealed class GameMinimap
{
    public required byte[] Pixels { get; init; }
    public required int Side { get; init; }
    public required Vector2 Corner { get; init; }
    public required float Size { get; init; }
    public required int TilesPerEdge { get; init; }
    public required IReadOnlyDictionary<(int Col, int Row), string> Tiles { get; init; }

    public float TileSize => Size / TilesPerEdge;

    /// <summary>How many pixels a metre the whole picture has: past this, the full-size tiles are sharper.</summary>
    public double PixelsPerMetre => Side / Size;

    /// <summary>
    /// Where a world's minimap tiles are: the world's own minimapsPath when it sets one, else gameplay\gui_new\minimaps\
    /// and the world file's name, under a DLC's own data folder for a DLC's world (dlc\bob\data\levels\bob\bob.w2w has its
    /// in dlc\bob\data\gameplay\gui_new\minimaps\bob; levels\the_spiral\spiral.w2w in gameplay\gui_new\minimaps\spiral).
    /// </summary>
    public static string Folder(RedObject world, string worldPath)
    {
        if (world["minimapsPath"] is string { Length: > 0 } own) return own.TrimEnd('\\');
        int levels = worldPath.IndexOf(@"levels\", StringComparison.OrdinalIgnoreCase);
        string root = levels > 0 ? worldPath[..levels] : "";
        return $@"{root}gameplay\gui_new\minimaps\{Path.GetFileNameWithoutExtension(worldPath)}";
    }

    /// <summary>
    /// Reads every tile of a world's minimap the game has (or <paramref name="elsewhere"/> gives, REDkit's pictures of
    /// them), each shrunk into the whole picture. Null when there are none. Reads from disk: off the UI thread.
    /// </summary>
    public static GameMinimap? Read(ResourceLocator locator, WorldFile world, string worldPath, Func<string, BitmapSource?>? elsewhere = null)
    {
        if (world.Terrain is not { } terrain) return null;
        string folder = Folder(world.World, worldPath);
        int n = terrain.TilesPerEdge, size = Math.Clamp(4096 / n, 32, 256), side = n * size;
        var pixels = new byte[side * side * 4];
        var tiles = new ConcurrentDictionary<(int, int), string>();
        Parallel.For(0, n * n, i =>
        {
            int col = i % n, row = i / n;
            string path = $@"{folder}\tile{col}x{row}.jpg";
            var picture = MeshTextures.Load(locator, path, size) ?? elsewhere?.Invoke(path);
            if (picture is null || picture.PixelWidth == 0) return;
            var scaled = new FormatConvertedBitmap(new TransformedBitmap(picture, new ScaleTransform((double)size / picture.PixelWidth, (double)size / picture.PixelHeight)),
                PixelFormats.Bgra32, null, 0);
            var tile = new byte[size * size * 4];
            scaled.CopyPixels(new Int32Rect(0, 0, Math.Min(size, scaled.PixelWidth), Math.Min(size, scaled.PixelHeight)), tile, size * 4, 0);
            for (int y = 0; y < size; y++) Array.Copy(tile, y * size * 4, pixels, (((n - 1 - row) * size + y) * side + col * size) * 4, size * 4);
            tiles[(col, row)] = path;
        });
        return tiles.IsEmpty ? null : new GameMinimap
        {
            Pixels = pixels, Side = side, Corner = terrain.Corner, Size = terrain.Size, TilesPerEdge = n, Tiles = new Dictionary<(int, int), string>(tiles),
        };
    }

    /// <summary>The whole picture, frozen, from <see cref="Pixels"/> as they are (after anything painted into them).</summary>
    public BitmapSource Picture()
    {
        var bitmap = BitmapSource.Create(Side, Side, 96, 96, PixelFormats.Bgra32, null, Pixels, Side * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// The game's full-size minimap tiles laid over a <see cref="WorldMap"/> for the part of the world in view, once it's
/// zoomed in past the whole minimap picture's own detail: read in the background as the map settles, the last few dozen
/// kept, and taken away again when zoomed out. The whole picture alone would be a quarter of the game's detail or less.
/// </summary>
public sealed class MinimapDetail
{
    private const string Prefix = "minimap tile:"; // loc-ignore: a key, never shown
    private const int Kept = 96;
    private readonly WorldMap _map;
    private readonly string? _after;
    private readonly Dictionary<string, BitmapSource> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = [];
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _settle;
    private GameMinimap? _minimap;
    private ResourceLocator? _locator;
    private Func<string, BitmapSource?>? _elsewhere;

    /// <param name="after">The map overlay the tiles go just over (the minimap's own), or null for over every other.</param>
    public MinimapDetail(WorldMap map, string? after = null)
    {
        _map = map;
        _after = after;
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _settle!.Stop();
            Update();
        }, map.Dispatcher);
        _settle.Stop();
        map.ViewChanged += (_, _) =>
        {
            if (_minimap is null) return;
            _settle.Stop();
            _settle.Start();
        };
    }

    /// <summary>The minimap whose tiles are drawn, or null for none (a world without one, or another look picked).</summary>
    public void Show(GameMinimap? minimap, ResourceLocator? locator, Func<string, BitmapSource?>? elsewhere = null)
    {
        if (!ReferenceEquals(minimap, _minimap))
        {
            _tiles.Clear();
            _order.Clear();
        }
        (_minimap, _locator, _elsewhere) = (minimap, locator, elsewhere);
        Update();
    }

    private void Update()
    {
        if (_minimap is not { } minimap || _locator is not { } locator || _map.PixelsPerMetre <= minimap.PixelsPerMetre * 1.2)
        {
            _map.SetOverlays(Prefix, [], _after);
            return;
        }
        var (min, max) = _map.VisibleArea;
        float ts = minimap.TileSize;
        int last = minimap.TilesPerEdge - 1;
        int c0 = Math.Max(0, (int)Math.Floor((min.X - minimap.Corner.X) / ts)), c1 = Math.Min(last, (int)Math.Floor((max.X - minimap.Corner.X) / ts));
        int r0 = Math.Max(0, (int)Math.Floor((min.Y - minimap.Corner.Y) / ts)), r1 = Math.Min(last, (int)Math.Floor((max.Y - minimap.Corner.Y) / ts));
        var wanted = new List<(string Path, Vector2 Corner)>();
        for (int c = c0; c <= c1; c++)
            for (int r = r0; r <= r1; r++)
                if (minimap.Tiles.TryGetValue((c, r), out var path)) wanted.Add((path, minimap.Corner + new Vector2(c * ts, r * ts)));
        // so many in view that the whole picture is as sharp as the screen there
        if (wanted.Count > Kept / 2) wanted.Clear();
        _map.SetOverlays(Prefix, wanted.Where(w => _tiles.ContainsKey(w.Path)).Select(w => (Prefix + w.Path, _tiles[w.Path], w.Corner, ts)), _after);
        var missing = wanted.Where(w => !_tiles.ContainsKey(w.Path) && _loading.Add(w.Path)).Select(w => w.Path).ToList();
        if (missing.Count == 0) return;
        var elsewhere = _elsewhere;
        _ = Task.Run(() => missing.AsParallel().Select(path => (Path: path, Picture: MeshTextures.Load(locator, path, 4096) ?? elsewhere?.Invoke(path))).ToList())
            .ContinueWith(t =>
            {
                foreach (var (path, tile) in t.Result)
                {
                    _loading.Remove(path);
                    if (tile is null || !ReferenceEquals(minimap, _minimap)) continue;
                    _tiles[path] = tile;
                    _order.AddLast(path);
                }
                while (_order.Count > Kept)
                {
                    _tiles.Remove(_order.First!.Value);
                    _order.RemoveFirst();
                }
                if (ReferenceEquals(minimap, _minimap)) Update();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
