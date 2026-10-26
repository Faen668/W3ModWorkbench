using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>What happened to one file: where it was written, or why it wasn't, and a note (values kept as bytes, nothing changed).</summary>
public sealed record Cr2wJsonResult(string Input, string? Output, string? Error, string? Note = null)
{
    public bool Ok => Error is null;
}

/// <summary>Which part of a batch is under way: finding the files in the folders, checking which are the right kind, or the work.</summary>
public enum Cr2wJsonStage
{
    Working,
    Finding,
    Checking,
}

/// <summary>How far a batch is: files done of the total (for Finding, files found so far with no total), and the file it's on.</summary>
public sealed record Cr2wJsonProgress(int Done, int Total, string Current, Cr2wJsonStage Stage = Cr2wJsonStage.Working);

/// <summary>Which files a folder gives a batch: CR2W files, converted JSON files, or both (for bulk edits).</summary>
public enum Cr2wInputKind
{
    Cr2w,
    Json,
    Both,
}

/// <summary>
/// Which files in a folder a batch takes, by their path below the folder (folders separated by <c>/</c>): those
/// <see cref="Only"/> matches, when it's given, and <see cref="Skip"/> doesn't. Regular expressions, case-insensitive,
/// matching anywhere in the path unless anchored: <c>\.w2scene$</c>, <c>.*w2scene</c>, <c>^quests/</c>.
/// Files given one by one are always taken.
/// </summary>
public sealed class Cr2wFileFilter
{
    public Regex? Only { get; }
    public Regex? Skip { get; }

    private Cr2wFileFilter(Regex? only, Regex? skip) => (Only, Skip) = (only, skip);

    /// <summary>The filter for the two patterns, or null when both are empty. A pattern that isn't a regular expression throws, saying why.</summary>
    public static Cr2wFileFilter? Make(string? only, string? skip)
    {
        var a = Pattern(only);
        var b = Pattern(skip);
        return a is null && b is null ? null : new Cr2wFileFilter(a, b);
    }

