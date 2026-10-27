using W3ModWorkbench.Localisation;
// Part of the built-in compiler: a C# port of the .ws-to-rsblob compiler written by WebSpam for Aelto's tw3-cahirb (MIT).
// https://github.com/Aelto/tw3-cahirb/pull/1

namespace W3ModWorkbench.Core.Scripts.Standalone;

public enum WrapKind { None, Class, Global }

public sealed record Wrap(WrapKind Kind, FunctionInfo? Target)
{
    public static readonly Wrap None = new(WrapKind.None, null);
}

public sealed class CompiledBody
{
    public required List<Ins> Code { get; init; }
    public required List<(string Name, Ty Type)> Locals { get; init; }
}

/// <summary>Turns one function's statements into bytecode.</summary>
public sealed partial class FunctionCompiler
{
    private enum Slot { First, Second }

    private sealed record Fixup(int Instruction, Slot Slot, int Label);

    private sealed record LoopLabels(int Break, int? Continue);

    private abstract record Resolved;
    private sealed record LocalVar(Ty Type) : Resolved;
    private sealed record ParamVar(Ty Type) : Resolved;
    private sealed record FieldVar(FieldInfo Field) : Resolved;
    private sealed record EnumConstant(string Enum, int Value) : Resolved;
    private sealed record GlobalObject(Op Op, Ty Type) : Resolved;

    private enum Receiver { ImplicitThis, Super, Object }

    private abstract record Callee;
    private sealed record MethodCallee(FunctionInfo Info, Receiver Receiver, Expr? Object) : Callee;
    private sealed record GlobalCallee(FunctionInfo Info) : Callee;
    private sealed record ArrayCallee(string Method, Expr Array, Ty Element) : Callee;
    private sealed record MapCallee(string Method, Expr Map, Ty MapType) : Callee;
    private sealed record ConstructorCallee(string Struct) : Callee;
    private sealed record WrappedCallee(FunctionInfo Info, bool IsClass) : Callee;

    private abstract record BinaryPlan;
    private sealed record LogicPlan(string Name) : BinaryPlan;
    private sealed record HandleAndPlan : BinaryPlan;
    private sealed record InternalPlan(string Name, Ty Left, Ty Right, Ty Result) : BinaryPlan;
    private sealed record TestPlan(bool Equal, Ty Type) : BinaryPlan;

    private readonly Symbols _symbols;
    private readonly Tables _tables;
    private readonly string? _thisClass;
    private readonly string _functionName;
    private readonly bool _isGlobal;
    private readonly Wrap _wrap;
    private readonly Dictionary<string, Ty> _params;
    private readonly Dictionary<string, Ty> _locals;
    private readonly Ty _returnType;
    private readonly List<Ins> _code = [];
    private readonly List<Fixup> _fixups = [];
    private readonly List<int?> _labels = [];
    private readonly List<LoopLabels> _loops = [];
    private int _line;
    // what's wrong, statement by statement: like wcc_lite, a function is compiled through to the end to find all of it
    private readonly List<CompileException> _errors = [];
    // what wcc_lite warns about, which doesn't stop the compile: told as it's found, so an error later in the function doesn't lose it
    private readonly Action<int, string>? _warn;
    private readonly FunctionSpecifiers _specifiers;

    private FunctionCompiler(Symbols symbols, Tables tables, FunctionDecl decl, string? thisClass, Wrap wrap,
        Dictionary<string, Ty> parms, Dictionary<string, Ty> locals, Ty returnType, Action<int, string>? warn)
    {
        _warn = warn;
        _specifiers = decl.Specifiers;
        _symbols = symbols;
        _tables = tables;
        _thisClass = thisClass;
        _isGlobal = thisClass is null;
        _functionName = decl.Name;
        _wrap = wrap;
        _params = parms;
        _locals = locals;
        _returnType = returnType;
    }

