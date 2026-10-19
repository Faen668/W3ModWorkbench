using System.IO;
using System.Text;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Cutscenes;

/// <summary>What a cutscene event does, to sort and colour them by.</summary>
public enum CutsceneEventKind { Dialog, Sound, Effect, Item, Appearance, Camera, Light, Cloth, Morph, World, Quest, Other }

/// <summary>
/// Something that happens at a time in a cutscene: a line, a sound, an effect, an item put in a hand. Scene events
/// belong to the whole cutscene (<see cref="Actor"/> is null); the others to one actor's animation.
/// </summary>
public sealed record CutsceneEvent(float Start, float Duration, CutsceneEventKind Kind, string Title, string Detail, string ClassName, string? Actor, string? Link)
{
    public float End => Start + Duration;

    /// <summary>The event as the file keeps it, for its properties.</summary>
    public RedObject? Source { get; init; }
}

/// <summary>One of an actor's animations: its body ("Root") or face, which play for the whole cutscene or part of it.</summary>
public sealed record CutsceneAnimation(string Actor, string Part, string Name, float Duration, float FramesPerSecond, IReadOnlyList<CutsceneEvent> Events)
{
    /// <summary>The animation (a CSkeletalAnimation) and its entry in the set (a CSkeletalAnimationSetEntry), for their properties.</summary>
    public RedObject? Object { get; init; }
    public RedObject? Entry { get; init; }

    /// <summary>The animation's full name, as REDkit lists it: actor:part:animation.</summary>
    public string FullName => $"{Actor}:{Part}:{Name}";
}

/// <summary>Someone or something in a cutscene: an actor, a prop or a camera, and the entity it's made from.</summary>
public sealed class CutsceneActor
{
    public required string Name { get; init; }

    /// <summary>Actor, Prop, Camera, or "Not listed" for an animation whose actor the cutscene doesn't define.</summary>
    public required string Type { get; init; }
    public string? Template { get; init; }
    public string? Appearance { get; init; }
    public string? VoiceTag { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool UseMimic { get; init; }

    /// <summary>Whether the actor is despawned when the cutscene ends.</summary>
    public bool KillMe { get; init; }
    public List<CutsceneAnimation> Animations { get; } = [];

    public IEnumerable<CutsceneEvent> Events => Animations.SelectMany(a => a.Events);
}

/// <summary>An effect the cutscene defines for its effect events: how long it lasts and the particles and lights it's made of.</summary>
public sealed record CutsceneEffect(string Name, float Length, bool Looped, IReadOnlyList<string> Parts)
{
    /// <summary>The effect (a CFXDefinition) as the file keeps it, for its properties.</summary>
    public RedObject? Source { get; init; }
}

/// <summary>A file the cutscene refers to, and what kind it is.</summary>
public sealed record CutsceneLink(string Path, string Role);

/// <summary>
/// A cutscene (.w2cutscene): a CCutsceneTemplate with the actors it spawns, an animation for each actor's body and face
/// (and the camera's), events timed along them, and effects. The scene events (lines, music, fades, effects, lights) are
/// kept after the template's properties, and each animation's own events (items, appearances, cloth) after its set
/// entry's, as lists of (size, class, properties). The animations' bone data isn't read.
/// </summary>
public sealed class CutsceneFile
{
    public required Cr2wFile File { get; init; }

    /// <summary>The cutscene itself, a CCutsceneTemplate, for its properties.</summary>
    public required RedObject Template { get; init; }
    public required float Duration { get; init; }
    public required List<CutsceneActor> Actors { get; init; }

    /// <summary>The events of the whole cutscene, by time.</summary>
    public required List<CutsceneEvent> SceneEvents { get; init; }
    public required List<CutsceneEffect> Effects { get; init; }

    /// <summary>The scenes (.w2scene) that play it.</summary>
    public required List<string> UsedIn { get; init; }

    /// <summary>The world it was made in.</summary>
    public string? Level { get; init; }

