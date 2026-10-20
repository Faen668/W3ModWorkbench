using System.IO;
using W3ModWorkbench.Core.Textures;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>
/// Finds the files a resource refers to by their game path: a mesh's buffers, its materials (.w2mi, .w2mg) and its
/// textures' pixels. It looks where the game would, mods first: the archives open in the tool, then the ones beside
/// the file being looked at, then the game's own bundles and texture caches (content0, its DLC and the installed
/// mods). The game's bundle and cache indexes are read once, the first time something isn't found nearer. With a
/// project open, the project's own game files come before all of them, as the mod being made is the one that wins.
/// </summary>
public sealed class ResourceLocator
{
    private readonly List<IArchive> _open;
    private readonly string? _near;
    private readonly string? _game;
    private readonly Func<string?>? _projectFiles;
    private readonly bool _otherMods;

    /// <param name="open">The archives open in the tool, which are looked in first.</param>
    /// <param name="near">The file being looked at (its bundle or cache), whose folder is looked in next.</param>
    /// <param name="projectFiles">The open project's workspace, asked each time as a project can be opened while a mesh is shown.</param>
    /// <param name="otherMods">
    /// False to look in no installed mod but the one the file is from: what it uses comes from its own mod, then the
    /// game (its DLC, then content0), as when a mod is looked at on its own; another mod's copy of a file never wins.
    /// </param>
    public ResourceLocator(IEnumerable<IArchive> open, IArchive? near, string? gameFolder, Func<string?>? projectFiles = null, bool otherMods = true)
    {
        _projectFiles = projectFiles;
        _near = near is null ? null : Path.GetDirectoryName(near.FilePath);
        _game = gameFolder;
        _otherMods = otherMods;
        _open = open.Where(a => otherMods || !IsOtherMod(a.FilePath)).ToList();
        if (near is not null && !_open.Contains(near)) _open.Insert(0, near);
    }

    /// <summary>Whether a file is in an installed mod other than the one the file looked at is from.</summary>
    private bool IsOtherMod(string path)
    {
        if (_game is null || ModFolder(path) is not { } mod) return false;
        return _near is null || !mod.Equals(ModFolder(Path.Combine(_near, "x")), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The installed mod's folder (game\mods\modName) a file is in, or null for one that isn't in one.</summary>
    private string? ModFolder(string path)
    {
        if (_game is null) return null;
        string mods = Path.GetFullPath(Path.Combine(_game, "mods")).TrimEnd('\\') + "\\";
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!full.StartsWith(mods, StringComparison.OrdinalIgnoreCase)) return null;
        int slash = full.IndexOf('\\', mods.Length);
        return slash < 0 ? null : full[..slash];
    }

    // ---------------------------------------------------------------- files in bundles

    /// <summary>A file by its game path, read from the nearest archive that has it. Null when none has.</summary>
    public byte[]? ReadFile(string path) => ProjectFile(path) is { } own ? File.ReadAllBytes(own) : Find(path) is { } entry ? Read(entry) : null;

    /// <summary>The open project's own copy of a game file, when it has one.</summary>
    public string? ProjectFile(string path)
    {
        if (string.IsNullOrEmpty(path) || _projectFiles?.Invoke() is not { } folder) return null;
        string file = Path.Combine(folder, path);
        return File.Exists(file) ? file : null;
    }

    /// <summary>Where a file is: the nearest archive with a file at that path. Null when none has one.</summary>
    public ArchiveEntry? Find(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var archive in _open.Where(a => a is not TextureCacheArchive))
            if (Index(archive).TryGetValue(path, out var entry)) return entry;
        foreach (var archive in Beside())
            if (Index(archive).TryGetValue(path, out var entry)) return entry;
        foreach (var archive in GameBundles())
            if (Index(archive).TryGetValue(path, out var entry)) return entry;
        return null;
    }

    /// <summary>
    /// One of a resource's numbered buffers (<c>x.w2mesh.1.buffer</c>). A buffer goes with the copy of the resource beside
    /// it, so the archives in <paramref name="owner"/>'s folder are looked in before any other.
    /// </summary>
    public byte[]? ReadBuffer(string resourcePath, int number, IArchive? owner)
    {
        string path = $"{resourcePath}.{number}.buffer";
        if (ProjectFile(resourcePath) is not null) return ProjectFile(path) is { } own ? File.ReadAllBytes(own) : ReadFile(path);
        if (owner is not null)
        {
            string? folder = Path.GetDirectoryName(owner.FilePath);
            foreach (var archive in _open.Concat(Beside(folder)).Where(a => Path.GetDirectoryName(a.FilePath) == folder))
                if (Index(archive).TryGetValue(path, out var entry)) return Read(entry);
        }
        return ReadFile(path);
    }

