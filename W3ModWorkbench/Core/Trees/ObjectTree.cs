using System.Globalization;
using W3ModWorkbench.Core.BehTrees;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Trees;

/// <summary>How a node of a tree file is drawn: its colour and kind, its name, a second line, and a label over it.</summary>
public sealed record TreeLook(uint Tint, string KindText, string Title, string Detail = "", string? Label = null, string? Comment = null,
    BehTreeNodeKind Kind = BehTreeNodeKind.Action, RedObject? Task = null);

/// <summary>A kind of node, for a tree file's legend: its name and colour.</summary>
public sealed record TreeLegend(string Name, uint Tint);

/// <summary>
/// A tree file read for the tree viewer (a spawn tree, a steering graph, a job tree): its CR2W file, the tree from its
/// root, how many nodes, the legend of its node kinds, and any tables it keeps beside the tree.
/// </summary>
public sealed class TreeDocument
{
    public required Cr2wFile File { get; init; }
    public required BehTreeNode? Root { get; init; }
    /// <summary>The file's own object (the spawn tree, the steering behaviour, the job tree), shown when no node is picked.</summary>
    public required RedObject Resource { get; init; }
    public required string KindName { get; init; }
    public required IReadOnlyList<TreeLegend> Legend { get; init; }
    public IReadOnlyList<TreeTable> Tables { get; init; } = [];
    /// <summary>A line about the tree for the status bar.</summary>
    public string Summary { get; init; } = "";

    public int NodeCount => Root?.All().Count() ?? 0;
}

/// <summary>A table a tree file keeps beside its tree (a spawn tree's creature definitions): its title, columns and rows.</summary>
public sealed record TreeTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Builds a tree of a file's objects from a root: a node's children are the objects of node classes its properties
/// point to (a handle, or a list of them), in the order the properties and lists have them. A node reached twice is
/// drawn once, where it's first reached.
/// </summary>
public static class ObjectTree
{
    public static BehTreeNode? Build(Cr2wFile file, int root, Func<string, bool> isNode, Func<RedObject, string, TreeLook> look,
        ISet<string>? notChildren = null)
    {
        var made = new HashSet<int>();
        return Node(file, root, "", isNode, look, notChildren, made, 0);
    }

    private static BehTreeNode? Node(Cr2wFile file, int export, string link, Func<string, bool> isNode, Func<RedObject, string, TreeLook> look,
        ISet<string>? notChildren, HashSet<int> made, int depth)
    {
        if (depth > 200 || export < 0 || export >= file.Exports.Count || !made.Add(export)) return null;
        var o = file.Object(export);
        var l = look(o, link);
        var node = new BehTreeNode
        {
            Object = o,
            Export = export,
            Kind = l.Kind,
            Title = l.Title,
            Detail = l.Detail,
            Comment = l.Comment,
            Task = l.Task,
            Tint = l.Tint,
            KindText = l.KindText,
            Label = l.Label,
        };
        foreach (var (name, _, value) in o.Properties)
        {
            if (notChildren?.Contains(name) == true) continue;
            var handles = value switch
            {
                RedHandle h => [h],
                System.Collections.IList list => list.OfType<RedHandle>().ToList(),
                _ => new List<RedHandle>(),
            };
            foreach (var h in handles)
                if (h.Export is int child && child < file.Exports.Count && isNode(file.Exports[child].ClassName)
                    && Node(file, child, name, isNode, look, notChildren, made, depth + 1) is { } kid)
                {
                    node.Children.Add(kid);
                    node.ChildLinks.Add(name);
                }
        }
        return node;
    }

    /// <summary>Whether a class is a kind of another, by the engine's classes and the game's scripts.</summary>
    public static bool IsA(string className, string ancestor) => Cr2wJson.IsA(className, ancestor);

    /// <summary>A class name in words without a prefix it's known by: CSpawnTreeInitializerGuardArea, less CSpawnTreeInitializer, reads "Guard area".</summary>
    public static string Words(string className, params string[] prefixes)
    {
        string name = className;
        foreach (string prefix in prefixes.OrderByDescending(p => p.Length))
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }
        return BehTreeFile.Words(name);
    }

    /// <summary>A value shown short: text, a number, a yes or no, an enum, a list of a few such; null when it isn't one.</summary>
    public static string? Short(object? value) => value switch
    {
        string s when s.Length > 0 && s != "None" => s,
        bool b => b ? L.T("SceneFile.Yes") : L.T("SceneFile.No"),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        int or uint or short or ushort or byte or sbyte or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture),
        RedEnum e => e.ToString(),
        System.Collections.IList list when list.Count > 0 && list.Cast<object?>().All(v => v is string or RedEnum) =>
            string.Join(", ", list.Cast<object?>().Select(v => v?.ToString()).Where(v => !string.IsNullOrEmpty(v) && v != "None")),
        _ => null,
    };

    /// <summary>A tag list's tags joined, from the bytes it's kept as; "" when there are none.</summary>
    public static string Tags(Cr2wFile file, object? value) => value switch
    {
        byte[] raw => string.Join(", ", file.Tags(raw)),
        RedObject holder => Tags(file, holder["tags"]),
        _ => Short(value) ?? "",
    };

    /// <summary>A game time kept as seconds since midnight ({m_seconds}) as hours and minutes.</summary>
    public static string? GameTime(object? value) =>
        value is RedObject t ? $"{t.Integer("m_seconds") / 3600 % 24}:{t.Integer("m_seconds") / 60 % 60:00}" : null;
}
