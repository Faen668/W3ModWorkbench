using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;

namespace W3ModWorkbench.Core.Journals;

/// <summary>
/// One entry of the journal: a quest, a group of them, a description, an objective, a character, a creature… Each
/// journal file (.journal) keeps one top entry and the entries under it; a top entry finds its place among the others
/// by its parent's GUID, which is another file's entry (a quest's group, a group's category).
/// </summary>
public sealed class JournalNode
{
    public required string Name { get; init; }
    /// <summary>The entry's object, or null for a category, which is no file's.</summary>
    public RedObject? Object { get; init; }
    public Cr2wFile? File { get; init; }
    /// <summary>The journal file it's in.</summary>
    public string? Path { get; init; }
    public Guid Guid { get; init; }
    public Guid ParentGuid { get; init; }
    public long Order { get; init; }
    public JournalNode? Parent { get; set; }
    public List<JournalNode> Children { get; } = [];
    /// <summary>The kind of entry in words: "Quest", "Description", "Objective", "Character"…</summary>
    public string Kind { get; init; } = "";
    /// <summary>Whether it's the top entry of its file.</summary>
    public bool IsFile { get; init; }

    public string ClassName => Object?.ClassName ?? "";

    public IEnumerable<JournalNode> All()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.All()) yield return node;
    }

    public override string ToString() => Name;
}

/// <summary>
/// The whole journal, as REDkit's journal editor shows it: every journal file's entries in one tree, under categories
/// (quests, characters, bestiary, glossary…), each group with the entries that name it as their parent, each file's
/// entries with those under them, ordered as the editor orders them.
/// </summary>
public static class JournalTree
{
    public static bool IsJournal(string path) => path.EndsWith(".journal", StringComparison.OrdinalIgnoreCase);

    /// <summary>The categories, by the start of their entries' classes, in the editor's order.</summary>
    private static readonly (string Prefix, string Name)[] Categories =
    [
        ("CJournalQuest", "Quests"), ("CJournalCharacter", "Characters"), ("CJournalCreature", "Bestiary"), ("CJournalGlossary", "Glossary"),
        ("CJournalItem", "Items"), ("CJournalPlace", "Places"), ("CJournalTutorial", "Tutorial"), ("CJournalStoryBook", "Storybook"),
    ];

    public static List<JournalNode> Build(IEnumerable<(string Path, Cr2wFile File)> files)
    {
        var tops = new List<JournalNode>();
        foreach (var (path, file) in files)
        {
            var resource = file.First("CJournalResource");
            if (resource?["entry"] is not RedHandle { Export: int at } || at >= file.Exports.Count) continue;
            if (Node(file, path, at, true, 0) is { } top) tops.Add(top);
        }
        // a top entry goes under the entry its parent GUID names, when it's in another file
        var byGuid = new Dictionary<Guid, JournalNode>();
        foreach (var node in tops.SelectMany(t => t.All()))
            if (node.Guid != Guid.Empty) byGuid.TryAdd(node.Guid, node);
        var categories = Categories.Select(c => new JournalNode { Name = c.Name, Kind = "Category" }).ToList();
        var other = new JournalNode { Name = "Other", Kind = "Category" };
        foreach (var top in tops)
        {
            if (top.ParentGuid != Guid.Empty && byGuid.TryGetValue(top.ParentGuid, out var parent) && !ReferenceEquals(parent, top) && !IsUnder(parent, top))
            {
                top.Parent = parent;
                parent.Children.Add(top);
                continue;
            }
            int c = Array.FindIndex(Categories, x => top.ClassName.StartsWith(x.Prefix, StringComparison.Ordinal));
            var category = c >= 0 ? categories[c] : other;
            top.Parent = category;
            category.Children.Add(top);
        }
        var roots = categories.Where(c => c.Children.Count > 0).ToList();
        if (other.Children.Count > 0) roots.Add(other);
        foreach (var root in roots) Sort(root);
        return roots;
    }

    /// <summary>Whether a node is under another, so linking it there would make a loop.</summary>
    private static bool IsUnder(JournalNode node, JournalNode ancestor)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    /// <summary>Each node's children in the editor's order: by their order number, then by name.</summary>
    private static void Sort(JournalNode node)
    {
        node.Children.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var child in node.Children) Sort(child);
    }

    private static JournalNode? Node(Cr2wFile file, string path, int export, bool top, int depth)
    {
        if (depth > 30) return null;
        var o = file.Object(export);
        string name = o["baseName"] as string is { Length: > 0 } b ? b : ObjectTree.Words(o.ClassName, "CJournal");
        // an entry from a DLC says so, as the editor says it
        if (top && o.ClassName.EndsWith("Group", StringComparison.Ordinal) && Dlc(path) is { } dlc) name += $" (dlc: {dlc})";
        var node = new JournalNode
        {
            Name = name,
            Object = o,
            File = file,
            Path = path,
            Guid = o["guid"] is Guid g ? g : Guid.Empty,
            ParentGuid = o["parentGuid"] is Guid p ? p : Guid.Empty,
            Order = o.Integer("order", 0),
            Kind = ObjectTree.Words(o.ClassName, "CJournalQuest", "CJournalCharacter", "CJournalCreature", "CJournalGlossary", "CJournalItem",
                "CJournalPlace", "CJournalTutorial", "CJournalStoryBook", "CJournal"),
            IsFile = top,
        };
        foreach (var h in o.Array("children").OfType<RedHandle>())
            if (h.Export is int child && child < file.Exports.Count && Node(file, path, child, false, depth + 1) is { } kid)
            {
                kid.Parent = node;
                node.Children.Add(kid);
            }
        return node;
    }

    /// <summary>The DLC a journal file is in, by its path (dlc\bob\…), or null.</summary>
    private static string? Dlc(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 && parts[0].Equals("dlc", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    /// <summary>The localized string IDs an entry shows (its title, description, name…), by property, in its order.</summary>
    public static IEnumerable<(string Property, uint Id)> Strings(RedObject o)
    {
        foreach (var (name, type, value) in o.Properties)
            if (type == "LocalizedString" && value is uint id && id != 0) yield return (name, id);
            else if (type == "LocalizedString" && value is int signed && signed != 0) yield return (name, (uint)signed);
    }
}
