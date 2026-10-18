// Part of the built-in compiler: a C# port of the .ws-to-rsblob compiler written by WebSpam for Aelto's tw3-cahirb (MIT).
// https://github.com/Aelto/tw3-cahirb/pull/1

namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>Something wrong with the project's scripts: where, and what.</summary>
public sealed record CompileProblem(string? File, int Line, string Text);

/// <summary>Raised when the project can't be compiled; <see cref="Problems"/> say why.</summary>
public sealed class CompileFailedException(List<CompileProblem> problems)
    : Exception(problems.Count == 1 ? problems[0].Text : $"{problems.Count} problems")
{
    public List<CompileProblem> Problems { get; } = problems;
}

/// <summary>
/// Lays the project's declarations out as a blob: enums, structs, classes, global functions, then the four kinds of
/// annotated function and the added fields, and the tables the bytecode links against.
/// </summary>
public sealed class Assembler
{
    private const int ImplicitEnumValue = 0x7FFF_FFFF;

    private sealed record PropDef(int Name, string Hint, int Flags, int Type, int Class, int Binding);

    private sealed record FuncDef(int Name, int OverrideClass, int Flags, PropDef? Return, List<PropDef> Params,
        List<PropDef> Locals, byte[] Code);

    private sealed record DefaultDef(int Name, int ValueName, string Value);

    private sealed class FunctionPlan
    {
        public required FunctionDecl Decl;
        public required string Label;
        public required string File;
        public string OverrideClass = "";
        public int Flags;
        public string? ThisClass;
        public Wrap Wrap = Wrap.None;
        public CompiledBody? Compiled;
    }

    private sealed class ClassPlan
    {
        public required ClassDecl Decl;
        public required string File;
        public required List<FunctionPlan> Methods;
    }

    private sealed class FreePlans
    {
        public List<FunctionPlan> Global = [], ReplaceGlobal = [], ReplaceClass = [], Add = [], Wrap = [];
    }

    private readonly Symbols _symbols;
    private readonly Tables _tables = new();
    private readonly List<CompileProblem> _problems = [];

    private Assembler(Symbols symbols) => _symbols = symbols;

    public static byte[] Assemble(Symbols symbols, IReadOnlyList<SourceFile> modFiles) => new Assembler(symbols).Run(modFiles);

