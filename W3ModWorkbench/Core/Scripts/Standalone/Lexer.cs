using System.Globalization;
using System.Text;

namespace W3ModWorkbench.Core.Scripts.Standalone;

public enum TokKind { Ident, Int, Float, Str, Name, Punct, Eof }

public sealed record Token(TokKind Kind, string Text, int Line, long Int = 0, float Float = 0);

/// <summary>Raised for a file that can't be read as WitcherScript; carries the line it stopped at.</summary>
public sealed class ScriptSyntaxException(string message, int line) : Exception(message)
{
    public int Line { get; } = line;
}

public static class Lexer
{
    // longest first, so "+=" wins over "+"
    private static readonly string[] Puncts =
    [
        "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "==", "!=", "<=", ">=", "&&", "||",
        "{", "}", "(", ")", "[", "]", ";", ":", ",", ".", "=", "<", ">", "+", "-", "*", "/", "%", "!", "&", "|", "^", "~", "?", "@",
    ];

    /// <param name="lenient">Skip characters the language doesn't have, for files only read for their declarations. A decompiled
    /// default can hold a bare resource path (<c>resdef:ai\reactions/x</c>) that is never compiled.</param>
    public static List<Token> Tokenize(string source, bool lenient = false)
    {
        var tokens = new List<Token>();
        int pos = 0, line = 1, n = source.Length;
        while (pos < n)
        {
            char c = source[pos];
            if (c == '\n') { line++; pos++; continue; }
            if (char.IsWhiteSpace(c) || c > 127 && !char.IsLetter(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < n && source[pos + 1] == '/')
            {
                while (pos < n && source[pos] != '\n') pos++;
                continue;
            }
            if (c == '/' && pos + 1 < n && source[pos + 1] == '*')
            {
                pos += 2;
                while (pos < n && !(source[pos] == '*' && pos + 1 < n && source[pos + 1] == '/'))
                {
                    if (source[pos] == '\n') line++;
                    pos++;
                }
                pos += 2;
                continue;
            }

            int start = pos, tokenLine = line;
            if (IsIdentStart(c))
            {
                while (pos < n && IsIdentPart(source[pos])) pos++;
                tokens.Add(new Token(TokKind.Ident, source[start..pos], tokenLine));
            }
            else if (char.IsAsciiDigit(c) || c == '.' && pos + 1 < n && char.IsAsciiDigit(source[pos + 1]))
            {
                tokens.Add(LexNumber(source, ref pos, tokenLine));
            }
            else if (c == '"' || c == '\'')
            {
                string text = LexQuoted(source, ref pos, c, ref line);
                tokens.Add(new Token(c == '"' ? TokKind.Str : TokKind.Name, text, tokenLine));
            }
            else
            {
                string? punct = Puncts.FirstOrDefault(p => string.CompareOrdinal(source, pos, p, 0, p.Length) == 0);
                if (punct is null)
                {
                    if (lenient)
                    {
                        pos++;
                        continue;
                    }
                    throw new ScriptSyntaxException($"unexpected character '{c}'", line);
                }
                pos += punct.Length;
                tokens.Add(new Token(TokKind.Punct, punct, tokenLine));
            }
        }
        tokens.Add(new Token(TokKind.Eof, "", line));
        return tokens;
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static Token LexNumber(string source, ref int pos, int line)
    {
        int start = pos, n = source.Length;
        if (source[pos] == '0' && pos + 1 < n && (source[pos + 1] is 'x' or 'X'))
        {
            pos += 2;
            while (pos < n && char.IsAsciiHexDigit(source[pos])) pos++;
            long hex = long.Parse(source.AsSpan(start + 2, pos - start - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Token(TokKind.Int, source[start..pos], line, hex);
        }

        bool isFloat = false;
        while (pos < n && (char.IsAsciiDigit(source[pos]) || source[pos] == '.'))
        {
            isFloat |= source[pos] == '.';
            pos++;
        }
        // an exponent, as in 1e-5
        if (pos < n && (source[pos] is 'e' or 'E') && pos + 1 < n
            && (char.IsAsciiDigit(source[pos + 1]) || source[pos + 1] is '+' or '-' && pos + 2 < n && char.IsAsciiDigit(source[pos + 2])))
        {
            isFloat = true;
            pos += 2;
            while (pos < n && char.IsAsciiDigit(source[pos])) pos++;
        }
        string digits = source[start..pos];
        if (pos < n && source[pos] is 'f' or 'F')
        {
            pos++;
            isFloat = true;
        }
        string text = source[start..pos];
        if (isFloat)
        {
            if (!float.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                throw new ScriptSyntaxException($"bad float '{digits}'", line);
            return new Token(TokKind.Float, text, line, 0, f);
        }
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long i))
            throw new ScriptSyntaxException($"bad int '{digits}'", line);
        return new Token(TokKind.Int, text, line, i);
    }

    /// <summary>
    /// A string or name literal. A backslash and the character after it are kept as written (the game reads the escapes
    /// when it runs), except that a backslash before the quote just gives the quote, as wcc_lite does.
    /// </summary>
    private static string LexQuoted(string source, ref int pos, char quote, ref int line)
    {
        var text = new StringBuilder();
        pos++;
        while (pos < source.Length && source[pos] != quote)
        {
            char c = source[pos];
            if (c == '\\' && pos + 1 < source.Length)
            {
                if (source[pos + 1] == '\n') line++;
                if (source[pos + 1] == quote) text.Append(quote);
                else text.Append(c).Append(source[pos + 1]);
                pos += 2;
                continue;
            }
            if (c == '\n') line++;
            text.Append(c);
            pos++;
        }
        if (pos >= source.Length) throw new ScriptSyntaxException("unterminated literal", line);
        pos++;
        return text.ToString();
    }
}
