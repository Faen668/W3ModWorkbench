using System.Numerics;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Scenes;

/// <summary>What a section's timeline event does, for grouping it onto a track.</summary>
public enum SceneEventKind { Camera, Animation, Mimics, LookAt, Light, Placement, Sound, Fade, Other }

/// <summary>Something a section plays in its own time: a line, a pause or a choice, and how long it lasts.</summary>
public sealed record SceneTimelineElement(string Id, SceneElementKind Kind, float Start, float Duration, string? Speaker, uint? StringId, RedObject Source);

/// <summary>
/// An event on a section's timeline: when it starts and how long it lasts in seconds from the section's start, whom
/// it's for, and the object it was read from (for its properties).
/// </summary>
public sealed record SceneTimelineEvent(string ClassName, string Name, SceneEventKind Kind, string? Actor, float Start, float Duration, RedObject Source);

/// <summary>
/// A section's timeline, as REDkit's Timeline tab lays it out: its elements one after another (each line as long as
/// its recorded voice, each pause and choice as long as it's set to), and the events it keeps after its properties,
/// each placed at a share (<c>startPosition</c>) of the element it hangs on. Only the section's default variant's
/// events are kept, as the game plays them for every language.
/// </summary>
public sealed class SceneSectionTimeline
{
    public required string Name { get; init; }
    public required float Duration { get; init; }
    public required List<SceneTimelineElement> Elements { get; init; }
    public required List<SceneTimelineEvent> Events { get; init; }

    /// <summary>The actors with events on the timeline, in the order they first appear.</summary>
    public IEnumerable<string> Actors => Events.Select(e => e.Actor).OfType<string>().Where(a => a.Length > 0).Distinct();

    /// <summary>
    /// Reads a section's timeline. A cutscene section's lines are said by its cutscene, not one after another:
    /// <paramref name="cutsceneLines"/> are the cutscene's dialog marks (start and length, in order), which put them
    /// at their times when there are as many as lines; without them a cutscene section's lines aren't placed.
    /// </summary>
    public static SceneSectionTimeline Read(Cr2wFile file, RedObject section, IReadOnlyList<(float Start, float Duration)>? cutsceneLines = null)
    {
        bool cutscene = section.ClassName == "CStorySceneCutsceneSection";
        var lineCount = section.Array("sceneElements").OfType<RedHandle>().Count(h => file.Object(h)?.ClassName == "CStorySceneLine");
        if (cutsceneLines is not null && cutsceneLines.Count != lineCount) cutsceneLines = null;
        int lineNumber = 0;
        // the default variant: its approved element durations, and the events it uses
        RedObject? variant = null;
        var defaultId = section["defaultVariantId"] is { } d ? Convert.ToInt32(d) : 0;
        foreach (var handle in section.Array("variants").OfType<RedHandle>())
            if (file.Object(handle) is { } v && (v["id"] is { } id ? Convert.ToInt32(id) : 0) == defaultId) variant = v;
        var approved = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var info in variant?.Array("elementInfo").OfType<RedObject>() ?? [])
            if (info["elementId"] is string id) approved[id] = info.Float("approvedDuration");
        var usedEvents = variant?.Array("events").OfType<Guid>().ToHashSet();

