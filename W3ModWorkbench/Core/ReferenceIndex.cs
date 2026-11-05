using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core;

/// <summary>A file that refers to another: the archive it's in (a bundle, or REDkit's r4data) and its path there.</summary>
public sealed record ReferenceSource(string ArchivePath, string Path);

/// <summary>
/// Which files refer to which: every resource's links to the files it uses (its CR2W imports, the hard and soft
/// references an entity, mesh, material, scene and the rest keep), read from the game's bundles, the installed mods'
/// and REDkit's r4data, turned round so a file's users can be looked up. Reading them all takes a few minutes, so each
/// archive's links are kept on disk and read again only when the archive changes (a mod installed or updated).
/// </summary>
public sealed class ReferenceIndex
{
    // 2: the links include those of the CR2W files nested in a file
    private const string Header = "W3MW-REFS 2";

    public static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "W3ModWorkbench", "References");

    // files that never refer to another: their bytes, pictures, sounds, video, text
    private static readonly HashSet<string> NoLinks = new(StringComparer.OrdinalIgnoreCase)
    {
        ".buffer", ".xbm", ".texarray", ".w2cube", ".dds", ".png", ".jpg", ".tga", ".bmp", ".wem", ".bnk", ".ogg", ".wav", ".usm", ".bk2",
        ".csv", ".xml", ".ws", ".txt", ".ini", ".json", ".log", ".w3strings", ".w3speech", ".cache", ".db", ".subs", ".redscripts", ".bin",
    };

    // the loose files worth reading whole, as CR2W files can be nested in them
    private static readonly HashSet<string> HoldsOthers = new(StringComparer.OrdinalIgnoreCase) { ".w2ent", ".w2cent", ".w2l" };

    private readonly Dictionary<string, List<ReferenceSource>> _usedBy;

    private ReferenceIndex(Dictionary<string, List<ReferenceSource>> usedBy, int files)
    {
        _usedBy = usedBy;
        Files = files;
    }

    /// <summary>How many files with links were read.</summary>
    public int Files { get; }

    /// <summary>The files that refer to a path (the game's, backslashes, any case).</summary>
    public IReadOnlyList<ReferenceSource> UsedBy(string path) => _usedBy.GetValueOrDefault(Key(path)) ?? [];

    private static string Key(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>
    /// The paths a file refers to, from its CR2W imports and those of the CR2W files inside it: a cooked entity keeps its
    /// components in one (flatCompiledData), its streamed components and attachments and its cooked effects in more, and
    /// a world layer's entities theirs. None for a file that isn't a resource.
    /// </summary>
    public static List<string> LinksOf(byte[] data)
    {
        if (!Cr2w.Cr2wFile.IsCr2w(data)) return [];
        var links = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var import in Cr2w.Cr2wFile.Read(data).Imports)
                if (import.Path.Length > 0 && seen.Add(import.Path)) links.Add(import.Path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a file whose header can't be read names nothing itself as far as the index knows
        }
        AddNested(data, links, seen);
        return links;
    }

    /// <summary>
    /// The imports of every CR2W file inside another, however deep: they're found by their bytes rather than by the
    /// properties that hold them, so a property that's new here can't hide any. Bytes that only look like the start of
    /// one are passed over, as their tables don't fit.
    /// </summary>
    private static void AddNested(byte[] data, List<string> links, HashSet<string> seen)
    {
        int at = 4;
        while (at < data.Length)
        {
            int found = data.AsSpan(at).IndexOf("CR2W"u8);
            if (found < 0) return;
            int start = at + found;
            at = start + 4;
            if (data.Length - start < 44) return;
            int version = BitConverter.ToInt32(data, start + 4);
            if (version > 170 || version < Cr2w.Cr2wFile.FirstVersion) continue;
            if (version > Cr2w.Cr2wLegacy.LastVersion) AddImports(data, start, links, seen);
            else
                try
                {
                    // older layouts keep their links differently, and are few
                    foreach (var import in Cr2w.Cr2wFile.Read(data[start..]).Imports)
                        if (import.Path.Length > 0 && seen.Add(import.Path)) links.Add(import.Path);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }

    /// <summary>
    /// The paths a loose file refers to. Only a file that can hold others is read whole; for the rest its header is
    /// enough (the string and import tables come before its objects), so a big source mesh costs a few kilobytes.
    /// Older layouts (154 and before) are read whole.
    /// </summary>
    public static List<string> LinksOfFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            var head = new byte[Math.Min(160, stream.Length)];
            stream.ReadExactly(head);
            if (!Cr2w.Cr2wFile.IsCr2w(head)) return [];
            int version = BitConverter.ToInt32(head, 4);
            if (version <= Cr2w.Cr2wLegacy.LastVersion || head.Length < 160 || HoldsOthers.Contains(Path.GetExtension(path))) return LinksOf(File.ReadAllBytes(path));
            // the strings table (0) and the imports table (2), each an offset, a count and a checksum after the header
            long stringsEnd = (long)BitConverter.ToInt32(head, 40) + BitConverter.ToInt32(head, 44);
            long importsEnd = BitConverter.ToInt32(head, 64) + BitConverter.ToInt32(head, 68) * 8L;
            long end = Math.Max(stringsEnd, importsEnd);
            if (end <= 0 || end > stream.Length || end > 64 << 20) return LinksOf(File.ReadAllBytes(path));
            var data = new byte[end];
            stream.Position = 0;
            stream.ReadExactly(data);
            var links = new List<string>();
            AddImports(data, 0, links, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return links;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// The import paths of a CR2W file of the current layout starting at <paramref name="start"/>, read from its header's
    /// strings and imports tables alone. Nothing is added when those tables don't fit in the bytes there.
    /// </summary>
    private static void AddImports(byte[] data, int start, List<string> links, HashSet<string> seen)
    {
        int room = data.Length - start;
        if (room < 160) return;
        int stringsAt = BitConverter.ToInt32(data, start + 40), stringsSize = BitConverter.ToInt32(data, start + 44);
        int importsAt = BitConverter.ToInt32(data, start + 64), count = BitConverter.ToInt32(data, start + 68);
        if (stringsAt < 0 || stringsSize < 0 || importsAt < 0 || count < 0 || (long)stringsAt + stringsSize > room || importsAt + count * 8L > room) return;
        for (int i = 0; i < count; i++)
        {
            int offset = BitConverter.ToInt32(data, start + importsAt + i * 8);
            if (offset < 0 || offset >= stringsSize) continue;
            var from = data.AsSpan(start + stringsAt + offset, stringsSize - offset);
            int zero = from.IndexOf((byte)0);
            string text = Encoding.UTF8.GetString(zero < 0 ? from : from[..zero]);
            if (text.Length > 0 && seen.Add(text)) links.Add(text);
        }
    }

    /// <summary>Whether a file can refer to others, as far as its name says.</summary>
    private static bool MayLink(string path) => !NoLinks.Contains(Path.GetExtension(path)) && !path.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- building

    /// <summary>
    /// The index over some archives (bundles and REDkit's depot; others are passed over): each archive's links from its
    /// cache when the archive hasn't changed, otherwise read from it and cached. <paramref name="progress"/> is told how
    /// many files are done of how many, and which archive is being read.
    /// </summary>
    public static Task<ReferenceIndex> BuildAsync(IEnumerable<IArchive> archives, IProgress<(int Done, int Total, string Name)>? progress, CancellationToken token) => Task.Run(() =>
    {
        var all = archives.DistinctBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        var list = all.Where(a => a is Bundle or RedkitDepot).ToList();
        Directory.CreateDirectory(CacheFolder);
        var work = list.Select(a => (Archive: a, Files: a.Entries.Where(e => MayLink(e.Path)).ToList())).ToList();
        var counter = new Counter(work.Sum(w => w.Files.Count), progress);
        var perArchive = new ConcurrentBag<(string Archive, List<(string Source, List<string> Targets)> Links)>();
        // the bundles one a thread, the biggest first; then REDkit's loose files, spread over every thread
        Parallel.ForEach(work.Where(w => w.Archive is not RedkitDepot).OrderByDescending(w => SafeLength(w.Archive.FilePath)),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = token },
            w => perArchive.Add((w.Archive.FilePath, LinksIn(w.Archive, w.Files, counter, token))));
        foreach (var w in work.Where(w => w.Archive is RedkitDepot))
            perArchive.Add((w.Archive.FilePath, LinksIn(w.Archive, w.Files, counter, token)));
        counter.Finish();

        // what the game or a mod has at the path as written; REDkit's copies aren't what the game loads
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in all.Where(a => a is not RedkitDepot))
            foreach (var entry in archive.Entries) exists.Add(entry.Path);

        var usedBy = new Dictionary<string, List<ReferenceSource>>(StringComparer.Ordinal);
        void Add(string target, ReferenceSource from)
        {
            string key = Key(target);
            if (!usedBy.TryGetValue(key, out var users)) usedBy[key] = users = [];
            // the same file naming both a path and the old folder's name for it is one user
            if (users.Count == 0 || !ReferenceEquals(users[^1], from)) users.Add(from);
        }
        int files = 0;
        foreach (var (archivePath, links) in perArchive)
            foreach (var (source, targets) in links)
            {
                files++;
                var from = new ReferenceSource(archivePath, source);
                foreach (string target in targets)
                {
                    Add(target, from);
                    // a path naming a DLC folder merged into the game means the file now at the path without it
                    if (Cr2w.ResourceLocator.MergedPath(target) is { } merged && !exists.Contains(target)) Add(merged, from);
                }
            }
        return new ReferenceIndex(usedBy, files);
    }, token);

    /// <summary>Counts the files read across threads, telling the progress every few hundred with the archive they're from.</summary>
    private sealed class Counter(int total, IProgress<(int, int, string)>? progress)
    {
        private int _done;
        private int _told;

        public void Add(int files, string name)
        {
            int done = Interlocked.Add(ref _done, files);
            int told = Volatile.Read(ref _told);
            if (done - told >= 500 && Interlocked.CompareExchange(ref _told, done, told) == told) progress?.Report((done, total, name));
        }

        public void Finish() => progress?.Report((total, total, ""));
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>What an archive's files refer to: from its cache when it's the same archive, otherwise read and cached.</summary>
    private static List<(string Source, List<string> Targets)> LinksIn(IArchive archive, List<ArchiveEntry> files, Counter counter, CancellationToken token)
    {
        string name = archive is RedkitDepot ? L.T("ReferenceIndex.REDkitsR4data") : Path.GetFileName(archive.FilePath);
        string signature = Signature(archive);
        string cache = Path.Combine(CacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(archive.FilePath.ToLowerInvariant())))[..24] + ".refs.gz");
        if (ReadCache(cache, signature) is { } cached)
        {
            counter.Add(files.Count, name);
            return cached;
        }

        var links = new List<(string, List<string>)>();
        if (archive is RedkitDepot)
        {
            // loose files: only their headers, several at once
            var found = new ConcurrentBag<(string, List<string>)>();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount), CancellationToken = token }, entry =>
            {
                if (entry is Scripts.LooseScriptEntry loose && LinksOfFile(loose.FullPath) is { Count: > 0 } targets) found.Add((entry.Path, targets));
                counter.Add(1, name);
            });
            links.AddRange(found);
        }
        else
            using (var stream = archive.ReadsFromDisk ? new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16) : null)
                foreach (var entry in files.OrderBy(e => e.SortKey))
                {
                    token.ThrowIfCancellationRequested();
                    counter.Add(1, name);
                    byte[] data;
                    try { data = entry.Read(stream); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException) { continue; }
                    if (LinksOf(data) is { Count: > 0 } targets) links.Add((entry.Path, targets));
                }
        WriteCache(cache, signature, links);
        return links;
    }

    /// <summary>What says an archive is the one cached: a bundle's size and time, REDkit's depot's file count and total size.</summary>
    private static string Signature(IArchive archive)
    {
        if (archive is RedkitDepot depot) return $"{depot.FilePath}|{depot.Entries.Count}|{depot.Entries.Sum(e => (long)e.Size)}";
        var info = new FileInfo(archive.FilePath);
        return $"{archive.FilePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static List<(string, List<string>)>? ReadCache(string file, string signature)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var reader = new StreamReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress), Encoding.UTF8);
            if (reader.ReadLine() != Header + "\t" + signature) return null;
            var links = new List<(string, List<string>)>();
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length > 1) links.Add((parts[0], [.. parts[1..]]));
            }
            return links;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache(string file, string signature, List<(string Source, List<string> Targets)> links)
    {
        try
        {
            string temp = file + ".tmp";
            using (var writer = new StreamWriter(new GZipStream(File.Create(temp), CompressionLevel.Fastest), new UTF8Encoding(false)))
            {
                writer.WriteLine(Header + "\t" + signature);
                foreach (var (source, targets) in links) writer.WriteLine(source + "\t" + string.Join('\t', targets));
            }
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes the cached links, so the next index reads every archive again.</summary>
    public static void ClearCache()
    {
        try
        {
            if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
