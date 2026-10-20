using System.IO;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;

namespace W3ModWorkbench.Core.Scenes;

/// <summary>What a part of a scene's flow is.</summary>
public enum SceneNodeKind { Input, Section, Cutscene, Video, Condition, Switch, Random, Script, Hub, Output }

/// <summary>What a section's element is: a spoken line, a note for the writers, a pause, a cutscene or video played.</summary>
public enum SceneElementKind { Line, Comment, Pause, Choice, Cutscene, Video, Other }

/// <summary>A way out of a part of the flow: what it's called ("yes", an option's text) and where it goes.</summary>
public sealed class SceneLink
{
    public required string Label { get; init; }

    /// <summary>The line of an option, when the link is a choice's.</summary>
    public uint? StringId { get; init; }

    /// <summary>What picking it does or needs, for a choice's option: "pay 100", "only if …".</summary>
    public string? Detail { get; init; }

    /// <summary>A choice's option the game picks out (the main story's way on), as REDkit marks its emphasised lines.</summary>
    public bool Emphasis { get; internal set; }
    public SceneNode? Target { get; internal set; }
}

/// <summary>Something in a section, in the order it plays.</summary>
public sealed class SceneElement
{
    public required SceneElementKind Kind { get; init; }
    public string? Speaker { get; init; }
    public string? SpeakingTo { get; init; }

    /// <summary>The string a line or comment says, by its ID in the game's strings.</summary>
    public uint? StringId { get; init; }
    public float Duration { get; init; }

    /// <summary>A background line is said in passing, without the camera turning to it.</summary>
    public bool Background { get; init; }
    public string? Detail { get; init; }

    /// <summary>A choice's options, each with the line it shows and where it leads.</summary>
    public List<SceneLink> Options { get; } = [];
}

/// <summary>A part of a scene's flow: where it starts or ends, a section that plays, or a branch.</summary>
public sealed class SceneNode
{
    /// <summary>Its place in the order the flow is read, from 1.</summary>
    public int Number { get; internal set; }
    public required SceneNodeKind Kind { get; init; }
    public required string Title { get; init; }

    /// <summary>A branch's condition, a script's function, a section's writers' comment.</summary>
    public string? Detail { get; init; }

    /// <summary>The cutscene a cutscene section plays.</summary>
    public string? Cutscene { get; init; }
    public List<SceneElement> Elements { get; } = [];

    /// <summary>The ways out, in order; a section's choice is among its elements instead.</summary>
    public List<SceneLink> Next { get; } = [];

    /// <summary>The parts that lead here.</summary>
    public List<SceneNode> From { get; } = [];

    internal int Export { get; init; }

    public IEnumerable<SceneLink> AllLinks => Next.Concat(Elements.SelectMany(e => e.Options));
}

/// <summary>Someone or something a scene uses, by the voice tag its lines name, and the entity it's made from.</summary>
public sealed record SceneActor(string Name, string? Template, bool Prop);

/// <summary>
/// A scene (.w2scene): the flow of a conversation or of the cutscenes a quest plays. Inputs lead through sections
/// (lines, choices, cutscenes, videos) and branches (fact conditions, random picks, scripts) to outputs. Each part
/// points onwards through link elements, which are followed to the part they reach. The lines are string IDs, put
/// into words with the game's strings; the camera and animation events each section keeps after its properties
/// aren't read.
/// </summary>
public sealed class SceneFile
{
    public required Cr2wFile File { get; init; }

    /// <summary>The flow's parts, in the order they're reached from the inputs; any reached from none come last.</summary>
    public required List<SceneNode> Nodes { get; init; }
    public required List<SceneActor> Actors { get; init; }

    /// <summary>The dialog sets (where the actors stand and the cameras around them) it uses.</summary>
    public required List<string> Dialogsets { get; init; }

    public IEnumerable<SceneElement> Lines => Nodes.SelectMany(n => n.Elements).Where(e => e.Kind == SceneElementKind.Line);

