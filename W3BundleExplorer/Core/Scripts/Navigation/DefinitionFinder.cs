using System.Text.RegularExpressions;

namespace W3BundleExplorer.Core.Scripts.Navigation;

/// <summary>
/// Works out what a word in a script refers to, the way a language server's "go to definition" does,
/// but from declarations alone: locals and parameters first, then members of the class the code is in
/// (and its bases), then globals. For <c>a.b</c> it follows the declared types of <c>a</c> through
/// variables, fields, function return types, casts and array elements, and falls back on every member
/// with that name when a type can't be worked out.
/// </summary>
public sealed partial class DefinitionFinder
{
    /// <summary>Engine globals that no script declares.</summary>
    private static readonly Dictionary<string, string> EngineGlobals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["thePlayer"] = "CR4Player",
        ["theGame"] = "CR4Game",
        ["theInput"] = "CInputManager",
        ["theSound"] = "CScriptSoundSystem",
        ["theTelemetry"] = "CR4TelemetryScriptProxy",
        ["theDebug"] = "CDebugAttributesManager",
        ["theTimer"] = "CTimerScriptKeyword",
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "new", "delete",
        "true", "false", "NULL", "class", "struct", "enum", "state", "function", "event", "var", "autobind", "extends",
        "in", "import", "abstract", "statemachine", "latent", "entry", "timer", "exec", "quest", "cleanup", "storyscene",
        "reward", "final", "private", "protected", "public", "editable", "saved", "const", "out", "optional", "inlined",
        "hint", "defaults", "single", "int", "float", "bool", "string", "name", "byte", "array", "void",
    };

    private readonly ParsedScript _doc;
    private readonly string _code;
    private readonly IReadOnlyList<ScriptIndex> _layers;

    /// <param name="doc">The script being read.</param>
    /// <param name="workspace">The other scripts to look in, nearest first.</param>
    public DefinitionFinder(ParsedScript doc, IReadOnlyList<ScriptIndex> workspace)
    {
        _doc = doc;
        _code = doc.Code;
        _layers = [new ScriptIndex(doc.Symbols), .. workspace];
    }

    /// <summary>The identifier at or just before an offset, if any.</summary>
    public (int Start, int Length)? WordAt(int offset)
    {
        if (offset < 0 || offset > _code.Length) return null;
        int start = offset, end = offset;
        while (start > 0 && ScriptParser.IsWordChar(_code[start - 1])) start--;
        while (end < _code.Length && ScriptParser.IsWordChar(_code[end])) end++;
        if (end == start || !ScriptParser.IsWordStart(_code[start])) return null;
        return (start, end - start);
    }

    /// <summary>Where the word starting at <paramref name="start"/> is declared, best guess first.</summary>
    public IReadOnlyList<ScriptSymbol> Find(int start, int length)
    {
        string word = _code.Substring(start, length);
        var fn = _doc.FunctionAt(start);
        string? self = fn?.Symbol.Owner ?? _doc.TypeAt(start)?.Symbol.TypeKey;

        // on a declaration itself: go to what it changes, overrides or extends
        if (_doc.Symbols.FirstOrDefault(s => s.Offset == start) is { } declared)
            return Declared(declared);

        switch (word)
        {
            case "wrappedMethod" when fn?.Symbol.Annotation == "wrapMethod":
                return Originals(fn.Symbol);
            case "this":
                return TypeDefinition(self);
            case "super":
                return TypeDefinition(self is null ? null : Chain(self).Skip(1).FirstOrDefault());
            case "parent" or "virtual_parent":
                return TypeDefinition(StateOwner(self));
        }
        if (Keywords.Contains(word)) return [];

        // Name( is a call, so only functions and events will do, or a struct built in place: SMyStruct(a, b)
        int after = NextNonSpace(start + length);
        bool call = after < _code.Length && _code[after] == '(';
        bool Fits(ScriptSymbol s) => !call || s.Kind is SymbolKind.Function or SymbolKind.Event or SymbolKind.Struct;

        int before = PrevNonSpace(start - 1);
        if (before >= 0 && _code[before] == '.')
        {
            // a.b: members of a's type, or any member called b
            if (TypeOf(before, fn, self, 0) is { } type)
            {
                // arrays and plain values only have the engine's built-in methods
                if (IsBuiltIn(type)) return [];
                if (MembersInChain(type, word, filter: Fits) is { Count: > 0 } members) return members;
            }
            return Distinct(All(word).Where(s => s.IsMember && Fits(s)));
        }

        if (!call && fn is not null && Local(fn, word) is { } local) return [local];
        if (self is not null && MembersInChain(self, word, includeStateOwner: true, filter: Fits) is { Count: > 0 } own) return own;
        if (!call && EngineGlobals.TryGetValue(word, out string? engineType)) return TypeDefinition(engineType);

        var globals = Distinct(All(word).Where(s => Fits(s) && (s.IsType || s.Kind == SymbolKind.EnumMember || (s.IsMember && s.Owner is null))));
        return globals.Count > 0 ? globals : Distinct(All(word).Where(s => s.IsMember && Fits(s)));
    }

    private static bool IsBuiltIn(string type) =>
        type.StartsWith("array<", StringComparison.OrdinalIgnoreCase)
        || type is "int" or "float" or "bool" or "string" or "name" or "byte" or "Uint64";

    // ---------------------------------------------------------------- declarations

    private IReadOnlyList<ScriptSymbol> Declared(ScriptSymbol s)
    {
        switch (s.Kind)
        {
            case SymbolKind.Function or SymbolKind.Event when s.Annotation is "wrapMethod" or "replaceMethod":
                return Originals(s);
            case SymbolKind.Function or SymbolKind.Event when s.Annotation is null && s.Owner is not null:
                // the base class version this one overrides
                foreach (string type in Chain(s.Owner).Skip(1))
                {
                    var found = Distinct(All(s.Name).Where(o => o.IsMember && IsOriginal(o) && Same(o.Owner, type)));
                    if (found.Count > 0) return found;
                }
                return [];
            case SymbolKind.Class or SymbolKind.State when s.Base is not null:
                return TypeDefinition(s.Kind == SymbolKind.State ? StateBase(s.Owner!, s.Base) : s.Base);
            case SymbolKind.Class or SymbolKind.Struct or SymbolKind.Enum:
                // the same type declared somewhere else, e.g. a mod adding values to one of the game's enums
                return Distinct(All(s.Name).Where(o => o.Kind == s.Kind && o.File.Key != s.File.Key));
            default:
                return [];
        }
    }

    /// <summary>What a @wrapMethod or @replaceMethod function changes.</summary>
    private IReadOnlyList<ScriptSymbol> Originals(ScriptSymbol s)
    {
        if (s.Owner is null)
            return Distinct(All(s.Name).Where(o => o.IsMember && o.Owner is null && o.Annotation is null));
        foreach (string type in Chain(s.Owner))
        {
            var found = Distinct(All(s.Name).Where(o => o.IsMember && IsOriginal(o) && Same(o.Owner, type)));
            if (found.Count > 0) return found;
        }
        return [];
    }

    /// <summary>A declaration a wrap or an override can point at: a plain one, or a method another mod adds with @addMethod.</summary>
    private static bool IsOriginal(ScriptSymbol s) => s.Annotation is null or "addMethod";

    private IReadOnlyList<ScriptSymbol> TypeDefinition(string? key) =>
        key is not null && Type(key) is { } t ? [t] : [];

    // ---------------------------------------------------------------- types

    private ScriptSymbol? Type(string key)
    {
        foreach (var layer in _layers)
            if (layer.Type(key) is { } t) return t;
        return null;
    }

    /// <summary>A type and its bases, nearest first. A state's base is another state of its class or the class's bases.</summary>
    private IEnumerable<string> Chain(string key)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = key;
        while (current is not null && seen.Add(current))
        {
            yield return current;
            var t = Type(current);
            if (t is null) yield break;
            current = t.Kind switch
            {
                SymbolKind.State => t.Base is null ? "CScriptableState" : StateBase(t.Owner!, t.Base),
                // a class without extends is a CObject
                SymbolKind.Class when t.Base is null && !Same(t.Name, "CObject") && !Same(t.Name, "IScriptable") && !Same(t.Name, "ISerializable") => "CObject",
                _ => t.Base,
            };
        }
    }

    /// <summary>state X in W3PlayerWitcher extends Combat: Combat is a state of W3PlayerWitcher or one of its bases.</summary>
    private string StateBase(string owner, string baseName)
    {
        foreach (string type in Chain(owner))
        {
            string key = ScriptSymbol.StateKey(type, baseName);
            if (Type(key) is not null) return key;
        }
        return baseName;
    }

    private string? StateOwner(string? key) =>
        key is not null && Type(key) is { Kind: SymbolKind.State } state ? state.Owner : null;

    private IReadOnlyList<ScriptSymbol> MembersInChain(string type, string name, bool includeStateOwner = false,
        Func<ScriptSymbol, bool>? filter = null)
    {
        var candidates = All(name).Where(s => s.IsMember && (filter is null || filter(s))).ToList();
        if (candidates.Count == 0) return [];
        foreach (string key in Chain(type))
        {
            var found = Distinct(candidates.Where(s => Same(s.Owner, key)));
            if (found.Count > 0) return found;
        }
        // code in a state can reach its statemachine's members through parent
        if (includeStateOwner && StateOwner(type) is { } owner) return MembersInChain(owner, name, filter: filter);
        return [];
    }

    /// <summary>The declared type of the expression that ends just before <paramref name="end"/>.</summary>
    private string? TypeOf(int end, FunctionScope? fn, string? self, int depth)
    {
        if (depth > 12) return null;
        int i = PrevNonSpace(end - 1);
        if (i < 0) return null;
        char c = _code[i];

        if (c == ')')
        {
            int open = MatchBack(i, '(', ')');
            if (open < 0) return null;
            int nameEnd = PrevNonSpace(open - 1);
            if (nameEnd >= 0 && ScriptParser.IsWordChar(_code[nameEnd]))
            {
                // a call: the function's return type
                int nameStart = WordStart(nameEnd);
                string f = _code[nameStart..(nameEnd + 1)];
                if (Keywords.Contains(f)) return null;
                int dot = PrevNonSpace(nameStart - 1);
                IEnumerable<ScriptSymbol> calls;
                if (dot >= 0 && _code[dot] == '.')
                    calls = TypeOf(dot, fn, self, depth + 1) is { } t ? MembersInChain(t, f) : All(f).Where(s => s.IsMember);
                else
                {
                    var own = self is null ? [] : MembersInChain(self, f, includeStateOwner: true);
                    calls = own.Count > 0 ? own : All(f).Where(s => s.IsMember && s.Owner is null);
                    // SMyStruct(a, b) builds the struct
                    if (!calls.Any() && All(f).FirstOrDefault(s => s.Kind == SymbolKind.Struct) is { } built) return built.TypeKey;
                }
                return calls.FirstOrDefault(s => s.Type is not null)?.Type;
            }
            // (CR4Player)x, (new X in this) or a bracketed expression
            if (Cast().Match(_code, open + 1, i - open - 1) is { Success: true } cast) return cast.Groups[1].Value;
            if (New().Match(_code, open + 1, i - open - 1) is { Success: true } created) return created.Groups[1].Value;
            return TypeOf(i, fn, self, depth + 1);
        }

        if (c == ']')
        {
            int open = MatchBack(i, '[', ']');
            if (open < 0) return null;
            string? array = TypeOf(open, fn, self, depth + 1);
            return array is not null && array.StartsWith("array<", StringComparison.OrdinalIgnoreCase) && array.EndsWith('>')
                ? array[6..^1]
                : null;
        }

        if (!ScriptParser.IsWordChar(c)) return null;
        int start = WordStart(i);
        string word = _code[start..(i + 1)];
        int before = PrevNonSpace(start - 1);
        if (before >= 0 && _code[before] == '.')
        {
            var fields = TypeOf(before, fn, self, depth + 1) is { } t ? MembersInChain(t, word) : All(word).Where(s => s.IsMember);
            return fields.FirstOrDefault(s => s.Kind == SymbolKind.Field)?.Type;
        }

        switch (word)
        {
            case "this": return self;
            case "super": return self is null ? null : Chain(self).Skip(1).FirstOrDefault();
            case "parent" or "virtual_parent": return StateOwner(self);
        }
        if (fn is not null && Local(fn, word) is { } local) return local.Type;
        if (self is not null && MembersInChain(self, word, includeStateOwner: true).FirstOrDefault(s => s.Kind == SymbolKind.Field) is { } field)
            return field.Type;
        if (EngineGlobals.TryGetValue(word, out string? engineType)) return engineType;
        return All(word).FirstOrDefault(s => s.Kind == SymbolKind.Field && s.Owner is null)?.Type;
    }

    // ---------------------------------------------------------------- locals

    /// <summary>A parameter of the function, or a variable declared in its body.</summary>
    private ScriptSymbol? Local(FunctionScope fn, string name)
    {
        if (Find(Param(), fn.ParamsStart, fn.ParamsEnd, name, SymbolKind.Parameter) is { } param) return param;
        return Find(LocalVar(), fn.BodyStart, fn.BodyEnd, name, SymbolKind.Local);

        ScriptSymbol? Find(Regex regex, int from, int to, string wanted, SymbolKind kind)
        {
            if (to <= from) return null;
            for (var m = regex.Match(_code, from, to - from); m.Success; m = m.NextMatch())
            {
                foreach (Capture capture in m.Groups["name"].Captures)
                {
                    if (!capture.Value.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    return new ScriptSymbol(capture.Value, kind, null, ScriptParser.Squash(m.Groups["type"].Value),
                        _doc.File, capture.Index, _doc.LineOf(capture.Index));
                }
            }
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Everything declared with this name. Exact-case matches win over the others.</summary>
    private IEnumerable<ScriptSymbol> All(string name)
    {
        var all = _layers.SelectMany(l => l.Named(name)).ToList();
        var exact = all.Where(s => s.Name == name).ToList();
        return exact.Count > 0 ? exact : all;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same declaration found twice in one source (a decompiled
    /// blob lists everything in all.ws as well) is kept once, preferring the file being read.
    /// Originals come before annotations that change them.
    /// </summary>
    private static List<ScriptSymbol> Distinct(IEnumerable<ScriptSymbol> symbols)
    {
        var list = symbols.ToList();
        if (list.Count == 0) return list;
        var seen = new HashSet<(string, SymbolKind, string?, string?, string)>();
        return list
            .Where(s => seen.Add((s.Name.ToLowerInvariant(), s.Kind, s.Owner?.ToLowerInvariant(), s.Annotation, s.File.SourceKey)))
            .OrderBy(s => s.Annotation is null ? 0 : 1)
            .ToList();
    }

    private int NextNonSpace(int i)
    {
        while (i < _code.Length && char.IsWhiteSpace(_code[i])) i++;
        return i;
    }

    private int PrevNonSpace(int i)
    {
        while (i >= 0 && char.IsWhiteSpace(_code[i])) i--;
        return i;
    }

    private int WordStart(int end)
    {
        while (end > 0 && ScriptParser.IsWordChar(_code[end - 1])) end--;
        return end;
    }

    private int MatchBack(int close, char open, char shut)
    {
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (_code[i] == shut) depth++;
            else if (_code[i] == open && --depth == 0) return i;
        }
        return -1;
    }

    [GeneratedRegex(@"\G\s*\(\s*(\w+)\s*\)")]
    private static partial Regex Cast();

    [GeneratedRegex(@"\G\s*new\s+(\w+)")]
    private static partial Regex New();

    // a : int, optional b, c : array< float >, out d : CActor
    [GeneratedRegex(@"(?:\b(?:out|optional)\s+)*(?<name>\w+)(?:\s*,\s*(?<name>\w+))*\s*:\s*(?<type>\w+(?:\s*<[^>;]*>)?)")]
    private static partial Regex Param();

    [GeneratedRegex(@"\bvar\s+(?<name>\w+)(?:\s*,\s*(?<name>\w+))*\s*:\s*(?<type>\w+(?:\s*<[^>;]*>)?)")]
    private static partial Regex LocalVar();
}
