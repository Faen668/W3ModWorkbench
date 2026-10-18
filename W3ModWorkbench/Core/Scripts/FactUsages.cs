using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Scripts.Navigation;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>Whether a call changes a fact or only reads it.</summary>
public enum FactAccess { Write, Read }

/// <summary>
/// One call of a facts function: <c>FactsAdd("q001_met_ciri", 1)</c>. <see cref="Fact"/> is the fact's name when it's
/// written out in quotes, or null when the code builds it (<see cref="Expression"/> then holds what's written).
/// </summary>
public sealed record FactUse(string? Fact, string Expression, string Function, FactAccess Access, CorpusFile File, ScriptHit Hit);

/// <summary>A fact and every place that sets or reads it.</summary>
public sealed record FactSummary(string Name, IReadOnlyList<FactUse> Uses)
{
    public int Writes => Uses.Count(u => u.Access == FactAccess.Write);
    public int Reads => Uses.Count(u => u.Access == FactAccess.Read);

    /// <summary>The mods and the game that use it, in the order first seen.</summary>
    public string Sources => string.Join(", ", Uses.Select(u => u.File.Label).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>Read but never set by any script: set by a quest or scene, or not at all.</summary>
    public bool OnlyRead => Writes == 0;

    /// <summary>Set but never read by any script: read by a quest or scene, or not at all.</summary>
    public bool OnlyWritten => Reads == 0;
}

/// <summary>
/// Finds the quest facts scripts use: the facts database calls with the fact's name, whether each sets or reads it.
/// Quests and scenes use facts too, but they're compiled resources, so only scripts are read.
/// </summary>
public static partial class FactUsages
{
    private static readonly Dictionary<string, FactAccess> Functions = new(StringComparer.Ordinal)
    {
        ["FactsAdd"] = FactAccess.Write,
        ["FactsSet"] = FactAccess.Write,
        ["FactsSubstract"] = FactAccess.Write,
        ["FactsRemove"] = FactAccess.Write,
        ["FactsQuerySum"] = FactAccess.Read,
        ["FactsQuerySumSince"] = FactAccess.Read,
        ["FactsQueryLatestValue"] = FactAccess.Read,
        ["FactsDoesExist"] = FactAccess.Read,
    };

    public static List<FactUse> Find(IReadOnlyList<CorpusFile> files, CancellationToken cancel)
    {
        var found = new ConcurrentBag<(int Order, FactUse Use)>();
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            string text;
            try { text = files[i].File.Text; }
            catch (Exception) { return; }
            if (text.IndexOf("Facts", StringComparison.Ordinal) < 0) return;
            string code = ScriptParser.Blank(text);
            List<int>? starts = null;
            for (var m = Call().Match(code); m.Success; m = m.NextMatch())
            {
                // a declaration of the function itself isn't a use of a fact
                string before = code[Math.Max(0, m.Index - 12)..m.Index];
                if (before.Contains("function", StringComparison.Ordinal)) continue;
                string function = m.Groups["fn"].Value;
                if (!Functions.TryGetValue(function, out var access)) continue;

                int at = m.Index + m.Length;
                while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
                string? fact = null;
                string expression;
                int start = at, length;
                if (at < text.Length && text[at] == '"' && text.IndexOf('"', at + 1) is var end and > 0 && text.IndexOf('\n', at, end - at) < 0)
                {
                    fact = text[(at + 1)..end];
                    expression = text[at..(end + 1)];
                    (start, length) = (at + 1, fact.Length);
                }
                else
                {
                    int stop = ArgumentEnd(code, at);
                    expression = Regex.Replace(text[at..stop].Trim(), @"\s+", " ");
                    length = Math.Max(1, stop - at);
                }
                starts ??= ScriptCorpus.LineStarts(text);
                var hit = ScriptCorpus.Hit(files[i].File, starts, text, start, length);
                found.Add((i, new FactUse(fact, expression, function, access, files[i], hit)));
            }
        });

        // a mod's loose copy of a game script repeats the game's lines: keep the game's
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FactUse>();
        foreach (var (_, use) in found.OrderBy(f => f.Use.File.Origin == CorpusOrigin.Game ? 0 : 1).ThenBy(f => f.Order).ThenBy(f => f.Use.Hit.Offset))
        {
            string key = $"{use.File.File.Path}|{use.Hit.LineText.Trim()}|{use.Function}|{use.Expression}";
            if (seen.Add(key) || use.File.Origin == CorpusOrigin.Game) result.Add(use);
        }
        return [.. result.OrderBy(u => u.File.Origin).ThenBy(u => u.File.File.Path, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Hit.Offset)];
    }

    /// <summary>The uses grouped by fact name, letter case ignored as the game does. Names the code builds are left out.</summary>
    public static List<FactSummary> Summarise(IEnumerable<FactUse> uses) =>
        [.. uses.Where(u => u.Fact is not null)
            .GroupBy(u => u.Fact!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FactSummary(g.Key, [.. g]))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Where the first argument ends: the comma or bracket at its own depth.</summary>
    private static int ArgumentEnd(string code, int from)
    {
        int depth = 0;
        for (int i = from; i < code.Length && i - from < 400; i++)
        {
            char c = code[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']')
            {
                if (depth == 0) return i;
                depth--;
            }
            else if ((c == ',' && depth == 0) || c is ';' or '{' or '}') return i;
        }
        return Math.Min(code.Length, from + 1);
    }

    [GeneratedRegex(@"\b(?<fn>Facts\w+)\s*\(")]
    private static partial Regex Call();
}
