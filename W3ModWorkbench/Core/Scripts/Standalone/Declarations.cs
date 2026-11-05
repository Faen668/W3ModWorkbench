namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// What wcc_lite checks of a project's declarations, pass by pass and in its words (so not translated): the types, then
/// the classes, then the functions, then the properties. As in wcc_lite, a pass that finds anything stops the compile,
/// so later passes aren't reached.
/// </summary>
public sealed class Declarations(Symbols symbols, IReadOnlyList<SourceFile> project, IEnumerable<SourceFile> game, Rtti rtti)
{
    private readonly HashSet<string> _nativeClasses = rtti.Classes.Select(c => c.Name).ToHashSet();
    private readonly HashSet<string> _nativeFunctions = [.. rtti.GlobalFunctions];
    private readonly Dictionary<string, RttiClass> _native = rtti.Classes.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
    private readonly HashSet<string> _gameClasses = game.SelectMany(f => f.Items.OfType<ClassItem>()).Select(c => Symbols.ClassKey(c.Decl)).ToHashSet();
    private readonly HashSet<string> _gameFunctions = game.SelectMany(f => f.Items.OfType<FunctionItem>()).Where(f => f.Decl.Annotation is null).Select(f => f.Decl.Name).ToHashSet();

    private IEnumerable<(string File, ClassDecl Decl)> Classes =>
        project.SelectMany(f => f.Items.OfType<ClassItem>().Select(c => (f.Path, c.Decl)));

    // loc-ignore-start: wcc_lite's own words
    public List<CompileProblem> Types()
    {
        var problems = new List<CompileProblem>();
        foreach (var (file, decl) in Classes)
        {
            if (!decl.IsStruct) continue;
            if (decl.IsImport)
            {
                if (!_nativeClasses.Contains(decl.Name)) problems.Add(new(file, decl.Line, $"Cannot find structure '{decl.Name}' to import."));
            }
            else if (decl.Fields.Count == 0)
                problems.Add(new(file, decl.Line, $"Cannot define empty structure '{decl.Name}'."));
            else if (_nativeClasses.Contains(decl.Name) && (!_gameClasses.Contains(decl.Name) || GameImports(decl.Name)))
                problems.Add(new(file, decl.Line, $"Structure '{decl.Name}' is already defined in C++, can only be imported."));
        }
        return problems;
    }

    public List<CompileProblem> ClassDefinitions()
    {
        var problems = new List<CompileProblem>();
        var linked = new Dictionary<string, string>();
        foreach (var (file, decl) in Classes)
        {
            if (decl.IsStruct) continue;
            string key = Symbols.ClassKey(decl);
            if (decl.StateMachineOf is { } machine)
            {
                if (!symbols.Classes.ContainsKey(machine))
                {
                    problems.Add(new(file, decl.Line, $"Cannot define state '{decl.Name}' in context of unknown class '{machine}'."));
                    continue;
                }
                if (decl.Parent is { } baseState)
                {
                    if (baseState == decl.Name && !BaseMachineHas(machine, baseState))
                        problems.Add(new(file, decl.Line, $"Recursive state definition. '{decl.Name}' in already based on '{baseState}'"));
                    else if (!StateExists(machine, baseState))
                        problems.Add(new(file, decl.Line, $"Unknown base state '{baseState}' in context of class '{machine}'"));
                }
                if (decl.IsImport && !_nativeClasses.Contains(key))
                    problems.Add(new(file, decl.Line, $"Cannot find class '{key}' to import."));
                continue;
            }
            if (decl.IsImport)
            {
                if (!_nativeClasses.Contains(key) || _gameClasses.Contains(key) && !GameImports(key))
                    problems.Add(new(file, decl.Line, $"Cannot find class '{key}' to import."));
                continue;
            }
            if (_nativeClasses.Contains(key) && !_gameClasses.Contains(key))
                problems.Add(new(file, decl.Line, $"Class '{key}' is already defined in C++, can only be imported."));
            if (GameImports(key))
                problems.Add(new(file, decl.Line, $"Class '{key}' is already defined in C++, can only be imported."));
            if (decl.Parent is not { } parent) continue;
            if (!symbols.Classes.ContainsKey(parent))
            {
                problems.Add(new(file, decl.Line, $"Unknown base class '{parent}'."));
                continue;
            }
            string? up = parent;
            bool cycle = false;
            for (int depth = 0; up is not null && depth <= 64 && !cycle; depth++)
            {
                cycle = up == key;
                up = linked.GetValueOrDefault(up);
            }
            if (cycle) problems.Add(new(file, decl.Line, $"Base class '{parent}' creates cyclic dependency for '{key}'."));
            else linked[key] = parent;
        }
        return problems;
    }

