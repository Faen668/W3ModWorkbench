using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>One property one copy of a file sets differently from another. From or To is null where the other copy has none.</summary>
public sealed record Cr2wChange(string Path, string? From, string? To)
{
    /// <summary>"CEntityTemplate #1 › name: old → new", the values cut short.</summary>
    public string Text => From is null ? $"{Path}  (+)" : To is null ? $"{Path}  (−)" : $"{Path}: {Short(From)} → {Short(To)}";

    private static string Short(string v) => v.Length > 40 ? v[..39] + "…" : v;
}

/// <summary>
/// What one copy of a CR2W file changes against another, property by property. Objects are matched by their class and
/// their place among the objects of that class (CEntityTemplate #1), not by their index, and references between them
/// (export:5) are named the same way, so a copy with one object more doesn't show every object after it as changed.
/// A list whose length changed counts as one change, not one for each item that moved.
/// </summary>
public static partial class Cr2wDiff
{
    /// <summary>The path that stands for a whole object: "CQuestPhase #1 › +", so an object added shows as one change (and one taken away as "› −").</summary>
    public const string ObjectMark = " › +";

    [GeneratedRegex(@"export:(\d+)")]
    private static partial Regex ExportRef();

    /// <summary>The properties <paramref name="to"/> sets differently from <paramref name="from"/>. Null when either can't be read.</summary>
    public static List<Cr2wChange>? Changes(byte[] from, byte[] to)
    {
        var a = Flatten(from);
        var b = Flatten(to);
        if (a is null || b is null) return null;
        // an object only one copy has is one change, not one for each of its properties
        var objectsA = a.Keys.Where(k => k.EndsWith(ObjectMark, StringComparison.Ordinal)).Select(k => k[..^ObjectMark.Length]).ToHashSet(StringComparer.Ordinal);
        var objectsB = b.Keys.Where(k => k.EndsWith(ObjectMark, StringComparison.Ordinal)).Select(k => k[..^ObjectMark.Length]).ToHashSet(StringComparer.Ordinal);
        var onlyOne = objectsA.Except(objectsB).Concat(objectsB.Except(objectsA)).ToList();
        bool InOnlyOne(string path) => onlyOne.Any(o => path.StartsWith(o + " › ", StringComparison.Ordinal) && path != o + ObjectMark);
        var changes = new List<Cr2wChange>();
        foreach (var (path, value) in a)
            if (InOnlyOne(path)) continue;
            else if (!b.TryGetValue(path, out var other))
                changes.Add(new Cr2wChange(path.EndsWith(ObjectMark, StringComparison.Ordinal) ? path[..^1] + "−" : path, value, null));
            else if (other != value) changes.Add(new Cr2wChange(path, value, other));
        foreach (var (path, value) in b)
            if (!a.ContainsKey(path) && !InOnlyOne(path)) changes.Add(new Cr2wChange(path, null, value));
        return [.. changes.OrderBy(c => c.Path, StringComparer.Ordinal)];
    }

