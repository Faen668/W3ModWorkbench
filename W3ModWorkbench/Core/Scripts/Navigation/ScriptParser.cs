using System.Text;

namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>A function's extent in a parsed file: its parameter list and body.</summary>
public sealed record FunctionScope(ScriptSymbol Symbol, int ParamsStart, int ParamsEnd, int BodyStart, int BodyEnd);

/// <summary>A class, state or struct's extent in a parsed file.</summary>
public sealed record TypeScope(ScriptSymbol Symbol, int Start, int End);

/// <summary>Everything declared in one script, plus where each function and type starts and ends.</summary>
public sealed class ParsedScript
{
    public required ScriptFile File { get; init; }

    /// <summary>The text with comments and strings blanked out, so offsets still match the original.</summary>
    public required string Code { get; init; }

    /// <summary>Where each line starts, for turning offsets into line numbers.</summary>
    public required List<int> LineStarts { get; init; }

    public List<ScriptSymbol> Symbols { get; } = [];
    public List<FunctionScope> Functions { get; } = [];
    public List<TypeScope> Types { get; } = [];

    public FunctionScope? FunctionAt(int offset) =>
        Functions.LastOrDefault(f => offset >= f.ParamsStart && offset <= f.BodyEnd);

    public TypeScope? TypeAt(int offset) =>
        Types.LastOrDefault(t => offset >= t.Start && offset <= t.End);

    /// <summary>1-based line of an offset.</summary>
    public int LineOf(int offset)
    {
        int index = LineStarts.BinarySearch(offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }
}

/// <summary>
/// Finds declarations in WitcherScript without compiling it: classes, states, structs, enums and their
/// values, functions, events and member variables, with the class each belongs to. Annotations
/// (<c>@wrapMethod(CR4Player)</c> and so on) give the function or field after them that class as its owner.
/// </summary>
public static class ScriptParser
{
    private enum ScopeKind { Type, Enum, Function, Block }

    private sealed record Scope(ScopeKind Kind, ScriptSymbol? Symbol, int Start, int ParamsStart = 0, int ParamsEnd = 0);

    private readonly record struct Token(int Start, int Length, bool IsWord);

