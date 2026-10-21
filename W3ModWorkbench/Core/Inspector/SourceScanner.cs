using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Inspector;

/// <summary>
/// Reads a folder (or a single bundle or compiled script file) into <see cref="InspectUnit"/>s: loose scripts and XML,
/// the decompiled scripts of every .rsblob, and every file inside every .bundle, each with its SHA-256.
/// </summary>
public static class SourceScanner
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".csv", ".txt", ".md", ".ini", ".settings", ".json", ".yml", ".yaml", ".redscripts", ".cfg" };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", ".svn" };
    private static readonly HashSet<string> SkippedFiles = new(StringComparer.OrdinalIgnoreCase) { "thumbs.db", "desktop.ini", ".ds_store" };

    /// <summary>Past this a file that isn't a bundle is hashed in a stream instead of being read whole.</summary>
    private const long ReadWholeLimit = 32 * 1024 * 1024;

    public static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    public static SourceScan Scan(string path, IProgress<InspectProgress>? progress, string phase, CancellationToken token)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        bool single = File.Exists(path);
        string root = single ? Path.GetDirectoryName(path)! : path;
        var scan = new SourceScan { Path = path, Name = Path.GetFileName(path) is { Length: > 0 } name ? name : path };

        var files = new List<string>();
        if (single) files.Add(path);
        else Enumerate(root, files, scan.Warnings, token);
        scan.Files = files.Count;

        var units = new ConcurrentBag<InspectUnit>();
        var warnings = new ConcurrentBag<string>();
        int bundles = 0, blobs = 0, done = 0;
        long bytes = 0;
        var oldest = DateTime.MaxValue;
        var newest = DateTime.MinValue;
        var dateLock = new object();

        Parallel.ForEach(files, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) }, file =>
        {
            string relative = Path.GetRelativePath(root, file);
            DateTime modified;
            long size;
            try
            {
                var info = new FileInfo(file);
                modified = info.LastWriteTime;
                size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{relative}: {ex.Message}");
                Interlocked.Increment(ref done);
                return;
            }

            lock (dateLock)
            {
                if (modified < oldest) oldest = modified;
                if (modified > newest) newest = modified;
            }
            Interlocked.Add(ref bytes, size);

            try
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".bundle")
                {
                    Interlocked.Increment(ref bundles);
                    ReadBundle(file, relative, modified, units, token);
                }
                else if (ext == ".rsblob")
                {
                    Interlocked.Increment(ref blobs);
                    ReadBlob(file, relative, modified, units);
                }
                else
                {
                    ReadLoose(file, relative, ext, size, modified, units);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"{relative}: {ex.Message}");
            }

            int finished = Interlocked.Increment(ref done);
            if (finished % 8 == 0 || finished == files.Count)
                progress?.Report(new InspectProgress(phase, (double)finished / Math.Max(1, files.Count), relative));
        });

        scan.Units.AddRange(units.OrderBy(u => u.Key, StringComparer.Ordinal).ThenBy(u => u.Origin, StringComparer.Ordinal));
        scan.Warnings.AddRange(warnings.Order(StringComparer.Ordinal));
        scan.Bundles = bundles;
        scan.Blobs = blobs;
        scan.Bytes = bytes;
        if (newest != DateTime.MinValue)
        {
            scan.Oldest = oldest;
            scan.Newest = newest;
        }
        return scan;
    }

    private static void Enumerate(string folder, List<string> files, List<string> warnings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder))
                if (!SkippedFiles.Contains(Path.GetFileName(file))) files.Add(file);
            foreach (string sub in Directory.EnumerateDirectories(folder))
            {
                if (SkippedFolders.Contains(Path.GetFileName(sub))) continue;
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; // a link could loop back
                Enumerate(sub, files, warnings, token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{folder}: {ex.Message}");
        }
    }

    private static string KeyOf(string path) => path.Replace('/', '\\').Trim('\\').ToLowerInvariant();

    private static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static void ReadLoose(string file, string relative, string ext, long size, DateTime modified, ConcurrentBag<InspectUnit> units)
    {
        var kind = KindOf(ext);
        if (kind == UnitKind.Asset && size > ReadWholeLimit)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            units.Add(new InspectUnit { Kind = kind, Origin = relative, Key = KeyOf(relative), Size = size, Hash = Convert.ToHexString(SHA256.HashData(stream)), Modified = modified });
            return;
        }
        byte[] data = File.ReadAllBytes(file);
        units.Add(MakeUnit(kind, relative, KeyOf(relative), data, modified));
    }

    private static void ReadBlob(string file, string relative, DateTime modified, ConcurrentBag<InspectUnit> units)
    {
        var archive = ScriptArchive.Open(file);
        foreach (var entry in archive.Entries)
            units.Add(MakeUnit(UnitKind.Script, $"{relative} ▸ {entry.Path}", KeyOf(entry.Path), entry.Read(null), modified));
    }

    private static void ReadBundle(string file, string relative, DateTime modified, ConcurrentBag<InspectUnit> units, CancellationToken token)
    {
        var bundle = Bundle.Open(file);
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (var entry in bundle.Entries.OrderBy(e => e.SortKey))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                byte[] data = entry.Read(stream);
                units.Add(MakeUnit(KindOf(Path.GetExtension(entry.Path)), $"{relative} ▸ {entry.Path}", KeyOf(entry.Path), data, modified));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                // an entry that won't unpack is skipped; the rest of the bundle is still read
                units.Add(new InspectUnit { Kind = UnitKind.Asset, Origin = $"{relative} ▸ {entry.Path}", Key = KeyOf(entry.Path), Size = entry.Size, Hash = $"unreadable:{ex.GetType().Name}:{bundle.Name}:{entry.Path}", Modified = modified });
            }
        }
    }

    private static UnitKind KindOf(string ext) => ext.ToLowerInvariant() switch
    {
        ".ws" => UnitKind.Script,
        ".xml" => UnitKind.Xml,
        var e when TextExtensions.Contains(e) => UnitKind.Text,
        _ => UnitKind.Asset,
    };

    private static InspectUnit MakeUnit(UnitKind kind, string origin, string key, byte[] data, DateTime modified)
    {
        var unit = new InspectUnit
        {
            Kind = kind,
            Origin = origin,
            Key = key,
            Size = data.Length,
            Hash = HashOf(data),
            Modified = modified,
            Text = kind == UnitKind.Asset ? null : TextDecoding.Decode(data),
        };
        switch (kind)
        {
            case UnitKind.Script:
                unit.Script = ScriptAnalyzer.Analyze(unit.Text!);
                unit.Sig1 = unit.Script.RawShingles;
                unit.Sig2 = unit.Script.AbstractShingles;
                break;
            case UnitKind.Xml:
                unit.Lines = LineAnalyzer.AnalyzeXml(unit.Text!);
                unit.Sig1 = unit.Lines.Lines;
                break;
            case UnitKind.Text:
                unit.Lines = LineAnalyzer.AnalyzeText(unit.Text!);
                unit.Sig1 = unit.Lines.Lines;
                break;
        }
        return unit;
    }
}

