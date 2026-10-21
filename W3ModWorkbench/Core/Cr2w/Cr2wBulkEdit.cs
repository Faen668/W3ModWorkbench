using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>What a bulk edit does to each object it reaches.</summary>
public enum BulkEditKind
{
    /// <summary>Puts the source file's value at the path in place of each target's (or adds it).</summary>
    Copy,
    /// <summary>Puts a JSON value at the path.</summary>
    Set,
    /// <summary>Removes the property at the path, so the game uses its default.</summary>
    Remove,
    /// <summary>Multiplies the numbers under the path whose property is one of the fields.</summary>
    Scale,
    /// <summary>Adds an amount to the numbers under the path whose property is one of the fields.</summary>
    Offset,
}

/// <summary>
/// One edit to make to many CR2W files at once, on their <see cref="Cr2wJson"/> form: the same tone mapping in every
/// <c>.env</c>, a value set everywhere, a property dropped, fog scaled. The path is property names from an object of
/// the file down, with <c>[n]</c> for an item of a list (<c>envParams.m_toneMapping</c>,
/// <c>envParams.m_globalFog.fogDensity.dataCurveValues[0].value</c>); it's tried on every object of the file whose class
/// is <see cref="ClassName"/> (every object, when that's empty).
/// </summary>
public sealed class Cr2wBulkEdit
{
    public required BulkEditKind Kind { get; init; }
    public required string Path { get; init; }
    public string ClassName { get; init; } = "";

    /// <summary>For Copy: the source file's JSON. Its value at the path, from its first object of the class, goes into each target.</summary>
    public JsonObject? Source { get; init; }

    /// <summary>For Set: the value.</summary>
    public JsonNode? Value { get; init; }

    /// <summary>For Scale and Offset: the factor or amount, and the properties whose numbers change (any, when empty).</summary>
    public double Amount { get; init; }
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>Whether Copy and Set add the property (and the structs on the way to it) where a target doesn't have it.</summary>
    public bool AddMissing { get; init; } = true;

    /// <summary>
    /// Makes the edit to a file's JSON. Returns how many places changed; 0 when the path wasn't found in any object
    /// of the class (or, for Copy, the source doesn't have it).
    /// </summary>
    public int Apply(JsonObject document)
    {
        var steps = ParsePath(Path);
        var objects = document["objects"] as JsonArray ?? throw new InvalidDataException(L.T("Cr2wBulkEdit.IsntConvertedCR2WFile"));
        JsonNode? copied = null;
        if (Kind == BulkEditKind.Copy)
        {
            if (Source is null) throw new InvalidOperationException(L.T("Cr2wBulkEdit.CopyNeedsSourceFile"));
            var from = SourceObject() ?? throw new InvalidDataException(ClassName.Length > 0 ? L.T("Cr2wBulkEdit.SourceHasNoClass", ClassName) : L.T("Cr2wBulkEdit.SourceHasNoObjects"));
            copied = Find(from, steps) ?? throw new InvalidDataException(L.T("Cr2wBulkEdit.SourceFileHasNothing", Path));
            MergeFileInfo(document, copied);
        }

        int changed = 0;
        foreach (var node in objects)
        {
            if (node is not JsonObject obj || obj["$data"] is not null) continue;
            if (ClassName.Length > 0 && (string?)obj["$class"] != ClassName) continue;
            switch (Kind)
            {
                case BulkEditKind.Copy:
                    if (Put(obj, steps, copied!.DeepClone())) changed++;
                    break;
                case BulkEditKind.Set:
                    if (Put(obj, steps, Value?.DeepClone())) changed++;
                    break;
                case BulkEditKind.Remove:
                    if (Remove(obj, steps)) changed++;
                    break;
                case BulkEditKind.Scale or BulkEditKind.Offset:
                    if (Find(obj, steps) is not { } under) break;
                    if (under is JsonValue) { if (Change(under) is double result && Put(obj, steps, Round(result))) changed++; }
                    else changed += Numbers(under, null, Fields.Count == 0);
                    break;
            }
        }
        return changed;
    }

