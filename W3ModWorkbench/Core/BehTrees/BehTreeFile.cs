using System.IO;
using System.Text;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Scripts.Standalone;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.BehTrees;

/// <summary>What a behaviour tree node does, which it's drawn as.</summary>
public enum BehTreeNodeKind
{
    /// <summary>Picks among or runs its children: a selector, sequence, choice or parallel.</summary>
    Composite,
    /// <summary>Wraps one child, adding a delay, a loop, a priority, a variable…</summary>
    Decorator,
    /// <summary>Runs its child only when a test passes.</summary>
    Condition,
    /// <summary>Runs a task written in WitcherScript (its task is the script class).</summary>
    Script,
    /// <summary>Does something itself, with no children: idles, plays an animation, moves…</summary>
    Action,
    /// <summary>A note in the tree, passing straight to its child.</summary>
    Comment,
    /// <summary>Runs another tree.</summary>
    Subtree,
}

/// <summary>
/// One node of a behaviour tree: its object in the file, what it is, a title and a line saying more, its children in
/// the order the file has them, and for a script node the task it runs.
/// </summary>
public sealed class BehTreeNode
{
    public required RedObject Object { get; init; }
    public required int Export { get; init; }
    public required BehTreeNodeKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    /// <summary>The node's priority, when it has one (the tree's selectors pick the highest that can run).</summary>
    public string? Priority { get; init; }
    public string? Comment { get; init; }
    /// <summary>A script node's task, the script class it runs, with its settings.</summary>
    public RedObject? Task { get; init; }
    /// <summary>The node's own colour (ARGB), for a tree of another kind than a behaviour tree; else its kind's.</summary>
    public uint? Tint { get; init; }
    /// <summary>What kind of node it is in words, for a tree of another kind; else its kind's name.</summary>
    public string? KindText { get; init; }
    /// <summary>A short label over the node, saying why its parent goes to it (a branch's condition, a time of day).</summary>
    public string? Label { get; set; }
    public List<BehTreeNode> Children { get; } = [];
    /// <summary>Which of the node's properties each child is in (child, children, child1, mountHorseChild…), in the same order.</summary>
    public List<string> ChildLinks { get; } = [];

    /// <summary>The names of the node's properties that hold its children, which the graph shows rather than the property list.</summary>
    public IEnumerable<string> LinkNames => ChildLinks.Distinct();

    /// <summary>Every node from this one down, this one first.</summary>
    public IEnumerable<BehTreeNode> All()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.All()) yield return node;
    }
}

/// <summary>
/// A behaviour tree (.w2behtree), the AI an NPC runs: a CBehTree whose rootNode is the first of the tree's nodes. A node
/// is any object of a class the engine derives from IBehTreeNodeDefinition; its children are the properties that point
/// to other nodes (child, children, child1 and child2, childNode, the mount and dismount children), and a script node's
/// work is the object its taskOrigin points to, of a script class. Every other object a node points to (a ticket
/// algorithm, an AI tree's data) is one of its settings.
/// </summary>
public sealed class BehTreeFile
{
    public required Cr2wFile File { get; init; }
    public required BehTreeNode? Root { get; init; }
    public required int NodeCount { get; init; }

    public static bool IsBehTree(string path) => path.EndsWith(".w2behtree", StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<Dictionary<string, RttiClass>> Classes = new(() =>
        Rtti.LoadBuiltIn().Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First()));

    /// <summary>Whether a class is a kind of another, as the engine's classes are derived.</summary>
    private static bool Derives(string className, string from)
    {
        var classes = Classes.Value;
        for (string? name = className; name is not null && classes.TryGetValue(name, out var cls); name = cls.Base)
            if (name == from) return true;
        return false;
    }

    public static bool IsNode(string className) => Derives(className, "IBehTreeNodeDefinition");