    /// <param name="warn">Told each warning, with its line.</param>
    public static CompiledBody Compile(Symbols symbols, Tables tables, FunctionDecl decl, string? thisClass, Wrap wrap, Action<int, string>? warn = null)
    {
        var raw = new List<(string, TypeRef)>();
        if (decl.Body is not null) CollectLocals(decl.Body, raw);
        var locals = raw.Select(l => (l.Item1, symbols.Resolve(l.Item2))).ToList();

        var parms = new Dictionary<string, Ty>();
        foreach (var p in decl.Params) parms[p.Name] = symbols.Resolve(p.Type);
        // an event returns a bool, and a wrapper what it wraps, whether or not it says so
        var ret = decl.ReturnType is { } rt ? symbols.Resolve(rt)
            : decl.Specifiers.IsEvent ? Ty.Bool
            : wrap.Target?.ReturnType ?? Ty.Void;

        var localMap = new Dictionary<string, Ty>();
        foreach (var (name, ty) in locals) localMap[name] = ty;

        var compiler = new FunctionCompiler(symbols, tables, decl, thisClass, wrap, parms, localMap, ret, warn);
        var body = decl.Body ?? [];
        if (body.Count > 0)
        {
            foreach (var statement in body) compiler.GenStmt(statement);
            if (compiler._errors.Count > 0) throw new CompileErrorsException(compiler._errors);
            compiler.Add(Op.Nop);
        }
        else if (decl.Body is null && decl.Specifiers.IsImport && (thisClass is null || decl.Annotation is { Kind: AnnotationKind.AddMethod }))
            compiler.GenImportCall(decl);
        return new CompiledBody { Code = compiler.Finish(), Locals = locals };
    }

    private static void CollectLocals(IEnumerable<Stmt> statements, List<(string, TypeRef)> output)
    {
        foreach (var s in statements) CollectLocalsIn(s, output);
    }

    private static void CollectLocalsIn(Stmt statement, List<(string, TypeRef)> output)
    {
        switch (statement.Kind)
        {
            case VarStmt v:
                foreach (string name in v.Decl.Names) output.Add((name, v.Decl.Type));
                break;
            case IfStmt i:
                CollectLocalsIn(i.Then, output);
                if (i.Else is not null) CollectLocalsIn(i.Else, output);
                break;
            case WhileStmt w: CollectLocalsIn(w.Body, output); break;
            case DoWhileStmt d: CollectLocalsIn(d.Body, output); break;
            case ForStmt f: CollectLocalsIn(f.Body, output); break;
            case SwitchStmt sw:
                foreach (var item in sw.Items.OfType<StmtItem>()) CollectLocalsIn(item.Stmt, output);
                break;
            case BlockStmt b: CollectLocals(b.Statements, output); break;
        }
    }

    private static string? TypeTag(Ty ty) => ty.Kind switch
    {
        TyKind.Int => "Int32",
        TyKind.Float => "Float",
        TyKind.Byte => "Uint8",
        TyKind.String => "String",
        TyKind.Bool => "Bool",
        TyKind.Struct or TyKind.Primitive => ty.Name,
        _ => null,
    };

    private static string OperatorPrefix(string op) => op switch
    {
        "+" or "+=" => "Add",
        "-" or "-=" => "Subtract",
        "*" or "*=" => "Multiply",
        "/" or "/=" => "Divide",
        "%" => "Modulo",
        "&" or "&=" => "And",
        "|" or "|=" => "Or",
        "^" => "Xor",
        "==" => "Equal",
        "!=" => "NotEqual",
        "<" => "Less",
        "<=" => "LessEqual",
        ">" => "Greater",
        ">=" => "GreaterEqual",
        _ => throw new CompileException(L.T("CodeGen.UnsupportedOperator", op)),
    };

    private static Ty Numeric(Ty ty) => ty.Kind == TyKind.Enum ? Ty.Int : ty;

    private static (Op Op, string Class)? GlobalObjectOf(string name) => name switch
    {
        "theGame" => (Op.TheGame, "CR4Game"),
        "thePlayer" => (Op.ThePlayer, "CR4Player"),
        "theCamera" => (Op.TheCamera, "CCameraDirector"),
        "theSound" => (Op.TheSound, "CScriptSoundSystem"),
        "theDebug" => (Op.TheDebug, "CDebugAttributesManager"),
        "theTimer" => (Op.TheTimer, "CTimerScriptKeyword"),
        "theInput" => (Op.TheInput, "CInputManager"),
        "theTelemetry" => (Op.TheTelemetry, "CR4TelemetryScriptProxy"),
        "theHud" => (Op.TheHud, "CR4Hud"),
        _ => null,
    };

    private CompileException Fail(string message) => new(message, _line);

    private int Add(Op op)
    {
        _code.Add(new Ins(op));
        return _code.Count - 1;
    }

