using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace W3BundleExplorer.Highlighting;

/// <summary>A coloured span within one line: start, length and its TextMate scope name.</summary>
public readonly record struct Token(int Start, int Length, string Scope);

/// <summary>
/// Runs a TextMate grammar (the format VS Code uses) over text one line at a time. Supports what
/// the WitcherScript grammar needs: match rules with captures, begin/end rules with inner patterns
/// (block comments, strings, names), and #repository includes. As in TextMate, the earliest match
/// wins and ties go to the rule listed first.
/// </summary>
public sealed class TextMateGrammar
{
    public sealed class Rule
    {
        public string? Name;
        public Regex? Match;
        public Regex? Begin;
        public Regex? End;
        public Dictionary<int, string> Captures = [];
        public List<Rule> Inner = [];
    }

    private readonly List<Rule> _rules;

    private TextMateGrammar(List<Rule> rules) => _rules = rules;

    /// <summary>The WitcherScript grammar from WebSpam's witcherscript-vscode extension (MIT), embedded as-is.</summary>
    public static TextMateGrammar WitcherScript { get; } = Load(typeof(TextMateGrammar).Assembly
        .GetManifestResourceStream("W3BundleExplorer.Highlighting.witcherscript.tmLanguage.json")!);

    public static TextMateGrammar Load(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var repository = root.TryGetProperty("repository", out var repo) ? repo : default;
        return new TextMateGrammar(Patterns(root.GetProperty("patterns"), repository));
    }

    private static List<Rule> Patterns(JsonElement patterns, JsonElement repository)
    {
        var rules = new List<Rule>();
        foreach (var p in patterns.EnumerateArray())
        {
            if (p.TryGetProperty("include", out var include))
            {
                string key = include.GetString()!.TrimStart('#');
                if (repository.ValueKind == JsonValueKind.Object && repository.TryGetProperty(key, out var entry))
                    rules.AddRange(entry.TryGetProperty("patterns", out var nested) ? Patterns(nested, repository) : [Build(entry, repository)]);
                continue;
            }
            rules.Add(Build(p, repository));
        }
        return rules;
    }

    private static Rule Build(JsonElement p, JsonElement repository)
    {
        const RegexOptions options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        var rule = new Rule { Name = p.TryGetProperty("name", out var n) ? n.GetString() : null };
        if (p.TryGetProperty("match", out var m)) rule.Match = new Regex(m.GetString()!, options);
        if (p.TryGetProperty("begin", out var b)) rule.Begin = new Regex(b.GetString()!, options);
        if (p.TryGetProperty("end", out var e)) rule.End = new Regex(e.GetString()!, options);
        if (p.TryGetProperty("captures", out var caps))
            foreach (var c in caps.EnumerateObject())
                if (int.TryParse(c.Name, out int group) && c.Value.TryGetProperty("name", out var cn))
                    rule.Captures[group] = cn.GetString()!;
        if (p.TryGetProperty("patterns", out var inner))
            rule.Inner = Patterns(inner, repository);
        return rule;
    }

    /// <summary>
    /// Colours one line. <paramref name="open"/> is the begin/end rule still running from the line
    /// before (a block comment, say); the return value is the one still running at the end of this line.
    /// </summary>
    public Rule? TokenizeLine(string line, Rule? open, List<Token> tokens)
    {
        int pos = 0;
        while (pos <= line.Length)
        {
            if (open is not null)
            {
                var end = open.End!.Match(line, pos);
                var (innerRule, innerMatch) = Earliest(open.Inner, line, pos);
                if (innerMatch is not null && (!end.Success || innerMatch.Index < end.Index))
                {
                    Add(tokens, pos, innerMatch.Index - pos, open.Name);
                    Emit(tokens, innerRule!, innerMatch);
                    pos = Advance(innerMatch, pos);
                    continue;
                }
                if (end.Success)
                {
                    Add(tokens, pos, end.Index + end.Length - pos, open.Name);
                    pos = Advance(end, pos);
                    open = null;
                    continue;
                }
                Add(tokens, pos, line.Length - pos, open.Name);
                return open;
            }

            var (rule, match) = Earliest(_rules, line, pos);
            if (rule is null || match is null) break;
            if (rule.Begin is not null)
            {
                Add(tokens, match.Index, match.Length, rule.Name);
                open = rule;
            }
            else
            {
                Emit(tokens, rule, match);
            }
            pos = Advance(match, pos);
        }
        return open;
    }

    private static int Advance(Match m, int pos) => Math.Max(m.Index + m.Length, m.Length == 0 ? pos + 1 : pos);

    private static (Rule?, Match?) Earliest(List<Rule> rules, string line, int pos)
    {
        Rule? bestRule = null;
        Match? best = null;
        if (pos > line.Length) return (null, null);
        foreach (var rule in rules)
        {
            var regex = rule.Match ?? rule.Begin;
            if (regex is null) continue;
            var m = regex.Match(line, pos);
            if (m.Success && (best is null || m.Index < best.Index))
            {
                best = m;
                bestRule = rule;
                if (m.Index == pos) break; // nothing can start earlier
            }
        }
        return (bestRule, best);
    }

    private static void Emit(List<Token> tokens, Rule rule, Match m)
    {
        Add(tokens, m.Index, m.Length, rule.Name);
        foreach (var (group, scope) in rule.Captures)
            if (group < m.Groups.Count && m.Groups[group].Success)
                Add(tokens, m.Groups[group].Index, m.Groups[group].Length, scope);
    }

    private static void Add(List<Token> tokens, int start, int length, string? scope)
    {
        if (scope is not null && length > 0)
            tokens.Add(new Token(start, length, scope));
    }
}
