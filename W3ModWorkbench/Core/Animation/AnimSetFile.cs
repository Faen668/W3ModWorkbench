using System.IO;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// One animation of a set: its name, length and frames, how its bone data is kept (compressed in the file, some of it
/// streamed from a buffer), the events its entry keeps (sounds, footsteps, things scripts hear), and how it moves the
/// character as a whole.
/// </summary>
public sealed record AnimSetAnimation(RedObject Entry, RedObject? Animation, RedObject? Buffer, List<AnimEvent> Events, MotionExtraction? Motion)
{
    public string Name => Animation?["name"] as string ?? "";
    public float Duration => Animation?.Float("duration") is > 0 and var d ? d : Buffer?.Float("duration") ?? 0;
    public float FramesPerSecond => Animation?.Float("framesPerSecond") is > 0 and var f ? f : 30;
    public int Frames => (int)(Buffer?.Integer("numFrames") ?? Math.Round(Duration * FramesPerSecond) + 1);

    /// <summary>How the bone data is kept: compressed, in parts for a long animation, or not read.</summary>
    public string BufferKind => Buffer?.ClassName switch
    {
        "CAnimationBufferBitwiseCompressed" => "Compressed",
        "CAnimationBufferMultipart" => "Compressed, in parts",
        null => "None",
        var c => ObjectTree.Words(c, "CAnimationBuffer"),
    };

    /// <summary>The size of the bone data before it was compressed, as the file says.</summary>
    public long SourceSize => Buffer?.Integer("sourceDataSize") ?? 0;

    /// <summary>The compressed bone data kept in the file itself (the rest, when it streams, is in a buffer).</summary>
    public long KeptSize => Buffer is null ? 0 : (Buffer["data"] as byte[])?.Length ?? 0;

    /// <summary>The bones the animation moves.</summary>
    public int Bones => Buffer?.Array("bones").Count ?? 0;

    /// <summary>How far the animation moves the character over the ground, and turns it, by its end.</summary>
    public (float Distance, float TurnDegrees) Travel
    {
        get
        {
            if (Motion is null) return (0, 0);
            var (position, yaw) = Motion.At(Duration);
            return (new System.Numerics.Vector2(position.X, position.Y).Length(), yaw * 180 / MathF.PI);
        }
    }
}

/// <summary>
/// An animation set (.w2anims, a CSkeletalAnimationSet): the animations a kind of character plays for something (a
/// weapon's attacks, a job's work), each in an entry with the events it fires, and the events files kept apart from it.
/// </summary>
public sealed class AnimSetFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Resource { get; init; }
    public required List<AnimSetAnimation> Animations { get; init; }

    /// <summary>The events files (.w2animev) the set names, whose events are on its animations too.</summary>
    public required List<string> EventFiles { get; init; }

    /// <summary>The rig the set says its animations are for, when it names one.</summary>
    public string? Skeleton { get; init; }

    public static bool IsAnimSet(string path) => path.EndsWith(".w2anims", StringComparison.OrdinalIgnoreCase);

    public static AnimSetFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var set = file.First("CSkeletalAnimationSet") ?? throw new InvalidDataException("This isn't an animation set.");
        var animations = new List<AnimSetAnimation>();
        foreach (var handle in set.Array("animations").OfType<RedHandle>())
        {
            if (file.Object(handle) is not { } entry) continue;
            var animation = entry["animation"] is RedHandle a ? file.Object(a) : null;
            var buffer = animation?["animBuffer"] is RedHandle b ? file.Object(b) : null;
            string name = animation?["name"] as string ?? "";
            var events = file.TailObjects(entry).Select(o => Event(o, name)).ToList();
            animations.Add(new AnimSetAnimation(entry, animation, buffer, events, animation is null ? null : MotionExtraction.Read(file, animation)));
        }
        return new AnimSetFile
        {
            File = file,
            Resource = set,
            Animations = animations,
            EventFiles = set.Array("extAnimEvents").OfType<RedHandle>().Select(file.PathOf).OfType<string>().ToList(),
            Skeleton = set["skeleton"] is RedHandle s ? file.PathOf(s) : null,
        };
    }

    /// <summary>An event an entry or an events file keeps, on an animation.</summary>
    public static AnimEvent Event(RedObject o, string animation) => new(
        o["animationName"] as string is { Length: > 0 } named && named != "None" ? named : animation,
        o["trackName"] as string is { Length: > 0 } t && t != "None" ? t : "__Default",
        o["eventName"] as string ?? ObjectTree.Words(o.ClassName, "CExtAnim", "CEAS", "C"),
        o.Float("startTime"),
        o.Float("duration"),
        o);
}