    private byte[] Run(IReadOnlyList<SourceFile> modFiles)
    {
        var items = modFiles.SelectMany(f => f.Items.Select(i => (File: f.Path, Item: i))).ToList();

        var enums = items.Select(i => i.Item).OfType<EnumItem>().Select(i => i.Decl).ToList();
        var structs = items.Where(i => i.Item is ClassItem { Decl.IsStruct: true }).Select(i => (i.File, ((ClassItem)i.Item).Decl)).ToList();
        var classes = items.Where(i => i.Item is ClassItem { Decl.IsStruct: false }).Select(i => (i.File, ((ClassItem)i.Item).Decl)).ToList();
        var plain = classes.Where(c => c.Decl.StateMachineOf is null).ToList();
        var states = classes.Where(c => c.Decl.StateMachineOf is not null).ToList();

        var classPlans = ClassOrder(plain).Concat(ClassOrder(states))
            .Select(c => new ClassPlan { Decl = c.Decl, File = c.File, Methods = MethodPlans(c.File, c.Decl) })
            .ToList();

        var free = new FreePlans();
        foreach (var (file, item) in items)
            if (item is FunctionItem f)
            {
                try { PlanFree(file, f.Decl, free); }
                catch (CompileException ex) { Problem(file, ex.Line, $"function {f.Decl.Name}: {ex.Message}"); }
            }

        var fields = items.Where(i => i.Item is FieldItem).Select(i => (i.File, ((FieldItem)i.Item).Decl)).ToList();

        foreach (var plan in free.Global.Concat(classPlans.SelectMany(c => c.Methods))
                     .Concat(free.ReplaceGlobal).Concat(free.ReplaceClass).Concat(free.Add).Concat(free.Wrap))
            Compile(plan);

        var enumDefs = enums.Select(EnumDefinition).ToList();
        var structDefs = new List<(int, int, List<PropDef>, List<DefaultDef>)>();
        foreach (var (file, decl) in structs)
            Guard(file, decl.Name, () => structDefs.Add(StructDefinition(decl)));
        var classDefs = new List<byte[]>();
        foreach (var plan in classPlans)
            Guard(plan.File, plan.Decl.Name, () => classDefs.Add(ClassDefinition(plan)));

        var global = FunctionDefinitions(free.Global);
        var replaceGlobal = FunctionDefinitions(free.ReplaceGlobal);
        var replaceClass = FunctionDefinitions(free.ReplaceClass);
        var add = FunctionDefinitions(free.Add);
        var wrap = FunctionDefinitions(free.Wrap);

        var addedFields = new List<PropDef>();
        foreach (var (file, field) in fields)
        {
            try
            {
                string cls = field.Annotation?.Class ?? throw new CompileException("a var outside a class needs @addField(Class)");
                foreach (string name in field.Names) addedFields.Add(ClassProperty(field, name, cls));
            }
            catch (CompileException ex)
            {
                Problem(file, ex.Line, ex.Message);
            }
        }

        if (_problems.Count > 0) throw new CompileFailedException(_problems);

        var w = new BlobWriter();
        w.U32(1); // format version
        w.String("x64");
        w.String("1048625");
        w.U32(0x7ea54400);
        w.U32(0x045ee5ed);
        w.String("Release");

        WriteArray(w, enumDefs, WriteEnum);
        WriteArray(w, structDefs, WriteStruct);
        w.Compressed(classDefs.Count);
        foreach (var bytes in classDefs) w.Bytes(bytes);
        WriteArray(w, global, WriteFunction);
        WriteArray(w, replaceGlobal, WriteFunction);
        WriteArray(w, replaceClass, WriteFunction);
        WriteArray(w, add, WriteFunction);
        WriteArray(w, wrap, WriteFunction);
        WriteArray(w, addedFields, WriteProperty);

        WriteArray(w, _tables.ImportTypes, (bw, t) =>
        {
            bw.Compressed(t.Name);
            bw.Compressed(t.Kind);
        });
        w.Compressed(0); // imported properties: the compiler doesn't use them
        WriteArray(w, _tables.ImportFunctions, (bw, f) =>
        {
            bw.Compressed(f.Name);
            bw.Compressed(f.ScopeType);
        });

        int tableOffset = w.Length;
        w.U32((uint)_tables.Strings.Count);
        foreach (string s in _tables.Strings) w.String(s);
        w.U32((uint)tableOffset);
        return w.ToArray();
    }

    private void Problem(string? file, int line, string text) => _problems.Add(new CompileProblem(file, line, text));

    private void Guard(string file, string what, Action action)
    {
        try { action(); }
        catch (CompileException ex) { Problem(file, ex.Line, $"{what}: {ex.Message}"); }
    }

    // ---- ordering and planning ----

