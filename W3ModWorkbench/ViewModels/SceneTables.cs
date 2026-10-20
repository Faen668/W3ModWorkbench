using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Scenes;

namespace W3ModWorkbench.ViewModels;

/// <summary>A voice tag a start of the scene needs, as REDkit's Starting Conditions list them.</summary>
public sealed record SceneVoicetagRow(string Input, string Voicetag, string ContextActor, string Invulnerable, string Optional)
{
    public static List<SceneVoicetagRow> Of(SceneFile scene)
    {
        var rows = new List<SceneVoicetagRow>();
        foreach (var export in scene.File.Exports.Where(x => x.ClassName == "CStorySceneInput"))
        {
            var input = scene.File.Object(export.Index);
            string name = input["inputName"] as string is { Length: > 0 } n ? n : "Input";
            foreach (var mapping in input.Array("voicetagMappings").OfType<RedObject>())
            {
                if (mapping["voicetag"] is not string tag || tag.Length == 0 || tag == "None") continue;
                rows.Add(new(name, tag, Tick(mapping["mustUseContextActor"]), Tick(mapping["invulnerable"]), Tick(mapping["actorOptional"])));
            }
        }
        return rows;
    }

    private static string Tick(object? value) => value is true ? "✓" : "";
}

/// <summary>An actor the scene defines, as REDkit's Definitions tab lists it.</summary>
public sealed record SceneActorRow(string Id, string Tags, string? Template, string AppearanceFilter, string Flags, int Lines)
{
    public string TemplateName => System.IO.Path.GetFileName(Template ?? "");

    public static List<SceneActorRow> Of(SceneFile scene)
    {
        var spoken = scene.Lines.GroupBy(l => l.Speaker ?? "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<SceneActorRow>();
        foreach (var export in scene.File.Exports.Where(x => x.ClassName is "CStorySceneActor" or "CStorySceneProp"))
        {
            var actor = scene.File.Object(export.Index);
            string id = actor["id"] as string ?? "";
            string? template = actor["entityTemplate"] is RedHandle h ? scene.File.Import(h)?.Path : null;
            var flags = new List<string>();
            if (export.ClassName == "CStorySceneProp") flags.Add("prop");
            if (actor["dontSearchByVoicetag"] is true) flags.Add("not found by voice tag");
            if (actor["useHiresShadows"] is true) flags.Add("hi-res shadows");
            if (actor["forceSpawn"] is true) flags.Add("always spawned");
            if (actor["useMimic"] is true) flags.Add("face animated");
            rows.Add(new(id, string.Join(", ", actor.Array("actorTags").OfType<string>()), template,
                string.Join(", ", actor.Array("appearanceFilter").OfType<string>()), string.Join(", ", flags), spoken.GetValueOrDefault(id)));
        }
        return rows;
    }
}

/// <summary>An animation the scene's timelines play, with whom and how often, as REDkit's Body and Mimics Anims tabs gather them.</summary>
public sealed record SceneAnimationRow(string Actor, string Kind, string Animation, int Uses, string Sections)
{
    public static List<SceneAnimationRow> Of(IEnumerable<SceneSectionTimeline> timelines)
    {
        var uses = new Dictionary<(string, string, string), (int Count, List<string> Sections)>();
        foreach (var timeline in timelines)
            foreach (var e in timeline.Events)
            {
                if (e.Source["animationName"] is not string name || name.Length == 0 || name == "None") continue;
                string kind = e.ClassName switch
                {
                    "CStorySceneEventMimicsAnim" => "Face",
                    "CStorySceneEventAdditiveAnimation" => "Additive",
                    "CStorySceneEventCameraAnim" => "Camera",
                    _ when e.Kind == SceneEventKind.Mimics => "Face",
                    _ => "Body",
                };
                var key = (e.Actor ?? "", kind, name);
                if (!uses.TryGetValue(key, out var seen)) seen = (0, []);
                if (!seen.Sections.Contains(timeline.Name)) seen.Sections.Add(timeline.Name);
                uses[key] = (seen.Count + 1, seen.Sections);
            }
        return uses.Select(u => new SceneAnimationRow(u.Key.Item1, u.Key.Item2, u.Key.Item3, u.Value.Count, string.Join(", ", u.Value.Sections)))
            .OrderBy(r => r.Actor).ThenBy(r => r.Kind).ThenBy(r => r.Animation).ToList();
    }
}

/// <summary>A camera the scene defines by name, with its lens and where it stands.</summary>
public sealed record SceneCameraRow(string Name, string Lens, string Place, SceneCameraDefinition Camera)
{
    public static List<SceneCameraRow> Of(IEnumerable<SceneCameraDefinition> cameras) =>
        cameras.Select(c => new SceneCameraRow(c.Name, $"{c.FieldOfView:0.#}°",
            $"{c.Position.X:0.00}, {c.Position.Y:0.00}, {c.Position.Z:0.00}", c)).ToList();
}

/// <summary>A dialog set in the list, by name, with how many stand in it.</summary>
public sealed record SceneDialogsetRow(string Name, string Detail, SceneDialogset Set)
{
    public static List<SceneDialogsetRow> Of(IEnumerable<SceneDialogset> sets) =>
        sets.Select(s => new SceneDialogsetRow(s.Name, $"{s.Slots.Count} {(s.Slots.Count == 1 ? "place" : "places")}"
            + (s.PlacementTags.Count > 0 ? $" · {string.Join(", ", s.PlacementTags)}" : ""), s)).ToList();
}

/// <summary>A place in a dialog set: who stands there, where, and which way they face.</summary>
public sealed record SceneSlotRow(string Slot, string Actor, string Place, string Facing)
{
    public static List<SceneSlotRow> Of(SceneDialogset set) =>
        set.Slots.Select(s => new SceneSlotRow(s.Name, s.Actor ?? "", $"{s.Position.X:0.00}, {s.Position.Y:0.00}, {s.Position.Z:0.00}",
            $"{s.Rotation.Z:0.#}°")).ToList();
}

/// <summary>A version of a section, as REDkit's Section tab lists them: which language it's for and how long it plays.</summary>
public sealed record SceneVariantRow(string Variant, string Language, string Events, string Length)
{
    public static List<SceneVariantRow> Of(Cr2wFile file, RedObject section)
    {
        int defaultId = section["defaultVariantId"] is { } d ? Convert.ToInt32(d) : 0;
        var rows = new List<SceneVariantRow>();
        foreach (var handle in section.Array("variants").OfType<RedHandle>())
        {
            if (file.Object(handle) is not { } v) continue;
            int id = v["id"] is { } i ? Convert.ToInt32(i) : 0;
            int locale = v["localeId"] is { } l ? Convert.ToInt32(l) : -1;
            float length = v.Array("elementInfo").OfType<RedObject>().Sum(e => e.Float("approvedDuration"));
            rows.Add(new($"Variant {id}{(id == defaultId ? " (default)" : "")}", // locale 2 is English (REDkit calls the Baron scene's variant 2 "base EN"); the rest aren't known by name
                locale == 2 ? "EN" : $"locale {locale}",
                $"{v.Array("events").Count}", length > 0 ? $"{length:0.0} s of lines" : ""));
        }
        return rows;
    }
}
