using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>What happened to one file: where it was written, or why it wasn't, and a note (values kept as bytes, nothing changed).</summary>
public sealed record Cr2wJsonResult(string Input, string? Output, string? Error, string? Note = null)
{
    public bool Ok => Error is null;
}

public sealed record Cr2wJsonProgress(int Done, int Total, string Current);

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
    public static List<(string File, string Root)> Inputs(IEnumerable<string> paths, bool fromJson, bool recursive)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string given in paths)
        {
            string path = Path.GetFullPath(given);
            if (File.Exists(path))
            {
                if (seen.Add(path)) result.Add((path, Path.GetDirectoryName(path)!));
                continue;
            }
            if (!Directory.Exists(path)) continue;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string file in Directory.EnumerateFiles(path, "*", option).Order(StringComparer.OrdinalIgnoreCase))
            {
                bool wanted = fromJson ? IsConvertedJson(file) : !file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) && IsCr2w(file);
                if (wanted && seen.Add(file)) result.Add((file, path));
            }
        }
        return result;
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
            if (!asLatest)
            {
                Write(output, Cr2wJson.FromJsonText(File.ReadAllText(file, Encoding.UTF8)));
                return new Cr2wJsonResult(file, output, null);
            }
            var document = Load(file);
            byte[] data = Cr2wJson.FromJson(Cr2wJson.UpgradeToLatest(document, out var report));
            Cr2wJson.ToJson(Cr2wFile.Read(data), out _); // what was written must read
            Write(output, data);
            return new Cr2wJsonResult(file, output, null, UpgradeNote(report));
        });

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
        if (file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8),
                    documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                    ?? throw new InvalidDataException(L.T("Cr2wJsonBatch.JsonIsntObject"));
            }
            catch (JsonException ex) { throw new InvalidDataException(L.T("Cr2wJsonBatch.JsonCantRead", ex.Message)); }
        }
        return Cr2wJson.ToJson(Cr2wFile.Read(File.ReadAllBytes(file)), out _);
    }

    // ---------------------------------------------------------------- shared

    private static void Write(string output, byte[] data)
    {
        FileSafety.CheckWrite(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, data);
    }

    private static List<Cr2wJsonResult> Each(IReadOnlyList<(string File, string Root)> inputs, IProgress<Cr2wJsonProgress>? progress,
        CancellationToken token, Func<string, string, Cr2wJsonResult> work)
    {
        var results = new Cr2wJsonResult[inputs.Count];
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
                progress?.Report(new Cr2wJsonProgress(Interlocked.Increment(ref done), inputs.Count, file));
            });
        }
        catch (OperationCanceledException) { }
        return [.. results.Where(r => r is not null)];
    }
}
