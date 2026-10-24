using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using W3ModWorkbench.Localisation;

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
        var file = root["file"] as JsonObject ?? throw new InvalidDataException(L.T("Cr2wJsonUpgrade.JsonHasNoFile"));
        var objects = root["objects"] as JsonArray ?? throw new InvalidDataException(L.T("Cr2wJsonUpgrade.JsonHasNoObjects"));
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
        // a behaviour graph's variables before 145, which the engine gives names and numbers when it loads them
        if (from < FirstNamedVariables)
            for (int i = 0; i < objects.Count; i++)
                if (objects[i] is JsonObject { } graph && (string?)graph["$class"] == "CBehaviorGraph" && graph["$tail"] is JsonValue tail)
                    graph["$tail"] = Convert.ToBase64String(NamedVariables(Convert.FromBase64String((string)tail!), objects, file, $"objects[{i}]"));
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

    /// <summary>The first version whose behaviour graphs keep their variables by name.</summary>
    private const int FirstNamedVariables = 145;

    /// <summary>
    /// A behaviour graph's lists of its variables, kept before 145 as just the objects, made as 145 on keeps them:
    /// <list type="bullet">
    /// <item>Before 145: a u32 2, the variables (a compressed count and that many object numbers), a u32 1 and the
    /// events (the same), then the vector variables and, from 139, the internal variables and internal vector
    /// variables, as the variables are.</item>
    /// <item>From 145: a u32 2, the variables, the events as before, then the other three, each list now the next
    /// variable number, how many there are and each one's name (a number into the names) and object number.</item>
    /// </list>
    /// The engine numbers an older file's variables in each list as they come (their <c>varIndex</c>, which it saves
    /// even when it's 0), and names each entry by its variable's name: the game's cooked copies of REDkit's older
    /// graphs show both. It saves the entries in the order of its own name table, which is that of the names the
    /// running game has registered and can't be told from the file; the order doesn't matter to it, as it reads them
    /// into a map, so they're kept in the order they were.
    /// </summary>
    private static byte[] NamedVariables(byte[] bytes, JsonArray objects, JsonObject file, string path)
    {
        var names = file["names"] as JsonArray ?? throw new InvalidDataException(L.T("Cr2wJsonUpgrade.JsonHasNoNames"));
        var lists = new List<List<int>>();
        int at = 0;
        bool ok = bytes.Length >= 4 && BitConverter.ToInt32(bytes) == 2;
        at = 4;
        while (ok && at < bytes.Length)
        {
            if (lists.Count == 1)
            {
                // the events' list starts with a u32 1
                if (at + 4 > bytes.Length || BitConverter.ToInt32(bytes, at) != 1) { ok = false; break; }
                at += 4;
            }
            if (!Upgrader.CompressedCount(bytes, ref at, out int count) || at + 4L * count > bytes.Length) { ok = false; break; }
            lists.Add([.. Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(bytes, at + 4 * i))]);
            at += 4 * count;
        }
        if (!ok || lists.Count is not (3 or 5))
            throw new NotSupportedException(L.T("Cr2wJsonUpgrade.BehaviourGraphWhoseLists", path, Cr2wFile.LastVersion));
        while (lists.Count < 5) lists.Add([]);

        var result = new List<byte>(bytes.Length * 2);
        void U32(int value) => result.AddRange(BitConverter.GetBytes(value));
        U32(2);
        for (int list = 0; list < 5; list++)
        {
            if (list == 1)
            {
                // the events, as they were
                U32(1);
                result.AddRange(CompressedCountBytes(lists[1].Count));
                foreach (int e in lists[1]) U32(e);
                continue;
            }
            U32(lists[list].Count);
            U32(lists[list].Count);
            for (int i = 0; i < lists[list].Count; i++)
            {
                int number = lists[list][i];
                if (number < 1 || number > objects.Count || objects[number - 1] is not JsonObject variable)
                    throw new NotSupportedException(L.T("Cr2wJsonUpgrade.BehaviourGraphVariableIsnt", path, Cr2wFile.LastVersion));
                // one without a name is kept under the empty name, as the engine keeps it
                string name = (string?)variable["name"] ?? "";
                int index = names.Select(n => (string?)n).ToList().IndexOf(name);
                if (index < 0) { index = names.Count; names.Add(name); }
                if (index > ushort.MaxValue) throw new NotSupportedException(L.T("Cr2wJsonUpgrade.TooManyNamesKeep", path));
                result.AddRange(BitConverter.GetBytes((ushort)index));
                U32(number);
                variable["varIndex"] = i;
            }
        }
        return [.. result];
    }

    /// <summary>A count as the engine compresses it (<see cref="PackedInt"/>), an empty one written 0x80 as the engine writes an empty list's.</summary>
    private static byte[] CompressedCountBytes(int value) => PackedInt.CountBytes(value);

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
            string className = (string?)node["$class"] ?? throw new InvalidDataException(L.T("Cr2wJsonUpgrade.HasNoClass", path));
            if (RenamedClasses.TryGetValue(className, out string? renamedClass) && node["$data"] is null)
            {
                node["$class"] = className = renamedClass;
                Note(L.T("Cr2wJsonUpgrade.Class", className), L.T("Cr2wJsonUpgrade.ClassNow", path, renamedClass));
            }
            if (node["$data"] is not null)
            {
                if (dependencies is not null)
                    throw new InvalidDataException(L.T("Cr2wJsonUpgrade.CouldntReadSoCant", path, className, Cr2wFile.LastVersion));
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
            // the 0 an object's properties end with before 139, which later versions have before them instead; a
            // script's arguments come before it, so there it's what's left after them
            if (node["$end"] is JsonValue endValue && endValue.TryGetValue(out bool ended) && !ended
                && node["$arguments"] is not null && (string?)node["$tail"] == "AA==")
                node.Remove("$tail");
            node.Remove("$end");
            if (node["$arguments"] is JsonObject arguments && ArgumentsClass(className) is { } argumentsClass) Struct(arguments, argumentsClass, path + ".$arguments");
            if (node["$events"] is JsonArray events)
                for (int i = 0; i < events.Count; i++)
                    if (events[i] is JsonObject item && (string?)item["$class"] is { } eventClass)
                        Struct(item, eventClass, $"{path}.$events[{i}]");
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
                    throw new NotSupportedException(why is null
                        ? L.T("Cr2wJsonUpgrade.TailNotChecked", path, className, Cr2wFile.LastVersion)
                        : L.T("Cr2wJsonUpgrade.TailUnreadable", path, className, why, Cr2wFile.LastVersion));
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
        /// <item>An entity template kept a list of files inside it until 155, which 164 always has empty: the engine
        /// no longer reads them (components of the older streaming, which the compiled data has in their place).</item>
        /// <item>An effect track item or a curve evaluator keeps the curves it holds; before 155 as a u32 count and
        /// always four of them, the unused ones 0, since as a u16 count and only those it has.</item>
        /// <item>An area's shape (in its node data) is version 3 before 155 and 4 since, the same otherwise.</item>
        /// <item>The classes in <see cref="SameTail"/> keep the same data in every version.</item>
        /// </list>
        /// </summary>
        private byte[]? Tail(string className, byte[] bytes, out string? why)
        {
            why = null;
            // a class of a mod's own scripts (which the engine's and the game's classes don't name) whose data is a node's,
            // as from 162 on, the same in 164: two mods' cooked 163 entities keep 0000000000000000800000, as 4,370 of the
            // game's 164 ones do
            if (from > 161 && !RttiClasses.Value.ContainsKey(className) && !ScriptParents.Value.ContainsKey(className) && NodeLists(bytes) >= 0)
                return bytes;
            if (IsNode(className))
            {
                if (from > 161) return bytes;
                int at = NodeLists(bytes);
                if (at < 0 || at + 64 > bytes.Length) return null;
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
            if (DerivesFrom(className, "CEntityTemplate"))
            {
                // (a character's template keeps the same, empty in every one of REDkit's, as the game's cooked ones)
                if (bytes.Length == 4 && BitConverter.ToInt32(bytes) == 0) return bytes;
                if (from > 155 || bytes.Length < 4) return null;
                int count = BitConverter.ToInt32(bytes), at = 4;
                bool components = false;
                for (int i = 0; i < count; i++)
                {
                    if (at + 4 > bytes.Length) return null;
                    int size = BitConverter.ToInt32(bytes, at);
                    at += 4;
                    if (size < 0 || at + (long)size > bytes.Length) return null;
                    if (size > 0)
                    {
                        // each a CR2W file of components (of the older streaming), which the engine doesn't read any more
                        int? objects = Cr2wObjects(bytes.AsSpan(at, size));
                        if (objects is null) return null;
                        components |= objects > 0;
                    }
                    at += size;
                }
                if (at != bytes.Length) return null;
                if (components)
                    Note(L.T("Cr2wJsonUpgrade.TemplateParts", className), L.T("Cr2wJsonUpgrade.KeptComponentsOlderStreaming", className));
                return [0, 0, 0, 0];
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
                    if (mips >= 0 && at + 4 == bytes.Length) return bytes;
                    // or cooked (an older mod's): a u32 0, how many mips, each its width, height and pitch, then the size
                    // of the mips it keeps, a u32 16 and those bytes; the same in all 394 of the game's cooked 163
                    // textures and all 14,978 of its 164 ones
                    if (BitConverter.ToInt32(bytes) == 0 && 8 + 12L * (uint)mips + 8 <= bytes.Length)
                    {
                        int kept = 8 + 12 * mips;
                        if (BitConverter.ToInt32(bytes, kept + 4) == 16 && kept + 8L + (uint)BitConverter.ToInt32(bytes, kept) == bytes.Length) return bytes;
                    }
                    // a few of REDkit's 163 textures (20) end with a low-resolution copy of their smallest mips instead
                    // of the u32 0: its size, a u32 16, then those bytes. No 164 texture has one (REDkit's 760 and the
                    // game's all end with the 0), so it's left out, the mips themselves kept, as every 164 texture is
                    if (mips >= 0 && at + 8 <= bytes.Length && BitConverter.ToInt32(bytes, (int)at + 4) == 16
                        && at + 8 + (uint)BitConverter.ToInt32(bytes, (int)at) == bytes.Length)
                    {
                        Note(L.T("Cr2wJsonUpgrade.LowMips", className), L.T("Cr2wJsonUpgrade.KeptLowResolutionCopy", className, Cr2wFile.LastVersion));
                        return [.. bytes.AsSpan(0, (int)at), 0, 0, 0, 0];
                    }
                    return null;
                }
                case "CFoliageResource":
                {
                    // its trees and its grass: each a compressed count of groups, each group an id and a compressed count
                    // of instances, 24 bytes each (position, scale, rotation), the same in every version
                    int at = 0;
                    for (int list = 0; list < 2; list++)
                    {
                        if (!CompressedCount(bytes, ref at, out int groups)) return null;
                        for (int g = 0; g < groups; g++)
                        {
                            if (at + 4 > bytes.Length) return null;
                            at += 4;
                            if (!CompressedCount(bytes, ref at, out int instances) || at + 24L * instances > bytes.Length) return null;
                            at += 24 * instances;
                        }
                    }
                    return at == bytes.Length ? bytes : null;
                }
                case "CTerrainTile":
                    // how many of its data buffers there are, each three u16s and a size, then a u32, the same in every version
                    return bytes.Length >= 8 && bytes.Length == 8 + 10L * BitConverter.ToInt32(bytes) ? bytes : null;
                case "CThumbnail":
                    // the picture: its size, then the PNG
                    return bytes.Length >= 4 && BitConverter.ToInt32(bytes) == bytes.Length - 4 ? bytes : null;
                case "CSkeletalAnimation":
                    // its animation data: its size, then the bytes (the engine's own compressed form, the same in every version)
                    return bytes.Length >= 4 && BitConverter.ToInt32(bytes) == bytes.Length - 4 ? bytes : null;
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
                case "CMaterialGraph":
                {
                    // its pixel and vertex shaders' parameters, each a compressed count and that many of (type, register,
                    // name as a number into the names, which saving as 164 keeps in order), then how many compiled
                    // shaders it has and each one's 20-byte key; the same in 163 and 164, and the game's cooked graphs
                    // have the same parameters (with no shaders)
                    int at = 0;
                    for (int list = 0; list < 2; list++)
                    {
                        if (!CompressedCount(bytes, ref at, out int count) || at + 4L * count > bytes.Length) return null;
                        at += 4 * count;
                    }
                    return at + 4 <= bytes.Length && at + 4 + 20L * BitConverter.ToUInt32(bytes, at) == bytes.Length ? bytes : null;
                }
                case "CBehaviorGraph":
                {
                    // its variables' lists as 145 on keep them (see NamedVariables), which 164 keeps alike: a u32 2, the
                    // variables, a u32 1 and the events, then the vector, internal and internal vector variables
                    int at = 0;
                    if (bytes.Length < 4 || BitConverter.ToInt32(bytes) != 2) return null;
                    at = 4;
                    for (int list = 0; list < 5; list++)
                    {
                        if (list == 1)
                        {
                            if (at + 4 > bytes.Length || BitConverter.ToInt32(bytes, at) != 1) return null;
                            at += 4;
                            if (!CompressedCount(bytes, ref at, out int events) || at + 4L * events > bytes.Length) return null;
                            at += 4 * events;
                            continue;
                        }
                        if (at + 8 > bytes.Length) return null;
                        int next = BitConverter.ToInt32(bytes, at), count = BitConverter.ToInt32(bytes, at + 4);
                        at += 8;
                        if (count < 0 || next < count || at + 6L * count > bytes.Length) return null;
                        at += 6 * count;
                    }
                    return at == bytes.Length ? bytes : null;
                }
                case "CBehaviorGraphBlendMultipleNode":
                {
                    // where each input blends: a compressed count, then each its input number (u32) and value (float),
                    // the same in every version
                    int at = 0;
                    return CompressedCount(bytes, ref at, out int count) && at + 8L * count == bytes.Length ? bytes : null;
                }
                case "CPhysicsDestructionResource":
                {
                    // a mesh's data (as CMesh's), then how many chunks it breaks into and each one's record: its size
                    // (counting itself), then what it keeps. 164 added 13 bytes to the end of each record (46 bytes
                    // before, 59 since), which the engine gives the same values when it loads an older one: the game's
                    // cooked copies of REDkit's 88 older destruction files show every one of their 5,117 records so
                    int at = MeshData(bytes);
                    if (at < 0 || at + 4 > bytes.Length) return null;
                    int count = BitConverter.ToInt32(bytes, at);
                    var result = new List<byte>(bytes.Length + 13 * Math.Max(count, 0));
                    result.AddRange(bytes.AsSpan(0, at + 4));
                    at += 4;
                    for (int i = 0; i < count; i++)
                    {
                        if (at + 4 > bytes.Length) return null;
                        int size = BitConverter.ToInt32(bytes, at);
                        if (size == DestructionChunk && at + size <= bytes.Length) result.AddRange(bytes.AsSpan(at, size));
                        else if (size == OldDestructionChunk && at + size <= bytes.Length)
                        {
                            result.AddRange(BitConverter.GetBytes(DestructionChunk));
                            result.AddRange(bytes.AsSpan(at + 4, size - 4));
                            result.AddRange(DestructionChunkAdded);
                        }
                        else return null;
                        at += size;
                    }
                    return at == bytes.Length ? [.. result] : null;
                }
                case "CMesh":
                    // its own data, the same in every version (see MeshData)
                    return MeshData(bytes) == bytes.Length ? bytes : null;
                case "CCookedExplorations":
                    // the cooked explorations: their size, "EXPL", the size again, and their own version (2), the same in
                    // 162 to 164, as the game's cooked copies of all ten of REDkit's have them
                    return bytes.Length >= 16 && BitConverter.ToInt32(bytes) == bytes.Length - 4 && BitConverter.ToUInt32(bytes, 4) == 0x4C505845
                        && BitConverter.ToInt32(bytes, 12) == 2 ? bytes : null;
                case "CWayPointsCollectionsSet":
                    // its collections: how many, each a GUID and an object number, the same as the game's cooked copies
                    return bytes.Length >= 4 && bytes.Length == 4 + 20L * BitConverter.ToUInt32(bytes) ? bytes : null;
                case "CWayPointsCollection":
                    // its waypoints: their size, then them, the same as the game's cooked copies of all 1,301 of REDkit's
                    return bytes.Length >= 4 && BitConverter.ToInt32(bytes) == bytes.Length - 4 ? bytes : null;
                case "CAnimPointCloudLookAtParam":
                {
                    // its triangles: a compressed count, each three u16s, the same as the game's cooked copies (92)
                    int at = 0;
                    return CompressedCount(bytes, ref at, out int triangles) && at + 6L * triangles == bytes.Length ? bytes : null;
                }
                case "CAnimationBufferUncompressed":
                    // the animation's own data, the same in 159 and 164 (the same three animations, saved in each,
                    // keep the same bytes); the game cooks these into compressed ones, so it has none to compare with
                    return from >= 159 ? bytes : null;
                case "CClipMap":
                    // its terrain tiles: how many, then each one's object number (a u32), as REDkit's 164 worlds keep them
                    return bytes.Length >= 4 && bytes.Length == 4 + 4L * BitConverter.ToUInt32(bytes) ? bytes : null;
                case "CUmbraTile":
                    // an occlusion tile's objects: how many, each 12 bytes (an id and an index), then 20 bytes of its
                    // settings, the same in every version and as the game's cooked tiles have them
                    return bytes.Length >= 24 && bytes.Length == 24 + 12L * BitConverter.ToUInt32(bytes) ? bytes : null;
                case "CUmbraScene":
                    // the occlusion grid: its size a side, a tile's size, how many tiles, then each tile's corner (four
                    // floats) and id, 20 bytes, the same in every version
                    return bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 8) == (ulong)BitConverter.ToUInt32(bytes) * BitConverter.ToUInt32(bytes)
                        && bytes.Length == 12 + 20L * BitConverter.ToUInt32(bytes, 8) ? bytes : null;
                case "CTextureArray":
                    if (bytes.Length == 20) return bytes;
                    // before 144 an array kept its own copy of its textures' data (its size, a magic number, 98ABC777,
                    // and the rest); every one of REDkit's newer arrays keeps 20 bytes of zeros instead, the cooked
                    // data being made from the textures it lists (bitmaps), so it's made that
                    if (bytes.Length >= 8 && BitConverter.ToUInt32(bytes, 4) == 0x98ABC777 && BitConverter.ToUInt32(bytes) == bytes.Length - 8)
                    {
                        Note(L.T("Cr2wJsonUpgrade.ArrayData", className), L.T("Cr2wJsonUpgrade.KeptOwnCopyTextures", className));
                        return new byte[20];
                    }
                    why = L.T("Cr2wJsonUpgrade.OlderFormKeepsTextures");
                    return null;
                case "CCubeTexture":
                    return CubeTexture(bytes);
                case "CParticleEmitter":
                {
                    // its cooked data (an older mod's; REDkit's emitters keep none), which ends with FFFFFFFF 00000000
                    // FFFFFFFF and 8 bytes of zeros in 163, and 16 more bytes, always zeros, in 164: every one of the
                    // game's 42 cooked 163 emitters and 8,663 164 ones, and the three 163 copies of the game's own
                    // emitters line up with them so
                    if (from != 163 || bytes.Length < 20) return null;
                    var end = bytes.AsSpan(bytes.Length - 20);
                    return end[..12].SequenceEqual(CookedEmitterEnd) && end[12..].IndexOfAnyExcept((byte)0) < 0 ? [.. bytes, .. new byte[16]] : null;
                }
            }
            if (className == "CCurve")
                // its points, 48 bytes each, as in every version
                return bytes.Length >= 4 && bytes.Length == 4 + 48L * BitConverter.ToInt32(bytes) ? bytes : null;
            if (className.StartsWith("CFXTrackItem", StringComparison.Ordinal) || className.StartsWith("CEvaluator", StringComparison.Ordinal) && className.EndsWith("Curve", StringComparison.Ordinal))
            {
                // the curves an effect track item or a curve evaluator holds: a u16, how many, and those objects;
                // before 155 how many was a u32 and there were always four, the unused ones 0
                if (bytes.Length >= 4 && bytes.Length == 4 + 4 * BitConverter.ToUInt16(bytes, 2)) return bytes;
                if (bytes.Length >= 4 && bytes[3] == 1)
                {
                    // or the curves themselves rather than objects (flagged 1 after how many): each a u16, how many
                    // samples, and those floats; as all 1,138 of the game's cooked 164 effect items keep them
                    int at = 4;
                    for (int i = 0; i < bytes[2] && at + 2 <= bytes.Length; i++) at += 2 + 4 * BitConverter.ToUInt16(bytes, at);
                    return at == bytes.Length ? bytes : null;
                }
                if (from > 154 || bytes.Length != 22) return null;
                int count = BitConverter.ToInt32(bytes, 2);
                if (count is < 0 or > 4) return null;
                for (int i = 0; i < 4; i++)
                    if ((BitConverter.ToInt32(bytes, 6 + i * 4) != 0) != (i < count)) return null;
                return [bytes[0], bytes[1], (byte)count, 0, .. bytes.AsSpan(6, count * 4)];
            }
            if (DerivesFrom(className, "CBehaviorGraphContainerNode"))
                return BehaviorContainer(className, bytes) ? bytes : null;
            return SameTail.Contains(className) ? bytes : null;
        }

        /// <summary>The size of a mesh's own data after its properties (see CMesh in <see cref="Tail"/>), or -1 when it doesn't fit.</summary>
        private static int MeshData(byte[] bytes)
        {
            // its LODs (a compressed count, each its chunks, a compressed count of u16s, and its distance, a float), then
            // its bones' names (u16s, numbers into the names, which saving as 164 keeps in order), their matrices (64
            // bytes each) and two lists of 4 bytes each a bone; the same in every version, and as the game's cooked
            // meshes keep them
            int at = 0;
            if (!CompressedCount(bytes, ref at, out int lods)) return -1;
            for (int lod = 0; lod < lods; lod++)
            {
                if (!CompressedCount(bytes, ref at, out int chunks) || at + 2L * chunks + 4 > bytes.Length) return -1;
                at += 2 * chunks + 4;
            }
            foreach (int size in (ReadOnlySpan<int>)[2, 64, 4, 4])
            {
                if (!CompressedCount(bytes, ref at, out int count) || at + (long)size * count > bytes.Length) return -1;
                at += size * count;
            }
            return at;
        }

        /// <summary>
        /// A cube map's data as 164 keeps it: an id (-1 or 0 where it isn't cooked), a u16 0, its format (u16), its edge
        /// (u16), how many mips (a byte) and a 0, the data's size, a u32 -1, then the data, each face's mips one after
        /// another (face by face; it's cooking that puts them mip by mip). That's the form of 159 on and of all 46 of the
        /// game's cooked cube maps; it's null when the data isn't one of the forms below.
        /// <list type="bullet">
        /// <item>142 to 151 have the same without the -1 after the size, which is put in.</item>
        /// <item>120 to 140 kept it another way: the size of the rest, a magic number (98ABC666), a 0, the format, the
        /// edge, the mips and the size again (u32s), where each face's mips are (offset and size, face by face), the data
        /// and a u32 0. The data is the same, so the header is made the newer way around it. The game's cooked copies
        /// of REDkit's four such cube maps have the same data, laid out mip by mip, but for a few pixels they were
        /// made again from.</item>
        /// </list>
        /// </summary>
        private static byte[]? CubeTexture(byte[] bytes)
        {
            if (bytes.Length < 16) return null;
            uint size = BitConverter.ToUInt32(bytes, 12);
            if (bytes.Length == 20 + (long)size && BitConverter.ToUInt32(bytes, 16) == 0xFFFFFFFF) return bytes;
            if (bytes.Length == 16 + (long)size) return [.. bytes.AsSpan(0, 16), 0xFF, 0xFF, 0xFF, 0xFF, .. bytes.AsSpan(16)];
            if (bytes.Length < 28 || BitConverter.ToUInt32(bytes, 4) != OldCubeMagic || BitConverter.ToUInt32(bytes) != bytes.Length - 8) return null;
            int format = BitConverter.ToInt32(bytes, 12), edge = BitConverter.ToInt32(bytes, 16), mips = BitConverter.ToInt32(bytes, 20);
            if (format is < 0 or > 0xFFFF || edge is < 1 or > 0xFFFF || mips is < 1 or > 32) return null;
            // the faces' mips must follow one another, face by face, from the end of the table to the u32 0 at the end
            int start = 28 + 6 * mips * 8, at = start;
            for (int i = 0; i < 6 * mips; i++)
            {
                int offset = BitConverter.ToInt32(bytes, 28 + 8 * i) + 4, length = BitConverter.ToInt32(bytes, 32 + 8 * i);
                if (offset != at || length < 0) return null;
                at += length;
            }
            if (at + 4 != bytes.Length || BitConverter.ToInt32(bytes, at) != 0) return null;
            var result = new List<byte>(at - start + 20);
            result.AddRange(BitConverter.GetBytes(-1));
            result.AddRange(BitConverter.GetBytes((ushort)0));
            result.AddRange(BitConverter.GetBytes((ushort)format));
            result.AddRange(BitConverter.GetBytes((ushort)edge));
            result.Add((byte)mips);
            result.Add(0);
            result.AddRange(BitConverter.GetBytes(at - start));
            result.AddRange(BitConverter.GetBytes(-1));
            result.AddRange(bytes.AsSpan(start, at - start));
            return [.. result];
        }

        /// <summary>The magic number cube maps before 142 start their data with.</summary>
        private const uint OldCubeMagic = 0x98ABC666;

        /// <summary>A destruction chunk's record's size before 164, and since.</summary>
        private const int OldDestructionChunk = 46, DestructionChunk = 59;

        /// <summary>What 164 added to the end of each destruction chunk's record, as the engine fills it in for an older one.</summary>
        private static readonly byte[] DestructionChunkAdded = [0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0, 0, 0];

        /// <summary>What a cooked emitter's data ends with before its zeros.</summary>
        private static readonly byte[] CookedEmitterEnd = [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];

        /// <summary>
        /// Whether a behaviour graph's container node keeps what it does after its properties in the form every version
        /// has (it's the same from 120 to 164): its child nodes (a compressed count and their object numbers), two lists
        /// of names (each a compressed count and numbers into the names), then a state machine's states and transitions
        /// (each listed as the nodes are), a u32 and its default state, or any other container's output node.
        /// </summary>
        private static bool BehaviorContainer(string className, byte[] bytes)
        {
            int at = 0;
            bool List(int size)
            {
                if (!CompressedCount(bytes, ref at, out int count) || at + (long)size * count > bytes.Length) return false;
                at += size * count;
                return true;
            }
            if (!List(4) || !List(2) || !List(2)) return false;
            if (DerivesFrom(className, "CBehaviorGraphStateMachineNode"))
            {
                if (!List(4) || !List(4)) return false;
                at += 8;
            }
            else at += 4;
            return at == bytes.Length;
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
                why = L.T("Cr2wJsonUpgrade.AreaShapeIsntOne");
                return false;
            }
            return true;
        }

        /// <summary>A compressed count: six bits and more-follows in the first byte (an empty one sometimes written as -0), then seven bits a byte.</summary>
        internal static bool CompressedCount(byte[] bytes, ref int at, out int value) =>
            PackedInt.TryRead(bytes, ref at, out value, out bool top) && (!top || value == 0);

        /// <summary>How many objects a CR2W file has, or null when the bytes aren't one.</summary>
        private static int? Cr2wObjects(ReadOnlySpan<byte> data)
        {
            try { return Cr2wFile.IsCr2w(data) ? Cr2wFile.Read(data.ToArray()).Exports.Count : null; }
            catch (InvalidDataException) { return null; }
        }

        /// <summary>Whether a class is a node (CNode or one derived from it: entities and components).</summary>
        private static bool IsNode(string className) => DerivesFrom(className, "CNode");

        /// <summary>Where a node's two lists of the objects attached to it and below it (each a u32 count and that many u32s) end in its data, or -1 when they don't fit.</summary>
        private static int NodeLists(byte[] bytes)
        {
            int at = 0;
            for (int list = 0; list < 2; list++)
            {
                if (at + 4 > bytes.Length) return -1;
                int count = BitConverter.ToInt32(bytes, at);
                at += 4;
                if (count < 0 || at + count * 4L > bytes.Length) return -1;
                at += count * 4;
            }
            return at;
        }

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
            node.Remove("$pad");
            if (className is "SSimpleCurve" or "SCurveData") Curve(node, className);
            var local = node["$types"] as JsonObject;
            var newLocal = new JsonObject();
            foreach (var (key, value) in node.ToList())
            {
                if (key.StartsWith('$')) continue;
                string stored = Stored(className, key);
                string oldType = local?[key] is JsonValue own && (string?)own is { } ownType ? ownType
                    : oldTypes.TryGetValue(oldClass + "." + stored, out var fileType) ? fileType
                    : EngineType(oldClass, stored) ?? EngineType(className, stored) ?? throw new InvalidDataException(L.T("Cr2wJsonUpgrade.TypeIsntKnown", oldClass, stored));
                string name = key;
                bool moved = false;
                if (Renamed.TryGetValue((className, stored), out var renamed) && !node.ContainsKey(renamed.Name))
                {
                    // moved to its new name, as a struct of the new type with the fields that type has
                    node.Remove(key);
                    name = stored = renamed.Name;
                    moved = true;
                    node[name] = value;
                    Note(L.T("Cr2wJsonUpgrade.Renamed", className, key), L.T("Cr2wJsonUpgrade.NowMovedThereEngine", className, key, name));
                }
                else if (IsGone(className, stored) || IsOtherStruct(oldType, EngineType(className, stored)))
                {
                    // the engine skips what its class doesn't have when it loads the file, so it's gone when it saves it
                    node.Remove(key);
                    Note(L.T("Cr2wJsonUpgrade.Gone", className, stored), L.T("Cr2wJsonUpgrade.IsntEnginesAnyMore", className, stored));
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
            if (Filled.TryGetValue(className, out var filled) && node.Any(p => !p.Key.StartsWith('$')))
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
                : ["times", L.T("Cr2wJsonUpgrade.ControlPoints"), "values", L.T("Cr2wJsonUpgrade.LeftCurveType"), L.T("Cr2wJsonUpgrade.RightCurveType")];
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
            if (HeldAlike(engine) == HeldAlike(translated))
            {
                // the engine converts it when it loads the file and saves it as its own type, as the game's cooked
                // copies of REDkit's older files show; the JSON holds both the same way, so it's written as that
                Note(L.T("Cr2wJsonUpgrade.Converted", className, property), L.T("Cr2wJsonUpgrade.FileButEngineSaved", className, property, oldType, engine));
                return engine;
            }
            Note(L.T("Cr2wJsonUpgrade.Retyped", className, property), L.T("Cr2wJsonUpgrade.FileButEngineKept", className, property, oldType, engine, translated));
            return translated;
        }

        private JsonNode? Value(string oldType, string newType, JsonNode? value, string path, bool moved = false)
        {
            if (value is JsonObject { } raw && raw["$raw"] is not null)
            {
                if (dependencies is not null) throw new InvalidDataException(L.T("Cr2wJsonUpgrade.CouldntReadSoCant2", path, Cr2wFile.LastVersion));
                return value;
            }
            if (IsSoft(oldType))
            {
                if (dependencies is null || value is null) return value;
                string text = (string)value!;
                string file = text.StartsWith("import#", StringComparison.Ordinal) && int.TryParse(text.AsSpan(7), out int n) && n >= 0 && n < dependencies.Count
                    ? dependencies[n] : text.StartsWith("import:", StringComparison.Ordinal) ? text[7..] : throw new InvalidDataException(L.T("Cr2wJsonUpgrade.IsntReference", path));
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
    private static string HeldAlike(string type) =>
        // a buffer either kind is its size and bytes; text, 8-bit, wide or a name, is a string in the JSON
        Regex.Replace(Kind(type), @"(?<=^|[:,])(SharedDataBuffer|StringAnsi|CName)$", m => m.Value == "SharedDataBuffer" ? "DataBuffer" : "String");

    private static string Kind(string type) =>
        Regex.Replace(Translate(type), @"array:\d+,\d+,", "array:").Replace("ptr:", "handle:", StringComparison.Ordinal).Replace("soft:", "handle:", StringComparison.Ordinal);
}
