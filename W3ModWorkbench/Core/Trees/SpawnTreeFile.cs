using System.IO;
using W3ModWorkbench.Core.BehTrees;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Trees;

/// <summary>
/// A spawn tree (.spawntree), what spawns a community's or an encounter's creatures: a CSpawnTree whose rootNode starts
/// a tree of nodes (parallels, conditions, random choices, times of day) down to the entries that spawn, each with the
/// initializers that set up what's spawned (its AI, where it guards, its attitude). Beside the tree it keeps its
/// creature definitions, the entity templates the entries spawn by name.
/// </summary>
public static class SpawnTreeFile
{
    public static bool IsSpawnTree(string path) => path.EndsWith(".spawntree", StringComparison.OrdinalIgnoreCase);

    // the colours REDkit's spawn tree editor gives each kind, brightened a little for our themes
    private const uint Composite = 0xFF9DA33A, Initializer = 0xFF4F86C6, Entry = 0xFF3FAE4F, Generator = 0xFF8E6BB0, Include = 0xFFD08A3C, Quest = 0xFFC75B7A;

    private static TreeLegend[] Legend =>
    [
        new(L.T("SpawnTreeFile.BranchParallelConditionRandom"), Composite), new(L.T("SpawnTreeFile.Initializer"), Initializer), new(L.T("SpawnTreeFile.SpawnEntry"), Entry),
        new(L.T("SpawnTreeFile.GeneratedEntries"), Generator), new(L.T("SpawnTreeFile.IncludedTree"), Include), new(L.T("SpawnTreeFile.QuestNode"), Quest),
    ];