    /// <summary>The source's first object of the class (or its first object that can be edited).</summary>
    public JsonObject? SourceObject() =>
        (Source?["objects"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(o => o["$data"] is null && (ClassName.Length == 0 || (string?)o["$class"] == ClassName));

    // ---------------------------------------------------------------- paths

    /// <summary>A path's steps: a property name, or a list index.</summary>
    public static List<object> ParsePath(string path)
    {
        var steps = new List<object>();
        foreach (string part in path.Split('.', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0) throw new FormatException(L.T("Cr2wBulkEdit.HasEmptyStep", path));
            string name = part;
            var indices = new List<int>();
            while (name.EndsWith(']') && name.LastIndexOf('[') is int open and > 0)
            {
                if (!int.TryParse(name[(open + 1)..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    throw new FormatException(L.T("Cr2wBulkEdit.DoesntHaveNumberBrackets", part));
                indices.Insert(0, index);
                name = name[..open];
            }
            if (name.Contains('[') || name.Contains(']')) throw new FormatException(L.T("Cr2wBulkEdit.IsntPropertyNameN", part));
            steps.Add(name);
            foreach (int index in indices) steps.Add(index);
        }
        if (steps.Count == 0) throw new FormatException(L.T("Cr2wBulkEdit.PathEmpty"));
        return steps;
    }

    public static JsonNode? Find(JsonNode? node, IReadOnlyList<object> steps)
    {
        foreach (var step in steps)
        {
            node = (node, step) switch
            {
                (JsonObject o, string name) when !name.StartsWith('$') => o[name],
                (JsonArray a, int index) when index < a.Count => a[index],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    private bool Put(JsonObject root, IReadOnlyList<object> steps, JsonNode? value)
    {
        JsonNode node = root;
        for (int i = 0; i < steps.Count - 1; i++)
        {
            JsonNode? next = (node, steps[i]) switch
            {
                (JsonObject o, string name) => o[name],
                (JsonArray a, int index) when index < a.Count => a[index],
                _ => null,
            };
            if (next is null)
            {
                // the structs on the way, added when they aren't there; a list item that isn't there can't be made up
                if (!AddMissing || node is not JsonObject parent || steps[i] is not string name || steps[i + 1] is int) return false;
                parent[name] = next = new JsonObject();
            }
            if (next is JsonObject { } raw && raw["$raw"] is not null) return false;
            node = next;
        }
        switch (node, steps[^1])
        {
            case (JsonObject o, string name):
                if (!o.ContainsKey(name) && !AddMissing) return false;
                o[name] = value;
                return true;
            case (JsonArray a, int index) when index < a.Count:
                a[index] = value;
                return true;
            default:
                return false;
        }
    }

    private static bool Remove(JsonObject root, IReadOnlyList<object> steps)
    {
        var parent = Find(root, steps.Take(steps.Count - 1).ToList()) ?? (steps.Count == 1 ? root : null);
        switch (parent, steps[^1])
        {
            case (JsonObject o, string name): return o.Remove(name);
            case (JsonArray a, int index) when index < a.Count: a.RemoveAt(index); return true;
            default: return false;
        }
    }

    /// <summary>Scales or offsets every number under a node whose property is one of the fields; how many changed.</summary>
    private int Numbers(JsonNode node, string? property, bool counts)
    {
        int changed = 0;
        switch (node)
        {
            case JsonObject o when o["$raw"] is null:
                foreach (var (key, value) in o.ToList())
                {
                    if (key.StartsWith('$') || value is null) continue;
                    bool field = counts || Fields.Contains(key, StringComparer.OrdinalIgnoreCase);
                    if (value is JsonValue && field && Change(value) is double result) { o[key] = Round(result); changed++; }
                    else if (value is not JsonValue) changed += Numbers(value, key, field);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is null) continue;
                    if (a[i] is JsonValue && counts && Change(a[i]!) is double result) { a[i] = Round(result); changed++; }
                    else if (a[i] is not JsonValue) changed += Numbers(a[i]!, property, counts);
                }
                break;
        }
        return changed;
    }

    private double? Change(JsonNode value)
    {
        if (value is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number) return null;
        // a number parsed from text, or one the converter made (a float, a long)
        double number = v.TryGetValue(out System.Text.Json.JsonElement e) ? e.GetDouble() : Convert.ToDouble(v.GetValue<object>(), CultureInfo.InvariantCulture);
        return Kind == BulkEditKind.Scale ? number * Amount : number + Amount;
    }

    /// <summary>A result written as the float it'll be stored as, so 0.1 × 3 shows as 0.3 rather than 0.30000000000000004.</summary>
    private static JsonNode Round(double value) => JsonValue.Create((float)value)!;

    // ---------------------------------------------------------------- what a copied value needs

    /// <summary>
    /// Gives the target what the copied value needs that it may not have: the types of the properties in it (from the
    /// source's list) and the files its references point at (from the source's imports).
    /// </summary>
    private void MergeFileInfo(JsonObject document, JsonNode copied)
    {
        if (Source?["file"] is not JsonObject from || document["file"] is not JsonObject to) return;
        if (from["types"] is JsonObject sourceTypes)
        {
            var types = to["types"] as JsonObject ?? (JsonObject)(to["types"] = new JsonObject());
            foreach (var (key, type) in sourceTypes)
                if (!types.ContainsKey(key)) types[key] = type?.DeepClone();
        }
        var sourceImports = (from["imports"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var imports = to["imports"] as JsonArray ?? (JsonArray)(to["imports"] = new JsonArray());
        foreach (string reference in References(copied))
        {
            string? path = null;
            JsonObject? entry = null;
            if (reference.StartsWith("import:", StringComparison.Ordinal)) path = reference[7..];
            else if (reference.StartsWith("import#", StringComparison.Ordinal)
                && int.TryParse(reference.AsSpan(7), CultureInfo.InvariantCulture, out int index) && index < sourceImports.Count)
                entry = sourceImports[index];
            entry ??= sourceImports.FirstOrDefault(i => string.Equals((string?)i["path"], path, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            string entryPath = (string?)entry["path"] ?? "";
            if (!imports.OfType<JsonObject>().Any(i => string.Equals((string?)i["path"], entryPath, StringComparison.OrdinalIgnoreCase)))
                imports.Add(entry.DeepClone());
        }
        // a reference by number means the source's numbering; by path it means the same in any file
        Renumber(copied, sourceImports);
    }

    private static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, value) in o)
                    foreach (string r in References(value)) yield return r;
                break;
            case JsonArray a:
                foreach (var value in a)
                    foreach (string r in References(value)) yield return r;
                break;
            case JsonValue v when v.TryGetValue(out string? s) && s.StartsWith("import", StringComparison.Ordinal):
                yield return s;
                break;
        }
    }

    private static void Renumber(JsonNode? node, List<JsonObject> sourceImports)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                    if (Replacement(value, sourceImports) is { } path) o[key] = path;
                    else Renumber(value, sourceImports);
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                    if (Replacement(a[i], sourceImports) is { } path) a[i] = path;
                    else Renumber(a[i], sourceImports);
                break;
        }
    }

    private static string? Replacement(JsonNode? value, List<JsonObject> sourceImports) =>
        value is JsonValue v && v.TryGetValue(out string? s) && s.StartsWith("import#", StringComparison.Ordinal)
        && int.TryParse(s.AsSpan(7), CultureInfo.InvariantCulture, out int index) && index < sourceImports.Count
            ? "import:" + (string?)sourceImports[index]["path"]
            : null;
}
