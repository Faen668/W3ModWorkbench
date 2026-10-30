using System.IO;
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
    /// <summary>The type a conversion the compiler inserted on its own turned this into; shown only where it changes the result.</summary>
    public string? Implicit { get; init; }
}

internal static class Precedence
{
    public const int Assign = 10, Or = 20, And = 30, BitOr = 43, BitXor = 44, BitAnd = 45, Equality = 50,
        Relational = 60, Additive = 70, Multiplicative = 80, Unary = 90, Primary = 100;
}

/// <summary>Turns a function's bytecode back into WitcherScript statements.</summary>
public sealed class Decompiler(ScriptBlob blob, EnumResolver enums, GameDeclarations? game = null)
{
    private readonly GameDeclarations _game = game ?? GameDeclarations.Empty;
    private ScriptFunction _func = null!;
    private List<Instruction> _code = [];
    private int _pos;
    private bool _nextIsMember;
    /// <summary>The type of the object a member call is made on, when known.</summary>
    private string? _nextReceiver;

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
                    // The label chain is the order cases are tested in; labels and bodies sit in memory in
                    // source order. Grouped labels (case A: case B:) are chained back to front, so write them as stored.
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
                        foreach (int li in physical.Skip(l).Take(run - l + 1))
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
        string? receiver = member ? _nextReceiver : CurrentClass;
        _nextIsMember = false;
        _nextReceiver = null;
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
            case Op.NameConst: return new(Quote(Name(ins.Arg(0)), '\''), Precedence.Primary);
            case Op.BoolTrue: return new("true", Precedence.Primary);
            case Op.BoolFalse: return new("false", Precedence.Primary);
            case Op.LocalVar:
                return Var(ins.Arg(0), _func.Locals);
            case Op.ParamVar:
                return Var(ins.Arg(0), _func.Params);
            case Op.ObjectVar: case Op.BindableVar: case Op.DefaultVar:
                return new(Name(ins.Arg(0)), Precedence.Primary) { Type = MemberType(ins.Arg(1), Name(ins.Arg(0))) };
            case Op.This: return new("this", Precedence.Primary) { Type = CurrentClass };
            case Op.TheGame: return new("theGame", Precedence.Primary) { Type = "CR4Game" };
            case Op.ThePlayer: return new("thePlayer", Precedence.Primary) { Type = "CR4Player" };
            case Op.TheCamera: return new("theCamera", Precedence.Primary) { Type = "CCameraDirector" };
            case Op.TheSound: return new("theSound", Precedence.Primary) { Type = "CScriptSoundSystem" };
            case Op.TheDebug: return new("theDebug", Precedence.Primary) { Type = "CDebugAttributesManager" };
            case Op.TheTimer: return new("theTimer", Precedence.Primary) { Type = "CTimerScriptKeyword" };
            case Op.TheInput: return new("theInput", Precedence.Primary) { Type = "CInputManager" };
            case Op.TheTelemetry: return new("theTelemetry", Precedence.Primary) { Type = "CR4TelemetryScriptProxy" };
            case Op.TheHud: return new("theHud", Precedence.Primary) { Type = "CR4Hud" };

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
                _nextReceiver = obj.Type is { } objType ? GameDeclarations.ClassName(objType) : null;
                var m = ParseExpr();
                if (m.Text.StartsWith("virtual_parent.", StringComparison.Ordinal))
                {
                    if (obj.Text == "parent") return m;
                    throw new NotSupportedException("Parent dispatch through a separate receiver cannot be preserved in source."); // loc-ignore
                }
                if (m.Text.StartsWith("super.", StringComparison.Ordinal))
                {
                    if (obj.Text == "this") return m;
                    throw new NotSupportedException("Parent dispatch through a separate receiver cannot be preserved in source."); // loc-ignore
                }
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
                if (f == -1)
                    return Call(_func.Kind == FunctionKind.Wrap && !member ? "wrappedMethod" : _func.Name, args, FromBlob(_func));
                if (f <= 0)
                    return Operator(f, args);
                var (name, owner) = f - 1 < blob.FunctionRefs.Count ? blob.FunctionRefs[f - 1] : ($"func{f}", -1);
                var signature = owner >= 0 && owner < blob.Types.Count ? MethodSignature(blob.Types[owner].Name, name) : GlobalSignature(name);
                string prefix = "";
                string currentOwner = _func.Class?.Name ?? _func.Owner;
                if (!member && owner >= 0 && currentOwner.Length > 0 && (name == _func.Name || NeedsSuper(currentOwner, TypeName(owner), name))
                    && TypeName(owner) != currentOwner)
                    prefix = "super.";
                return Call(prefix + name, args, signature);
            }

            case Op.VirtualFunc:
            {
                string name = Name(ins.Arg(2));
                var args = ParseArgs();
                // @self: a method of the same name as this one, on this object or (a member call) another's
                if (name == "@self" && !member)
                    return Call(_func.Kind == FunctionKind.Wrap ? "wrappedMethod" : _func.Name, args, FromBlob(_func));
                if (name == "@self") name = _func.Name;
                return Call(name, args, (receiver is null ? null : MethodSignature(receiver, name)) ?? AnySignature(name));
            }

            case Op.EntryFunc:
            {
                string name = Name(ins.Arg(1));
                return Call(name == "@self" ? _func.Kind == FunctionKind.Wrap && !member ? "wrappedMethod" : _func.Name : name, ParseArgs());
            }

            case Op.VirtualParentFunc:
                return Call("virtual_parent." + Name(ins.Arg(2)), ParseArgs());

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
                return new(owner.IsEmpty ? $"new {t}" : $"new {t} in {Wrap(owner, Precedence.Unary)}", Precedence.Unary) { Type = t }; // loc-ignore
            }

            case Op.Delete:
                return new($"delete {Wrap(ParseExpr(), Precedence.Unary)}", Precedence.Unary); // loc-ignore

            case Op.Constructor:
            {
                var values = new List<string>();
                for (int n = 0; n < ins.Arg(0); n++)
                {
                    values.Add(ParseExpr().Text);
                }
                return new($"{TypeName(ins.Arg(1))}({string.Join(", ", values)})", Precedence.Primary);
            }

            case >= Op.ArrayClear and <= Op.ArrayElement:
                return ArrayOp(ins.Op);
            case Op.MapClear or Op.MapSize or Op.MapContains or Op.MapErase or Op.MapElement or Op.MapTryGet or Op.MapKey:
                return MapOperation(ins.Op);

            case >= Op.StaticArraySize and <= Op.StaticArrayElement:
                return StaticArrayOp(ins.Op);

            case Op.IntToEnum:
            {
                // a constant's name compiles to a bare number of the enum's size; a number converted was written as one
                var e = ParseExpr();
                string type = TypeName(ins.Arg(0));
                if (e.Literal is not null)
                    return new($"({type}){Wrap(e, Precedence.Unary)}", Precedence.Unary) { EnumType = type, Type = type };
                return e with { EnumType = type };
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
                return new($"({TypeName(ins.Arg(0))}){Wrap(ParseExpr(), Precedence.Unary)}", Precedence.Unary) { Type = TypeName(ins.Arg(0)) };

            case Op.FloatToInt: case Op.StringToInt: case Op.BoolToInt:
                return new($"(int){Wrap(Explicit(ParseExpr()), Precedence.Unary)}", Precedence.Unary);
            case Op.StringToFloat: case Op.BoolToFloat:
                return new($"(float){Wrap(Explicit(ParseExpr()), Precedence.Unary)}", Precedence.Unary);
            case Op.IntToByte: case Op.FloatToByte: case Op.StringToByte: case Op.BoolToByte:
                return new($"(byte){Wrap(Explicit(ParseExpr()), Precedence.Unary)}", Precedence.Unary);

            // conversions the compiler inserts on its own
            case Op.ByteToBool: case Op.ByteToInt: case Op.ByteToFloat: case Op.ByteToString: case Op.BoolToString:
            case Op.IntToBool: case Op.IntToFloat: case Op.IntToString: case Op.FloatToBool: case Op.FloatToString:
            case Op.NameToBool: case Op.NameToString: case Op.StringToBool: case Op.ObjectToBool: case Op.ObjectToString:
            case Op.EnumToString:
            {
                var e = ParseExpr();
                // (int)EFDT_Head passed on as a float: an enum converts to int on its own, but not on to a float, so the cast stays
                if (ins.Op is Op.IntToBool or Op.IntToFloat or Op.IntToString && e.EnumType is not null && e.Inner?.Literal is not null && !e.Text.StartsWith('('))
                    return new($"(int){e.Text}", Precedence.Unary);
                return e with { Implicit = ins.Op switch
                {
                    Op.ByteToInt => "int",
                    Op.ByteToFloat or Op.IntToFloat => "float",
                    Op.ByteToBool or Op.IntToBool or Op.FloatToBool or Op.NameToBool or Op.StringToBool or Op.ObjectToBool => "bool",
                    _ => "string",
                } };
            }

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
    private string? MemberType(int typeIndex, string name) =>
        typeIndex >= 0 && typeIndex < blob.Types.Count ? FieldType(blob.Types[typeIndex].Name, name) : null;

    /// <summary>The declared type of field <paramref name="name"/> on <paramref name="owner"/> or a class it extends, in the blob or the game.</summary>
    internal string? FieldType(string owner, string name)
    {
        for (int depth = 0; owner.Length > 0 && depth < 64; depth++)
        {
            var script = blob.Classes.FirstOrDefault(c => !c.IsState && c.Name == owner);
            var type = script?.Properties.FirstOrDefault(p => p.Name == name)?.Type
                ?? blob.Structs.FirstOrDefault(c => c.Name == owner)?.Properties.FirstOrDefault(p => p.Name == name)?.Type
                ?? blob.AddedFields.FirstOrDefault(f => f.Owner == owner && f.Name == name)?.Type;
            if (type is not null) return type;
            var decl = _game.Types.GetValueOrDefault(owner);
            if (decl?.Fields.TryGetValue(name, out type) == true) return type;
            owner = script is { Base.Length: > 0 } ? script.Base : decl?.Parent ?? "";
        }
        return null;
    }

    private string? CurrentClass => _func.Class is { IsState: false } c ? c.Name : _func.Owner.Length > 0 ? _func.Owner : null;

    private static FunctionSignature FromBlob(ScriptFunction f) =>
        new([.. f.Params.Select(p => p.Type)], f.Return.Count > 0 ? f.Return[0].Type : null);

    private FunctionSignature? GlobalSignature(string name) =>
        blob.Globals.Concat(blob.ReplacedGlobals).FirstOrDefault(f => f.Name == name) is { } own ? FromBlob(own) : _game.Globals.GetValueOrDefault(name);

    /// <summary><paramref name="cls"/>'s method <paramref name="name"/>, its own or one it inherits, from the blob or the game.</summary>
    private FunctionSignature? MethodSignature(string cls, string name)
    {
        for (int depth = 0; cls.Length > 0 && depth < 64; depth++)
        {
            var script = blob.Classes.FirstOrDefault(c => !c.IsState && c.Name == cls);
            if (script?.Functions.FirstOrDefault(f => f.Name == name) is { } own) return FromBlob(own);
            if (blob.AddedMethods.Concat(blob.Replaced).FirstOrDefault(f => f.Owner == cls && f.Name == name) is { } added) return FromBlob(added);
            var decl = _game.Types.GetValueOrDefault(cls);
            if (decl?.Methods.TryGetValue(name, out var sig) == true) return sig;
            cls = script is { Base.Length: > 0 } ? script.Base : decl?.Parent ?? "";
        }
        return null;
    }

    /// <summary>A method called on something of unknown type: every method of that name, with the parameter types they all agree on.</summary>
    private FunctionSignature? AnySignature(string name)
    {
        var all = blob.Classes.SelectMany(c => c.Functions).Concat(blob.AddedMethods).Concat(blob.Replaced)
            .Where(f => f.Name == name).Select(FromBlob)
            .Concat(_game.MethodsByName.GetValueOrDefault(name) ?? []).ToList();
        if (all.Count == 0) return null;
        int count = all.Max(s => s.Params.Count);
        var types = Enumerable.Range(0, count)
            .Select(i => all.Select(s => s.Params.ElementAtOrDefault(i)).Distinct().ToList() is [{ } only] ? only : "").ToList();
        return new(types, all.Select(s => s.Return).Distinct().Count() == 1 ? all[0].Return : null);
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

    /// <summary>A call; a bare number passed where the parameter is an enum was written as the enum's constant, so it's shown as one.</summary>
    private Expr Call(string name, List<Expr> args, FunctionSignature? signature = null)
    {
        int used = args.Count;
        while (used > 0 && args[used - 1].IsEmpty) used--;
        var text = args.Take(used).Select((a, i) => AsEnum(signature?.Params.ElementAtOrDefault(i), a).Text);
        return new($"{name}({string.Join(", ", text)})", Precedence.Primary) { Type = signature?.Return };
    }

    private static Expr Operator(int id, List<Expr> args)
    {
        if (!Operators.TryGet(id, out string symbol, out int prec) || args.Count is 0 or > 2)
            return new($"{Operators.NameOf(id)}({string.Join(", ", args.Select(a => a.Text))})", Precedence.Primary);
        if (args.Count == 1)
            return new($"{symbol}{Wrap(Explicit(args[0], numericOnly: true), Precedence.Primary)}", Precedence.Unary);
        return Binary(Explicit(args[0], numericOnly: true), symbol, prec, Explicit(args[1], numericOnly: true));
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
        // left-associative, as the compiler reads a && b && c: only a chain on the right keeps its brackets
        string left = Wrap(a, prec);
        string right = b.Prec <= prec ? $"({b.Text})" : b.Text;
        return new($"{left} {symbol} {right}", prec);
    }

    private static readonly Lazy<Standalone.Rtti> BuiltIn = new(Standalone.Rtti.LoadBuiltIn);

    /// <summary>
    /// Whether a direct call to <paramref name="target"/>'s <paramref name="name"/> needs "super.": only when a class between it and
    /// <paramref name="owner"/> defines the name again, so a plain call would reach that one instead.
    /// </summary>
    private bool NeedsSuper(string owner, string target, string name)
    {
        for (int depth = 0; owner.Length > 0 && owner != target && depth < 64; depth++)
        {
            var script = blob.Classes.FirstOrDefault(value => !value.IsState && value.Name == owner);
            var native = BuiltIn.Value.Classes.FirstOrDefault(value => value.Name == owner);
            if (script?.Functions.Any(f => f.Name == name) == true || native?.Methods.Contains(name) == true
                || blob.AddedMethods.Any(f => (f.Class?.Name ?? f.Owner) == owner && f.Name == name))
                return true;
            owner = script?.Base ?? native?.Base ?? "";
        }
        return false;
    }

    /// <summary>
    /// An operator picks its overload from its operands' number types, and a cast takes what it's given: there a conversion
    /// the compiler inserted has to be written out, or (float)a / b compiles back as an int division.
    /// </summary>
    private static Expr Explicit(Expr e, bool numericOnly = false) =>
        e.Implicit is { } type && e.Literal is null && (!numericOnly || type is "int" or "float") ? new($"({type}){Wrap(e, Precedence.Primary)}", Precedence.Unary) : e;

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
            Op.MapTryGet => new($"{m}.TryGet({ParseExpr().Text}, {ParseExpr().Text})", Precedence.Primary),
            _ => new($"{m}[{ParseExpr().Text}]", Precedence.Primary), // MapElement
        };
    }

    private Expr ArrayOp(Op op)
    {
        var arr = ParseExpr();
        string a = Wrap(arr, Precedence.Primary);
        // array:2,0,EJournalStatus holds EJournalStatus
        string? element = arr.Type is { } at && at.StartsWith("array:", StringComparison.Ordinal) && at.Split(',', 3) is [_, _, var inner] ? inner : null;
        Expr One() => ParseExpr();
        return op switch
        {
            Op.ArrayClear => new($"{a}.Clear()", Precedence.Primary),
            Op.ArraySize => new($"{a}.Size()", Precedence.Primary),
            Op.ArrayPopBack => new($"{a}.PopBack()", Precedence.Primary),
            Op.ArrayLast => new($"{a}.Last()", Precedence.Primary) { Type = element },
            Op.ArrayResize => new($"{a}.Resize({One().Text})", Precedence.Primary),
            Op.ArrayGrow => new($"{a}.Grow({One().Text})", Precedence.Primary),
            Op.ArrayFindFirst or Op.ArrayFindFirstFast => new($"{a}.FindFirst({One().Text})", Precedence.Primary),
            Op.ArrayFindLast or Op.ArrayFindLastFast => new($"{a}.FindLast({One().Text})", Precedence.Primary),
            Op.ArrayContains or Op.ArrayContainsFast => new($"{a}.Contains({One().Text})", Precedence.Primary),
            Op.ArrayPushBack => new($"{a}.PushBack({One().Text})", Precedence.Primary),
            Op.ArrayRemove or Op.ArrayRemoveFast => new($"{a}.Remove({One().Text})", Precedence.Primary),
            Op.ArrayErase => new($"{a}.Erase({One().Text})", Precedence.Primary),
            Op.ArrayEraseFast => new($"{a}.EraseFast({One().Text})", Precedence.Primary),
            Op.ArrayInsert => Insert(a),
            _ => new($"{a}[{One().Text}]", Precedence.Primary) { Type = element }, // ArrayElement
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
        if (!float.IsFinite(f)) throw new NotSupportedException($"{f} can't be written as a WitcherScript number."); // loc-ignore
        string s = f.ToString("R", CultureInfo.InvariantCulture);
        int exponentAt = s.IndexOf('E');
        if (exponentAt >= 0)
        {
            int exponent = int.Parse(s[(exponentAt + 1)..], CultureInfo.InvariantCulture);
            string sign = s[0] == '-' ? "-" : "";
            string mantissa = s[sign.Length..exponentAt];
            int dot = mantissa.IndexOf('.');
            int point = (dot >= 0 ? dot : mantissa.Length) + exponent;
            string digits = mantissa.Replace(".", "");
            s = sign + (point <= 0 ? "0." + new string('0', -point) + digits
                : point >= digits.Length ? digits + new string('0', point - digits.Length)
                : digits[..point] + "." + digits[point..]);
        }
        return s.Contains('.') ? s : s + ".0";
    }

    public static string Quote(string s, char delimiter = '"')
    {
        var sb = new StringBuilder(delimiter.ToString());
        foreach (char c in s)
        {
            sb.Append(c == delimiter ? "\\" + delimiter : c.ToString());
        }
        return sb.Append(delimiter).ToString();
    }
}