    private int Add(Ins ins)
    {
        _code.Add(ins);
        return _code.Count - 1;
    }

    private int NewLabel()
    {
        _labels.Add(null);
        return _labels.Count - 1;
    }

    private void Place(int label) => _labels[label] = _code.Count;

    private void JumpTo(int instruction, Slot slot, int label) => _fixups.Add(new Fixup(instruction, slot, label));

    private void Breakpoint(int line) => Add(new Ins(Op.Line) { A = line, B = 0 });

    private List<Ins> Finish()
    {
        int offset = 0;
        var offsets = new int[_code.Count + 1];
        for (int i = 0; i < _code.Count; i++)
        {
            offsets[i] = offset;
            _code[i].Offset = offset;
            offset += _code[i].RuntimeSize;
        }
        offsets[_code.Count] = offset;

        foreach (var fixup in _fixups)
        {
            int targetIndex = _labels[fixup.Label] ?? throw new CompileException(L.T("CodeGen.JumpHasNowhereGo"));
            int target = offsets[targetIndex];
            var ins = _code[fixup.Instruction];
            int fieldEnd = ins.Op == Op.Switch ? 11 : fixup.Slot == Slot.First ? 3 : 5;
            int value = target - (offsets[fixup.Instruction] + fieldEnd);

            switch (ins.Op, fixup.Slot)
            {
                case (Op.Jump or Op.JumpIfFalse or Op.Skip, _):
                case (Op.SwitchLabel, Slot.First):
                case (Op.Conditional, Slot.First):
                case (Op.Context or Op.FinalFunc or Op.VirtualFunc or Op.VirtualParentFunc or Op.EntryFunc, _):
                    ins.A = (short)value;
                    break;
                case (Op.Switch, _):
                case (Op.SwitchLabel, Slot.Second):
                case (Op.Conditional, Slot.Second):
                    ins.B = (short)value;
                    break;
                default:
                    throw new CompileException(L.T("CodeGen.JumpSetInstructionCant"));
            }
            if (value is > short.MaxValue or < short.MinValue && ins.Op is not (Op.Context or Op.FinalFunc or Op.VirtualFunc or Op.VirtualParentFunc or Op.EntryFunc))
                throw new CompileException(L.T("CodeGen.FunctionTooLongJump"));
            if (value is > ushort.MaxValue or < 0 && ins.Op is Op.Context or Op.FinalFunc or Op.VirtualFunc or Op.VirtualParentFunc or Op.EntryFunc)
                throw new CompileException(L.T("CodeGen.FunctionTooLongCalls"));
        }
        return _code;
    }

    // ---- statements ----

    private void GenStmt(Stmt statement)
    {
        try
        {
            GenStatement(statement);
        }
        catch (CompileException ex)
        {
            _errors.Add(ex);
        }
    }