    /// <summary>The audio event that plays its recorded sound and voices.</summary>
    public string? AudioTrack { get; init; }

    /// <summary>Tags of things in the world it hides while it plays.</summary>
    public required List<string> HiddenTags { get; init; }

    /// <summary>Tags of where in the world it's played.</summary>
    public required List<string> PlaceTags { get; init; }
    public float FadeBefore { get; init; }
    public float FadeAfter { get; init; }

    /// <summary>Every event, the scene's and the actors', by time.</summary>
    public IEnumerable<CutsceneEvent> AllEvents => SceneEvents.Concat(Actors.SelectMany(a => a.Events)).OrderBy(e => e.Start);

    /// <summary>The files it refers to: the actors' entities, effects' particles and materials, the scenes and the world.</summary>
    public IEnumerable<CutsceneLink> Links =>
        File.Imports.Select(i => new CutsceneLink(i.Path, Role(i.Path)))
            .Concat(UsedIn.Select(p => new CutsceneLink(p, "Scene that plays it")))
            .Concat(Level is null ? [] : [new CutsceneLink(Level, "World it was made in")])
            .DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase);

    private static string Role(string path) => FileKinds.Of(path)?.Name ?? "File";

    /// <summary>
    /// Puts words to the dialog events, which are only marks: the cutscene's section in the scene that plays it has its
    /// lines in the same order. Done only when there are as many lines as marks, so none is put at the wrong time.
    /// </summary>
    public bool NameDialogLines(IReadOnlyList<(string Speaker, string Text)> lines)
    {
        var marks = SceneEvents.Select((e, i) => (e, i)).Where(x => x.e.Kind == CutsceneEventKind.Dialog).OrderBy(x => x.e.Start).ToList();
        if (marks.Count == 0 || marks.Count != lines.Count) return false;
        for (int n = 0; n < marks.Count; n++)
        {
            var (mark, index) = marks[n];
            SceneEvents[index] = mark with { Title = lines[n].Text, Detail = lines[n].Speaker };
        }
        return true;
    }

    /// <summary>
    /// The near clipping plane a clipping-planes event sets, in metres: one of the engine's steps, its custom distance,
    /// or, left unset, the environment's (which isn't known here, so a typical 25 cm).
    /// </summary>
    public static float NearPlane(RedObject clipping)
    {
        string name = clipping["nearPlaneDistance"] is RedEnum near ? near.ToString() : "NP_DefaultEnv";
        return name switch
        {
            "NP_VeryClose5cm" => 0.05f,
            "NP_Close10cm" => 0.1f,
            "NP_Medium25cm" => 0.25f,
            "NP_Further40cm" => 0.4f,
            "NP_Far60cm" => 0.6f,
            "NP_VeryFar120cm" => 1.2f,
            "NP_Extreme240cm" => 2.4f,
            "NP_CustomValue" => clipping.Struct("customPlaneDistance")?.Float("nearPlaneDistance") is > 0 and var custom ? custom : 0.25f,
            _ => 0.25f,
        };
    }

    /// <summary>Whether a file's name says it's a cutscene.</summary>
    public static bool IsCutscene(string name) => name.EndsWith(".w2cutscene", StringComparison.OrdinalIgnoreCase);

    public static CutsceneFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var template = file.First("CCutsceneTemplate") ?? throw new InvalidDataException("There's no cutscene in it: it has no CCutsceneTemplate.");

        var actors = new List<CutsceneActor>();
        foreach (var def in template.Array("actorsDef").OfType<RedObject>())
        {
            actors.Add(new CutsceneActor
            {
                Name = def["name"] as string ?? "",
                Type = Humanize((def["type"] as RedEnum)?.ToString() ?? "CAT_Actor", "CAT_"),
                Template = def["template"] is RedHandle h ? file.Import(h)?.Path : null,
                Appearance = NameOrNull(def["appearance"]),
                VoiceTag = NameOrNull(def["voiceTag"]),
                Tags = file.Tags(def.Bytes("tag")),
                UseMimic = def["useMimic"] is true,
                KillMe = def["killMe"] is true,
            });
        }