    public static TreeDocument Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var tree = file.First("CSpawnTree") ?? throw new InvalidDataException(L.T("SpawnTreeFile.IsntSpawnTree"));
        BehTreeNode? root = tree["rootNode"] is RedHandle { Export: int at }
            ? ObjectTree.Build(file, at, IsNode, (o, link) => Look(file, o, link), new HashSet<string>(StringComparer.Ordinal) { "parent" })
            : null;
        var definitions = tree.Array("creatureDefinition").OfType<RedHandle>().Where(h => h.Export is int i && i < file.Exports.Count)
            .Select(h => file.Object(h.Export!.Value)).ToList();
        var rows = definitions.Select(d => (IReadOnlyList<string>)
        [
            d["definitionName"] as string ?? "",
            Reference(file, d["entityTemplate"]),
            d.Integer("totalSpawnLimit").ToString(),
            d.Integer("maxSpawnedAtOnce").ToString(),
            d["override"] is true ? L.T("SceneFile.Yes") : "",
            ObjectTree.Short(d["forcedAppearance"]) ?? "",
            ObjectTree.Tags(file, d["tags"]),
        ]).ToList();
        int entries = root?.All().Count(n => n.Tint == Entry) ?? 0;
        return new TreeDocument
        {
            File = file,
            Root = root,
            Resource = tree,
            KindName = L.T("SpawnTreeFile.SpawnTree"),
            Legend = Legend,
            Tables = [new TreeTable(L.T("SpawnTreeFile.CreatureDefinitions"), [L.T("SpawnTreeFile.Definition"), L.T("SpawnTreeFile.EntityTemplate"), L.T("SpawnTreeFile.TotalSpawnLimit"), L.T("SpawnTreeFile.MaxSpawnedOnce"), L.T("SpawnTreeFile.Override"), L.T("SpawnTreeFile.ForcedAppearance"), L.T("SpawnTreeFile.Tags")], rows)],
            Summary = $"{L.T("Count.CreatureDefinition", definitions.Count)}, {L.T("Count.SpawnEntry", entries)}",
        };
    }

    private static bool IsNode(string className) =>
        ObjectTree.IsA(className, "ISpawnTreeBaseNode") || ObjectTree.IsA(className, "ISpawnTreeInitializer");

    /// <summary>A soft or hard reference's file path, or "".</summary>
    private static string Reference(Cr2wFile file, object? value) =>
        value is RedHandle h ? file.PathOf(h) ?? "" : "";

    private static TreeLook Look(Cr2wFile file, RedObject o, string link)
    {
        string c = o.ClassName;
        string? label = o["comment"] is string comment && comment.Length > 0 && comment != "None" ? comment : null;
        string? name = o["nodeName"] is string n && n.Length > 0 && n != "None" ? n : null;
        TreeLook Make(uint tint, string kind, string title, string detail = "") => new(tint, kind, name ?? title, detail, label);

        if (ObjectTree.IsA(c, "ISpawnTreeInitializer"))
            return Make(Initializer, L.T("SpawnTreeFile.Initializer"), ObjectTree.Words(c, "CSpawnTreeInitializer", "CSpawnTree", "ISpawnTreeInitializer"), InitializerDetail(file, o));
        switch (c)
        {
            case "CSpawnTreeParallelNode": return Make(Composite, L.T("SpawnTreeFile.Parallel"), L.T("SpawnTreeFile.Parallel"), L.T("SpawnTreeFile.RunsAllBranches"));
            case "CSpawnTreeSelectRandomNode": return Make(Composite, L.T("SpawnTreeFile.RandomChoice"), L.T("SpawnTreeFile.SelectRandom"), L.T("SpawnTreeFile.RunsOneBranches"));
            case "CSpawnTreeEntryList": return Make(Composite, L.T("SpawnTreeFile.EntryList"), L.T("SpawnTreeFile.EntryList"));
            case "CSpawnTreeConditionNode": return Make(Composite, L.T("SpawnTreeFile.Condition"), L.T("SpawnTreeFile.Condition"), Conditions(file, o));
            case "CSpawnTreeDecoratorInitializersList": return Make(Initializer, L.T("SpawnTreeFile.Initializers"), L.T("SpawnTreeFile.Initializers"), L.T("SpawnTreeFile.SetUpEverythingSpawned"));
            case "CSpawnTreeVoidDecorator": return Make(Composite, L.T("SpawnTreeFile.Decorator"), L.T("SpawnTreeFile.Decorator"));
            case "CSpawnTreeTimetableEntry":
            {
                string? from = ObjectTree.GameTime(o["begin"]), to = ObjectTree.GameTime(o["end"]);
                return Make(Entry, L.T("SpawnTreeFile.SpawnEntries"), from is null && to is null ? L.T("SpawnTreeFile.SpawnEntries") : L.T("SpawnTreeFile.SpawnEntries2", from ?? "0:00", to ?? "0:00"),
                    from is null && to is null ? L.T("SpawnTreeFile.AnyTime") : L.T("SpawnTreeFile.TheseTimesDay"));
            }
            case "CCreatureEntry":
            {
                string definition = o["creatureDefinition"] as string ?? "?";
                long min = o.Integer("quantityMin", 1), max = o.Integer("quantityMax", 1);
                string count = min == max ? $"{min}" : L.T("SpawnTreeFile.Range", min, max);
                return Make(Entry, L.T("SpawnTreeFile.SpawnEntry"), L.T("SpawnTreeFile.Entry", definition), L.T("SpawnTreeFile.Them", count, Spawner(file, o)));
            }
            case "CCreaturePartyEntry":
                return Make(Entry, L.T("SpawnTreeFile.Party"), L.T("SpawnTreeFile.Party"), $"{L.T("Count.Member", o.Array("subDefinitions").Count)}" + Spawner(file, o));
            case "CSpawnTreeEntrySubDefinition":
                return Make(Entry, L.T("SpawnTreeFile.PartyMember"), $"{o["partyMemberId"] as string ?? L.T("SpawnTreeFile.Member")}", o["creatureDefinition"] as string ?? "");
            case "CWorkEntryGenerator" or "CWanderAndWorkEntryGenerator":
            {
                int count = o.Array("entries").Count;
                return Make(Generator, L.T("SpawnTreeFile.GeneratedEntries"), c == "CWorkEntryGenerator" ? L.T("SpawnTreeFile.WorkSmartAIEntries") : L.T("SpawnTreeFile.WanderWorkEntries"),
                    $"{L.T("Count.Entry", count)}: " + string.Join(", ", o.Array("entries").OfType<RedObject>()
                        .Select(e => e.Struct("creatureEntry")?.Struct("creatureDefinition")?["creatureDefinition"] as string).Where(d => d is not null).Distinct()));
            }
            case "CSpawnTreeIncludeTreeNode":
                return Make(Include, L.T("SpawnTreeFile.IncludedTree"), L.T("SpawnTreeFile.Include"), o["spawnTree"] is RedHandle ? L.T("SpawnTreeFile.AnotherSpawnTree") : "");
            case "CSpawnTreeQuestNode" or "CSpawnTreeQuestPhase":
                return Make(Quest, L.T("SpawnTreeFile.QuestNode"), ObjectTree.Words(c, "CSpawnTree"), o["questPhase"] as string ?? "");
        }
        return Make(Composite, L.T("SpawnTreeFile.Node"), ObjectTree.Words(c, "CSpawnTree", "ISpawnTree", "C"));
    }

    /// <summary>A spawn entry's spawn point tags, when it has any.</summary>
    private static string Spawner(Cr2wFile file, RedObject o) =>
        o.Struct("baseSpawner") is { } s && ObjectTree.Tags(file, s["tags"]) is { Length: > 0 } tags ? "  ·  " + L.T("SpawnTreeFile.At", tags) : "";

    /// <summary>What a condition node tests: each fact, its comparison and value, joined by "and".</summary>
    private static string Conditions(Cr2wFile file, RedObject o)
    {
        var tests = new List<string>();
        foreach (var h in o.Array("conditions").OfType<RedHandle>())
        {
            if (h.Export is not int i || i >= file.Exports.Count) continue;
            var c = file.Object(i);
            if (c["fact"] is string fact)
            {
                string op = (c["operator"] as RedEnum)?.ToString() switch
                {
                    null or "EO_Equal" => "=", "EO_NotEqual" => "≠", "EO_Less" => "<", "EO_LessEqual" => "≤", "EO_Greater" => ">", "EO_GreaterEqual" => "≥",
                    var other => other,
                };
                tests.Add($"{fact} {op} {c.Integer("factValue")}");
            }
            else tests.Add(ObjectTree.Words(c.ClassName, "W3", "CSpawn", "C"));
        }
        return tests.Count == 0 ? L.T("SpawnTreeFile.Always") : string.Join(L.T("SpawnTreeFile.And"), tests);
    }

    /// <summary>An initializer's settings that say what it does: a guard area's tag, a spawner's tags, an AI's tree.</summary>
    private static string InitializerDetail(Cr2wFile file, RedObject o)
    {
        if (ObjectTree.Short(o["guardAreaTag"]) is { } guard) return L.T("SpawnTreeFile.GuardArea", guard);
        if (o.Struct("spawner") is { } spawner && ObjectTree.Tags(file, spawner["tags"]) is { Length: > 0 } tags) return L.T("SpawnTreeFile.At", tags);
        foreach (var (name, _, value) in o.Properties)
            if (name is not ("id" or "graphPosX" or "graphPosY" or "comment" or "overrideDeepInitializers") && ObjectTree.Short(value) is { Length: > 0 and < 40 } text)
                return $"{name} {text}";
        return "";
    }
}
