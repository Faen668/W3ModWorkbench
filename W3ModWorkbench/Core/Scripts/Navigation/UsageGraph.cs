using W3ModWorkbench.Localisation;
namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>One box in the usage graph: the declaration being looked at, or a function (or type, or file) that uses what is further right.</summary>
public sealed class UsageNode
{
    public required string Key { get; init; }

    /// <summary>The declaration, or null for a file that uses something outside any function or type.</summary>
    public ScriptSymbol? Symbol { get; init; }

    public required ScriptFile File { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }
    public int Line { get; init; }

    /// <summary>Steps from the thing the graph is about: 0 for it, 1 for what uses it directly.</summary>
    public int Depth { get; set; }

    public bool IsTarget { get; init; }

    /// <summary>The function calls itself.</summary>
    public bool IsRecursive { get; set; }

    /// <summary>Callers that were found but left out to keep the graph readable.</summary>
    public int MoreCallers { get; set; }

    /// <summary>Where a function's callers weren't looked for, because it is as far out as the graph goes.</summary>
    public bool Unexpanded { get; set; }

    public string Title => Symbol?.Title ?? File.Path;
    public string Detail => $"{File.Path} : {Line}";
}

/// <summary>One use: <see cref="From"/> mentions <see cref="To"/> at each of the <see cref="Sites"/>.</summary>
public sealed class UsageEdge(UsageNode from, UsageNode to)
{
    public UsageNode From { get; } = from;
    public UsageNode To { get; } = to;
    public List<ScriptHit> Sites { get; } = [];
}

public sealed class UsageGraph
{
    public required UsageNode Target { get; init; }
    public List<UsageNode> Nodes { get; } = [];
    public List<UsageEdge> Edges { get; } = [];

    /// <summary>The graph stopped growing at the most nodes it shows.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Works out who uses a declaration, and who uses them in turn, as far out as asked. A use is a reference found
/// the way find all references finds them; the caller is the function it's written in.
/// </summary>
public static class UsageGraphBuilder
{
    public const int MaxNodes = 150;

    /// <summary>The most callers one function lists; the rest are only counted.</summary>
    public const int MaxCallersEach = 24;

    public static UsageGraph Build(IReadOnlyList<ScriptSymbol> targets, ParsedScript current, IReadOnlyList<ScriptIndex> layers,
        int levels, CancellationToken cancel, Action<string>? progress = null)
    {
        var first = targets[0];
        var graph = new UsageGraph { Target = Node(first, depth: 0, isTarget: true) };
        var nodes = new Dictionary<string, UsageNode> { [graph.Target.Key] = graph.Target };
        var edges = new Dictionary<(string, string), UsageEdge>();
        graph.Nodes.Add(graph.Target);

        // each level asks for the uses of everything that was new in the one before
        var frontier = new List<(UsageNode Node, IReadOnlyList<ScriptSymbol> Targets, ParsedScript Parsed)> { (graph.Target, targets, current) };
        for (int depth = 1; depth <= levels && frontier.Count > 0; depth++)
        {
            var next = new List<(UsageNode, IReadOnlyList<ScriptSymbol>, ParsedScript)>();
            foreach (var (node, symbols, parsed) in frontier)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Invoke(L.T("UsageGraph.LevelLookingWhatUses", depth, node.Title));
                var hits = ScriptSearch.Scan(symbols, parsed, layers, cancel);
                int listed = 0, skipped = 0;
                foreach (var (hit, hitParsed) in hits)
                {
                    if (hit.Label == "declaration") continue;
                    var function = hitParsed.FunctionAt(hit.Offset);
                    var user = UserOf(hitParsed, function, hit.Offset);
                    if (user.Key == node.Key)
                    {
                        node.IsRecursive = true;
                        continue;
                    }
                    bool known = nodes.TryGetValue(user.Key, out var existing);
                    if (!known)
                    {
                        if (listed >= MaxCallersEach || graph.Nodes.Count >= MaxNodes)
                        {
                            skipped++;
                            if (graph.Nodes.Count >= MaxNodes) graph.Truncated = true;
                            continue;
                        }
                        listed++;
                        existing = user;
                        user.Depth = depth;
                        nodes[user.Key] = user;
                        graph.Nodes.Add(user);
                        // only functions are worth following: what uses a type or a whole file is too broad
                        if (function is not null)
                            next.Add((user, Targets(function.Symbol, hitParsed, layers), hitParsed));
                    }
                    var key = (existing!.Key, node.Key);
                    if (!edges.TryGetValue(key, out var edge))
                    {
                        edge = new UsageEdge(existing, node);
                        edges[key] = edge;
                        graph.Edges.Add(edge);
                    }
                    edge.Sites.Add(hit);
                }
                node.MoreCallers += skipped;
            }
            frontier = next;
        }
        foreach (var (node, _, _) in frontier) node.Unexpanded = true;
        return graph;
    }

    /// <summary>
    /// What a use of a function leads to. A wrap or replace stands for the function it changes too, since calls
    /// are written against that one.
    /// </summary>
    public static IReadOnlyList<ScriptSymbol> Targets(ScriptSymbol function, ParsedScript parsed, IReadOnlyList<ScriptIndex> layers)
    {
        if (function.Annotation is not ("wrapMethod" or "replaceMethod")) return [function];
        var found = new DefinitionFinder(parsed, layers).Find(function.Offset, function.Name.Length);
        return found.Count == 0 ? [function] : [function, .. found.Where(f => f.Offset != function.Offset || f.File.Key != function.File.Key)];
    }

    /// <summary>The function a use is written in; otherwise the type, or failing that the file.</summary>
    private static UsageNode UserOf(ParsedScript parsed, FunctionScope? function, int offset)
    {
        if (function is not null) return Node(function.Symbol, depth: 0, isTarget: false);
        if (parsed.TypeAt(offset) is { } type) return Node(type.Symbol, depth: 0, isTarget: false);
        return new UsageNode { Key = "file:" + parsed.File.Key, File = parsed.File, Line = 1 };
    }

    private static UsageNode Node(ScriptSymbol symbol, int depth, bool isTarget) => new()
    {
        Key = $"{symbol.File.Key}@{symbol.Offset}",
        Symbol = symbol,
        File = symbol.File,
        Offset = symbol.Offset,
        Length = symbol.Name.Length,
        Line = symbol.Line,
        Depth = depth,
        IsTarget = isTarget,
    };
}
