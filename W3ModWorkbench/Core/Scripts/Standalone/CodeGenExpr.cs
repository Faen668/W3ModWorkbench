// Part of the built-in compiler: a C# port of the .ws-to-rsblob compiler written by WebSpam for Aelto's tw3-cahirb (MIT).
// https://github.com/Aelto/tw3-cahirb/pull/1

namespace W3ModWorkbench.Core.Scripts.Standalone;

// Expressions: the other half of FunctionCompiler.
public sealed partial class FunctionCompiler
{
    /// <summary>A variable, field, member or element: something with a place of its own, not a temporary.</summary>
    private bool IsVariable(Expr e) => e.Kind switch
    {
        Ident id => ResolveIdent(id.Name) is LocalVar or ParamVar or FieldVar,
        Member or Index => true,
        _ => false,
    };

    /// <param name="strictly">
    /// Where wcc_lite allows only the conversions it makes by itself: a value assigned, passed or returned. An operator's
    /// operands and a condition convert as they need to.
    /// </param>
    private void GenAs(Expr expr, Ty target, bool strictly = false)
    {
        var source = TypeOf(expr);
        foreach (var conversion in ConvertInstructions(source, target, implicitly: strictly, fromVariable: strictly && IsVariable(expr))) Add(conversion);
        Gen(expr);
    }

    private void InternalCall(string name, int line, Action operands)
    {
        int index = Operators.IndexOf(name);
        if (index < 0) throw Fail($"unknown operator {name}");
        int end = NewLabel();
        int at = Add(new Ins(Op.FinalFunc) { B = line, C = FuncRef.Operator(index) });
        JumpTo(at, Slot.First, end);
        operands();
        Add(Op.ParamEnd);
        Place(end);
    }

    private (int Name, int Scope) PropertyOf(FieldInfo field) => (_tables.Name(field.Name), _tables.ClassType(field.Owner, _symbols));

    private void GenEnumConstant(string enumName, int value)
    {
        var info = _symbols.Enums[enumName];
        bool Fits(int min, int max) => info.Values.All(v => v.Value >= min && v.Value <= max);
        if (info.IsScript && Fits(sbyte.MinValue, sbyte.MaxValue))
            Add(new Ins(Op.ByteConst) { A = (byte)value });
        else if (info.IsScript && Fits(short.MinValue, short.MaxValue))
            Add(new Ins(Op.ShortConst) { A = (short)value });
        else
            Add(new Ins(Op.IntConst) { A = value });
    }

