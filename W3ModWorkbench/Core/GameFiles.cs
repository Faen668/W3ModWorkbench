using System.IO;
using W3ModWorkbench.Core.Sounds;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core;

/// <summary>
/// The game's own files, as one archive to browse: the bundles, texture caches, sound caches and voice lines of content0 and the official DLC
/// (<see cref="OfficialDlc"/>), each game path once, from where the game would load it (the DLC over content0). Nothing
/// a mod adds is in it, in mods or installed as DLC. Each file stays an entry of the archive it's in, so it's read,
/// previewed, opened and copied as from that archive.
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

    public string Owner => L.T("GameFiles.GameFiles");

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    public bool ReadsFromDisk => false;

    /// <summary>The bundles, texture caches, sound caches and speech files it's made of, in the order the game looks in them.</summary>
    public IReadOnlyList<IArchive> Parts { get; }

    /// <summary>
    /// Reads the indexes of every bundle and texture cache in the game's folder. A texture is listed from a cache only
    /// when no bundle has its .xbm, as the bundle's is the game file and the cache holds its pixels. Archives that can't
    /// be read are left out, and <paramref name="progress"/> is told the name of each as it's read.
    /// </summary>
    public static GameFilesArchive Open(string game, Action<string>? progress = null)
    {
        // the official DLC over the game's own content, so the first archive with a path is the one it uses; what
        // mods put in dlc isn't the game's
        var folders = new List<(string Content, string Kind)>();
        string dlcFolder = Path.Combine(game, "dlc");
        if (Directory.Exists(dlcFolder))
            foreach (string dlc in Directory.GetDirectories(dlcFolder).Order(StringComparer.OrdinalIgnoreCase))
                if (OfficialDlc.Is(game, Path.GetFileName(dlc))) folders.Add((Path.Combine(dlc, "content"), "DLC"));
        folders.Add((Path.Combine(game, "content", "content0", "bundles"), "content0"));

        var entries = new List<ArchiveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundles = new List<IArchive>();
        var caches = new List<TextureCacheArchive>();
        var sounds = new List<IArchive>();
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
            // its sounds and voice lines, beside its texture cache: the official DLC's speech files are empty
            string folder = kind == "content0" ? Path.GetDirectoryName(content)! : content;
            foreach (string file in new[] { Path.Combine(folder, "soundspc.cache") }.Concat(Directory.Exists(folder) ? Directory.GetFiles(folder, "*pc.w3speech") : []))
                if (File.Exists(file) && new FileInfo(file).Length > 64)
                    try
                    {
                        progress?.Invoke(Path.GetFileName(file));
                        sounds.Add(file.EndsWith(".cache", StringComparison.OrdinalIgnoreCase) ? SoundCacheArchive.Open(file) : SpeechArchive.Open(file));
                        any = true;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) { }
            if (any) used[kind] = used.GetValueOrDefault(kind) + 1;
        }
        foreach (var cache in caches)
            foreach (var entry in cache.Entries)
                if (seen.Add(entry.Path)) entries.Add(entry);
        foreach (var archive in sounds)
            foreach (var entry in archive.Entries)
                if (seen.Add(entry.Path)) entries.Add(entry);

        var parts = new List<string>();
        if (used.ContainsKey("content0")) parts.Add("content0");
        if (used.GetValueOrDefault("DLC") is > 0 and var dlcs) parts.Add(L.T("GameFiles.DLC", dlcs));
        return new GameFilesArchive(game, entries, [.. bundles, .. caches, .. sounds], string.Join(", ", parts));
    }
}