    private static byte[] Read(ArchiveEntry entry)
    {
        if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
        using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return entry.Read(fs);
    }

    /// <summary>
    /// Every game path with an extension (".xbm", ".w2mi") in the archives looked in, sorted, without repeats. For .xbm
    /// the texture caches' paths are included, as a texture can be in a cache with no .xbm in any bundle.
    /// </summary>
    public List<string> AllPaths(string extension)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archives = _open.Concat(Beside()).Concat(GameBundles()).Distinct().ToList();
        if (extension.Equals(".xbm", StringComparison.OrdinalIgnoreCase)) archives.AddRange(Caches());
        foreach (var archive in archives)
            foreach (var entry in archive.Entries)
                if (entry.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) paths.Add(entry.Path);
        if (_projectFiles?.Invoke() is { } folder && Directory.Exists(folder))
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*" + extension, SearchOption.AllDirectories))
                    paths.Add(Path.GetRelativePath(folder, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return [.. paths.Order(StringComparer.OrdinalIgnoreCase)];
    }

    // ---------------------------------------------------------------- collision caches

    /// <summary>
    /// A cooked resource's entry in the game's collision caches (a cloth's APEX asset), from the nearest that has it:
    /// beside the file being looked at, then the installed mods', the DLC's and content0's. Null when none has.
    /// </summary>
    public byte[]? ReadCollision(string path)
    {
        var places = new List<string>();
        if (_near is not null) places.Add(Path.Combine(_near, "collision.cache"));
        if (_game is not null)
        {
            foreach (string folder in _otherMods ? new[] { "mods", "dlc" } : ["dlc"])
            {
                string root = Path.Combine(_game, folder);
                if (!Directory.Exists(root)) continue;
                try { places.AddRange(Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase).Select(m => Path.Combine(m, "content", "collision.cache"))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            places.Add(Path.Combine(_game, "content", "content0", "collision.cache"));
        }
        foreach (string place in places)
            if (CollisionCacheAt(place) is { } cache && cache.Has(path))
                try { return cache.Read(path); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        return null;
    }

    private static readonly Dictionary<string, Meshes.CollisionCache?> OpenedCollisionCaches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A collision cache on disk, its index read once. Null when there's none there or it can't be read.</summary>
    private static Meshes.CollisionCache? CollisionCacheAt(string path)
    {
        lock (OpenedCollisionCaches)
        {
            if (OpenedCollisionCaches.TryGetValue(path, out var known)) return known;
            Meshes.CollisionCache? cache = null;
            try { if (File.Exists(path)) cache = Meshes.CollisionCache.Open(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            return OpenedCollisionCaches[path] = cache;
        }
    }

    // ---------------------------------------------------------------- textures

    /// <summary>
    /// The pixels of a texture an .xbm path names: from a texture cache that's open, the one beside the file being looked
    /// at, or the game's (content0, DLC, mods). Null when no cache has it.
    /// </summary>
    public TextureEntry? FindTexture(string xbmPath)
    {
        foreach (var cache in Caches())
            if (FindInCache(cache, xbmPath) is { } entry) return entry;
        return null;
    }

    private IEnumerable<TextureCacheArchive> Caches()
    {
        foreach (var cache in _open.OfType<TextureCacheArchive>()) yield return cache;
        if (_near is not null && Cache(Path.Combine(_near, "texture.cache")) is { } beside) yield return beside;
        if (_game is null) yield break;
        if (Cache(Path.Combine(_game, "content", "content0", "texture.cache")) is { } content0) yield return content0;
        foreach (string path in GameCachePaths(_game))
            if ((_otherMods || !IsOtherMod(path)) && Cache(path) is { } cache) yield return cache;
    }

    /// <summary>Each cache's textures by path, made the first time a cache is looked in, so finding one is quick.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextureCacheArchive, Dictionary<string, TextureEntry>> CacheIndex = [];

    public static TextureEntry? FindInCache(TextureCacheArchive cache, string path)
    {
        var index = CacheIndex.GetValue(cache, c => c.Entries.OfType<TextureEntry>()
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));
        return index.GetValueOrDefault(path);
    }

    /// <summary>Texture caches opened from disk, kept so the next texture doesn't read the index again. Null for a file that isn't one.</summary>
    private static readonly Dictionary<string, TextureCacheArchive?> OpenedCaches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A texture cache on disk, opened once. Null when there's none there or it can't be read.</summary>
    public static TextureCacheArchive? Cache(string path)
    {
        lock (OpenedCaches)
        {
            if (OpenedCaches.TryGetValue(path, out var known)) return known;
            TextureCacheArchive? cache = null;
            try { if (File.Exists(path) && TextureCacheArchive.LooksLikeCache(path)) cache = TextureCacheArchive.Open(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            return OpenedCaches[path] = cache;
        }
    }

    private static readonly Dictionary<string, List<string>> CachePathsByGame = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The texture caches of the game's DLC and installed mods, mods first as the game loads them over DLC.</summary>
    private static List<string> GameCachePaths(string game)
    {
        lock (CachePathsByGame)
        {
            if (CachePathsByGame.TryGetValue(game, out var known)) return known;
            var paths = new List<string>();
            foreach (string folder in new[] { "mods", "dlc" })
            {
                string root = Path.Combine(game, folder);
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string mod in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        string cache = Path.Combine(mod, "content", "texture.cache");
                        if (File.Exists(cache)) paths.Add(cache);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return CachePathsByGame[game] = paths;
        }
    }

    // ---------------------------------------------------------------- bundle indexes

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IArchive, Dictionary<string, ArchiveEntry>> EntryIndex = [];

    private static Dictionary<string, ArchiveEntry> Index(IArchive archive) => EntryIndex.GetValue(archive, a =>
    {
        var index = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in a.Entries) index.TryAdd(entry.Path, entry);
        return index;
    });

    private static readonly Dictionary<string, Bundle?> OpenedBundles = new(StringComparer.OrdinalIgnoreCase);

    private static Bundle? OpenBundle(string path)
    {
        lock (OpenedBundles)
        {
            if (OpenedBundles.TryGetValue(path, out var known)) return known;
            Bundle? bundle = null;
            try { bundle = Bundle.Open(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            return OpenedBundles[path] = bundle;
        }
    }

    /// <summary>The bundles in the folder of the file being looked at (or <paramref name="folder"/>), that aren't open.</summary>
    private IEnumerable<IArchive> Beside(string? folder = null)
    {
        folder ??= _near;
        if (folder is null || !Directory.Exists(folder)) yield break;
        string[] files;
        try { files = Directory.GetFiles(folder, "*.bundle"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
            if (!_open.Any(a => a.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase)) && OpenBundle(file) is { } bundle)
                yield return bundle;
    }

    private static readonly Dictionary<string, List<string>> BundlePathsByGame = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's bundles: installed mods first, then DLC, then content0, as the game loads them.</summary>
    private IEnumerable<IArchive> GameBundles()
    {
        if (_game is null) yield break;
        List<string> paths;
        lock (BundlePathsByGame)
        {
            if (!BundlePathsByGame.TryGetValue(_game, out paths!))
            {
                paths = [];
                foreach (string folder in new[] { "mods", "dlc" })
                {
                    string root = Path.Combine(_game, folder);
                    if (!Directory.Exists(root)) continue;
                    try
                    {
                        foreach (string mod in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
                        {
                            string content = Path.Combine(mod, "content");
                            if (Directory.Exists(content)) paths.AddRange(Directory.GetFiles(content, "*.bundle").Order(StringComparer.OrdinalIgnoreCase));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                string bundles = Path.Combine(_game, "content", "content0", "bundles");
                if (Directory.Exists(bundles)) paths.AddRange(Directory.GetFiles(bundles, "*.bundle").Order(StringComparer.OrdinalIgnoreCase));
                BundlePathsByGame[_game] = paths;
            }
        }
        foreach (string path in paths)
            if ((_otherMods || !IsOtherMod(path)) && !_open.Any(a => a.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)) && OpenBundle(path) is { } bundle)
                yield return bundle;
    }
}