    private void GenStatement(Stmt statement)
    {
        int line = statement.Line;
        _line = line;
        switch (statement.Kind)
        {
            case VarStmt v:
                if (v.Decl.Initializer is { } init)
                    foreach (string name in Enumerable.Reverse(v.Decl.Names)) // `var a, b : T = x;` sets b first, as wcc_lite does
                    {
                        var ty = _locals[name];
                        Breakpoint(line);
                        Add(Op.Assign);
                        Add(new Ins(Op.LocalVar) { A = _tables.Name(name) });
                        GenAs(init, ty, strictly: true);
                    }
                break;
            case ExprStmt e:
                Breakpoint(line);
                Gen(e.Expr);
                break;
            case IfStmt i:
            {
                Breakpoint(line);
                int elseLabel = NewLabel();
                int at = Add(Op.JumpIfFalse);
                JumpTo(at, Slot.First, elseLabel);
                GenAs(i.Cond, Ty.Bool);
                GenStmt(i.Then);
                if (i.Else is { } otherwise)
                {
                    int end = NewLabel();
                    int jump = Add(Op.Jump);
                    JumpTo(jump, Slot.First, end);
                    Place(elseLabel);
                    GenStmt(otherwise);
                    Place(end);
                }
                else
                    Place(elseLabel);
                break;
            }
            case WhileStmt w:
            {
                int start = NewLabel(), end = NewLabel(), again = NewLabel();
                Place(start);
                int at = Add(Op.JumpIfFalse);
                JumpTo(at, Slot.First, end);
                Breakpoint(line);
                GenAs(w.Cond, Ty.Bool);
                LoopBody(w.Body, end, again);
                Place(again); // `continue` goes to the jump back to the top, as wcc_lite does
                int back = Add(Op.Jump);
                JumpTo(back, Slot.First, start);
                Place(end);
                break;
            }
            case DoWhileStmt d:
            {
                int start = NewLabel(), check = NewLabel(), end = NewLabel();
                Place(start);
                LoopBody(d.Body, end, check);
                Place(check);
                int at = Add(Op.JumpIfFalse);
                JumpTo(at, Slot.First, end);
                Breakpoint(line);
                GenAs(d.Cond, Ty.Bool);
                int back = Add(Op.Jump);
                JumpTo(back, Slot.First, start);
                Place(end);
                break;
            }
            case ForStmt f:
            {
                if (f.Init is { } init2)
                {
                    Breakpoint(line);
                    Gen(init2);
                }
                int start = NewLabel(), next = NewLabel(), end = NewLabel();
                Place(start);
                if (f.Cond is { } cond)
                {
                    int at = Add(Op.JumpIfFalse);
                    JumpTo(at, Slot.First, end);
                    GenAs(cond, Ty.Bool);
                }
                LoopBody(f.Body, end, next);
                Place(next);
                if (f.Step is { } step)
                {
                    Breakpoint(line);
                    Gen(step);
                }
                int back = Add(Op.Jump);
                JumpTo(back, Slot.First, start);
                Place(end);
                break;
            }
            case SwitchStmt sw:
                Breakpoint(line);
                GenSwitch(sw.Value, sw.Items);
                break;
            case BreakStmt:
            {
                if (_loops.Count == 0) throw Fail("break outside a loop or switch");
                int target = _loops[^1].Break;
                Breakpoint(line);
                int at = Add(Op.Jump);
                JumpTo(at, Slot.First, target);
                break;
            }
            case ContinueStmt:
            {
                int? target = _loops.AsEnumerable().Reverse().Select(l => l.Continue).FirstOrDefault(c => c.HasValue);
                if (target is null) throw Fail("continue outside a loop");
                Breakpoint(line);
                int at = Add(Op.Jump);
                JumpTo(at, Slot.First, target.Value);
                break;
            }
            case ReturnStmt r:
                if (r.Value is { } returned && _returnType.Kind == TyKind.Void && TypeOf(returned).Kind != TyKind.Void) throw Fail($"Function '{_functionName}' can't return a value");
                if (r.Value is null && _returnType.Kind != TyKind.Void) throw Fail($"Function '{_functionName}' should return a value");
                Add(Op.Return);
                if (r.Value is { } value)
                {
                    var source = TypeOf(value);
                    foreach (var conversion in ConvertInstructions(source, _returnType, implicitly: true)) Add(conversion);
                    Breakpoint(line);
                    Gen(value);
                }
                else
                {
                    Breakpoint(line);
                    Add(Op.Nop);
                }
                break;
            case DeleteStmt del:
                Breakpoint(line);
                Add(Op.Delete);
                Gen(del.Value);
                break;
            case BlockStmt b:
                foreach (var s in b.Statements) GenStmt(s);
                break;
            case EmptyStmt:
                break;
        }
    }

    private void LoopBody(Stmt body, int end, int next)
    {
        _loops.Add(new LoopLabels(end, next));
        try
        {
            GenStmt(body);
            if (body.Kind is EmptyStmt) Add(Op.Nop); // `for (...);` has a body of nothing, which still takes an instruction
        }
        finally { _loops.RemoveAt(_loops.Count - 1); }
    }

