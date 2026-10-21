using System.Text;
using W3ModWorkbench.Core.Scripts.Standalone;

namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>A declaration written out from the engine's class list, for a name no script declares: to read, never a file.</summary>
/// <param name="Path">Where it's said to be, for the tab: native\CFoo.ws.</param>
/// <param name="Offset">Where the name is in <paramref name="Text"/>, to put the cursor on.</param>
public sealed record NativeDeclaration(string Name, string Path, string Text, int Offset);

/// <summary>
/// The engine's own classes, structs, enums and functions as WitcherScript, generated on demand from the engine's class
/// list the built-in compiler carries (what the game's <c>-dumprtti</c> writes): a class's parent, its properties with
/// their types and its functions; an enum's values. The list names a native function but not its parameters, so a
/// function the game's scripts declare with <c>import</c> is shown with that declaration's signature, and any other is
/// shown by name with a note that its parameters aren't known.
/// </summary>
public static class NativeDeclarations
{
    private static readonly Lazy<Rtti> Engine = new(Rtti.LoadBuiltIn);

    /// <summary>Whether the engine has a class, struct, enum, global function or simple type of this name.</summary>
    public static bool Has(string name) =>
        Engine.Value.Classes.Any(c => c.Name == name) || Engine.Value.Enums.Any(e => e.Name == name)
        || Engine.Value.GlobalFunctions.Contains(name) || Engine.Value.SimpleTypes.Contains(name);

    /// <summary>
    /// The declaration of the engine's class, enum, function or simple type <paramref name="name"/>, or of the class
    /// that has a native member of that name when <paramref name="owner"/> is given, with the cursor on the member.
    /// Null when the engine has nothing of that name.
    /// </summary>
    /// <param name="layers">The scripts to take native functions' signatures from: their <c>import</c> declarations.</param>
    public static NativeDeclaration? For(string name, string? owner, IReadOnlyList<ScriptIndex> layers)
    {
        var engine = Engine.Value;
        if (owner is not null && ClassOwning(engine, owner, name) is { } holder)
            return ClassText(engine, holder, name, layers);
        if (engine.Classes.FirstOrDefault(c => c.Name == name) is { } cls) return ClassText(engine, cls, null, layers);
        if (engine.Enums.FirstOrDefault(e => e.Name == name) is { } en) return EnumText(en);
        if (engine.GlobalFunctions.Contains(name)) return FunctionText(name, layers);
        if (engine.SimpleTypes.Contains(name))
        {
            string text = Header(name) + $"// {name} is one of the engine's simple types: a value the engine stores in a way of its own.\n" // loc-ignore
                + "// Scripts pass it around and the engine's functions read it, but it has no members to call.\n\n" // loc-ignore
                + $"import struct {name} {{}}\n"; // loc-ignore
            return new NativeDeclaration(name, Path(name), text, text.LastIndexOf(name, StringComparison.Ordinal));
        }
        return null;
    }

    /// <summary>The class, <paramref name="owner"/> or one of its parents, whose native list has <paramref name="member"/>.</summary>
    private static RttiClass? ClassOwning(Rtti engine, string owner, string member)
    {
        for (var cls = engine.Classes.FirstOrDefault(c => c.Name == owner); cls is not null; cls = cls.Base is null ? null : engine.Classes.FirstOrDefault(c => c.Name == cls.Base))
            if (cls.Methods.Contains(member) || cls.Properties.Any(p => p.Name == member)) return cls;
        return null;
    }

    private static string Path(string name) => $@"native\{name}.ws";

    private static string Header(string name) =>
        $"// {name}: written out by W3 Mod Workbench from the engine's own class list.\n" // loc-ignore
        + "// It isn't a file in the game: the engine itself declares this in its code, and scripts reach it with 'import'.\n\n"; // loc-ignore

