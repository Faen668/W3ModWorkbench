namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// wcc_lite's "Script Extensions" passes over a mod's annotated declarations, in its order: adding properties, adding
/// functions, replacing global functions, replacing class functions, then wrapping class functions. Each says what's
/// wrong in wcc_lite's words, and as in wcc_lite a pass that finds anything stops the compile there, so the passes
/// after it aren't checked.
/// </summary>
public static class Extensions
{
    public static List<CompileProblem> Check(Symbols symbols, IReadOnlyList<SourceFile> files)
    {
        var items = files.SelectMany(f => f.Items.Select(i => (File: f.Path, Item: i))).ToList();
        var fields = items.Where(i => i.Item is FieldItem).Select(i => (i.File, Decl: ((FieldItem)i.Item).Decl)).ToList();
        var functions = items.Where(i => i.Item is FunctionItem { Decl.Annotation: not null })
            .Select(i => (i.File, Decl: ((FunctionItem)i.Item).Decl)).ToList();
        List<(string File, FunctionDecl Decl)> Annotated(AnnotationKind kind) =>
            [.. functions.Where(f => f.Decl.Annotation!.Kind == kind)];

        var passes = new Func<List<CompileProblem>>[]
        {
            () => AddFields(symbols, fields),
            () => AddMethods(symbols, Annotated(AnnotationKind.AddMethod)),
            () => ReplaceGlobals(symbols, [.. Annotated(AnnotationKind.ReplaceMethod).Where(f => f.Decl.Annotation!.Class is null)]),
            () => ReplaceMethods(symbols, [.. Annotated(AnnotationKind.ReplaceMethod).Where(f => f.Decl.Annotation!.Class is not null)]),
            () => WrapMethods(symbols, Annotated(AnnotationKind.WrapMethod)),
        };
        foreach (var pass in passes)
            if (pass() is { Count: > 0 } problems) return problems;
        return [];
    }

    /// <summary>Where wcc_lite says a declaration's problem is: its annotation's line.</summary>
    private static int At(Annotation? annotation, int line) => annotation is { Line: > 0 } a ? a.Line : line;

    private static List<CompileProblem> AddFields(Symbols symbols, List<(string File, FieldDecl Decl)> fields)
    {
        var problems = new List<CompileProblem>();
        foreach (var (file, field) in fields)
            foreach (string name in field.Names)
            {
                if (field.Annotation?.Class is not { } cls)
                    problems.Add(new(file, At(field.Annotation, field.Line), $"@addField of property '{name}' has no target class.")); // loc-ignore
                else if (!symbols.Classes.ContainsKey(cls))
                    problems.Add(new(file, At(field.Annotation, field.Line), $"@addField cannot add '{cls}::{name}' because the class '{cls}' is not defined.")); // loc-ignore
            }
        // then each added field's type, as wcc_lite creates the property
        foreach (var (file, field) in fields)
            if (field.Annotation?.Class is { } cls && symbols.Classes.ContainsKey(cls))
                problems.AddRange(UnknownTypes.CheckAddedField(symbols, file, field, cls));
        return problems;
    }

    private static List<CompileProblem> AddMethods(Symbols symbols, List<(string File, FunctionDecl Decl)> functions)
    {
        var problems = new List<CompileProblem>();
        foreach (var (file, decl) in functions)
        {
            // a class's function is named Class::name, a global one by its name
            string subject = decl.Annotation!.Class is { } owner ? $"{owner}::{decl.Name}" : decl.Name;
            if (decl.Annotation!.Class is not { } cls)
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@addMethod of class function '{subject}' has no target class.")); // loc-ignore
            else if (!symbols.Classes.ContainsKey(cls))
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@addMethod cannot add '{subject}' because the class '{cls}' is not defined.")); // loc-ignore
        }
        return problems;
    }

