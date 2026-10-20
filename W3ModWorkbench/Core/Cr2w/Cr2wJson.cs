using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Scripts.Standalone;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>What converting a file to JSON found: how many values had to be kept as their bytes, and where.</summary>
public sealed record Cr2wJsonReport(int RawValues, int RawObjects, IReadOnlyList<string> RawPaths)
{
    public bool FullyReadable => RawValues == 0 && RawObjects == 0;
}

/// <summary>
/// Turns any CR2W file (<c>.env</c>, <c>.w2mi</c>, <c>.w2ent</c>…) into JSON that can be edited by hand or by a script,
/// and back. Nothing is lost: a file turned into JSON and back comes out byte for byte the same, and every value is
/// checked for that as it's written out. One that wouldn't come back the same is kept as its bytes instead
/// (<c>{"$raw": "base64"}</c>), so it can't be edited but still survives.
/// <para>
/// The JSON is the file's objects, in order, each one its class (<c>$class</c>) and its properties by name. Structs are
/// objects, arrays are arrays, enums are their names (a flags enum is a list of them), handles are <c>null</c>,
/// <c>"export:N"</c> or <c>"import:path"</c>. A property's engine type isn't repeated on every value: the file block
/// at the end lists the type of each class's properties (<c>"SSimpleCurve.dataCurveValues": "array:2,0,SCurveDataEntry"</c>),
/// an object lists its own in <c>$types</c> only when they differ, and a property the file never had is looked up in
/// the engine's own type list. The file block also keeps the header, names and imports, so indices stay right.
/// </para>
/// </summary>
public static partial class Cr2wJson
{
    public const string FormatName = "w3mw-cr2w-json";
    public const int FormatVersion = 1;

    private static readonly Lazy<Rtti> EngineTypes = new(Rtti.LoadBuiltIn);

    private static readonly Lazy<Dictionary<string, RttiClass>> RttiClasses = new(() =>
    {
        var classes = new Dictionary<string, RttiClass>(StringComparer.Ordinal);
        foreach (var c in EngineTypes.Value.Classes) classes.TryAdd(c.Name, c);
        return classes;
    });

    private static readonly Lazy<HashSet<string>> RttiEnums = new(() => EngineTypes.Value.Enums.Select(e => e.Name).ToHashSet(StringComparer.Ordinal));

    /// <summary>A property's type as the engine declares it, looking through the class's bases; null when unknown.</summary>
    public static string? EngineType(string className, string property)
    {
        var classes = RttiClasses.Value;
        for (int depth = 0; depth < 64 && classes.TryGetValue(className, out var c); depth++)
        {
            foreach (var (name, type) in c.Properties)
                if (name == property) return type;
            if (c.Base is null) break;
            className = c.Base;
        }
        return null;
    }

    // ---------------------------------------------------------------- to JSON

    public static string ToJsonText(Cr2wFile file, out Cr2wJsonReport report, string? source = null) =>
        Format(ToJson(file, out report, source));

    public static JsonObject ToJson(Cr2wFile file, out Cr2wJsonReport report, string? source = null)
    {
        if (file.Legacy is { } layout) return ToJsonLegacy(file, layout, out report, source);
        var d = file.Data;
        (int Offset, int Count) Table(int i) => (I32(d, 40 + i * 12), I32(d, 44 + i * 12));
        for (int i = 6; i < 10; i++)
            if (Table(i).Count > 0) throw new NotSupportedException("This file has embedded files of its own, which can't be converted yet.");

        var (fileNames, latin1) = Strings(d);
        var context = new FileContext(file.Version, fileNames, fileNames.Count == file.Names.Count
            ? file.Imports.Select((i, n) => (Text(d, I32(d, Table(2).Offset + n * 8), latin1), i.ClassName, i.Flags)).ToList()
            : throw new InvalidDataException("The names table can't be read."), adding: false);
        var decoder = new Decoder(file, context);
        var objects = new JsonArray();
        var (exportsAt, _) = Table(4);
        foreach (var export in file.Exports)
        {
            int at = exportsAt + export.Index * 24;
            int flags = U16(d, at + 2), parent = I32(d, at + 4), template = I32(d, at + 16);
            uint crc = (uint)I32(d, at + 20);
            objects.Add(ObjectNode(decoder, context, export, d, head =>
            {
                if (parent != 0) head["$parent"] = parent - 1;
                if (flags != 0) head["$flags"] = flags;
                if (template != 0) head["$template"] = template;
                uint actual = Checksums.Crc32(d.AsSpan(export.Offset, export.Size));
                if (crc != actual) head["$crc"] = new JsonArray(crc, actual);
            }));
        }

        var names = new JsonArray();
        foreach (string name in fileNames) names.Add(name);
        var imports = new JsonArray();
        foreach (var import in context.Imports)
        {
            var entry = new JsonObject { ["path"] = import.Path, ["class"] = import.ClassName };
            if (import.Flags != 0) entry["flags"] = import.Flags;
            imports.Add(entry);
        }
        var (buffersAt, bufferCount) = Table(5);
        var buffers = new JsonArray();
        for (int i = 0; i < bufferCount; i++)
        {
            int at = buffersAt + i * 24;
            int offset = I32(d, at + 8), disk = I32(d, at + 12);
            var entry = new JsonObject
            {
                ["index"] = I32(d, at + 4),
                ["flags"] = I32(d, at),
                ["diskSize"] = disk,
                ["memorySize"] = I32(d, at + 16),
                ["crc"] = (uint)I32(d, at + 20),
            };
            if (offset > 0) entry["data"] = Convert.ToBase64String(d, offset, disk);
            buffers.Add(entry);
        }
        var types = new JsonObject();
        foreach (var (key, type) in context.Types) types[key] = type;

        var (propertiesAt, propertyCount) = Table(3);
        int bufferSize = I32(d, 28);
        var fileBlock = new JsonObject
        {
            ["version"] = file.Version,
            ["flags"] = I32(d, 8),
            ["timestamp"] = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(12)),
            ["build"] = I32(d, 20),
            ["chunks"] = I32(d, 36),
            ["names"] = names,
            ["imports"] = imports,
        };
        // a name's hash when it isn't the one that would be worked out
        var hashes = new JsonObject();
        for (int i = 0; i < fileNames.Count; i++)
        {
            uint hash = (uint)I32(d, I32(d, 52) + i * 8 + 4);
            if (hash != (fileNames[i].Length == 0 ? 0 : Checksums.Fnv1a32(fileNames[i]))) hashes[i.ToString(CultureInfo.InvariantCulture)] = hash;
        }
        if (hashes.Count > 0) fileBlock["nameHashes"] = hashes;
        if (StringOrder(d, fileNames, context.Imports) is { } order) fileBlock["stringOrder"] = order;
        if (latin1.Count > 0) fileBlock["latin1"] = new JsonArray([.. latin1.Order().Select(s => (JsonNode)s)]);
        if (buffers.Count > 0) fileBlock["buffers"] = buffers;
        if (bufferSize > 0 && bufferSize < d.Length) fileBlock["trailer"] = Convert.ToBase64String(d, bufferSize, d.Length - bufferSize);
        var properties = d.AsSpan(propertiesAt, propertyCount * 16);
        if (propertyCount != 1 || properties.ContainsAnyExcept((byte)0))
        {
            fileBlock["propertyCount"] = propertyCount;
            fileBlock["properties"] = Convert.ToBase64String(properties);
        }
        if (TableQuirks(d) is { } quirks) fileBlock["tableHeaders"] = quirks;
        fileBlock["types"] = types;