    private void GenSwitch(Expr expr, List<SwitchItem> items)
    {
        var ty = TypeOf(expr);
        int exprType = _tables.ImportType(ty, _symbols);
        int end = NewLabel(), defaultLabel = NewLabel();
        bool hasDefault = items.Any(i => i is DefaultItem);

        int switchAt = Add(new Ins(Op.Switch) { A = exprType });
        Gen(expr);

        var groups = new List<List<(int At, int Label)>>();
        bool inGroup = false;
        _loops.Add(new LoopLabels(end, null));

        foreach (var item in items)
        {
            switch (item)
            {
                case CaseItem c:
                {
                    int label = NewLabel();
                    Place(label);
                    int at = Add(Op.SwitchLabel);
                    if (inGroup)
                    {
                        var group = groups[^1];
                        JumpTo(at, Slot.First, group[^1].Label);
                        group.Add((at, label));
                    }
                    else
                        groups.Add([(at, label)]);
                    inGroup = true;

                    int after = NewLabel();
                    JumpTo(at, Slot.Second, after);
                    GenAs(c.Value, ty);
                    Place(after);
                    break;
                }
                case DefaultItem:
                    inGroup = false;
                    Place(defaultLabel);
                    Add(Op.SwitchDefault);
                    break;
                case StmtItem s:
                    inGroup = false;
                    GenStmt(s.Stmt);
                    break;
            }
        }

        _loops.RemoveAt(_loops.Count - 1);
        Place(end);

        int fallback = hasDefault ? defaultLabel : end;
        int EntryOf(List<(int At, int Label)>? group) => group is { Count: > 0 } ? group[^1].Label : fallback;

        JumpTo(switchAt, Slot.First, EntryOf(groups.Count > 0 ? groups[0] : null));
        for (int index = 0; index < groups.Count; index++)
        {
            int headAt = groups[index][0].At;
            int next = EntryOf(index + 1 < groups.Count ? groups[index + 1] : null);
            JumpTo(headAt, Slot.First, next);
        }
    }

    // ---- names and types ----

    private Resolved ResolveIdent(string name)
    {
        if (_locals.TryGetValue(name, out var local)) return new LocalVar(local);
        if (_params.TryGetValue(name, out var param)) return new ParamVar(param);
        if (_thisClass is not null && _symbols.FindField(_thisClass, name) is { } field) return new FieldVar(field);
        if (_symbols.EnumMembers.TryGetValue(name, out string? enumName))
            return new EnumConstant(enumName, _symbols.Enums[enumName].ValueOf(name) ?? 0);
        if (GlobalObjectOf(name) is { } global) return new GlobalObject(global.Op, Ty.Class(global.Class));
        throw Fail($"I dont know any '{name}'");
    }

    private Ty ThisType() => Ty.Class(_thisClass ?? throw Fail("'this' has no sense outside class scope"));

    private string ParentClass()
    {
        string cls = _thisClass ?? throw Fail("'super' is valid only inside function in class");
        return _symbols.Classes.GetValueOrDefault(cls)?.Parent ?? throw Fail($"{cls} has no parent class");
    }

    private string StateMachine()
    {
        string cls = _thisClass ?? throw Fail("'parent' has no sense outside class");
        return _symbols.Classes.GetValueOrDefault(cls)?.StateMachine ?? throw Fail($"Class '{cls}' is not a state and therefore 'parent' has no sense");
    }

    private FieldInfo FieldOf(Ty ty, string name)
    {
        if (ty.Kind is not (TyKind.Class or TyKind.Struct) || _symbols.FindField(ty.Name, name) is not { } field)
            throw Fail($"'{name}' is not a member of '{ReferenceName(ty)}'");
        return field;
    }

    /// <summary>How wcc_lite names the type of something it reads a member of: a reference to it, a class's by its handle.</summary>
    private static string ReferenceName(Ty ty) => ty.Kind == TyKind.Class ? $"&handle:{ty.Name}" : $"&{ty.RttiName}";

