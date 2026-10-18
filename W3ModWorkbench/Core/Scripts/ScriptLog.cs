using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Scripts.Navigation;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>A line of the game's scripts log: <c>[Channel] message</c>.</summary>
/// <param name="Number">1-based, counted from the start of the file.</param>
public sealed record LogLine(int Number, string Channel, string Message, string Text)
{
    /// <summary>Asserts and errors, which the viewer picks out.</summary>
    public bool IsProblem => Channel.Equals("ASSERT", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("error", StringComparison.OrdinalIgnoreCase) || Message.Contains("warning", StringComparison.OrdinalIgnoreCase);

    public static LogLine Parse(int number, string text)
    {
        if (text.StartsWith('[') && text.IndexOf(']') is var end and > 1)
            return new LogLine(number, text[1..end], text[(end + 1)..].TrimStart(), text);
        return new LogLine(number, "", text, text);
    }
}

/// <summary>
/// Follows a log file as the game writes it: each call reads what was added since the last, a whole line at a time.
/// The file is opened for each read and shared, so the game can keep writing (and start it again) while it's watched.
/// </summary>
public sealed class LogTail(string path)
{
    private long _position;
    private int _lines;
    private readonly StringBuilder _partial = new();

    public string Path { get; } = path;

    /// <summary>The file was made again (the game started again) since the last read, so it's read from the start.</summary>
    public bool Restarted { get; private set; }

    /// <summary>The lines added since the last call. The first call reads the whole file.</summary>
    public List<LogLine> ReadNew()
    {
        Restarted = false;
        var result = new List<LogLine>();
        if (!File.Exists(Path)) return result;
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _position)
        {
            // shorter than what was read: it's a new log
            _position = 0;
            _lines = 0;
            _partial.Clear();
            Restarted = true;
        }
        if (fs.Length == _position) return result;
        fs.Position = _position;
        var buffer = new byte[Math.Min(fs.Length - _position, 64L * 1024 * 1024)];
        int read = fs.Read(buffer, 0, buffer.Length);
        _position += read;
        int skip = _position == read && read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        _partial.Append(Encoding.UTF8.GetString(buffer, skip, read - skip));

        string text = _partial.ToString();
        int start = 0;
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', start))
        {
            result.Add(LogLine.Parse(++_lines, text[start..i].TrimEnd('\r')));
            start = i + 1;
        }
        _partial.Clear();
        _partial.Append(text, start, text.Length - start);
        return result;
    }
}

/// <summary>A call in a script that may have written a log line, and how well its text fits the line.</summary>
public sealed record LogSource(CorpusFile File, ScriptHit Hit, int Score);

/// <summary>Finds the LogChannel (or Log) call that writes a line of the scripts log.</summary>
public static partial class LogSources
{
    /// <summary>
    /// The calls on the line's channel, best fit first: the more of the line's text the call's strings make up, the
    /// better. When none of their strings are in the line, every call on the channel is listed.
    /// </summary>
    public static List<LogSource> Find(LogLine line, IReadOnlyList<CorpusFile> files, CancellationToken cancel)
    {
        bool plain = line.Channel.Length == 0 || line.Channel.Equals("Script", StringComparison.OrdinalIgnoreCase);
        var direct = Scan(line, files, plain ? ["Log"] : ["LogChannel"], plain ? null : line.Channel, cancel);
        if (direct.Any(s => s.Score > 0)) return [.. direct.Where(s => s.Score > 0)];

        // the game often logs a channel through a small function of its own, function LogBoat(str : string) { LogChannel('Boat', str); }:
        // then the line's text is in the calls to that function
        var wrappers = direct.Select(s => Wrapper().Match(s.Hit.LineText)).Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wrappers.Count > 0 && Scan(line, files, wrappers, null, cancel) is { Count: > 0 } through)
        {
            var scored = through.Where(s => s.Score > 0).ToList();
            return scored.Count > 0 ? scored : [.. direct, .. through.Take(200)];
        }
        return direct;
    }

    /// <summary>Calls of the named functions, with a channel name first when <paramref name="channel"/> is given, scored by how much of the line their strings make up.</summary>
    private static List<LogSource> Scan(LogLine line, IReadOnlyList<CorpusFile> files, IReadOnlyList<string> functions, string? channel, CancellationToken cancel)
    {
        var call = new Regex($@"\b(?<fn>{string.Join("|", functions.Select(Regex.Escape))})\s*\(", RegexOptions.IgnoreCase);
        var found = new System.Collections.Concurrent.ConcurrentBag<(int Order, LogSource Source)>();
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            string text;
            try { text = files[i].File.Text; }
            catch (Exception) { return; }
            if (channel is not null && text.IndexOf(channel, StringComparison.OrdinalIgnoreCase) < 0) return;
            if (channel is null && !functions.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase))) return;
            string code = ScriptParser.Blank(text);
            List<int>? starts = null;
            for (var m = call.Match(code); m.Success; m = m.NextMatch())
            {
                // a declaration of the function isn't a call of it
                string before = code[Math.Max(0, m.Index - 12)..m.Index];
                if (before.Contains("function", StringComparison.Ordinal)) continue;
                int open = m.Index + m.Length - 1;
                int close = Close(code, open);
                if (close < 0) continue;
                string args = text[(open + 1)..close];
                if (channel is not null)
                {
                    // LogChannel('Channel', ...): the name is the first argument
                    var named = Channel().Match(args);
                    if (!named.Success || !named.Groups[1].Value.Equals(channel, StringComparison.OrdinalIgnoreCase)) continue;
                }
                int score = 0;
                foreach (Match s in Literal().Matches(args))
                {
                    string piece = s.Groups[1].Value.Trim();
                    if (piece.Length >= 3 && line.Message.Contains(piece, StringComparison.OrdinalIgnoreCase)) score += piece.Length;
                }
                starts ??= ScriptCorpus.LineStarts(text);
                found.Add((i, new LogSource(files[i], ScriptCorpus.Hit(files[i].File, starts, text, m.Index, m.Groups["fn"].Length), score)));
            }
        });
        return [.. found.OrderByDescending(f => f.Source.Score).ThenBy(f => f.Order).ThenBy(f => f.Source.Hit.Offset).Select(f => f.Source)];
    }

    private static int Close(string code, int open)
    {
        int depth = 0;
        for (int i = open; i < code.Length && i - open < 1500; i++)
        {
            if (code[i] == '(') depth++;
            else if (code[i] == ')' && --depth == 0) return i;
            else if (code[i] is ';' or '{') return -1;
        }
        return -1;
    }

    [GeneratedRegex(@"\bfunction\s+(\w+)\s*\(")]
    private static partial Regex Wrapper();

    [GeneratedRegex(@"^\s*'([^']*)'")]
    private static partial Regex Channel();

    [GeneratedRegex(@"""((?:[^""\\\r\n]|\\.)*)""")]
    private static partial Regex Literal();
}
