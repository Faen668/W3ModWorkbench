using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>A parameter's name to show before the argument at <paramref name="Offset"/> in a call: <c>Heal(amount: 10)</c>.</summary>
public sealed record ParameterHint(int Offset, string Name);

/// <summary>A local, parameter or private field that's declared and never used, to draw faded.</summary>
public sealed record UnusedName(int Offset, int Length, SymbolKind Kind);

public sealed partial class DefinitionFinder
{
    private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal) { "if", "while", "for", "switch", "return", "function", "event", "delete", "new" };

    /// <summary>
    /// The parameter names of the calls whose arguments start between <paramref name="from"/> and <paramref name="to"/>,
    /// one for each argument, from the declaration the call goes to. An argument that is the parameter's own name
    /// (<c>Heal(amount)</c>), or ends with it (<c>Heal(this.amount)</c>), needs none and gets none.
    /// </summary>
    public List<ParameterHint> ParameterHints(int from, int to)
    {
        var hints = new List<ParameterHint>();
        from = Math.Max(0, from);
        to = Math.Min(_code.Length, to);
        // a call can start before the range and have arguments in it: look back a little
        int scanFrom = Math.Max(0, from - 400);
        for (int i = scanFrom; i < to; i++)
        {
            if (!ScriptParser.IsWordStart(_code[i]) || i > 0 && ScriptParser.IsWordChar(_code[i - 1])) continue;
            int end = i;
            while (end < _code.Length && ScriptParser.IsWordChar(_code[end])) end++;
            int open = NextNonSpace(end);
            if (open >= _code.Length || _code[open] != '(' || NotCalls.Contains(_code[i..end]) || IsDeclarationName(i))
            {
                i = end - 1;
                continue;
            }
            var args = Arguments(open);
            if (args.Count > 0 && args[^1].Start >= from && args[0].Start < to
                && Find(i, end - i).FirstOrDefault(s => s.Kind is SymbolKind.Function or SymbolKind.Event) is { } target
                && ParameterNames(target) is { Count: > 0 } names)
            {
                for (int a = 0; a < args.Count && a < names.Count; a++)
                {
                    var (start, text) = args[a];
                    if (start < from || start >= to || text.Length == 0) continue;
                    string last = text[(text.LastIndexOf('.') + 1)..].Trim();
                    if (last.Equals(names[a], StringComparison.OrdinalIgnoreCase)) continue;
                    hints.Add(new ParameterHint(start, names[a]));
                }
            }
            i = end - 1;
        }
        return hints;
    }

    /// <summary>Whether the word at an offset is the name in a declaration: this script's, or after function or event.</summary>
    private bool IsDeclarationName(int start)
    {
        if (_doc.Symbols.Any(s => s.Offset == start)) return true;
        int before = PrevNonSpace(start - 1);
        if (before < 0) return false;
        int wordStart = WordStart(before + 1);
        string word = _code[wordStart..(before + 1)];
        return word is "function" or "event";
    }

    /// <summary>A call's arguments: where each starts (its first character that isn't a space) and its text, trimmed.</summary>
    private List<(int Start, string Text)> Arguments(int open)
    {
        var args = new List<(int, string)>();
        int depth = 0, argStart = open + 1;
        for (int i = open + 1; i < _code.Length; i++)
        {
            char c = _code[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']')
            {
                if (depth-- == 0)
                {
                    Add(argStart, i);
                    return args.Count == 1 && args[0].Item2.Length == 0 ? [] : args;
                }
            }
            else if (c == ',' && depth == 0)
            {
                Add(argStart, i);
                argStart = i + 1;
            }
            else if (c is ';' or '{' or '}') return [];
        }
        return [];

        void Add(int from, int to)
        {
            int s = from;
            while (s < to && char.IsWhiteSpace(_code[s])) s++;
            args.Add((s, _code[s..to].Trim()));
        }
    }

    /// <summary>A function's parameter names in order, read from its declaration: <c>(a : int, optional b, c : float)</c> is a, b, c.</summary>
    private static List<string> ParameterNames(ScriptSymbol function)
    {
        string text;
        try { text = function.File.Text; }
        catch (Exception) { return []; }
        int open = text.IndexOf('(', function.Offset);
        if (open < 0 || open - function.Offset > function.Name.Length + 40) return [];
        int close = text.IndexOf(')', open);
        if (close < 0) return [];
        string list = ScriptParser.Blank(text[(open + 1)..close]);
        var names = new List<string>();
        foreach (Match m in Param().Matches(list))
            foreach (Capture name in m.Groups["name"].Captures)
                names.Add(name.Value);
        return names;
    }

    /// <summary>
    /// The locals, parameters and private fields declared in this script and never used in it. A parameter the
    /// signature can't do without isn't one: an event's, a wrapped or replaced function's, or one that overrides a parent's.
    /// </summary>
    public List<UnusedName> UnusedNames()
    {
        var unused = new List<UnusedName>();
        foreach (var fn in _doc.Functions)
        {
            if (fn.BodyEnd <= fn.BodyStart) continue;
            // a timer's parameters (dt, id) are the engine's to give, whether the timer reads them or not
            bool fixedSignature = fn.Symbol.Kind == SymbolKind.Event || fn.Symbol.Annotation is "wrapMethod" or "replaceMethod"
                || IsTimer(fn.Symbol)
                || fn.Symbol.Annotation is null && fn.Symbol.Owner is not null && Declared(fn.Symbol).Count > 0;
            foreach (var local in Locals(fn, fn.BodyEnd))
            {
                if (local.Kind == SymbolKind.Parameter && fixedSignature) continue;
                if (!UsedInFunction(fn, local)) unused.Add(new UnusedName(local.Offset, local.Name.Length, local.Kind));
            }
        }
        foreach (var field in _doc.Symbols.Where(s => s.Kind == SymbolKind.Field && s.Owner is not null && IsPrivate(s)))
            if (Uses(field.Offset, field.Name.Length) is not { Count: > 1 })
                unused.Add(new UnusedName(field.Offset, field.Name.Length, SymbolKind.Field));
        return unused;
    }

    /// <summary>
    /// Whether a local or parameter is named anywhere in its function besides its declaration. A word after a dot
    /// (<c>x.name</c>) or before a bracket (<c>name(</c>) is a member or a call, not the variable.
    /// </summary>
    private bool UsedInFunction(FunctionScope fn, ScriptSymbol local)
    {
        int from = Math.Min(fn.ParamsStart, fn.BodyStart), to = fn.BodyEnd;
        for (int at = _code.IndexOf(local.Name, from, StringComparison.OrdinalIgnoreCase); at >= 0 && at < to;
             at = _code.IndexOf(local.Name, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = at + local.Name.Length;
            if (at == local.Offset) continue;
            if (at > 0 && ScriptParser.IsWordChar(_code[at - 1]) || end < _code.Length && ScriptParser.IsWordChar(_code[end])) continue;
            int before = PrevNonSpace(at - 1);
            if (before >= 0 && _code[before] == '.') continue;
            int after = NextNonSpace(end);
            if (after < _code.Length && _code[after] == '(') continue;
            // another declaration of the same name in a var list isn't a use either
            if (before >= 0 && (_code[before] == ',' || _code[before] == 'r' && before >= 2 && _code.AsSpan(before - 2, 3).SequenceEqual("var")) && IsInVarList(at)) continue;
            return true;
        }
        return false;
    }

    /// <summary>Whether a word is one of the names a var declaration lists: var a, b : int.</summary>
    private bool IsInVarList(int at)
    {
        int lineStart = _code.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        int colon = _code.IndexOf(':', at);
        int semi = _code.IndexOf(';', at);
        return _code[lineStart..at].TrimStart().StartsWith("var ", StringComparison.Ordinal) && colon >= 0 && (semi < 0 || colon < semi);
    }

    /// <summary>Whether a function is declared as a timer: <c>timer function Tick(dt : float, id : int)</c>.</summary>
    private bool IsTimer(ScriptSymbol function)
    {
        int lineStart = _code.LastIndexOf('\n', Math.Max(0, function.Offset - 1)) + 1;
        return Regex.IsMatch(_code[lineStart..function.Offset], @"\btimer\b");
    }

    /// <summary>Whether a field's declaration says private.</summary>
    private bool IsPrivate(ScriptSymbol field)
    {
        int lineStart = _code.LastIndexOf('\n', Math.Max(0, field.Offset - 1)) + 1;
        return Regex.IsMatch(_code[lineStart..field.Offset], @"\bprivate\b");
    }
}
