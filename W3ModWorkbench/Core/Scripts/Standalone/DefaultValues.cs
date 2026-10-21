using System.Globalization;

namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// wcc_lite's "Creating default values" pass, after the script extensions and before the functions are compiled. A
/// class's <c>default name = value;</c> is kept as text, and wcc_lite reads that text as the field's type, reporting
/// every one it can't (and still going through the rest) before it stops the compile. What it reads, found by running
/// it (2026-10-21): an enum's text must be one of its names (a number isn't); a number's must start with one ("2.5" and
/// "5" do for an int, a word doesn't); a bool's must start with true or false, either case, or a number; a string's and
/// a name's can be anything. NULL for an object is "Unknown class 'NULL'", and a field the class doesn't have is an error
/// of its own. A bool that only starts with true or false ("falsesss") gets through wcc_lite, so it's a warning here.
/// </summary>
public static class DefaultValues
{
    /// <summary>The errors stop the compile as wcc_lite's do; the warnings don't.</summary>
    public static (List<CompileProblem> Errors, List<CompileProblem> Warnings) Check(Symbols symbols, IReadOnlyList<SourceFile> files)
    {
        var errors = new List<CompileProblem>();
        var warnings = new List<CompileProblem>();
        foreach (var file in files)
            foreach (var item in file.Items)
            {
                if (item is not ClassItem { Decl: var decl }) continue;
                string cls = Symbols.ClassKey(decl);
                foreach (var d in decl.Defaults)
                {
                    // a state machine's first state, as `default autoState = 'Idle';`, is no field
                    if (d.Field == "autoState") continue;
                    int line = Math.Max(d.Value.Line, 1);
                    // loc-ignore-start: wcc_lite's own words, as its compile messages are
                    if (symbols.FindField(cls, d.Field) is not { } field)
                    {
                        // a class the compiler doesn't know the fields of (an engine one the dump leaves out) isn't checked
                        if (symbols.Classes.ContainsKey(cls)) errors.Add(new(file.Path, line, $"Property '{d.Field}' does not exist in '{decl.Name}'"));
                        continue;
                    }
                    if (Text(d.Value.Kind) is not { } text)
                        continue; // an expression wcc_lite doesn't take as a default at all, which the assembler reports
                    var type = field.Type;
                    if (type.IsClass && d.Value.Kind is NullLit)
                        errors.Add(new(file.Path, line, "Unknown class 'NULL'"));
                    else if (Problem(symbols, type, text) is { } problem)
                        errors.Add(new(file.Path, line, $"Unable to parse value: {problem}"));
                    else if (type.Kind == TyKind.Bool && !IsNumber(text)
                        && !text.Equals("true", StringComparison.OrdinalIgnoreCase) && !text.Equals("false", StringComparison.OrdinalIgnoreCase))
                        warnings.Add(new(file.Path, line, $"'{text}' isn't true or false. wcc_lite takes it, as it reads only how a bool's value starts, but it's likely a typo."));
                    // loc-ignore-end
                }
            }
        return (errors, warnings);
    }

    /// <summary>
    /// The same pass's warning for a state machine none of whose defaults, nor those of the classes it extends, says which
    /// state it starts in (<c>default autoState = 'Idle';</c>).
    /// </summary>
    public static List<CompileProblem> AutoStates(Symbols symbols, IReadOnlyList<SourceFile> checkedFiles, IEnumerable<SourceFile> allFiles)
    {
        var decls = new Dictionary<string, ClassDecl>();
        foreach (var file in checkedFiles.Concat(allFiles))
            foreach (var item in file.Items)
                if (item is ClassItem { Decl: var d }) decls.TryAdd(Symbols.ClassKey(d), d);
        bool HasAutoState(string? key)
        {
            for (int depth = 0; key is not null && depth <= 64; depth++)
            {
                if (!decls.TryGetValue(key, out var decl)) return false;
                if (decl.Defaults.Any(d => d.Field == "autoState")) return true;
                key = symbols.Classes.GetValueOrDefault(key)?.Parent;
            }
            return false;
        }
        var warnings = new List<CompileProblem>();
        foreach (var file in checkedFiles)
            foreach (var item in file.Items)
                if (item is ClassItem { Decl: { IsStateMachine: true } decl } && !HasAutoState(Symbols.ClassKey(decl)))
                    warnings.Add(new(file.Path, Math.Max(decl.Line, 1), $"State machine '{decl.Name}' has no autostate.")); // loc-ignore: wcc_lite's words
        return warnings;
    }

    /// <summary>The text wcc_lite keeps for a default, or null for one it isn't kept for.</summary>
    private static string? Text(ExprKind value) => value switch
    {
        IntLit i => i.Value.ToString(CultureInfo.InvariantCulture),
        FloatLit f => f.Text,
        StrLit s => s.Value,
        NameLit n => n.Value,
        BoolLit b => b.Value ? "true" : "false",
        Ident id => id.Name,
        Unary { Op: "-", Operand.Kind: var operand } when Text(operand) is { } inner => "-" + inner,
        NullLit => "NULL",
        _ => null,
    };

    /// <summary>Why wcc_lite can't read the text as the type, or null when it can, or the type is one it reads anything as.</summary>
    private static string? Problem(Symbols symbols, Ty type, string text) => type.Kind switch
    {
        TyKind.Enum when symbols.Enums.TryGetValue(type.Name, out var info) && info.ValueOf(text) is null =>
            $"{type.Name} has no '{text}'.", // loc-ignore
        TyKind.Int or TyKind.Byte or TyKind.Float when !IsNumber(text) => $"'{text}' isn't a number.", // loc-ignore
        TyKind.Bool when !IsNumber(text) && !text.StartsWith("true", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("false", StringComparison.OrdinalIgnoreCase) => $"'{text}' isn't true or false.", // loc-ignore
        _ => null,
    };

    /// <summary>Whether the text starts with a number, as wcc_lite reads one: "2.5" and "5x" do, "abc" doesn't.</summary>
    private static bool IsNumber(string text)
    {
        var s = text.AsSpan().TrimStart();
        if (s.Length > 0 && s[0] is '-' or '+') s = s[1..];
        if (s.Length > 0 && s[0] == '.') s = s[1..];
        return s.Length > 0 && char.IsAsciiDigit(s[0]);
    }
}
