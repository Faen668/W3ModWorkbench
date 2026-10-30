using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Packing;

/// <summary>
/// Cooks REDkit's source resources other than textures, as REDkit's <c>wcc_lite cook</c> does, worked out by setting
/// REDkit's r4data files beside the game's cooked copies of them.
/// <para>
/// Every cooked file is version 164 with no timestamp or build number, every object carries the cook's flag (0x2000),
/// and the file keeps none of the editor's extras: no property block, no stale checksums, nothing after its data. The
/// CR2W files kept inside a resource (an entity's streamed components, its flattened copy) are written the same way,
/// but without the cook's flag.
/// </para>
/// <para>
/// An entity template loses what only REDkit's entity editor uses: where its parts sit in the editor's graph, the
/// preview entity's name, the editor's view offset and when it was last compiled. Its flattened copy
/// (<c>flatCompiledData</c>, which the game spawns the entity from) is made again from the entity and its components as
/// the template has them, editor fields and all, since REDkit's own copy can be stale (bounding boxes not filled in).
/// </para>
/// </summary>
public static class ResourceCooker
{
    private const int CookedFlag = 0x2000;

    /// <summary>Whether Build cooks a source file of this kind.</summary>
    public static bool Cooks(string gamePath) => gamePath.EndsWith(".w2mi", StringComparison.OrdinalIgnoreCase)
        || gamePath.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why Build can't cook this source file yet (an entity that includes other templates, plays effects or has
    /// appearances: the cook builds those from other files and bakes the effects' curves), or null when it can.
    /// </summary>
    public static string? CantCook(string gamePath, byte[] source)
    {
        if (!gamePath.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase)) return null;
        var file = Cr2wFile.Read(source);
        if (TextureCooker.IsCooked(file) || file.First("CEntityTemplate") is not { } template) return null;
        bool Has(string property) => template[property] is System.Collections.IList { Count: > 0 };
        return Has("includes") ? L.T("ResourceCooker.EntityIncludes")
            : Has("effects") ? L.T("ResourceCooker.EntityEffects")
            : Has("appearances") ? L.T("ResourceCooker.EntityAppearances")
            : null;
    }

    /// <summary>A REDkit source file cooked, or null when it's cooked already.</summary>
    /// <exception cref="InvalidDataException">It can't be cooked yet (see <see cref="CantCook"/>), or can't be read.</exception>
    public static byte[]? Cook(string gamePath, byte[] source)
    {
        var file = Cr2wFile.Read(source);
        if (TextureCooker.IsCooked(file)) return null;
        if (CantCook(gamePath, source) is { } why) throw new InvalidDataException(why);
        var json = Upgraded(file);
        if (gamePath.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase)) CookEntity(json);
        CookInner(json);
        Finish(json, cooked: true);
        return Write(json);
    }

    // ---------------------------------------------------------------- entities

    /// <summary>The fields only REDkit's entity editor uses, on any object.</summary>
    private static readonly string[] EditorFields = ["graphPositionX", "graphPositionY"];

    /// <summary>The template's fields only REDkit's entity editor uses.</summary>
    private static readonly string[] EditorTemplateFields = ["backgroundOffset", "dataCompilationTime", "instancePropEntries"];

    private static void CookEntity(JsonObject json)
    {
        var objects = (JsonArray)json["objects"]!;
        if (objects.Count == 0 || objects[0] is not JsonObject { } template || (string?)template["$class"] != "CEntityTemplate") return;
        int entity = ExportIndex(template["entityObject"]);
        if (entity <= 0 || entity >= objects.Count) return;

        Attachments(objects);
        // the flattened copy first, from the objects as they are, editor fields and all
        template["flatCompiledData"] = Convert.ToBase64String(Flatten(json, entity));

        foreach (string field in EditorTemplateFields) template.Remove(field);
        if (template["templateParams"] is JsonArray parameters)
        {
            for (int i = parameters.Count - 1; i >= 0; i--)
                if (parameters[i] is null) parameters.RemoveAt(i);
            if (parameters.Count == 0) template.Remove("templateParams");
        }
        // an appearance listed with no voice tag says nothing
        if (template["voicetagAppearances"] is JsonArray voicetags)
        {
            for (int i = voicetags.Count - 1; i >= 0; i--)
                if (voicetags[i] is not JsonObject { } entry || string.IsNullOrEmpty((string?)entry["voicetag"])) voicetags.RemoveAt(i);
            if (voicetags.Count == 0) template.Remove("voicetagAppearances");
        }
        ((JsonObject)objects[entity]!).Remove("name");
        foreach (var node in objects)
            if (node is JsonObject o)
                foreach (string field in EditorFields) o.Remove(field);
        SetBefore(template, "cookedEffectsVersion", 1, "$tail");
    }

    /// <summary>
    /// Each attachment's two ends, which REDkit's files leave out (marking the attachment broken) and the game's cooked
    /// files have: the component whose first list after its properties holds the attachment is its child, and the one
    /// whose second list does is its parent.
    /// </summary>
    private static void Attachments(JsonArray objects)
    {
        var parentOf = new Dictionary<int, int>();
        var childOf = new Dictionary<int, int>();
        for (int i = 0; i < objects.Count; i++)
            if (objects[i] is JsonObject o && o["$tail"] is JsonValue tail && tail.TryGetValue<string>(out var t)
                && AttachmentLists(Convert.FromBase64String(t)) is var (asChild, asParent))
            {
                foreach (int a in asChild) childOf.TryAdd(a, i);
                foreach (int a in asParent) parentOf.TryAdd(a, i);
            }
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not JsonObject o || o["isBroken"] is null || !parentOf.TryGetValue(i, out int parent) || !childOf.TryGetValue(i, out int child)) continue;
            o.Remove("isBroken");
            o["parent"] = $"export:{parent}";
            o["child"] = $"export:{child}";
            Cr2wJson.InSaveOrder(o);
        }
    }

    /// <summary>A node's two lists of attachments (objects from 0): those it's the child of, and those it's the parent of.</summary>
    private static (List<int> AsChild, List<int> AsParent)? AttachmentLists(byte[] tail)
    {
        int at = 0;
        List<int>? List()
        {
            if (at + 4 > tail.Length) return null;
            int count = BitConverter.ToInt32(tail, at);
            if (count < 0 || at + 4 + count * 4L > tail.Length) return null;
            var list = Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(tail, at + 4 + i * 4) - 1).ToList();
            at += 4 + count * 4;
            return list;
        }
        return List() is { } first && List() is { } second ? (first, second) : null;
    }

    /// <summary>
    /// The entity and the objects under it as a CR2W file of their own, numbered from the entity: the copy the game
    /// spawns the entity from. The CR2W files inside them (streamed components) go in as the source has them.
    /// </summary>
    private static byte[] Flatten(JsonObject json, int entity)
    {
        var objects = (JsonArray)json["objects"]!;
        var inside = new List<int> { entity };
        for (int i = entity + 1; i < objects.Count; i++)
            if (objects[i] is JsonObject o && o["$parent"] is { } p && inside.Contains((int)p!)) inside.Add(i);
        var renumber = inside.Select((old, at) => (old, at)).ToDictionary(x => x.old, x => x.at);

        var flat = new JsonArray();
        foreach (int old in inside)
        {
            var o = (JsonObject)objects[old]!.DeepClone();
            if (old == entity) o.Remove("$parent");
            else o["$parent"] = renumber[(int)o["$parent"]!];
            Renumber(o, renumber);
            if (o["$tail"] is JsonValue tail && tail.TryGetValue<string>(out var t))
            {
                byte[] bytes = Convert.FromBase64String(t);
                if (old == entity ? EntityTail(bytes, renumber) : ComponentTail(bytes, renumber)) o["$tail"] = Convert.ToBase64String(bytes);
            }
            flat.Add(o);
        }
        var result = new JsonObject
        {
            ["$format"] = json["$format"]!.DeepClone(), ["$formatVersion"] = json["$formatVersion"]!.DeepClone(),
            ["objects"] = flat, ["file"] = json["file"]!.DeepClone(),
        };
        Finish(result, cooked: false);
        return Write(result);
    }

    /// <summary>Changes <c>export:N</c> references to the new numbering, anywhere in a node.</summary>
    private static void Renumber(JsonNode node, Dictionary<int, int> renumber)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (key != "$parent" && o[key] is { } child)
                    {
                        if (Reference(child, renumber, out var changed)) o[key] = changed;
                        else Renumber(child, renumber);
                    }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                    if (a[i] is { } child)
                    {
                        if (Reference(child, renumber, out var changed)) a[i] = changed;
                        else Renumber(child, renumber);
                    }
                break;
        }
    }

    /// <summary>Whether a node is an <c>export:N</c> reference, and if so, the reference renumbered (null when it's to an object left out).</summary>
    private static bool Reference(JsonNode node, Dictionary<int, int> renumber, out JsonNode? changed)
    {
        changed = null;
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s) || !s.StartsWith("export:", StringComparison.Ordinal)
            || !int.TryParse(s.AsSpan(7), out int old)) return false;
        if (renumber.TryGetValue(old, out int now)) changed = JsonValue.Create($"export:{now}");
        return true;
    }

    /// <summary>An entity's data after its properties: two lists of attachments, then its components by number (from 1).</summary>
    private static bool EntityTail(byte[] tail, Dictionary<int, int> renumber)
    {
        int at = 0;
        if (!Attachments(tail, ref at, renumber) || !Attachments(tail, ref at, renumber) || at >= tail.Length) return false;
        int count = PackedInt.Read(tail, ref at);
        return Numbers(tail, at, count, renumber);
    }

    /// <summary>A component's data after its properties: its two lists of attachments.</summary>
    private static bool ComponentTail(byte[] tail, Dictionary<int, int> renumber)
    {
        int at = 0;
        return Attachments(tail, ref at, renumber) && Attachments(tail, ref at, renumber);
    }

    private static bool Attachments(byte[] tail, ref int at, Dictionary<int, int> renumber)
    {
        if (at + 4 > tail.Length) return false;
        int count = BitConverter.ToInt32(tail, at);
        at += 4;
        if (count < 0 || !Numbers(tail, at, count, renumber)) return false;
        at += count * 4;
        return true;
    }

    /// <summary>Object numbers (from 1) at a place in the bytes, changed to the new numbering.</summary>
    private static bool Numbers(byte[] bytes, int at, int count, Dictionary<int, int> renumber)
    {
        if (at + count * 4 > bytes.Length) return false;
        for (int i = 0; i < count; i++, at += 4)
        {
            int old = BitConverter.ToInt32(bytes, at) - 1;
            BitConverter.TryWriteBytes(bytes.AsSpan(at), (renumber.TryGetValue(old, out int now) ? now : -1) + 1);
        }
        return true;
    }

    private static int ExportIndex(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("export:", StringComparison.Ordinal) && int.TryParse(s.AsSpan(7), out int i) ? i : -1;

    /// <summary>Sets a property, placed before another (or last when that isn't there).</summary>
    private static void SetBefore(JsonObject o, string key, JsonNode value, string before)
    {
        o.Remove(key);
        var tail = o[before]?.DeepClone();
        o.Remove(before);
        o[key] = value;
        if (tail is not null) o[before] = tail;
    }

    // ---------------------------------------------------------------- every file

    /// <summary>A CR2W file as JSON, saved as version 164 if it's older.</summary>
    private static JsonObject Upgraded(Cr2wFile file) => Cr2wJson.UpgradeToLatest(Cr2wJson.ToJson(file, out _), out _);

    /// <summary>The CR2W files kept inside a file's properties, each written as a cooked file is (without the cook's flag).</summary>
    private static void CookInner(JsonObject json)
    {
        foreach (var node in (JsonArray)json["objects"]!)
            if (node is JsonObject o)
                foreach (var key in o.Select(p => p.Key).Where(k => k is not ("flatCompiledData" or "streamedAttachments")).ToList())
                    if (o[key] is { } value && Inner(value) is var cooked && !ReferenceEquals(cooked, value)) o[key] = cooked;
    }

    private static JsonNode Inner(JsonNode node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith("Q1IyV", StringComparison.Ordinal):
                byte[] bytes;
                try { bytes = Convert.FromBase64String(s); }
                catch (FormatException) { return node; }
                if (!Cr2wFile.IsCr2w(bytes)) return node;
                var json = Upgraded(Cr2wFile.Read(bytes));
                CookInner(json);
                Finish(json, cooked: false);
                return JsonValue.Create(Convert.ToBase64String(Write(json)));
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (o[key] is { } child && Inner(child) is var cooked && !ReferenceEquals(cooked, child)) o[key] = cooked;
                return o;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                    if (a[i] is { } child && Inner(child) is var cooked && !ReferenceEquals(cooked, child)) a[i] = cooked;
                return a;
            default:
                return node;
        }
    }

    /// <summary>What every cooked file is: the newest version, no editor extras, every object flagged as cooked (if <paramref name="cooked"/>).</summary>
    private static void Finish(JsonObject json, bool cooked)
    {
        var file = (JsonObject)json["file"]!;
        file["version"] = Cr2wFile.LastVersion;
        file["timestamp"] = 0L;
        file["build"] = 0;
        file.Remove("propertyCount");
        file.Remove("properties");
        file.Remove("trailer");
        // an older file's DataBuffer is saved as the SharedDataBuffer the engine has there now
        if (file["types"] is JsonObject types)
            foreach (var (key, type) in types.ToList())
                if ((string?)type == "DataBuffer" && key.Split('.') is [var owner, var property] && Cr2wJson.EngineType(owner, property) == "SharedDataBuffer")
                    types[key] = "SharedDataBuffer";
        foreach (var node in (JsonArray)json["objects"]!)
        {
            if (node is not JsonObject o) continue;
            o.Remove("$crc");
            Cr2wJson.InSaveOrder(o);
            if (cooked) o["$flags"] = ((int?)o["$flags"] ?? 0) | CookedFlag;
        }
    }

    /// <summary>
    /// The file written with the names and imports it uses, in the order the game's cook writes them: as they're first
    /// met going through the objects from the first, following each reference to an object it hasn't met yet before
    /// going on (see <see cref="FirstUse"/>). What it no longer uses goes.
    /// </summary>
    private static byte[] Write(JsonObject json)
    {
        var file = (JsonObject)json["file"]!;
        foreach (string table in new[] { "names", "latin1", "nameHashes", "stringOrder", "tableHeaders" }) file.Remove(table);
        var order = FirstUse(json);
        // imports nothing refers to any more go; numbered references (import#N) keep the list as it is
        if (file["imports"] is JsonArray imports && !json.ToJsonString().Contains("\"import#", StringComparison.Ordinal))
        {
            var byPath = imports.OfType<JsonObject>().GroupBy(i => (string?)i["path"] ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            // an import only soft references are to is marked so (4); any other has no flags
            file["imports"] = new JsonArray([.. order.Imports.Where(byPath.ContainsKey).Select(p =>
            {
                var import = (JsonObject)byPath[p].DeepClone();
                if (order.Soft.Contains(p)) import["flags"] = 4;
                else import.Remove("flags");
                return (JsonNode)import;
            })]);
        }
        byte[] unordered = Cr2wJson.FromJson(json);
        var used = ((JsonArray)Cr2wJson.ToJson(Cr2wFile.Read(unordered), out _)["file"]!["names"]!).Select(n => (string)n!).ToList();
        var usedSet = new HashSet<string>(used, StringComparer.Ordinal);
        var names = new List<string> { "" };
        names.AddRange(order.Names.Where(usedSet.Contains));
        names.AddRange(used.Where(n => !order.Names.Contains(n)));
        file["names"] = new JsonArray([.. names.Distinct(StringComparer.Ordinal).Select(n => (JsonNode?)n)]);
        return Cr2wJson.FromJson(json);
    }

    /// <summary>The types whose values hold no names.</summary>
    private static readonly HashSet<string> NamelessTypes = new(StringComparer.Ordinal)
    {
        "Bool", "Int8", "Int16", "Int32", "Int64", "Uint8", "Uint16", "Uint32", "Uint64", "Float", "Double", "String",
        "StringAnsi", "LocalizedString", "CGUID", "CDateTime", "EngineTransform", "EngineQsTransform", "DataBuffer",
        "SharedDataBuffer", "DeferredDataBuffer", "IdTag",
    };

    /// <summary>
    /// The names and import paths in the order the game's cook first writes them. For each object in turn: its class,
    /// then each property's name, type and the names in its value (an enum's or a CName's, a struct's fields in turn, the
    /// class of the object or import a reference is to; an object not written yet is written there and then, and the
    /// object an attachment hangs from before the attachment's own properties). The properties the cook adds to a template come after everything else, as if they were set after the
    /// rest was written.
    /// </summary>
    private static (List<string> Names, List<string> Imports, HashSet<string> Soft) FirstUse(JsonObject json)
    {
        var objects = (JsonArray)json["objects"]!;
        var file = (JsonObject)json["file"]!;
        var types = (file["types"] as JsonObject ?? []).Where(t => t.Value is JsonValue).ToDictionary(t => t.Key, t => (string)t.Value!);
        var importClass = (file["imports"] as JsonArray ?? []).OfType<JsonObject>()
            .GroupBy(i => (string?)i["path"] ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (string?)g.First()["class"] ?? "", StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var imports = new List<string>();
        var seenImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var soft = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<int>();
        void Name(string? n)
        {
            if (!string.IsNullOrEmpty(n) && seenNames.Add(n)) names.Add(n);
        }
        string? TypeOf(string owner, string property) =>
            types.TryGetValue(owner + "." + property, out var t) ? t : Cr2wJson.EngineType(owner, property);

        void Value(string? type, JsonNode? value, bool late = false)
        {
            if (value is null || type is not null && NamelessTypes.Contains(type)) return;
            if (type is null)
            {
                // a type the walk can't tell (a native type's own layout): any names it holds, in order
                switch (value)
                {
                    case JsonObject fields:
                        foreach (var (_, fieldValue) in fields) Value(null, fieldValue);
                        break;
                    case JsonArray items:
                        foreach (var item in items) Value(null, item);
                        break;
                    case JsonValue v when v.TryGetValue<string>(out var text) && !text.StartsWith("export:", StringComparison.Ordinal):
                        Name(text);
                        break;
                }
                return;
            }
            if (type.StartsWith("array:", StringComparison.Ordinal) || type.StartsWith('['))
            {
                string element = type.StartsWith('[') ? type[(type.IndexOf(']') + 1)..] : Regex.Replace(type, @"^array:(\d+,\d+,)?", "");
                if (value is JsonArray items)
                    foreach (var item in items) Value(element, item);
                return;
            }
            if (type.StartsWith("handle:", StringComparison.Ordinal) || type.StartsWith("ptr:", StringComparison.Ordinal)
                || type.StartsWith("soft:", StringComparison.Ordinal))
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var r))
                {
                    if (r.StartsWith("export:", StringComparison.Ordinal) && int.TryParse(r.AsSpan(7), out int at)
                        && at >= 0 && at < objects.Count)
                    {
                        // an object not written yet is written there and then
                        Name((string?)objects[at]?["$class"]);
                        if (!late) Visit(at, late: false);
                    }
                    else if (r.StartsWith("import:", StringComparison.Ordinal))
                    {
                        string path = r[7..];
                        if (seenImports.Add(path)) imports.Add(path);
                        (type.StartsWith("soft:", StringComparison.Ordinal) ? soft : hard).Add(path);
                        Name(importClass.GetValueOrDefault(path));
                    }
                }
                return;
            }
            switch (value)
            {
                case JsonObject fields:
                    foreach (var (field, fieldValue) in fields)
                    {
                        if (field.StartsWith('$')) continue;
                        Name(field);
                        string? fieldType = TypeOf(type, field);
                        Name(fieldType);
                        Value(fieldType, fieldValue);
                    }
                    break;
                case JsonArray flags:
                    foreach (var flag in flags)
                        if (flag is JsonValue f && f.TryGetValue<string>(out var n)) Name(n);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var text) && !NamelessTypes.Contains(type):
                    Name(text);
                    break;
            }
        }

        void Visit(int index, bool late)
        {
            if (objects[index] is not JsonObject o || !late && !visited.Add(index)) return;
            string className = (string?)o["$class"] ?? "";
            if (!late) Name(className);
            // an attachment writes the object it hangs from before its own properties
            if (!late && className.EndsWith("Attachment", StringComparison.Ordinal) && ExportIndex(o["parent"]) is var parent and >= 0 && parent < objects.Count)
            {
                Name((string?)objects[parent]?["$class"]);
                Visit(parent, late: false);
            }
            foreach (var (property, value) in o)
            {
                if (property.StartsWith('$') || CookAdded.Contains(property) != late) continue;
                Name(property);
                string? type = TypeOf(className, property);
                Name(type);
                Value(type, value);
            }
            // a material's parameters, each a name, a type and a value
            foreach (var parameter in (late ? [] : o["$parameters"] as JsonArray ?? []).OfType<JsonObject>())
            {
                Name((string?)parameter["name"]);
                string? type = (string?)parameter["type"];
                Name(type);
                Value(type, parameter["value"]);
            }
            // an entity's components and a component's attachments, listed after its properties
            if (!late && o["$tail"] is JsonValue tail && tail.TryGetValue<string>(out var t))
                foreach (int next in TailObjects(Convert.FromBase64String(t)))
                    if (next >= 0 && next < objects.Count && (int?)objects[next]?["$parent"] == index) Visit(next, late: false);
        }

        for (int i = 0; i < objects.Count; i++) Visit(i, late: false);
        for (int i = 0; i < objects.Count; i++) Visit(i, late: true);
        soft.ExceptWith(hard);
        return (names, imports, soft);
    }

    /// <summary>The objects (from 0) an entity's or a component's data after its properties lists; none for anything else.</summary>
    private static List<int> TailObjects(byte[] tail)
    {
        var found = new List<int>();
        int at = 0;
        bool List()
        {
            if (at + 4 > tail.Length) return false;
            int count = BitConverter.ToInt32(tail, at);
            if (count < 0 || at + 4 + count * 4L > tail.Length) return false;
            for (int i = 0; i < count; i++) found.Add(BitConverter.ToInt32(tail, at + 4 + i * 4) - 1);
            at += 4 + count * 4;
            return true;
        }
        if (!List() || !List()) return [];
        if (at == tail.Length) return found;
        int components = PackedInt.Read(tail, ref at);
        if (components < 0 || at + components * 4L + 2 != tail.Length) return [];
        for (int i = 0; i < components; i++) found.Add(BitConverter.ToInt32(tail, at + i * 4) - 1);
        return found;
    }

    /// <summary>The template properties the cook adds, whose names come last.</summary>
    private static readonly HashSet<string> CookAdded = new(StringComparer.Ordinal) { "flatCompiledData", "streamedAttachments", "cookedEffects", "cookedEffectsVersion" };
}
