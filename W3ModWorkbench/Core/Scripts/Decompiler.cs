using System.Globalization;
using System.Text;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>An expression rendered as WitcherScript, with its precedence and, for enum casts, what it wrapped.</summary>
internal sealed record Expr(string Text, int Prec)
{
    public static readonly Expr Empty = new("", Precedence.Primary);
    public bool IsEmpty => Text.Length == 0;
    public string? EnumType { get; init; }
    /// <summary>The declared type of a variable or field, when known.</summary>
    public string? Type { get; init; }
    public Expr? Inner { get; init; }
    public long? Literal { get; init; }
}

internal static class Precedence
{
    public const int Assign = 10, Or = 20, And = 30, BitOr = 43, BitXor = 44, BitAnd = 45, Equality = 50,
        Relational = 60, Additive = 70, Multiplicative = 80, Unary = 90, Primary = 100;
}

/// <summary>Turns a function's bytecode back into WitcherScript statements.</summary>
public sealed class Decompiler(ScriptBlob blob, EnumResolver enums)
{
    private ScriptFunction _func = null!;
    private List<Instruction> _code = [];
    private int _pos;
    private bool _nextIsMember;

    // ------------------------------------------------------------------ statements

    private enum Kind { Line, Nop, Expr, Return, If, Jump, Switch, Case, Default }

    private sealed class Stmt
    {
        public Kind Kind;
        public int Rt;
        public int Line;
        public Expr? E;
        public int Target;
        public int Target2;
    }

    private sealed record Ctx(int? Break, HashSet<int> Continue);

    private List<Stmt> _stmts = [];
    private Dictionary<int, int> _index = [];
    private Dictionary<int, List<int>> _backJumps = [];