    private bool GameImports(string key) => game.SelectMany(f => f.Items.OfType<ClassItem>()).Any(c => c.Decl.IsImport && Symbols.ClassKey(c.Decl) == key);

    private bool StateExists(string machine, string state)
    {
        string? current = machine;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (symbols.Classes.ContainsKey(Symbols.StateClassKey(current, state))) return true;
            current = symbols.Classes.GetValueOrDefault(current)?.Parent;
        }
        return false;
    }

    private bool BaseMachineHas(string machine, string state) =>
        symbols.Classes.GetValueOrDefault(machine)?.Parent is { } parent && StateExists(parent, state);

    public List<CompileProblem> Functions()
    {
        var problems = new List<CompileProblem>();
        foreach (var file in project)
            foreach (var item in file.Items)
                switch (item)
                {
                    case FunctionItem { Decl: { Annotation: null } decl }:
                        GlobalFunction(file.Path, decl, problems);
                        break;
                    case FunctionItem { Decl: { Annotation: { Kind: AnnotationKind.AddMethod, Class: { } target } } decl }:
                        Method(file.Path, decl, target, false, isAdded: true, problems);
                        break;
                    case ClassItem { Decl: { IsStruct: false } decl }:
                    {
                        string key = Symbols.ClassKey(decl);
                        var seen = new Dictionary<string, FunctionDecl>();
                        foreach (var method in decl.Methods)
                        {
                            Method(file.Path, method, key, decl.StateMachineOf is not null, isAdded: false, problems);
                            if (!seen.TryAdd(method.Name, method) && seen[method.Name].Params.Count != method.Params.Count)
                                problems.Add(new(file.Path, method.Line, $"Function '{method.Name}' has a different number of parameters than its previous definition."));
                        }
                        break;
                    }
                }
        return problems;
    }

    private void GlobalFunction(string file, FunctionDecl decl, List<CompileProblem> problems)
    {
        var s = decl.Specifiers;
        string name = decl.Name;
        void Say(string text) => problems.Add(new(file, decl.Line, text));
        if (s.Visibility != Visibility.Default) Say($"Global function '{name}' has an access modifier but it has not meaning in global scope.");
        else if (s.IsFinal) Say($"Global function '{name}' is specified as 'final' but it has not meaning in global scope.");
        else if (s.IsEvent) Say($"Global function '{name}' is specified as 'event' but it has not meaning in global scope");
        else if (s.IsEntry) Say($"Global function '{name}' is specified as 'entry' but it has not meaning in global scope");
        else if (s.IsTimer) Say($"Global function '{name}' is specified as 'timer' but it has not meaning in global scope");
        else if (s.IsCleanup) Say($"Global function '{name}' is specified as 'cleanup' but it has not meaning in global scope");
        else if (s.IsExec && s.IsLatent) Say($"Global exec function '{name}' cannot also be 'latent'.");
        else if (s.IsStoryScene && (decl.Params.Count == 0 || decl.Params[0].Type.Name != "CStoryScenePlayer"))
            Say($"Scene function '{name}' should have a first parameter of type CStoryScenePlayer.");
        else if (s.IsImport && !_nativeFunctions.Contains(name)) Say($"Cannot find global function '{name}' to import.");
        else if (_gameFunctions.Contains(name) && _nativeFunctions.Contains(name))
            Say($"Function '{name}' has already been defined in current compilation unit.");
    }

    private void Method(string file, FunctionDecl decl, string key, bool inState, bool isAdded, List<CompileProblem> problems)
    {
        var s = decl.Specifiers;
        string full = $"{key}::{decl.Name}";
        void Say(string text) => problems.Add(new(file, isAdded && decl.Annotation is { Line: > 0 } a ? a.Line : decl.Line, text));
        bool returns = decl.ReturnType is { Name: not "void" };

        if (s.IsImport && s.IsEntry) { Say($"Entry function '{full}' is declared as import but there is no such thing as native 'entry' functions."); return; }
        if (s.IsImport && s.IsTimer) { Say($"Timer function '{full}' is declared as import but there is no such thing as native 'timer' functions."); return; }
        if (s.IsImport && s.IsCleanup) { Say($"Cleanup function '{full}' is declared as import but there is no such thing as native 'cleanup' functions."); return; }
        if (s.IsEvent)
        {
            if (!decl.Name.StartsWith("On", StringComparison.Ordinal)) Say($"Event function '{full}' name should start with \"On\".");
            else if (returns) Say($"Event function '{full}' should not have an explicit return type. It is implicitly always bool.");
        }
        if (s.IsEntry)
        {
            if (!inState && !isAdded) Say($"State entry function '{full}' cannot be declared outside of state.");
            else if (returns) Say($"State entry function '{full}' cannot return anything.");
        }
        if (s.IsCleanup)
        {
            if (!inState && !isAdded) Say($"Cleanup function '{full}' cannot be declared outside of state.");
            else if (returns) Say($"Cleanup function '{full}' cannot return annything.");
            else if (decl.Params.Count > 0) Say($"Cleanup function {full}' should take no parameters.");
        }
        if (s.IsExec) Say($"Exec function '{full}' cannot be declared inside a class.");
        if (s.IsTimer)
        {
            if (returns) Say($"Timer function '{full}' cannot return annything.");
            else if (decl.Params.Count != 2) Say($"Timer function '{full}' should take exactly two parameters (id and deltaTime).");
            else if (decl.Params[0].Type.Name is not ("float" or "GameTime") || decl.Params[1].Type.Name != "int")
                Say($"Timer function '{full}' should take float/gametime parameter (delta time) and int parameter (id).");
        }

        // against what it overrides
        if (symbols.Classes.GetValueOrDefault(key)?.Parent is { } parent && symbols.FindMethod(parent, decl.Name) is { IsOpaque: false } inherited)
        {
            var b = inherited.Specifiers;
            string owner = inherited.Owner ?? parent;
            if (b.IsFinal) Say($"Function '{full}' cannot override 'final' declaration in class '{owner}'.");
            else if (Rank(s.Visibility) < Rank(b.Visibility) && b.Visibility != Visibility.Default)
                Say($"Function '{full}' cannot have a weaker access modifier than in its ancestor class '{owner}'.");
            else if (Flags(s) != Flags(b))
                Say($"Function '{full}' has different specifiers ({Flags(s):X}) than in its ancestor class '{owner}' ({Flags(b):X}).");
        }
        if (s.IsImport && !s.IsEntry && !isAdded && !(_native.TryGetValue(key, out var native) && native.Methods.Contains(decl.Name))
            && !(symbols.Classes.GetValueOrDefault(key)?.Parent is { } up && symbols.FindMethod(up, decl.Name) is not null))
            Say($"Cannot find function '{full}' to import.");
    }

    private static int Rank(Visibility v) => v switch { Visibility.Private => 2, Visibility.Protected => 1, _ => 0 };

    private static int Flags(FunctionSpecifiers s) => (s.IsLatent ? 0x40 : 0) | (s.IsEvent ? 0x20 : 0);

    public List<CompileProblem> Properties()
    {
        var problems = new List<CompileProblem>();
        foreach (var file in project)
            foreach (var item in file.Items)
            {
                if (item is ClassItem { Decl: var decl })
                {
                    ClassProperties(file.Path, decl, problems);
                    foreach (var method in decl.Methods) Locals(file.Path, method, problems);
                }
                else if (item is FunctionItem { Decl: var function })
                    Locals(file.Path, function, problems);
            }
        return problems;
    }

    private void ClassProperties(string file, ClassDecl decl, List<CompileProblem> problems)
    {
        string key = Symbols.ClassKey(decl);
        var seen = new HashSet<string>();
        foreach (var field in decl.Fields)
            foreach (string name in field.Names)
            {
                void Say(string text) => problems.Add(new(file, field.Line is > 0 ? field.Line : decl.Line, text));
                if (decl.IsStruct && field.Specifiers.Visibility != Visibility.Default)
                {
                    string word = field.Specifiers.Visibility.ToString().ToLowerInvariant();
                    Say($"'{word}' has no sense for property '{name}' in struct '{decl.Name}'.");
                    continue;
                }
                if (field.Specifiers.IsImport)
                {
                    if (!_native.TryGetValue(key, out var native) || !native.Properties.Any(p => p.Name == name))
                        Say($"Property '{name}' does not exist in {(decl.IsStruct ? "struct" : "class")} '{key}'. Cannot import.");
                    continue;
                }
                if (!seen.Add(name))
                {
                    Say($"Property '{key}::{name}' is already defined.");
                    continue;
                }
                if (symbols.Classes.GetValueOrDefault(key)?.Parent is { } parent && symbols.FindField(parent, name) is { } inherited)
                    Say($"Property '{key}::{name}' already exists in base struct '{inherited.Owner}'");
                if (field.IsBinding && !symbols.IsSubclass(key, "CEntity"))
                {
                    Say($"Auto bindable property '{name}' uses type 'handle:{field.Type.Name}' that is not allowed by class 'CObject'.");
                    Say($"Property '{key}::{name}' could not be created.");
                }
            }
    }

    private static void Locals(string file, FunctionDecl function, List<CompileProblem> problems)
    {
        var parameters = new HashSet<string>();
        foreach (var p in function.Params)
            if (!parameters.Add(p.Name)) problems.Add(new(file, function.Line, $"Parameter '{p.Name}' is already defined"));
        if (function.Body is null) return;
        var locals = new HashSet<string>();
        foreach (var statement in function.Body)
            if (statement.Kind is VarStmt v)
                foreach (string name in v.Decl.Names)
                {
                    if (parameters.Contains(name)) problems.Add(new(file, v.Decl.Line, $"Local variable '{name}' cannot have the same name as a parameter."));
                    else if (!locals.Add(name)) problems.Add(new(file, v.Decl.Line, $"Variable '{name}' is defined more than once in function '{function.Name}'."));
                }
    }

    /// <summary>What wcc_lite warns of in the declarations.</summary>
    public List<CompileProblem> Warnings()
    {
        var warnings = new List<CompileProblem>();
        foreach (var (file, decl) in Classes)
        {
            if (decl.StateMachineOf is { } machine && !IsStateMachine(machine))
                warnings.Add(new(file, decl.Line, $"Adding state '{decl.Name}' to class '{machine}' which is not a state machine. Did you forget the 'statemachine' keyword in class?"));
            foreach (var field in decl.Fields)
                if (field.Specifiers.IsSaved && symbols.Resolve(field.Type) is { Kind: TyKind.Class } type && symbols.IsSubclass(type.Name, "CNode"))
                    foreach (string name in field.Names)
                        warnings.Add(new(file, field.Line > 0 ? field.Line : decl.Line, $"Saved property '{name}' of type 'handle:{type.Name}' won't be saved because it references CNode. Use EntityHandle instead if that's an entity or remove saved property attribute."));
        }
        return warnings;
    }

    private bool IsStateMachine(string cls) =>
        project.Concat(game).SelectMany(f => f.Items.OfType<ClassItem>()).Any(c => c.Decl.Name == cls && c.Decl.IsStateMachine);

    /// <summary>What wcc_lite says when it binds a function to the one it overrides, after the extensions.</summary>
    public List<CompileProblem> Binding()
    {
        var problems = new List<CompileProblem>();
        foreach (var (file, decl) in Classes)
        {
            if (decl.IsStruct || symbols.Classes.GetValueOrDefault(Symbols.ClassKey(decl))?.Parent is not { } parent) continue;
            foreach (var method in decl.Methods)
            {
                if (symbols.FindMethod(parent, method.Name) is not { IsOpaque: false } b || b.Specifiers.IsFinal || Flags(method.Specifiers) != Flags(b.Specifiers)) continue;
                void Say(string text) => problems.Add(new(file, method.Line, text));
                if (b.Specifiers.IsTimer)
                {
                    Say($"Function '{method.Name}' in class '{b.Owner}' is a timer function. Cannot override it directly.");
                    continue;
                }
                var ours = symbols.FunctionInfoOf(method, Symbols.ClassKey(decl));
                if (!method.Specifiers.IsEvent && ours.ReturnType != b.ReturnType)
                    Say($"Function '{method.Name}' has different return type '{Shown(ours.ReturnType)}' than base function ({Shown(b.ReturnType)}).");
                if (ours.Params.Count != b.Params.Count)
                    Say($"Function '{method.Name}' takes {ours.Params.Count} parameter(s) which is inconsistent with base function ({b.Params.Count}).");
                else
                    for (int i = 0; i < ours.Params.Count; i++)
                        if (ours.Params[i].Type != b.Params[i].Type)
                            Say($"Function '{method.Name}' parameter '{ours.Params[i].Name}' has different type '{Shown(ours.Params[i].Type)}' than in base function ({Shown(b.Params[i].Type)}).");
            }
        }
        return problems;
    }

    private static string Shown(Ty type) => type.Kind == TyKind.Class ? $"handle:{type.Name}" : type.RttiName;
    // loc-ignore-end
}