        // each animation is named actor:part:animation, part being Root for the body and face for the face
        foreach (var handle in template.Array("animations").OfType<RedHandle>())
        {
            if (file.Object(handle) is not { } entry || entry["animation"] is not RedHandle a || file.Object(a) is not { } animation) continue;
            string full = animation["name"] as string ?? "";
            var parts = full.Split(':', 3);
            string actorName = parts.Length == 3 ? parts[0] : "";
            string part = parts.Length == 3 ? parts[1] : "";
            string name = parts.Length == 3 ? parts[2] : full;
            var actor = actors.FirstOrDefault(x => x.Name == actorName);
            if (actor is null)
            {
                actor = new CutsceneActor { Name = actorName.Length > 0 ? actorName : "(unnamed)", Type = "Not listed" };
                actors.Add(actor);
            }
            var events = file.TailObjects(entry).Select(e => Describe(file, e, actor.Name)).OrderBy(e => e.Start).ToList();
            actor.Animations.Add(new CutsceneAnimation(actor.Name, part, name, animation.Float("duration"), animation.Float("framesPerSecond", 30), events)
            {
                Object = animation,
                Entry = entry,
            });
        }

        // the template keeps four bytes before its events
        var sceneEvents = file.TailObjects(template, 4).Select(e => Describe(file, e, null)).OrderBy(e => e.Start).ToList();

        var effects = template.Array("effects").OfType<RedHandle>().Select(file.Object).OfType<RedObject>().Select(fx => ReadEffect(file, fx)).ToList();

        float duration = actors.SelectMany(x => x.Animations).Select(x => x.Duration).DefaultIfEmpty(0).Max();
        if (duration <= 0) duration = sceneEvents.Concat(actors.SelectMany(x => x.Events)).Select(e => e.End).DefaultIfEmpty(0).Max();

