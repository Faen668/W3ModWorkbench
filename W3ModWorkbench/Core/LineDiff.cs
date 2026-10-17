namespace W3ModWorkbench.Core;

public enum DiffKind : byte { Same, Removed, Added, Changed }

/// <summary>
/// Which lines differ between two versions of a file, with Myers' diff. A run of removed lines next to a run
/// of added ones is marked changed on both sides, the way a side-by-side view shows an edited block.
/// Trailing spaces and line endings don't count as differences.
/// </summary>
public static class LineDiff
{
    /// <summary>Past this many differences the middle of the files is just marked changed: they hardly match.</summary>
    private const int MaxEdits = 3000;

    /// <returns>For each line of the old file and of the new one, how it differs.</returns>
    public static (DiffKind[] Old, DiffKind[] New) Compare(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] a = [.. oldLines.Select(l => Id(ids, l))];
        int[] b = [.. newLines.Select(l => Id(ids, l))];
        var oldKinds = new DiffKind[a.Length];
        var newKinds = new DiffKind[b.Length];

        // lines the same at both ends need no searching
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

        var edits = Edits(a[start..endA], b[start..endB]);
        if (edits is null)
        {
            for (int i = start; i < endA; i++) oldKinds[i] = DiffKind.Changed;
            for (int j = start; j < endB; j++) newKinds[j] = DiffKind.Changed;
            return (oldKinds, newKinds);
        }

        // walk the edits in order, pairing up each block of removals and additions
        int x = start, y = start;
        int blockA = x, blockB = y;
        void CloseBlock()
        {
            bool both = x > blockA && y > blockB;
            for (int i = blockA; i < x; i++) oldKinds[i] = both ? DiffKind.Changed : DiffKind.Removed;
            for (int j = blockB; j < y; j++) newKinds[j] = both ? DiffKind.Changed : DiffKind.Added;
        }
        foreach (var edit in edits)
        {
            if (edit == Edit.Same)
            {
                CloseBlock();
                x++;
                y++;
                blockA = x;
                blockB = y;
            }
            else if (edit == Edit.Remove) x++;
            else y++;
        }
        CloseBlock();
        return (oldKinds, newKinds);
    }

    private enum Edit : byte { Same, Remove, Add }

    private static int Id(Dictionary<string, int> ids, string line)
    {
        line = line.TrimEnd();
        if (!ids.TryGetValue(line, out int id)) ids[line] = id = ids.Count;
        return id;
    }

    /// <summary>The shortest list of edits that turns <paramref name="a"/> into <paramref name="b"/>, or null when it would be too long.</summary>
    private static List<Edit>? Edits(int[] a, int[] b)
    {
        int n = a.Length, m = b.Length;
        int max = Math.Min(n + m, MaxEdits);
        int offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= max && found < 0; d++)
        {
            // what each diagonal reached after d - 1 edits, for walking back afterwards
            trace.Add(v[(offset - d)..(offset + d + 1)]);
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        if (found < 0) return null;

        var edits = new List<Edit>(n + m);
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var before = trace[d];
            int V(int k) => before[k + d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && V(k - 1) < V(k + 1)) ? k + 1 : k - 1;
            int prevX = V(prevK), prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { edits.Add(Edit.Same); cx--; cy--; }
            edits.Add(cx == prevX ? Edit.Add : Edit.Remove);
            cx = prevX;
            cy = prevY;
        }
        while (cx > 0 && cy > 0) { edits.Add(Edit.Same); cx--; cy--; }
        edits.Reverse();
        return edits;
    }
}
