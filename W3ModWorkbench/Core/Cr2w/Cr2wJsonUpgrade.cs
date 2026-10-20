using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>What saving a file as the newest version changed, and what it couldn't be sure of.</summary>
public sealed record Cr2wUpgradeReport(int FromVersion, int ToVersion, IReadOnlyList<string> Notes)
{
    public bool Changed => FromVersion != ToVersion;
}

public static partial class Cr2wJson
{
    /// <summary>
    /// A converted file made into the newest version (164), the way the engine would save it after loading it: for
    /// REDkit's older source files, which go back to 120. The JSON stays the same where the versions agree; what
    /// changes is what the newer engine names or keeps differently:
    /// <list type="bullet">
    /// <item>each property's type becomes the one the engine declares for it, where that's the same kind of value (the
    /// Witcher 2's names, <c>@Float</c>, <c>*CEntity</c>, <c>~CEntityTemplate</c>, <c>Uint</c>, become
    /// <c>array:2,0,Float</c>, <c>handle:CEntity</c>, <c>soft:CEntityTemplate</c>, <c>Uint32</c>);</item>
    /// <item>soft references, which the older layout keeps in a list of dependencies, become imports (flagged 4, as
    /// the engine writes them);</item>
    /// <item>the older layout's object-entry fields the newer one hasn't (a name, eight bytes, a template that's
    /// always "none") go, and an object's flags keep their low 16 bits, which are all the newer entry has;</item>
    /// <item>objects and structs saved without the zero byte (some in 155 to 157) get it.</item>
    /// </list>
    /// A property the engine has as a different kind of value, or no longer has, is kept as it was and noted (the
    /// engine converts or skips such a property itself when it loads the file). So is the data a class keeps after its
    /// properties (<c>$tail</c>), which can't be checked. A file whose objects or values couldn't be read in an older
    /// layout (kept as bytes) can't be saved as the newest: those bytes are in the older layout's form.
    /// </summary>
    public static JsonObject UpgradeToLatest(JsonObject source, out Cr2wUpgradeReport report)
    {
        var root = (JsonObject)source.DeepClone();
        var file = root["file"] as JsonObject ?? throw new InvalidDataException("The JSON has no \"file\" block.");
        var objects = root["objects"] as JsonArray ?? throw new InvalidDataException("The JSON has no \"objects\" list.");
        int from = (int?)file["version"] ?? Cr2wFile.LastVersion;
        if (from >= Cr2wFile.LastVersion)
        {
            report = new Cr2wUpgradeReport(from, from, []);
            return root;
        }
        bool legacy = from <= Cr2wLegacy.LastVersion;

        var upgrader = new Upgrader(from,
            (file["types"] as JsonObject ?? []).Where(t => t.Value is JsonValue).ToDictionary(t => t.Key, t => (string)t.Value!),
            legacy ? (file["dependencies"] as JsonArray ?? []).Select(n => (string?)n ?? "").ToList() : null);
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is not JsonObject node) continue;
            upgrader.Object(node, $"objects[{i}]");
        }

        // the soft references' files as imports, as the engine flags them
        var imports = file["imports"] as JsonArray ?? [];
        foreach (var (path, className, soft) in upgrader.SoftImports)
            if (!imports.Any(i => string.Equals((string?)i?["path"], path, StringComparison.OrdinalIgnoreCase)))
            {
                var entry = new JsonObject { ["path"] = path, ["class"] = className };
                if (soft) entry["flags"] = 4;
                imports.Add(entry);
            }
        file["imports"] = imports;

        file["version"] = Cr2wFile.LastVersion;
        file.Remove("dependencies");
        file.Remove("dependencyCount");
        file["chunks"] ??= 6;
        var types = new JsonObject();
        foreach (var (key, type) in upgrader.Types) types[key] = type;
        file["types"] = types;
        // the file block's order as a newer file has it, the types last
        foreach (string key in new[] { "trailer", "types" })
            if (file[key] is { } moved) { file.Remove(key); file[key] = moved; }

        report = new Cr2wUpgradeReport(from, Cr2wFile.LastVersion, upgrader.Notes);
        return root;
    }

    /// <summary>A file's bytes saved as the newest version (see <see cref="UpgradeToLatest"/>).</summary>
    public static byte[] UpgradeToLatest(Cr2wFile file, out Cr2wUpgradeReport report) =>
        FromJson(UpgradeToLatest(ToJson(file, out _), out report));

    /// <summary>
    /// Properties the engine renamed, which it moves to their new name when it loads an older file: by class and old
    /// name, the new name and the new type (a struct of the same fields).
    /// </summary>
    private static readonly Dictionary<(string Class, string Property), (string Name, string Type)> Renamed = new()
    {
        [("CAreaEnvironmentParams", "m_ssao")] = ("m_ssaoNV", "CEnvNVSSAOParameters"),
    };

    /// <summary>
    /// What the engine gives the properties an older file's struct doesn't have, where that isn't the class's default
    /// (so it then saves them): an older environment's water has its newer curves empty, the colours as colours. Only
    /// where something of the struct is left; one with nothing left isn't saved at all.
    /// </summary>
    private static readonly Dictionary<string, (string Property, string Json)[]> Filled = new()
    {
        ["CEnvWaterParameters"] =
        [
            ("waterFlowIntensity", "{}"), ("underwaterBrightness", "{}"), ("underWaterFogIntensity", "{}"),
            ("waterColor", "{\"CurveType\":\"SCT_Color\"}"), ("underWaterColor", "{\"CurveType\":\"SCT_Color\"}"),
            ("waterFresnel", "{}"), ("waterCaustics", "{}"), ("waterFoamIntensity", "{}"), ("waterAmbientScale", "{}"), ("waterDiffuseScale", "{}"),
        ],
    };

    /// <summary>
    /// Classes whose data after their properties (once the 0 before 139 is gone) is as version 164 has it, checked
    /// against REDkit's own 164 files and the game's cooked ones. A file in the older layout with an object of
    /// any other class that keeps such data isn't saved as 164: that data may be in a form the engine no longer reads.
    /// </summary>
    private static readonly HashSet<string> SameTail = new(StringComparer.Ordinal)
    {
        // a skeleton's bones, 48 bytes each in every version; the same as the cooked copy's but for 8 skeletons edited since
        "CSkeleton",
        // the same bytes, in every one of REDkit's older files that the game has cooked, as the cooked copy's
        "CAlwaysTransitionCondition", "CAnimEventTransitionCondition", "CAnimationEndCondition",
        "CBehaviorConstraintVectorObject", "CBehaviorEventDescription", "CBehaviorGraphAdjustDirectionNode",
        "CBehaviorGraphAnimEventTrackNode", "CBehaviorGraphAnimationEnumSequentialSwitchNode",
        "CBehaviorGraphAnimationEnumSwitchNode", "CBehaviorGraphAnimationExtNode",
        "CBehaviorGraphAnimationManualSwitchNode", "CBehaviorGraphAnimationManualWithInputSlotNode",
        "CBehaviorGraphAnimationNode", "CBehaviorGraphAnimationOutputSocket", "CBehaviorGraphAnimationSlotNode",
        "CBehaviorGraphBlendNode", "CBehaviorGraphBlendOverrideNode", "CBehaviorGraphCameraControllerNode",
        "CBehaviorGraphCharacterRotationNode", "CBehaviorGraphComboTransitionInterface",
        "CBehaviorGraphComboTransitionNode", "CBehaviorGraphComparatorNode", "CBehaviorGraphConstraintNodeCameraFocus",
        "CBehaviorGraphConstraintNodeCameraLookAt", "CBehaviorGraphConstraintNodeLookAt",
        "CBehaviorGraphConstraintNodeRoll", "CBehaviorGraphConstraintReset", "CBehaviorGraphCurveDampValueNode",
        "CBehaviorGraphCurveMapValueNode", "CBehaviorGraphCutsceneControllerNode", "CBehaviorGraphDampValueNode",
        "CBehaviorGraphDirectionalMovementStartNode", "CBehaviorGraphEngineValueNode",
        "CBehaviorGraphEventWatchdogNode", "CBehaviorGraphFilterNode", "CBehaviorGraphFloatValueNode",
        "CBehaviorGraphFlowTransitionNode", "CBehaviorGraphGameplayAdditiveNode", "CBehaviorGraphGetBoneTransformNode",
        "CBehaviorGraphGetCustomTrackNode", "CBehaviorGraphGlobalComboTransitionNode", "CBehaviorGraphInputNode",
        "CBehaviorGraphLatchValueNode", "CBehaviorGraphMatchFromPoseNode", "CBehaviorGraphMathNode",
        "CBehaviorGraphMemoryNode", "CBehaviorGraphOneMinusNode", "CBehaviorGraphOutputNode",
        "CBehaviorGraphOverrideFloatTracksNode", "CBehaviorGraphParentInputNode", "CBehaviorGraphParentValueInputNode",
        "CBehaviorGraphPointerTransitionNode", "CBehaviorGraphPoseSlotNode", "CBehaviorGraphPositionControllerNode",
        "CBehaviorGraphPositionControllerWithDampNode", "CBehaviorGraphRagdollNode", "CBehaviorGraphRandomNode",
        "CBehaviorGraphRandomValueNode", "CBehaviorGraphRotateBoneNode", "CBehaviorGraphRotationControllerNode",
        "CBehaviorGraphSelectionValueNode", "CBehaviorGraphSpeedModulationNode",
        "CBehaviorGraphStateTransitionBlendNode", "CBehaviorGraphStateTransitionGlobalBlendNode",
        "CBehaviorGraphStateTransitionMatchFromPoseNode", "CBehaviorGraphTimerValueNode",
        "CBehaviorGraphTranslateBoneNode", "CBehaviorGraphValueInterpolationNode", "CBehaviorGraphVariableInputSocket",
        "CBehaviorGraphVariableNode", "CBehaviorGraphVectorMathNode", "CBehaviorGraphVectorVariableNode",
        "CBehaviorSyncMethodDuration", "CBehaviorSyncMethodEventProp", "CBehaviorSyncMethodEventStart",
        "CBehaviorVariable", "CBehaviorVectorVariable", "CCompositeSimultaneousTransitionCondition",
        "CCompositeTransitionCondition", "CEnvironmentDefinition", "CEvaluatorFloatConst",
        "CEvaluatorFloatRandomUniform", "CEvaluatorFloatStartEnd", "CEventStateTransitionCondition", "CFont",
        "CGenericGrassMask", "CJobAction", "CJobForceOutAction", "CJobTree", "CJobTreeNode", "CJournalCreatureGroup",
        "CJournalGlossaryGroup", "CJournalResource", "CParentInputValueStateTransitionCondition",
        "CParticleDrawerBillboard", "CParticleDrawerEmitterOrientation", "CParticleDrawerFacingTrail",
        "CParticleDrawerMesh", "CParticleDrawerMotionBlur", "CParticleDrawerScreen", "CParticleDrawerTrail",
        "CParticleSystem", "CPoseCompressionCamera", "CQuestFactsDBCondition", "CQuestGraph", "CQuestLayersHiderBlock",
        "CQuestPauseConditionBlock", "CQuestPhase", "CQuestPhaseInputBlock", "CRagdoll", "CSkeletalAnimationSet",
        "CSkeletalAnimationSetEntry", "CSwarmCellMap", "CVariableValueStateTransitionCondition", "CWitcherGameResource",
    };

    /// <summary>Classes the engine renamed, which it reads an older file's by their new name.</summary>
    private static readonly Dictionary<string, string> RenamedClasses = new(StringComparer.Ordinal)
    {
        ["CEnvAmbientOnlyParameters"] = "CEnvReflectionProbesGenParameters",
    };

    /// <summary>Properties' defaults, which the engine doesn't save, as the game's files show them.</summary>
    private static readonly Dictionary<(string Class, string Property), string> Defaults = new()
    {
        [("CEnvGlobalSkyParameters", "activateFactor")] = "1",
    };

    private sealed class Upgrader(int from, Dictionary<string, string> oldTypes, List<string>? dependencies)
    {
        public readonly Dictionary<string, string> Types = new(StringComparer.Ordinal);
        public readonly List<string> Notes = [];
        public readonly List<(string Path, string ClassName, bool Soft)> SoftImports = [];
        private readonly HashSet<string> _noted = new(StringComparer.Ordinal);

        public void Object(JsonObject node, string path)
        {
            string className = (string?)node["$class"] ?? throw new InvalidDataException($"{path} has no \"$class\".");
            if (RenamedClasses.TryGetValue(className, out string? renamedClass) && node["$data"] is null)
            {
                node["$class"] = className = renamedClass;
                Note($"class {className}", $"{path}: the class is {renamedClass} now.");
            }
            if (node["$data"] is not null)
            {
                if (dependencies is not null)
                    throw new InvalidDataException($"{path} ({className}) couldn't be read, so it can't be saved as version {Cr2wFile.LastVersion}.");
                return; // a newer layout's object, the same bytes in 164
            }
            node.Remove("$name");
            node.Remove("$extra");
            if (node["$template"] is JsonValue template && Whole(template) is 0 or 0xFFFF or -1) node.Remove("$template");
            if (node["$flags"] is JsonValue flags)
            {
                long low = Whole(flags) & 0xFFFF;
                if (low == 0) node.Remove("$flags"); else node["$flags"] = (int)low;
            }
            // the 0 an object's properties end with before 139, which later versions have before them instead
            node.Remove("$end");
            if (node["$parameters"] is JsonArray parameters)
                for (int i = 0; i < parameters.Count; i++)
                    if (parameters[i] is JsonObject parameter && (string?)parameter["type"] is { } type)
                    {
                        // a texture parameter is a handle in 164 where older files have it as a pointer, stored alike
                        string newType = Translate(type) is var t && t.StartsWith("ptr:", StringComparison.Ordinal) ? "handle:" + t[4..] : Translate(type);
                        parameter["type"] = newType;
                        parameter["value"] = Value(type, newType, parameter["value"]?.DeepClone(), $"{path}.$parameters[{i}]");
                    }
            if (node["$tail"] is JsonValue tail)
            {
                // what a class keeps after its properties is its own format, which has changed for some classes
                byte[] bytes = Convert.FromBase64String((string)tail!);
                byte[]? converted = Tail(className, bytes, out string? why);
                if (converted is null)
                    throw new NotSupportedException($"{path} is a {className}, which keeps data of its own after its properties"
                        + (why is null ? $", and that hasn't been checked against version {Cr2wFile.LastVersion} files yet" : $": {why}")
                        + $", so the file can't be saved as {Cr2wFile.LastVersion}.");
                if (converted.Length == 0) node.Remove("$tail");
                else if (!converted.AsSpan().SequenceEqual(bytes)) node["$tail"] = Convert.ToBase64String(converted);
            }
            Struct(node, className, path);
        }

        /// <summary>
        /// The data a class keeps after its properties, as version 164 has it; null when that isn't known (and
        /// <paramref name="why"/> when there's more to say). Each change was found and checked against REDkit's own files
        /// of every version and the game's cooked copies of the older ones:
        /// <list type="bullet">
        /// <item>A node (an entity or a component) keeps two lists of the objects attached to it and below it, and up to
        /// 161 its transform in the world after them (64 bytes), which 162 stopped saving.</item>
        /// <item>A layer kept 11 to 13 bytes of nothing (zeros, and an empty list) until 159.</item>
        /// <item>An entity template kept a list of files inside it until 155, which 164 always has empty; when they
        /// are empty too (as most were) that's the same. Those with components in them can't be converted yet: the newer
        /// engine keeps those inside the entity's own data.</item>
        /// <item>An effect track item or a curve evaluator keeps the curves it holds; before 155 as a u32 count and
        /// always four of them, the unused ones 0, since as a u16 count and only those it has.</item>
        /// <item>An area's shape (in its node data) is version 3 before 155 and 4 since, the same otherwise.</item>
        /// <item>The classes in <see cref="SameTail"/> keep the same data in every version.</item>
        /// </list>
        /// </summary>
        private byte[]? Tail(string className, byte[] bytes, out string? why)
        {
            why = null;
            if (IsNode(className))
            {
                if (from > 161) return bytes;
                int at = 0;
                for (int list = 0; list < 2; list++)
                {
                    if (at + 4 > bytes.Length) return null;
                    int count = BitConverter.ToInt32(bytes, at);
                    at += 4;
                    if (count < 0 || at + count * 4L > bytes.Length) return null;
                    at += count * 4;
                }
                if (at + 64 > bytes.Length) return null;
                byte[] node = [.. bytes.AsSpan(0, at), .. bytes.AsSpan(at + 64)];
                if (!AreaShapes(node.AsSpan(at), DerivesFrom(className, "CAreaComponent"), out why)) return null;
                return node;
            }
            if (className == "CLayer")
            {
                if (from > 159) return bytes;
                if (bytes.Length is >= 11 and <= 13 && bytes.AsSpan(0, Math.Min(12, bytes.Length)).IndexOfAnyExcept((byte)0) < 0
                    && (bytes.Length < 13 || bytes[12] == 0x80)) return [];
                return null;
            }
            if (className == "CEntityTemplate")
            {
                if (bytes.Length == 4 && BitConverter.ToInt32(bytes) == 0) return bytes;
                if (from > 154 || bytes.Length < 4) return null;
                int count = BitConverter.ToInt32(bytes), at = 4;
                for (int i = 0; i < count; i++)
                {
                    if (at + 4 > bytes.Length) return null;
                    int size = BitConverter.ToInt32(bytes, at);
                    at += 4;
                    if (size < 0 || at + (long)size > bytes.Length) return null;
                    if (size > 0 && !IsEmptyCr2w(bytes.AsSpan(at, size)))
                    {
                        why = "its older form keeps compiled components in it, which the newer engine keeps in the entity's own data instead, and that move isn't done yet";
                        return null;
                    }
                    at += size;
                }
                return at == bytes.Length ? [0, 0, 0, 0] : null;
            }
            switch (className)
            {
                case "CBitmapTexture" or "CSwfTexture":
                {
                    // the source image: a u32, how many mips, each its width, height, pitch, size and bytes, then a u32
                    if (bytes.Length < 8) return null;
                    int mips = BitConverter.ToInt32(bytes, 4);
                    long at = 8;
                    for (int m = 0; m < mips && at + 16 <= bytes.Length; m++) at += 16 + (uint)BitConverter.ToInt32(bytes, (int)at + 12);
                    return mips >= 0 && at + 4 == bytes.Length ? bytes : null;
                }
                case "CSourceTexture":
                    // its size, then its bytes
                    return bytes.Length >= 4 && BitConverter.ToInt32(bytes) == bytes.Length - 4 ? bytes : null;
                case "CSwfResource":
                {
                    // the movie and what goes with it, each its size then its bytes
                    long at = 0;
                    for (int block = 0; block < 2 && at + 4 <= bytes.Length; block++) at += 4 + (uint)BitConverter.ToInt32(bytes, (int)at);
                    return at == bytes.Length ? bytes : null;
                }
                case "CTextureArray":
                    if (bytes.Length == 20) return bytes;
                    why = "its older form keeps the textures' data in it, which the newer one doesn't";
                    return null;
                case "CCubeTexture":
                    if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes) == 0xFFFFFFFF) return bytes;
                    why = "it's in an older form than REDkit's own cube maps, which isn't known yet";
                    return null;
            }
            if (className == "CCurve")
                // its points, 48 bytes each, as in every version
                return bytes.Length >= 4 && bytes.Length == 4 + 48L * BitConverter.ToInt32(bytes) ? bytes : null;
            if (className.StartsWith("CFXTrackItem", StringComparison.Ordinal) || className.StartsWith("CEvaluator", StringComparison.Ordinal) && className.EndsWith("Curve", StringComparison.Ordinal))
            {
                // the curves an effect track item or a curve evaluator holds: a u16, how many, and those objects;
                // before 155 how many was a u32 and there were always four, the unused ones 0
                if (bytes.Length >= 4 && bytes.Length == 4 + 4 * BitConverter.ToUInt16(bytes, 2)) return bytes;
                if (from > 154 || bytes.Length != 22) return null;
                int count = BitConverter.ToInt32(bytes, 2);
                if (count is < 0 or > 4) return null;
                for (int i = 0; i < 4; i++)
                    if ((BitConverter.ToInt32(bytes, 6 + i * 4) != 0) != (i < count)) return null;
                return [bytes[0], bytes[1], (byte)count, 0, .. bytes.AsSpan(6, count * 4)];
            }
            return SameTail.Contains(className) ? bytes : null;
        }

        /// <summary>
        /// Makes the area shapes in a node's data the version 164 has. An area's shape is kept as its size (counting
        /// itself), a version, how many convex parts there are, and each part: a version, three vectors (its bounds),
        /// four counts (k points, 2k-4 faces, 6k-12 edges and k again for a closed hull) and its data, 302k-515 bytes in
        /// all. Before 155 both versions are 3, and the engine, reading them, saves 4: the rest is the same. A shape is
        /// only changed when every part adds up to its size exactly; one that looks like a shape but doesn't add up
        /// can't be made sure of, so the node isn't converted (false).
        /// </summary>
        private bool AreaShapes(Span<byte> data, bool isArea, out string? why)
        {
            why = null;
            if (from > 154) return true;
            for (int start = 0; start + 6 <= data.Length; start++)
            {
                if (data[start + 4] != 3 || data[start + 5] == 0 || start + 7 > data.Length || data[start + 6] != 3) continue;
                int size = BitConverter.ToInt32(data[start..]);
                if (size < 6 + 53 || start + (long)size > data.Length) continue;
                int at = start + 6;
                var versions = new List<int> { start + 4 };
                bool fits = true;
                for (int part = 0; part < data[start + 5]; part++)
                {
                    if (at + 53 > start + size || data[at] != 3) { fits = false; break; }
                    int k = data[at + 49];
                    if (data[at + 50] != 2 * k - 4 || data[at + 51] != 6 * k - 12 || data[at + 52] != k || k < 4) { fits = false; break; }
                    versions.Add(at);
                    at += 302 * k - 515;
                }
                if (!fits || at != start + size) continue;
                foreach (int v in versions) data[v] = 4;
                start = at - 1;
            }
            // an area's own shape is at the start of its data: one left as it was couldn't be read
            if (isArea && data.Length >= 7 && data[4] == 3 && data[6] == 3 && BitConverter.ToInt32(data) <= data.Length)
            {
                why = "its area shape isn't one this can read (not a closed hull)";
                return false;
            }
            return true;
        }

        /// <summary>Whether bytes are a CR2W file with no objects in it.</summary>
        private static bool IsEmptyCr2w(ReadOnlySpan<byte> data)
        {
            try { return Cr2wFile.IsCr2w(data) && Cr2wFile.Read(data.ToArray()).Exports.Count == 0; }
            catch (InvalidDataException) { return false; }
        }

        /// <summary>Whether a class is a node (CNode or one derived from it: entities and components).</summary>
        private static bool IsNode(string className) => DerivesFrom(className, "CNode");

        /// <summary>Whether a class is or derives from another, by the engine's classes and the game's script classes, which go on from them.</summary>
        private static bool DerivesFrom(string className, string ancestor)
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

        private void Struct(JsonObject node, string className, string path, string? oldClass = null)
        {
            oldClass ??= className;
            node.Remove("$marker");
            if (className is "SSimpleCurve" or "SCurveData") Curve(node, className);
            var local = node["$types"] as JsonObject;
            var newLocal = new JsonObject();
            foreach (var (key, value) in node.ToList())
            {
                if (key.StartsWith('$')) continue;
                string stored = Stored(className, key);
                string oldType = local?[key] is JsonValue own && (string?)own is { } ownType ? ownType
                    : oldTypes.TryGetValue(oldClass + "." + stored, out var fileType) ? fileType
                    : EngineType(oldClass, stored) ?? EngineType(className, stored) ?? throw new InvalidDataException($"The type of {oldClass}.{stored} isn't known.");
                string name = key;
                bool moved = false;
                if (dependencies is not null && Renamed.TryGetValue((className, stored), out var renamed) && !node.ContainsKey(renamed.Name))
                {
                    // moved to its new name, as a struct of the new type with the fields that type has
                    node.Remove(key);
                    name = stored = renamed.Name;
                    moved = true;
                    node[name] = value;
                    Note($"renamed {className}.{key}", $"{className}.{key} is {className}.{name} now; it's moved there, as the engine does.");
                }
                else if (IsGone(className, stored) || IsOtherStruct(oldType, EngineType(className, stored)))
                {
                    // the engine skips what its class doesn't have when it loads the file, so it's gone when it saves it
                    node.Remove(key);
                    Note($"gone {className}.{stored}", $"{className}.{stored} isn't in the engine's {className} any more, so it's left out, as the engine leaves it out.");
                    continue;
                }
                string newType = moved ? EngineType(className, stored)! : NewType(className, stored, oldType, $"{path}.{name}");
                var newValue = Value(oldType, newType, node[name]?.DeepClone(), $"{path}.{name}", moved);
                if (IsDefault(className, stored, newValue))
                {
                    // the engine saves only what isn't its default
                    node.Remove(name);
                    continue;
                }
                node[name] = newValue;
                string typeKey = className + "." + stored;
                if (!Types.TryAdd(typeKey, newType) && Types[typeKey] != newType) newLocal[name] = newType;
            }
            // only where something of the struct is left (one with nothing left isn't saved at all)
            if (dependencies is not null && Filled.TryGetValue(className, out var filled) && node.Any(p => !p.Key.StartsWith('$')))
                foreach (var (property, json) in filled)
                    if (!node.ContainsKey(property))
                    {
                        node[property] = JsonNode.Parse(json);
                        Types.TryAdd(className + "." + property, EngineType(className, property)!);
                    }
            InEngineOrder(node, className);
            node.Remove("$types");
            if (newLocal.Count > 0)
            {
                // where the decoder puts them: after the class and the entry's fields, before the properties
                var rest = node.Where(p => !p.Key.StartsWith('$')).Select(p => p.Key).ToList();
                node["$types"] = newLocal;
                foreach (string key in rest) { var v = node[key]; node.Remove(key); node[key] = v; }
            }
            if (node["$attachment"] is JsonObject attachment && (string?)attachment["$class"] is { } attachmentClass)
                Struct(attachment, attachmentClass, path + ".$attachment");
        }

        /// <summary>
        /// A curve as older files keep it, a list each of times, values, control points and the curve types either
        /// side of each point, made into the list of points the engine now has (<c>dataCurveValues</c>, or
        /// <c>Curve Values</c> in an <c>SCurveData</c>), each point its time, control point, value and curve types.
        /// </summary>
        private static void Curve(JsonObject node, string className)
        {
            bool simple = className == "SSimpleCurve";
            string[] old = simple
                ? ["dataTimes", "dataControlPoints", "dataValues", "dataCurveType0", "dataCurveType1"]
                : ["times", "control points", "values", "left curve type", "right curve type"];
            string[] point = ["time", "controlPoint", "value", "curveTypeL", "curveTypeR"];
            if (!old.Any(node.ContainsKey)) return;
            var lists = old.Select(k => node[k] as JsonArray).ToArray();
            int count = lists.Max(l => l?.Count ?? 0);
            var points = new JsonArray();
            for (int i = 0; i < count; i++)
            {
                var entry = new JsonObject();
                // the engine leaves out what's at its default, which is 0 for each
                for (int f = 0; f < old.Length; f++)
                    if (lists[f] is { } list && i < list.Count && !IsZero(list[i])) entry[point[f]] = list[i]?.DeepClone();
                points.Add(entry);
            }
            // in the place of the first of the old lists, as the engine orders them
            var rebuilt = node.ToList();
            node.Clear();
            bool placed = false;
            foreach (var (key, value) in rebuilt)
            {
                if (old.Contains(key))
                {
                    if (!placed) { node[simple ? "dataCurveValues" : "Curve Values"] = points; placed = true; }
                    continue;
                }
                node[key] = value;
            }
        }

        /// <summary>Whether a value is 0, or a struct of nothing but 0s.</summary>
        private static bool IsZero(JsonNode? value) => value switch
        {
            JsonObject o => o.All(p => p.Key.StartsWith('$') || IsZero(p.Value)),
            JsonValue v => v.GetValue<object>() switch
            {
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } e => e.GetDouble() == 0,
                System.Text.Json.JsonElement => false,
                string or bool => false,
                IConvertible c => Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture) == 0,
                _ => false,
            },
            _ => false,
        };

        /// <summary>
        /// Whether a property is now a struct of another class than the file has it as, which the engine can't move a
        /// value between, so it skips it.
        /// </summary>
        private static bool IsOtherStruct(string oldType, string? engineType) =>
            engineType is not null && Translate(oldType) != engineType && IsStructClass(engineType)
            && (IsStructClass(oldType) || !RttiClasses.Value.ContainsKey(oldType) && !IsArray(oldType) && !IsHandle(oldType) && !IsSoft(oldType)
                && !IsFixed(oldType, out _, out _) && !NativeTypes.Contains(oldType) && !IsEnum(oldType) && char.IsUpper(oldType[0]) && oldType.Length > 6);

        private static bool IsStructClass(string type) =>
            RttiClasses.Value.TryGetValue(type, out var c) && c.IsStruct && !NativeTypes.Contains(type);

        /// <summary>
        /// Whether a value is what the engine has by default, so it doesn't save it: a struct left with nothing in it,
        /// or one of the defaults the game's files show (<see cref="Defaults"/>).
        /// </summary>
        private static bool IsDefault(string className, string property, JsonNode? value) =>
            value is JsonObject { Count: 0 } && IsStructClass(EngineType(className, property) ?? "")
            || Defaults.TryGetValue((className, property), out string? json) && value is not null && value.ToJsonString() == json;

        /// <summary>Whether the engine's class (one it knows) no longer has a property.</summary>
        private static bool IsGone(string className, string property) =>
            RttiClasses.Value.ContainsKey(className) && !Aliases.ContainsKey(className) && EngineType(className, property) is null;

        /// <summary>The properties in the order the engine declares them, its base classes' first, as it saves them.</summary>
        private static void InEngineOrder(JsonObject node, string className)
        {
            var order = new List<string>();
            for (string? c = className; c is not null && RttiClasses.Value.TryGetValue(c, out var rtti); c = rtti.Base)
                order.InsertRange(0, rtti.Properties.Select(p => p.Name));
            if (order.Count == 0) return;
            int Rank(string key) { int i = order.IndexOf(Stored(className, key)); return i < 0 ? int.MaxValue : i; }
            var props = node.Where(p => !p.Key.StartsWith('$')).Select(p => p.Key).ToList();
            var sorted = props.OrderBy(Rank).ToList(); // stable: those it doesn't know keep their order, last
            if (sorted.SequenceEqual(props)) return;
            foreach (string key in sorted) { var v = node[key]; node.Remove(key); node[key] = v; }
        }

        private string NewType(string className, string property, string oldType, string path)
        {
            string translated = Translate(oldType);
            string? engine = EngineType(className, property);
            if (engine is null) return translated;
            if (Kind(engine) == Kind(translated)) return engine;
            Note($"retyped {className}.{property}", $"{className}.{property} is {oldType} in the file but {engine} in the engine; it's kept as {translated} (the engine converts it when it loads the file).");
            return translated;
        }

        private JsonNode? Value(string oldType, string newType, JsonNode? value, string path, bool moved = false)
        {
            if (value is JsonObject { } raw && raw["$raw"] is not null)
            {
                if (dependencies is not null) throw new InvalidDataException($"{path} couldn't be read, so it can't be saved as version {Cr2wFile.LastVersion}.");
                return value;
            }
            if (IsSoft(oldType))
            {
                if (dependencies is null || value is null) return value;
                string text = (string)value!;
                string file = text.StartsWith("import#", StringComparison.Ordinal) && int.TryParse(text.AsSpan(7), out int n) && n >= 0 && n < dependencies.Count
                    ? dependencies[n] : text.StartsWith("import:", StringComparison.Ordinal) ? text[7..] : throw new InvalidDataException($"{path} isn't a reference.");
                SoftImports.Add((file, Target(newType), IsSoft(newType)));
                return "import:" + file;
            }
            if (IsHandle(oldType)) return value;
            if (oldType == "CVariant" && value is JsonObject variant && (string?)variant["type"] is { Length: > 0 } inner)
            {
                string innerNew = Translate(inner);
                variant["type"] = innerNew;
                variant["value"] = Value(inner, innerNew, variant["value"]?.DeepClone(), path + ".value");
                return variant;
            }
            if ((IsArray(oldType) || IsFixed(oldType, out _, out _)) && value is JsonArray list)
            {
                string oldElement = IsFixed(oldType, out string fixedOld, out _) ? fixedOld : ElementType(oldType);
                string newElement = IsFixed(newType, out string fixedNew, out _) ? fixedNew : IsArray(newType) ? ElementType(newType) : Translate(oldElement);
                for (int i = 0; i < list.Count; i++) list[i] = Value(oldElement, newElement, list[i]?.DeepClone(), $"{path}[{i}]");
                return list;
            }
            if (value is JsonObject fields && !NativeTypes.Contains(oldType) && !IsEnum(oldType))
            {
                if (moved) Struct(fields, newType, path, oldType);
                else Struct(fields, Kind(newType) == Kind(oldType) ? newType : Translate(oldType), path, oldType);
                return fields;
            }
            return value;
        }

        private static long Whole(JsonValue value) => Convert.ToInt64(value.GetValue<object>() is System.Text.Json.JsonElement e ? e.GetInt64() : value.GetValue<object>(), System.Globalization.CultureInfo.InvariantCulture);

        private void Note(string key, string text)
        {
            if (_noted.Add(key)) Notes.Add(text);
        }
    }

    /// <summary>
    /// The game's script classes (and the native classes the scripts declare, which REDkit's list of the engine's
    /// leaves out) and the class each extends, from the vanilla scripts.
    /// </summary>
    private static readonly Lazy<Dictionary<string, string>> ScriptParents = new(() =>
    {
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = typeof(Cr2wJson).Assembly.GetManifestResourceStream("W3ModWorkbench.Core.Cr2w.scriptclasses.txt.gz");
        if (stream is null) return parents;
        using var reader = new StreamReader(new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress));
        for (string? line; (line = reader.ReadLine()) is not null;)
        {
            if (line.StartsWith('#')) continue;
            int tab = line.IndexOf('\t');
            if (tab > 0) parents[line[..tab]] = line[(tab + 1)..];
        }
        return parents;
    });

    /// <summary>The engine's own types, which a struct of the same name would be mistaken for.</summary>
    private static readonly HashSet<string> NativeTypes = new(StringComparer.Ordinal)
    {
        "CDateTime", "CGUID", "CName", "CPhysicalCollision", "CVariant", "DataBuffer", "DeferredDataBuffer", "EngineQsTransform",
        "EngineTransform", "EntityHandle", "GlobalVisID", "IdTag", "LocalizedString", "SharedDataBuffer", "SMeshTypeResourceLODLevel",
        "String", "StringAnsi", "StringUni", "TagList",
    };

    /// <summary>A type as a file of 155 on names it: the Witcher 2's <c>@T</c>, <c>*T</c>, <c>#T</c>, <c>~T</c>, <c>Uint</c> and <c>Int</c> made the newer way.</summary>
    internal static string Translate(string type)
    {
        if (RenamedClasses.TryGetValue(type, out string? renamed)) return renamed;
        if (type.StartsWith('@')) return "array:2,0," + Translate(type[1..]);
        if (type.StartsWith('*') || type.StartsWith('#')) return "handle:" + Translate(type[1..]);
        if (type.StartsWith('~')) return "soft:" + Translate(type[1..]);
        if (type.StartsWith("handle:", StringComparison.Ordinal) || type.StartsWith("ptr:", StringComparison.Ordinal) || type.StartsWith("soft:", StringComparison.Ordinal))
            return type[..(type.IndexOf(':') + 1)] + Translate(type[(type.IndexOf(':') + 1)..]);
        if (type == "Uint") return "Uint32";
        if (type == "Int") return "Int32";
        if (type.StartsWith("array:", StringComparison.Ordinal) || type.StartsWith("static:", StringComparison.Ordinal))
        {
            string element = ElementType(type);
            return type[..^element.Length] + Translate(element);
        }
        if (IsFixed(type, out string fixedElement, out _)) return type[..^fixedElement.Length] + Translate(fixedElement);
        return type;
    }

    /// <summary>What kind of value a type is, whatever the numbers in it: two types of one kind are stored alike.</summary>
    private static string Kind(string type) =>
        Regex.Replace(Translate(type), @"array:\d+,\d+,", "array:").Replace("ptr:", "handle:", StringComparison.Ordinal).Replace("soft:", "handle:", StringComparison.Ordinal);
}
