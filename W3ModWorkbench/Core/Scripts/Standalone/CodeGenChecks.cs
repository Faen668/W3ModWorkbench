namespace W3ModWorkbench.Core.Scripts.Standalone;

// loc-ignore-start: wcc_lite's own words and the language's own tokens, which are never translated

// What wcc_lite checks as it compiles a function, in its words: the other other half of FunctionCompiler. The messages
// stay in English because they're wcc_lite's own, and each rule was measured against it.
public sealed partial class FunctionCompiler
{
    private enum Lvalue { Writable, Const, NotVariable }

    /// <summary>Whether something can be assigned to, as wcc_lite sees it.</summary>
    private Lvalue PlaceOf(Expr e)
    {
        switch (e.Kind)
        {
            case Ident id when IsDefaultKeyword(id.Name):
                return Lvalue.Const;
            case Ident id when IsClassName(id.Name):
                return Lvalue.NotVariable;
            case Ident id:
                return ResolveIdent(id.Name) switch
                {
                    LocalVar or ParamVar => Lvalue.Writable,
                    FieldVar f => f.Field.IsConst ? Lvalue.Const : Lvalue.Writable,
                    GlobalObject => Lvalue.Const,
                    _ => Lvalue.NotVariable,
                };
            case Member { Object.Kind: Ident { Name: "default" } }:
                return Lvalue.Const;
            case Member m:
                if (m.Object.Kind is ThisExpr or SuperExpr or ParentExpr or VirtualParentExpr) return FieldOf(TypeOf(m.Object), m.Name).IsConst ? Lvalue.Const : Lvalue.Writable;
                if (PlaceOf(m.Object) is var through and not Lvalue.Writable) return through;
                return FieldOf(TypeOf(m.Object), m.Name).IsConst ? Lvalue.Const : Lvalue.Writable;
            case Index ix:
                return PlaceOf(ix.Array);
            case Assign:
                return Lvalue.Const;
            default:
                return Lvalue.NotVariable;
        }
    }

    private bool IsDefaultKeyword(string name) => name == "default";

    /// <summary>A class's name in code means the class, even with a local of that name, and it can't be assigned to.</summary>
    private bool IsClassName(string name) => _symbols.Classes.ContainsKey(name) && !_symbols.Enums.ContainsKey(name);

    private void CheckAssignTarget(Expr target)
    {
        CheckSuperScope(target);
        switch (PlaceOf(target))
        {
            case Lvalue.Const: throw Fail("Left hand side is const");
            case Lvalue.NotVariable: throw Fail("L-value required");
        }
    }

    /// <summary>What's written to by a compound assignment, which wcc_lite treats as an operator's out parameter.</summary>
    private void CheckOperatorTarget(Expr target, Ty type)
    {
        CheckSuperScope(target);
        if (PlaceOf(target) == Lvalue.Writable) return;
        _errors.Add(Fail($"Expected type '{MessageName(type)}' to be assignable"));
        throw Fail("With parameter '__param0'");
    }

    private void CheckSuperScope(Expr target)
    {
        if (target.Kind is Member { Object.Kind: SuperExpr }) throw Fail("Only functions can be accessed in 'super' scope");
    }

    /// <summary>A variable's value, as messages show it: with a &amp; when it can be written to.</summary>
    private string ValueName(Expr e, Ty type) => (IsReference(e) ? "&" : "") + (type.Kind == TyKind.Null ? "void" : MessageName(type));

    private bool IsReference(Expr e)
    {
        try
        {
            return PlaceOf(e) == Lvalue.Writable;
        }
        catch (CompileException)
        {
            return false;
        }
    }

    /// <summary>Reading a struct's member needs the struct to live somewhere: not a temporary a call handed back.</summary>
    private void CheckStructOwner(Expr obj, string member)
    {
        if (!ComesFromVariable(obj))
            throw Fail($"Cannot access struct property '{member}'. Parent scope does not come from variable.");
    }

