using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench.Core.Compare;

/// <summary>How a row of a comparison differs: alike, only on the left, only on the right, or on both with different values.</summary>
public enum DiffState { Same, Removed, Added, Changed }

/// <summary>
/// A row of a comparison: an object, a property, an item of a list, or a string, with its value on each side (null
/// where that side hasn't got it) and what's inside it.
/// </summary>
public sealed class DiffNode
{
    public required string Name { get; init; }
    public string? Left { get; set; }
    public string? Right { get; set; }
    public List<DiffNode> Children { get; } = [];

    /// <summary>Whether the left side has it at all (a branch has it when any of what's in it is there).</summary>
    public bool OnLeft => Left is not null || Children.Any(c => c.OnLeft);
    public bool OnRight => Right is not null || Children.Any(c => c.OnRight);

    public DiffState State
    {
        get
        {
            if (!OnLeft) return DiffState.Added;
            if (!OnRight) return DiffState.Removed;
            if (Left != Right || Children.Any(c => c.State != DiffState.Same)) return DiffState.Changed;
            return DiffState.Same;
        }
    }

    /// <summary>The differences in it: a leaf that differs counts as one, a branch as the sum of its own.</summary>
    public int Differences => Children.Count == 0 || State is DiffState.Added or DiffState.Removed
        ? (State == DiffState.Same ? 0 : 1)
        : Children.Sum(c => c.Differences) + (Left != Right ? 1 : 0);

    internal DiffNode Child(string name)
    {
        var child = Children.FirstOrDefault(c => c.Name == name);
        if (child is null) Children.Add(child = new DiffNode { Name = name });
        return child;
    }
}

/// <summary>
/// Two versions of a file as a tree to compare: a CR2W file's objects and their properties (objects matched as
/// <see cref="Cr2wDiff"/> matches them, so a copy with an object more doesn't shift the rest), or a strings file's
/// strings by ID.
/// </summary>
public static class DiffTree
{
    /// <summary>Values longer than this are shown cut short: a buffer's data says nothing in full.</summary>
    private const int Longest = 160;

    /// <summary>Two CR2W files compared. Null when either can't be read.</summary>
    public static DiffNode? Cr2w(byte[] left, byte[] right)
    {
        var a = Cr2wDiff.Flatten(left);
        var b = Cr2wDiff.Flatten(right);
        if (a is null || b is null) return null;
        var root = new DiffNode { Name = "" };
        foreach (var (path, value) in a) Place(root, path, value, isLeft: true);
        foreach (var (path, value) in b) Place(root, path, value, isLeft: false);
        Sort(root);
        return root;
    }

    /// <summary>
    /// A flattened path into the tree: the object ("CEntityTemplate #1", or a nested one's whole label), then its
    /// property and each step into it (appearances, [2], name). A list's own entry ("appearances[]") holds its length.
    /// </summary>
    private static void Place(DiffNode root, string path, string value, bool isLeft)
    {
        int split = path.LastIndexOf(" › ", StringComparison.Ordinal);
        string objectName = split < 0 ? path : path[..split];
        string property = split < 0 ? "" : path[(split + 3)..];
        var node = root.Child(objectName);
        if (property == "+")
        {
            // the object itself: its class, to show on the side that has it
            Set(node, isLeft, Unquote(value));
            return;
        }
        bool count = property.EndsWith("[]", StringComparison.Ordinal);
        if (count) property = property[..^2];
        foreach (string step in Steps(property)) node = node.Child(step);
        Set(node, isLeft, count ? $"[{value}]" : Show(value));
    }

    private static void Set(DiffNode node, bool isLeft, string value)
    {
        if (isLeft) node.Left = value;
        else node.Right = value;
    }

    /// <summary>"appearances[2].name" as appearances, [2], name.</summary>
    private static IEnumerable<string> Steps(string property)
    {
        int start = 0;
        for (int i = 0; i < property.Length; i++)
        {
            if (property[i] == '.')
            {
                if (i > start) yield return property[start..i];
                start = i + 1;
            }
            else if (property[i] == '[')
            {
                if (i > start) yield return property[start..i];
                int close = property.IndexOf(']', i);
                if (close < 0) break;
                yield return property[i..(close + 1)];
                start = i = close;
                start++;
            }
        }
        if (start < property.Length) yield return property[start..];
    }

    private static string Show(string json)
    {
        string text = Unquote(json);
        return text.Length > Longest ? text[..Longest] + $"…  ({text.Length:N0})" : text;
    }

    private static string Unquote(string json) =>
        json.Length >= 2 && json[0] == '"' && json[^1] == '"' ? System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? json : json;

    /// <summary>Objects and properties in name order, list items in their order.</summary>
    private static void Sort(DiffNode node)
    {
        var sorted = node.Children.OrderBy(c => c.Name.StartsWith('[') ? 1 : 0)
            .ThenBy(c => c.Name.StartsWith('[') && int.TryParse(c.Name[1..^1], out int i) ? i : 0)
            .ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        node.Children.Clear();
        node.Children.AddRange(sorted);
        foreach (var child in node.Children) Sort(child);
    }

    /// <summary>Two strings files compared by ID: each string's text on either side.</summary>
    public static DiffNode Strings(IEnumerable<W3String> left, IEnumerable<W3String> right)
    {
        var root = new DiffNode { Name = "" };
        var byId = new SortedDictionary<uint, DiffNode>();
        foreach (var (s, isLeft) in left.Select(s => (s, true)).Concat(right.Select(s => (s, false))))
        {
            if (!byId.TryGetValue(s.Id, out var node)) byId[s.Id] = node = new DiffNode { Name = s.Id.ToString() };
            Set(node, isLeft, s.Text);
        }
        root.Children.AddRange(byId.Values);
        return root;
    }
}
