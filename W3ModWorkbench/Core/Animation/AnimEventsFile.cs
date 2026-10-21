using System.IO;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Animation;

/// <summary>One event of an animation events file: on which animation, which track, when, for how long, and its object.</summary>
public sealed record AnimEvent(string Animation, string Track, string Name, float Start, float Duration, RedObject Object)
{
    /// <summary>What the event does in a few words: the sound it plays, the effect it starts, its kind.</summary>
    public string Label =>
        Text(Object["soundEventName"]) is { Length: > 0 } sound ? sound
        : Text(Object["effectName"]) is { Length: > 0 } effect && effect != "None" ? effect
        : Name;

    /// <summary>
    /// A text value: a string, or an 8-bit string (StringAnsi) the reader keeps as its bytes, a compressed length
    /// counting its last 0 and then its characters.
    /// </summary>
    public static string? Text(object? value)
    {
        if (value is string s) return s;
        if (value is not byte[] b || b.Length == 0) return null;
        int at = 0, first = b[at++], length = first & 0x3F;
        if ((first & 0x40) != 0)
            for (int shift = 6; at < b.Length; shift += 7)
            {
                int next = b[at++];
                length |= (next & 0x7F) << shift;
                if ((next & 0x80) == 0) break;
            }
        if ((first & 0x80) != 0 || length <= 1 || at + length - 1 > b.Length) return null;
        return System.Text.Encoding.Latin1.GetString(b, at, length - 1);
    }

    /// <summary>Whether it's a sound, a footstep or something else, for its marker.</summary>
    public string Kind => Object.ClassName switch
    {
        "CExtAnimFootstepEvent" => "footstep",
        var c when c.Contains("Sound", StringComparison.Ordinal) => "sound",
        var c when c.Contains("Effect", StringComparison.Ordinal) => "effect",
        _ => Duration > 0 ? "duration" : "event",
    };
}

/// <summary>
/// An animation events file (.w2animev): events timed to animations (sounds, footsteps, effects, things scripts hear)
/// kept apart from the animations, on a set of them, so sound designers can work on them alone. Each event names its
/// animation and track, and when it starts (and for how long, for those that last).
/// </summary>
public sealed class AnimEventsFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Resource { get; init; }
    public required List<AnimEvent> Events { get; init; }

    /// <summary>The animations the events are on, in name order.</summary>
    public List<string> Animations => [.. Events.Select(e => e.Animation).Distinct().Order(StringComparer.OrdinalIgnoreCase)];

    public static bool IsAnimEvents(string path) => path.EndsWith(".w2animev", StringComparison.OrdinalIgnoreCase);

    public static AnimEventsFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var resource = file.First("CExtAnimEventsFile") ?? throw new InvalidDataException(L.T("AnimEventsFile.IsntAnimationEventsFile"));
        var events = file.TailObjects(resource).Select(o => new AnimEvent(
            o["animationName"] as string ?? "",
            o["trackName"] as string is { Length: > 0 } t && t != "None" ? t : "__Default",
            o["eventName"] as string ?? ObjectTree.Words(o.ClassName, "CExtAnim", "CEAS", "C"),
            o.Float("startTime"),
            o.Float("duration"),
            o)).ToList();
        return new AnimEventsFile { File = file, Resource = resource, Events = events };
    }

    /// <summary>
    /// The animation set the events are most likely for, by the file's name: man_axe_1handed_sounds.w2animev's set is
    /// man_axe_1handed.w2anims. Null when no set has that name.
    /// </summary>
    public static string? AnimsetByName(string path, IReadOnlyCollection<string> animsets)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (string suffix in new[] { "_animation_sounds", "_sounds", "_sound", "_fx", "_events" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        return animsets.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a).Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
