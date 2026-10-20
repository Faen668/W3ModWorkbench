using System.IO;
using W3ModWorkbench.Core.Textures;

namespace W3ModWorkbench.Core;

/// <summary>
/// Every file the game has, as one archive to browse: the bundles and texture caches of the installed mods, the DLC and
/// content0, each game path once, from where the game would load it (mods first, then DLC, then content0). Each file
/// stays an entry of the archive it's in, so it's read, previewed, opened and copied as from that archive.
/// </summary>
public sealed class GameFilesArchive : IArchive
{
    private GameFilesArchive(string game, List<ArchiveEntry> entries, List<IArchive> parts, string summary)
    {
        Parts = parts;
        FilePath = game;
        Entries = entries;
        Name = summary;
    }

    /// <summary>The game's folder.</summary>
    public string FilePath { get; }

    /// <summary>What it's made of, such as "content0, 13 DLC, 29 mods".</summary>
    public string Name { get; }

    public string Owner => "Game files";

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    public bool ReadsFromDisk => false;

    /// <summary>The bundles and texture caches it's made of, in the order the game looks in them.</summary>
    public IReadOnlyList<IArchive> Parts { get; }

    private Dictionary<string, List<ArchiveEntry>>? _copies;

    /// <summary>
    /// Every copy of a file the game's archives have, in the order the game looks for it (the first is the one it loads):
    /// a mod's, a DLC's and content0's. A texture in a cache and an .xbm in a bundle share a path but aren't copies, so a
    /// cache's textures are only copies of each other.
    /// </summary>
    public IReadOnlyList<ArchiveEntry> CopiesOf(ArchiveEntry entry)
    {
        if (_copies is null)
        {
            var copies = new Dictionary<string, List<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in Parts)
                foreach (var e in part.Entries)
                {
                    string key = (e is TextureEntry ? "cache:" : "") + e.Path;
                    if (!copies.TryGetValue(key, out var list)) copies[key] = list = [];
                    list.Add(e);
                }
            _copies = copies;
        }
        return _copies.TryGetValue((entry is TextureEntry ? "cache:" : "") + entry.Path, out var found) ? found : [entry];
    }

    /// <summary>
    /// Reads the indexes of every bundle and texture cache in the game's folder. A texture is listed from a cache only
    /// when no bundle has its .xbm, as the bundle's is the game file and the cache holds its pixels. Archives that can't
    /// be read are left out, and <paramref name="progress"/> is told the name of each as it's read.
    /// </summary>
    public static GameFilesArchive Open(string game, Action<string>? progress = null)
    {
        // the game loads mods over DLC over its own content, so the first archive with a path is the one it uses
        var folders = new List<(string Content, string Kind)>();
        foreach (var (root, kind) in new[] { ("mods", "mod"), ("dlc", "DLC") })
        {
            string path = Path.Combine(game, root);
            if (!Directory.Exists(path)) continue;
            foreach (string mod in Directory.GetDirectories(path).Order(StringComparer.OrdinalIgnoreCase))
                folders.Add((Path.Combine(mod, "content"), kind));
        }
        folders.Add((Path.Combine(game, "content", "content0", "bundles"), "content0"));

        var entries = new List<ArchiveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundles = new List<IArchive>();
        var caches = new List<TextureCacheArchive>();
        var used = new Dictionary<string, int>();
        foreach (var (content, kind) in folders)
        {
            if (!Directory.Exists(content)) continue;
            bool any = false;
            foreach (string file in Directory.GetFiles(content, "*.bundle").Order(StringComparer.OrdinalIgnoreCase))
            {
                progress?.Invoke(Path.GetFileName(file));
                Bundle bundle;
                try { bundle = Bundle.Open(file); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
                any = true;
                bundles.Add(bundle);
                foreach (var entry in bundle.Entries)
                    if (seen.Add(entry.Path)) entries.Add(entry);
            }
            // content0's cache is beside its bundles folder, a mod's beside its bundles
            string cache = kind == "content0" ? Path.Combine(Path.GetDirectoryName(content)!, "texture.cache") : Path.Combine(content, "texture.cache");
            if (File.Exists(cache) && TextureCacheArchive.LooksLikeCache(cache))
                try
                {
                    caches.Add(TextureCacheArchive.Open(cache));
                    any = true;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            if (any) used[kind] = used.GetValueOrDefault(kind) + 1;
        }
        foreach (var cache in caches)
            foreach (var entry in cache.Entries)
                if (seen.Add(entry.Path)) entries.Add(entry);

        var parts = new List<string>();
        if (used.ContainsKey("content0")) parts.Add("content0");
        if (used.GetValueOrDefault("DLC") is > 0 and var dlc) parts.Add($"{dlc} DLC");
        if (used.GetValueOrDefault("mod") is > 0 and var mods) parts.Add(Format.Count(mods, "mod"));
        return new GameFilesArchive(game, entries, [.. bundles, .. caches], string.Join(", ", parts));
    }
}
