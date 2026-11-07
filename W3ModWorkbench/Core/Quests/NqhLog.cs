using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Quests;

/// <summary>What a quest block did, as Native Quest Hook's log says it.</summary>
public enum NqhLogKind
{
    /// <summary>It started.</summary>
    Started,
    /// <summary>It was started again by a load, a world change or the hook attaching: the state, not progress.</summary>
    Replayed,
    /// <summary>It finished and fired an output: the quest moved on.</summary>
    Finished,
    /// <summary>It stopped without finishing: a load, leaving its world, or its quest stopping.</summary>
    Stopped,
    /// <summary>An input of a block already running was reached again: a join, or a loop.</summary>
    InputAgain,
}

/// <summary>One block event from Native Quest Hook's log.</summary>
public sealed record NqhLogEvent(TimeSpan Time, NqhLogKind Kind, string ClassName, string Name, Guid Guid, Guid? Scope, string Output,
    IReadOnlyList<Guid> Next, uint Caller, bool Watched, string Line);

/// <summary>
/// Reads Native Quest Hook's log (bin\x64_dx12\NativeQuestHook.log), as the game writes it or after. A block's line
/// is "time verb class "name" guid | scope guid | out output -> [next guids] | from +0xcaller", the verb activated
/// (with "(replay)" for a load's), deactivated or "input again". A finish and a stop are both "deactivated": the place
/// they're called from tells them apart, and that differs between builds of the game, so it's learnt from the log
/// itself: deactivations that lead on to blocks come from the finish.
/// </summary>
public sealed partial class NqhLogReader
{
    // "12:34:56.789 " then, in the first test builds, "[thread] quest: "
    [GeneratedRegex("""^(?<time>\d\d:\d\d:\d\d\.\d{3}) (?:\[\d+\] quest: )?(?<verb>activated|deactivated|input again)(?<replay> \(replay\))? (?<class>\S+) "(?<name>.*)" (?<guid>[0-9a-f-]{36}) \| (?:thread \S+ )?scope (?<scope>\S+) \| out (?<out>.*?) -> \[(?<next>[0-9a-f -]*)\] \| from \+0x(?<caller>[0-9a-f]+)(?<watched> \| watched)?""",
        RegexOptions.CultureInvariant)]
    private static partial Regex BlockLine();

    private readonly Dictionary<uint, int> _leadingOn = [];
    private uint? _finish;

    /// <summary>The caller a normal finish comes from, once the log has shown it.</summary>
    public uint? FinishCaller => _finish;

    /// <summary>A line read as a block event; null for any other line (the hook's own notes).</summary>
    public NqhLogEvent? Read(string line)
    {
        var m = BlockLine().Match(line);
        if (!m.Success || !Guid.TryParse(m.Groups["guid"].Value, out var guid)) return null;
        var time = TimeSpan.ParseExact(m.Groups["time"].Value, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        var next = m.Groups["next"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToList();
        uint caller = uint.Parse(m.Groups["caller"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        string output = m.Groups["out"].Value;
        NqhLogKind kind;
        switch (m.Groups["verb"].Value)
        {
            case "activated": kind = m.Groups["replay"].Success ? NqhLogKind.Replayed : NqhLogKind.Started; break;
            case "input again": kind = NqhLogKind.InputAgain; break;
            default:
                if (next.Count > 0)
                {
                    _leadingOn[caller] = _leadingOn.GetValueOrDefault(caller) + 1;
                    _finish = _leadingOn.MaxBy(p => p.Value).Key;
                }
                // until the log has shown where finishes come from: one that leads on, or fires a named output, finished
                kind = (_finish is { } f ? caller == f : next.Count > 0 || output != "None") ? NqhLogKind.Finished : NqhLogKind.Stopped;
                break;
        }
        return new NqhLogEvent(time, kind, m.Groups["class"].Value, m.Groups["name"].Value, guid,
            Guid.TryParse(m.Groups["scope"].Value, out var scope) ? scope : null, output, next, caller, m.Groups["watched"].Success, line);
    }
}

/// <summary>
/// Follows a log file as it grows, reading what's been added since the last look. A file that got shorter was started
/// again (the game was restarted, and the hook keeps the last one as NativeQuestHook.prev.log), and is read from the top.
/// </summary>
public sealed class LogTail(string path)
{
    private long _position;
    private string _partial = "";

    public string Path { get; } = path;

    /// <summary>Whether the file was started again since the last look, which <see cref="ReadNew"/> says once.</summary>
    public bool Restarted { get; private set; }

    /// <summary>The whole lines added since the last look; empty when there are none or the file isn't there.</summary>
    public List<string> ReadNew()
    {
        Restarted = false;
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                _position = 0;
                _partial = "";
                Restarted = true;
            }
            if (stream.Length == _position) return lines;
            stream.Position = _position;
            var bytes = new byte[stream.Length - _position];
            int read = stream.Read(bytes, 0, bytes.Length);
            _position += read;
            // the hook writes names as the game keeps them, one byte a letter
            string text = _partial + Encoding.Latin1.GetString(bytes, 0, read);
            int end = text.LastIndexOf('\n');
            if (end < 0)
            {
                _partial = text;
                return lines;
            }
            _partial = text[(end + 1)..];
            foreach (string line in text[..end].Split('\n')) lines.Add(line.TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lines;
    }
}