    public static BehTreeFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var tree = file.First("CBehTree") ?? throw new InvalidDataException(L.T("BehTreeFile.IsntBehaviourTree"));
        var made = new Dictionary<int, BehTreeNode>();
        BehTreeNode? root = tree["rootNode"] is RedHandle { Export: int at } ? Node(file, at, made, 0) : null;
        return new BehTreeFile { File = file, Root = root, NodeCount = made.Count };
    }

    private static BehTreeNode? Node(Cr2wFile file, int export, Dictionary<int, BehTreeNode> made, int depth)
    {
        // a node is listed once, the first place it's reached, so a node two parents share isn't drawn twice
        if (made.ContainsKey(export) || depth > 200) return null;
        var o = file.Object(export);
        if (!IsNode(o.ClassName)) return null;
        var task = o["taskOrigin"] is RedHandle { Export: int t } ? file.Object(t) : null;
        var node = new BehTreeNode
        {
            Object = o,
            Export = export,
            Kind = KindOf(o.ClassName),
            Title = TitleOf(o, task),
            Detail = DetailOf(o, task),
            Priority = o["priority"] is RedObject p ? Variable(p) : null,
            Comment = o["comment"] is string c && c.Length > 0 && c != "None" ? c : null,
            Task = task,
        };
        made[export] = node;
        foreach (var (name, _, value) in o.Properties)
        {
            if (name == "taskOrigin") continue;
            var handles = value switch
            {
                RedHandle h => [h],
                System.Collections.IList list => list.OfType<RedHandle>().ToList(),
                _ => new List<RedHandle>(),
            };
            foreach (var h in handles)
                if (h.Export is int child && IsNode(file.Exports[child].ClassName) && Node(file, child, made, depth + 1) is { } kid)
                {
                    node.Children.Add(kid);
                    node.ChildLinks.Add(name);
                }
        }
        return node;
    }

    private static BehTreeNodeKind KindOf(string className)
    {
        if (className.Contains("Comment", StringComparison.Ordinal)) return BehTreeNodeKind.Comment;
        if (className.Contains("Subtree", StringComparison.Ordinal)) return BehTreeNodeKind.Subtree;
        // a metanode sets something up for the nodes under it, as a decorator does
        if (className.Contains("Metanode", StringComparison.Ordinal)) return BehTreeNodeKind.Decorator;
        if (className.Contains("Script", StringComparison.Ordinal)) return BehTreeNodeKind.Script;
        if (className.Contains("Condition", StringComparison.Ordinal)) return BehTreeNodeKind.Condition;
        if (Derives(className, "IBehTreeNodeCompositeDefinition") || className.Contains("Selector") || className.Contains("Sequence")
            || className.Contains("Choice") || className.Contains("Parallel")) return BehTreeNodeKind.Composite;
        if (Derives(className, "IBehTreeNodeDecoratorDefinition") || className.Contains("Decorator")) return BehTreeNodeKind.Decorator;
        return BehTreeNodeKind.Action;
    }

    /// <summary>
    /// A node's name in words: its task's, for a script node, else its class's without the engine's wrapping, and without
    /// a first word that only says the kind the node's colour already says (Decorator, Condition, Atomic, Node).
    /// </summary>
    private static string TitleOf(RedObject node, RedObject? task)
    {
        if (node["debugName"] is string debug && debug.Length > 0 && debug != "None") return debug;
        string title = Words(task?.ClassName ?? node.ClassName);
        foreach (string kind in new[] { "Decorator ", "Condition ", "Atomic ", "Node " })
            if (title.Length > kind.Length + 2 && title.StartsWith(kind, StringComparison.Ordinal))
                return char.ToUpperInvariant(title[kind.Length]) + title[(kind.Length + 1)..];
        return title;
    }

    /// <summary>
    /// The second line: the node's class in words for a script node (so the task above says what and this says how),
    /// and the variable a conditional node tests, as "name is value".
    /// </summary>
    private static string DetailOf(RedObject node, RedObject? task)
    {
        var parts = new List<string>();
        if (node["val"] is RedObject val && Variable(val) is { } test)
            parts.Add(L.T(node["invert"] is true || node["invertCondition"] is true ? "BehTreeFile.Unless" : "BehTreeFile.If", test));
        if (node["data"] is RedHandle) parts.Add(L.T("BehTreeFile.AITree"));
        // then what it's set to do: its task's settings, or its own
        parts.AddRange(Settings(task ?? node).Take(Math.Max(0, 2 - parts.Count)));
        if (parts.Count == 0 && task is not null) parts.Add(Words(node.ClassName));
        return string.Join("  ·  ", parts);
    }

    // the settings most nodes have, which say nothing about what this one does
    private static readonly HashSet<string> Common =
    [
        "priority", "comment", "debugName", "graphPosX", "graphPosY", "forwardAvailability", "forwardTestIfNotAvailable", "invertAvailability",
        "skipIfActive", "runMainOnActivation", "val", "invert", "invertCondition", "data", "taskOrigin", "child", "children", "child1", "child2", "childNode",
    ];

    /// <summary>A node's or task's settings as short "name value" pairs, the ones with a value that fits on a line.</summary>
    private static IEnumerable<string> Settings(RedObject o)
    {
        foreach (var (name, _, value) in o.Properties)
        {
            if (Common.Contains(name)) continue;
            string? text = value switch
            {
                string s when s.Length > 0 && s != "None" => s,
                bool b => b ? "true" : "false",
                float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                int or uint or short or ushort or byte or sbyte or long => value.ToString(),
                RedEnum e => e.ToString(),
                RedObject v when v.ClassName.StartsWith("CBehTreeVal", StringComparison.Ordinal) => Variable(v),
                _ => null,
            };
            if (text is { Length: > 0 and < 40 }) yield return $"{name} {text}";
        }
    }

    /// <summary>A behaviour variable or constant ({varName, value}) as "name is value", or the value alone when it isn't bound to one.</summary>
    private static string? Variable(RedObject v)
    {
        string? name = v["varName"] as string;
        string value = v["value"] switch
        {
            bool b => b ? "true" : "false",
            float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            RedEnum e => e.ToString(),
            null => "",
            var other => other.ToString() ?? "",
        };
        if (string.IsNullOrEmpty(name) || name == "None") return value.Length > 0 ? value : null;
        return value.Length > 0 ? L.T("BehTreeFile.Is", name, value) : name;
    }

    /// <summary>
    /// A class name in words: the engine's prefixes and suffixes off (CBehTreeNode, CBTTask, Definition, Def…) and the
    /// words split, so CBehTreeNodeAtomicPlayAnimationEventDefinition reads "Atomic play animation event".
    /// </summary>
    public static string Words(string className)
    {
        string name = className;
        foreach (string prefix in new[] { "CBehTreeNode", "CBehTreeTask", "CBehTree", "IBehTree", "CBTTask", "CBTCond", "BTTask", "BTCond", "CBT", "Task" })
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }
        foreach (string suffix in new[] { "Definition", "Defintion", "Def" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)) { name = name[..^suffix.Length]; break; }
        var words = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool boundary = i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]));
            if (boundary || c == '_') words.Append(' ');
            if (c != '_') words.Append(words.Length == 0 ? c : boundary ? char.ToLowerInvariant(c) : c);
        }
        string text = words.ToString().Replace("  ", " ").Trim();
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : className;
    }
}