    private Ty TypeOf(Expr expr)
    {
        switch (expr.Kind)
        {
            case IntLit: return Ty.Int;
            case FloatLit: return Ty.Float;
            case StrLit: return Ty.String;
            case NameLit: return Ty.Name_;
            case BoolLit: return Ty.Bool;
            case NullLit: return Ty.Null;
            case Ident id:
                return ResolveIdent(id.Name) switch
                {
                    LocalVar l => l.Type,
                    ParamVar p => p.Type,
                    GlobalObject g => g.Type,
                    FieldVar f => f.Field.Type,
                    EnumConstant e => Ty.Enum(e.Enum),
                    _ => throw Fail("unknown name"),
                };
            case ThisExpr: return ThisType();
            case SuperExpr: return Ty.Class(ParentClass());
            case ParentExpr or VirtualParentExpr: return Ty.Class(StateMachine());
            case Member m:
            {
                var objectType = m.Object.Kind is SuperExpr ? ThisType() : TypeOf(m.Object);
                return FieldOf(objectType, m.Name).Type;
            }
            case Call c:
                return ResolveCall(c.Callee) switch
                {
                    MethodCallee m => m.Info.ReturnType,
                    GlobalCallee g => g.Info.ReturnType,
                    WrappedCallee w => w.Info.ReturnType,
                    MapCallee mc => mc.Method switch
                    {
                        "Size" => Ty.Int,
                        "Contains" or "Erase" or "TryGet" => Ty.Bool,
                        "Key" => mc.MapType.Key!,
                        _ => Ty.Void,
                    },
                    ArrayCallee a => a.Method switch
                    {
                        "Size" or "FindFirst" or "FindLast" => Ty.Int,
                        "Contains" or "Remove" or "RemoveFast" => Ty.Bool,
                        "Last" or "PopBack" => a.Element,
                        _ => Ty.Void,
                    },
                    ConstructorCallee k => Ty.Struct(k.Struct),
                    _ => throw Fail("unknown call"),
                };
            case Index ix:
                return TypeOf(ix.Array) is { Kind: TyKind.Array or TyKind.Map } arr ? arr.Element!
                    : throw Fail($"{TypeOf(ix.Array)} can't be indexed");
            case Unary { Op: "!" }: return Ty.Bool;
            case Unary { Op: "~" }: return Ty.Int;
            case Unary u: return Numeric(TypeOf(u.Operand));
            case Binary b:
                return PlanBinary(b.Op, TypeOf(b.Left), TypeOf(b.Right)) switch
                {
                    InternalPlan p => p.Result,
                    _ => Ty.Bool,
                };
            case Assign a: return TypeOf(a.Target);
            case Ternary t:
                return TypeOf(t.Then) is { Kind: TyKind.Null } ? TypeOf(t.Else) : TypeOf(t.Then);
            case Cast c: return _symbols.Resolve(c.Type);
            case New n: return _symbols.Resolve(n.Type);
        }
        throw Fail("unsupported expression");
    }

    private Callee ResolveCall(Expr callee)
    {
        switch (callee.Kind)
        {
            case Ident id:
            {
                string name = id.Name;
                if (name == "wrappedMethod")
                {
                    if (_wrap is { Kind: WrapKind.Class, Target: { } t1 }) return new WrappedCallee(t1, true);
                    if (_wrap is { Kind: WrapKind.Global, Target: { } t2 }) return new WrappedCallee(t2, false);
                }
                if (_thisClass is not null && _symbols.FindMethod(_thisClass, name) is { } method)
                    return new MethodCallee(method, Receiver.ImplicitThis, null);
                if (_symbols.Functions.TryGetValue(name, out var function)) return new GlobalCallee(function);
                if (_symbols.Classes.TryGetValue(name, out var cls) && cls.IsStruct) return new ConstructorCallee(name);
                throw Fail($"Could not find function '{name}'");
            }
            case Member m:
            {
                if (m.Object.Kind is SuperExpr)
                {
                    string parent = ParentClass();
                    var method = _symbols.FindMethod(parent, m.Name) ?? throw Fail($"Could not find function '{m.Name}'");
                    return new MethodCallee(method, Receiver.Super, null);
                }
                var objectType = TypeOf(m.Object);
                if (objectType.Kind == TyKind.Array) return new ArrayCallee(m.Name, m.Object, objectType.Element!);
                if (objectType.Kind == TyKind.Map) return new MapCallee(m.Name, m.Object, objectType);
                if (objectType.Kind == TyKind.Class)
                {
                    var method = _symbols.FindMethod(objectType.Name, m.Name)
                        ?? throw Fail($"Could not find function '{m.Name}'");
                    return new MethodCallee(method, Receiver.Object, m.Object);
                }
                throw Fail($"can't call '{m.Name}' on {objectType}");
            }
        }
        throw Fail("unsupported call target");
    }