        var elements = new List<SceneTimelineElement>();
        var byExport = new Dictionary<int, SceneTimelineElement>();
        float time = 0;
        foreach (var handle in section.Array("sceneElements").OfType<RedHandle>())
        {
            if (file.Object(handle) is not { } e) continue;
            string id = e["elementID"] as string ?? "";
            var (kind, duration) = e.ClassName switch
            {
                "CStorySceneLine" => (SceneElementKind.Line, approved.GetValueOrDefault(id, e.Float("approvedDuration", 3))),
                "CStoryScenePauseElement" => (SceneElementKind.Pause, approved.GetValueOrDefault(id, e.Float("approvedDuration", e.Float("duration", 1)))),
                "CStorySceneChoice" => (SceneElementKind.Choice, approved.GetValueOrDefault(id, e.Float("approvedDuration", 10))),
                "CStorySceneComment" => (SceneElementKind.Comment, 0f),
                "CStorySceneCutscenePlayer" => (SceneElementKind.Cutscene, approved.GetValueOrDefault(id, e.Float("approvedDuration"))),
                _ => (SceneElementKind.Other, approved.GetValueOrDefault(id, e.Float("approvedDuration", e.Float("duration")))),
            };
            uint? stringId = e["dialogLine"] is { } line ? Convert.ToUInt32(line) : null;
            float start = time;
            if (cutscene && kind == SceneElementKind.Line)
            {
                // said in the cutscene at its mark (a moment: the line keeps its own length), with the cutscene's time running on
                if (cutsceneLines is null) continue;
                var (at, length) = cutsceneLines[lineNumber++];
                start = at;
                if (length > 0) duration = length;
                // a cutscene's line keeps no length of its own: drawn up to the next one, a few seconds at most
                if (duration <= 0) duration = Math.Clamp((lineNumber < cutsceneLines.Count ? cutsceneLines[lineNumber].Start : at + 4) - at, 0.5f, 4);
            }
            var element = new SceneTimelineElement(id, kind, start, duration, e["voicetag"] as string, stringId, e);
            elements.Add(element);
            if (handle.Export is int export) byExport[export] = element;
            if (!(cutscene && kind == SceneElementKind.Line)) time += duration;
        }

        var events = new List<SceneTimelineEvent>();
        foreach (var e in file.TailObjects(section))
        {
            if (usedEvents is { Count: > 0 } && e["GUID"] is Guid guid && !usedEvents.Contains(guid)) continue;
            // placed at a share of the element it hangs on; one on no element sits at the section's start
            float start = 0;
            if (e["sceneElement"] is RedHandle on && on.Export is int export && byExport.TryGetValue(export, out var element))
                start = element.Start + e.Float("startPosition") * element.Duration;
            events.Add(new SceneTimelineEvent(e.ClassName, NameOf(e), KindOf(e.ClassName), e["actor"] as string, start, e.Float("duration"), e));
        }
        events.Sort((a, b) => a.Start.CompareTo(b.Start));
        // an event's name: a camera cut by the camera it cuts to, otherwise the name it was given, the animation's own
        // friendly name, the face it pulls, or the animation, before what kind of event it is
        static string NameOf(RedObject e)
        {
            static string? Set(object? value) => value as string is { Length: > 0 } s && s != "None" ? s : null;
            return (e.ClassName == "CStorySceneEventCustomCameraInstance" ? Set(e["customCameraName"]) : null)
                ?? Set(e["eventName"]) ?? Set(e["friendlyName"])
                ?? (Set(e["mimicsLayer_Pose"]) is { } pose ? $"{pose} face" : null)
                ?? Set(e["animationName"]) ?? Set(e["customCameraName"]) ?? Friendly(e.ClassName);
        }
        return new SceneSectionTimeline { Name = section["sectionName"] as string ?? "", Duration = time, Elements = elements, Events = events };
    }

    private static SceneEventKind KindOf(string className) => className switch
    {
        _ when className.Contains("Camera", StringComparison.Ordinal) && className.Contains("Light", StringComparison.Ordinal) => SceneEventKind.Light,
        _ when className.Contains("Camera", StringComparison.Ordinal) => SceneEventKind.Camera,
        _ when className.Contains("Mimic", StringComparison.Ordinal) => SceneEventKind.Mimics,
        _ when className.Contains("LookAt", StringComparison.Ordinal) => SceneEventKind.LookAt,
        _ when className.Contains("Animation", StringComparison.Ordinal) || className.Contains("Pose", StringComparison.Ordinal) => SceneEventKind.Animation,
        _ when className.Contains("Placement", StringComparison.Ordinal) => SceneEventKind.Placement,
        _ when className.Contains("Sound", StringComparison.Ordinal) => SceneEventKind.Sound,
        _ when className.Contains("Fade", StringComparison.Ordinal) => SceneEventKind.Fade,
        _ => SceneEventKind.Other,
    };

    /// <summary>A class's name as words, for an event with no name of its own: CStorySceneEventCameraAnim as "Camera anim".</summary>
    private static string Friendly(string className)
    {
        string name = className.Replace("CStorySceneEvent", "").Replace("CStoryScene", "");
        var words = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return words.Length > 1 ? char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant() : words;
    }
}