    private static NativeDeclaration ClassText(Rtti engine, RttiClass cls, string? focus, IReadOnlyList<ScriptIndex> layers)
    {
        var text = new StringBuilder(Header(cls.Name));
        var chain = new List<string>();
        for (var at = cls.Base; at is not null && chain.Count < 12; at = engine.Classes.FirstOrDefault(c => c.Name == at)?.Base) chain.Add(at);
        if (chain.Count > 0) text.Append($"// Its parents: {string.Join(" > ", chain)}\n\n"); // loc-ignore
        string kind = cls.IsStruct ? "struct" : "class";
        text.Append($"import {kind} {cls.Name}{(cls.Base is not null ? " extends " + cls.Base : "")}\n{{\n"); // loc-ignore
        int focusAt = -1;
        if (cls.Properties.Count > 0)
        {
            foreach (var (name, type) in cls.Properties.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (name == focus) focusAt = text.Length + "\timport var ".Length; // loc-ignore
                text.Append($"\timport var {name} : {TypeName(type)};\n"); // loc-ignore
            }
        }
        if (cls.Methods.Count > 0)
        {
            if (cls.Properties.Count > 0) text.Append('\n');
            var unknown = new List<string>();
            foreach (string method in cls.Methods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                if (Signature(layers, cls.Name, method) is { } declared)
                {
                    if (method == focus) focusAt = text.Length + 1 + declared.IndexOf(method, StringComparison.Ordinal);
                    text.Append('\t').Append(declared).Append('\n');
                }
                else unknown.Add(method);
            }
            if (unknown.Count > 0)
            {
                text.Append("\n\t// No script declares these with 'import', so their parameters aren't known:\n"); // loc-ignore
                foreach (string method in unknown)
                {
                    if (method == focus) focusAt = text.Length + "\timport function ".Length; // loc-ignore
                    text.Append($"\timport function {method}(/* ? */);\n"); // loc-ignore
                }
            }
        }
        text.Append("}\n");
        string result = text.ToString();
        int nameAt = result.IndexOf($"{kind} {cls.Name}", StringComparison.Ordinal) + kind.Length + 1;
        return new NativeDeclaration(cls.Name, Path(cls.Name), result, focusAt >= 0 ? focusAt : nameAt);
    }

    private static NativeDeclaration EnumText(RttiEnum en)
    {
        var text = new StringBuilder(Header(en.Name));
        text.Append($"enum {en.Name}\n{{\n"); // loc-ignore
        foreach (var (name, value) in en.Options) text.Append($"\t{name} = {value},\n");
        text.Append("}\n");
        string result = text.ToString();
        return new NativeDeclaration(en.Name, Path(en.Name), result, result.IndexOf($"enum {en.Name}", StringComparison.Ordinal) + 5);
    }

    private static NativeDeclaration FunctionText(string name, IReadOnlyList<ScriptIndex> layers)
    {
        string declared = Signature(layers, null, name) ?? $"import function {name}(/* ? */);"; // loc-ignore
        string text = Header(name) + (declared.Contains("/* ? */", StringComparison.Ordinal)
            ? "// No script declares it with 'import', so its parameters aren't known.\n\n" : "") + declared + "\n"; // loc-ignore
        return new NativeDeclaration(name, Path(name), text, text.LastIndexOf(name, StringComparison.Ordinal));
    }

    /// <summary>
    /// A native function's declaration as a script writes it (<c>import final function GetValueAt( column : int ) : string;</c>),
    /// from the first script that declares it in that class (or globally), on one line. Null when none does.
    /// </summary>
    private static string? Signature(IReadOnlyList<ScriptIndex> layers, string? owner, string name)
    {
        foreach (var layer in layers)
            foreach (var symbol in layer.Named(name))
            {
                if (symbol.Kind is not (SymbolKind.Function or SymbolKind.Event) || symbol.Owner != owner) continue;
                string code;
                try { code = symbol.File.Text; }
                catch (Exception) { continue; }
                int start = code.LastIndexOf('\n', Math.Max(0, symbol.Offset - 1)) + 1;
                int end = code.IndexOfAny([';', '{'], symbol.Offset);
                if (end < 0) continue;
                string line = System.Text.RegularExpressions.Regex.Replace(code[start..end].Trim(), @"\s+", " ");
                return line.StartsWith("import", StringComparison.Ordinal) ? line + ";" : null;
            }
        return null;
    }

    /// <summary>An engine type as WitcherScript writes it: Float is float, handle:CEntity is CEntity, array:2,0,Int32 is array&lt;int&gt;.</summary>
    public static string TypeName(string type)
    {
        if (type.StartsWith("handle:", StringComparison.Ordinal)) return TypeName(type[7..]);
        if (type.StartsWith("ptr:", StringComparison.Ordinal)) return TypeName(type[4..]);
        if (type.StartsWith("soft:", StringComparison.Ordinal)) return TypeName(type[5..]);
        if (type.StartsWith("array:", StringComparison.Ordinal))
        {
            int comma = type.IndexOf(',', type.IndexOf(',') + 1);
            return $"array<{TypeName(comma > 0 ? type[(comma + 1)..] : type[6..])}>";
        }
        return type switch
        {
            "Float" => "float",
            "Int32" => "int",
            "Bool" => "bool",
            "String" => "string",
            "CName" => "name",
            "Uint8" => "byte",
            _ => type,
        };
    }
}
