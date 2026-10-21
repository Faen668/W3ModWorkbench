using System.IO;
using W3ModWorkbench.Core.BehTrees;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Trees;

/// <summary>
/// A job tree (.w2job), what an NPC does at an action point: a CJobTree whose root node starts a tree of nodes, each
/// running its children in order or picking one at random, some times over or looping, with the actions it plays on
/// entering, leaving and being made to leave in a hurry (each an animation of its category's set, at a place, with an
/// item maybe). The tree's settings say how the NPC moves to it and what happens to what it holds.
/// </summary>
public static class JobTreeFile
{
    public static bool IsJobTree(string path) => path.EndsWith(".w2job", StringComparison.OrdinalIgnoreCase);

    private const uint Node = 0xFF9DA33A, Enter = 0xFF3FAE4F, Leave = 0xFF4F86C6, ForceOut = 0xFFD0614A;

    private static readonly TreeLegend[] Legend = [new("Node (a sequence or a random pick)", Node), new("On entering", Enter), new("On leaving", Leave), new("When made to leave", ForceOut)];

    /// <summary>The properties that hold a node's actions, and what each is called.</summary>
    private static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        ["onEnterAction"] = "enter", ["onLeaveAction"] = "leave", ["onFastLeaveAction"] = "fast leave",
    };

    public static TreeDocument Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var tree = file.First("CJobTree") ?? throw new InvalidDataException("This isn't a job tree.");
        BehTreeNode? root = tree["jobTreeRootNode"] is RedHandle { Export: int at }
            ? ObjectTree.Build(file, at, c => c is "CJobTreeNode" || ObjectTree.IsA(c, "CJobActionBase"), (o, link) => Look(o, link))
            : null;
        if (root is not null) Labels(root);
        var all = root?.All().ToList() ?? [];
        int actions = all.Count(n => n.Object.ClassName != "CJobTreeNode");
        var categories = all.SelectMany(n => n.Object.Array("validCategories").OfType<string>()).Distinct().ToList();
        return new TreeDocument
        {
            File = file,
            Root = root,
            Resource = tree,
            KindName = "Job tree",
            Legend = Legend,
            Summary = $"{Format.Count(actions, "action")}" + (categories.Count > 0 ? $", for {string.Join(", ", categories)}" : ""),
        };
    }

    /// <summary>An action's place: when it plays; a node's: its order among its parent's nodes.</summary>
    private static void Labels(BehTreeNode node)
    {
        int n = 0;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            child.Label = Actions.TryGetValue(node.ChildLinks[i], out var when) ? when : $"{++n}";
            Labels(child);
        }
    }

    private static TreeLook Look(RedObject o, string link)
    {
        if (o.ClassName == "CJobTreeNode")
        {
            string mode = (o["selectionMode"] as RedEnum)?.ToString() switch
            {
                null => "Sequence",
                var m when m.Contains("Random", StringComparison.OrdinalIgnoreCase) => "Random",
                var m => ObjectTree.Words(m, "EJTNSM_", "JTNSM_"),
            };
            long times = o.Integer("iterations", 1);
            string title = o["looped"] is true ? $"{mode}, looped" : $"{mode} ×{Math.Max(1, times)}";
            var said = new List<string>();
            if (ObjectTree.Short(o["validCategories"]) is { Length: > 0 } categories) said.Add(categories);
            if (ObjectTree.Short(o["leftItem"]) is { Length: > 0 } left) said.Add($"left hand {left}");
            if (ObjectTree.Short(o["rightItem"]) is { Length: > 0 } right) said.Add($"right hand {right}");
            return new(Node, "Node", title, string.Join("  ·  ", said), Kind: BehTreeNodeKind.Composite);
        }
        uint tint = link switch { "onEnterAction" => Enter, "onLeaveAction" => Leave, _ => ForceOut };
        string kind = link switch { "onEnterAction" => "On entering", "onLeaveAction" => "On leaving", _ => "When made to leave" };
        string anim = o["animName"] as string is { Length: > 0 } a && a != "None" ? a : "(no animation)";
        var detail = new List<string>();
        if (ObjectTree.Short(o["place"]) is { Length: > 0 } place) detail.Add($"at {place}");
        if (ObjectTree.Short(o["itemName"]) is { Length: > 0 } item) detail.Add($"item {item}");
        if (o["categoryName"] is string category && category.Length > 0) detail.Add(category);
        return new(tint, kind, anim, string.Join("  ·  ", detail), Kind: BehTreeNodeKind.Action);
    }

    /// <summary>The animation set a job category's animations are in, from the game's table of them (animations.csv).</summary>
    public static string? AnimsetOf(string category, Func<string, byte[]?> read)
    {
        if (read(@"gameplay\globals\animations.csv") is not { } data) return null;
        foreach (var line in System.Text.Encoding.UTF8.GetString(data).Split('\n'))
        {
            var parts = line.Trim().Split(';');
            if (parts.Length >= 2 && parts[0].Equals(category, StringComparison.OrdinalIgnoreCase)) return parts[1].Trim();
        }
        return null;
    }

    /// <summary>Every job category and its animation set, from the game's table of them.</summary>
    public static List<(string Category, string Animset)> Categories(Func<string, byte[]?> read)
    {
        var list = new List<(string, string)>();
        if (read(@"gameplay\globals\animations.csv") is not { } data) return list;
        foreach (var line in System.Text.Encoding.UTF8.GetString(data).Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(';');
            if (parts.Length >= 2 && parts[0].Length > 0) list.Add((parts[0], parts[1].Trim()));
        }
        return list;
    }

    /// <summary>A character to preview a job category's animations on: a woman, a child or a dwarf for theirs, else a man.</summary>
    public static string PreviewEntity(string? category)
    {
        string c = category ?? "";
        if (c.Contains("woman", StringComparison.OrdinalIgnoreCase)) return PreviewEntities[1];
        if (c.Contains("child", StringComparison.OrdinalIgnoreCase)) return PreviewEntities[2];
        if (c.Contains("dwarf", StringComparison.OrdinalIgnoreCase)) return PreviewEntities[3];
        return PreviewEntities[0];
    }

    /// <summary>
    /// Community NPCs to preview animations on, a man, a woman, a child and a dwarf: whole characters with their own
    /// appearances (dyed as the game dyes them) and skeletons, so a child is a child's size.
    /// </summary>
    public static readonly string[] PreviewEntities =
    [
        @"gameplay\community\community_npcs\novigrad\regular\novigrad_citizen_man.w2ent",
        @"gameplay\community\community_npcs\novigrad\regular\novigrad_citizen_woman.w2ent",
        @"gameplay\community\community_npcs\nml\regular\verden_warchild_boy.w2ent",
        @"gameplay\community\community_npcs\novigrad\nonhumans\dwarf_man.w2ent",
    ];
}
