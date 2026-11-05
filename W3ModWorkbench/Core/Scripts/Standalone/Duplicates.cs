namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// wcc_lite's check that a mod defines each class, struct and state once: a second one, in the same file or another, is
/// an error in its words. It's said at each one, so the list goes to both. wcc_lite lets a global function be defined
/// twice without a word, but the edits made in one of them would go nowhere (a decompiled blob's all.ws left next to the
/// files it repeats, say), so it's a warning here.
/// </summary>
public static class Duplicates
{
    // loc-ignore-start: wcc_lite's own words
    public static (List<CompileProblem> Errors, List<CompileProblem> RepeatedFunctions) Check(IReadOnlyList<SourceFile> files)
    {
        var problems = new List<CompileProblem>();
        var repeated = new List<CompileProblem>();
        var classes = new Dictionary<string, (string File, int Line)>();
        var functions = new Dictionary<string, (string File, int Line)>();
        foreach (var file in files)
            foreach (var item in file.Items)
                switch (item)
                {
                    case ClassItem { Decl: { IsImport: false } decl }:
                        string key = Symbols.ClassKey(decl);
                        if (!classes.TryAdd(key, (file.Path, decl.Line)))
                            Add(classes[key], (file.Path, decl.Line), decl.StateMachineOf is { } machine
                                ? $"State '{decl.Name}' is already defined in '{machine}'."
                                : $"{(decl.IsStruct ? "Struct" : "Class")} '{decl.Name}' is already defined in current compilation unit.");
                        break;
                    case FunctionItem { Decl: { Annotation: null } function } when !function.Specifiers.IsImport:
                        if (!functions.TryAdd(function.Name, (file.Path, function.Line)))
                            repeated.Add(new(file.Path, function.Line, $"Global function '{function.Name}' already defined."));
                        break;
                }
        return (problems, repeated);

        // said at the first one as well, once, so the list goes to both
        void Add((string File, int Line) first, (string File, int Line) again, string text)
        {
            var atFirst = new CompileProblem(first.File, first.Line, text);
            if (!problems.Contains(atFirst)) problems.Add(atFirst);
            problems.Add(new(again.File, again.Line, text));
        }
    }
    // loc-ignore-end
}