    /// <summary>
    /// The order the compiler lists classes in. Classes that extend nothing come first, in the order they were written;
    /// then the ones that extend the game's classes, grouped by the class they extend (in the order the game declares
    /// those). Each class is followed by the project's classes that extend it.
    /// </summary>
    private List<(string File, ClassDecl Decl)> ClassOrder(List<(string File, ClassDecl Decl)> classes)
    {
        var keys = new Dictionary<string, int>();
        for (int i = 0; i < classes.Count; i++) keys.TryAdd(Symbols.ClassKey(classes[i].Decl), i);

        string? ParentKey(ClassDecl c) =>
            c.Parent is null ? null
            : c.StateMachineOf is not null ? _symbols.Classes.GetValueOrDefault(Symbols.ClassKey(c))?.Parent
            : c.Parent;

        int? ParentOf(ClassDecl c) => ParentKey(c) is { } key && keys.TryGetValue(key, out int p) ? p : null;

        int GroupOf(int index)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                if (ParentOf(classes[index].Decl) is { } parent && parent != index)
                {
                    index = parent;
                    continue;
                }
                if (ParentKey(classes[index].Decl) is not { } outside) return -1;
                return _symbols.Classes.TryGetValue(outside, out var info) ? info.Rank : int.MaxValue;
            }
            return int.MaxValue;
        }

        var children = classes.Select(_ => new List<int>()).ToArray();
        var roots = new List<int>();
        for (int i = 0; i < classes.Count; i++)
        {
            if (ParentOf(classes[i].Decl) is { } parent && parent != i) children[parent].Add(i);
            else roots.Add(i);
        }
        roots = [.. roots.OrderBy(GroupOf).ThenBy(i => i)];

        var order = new List<(string, ClassDecl)>();
        var stack = new Stack<int>(Enumerable.Reverse(roots));
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            order.Add(classes[index]);
            foreach (int child in Enumerable.Reverse(children[index])) stack.Push(child);
        }
        return order;
    }

    private static int VisibilityFlags(Visibility v) => v switch
    {
        Visibility.Private => 0x4000,
        Visibility.Protected => 0x8000,
        _ => 0x10000,
    };

    private static int EventTimerEntryFlags(FunctionSpecifiers s)
    {
        int flags = 0;
        if (s.IsEvent) flags |= 0x20;
        if (s.IsTimer) flags |= 0x400;
        if (s.IsEntry) flags |= 0x80;
        return flags;
    }

    private static int KindFlags(FunctionSpecifiers s)
    {
        int flags = EventTimerEntryFlags(s);
        if (s.IsFinal) flags |= 0x10;
        if (s.IsLatent) flags |= 0x40;
        if (s.IsImport) flags |= 0x1;
        if (s.IsQuest) flags |= 0x1000;
        if (s.IsCleanup) flags |= 0x2000;
        if (s.IsStoryScene) flags |= 0x800;
        if (s.IsReward) flags |= 0x20000;
        return flags;
    }

    private List<FunctionPlan> MethodPlans(string file, ClassDecl decl)
    {
        string key = Symbols.ClassKey(decl);
        return decl.Methods.Select(m => new FunctionPlan
        {
            Decl = m,
            Label = $"{decl.Name}::{m.Name}",
            File = file,
            Flags = VisibilityFlags(m.Specifiers.Visibility) | KindFlags(m.Specifiers) | (m.Body is null ? 0x200 : 0),
            ThisClass = key,
        }).ToList();
    }

    private void PlanFree(string file, FunctionDecl decl, FreePlans plans)
    {
        FunctionPlan Plan(int flags, string? cls = null, Wrap? wrap = null) => new()
        {
            Decl = decl,
            Label = $"function {decl.Name}",
            File = file,
            Flags = flags,
            ThisClass = cls,
            OverrideClass = cls ?? "",
            Wrap = wrap ?? Wrap.None,
        };

        if (decl.Annotation is not { } annotation)
        {
            plans.Global.Add(Plan(0x2 | (decl.Specifiers.IsExec ? 0x100 : 0) | KindFlags(decl.Specifiers)));
            return;
        }

        switch (annotation.Kind, annotation.Class)
        {
            case (AnnotationKind.AddMethod, { } cls):
                plans.Add.Add(Plan(0x80000 | VisibilityFlags(decl.Specifiers.Visibility) | KindFlags(decl.Specifiers), cls));
                break;
            case (AnnotationKind.WrapMethod, { } cls):
            {
                var target = _symbols.FindMethod(cls, decl.Name)
                    ?? throw new CompileException($"@wrapMethod target {cls}.{decl.Name} not found");
                plans.Wrap.Add(Plan(0x100000 | 0x10000 | EventTimerEntryFlags(target.Specifiers), cls, new Wrap(WrapKind.Class, target)));
                break;
            }
            case (AnnotationKind.WrapMethod, null):
            {
                var target = _symbols.Functions.GetValueOrDefault(decl.Name)
                    ?? throw new CompileException($"@wrapMethod target {decl.Name} not found");
                plans.Wrap.Add(Plan(0x100000 | 0x2, null, new Wrap(WrapKind.Global, target)));
                break;
            }
            case (AnnotationKind.ReplaceMethod, { } cls):
                plans.ReplaceClass.Add(Plan(0x40000 | 0x10000, cls));
                break;
            case (AnnotationKind.ReplaceMethod, null):
                plans.ReplaceGlobal.Add(Plan(0x40000 | 0x2));
                break;
            default:
                throw new CompileException($"unsupported annotation {annotation.Kind}");
        }
    }

    private void Compile(FunctionPlan plan)
    {
        try
        {
            plan.Compiled = FunctionCompiler.Compile(_symbols, _tables, plan.Decl, plan.ThisClass, plan.Wrap);
        }
        catch (CompileException ex)
        {
            Problem(plan.File, ex.Line, $"{plan.Label}: {ex.Message}");
        }
    }

    // ---- definitions ----

    private (int Name, List<(int Name, int Value)> Members) EnumDefinition(EnumDecl decl) =>
        (_tables.Name(decl.Name), decl.Members.Select(m => (_tables.Name(m.Name), m.Value is { } v ? (int)v : ImplicitEnumValue)).ToList());

    private PropDef Property(string name, Ty ty, int flags, string cls, string binding = "")
    {
        return new(_tables.Name(name), "", flags, _tables.Name(Tables.DefinitionTypeName(ty, _symbols)), _tables.Name(cls), _tables.Name(binding));
    }

    /// <summary>What a var's keywords set: editable, const, inlined, import and saved, and autobind's binding.</summary>
    private static int FieldFlags(FieldDecl field)
    {
        var s = field.Specifiers;
        int flags = 0;
        if (s.IsEditable) flags |= 0x1;
        if (s.IsConst) flags |= 0x2;
        if (s.IsInlined) flags |= 0x4;
        if (s.IsImport) flags |= 0x4000;
        if (s.IsSaved) flags |= 0x8000;
        if (field.IsBinding)
        {
            flags |= 0x80000 | 0x2 | 0x8;
            if (s.IsOptional) flags |= 0x100000;
        }
        return flags;
    }

    private PropDef ClassProperty(FieldDecl field, string name, string cls)
    {
        var ty = _symbols.Resolve(field.Type);
        int visibility = field.Specifiers.Visibility switch { Visibility.Private => 0x10000, Visibility.Protected => 0x20000, _ => 0x40000 };
        return Property(name, ty, 0x20 | visibility | FieldFlags(field), cls, field.Binding ?? "");
    }

    private List<DefaultDef> DefaultValues(ClassDecl decl) =>
        decl.Defaults.Select(d => new DefaultDef(_tables.Name(d.Field), _tables.Name(""), DefaultText(d.Value))).ToList();

    private static string DefaultText(Expr expr) => expr.Kind switch
    {
        IntLit i => i.Value.ToString(),
        FloatLit f => f.Text,
        StrLit s => s.Value,
        NameLit n => n.Value,
        BoolLit b => b.Value ? "true" : "false",
        Ident id => id.Name,
        Unary { Op: "-", Operand.Kind: FloatLit f } => (-f.Value).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        Unary { Op: "-" } u => "-" + DefaultText(u.Operand),
        NullLit => "NULL",
        _ => throw new CompileException("this default value isn't supported", expr.Line),
    };

    private (int, int, List<PropDef>, List<DefaultDef>) StructDefinition(ClassDecl decl)
    {
        int name = _tables.Name(decl.Name);
        var props = new List<PropDef>();
        foreach (var field in decl.Fields)
        {
            var ty = _symbols.Resolve(field.Type);
            foreach (string n in field.Names) props.Add(Property(n, ty, FieldFlags(field), ""));
        }
        return (name, decl.IsImport ? 2 : 0, props, DefaultValues(decl));
    }

    private byte[] ClassDefinition(ClassPlan plan)
    {
        var decl = plan.Decl;
        int name = _tables.Name(decl.Name);
        int parent = _tables.Name(decl.Parent ?? "");
        int machine = _tables.Name(decl.StateMachineOf ?? "");

        var props = new List<PropDef>();
        foreach (var field in decl.Fields)
            foreach (string n in field.Names) props.Add(ClassProperty(field, n, ""));

        var functions = FunctionDefinitions(plan.Methods);
        var defaults = DefaultValues(decl);

        var w = new BlobWriter();
        w.Compressed(name);
        w.Compressed(parent);
        w.Compressed(machine);
        w.U8(decl.StateMachineOf is null ? 0 : 1);
        w.Compressed((decl.IsAbstract ? 1 : 0) | (decl.IsImport ? 2 : 0) | (decl.IsStateMachine ? 0x400 : 0));
        WriteArray(w, props, WriteProperty);
        WriteArray(w, functions, WriteFunction);
        WriteArray(w, defaults, WriteDefault);
        return w.ToArray();
    }

    private List<FuncDef> FunctionDefinitions(List<FunctionPlan> plans)
    {
        var result = new List<FuncDef>();
        foreach (var plan in plans)
        {
            if (plan.Compiled is null) continue;
            try
            {
                result.Add(FunctionDefinition(plan));
            }
            catch (CompileException ex)
            {
                Problem(plan.File, ex.Line, $"{plan.Label}: {ex.Message}");
            }
        }
        return result;
    }

    private FuncDef FunctionDefinition(FunctionPlan plan)
    {
        var decl = plan.Decl;
        int name = _tables.Name(decl.Name);
        int overrideClass = _tables.Name(plan.OverrideClass);

        PropDef? ret = null;
        if (decl.ReturnType is { } rt && _symbols.Resolve(rt) is { Kind: not TyKind.Void } returnType)
            ret = Property("__return", returnType, 0x40, "");

        var parms = new List<PropDef>();
        foreach (var p in decl.Params)
        {
            int flags = 0x80 | (p.IsOut ? 0x200 : 0) | (p.IsOptional ? 0x400 : 0);
            parms.Add(Property(p.Name, _symbols.Resolve(p.Type), flags, ""));
        }

        var locals = plan.Compiled!.Locals.Select(l => Property(l.Name, l.Type, 0x100, "")).ToList();
        return new FuncDef(name, overrideClass, plan.Flags, ret, parms, locals, Emit.Function(plan.Compiled.Code));
    }

    // ---- writing ----

    private static void WriteArray<T>(BlobWriter w, IReadOnlyCollection<T> items, Action<BlobWriter, T> write)
    {
        w.Compressed(items.Count);
        foreach (var item in items) write(w, item);
    }

    private static void WriteProperty(BlobWriter w, PropDef p)
    {
        w.Compressed(p.Name);
        w.String(p.Hint);
        w.Compressed(p.Flags);
        w.Compressed(p.Type);
        w.Compressed(p.Class);
        w.Compressed(p.Binding);
    }

    private static void WriteDefault(BlobWriter w, DefaultDef d)
    {
        w.Compressed(d.Name);
        w.Compressed(d.ValueName);
        w.String(d.Value);
        w.Compressed(0); // no sub values
    }

    private static void WriteEnum(BlobWriter w, (int Name, List<(int Name, int Value)> Members) e)
    {
        w.Compressed(e.Name);
        WriteArray(w, e.Members, (bw, m) =>
        {
            bw.Compressed(m.Name);
            bw.Compressed(m.Value);
        });
    }

    private static void WriteStruct(BlobWriter w, (int Name, int Flags, List<PropDef> Props, List<DefaultDef> Defaults) s)
    {
        w.Compressed(s.Name);
        w.Compressed(s.Flags);
        WriteArray(w, s.Props, WriteProperty);
        WriteArray(w, s.Defaults, WriteDefault);
    }

    private static void WriteFunction(BlobWriter w, FuncDef f)
    {
        w.Compressed(f.Name);
        w.Compressed(f.OverrideClass);
        w.Compressed(f.Flags);
        if (f.Return is { } ret)
        {
            w.U8(1);
            WriteProperty(w, ret);
        }
        else
            w.U8(0);
        WriteArray(w, f.Params, WriteProperty);
        WriteArray(w, f.Locals, WriteProperty);
        w.Compressed(f.Code.Length);
        w.Bytes(f.Code);
    }
}
