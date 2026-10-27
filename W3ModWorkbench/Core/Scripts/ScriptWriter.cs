using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>One decompiled .ws file.</summary>
public sealed record ScriptDocument(string Path, string Text, int BytecodeSize);

/// <summary>Writes a <see cref="ScriptBlob"/> back out as WitcherScript source files.</summary>
public static class ScriptWriter
{
    private const int FuncImport = 0x1, FuncNative = 0x200, FuncGlobal = 0x2, FuncFinal = 0x10, FuncEvent = 0x20, FuncLatent = 0x40, FuncEntry = 0x80,
        FuncExec = 0x100, FuncTimer = 0x400, FuncQuest = 0x1000, FuncPrivate = 0x4000, FuncProtected = 0x8000,
        FuncPublic = 0x10000, FuncReplace = 0x40000, FuncAddMethod = 0x80000, FuncWrap = 0x100000,
        FuncStoryScene = 0x800, FuncCleanup = 0x2000, FuncReward = 0x20000;

    private const int KnownFunctionFlags = FuncImport | FuncNative | FuncGlobal | FuncFinal | FuncEvent | FuncLatent | FuncEntry | FuncExec
        | FuncTimer | FuncQuest | FuncPrivate | FuncProtected | FuncPublic | FuncReplace | FuncAddMethod | FuncWrap | FuncStoryScene | FuncCleanup | FuncReward;

    private const int VarEditable = 0x1, VarConst = 0x2, VarInlined = 0x4, VarImport = 0x4000, VarSaved = 0x8000,
        VarPrivate = 0x10000, VarProtected = 0x20000;

    private const int ParamOut = 0x200, ParamOptional = 0x400;
    private const int ClassAbstract = 0x1, ClassImport = 0x2, ClassStatemachine = 0x400;

    public static List<ScriptDocument> Write(ScriptBlob blob, string sourceLabel)
    {
        var enums = new EnumResolver(blob);
        var decompiler = new Decompiler(blob, enums);
        string header = Header(blob, sourceLabel, enums);
        var docs = new List<ScriptDocument>();
        var all = new StringBuilder(header);

        void Add(string path, string body, int bytes)
        {
            body = body.TrimEnd() + "\n";
            docs.Add(new ScriptDocument(path, header + body, bytes));
            all.Append(body).Append('\n');
        }

        if (blob.Enums.Count > 0 || blob.Structs.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var e in blob.Enums) WriteEnum(sb, e, enums.IsExtension(e));
            foreach (var s in blob.Structs) WriteStruct(sb, s);
            Add("scripts\\types.ws", sb.ToString(), 0);
        }

        var defined = blob.Classes.Where(c => !c.IsState).Select(c => c.Name).ToHashSet();
        foreach (var c in blob.Classes.Where(c => !c.IsState))
        {
            var sb = new StringBuilder();
            WriteClass(sb, c, decompiler);
            foreach (var state in blob.Classes.Where(s => s.IsState && s.StateOf == c.Name))
                WriteClass(sb, state, decompiler);
            Add($"scripts\\classes\\{c.Name}.ws", sb.ToString(), CodeSize(blob.Classes.Where(x => x == c || x.StateOf == c.Name)));
        }
        foreach (var state in blob.Classes.Where(s => s.IsState && !defined.Contains(s.StateOf)))
        {
            var sb = new StringBuilder();
            WriteClass(sb, state, decompiler);
            Add($"scripts\\classes\\{state.StateOf}.{state.Name}.ws", sb.ToString(), CodeSize([state]));
        }