    private static Regex? Pattern(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return new Regex(text.Trim(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex) { throw new ArgumentException(L.T("Cr2wFileFilter.BadPattern", text.Trim(), ex.Message), ex); }
    }

    /// <summary>Whether a file found in a folder is taken: by its path below that folder.</summary>
    public bool Takes(string folder, string file)
    {
        string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
        try { return (Only?.IsMatch(relative) ?? true) && !(Skip?.IsMatch(relative) ?? false); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

/// <summary>
/// Converting and bulk editing many files at once, for the CR2W ⇄ JSON window and the command line. A CR2W file
/// <c>name.env</c> becomes <c>name.env.json</c> beside it or under an output folder (keeping the folders it was in
/// below the folder that was given), and back. Nothing is ever written over the game's own files.
/// </summary>
public static class Cr2wJsonBatch
{
    public const string Suffix = ".json";

    /// <summary>The files to work on: the files given, and the files in the folders given (with their subfolders when <paramref name="recursive"/>).</summary>
    /// <param name="fromJson">Look for converted JSON files in folders, rather than CR2W ones.</param>
    public static List<(string File, string Root)> Inputs(IEnumerable<string> paths, bool fromJson, bool recursive,
        IProgress<Cr2wJsonProgress>? progress = null, CancellationToken token = default, Cr2wFileFilter? filter = null) =>
        Inputs(paths, fromJson ? Cr2wInputKind.Json : Cr2wInputKind.Cr2w, recursive, progress, token, filter);

    /// <summary>
    /// The files to work on: the files given, as they are, and the files of the kind asked for in the folders given.
    /// A big folder (REDkit's r4data has over 200,000 files) takes a while, so it's said as it goes: the files found,
    /// then each checked, many at once. Files whose extension says they can't be CR2W (pictures, sounds, text) are
    /// passed over without being opened, as are those the <paramref name="filter"/> leaves out.
    /// </summary>
    public static List<(string File, string Root)> Inputs(IEnumerable<string> paths, Cr2wInputKind kind, bool recursive,
        IProgress<Cr2wJsonProgress>? progress = null, CancellationToken token = default, Cr2wFileFilter? filter = null)
    {
        var given = new List<(string File, string Root)>();
        var found = new List<(string File, string Root)>();
        var report = new Throttled(progress);
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (string entry in paths)
        {
            string path = Path.GetFullPath(entry);
            if (File.Exists(path))
            {
                given.Add((path, Path.GetDirectoryName(path)!));
                continue;
            }
            if (!Directory.Exists(path)) continue;
            var inFolder = new List<string>();
            foreach (string file in Directory.EnumerateFiles(path, "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (MightBe(file, kind) && (filter?.Takes(path, file) ?? true)) inFolder.Add(file);
                report.Maybe(() => new Cr2wJsonProgress(found.Count + inFolder.Count, 0, file, Cr2wJsonStage.Finding));
            }
            inFolder.Sort(StringComparer.OrdinalIgnoreCase);
            found.AddRange(inFolder.Select(f => (f, path)));
        }

        // each file's first bytes, many at once; the order found is kept
        var wanted = new bool[found.Count];
        int done = 0;
        Parallel.For(0, found.Count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, i =>
        {
            string file = found[i].File;
            wanted[i] = file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? IsConvertedJson(file) : IsCr2w(file);
            int now = Interlocked.Increment(ref done);
            report.Maybe(() => new Cr2wJsonProgress(now, found.Count, file, Cr2wJsonStage.Checking), now == found.Count);
        });

        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in given)
            if (seen.Add(input.File)) result.Add(input);
        for (int i = 0; i < found.Count; i++)
            if (wanted[i] && seen.Add(found[i].File)) result.Add(found[i]);
        return result;
    }

    /// <summary>The first file in a folder a batch of this kind would take, without looking through the rest.</summary>
    public static string? FirstInput(string folder, Cr2wInputKind kind, bool recursive, Cr2wFileFilter? filter = null)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        return Directory.EnumerateFiles(folder, "*", options)
            .FirstOrDefault(f => MightBe(f, kind) && (filter?.Takes(folder, f) ?? true)
                && (f.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? IsConvertedJson(f) : IsCr2w(f)));
    }

    /// <summary>Whether a file in a folder is worth opening for this kind of batch: by its name alone.</summary>
    private static bool MightBe(string file, Cr2wInputKind kind)
    {
        bool json = file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
        return kind switch
        {
            Cr2wInputKind.Json => json,
            Cr2wInputKind.Cr2w => !json && !NeverCr2w.Contains(Path.GetExtension(file)),
            _ => json || !NeverCr2w.Contains(Path.GetExtension(file)),
        };
    }

    /// <summary>Extensions of files that are never CR2W: pictures, sounds, video, text and source, models, programs.</summary>
    private static readonly HashSet<string> NeverCr2w = new(StringComparer.OrdinalIgnoreCase) // loc-ignore-start
    {
        ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".psd", ".exr", ".hdr",
        ".wem", ".bnk", ".wav", ".ogg", ".mp3", ".opus", ".pck", ".usm", ".bik", ".mp4", ".avi",
        ".txt", ".xml", ".csv", ".ini", ".ws", ".log", ".md", ".html", ".htm", ".yml", ".yaml", ".lua", ".py", ".bat", ".cmd",
        ".fbx", ".obj", ".nif", ".re", ".apb", ".apx", ".max", ".ma", ".mb", ".blend", ".gltf", ".glb",
        ".exe", ".dll", ".pdb", ".zip", ".7z", ".rar", ".swf", ".ttf", ".otf", ".fnt",
    }; // loc-ignore-end

    /// <summary>
    /// Passes progress on at most about ten times a second, plus the last: a scan of 200,000 files would otherwise
    /// queue as many updates for the window.
    /// </summary>
    private sealed class Throttled(IProgress<Cr2wJsonProgress>? progress)
    {
        private long _next;

        public void Maybe(Func<Cr2wJsonProgress> make, bool last = false)
        {
            if (progress is null) return;
            long now = Environment.TickCount64, next = Interlocked.Read(ref _next);
            if (!last && (now < next || Interlocked.CompareExchange(ref _next, now + 100, next) != next)) return;
            progress.Report(make());
        }
    }

    private static readonly HashSet<string> Cr2wExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".w2mi", ".w2mat", ".w2ent", ".w2mesh", ".xbm", ".w2scene", ".journal", ".w2quest", ".w2phase", ".w2fx", ".w2anims",
        ".w2l", ".w2w", ".redswf", ".w2rig", ".w2beh", ".w2behtree", ".w2ter", ".w2cube", ".w2job", ".w2comm", ".w2p", ".w2mg",
        ".w2em", ".w2cutscene", ".w2ragdoll", ".w2animev", ".vbrush", ".w2sm", ".w2dyng", ".w2steer", ".w2je", ".w2cl", ".w2srt",
        ".w2fnt", ".w2lc", ".w2ow", ".w2spe", ".w3dyng", ".w2pe", ".w3ac", ".w3fac", ".w3hud", ".w3tutorial", ".w3gld", ".w3oprt",
        // the rest of the CR2W kinds in the game's bundles
        ".flyr", ".w3occlusion", ".w3occlusiondef", ".srt", ".redfur", ".spawntree", ".cellmap", ".reddest", ".texarray", ".redapex",
        ".redcloth", ".w2sf", ".redgame", ".w2cent", ".w3app", ".menu", ".popup", ".hud", ".guiconfig", ".reddlc", ".formation",
        ".grassmask", ".w3simplex", ".redexp", ".redwpset", ".w2qm", ".w2am", ".w2dset", ".redicsv",
    };

    /// <summary>Whether a file's name says it's a CR2W resource (its bytes say for sure).</summary>
    public static bool LooksLikeCr2w(string name) => Cr2wExtensions.Contains(Path.GetExtension(name));

    /// <summary>Whether a file on disk starts like a CR2W file.</summary>
    public static bool IsCr2w(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[4];
            return stream.Length >= 160 && stream.Read(head) == 4 && head.SequenceEqual("CR2W"u8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Whether a .json file was made by the converter: it says so near its start.</summary>
    public static bool IsConvertedJson(string file)
    {
        if (!file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            var start = new char[256];
            int read = reader.ReadBlock(start, 0, start.Length);
            return new string(start, 0, read).Contains(Cr2wJson.FormatName, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Where a file's result goes: beside it, or under <paramref name="outputFolder"/> at the place it had under the
    /// folder it was found in. A CR2W file gains <c>.json</c>; a JSON file loses it.
    /// </summary>
    public static string OutputPath(string file, string root, string? outputFolder, string? newName = null)
    {
        newName ??= Path.GetFileName(file);
        if (string.IsNullOrWhiteSpace(outputFolder)) return Path.Combine(Path.GetDirectoryName(file)!, newName);
        string relative = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
        return Path.GetFullPath(Path.Combine(outputFolder, relative == "." ? "" : relative, newName));
    }

    public static string JsonName(string file) => Path.GetFileName(file) + Suffix;

    public static string Cr2wName(string file)
    {
        string name = Path.GetFileName(file);
        return name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? name[..^Suffix.Length] : name + ".cr2w";
    }

    // ---------------------------------------------------------------- converting

    /// <summary>A CR2W file's JSON, checked to come back as the same bytes. The note says what was kept as bytes.</summary>
    public static string ToJsonText(byte[] data, string? source, out string? note)
    {
        var file = Cr2wFile.Read(data);
        string json = Cr2wJson.ToJsonText(file, out var report, source);
        // the whole file must come back as it was, or it isn't given out as if it would
        if (!Cr2wJson.FromJsonText(json).AsSpan().SequenceEqual(data))
            throw new InvalidDataException(L.T("Cr2wJsonBatch.WouldntConvertBackSame"));
        note = report.FullyReadable ? null
            : L.T("Cr2wJsonBatch.KeptAsBytes", report.RawValues + report.RawObjects, string.Join(", ", report.RawPaths.Take(3)) + (report.RawPaths.Count > 3 ? ", …" : ""));
        return json;
    }

    public static List<Cr2wJsonResult> ToJson(IReadOnlyList<(string File, string Root)> inputs, string? outputFolder, bool overwrite,
        IProgress<Cr2wJsonProgress>? progress = null, CancellationToken token = default) =>
        Each(inputs, progress, token, (file, root) =>
        {
            string output = OutputPath(file, root, outputFolder, JsonName(file));
            if (!overwrite && File.Exists(output)) return new Cr2wJsonResult(file, null, L.T("Cr2wJsonBatch.JsonFileThereAlready"));
            string json = ToJsonText(File.ReadAllBytes(file), FileSafety.GamePathOf(file), out string? note);
            Write(output, Encoding.UTF8.GetBytes(json));
            return new Cr2wJsonResult(file, output, null, note);
        });

    /// <param name="asLatest">Write files of older versions as version 164 (see <see cref="Cr2wJson.UpgradeToLatest(JsonObject, out Cr2wUpgradeReport)"/>).</param>
    public static List<Cr2wJsonResult> FromJson(IReadOnlyList<(string File, string Root)> inputs, string? outputFolder, bool overwrite,
        IProgress<Cr2wJsonProgress>? progress = null, CancellationToken token = default, bool asLatest = false) =>
        Each(inputs, progress, token, (file, root) =>
        {
            string output = OutputPath(file, root, outputFolder, Cr2wName(file));
            if (!overwrite && File.Exists(output)) return new Cr2wJsonResult(file, null, L.T("Cr2wJsonBatch.ThereAlreadyTurnOverwrite", Path.GetFileName(output)));
            Write(output, FromJsonText(File.ReadAllText(file, Encoding.UTF8), asLatest, out string? note));
            return new Cr2wJsonResult(file, output, null, note);
        });

    /// <summary>
    /// A converted JSON file's text as CR2W; with <paramref name="asLatest"/>, a file of an older version is written as
    /// version 164, and the note says what that changed.
    /// </summary>
    public static byte[] FromJsonText(string json, bool asLatest, out string? note)
    {
        note = null;
        if (!asLatest) return Cr2wJson.FromJsonText(json);
        byte[] data = Cr2wJson.FromJson(Cr2wJson.UpgradeToLatest(Parse(json), out var report));
        Cr2wJson.ToJson(Cr2wFile.Read(data), out _); // what was written must read
        note = UpgradeNote(report);
        return data;
    }

    /// <summary>
    /// Saves CR2W files of older versions as version 164, the way the engine would after loading them (see
    /// <see cref="Cr2wJson.UpgradeToLatest(JsonObject, out Cr2wUpgradeReport)"/>), under <paramref name="outputFolder"/> or
    /// over themselves (with <paramref name="overwrite"/>). Files that are 164 already are left as they are.
    /// </summary>
    public static List<Cr2wJsonResult> ResaveAsLatest(IReadOnlyList<(string File, string Root)> inputs, string? outputFolder, bool overwrite,
        IProgress<Cr2wJsonProgress>? progress = null, CancellationToken token = default) =>
        Each(inputs, progress, token, (file, root) =>
        {
            var source = Cr2wFile.Read(File.ReadAllBytes(file));
            if (source.Version >= Cr2wFile.LastVersion) return new Cr2wJsonResult(file, null, null, L.T("Cr2wJsonBatch.VersionAlready", source.Version));
            string output = OutputPath(file, root, outputFolder);
            if (!overwrite && File.Exists(output))
                return new Cr2wJsonResult(file, null, output.Equals(file, StringComparison.OrdinalIgnoreCase)
                    ? L.T("Cr2wJsonBatch.WouldSavedOverItself")
                    : L.T("Cr2wJsonBatch.ThereAlreadyTurnOverwrite", Path.GetFileName(output)));
            byte[] data = Cr2wJson.UpgradeToLatest(source, out var report);
            Cr2wJson.ToJson(Cr2wFile.Read(data), out _); // what was written must read
            Write(output, data);
            return new Cr2wJsonResult(file, output, null, UpgradeNote(report));
        });

    /// <summary>What saving as the newest version did, in a line.</summary>
    private static string? UpgradeNote(Cr2wUpgradeReport report)
    {
        if (!report.Changed) return null;
        string note = L.T("Cr2wJsonBatch.VersionSaved", report.FromVersion, report.ToVersion);
        if (report.Notes.Count > 0) note += L.T("Cr2wJsonBatch.EngineChanges", report.Notes.Count, string.Join(" ", report.Notes.Take(2)) + (report.Notes.Count > 2 ? " …" : ""));
        return note;
    }

    /// <summary>
    /// For extracting from the game's bundles: a CR2W file as its JSON (name.env as name.env.json), or null to write it
    /// as it is (it isn't CR2W, or it wouldn't come back the same).
    /// </summary>
    public static (string Extension, byte[] Data)? ExtractAsJson(ArchiveEntry entry, byte[] data)
    {
        if (!Cr2wFile.IsCr2w(data)) return null;
        try { return (Path.GetExtension(entry.Path) + Suffix, Encoding.UTF8.GetBytes(ToJsonText(data, entry.Path, out _))); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FormatException or InvalidOperationException or ArgumentException) { return null; }
    }

    // ---------------------------------------------------------------- bulk editing

    /// <summary>
    /// Makes the edits to each file, CR2W or converted JSON, and writes it back in the form it was in: under
    /// <paramref name="outputFolder"/>, or over itself when that's empty (never over the game's files).
    /// A file the edits didn't change isn't written. With <paramref name="dryRun"/> nothing is written at all: each
    /// file only says what would change, and whether the result could be saved.
    /// </summary>
    public static List<Cr2wJsonResult> BulkEdit(IReadOnlyList<(string File, string Root)> inputs, IReadOnlyList<Cr2wBulkEdit> edits, string? outputFolder,
        IProgress<Cr2wJsonProgress>? progress = null, CancellationToken token = default, bool dryRun = false) =>
        Each(inputs, progress, token, (file, root) =>
        {
            bool json = file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
            var document = Load(file);
            var changes = new List<string>();
            foreach (var edit in edits)
            {
                int changed = edit.Apply(document);
                if (changed > 0) changes.Add(Done(edit, changed));
            }
            if (changes.Count == 0) return new Cr2wJsonResult(file, null, null, L.T("Cr2wJsonBatch.NothingChangeNoObject"));
            string output = OutputPath(file, root, outputFolder);
            // built either way, so a value the edit made that can't be saved is said now rather than at the next convert
            byte[] built = Cr2wJson.FromJson(document);
            if (dryRun)
            {
                if (FileSafety.Protection(output) is { } why) return new Cr2wJsonResult(file, null, L.T("Cr2wJsonBatch.CantSavedThere", why));
                return new Cr2wJsonResult(file, output, null, string.Join("; ", changes));
            }
            Write(output, json ? Encoding.UTF8.GetBytes(Cr2wJson.Format(document)) : built);
            return new Cr2wJsonResult(file, output, null, string.Join("; ", changes));
        });

    /// <summary>What an edit did to a file, in a few words.</summary>
    private static string Done(Cr2wBulkEdit edit, int changed)
    {
        string objects = changed == 1 ? "" : L.T("Cr2wJsonBatch.Objects", changed);
        return edit.Kind switch
        {
            BulkEditKind.Copy => L.T("Cr2wJsonBatch.Copied", edit.Path, objects),
            BulkEditKind.Set => L.T("Cr2wJsonBatch.Set", edit.Path, objects),
            BulkEditKind.Remove => L.T("Cr2wJsonBatch.Removed", edit.Path, objects),
            _ => L.T(edit.Kind == BulkEditKind.Scale ? "Cr2wJsonBatch.Multiplied" : "Cr2wJsonBatch.AddedTo", changed, edit.Path),
        };
    }

    /// <summary>A CR2W file or a converted JSON file, as JSON.</summary>
    public static JsonObject Load(string file)
    {
        if (file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return Parse(File.ReadAllText(file, Encoding.UTF8));
        return Cr2wJson.ToJson(Cr2wFile.Read(File.ReadAllBytes(file)), out _);
    }

    /// <summary>A converted JSON file's text, read as JSON.</summary>
    public static JsonObject Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new InvalidDataException(L.T("Cr2wJsonBatch.JsonIsntObject"));
        }
        catch (JsonException ex) { throw new InvalidDataException(L.T("Cr2wJsonBatch.JsonCantRead", ex.Message)); }
    }

    // ---------------------------------------------------------------- shared

    internal static void Write(string output, byte[] data)
    {
        FileSafety.CheckWrite(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, data);
    }

    private static List<Cr2wJsonResult> Each(IReadOnlyList<(string File, string Root)> inputs, IProgress<Cr2wJsonProgress>? progress,
        CancellationToken token, Func<string, string, Cr2wJsonResult> work)
    {
        var results = new Cr2wJsonResult[inputs.Count];
        var report = new Throttled(progress);
        int done = 0;
        try
        {
            Parallel.For(0, inputs.Count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            {
                var (file, root) = inputs[i];
                try { results[i] = work(file, root); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
                    or FormatException or InvalidOperationException or JsonException or ArgumentException)
                {
                    results[i] = new Cr2wJsonResult(file, null, ex.Message);
                }
                int now = Interlocked.Increment(ref done);
                report.Maybe(() => new Cr2wJsonProgress(now, inputs.Count, file), now == inputs.Count);
            });
        }
        catch (OperationCanceledException) { }
        return [.. results.Where(r => r is not null)];
    }
}