    /// <summary>The quest facts its conditions and choices check or set, sorted.</summary>
    public IEnumerable<string> Facts => File.Exports
        .Where(e => e.ClassName.Contains("FactsDB", StringComparison.Ordinal))
        .Select(e => File.Object(e.Index))
        .Select(o => o["factId"] as string ?? o["factID"] as string ?? o["factId1"] as string)
        .OfType<string>().Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>The script functions its script parts run, sorted.</summary>
    public IEnumerable<string> Scripts => File.Exports
        .Where(e => e.ClassName == "CStorySceneScript")
        .Select(e => File.Object(e.Index)["functionName"] as string)
        .OfType<string>().Where(f => f.Length > 0 && f != "None").Distinct().Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>The files it refers to: actors' and props' entities, cutscenes, dialog sets.</summary>
    public IEnumerable<CutsceneLink> Links =>
        File.Imports.Select(i => new CutsceneLink(i.Path, FileKinds.Of(i.Path)?.Name ?? "File"))
            .Concat(Dialogsets.Select(d => new CutsceneLink(d, "Dialog set")))
            .DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase);

    public static bool IsScene(string name) => name.EndsWith(".w2scene", StringComparison.OrdinalIgnoreCase);

    public static SceneFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var scene = file.First("CStoryScene") ?? throw new InvalidDataException("There's no scene in it: it has no CStoryScene.");
        var nodes = new Dictionary<int, SceneNode>();
        var pending = new List<(SceneLink Link, object? Handle)>();

        SceneLink Link(string label, object? handle, uint? stringId = null, string? detail = null)
        {
            var link = new SceneLink { Label = label, StringId = stringId, Detail = detail };
            pending.Add((link, handle));
            return link;
        }

        // the parts are the scene's control parts, and its sections in case one isn't listed among them
        var parts = scene.Array("controlParts").Concat(scene.Array("sections")).OfType<RedHandle>()
            .Select(h => h.Export).OfType<int>().Distinct().ToList();
        foreach (int index in parts)
        {
            if (index >= file.Exports.Count) continue;
            var o = file.Object(index);
            string comment = o["comment"] as string ?? "";
            SceneNode node;
            switch (o.ClassName)
            {
                case "CStorySceneInput":
                    node = new SceneNode { Kind = SceneNodeKind.Input, Title = Named(o["inputName"] as string, "Start"), Detail = Words(comment), Export = index };
                    node.Next.Add(Link("", o["nextLinkElement"]));
                    break;
                case "CStorySceneOutput":
                    node = new SceneNode
                    {
                        Kind = SceneNodeKind.Output, Title = Named(o["name"] as string, "End"), Export = index,
                        Detail = Words(Join(comment, o["endsWithBlackscreen"] is true ? "ends on a black screen" : null, o["questOutput"] is true ? "tells the quest" : null)),
                    };
                    break;
                case "CStorySceneSection" or "CStorySceneCutsceneSection" or "CStorySceneVideoSection":
                    node = Section(file, o, index, Link);
                    break;
                case "CStorySceneFlowCondition":
                    node = new SceneNode { Kind = SceneNodeKind.Condition, Title = Named(comment, "Condition"), Detail = Condition(file, o["questCondition"]), Export = index };
                    node.Next.Add(Link("yes", o["trueLink"]));
                    node.Next.Add(Link("no", o["falseLink"]));
                    break;
                case "CStorySceneFlowSwitch":
                    node = new SceneNode { Kind = SceneNodeKind.Switch, Title = Named(comment, "Switch"), Export = index };
                    foreach (var c in o.Array("cases").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
                        node.Next.Add(Link("when " + (Condition(file, c["whenCondition"]) ?? "?"), c["thenLink"]));
                    node.Next.Add(Link("otherwise", o["defaultLink"]));
                    break;
                case "CStorySceneRandomizer":
                    node = new SceneNode { Kind = SceneNodeKind.Random, Title = Named(comment, "One at random"), Export = index };
                    int n = 0;
                    foreach (var output in o.Array("outputs")) node.Next.Add(Link($"{++n}", output));
                    break;
                case "CStorySceneScript":
                    node = new SceneNode { Kind = SceneNodeKind.Script, Title = Named(comment, "Script"), Detail = o["functionName"] is string fn ? $"runs {fn}" : null, Export = index };
                    node.Next.Add(Link("", o["nextLinkElement"]));
                    foreach (var extra in o.Array("links")) node.Next.Add(Link("", extra));
                    break;
                default:
                    // link hubs and anything else that passes the flow on
                    node = new SceneNode { Kind = SceneNodeKind.Hub, Title = Named(comment, CutsceneFile.Humanize(o.ClassName, "CStoryScene")), Export = index };
                    node.Next.Add(Link("", o["nextLinkElement"]));
                    break;
            }
            nodes[index] = node;
        }

        // links go through link elements to the part they reach
        foreach (var (link, handle) in pending)
        {
            link.Target = Follow(file, handle, nodes);
        }
        foreach (var node in nodes.Values)
        {
            node.Next.RemoveAll(l => l.Target is null && l.Label.Length == 0);
            foreach (var link in node.AllLinks)
                if (link.Target is { } target && !target.From.Contains(node)) target.From.Add(node);
        }

        var ordered = Order(nodes.Values);
        var actors = scene.Array("sceneTemplates").OfType<RedHandle>().Select(file.Object).OfType<RedObject>()
            .Select(a => new SceneActor(a["id"] as string ?? "", a["entityTemplate"] is RedHandle t ? file.Import(t)?.Path : null, false))
            .Concat(scene.Array("sceneProps").OfType<RedHandle>().Select(file.Object).OfType<RedObject>()
                .Select(p => new SceneActor(p["id"] as string ?? "", p["entityTemplate"] is RedHandle t ? file.Import(t)?.Path : null, true)))
            .Where(a => a.Name.Length > 0).ToList();
        var dialogsets = scene.Array("dialogsetInstances").OfType<RedHandle>().Select(file.Object).OfType<RedObject>()
            .Select(d => d["path"] as string).OfType<string>().Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new SceneFile { File = file, Nodes = ordered, Actors = actors, Dialogsets = dialogsets };
    }

    /// <summary>A section: its elements in the order they play, its choice, and where it goes after.</summary>
    private static SceneNode Section(Cr2wFile file, RedObject o, int index, Func<string, object?, uint?, string?, SceneLink> link)
    {
        var kind = o.ClassName switch { "CStorySceneCutsceneSection" => SceneNodeKind.Cutscene, "CStorySceneVideoSection" => SceneNodeKind.Video, _ => SceneNodeKind.Section };
        string? cutscene = o["cutscene"] is RedHandle c ? file.Import(c)?.Path : null;
        var node = new SceneNode
        {
            Kind = kind,
            Title = Named((o["sectionName"] as string)?.Trim(), kind == SceneNodeKind.Cutscene ? "Cutscene" : "Section"),
            Detail = Words(Join(o["comment"] as string, o["videoFileName"] is string video && video.Length > 0 ? "plays " + video : null,
                o["isGameplay"] is true ? "in gameplay" : null, o["dialogsetChangeTo"] is string set && set.Length > 0 && set != "None" ? "dialog set " + set : null)),
            Cutscene = cutscene,
            Export = index,
        };
        var elements = o.Array("sceneElements").OfType<RedHandle>().Select(h => h.Export).OfType<int>().ToList();
        // a section's choice is in its own property, and sometimes among its elements too
        if (o["choice"] is RedHandle { Export: int own } && !elements.Contains(own)) elements.Add(own);
        foreach (int e in elements)
        {
            if (e >= file.Exports.Count) continue;
            var el = file.Object(e);
            switch (el.ClassName)
            {
                case "CStorySceneLine":
                    node.Elements.Add(new SceneElement
                    {
                        Kind = SceneElementKind.Line, Speaker = NameOrNull(el["voicetag"]), SpeakingTo = NameOrNull(el["speakingTo"]),
                        StringId = el["dialogLine"] as uint?, Duration = el.Float("approvedDuration", el.Float("duration")), Background = el["isBackgroundLine"] is true,
                        Detail = el["isSilent"] is true ? "silent" : null,
                    });
                    break;
                case "CStorySceneComment":
                    node.Elements.Add(new SceneElement { Kind = SceneElementKind.Comment, StringId = el["commentText"] as uint? });
                    break;
                case "CStoryScenePauseElement":
                    node.Elements.Add(new SceneElement { Kind = SceneElementKind.Pause, Duration = el.Float("approvedDuration", el.Float("duration")) });
                    break;
                case "CStorySceneCutscenePlayer":
                    node.Elements.Add(new SceneElement { Kind = SceneElementKind.Cutscene, Duration = el.Float("approvedDuration"), Detail = cutscene });
                    break;
                case "CStorySceneVideoElement":
                    node.Elements.Add(new SceneElement { Kind = SceneElementKind.Video, Duration = el.Float("approvedDuration"), Detail = Words(el["description"] as string) });
                    break;
                case "CStorySceneChoice":
                    var choice = new SceneElement
                    {
                        Kind = SceneElementKind.Choice, Duration = el.Float("timeLimit"),
                        Detail = Words(Join(el["isLooped"] is true ? "asked again until left" : null, el.Float("timeLimit") > 0 ? $"{el.Float("timeLimit"):0.#} s to choose" : null)),
                    };
                    foreach (var line in el.Array("choiceLines").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
                    {
                        string? needs = line["questCondition"] is RedHandle q ? Condition(file, q) : null;
                        string? does = line["action"] is RedHandle a && file.Object(a) is { } action ? Action(action) : null;
                        var option = link("", line["nextLinkElement"], line["choiceLine"] as uint?,
                            Words(Join(does, needs is null ? null : "only if " + needs, line["singleUseChoice"] is true ? "once" : null,
                                line["isDefaultChoice"] is true ? "picked when time runs out" : null, line["hidden"] is true ? "hidden" : null)));
                        option.Emphasis = line["emphasisLine"] is true;
                        choice.Options.Add(option);
                    }
                    node.Elements.Add(choice);
                    break;
                case "CStorySceneQuestChoiceLine":
                    // a line the quest adds to a choice from outside the scene
                    node.Elements.Add(new SceneElement { Kind = SceneElementKind.Comment, StringId = el["commentText"] as uint?, Detail = "an option the quest adds" });
                    break;
                default:
                    node.Elements.Add(new SceneElement { Kind = SceneElementKind.Other, Detail = CutsceneFile.Humanize(el.ClassName, "CStoryScene"), Duration = el.Float("approvedDuration") });
                    break;
            }
        }
        node.Next.Add(link("", o["nextLinkElement"], null, null));
        return node;
    }

    /// <summary>The part a link reaches, through any link elements in between. Null for a link that goes nowhere.</summary>
    private static SceneNode? Follow(Cr2wFile file, object? handle, Dictionary<int, SceneNode> nodes)
    {
        for (int hops = 0; hops < 64 && handle is RedHandle { Export: int index } && index < file.Exports.Count; hops++)
        {
            if (nodes.TryGetValue(index, out var node)) return node;
            handle = file.Object(index)["nextLinkElement"];
        }
        return null;
    }

    /// <summary>The parts from the inputs onwards, each numbered; those no input reaches come after.</summary>
    private static List<SceneNode> Order(IEnumerable<SceneNode> all)
    {
        var list = all.OrderBy(n => n.Export).ToList();
        var ordered = new List<SceneNode>();
        var seen = new HashSet<SceneNode>();
        var queue = new Queue<SceneNode>(list.Where(n => n.Kind == SceneNodeKind.Input));
        foreach (var start in queue) seen.Add(start);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            ordered.Add(node);
            foreach (var link in node.AllLinks)
                if (link.Target is { } next && seen.Add(next)) queue.Enqueue(next);
        }
        ordered.AddRange(list.Where(n => !seen.Contains(n)));
        for (int i = 0; i < ordered.Count; i++) ordered[i].Number = i + 1;
        return ordered;
    }

    /// <summary>A quest condition in words: facts compared, conditions joined, or the kind of check and what it names.</summary>
    public static string? Condition(Cr2wFile file, object? handle)
    {
        if (handle is not RedHandle h || file.Object(h) is not { } c) return null;
        switch (c.ClassName)
        {
            case "CQuestFactsDBCondition" or "CQuestFactsDBForbiddenCondition":
                string fact = c["factId"] as string ?? "?";
                string query = (c["queryFact"] as RedEnum)?.ToString() ?? "QF_Sum";
                string compare = Compare((c["compareFunc"] as RedEnum)?.ToString());
                string value = $"{c.Integer("value")}";
                string what = query == "QF_DoesExist" ? $"fact {fact} exists" : $"fact {fact} {compare} {value}";
                if (query == "QF_DoesExist" && c.Integer("value") == 0 && compare is "=" or "≤") what = $"fact {fact} doesn't exist";
                return c.ClassName.Contains("Forbidden") ? "not " + what : what;
            case "CQuestLogicOperationCondition":
                string op = (c["logicOperation"] as RedEnum)?.ToString() switch { "LO_Or" => " or ", "LO_Xor" => " xor ", "LO_Nand" => " nand ", "LO_Nor" => " nor ", _ => " and " };
                var parts = c.Array("conditions").Select(x => Condition(file, x)).OfType<string>().ToList();
                return parts.Count == 0 ? null : "(" + string.Join(op, parts) + ")";
            default:
                // the kind of check and the values it was given, which say most of what it does
                var said = c.Properties.Where(p => p.Value is string or bool or int or uint or float or RedEnum && p.Name != "isFulfilled")
                    .Select(p => $"{p.Name} {Value(p.Value)}").Take(4);
                return Join(CutsceneFile.Humanize(c.ClassName.Replace("W3QuestCond_", "").Replace("CQuest", "").Replace("CQC", ""), "Condition"), string.Join(", ", said));
        }
    }

    private static string Value(object? v) => v switch { RedEnum e => e.ToString(), bool b => b ? "yes" : "no", _ => v?.ToString() ?? "" };

    private static string Compare(string? func) => func switch
    {
        "CF_NotEqual" => "≠", "CF_Less" => "<", "CF_LessEqual" => "≤", "CF_Greater" => ">", "CF_GreaterEqual" => "≥", _ => "=",
    };

    /// <summary>What picking an option does: pay, leave, open a shop.</summary>
    private static string Action(RedObject action)
    {
        string name = action.ClassName.Replace("StorySceneChoiceAction", "").Replace("SceneChoiceAction", "").Replace("ChoiceAction", "");
        string words = CutsceneFile.Humanize(name, "C").ToLowerInvariant();
        return action["money"] is int money && money > 0 ? $"{words}, {money} crowns" : words;
    }

    private static string? NameOrNull(object? value) => value is string s && s.Length > 0 && s != "None" ? s : null;

    private static string Named(string? name, string otherwise) => string.IsNullOrWhiteSpace(name) || name == "None" ? otherwise : name;

    private static string? Words(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Join(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
}
