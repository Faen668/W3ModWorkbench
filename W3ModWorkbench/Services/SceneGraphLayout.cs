using System.Windows;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Scenes;

namespace W3ModWorkbench;

/// <summary>
/// Where each part of a scene's flow goes in its graph. REDkit's copy of one of the game's scenes keeps the editor's
/// graph (CStorySceneGraph: a block for each part, with its position), which cooking leaves out; with it the graph is
/// laid out as the writers laid it out in REDkit. Without it (a mod's scene, or no REDkit), the parts go left to right
/// by how far the flow is from a start, each column in the order the flow reaches them.
/// </summary>
public static class SceneGraphLayout
{
    // REDkit's blocks are small; ours carry their lines, so its positions are spread out to keep them apart
    private const double ScaleX = 2.0, ScaleY = 1.8;
    private const double ColumnWidth = 280, RowHeight = 150;

    /// <summary>Each part's top-left corner, and whether it came from REDkit's graph.</summary>
    public static (Dictionary<SceneNode, Point> Places, bool FromRedkit) Layout(SceneFile scene, Cr2wFile? redkit)
    {
        if (redkit is not null && FromRedkit(scene, redkit) is { Count: > 0 } placed && placed.Count >= scene.Nodes.Count * 3 / 4)
        {
            // a part REDkit's graph hasn't got goes beside the last one, in flow order
            double x = placed.Values.Max(p => p.X) + ColumnWidth, y = 0;
            foreach (var node in scene.Nodes.Where(n => !placed.ContainsKey(n)))
            {
                placed[node] = new Point(x, y);
                y += RowHeight;
            }
            return (placed, true);
        }
        return (Columns(scene), false);
    }

    private static Dictionary<SceneNode, Point>? FromRedkit(SceneFile scene, Cr2wFile redkit)
    {
        try
        {
            // each block, by the part it stands for in REDkit's file
            var blocks = new Dictionary<int, Point>();
            foreach (var export in redkit.Exports.Where(x => x.ClassName.StartsWith("CStoryScene", StringComparison.Ordinal) && x.ClassName.EndsWith("Block", StringComparison.Ordinal)))
            {
                var block = redkit.Object(export.Index);
                if (block.Struct("position") is not { } position) continue;
                // the block's handle to its part: any handle but its sockets
                var part = block.Properties.Where(p => p.Name != "sockets").Select(p => p.Value).OfType<RedHandle>().FirstOrDefault(h => h.Export is not null);
                if (part.Export is int at) blocks[at] = new Point(position.Float("X") * ScaleX, position.Float("Y") * ScaleY);
            }
            if (blocks.Count == 0) return null;
            var places = new Dictionary<SceneNode, Point>();
            foreach (var node in scene.Nodes)
                if (scene.File.Object(node.Export) is { } cooked && RedkitSource.Match(redkit, cooked, scene.File) is { Export: { } theirs }
                    && blocks.TryGetValue(theirs.Index, out var place))
                    places[node] = place;
            return places;
        }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Columns by the fewest steps from a start; parts nothing leads to in a last column.</summary>
    private static Dictionary<SceneNode, Point> Columns(SceneFile scene)
    {
        var depth = new Dictionary<SceneNode, int>();
        var queue = new Queue<SceneNode>();
        foreach (var start in scene.Nodes.Where(n => n.Kind == SceneNodeKind.Input))
        {
            depth[start] = 0;
            queue.Enqueue(start);
        }
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var next in node.AllLinks.Select(l => l.Target).OfType<SceneNode>())
                if (!depth.ContainsKey(next))
                {
                    depth[next] = depth[node] + 1;
                    queue.Enqueue(next);
                }
        }
        int last = depth.Count == 0 ? 0 : depth.Values.Max() + 1;
        var places = new Dictionary<SceneNode, Point>();
        var rows = new Dictionary<int, int>();
        foreach (var node in scene.Nodes)
        {
            int column = depth.TryGetValue(node, out int d) ? d : last;
            int row = rows.GetValueOrDefault(column);
            rows[column] = row + 1;
            places[node] = new Point(column * ColumnWidth, row * RowHeight);
        }
        return places;
    }
}