        var root = new JsonObject { ["$format"] = FormatName, ["$formatVersion"] = FormatVersion };
        if (source is not null) root["$source"] = source;
        root["objects"] = objects;
        root["file"] = fileBlock;
        report = new Cr2wJsonReport(decoder.RawValues, decoder.RawObjects, decoder.RawPaths);
        return root;
    }

    /// <summary>
    /// An object as JSON: its class, then its export entry's fields that aren't the usual ones (<paramref name="entry"/>
    /// adds them), then its properties. The whole object must come back the same, its tail included; when it wouldn't,
    /// it's kept as its bytes.
    /// </summary>
    private static JsonObject ObjectNode(Decoder decoder, FileContext context, Cr2wExport export, byte[] d, Action<JsonObject> entry)
    {
        var data = d.AsSpan(export.Offset, export.Size);
        int rawValues = decoder.RawValues, rawPaths = decoder.RawPaths.Count;
        var node = decoder.Export(export, $"objects[{export.Index}]");
        bool same = SameAgain(node, data, context);
        if (context.MarkerVaries && (!same || decoder.RawValues > rawValues))
        {
            // some files of 155 to 157 leave out the zero byte objects start with: read it that way too, and keep
            // whichever comes back the same with fewer values kept as bytes
            int firstValues = decoder.RawValues;
            var firstPaths = decoder.RawPaths.GetRange(rawPaths, decoder.RawPaths.Count - rawPaths);
            decoder.RawValues = rawValues;
            decoder.RawPaths.RemoveRange(rawPaths, decoder.RawPaths.Count - rawPaths);
            decoder.OtherMarker = true;
            var other = decoder.Export(export, $"objects[{export.Index}]");
            decoder.OtherMarker = false;
            if (SameAgain(other, data, context) && (!same || decoder.RawValues < firstValues)) { node = other; same = true; }
            else
            {
                decoder.RawValues = firstValues;
                decoder.RawPaths.RemoveRange(rawPaths, decoder.RawPaths.Count - rawPaths);
                decoder.RawPaths.AddRange(firstPaths);
            }
        }
        if (!same)
        {
            node = new JsonObject { ["$class"] = export.ClassName, ["$data"] = Convert.ToBase64String(data) };
            decoder.RawValues = rawValues;
            decoder.RawPaths.RemoveRange(rawPaths, decoder.RawPaths.Count - rawPaths);
            decoder.RawObjects++;
            decoder.RawPaths.Add($"objects[{export.Index}] ({export.ClassName})");
        }
        var head = new JsonObject { ["$class"] = node["$class"]!.DeepClone() };
        entry(head);
        foreach (var (key, value) in node.ToList())
        {
            if (key == "$class") continue;
            node.Remove(key);
            head[key] = value;
        }
        return head;
    }

    /// <summary>Whether an object read as JSON comes back as the same bytes.</summary>
    private static bool SameAgain(JsonObject node, ReadOnlySpan<byte> data, FileContext context)
    {
        if (node["$data"] is not null) return false;
        try { return data.SequenceEqual(new Encoder(context).Export(node)); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// A file in the older layout (154 and before). Its objects are as a newer file's; the file block has what that
    /// layout has instead of tables: the names, the imports, the dependencies and the trailer.
    /// </summary>
    private static JsonObject ToJsonLegacy(Cr2wFile file, Cr2wLegacy.Layout layout, out Cr2wJsonReport report, string? source)
    {
        var d = file.Data;
        var context = new FileContext(file.Version, layout.Names, file.Imports.Select(i => (i.Path, i.ClassName, i.Flags)).ToList(), adding: false)
        {
            Dependencies = [.. layout.Dependencies],
        };
        var decoder = new Decoder(file, context);
        var objects = new JsonArray();
        foreach (var export in file.Exports)
        {
            var e = layout.Exports[export.Index];
            objects.Add(ObjectNode(decoder, context, export, d, head =>
            {
                if (e.Parent != 0) head["$parent"] = e.Parent - 1;
                if (e.Flags != 0) head["$flags"] = e.Flags;
                if (e.Template != 0) head["$template"] = e.Template;
                if (e.Name.Length > 0) head["$name"] = e.Name;
                if (e.Extra.AsSpan().ContainsAnyExcept((byte)0)) head["$extra"] = Convert.ToBase64String(e.Extra);
            }));
        }

        var imports = new JsonArray();
        foreach (var import in context.Imports)
        {
            var entry = new JsonObject { ["path"] = import.Path, ["class"] = import.ClassName };
            if (import.Flags != 0) entry["flags"] = import.Flags;
            imports.Add(entry);
        }
        var types = new JsonObject();
        foreach (var (key, type) in context.Types) types[key] = type;
        var fileBlock = new JsonObject
        {
            ["version"] = file.Version,
            ["flags"] = layout.Flags,
            ["names"] = new JsonArray([.. layout.Names.Select(n => (JsonNode)n)]),
            ["imports"] = imports,
        };
        if (layout.Dependencies.Count > 0) fileBlock["dependencies"] = new JsonArray([.. layout.Dependencies.Select(n => (JsonNode)n)]);
        if (layout.DependencyCount != layout.Dependencies.Count + 1) fileBlock["dependencyCount"] = layout.DependencyCount;
        if (layout.DataEnd < d.Length) fileBlock["trailer"] = Convert.ToBase64String(d, layout.DataEnd, d.Length - layout.DataEnd);
        fileBlock["types"] = types;

        var root = new JsonObject { ["$format"] = FormatName, ["$formatVersion"] = FormatVersion };
        if (source is not null) root["$source"] = source;
        root["objects"] = objects;
        root["file"] = fileBlock;
        report = new Cr2wJsonReport(decoder.RawValues, decoder.RawObjects, decoder.RawPaths);
        return root;
    }

    /// <summary>
    /// Whether a class keeps events after its properties, and how many bytes come before them: a scene section (and
    /// those derived from it), an animation's entry in its set, an animation events file, a cutscene (four bytes first).
    /// </summary>
    private static int? EventsBefore(string className) => className switch
    {
        "CSkeletalAnimationSetEntry" or "CExtAnimEventsFile" => 0,
        "CCutsceneTemplate" => 4,
        _ => IsA(className, "CStorySceneSection") ? 0 : null,
    };

    /// <summary>Whether a class is or derives from another, by the engine's classes and the game's script classes.</summary>
    internal static bool IsA(string className, string ancestor)
    {
        for (int depth = 0; depth < 64; depth++)
        {
            if (className == ancestor) return true;
            string? parent = RttiClasses.Value.TryGetValue(className, out var c) ? c.Base
                : ScriptParents.Value.TryGetValue(className, out var p) ? p : null;
            if (string.IsNullOrEmpty(parent)) return false;
            className = parent;
        }
        return false;
    }

    /// <summary>
    /// The class a script's arguments' types are kept under in the file block's types, for the classes that keep
    /// arguments after their properties (a scene's script, a quest's script block); null for any other.
    /// </summary>
    private static string? ArgumentsClass(string className) =>
        IsA(className, "CStorySceneScript") ? "CStorySceneScript.arguments"
        : IsA(className, "CQuestScriptBlock") ? "CQuestScriptBlock.arguments" : null;

    /// <summary>The size in bytes of each of the ten tables' entries; the strings table's count is its size.</summary>
    private static readonly int[] TableEntrySizes = [1, 8, 8, 16, 24, 24, 0, 0, 0, 0];

    /// <summary>
    /// The table headers that aren't what writing the file would give: an empty table with an offset or a checksum
    /// (some tools leave them), or a checksum that isn't of the table's bytes (REDkit saves a few like that). Each is
    /// kept as [offset, checksum, the checksum of the bytes]; null when there are none.
    /// </summary>
    private static JsonObject? TableQuirks(byte[] d)
    {
        var quirks = new JsonObject();
        for (int i = 0; i < 6; i++)
        {
            int offset = I32(d, 40 + i * 12), count = I32(d, 44 + i * 12);
            uint crc = (uint)I32(d, 48 + i * 12);
            uint actual = count == 0 ? 0 : Checksums.Crc32(d.AsSpan(offset, count * TableEntrySizes[i]));
            if ((count == 0 && offset != 0) || crc != actual)
                quirks[i.ToString(CultureInfo.InvariantCulture)] = new JsonArray(offset, crc, actual);
        }
        return quirks.Count > 0 ? quirks : null;
    }

    // ---------------------------------------------------------------- from JSON

    public static byte[] FromJsonText(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new InvalidDataException($"The JSON can't be read: {ex.Message}"); }
        return FromJson(node as JsonObject ?? throw new InvalidDataException("The JSON isn't an object."));
    }

    public static byte[] FromJson(JsonObject root)
    {
        if ((string?)root["$format"] != FormatName)
            throw new InvalidDataException($"This JSON wasn't made by W3 Mod Workbench's CR2W converter (\"$format\" isn't \"{FormatName}\").");
        if ((int?)root["$formatVersion"] is not int formatVersion || formatVersion > FormatVersion)
            throw new InvalidDataException("This JSON is from a newer version of W3 Mod Workbench; update to convert it.");
        var fileBlock = root["file"] as JsonObject ?? throw new InvalidDataException("The JSON has no \"file\" block.");
        var objects = root["objects"] as JsonArray ?? throw new InvalidDataException("The JSON has no \"objects\" list.");

        var names = (fileBlock["names"] as JsonArray ?? []).Select(n => (string?)n ?? "").ToList();
        if (names.Count == 0) names.Add("");
        var imports = new List<(string, string, int)>();
        foreach (var entry in fileBlock["imports"] as JsonArray ?? [])
            imports.Add(((string?)entry?["path"] ?? "", (string?)entry?["class"] ?? "", (int?)entry?["flags"] ?? 0));
        int version = (int?)fileBlock["version"] ?? 164;
        if (version is < Cr2wFile.FirstVersion or > 170)
            throw new InvalidDataException($"CR2W version {version} can't be written ({Cr2wFile.FirstVersion} to {Cr2wFile.LastVersion} can).");
        if (version <= Cr2wLegacy.LastVersion) return FromJsonLegacy(fileBlock, objects, version, names, imports);
        byte[] properties = fileBlock["properties"] is { } p ? Convert.FromBase64String((string)p!) : new byte[16];
        int propertyCount = (int?)fileBlock["propertyCount"] ?? properties.Length / 16;
        byte[] trailer = fileBlock["trailer"] is { } t ? Convert.FromBase64String((string)t!) : [];

        var builder = Cr2wBuilder.Create(version, (int?)fileBlock["flags"] ?? 0, (long?)fileBlock["timestamp"] ?? 0,
            (int?)fileBlock["build"] ?? 0, (int?)fileBlock["chunks"] ?? 6, names, imports, properties, propertyCount, trailer,
            (fileBlock["latin1"] as JsonArray ?? []).Select(n => (string?)n ?? ""),
            (fileBlock["nameHashes"] as JsonObject ?? []).Where(h => int.TryParse(h.Key, out _) && h.Value is not null)
                .ToDictionary(h => int.Parse(h.Key, CultureInfo.InvariantCulture), h => (uint)h.Value!),
            (fileBlock["stringOrder"] as JsonArray)?.Select(n => (int?)n ?? -1).ToList(),
            (fileBlock["tableHeaders"] as JsonObject ?? []).Where(h => int.TryParse(h.Key, out _) && h.Value is JsonArray { Count: 3 })
                .ToDictionary(h => int.Parse(h.Key, CultureInfo.InvariantCulture),
                    h => ((int)h.Value![0]!, (uint)h.Value![1]!, (uint)h.Value![2]!)));

        var context = new FileContext(version, names, imports, adding: true) { Builder = builder };
        foreach (var (key, type) in fileBlock["types"] as JsonObject ?? [])
            if ((string?)type is { } text) context.Types[key] = text;

        for (int i = 0; i < objects.Count; i++)
        {
            var node = objects[i] as JsonObject ?? throw new InvalidDataException($"objects[{i}] isn't an object.");
            string className = (string?)node["$class"] ?? throw new InvalidDataException($"objects[{i}] has no \"$class\".");
            byte[] data;
            try { data = new Encoder(context).Export(node); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"objects[{i}] ({className}): {ex.Message}"); }
            uint? crc = null;
            if (node["$crc"] is JsonArray { Count: 2 } kept && (uint?)kept[1] == Checksums.Crc32(data)) crc = (uint?)kept[0];
            int parent = node["$parent"] is { } at ? (int)at! + 1 : 0;
            builder.AddExport(className, (int?)node["$flags"] ?? 0, parent, (int?)node["$template"] ?? 0, data, crc);
        }
        foreach (var entry in fileBlock["buffers"] as JsonArray ?? [])
        {
            if (entry is null) continue;
            byte[]? data = entry["data"] is { } b ? Convert.FromBase64String((string)b!) : null;
            builder.AddBuffer((int?)entry["flags"] ?? 0, (int?)entry["index"] ?? 0, data, (int?)entry["diskSize"] ?? data?.Length ?? 0,
                (int?)entry["memorySize"] ?? data?.Length ?? 0, (uint?)entry["crc"] ?? 0);
        }
        return builder.Build();
    }

    /// <summary>A file in the older layout (154 and before), from the file block <see cref="ToJsonLegacy"/> makes.</summary>
    private static byte[] FromJsonLegacy(JsonObject fileBlock, JsonArray objects, int version, List<string> names, List<(string, string, int)> imports)
    {
        var dependencies = (fileBlock["dependencies"] as JsonArray ?? []).Select(n => (string?)n ?? "").ToList();
        var context = new FileContext(version, names, imports, adding: true) { Dependencies = dependencies };
        foreach (var (key, type) in fileBlock["types"] as JsonObject ?? [])
            if ((string?)type is { } text) context.Types[key] = text;

        var exports = new List<(Cr2wLegacy.Export, byte[])>();
        for (int i = 0; i < objects.Count; i++)
        {
            var node = objects[i] as JsonObject ?? throw new InvalidDataException($"objects[{i}] isn't an object.");
            string className = (string?)node["$class"] ?? throw new InvalidDataException($"objects[{i}] has no \"$class\".");
            byte[] data;
            try { data = new Encoder(context).Export(node); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"objects[{i}] ({className}): {ex.Message}"); }
            int parent = node["$parent"] is { } at ? (int)at! + 1 : 0;
            byte[] extra = node["$extra"] is { } e ? Convert.FromBase64String((string)e!) : new byte[8];
            if (extra.Length != 8) throw new InvalidDataException($"objects[{i}] ({className}): \"$extra\" should be 8 bytes.");
            exports.Add((new Cr2wLegacy.Export(context.Name(className), parent, 0, data.Length, (int?)node["$flags"] ?? 0,
                (int?)node["$template"] ?? 0, (string?)node["$name"] ?? "", extra), data));
        }
        var links = context.Imports.Select(i => new Cr2wLegacy.Link(i.Path, context.Name(i.ClassName), i.Flags)).ToList();
        int dependencyCount = (int?)fileBlock["dependencyCount"] ?? dependencies.Count + 1;
        byte[] trailer = fileBlock["trailer"] is { } t ? Convert.FromBase64String((string)t!) : [];
        return Cr2wLegacy.Write(version, (int?)fileBlock["flags"] ?? 0, context.Names, links, dependencies, dependencyCount, exports, trailer);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>The names, imports and property types both directions work with.</summary>
    private sealed class FileContext
    {
        public readonly List<string> Names;
        public readonly List<(string Path, string ClassName, int Flags)> Imports;
        public readonly Dictionary<string, string> Types = new(StringComparer.Ordinal);
        /// <summary>The newer layout's writer, which keeps the names and imports itself; null for the older layout.</summary>
        public Cr2wBuilder? Builder;
        /// <summary>Whether objects and structs start with a zero byte, and whether properties have two bytes after their type.</summary>
        public readonly bool Marker, Pad;
        /// <summary>Whether the version's objects can be either way about the zero byte (the newer layout, 155 on).</summary>
        public readonly bool MarkerVaries;
        /// <summary>The older layout's dependencies, which its soft references are to; null for the newer layout.</summary>
        public List<string>? Dependencies;
        private readonly Dictionary<string, int> _nameIndex = new(StringComparer.Ordinal);
        private readonly bool _adding;

        public FileContext(int version, IReadOnlyList<string> names, List<(string, string, int)> imports, bool adding)
        {
            Names = [.. names];
            Imports = imports;
            _adding = adding;
            Marker = Cr2wLegacy.HasObjectMarker(version);
            MarkerVaries = Cr2wLegacy.MarkerVaries(version);
            Pad = Cr2wLegacy.HasPropertyPad(version);
            for (int i = 0; i < Names.Count; i++) _nameIndex.TryAdd(Names[i], i);
        }

        public string NameAt(int index) => index >= 0 && index < Names.Count ? Names[index] : throw new InvalidDataException($"Name {index} isn't in the file.");

        public int Name(string text)
        {
            if (_nameIndex.TryGetValue(text, out int index)) return index;
            if (!_adding) throw new InvalidDataException($"The name \"{text}\" isn't in the file.");
            index = Builder?.Name(text) ?? Names.Count;
            Names.Add(text);
            _nameIndex[text] = index;
            return index;
        }

        /// <summary>A dependency's 0-based index by its path, adding it when the file doesn't have it.</summary>
        public int Dependency(string path)
        {
            int index = Dependencies!.FindIndex(d => d.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return index;
            if (!_adding) throw new InvalidDataException($"\"{path}\" isn't one of the file's dependencies.");
            Dependencies.Add(path);
            return Dependencies.Count - 1;
        }

        /// <summary>An import's 0-based index by its path, adding it (of the class the reference is to) when the file doesn't have it.</summary>
        public int Import(string path, string className)
        {
            int index = Imports.FindIndex(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return index;
            if (!_adding) throw new InvalidDataException($"\"{path}\" isn't one of the file's imports.");
            if (Builder is null) Name(className);
            index = Builder?.Import(path, className) ?? Imports.Count;
            Imports.Add((path, className, 0));
            return index;
        }
    }

    /// <summary>
    /// Properties shown by another name than the file has them. The engine saves a curve point's under names that lost
    /// their first letters (<c>me</c> for time, <c>ntrolPoint</c> for controlPoint); the JSON has the whole names.
    /// </summary>
    private static readonly Dictionary<string, (string Stored, string Shown)[]> Aliases = new(StringComparer.Ordinal)
    {
        ["SCurveDataEntry"] = [("me", "time"), ("ntrolPoint", "controlPoint"), ("lue", "value"), ("rveTypeL", "curveTypeL"), ("rveTypeR", "curveTypeR")],
    };

    private static string Shown(string className, string stored) =>
        Aliases.TryGetValue(className, out var names) && names.FirstOrDefault(n => n.Stored == stored).Shown is { } shown ? shown : stored;

    private static string Stored(string className, string shown) =>
        Aliases.TryGetValue(className, out var names) && names.FirstOrDefault(n => n.Shown == shown).Stored is { } stored ? stored : shown;

    /// <summary>The type a property of a class has: the object's own $types, then the file's list, then the engine's.</summary>
    private static string TypeOf(FileContext context, string className, string property, JsonObject? local)
    {
        if (local?["$types"]?[Shown(className, property)] is JsonValue own && (string?)own is { } ownType) return ownType;
        if (context.Types.TryGetValue(className + "." + property, out var type)) return type;
        return EngineType(className, property)
            ?? throw new InvalidDataException($"The type of {className}.{property} isn't known: add it to \"file\".\"types\" (for example \"{className}.{property}\": \"Float\").");
    }

    private static string ElementType(string type)
    {
        // "array:2,0,handle:IMaterial" → "handle:IMaterial"; "static:4,Float" → "Float"; "@*CEntity" → "*CEntity"
        if (type.StartsWith('@')) return type[1..];
        int colon = type.IndexOf(':');
        string rest = type[(colon + 1)..];
        for (int i = 0, commas = type.StartsWith("static:", StringComparison.Ordinal) ? 1 : 2; i < rest.Length; i++)
            if (rest[i] == ',' && --commas == 0) return rest[(i + 1)..];
        return rest;
    }

    /// <summary>A fixed-size array, <c>[7]Float</c>: that many values, with no count in front.</summary>
    private static bool IsFixed(string type, out string element, out int count)
    {
        element = "";
        count = 0;
        int close = type.IndexOf(']');
        if (type.Length < 3 || type[0] != '[' || close < 2 || !int.TryParse(type.AsSpan(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out count)) return false;
        element = type[(close + 1)..];
        return element.Length > 0;
    }

    // the Witcher 2's way of naming them, which files before 155 can have: @T for an array of T, *T and #T for a handle
    // to one (#T only in one file of REDkit's, a reference to a resource), ~T for a soft reference
    private static bool IsArray(string type) => type.StartsWith("array:", StringComparison.Ordinal) || type.StartsWith("static:", StringComparison.Ordinal) || type.StartsWith('@');
    private static bool IsHandle(string type) => type.StartsWith("handle:", StringComparison.Ordinal) || type.StartsWith("ptr:", StringComparison.Ordinal) || type.StartsWith('*') || type.StartsWith('#');
    private static bool IsSoft(string type) => type.StartsWith("soft:", StringComparison.Ordinal) || type.StartsWith('~');
    /// <summary>The engine's enums, and by the usual naming (E and a capital) those it doesn't list.</summary>
    private static bool IsEnum(string type) =>
        !type.Contains(':') && (RttiEnums.Value.Contains(type) || type.Length > 1 && type[0] == 'E' && char.IsUpper(type[1]) && !RttiClasses.Value.ContainsKey(type));
    private static string Target(string type) => type.StartsWith('*') || type.StartsWith('~') || type.StartsWith('#') ? type[1..] : type[(type.IndexOf(':') + 1)..];

    // ---------------------------------------------------------------- reading

    private sealed class Decoder(Cr2wFile file, FileContext context)
    {
        private readonly byte[] _d = file.Data;
        public int RawValues, RawObjects;
        public readonly List<string> RawPaths = [];

        /// <summary>
        /// Read the object the other way from its version about the zero byte before its properties: files of 155 to 157
        /// mostly don't have it, but a few objects and structs in them do (and 158 to 159 may have the odd one without).
        /// Such an object or struct says so: <c>"$marker": true</c> or <c>false</c>.
        /// </summary>
        public bool OtherMarker;

        public JsonObject Export(Cr2wExport export, string path)
        {
            int pos = export.Offset, end = export.Offset + export.Size;
            var node = new JsonObject { ["$class"] = export.ClassName };
            if (OtherMarker) node["$marker"] = !context.Marker;
            try
            {
                if (export.Size > 0) Struct(node, export.ClassName, ref pos, end, path, allowShort: true, marker: context.Marker != OtherMarker);
                if (context.Pad && export.Size > 0)
                {
                    // before 139 an object's properties end with a 0 (later versions have it before them instead)
                    if (pos < end && _d[pos] == 0) pos++;
                    else node["$end"] = false;
                }
                if (export.ClassName == "CMaterialInstance" && pos < end && Parameters(ref pos, end, path) is { } parameters)
                    node["$parameters"] = parameters;
                if (EventsBefore(export.ClassName) is int before && pos + before + 4 <= end && Events(ref pos, end, before, path) is var (events, head))
                {
                    if (head.AsSpan().ContainsAnyExcept((byte)0)) node["$eventsBefore"] = Convert.ToBase64String(head);
                    node["$events"] = events;
                }
                if (ArgumentsClass(export.ClassName) is { } argumentsClass && pos < end && Arguments(ref pos, end, argumentsClass, path) is { } arguments)
                    node["$arguments"] = arguments;
                if (pos < end) node["$tail"] = Convert.ToBase64String(_d, pos, end - pos);
                return node;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
            {
                return new JsonObject { ["$class"] = export.ClassName, ["$data"] = Convert.ToBase64String(_d, export.Offset, export.Size) };
            }
        }

        /// <summary>
        /// The events a class keeps after its properties (a scene section's, an animation's, a cutscene's), as objects:
        /// <paramref name="before"/> bytes (a cutscene has four), how many, then each its size (counting itself and the
        /// class), its class and its properties. Null, and nothing read, when they can't all be read.
        /// </summary>
        private (JsonArray Events, byte[] Before)? Events(ref int pos, int end, int before, string path)
        {
            int start = pos, rawValues = RawValues, rawPaths = RawPaths.Count;
            try
            {
                byte[] head = _d.AsSpan(pos, before).ToArray();
                pos += before;
                Need(pos, 4, end);
                int count = I32(_d, pos);
                pos += 4;
                if (count < 0 || count > 100_000) throw new InvalidDataException();
                var list = new JsonArray();
                for (int i = 0; i < count; i++)
                {
                    Need(pos, 6, end);
                    int size = I32(_d, pos), eventEnd = pos + size;
                    if (size < 6 || eventEnd > end) throw new InvalidDataException();
                    string className = context.NameAt(U16(_d, pos + 4));
                    pos += 6;
                    int eventStart = pos;
                    var node = new JsonObject { ["$class"] = className };
                    try { Struct(node, className, ref pos, eventEnd, $"{path}.$events[{i}]"); }
                    catch (Exception ex) when (context.MarkerVaries && ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                    {
                        // one saved the other way about the zero byte
                        pos = eventStart;
                        node = new JsonObject { ["$class"] = className, ["$marker"] = !context.Marker };
                        Struct(node, className, ref pos, eventEnd, $"{path}.$events[{i}]", marker: !context.Marker);
                    }
                    if (pos != eventEnd) throw new InvalidDataException();
                    list.Add(node);
                }
                return (list, head);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                pos = start;
                RawValues = rawValues;
                RawPaths.RemoveRange(rawPaths, RawPaths.Count - rawPaths);
                return null;
            }
        }

        /// <summary>
        /// A script's arguments (a scene's script, a quest's script block), which it keeps after its properties as
        /// properties are kept, (name, type, size, value) until a name of 0, without the zero byte before them in any
        /// version. Their types are under <see cref="ArgumentsClass"/> in the file block. Null, and nothing read, when
        /// they can't be read.
        /// </summary>
        private JsonObject? Arguments(ref int pos, int end, string argumentsClass, string path)
        {
            int start = pos, rawValues = RawValues, rawPaths = RawPaths.Count;
            try
            {
                var node = new JsonObject();
                Struct(node, argumentsClass, ref pos, end, path + ".$arguments", marker: false);
                return node;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                pos = start;
                RawValues = rawValues;
                RawPaths.RemoveRange(rawPaths, RawPaths.Count - rawPaths);
                return null;
            }
        }

        /// <summary>
        /// A material instance's parameters, which it keeps after its properties: how many, then each its size (counting
        /// itself, the name and the type), name, type and value. Null, and nothing read, when they can't all be read.
        /// </summary>
        private JsonArray? Parameters(ref int pos, int end, string path)
        {
            int start = pos, rawValues = RawValues, rawPaths = RawPaths.Count;
            try
            {
                Need(pos, 4, end);
                int count = I32(_d, pos);
                pos += 4;
                if (count < 0 || count > 10_000) throw new InvalidDataException();
                var list = new JsonArray();
                for (int i = 0; i < count; i++)
                {
                    Need(pos, 8, end);
                    int size = I32(_d, pos), valueEnd = pos + size;
                    string name = context.NameAt(U16(_d, pos + 4)), type = context.NameAt(U16(_d, pos + 6));
                    if (size < 8 || valueEnd > end) throw new InvalidDataException();
                    pos += 8;
                    string here = $"{path}.$parameters[{i}]";
                    int valueStart = pos;
                    JsonNode? value;
                    try
                    {
                        value = Value(type, ref pos, valueEnd, tagged: true, here);
                        if (pos != valueEnd) throw new InvalidDataException();
                        if (!_d.AsSpan(valueStart, valueEnd - valueStart).SequenceEqual(new Encoder(context).Value(type, value, tagged: true))) throw new InvalidDataException();
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException or InvalidOperationException)
                    {
                        value = new JsonObject { ["$raw"] = Convert.ToBase64String(_d, valueStart, valueEnd - valueStart) };
                        RawValues++;
                        RawPaths.Add($"{here} ({type})");
                    }
                    pos = valueEnd;
                    list.Add(new JsonObject { ["name"] = name, ["type"] = type, ["value"] = value });
                }
                return list;
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                pos = start;
                RawValues = rawValues;
                RawPaths.RemoveRange(rawPaths, RawPaths.Count - rawPaths);
                return null;
            }
        }

        /// <summary>
        /// A zero byte (from 158, or as <paramref name="marker"/> says), then (name, type, size, value) until a name of 0;
        /// before 139 there are two more bytes, always 0xFFFF, between the type and the size.
        /// </summary>
        private void Struct(JsonObject node, string className, ref int pos, int end, string path, bool allowShort = false, bool? marker = null)
        {
            if (marker ?? context.Marker)
            {
                if (pos >= end || _d[pos] != 0) throw new InvalidDataException();
                pos++;
            }
            while (true)
            {
                if (pos + 2 > end)
                {
                    if (allowShort) return; // an object can end without the closing name; its check then keeps its bytes
                    throw new InvalidDataException();
                }
                int name = U16(_d, pos);
                pos += 2;
                if (name == 0) return;
                Need(pos, 6, end);
                string stored = context.NameAt(name), property = Shown(className, stored);
                string type = context.NameAt(U16(_d, pos));
                pos += 2;
                if (context.Pad)
                {
                    Need(pos, 6, end);
                    if (U16(_d, pos) != 0xFFFF) throw new InvalidDataException();
                    pos += 2;
                }
                int size = I32(_d, pos);
                pos += 4;
                int valueEnd = pos - 4 + size;
                if (size < 4 || valueEnd > end) throw new InvalidDataException();
                if (node.ContainsKey(property)) throw new InvalidDataException(); // can't be keyed by name

                // its type, said once for the file, or on the object when it isn't the file's usual one
                string key = className + "." + stored;
                if (!context.Types.TryGetValue(key, out var usual)) context.Types[key] = type;
                else if (usual != type) ((node["$types"] ??= new JsonObject()) as JsonObject)![property] = type;

                string here = path + "." + property;
                JsonNode? value;
                int start = pos;
                try
                {
                    value = Value(type, ref pos, valueEnd, tagged: true, here);
                    if (pos != valueEnd) throw new InvalidDataException();
                    var again = new Encoder(context).Value(type, value, tagged: true);
                    if (!_d.AsSpan(start, valueEnd - start).SequenceEqual(again)) throw new InvalidDataException();
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException or InvalidOperationException)
                {
                    value = new JsonObject { ["$raw"] = Convert.ToBase64String(_d, start, valueEnd - start) };
                    RawValues++;
                    RawPaths.Add($"{here} ({type})");
                }
                pos = valueEnd;
                node[property] = value;
            }
        }

        private JsonNode? Value(string type, ref int pos, int end, bool tagged, string path)
        {
            switch (type)
            {
                // a bool is 0 or 1; the odd one saved as something else is kept as that number
                case "Bool": Need(pos, 1, end); return _d[pos++] switch { 0 => false, 1 => true, var other => (long)other };
                case "Int8": Need(pos, 1, end); return (long)(sbyte)_d[pos++];
                case "Uint8": Need(pos, 1, end); return (long)_d[pos++];
                case "Int16": Need(pos, 2, end); pos += 2; return (long)(short)U16(_d, pos - 2);
                case "Uint16": Need(pos, 2, end); pos += 2; return (long)U16(_d, pos - 2);
                case "Int32" or "Int": Need(pos, 4, end); pos += 4; return (long)I32(_d, pos - 4);
                case "Uint32" or "Uint" or "LocalizedString": Need(pos, 4, end); pos += 4; return (long)(uint)I32(_d, pos - 4);
                case "Int64": Need(pos, 8, end); pos += 8; return BinaryPrimitives.ReadInt64LittleEndian(_d.AsSpan(pos - 8));
                case "Uint64" or "CDateTime": Need(pos, 8, end); pos += 8; return BinaryPrimitives.ReadUInt64LittleEndian(_d.AsSpan(pos - 8));
                case "Float": Need(pos, 4, end); pos += 4; return Number(BinaryPrimitives.ReadSingleLittleEndian(_d.AsSpan(pos - 4)));
                case "Double": Need(pos, 8, end); pos += 8; return Number(BinaryPrimitives.ReadDoubleLittleEndian(_d.AsSpan(pos - 8)));
                case "CName": Need(pos, 2, end); pos += 2; return context.NameAt(U16(_d, pos - 2));
                case "String": return Text(ref pos, end);
                case "StringAnsi":
                {
                    // its length as a compressed number, counting the 0 it ends with, then that many 8-bit characters;
                    // an empty one is the number written as -0
                    int length = Compressed(ref pos, end, out bool negative);
                    if (negative) return length == 0 ? "" : throw new InvalidDataException();
                    Need(pos, length, end);
                    pos += length;
                    string text = Encoding.Latin1.GetString(_d, pos - length, length);
                    return text.EndsWith('\0') ? text[..^1] : text;
                }
                case "DeferredDataBuffer": Need(pos, 2, end); pos += 2; return (long)U16(_d, pos - 2);
                case "CGUID": Need(pos, 16, end); pos += 16; return new Guid(_d.AsSpan(pos - 16, 16)).ToString();
                case "TagList":
                {
                    // how many as a compressed number, then the names; an empty list is sometimes -0, which is null here
                    int count = Compressed(ref pos, end, out bool negative);
                    if (negative) return count == 0 ? null : throw new InvalidDataException();
                    var tags = new JsonArray();
                    for (int i = 0; i < count; i++) { Need(pos, 2, end); tags.Add(context.NameAt(U16(_d, pos))); pos += 2; }
                    return tags;
                }
                case "CPhysicalCollision":
                {
                    // a version (2), how many collision types as a compressed number, their names, then two 64-bit
                    // masks the engine works out from them
                    Need(pos, 4, end);
                    var collision = new JsonObject { ["version"] = (long)I32(_d, pos) };
                    pos += 4;
                    int count = Compressed(ref pos, end, out bool negative);
                    if (negative) throw new InvalidDataException();
                    var types = new JsonArray();
                    for (int i = 0; i < count; i++) { Need(pos, 2, end); types.Add(context.NameAt(U16(_d, pos))); pos += 2; }
                    collision["types"] = types;
                    Need(pos, 16, end);
                    collision["masks"] = new JsonArray(BinaryPrimitives.ReadUInt64LittleEndian(_d.AsSpan(pos)), BinaryPrimitives.ReadUInt64LittleEndian(_d.AsSpan(pos + 8)));
                    pos += 16;
                    return collision;
                }
                case "SMeshTypeResourceLODLevel":
                    // the distance the level starts at
                    Need(pos, 4, end);
                    pos += 4;
                    return Number(BinaryPrimitives.ReadSingleLittleEndian(_d.AsSpan(pos - 4)));
                case "EngineTransform":
                {
                    // a byte of which parts there are, then each as three floats
                    Need(pos, 1, end);
                    int parts = _d[pos++];
                    if ((parts & ~7) != 0) throw new InvalidDataException();
                    var transform = new JsonObject();
                    if ((parts & 1) != 0) transform["position"] = Floats(ref pos, end, "X", "Y", "Z");
                    if ((parts & 2) != 0) transform["rotation"] = Floats(ref pos, end, "Pitch", "Yaw", "Roll");
                    if ((parts & 4) != 0) transform["scale"] = Floats(ref pos, end, "X", "Y", "Z");
                    return transform;
                }
                case "IdTag":
                    Need(pos, 17, end);
                    pos += 17;
                    return new JsonObject { ["type"] = (long)_d[pos - 17], ["guid"] = new Guid(_d.AsSpan(pos - 16, 16)).ToString() };
                case "EntityHandle":
                {
                    // a byte of what kind it is, then by the kind: nothing (0), two GUIDs (1), an IdTag (2)
                    Need(pos, 1, end);
                    int kind = _d[pos++];
                    var handle = new JsonObject { ["type"] = (long)kind };
                    switch (kind)
                    {
                        case 0: break;
                        case 1:
                            Need(pos, 32, end);
                            handle["guids"] = new JsonArray(new Guid(_d.AsSpan(pos, 16)).ToString(), new Guid(_d.AsSpan(pos + 16, 16)).ToString());
                            pos += 32;
                            break;
                        case 2:
                            Need(pos, 17, end);
                            handle["idTag"] = new JsonObject { ["type"] = (long)_d[pos], ["guid"] = new Guid(_d.AsSpan(pos + 1, 16)).ToString() };
                            pos += 17;
                            break;
                        default:
                            // one this doesn't know: GUIDs to the end, when it's a property of its own
                            if (!tagged || (end - pos) % 16 != 0) throw new InvalidDataException();
                            var guids = new JsonArray();
                            while (pos < end) { guids.Add(new Guid(_d.AsSpan(pos, 16)).ToString()); pos += 16; }
                            handle["guids"] = guids;
                            break;
                    }
                    return handle;
                }
                case "EngineQsTransform":
                {
                    // a byte of which parts there are, then the position, the rotation (a quaternion) and the scale
                    Need(pos, 1, end);
                    int parts = _d[pos++];
                    if ((parts & ~7) != 0) throw new InvalidDataException();
                    var transform = new JsonObject();
                    if ((parts & 1) != 0) transform["position"] = Floats(ref pos, end, "X", "Y", "Z");
                    if ((parts & 2) != 0) transform["rotation"] = Floats(ref pos, end, "X", "Y", "Z", "W");
                    if ((parts & 4) != 0) transform["scale"] = Floats(ref pos, end, "X", "Y", "Z");
                    return transform;
                }
                case "SharedDataBuffer" or "DataBuffer":
                {
                    Need(pos, 4, end);
                    int length = I32(_d, pos);
                    pos += 4;
                    Need(pos, length, end);
                    pos += length;
                    return Convert.ToBase64String(_d, pos - length, length);
                }
                case "CVariant":
                {
                    // a value of any type: the type's name, a size that counts itself, the value
                    Need(pos, 6, end);
                    string inner = context.NameAt(U16(_d, pos));
                    int size = I32(_d, pos + 2);
                    pos += 6;
                    int valueEnd = pos - 4 + size;
                    if (size < 4 || valueEnd > end) throw new InvalidDataException();
                    // one with no type has no value
                    if (inner.Length == 0) return size == 4 ? new JsonObject { ["type"] = "", ["value"] = null } : throw new InvalidDataException();
                    var variant = new JsonObject { ["type"] = inner, ["value"] = Value(inner, ref pos, valueEnd, tagged: true, path) };
                    if (pos != valueEnd) throw new InvalidDataException();
                    return variant;
                }
            }
            if (IsHandle(type))
            {
                Need(pos, 4, end);
                int raw = I32(_d, pos);
                pos += 4;
                if (raw == 0) return null;
                if (raw > 0) return $"export:{raw - 1}";
                return ImportText(-raw - 1);
            }
            if (IsSoft(type))
            {
                Need(pos, 2, end);
                int raw = U16(_d, pos);
                pos += 2;
                return raw == 0 ? null : context.Dependencies is { } dependencies ? DependencyText(dependencies, raw - 1) : ImportText(raw - 1);
            }
            if (IsFixed(type, out string fixedElement, out int fixedCount))
            {
                // how many there are, which is the size the type says, then them
                Need(pos, 4, end);
                int count = I32(_d, pos);
                pos += 4;
                if (count != fixedCount) throw new InvalidDataException();
                var items = new JsonArray();
                for (int i = 0; i < count; i++) items.Add(Value(fixedElement, ref pos, end, tagged: false, $"{path}[{i}]"));
                return items;
            }
            if (IsArray(type))
            {
                string element = ElementType(type);
                Need(pos, 4, end);
                int count = I32(_d, pos);
                pos += 4;
                if (count < 0 || count > 50_000_000) throw new InvalidDataException();
                if (context.Pad)
                {
                    // before 139, the element type's name and 0xFFFF follow the count
                    Need(pos, 4, end);
                    if (context.NameAt(U16(_d, pos)) != element || U16(_d, pos + 2) != 0xFFFF) throw new InvalidDataException();
                    pos += 4;
                }
                if (element is "Uint8" or "Int8")
                {
                    Need(pos, count, end);
                    pos += count;
                    return Convert.ToBase64String(_d, pos - count, count);
                }
                var list = new JsonArray();
                for (int i = 0; i < count; i++) list.Add(Value(element, ref pos, end, tagged: false, $"{path}[{i}]"));
                return list;
            }
            if (IsEnum(type))
            {
                // a plain enum is one name; a flags enum, which only a tagged value can be, is names until a 0
                if (!tagged || end - pos == 2) { Need(pos, 2, end); pos += 2; return context.NameAt(U16(_d, pos - 2)); }
                var values = new JsonArray();
                while (true)
                {
                    Need(pos, 2, end);
                    int v = U16(_d, pos);
                    pos += 2;
                    if (v == 0) break;
                    values.Add(context.NameAt(v));
                }
                return values;
            }
            // an enum the scripts declare, which the engine's list doesn't have: a name, where a struct couldn't be
            if (tagged && end - pos == 2 && U16(_d, pos) != 0 && !RttiClasses.Value.ContainsKey(type)) { pos += 2; return context.NameAt(U16(_d, pos - 2)); }
            var node = new JsonObject();
            int start = pos, rawValues = RawValues, rawPaths = RawPaths.Count;
            try { Struct(node, type, ref pos, end, path); }
            catch (Exception ex) when (context.MarkerVaries && ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // one saved the other way about the zero byte (155 to 157 mostly lack it, but some objects have it)
                pos = start;
                RawValues = rawValues;
                RawPaths.RemoveRange(rawPaths, RawPaths.Count - rawPaths);
                node = new JsonObject { ["$marker"] = !context.Marker };
                Struct(node, type, ref pos, end, path, marker: !context.Marker);
            }
            if (type == "SAppearanceAttachment") node["$attachment"] = Attachment(ref pos, end, path + ".$attachment");
            return node;
        }

        /// <summary>
        /// A compressed number: the first byte has the sign (0x80), whether more bytes follow (0x40) and the low six
        /// bits; each byte after has seven more bits and 0x80 when another follows.
        /// </summary>
        private int Compressed(ref int pos, int end, out bool negative)
        {
            Need(pos, 1, end);
            int first = _d[pos++];
            negative = (first & 0x80) != 0;
            int value = first & 0x3F;
            if ((first & 0x40) != 0)
            {
                int shift = 6, b;
                do
                {
                    Need(pos, 1, end);
                    b = _d[pos++];
                    value |= (b & 0x7F) << shift;
                    shift += 7;
                } while ((b & 0x80) != 0 && shift < 32);
            }
            return value;
        }

        /// <summary>
        /// The attachment an entity appearance's SAppearanceAttachment keeps after its properties: a size that counts
        /// itself, a 1, the attachment's class and its properties (a CAnimatedAttachment and the slot it goes on).
        /// </summary>
        private JsonObject Attachment(ref int pos, int end, string path)
        {
            Need(pos, 7, end);
            int size = I32(_d, pos), objectEnd = pos + size;
            if (size < 7 || objectEnd > end || _d[pos + 4] != 1) throw new InvalidDataException();
            string className = context.NameAt(U16(_d, pos + 5));
            pos += 7;
            var attachment = new JsonObject { ["$class"] = className };
            int start = pos, rawValues = RawValues, rawPaths = RawPaths.Count;
            try
            {
                Struct(attachment, className, ref pos, objectEnd, path);
                if (pos != objectEnd) throw new InvalidDataException();
            }
            catch (Exception ex) when (context.MarkerVaries && ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // one saved the other way about the zero byte (155 to 157 mostly lack it, but some objects have it)
                pos = start;
                RawValues = rawValues;
                RawPaths.RemoveRange(rawPaths, RawPaths.Count - rawPaths);
                attachment = new JsonObject { ["$class"] = className, ["$marker"] = !context.Marker };
                Struct(attachment, className, ref pos, objectEnd, path, marker: !context.Marker);
                if (pos != objectEnd) throw new InvalidDataException();
            }
            return attachment;
        }

        private JsonObject Floats(ref int pos, int end, params string[] names)
        {
            var result = new JsonObject();
            foreach (string name in names)
            {
                Need(pos, 4, end);
                result[name] = Number(BinaryPrimitives.ReadSingleLittleEndian(_d.AsSpan(pos)));
                pos += 4;
            }
            return result;
        }

        /// <summary>A soft reference in the older layout, which is to one of the file's dependencies.</summary>
        private static string DependencyText(List<string> dependencies, int index)
        {
            if (index < 0 || index >= dependencies.Count) throw new InvalidDataException();
            int first = dependencies.FindIndex(d => d.Equals(dependencies[index], StringComparison.OrdinalIgnoreCase));
            return first == index ? "import:" + dependencies[index] : $"import#{index}";
        }

        private string ImportText(int index)
        {
            if (index < 0 || index >= context.Imports.Count) throw new InvalidDataException();
            string importPath = context.Imports[index].Path;
            // by path when that finds this import, else by number
            int first = context.Imports.FindIndex(i => i.Path.Equals(importPath, StringComparison.OrdinalIgnoreCase));
            return first == index ? "import:" + importPath : $"import#{index}";
        }

        private string Text(ref int pos, int end)
        {
            Need(pos, 1, end);
            int first = _d[pos++];
            bool narrow = (first & 0x80) != 0;
            int length = first & 0x3F;
            if ((first & 0x40) != 0)
            {
                int shift = 6, b;
                do
                {
                    Need(pos, 1, end);
                    b = _d[pos++];
                    length |= (b & 0x7F) << shift;
                    shift += 7;
                } while ((b & 0x80) != 0 && shift < 32);
            }
            int bytes = narrow ? length : length * 2;
            Need(pos, bytes, end);
            pos += bytes;
            return narrow ? Encoding.Latin1.GetString(_d, pos - bytes, bytes) : Encoding.Unicode.GetString(_d, pos - bytes, bytes);
        }

        private void Need(int pos, int count, int end)
        {
            if (count < 0 || pos + count > end || pos + count > _d.Length) throw new InvalidDataException();
        }
    }

    /// <summary>
    /// A float as a JSON number, or as a string for the values JSON has no number for. A NaN that isn't the usual one
    /// keeps its bits: <c>"NaN:ffffffff"</c> (some files have them, where a value was never set).
    /// </summary>
    private static JsonNode Number(double value)
    {
        if (double.IsNaN(value))
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            return bits == BitConverter.DoubleToInt64Bits(double.NaN) ? "NaN" : $"NaN:{bits:x16}";
        }
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        return JsonValue.Create(value)!;
    }

    private static JsonNode Number(float value)
    {
        if (float.IsNaN(value))
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            return bits == BitConverter.SingleToInt32Bits(float.NaN) ? "NaN" : $"NaN:{bits:x8}";
        }
        if (float.IsInfinity(value)) return Number((double)value);
        return JsonValue.Create(value)!;
    }

    // ---------------------------------------------------------------- writing

    private sealed class Encoder(FileContext context)
    {
        private readonly MemoryStream _out = new();

        public byte[] Export(JsonObject node)
        {
            if (node["$data"] is { } data) return Convert.FromBase64String((string)data!);
            string className = (string?)node["$class"] ?? throw new InvalidDataException("An object has no \"$class\".");
            Struct(className, node);
            if (context.Pad && !(node["$end"] is JsonValue end && end.TryGetValue(out bool has) && !has)) _out.WriteByte(0);
            if (node["$parameters"] is JsonArray parameters)
            {
                I32(parameters.Count);
                for (int i = 0; i < parameters.Count; i++)
                {
                    var parameter = parameters[i] as JsonObject ?? throw new InvalidDataException($"$parameters[{i}] isn't an object.");
                    string type = Str(parameter["type"]);
                    long sizeAt = _out.Position;
                    I32(0);
                    U16(context.Name(Str(parameter["name"])));
                    U16(context.Name(type));
                    Write(type, parameter["value"], tagged: true, $"$parameters[{i}]");
                    long after = _out.Position;
                    _out.Position = sizeAt;
                    I32((int)(after - sizeAt));
                    _out.Position = after;
                }
            }
            if (node["$arguments"] is JsonObject arguments) Struct(ArgumentsClass(className) ?? throw new InvalidDataException($"A {className} has no arguments."), arguments, marker: false);
            if (node["$events"] is JsonArray events)
            {
                byte[] before = node["$eventsBefore"] is JsonValue b ? Convert.FromBase64String((string)b!) : new byte[EventsBefore(className) ?? 0];
                _out.Write(before);
                I32(events.Count);
                for (int i = 0; i < events.Count; i++)
                {
                    var item = events[i] as JsonObject ?? throw new InvalidDataException($"$events[{i}] isn't an object.");
                    string eventClass = Str(item["$class"]);
                    long sizeAt = _out.Position;
                    I32(0);
                    U16(context.Name(eventClass));
                    Struct(eventClass, item);
                    long after = _out.Position;
                    _out.Position = sizeAt;
                    I32((int)(after - sizeAt));
                    _out.Position = after;
                }
            }
            if (node["$tail"] is { } tail) _out.Write(Convert.FromBase64String((string)tail!));
            return _out.ToArray();
        }

        public byte[] Value(string type, JsonNode? value, bool tagged)
        {
            Write(type, value, tagged, type);
            return _out.ToArray();
        }

        private void Struct(string className, JsonObject node, bool? marker = null)
        {
            if (marker ?? (node["$marker"] is JsonValue m && m.TryGetValue(out bool has) ? has : context.Marker)) _out.WriteByte(0);
            foreach (var (property, value) in node)
            {
                if (property.StartsWith('$')) continue;
                string stored = Stored(className, property);
                string type = TypeOf(context, className, stored, node);
                U16(context.Name(stored));
                U16(context.Name(type));
                if (context.Pad) U16(0xFFFF);
                long sizeAt = _out.Position;
                I32(0);
                Write(type, value, tagged: true, $"{className}.{property}");
                long after = _out.Position;
                _out.Position = sizeAt;
                I32((int)(after - sizeAt));
                _out.Position = after;
            }
            U16(0);
        }

        private void Write(string type, JsonNode? value, bool tagged, string where)
        {
            if (value is JsonObject raw && raw["$raw"] is { } bytes)
            {
                _out.Write(Convert.FromBase64String((string)bytes!));
                return;
            }
            try { WriteValue(type, value, tagged, where); }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
            {
                throw new InvalidDataException($"{where} should be a {Describe(type)}, not {Show(value)}.");
            }
        }

        private void WriteValue(string type, JsonNode? value, bool tagged, string where)
        {
            switch (type)
            {
                case "Bool": _out.WriteByte(Held(value) is bool or JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } ? (Truth(value) ? (byte)1 : (byte)0) : checked((byte)Integer(value))); return;
                case "Int8": _out.WriteByte((byte)checked((sbyte)Integer(value))); return;
                case "Uint8": _out.WriteByte(checked((byte)Integer(value))); return;
                case "Int16": U16((ushort)checked((short)Integer(value))); return;
                case "Uint16": U16(checked((ushort)Integer(value))); return;
                case "Int32" or "Int": I32(checked((int)Integer(value))); return;
                case "Uint32" or "Uint" or "LocalizedString": I32((int)checked((uint)Integer(value))); return;
                case "Int64": Span8(s => BinaryPrimitives.WriteInt64LittleEndian(s, checked((long)Integer(value)))); return;
                case "Uint64" or "CDateTime": Span8(s => BinaryPrimitives.WriteUInt64LittleEndian(s, checked((ulong)Integer(value)))); return;
                case "Float":
                {
                    Span<byte> s = stackalloc byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(s, Single(value));
                    _out.Write(s);
                    return;
                }
                case "Double": Span8(s => BinaryPrimitives.WriteDoubleLittleEndian(s, Real(value))); return;
                case "CName": U16(context.Name(Str(value))); return;
                case "String": Text(Str(value)); return;
                case "StringAnsi":
                {
                    string text = value is null ? "" : Str(value);
                    if (text.Length == 0) { Compressed(0, negative: true); return; }
                    byte[] bytes = Encoding.Latin1.GetBytes(text.EndsWith('\0') ? text : text + "\0");
                    Compressed(bytes.Length, negative: false);
                    _out.Write(bytes);
                    return;
                }
                case "DeferredDataBuffer": U16(checked((ushort)Integer(value))); return;
                case "CGUID": _out.Write(Guid.Parse(Str(value)).ToByteArray()); return;
                case "TagList":
                {
                    if (value is null) { Compressed(0, negative: true); return; }
                    var tags = value as JsonArray ?? throw new FormatException();
                    Compressed(tags.Count, negative: false);
                    foreach (var tag in tags) U16(context.Name(Str(tag)));
                    return;
                }
                case "CPhysicalCollision":
                {
                    var collision = value as JsonObject ?? throw new FormatException();
                    I32(checked((int)Integer(collision["version"] ?? 2)));
                    var types = collision["types"] as JsonArray ?? [];
                    Compressed(types.Count, negative: false);
                    foreach (var type2 in types) U16(context.Name(Str(type2)));
                    var masks = collision["masks"] as JsonArray ?? throw new FormatException();
                    if (masks.Count != 2) throw new FormatException();
                    Span8(s2 => BinaryPrimitives.WriteUInt64LittleEndian(s2, checked((ulong)Integer(masks[0]))));
                    Span8(s2 => BinaryPrimitives.WriteUInt64LittleEndian(s2, checked((ulong)Integer(masks[1]))));
                    return;
                }
                case "SMeshTypeResourceLODLevel":
                {
                    Span<byte> s = stackalloc byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(s, Single(value));
                    _out.Write(s);
                    return;
                }
                case "EngineTransform":
                {
                    var transform = value as JsonObject ?? throw new FormatException();
                    var position = transform["position"] as JsonObject;
                    var rotation = transform["rotation"] as JsonObject;
                    var scale = transform["scale"] as JsonObject;
                    _out.WriteByte((byte)((position is null ? 0 : 1) | (rotation is null ? 0 : 2) | (scale is null ? 0 : 4)));
                    if (position is not null) Floats(position, "X", "Y", "Z");
                    if (rotation is not null) Floats(rotation, "Pitch", "Yaw", "Roll");
                    if (scale is not null) Floats(scale, "X", "Y", "Z");
                    return;
                }
                case "IdTag":
                    _out.WriteByte(checked((byte)Integer(value?["type"])));
                    _out.Write(Guid.Parse(Str(value?["guid"])).ToByteArray());
                    return;
                case "EntityHandle":
                    _out.WriteByte(checked((byte)Integer(value?["type"])));
                    if (value?["idTag"] is JsonObject idTag)
                    {
                        _out.WriteByte(checked((byte)Integer(idTag["type"])));
                        _out.Write(Guid.Parse(Str(idTag["guid"])).ToByteArray());
                    }
                    foreach (var guid in value?["guids"] as JsonArray ?? []) _out.Write(Guid.Parse(Str(guid)).ToByteArray());
                    return;
                case "EngineQsTransform":
                {
                    var transform = value as JsonObject ?? throw new FormatException();
                    var position = transform["position"] as JsonObject;
                    var rotation = transform["rotation"] as JsonObject;
                    var scale = transform["scale"] as JsonObject;
                    _out.WriteByte((byte)((position is null ? 0 : 1) | (rotation is null ? 0 : 2) | (scale is null ? 0 : 4)));
                    if (position is not null) Floats(position, "X", "Y", "Z");
                    if (rotation is not null) Floats(rotation, "X", "Y", "Z", "W");
                    if (scale is not null) Floats(scale, "X", "Y", "Z");
                    return;
                }
                case "SharedDataBuffer" or "DataBuffer":
                {
                    byte[] bytes = Convert.FromBase64String(Str(value));
                    I32(bytes.Length);
                    _out.Write(bytes);
                    return;
                }
                case "CVariant":
                {
                    string inner = Str(value?["type"]);
                    U16(context.Name(inner));
                    long sizeAt = _out.Position;
                    I32(0);
                    if (inner.Length > 0) Write(inner, value?["value"], tagged: true, where + ".value");
                    long after = _out.Position;
                    _out.Position = sizeAt;
                    I32((int)(after - sizeAt));
                    _out.Position = after;
                    return;
                }
            }
            if (IsHandle(type)) { I32(Reference(Target(type), value, soft: false)); return; }
            if (IsSoft(type)) { U16(checked((ushort)Reference(Target(type), value, soft: true))); return; }
            if (IsFixed(type, out string fixedElement, out int fixedCount))
            {
                var items = value as JsonArray ?? throw new FormatException();
                if (items.Count != fixedCount) throw new FormatException();
                I32(items.Count);
                for (int i = 0; i < items.Count; i++) Write(fixedElement, items[i], tagged: false, $"{where}[{i}]");
                return;
            }
            if (IsArray(type))
            {
                string element = ElementType(type);
                if (element is "Uint8" or "Int8")
                {
                    byte[] bytes = Convert.FromBase64String(Str(value));
                    I32(bytes.Length);
                    ArrayPad(element);
                    _out.Write(bytes);
                    return;
                }
                var list = value as JsonArray ?? throw new FormatException();
                I32(list.Count);
                ArrayPad(element);
                for (int i = 0; i < list.Count; i++) Write(element, list[i], tagged: false, $"{where}[{i}]");
                return;
            }
            if (IsEnum(type))
            {
                if (value is JsonArray flags && tagged)
                {
                    foreach (var flag in flags) U16(context.Name(Str(flag)));
                    U16(0);
                    return;
                }
                U16(context.Name(Str(value)));
                return;
            }
            // an enum the scripts declare, which the engine's list doesn't have
            if (value is JsonValue && tagged && !RttiClasses.Value.ContainsKey(type)) { U16(context.Name(Str(value))); return; }
            var fields = value as JsonObject ?? throw new FormatException();
            Struct(type, fields);
            if (type == "SAppearanceAttachment") Attachment(fields["$attachment"] as JsonObject ?? throw new FormatException(), where + ".$attachment");
        }

        private void Attachment(JsonObject attachment, string where)
        {
            string className = Str(attachment["$class"]);
            long sizeAt = _out.Position;
            I32(0);
            _out.WriteByte(1);
            U16(context.Name(className));
            Struct(className, attachment);
            long after = _out.Position;
            _out.Position = sizeAt;
            I32((int)(after - sizeAt));
            _out.Position = after;
        }

        private void Floats(JsonObject node, params string[] names)
        {
            Span<byte> s = stackalloc byte[4];
            foreach (string name in names)
            {
                BinaryPrimitives.WriteSingleLittleEndian(s, node[name] is null ? 0 : Single(node[name]));
                _out.Write(s);
            }
        }

        /// <summary>A compressed number: sign, more-follows and six bits in the first byte, then seven bits a byte.</summary>
        private void Compressed(int value, bool negative)
        {
            int first = (negative ? 0x80 : 0) | (value & 0x3F);
            if (value > 0x3F) first |= 0x40;
            _out.WriteByte((byte)first);
            for (int rest = value >> 6; rest > 0; rest >>= 7)
                _out.WriteByte((byte)((rest & 0x7F) | (rest > 0x7F ? 0x80 : 0)));
        }

        /// <summary>Before 139, an array's count is followed by its element type's name and 0xFFFF.</summary>
        private void ArrayPad(string element)
        {
            if (!context.Pad) return;
            U16(context.Name(element));
            U16(0xFFFF);
        }

        /// <summary>null, "export:N" (0-based), "import:path" or "import#N" as a handle's value. A soft reference in the
        /// older layout is to one of the file's dependencies.</summary>
        private int Reference(string targetClass, JsonNode? value, bool soft)
        {
            if (value is null) return 0;
            string text = Str(value);
            if (text.StartsWith("export:", StringComparison.Ordinal) && !soft)
                return int.Parse(text.AsSpan(7), CultureInfo.InvariantCulture) + 1;
            if (soft && context.Dependencies is not null)
            {
                if (text.StartsWith("import#", StringComparison.Ordinal)) return int.Parse(text.AsSpan(7), CultureInfo.InvariantCulture) + 1;
                if (!text.StartsWith("import:", StringComparison.Ordinal)) throw new FormatException();
                return context.Dependency(text[7..]) + 1;
            }
            int import;
            if (text.StartsWith("import#", StringComparison.Ordinal)) import = int.Parse(text.AsSpan(7), CultureInfo.InvariantCulture);
            else if (text.StartsWith("import:", StringComparison.Ordinal)) import = context.Import(text[7..], targetClass);
            else throw new FormatException();
            return soft ? import + 1 : -(import + 1);
        }

        private void Text(string text)
        {
            // 8-bit when every character fits, else UTF-16; the length's low six bits go in the first byte
            bool narrow = text.All(c => c <= 0xFF);
            int length = text.Length;
            int first = (narrow ? 0x80 : 0) | (length & 0x3F);
            if (length > 0x3F) first |= 0x40;
            _out.WriteByte((byte)first);
            for (int rest = length >> 6; rest > 0; rest >>= 7)
                _out.WriteByte((byte)((rest & 0x7F) | (rest > 0x7F ? 0x80 : 0)));
            _out.Write(narrow ? Encoding.Latin1.GetBytes(text) : Encoding.Unicode.GetBytes(text));
        }

        /// <summary>What a JSON value holds: a JsonElement when it was parsed from text, else the value it was made from.</summary>
        private static object Held(JsonNode? value) =>
            value is not JsonValue v ? throw new FormatException() : v.TryGetValue(out JsonElement e) ? e : v.GetValue<object>();

        private static string Str(JsonNode? value) => Held(value) switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
            _ => throw new FormatException(),
        };

        private static decimal Integer(JsonNode? value)
        {
            decimal result = Held(value) switch
            {
                JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt64(out long l) ? l : e.TryGetUInt64(out ulong u) ? u : e.GetDecimal(),
                string or bool or JsonElement => throw new FormatException(),
                IConvertible c => Convert.ToDecimal(c, CultureInfo.InvariantCulture),
                _ => throw new FormatException(),
            };
            return result == decimal.Truncate(result) ? result : throw new FormatException();
        }

        private static float Single(JsonNode? value) => Held(value) switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetSingle(),
            float f => f,
            JsonElement { ValueKind: JsonValueKind.String } e when NaNBits(e.GetString()!, 8) is long bits => BitConverter.Int32BitsToSingle((int)bits),
            string t when NaNBits(t, 8) is long bits => BitConverter.Int32BitsToSingle((int)bits),
            _ => (float)Real(value),
        };

        /// <summary>The bits of a NaN written as "NaN:" and hex digits (8 for a float, 16 for a double).</summary>
        private static long? NaNBits(string text, int digits) =>
            text.Length == 4 + digits && text.StartsWith("NaN:", StringComparison.Ordinal)
            && ulong.TryParse(text.AsSpan(4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong bits) ? (long)bits : null;

        private static double Real(JsonNode? value) => Held(value) switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } e => Special(e.GetString()!),
            string s => Special(s),
            bool or JsonElement => throw new FormatException(),
            IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture),
            _ => throw new FormatException(),
        };

        private static double Special(string s) => s switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            _ when NaNBits(s, 16) is long bits => BitConverter.Int64BitsToDouble(bits),
            _ => throw new FormatException(),
        };

        private static bool Truth(JsonNode? value) => Held(value) switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new FormatException(),
        };

        private void U16(int value) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)value); _out.Write(s); }
        private void I32(int value) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(s, value); _out.Write(s); }

        private void Span8(SpanAction write)
        {
            Span<byte> s = stackalloc byte[8];
            write(s);
            _out.Write(s);
        }

        private delegate void SpanAction(Span<byte> span);
    }

    private static string Describe(string type) => type switch
    {
        "Bool" => "true or false",
        "Float" or "Double" => "number",
        "Int8" or "Uint8" or "Int16" or "Uint16" or "Int32" or "Uint32" or "Int64" or "Uint64" => $"whole number ({type})",
        "CName" or "String" => "string",
        _ when IsHandle(type) || IsSoft(type) => "reference (null, \"export:N\" or \"import:path\")",
        _ when IsArray(type) || IsFixed(type, out _, out _) => $"list ({type})",
        "EngineTransform" => "transform ({ \"position\": {X, Y, Z}, \"rotation\": {Pitch, Yaw, Roll}, \"scale\": {X, Y, Z} }, each optional)",
        "CVariant" => "variant ({ \"type\": \"Float\", \"value\": 1 })",
        "SharedDataBuffer" or "DataBuffer" => "base64 string",
        _ when IsEnum(type) => $"{type} value name",
        _ => $"{type} object",
    };

    private static string Show(JsonNode? value)
    {
        string text = value?.ToJsonString() ?? "null";
        return text.Length > 60 ? text[..57] + "..." : text;
    }

    // ---------------------------------------------------------------- layout

    /// <summary>
    /// Indented JSON, with small objects and lists of plain values on one line (a vector, a curve point), so a file
    /// full of curves stays readable and diffs line by line.
    /// </summary>
    public static string Format(JsonNode node)
    {
        var text = new StringBuilder();
        Write(text, node, 0);
        text.Append('\n');
        return text.ToString();
    }

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int LineLimit = 110;

    private static void Write(StringBuilder text, JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonObject o when o.Count == 0: text.Append("{}"); return;
            case JsonArray a when a.Count == 0: text.Append("[]"); return;
            case JsonObject o:
            {
                if (OneLine(o) is { } line) { text.Append(line); return; }
                text.Append("{\n");
                int i = 0;
                foreach (var (key, value) in o)
                {
                    Indent(text, depth + 1);
                    text.Append(JsonSerializer.Serialize(key, Compact)).Append(": ");
                    Write(text, value, depth + 1);
                    text.Append(++i < o.Count ? ",\n" : "\n");
                }
                Indent(text, depth);
                text.Append('}');
                return;
            }
            case JsonArray a:
            {
                if (OneLine(a) is { } line) { text.Append(line); return; }
                text.Append("[\n");
                for (int i = 0; i < a.Count; i++)
                {
                    Indent(text, depth + 1);
                    Write(text, a[i], depth + 1);
                    text.Append(i + 1 < a.Count ? ",\n" : "\n");
                }
                Indent(text, depth);
                text.Append(']');
                return;
            }
            default:
                text.Append(node?.ToJsonString(Compact) ?? "null");
                return;
        }
    }

    /// <summary>The node on one line, when it's only plain values or objects of them and short enough.</summary>
    private static string? OneLine(JsonNode node)
    {
        bool Flat(JsonNode? n, int level) => n switch
        {
            JsonObject o => level < 2 && o.All(p => Flat(p.Value, level + 1)),
            JsonArray a => level < 1 && a.All(v => v is not (JsonObject or JsonArray)),
            _ => true,
        };
        if (!Flat(node, 0)) return null;
        var text = new StringBuilder();
        void Put(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o when o.Count == 0: text.Append("{}"); break;
                case JsonObject o:
                    text.Append("{ ");
                    int i = 0;
                    foreach (var (key, value) in o)
                    {
                        if (i++ > 0) text.Append(", ");
                        text.Append(JsonSerializer.Serialize(key, Compact)).Append(": ");
                        Put(value);
                    }
                    text.Append(" }");
                    break;
                case JsonArray a:
                    text.Append('[');
                    for (int k = 0; k < a.Count; k++)
                    {
                        if (k > 0) text.Append(", ");
                        Put(a[k]);
                    }
                    text.Append(']');
                    break;
                default: text.Append(n?.ToJsonString(Compact) ?? "null"); break;
            }
            if (text.Length > LineLimit) throw new OperationCanceledException();
        }
        try { Put(node); }
        catch (OperationCanceledException) { return null; }
        return text.ToString();
    }

    private static void Indent(StringBuilder text, int depth) => text.Append(' ', depth * 2);

    /// <summary>
    /// The order the strings table has its names and import paths in, as numbers into the names and then on into the
    /// imports; null when it's names first, then imports, which is what's written without one.
    /// </summary>
    private static JsonArray? StringOrder(byte[] d, List<string> names, List<(string Path, string ClassName, int Flags)> imports)
    {
        int namesAt = I32(d, 52), importsAt = I32(d, 64);
        var byOffset = new SortedDictionary<int, int>();
        for (int i = 0; i < names.Count; i++) byOffset.TryAdd(I32(d, namesAt + i * 8), i);
        for (int i = 0; i < imports.Count; i++) byOffset.TryAdd(I32(d, importsAt + i * 8), names.Count + i);
        var actual = byOffset.Values.ToList();

        var usual = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Count; i++) if (seen.Add(names[i])) usual.Add(i);
        for (int i = 0; i < imports.Count; i++) if (seen.Add(imports[i].Path)) usual.Add(names.Count + i);
        return actual.SequenceEqual(usual) ? null : new JsonArray([.. actual.Select(i => (JsonNode)i)]);
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// The names, read from the strings table the way they'll be written back: as UTF-8, or as 8-bit text for the few
    /// that aren't valid UTF-8 (those are listed, so they're written back as they were).
    /// </summary>
    private static (List<string> Names, HashSet<string> Latin1) Strings(byte[] d)
    {
        var latin1 = new HashSet<string>(StringComparer.Ordinal);
        int namesAt = I32(d, 52), count = I32(d, 56);
        var names = new List<string>(count);
        for (int i = 0; i < count; i++) names.Add(Text(d, I32(d, namesAt + i * 8), latin1));
        return (names, latin1);
    }

    private static string Text(byte[] d, int offset, HashSet<string> latin1)
    {
        int stringsAt = I32(d, 40), size = I32(d, 44);
        if (offset < 0 || offset >= size) return "";
        var from = d.AsSpan(stringsAt + offset, size - offset);
        int end = from.IndexOf((byte)0);
        var bytes = end < 0 ? from : from[..end];
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            string text = Encoding.Latin1.GetString(bytes);
            latin1.Add(text);
            return text;
        }
    }

    private static int I32(byte[] d, int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
    private static int U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
}