    private void CheckArrayOwner(Expr array)
    {
        if (!ComesFromVariable(array)) throw Fail("Only arrays coming from variables can be indexed");
    }

    private bool ComesFromVariable(Expr e) => e.Kind switch
    {
        Ident => true,
        ThisExpr or SuperExpr or ParentExpr or VirtualParentExpr => true,
        Member m => ComesFromVariable(m.Object),
        Index ix => ComesFromVariable(ix.Array),
        _ => false,
    };

    /// <summary>A private member is for its own class, a protected one for that class and those below it.</summary>
    private bool CanSee(Visibility visibility, string? owner)
    {
        if (owner is null) return true;
        return visibility switch
        {
            Visibility.Private => _thisClass == owner,
            Visibility.Protected => _thisClass is not null && (_symbols.IsSubclass(_thisClass, owner)
                || _symbols.Classes.GetValueOrDefault(_thisClass)?.StateMachine is { } machine && _symbols.IsSubclass(machine, owner)),
            _ => true,
        };
    }

    private FieldInfo FieldAccessed(FieldInfo field)
    {
        CheckFieldAccess(field);
        return field;
    }

    private void CheckFieldAccess(FieldInfo field)
    {
        if (CanSee(field.Visibility, field.Owner)) return;
        string word = field.Visibility == Visibility.Private ? "private" : "protected";
        throw Fail($"Cannot access {word} property '{field.Name}' in class '{field.Owner}' here.");
    }

    private void CheckCall(FunctionInfo info)
    {
        if (!CanSee(info.Specifiers.Visibility, info.Owner))
        {
            string word = info.Specifiers.Visibility == Visibility.Private ? "private" : "protected";
            throw Fail($"Cannot call {word} function '{info.Name}' in class '{info.Owner}' here.");
        }
        if (info.Specifiers.IsExec) throw Fail($"Cannot call exec function '{info.Name}' from scripts instead of the console.");
        if (info.Specifiers.IsLatent && !_specifiers.IsLatent && !_specifiers.IsEntry)
            throw Fail($"Latent function '{info.Name}' can be called only from inside of state entry or latent function");
    }

    private void CheckCast(Expr operand, Ty from, Ty to)
    {
        if (from == to) return;
        if (IsImplicit(from, to) || IsExplicitOnly(from, to)) return;
        throw Fail($"Unable to convert from '{ValueName(operand, from)}' to '{MessageName(to)}'");
    }

    private bool IsState(string cls) => _symbols.Classes.GetValueOrDefault(cls)?.StateMachine is not null;

    private void CheckNew(New n)
    {
        string name = n.Type.Name;
        if (_symbols.Enums.ContainsKey(name) || !_symbols.Classes.TryGetValue(name, out var cls))
            throw Fail($"Class '{name}' is unknown");
        if (cls.IsStruct) throw Fail($"Class '{name}' is not a scriptable class. Only scriptable classes can be created in scripts.");
        if (cls.IsAbstract) throw Fail($"Cannot create instance of abstract class '{name}'");
        if (n.Owner is null && _thisClass is null) throw Fail("Objects created using 'new' should be created in the context of a parent object.");
    }

    /// <summary>Whether an expression calls a latent function anywhere inside it.</summary>
    private bool CallsLatent(Expr? e)
    {
        if (e is null) return false;
        switch (e.Kind)
        {
            case Call c:
                if (TryResolveCall(c.Callee) is { } info && info.Specifiers.IsLatent) return true;
                return CallsLatent(c.Callee) || c.Args.Any(CallsLatent);
            case Member m: return CallsLatent(m.Object);
            case Index ix: return CallsLatent(ix.Array) || CallsLatent(ix.Idx);
            case Unary u: return CallsLatent(u.Operand);
            case Binary b: return CallsLatent(b.Left) || CallsLatent(b.Right);
            case Assign a: return CallsLatent(a.Target) || CallsLatent(a.Value);
            case Ternary t: return CallsLatent(t.Cond) || CallsLatent(t.Then) || CallsLatent(t.Else);
            case Cast c: return CallsLatent(c.Operand);
            case New n: return CallsLatent(n.Owner);
            default: return false;
        }
    }

