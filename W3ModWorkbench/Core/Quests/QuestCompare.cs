using System.Text;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Quests;

/// <summary>What happened to a block between two copies of a quest file.</summary>
[Flags]
public enum QuestChange
{
    None = 0,
    /// <summary>Only the other copy has it.</summary>
    Added = 1,
    /// <summary>Only the first copy (the game's) has it.</summary>
    Removed = 2,
    /// <summary>Its links out go elsewhere: another block, another input, another output.</summary>
    Relinked = 4,
    /// <summary>It's in another phase of the file.</summary>
    Moved = 8,
    /// <summary>Its properties differ: its name, a fact, a function's parameters…</summary>
    Changed = 16,
}

/// <summary>A block's part in a comparison: the copy of it in each file (null where it isn't), and what changed.</summary>
public sealed record QuestBlockChange(Guid Guid, QuestBlock? Before, QuestBlock? After, QuestChange Change, IReadOnlyList<string> Properties);

/// <summary>
/// Two copies of a quest file compared block by block by their GUIDs, as a mod's copy against the game's: blocks kept,
/// added, removed, moved to another phase, relinked, or with properties changed. Mods made in REDkit keep the game's
/// GUIDs, so this tells what a mod did to a quest. Blocks with no GUID (notes) aren't compared.
/// </summary>
public static class QuestCompare
{
    public static List<QuestBlockChange> Compare(QuestFile before, QuestFile after)
    {
        var a = ByGuid(before);
        var b = ByGuid(after);
        var result = new List<QuestBlockChange>();
        foreach (var (guid, old) in a)
        {
            if (!b.TryGetValue(guid, out var now))
            {
                result.Add(new QuestBlockChange(guid, old, null, QuestChange.Removed, []));
                continue;
            }
            var change = QuestChange.None;
            if (PhasePath(old) != PhasePath(now)) change |= QuestChange.Moved;
            if (!Links(old).SetEquals(Links(now))) change |= QuestChange.Relinked;
            var properties = Differences(old, before.Cr2w, now, after.Cr2w);
            if (properties.Count > 0) change |= QuestChange.Changed;
            result.Add(new QuestBlockChange(guid, old, now, change, properties));
        }
        foreach (var (guid, now) in b)
            if (!a.ContainsKey(guid)) result.Add(new QuestBlockChange(guid, null, now, QuestChange.Added, []));
        return result;
    }

    private static Dictionary<Guid, QuestBlock> ByGuid(QuestFile file)
    {
        var map = new Dictionary<Guid, QuestBlock>();
        foreach (var block in file.AllBlocks())
            if (block.Guid is { } g) map.TryAdd(g, block);
        return map;
    }

    /// <summary>The phases a block is in, by their GUIDs (a phase block keeps its GUID in both copies).</summary>
    private static string PhasePath(QuestBlock block)
    {
        var sb = new StringBuilder();
        for (var owner = block.Graph.Owner; owner is not null; owner = owner.Graph.Owner) sb.Insert(0, (owner.Guid?.ToString() ?? owner.Name) + "/");
        return sb.ToString();
    }

    /// <summary>A block's links out as output, the block led to (by GUID) and its input.</summary>
    private static HashSet<string> Links(QuestBlock block) =>
        [.. block.Links.Select(l => $"{l.Output}>{l.To.Guid?.ToString() ?? l.To.Name}>{l.Input}")];

    // what a block keeps that isn't a property of its own: the graph shows those
    private static readonly HashSet<string> NotCompared = ["cachedConnections", "sockets", "embeddedGraph", "position", "guid", "graph"];

    /// <summary>The names of a block's properties whose values differ between the copies, nested objects compared by what they hold.</summary>
    private static List<string> Differences(QuestBlock old, Cr2wFile oldFile, QuestBlock now, Cr2wFile nowFile)
    {
        var names = old.Object.Properties.Select(p => p.Name).Concat(now.Object.Properties.Select(p => p.Name)).Where(n => !NotCompared.Contains(n)).Distinct();
        var differ = new List<string>();
        foreach (string name in names)
            if (Canon(old.Object, name, oldFile, 0) != Canon(now.Object, name, nowFile, 0)) differ.Add(name);
        return differ;
    }

    private static string Canon(RedObject owner, string name, Cr2wFile file, int depth)
    {
        var property = owner.Properties.FirstOrDefault(p => p.Name == name);
        return property.Name is null ? "" : Canon(property.Value, property.Type, file, depth);
    }

    private static string ElementType(string type)
    {
        int comma = type.LastIndexOf(',');
        return comma >= 0 ? type[(comma + 1)..] : "";
    }

    /// <summary>A CVariant read as the value it holds, whose words are named by its own file's name table.</summary>
    private static string Variant(byte[] raw, Cr2wFile file, int depth) =>
        file.Variant(raw) is { } v ? v.Type + ":" + Canon(v.Value, v.Type, file, depth + 1) : Convert.ToHexString(raw);

    /// <summary>A value as text that's the same in both files when the values are: paths for references, objects by what they hold.</summary>
    private static string Canon(object? value, string type, Cr2wFile file, int depth)
    {
        if (depth > 8) return "…";
        switch (value)
        {
            case null: return "";
            case RedObject o:
                return o.ClassName + "{" + string.Join(";", o.Properties.Select(p => p.Name + "=" + Canon(p.Value, p.Type, file, depth + 1))) + "}";
            case RedHandle h:
                if (file.PathOf(h) is { } path) return "@" + path.ToLowerInvariant();
                if (!h.Soft && file.Object(h) is { } inner)
                    return inner["guid"] is Guid g && inner.ClassName.EndsWith("Block", StringComparison.Ordinal) ? "#" + g : Canon(inner, "", file, depth + 1);
                return "";
            case List<object?> list: return "[" + string.Join(",", list.Select(v => Canon(v, ElementType(type), file, depth + 1))) + "]";
            // a tag list or a variant names its words by the file's own name table, so they're compared as words
            case byte[] raw when type == "TagList": return "tags:" + string.Join(",", file.Tags(raw));
            case byte[] raw when type == "CVariant": return Variant(raw, file, depth);
            case byte[] raw: return Convert.ToHexString(raw);
            case RedEnum e: return string.Join("|", e.Values);
            case float f: return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            default: return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }
}