    private void Gen(Expr expr)
    {
        int line = expr.Line;
        switch (expr.Kind)
        {
            case IntLit { Value: 0 }: Add(Op.IntZero); break;
            case IntLit { Value: 1 }: Add(Op.IntOne); break;
            case IntLit i: Add(new Ins(Op.IntConst) { A = (int)i.Value }); break;
            case FloatLit f: Add(new Ins(Op.FloatConst) { Float = f.Value }); break;
            case StrLit s: Add(new Ins(Op.StringConst) { Text = s.Value }); break;
            case NameLit n: Add(new Ins(Op.NameConst) { A = _tables.Name(n.Value == "None" ? "" : n.Value) }); break;
            case BoolLit { Value: true }: Add(Op.BoolTrue); break;
            case BoolLit: Add(Op.BoolFalse); break;
            case NullLit: Add(Op.Null); break;
            case Ident id:
                switch (ResolveIdent(id.Name))
                {
                    case LocalVar:
                        Add(new Ins(Op.LocalVar) { A = _tables.Name(id.Name) });
                        break;
                    case ParamVar:
                        Add(new Ins(Op.ParamVar) { A = _tables.Name(id.Name) });
                        break;
                    case FieldVar fv:
                    {
                        var (name, scope) = PropertyOf(fv.Field);
                        Add(new Ins(fv.Field.IsBindable ? Op.BindableVar : Op.ObjectVar) { A = name, B = scope });
                        break;
                    }
                    case EnumConstant ec: GenEnumConstant(ec.Enum, ec.Value); break;
                    case GlobalObject g: Add(g.Op); break;
                }
                break;
            case ThisExpr: Add(Op.This); break;
            case ParentExpr or VirtualParentExpr:
                Add(Op.Parent);
                Add(Op.This);
                break;
            case SuperExpr: throw Fail("'super' can only be followed by a member");
            case Member m: GenMember(m.Object, m.Name); break;
            case Call c: GenCall(c.Callee, c.Args, line); break;
            case Index ix:
            {
                var arrayType = TypeOf(ix.Array);
                Add(new Ins(arrayType.Kind == TyKind.Map ? MapOp.Element : Op.ArrayElement) { A = _tables.ImportType(arrayType, _symbols) });
                Gen(ix.Array);
                GenAs(ix.Idx, arrayType.Kind == TyKind.Map ? arrayType.Key! : Ty.Int);
                break;
            }
            case Unary u: GenUnary(u.Op, u.Operand, line); break;
            case Binary b: GenBinary(b.Op, b.Left, b.Right, line); break;
            case Assign a: GenAssign(a.Op, a.Target, a.Value, line); break;
            case Ternary t:
            {
                var ty = TypeOf(expr);
                int elseLabel = NewLabel(), end = NewLabel();
                int at = Add(Op.Conditional);
                JumpTo(at, Slot.First, elseLabel);
                JumpTo(at, Slot.Second, end);
                GenAs(t.Cond, Ty.Bool);
                GenAs(t.Then, ty);
                Place(elseLabel);
                GenAs(t.Else, ty);
                Place(end);
                break;
            }
            case Cast c:
            {
                var target = _symbols.Resolve(c.Type);
                var source = TypeOf(c.Operand);
                if (source.Kind == TyKind.Class && target.Kind == TyKind.Class && !_symbols.IsSubclass(source.Name, target.Name))
                    Add(new Ins(Op.DynamicCast) { A = _tables.ClassType(target.Name, _symbols) });
                else
                    foreach (var conversion in ConvertInstructions(source, target)) Add(conversion);
                Gen(c.Operand);
                break;
            }
            case New n:
            {
                var cls = _symbols.Resolve(n.Type);
                if (cls.Kind != TyKind.Class) throw Fail($"'new' needs a class, and {n.Type.Name} isn't one");
                Add(new Ins(Op.New) { A = _tables.ClassType(cls.Name, _symbols) });
                if (n.Owner is { } owner) Gen(owner);
                else Add(Op.Nop);
                break;
            }
            default:
                throw Fail("unsupported expression");
        }
    }

    private void GenMember(Expr obj, string name)
    {
        if (obj.Kind is SuperExpr)
        {
            var (n, s) = PropertyOf(FieldOf(ThisType(), name));
            Add(new Ins(Op.ObjectVar) { A = n, B = s });
            return;
        }

        var objectType = TypeOf(obj);
        var field = FieldOf(objectType, name);
        if (objectType.Kind == TyKind.Struct)
        {
            var (n, s) = PropertyOf(field);
            Add(new Ins(Op.StructMember) { A = n, B = s });
            Gen(obj);
            return;
        }

        int end = NewLabel();
        int at = Add(Op.Context);
        JumpTo(at, Slot.First, end);
        Gen(obj);
        var (pn, ps) = PropertyOf(field);
        Add(new Ins(field.IsBindable ? Op.BindableVar : Op.ObjectVar) { A = pn, B = ps });
        Place(end);
    }

    private void GenArgs(FunctionInfo info, List<Expr?> args)
    {
        if (info.IsOpaque)
        {
            foreach (var arg in args)
                if (arg is not null) Gen(arg);
                else Add(Op.Nop);
            return;
        }
        var parms = info.Params;
        if (args.Count > parms.Count) throw Fail($"Function '{info.Name}' does not take {args.Count} param(s)");
        for (int i = 0; i < parms.Count; i++)
        {
            if (i < args.Count && args[i] is { } arg) GenAs(arg, parms[i].Type, strictly: true);
            else Add(Op.Nop);
        }
    }

