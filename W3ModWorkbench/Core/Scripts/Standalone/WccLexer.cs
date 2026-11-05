using System.Globalization;
using System.Text;

namespace W3ModWorkbench.Core.Scripts.Standalone;

// loc-ignore-start: wcc_lite's own words and the language's own tokens, which are never translated

/// <summary>A word or mark of WitcherScript as wcc_lite reads it: <see cref="Terminal"/> is the name its messages call it by.</summary>
public sealed record WccToken(string Terminal, string Text, int Line);

/// <summary>A problem found reading a script. Line 0 means wcc_lite gives no line.</summary>
public sealed record WccMessage(int Line, string Text);

/// <summary>
/// Splits a script into tokens exactly as wcc_lite does, with the same messages: the rules were worked out from how it
/// behaves and checked case by case against it. It reads the file's UTF-8 bytes, so anything outside ASCII counts as
/// whitespace except inside strings. Its messages stay in English, as they are wcc_lite's own.
/// </summary>
public static class WccLexer
{
    private const string Singles = "{}()[]<>=+-*/%!&|^~?:;,.@";
    private const string Opens = "{([", Closes = "})]";
    private static readonly string[] Directives = ["define", "undefine", "ifdef", "ifndef", "else", "endif"];

    /// <summary>The spellings that aren't identifiers, and the terminals they are.</summary>
    public static readonly IReadOnlyDictionary<string, string> Keywords = BuildKeywords();

    private static Dictionary<string, string> BuildKeywords()
    {
        var map = new Dictionary<string, string>();
        void Plain(string names)
        {
            foreach (string name in names.Split(' ')) map[name] = "TOKEN_" + name.ToUpperInvariant();
        }
        Plain("import abstract const extends class statemachine state enum struct function def editable final out optional "
            + "autobind any single local inlined private protected public event timer cleanup array map set defaults default "
            + "hint entry enter latent var exec saved quest savepoint reward new delete in if else switch case for while do "
            + "return break continue this super parent virtual_parent");
        foreach (string name in "string byte bool int float name void".Split(' ')) map[name] = "TOKEN_TYPE_" + name.ToUpperInvariant();
        map["true"] = "TOKEN_BOOL_TRUE";
        map["false"] = "TOKEN_BOOL_FALSE";
        map["NULL"] = "TOKEN_NULL";
        map["storyscene"] = "TOKEN_SCENE";
        map["replaceMethod"] = "TOKEN_EXT_REPLACE";
        map["addMethod"] = "TOKEN_EXT_ADD";
        map["wrapMethod"] = "TOKEN_EXT_WRAP";
        map["addField"] = "TOKEN_EXT_ADD_FIELD";
        map["theGame"] = "TOKEN_GET_GAME";
        map["thePlayer"] = "TOKEN_GET_PLAYER";
        map["theCamera"] = "TOKEN_GET_CAMERA";
        map["theSound"] = "TOKEN_GET_SOUND";
        map["theDebug"] = "TOKEN_GET_DEBUG";
        map["theTimer"] = "TOKEN_GET_TIMER";
        map["theInput"] = "TOKEN_GET_INPUT";
        map["theTelemetry"] = "TOKEN_GET_TELEMETRY";
        map["+="] = "TOKEN_OP_IADD";
        map["-="] = "TOKEN_OP_ISUB";
        map["*="] = "TOKEN_OP_IMUL";
        map["/="] = "TOKEN_OP_IDIV";
        map["&="] = "TOKEN_OP_IAND";
        map["|="] = "TOKEN_OP_IOR";
        map["||"] = "TOKEN_OP_LOGIC_OR";
        map["&&"] = "TOKEN_OP_LOGIC_AND";
        map["=="] = "TOKEN_OP_EQUAL";
        map["!="] = "TOKEN_OP_NOTEQUAL";
        map[">="] = "TOKEN_OP_GREQ";
        map["<="] = "TOKEN_OP_LEEQ";
        return map;
    }

    public static bool IsKeyword(string word) => Keywords.ContainsKey(word);

