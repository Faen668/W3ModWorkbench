using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Scripts.Navigation;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>A parameter of a console command: its name, its type, and whether it can be left out.</summary>
public sealed record CommandParameter(string Name, string Type, bool Optional);

/// <summary>
/// An <c>exec function</c>: a function the game's debug console can run by name, such as <c>additem('Aerondight')</c>.
/// </summary>
public sealed record ConsoleCommand(string Name, IReadOnlyList<CommandParameter> Parameters, CorpusFile File, int Offset, int Line)
{
    /// <summary>name(a : int, optional b : name), as it's declared.</summary>
    public string Signature => $"{Name}({string.Join(", ", Parameters.Select(p => (p.Optional ? "optional " : "") + $"{p.Name} : {p.Type}"))})";

    /// <summary>What to type in the console, with each parameter as a placeholder: names and strings in quotes.</summary>
    public string Command(bool withOptional)
    {
        var shown = Parameters.Where(p => withOptional || !p.Optional).Select(p => p.Type.ToLowerInvariant() switch
        {
            "name" or "string" => $"'{p.Name}'",
            _ => p.Name,
        });
        return $"{Name}({string.Join(", ", shown)})";
    }
}

/// <summary>Finds the console commands that scripts declare.</summary>
public static partial class ConsoleCommands
{
    /// <summary>
    /// Every exec function in <paramref name="files"/>, sorted by name. A command a mod's loose copy of a game script
    /// declares exactly as the game does is listed once, as the game's.
    /// </summary>
    public static List<ConsoleCommand> Find(IReadOnlyList<CorpusFile> files, CancellationToken cancel)
    {
        var found = new ConcurrentBag<(int Order, ConsoleCommand Command)>();
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            string text;
            try { text = files[i].File.Text; }
            catch (Exception) { return; }
            if (text.IndexOf("exec", StringComparison.OrdinalIgnoreCase) < 0) return;
            string code = ScriptParser.Blank(text);
            List<int>? starts = null;
            for (var m = Exec().Match(code); m.Success; m = m.NextMatch())
            {
                int open = m.Index + m.Length - 1;
                int close = Close(code, open);
                if (close < 0) continue;
                starts ??= ScriptCorpus.LineStarts(text);
                int name = m.Groups["name"].Index;
                var hit = ScriptCorpus.Hit(files[i].File, starts, text, name, m.Groups["name"].Length);
                found.Add((i, new ConsoleCommand(m.Groups["name"].Value, Parameters(code[(open + 1)..close]), files[i], name, hit.Line)));
            }
        });

        // the game's first, so a mod's copy of one of its files doesn't claim its commands
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ConsoleCommand>();
        foreach (var (_, command) in found.OrderBy(f => f.Command.File.Origin == CorpusOrigin.Game ? 0 : 1).ThenBy(f => f.Order).ThenBy(f => f.Command.Offset))
        {
            string key = $"{command.File.File.Path}|{command.Signature}";
            if (command.File.Origin == CorpusOrigin.Game || !seen.Contains(key))
            {
                seen.Add(key);
                result.Add(command);
            }
        }
        return [.. result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.File.Origin)];
    }

    /// <summary>The ) that closes the ( at <paramref name="open"/>, or -1.</summary>
    private static int Close(string code, int open)
    {
        int depth = 0;
        for (int i = open; i < code.Length && i - open < 2000; i++)
        {
            if (code[i] == '(') depth++;
            else if (code[i] == ')' && --depth == 0) return i;
            else if (code[i] is '{' or ';') return -1;
        }
        return -1;
    }

    /// <summary>
    /// <c>a : int, optional b, c : name</c>: names written together share the type that follows them, and
    /// optional (or out) goes with the name it's written before.
    /// </summary>
    public static List<CommandParameter> Parameters(string list)
    {
        var result = new List<CommandParameter>();
        var waiting = new List<(string Name, bool Optional)>();
        foreach (string part in Split(list))
        {
            string item = Regex.Replace(part.Trim(), @"\s+", " ");
            if (item.Length == 0) continue;
            int colon = item.IndexOf(':');
            string head = colon < 0 ? item : item[..colon].Trim();
            var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            bool optional = words.Any(w => w == "optional");
            waiting.Add((words[^1], optional));
            if (colon < 0) continue;
            string type = ScriptParser.Squash(item[(colon + 1)..]);
            result.AddRange(waiting.Select(w => new CommandParameter(w.Name, type, w.Optional)));
            waiting.Clear();
        }
        result.AddRange(waiting.Select(w => new CommandParameter(w.Name, "?", w.Optional)));
        return result;
    }

    /// <summary>Splits on commas outside &lt;&gt;, so array&lt;int&gt; stays whole.</summary>
    private static IEnumerable<string> Split(string list)
    {
        int depth = 0, from = 0;
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] == '<') depth++;
            else if (list[i] == '>') depth--;
            else if (list[i] == ',' && depth == 0)
            {
                yield return list[from..i];
                from = i + 1;
            }
        }
        yield return list[from..];
    }

    [GeneratedRegex(@"\bexec\s+(?:\w+\s+)*?function\s+(?<name>\w+)\s*\(")]
    private static partial Regex Exec();
}
