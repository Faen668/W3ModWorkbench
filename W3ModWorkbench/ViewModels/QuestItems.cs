using System.Globalization;
using System.IO;
using System.Text;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Quests;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.ViewModels;

/// <summary>The groups REDkit's add-block menu puts quest blocks in, which the quest graph colours them by.</summary>
public enum QuestCategory { Phase, PhaseIO, Flow, Logical, Scenes, Scripting, GameSystems, Gameplay, Journal, Camera, Notes, Other }

/// <summary>
/// What the quest graph and its list say about a block: its title, a line saying what it does, its kind, the phase it's
/// in, its inputs and outputs as drawn, and the text a search looks through. Worked out once when a file is read.
/// </summary>
public sealed class QuestBlockItem
{
    public required QuestBlock Block { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    /// <summary>The block's kind in the game's words: its class's default name ("Script", "Pause"), else its class.</summary>
    public required string Kind { get; init; }
    public required QuestCategory Category { get; init; }
    /// <summary>The embedded phases it's in, outermost first, "Setup › Waiting"; empty in the file's own graph.</summary>
    public required string Phase { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public required IReadOnlyList<string> Outputs { get; init; }
    public required string SearchText { get; init; }

    public string ClassName => Block.ClassName;
    public string Guid => Block.Guid is { } g ? g.ToString() : "";
    public string OutputsText => string.Join(", ", Outputs);
    public string Comment => Block.Comment;
    /// <summary>Whether another block in the files looked at has the same GUID.</summary>
    public bool Duplicate { get; set; }
    /// <summary>What a comparison with another copy of the file found, in words; empty with none, or nothing changed.</summary>
    public string Change { get; set; } = "";
    /// <summary>What a comparison found, for marking it.</summary>
    public Core.Quests.QuestChange ChangeKind { get; set; }
    /// <summary>A block only the copy compared with has: listed, but not in the file shown.</summary>
    public bool Elsewhere { get; init; }

    /// <summary>The same GUID as REDkit shows it: four groups of eight upper-case hex digits, the bytes read as little-endian numbers.</summary>
    public static string RedkitForm(Guid guid)
    {
        Span<byte> b = stackalloc byte[16];
        guid.TryWriteBytes(b);
        var parts = new string[4];
        for (int i = 0; i < 4; i++) parts[i] = BitConverter.ToUInt32(b[(i * 4)..]).ToString("X8", CultureInfo.InvariantCulture);
        return string.Join("-", parts);
    }

    /// <summary>
    /// A GUID typed or pasted in either form, Native Quest Hook's (the .NET form, 8-4-4-4-12) or REDkit's (8-8-8-8); null
    /// when it's neither.
    /// </summary>
    public static Guid? ParseGuid(string text)
    {
        text = text.Trim().Trim('"', '\'', '{', '}', '(', ')');
        if (System.Guid.TryParseExact(text, "D", out var g) || System.Guid.TryParseExact(text, "N", out g)) return g;
        var parts = text.Split('-');
        if (parts.Length != 4 || parts.Any(p => p.Length != 8)) return null;
        Span<byte> bytes = stackalloc byte[16];
        for (int i = 0; i < 4; i++)
        {
            if (!uint.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint n)) return null;
            BitConverter.TryWriteBytes(bytes[(i * 4)..], n);
        }
        return new Guid(bytes);
    }

    public static QuestCategory CategoryOf(string className) => className switch
    {
        "CQuestPhaseBlock" => QuestCategory.Phase,
        "CQuestPhaseInputBlock" or "CQuestPhaseOutputBlock" or "CQuestStartBlock" or "CQuestEndBlock" => QuestCategory.PhaseIO,
        "CQuestPauseConditionBlock" or "CQuestConditionBlock" or "CQuestCutControlBlock" or "CQuestRandomBlock" => QuestCategory.Flow,
        "CQuestAndBlock" or "CQuestXorBlock" => QuestCategory.Logical,
        "CQuestSceneBlock" or "CQuestInteractionDialogBlock" or "CQuestContextDialogBlock" or "CQuestScenePrepareBlock" => QuestCategory.Scenes,
        "CQuestScriptBlock" or "CQuestScriptedActionsBlock" or "CQuestPokeScriptedActionsBlock" or "CQuestResetScriptedActionsBlock"
            or "CQuestRiderScriptedActionsBlock" or "CQuestCombatManagerBlock" or "CQuestMonsterCombatManagerBlock" => QuestCategory.Scripting,
        "CQuestFactsDBChangingBlock" or "CQuestLayersHiderBlock" or "CQuestCheckpointBlock" or "CQuestChangeWorldBlock" or "CDeniedAreaBlock"
            or "CQuestManageFastTravelBlock" or "CManageSwitchBlock" or "CQuestGraphMinigameBlock" or "CQuestTimeManagementBlock"
            or "CQuestDeletionMarkerBlock" => QuestCategory.GameSystems,
        "CDescriptionGraphBlock" or "CCommentGraphBlock" => QuestCategory.Notes,
        "CQuestContentActivatorBlock" => QuestCategory.Other,
        _ when className.StartsWith("CJournal", StringComparison.Ordinal) => QuestCategory.Journal,
        _ when className.StartsWith("CQuestStaticCamera", StringComparison.Ordinal) => QuestCategory.Camera,
        _ when className.StartsWith("CQuest", StringComparison.Ordinal) => QuestCategory.Gameplay,
        _ => QuestCategory.Other,
    };

    public static string CategoryName(QuestCategory category) => category switch
    {
        QuestCategory.Phase => L.T("QuestGraph.Category.Phase"),
        QuestCategory.PhaseIO => L.T("QuestGraph.Category.PhaseIO"),
        QuestCategory.Flow => L.T("QuestGraph.Category.Flow"),
        QuestCategory.Logical => L.T("QuestGraph.Category.Logical"),
        QuestCategory.Scenes => L.T("QuestGraph.Category.Scenes"),
        QuestCategory.Scripting => L.T("QuestGraph.Category.Scripting"),
        QuestCategory.GameSystems => L.T("QuestGraph.Category.GameSystems"),
        QuestCategory.Gameplay => L.T("QuestGraph.Category.Gameplay"),
        QuestCategory.Journal => L.T("QuestGraph.Category.Journal"),
        QuestCategory.Camera => L.T("QuestGraph.Category.Camera"),
        QuestCategory.Notes => L.T("QuestGraph.Category.Notes"),
        _ => L.T("QuestGraph.Category.Other"),
    };

    /// <summary>
    /// Every block of a file, described. <paramref name="journal"/> names a journal path's entries ("mq4005_sword ›
    /// Objective › Read Berengar's notes"), or null when they can't be found.
    /// </summary>
    public static List<QuestBlockItem> Describe(QuestFile file, Func<RedObject, Cr2wFile, string?>? journal = null)
    {
        var items = new List<QuestBlockItem>();
        foreach (var block in file.AllBlocks()) items.Add(Describe(block, file.Cr2w, journal));
        return items;
    }

    public static QuestBlockItem Describe(QuestBlock block, Cr2wFile file, Func<RedObject, Cr2wFile, string?>? journal = null)
    {
        var o = block.Object;
        string kind = QuestNames.Default(block.ClassName) ?? QuestNames.Short(block.ClassName);
        string detail = DetailOf(block, file, journal);
        // REDkit titles a script or reward block by what it runs, "Script [EnableDynamicMappin]"; it keeps that as the caption
        string title = block.ClassName is "CDescriptionGraphBlock" or "CCommentGraphBlock"
            ? (string.IsNullOrEmpty(block.Caption) ? kind : block.Caption)
            : !string.IsNullOrEmpty(block.StoredName) ? block.StoredName
            : !string.IsNullOrEmpty(block.Caption) ? block.Caption
            : block.IsPhaseInput || block.IsPhaseOutput ? block.PhaseSocket! : kind;
        var phase = new List<string>();
        for (var owner = block.Graph.Owner; owner is not null; owner = owner.Graph.Owner) phase.Insert(0, owner.Name);

        var search = new StringBuilder();
        search.Append(title).Append('\n').Append(kind).Append('\n').Append(block.ClassName).Append('\n').Append(block.Comment).Append('\n').Append(detail);
        if (block.Guid is { } g) search.Append('\n').Append(g.ToString()).Append('\n').Append(g.ToString("N")).Append('\n').Append(RedkitForm(g));
        foreach (string text in Strings(o, file, 0)) search.Append('\n').Append(text);

        return new QuestBlockItem
        {
            Block = block,
            Title = title,
            Detail = detail,
            Kind = kind,
            Category = CategoryOf(block.ClassName),
            Phase = string.Join(" › ", phase),
            Inputs = InputsOf(block),
            Outputs = OutputsOf(block),
            SearchText = search.ToString(),
        };
    }

    /// <summary>A block's inputs as drawn: its input sockets in an uncooked file, else the inputs links lead to (In when none do).</summary>
    public static IReadOnlyList<string> InputsOf(QuestBlock block)
    {
        if (block.IsPhaseInput || block.ClassName is "CQuestStartBlock" or "CDescriptionGraphBlock" or "CCommentGraphBlock") return [];
        var names = new List<string>();
        if (block.Sockets.Count > 0)
            names.AddRange(block.Sockets.Where(s => !s.IsOutput && (s.Shown || block.Incoming.Any(l => l.Input == s.Name))).Select(s => s.Name));
        else
        {
            // an embedded phase's inputs are its own input blocks
            if (block.Embedded is { } inner) names.AddRange(inner.Blocks.Where(b => b.IsPhaseInput).Select(b => b.PhaseSocket!));
            names.AddRange(block.Incoming.Where(l => !QuestBlock.IsCut(l)).Select(l => l.Input));
            if (names.Count == 0 && block.ClassName is not ("CQuestAndBlock" or "CQuestXorBlock")) names.Add("In");
        }
        return [.. names.Select(Socket).Distinct()];
    }

    /// <summary>A block's outputs as drawn: its output sockets, or the ones its file keeps; an embedded phase's are its output blocks.</summary>
    public static IReadOnlyList<string> OutputsOf(QuestBlock block)
    {
        if (block.IsPhaseOutput || block.ClassName is "CQuestEndBlock" or "CDescriptionGraphBlock" or "CCommentGraphBlock") return [];
        // an uncooked block's outputs REDkit hides as unused stay hidden, unless something links from them
        var names = block.Sockets.Count > 0
            ? block.Sockets.Where(s => s.IsOutput && (s.Shown || block.Links.Any(l => l.Output == s.Name))).Select(s => s.Name).ToList()
            : block.Outputs().ToList();
        if (block.Sockets.Count == 0 && block.Embedded is { } inner)
            names.AddRange(inner.Blocks.Where(b => b.IsPhaseOutput).Select(b => b.PhaseSocket!));
        return [.. names.Select(Socket).Distinct()];
    }

    /// <summary>A socket's name as shown: an unnamed one is None, as the game and Native Quest Hook name it.</summary>
    public static string Socket(string name) => string.IsNullOrWhiteSpace(name) ? "None" : name;

    // ---------------------------------------------------------------- what a block does, in a line

    private static string DetailOf(QuestBlock block, Cr2wFile file, Func<RedObject, Cr2wFile, string?>? journal)
    {
        var o = block.Object;
        switch (block.ClassName)
        {
            case "CQuestScriptBlock":
            {
                string fn = o["functionName"] as string ?? "";
                var args = file.Array(o, "parameters").OfType<RedObject>()
                    .Select(p => $"{p["name"]}={(p["value"] is byte[] raw ? Variant(raw, file) : Value(p["value"], file))}").Take(4).ToList();
                return args.Count == 0 ? fn : $"{fn}({string.Join(", ", args)})";
            }
            case "CQuestFactsDBChangingBlock":
            {
                string fact = o["factID"] as string ?? "";
                long value = o.Integer("value", 1);
                return o["setExactValue"] is true ? $"{fact} = {value}" : $"{fact} {(value < 0 ? "-" : "+")}{Math.Abs(value)}";
            }
            case "CQuestConditionBlock":
                return o["questCondition"] is RedHandle h && file.Object(h) is { } c ? Condition(c, file) : "";
            case "CQuestPauseConditionBlock":
                return string.Join("  ·  ", file.Array(o, "conditions").OfType<RedHandle>().Select(file.Object).OfType<RedObject>().Select(c => Condition(c, file)));
            case "CQuestSceneBlock" or "CQuestInteractionDialogBlock" or "CQuestContextDialogBlock":
                return o["scene"] is RedHandle s && file.PathOf(s) is { } scene ? Path.GetFileNameWithoutExtension(scene) : "";
            case "CQuestPhaseBlock":
                return block.ExternalPhase is { } ext ? Path.GetFileName(ext) : block.Embedded is { } inner ? L.T("Count.Block", inner.Blocks.Count) : "";
            case "CQuestRewardBlock":
                return o["rewardName"] as string ?? "";
            case "CQuestLayersHiderBlock":
                return o["world"] is string world && world.Length > 0 ? Path.GetFileNameWithoutExtension(world) : "";
            case "CQuestStoryPhaseSetterBlock":
                return string.Join(", ", file.Array(o, "spawnsets").OfType<RedHandle>().Select(file.Object).OfType<RedObject>()
                    .Select(a => a["phase"] as string ?? (a["spawnset"] is RedHandle sh ? Path.GetFileNameWithoutExtension(file.PathOf(sh) ?? "") : "")));
            case "CQuestDeletionMarkerBlock":
                return L.T("Count.Guid", block.DeletedGuids.Count);
            case "CDescriptionGraphBlock":
                return block.DescriptionText ?? "";
        }
        if (block.ClassName.StartsWith("CJournal", StringComparison.Ordinal))
            foreach (var (_, _, value) in o.Properties)
                if (value is RedHandle jh && file.Object(jh) is { ClassName: "CJournalPath" } path)
                    return journal?.Invoke(path, file) ?? JournalFile(path, file);
        // anything else: its first tag or name
        foreach (var (name, type, value) in o.Properties)
        {
            if (name is "name" or "comment" or "caption" or "guid") continue;
            if (value is string s && s.Length > 0 && type is "CName" or "String") return s;
            if (type == "TagList" && value is byte[] raw && file.Tags(raw) is { Count: > 0 } tags) return string.Join(", ", tags);
        }
        return "";
    }

    /// <summary>The journal file a journal path ends in, when its entries can't be named.</summary>
    private static string JournalFile(RedObject path, Cr2wFile file)
    {
        var at = path;
        for (int i = 0; i < 12 && at["child"] is RedHandle h && file.Object(h) is { } child; i++) at = child;
        return at["resource"] is RedHandle r && file.PathOf(r) is { } p ? Path.GetFileNameWithoutExtension(p) : "";
    }

    private static readonly Dictionary<string, string> Compare = new()
    {
        ["CF_Equal"] = "==", ["CF_NotEqual"] = "!=", ["CF_Less"] = "<", ["CF_LessEqual"] = "<=", ["CF_Greater"] = ">", ["CF_GreaterEqual"] = ">=",
    };

    /// <summary>A quest condition in a few words: a fact's test as the game writes it, else its class and what it's about.</summary>
    public static string Condition(RedObject c, Cr2wFile file)
    {
        string Enum(string name, string otherwise) => c[name] is RedEnum e && e.Values.Count > 0 ? e.Values[0] : c[name] as string ?? otherwise;
        switch (c.ClassName)
        {
            case "CQuestFactsDBCondition":
            {
                string op = Compare.GetValueOrDefault(Enum("compareFunc", "CF_Equal"), "==");
                string fact = c["factId"] as string ?? "";
                return Enum("queryFact", "QF_Value") == "QF_DoesExist" ? L.T("QuestGraph.FactExists", fact) : $"{fact} {op} {c.Integer("value")}";
            }
            case "CQuestLogicOperationCondition":
            {
                string op = Enum("logicOperation", "LO_And") switch { "LO_Or" => " || ", "LO_Xor" => " ^ ", "LO_Nand" => " !& ", "LO_Nor" => " !| ", _ => " && " };
                var parts = file.Array(c, "conditions").OfType<RedHandle>().Select(file.Object).OfType<RedObject>().Select(x => Condition(x, file)).ToList();
                return parts.Count == 0 ? QuestNames.Short(c.ClassName) : "(" + string.Join(op, parts) + ")";
            }
            case "CQuestInsideTriggerCondition":
                return c["isInside"] is false ? L.T("QuestGraph.Outside", c["triggerTag"] as string ?? "") : L.T("QuestGraph.Inside", c["triggerTag"] as string ?? "");
            case "CQuestHiResRealtimeDelayCondition":
                return L.T("QuestGraph.Seconds", (c.Integer("hours") * 3600 + c.Integer("minutes") * 60 + c.Integer("seconds") + c.Integer("miliseconds") / 1000.0)
                    .ToString("0.###", CultureInfo.CurrentCulture));
            case "CQuestRealtimeDelayCondition":
                return L.T("QuestGraph.Seconds", c.Struct("howLong")?.Integer("m_seconds") ?? 0);
        }
        foreach (var (name, type, value) in c.Properties)
            if (value is string s && s.Length > 0 && type is "CName" or "String") return $"{QuestNames.Short(c.ClassName)}: {s}";
        return QuestNames.Short(c.ClassName);
    }

    /// <summary>
    /// A CVariant's value in words, as a script block's parameters keep theirs: its type's name, a size that counts
    /// itself, then the value. The common types are read; any other shows its type.
    /// </summary>
    public static string Variant(byte[] raw, Cr2wFile file)
    {
        if (raw.Length < 6) return "";
        int nameIndex = BitConverter.ToUInt16(raw, 0);
        string type = nameIndex < file.Names.Count ? file.Names[nameIndex] : "";
        var v = raw.AsSpan(6);
        try
        {
            switch (type)
            {
                case "Int32" or "Int": return BitConverter.ToInt32(v).ToString(CultureInfo.InvariantCulture);
                case "Uint32" or "Uint": return BitConverter.ToUInt32(v).ToString(CultureInfo.InvariantCulture);
                case "Int16": return BitConverter.ToInt16(v).ToString(CultureInfo.InvariantCulture);
                case "Uint8" or "Int8": return v[0].ToString(CultureInfo.InvariantCulture);
                case "Float": return BitConverter.ToSingle(v).ToString("0.###", CultureInfo.InvariantCulture);
                case "Bool": return v[0] != 0 ? "true" : "false";
                case "CName":
                {
                    int n = BitConverter.ToUInt16(v);
                    return n < file.Names.Count ? file.Names[n] : "";
                }
                case "String":
                {
                    int at = 0;
                    if (!Core.PackedInt.TryRead(v, ref at, out int length, out bool narrow)) return "";
                    int bytes = narrow ? length : length * 2;
                    return narrow ? Encoding.Latin1.GetString(v.Slice(at, bytes)) : Encoding.Unicode.GetString(v.Slice(at, bytes));
                }
                case "TagList": return string.Join(",", file.Tags(v.ToArray()));
            }
            // an enum is the name of its value
            if (type.Length > 1 && type[0] == 'E' && char.IsUpper(type[1]) && v.Length >= 2)
            {
                int n = BitConverter.ToUInt16(v);
                return n < file.Names.Count ? file.Names[n] : type;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or IndexOutOfRangeException) { }
        return type;
    }

    private static string Value(object? value, Cr2wFile file) => value switch
    {
        RedObject v when v["value"] is { } inner => Value(inner, file),
        RedEnum e => string.Join("|", e.Values),
        RedHandle h => file.PathOf(h) is { } p ? Path.GetFileName(p) : file.Object(h)?.ClassName ?? "",
        byte[] raw => string.Join(",", file.Tags(raw)),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        null => "",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Every name, string and path in an object and the objects it holds, for a search to find a block by any of them.</summary>
    private static IEnumerable<string> Strings(RedObject o, Cr2wFile file, int depth)
    {
        if (depth > 6) yield break;
        yield return o.ClassName;
        foreach (var (_, type, value) in o.Properties)
        {
            if (type == "CVariant" && value is byte[] variant) { yield return Variant(variant, file); continue; }
            foreach (string s in StringsOf(value, file, depth)) yield return s;
        }
    }

    private static IEnumerable<string> StringsOf(object? value, Cr2wFile file, int depth)
    {
        switch (value)
        {
            case string s when s.Length > 0: yield return s; break;
            case RedEnum e: foreach (string v in e.Values) yield return v; break;
            case RedObject r: foreach (string s in Strings(r, file, depth + 1)) yield return s; break;
            case RedHandle h:
                if (file.PathOf(h) is { } p) yield return p;
                else if (!h.Soft && file.Object(h) is { } inner && !inner.ClassName.EndsWith("Block", StringComparison.Ordinal) && inner.ClassName != "CQuestGraph")
                    foreach (string s in Strings(inner, file, depth + 1)) yield return s;
                break;
            case List<object?> list: foreach (var item in list) foreach (string s in StringsOf(item, file, depth)) yield return s; break;
            case byte[] raw when raw.Length > 0 && raw[0] > 0 && raw[0] < 64: foreach (string t in file.Tags(raw)) yield return t; break;
            case Guid g: yield return g.ToString(); break;
        }
    }
}
