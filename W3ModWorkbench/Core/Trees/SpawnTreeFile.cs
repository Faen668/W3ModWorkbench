using System.IO;
using W3ModWorkbench.Core.BehTrees;
using W3ModWorkbench.Core.Cr2w;

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

    private static readonly TreeLegend[] Legend =
    [
        new("Branch (parallel, condition, random…)", Composite), new("Initializer", Initializer), new("Spawn entry", Entry),
        new("Generated entries", Generator), new("Included tree", Include), new("Quest node", Quest),
    ];

    public static TreeDocument Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var tree = file.First("CSpawnTree") ?? throw new InvalidDataException("This isn't a spawn tree.");
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
            d["override"] is true ? "yes" : "",
            ObjectTree.Short(d["forcedAppearance"]) ?? "",
            ObjectTree.Tags(file, d["tags"]),
        ]).ToList();
        int entries = root?.All().Count(n => n.Tint == Entry) ?? 0;
        return new TreeDocument
        {
            File = file,
            Root = root,
            Resource = tree,
            KindName = "Spawn tree",
            Legend = Legend,
            Tables = [new TreeTable("Creature definitions", ["Definition", "Entity template", "Total spawn limit", "Max spawned at once", "Override", "Forced appearance", "Tags"], rows)],
            Summary = $"{Format.Count(definitions.Count, "creature definition")}, {Format.Count(entries, "spawn entry", "spawn entries")}",
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
            return Make(Initializer, "Initializer", ObjectTree.Words(c, "CSpawnTreeInitializer", "CSpawnTree", "ISpawnTreeInitializer"), InitializerDetail(file, o));
        switch (c)
        {
            case "CSpawnTreeParallelNode": return Make(Composite, "Parallel", "Parallel", "runs all of its branches");
            case "CSpawnTreeSelectRandomNode": return Make(Composite, "Random choice", "Select random", "runs one of its branches");
            case "CSpawnTreeEntryList": return Make(Composite, "Entry list", "Entry list");
            case "CSpawnTreeConditionNode": return Make(Composite, "Condition", "Condition", Conditions(file, o));
            case "CSpawnTreeDecoratorInitializersList": return Make(Initializer, "Initializers", "Initializers", "set up everything spawned below");
            case "CSpawnTreeVoidDecorator": return Make(Composite, "Decorator", "Decorator");
            case "CSpawnTreeTimetableEntry":
            {
                string? from = ObjectTree.GameTime(o["begin"]), to = ObjectTree.GameTime(o["end"]);
                return Make(Entry, "Spawn entries", from is null && to is null ? "Spawn entries" : $"Spawn entries ({from ?? "0:00"} to {to ?? "0:00"})",
                    from is null && to is null ? "at any time" : "at these times of day");
            }
            case "CCreatureEntry":
            {
                string definition = o["creatureDefinition"] as string ?? "?";
                long min = o.Integer("quantityMin", 1), max = o.Integer("quantityMax", 1);
                string count = min == max ? $"{min}" : $"{min} to {max}";
                return Make(Entry, "Spawn entry", $"Entry {definition}", $"{count} of them" + Spawner(file, o));
            }
            case "CCreaturePartyEntry":
                return Make(Entry, "Party", "Party", $"{Format.Count(o.Array("subDefinitions").Count, "member")}" + Spawner(file, o));
            case "CSpawnTreeEntrySubDefinition":
                return Make(Entry, "Party member", $"{o["partyMemberId"] as string ?? "Member"}", o["creatureDefinition"] as string ?? "");
            case "CWorkEntryGenerator" or "CWanderAndWorkEntryGenerator":
            {
                int count = o.Array("entries").Count;
                return Make(Generator, "Generated entries", c == "CWorkEntryGenerator" ? "Work smart AI entries" : "Wander and work entries",
                    $"{Format.Count(count, "entry", "entries")}: " + string.Join(", ", o.Array("entries").OfType<RedObject>()
                        .Select(e => e.Struct("creatureEntry")?.Struct("creatureDefinition")?["creatureDefinition"] as string).Where(d => d is not null).Distinct()));
            }
            case "CSpawnTreeIncludeTreeNode":
                return Make(Include, "Included tree", "Include", o["spawnTree"] is RedHandle ? "another spawn tree" : "");
            case "CSpawnTreeQuestNode" or "CSpawnTreeQuestPhase":
                return Make(Quest, "Quest node", ObjectTree.Words(c, "CSpawnTree"), o["questPhase"] as string ?? "");
        }
        return Make(Composite, "Node", ObjectTree.Words(c, "CSpawnTree", "ISpawnTree", "C"));
    }

    /// <summary>A spawn entry's spawn point tags, when it has any.</summary>
    private static string Spawner(Cr2wFile file, RedObject o) =>
        o.Struct("baseSpawner") is { } s && ObjectTree.Tags(file, s["tags"]) is { Length: > 0 } tags ? $"  ·  at {tags}" : "";

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
        return tests.Count == 0 ? "always" : string.Join(" and ", tests);
    }

    /// <summary>An initializer's settings that say what it does: a guard area's tag, a spawner's tags, an AI's tree.</summary>
    private static string InitializerDetail(Cr2wFile file, RedObject o)
    {
        if (ObjectTree.Short(o["guardAreaTag"]) is { } guard) return $"guard area {guard}";
        if (o.Struct("spawner") is { } spawner && ObjectTree.Tags(file, spawner["tags"]) is { Length: > 0 } tags) return $"at {tags}";
        foreach (var (name, _, value) in o.Properties)
            if (name is not ("id" or "graphPosX" or "graphPosY" or "comment" or "overrideDeepInitializers") && ObjectTree.Short(value) is { Length: > 0 and < 40 } text)
                return $"{name} {text}";
        return "";
    }
}