    public static List<WccToken> Tokenize(string source, List<WccMessage> errors)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        var code = new byte[bytes.Length + 1];
        bytes.CopyTo(code, 0);
        return new Reader(code, errors).Run();
    }

    private sealed class Reader(byte[] code, List<WccMessage> errors)
    {
        private enum Mode { Start, LineComment, BlockComment, String, Name, Word, Directive }
        private enum Kind { None, Single, Integer, Float, Word }

        private readonly List<WccToken> _tokens = [];
        private readonly List<byte> _text = [];
        private readonly List<byte> _directive = [], _argument = [];
        private readonly List<(int Line, int Bracket)> _brackets = [];
        private readonly HashSet<string> _defined = [];
        private readonly Stack<bool> _conditions = new();
        private int _line = 1, _i;

        private void Error(string text) => errors.Add(new WccMessage(_line, text));

        public List<WccToken> Run()
        {
            int length = code.Length - 1, startLine = 1, wordStart = 0, directiveKind = -1;
            bool lineStart = true, again = false, inArgument = false;
            byte recover = 0;
            Kind kind = Kind.None;
            var mode = Mode.Start;

            for (_i = 0; _i <= length; _i++)
            {
                if (again)
                {
                    _i--;
                    again = false;
                }
                sbyte c = (sbyte)code[_i];
                bool newlineCounts = true;

                if (recover != 0 && (c == recover || c <= 0x20))
                {
                    recover = 0;
                    mode = Mode.Start;
                    if (c is 10 or 13)
                    {
                        Error("Unexpected new line"); // loc-ignore
                        continue; // this line isn't counted, as in wcc_lite
                    }
                    if (c > 0x20) continue;
                    _text.Clear();
                }

                switch (mode)
                {
                    case Mode.Start:
                        if (c <= 0x20) break;
                        newlineCounts = false;
                        if (c == '/' && code[_i + 1] == '/') { mode = Mode.LineComment; _i++; }
                        else if (c == '/' && code[_i + 1] == '*') { mode = Mode.BlockComment; startLine = _line; _i++; }
                        else if (c == '#')
                        {
                            if (lineStart) { mode = Mode.Directive; startLine = _line; }
                            else Error("Directive token not at line start"); // loc-ignore
                            ResetDirective(ref inArgument, ref directiveKind);
                        }
                        else if (c == '"') { mode = Mode.String; startLine = _line; }
                        else if (c == '\'') { mode = Mode.Name; startLine = _line; }
                        else { mode = Mode.Word; again = true; }
                        break;

                    case Mode.LineComment:
                        if (c is 0 or 10 or 13) mode = Mode.Start;
                        break;

                    case Mode.BlockComment:
                        if (c == '*' && code[_i + 1] == '/') { mode = Mode.Start; _i++; }
                        break;

                    case Mode.String:
                        if (c == '\\' && _i >= 1 && _i + 1 <= length && code[_i + 1] == '"' && code[_i - 1] != '\\')
                        {
                            _text.Add((byte)'"');
                            _i++;
                            newlineCounts = false;
                        }
                        else if (c == '"')
                        {
                            Emit("TOKEN_STRING", Take());
                            mode = Mode.Start;
                            newlineCounts = false;
                        }
                        else if (c is 10 or 13)
                        {
                            Error("New line in constant"); // loc-ignore
                            _text.Clear();
                            mode = Mode.Start;
                        }
                        else _text.Add((byte)c);
                        break;

                    case Mode.Name:
                        if (c == '\'')
                        {
                            Emit("TOKEN_NAME", Take());
                            mode = Mode.Start;
                            newlineCounts = false;
                        }
                        else if (c is 10 or 13)
                        {
                            Error("New line in constant"); // loc-ignore
                            _text.Clear();
                            mode = Mode.Start;
                        }
                        else if (IsNameCharacter(c))
                        {
                            _text.Add((byte)c);
                            newlineCounts = false;
                        }
                        else
                        {
                            Error("Name identifier can contain only digits and letters"); // loc-ignore
                            _text.Clear();
                            recover = (byte)'\'';
                            mode = Mode.Start;
                            newlineCounts = false;
                        }
                        break;

                    case Mode.Word:
                    {
                        if (_text.Count == 0)
                        {
                            wordStart = _i;
                            kind = Kind.None;
                        }
                        var now = Classify(wordStart, _i);
                        if (c > 0x20 && now != Kind.None)
                        {
                            _text.Add((byte)c);
                            kind = now;
                            break;
                        }
                        mode = Mode.Start;
                        if (kind == Kind.None)
                            Error($"Unexpected '{(char)(byte)c}'"); // loc-ignore
                        else
                        {
                            Flush(kind);
                            if (c > 0x20) again = true;
                        }
                        break;
                    }

                    case Mode.Directive:
                        if (c is 0 or 10 or 13)
                        {
                            if (!inArgument) directiveKind = Array.IndexOf(Directives, Ascii(_directive));
                            if (directiveKind < 0)
                                Error($"Unexpected newline in directive token '{Ascii(_directive)}'"); // loc-ignore
                            else if (directiveKind <= 3 && _argument.Count == 0)
                                Error($"Preprocessor directive '{Ascii(_directive)}' expects an identifier"); // loc-ignore
                            else
                                Directive(directiveKind, Ascii(_argument));
                            ResetDirective(ref inArgument, ref directiveKind);
                            mode = Mode.Start;
                        }
                        else if (c <= 0x20)
                        {
                            if (!inArgument)
                            {
                                directiveKind = Array.IndexOf(Directives, Ascii(_directive));
                                if (directiveKind >= 0) inArgument = true;
                                else
                                {
                                    Error($"Unknown directive token '{Ascii(_directive)}'"); // loc-ignore
                                    ResetDirective(ref inArgument, ref directiveKind);
                                    mode = Mode.Start;
                                }
                            }
                        }
                        else (inArgument ? _argument : _directive).Add((byte)c);
                        break;
                }

                // what ends every character: lines are counted, and the end of the file is noticed
                bool wasLineStart = lineStart;
                lineStart = false;
                if (newlineCounts && !again && c is 10 or 13)
                {
                    if (code[_i] == 13 && _i + 1 < length && code[_i + 1] == 10) _i++;
                    _line++;
                    lineStart = true;
                }
                if (again) lineStart = wasLineStart;
                if (c == 0) break;
            }

            string? open = mode switch
            {
                Mode.BlockComment => "comment",
                Mode.Directive => "preprocessor directive",
                Mode.String => "string",
                Mode.Name => "name",
                _ => null,
            };
            if (open is not null) Error($"Unexpected end of file before {open} at line '{startLine}' was closed"); // loc-ignore
            if (_brackets.Count > 0)
            {
                var last = _brackets[^1];
                Error($"Unexpected end of file found after '{Opens[last.Bracket]}' at line {last.Line}"); // loc-ignore
            }
            if (_conditions.Count > 0) errors.Add(new WccMessage(0, "stray #ifdef")); // loc-ignore
            return _tokens;
        }

        private void ResetDirective(ref bool inArgument, ref int kind)
        {
            _directive.Clear();
            _argument.Clear();
            inArgument = false;
            kind = -1;
        }

        private static bool IsNameCharacter(sbyte c) =>
            c is >= (sbyte)'0' and <= (sbyte)'9' or >= (sbyte)'A' and <= (sbyte)'Z' or >= (sbyte)'a' and <= (sbyte)'z'
                or (sbyte)' ' or (sbyte)'*' or (sbyte)'.' or (sbyte)'_';

        /// <summary>What the characters from <paramref name="start"/> to <paramref name="end"/> make, if they make anything yet.</summary>
        private Kind Classify(int start, int end)
        {
            var kind = Kind.None;
            if (start == end && Singles.Contains((char)code[start])) kind = Kind.Single;
            int length = end - start + 1;
            if (length >= 2 && code[start] == '0' && code[start + 1] is (byte)'x' or (byte)'X')
            {
                bool hex = true;
                for (int i = start + 2; i <= end; i++) hex &= char.IsAsciiHexDigit((char)code[i]);
                if (hex) kind = Kind.Integer;
            }
            else
            {
                bool digits = true;
                for (int i = start; i <= end; i++) digits &= char.IsAsciiDigit((char)code[i]);
                if (digits) kind = Kind.Integer;
            }
            if (IsFloat(start, end)) kind = Kind.Float;
            if (IsWord(start, end)) kind = Kind.Word;
            return kind;
        }

        private bool IsFloat(int start, int end)
        {
            int part = 0, digits = 0;
            for (int i = start; i <= end; i++)
            {
                byte b = code[i];
                if (part == 2) return false;
                if (char.IsAsciiDigit((char)b)) digits++;
                else if (part == 0 && b == '.') part = 1;
                else if (part == 1 && b == 'f') part = 2;
                else return false;
            }
            return part != 0 && digits != 0;
        }

        private bool IsWord(int start, int end)
        {
            string text = Encoding.Latin1.GetString(code, start, end - start + 1);
            if (Keywords.ContainsKey(text)) return true;
            for (int i = 0; i < text.Length; i++)
                if (!(text[i] == '_' || char.IsAsciiLetter(text[i]) || i > 0 && char.IsAsciiDigit(text[i]))) return false;
            return true;
        }

        private void Flush(Kind kind)
        {
            string text = Take();
            switch (kind)
            {
                case Kind.Single:
                {
                    char mark = text[0];
                    int open = Opens.IndexOf(mark), close = Closes.IndexOf(mark);
                    if (open >= 0) _brackets.Add((_line, open));
                    else if (close >= 0)
                    {
                        if (_brackets.Count == 0)
                            Error($"Found unexpected '{mark}'"); // loc-ignore
                        else if (_brackets[^1].Bracket != close)
                        {
                            var top = _brackets[^1];
                            Error($"Unexpected bracket '{mark}', expected '{Closes[top.Bracket]}' to match '{Opens[top.Bracket]}' on line {top.Line}"); // loc-ignore
                        }
                        else _brackets.RemoveAt(_brackets.Count - 1);
                    }
                    Emit($"'{mark}'", text);
                    break;
                }
                case Kind.Integer:
                    if (text is "0x" or "0X") Error("Unfinished integer hex constant"); // loc-ignore
                    else Emit("TOKEN_INTEGER", text);
                    break;
                case Kind.Float:
                    Emit("TOKEN_FLOAT", text);
                    break;
                default:
                    Emit(Keywords.GetValueOrDefault(text, "TOKEN_IDENT"), text);
                    break;
            }
        }

        private string Take()
        {
            string text = Encoding.UTF8.GetString(_text.ToArray());
            _text.Clear();
            return text;
        }

        private static string Ascii(List<byte> bytes) => Encoding.Latin1.GetString(bytes.ToArray());

        private void Emit(string terminal, string text)
        {
            if (_conditions.All(active => active)) _tokens.Add(new WccToken(terminal, text, _line));
        }

        private void Directive(int kind, string argument)
        {
            bool active = _conditions.All(c => c);
            switch (kind)
            {
                case 0:
                    if (active) _defined.Add(argument);
                    break;
                case 1:
                    if (active) _defined.Remove(argument);
                    break;
                case 2:
                    _conditions.Push(_defined.Contains(argument));
                    break;
                case 3:
                    _conditions.Push(!_defined.Contains(argument));
                    break;
                case 4:
                    if (_conditions.Count == 0) errors.Add(new WccMessage(0, "unaccounted #else")); // loc-ignore
                    else _conditions.Push(!_conditions.Pop());
                    break;
                default:
                    if (_conditions.Count == 0) errors.Add(new WccMessage(0, "unaccounted #endif")); // loc-ignore
                    else _conditions.Pop();
                    break;
            }
        }
    }

    /// <summary>The value of a whole-number token as wcc_lite takes it: a leading 0 means octal and 0x hex. Null when it doesn't fit an int.</summary>
    public static long? IntegerValue(string text)
    {
        int radix = 10, start = 0;
        if (text.Length > 1 && text[0] == '0')
        {
            if (text[1] is 'x' or 'X') { radix = 16; start = 2; }
            else { radix = 8; start = 1; }
        }
        long value = 0;
        for (int i = start; i < text.Length; i++)
        {
            int digit = char.IsAsciiHexDigit(text[i]) ? Convert.ToInt32(text[i].ToString(), 16) : 99;
            if (digit >= radix) break; // reading stops at the first digit that doesn't belong: 09 is 0
            value = value * radix + digit;
            if (value > int.MaxValue) return null;
        }
        return value;
    }

    public static float FloatValue(string text) =>
        float.TryParse(text.TrimEnd('f'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0;

    /// <summary>The tokens the compiler's own parser reads, from wcc_lite's.</summary>
    public static List<Token> ForParser(List<WccToken> tokens, int lastLine)
    {
        var result = new List<Token>(tokens.Count + 1);
        foreach (var t in tokens)
            result.Add(t.Terminal switch
            {
                "TOKEN_INTEGER" => new Token(TokKind.Int, t.Text, t.Line, IntegerValue(t.Text) ?? 0),
                "TOKEN_FLOAT" => new Token(TokKind.Float, t.Text, t.Line, 0, FloatValue(t.Text)),
                "TOKEN_STRING" => new Token(TokKind.Str, t.Text, t.Line),
                "TOKEN_NAME" => new Token(TokKind.Name, t.Text, t.Line),
                _ when t.Terminal[0] == '\'' || t.Text.Length > 0 && !char.IsAsciiLetter(t.Text[0]) && t.Text[0] != '_' => new Token(TokKind.Punct, t.Text, t.Line),
                _ => new Token(TokKind.Ident, t.Text, t.Line),
            });
        result.Add(new Token(TokKind.Eof, "", lastLine));
        return result;
    }
}
// loc-ignore-end
