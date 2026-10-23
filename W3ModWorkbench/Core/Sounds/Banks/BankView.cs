using System.Globalization;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>One line of a bank's tree: a group (events, sounds, buses…) or an object, with what's under it.</summary>
public sealed record BankTreeNode(string Title, string Kind, BankObject? Object, List<BankTreeNode> Children)
{
    /// <summary>What a line that isn't an object (the bank, a group) shows when it's picked.</summary>
    public List<BankSummaryRow>? Rows { get; init; }

    /// <summary>The values a line that isn't an object shows (a section of the bank), when it has them.</summary>
    public BankField? Fields { get; init; }
}

/// <summary>A setting of an object, put plainly: what it is and its value.</summary>
public sealed record BankSummaryRow(string Label, string Value);

/// <summary>
/// A bank's objects put plainly: a tree of its events, what each one's actions do and to what, down through containers
/// to the sounds, then every object by kind; each object's settings in words (where its sound is, its volume and other
/// properties, effects, states, game parameters, children, playlist…); and the whole bank as text. IDs are named
/// where a name is known.
/// </summary>
public sealed class BankView(BankObjects bank, IReadOnlyDictionary<uint, string> names)
{
    public BankObjects Bank { get; } = bank;

    public string? NameOf(long id) =>
        id is <= 0 or > uint.MaxValue ? null : names.GetValueOrDefault((uint)id) ?? Bank.BankFileNames.GetValueOrDefault((uint)id);

    /// <summary>How many of the bank's hashed IDs (events, buses, states…) have names.</summary>
    public (int Named, int All) NameCount()
    {
        var all = new HashSet<long>();
        foreach (var o in Bank.Objects)
            foreach (var f in Walk(o.Fields))
                if (f.Hash != BankHash.None && f.Long > 0) all.Add(f.Long);
        return (all.Count(id => NameOf(id) is not null), all.Count);
    }

    private static IEnumerable<BankField> Walk(BankField f)
    {
        yield return f;
        foreach (var c in f.Children)
            foreach (var d in Walk(c)) yield return d;
    }

    private static long Id(BankField? f) => f?.Long ?? 0;

    // ---------------------------------------------------------------- kinds and titles

    public string KindOf(BankObject o) => o.ClassName switch
    {
        "CAkSound" => L.T("BankView.Kind.Sound"),
        "CAkRanSeqCntr" => o.Fields.Find("container")?.Find("mode")?.Long == 1 ? L.T("BankView.Kind.SequenceContainer") : L.T("BankView.Kind.RandomContainer"),
        "CAkSwitchCntr" => L.T("BankView.Kind.SwitchContainer"),
        "CAkLayerCntr" => L.T("BankView.Kind.BlendContainer"),
        "CAkActorMixer" => L.T("BankView.Kind.ActorMixer"),
        "CAkBus" => L.T("BankView.Kind.Bus"),
        "CAkAuxBus" => L.T("BankView.Kind.AuxBus"),
        "CAkFeedbackBus" => L.T("BankView.Kind.FeedbackBus"),
        "CAkFeedbackNode" => L.T("BankView.Kind.FeedbackNode"),
        "CAkMusicSegment" => L.T("BankView.Kind.MusicSegment"),
        "CAkMusicTrack" => L.T("BankView.Kind.MusicTrack"),
        "CAkMusicSwitchCntr" => L.T("BankView.Kind.MusicSwitch"),
        "CAkMusicRanSeqCntr" => L.T("BankView.Kind.MusicPlaylist"),
        "CAkEvent" => L.T("BankView.Kind.Event"),
        "CAkDialogueEvent" => L.T("BankView.Kind.DialogueEvent"),
        "CAkState" => L.T("BankView.Kind.State"),
        "CAkFxShareSet" => L.T("BankView.Kind.EffectShareSet"),
        "CAkFxCustom" => L.T("BankView.Kind.Effect"),
        "CAkAttenuation" => L.T("BankView.Kind.Attenuation"),
        "CAkLFOModulator" => L.T("BankView.Kind.Lfo"),
        "CAkEnvelopeModulator" => L.T("BankView.Kind.Envelope"),
        "CAkTimeModulator" => L.T("BankView.Kind.TimeModulator"),
        "CAkAudioDevice" => L.T("BankView.Kind.AudioDevice"),
        "CAkAction" => L.T("BankView.Kind.Action", ActionName(o)),
        _ => o.TypeName,
    };