/// <summary>
/// A place an actor stands in a dialog set: its slot, who stands there, where, and its rotation as stored (three angles in
/// degrees; the third is the turn round the upright, so 180 faces the other way), as REDkit's Dialogset settings list it.
/// </summary>
public sealed record SceneDialogsetSlot(int Number, string Name, string? Actor, Vector3 Position, Vector3 Rotation, RedObject Source);

/// <summary>A dialog set the scene uses: where its actors stand, and the placement tag that puts it in the world.</summary>
public sealed record SceneDialogset(string Name, string? Path, List<string> PlacementTags, List<SceneDialogsetSlot> Slots, RedObject Source);

/// <summary>A camera the scene defines by name, for its camera events: where it stands and its lens.</summary>
public sealed record SceneCameraDefinition(string Name, Vector3 Position, Vector3 Rotation, float FieldOfView, RedObject Source);

/// <summary>What a scene keeps besides its flow: its dialog sets and its cameras.</summary>
public static class SceneStaging
{
    public static List<SceneDialogset> Dialogsets(Cr2wFile file, RedObject scene)
    {
        var result = new List<SceneDialogset>();
        foreach (var handle in scene.Array("dialogsetInstances").OfType<RedHandle>())
        {
            if (file.Object(handle) is not { } set) continue;
            var slots = new List<SceneDialogsetSlot>();
            foreach (var slotHandle in set.Array("slots").OfType<RedHandle>())
                if (file.Object(slotHandle) is { } slot)
                {
                    var (position, rotation) = Transform(slot["slotPlacement"]);
                    slots.Add(new SceneDialogsetSlot(slot["slotNumber"] is { } n ? Convert.ToInt32(n) : slots.Count + 1, slot["slotName"] as string ?? "",
                        slot["actorName"] as string, position, rotation, slot));
                }
            result.Add(new SceneDialogset(set["name"] as string ?? "", set["path"] as string, set.Array("placementTag").OfType<string>().ToList(), slots, set));
        }
        return result;
    }

    public static List<SceneCameraDefinition> Cameras(RedObject scene) =>
        scene.Array("cameraDefinitions").OfType<RedObject>().Select(c =>
        {
            var (position, rotation) = Transform(c["cameraTransform"]);
            return new SceneCameraDefinition(c["cameraName"] as string ?? "", position, rotation, c.Float("cameraFov"), c);
        }).ToList();

    /// <summary>
    /// An EngineTransform's position and rotation (three angles in degrees as stored, the third the turn round the upright):
    /// kept as a byte saying which parts there are, then three floats for each. Zero for what isn't there.
    /// </summary>
    public static (Vector3 Position, Vector3 Rotation) Transform(object? value)
    {
        if (value is not byte[] raw || raw.Length < 1) return (Vector3.Zero, Vector3.Zero);
        int parts = raw[0], at = 1;
        Vector3 Next()
        {
            if (at + 12 > raw.Length) return Vector3.Zero;
            var v = new Vector3(BitConverter.ToSingle(raw, at), BitConverter.ToSingle(raw, at + 4), BitConverter.ToSingle(raw, at + 8));
            at += 12;
            return v;
        }
        var position = (parts & 1) != 0 ? Next() : Vector3.Zero;
        var rotation = (parts & 2) != 0 ? Next() : Vector3.Zero;
        return (position, rotation);
    }
}