    /// <summary>
    /// An import a mod declares, global or added to a class, as wcc_lite compiles it: <c>return Self(params…);</c>,
    /// the call on the line the declaration starts on (its annotation's, when it has one).
    /// </summary>
    private void GenImportCall(FunctionDecl decl)
    {
        Add(Op.Return);
        int end = NewLabel();
        int at = Add(new Ins(Op.FinalFunc) { B = decl.Annotation is { Line: > 0 } a ? a.Line : decl.Line, C = FuncRef.Current });
        JumpTo(at, Slot.First, end);
        foreach (var p in decl.Params) Add(new Ins(Op.ParamVar) { A = _tables.Name(p.Name) });
        Add(Op.ParamEnd);
        Place(end);
        Add(Op.Nop);
    }

    private void GenCall(Expr callee, List<Expr?> args, int line)
    {
        switch (ResolveCall(callee))
        {
            case WrappedCallee w:
            {
                int end = NewLabel();
                int at = w.IsClass
                    ? Add(new Ins(Op.VirtualFunc) { B = line, C = _tables.Name("@self") })
                    : Add(new Ins(Op.FinalFunc) { B = line, C = FuncRef.Current });
                JumpTo(at, Slot.First, end);
                GenArgs(w.Info, args);
                Add(Op.ParamEnd);
                Place(end);
                break;
            }
            case GlobalCallee g:
            {
                int end = NewLabel();
                int function = _isGlobal && g.Info.Name == _functionName
                    ? FuncRef.Current
                    : FuncRef.Import(_tables.ImportFunction(g.Info.Name, null, _symbols));
                int at = Add(new Ins(Op.FinalFunc) { B = line, C = function });
                JumpTo(at, Slot.First, end);
                GenArgs(g.Info, args);
                Add(Op.ParamEnd);
                Place(end);
                break;
            }
            case MethodCallee m:
            {
                int? contextEnd = null;
                if (m.Receiver == Receiver.Object)
                {
                    contextEnd = NewLabel();
                    int at = Add(Op.Context);
                    JumpTo(at, Slot.First, contextEnd.Value);
                    Gen(m.Object!);
                }
                bool viaParent = m.Receiver == Receiver.Object && m.Object!.Kind is ParentExpr;
                bool viaVirtualParent = m.Receiver == Receiver.Object && m.Object!.Kind is VirtualParentExpr;
                Invoke(m.Info, args, line, m.Receiver == Receiver.Super, viaParent, viaVirtualParent);
                if (contextEnd is { } e) Place(e);
                break;
            }
            case ArrayCallee a: GenArrayCall(a, args); break;
            case MapCallee mc: GenMapCall(mc, args); break;
            case ConstructorCallee k: GenConstructor(k.Struct, args); break;
        }
    }

    private void GenArrayCall(ArrayCallee a, List<Expr?> args)
    {
        var element = a.Element;
        int typeRef = _tables.ImportType(TypeOf(a.Array), _symbols);
        (Op Op, Ty[] Params) spec = a.Method switch
        {
            "Size" => (Op.ArraySize, []),
            "Clear" => (Op.ArrayClear, []),
            "PushBack" => (Op.ArrayPushBack, [element]),
            "PopBack" => (Op.ArrayPopBack, []),
            "Insert" => (Op.ArrayInsert, [Ty.Int, element]),
            "Remove" => (Op.ArrayRemove, [element]),
            "RemoveFast" => (Op.ArrayRemoveFast, [element]),
            "Contains" => (Op.ArrayContains, [element]),
            "FindFirst" => (Op.ArrayFindFirst, [element]),
            "FindLast" => (Op.ArrayFindLast, [element]),
            "Resize" => (Op.ArrayResize, [Ty.Int]),
            "Grow" => (Op.ArrayGrow, [Ty.Int]),
            "Erase" => (Op.ArrayErase, [Ty.Int]),
            "EraseFast" => (Op.ArrayEraseFast, [Ty.Int]),
            "Last" => (Op.ArrayLast, []),
            _ => throw Fail($"arrays have no method '{a.Method}'"),
        };
        if (args.Count != spec.Params.Length) throw Fail($"array {a.Method} takes {spec.Params.Length} arguments, not {args.Count}");
        // wcc_lite compares a variable's value in place (the Fast operations) and a temporary (a literal, `this`, a call) the long way
        var op = spec.Op;
        if (args.Count > 0 && args[0] is { } firstArg && IsVariable(firstArg))
            op = op switch
            {
                Op.ArrayContains => Op.ArrayContainsFast,
                Op.ArrayFindFirst => Op.ArrayFindFirstFast,
                Op.ArrayFindLast => Op.ArrayFindLastFast,
                Op.ArrayRemove => Op.ArrayRemoveFast,
                _ => op,
            };
        Add(new Ins(op) { A = typeRef });
        Gen(a.Array);
        for (int i = 0; i < args.Count; i++)
            GenAs(args[i] ?? throw Fail("an argument is missing"), spec.Params[i]);
    }

