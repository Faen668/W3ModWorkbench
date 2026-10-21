using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>A place in a script that a search found: the line it's on, and what's matched in it.</summary>
/// <param name="Column">Where the match starts in <paramref name="LineText"/>.</param>
/// <param name="Label">Why it's listed when that isn't plain, e.g. "declaration" or "maybe".</param>
public sealed record ScriptHit(ScriptFile File, int Offset, int Length, int Line, string LineText, int Column, string? Label = null);

/// <summary>Searches that read every script in a workspace: references to a declaration, and plain text.</summary>
public static class ScriptSearch
{
    /// <summary>More than this many and the search stops: the list would be too long to read anyway.</summary>
    public const int MaxHits = 5000;

    /// <summary>
    /// Every place that refers to one of <paramref name="targets"/>: each use of the name is looked up the way
    /// go to definition does, and kept when it leads to one of them. The declarations themselves are listed too.
    /// A local variable or parameter is only looked for in its own file.
    /// </summary>
    /// <param name="current">The file the search started in, parsed already.</param>
    public static List<ScriptHit> References(IReadOnlyList<ScriptSymbol> targets, ParsedScript current, IReadOnlyList<ScriptIndex> layers,
        CancellationToken cancel) =>
        [.. Scan(targets, current, layers, cancel).Select(h => h.Hit)];

    /// <summary>
    /// The same search as <see cref="References"/>, with each hit's file parsed, for work that needs to know
    /// which function or type a hit is in (the usage graph).
    /// </summary>
    internal static List<(ScriptHit Hit, ParsedScript Parsed)> Scan(IReadOnlyList<ScriptSymbol> targets, ParsedScript current,
        IReadOnlyList<ScriptIndex> layers, CancellationToken cancel)
    {
        if (targets.Count == 0) return [];
        string name = targets[0].Name;
        var wanted = targets.Select(t => (t.File.Key, t.Offset)).ToHashSet();
        bool local = targets.All(t => t.Kind is SymbolKind.Local or SymbolKind.Parameter);
        var files = local ? [current.File] : FilesOf(current.File, layers);

        var hits = new ConcurrentBag<(int Order, ScriptHit Hit, ParsedScript Parsed)>();
        int count = 0;
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = cancel }, (i, loop) =>
        {
            var file = files[i];
            bool own = file == current.File;
            string text;
            try
            {
                text = file.Text;
            }
            catch (Exception)
            {
                return; // a file that can't be read just isn't searched
            }
            if (!own && text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) return;
            var parsed = own ? current : ScriptParser.Parse(text, file);
            var finder = new DefinitionFinder(parsed, layers);
            foreach (int offset in WholeWords(parsed.Code, name))
            {
                if (cancel.IsCancellationRequested || count > MaxHits) { loop.Stop(); return; }
                string? label = null;
                if (wanted.Contains((file.Key, offset))) label = "declaration";
                else
                {
                    var found = finder.Find(offset, name.Length);
                    if (!found.Any(f => wanted.Contains((f.File.Key, f.Offset)))) continue;
                    // several fit and this is only one of them: the type before the dot couldn't be worked out
                    if (found.Count > 1) label = "maybe";
                }
                hits.Add((i, Hit(file, parsed, text, offset, name.Length, label), parsed));
                Interlocked.Increment(ref count);
            }
        });
        return [.. hits.OrderBy(h => h.Order).ThenBy(h => h.Hit.Offset).Select(h => (h.Hit, h.Parsed))];
    }

    /// <summary>
    /// Every line in the workspace's scripts that matches <paramref name="pattern"/>, one hit per match.
    /// Comments and strings are searched too, as in any text search.
    /// </summary>
    public static List<ScriptHit> Text(Regex pattern, ScriptFile current, IReadOnlyList<ScriptIndex> layers, CancellationToken cancel) =>
        Text(pattern, FilesOf(current, layers), null, cancel);

    /// <summary>
    /// Every line in <paramref name="files"/> that matches <paramref name="pattern"/>, in their order. A file's text
    /// comes from <paramref name="unsaved"/> when it's there (keyed by <see cref="ScriptFile.Key"/>), so changes
    /// not saved yet are searched as they are.
    /// </summary>
    public static List<ScriptHit> Text(Regex pattern, IReadOnlyList<ScriptFile> files, IReadOnlyDictionary<string, string>? unsaved,
        CancellationToken cancel, IProgress<int>? progress = null)
    {
        var hits = new ConcurrentBag<(int Order, ScriptHit Hit)>();
        int count = 0, done = 0;
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = cancel }, (i, loop) =>
        {
            if (Interlocked.Increment(ref done) % 200 == 0) progress?.Report(done);
            string text;
            try
            {
                text = unsaved is not null && unsaved.TryGetValue(files[i].Key, out var edited) ? edited : files[i].Text;
            }
            catch (Exception)
            {
                return;
            }
            List<int>? starts = null;
            for (var m = pattern.Match(text); m.Success; m = m.NextMatch())
            {
                if (m.Length == 0) continue;
                if (cancel.IsCancellationRequested || Interlocked.Increment(ref count) > MaxHits) { loop.Stop(); return; }
                starts ??= LineStarts(text);
                hits.Add((i, Hit(files[i], starts, text, m.Index, m.Length, null)));
            }
        });
        return [.. hits.OrderBy(h => h.Order).ThenBy(h => h.Hit.Offset).Select(h => h.Hit)];
    }

    /// <summary>The current file first, then every other file in the layers, each once.</summary>
    private static List<ScriptFile> FilesOf(ScriptFile current, IReadOnlyList<ScriptIndex> layers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Key };
        var files = new List<ScriptFile> { current };
        foreach (var layer in layers)
            foreach (var file in layer.Files)
                if (seen.Add(file.Key)) files.Add(file);
        return files;
    }

    /// <summary>Where <paramref name="word"/> appears on its own, not inside a longer name. Letter case is ignored, as the game does.</summary>
    private static IEnumerable<int> WholeWords(string code, string word)
    {
        for (int i = code.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = code.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = i + word.Length;
            if (i > 0 && ScriptParser.IsWordChar(code[i - 1])) continue;
            if (end < code.Length && ScriptParser.IsWordChar(code[end])) continue;
            yield return i;
        }
    }

    private static ScriptHit Hit(ScriptFile file, ParsedScript parsed, string text, int offset, int length, string? label) =>
        Hit(file, parsed.LineStarts, text, offset, length, label);

    internal static ScriptHit Hit(ScriptFile file, List<int> starts, string text, int offset, int length, string? label)
    {
        int index = starts.BinarySearch(offset);
        int line = index >= 0 ? index : ~index - 1;
        int start = starts[line];
        int end = line + 1 < starts.Count ? starts[line + 1] : text.Length;
        string lineText = text[start..end].TrimEnd('\r', '\n').Replace('\t', ' ');
        return new ScriptHit(file, offset, length, line + 1, lineText, offset - start, label);
    }

    internal static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }
}
