using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.World;

/// <summary>
/// One split of a simplex tree: a line across the map, the side it faces (its normal) and where it lies (the offset).
/// A point is on its positive side when <c>normal · point + offset</c> is 0 or more. Each side either goes on to
/// another split (its number) or ends in an area (its id, or -1 for none).
/// </summary>
public sealed record SimplexSplit(int Index, int Parent, Vector2 Normal, float Offset, int Positive, int Negative, int PositiveArea, int NegativeArea)
{
    public float Side(Vector2 point) => Vector2.Dot(Normal, point) + Offset;
}

/// <summary>A piece of the map no split divides further: its outline, the area it belongs to (-1 for none) and the split it ends.</summary>
public sealed record SimplexCell(int Area, IReadOnlyList<Vector2> Outline, int Split, bool Positive)
{
    public float Size
    {
        get
        {
            float twice = 0;
            for (int i = 0; i < Outline.Count; i++)
            {
                var (a, b) = (Outline[i], Outline[(i + 1) % Outline.Count]);
                twice += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(twice) / 2;
        }
    }

    public Vector2 Centre
    {
        get
        {
            var sum = Vector2.Zero;
            foreach (var p in Outline) sum += p;
            return Outline.Count > 0 ? sum / Outline.Count : sum;
        }
    }
}

/// <summary>
/// A simplex tree (.w3simplex, a CResourceSimplexTree): a map of a world divided by straight lines into areas, as a tree
/// of splits from the first. A world uses them for places with something of their own: where the wind is damped, where
/// water can be seen from, where occlusion is tuned. REDkit draws and edits them in its SimplexTree Editor.
/// </summary>
public sealed class SimplexTreeFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Resource { get; init; }
    public required IReadOnlyList<SimplexSplit> Splits { get; init; }

    public static bool IsSimplexTree(string path) => path.EndsWith(".w3simplex", StringComparison.OrdinalIgnoreCase);

    public static SimplexTreeFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var tree = file.First("CResourceSimplexTree") ?? throw new InvalidDataException(L.T("SimplexTreeFile.NotSimplexTree"));
        // a value left at its default isn't saved: -1 for the links, 0 for the numbers
        var splits = tree.Array("nodes").OfType<RedObject>().Select((n, i) => new SimplexSplit(i, (int)n.Integer("parent", -1),
            new Vector2(n.Float("normalX"), n.Float("normalY")), n.Float("offset"),
            (int)n.Integer("positiveStruct", -1), (int)n.Integer("negativeStruct", -1),
            (int)n.Integer("positiveID", -1), (int)n.Integer("negativeID", -1))).ToList();
        return new SimplexTreeFile { File = file, Resource = tree, Splits = splits };
    }

    /// <summary>The areas the tree names, in order.</summary>
    public IReadOnlyList<int> Areas => Splits.SelectMany(s => new[] { s.PositiveArea, s.NegativeArea }).Where(a => a >= 0).Distinct().Order().ToList();

    /// <summary>
    /// How far the splits reach from the middle of the map: past the furthest a line passes from it, so every area
    /// closes inside a square of twice this, and at least a world's 1 km.
    /// </summary>
    public float Reach => Math.Max(1024, Splits.Count == 0 ? 0 : Splits.Max(s => Math.Abs(s.Offset) / Math.Max(s.Normal.Length(), 1e-6f)) * 1.5f);

    /// <summary>The pieces of a square <paramref name="reach"/> each way from the middle, cut by every split down to its areas.</summary>
    public List<SimplexCell> Cells(float reach) => Cells(new Vector2(-reach), new Vector2(reach));

    /// <summary>The pieces of a box on the map (a world's ground), cut by every split down to its areas.</summary>
    public List<SimplexCell> Cells(Vector2 min, Vector2 max)
    {
        var cells = new List<SimplexCell>();
        if (Splits.Count == 0) return cells;
        var square = new List<Vector2> { min, new(max.X, min.Y), max, new(min.X, max.Y) };
        var stack = new Stack<(int Split, List<Vector2> Outline, int Depth)>();
        stack.Push((0, square, 0));
        while (stack.Count > 0)
        {
            var (at, outline, depth) = stack.Pop();
            var split = Splits[at];
            foreach (bool positive in new[] { true, false })
            {
                var part = Clip(outline, split, positive);
                if (part.Count < 3) continue;
                int next = positive ? split.Positive : split.Negative;
                // a loop in a damaged file would never end
                if (next >= 0 && next < Splits.Count && depth < Splits.Count) stack.Push((next, part, depth + 1));
                else cells.Add(new SimplexCell(positive ? split.PositiveArea : split.NegativeArea, part, at, positive));
            }
        }
        return cells;
    }

    /// <summary>The area a point on the map is in (-1 for none), and the splits passed on the way down to it.</summary>
    public (int Area, List<int> Path) Find(Vector2 point)
    {
        var path = new List<int>();
        int at = 0;
        while (at >= 0 && at < Splits.Count && path.Count <= Splits.Count)
        {
            path.Add(at);
            var split = Splits[at];
            bool positive = split.Side(point) >= 0;
            int next = positive ? split.Positive : split.Negative;
            if (next < 0) return (positive ? split.PositiveArea : split.NegativeArea, path);
            at = next;
        }
        return (-1, path);
    }

    /// <summary>The part of an outline on one side of a split's line.</summary>
    private static List<Vector2> Clip(List<Vector2> outline, SimplexSplit split, bool positive)
    {
        var result = new List<Vector2>(outline.Count + 1);
        float sign = positive ? 1 : -1;
        for (int i = 0; i < outline.Count; i++)
        {
            var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
            float dp = split.Side(p) * sign, dq = split.Side(q) * sign;
            if (dp >= 0) result.Add(p);
            if (dp >= 0 != dq >= 0) result.Add(p + (q - p) * (dp / (dp - dq)));
        }
        return result;
    }
}