    private bool IsWritableMapOutput(Expr expression) => expression.Kind switch
    {
        Ident id => ResolveIdent(id.Name) is LocalVar or ParamVar || ResolveIdent(id.Name) is FieldVar { Field.IsConst: false },
        Member member => !FieldOf(TypeOf(member.Object), member.Name).IsConst
            && !(member.Object.Kind is Ident id && ResolveIdent(id.Name) is GlobalObject)
            && (TypeOf(member.Object).Kind != TyKind.Struct || IsWritableMapOutput(member.Object)),
        Index index => IsWritableMapOutput(index.Array),
        _ => false
    };

    private void GenMapCall(MapCallee m, List<Expr?> args)
    {
        (Op Op, Ty[] Params) spec = m.Method switch
        {
            "Clear" => (MapOp.Clear, []),
            "Size" => (MapOp.Size, []),
            "Contains" => (MapOp.Contains, [m.MapType.Key!]),
            "Erase" => (MapOp.Erase, [m.MapType.Key!]),
            "TryGet" => (MapOp.TryGet, [m.MapType.Key!, m.MapType.Element!]),
            "Key" => (MapOp.Key, [Ty.Int]),
            _ => throw Fail($"maps have no method '{m.Method}'"),
        };
        if (args.Count != spec.Params.Length) throw Fail($"map {m.Method} takes {spec.Params.Length} arguments, not {args.Count}");
        if (m.Method == "TryGet" && (args[1] is not { } output || !IsWritableMapOutput(output) || TypeOf(output) != m.MapType.Element))
            throw Fail($"map TryGet needs an assignable {m.MapType.Element} as its second argument");
        Add(new Ins(spec.Op) { A = _tables.ImportType(m.MapType, _symbols) });
        Gen(m.Map);
        for (int i = 0; i < args.Count; i++)
            if (m.Method == "TryGet" && i == 1) Gen(args[i]!);
            else GenAs(args[i] ?? throw Fail("an argument is missing"), spec.Params[i]);
    }

    private void GenConstructor(string name, List<Expr?> args)
    {
        var cls = _symbols.Classes[name];
        var fields = cls.FieldOrder.Select(f => cls.Fields[f].Type).ToList();
        int typeRef = _tables.ImportType(Ty.Struct(name), _symbols);
        Add(new Ins(Op.Constructor) { A = args.Count, B = typeRef });
        for (int i = 0; i < args.Count && i < fields.Count; i++)
            GenAs(args[i] ?? throw Fail("an argument is missing"), fields[i]);
    }