        return new CutsceneFile
        {
            File = file,
            Template = template,
            Duration = duration,
            Actors = actors,
            SceneEvents = sceneEvents,
            Effects = effects,
            UsedIn = template.Array("usedInFiles").OfType<string>().ToList(),
            Level = template["lastLevelLoaded"] as string is { Length: > 0 } level ? level : null,
            AudioTrack = template["burnedAudioTrackName"] as string is { Length: > 0 } audio ? audio : null,
            HiddenTags = template.Array("entToHideTags").OfType<string>().ToList(),
            PlaceTags = file.Tags(template.Bytes("point")),
            FadeBefore = template.Float("fadeBefore"),
            FadeAfter = template.Float("fadeAfter"),
        };
    }

    /// <summary>An effect, with the particle systems and materials its tracks use, and its lights.</summary>
    private static CutsceneEffect ReadEffect(Cr2wFile file, RedObject fx)
    {
        var parts = new List<string>();
        foreach (var group in fx.Array("trackGroups").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
            foreach (var track in group.Array("tracks").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
                foreach (var item in track.Array("trackItems").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
                {
                    string? part = item.ClassName switch
                    {
                        "CFXTrackItemParticles" => item["particleSystem"] is RedHandle p ? file.Import(p)?.Path : null,
                        "CFXTrackItemMaterialOverride" => item["material"] is RedHandle m ? file.Import(m)?.Path ?? "material override" : null,
                        "CFXTrackItemDynamicLight" => "dynamic light",
                        "CFXTrackItemFlare" => "flare",
                        _ => Humanize(item.ClassName, "CFXTrackItem"),
                    };
                    if (part is not null && !parts.Contains(part)) parts.Add(part);
                }
        return new CutsceneEffect(fx["name"] as string ?? "", fx.Float("length"), fx["isLooped"] is true, parts) { Source = fx };
    }

    /// <summary>An event in words: what kind it is, a short title, and the rest of what it says.</summary>
    private static CutsceneEvent Describe(Cr2wFile file, RedObject e, string? actor)
    {
        float start = e.Float("startTime");
        float duration = e.Float("duration", e.Float("durationTime"));
        string eventName = e["eventName"] as string ?? "";
        string? link = null;
        CutsceneEventKind kind;
        string title, detail;
        switch (e.ClassName)
        {
            case "CExtAnimCutsceneDialogEvent":
                (kind, title, detail) = (CutsceneEventKind.Dialog, "Dialog line", eventName);
                break;
            case "CExtAnimCutsceneSoundEvent":
                (kind, title, detail) = (CutsceneEventKind.Sound, e["soundEventName"] as string ?? eventName, Join(eventName, Bone(e)));
                break;
            case "CExtAnimFootstepEvent":
                (kind, title, detail) = (CutsceneEventKind.Sound, e["soundEventName"] as string ?? eventName, Join("footstep", Bone(e)));
                break;
            case "CExtAnimCutsceneEffectEvent":
                link = e["template"] is RedHandle t ? file.Import(t)?.Path : null;
                (kind, title, detail) = (CutsceneEventKind.Effect, NameOrNull(e["effect"]) ?? eventName,
                    Join(link is null ? null : "on " + Path.GetFileName(link), e.Struct("spawnPosMS") is { } at ? $"at {at.Float("X"):0.##}, {at.Float("Y"):0.##}, {at.Float("Z"):0.##}" : null));
                break;
            case "CExtAnimCutsceneActorEffect":
                (kind, title, detail) = (CutsceneEventKind.Effect, NameOrNull(e["effectName"]) ?? eventName, "on the actor");
                break;
            case "CExtAnimItemEffectEvent":
                (kind, title, detail) = (CutsceneEventKind.Effect, NameOrNull(e["effectName"]) ?? eventName, Join("on the item", eventName));
                break;
            case "CExtAnimItemEvent":
                string? item = NameOrNull(e["itemName_optional"]), category = NameOrNull(e["category"]);
                (kind, title, detail) = (CutsceneEventKind.Item, item ?? category ?? eventName,
                    Join(e["action"] is RedEnum action ? Humanize(action.ToString(), "IA_") : null, item is null ? null : category));
                break;
            case "CExtAnimCutsceneBodyPartEvent":
                (kind, title, detail) = (CutsceneEventKind.Appearance, NameOrNull(e["appearance"]) ?? eventName, "appearance");
                break;
            case "CExtAnimCutsceneFadeEvent":
                (kind, title, detail) = (CutsceneEventKind.Camera, e["in"] is false ? "Fade out" : "Fade in",
                    Join(duration > 0 ? $"over {duration:0.##} s" : null, e.Struct("color") is { } c ? $"to #{c.Integer("Red"):X2}{c.Integer("Green"):X2}{c.Integer("Blue"):X2}" : null));
                break;
            case "CExtAnimCutsceneSetClippingPlanesEvent":
                // the custom distances count only when a plane is set to its custom value; one left unset is the environment's
                string nearName = e["nearPlaneDistance"] is RedEnum near ? near.ToString() : "NP_DefaultEnv";
                string farName = e["farPlaneDistance"] is RedEnum far ? far.ToString() : "FP_DefaultEnv";
                var custom = e.Struct("customPlaneDistance");
                string nearText = nearName == "NP_CustomValue" ? $"near {custom?.Float("nearPlaneDistance") ?? 0:0.###} m" : "near: " + Humanize(nearName, "NP_");
                string farText = farName == "FP_CustomValue" ? $"far {custom?.Float("farPlaneDistance") ?? 0:0.#} m" : "far: " + Humanize(farName, "FP_");
                (kind, title, detail) = (CutsceneEventKind.Camera, "Clipping planes", $"{nearText}, {farText}");
                break;
            case "CExtAnimCutsceneBokehDofEvent":
            case "CExtAnimCutsceneBokehDofBlendEvent":
                var dof = e.Struct("bokehDofParams") ?? e.Struct("bokehDofParamsEnd");
                (kind, title, detail) = (CutsceneEventKind.Camera, "Depth of field", dof is null ? eventName : $"focus at {dof.Float("planeInFocus"):0.##} m");
                break;
            case "CExtAnimCutsceneLightEvent":
                (kind, title, detail) = (CutsceneEventKind.Light, eventName,
                    Join(string.Join(", ", file.Tags(e.Bytes("tag"))), $"brightness {e.Float("brightness"):0.##}", e["radius"] is null ? null : $"radius {e.Float("radius"):0.##}"));
                break;
            case "CExtAnimCutsceneResetClothAndDangleEvent":
                (kind, title, detail) = (CutsceneEventKind.Cloth, "Reset cloth and dangles", e["forceRelaxedState"] is true ? "relaxed" : "");
                break;
            case "CExtAnimCutsceneDisableClothEvent":
                (kind, title, detail) = (CutsceneEventKind.Cloth, "Cloth off", Weight(e));
                break;
            case "CExtAnimCutsceneDisableDangleEvent":
                (kind, title, detail) = (CutsceneEventKind.Cloth, "Dangles off", Weight(e));
                break;
            case "CExtAnimMorphEvent":
                (kind, title, detail) = (CutsceneEventKind.Morph, NameOrNull(e["morphComponentId"]) ?? eventName, e["invertWeight"] is true ? "morph, inverted" : "morph");
                break;
            case "CExtAnimCutsceneQuestEvent":
                (kind, title, detail) = (CutsceneEventKind.Quest, eventName, "tells the quest");
                break;
            case "CExtAnimCutsceneHideTerrainEvent":
                (kind, title, detail) = (CutsceneEventKind.World, "Terrain hidden", eventName);
                break;
            case "CExtAnimCutsceneWindEvent":
                (kind, title, detail) = (CutsceneEventKind.World, "Wind", $"× {e.Float("factor", 1):0.##}");
                break;
            case "CExtAnimCutsceneSurfaceEffect":
                (kind, title, detail) = (CutsceneEventKind.World, eventName, $"surface effect, radius {e.Float("radius"):0.##}");
                break;
            default:
                (kind, title, detail) = (CutsceneEventKind.Other, eventName.Length > 0 ? eventName : e.ClassName, Humanize(e.ClassName, "CExtAnim"));
                break;
        }
        return new CutsceneEvent(start, duration, kind, title, detail, e.ClassName, actor, link) { Source = e };
    }

    private static string? Bone(RedObject e) => NameOrNull(e["bone"]) is { } bone && !bone.StartsWith('(') ? "at " + bone : null;

    private static string Weight(RedObject e) => e["weight"] is null ? "" : $"weight {e.Float("weight"):0.##}";

    /// <summary>A name that isn't empty or None, else null.</summary>
    private static string? NameOrNull(object? value) => value is string s && s.Length > 0 && s != "None" ? s : null;

    private static string Join(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>An engine name in words: "IA_MountToRightHand" → "Mount to right hand", "CAT_Actor" → "Actor".</summary>
    public static string Humanize(string name, string prefix)
    {
        if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
        if (name.EndsWith("Event", StringComparison.Ordinal) && name.Length > 5) name = name[..^5];
        var words = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '_') { words.Append(' '); continue; }
            bool boundary = i > 0 && (char.IsUpper(c) && (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))
                || char.IsDigit(c) && char.IsLetter(name[i - 1]));
            if (boundary) words.Append(' ');
            words.Append(words.Length == 0 ? c : boundary && i + 1 < name.Length && char.IsLower(name[i + 1]) ? char.ToLowerInvariant(c) : c);
        }
        string result = words.ToString().Trim();
        return result.Length > 0 ? char.ToUpperInvariant(result[0]) + result[1..] : result;
    }
}