        // global functions, including annotated ones (an empty owner means a global target)
        var globals = blob.ReplacedGlobals
            .Concat(blob.Replaced.Concat(blob.AddedMethods).Concat(blob.Wrapped).Where(f => f.Owner.Length == 0))
            .Concat(blob.Globals).ToList();
        if (globals.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var f in globals)
                WriteFunction(sb, f, 0, decompiler);
            Add("scripts\\functions.ws", sb.ToString(), globals.Sum(f => f.Code.Length));
        }

        // annotations, one file per vanilla class they change
        var targets = blob.AddedFields.Select(f => f.Owner)
            .Concat(blob.AddedMethods.Concat(blob.Wrapped).Concat(blob.Replaced).Select(f => f.Owner))
            .Where(t => t.Length > 0).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
        foreach (string target in targets)
        {
            var sb = new StringBuilder();
            foreach (var field in blob.AddedFields.Where(f => f.Owner == target))
                sb.Append($"@addField({target})\n{VarModifiers(field.Flags)}var {field.Name} : {FormatType(field.Type)};\n\n"); // loc-ignore
            var funcs = blob.AddedMethods.Concat(blob.Wrapped).Concat(blob.Replaced).Where(f => f.Owner == target).ToList();
            foreach (var f in funcs)
                WriteFunction(sb, f, 0, decompiler);
            Add($"scripts\\annotations\\{target}.ws", sb.ToString(), funcs.Sum(f => f.Code.Length));
        }

        docs.Insert(0, new ScriptDocument("scripts\\all.ws", all.ToString().TrimEnd() + "\n", blob.AllFunctions.Sum(f => f.Code.Length)));
        return docs;
    }

    private static int CodeSize(IEnumerable<ScriptClass> classes) => classes.Sum(c => c.Functions.Sum(f => f.Code.Length));

    private static string Header(ScriptBlob blob, string source, EnumResolver enums)
    {
        var sb = new StringBuilder();
        sb.Append($"// Decompiled from {source} by W3 Mod Workbench\n"); // loc-ignore
        sb.Append($"// Compiled with REDkit build {blob.Build} ({blob.Configuration}, {blob.Platform})\n"); // loc-ignore
        sb.Append("// Comments, formatting and variable initialisers are not kept in compiled scripts.\n"); // loc-ignore
        if (!enums.HasGameScripts)
            sb.Append("// Game scripts were not found next to this file, so some enum values show as numbers.\n"); // loc-ignore
        sb.Append('\n');
        return sb.ToString();
    }

    private static void WriteEnum(StringBuilder sb, ScriptEnum e, bool extension)
    {
        if (extension)
            sb.Append("// Adds values to the game's enum of the same name\n"); // loc-ignore
        sb.Append($"enum {e.Name}\n{{\n"); // loc-ignore
        foreach (var (name, value) in e.Members)
            sb.Append(value == int.MaxValue ? $"\t{name},\n" : $"\t{name} = {value},\n");
        sb.Append("}\n\n");
    }

    private static void WriteStruct(StringBuilder sb, ScriptStruct s)
    {
        sb.Append($"{((s.Flags & 2) != 0 ? "import " : "")}struct {s.Name}\n{{\n"); // loc-ignore
        foreach (var p in s.Properties)
            WriteProperty(sb, p);
        WriteDefaults(sb, s.Defaults, s.Properties);
        sb.Append("}\n\n");
    }

    private static void WriteClass(StringBuilder sb, ScriptClass c, Decompiler decompiler)
    {
        if (c.IsState)
            sb.Append($"state {c.Name} in {c.StateOf}{(c.Base.Length > 0 ? " extends " + c.Base : "")}\n{{\n"); // loc-ignore
        else
        {
            string mods = ((c.Flags & ClassImport) != 0 ? "import " : "") + ((c.Flags & ClassStatemachine) != 0 ? "statemachine " : "") + ((c.Flags & ClassAbstract) != 0 ? "abstract " : "");
            sb.Append($"{mods}class {c.Name}{(c.Base.Length > 0 ? " extends " + c.Base : "")}\n{{\n"); // loc-ignore
        }

        foreach (var p in c.Properties)
            WriteProperty(sb, p);
        WriteDefaults(sb, c.Defaults, c.Properties);
        if (c.Properties.Count > 0 || c.Defaults.Count > 0)
            sb.Append('\n');

        foreach (var f in c.Functions)
            WriteFunction(sb, f, 1, decompiler, (c.Flags & ClassAbstract) != 0);
        if (sb[^1] == '\n' && sb[^2] == '\n') sb.Length--;
        sb.Append("}\n\n");
    }

    private static void WriteDefaults(StringBuilder sb, List<ScriptDefault> defaults, List<ScriptProperty> props)
    {
        foreach (var d in defaults)
        {
            string type = props.FirstOrDefault(p => p.Name == d.Property)?.Type ?? "";
            if (d.Children.Count == 0)
                sb.Append($"\tdefault {d.Property} = {DefaultValue(type, d.Value)};\n"); // loc-ignore
            else
                WriteDefaultChildren(sb, d.Property, type, d.Children);
        }
        foreach (var p in props.Where(p => p.Hint.Length > 0))
            sb.Append($"\thint {p.Name} = {Decompiler.Quote(p.Hint)};\n"); // loc-ignore
    }

    private static string DefaultValue(string type, string value)
    {
        string element = type.StartsWith("array:", StringComparison.Ordinal) ? type[(type.LastIndexOf(',') + 1)..] : type;
        return element switch
        {
            "String" => Decompiler.Quote(value),
            "CName" => $"'{value}'",
            _ => value,
        };
    }

    /// <summary>
    /// Struct fields come out as "default prop.field = value". Array defaults have no source form
    /// we can be sure of, so their values are listed as a comment.
    /// </summary>
    private static void WriteDefaultChildren(StringBuilder sb, string path, string type, List<ScriptDefaultNode> children)
    {
        if (children.All(c => c.Key.Length == 0))
        {
            sb.Append($"\t// default {path}: {children.Count} array value{(children.Count == 1 ? "" : "s")}\n"); // loc-ignore
            foreach (var c in children)
                sb.Append($"\t//     {(c.Children.Count == 0 ? DefaultValue(type, c.Value) : "{ ... }")}\n");
            return;
        }
        foreach (var c in children)
        {
            if (c.Children.Count == 0)
                sb.Append($"\tdefault {path}.{c.Key} = {c.Value};\n"); // loc-ignore
            else
                WriteDefaultChildren(sb, $"{path}.{c.Key}", "", c.Children);
        }
    }

    private static string VarModifiers(int flags)
    {
        var sb = new StringBuilder();
        if ((flags & VarPrivate) != 0) sb.Append("private ");
        else if ((flags & VarProtected) != 0) sb.Append("protected ");
        if ((flags & VarImport) != 0) sb.Append("import ");
        if ((flags & VarEditable) != 0) sb.Append("editable ");
        if ((flags & VarSaved) != 0) sb.Append("saved ");
        if ((flags & VarConst) != 0) sb.Append("const ");
        if ((flags & VarInlined) != 0) sb.Append("inlined ");
        return sb.ToString();
    }

    private static void WriteProperty(StringBuilder sb, ScriptProperty property)
    {
        string modifiers = VarModifiers(property.Flags);
        if ((property.Flags & 0x80000) == 0)
        {
            sb.Append($"\t{modifiers}var {property.Name} : {FormatType(property.Type)};\n"); // loc-ignore
            return;
        }
        modifiers = modifiers.Replace("const ", "");
        string optional = (property.Flags & 0x100000) != 0 ? "optional " : "";
        sb.Append($"\t{modifiers}{optional}autobind {property.Name} : {FormatType(property.Type)}");
        if (property.Binding.Length > 0) sb.Append(" = ").Append(Decompiler.Quote(property.Binding));
        sb.Append(";\n");
    }

    private static void WriteFunction(StringBuilder sb, ScriptFunction f, int depth, Decompiler decompiler, bool abstractOwner = false)
    {
        string pad = new('\t', depth);
        string? annotation = f.Kind switch
        {
            FunctionKind.AddMethod => $"@addMethod({f.Owner})",
            FunctionKind.Wrap => $"@wrapMethod({f.Owner})",
            FunctionKind.Replace => $"@replaceMethod({f.Owner})",
            FunctionKind.ReplaceGlobal => "@replaceMethod()",
            _ => null,
        };
        if (annotation is not null)
            sb.Append(pad).Append(annotation).Append('\n');

        var mods = new StringBuilder();
        if ((f.Flags & FuncPrivate) != 0) mods.Append("private ");
        else if ((f.Flags & FuncProtected) != 0) mods.Append("protected ");
        if ((f.Flags & FuncImport) != 0 || ((f.Flags & FuncNative) != 0 && f.Code.Length == 0 && !abstractOwner)) mods.Append("import ");
        if ((f.Flags & FuncFinal) != 0) mods.Append("final ");
        if ((f.Flags & FuncEntry) != 0) mods.Append("entry ");
        if ((f.Flags & FuncLatent) != 0) mods.Append("latent ");
        if ((f.Flags & FuncExec) != 0) mods.Append("exec ");
        if ((f.Flags & FuncQuest) != 0) mods.Append("quest ");
        if ((f.Flags & FuncTimer) != 0) mods.Append("timer ");
        if ((f.Flags & FuncStoryScene) != 0) mods.Append("storyscene ");
        if ((f.Flags & FuncCleanup) != 0) mods.Append("cleanup ");
        if ((f.Flags & FuncReward) != 0) mods.Append("reward ");
        if (f.Kind is FunctionKind.Wrap or FunctionKind.Replace or FunctionKind.ReplaceGlobal) mods.Clear();
        // annotations always use "function", even when the target is an event
        mods.Append((f.Flags & FuncEvent) != 0 && annotation is null ? "event " : "function ");

        var parameters = f.Params.Select(p =>
            ((p.Flags & ParamOut) != 0 ? "out " : "") + ((p.Flags & ParamOptional) != 0 ? "optional " : "")
            + $"{p.Name} : {FormatType(p.Type)}");
        string ret = f.Return.Count > 0 && (f.Flags & FuncEvent) == 0 ? " : " + FormatType(f.Return[0].Type) : "";
        int unknown = f.Flags & ~KnownFunctionFlags;
        string note = unknown != 0 ? $"  // flags 0x{unknown:X}" : "";
        sb.Append($"{pad}{mods}{f.Name}({string.Join(", ", parameters)}){ret}{note}\n");

        if (f.Code.Length == 0 || (f.Flags & FuncImport) != 0)
        {
            sb.Length--;
            sb.Append(";\n\n");
            return;
        }

        sb.Append(pad).Append("{\n");
        foreach (var local in f.Locals)
            sb.Append($"{pad}\tvar {local.Name} : {FormatType(local.Type)};\n"); // loc-ignore
        if (f.Locals.Count > 0)
            sb.Append('\n');

        List<string> body;
        try
        {
            body = decompiler.DecompileBody(f, depth + 1);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            body = [$"{pad}\t// This function could not be decompiled: {ex.Message}"]; // loc-ignore
        }
        foreach (var line in body)
            sb.Append(line).Append('\n');
        sb.Append(pad).Append("}\n\n");
    }

    /// <summary>One type from the text, then past the comma after it. An array or map brings two numbers and its own types.</summary>
    private static string ParseType(string t, ref int at)
    {
        bool map = t.AsSpan(at).StartsWith("map:", StringComparison.Ordinal);
        if (map || t.AsSpan(at).StartsWith("array:", StringComparison.Ordinal))
        {
            at += map ? 4 : 6;
            for (int numbers = 0; numbers < 2; numbers++) at = t.IndexOf(',', at) + 1;
            if (!map) return $"array<{ParseType(t, ref at)}>";
            string key = ParseType(t, ref at);
            return $"map<{key}, {ParseType(t, ref at)}>";
        }
        int end = t.IndexOf(',', at);
        if (end < 0) end = t.Length;
        string plain = t[at..end];
        at = Math.Min(end + 1, t.Length);
        return FormatType(plain);
    }

    public static string FormatType(string t)
    {
        if (t.StartsWith("map:", StringComparison.Ordinal))
        {
            int at = 0;
            return ParseType(t, ref at); // "map:2,0,Int32,Float" is map<int, float>
        }
        if (t.StartsWith("array:", StringComparison.Ordinal))
        {
            int comma = t.IndexOf(',', t.IndexOf(',') + 1);
            return $"array<{FormatType(comma > 0 ? t[(comma + 1)..] : t[6..])}>";
        }
        foreach (var prefix in new[] { "handle:", "ptr:", "soft:" })
            if (t.StartsWith(prefix, StringComparison.Ordinal))
                return FormatType(t[prefix.Length..]);
        return t switch
        {
            "Int32" => "int",
            "Float" => "float",
            "Bool" => "bool",
            "String" => "string",
            "CName" => "name",
            "Uint8" => "byte",
            _ => t,
        };
    }

    /// <summary>A short label such as "modX\precompiled.rsblob" for the file header.</summary>
    public static string Label(string blobPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(blobPath) ?? "");
        string owner = dir.Name.Equals("content", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null ? dir.Parent.Name : dir.Name;
        return $"{owner}\\{Path.GetFileName(blobPath)}";
    }
}