/// <summary>
/// The game's own scripts, read once and kept: code the game already has is no sign of copying, because every mod that
/// wraps or replaces a game function starts from it.
/// </summary>
public sealed class GameBaseline
{
    private static readonly Dictionary<string, GameBaseline> Cache = new(StringComparer.OrdinalIgnoreCase);

    public int Files { get; private init; }
    public ulong[] Raw { get; private init; } = [];
    public ulong[] Shape { get; private init; } = [];
    public HashSet<ulong> FunctionBodies { get; private init; } = [];
    public HashSet<ulong> FunctionShapes { get; private init; } = [];
    public HashSet<string> Comments { get; private init; } = [];
    public HashSet<string> Strings { get; private init; } = [];
    public HashSet<string> Classes { get; private init; } = [];
    public HashSet<string> FunctionNames { get; private init; } = [];

    public static GameBaseline Load(string gameFolder, IProgress<InspectProgress>? progress, CancellationToken token)
    {
        string scripts = Path.Combine(gameFolder, "content", "content0", "scripts");
        lock (Cache)
        {
            if (Cache.TryGetValue(scripts, out var cached)) return cached;
        }

        var files = Directory.EnumerateFiles(scripts, "*.ws", SearchOption.AllDirectories).ToList();
        var infos = new ConcurrentBag<ScriptInfo>();
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) }, file =>
        {
            try
            {
                infos.Add(ScriptAnalyzer.Analyze(TextDecoding.Decode(File.ReadAllBytes(file))));
            }
            catch (IOException)
            {
                // an unreadable game file just leaves a gap in what's ignored
            }
            int finished = Interlocked.Increment(ref done);
            if (finished % 40 == 0) progress?.Report(new InspectProgress(L.T("SourceScanner.ReadingGamesOwnScripts"), (double)finished / files.Count, ""));
        });

        var baseline = new GameBaseline
        {
            Files = files.Count,
            Raw = Hashing.Distinct([.. infos.SelectMany(i => i.RawShingles)]),
            Shape = Hashing.Distinct([.. infos.SelectMany(i => i.AbstractShingles)]),
            FunctionBodies = [.. infos.SelectMany(i => i.Functions).Select(f => f.RawHash)],
            FunctionShapes = [.. infos.SelectMany(i => i.Functions).Select(f => f.AbstractHash)],
            Comments = [.. infos.SelectMany(i => i.Comments)],
            Strings = [.. infos.SelectMany(i => i.Strings)],
            Classes = [.. infos.SelectMany(i => i.Classes)],
            FunctionNames = [.. infos.SelectMany(i => i.Functions).Select(f => f.Title)],
        };
        lock (Cache) Cache[scripts] = baseline;
        return baseline;
    }
}