    private void Invoke(FunctionInfo info, List<Expr?> args, int line, bool isSuper, bool viaParent = false, bool viaVirtualParent = false)
    {
        int end = NewLabel();
        int at;
        if (info.Specifiers.IsEntry && !_specifiers.IsEntry && !_specifiers.IsEvent)
            _warn?.Invoke(line, $"Entry function '{info.Name}' should be called only from events or other entry functions."); // loc-ignore: wcc_lite's words
        if (info.Specifiers.IsEntry && !isSuper)
            at = Add(new Ins(Op.EntryFunc) { C = _tables.Name(info.Name) });
        else if (isSuper || viaParent || info.IsFinalCall)
        {
            string owner = info.Owner ?? throw Fail($"{info.Name} has no class");
            int function = isSuper || owner != _thisClass || info.Name != _functionName
                ? FuncRef.Import(_tables.ImportFunction(info.Name, owner, _symbols))
                : FuncRef.Current; // a function calling itself
            at = Add(new Ins(Op.FinalFunc) { B = line, C = function });
        }
        else if (viaVirtualParent && !isSuper)
        {
            // virtual_parent.F() asks the state machine's own F, whatever a state of it might have changed
            string name = _wrap.Kind == WrapKind.None && info.Name == _functionName ? "@self" : info.Name;
            at = Add(new Ins(Op.VirtualParentFunc) { B = line, C = _tables.Name(name) });
        }
        else
        {
            bool isWrap = _wrap.Kind != WrapKind.None;
            string name = !isWrap && info.Name == _functionName ? "@self" : info.Name;
            at = Add(new Ins(Op.VirtualFunc) { B = line, C = _tables.Name(name) });
        }
        JumpTo(at, Slot.First, end);
        GenArgs(info, args);
        Add(Op.ParamEnd);
        Place(end);
    }

    private void GenUnary(string op, Expr operand, int line)
    {
        switch (op)
        {
            case "!": InternalCall("LogicNot_Bool", line, () => GenAs(operand, Ty.Bool)); break;
            case "~": InternalCall("BitNot_Int32", line, () => GenAs(operand, Ty.Int)); break;
            default:
            {
                var ty = Numeric(TypeOf(operand));
                string tag = TypeTag(ty) ?? throw Fail($"{ty} can't be negated");
                InternalCall($"Neg_{tag}", line, () => GenAs(operand, ty));
                break;
            }
        }
    }

    private void GenBinary(string op, Expr left, Expr right, int line)
    {
        switch (PlanBinary(op, TypeOf(left), TypeOf(right)))
        {
            case LogicPlan lp:
                InternalCall(lp.Name, line, () =>
                {
                    GenAs(left, Ty.Bool);
                    int after = NewLabel();
                    int at = Add(Op.Skip);
                    JumpTo(at, Slot.First, after);
                    GenAs(right, Ty.Bool);
                    Place(after);
                });
                break;
            case HandleAndPlan:
                InternalCall("LogicAnd_handle_IScriptable_handle_IScriptable", line, () =>
                {
                    Gen(left);
                    Gen(right);
                });
                break;
            case InternalPlan ip:
                InternalCall(ip.Name, line, () =>
                {
                    GenAs(left, ip.Left);
                    GenAs(right, ip.Right);
                });
                break;
            case TestPlan tp:
                Add(new Ins(tp.Equal ? Op.TestEqual : Op.TestNotEqual) { A = _tables.ImportType(tp.Type, _symbols) });
                GenAs(left, tp.Type);
                GenAs(right, tp.Type);
                break;
        }
    }

    private void GenAssign(string op, Expr target, Expr value, int line)
    {
        var targetType = TypeOf(target);
        if (op == "=")
        {
            Add(Op.Assign);
            Gen(target);
            GenAs(value, targetType, strictly: true);
            return;
        }

        var valueType = Numeric(TypeOf(value));
        string prefix = OperatorPrefix(op);
        string targetTag = TypeTag(targetType) ?? throw Fail($"{op} can't be used on {targetType}");

        // the value's own type first; a whole number may also stand in for a float (a vector *= 2); then the target's
        var order = new List<Ty> { valueType };
        if (valueType.Kind is TyKind.Int or TyKind.Byte) order.Add(Ty.Float);
        order.AddRange([targetType, Ty.Float, Ty.Int]);
        foreach (var candidate in order)
        {
            if (TypeTag(candidate) is not { } tag) continue;
            string name = $"Assign{prefix}_{targetTag}_{tag}";
            if (Operators.IndexOf(name) < 0) continue;
            InternalCall(name, line, () =>
            {
                Gen(target);
                GenAs(value, candidate);
            });
            return;
        }
        throw Fail($"Unable to find suitable operator '{op}' for given types ({targetType.RttiName}, {valueType.RttiName})");
    }
}
