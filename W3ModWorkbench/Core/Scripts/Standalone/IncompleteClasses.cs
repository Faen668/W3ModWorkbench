namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// wcc_lite's "Scanning for incomplete classes" pass: a class that isn't abstract (an imported one too) but leaves a function of
/// one of its base classes without a body, a function declared there as only <c>function F();</c>, is warned about, with
/// each function it's missing. It runs before the script extensions, so a function a mod adds with @addMethod doesn't
/// count. Only warnings: it never stops the compile.
/// </summary>
public static class IncompleteClasses
{
    /// <summary>
    /// Classes the engine makes abstract though their scripts don't say so, which wcc_lite doesn't warn about. The engine's
    /// dump doesn't mark them: these are the ones a compile of the game's own scripts shows (2026-10-21).
    /// </summary>
    private static readonly HashSet<string> EngineAbstract = ["CCommonGame"];

    /// <param name="checkedFiles">The files whose classes are checked: the project's.</param>
    /// <param name="allFiles">Every script file, for the base classes' declarations.</param>
    public static List<CompileProblem> Check(Symbols symbols, IReadOnlyList<SourceFile> checkedFiles, IEnumerable<SourceFile> allFiles)
    {
        // the declarations by class, as Symbols names them: the first one read wins, as the project's files come first
        var decls = new Dictionary<string, ClassDecl>();
        foreach (var file in checkedFiles.Concat(allFiles))
            foreach (var item in file.Items)
                if (item is ClassItem { Decl: var d }) decls.TryAdd(Symbols.ClassKey(d), d);

        var warnings = new List<CompileProblem>();
        foreach (var file in checkedFiles)
            foreach (var item in file.Items)
            {
                if (item is not ClassItem { Decl: var decl } || decl.IsAbstract || decl.IsStruct || EngineAbstract.Contains(decl.Name)) continue;
                string key = Symbols.ClassKey(decl);
                var missing = Missing(symbols, decls, key);
                if (missing.Count == 0) continue;
                int line = Math.Max(decl.Line, 1);
                // loc-ignore-start: wcc_lite's own words
                // wcc_lite writes each function on a line of its own after the class's; here they're one message, a line each
                warnings.Add(new(file.Path, line, $"Class {decl.Name} is not marked as abstract, but has missing functions!"
                    + string.Concat(missing.Select(m => $"\n - '{m.Function}' in base class '{m.Owner}'"))));
                // loc-ignore-end
            }
        return warnings;
    }

    /// <summary>
    /// The functions with no body up the class's chain that nothing nearer gives one, each with the class that declares
    /// it: as wcc_lite lists them, the farthest class first, each in the order it declares them.
    /// </summary>
    private static List<(string Function, string Owner)> Missing(Symbols symbols, Dictionary<string, ClassDecl> decls, string key)
    {
        var bodied = new HashSet<string>();
        var byClass = new List<List<(string, string)>>();
        if (decls.TryGetValue(key, out var own))
            foreach (var f in own.Methods)
                if (f.Body is not null || f.Specifiers.IsImport) bodied.Add(f.Name);
        string? current = symbols.Classes.GetValueOrDefault(key)?.Parent;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (!decls.TryGetValue(current, out var decl)) break; // an engine class: its functions are the engine's
            var missing = new List<(string, string)>();
            byClass.Add(missing);
            foreach (var f in decl.Methods)
            {
                if (f.Body is null && !f.Specifiers.IsImport && !bodied.Contains(f.Name))
                    missing.Add((f.Name, decl.Name));
                bodied.Add(f.Name);
            }
            current = symbols.Classes.GetValueOrDefault(current)?.Parent;
        }
        byClass.Reverse();
        return [.. byClass.SelectMany(m => m)];
    }
}