    private FunctionInfo? TryResolveCall(Expr callee)
    {
        try
        {
            return ResolveCall(callee) switch
            {
                MethodCallee m => m.Info,
                GlobalCallee g => g.Info,
                WrappedCallee w => w.Info,
                _ => null,
            };
        }
        catch (CompileException)
        {
            return null;
        }
    }

    private void CheckCondition(Expr condition)
    {
        if (CallsLatent(condition)) throw Fail("Conditional expression must not be latent");
        var type = TypeOf(condition);
        if (type.Kind is TyKind.Struct or TyKind.Array or TyKind.Map or TyKind.Void or TyKind.Primitive or TyKind.Enum or TyKind.Null)
        {
            _errors.Add(Fail("Conditional expression should be convertable to boolean type"));
            throw Fail($"Unable to convert from '{ValueName(condition, type)}' to 'Bool'");
        }
    }

    private void CheckSubscript(Expr index)
    {
        var type = TypeOf(index);
        if (type.Kind is TyKind.Int or TyKind.Byte or TyKind.Enum) return;
        _errors.Add(Fail("Array subscript should be an integer type"));
        throw Fail(IsExplicitOnly(type, Ty.Int)
            ? $"No implicit cast exists for converting '{ValueName(index, type)}' to 'Int32'"
            : $"Unable to convert from '{ValueName(index, type)}' to 'Int32'");
    }

    private static string OperatorName(string op) => op switch
    {
        "+" or "+=" => "OperatorAdd",
        "-" or "-=" => "OperatorSubtract",
        "*" or "*=" => "OperatorMultiply",
        "/" or "/=" => "OperatorDivide",
        "%" => "OperatorModulo",
        "&" or "&=" => "OperatorAnd",
        "|" or "|=" => "OperatorOr",
        "^" => "OperatorXor",
        "==" => "OperatorEqual",
        "!=" => "OperatorNotEqual",
        "<" => "OperatorLess",
        "<=" => "OperatorLessEqual",
        ">" => "OperatorGreater",
        ">=" => "OperatorGreaterEqual",
        "&&" => "OperatorLogicAnd",
        "||" => "OperatorLogicOr",
        _ => op,
    };

    private static bool IsNumber(Ty t) => t.Kind is TyKind.Int or TyKind.Float or TyKind.Byte or TyKind.Enum;

    /// <summary>The operators wcc_lite has for these types, as far as they can be told apart from its messages.</summary>
    private void CheckOperands(Binary b, Ty left, Ty right)
    {
        string op = b.Op;
        if (op is "&&" or "||")
        {
            CheckLogicOperand(op == "&&" ? "LogicAnd" : "LogicOr", b.Left, left, b.Right, right, first: true);
            CheckLogicOperand(op == "&&" ? "LogicAnd" : "LogicOr", b.Left, left, b.Right, right, first: false);
            return;
        }
        bool fine = op switch
        {
            "==" or "!=" => left.Kind == TyKind.Class && right.Kind == TyKind.Class
                || IsNumber(left) && IsNumber(right)
                || left == right && left.Kind is not (TyKind.Null or TyKind.Void),
            "<" or "<=" or ">" or ">=" => IsNumber(left) && IsNumber(right) || left == right && TypeTag(left) is { } tag && Operators.IndexOf($"{OperatorPrefix(op)}_{tag}_{tag}") >= 0,
            _ => true,
        };
        if (!fine)
            throw Fail($"Unable to find suitable operator '{OperatorName(op)}' for given types ({ValueName(b.Left, left)}, {ValueName(b.Right, right)})");
    }