    /// <summary>Every property of every object, by its path, with its value as JSON text. Null when the file can't be read.</summary>
    public static Dictionary<string, string>? Flatten(byte[] data)
    {
        JsonObject json;
        try { json = Cr2wJson.ToJson(Cr2wFile.Read(data), out _); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or EndOfStreamException)
        {
            return null;
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json["objects"] is not JsonArray objects) return result;

        // each object's name: a quest block's guid, or else its class and which of that class it is under its parent
        var labels = Labels(objects);
        string Name(string text) => ExportRef().Replace(text, m => int.TryParse(m.Groups[1].Value, out int n) && n >= 0 && n < labels.Length ? labels[n] : m.Value);

        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not JsonObject o) continue;
            result[labels[i] + ObjectMark] = o["$class"]?.ToJsonString() ?? "";
            foreach (var (key, value) in o)
            {
                // where an object sits follows from what holds it, which shows as a change of its own
                if (key is "$class" or "$flags" or "$crc" or "$parent") continue;
                Add(result, $"{labels[i]} › {key}", value, Name);
            }
        }
        if (json["file"]?["buffers"] is JsonArray buffers)
            for (int i = 0; i < buffers.Count; i++)
                result[$"buffer {i + 1}"] = buffers[i]?["data"]?.ToJsonString() ?? buffers[i]?.ToJsonString() ?? "";
        return result;
    }

    /// <summary>
    /// A name for each object that stays the same when another copy of the file adds or takes away objects: the guid
    /// of an object that has one (a quest graph's blocks), otherwise its parent's name and its class with its place
    /// among its parent's objects of that class.
    /// </summary>
    private static string[] Labels(JsonArray objects)
    {
        var labels = new string?[objects.Count];
        var counts = new Dictionary<(int Parent, string Class), int>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        // a guid names an object only when no other object has it (a journal path repeats the guid of the entry it points at)
        static string? Guid(JsonNode? o) => o?["guid"] is JsonValue g && g.TryGetValue(out string? guid) && guid.Length >= 8 ? guid : null;
        var unique = objects.Select(Guid).OfType<string>().GroupBy(g => g, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int Parent(JsonNode? o, int i) => o?["$parent"] is JsonValue p && p.TryGetValue(out int at) && at >= 0 && at < objects.Count && at != i ? at : -1;
        // many objects of one class under one parent (a phase's journal paths) are told apart by what they hold, as their order shifts
        var crowded = objects.Select((o, i) => (Parent: Parent(o, i), Class: o?["$class"]?.GetValue<string>() ?? "?"))
            .GroupBy(x => x).Where(g => g.Count() >= 10).Select(g => g.Key).ToHashSet();
        string Label(int i, int depth)
        {
            if (labels[i] is { } done) return done;
            var o = objects[i] as JsonObject;
            string cls = o?["$class"]?.GetValue<string>() ?? "?";
            int parent = Parent(o, i);
            string label;
            if (Guid(o) is { } guid && unique.Contains(guid)) label = $"{cls} {guid[..8]}";
            else if (crowded.Contains((parent, cls)) && o is not null)
            {
                // references are indices, which shift too
                string content = ExportRef().Replace(o.ToJsonString(), "export");
                uint hash = Checksums.Crc32(System.Text.Encoding.UTF8.GetBytes(content));
                string own = $"{cls} ‹{hash:x8}›";
                label = parent <= 0 || depth > 16 ? own : $"{Label(parent, depth + 1)} › {own}";
            }
            else
            {
                var key = (parent, cls);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                string own = $"{cls} #{counts[key]}";
                // the first object holds the rest, so what's right under it needs no prefix
                label = parent <= 0 || depth > 16 ? own : $"{Label(parent, depth + 1)} › {own}";
            }
            string first = label;
            for (int n = 2; !used.Add(label); n++) label = $"{first} ({n})";
            return labels[i] = label;
        }
        for (int i = 0; i < objects.Count; i++) Label(i, 0);
        return labels!;
    }

    /// <summary>"CEntity #1 › components, name; CMesh #2 › …": the objects changed, each with the properties changed in it.</summary>
    /// <param name="separator">What goes between objects: "; ", or a new line.</param>
    public static string Summary(IEnumerable<string> paths, int objects = 3, string separator = "; ")
    {
        var byObject = new List<(string Object, List<string> Properties)>();
        foreach (string path in paths)
        {
            int at = path.LastIndexOf(" › ", StringComparison.Ordinal);
            string obj = at < 0 ? "" : path[..at];
            string rest = at < 0 ? path : path[(at + 3)..];
            int end = rest.IndexOfAny(['.', '[']);
            string property = end < 0 ? rest : rest[..end];
            var entry = byObject.FirstOrDefault(x => x.Object == obj);
            if (entry.Properties is null) byObject.Add(entry = (obj, []));
            if (!entry.Properties.Contains(property)) entry.Properties.Add(property);
        }
        string text = string.Join(separator, byObject.Take(objects).Select(x => (x.Object.Length > 0 ? x.Object + " › " : "") + string.Join(", ", x.Properties.Take(4)) + (x.Properties.Count > 4 ? ", …" : "")));
        return byObject.Count > objects ? text + separator + "…" : text;
    }

    private static void Add(Dictionary<string, string> result, string path, JsonNode? value, Func<string, string> name)
    {
        switch (value)
        {
            case JsonObject o when o.Count > 0:
                foreach (var (key, child) in o) Add(result, $"{path}.{key}", child, name);
                break;
            case JsonArray a when a.Count > 0 && a.Any(x => x is JsonObject or JsonArray):
                // a list of structures: item by item, but only when no item was added or taken away
                result[$"{path}[]"] = a.Count.ToString();
                for (int i = 0; i < a.Count; i++) Add(result, $"{path}[{i}]", a[i], name);
                break;
            default:
                result[path] = name(value?.ToJsonString() ?? "null");
                break;
        }
    }

    /// <summary>
    /// The changes as the paths of what changed, a list whose length changed standing for the items in it: those
    /// items moved, so their own paths say nothing.
    /// </summary>
    public static HashSet<string> Touched(IEnumerable<Cr2wChange> changes)
    {
        var list = changes.ToList();
        var resized = list.Where(c => c.Path.EndsWith("[]", StringComparison.Ordinal)).Select(c => c.Path[..^2]).ToList();
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in list)
        {
            string? within = resized.FirstOrDefault(r => c.Path.StartsWith(r + "[", StringComparison.Ordinal));
            touched.Add(within ?? (c.Path.EndsWith("[]", StringComparison.Ordinal) ? c.Path[..^2] : c.Path));
        }
        return touched;
    }
}