    private BinaryPlan PlanBinary(string op, Ty left, Ty right)
    {
        switch (op)
        {
            case "&&" when left.IsClass && right.IsClass: return new HandleAndPlan();
            case "&&": return new LogicPlan("LogicAnd_Bool_Bool");
            case "||": return new LogicPlan("LogicOr_Bool_Bool");
            case "+" when left.Kind == TyKind.String || right.Kind == TyKind.String
                || left.Kind == TyKind.Name && right.Kind == TyKind.Name:
                return new InternalPlan("Add_String_String", Ty.String, Ty.String, Ty.String);
        }

        string prefix = OperatorPrefix(op);
        bool isComparison = op is "==" or "!=" or "<" or "<=" or ">" or ">=";
        var l0 = Numeric(left);
        var r0 = Numeric(right);

        var candidates = new List<(Ty, Ty)> { (l0, r0) };
        if (l0.Kind == TyKind.Int && r0.Kind == TyKind.Float || l0.Kind == TyKind.Float && r0.Kind == TyKind.Int)
            candidates.Add((Ty.Float, Ty.Float));
        else if (l0.Kind == TyKind.Byte && r0.Kind is TyKind.Int or TyKind.Byte || l0.Kind == TyKind.Int && r0.Kind == TyKind.Byte)
        {
            candidates.Add((Ty.Int, Ty.Int));
            candidates.Add((Ty.Byte, Ty.Byte));
        }
        else if (r0.Kind == TyKind.Int)
            candidates.Add((l0, Ty.Float));
        else if (l0.Kind == TyKind.Int)
            candidates.Add((Ty.Float, r0));

        foreach (var (l, r) in candidates)
        {
            if (TypeTag(l) is not { } lt || TypeTag(r) is not { } rt) continue;
            string name = $"{prefix}_{lt}_{rt}";
            if (Operators.IndexOf(name) < 0) continue;
            var result = isComparison ? Ty.Bool : l.Kind == TyKind.Float && r.Kind == TyKind.Struct ? r : l;
            return new InternalPlan(name, l, r, result);
        }

        if (op is "==" or "!=")
            return new TestPlan(op == "==", left.Kind == TyKind.Null ? right : left);
        throw Fail($"Unable to find suitable operator '{op}' for given types ({left.RttiName}, {right.RttiName})");
    }

    /// <summary>
    /// Whether wcc_lite converts one type to another without a cast, as measured against it: numbers widen to each
    /// other only upwards from int and byte, anything simple becomes a string, an object goes up its class tree, and
    /// none of the others go without being asked.
    /// </summary>
    private bool IsImplicit(Ty from, Ty to) => (from.Kind, to.Kind) switch
    {
        (TyKind.Null, TyKind.Class) => true,
        (TyKind.Class, TyKind.Class) => _symbols.IsSubclass(from.Name, to.Name),
        (TyKind.Class, TyKind.Bool or TyKind.String) => true,
        (TyKind.Int, TyKind.Float or TyKind.Byte or TyKind.Bool or TyKind.String or TyKind.Enum) => true,
        (TyKind.Byte, TyKind.Int or TyKind.Float or TyKind.Bool or TyKind.String) => true,
        (TyKind.Float, TyKind.Bool or TyKind.String) => true,
        (TyKind.Bool, TyKind.String) => true,
        (TyKind.String, TyKind.Bool) => true,
        (TyKind.Name, TyKind.String) => true,
        (TyKind.Enum, TyKind.Int or TyKind.String) => true,
        (TyKind.Enum, TyKind.Enum) => from.Name == to.Name,
        // not measured: left as they were
        (TyKind.Array, TyKind.Array) or (TyKind.Map, TyKind.Map) or (TyKind.Struct, TyKind.Struct) => true,
        (TyKind.Primitive, _) or (_, TyKind.Primitive) or (_, TyKind.Void) => true,
        _ => false,
    };

    /// <summary>Whether wcc_lite converts one type to another when a cast is written, and not without.</summary>
    private bool IsExplicitOnly(Ty from, Ty to) => (from.Kind, to.Kind) switch
    {
        (TyKind.Float or TyKind.Bool or TyKind.String, TyKind.Int or TyKind.Byte) => true,
        (TyKind.Bool or TyKind.String, TyKind.Float) => true,
        (TyKind.Name, TyKind.Bool) => true,
        (TyKind.Class, TyKind.Class) => _symbols.IsSubclass(to.Name, from.Name),
        _ => false,
    };

    /// <summary>A type as wcc_lite's messages name it: an object's by its handle.</summary>
    private static string MessageName(Ty ty) => ty.Kind == TyKind.Class ? $"handle:{ty.Name}" : ty.RttiName;