    private void CheckLogicOperand(string name, Expr a, Ty aType, Expr? b, Ty bType, bool first)
    {
        var type = first ? aType : bType;
        if (type.Kind is not (TyKind.Name or TyKind.Null or TyKind.Enum or TyKind.Struct or TyKind.Array or TyKind.Map or TyKind.Void)) return;
        string other = b is null ? "void" : ValueName(b, bType);
        throw Fail($"Unable to find suitable operator 'Operator{name}' for given types ({ValueName(a, aType)}, {other})");
    }

    /// <summary>The two values of <c>a ? b : c</c>: the second has to be able to become the first.</summary>
    private void CheckTernary(Ternary t, Ty then, Ty otherwise)
    {
        bool thenEmpty = then.Kind is TyKind.Null or TyKind.Void, elseEmpty = otherwise.Kind is TyKind.Null or TyKind.Void;
        string Names() => $"Incompatible types '{ValueName(t.Then, then)}' and '{ValueName(t.Else, otherwise)}'";
        if (then.Kind == TyKind.Null && otherwise.Kind == TyKind.Class) throw Fail("Unable to convert from 'void' to 'void'");
        if (thenEmpty && elseEmpty || then.Kind == TyKind.Void || otherwise.Kind == TyKind.Void) throw Fail(Names());
        if (then.Kind == TyKind.Class && otherwise.Kind == TyKind.Class)
        {
            if (!_symbols.IsSubclass(then.Name, otherwise.Name) && !_symbols.IsSubclass(otherwise.Name, then.Name)) throw Fail(Names());
            return;
        }
        if (then == otherwise || elseEmpty) return;
        if (then.Kind == TyKind.Name && otherwise.Kind == TyKind.String) throw Fail($"Unable to convert from 'String' to '{ValueName(t.Then, then)}'");
        if (!IsImplicit(otherwise, then) && !IsExplicitOnly(otherwise, then) && !IsImplicit(then, otherwise)) throw Fail(Names());
    }

    /// <summary>
    /// wcc_lite's own reckoning of whether a function always returns: null if it does, or the line it says doesn't. It
    /// looks from the last statement back: a return settles it, ordinary statements are passed over, and the first
    /// statement with a body settles it by that body alone. So a loop counts as returning if its body does (a <c>for</c>
    /// only with a step, or with nothing in its brackets), and a switch by its first label.
    /// </summary>
    private int? NotReturning(IReadOnlyList<Stmt> statements, int line, bool warn = true)
    {
        int first = statements.FirstOrDefault(s => s.Kind is not VarStmt)?.Line ?? line;
        for (int i = statements.Count - 1; i >= 0; i--)
        {
            var s = statements[i];
            switch (s.Kind)
            {
                case ReturnStmt:
                    if (warn && statements.Skip(i + 1).Any(after => after.Kind is not EmptyStmt))
                        _warn?.Invoke(s.Line, "Return statement prevents the execution of successive lines of script");
                    return null;
                case BlockStmt b: return NotReturning(b.Statements, s.Line);
                case IfStmt { Else: { } otherwise } branch: return NotReturning([branch.Then], s.Line) ?? NotReturning([otherwise], s.Line);
                case IfStmt: return s.Line;
                case WhileStmt w: return NotReturning([w.Body], s.Line);
                case DoWhileStmt d: return NotReturning([d.Body], s.Line);
                case ForStmt f when f.Step is not null || f.Init is null && f.Cond is null: return NotReturning([f.Body], s.Line);
                case ForStmt: return s.Line;
                case SwitchStmt sw:
                {
                    int label = sw.Items.FindIndex(x => x is CaseItem or DefaultItem);
                    var held = sw.Items.Skip(label + 1).SkipWhile(x => x is not StmtItem).TakeWhile(x => x is StmtItem).OfType<StmtItem>().Select(x => x.Stmt).ToList();
                    return NotReturning(held, s.Line, warn: false);
                }
            }
        }
        return first;
    }
}
// loc-ignore-end
