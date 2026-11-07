namespace W3ModWorkbench.Core.Quests;

/// <summary>Where each block goes (its top left corner), and the points a long link passes through on its way.</summary>
public sealed record QuestPlacement(
    IReadOnlyDictionary<QuestBlock, (double X, double Y)> Places,
    IReadOnlyDictionary<(QuestBlock From, QuestBlock To), List<(double X, double Y)>> Routes);

/// <summary>
/// Places a graph's blocks for drawing when the file keeps no places (a cooked file): in columns that follow the links
/// from the phase's inputs, so each block sits after the blocks that lead to it, the phase's outputs last, and each
/// column ordered to keep its blocks near the ones they link with. A link that loops back doesn't push a block on.
/// Blocks with no links (notes, leftovers) go in a row of their own after the rest.
/// </summary>
public static class QuestLayout
{
    public const double ColumnGap = 90, RowGap = 26, LooseGap = 60;

    /// <summary>
    /// Each block's top left corner. <paramref name="size"/> is a block's width and height as drawn. With
    /// <paramref name="downwards"/> the columns become rows and the graph runs top to bottom.
    /// </summary>
    public static QuestPlacement Place(IReadOnlyList<QuestBlock> blocks, Func<QuestBlock, (double W, double H)> size, bool downwards = false)
    {
        var result = new Dictionary<QuestBlock, (double X, double Y)>();
        var routes = new Dictionary<(QuestBlock From, QuestBlock To), List<(double X, double Y)>>();
        if (blocks.Count == 0) return new QuestPlacement(result, routes);
        var inGraph = blocks.ToHashSet();
        // along the flow and across it: a block's width and height, or the other way about
        (double Along, double Across) Extent(QuestBlock b) { var (w, h) = size(b); return downwards ? (h, w) : (w, h); }

        var next = blocks.ToDictionary(b => b, b => b.Links.Where(l => inGraph.Contains(l.To) && !QuestBlock.IsCut(l)).Select(l => l.To).Distinct().ToList());
        var prev = blocks.ToDictionary(b => b, _ => new List<QuestBlock>());
        foreach (var (b, list) in next) foreach (var to in list) if (!prev[to].Contains(b)) prev[to].Add(b);
        var linked = blocks.Where(b => next[b].Count > 0 || prev[b].Count > 0 || b.Links.Any(l => inGraph.Contains(l.To)) || b.Incoming.Any(l => inGraph.Contains(l.From))).ToList();
        var loose = blocks.Where(b => !linked.Contains(b)).ToList();

        // the links that loop back: found going depth first from where the graph starts
        var starts = linked.Where(b => b.IsPhaseInput || b.ClassName == "CQuestStartBlock")
            .Concat(linked.Where(b => prev[b].Count == 0)).Concat(linked).Distinct().ToList();
        var back = new HashSet<(QuestBlock, QuestBlock)>();
        var state = new Dictionary<QuestBlock, int>(); // 1 on the way, 2 done
        var discovered = new List<QuestBlock>();
        foreach (var start in starts)
        {
            if (state.ContainsKey(start)) continue;
            // without recursion: a long chain of blocks mustn't run out of stack
            var stack = new Stack<(QuestBlock Block, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            discovered.Add(start);
            while (stack.Count > 0)
            {
                var (block, i) = stack.Pop();
                if (i < next[block].Count)
                {
                    stack.Push((block, i + 1));
                    var to = next[block][i];
                    if (!state.TryGetValue(to, out int s))
                    {
                        state[to] = 1;
                        discovered.Add(to);
                        stack.Push((to, 0));
                    }
                    else if (s == 1) back.Add((block, to));
                }
                else state[block] = 2;
            }
        }

        // columns: one after the furthest block leading to it, along links that go forward
        var column = new Dictionary<QuestBlock, int>();
        var forwardIn = linked.ToDictionary(b => b, b => prev[b].Count(p => !back.Contains((p, b))));
        var ready = new Queue<QuestBlock>(discovered.Where(b => forwardIn[b] == 0));
        foreach (var b in linked) column[b] = 0;
        while (ready.Count > 0)
        {
            var b = ready.Dequeue();
            foreach (var to in next[b])
            {
                if (back.Contains((b, to))) continue;
                column[to] = Math.Max(column[to], column[b] + 1);
                if (--forwardIn[to] == 0) ready.Enqueue(to);
            }
        }
        // the phase's outputs (and a quest's end) after everything else, as REDkit draws them
        int last = linked.Where(b => !IsEnd(b)).Select(b => column[b]).DefaultIfEmpty(-1).Max() + 1;
        foreach (var b in linked.Where(IsEnd)) column[b] = Math.Max(column[b], last);

        // a link that skips columns holds a place in each column it crosses, so blocks there keep out of its way
        int columns = linked.Count == 0 ? 0 : column.Values.Max() + 1;
        var col = new Dictionary<object, int>();
        var before = new Dictionary<object, List<object>>();
        var after = new Dictionary<object, List<object>>();
        foreach (var b in linked) { col[b] = column[b]; before[b] = []; after[b] = []; }
        var order = Enumerable.Range(0, columns).Select(c => discovered.Where(b => column[b] == c).Cast<object>().ToList()).ToList();
        var chains = new Dictionary<(QuestBlock, QuestBlock), List<Through>>();
        foreach (var b in discovered)
            foreach (var to in next[b])
            {
                if (back.Contains((b, to))) continue;
                object from = b;
                var chain = new List<Through>();
                if (column[to] - column[b] > 1) chains[(b, to)] = chain;
                for (int c = column[b] + 1; c < column[to]; c++)
                {
                    var through = new Through();
                    chain.Add(through);
                    col[through] = c;
                    before[through] = [from];
                    after[through] = [];
                    after[from].Add(through);
                    order[c].Add(through);
                    from = through;
                }
                after[from].Add(to);
                before[to].Add(from);
            }
        (double Along, double Across) Size(object n) => n is QuestBlock b ? Extent(b) : (0, Through.Width);

        // the order down each column: as found, then each moved towards the middle of what it links with
        var rank = new Dictionary<object, double>();
        foreach (var list in order) for (int i = 0; i < list.Count; i++) rank[list[i]] = i;
        for (int sweep = 0; sweep < 8; sweep++)
        {
            bool forward = sweep % 2 == 0;
            for (int k = 0; k < columns; k++)
            {
                int c = forward ? k : columns - 1 - k;
                var list = order[c];
                var key = list.ToDictionary(n => n, n =>
                {
                    var near = (forward ? before[n] : after[n]).Where(m => col[m] != c).ToList();
                    return near.Count == 0 ? rank[n] : near.Average(m => rank[m]);
                });
                order[c] = [.. list.OrderBy(n => key[n]).ThenBy(n => rank[n])];
                for (int i = 0; i < order[c].Count; i++) rank[order[c][i]] = i;
            }
        }

        // where each column starts along the flow
        var start0 = new double[columns + 1];
        for (int c = 0; c < columns; c++)
            start0[c + 1] = start0[c] + (order[c].Count == 0 ? 0 : order[c].Max(n => Size(n).Along)) + ColumnGap;

        // across the flow: stacked in order, then each pulled towards what it links with, keeping the order
        var across = new Dictionary<object, double>();
        foreach (var list in order)
        {
            double y = 0;
            foreach (var n in list) { across[n] = y; y += Size(n).Across + RowGap; }
        }
        double Middle(object n) => across[n] + Size(n).Across / 2;
        for (int pass = 0; pass < 8; pass++)
        {
            bool forward = pass % 2 == 0;
            for (int k = 0; k < columns; k++)
            {
                int c = forward ? k : columns - 1 - k;
                var list = order[c];
                if (list.Count == 0) continue;
                var want = list.Select(n =>
                {
                    var near = before[n].Concat(after[n]).Where(m => col[m] != c).ToList();
                    return near.Count == 0 ? across[n] : near.Average(Middle) - Size(n).Across / 2;
                }).ToList();
                // down the column none before the end of the one above it, then back up so they sit near what they want
                var placed = new double[list.Count];
                for (int i = 0; i < list.Count; i++)
                    placed[i] = i == 0 ? want[i] : Math.Max(want[i], placed[i - 1] + Size(list[i - 1]).Across + RowGap);
                for (int i = list.Count - 2; i >= 0; i--)
                    placed[i] = Math.Min(placed[i], placed[i + 1] - Size(list[i]).Across - RowGap);
                for (int i = 0; i < list.Count; i++) across[list[i]] = placed[i];
            }
        }

        double top = linked.Count == 0 ? 0 : linked.Min(b => across[b]);
        foreach (var b in linked)
        {
            double a = start0[column[b]], x = across[b] - top;
            result[b] = downwards ? (x, a) : (a, x);
        }
        // the way each long link goes: through the middle of each column it crosses, at the place it holds there
        foreach (var (edge, chain) in chains)
            routes[edge] = chain.Select(t =>
            {
                int c = col[t];
                double a = (start0[c] + start0[c + 1] - ColumnGap) / 2, x = across[t] - top + Through.Width / 2;
                return downwards ? (x, a) : (a, x);
            }).ToList();

        // blocks with no links in a row after the rest
        double end = linked.Count == 0 ? 0 : linked.Max(b => (downwards ? result[b].X + size(b).W : result[b].Y + size(b).H)) + LooseGap;
        double along = 0;
        foreach (var b in loose)
        {
            var (w, h) = size(b);
            result[b] = downwards ? (end, along) : (along, end);
            along += (downwards ? h : w) + RowGap;
        }
        return new QuestPlacement(result, routes);
    }

    /// <summary>A place a link holds in a column it passes through.</summary>
    private sealed class Through
    {
        public const double Width = 14;
    }

    private static bool IsEnd(QuestBlock b) => b.IsPhaseOutput || b.ClassName == "CQuestEndBlock";
}