    /// <summary>Returns the body lines (without braces), indented by <paramref name="depth"/> tabs.</summary>
    public List<string> DecompileBody(ScriptFunction func, int depth)
    {
        _func = func;
        _code = Bytecode.Decode(func.Code);
        _pos = 0;
        _stmts = BuildStatements();
        _index = [];
        for (int i = _stmts.Count - 1; i >= 0; i--)
            _index[_stmts[i].Rt] = i;
        _index[func.RuntimeSize] = _stmts.Count;
        _backJumps = [];
        for (int i = 0; i < _stmts.Count; i++)
            if (_stmts[i].Kind == Kind.Jump && _stmts[i].Target <= _stmts[i].Rt)
                (_backJumps.TryGetValue(_stmts[i].Target, out var l) ? l : _backJumps[_stmts[i].Target] = []).Add(i);

        var lines = new List<string>();
        Emit(lines, 0, _stmts.Count, depth, new Ctx(null, []));
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private List<Stmt> BuildStatements()
    {
        var list = new List<Stmt>();
        int line = 0;
        string? switchType = null;
        while (_pos < _code.Count)
        {
            var ins = _code[_pos];
            var st = new Stmt { Rt = ins.Rt, Line = line };
            switch (ins.Op)
            {
                case Op.Line:
                    line = ins.Arg(0);
                    st.Kind = Kind.Line;
                    st.Line = line;
                    _pos++;
                    break;
                case Op.Nop:
                case Op.Target:
                case Op.SaveValue:
                case Op.SavePointEnd:
                    st.Kind = Kind.Nop;
                    _pos++;
                    break;
                case Op.SavePoint:
                    // save point data for latent functions: saved values up to SavePointEnd, nothing to show
                    _pos++;
                    st.Kind = Kind.Nop;
                    while (_pos < _code.Count && _code[_pos].Op != Op.SavePointEnd)
                    {
                        if (_code[_pos].Op == Op.SaveValue) { _pos++; ParseExpr(); }
                        else _pos++;
                    }
                    if (_pos < _code.Count) _pos++;
                    break;
                case Op.JumpIfFalse:
                    _pos++;
                    st.Kind = Kind.If;
                    st.Target = ins.JumpTarget;
                    st.E = ParseExpr();
                    break;
                case Op.Jump:
                    _pos++;
                    st.Kind = Kind.Jump;
                    st.Target = ins.JumpTarget;
                    break;
                case Op.Return:
                    _pos++;
                    st.Kind = Kind.Return;
                    st.E = AsEnum(_func.Return.Count > 0 ? _func.Return[0].Type : null, ParseExpr());
                    break;
                case Op.Switch:
                    _pos++;
                    st.Kind = Kind.Switch;
                    st.Target = ins.RtEnd + ins.Arg(1);
                    st.E = ParseExpr();
                    switchType = st.E.Type ?? blob.Types.ElementAtOrDefault(ins.Arg(0)).Name;
                    break;
                case Op.SwitchLabel:
                    _pos++;
                    st.Kind = Kind.Case;
                    st.Target = ins.Rt + 3 + ins.Arg(0);
                    st.Target2 = ins.RtEnd + ins.Arg(1);
                    st.E = AsEnum(switchType, ParseExpr());
                    break;
                case Op.SwitchDefault:
                    _pos++;
                    st.Kind = Kind.Default;
                    break;
                default:
                    st.Kind = Kind.Expr;
                    st.E = ParseExpr();
                    break;
            }
            list.Add(st);
        }
        return list;
    }

    private bool Meaningful(int i) => _stmts[i].Kind is not (Kind.Line or Kind.Nop);

    private int Prev(int j, int lower)
    {
        int k = j - 1;
        while (k >= lower && !Meaningful(k)) k--;
        return k;
    }

    private int Next(int i, int upper)
    {
        while (i < upper && !Meaningful(i)) i++;
        return i;
    }

    private int IndexAt(int rt) => _index.TryGetValue(rt, out int i) ? i : -1;

    /// <summary>The in-memory offset of the Line marker(s) directly in front of statement <paramref name="i"/>.</summary>
    private int GroupStart(int i)
    {
        while (i > 0 && _stmts[i - 1].Kind == Kind.Line) i--;
        return _stmts[i].Rt;
    }

    /// <summary>A JumpIfFalse at <paramref name="i"/> heads a loop when its body ends with a jump back to it.</summary>
    private bool IsLoop(int i, int end, out int exit, out int backJump)
    {
        exit = IndexAt(_stmts[i].Target);
        backJump = -1;
        if (exit < 0 || exit > end) return false;
        int k = Prev(exit, i + 1);
        if (k <= i || _stmts[k].Kind != Kind.Jump) return false;
        int to = _stmts[k].Target;
        if (to != _stmts[i].Rt && to != GroupStart(i)) return false;
        backJump = k;
        return true;
    }

    /// <summary>
    /// do { body } while (cond) compiles to: body, JumpIfFalse(cond) past the loop, Jump back to the body.
    /// </summary>
    private bool IsDoWhile(int i, int end, out int cond, out int back)
    {
        cond = back = -1;
        if (!_backJumps.TryGetValue(_stmts[i].Rt, out var jumps)) return false;
        int first = Next(i, end);
        if (first < end && _stmts[first].Kind == Kind.If && IsLoop(first, end, out _, out _)) return false; // a while loop
        foreach (int k in jumps)
        {
            if (k <= i || k >= end) continue;
            int j = Prev(k, i + 1);
            if (j > first && _stmts[j].Kind == Kind.If && IndexAt(_stmts[j].Target) == k + 1)
            {
                cond = j;
                back = k;
                return true;
            }
        }
        return false;
    }

    /// <returns>The number of top-level statements written.</returns>
    private int Emit(List<string> lines, int start, int end, int depth, Ctx ctx)
    {
        string pad = new('\t', depth);
        int count = 0;
        int lastLine = -1;
        int i = start;
        while (i < end)
        {
            var st = _stmts[i];
            if (IsDoWhile(i, end, out int doCond, out int doBack))
            {
                lines.Add(pad + "do");
                lines.Add(pad + "{");
                var loop = new Ctx(_stmts[doCond].Target, [_stmts[doCond].Rt, GroupStart(doCond)]);
                Emit(lines, i, doCond, depth + 1, loop);
                lines.Add($"{pad}}} while ({_stmts[doCond].E!.Text});"); // loc-ignore
                count++;
                i = doBack + 1;
                continue;
            }
            if (Meaningful(i) && lastLine > 0 && st.Line > lastLine + 1 && count > 0)
                lines.Add("");
            if (Meaningful(i) && st.Line > 0)
                lastLine = st.Line;

            switch (st.Kind)
            {
                case Kind.Line:
                case Kind.Nop:
                    i++;
                    continue;

                case Kind.Expr:
                {
                    // for (init; cond; step): init, condition and step all carry the same source line
                    int n = Next(i + 1, end);
                    if (n < end && _stmts[n].Kind == Kind.If && _stmts[n].Line == st.Line
                        && IsLoop(n, end, out int exit, out int back))
                    {
                        int step = Prev(back, n + 1);
                        if (step > n && _stmts[step].Kind == Kind.Expr && _stmts[step].Line == st.Line)
                        {
                            lines.Add($"{pad}for ({st.E!.Text}; {_stmts[n].E!.Text}; {_stmts[step].E!.Text})"); // loc-ignore
                            lines.Add(pad + "{");
                            // continue jumps to the step, or (older compilers) to the loop's closing jump
                            var cont = new HashSet<int> { _stmts[step].Rt, GroupStart(step), _stmts[back].Rt, GroupStart(back) };
                            Emit(lines, n + 1, step, depth + 1, new Ctx(_stmts[n].Target, cont));
                            lines.Add(pad + "}");
                            count++;
                            i = exit;
                            continue;
                        }
                    }
                    lines.Add($"{pad}{st.E!.Text};");
                    count++;
                    i++;
                    continue;
                }

                case Kind.Return:
                    lines.Add(st.E!.IsEmpty ? $"{pad}return;" : $"{pad}return {st.E.Text};"); // loc-ignore
                    count++;
                    i++;
                    continue;

                case Kind.Jump:
                    if (ctx.Break == st.Target)
                        lines.Add($"{pad}break;");
                    else if (ctx.Continue.Contains(st.Target))
                        lines.Add($"{pad}continue;");
                    else
                        lines.Add($"{pad}// (jump to offset {st.Target} could not be structured)"); // loc-ignore
                    count++;
                    i++;
                    continue;

                case Kind.If:
                {
                    if (IsLoop(i, end, out int exit, out int back))
                    {
                        lines.Add($"{pad}while ({st.E!.Text})"); // loc-ignore
                        lines.Add(pad + "{");
                        Emit(lines, i + 1, back, depth + 1, new Ctx(st.Target, [st.Rt, GroupStart(i), _stmts[back].Rt, GroupStart(back)]));
                        lines.Add(pad + "}");
                        count++;
                        i = exit;
                        continue;
                    }

                    int j = IndexAt(st.Target);
                    if (j < 0 || j > end)
                    {
                        lines.Add($"{pad}if ({st.E!.Text})  // (target offset {st.Target} could not be structured)"); // loc-ignore
                        count++;
                        i++;
                        continue;
                    }

                    int k = Prev(j, i + 1);
                    if (k > i && _stmts[k].Kind == Kind.Jump && _stmts[k].Target > st.Target
                        && ctx.Break != _stmts[k].Target && !ctx.Continue.Contains(_stmts[k].Target)
                        && IndexAt(_stmts[k].Target) is int elseEnd && elseEnd >= j && elseEnd <= end)
                    {
                        lines.Add($"{pad}if ({st.E!.Text})"); // loc-ignore
                        lines.Add(pad + "{");
                        Emit(lines, i + 1, k, depth + 1, ctx);
                        lines.Add(pad + "}");

                        var elseLines = new List<string>();
                        int inner = Emit(elseLines, j, elseEnd, depth + 1, ctx);
                        string innerPad = pad + "\t";
                        if (inner == 1 && elseLines.Count > 0 && elseLines[0].StartsWith(innerPad + "if (", StringComparison.Ordinal))
                        {
                            // else if: lift the nested if chain up one level
                            lines.Add($"{pad}else {elseLines[0][innerPad.Length..]}"); // loc-ignore
                            foreach (var l in elseLines.Skip(1))
                                lines.Add(l.StartsWith('\t') ? l[1..] : l);
                        }
                        else
                        {
                            lines.Add(pad + "else");
                            lines.Add(pad + "{");
                            lines.AddRange(elseLines);
                            lines.Add(pad + "}");
                        }
                        count++;
                        i = elseEnd;
                        continue;
                    }

                    lines.Add($"{pad}if ({st.E!.Text})"); // loc-ignore
                    lines.Add(pad + "{");
                    Emit(lines, i + 1, j, depth + 1, ctx);
                    lines.Add(pad + "}");
                    count++;
                    i = j;
                    continue;
                }

                case Kind.Switch:
                {
                    var labels = new List<int>();
                    int idx = IndexAt(st.Target);
                    int switchEnd = -1;
                    while (idx >= 0 && idx < end && _stmts[idx].Kind is Kind.Case or Kind.Default && !labels.Contains(idx))
                    {
                        labels.Add(idx);
                        if (_stmts[idx].Kind == Kind.Default) break;
                        int next = IndexAt(_stmts[idx].Target);
                        if (next < 0 || next >= end || _stmts[next].Kind is not (Kind.Case or Kind.Default))
                        {
                            switchEnd = next;
                            break;
                        }
                        idx = next;
                    }
                    if (labels.Count == 0)
                    {
                        lines.Add($"{pad}switch ({st.E!.Text})  // (cases could not be read)"); // loc-ignore
                        count++;
                        i++;
                        continue;
                    }
                    // The label chain is the order cases are tested in; bodies follow the order labels
                    // sit in memory. Grouped labels (case A: case B:) are stored back to front.
                    var physical = labels.OrderBy(x => x).ToList();
                    if (switchEnd < 0)
                    {
                        // ends with default: the cases' break jumps all land on the end
                        int defaultAt = physical[^1], found = -1;
                        for (int q = physical[0]; q < defaultAt && found < 0; q++)
                            if (_stmts[q].Kind == Kind.Jump && _stmts[q].Target > _stmts[defaultAt].Rt)
                                found = IndexAt(_stmts[q].Target);
                        // every case returned: default runs to the end of the enclosing block
                        switchEnd = found > defaultAt && found <= end ? found : end;
                    }

                    int breakRt = switchEnd < _stmts.Count ? _stmts[switchEnd].Rt : _func.RuntimeSize;
                    var inner = ctx with { Break = breakRt };
                    lines.Add($"{pad}switch ({st.E!.Text})"); // loc-ignore
                    lines.Add(pad + "{");
                    for (int l = 0; l < physical.Count;)
                    {
                        // a run of labels with nothing between them shares one body
                        int run = l;
                        while (run + 1 < physical.Count && Next(physical[run] + 1, physical[run + 1]) == physical[run + 1])
                            run++;
                        foreach (int li in physical.Skip(l).Take(run - l + 1).OrderBy(labels.IndexOf))
                            lines.Add(_stmts[li].Kind == Kind.Default ? $"{pad}\tdefault:" : $"{pad}\tcase {_stmts[li].E!.Text}:"); // loc-ignore
                        int bodyEnd = run + 1 < physical.Count ? physical[run + 1] : switchEnd;
                        Emit(lines, physical[run] + 1, bodyEnd, depth + 2, inner);
                        l = run + 1;
                    }
                    lines.Add(pad + "}");
                    count++;
                    i = Math.Max(switchEnd, i + 1);
                    continue;
                }

                default:
                    lines.Add($"{pad}// (unexpected {st.Kind})"); // loc-ignore
                    i++;
                    continue;
            }
        }
        return count;
    }

    // ------------------------------------------------------------------ expressions

    private string Name(int i) => i >= 0 && i < blob.Names.Length ? blob.Names[i] : $"name{i}";

    private string TypeName(int i) =>
        i >= 0 && i < blob.Types.Count ? ScriptWriter.FormatType(blob.Types[i].Name) : $"type{i}";

    private static string Wrap(Expr e, int prec) => e.Prec < prec ? $"({e.Text})" : e.Text;

    private Expr ParseExpr()
    {
        while (_pos < _code.Count && _code[_pos].Op is Op.Line or Op.Target)
            _pos++;
        if (_pos >= _code.Count)
            throw new InvalidOperationException("The code ended in the middle of an expression."); // loc-ignore

        bool member = _nextIsMember;
        _nextIsMember = false;
        var ins = _code[_pos++];

        switch (ins.Op)
        {
            case Op.Nop: return Expr.Empty;
            case Op.Null: return new("NULL", Precedence.Primary);
            case Op.IntOne: return Int(1);
            case Op.IntZero: return Int(0);
            case Op.IntConst: case Op.ShortConst: case Op.ByteConst: return Int(ins.Arg(0));
            case Op.FloatConst: return new(FormatFloat(ins.Float), ins.Float < 0 ? Precedence.Unary : Precedence.Primary);
            case Op.StringConst: return new(Quote(ins.Text ?? ""), Precedence.Primary);
            case Op.NameConst: return new($"'{Name(ins.Arg(0))}'", Precedence.Primary);
            case Op.BoolTrue: return new("true", Precedence.Primary);
            case Op.BoolFalse: return new("false", Precedence.Primary);
            case Op.LocalVar:
                return Var(ins.Arg(0), _func.Locals);
            case Op.ParamVar:
                return Var(ins.Arg(0), _func.Params);
            case Op.ObjectVar: case Op.BindableVar: case Op.DefaultVar:
                return new(Name(ins.Arg(0)), Precedence.Primary) { Type = MemberType(ins.Arg(1), Name(ins.Arg(0))) };
            case Op.This: return new("this", Precedence.Primary);
            case Op.TheGame: return new("theGame", Precedence.Primary);
            case Op.ThePlayer: return new("thePlayer", Precedence.Primary);
            case Op.TheCamera: return new("theCamera", Precedence.Primary);
            case Op.TheSound: return new("theSound", Precedence.Primary);
            case Op.TheDebug: return new("theDebug", Precedence.Primary);
            case Op.TheTimer: return new("theTimer", Precedence.Primary);
            case Op.TheInput: return new("theInput", Precedence.Primary);
            case Op.TheTelemetry: return new("theTelemetry", Precedence.Primary);
            case Op.TheHud: return new("theHud", Precedence.Primary);

            case Op.Parent:
            {
                var of = ParseExpr();
                return new(of.Text == "this" ? "parent" : $"{Wrap(of, Precedence.Primary)}.parent", Precedence.Primary);
            }

            case Op.Assign:
            {
                var l = ParseExpr();
                var r = AsEnum(l.Type, ParseExpr());
                return new($"{l.Text} = {r.Text}", Precedence.Assign);
            }

            case Op.Context:
            {
                var obj = ParseExpr();
                _nextIsMember = true;
                var m = ParseExpr();
                return new($"{Wrap(obj, Precedence.Primary)}.{m.Text}", Precedence.Primary) { Type = m.Type };
            }

            case Op.StructMember:
            {
                var of = ParseExpr();
                return new($"{Wrap(of, Precedence.Primary)}.{Name(ins.Arg(0))}", Precedence.Primary)
                {
                    Type = MemberType(ins.Arg(1), Name(ins.Arg(0))),
                };
            }

            case Op.FinalFunc:
            {
                int f = ins.Arg(2);
                var args = ParseArgs();
                if (f == -1 && _func.Kind == FunctionKind.Wrap)
                    return Call("wrappedMethod", args); // a wrapped global function calls the original this way
                if (f <= 0)
                    return Operator(f, args);
                var (name, owner) = f - 1 < blob.FunctionRefs.Count ? blob.FunctionRefs[f - 1] : ($"func{f}", -1);
                string prefix = "";
                if (!member && owner >= 0 && _func.Class is { } cls && name == _func.Name
                    && blob.Types[owner].Name != cls.Name)
                    prefix = "super.";
                return Call(prefix + name, args);
            }

            case Op.VirtualFunc:
            {
                string name = Name(ins.Arg(2));
                var args = ParseArgs();
                return Call(name == "@self" ? "wrappedMethod" : name, args);
            }

            case Op.EntryFunc:
                return Call(Name(ins.Arg(1)), ParseArgs());

            case Op.VirtualParentFunc:
                return Call("super." + Name(ins.Arg(2)), ParseArgs());

            case Op.Conditional:
            {
                // the engine's notes call this one unfinished; WitcherScript has no ?: in source
                var cond = ParseExpr();
                var whenTrue = ParseExpr();
                var whenFalse = ParseExpr();
                return new($"({cond.Text} ? {whenTrue.Text} : {whenFalse.Text})", Precedence.Primary);
            }

            case Op.TestEqual:
            case Op.TestNotEqual:
            {
                var a = ParseExpr();
                var b = ParseExpr();
                return Binary(a, ins.Op == Op.TestEqual ? "==" : "!=", Precedence.Equality, b);
            }

            case Op.New:
            {
                var owner = ParseExpr();
                string t = TypeName(ins.Arg(0));
                return new(owner.IsEmpty ? $"new {t}" : $"new {t} in {Wrap(owner, Precedence.Unary)}", Precedence.Unary); // loc-ignore
            }

            case Op.Delete:
                return new($"delete {Wrap(ParseExpr(), Precedence.Unary)}", Precedence.Unary); // loc-ignore

            case Op.Constructor:
            {
                var values = new List<string>();
                for (int n = 0; n < ins.Arg(0); n++)
                {
                    if (_pos < _code.Count && _code[_pos].Op == Op.StructMember)
                        _pos++;
                    values.Add(ParseExpr().Text);
                }
                return new($"{TypeName(ins.Arg(1))}({string.Join(", ", values)})", Precedence.Primary);
            }

            case >= Op.ArrayClear and <= Op.ArrayElement:
                return ArrayOp(ins.Op);
            case Op.MapClear or Op.MapSize or Op.MapContains or Op.MapErase or Op.MapElement or Op.MapKey:
                return MapOperation(ins.Op);

            case >= Op.StaticArraySize and <= Op.StaticArrayElement:
                return StaticArrayOp(ins.Op);

            case Op.IntToEnum:
            {
                var e = ParseExpr();
                if (e.Literal is long v && enums.Find(TypeName(ins.Arg(0)), v) is { } constant)
                    return new(constant, Precedence.Primary);
                return e with { EnumType = TypeName(ins.Arg(0)) };
            }

            case Op.EnumToInt:
            {
                var e = ParseExpr();
                string type = TypeName(ins.Arg(0));
                string text = e.Literal is long v && enums.Find(type, v) is { } constant
                    ? constant
                    : e.Literal is long raw ? $"({type}){raw}" : $"(int){Wrap(e, Precedence.Unary)}";
                return new(text, Precedence.Unary) { EnumType = type, Inner = e };
            }

            case Op.DynamicCast:
                return new($"({TypeName(ins.Arg(0))}){Wrap(ParseExpr(), Precedence.Unary)}", Precedence.Unary);

            case Op.FloatToInt: case Op.StringToInt: case Op.BoolToInt:
                return new($"(int){Wrap(ParseExpr(), Precedence.Unary)}", Precedence.Unary);
            case Op.StringToFloat: case Op.BoolToFloat:
                return new($"(float){Wrap(ParseExpr(), Precedence.Unary)}", Precedence.Unary);
            case Op.IntToByte: case Op.FloatToByte: case Op.StringToByte: case Op.BoolToByte:
                return new($"(byte){Wrap(ParseExpr(), Precedence.Unary)}", Precedence.Unary);

            // conversions the compiler inserts on its own
            case Op.ByteToBool: case Op.ByteToInt: case Op.ByteToFloat: case Op.ByteToString: case Op.BoolToString:
            case Op.IntToBool: case Op.IntToFloat: case Op.IntToString: case Op.FloatToBool: case Op.FloatToString:
            case Op.NameToBool: case Op.NameToString: case Op.StringToBool: case Op.ObjectToBool: case Op.ObjectToString:
            case Op.EnumToString:
                return ParseExpr();

            default:
                throw new NotSupportedException($"{ins.Op} cannot appear inside an expression."); // loc-ignore
        }
    }

    private Expr Var(int nameIndex, List<ScriptProperty> scope)
    {
        string name = Name(nameIndex);
        return new(name, Precedence.Primary) { Type = scope.FirstOrDefault(p => p.Name == name)?.Type };
    }

    /// <summary>The declared type of field <paramref name="name"/> on the type at <paramref name="typeIndex"/>, if the blob defines it.</summary>
    private string? MemberType(int typeIndex, string name)
    {
        if (typeIndex < 0 || typeIndex >= blob.Types.Count) return null;
        string owner = blob.Types[typeIndex].Name;
        return blob.Classes.FirstOrDefault(c => c.Name == owner)?.Properties.FirstOrDefault(p => p.Name == name)?.Type
            ?? blob.Structs.FirstOrDefault(c => c.Name == owner)?.Properties.FirstOrDefault(p => p.Name == name)?.Type
            ?? blob.AddedFields.FirstOrDefault(f => f.Owner == owner && f.Name == name)?.Type;
    }

    /// <summary>An integer literal stored into an enum-typed place reads better as the enum's constant.</summary>
    private Expr AsEnum(string? type, Expr e) =>
        type is not null && e.Literal is long v && enums.Find(type, v) is { } constant
            ? new Expr(constant, Precedence.Primary)
            : e;

    private static Expr Int(long v) => new(v.ToString(CultureInfo.InvariantCulture), v < 0 ? Precedence.Unary : Precedence.Primary) { Literal = v };

    private List<Expr> ParseArgs()
    {
        var args = new List<Expr>();
        while (_pos < _code.Count)
        {
            var op = _code[_pos].Op;
            if (op == Op.ParamEnd) { _pos++; break; }
            if (op == Op.Skip) { _pos++; continue; }
            args.Add(ParseExpr());
        }
        return args;
    }

    private static Expr Call(string name, List<Expr> args)
    {
        int used = args.Count;
        while (used > 0 && args[used - 1].IsEmpty) used--;
        return new($"{name}({string.Join(", ", args.Take(used).Select(a => a.Text))})", Precedence.Primary);
    }

    private static Expr Operator(int id, List<Expr> args)
    {
        if (!Operators.TryGet(id, out string symbol, out int prec) || args.Count is 0 or > 2)
            return new($"{Operators.NameOf(id)}({string.Join(", ", args.Select(a => a.Text))})", Precedence.Primary);
        if (args.Count == 1)
            return new($"{symbol}{Wrap(args[0], Precedence.Unary)}", Precedence.Unary);
        return Binary(args[0], symbol, prec, args[1]);
    }

    private static Expr Binary(Expr a, string symbol, int prec, Expr b)
    {
        // enum == enum compiles to (int)a == (int)b; show it the way it was written
        if (a.EnumType is not null && a.EnumType == b.EnumType && a.Inner is not null && b.Inner is not null)
        {
            // a constant side already reads as its enum name; a variable side drops the cast
            a = a.Inner.Literal is null ? a.Inner : a;
            b = b.Inner.Literal is null ? b.Inner : b;
        }

        if (prec == Precedence.Assign)
            return new($"{a.Text} {symbol} {b.Text}", prec);
        string left = Wrap(a, prec);
        string right = b.Prec <= prec ? $"({b.Text})" : b.Text;
        // && and || chains are associative; keep them flat
        if ((symbol is "&&" or "||") && b.Prec == prec && b.Text.Contains(symbol, StringComparison.Ordinal))
            right = b.Text;
        return new($"{left} {symbol} {right}", prec);
    }

    private Expr MapOperation(Op op)
    {
        string m = Wrap(ParseExpr(), Precedence.Primary);
        return op switch
        {
            Op.MapClear => new($"{m}.Clear()", Precedence.Primary),
            Op.MapSize => new($"{m}.Size()", Precedence.Primary),
            Op.MapContains => new($"{m}.Contains({ParseExpr().Text})", Precedence.Primary),
            Op.MapErase => new($"{m}.Erase({ParseExpr().Text})", Precedence.Primary),
            Op.MapKey => new($"{m}.Key({ParseExpr().Text})", Precedence.Primary),
            _ => new($"{m}[{ParseExpr().Text}]", Precedence.Primary), // MapElement
        };
    }

    private Expr ArrayOp(Op op)
    {
        var arr = ParseExpr();
        string a = Wrap(arr, Precedence.Primary);
        Expr One() => ParseExpr();
        return op switch
        {
            Op.ArrayClear => new($"{a}.Clear()", Precedence.Primary),
            Op.ArraySize => new($"{a}.Size()", Precedence.Primary),
            Op.ArrayPopBack => new($"{a}.PopBack()", Precedence.Primary),
            Op.ArrayLast => new($"{a}.Last()", Precedence.Primary),
            Op.ArrayResize => new($"{a}.Resize({One().Text})", Precedence.Primary),
            Op.ArrayGrow => new($"{a}.Grow({One().Text})", Precedence.Primary),
            Op.ArrayFindFirst or Op.ArrayFindFirstFast => new($"{a}.FindFirst({One().Text})", Precedence.Primary),
            Op.ArrayFindLast or Op.ArrayFindLastFast => new($"{a}.FindLast({One().Text})", Precedence.Primary),
            Op.ArrayContains or Op.ArrayContainsFast => new($"{a}.Contains({One().Text})", Precedence.Primary),
            Op.ArrayPushBack => new($"{a}.PushBack({One().Text})", Precedence.Primary),
            Op.ArrayRemove or Op.ArrayRemoveFast => new($"{a}.Remove({One().Text})", Precedence.Primary),
            Op.ArrayErase or Op.ArrayEraseFast => new($"{a}.Erase({One().Text})", Precedence.Primary),
            Op.ArrayInsert => Insert(a),
            _ => new($"{a}[{One().Text}]", Precedence.Primary), // ArrayElement
        };

        Expr Insert(string target)
        {
            var at = ParseExpr();
            var value = ParseExpr();
            return new($"{target}.Insert({at.Text}, {value.Text})", Precedence.Primary);
        }
    }

    /// <summary>Fixed-size arrays ("var a : T[4]") use their own opcodes, with no type operand.</summary>
    private Expr StaticArrayOp(Op op)
    {
        string a = Wrap(ParseExpr(), Precedence.Primary);
        return op switch
        {
            Op.StaticArraySize => new($"{a}.Size()", Precedence.Primary),
            Op.StaticArrayLast => new($"{a}.Last()", Precedence.Primary),
            Op.StaticArrayFindFirst or Op.StaticArrayFindFirstFast => new($"{a}.FindFirst({ParseExpr().Text})", Precedence.Primary),
            Op.StaticArrayFindLast or Op.StaticArrayFindLastFast => new($"{a}.FindLast({ParseExpr().Text})", Precedence.Primary),
            Op.StaticArrayContains or Op.StaticArrayContainsFast => new($"{a}.Contains({ParseExpr().Text})", Precedence.Primary),
            _ => new($"{a}[{ParseExpr().Text}]", Precedence.Primary), // StaticArrayElement
        };
    }

    public static string FormatFloat(float f)
    {
        string s = f.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains("Infinity") && !s.Contains("NaN"))
            s += ".0";
        return s;
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }
}