    public static ParsedScript Parse(string text, ScriptFile file)
    {
        string code = Blank(text);
        var tokens = Tokenize(code);
        var result = new ParsedScript { File = file, Code = code, LineStarts = LineStarts(text) };

        var stack = new List<Scope>();
        Scope? pending = null;      // a type or function header waiting for its {
        (string Kind, string? Target)? annotation = null;

        string Word(int i) => i < tokens.Count && tokens[i].IsWord ? code.Substring(tokens[i].Start, tokens[i].Length) : "";
        bool Is(int i, char c) => i < tokens.Count && !tokens[i].IsWord && code[tokens[i].Start] == c;
        int Line(int offset) => result.LineOf(offset);
        ScriptSymbol Symbol(int i, SymbolKind kind, string? owner, string? type = null) =>
            new(Word(i), kind, owner, type, file, tokens[i].Start, Line(tokens[i].Start));

        // the type whose members are being declared: the innermost class, state or struct
        ScriptSymbol? CurrentType() => stack.LastOrDefault(s => s.Kind == ScopeKind.Type)?.Symbol;
        bool InFunction() => stack.Any(s => s.Kind == ScopeKind.Function);

        // reads a type such as int, CR4Player or array< SItemUniqueId > starting at token i
        string? ReadType(ref int i)
        {
            if (!tokens[i].IsWord) return null;
            int start = tokens[i].Start, end = start + tokens[i].Length;
            i++;
            if (Is(i, '<'))
            {
                int depth = 0;
                for (; i < tokens.Count; i++)
                {
                    if (Is(i, '<')) depth++;
                    else if (Is(i, '>') && --depth == 0) { end = tokens[i].Start + 1; i++; break; }
                    else if (Is(i, ';') || Is(i, '{')) break;
                }
            }
            return Squash(code[start..end]);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!t.IsWord)
            {
                char c = code[t.Start];
                if (c == '@' && Word(i + 1).Length > 0)
                {
                    // @wrapMethod(CR4Player), @addField(CActor), @replaceMethod
                    string kind = Word(i + 1);
                    string? target = null;
                    i++;
                    if (Is(i + 1, '('))
                    {
                        if (Word(i + 2).Length > 0 && Is(i + 3, ')')) { target = Word(i + 2); i += 3; }
                        else if (Is(i + 2, ')')) i += 2;
                    }
                    annotation = (kind, target);
                }
                else if (c == '{')
                {
                    stack.Add(pending is null ? new Scope(ScopeKind.Block, null, t.Start) : pending with { Start = t.Start });
                    pending = null;
                }
                else if (c == '}' && stack.Count > 0)
                {
                    var scope = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    int end = t.Start + 1;
                    if (scope.Kind == ScopeKind.Function)
                        result.Functions.Add(new FunctionScope(scope.Symbol!, scope.ParamsStart, scope.ParamsEnd, scope.Start, end));
                    else if (scope.Kind is ScopeKind.Type or ScopeKind.Enum)
                        result.Types.Add(new TypeScope(scope.Symbol!, scope.Start, end));
                }
                else if (c == ';')
                {
                    pending = null;
                }
                else if (c == ',' && stack.Count > 0 && stack[^1].Kind == ScopeKind.Enum && Word(i + 1).Length > 0)
                {
                    result.Symbols.Add(Symbol(i + 1, SymbolKind.EnumMember, stack[^1].Symbol!.Name));
                }
                continue;
            }

            string word = Word(i);
            if (InFunction())
            {
                // locals are looked up on demand, nothing inside a body is a declaration we index
                continue;
            }

            if ((word is "class" or "struct" or "enum") && Word(i + 1).Length > 0)
            {
                var kind = word switch { "class" => SymbolKind.Class, "struct" => SymbolKind.Struct, _ => SymbolKind.Enum };
                var sym = Symbol(i + 1, kind, null);
                i++;
                if (Word(i + 1) == "extends" && Word(i + 2).Length > 0)
                {
                    sym = sym with { Base = Word(i + 2) };
                    i += 2;
                }
                result.Symbols.Add(sym);
                pending = new Scope(kind == SymbolKind.Enum ? ScopeKind.Enum : ScopeKind.Type, sym, 0);
                annotation = null;
                if (kind == SymbolKind.Enum && Is(i + 1, '{') && Word(i + 2).Length > 0)
                    result.Symbols.Add(Symbol(i + 2, SymbolKind.EnumMember, sym.Name));
            }
            else if (word == "state" && Word(i + 1).Length > 0 && Word(i + 2) == "in" && Word(i + 3).Length > 0)
            {
                // state Combat in W3PlayerWitcher extends ExtendedMovable
                string owner = Word(i + 3);
                var sym = Symbol(i + 1, SymbolKind.State, owner);
                i += 3;
                if (Word(i + 1) == "extends" && Word(i + 2).Length > 0)
                {
                    sym = sym with { Base = Word(i + 2) };
                    i += 2;
                }
                result.Symbols.Add(sym);
                pending = new Scope(ScopeKind.Type, sym, 0);
                annotation = null;
            }
            else if ((word is "function" or "event") && Word(i + 1).Length > 0 && Is(i + 2, '('))
            {
                var owner = CurrentType();
                string? ownerKey = owner?.TypeKey;
                string? ownerTitle = owner?.Kind == SymbolKind.State ? $"{owner.Owner}.{owner.Name}" : null;
                string? annotationKind = null;
                if (annotation is { } a && a.Kind is "wrapMethod" or "replaceMethod" or "addMethod")
                {
                    annotationKind = a.Kind;
                    ownerKey = a.Target;
                    ownerTitle = null;
                }
                annotation = null;

                int nameIndex = i + 1;
                int open = i + 2, close = open, depth = 0;
                for (; close < tokens.Count; close++)
                {
                    if (Is(close, '(')) depth++;
                    else if (Is(close, ')') && --depth == 0) break;
                }
                i = Math.Min(close, tokens.Count - 1);
                string? returns = null;
                if (Is(i + 1, ':') && i + 2 < tokens.Count)
                {
                    int ti = i + 2;
                    returns = ReadType(ref ti);
                    i = ti - 1;
                }

                var sym = Symbol(nameIndex, word == "event" ? SymbolKind.Event : SymbolKind.Function, ownerKey, returns)
                    with { Annotation = annotationKind, OwnerTitle = ownerTitle };
                result.Symbols.Add(sym);
                int paramsEnd = close < tokens.Count ? tokens[close].Start : code.Length;
                pending = new Scope(ScopeKind.Function, sym, 0, tokens[open].Start, paramsEnd);
            }
            else if (word is "var" or "autobind")
            {
                // var a, b : int;   autobind inv : CInventoryComponent = single;
                var owner = CurrentType();
                string? ownerKey = owner?.TypeKey;
                string? ownerTitle = owner?.Kind == SymbolKind.State ? $"{owner.Owner}.{owner.Name}" : null;
                string? annotationKind = null;
                if (annotation is { Kind: "addField" } a)
                {
                    annotationKind = a.Kind;
                    ownerKey = a.Target;
                    ownerTitle = null;
                }
                annotation = null;

                var names = new List<int>();
                int j = i + 1;
                for (; j < tokens.Count && tokens[j].IsWord; j++)
                {
                    names.Add(j);
                    if (!Is(j + 1, ',')) { j++; break; }
                    j++;
                }
                if (!Is(j, ':') || j + 1 >= tokens.Count) continue;
                int typeIndex = j + 1;
                string? type = ReadType(ref typeIndex);
                foreach (int n in names)
                    result.Symbols.Add(Symbol(n, SymbolKind.Field, ownerKey, type) with { Annotation = annotationKind, OwnerTitle = ownerTitle });
                i = typeIndex - 1;
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- text

    /// <summary>Replaces comments, strings and names ('...') with spaces, keeping line breaks and offsets.</summary>
    public static string Blank(string text)
    {
        var sb = new StringBuilder(text);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') sb[i++] = ' ';
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                for (; i < end; i++) if (text[i] is not ('\n' or '\r')) sb[i] = ' ';
            }
            else if (c is '"' or '\'')
            {
                // keep the quotes so a string still reads as one value
                i++;
                while (i < text.Length && text[i] != c && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) sb[i++] = ' ';
                    sb[i++] = ' ';
                }
                i++;
            }
            else i++;
        }
        return sb.ToString();
    }

    private static List<Token> Tokenize(string code)
    {
        var tokens = new List<Token>(code.Length / 6);
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (IsWordStart(c))
            {
                int start = i;
                while (i < code.Length && IsWordChar(code[i])) i++;
                tokens.Add(new Token(start, i - start, true));
            }
            else if (char.IsDigit(c))
            {
                while (i < code.Length && (IsWordChar(code[i]) || code[i] == '.')) i++;
            }
            else
            {
                if (c is not ('"' or '\'')) tokens.Add(new Token(i, 1, false));
                i++;
            }
        }
        return tokens;
    }

    public static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>array< int > → array<int></summary>
    public static string Squash(string type)
    {
        var sb = new StringBuilder(type.Length);
        foreach (char c in type) if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }
}