    /// <param name="implicitly">Without a cast written: only the conversions wcc_lite makes by itself are allowed.</param>
    /// <param name="fromVariable">The value is a variable's, which wcc_lite's messages name as a reference (<c>&amp;Float</c>).</param>
    private List<Ins> ConvertInstructions(Ty from, Ty to, bool implicitly = false, bool fromVariable = false)
    {
        var result = new List<Ins>();
        if (from == to) return result;
        if (implicitly && !IsImplicit(from, to))
        {
            string source = (fromVariable ? "&" : "") + MessageName(from), target = MessageName(to);
            throw Fail(IsExplicitOnly(from, to)
                ? $"No implicit cast exists for converting '{source}' to '{target}'"
                : $"Unable to convert from '{source}' to '{target}'");
        }
        Ins Plain(Op op) => new(op);
        Ins Typed(Op op, string enumName) => new(op) { A = _tables.ImportType(Ty.Enum(enumName), _symbols) };

        switch (from.Kind, to.Kind)
        {
            case (TyKind.Null, _): break;
            case (TyKind.Class, TyKind.Class): break;
            case (TyKind.Class, TyKind.Bool): result.Add(Plain(Op.ObjectToBool)); break;
            case (TyKind.Class, TyKind.String): result.Add(Plain(Op.ObjectToString)); break;
            case (TyKind.Int, TyKind.Float): result.Add(Plain(Op.IntToFloat)); break;
            case (TyKind.Int, TyKind.Byte): result.Add(Plain(Op.IntToByte)); break;
            case (TyKind.Int, TyKind.Bool): result.Add(Plain(Op.IntToBool)); break;
            case (TyKind.Int, TyKind.String): result.Add(Plain(Op.IntToString)); break;
            case (TyKind.Int, TyKind.Enum): result.Add(Typed(Op.IntToEnum, to.Name)); break;
            case (TyKind.Byte, TyKind.Int): result.Add(Plain(Op.ByteToInt)); break;
            case (TyKind.Byte, TyKind.Float): result.Add(Plain(Op.ByteToFloat)); break;
            case (TyKind.Byte, TyKind.Bool): result.Add(Plain(Op.ByteToBool)); break;
            case (TyKind.Byte, TyKind.String): result.Add(Plain(Op.ByteToString)); break;
            case (TyKind.Float, TyKind.Int): result.Add(Plain(Op.FloatToInt)); break;
            case (TyKind.Float, TyKind.Byte): result.Add(Plain(Op.FloatToByte)); break;
            case (TyKind.Float, TyKind.Bool): result.Add(Plain(Op.FloatToBool)); break;
            case (TyKind.Float, TyKind.String): result.Add(Plain(Op.FloatToString)); break;
            case (TyKind.Bool, TyKind.Int): result.Add(Plain(Op.BoolToInt)); break;
            case (TyKind.Bool, TyKind.Byte): result.Add(Plain(Op.BoolToByte)); break;
            case (TyKind.Bool, TyKind.Float): result.Add(Plain(Op.BoolToFloat)); break;
            case (TyKind.Bool, TyKind.String): result.Add(Plain(Op.BoolToString)); break;
            case (TyKind.Name, TyKind.String): result.Add(Plain(Op.NameToString)); break;
            case (TyKind.Name, TyKind.Bool): result.Add(Plain(Op.NameToBool)); break;
            case (TyKind.String, TyKind.Int): result.Add(Plain(Op.StringToInt)); break;
            case (TyKind.String, TyKind.Float): result.Add(Plain(Op.StringToFloat)); break;
            case (TyKind.String, TyKind.Bool): result.Add(Plain(Op.StringToBool)); break;
            case (TyKind.String, TyKind.Byte): result.Add(Plain(Op.StringToByte)); break;
            case (TyKind.Enum, TyKind.Int): result.Add(Typed(Op.EnumToInt, from.Name)); break;
            case (TyKind.Enum, TyKind.String): result.Add(Typed(Op.EnumToString, from.Name)); break;
            case (TyKind.Enum, TyKind.Float or TyKind.Bool or TyKind.Byte):
                result.AddRange(ConvertInstructions(Ty.Int, to));
                result.Add(Typed(Op.EnumToInt, from.Name));
                break;
            case (TyKind.Enum, TyKind.Enum): break;
            case (TyKind.Array, TyKind.Array): break;
            case (TyKind.Map, TyKind.Map): break;
            case (TyKind.Struct, TyKind.Struct): break;
            case (TyKind.Primitive, _) or (_, TyKind.Primitive): break;
            case (_, TyKind.Void): break;
            default: throw Fail($"No implicit cast exists for converting '{from.RttiName}' to '{to.RttiName}'");
        }
        return result;
    }
}