    private static string ActionName(BankObject o) => o.Fields.Find("type")?.Meaning ?? "";

    /// <summary>An object's name when it has one, else what it is and its ID; an effect by its plugin.</summary>
    public string Title(BankObject o)
    {
        if (NameOf(o.Id) is { } name) return name;
        // effects are known by their plugin; several of one plugin are told apart by their IDs
        if (o.ClassName is "CAkFxCustom" or "CAkFxShareSet" && o.Fields.Find("effect")?.Find("plugin")?.Meaning is { } plugin) return $"{plugin} {o.Id}";
        return $"{KindOf(o)} {o.Id}";
    }

    /// <summary>A line of the tree: the object's name (or an effect's plugin), else its ID; its kind shows beside it.</summary>
    public string TreeTitle(BankObject o)
    {
        string title = Title(o);
        return title == $"{KindOf(o)} {o.Id}" ? o.Id.ToString(CultureInfo.InvariantCulture) : title;
    }

    /// <summary>What an ID is: its name, the object of the bank it is, or the number.</summary>
    public string Describe(long id)
    {
        if (id is 0 or -1) return L.T("Common.None");
        if (Bank[(uint)id] is { } o) return NameOf(id) is { } n ? $"{n} ({KindOf(o)})" : Title(o);
        return NameOf(id) is { } name ? $"{name} ({id})" : id.ToString(CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- the tree

    /// <summary>The events, each down to what it plays, then every object by kind.</summary>
    public List<BankTreeNode> Tree()
    {
        var groups = new List<BankTreeNode>();
        var events = Bank.Objects.Where(o => o.ClassName is "CAkEvent" or "CAkDialogueEvent").ToList();
        if (events.Count > 0)
            // named events first, then those known only by their IDs
            groups.Add(Group(L.T("BankView.Group.Events"), events.OrderBy(e => NameOf(e.Id) is null).ThenBy(Title, StringComparer.OrdinalIgnoreCase)
                .Select(e => Node(e, [], 0)).ToList()));

        (string Title, Func<BankObject, bool> Is)[] kinds =
        [
            (L.T("BankView.Group.Sounds"), o => o.ClassName is "CAkSound" or "CAkRanSeqCntr" or "CAkSwitchCntr" or "CAkLayerCntr" or "CAkActorMixer" or "CAkFeedbackNode"),
            (L.T("BankView.Group.Music"), o => o.ClassName.StartsWith("CAkMusic", StringComparison.Ordinal)),
            (L.T("BankView.Group.Actions"), o => o.ClassName == "CAkAction"),
            (L.T("BankView.Group.Buses"), o => o.ClassName is "CAkBus" or "CAkAuxBus" or "CAkFeedbackBus"),
            (L.T("BankView.Group.States"), o => o.ClassName == "CAkState"),
            (L.T("BankView.Group.Effects"), o => o.ClassName is "CAkFxCustom" or "CAkFxShareSet" or "CAkAudioDevice"),
            (L.T("BankView.Group.Attenuations"), o => o.ClassName == "CAkAttenuation"),
            (L.T("BankView.Group.Modulators"), o => o.ClassName is "CAkLFOModulator" or "CAkEnvelopeModulator" or "CAkTimeModulator"),
        ];
        var placed = new HashSet<BankObject>(events);
        foreach (var (title, isKind) in kinds)
        {
            var of = Bank.Objects.Where(o => !placed.Contains(o) && isKind(o)).ToList();
            placed.UnionWith(of);
            if (of.Count > 0) groups.Add(Group(title, of.Select(o => new BankTreeNode(TreeTitle(o), KindOf(o), o, [])).ToList()));
        }
        var rest = Bank.Objects.Where(o => !placed.Contains(o)).ToList();
        if (rest.Count > 0) groups.Add(Group(L.T("BankView.Group.Other"), rest.Select(o => new BankTreeNode(TreeTitle(o), KindOf(o), o, [])).ToList()));
        if (ProjectSettings() is { } project) groups.Add(project);
        return groups;
    }

    private BankField? Section(string tag) => Bank.Sections.FirstOrDefault(s => s.Tag == tag).Fields;

    /// <summary>All the bank's sections but its objects, as one group of values (for the bank's own line).</summary>
    public BankField SectionsField()
    {
        var all = new BankField(L.T("BankView.Sections"), "node", null, 0, 0);
        foreach (var (_, field) in Bank.Sections) all.Children.Add(field);
        return all;
    }

    /// <summary>
    /// An Init.bnk's global settings: state groups and their transitions, switch groups and the game parameters that
    /// drive them, every game parameter's starting value, the plugins the project uses, obstruction and occlusion.
    /// Null for a bank without them.
    /// </summary>
    private BankTreeNode? ProjectSettings()
    {
        var settings = Section("STMG");
        var plugins = Section("INIT");
        var curves = Section("ENVS");
        if (settings is null && plugins is null && curves is null) return null;
        var lines = new List<BankTreeNode>();
        var rows = new List<BankSummaryRow>();
        if (settings is not null)
        {
            rows.Add(new(L.T("BankView.VolumeThreshold"), settings.Find("volumeThreshold")?.Text ?? ""));
            rows.Add(new(L.T("BankView.MaxVoices"), $"{settings.Find("maxVoices")?.Long}"));
            if (settings.Find("maxVirtualVoices") is { } virtualVoices) rows.Add(new(L.T("BankView.MaxVirtualVoices"), $"{virtualVoices.Long}"));

            var groups = (settings.Find("stateGroups")?.Children ?? []).Select(g =>
            {
                var transitions = (g.Find("transitions")?.Children ?? []).Select(t =>
                    $"{Describe(Id(t.Find("from")))} → {Describe(Id(t.Find("to")))}: {t.Find("time")?.Text}");
                return new BankTreeNode(Describe(Id(g.Find("group"))), L.T("BankView.Kind.StateGroup"), null, [])
                {
                    Rows = [new(L.T("BankView.DefaultTransition"), g.Find("defaultTransition")?.Text ?? ""), new(L.T("BankView.Transitions"), string.Join("\n", transitions))],
                    Fields = g,
                };
            }).ToList();
            if (groups.Count > 0)
                lines.Add(new(L.T("BankView.StateGroups"), L.T("Count.Object", groups.Count), null, groups)
                {
                    Rows = (settings.Find("stateGroups")?.Children ?? []).Select(g => new BankSummaryRow(Describe(Id(g.Find("group"))),
                        $"{g.Find("defaultTransition")?.Text}  ·  {L.T("BankView.TransitionCount", g.Find("transitionCount")?.Long ?? 0)}")).ToList(),
                    Fields = settings.Find("stateGroups"),
                });

            var switches = (settings.Find("switchGroups")?.Children ?? []).Select(s =>
                new BankTreeNode(Describe(Id(s.Find("group"))), L.T("BankView.Kind.SwitchGroup"), null, [])
                {
                    Rows = [new(L.T("BankView.GameParameter"), Describe(Id(s.Find("parameter")))), new(L.T("BankView.Curves"), PointsText(s))],
                    Fields = s,
                }).ToList();
            if (switches.Count > 0)
                lines.Add(new(L.T("BankView.SwitchGroups"), L.T("Count.Object", switches.Count), null, switches)
                {
                    Rows = (settings.Find("switchGroups")?.Children ?? []).Select(s => new BankSummaryRow(Describe(Id(s.Find("group"))), Describe(Id(s.Find("parameter"))))).ToList(),
                    Fields = settings.Find("switchGroups"),
                });

            var parameters = (settings.Find("parameters")?.Children ?? []).Select(p => new BankSummaryRow(Describe(Id(p.Find("parameter"))),
                L.T("BankView.ParameterDefault", p.Find("default")?.Text ?? "")
                + (p.Find("ramping") is { Long: > 0 } ramp ? $"  ·  {ramp.Meaning} {p.Find("rampUp")?.Text}/{p.Find("rampDown")?.Text}" : "")
                + (p.Find("builtIn") is { Long: > 0 } builtIn ? $"  ·  {builtIn.Meaning}" : ""))).ToList();
            if (parameters.Count > 0)
                lines.Add(new(L.T("BankView.GameParameters"), L.T("Count.Object", parameters.Count), null, []) { Rows = parameters, Fields = settings.Find("parameters") });
        }
        if (plugins is not null)
        {
            var list = (plugins.Find("plugins")?.Children ?? []).Select(p => new BankSummaryRow(p.Find("plugin")?.Meaning ?? "", p.Find("library")?.Text ?? "")).ToList();
            lines.Add(new(L.T("BankView.Plugins"), L.T("Count.Object", list.Count), null, []) { Rows = list, Fields = plugins });
        }
        if (curves is not null)
        {
            var list = curves.Children.Select(c => new BankSummaryRow(c.Name,
                (c.Find("on")?.Long == 1 ? "" : L.T("BankView.Off") + "  ") + PointsText(c))).ToList();
            lines.Add(new(L.T("BankView.ObstructionOcclusion"), "", null, []) { Rows = list, Fields = curves });
        }
        if (Section("PLAT")?.Find("platform") is { } platform) rows.Add(new(L.T("BankView.Platform"), platform.Text));
        return new BankTreeNode(L.T("BankView.Group.Project"), "", null, lines) { Rows = rows, Fields = settings };
    }

    private BankTreeNode Group(string title, List<BankTreeNode> children) =>
        new(title, L.T("Count.Object", children.Count), null, children)
        {
            Rows = children.Where(c => c.Object is not null).GroupBy(c => c.Kind).OrderByDescending(g => g.Count())
                .Select(g => new BankSummaryRow(g.Key, $"{g.Count():N0}")).ToList(),
        };

    /// <summary>An object and what it leads to: an event's actions, an action's target, a container's children.</summary>
    private BankTreeNode Node(BankObject o, HashSet<uint> path, int depth)
    {
        var children = new List<BankTreeNode>();
        if (depth < 10 && path.Add(o.Id))
        {
            foreach (long next in Leads(o))
            {
                if (Bank[(uint)next] is { } child) children.Add(Node(child, path, depth + 1));
                else if (next > 0) children.Add(new BankTreeNode(Describe(next), L.T("BankView.NotInBank"), null, []));
            }
            path.Remove(o.Id);
        }
        return new BankTreeNode(TreeTitle(o), KindOf(o), o, children);
    }

    /// <summary>The IDs an object leads to: an event's actions, an action's target, a container's children and what it plays.</summary>
    private static IEnumerable<long> Leads(BankObject o)
    {
        var f = o.Fields;
        return o.ClassName switch
        {
            "CAkEvent" => f.Find("actions")?.Children.Select(a => a.Long) ?? [],
            "CAkAction" => f.Find("targetFlags")?.Long == 1 ? [] : [Id(f.Find("target"))],
            "CAkDialogueEvent" => f.FindAll("plays").Select(p => p.Long).Distinct(),
            _ => (f.Find("children")?.Children.Where(c => c.Name == "child").Select(c => c.Long) ?? [])
                .Concat(f.FindAll("segment").Select(s => s.Long))
                .Where(id => id > 0).Distinct(),
        };
    }

    // ---------------------------------------------------------------- an object's settings

    public List<BankSummaryRow> Summary(BankObject o)
    {
        var rows = new List<BankSummaryRow>
        {
            new(L.T("BankView.Kind"), $"{KindOf(o)}  ({o.ClassName})"),
            new(L.T("BankView.Id"), NameOf(o.Id) is { } name ? $"{o.Id}  ·  {name}" : $"{o.Id}"),
        };
        var f = o.Fields;
        switch (o.ClassName)
        {
            case "CAkEvent":
                var actions = (f.Find("actions")?.Children ?? []).Select(a =>
                    Bank[(uint)a.Long] is { } act ? $"{ActionName(act)} → {Describe(Id(act.Fields.Find("target")))}" : Describe(a.Long));
                rows.Add(new(L.T("BankView.Actions"), string.Join("\n", actions)));
                break;
            case "CAkAction":
                ActionRows(f, rows);
                break;
            case "CAkState":
                rows.Add(new(L.T("BankView.Properties"), StateValues(f.Find("values")) is { Length: > 0 } values ? values : L.T("Common.None")));
                break;
            case "CAkFxCustom" or "CAkFxShareSet" or "CAkAudioDevice":
                EffectRows(f, rows);
                break;
            case "CAkAttenuation":
                AttenuationRows(f, rows);
                break;
            case "CAkLFOModulator" or "CAkEnvelopeModulator" or "CAkTimeModulator":
                rows.Add(new(L.T("BankView.Properties"), Props(f.Find("modulator")?.Find("properties"), modulator: true) is { Length: > 0 } p ? p : L.T("Common.None")));
                CurveRows(f.Find("modulator")?.Find("curves"), rows);
                break;
            case "CAkDialogueEvent":
                rows.Add(new(L.T("BankView.Plays"), string.Join("\n", Leads(o).Select(Describe))));
                break;
            case "CAkBus" or "CAkAuxBus" or "CAkFeedbackBus":
                BusRows(f, rows);
                break;
            default:
                NodeRows(o, rows);
                break;
        }
        if (!o.ReadWhole) rows.Add(new(L.T("BankView.ReadProblem"), o.Fields.Error ?? L.T("BankView.BytesUnread", o.Fields.Find("unread")?.Size ?? 0)));
        return rows;
    }

    private void ActionRows(BankField f, List<BankSummaryRow> rows)
    {
        rows.Add(new(L.T("BankView.Action"), f.Find("type")?.Meaning ?? ""));
        bool bus = f.Find("targetFlags")?.Long == 1;
        if (Id(f.Find("target")) is not 0 and var target) rows.Add(new(L.T("BankView.Target"), (bus ? L.T("BankView.Bus") + " " : "") + Describe(target)));
        if (f.Find("fade")?.Find("curve")?.Meaning is { } curve) rows.Add(new(L.T("BankView.FadeCurve"), curve));
        if (Props(f.Find("properties")) is { Length: > 0 } p) rows.Add(new(L.T("BankView.Properties"), p));
        if (f.Find("type")?.Long >> 8 == 0x12)
            rows.Add(new(L.T("BankView.StateGroup"), $"{Describe(Id(f.Find("group")))} → {Describe(Id(f.Find("state")))}"));
        if (f.Find("type")?.Long >> 8 == 0x19)
            rows.Add(new(L.T("BankView.SwitchGroup"), $"{Describe(Id(f.Find("group")))} → {Describe(Id(f.Find("switch")))}"));
        if (f.Find("value") is { } value)
            rows.Add(new(L.T("BankView.Value"), $"{value.Find("value")?.Text} ({value.Find("min")?.Text} … {value.Find("max")?.Text})  ·  {value.Find("meaning")?.Meaning}"));
        if (f.Find("bank") is { Long: > 0 } file) rows.Add(new(L.T("BankView.Bank"), Describe(file.Long)));
    }

    private void EffectRows(BankField f, List<BankSummaryRow> rows)
    {
        var e = f.Find("effect");
        if (e?.Find("plugin") is { } plugin) rows.Add(new(L.T("BankView.Plugin"), plugin.Meaning ?? plugin.Text));
        if (e?.Find("settings") is { } settings)
            rows.Add(new(L.T("BankView.Settings"), settings.IsGroup
                ? string.Join("\n", settings.Children.Select(s => $"{s.Name}  {(NameOf(s.Long) is { } n && s.Type == "tid" ? n : s.Text)}"))
                : L.T("BankView.SettingsBytes", settings.Size, settings.Text)));
        CurveRows(e?.Find("curves"), rows);
    }

    private void AttenuationRows(BankField f, List<BankSummaryRow> rows)
    {
        if (f.Find("coneSettings") is { } cone)
            rows.Add(new(L.T("BankView.Cone"), string.Join("  ·  ", cone.Children.Select(c => $"{c.Name} {c.Text}"))));
        var curves = f.Find("distanceCurves")?.Children ?? [];
        rows.Add(new(L.T("BankView.Curves"), string.Join("\n", curves.Select((c, i) =>
            L.T("BankView.Curve", i, c.Find("scaling")?.Meaning ?? "", PointsText(c))))));
        CurveRows(f.Find("curves"), rows);
    }

    private void BusRows(BankField f, List<BankSummaryRow> rows)
    {
        if (f.Find("parentBus") is { Long: > 0 } parent) rows.Add(new(L.T("BankView.OutputBus"), Describe(parent.Long)));
        if (Props(f.Find("bus")?.Find("properties")) is { Length: > 0 } p) rows.Add(new(L.T("BankView.Properties"), p));
        var effects = EffectsIn(f.Find("effects"));
        if (effects.Count > 0) rows.Add(new(L.T("BankView.Effects"), string.Join("\n", effects)));
        if (f.Find("bus")?.Find("maxInstances") is { Long: > 0 } max) rows.Add(new(L.T("BankView.MaxInstances"), $"{max.Long}"));
        var ducks = (f.Find("ducks")?.Children ?? []).Select(d => $"{Describe(Id(d.Find("bus")))}  {d.Find("volume")?.Text} dB");
        if (ducks.Any()) rows.Add(new(L.T("BankView.Ducking"), string.Join("\n", ducks)));
        StateRows(f.Find("states"), rows);
        CurveRows(f.Find("curves"), rows);
    }

    private void NodeRows(BankObject o, List<BankSummaryRow> rows)
    {
        var f = o.Fields;
        var s = f.Find("settings");
        if (s?.Find("parent") is { Long: > 0 } parent) rows.Add(new(L.T("BankView.Parent"), Describe(parent.Long)));
        if (s?.Find("outputBus") is { Long: > 0 } busId) rows.Add(new(L.T("BankView.OutputBus"), Describe(busId.Long)));
        foreach (var id in f.FindAll("sourceId"))
        {
            var source = id.Parent!;
            if (source.Find("storage") is not { } storage) continue;
            string codec = source.Find("plugin")?.Meaning ?? "";
            rows.Add(new(L.T("BankView.Source"), $"{id.Long}  ·  {codec}  ·  {StorageText(storage)}"
                + (source.Find("size") is { Long: > 0 } size ? $"  ·  {Format.Bytes(size.Long)}" : "")));
        }
        if (Props(s?.Find("properties")) is { Length: > 0 } props) rows.Add(new(L.T("BankView.Properties"), props));
        if (Ranges(s?.Find("randomised")) is { Length: > 0 } ranges) rows.Add(new(L.T("BankView.Randomised"), ranges));
        var effects = EffectsIn(s?.Find("effects"));
        if (effects.Count > 0) rows.Add(new(L.T("BankView.Effects"), string.Join("\n", effects)));

        if (f.Find("container") is { } c)
        {
            bool random = c.Find("mode")?.Long != 1;
            rows.Add(new(L.T("BankView.Mode"), random ? $"{c.Find("mode")?.Meaning}  ·  {c.Find("randomMode")?.Meaning}" : $"{c.Find("mode")?.Meaning}"));
            if (c.Find("transition") is { Long: > 0 } transition)
                rows.Add(new(L.T("BankView.Transition"), $"{transition.Meaning}  ·  {c.Find("transitionTime")?.Text} ms"));
            long loops = c.Find("loopCount")?.Long ?? 1;
            rows.Add(new(L.T("BankView.Loop"), loops switch { 0 => L.T("BankView.LoopForever"), 1 => L.T("BankView.LoopOnce"), _ => L.T("BankView.LoopTimes", loops) }));
            rows.Add(new(L.T("BankView.AvoidRepeat"), $"{c.Find("avoidRepeats")?.Long}"));
            var items = (f.Find("playlist")?.FindAll("item") ?? []).Select(i => $"{Describe(Id(i.Find("plays")))}  ·  {L.T("BankView.Weight", (i.Find("weight")?.Long ?? 0) / 1000.0)}");
            rows.Add(new(L.T("BankView.Playlist"), string.Join("\n", items)));
        }
        if (f.Find("switch") is { } sw && o.ClassName == "CAkSwitchCntr")
        {
            rows.Add(new(L.T("BankView.SwitchGroup"), $"{Describe(Id(sw.Find("group")))}  ({sw.Find("groupKind")?.Meaning})"));
            rows.Add(new(L.T("BankView.DefaultSwitch"), Describe(Id(sw.Find("default")))));
            var assigned = (f.Find("switches")?.Children ?? []).Select(p =>
                $"{Describe(Id(p.Find("value")))} → {string.Join(", ", p.Children.Where(x => x.Name == "plays").Select(n => Describe(n.Long)))}");
            rows.Add(new(L.T("BankView.Assigned"), string.Join("\n", assigned)));
        }
        if (o.ClassName == "CAkMusicSegment")
        {
            if (f.Find("duration") is { } duration) rows.Add(new(L.T("BankView.Duration"), $"{duration.Number / 1000:0.000} s"));
            var markers = (f.Find("markers")?.Children ?? []).Select(m => $"{(m.Find("position")?.Number ?? 0) / 1000:0.000} s  {m.Find("name")?.Text}");
            rows.Add(new(L.T("BankView.Markers"), string.Join("\n", markers)));
        }
        var children = f.Find("children")?.Children.Where(x => x.Name == "child").Select(x => Describe(x.Long)).ToList() ?? [];
        if (children.Count > 0) rows.Add(new(L.T("BankView.Children"), string.Join("\n", children)));

        if (s?.Find("positioning") is { } pos && (pos.Find("flags")?.Long & 1) == 1)
        {
            bool is3d = pos.Find("is3d")?.Long == 1 || pos.Find("flags3d") is not null;
            string text = is3d ? L.T("BankView.Positioning3d") : L.T("BankView.Positioning2d");
            if (pos.Find("attenuation") is { Long: > 0 } att) text += "  ·  " + L.T("BankView.AttenuationIs", Describe(att.Long));
            rows.Add(new(L.T("BankView.Positioning"), text));
        }
        else if (PropertyId(s?.Find("properties"), "Attenuation") is { } attProp)
            rows.Add(new(L.T("BankView.Positioning"), L.T("BankView.AttenuationIs", Describe(attProp))));

        StateRows(s?.Find("states"), rows);
        CurveRows(s?.Find("curves"), rows);
        if (s?.Find("limits") is { } limits)
        {
            if (limits.Find("maxInstances") is { Long: > 0 } max) rows.Add(new(L.T("BankView.MaxInstances"), $"{max.Long}"));
            rows.Add(new(L.T("BankView.VirtualVoice"), $"{limits.Find("belowThreshold")?.Meaning}  ·  {limits.Find("virtualResume")?.Meaning}"));
        }
    }

    /// <summary>The effects in an object's slots, in slot order.</summary>
    private List<string> EffectsIn(BankField? effects) =>
        (effects?.Find("slots")?.Children ?? []).Select(slot => Describe(Id(slot.Find("effect")))).ToList();

    private string StorageText(BankField storage) => storage.Long == 0 ? L.T("BankView.KeptInBank")
        : storage.Meaning == "Streamed" ? L.T("BankView.Streamed") : L.T("BankView.Prefetched");

    private void StateRows(BankField? states, List<BankSummaryRow> rows)
    {
        foreach (var group in states?.Find("groups")?.Children ?? [])
        {
            var list = (group.Find("states")?.Children ?? []).Select(s =>
            {
                string name = Describe(Id(s.Find("state")));
                // up to 145 a state points at a State object with its values; later it keeps them itself
                string values = s.Find("stateObject") is { } instance && Bank[(uint)instance.Long] is { } state
                    ? StateValues(state.Fields.Find("values")) : StateValues(s.Find("values"));
                return $"{name}: {(values.Length > 0 ? values.Replace("\n", ", ") : L.T("Common.None"))}";
            });
            rows.Add(new(L.T("BankView.States", Describe(Id(group.Find("group")))), string.Join("\n", list)));
        }
    }

    private void CurveRows(BankField? curves, List<BankSummaryRow> rows)
    {
        var list = (curves?.Find("set")?.Children ?? []).Select(c =>
            $"{Describe(Id(c.Find("parameter")))} → {c.Find("changes")?.Meaning ?? c.Find("changes")?.Text}:  {PointsText(c)}").ToList();
        if (list.Count > 0) rows.Add(new(L.T("BankView.GameParameters"), string.Join("\n", list)));
    }

    /// <summary>A curve's points as (x, y) pairs: each point's first two values.</summary>
    private static string PointsText(BankField curve) =>
        string.Join("  ", (curve.Find("points")?.Children ?? []).Where(p => p.Children.Count >= 2).Select(p => $"({p.Children[0].Text}, {p.Children[1].Text})"));

    /// <summary>The properties a bundle sets (Volume -7 dB…), one a line.</summary>
    private static string Props(BankField? bundle, bool modulator = false) =>
        string.Join("\n", (bundle?.Find("set")?.Children ?? []).Select(p =>
        {
            string? name = p.Find("property")?.Meaning;
            // a loop count of 0 loops for ever
            string forever = name == "Loop Count" && p.Find("value")?.Long == 0 ? $" ({L.T("BankView.LoopForever")})" : "";
            return $"{name ?? p.Find("property")?.Text} {p.Find("value")?.Text}{(modulator ? "" : BankTerms.Unit(name))}{forever}";
        }));

    private static string Ranges(BankField? bundle) =>
        string.Join("\n", (bundle?.Find("set")?.Children ?? []).Select(p =>
        {
            string? name = p.Find("property")?.Meaning;
            return $"{name ?? p.Find("property")?.Text} {p.Find("min")?.Text} … {p.Find("max")?.Text}{BankTerms.Unit(name)}";
        }));

    private static string StateValues(BankField? values) =>
        string.Join("\n", (values?.Find("set")?.Children ?? []).Select(v =>
        {
            string? name = v.Find("property")?.Meaning;
            return $"{name ?? v.Find("property")?.Text} {v.Find("value")?.Text}{BankTerms.Unit(name)}";
        }));

    private static long? PropertyId(BankField? bundle, string property) =>
        bundle?.Find("set")?.Children.FirstOrDefault(p => p.Find("property")?.Meaning == property)?.Find("value")?.Long;

    // ---------------------------------------------------------------- as text

    /// <summary>The bank, or one object, as text: every value under its name, IDs named where they can be.</summary>
    public string Text(BankObject? only = null)
    {
        var sb = new StringBuilder();
        string? Named(BankField f) => f.Type == "tid" || f.Hash != BankHash.None ? NameOf(f.Long) : null;
        if (only is null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Wwise bank version {Bank.Version}, ID {Bank.BankId}") // loc-ignore
              .Append(NameOf(Bank.BankId) is { } bankName ? $" ({bankName})" : "").Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"{Bank.Objects.Count} objects\n\n"); // loc-ignore
        }
        foreach (var o in only is null ? Bank.Objects : [only])
        {
            sb.Append("# ").Append(Title(o)).Append("  (").Append(KindOf(o)).Append(")\n");
            sb.Append(o.Fields.Dump(Named)).Append('\n');
        }
        return sb.ToString();
    }
}