    private static List<CompileProblem> ReplaceGlobals(Symbols symbols, List<(string File, FunctionDecl Decl)> functions)
    {
        var problems = new List<CompileProblem>();
        var seen = new HashSet<string>();
        foreach (var (file, decl) in functions)
        {
            if (!seen.Add(decl.Name))
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@replaceMethod duplicate found for '{decl.Name}'.")); // loc-ignore
            else if (decl.Specifiers.IsImport || decl.Body is null)
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@replaceMethod cannot replace an import ('{decl.Name}').")); // loc-ignore
            else if (!symbols.Functions.TryGetValue(decl.Name, out var target))
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@replaceMethod cannot replace '{decl.Name}' because it has no previous definition.")); // loc-ignore
            else if (target.IsOpaque || target.Specifiers.IsImport)
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@replaceMethod cannot replace '{decl.Name}' because it is a native function.")); // loc-ignore
        }
        return problems;
    }

    private static List<CompileProblem> ReplaceMethods(Symbols symbols, List<(string File, FunctionDecl Decl)> functions)
    {
        var problems = new List<CompileProblem>();
        var seen = new HashSet<string>();
        foreach (var (file, decl) in functions)
        {
            // a class's function is named Class::name, a global one by its name
            string subject = decl.Annotation!.Class is { } owner ? $"{owner}::{decl.Name}" : decl.Name;
            string cls = decl.Annotation!.Class!;
            if (!seen.Add($"{cls}::{decl.Name}"))
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@replaceMethod duplicate found for '{subject}'.")); // loc-ignore
            else if (decl.Specifiers.IsImport || decl.Body is null)
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@replaceMethod cannot replace an import ('{subject}').")); // loc-ignore
            else if (!symbols.Classes.TryGetValue(cls, out var info))
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@replaceMethod cannot replace '{subject}' because the class '{cls}' is not defined.")); // loc-ignore
            else if (!info.Methods.TryGetValue(decl.Name, out var target))
                problems.Add(new(file, At(decl.Annotation, decl.Line), symbols.FindMethod(cls, decl.Name) is { } inherited
                    ? $"@replaceMethod cannot replace '{subject}' because it is a definition inherited from '{inherited.Owner}'." // loc-ignore
                    : $"@replaceMethod cannot replace '{subject}' because it has no previous definition.")); // loc-ignore
            else if (target.IsOpaque || target.Specifiers.IsImport)
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@replaceMethod cannot replace '{subject}' because it is a native class function.")); // loc-ignore
        }
        return problems;
    }

    private static List<CompileProblem> WrapMethods(Symbols symbols, List<(string File, FunctionDecl Decl)> functions)
    {
        var problems = new List<CompileProblem>();
        foreach (var (file, decl) in functions)
        {
            // a class's function is named Class::name, a global one by its name
            string subject = decl.Annotation!.Class is { } owner ? $"{owner}::{decl.Name}" : decl.Name;
            FunctionInfo? target;
            if (decl.Annotation!.Class is { } cls)
            {
                if (!symbols.Classes.TryGetValue(cls, out var info))
                {
                    problems.Add(new(file, At(decl.Annotation, decl.Line), $"@wrapMethod cannot wrap '{subject}' because the class '{cls}' is not defined.")); // loc-ignore
                    continue;
                }
                if (!info.Methods.TryGetValue(decl.Name, out target))
                {
                    problems.Add(new(file, At(decl.Annotation, decl.Line), symbols.FindMethod(cls, decl.Name) is { } inherited
                        ? $"@wrapMethod cannot wrap '{subject}' because it is a definition inherited from '{inherited.Owner}'." // loc-ignore
                        : $"@wrapMethod cannot wrap '{subject}' because it is not defined.")); // loc-ignore
                    continue;
                }
            }
            else if (!symbols.Functions.TryGetValue(decl.Name, out target))
            {
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@wrapMethod cannot wrap '{subject}' because it is not defined.")); // loc-ignore
                continue;
            }
            // wcc_lite says only modio mods may wrap a native function, but it wraps an import's all the same
            if (decl.Body is not { Count: > 0 })
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@wrapMethod Wrapper code cannot be empty ('{subject}').")); // loc-ignore
            else if (!decl.UsesWrappedMethod)
                problems.Add(new(file, At(decl.Annotation, decl.Line), $"@wrapMethod Wrapper '{subject}' must contain a 'wrappedMethod' token.")); // loc-ignore
        }
        return problems;
    }
}
