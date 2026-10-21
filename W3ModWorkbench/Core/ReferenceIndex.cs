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
    private const string Header = "W3MW-REFS 1";

    public static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "W3ModWorkbench", "References");

    // files that never refer to another: their bytes, pictures, sounds, video, text
    private static readonly HashSet<string> NoLinks = new(StringComparer.OrdinalIgnoreCase)
    {
        ".buffer", ".xbm", ".texarray", ".w2cube", ".dds", ".png", ".jpg", ".tga", ".bmp", ".wem", ".bnk", ".ogg", ".wav", ".usm", ".bk2",
        ".csv", ".xml", ".ws", ".txt", ".ini", ".json", ".log", ".w3strings", ".w3speech", ".cache", ".db", ".subs", ".redscripts", ".bin",
    };

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

    /// <summary>The paths a file refers to, from its CR2W imports; none for a file that isn't a resource.</summary>
    public static List<string> LinksOf(byte[] data)
    {
        if (!Cr2w.Cr2wFile.IsCr2w(data)) return [];
        try
        {
            return [.. Cr2w.Cr2wFile.Read(data).Imports.Select(i => i.Path).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a file whose header can't be read refers to nothing as far as the index knows
            return [];
        }
    }

    /// <summary>
    /// The paths a loose file refers to, reading only its header (the string and import tables come before its objects),
    /// so a big source mesh costs a few kilobytes; older layouts (154 and before) are read whole.
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
            if (version <= Cr2w.Cr2wLegacy.LastVersion || head.Length < 160) return LinksOf(File.ReadAllBytes(path));
            // the strings table (0) and the imports table (2), each an offset, a count and a checksum after the header
            long stringsEnd = (long)BitConverter.ToInt32(head, 40) + BitConverter.ToInt32(head, 44);
            long importsEnd = BitConverter.ToInt32(head, 64) + BitConverter.ToInt32(head, 68) * 8L;
            long end = Math.Max(stringsEnd, importsEnd);
            if (end <= 0 || end > stream.Length || end > 64 << 20) return LinksOf(File.ReadAllBytes(path));
            var data = new byte[end];
            stream.Position = 0;
            stream.ReadExactly(data);
            return Imports(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>The import paths in the start of a CR2W file of the current layout, as far as its strings and imports go.</summary>
    private static List<string> Imports(byte[] data)
    {
        int stringsAt = BitConverter.ToInt32(data, 40), stringsSize = BitConverter.ToInt32(data, 44);
        int importsAt = BitConverter.ToInt32(data, 64), count = BitConverter.ToInt32(data, 68);
        var links = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int offset = BitConverter.ToInt32(data, importsAt + i * 8);
            if (offset < 0 || offset >= stringsSize) continue;
            var from = data.AsSpan(stringsAt + offset, stringsSize - offset);
            int zero = from.IndexOf((byte)0);
            string text = Encoding.UTF8.GetString(zero < 0 ? from : from[..zero]);
            if (text.Length > 0 && !links.Contains(text, StringComparer.OrdinalIgnoreCase)) links.Add(text);
        }
        return links;
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
        var list = archives.Where(a => a is Bundle or RedkitDepot).DistinctBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
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

        var usedBy = new Dictionary<string, List<ReferenceSource>>(StringComparer.Ordinal);
        int files = 0;
        foreach (var (archivePath, links) in perArchive)
            foreach (var (source, targets) in links)
            {
                files++;
                var from = new ReferenceSource(archivePath, source);
                foreach (string target in targets)
                {
                    string key = Key(target);
                    if (!usedBy.TryGetValue(key, out var users)) usedBy[key] = users = [];
                    users.Add(from);
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
