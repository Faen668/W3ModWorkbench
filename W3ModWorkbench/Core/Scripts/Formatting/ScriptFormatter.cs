using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Scripts.Formatting;

/// <summary>How formatting lays a script out. Each choice is the user's; the defaults are the game's own scripts' style.</summary>
/// <param name="UseTabs">Indent with tabs, as the game's scripts are, or with spaces.</param>
/// <param name="IndentSize">How many spaces an indent is, when not tabs.</param>
/// <param name="CompactColon">var x: int rather than var x : int.</param>
/// <param name="BraceOnOwnLine">A { on a line of its own, as the game's scripts have it, rather than at the end of the line before.</param>
/// <param name="AlignFieldColons">Line up the colons of a class's fields declared one after another.</param>
/// <param name="Annotations">Where an annotation goes against its function or var.</param>
/// <param name="ParenSpacing">Spaces just inside brackets: kept as written, taken out, or put in.</param>
/// <param name="KeepAlignment">
/// Keep the tabs or runs of spaces a declaration uses to line its colon and type up with the lines around it, as the
/// game's own scripts do; off, it's one space (or none, with <paramref name="CompactColon"/>).
/// </param>
public sealed record FormatOptions(
    bool UseTabs = true, int IndentSize = 4, bool CompactColon = false, bool BraceOnOwnLine = true, bool AlignFieldColons = false,
    AnnotationPlacement Annotations = AnnotationPlacement.AsWritten, ParenSpacing ParenSpacing = ParenSpacing.AsWritten,
    bool KeepAlignment = true);

public enum AnnotationPlacement { AsWritten, OwnLine, SameLine }

public enum ParenSpacing { AsWritten, None, Spaced }

/// <summary>The formatted text, or why it wasn't formatted.</summary>
/// <param name="Refused">Why it wasn't formatted, in English, for logs; the app words it from <paramref name="Reason"/>.</param>
/// <param name="Line">The line formatting would have changed more than spaces on, for <see cref="RefusedReason.WouldChangeCode"/>.</param>
public sealed record FormatResult(string Text, bool Changed, string? Refused = null, RefusedReason Reason = RefusedReason.None, int Line = 0);

/// <summary>Why formatting left a file as it was.</summary>
public enum RefusedReason { None, Unreadable, WouldChangeCode }

/// <summary>
/// Lays out WitcherScript: indentation by braces (and a level more for a case's statements, an if's single statement and
/// a line that carries on a bracket or an operator), one space around operators and after commas, keywords followed by a
/// space, the colon of a declaration as asked, braces where asked, trailing spaces and runs of blank lines gone.
/// It changes only the spaces and line breaks between the code's tokens: comments, strings and every token stay as
/// they are, and the result is checked for that before it's given back. A text that doesn't pass is refused.
/// </summary>
public static class ScriptFormatter
{
    public static FormatResult Format(string text, FormatOptions? options = null)
    {
        options ??= new FormatOptions();
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        string source = text.Replace("\r\n", "\n");
        List<Token> before;
        try
        {
            before = Tokenize(source);
        }
        catch (FormatException ex)
        {
            return new FormatResult(text, false, ex.Message, RefusedReason.Unreadable);
        }
        string laidOut = Braces(source, before, options);
        string spaced = Lines(laidOut, options);
        if (options.AlignFieldColons) spaced = AlignColons(spaced, options);
        string result = spaced.TrimEnd('\n') + "\n";
        if (!source.EndsWith('\n')) result = result.TrimEnd('\n');
        // nothing but spaces and line breaks may differ
        if (FirstDifference(before, Tokenize(result)) is { } at)
        {
            int line = source.Take(before[Math.Min(at, before.Count - 1)].Start).Count(c => c == '\n') + 1;
            return new FormatResult(text, false, L.T("ScriptFormatter.FormattingWouldHaveChanged", line),
                RefusedReason.WouldChangeCode, line);
        }
        result = result.Replace("\n", newline);
        return new FormatResult(result, result != text);
    }

    // ---------------------------------------------------------------- tokens

    private enum Kind { Word, Number, String, Name, Punct, LineComment, BlockComment }

    private readonly record struct Token(Kind Kind, string Text, int Start);

    private static readonly string[] Operators =
        ["<<=", ">>=", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "++", "--", "<<", ">>", "->", "::"];

    /// <param name="line">A single line: a /* comment that carries on past its end is a comment to the end of the line.</param>
    private static List<Token> Tokenize(string s, bool line = false)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int start = i;
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                tokens.Add(new Token(Kind.LineComment, s[start..i].TrimEnd(), start));
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0 && !line) throw new FormatException(L.T("ScriptFormatter.CommentIsntClosed"));
                i = end < 0 ? s.Length : end + 2;
                tokens.Add(new Token(Kind.BlockComment, s[start..i], start));
            }
            else if (c is '"' or '\'')
            {
                i++;
                while (i < s.Length && s[i] != c && s[i] != '\n') i += s[i] == '\\' ? 2 : 1;
                if (i >= s.Length || s[i] != c) throw new FormatException(L.T("ScriptFormatter.StringNameIsntClosed"));
                i++;
                tokens.Add(new Token(c == '"' ? Kind.String : Kind.Name, s[start..i], start));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                tokens.Add(new Token(Kind.Word, s[start..i], start));
            }
            else if (char.IsDigit(c) || c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.')) i++;
                tokens.Add(new Token(Kind.Number, s[start..i], start));
            }
            else
            {
                string? op = Operators.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
                i += op?.Length ?? 1;
                tokens.Add(new Token(Kind.Punct, s[start..i], start));
            }
        }
        return tokens;
    }

    /// <summary>
    /// Where two texts' tokens first differ, or null when they're the same in the same order (comments compared
    /// without the spaces their lines start and end with).
    /// </summary>
    private static int? FirstDifference(List<Token> a, List<Token> b)
    {
        int n = Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
            if (a[i].Kind != b[i].Kind || Normal(a[i]) != Normal(b[i])) return i;
        return a.Count == b.Count ? null : n;
    }

    private static string Normal(Token t) => t.Kind == Kind.BlockComment
        ? string.Join("\n", t.Text.Replace("\r", "").Split('\n').Select(l => l.Trim()))
        : t.Text;

    // ---------------------------------------------------------------- braces and annotations

    /// <summary>Moves { onto a line of its own or up onto the line before, and annotations onto or off their line, as asked.</summary>
    private static string Braces(string source, List<Token> tokens, FormatOptions options)
    {
        // annotations first, so a function they're split from gets its brace like any other
        var lines = source.Split('\n').ToList();
        if (options.Annotations != AnnotationPlacement.AsWritten) lines = Annotations(lines, options.Annotations);
        var output = new List<string>();
        // what's commented out is left as it is, code or not
        var commented = InBlockComment(lines);
        bool aboveCommented = false;
        for (int index = 0; index < lines.Count; index++)
        {
            string line = lines[index];
            if (commented[index])
            {
                output.Add(line);
                aboveCommented = true;
                continue;
            }
            bool joinable = !aboveCommented;
            aboveCommented = false;
            string trimmed = line.TrimEnd();
            string code = CodePart(trimmed);
            if (options.BraceOnOwnLine)
            {
                // header { at the end of a line: the { goes below
                if (code.EndsWith('{') && code.Trim().Length > 1 && !code.TrimStart().StartsWith("//", StringComparison.Ordinal) && Unquoted(code))
                {
                    string head = code[..code.LastIndexOf('{')].TrimEnd();
                    output.Add(head + trimmed[code.Length..]);
                    output.Add(Indent(line) + "{");
                    continue;
                }
            }
            else if (code.Trim() == "{" && output.Count > 0 && joinable)
            {
                // a { alone goes up to its header, unless the line above is a comment or ends a statement
                int prev = output.Count - 1;
                string above = CodePart(output[prev].TrimEnd());
                if (above.Trim().Length > 0 && !above.TrimEnd().EndsWith(';') && !above.TrimEnd().EndsWith('}') && !above.TrimEnd().EndsWith('{')
                    && above.Length == output[prev].TrimEnd().Length && !above.TrimStart().StartsWith('@'))
                {
                    output[prev] = output[prev].TrimEnd() + " {" + trimmed[(trimmed.IndexOf('{') + 1)..];
                    continue;
                }
            }
            output.Add(line);
        }
        return string.Join("\n", output);
    }

    /// <summary>
    /// Which lines are part of a /* comment that spans lines: from the line it opens on to the one it closes on.
    /// Quotes are respected, so a /* in a string doesn't count.
    /// </summary>
    private static bool[] InBlockComment(IReadOnlyList<string> lines)
    {
        var result = new bool[lines.Count];
        bool open = false;
        for (int n = 0; n < lines.Count; n++)
        {
            string line = lines[n];
            bool touched = open;
            char? quote = null;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (open)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { open = false; i++; }
                    continue;
                }
                if (quote is not null) { if (c == '\\') i++; else if (c == quote) quote = null; continue; }
                if (c is '"' or '\'') quote = c;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { open = true; i++; }
            }
            // a comment that opens and closes on one line leaves the line alone; one that spans lines takes them all
            result[n] = touched || open;
        }
        return result;
    }

    private static List<string> Annotations(List<string> lines, AnnotationPlacement placement)
    {
        var output = new List<string>();
        var commented = InBlockComment(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (commented[i] || i + 1 < lines.Count && commented[i + 1]) { output.Add(line); continue; }
            string code = CodePart(line.TrimEnd());
            var annotation = System.Text.RegularExpressions.Regex.Match(code, @"^(\s*)(@\w+\s*\(\s*\w+\s*\))\s*(.*)$");
            if (!annotation.Success) { output.Add(line); continue; }
            string rest = annotation.Groups[3].Value + line.TrimEnd()[code.Length..];
            if (placement == AnnotationPlacement.OwnLine && rest.Trim().Length > 0)
            {
                output.Add(annotation.Groups[1].Value + annotation.Groups[2].Value);
                output.Add(annotation.Groups[1].Value + rest.Trim());
            }
            else if (placement == AnnotationPlacement.SameLine && rest.Trim().Length == 0 && i + 1 < lines.Count && lines[i + 1].Trim().Length > 0
                     && !lines[i + 1].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                output.Add(annotation.Groups[1].Value + annotation.Groups[2].Value + " " + lines[i + 1].Trim());
                i++;
            }
            else output.Add(line);
        }
        return output;
    }

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    /// <summary>A line without its // comment: the code part, quotes respected.</summary>
    private static string CodePart(string line)
    {
        char? quote = null;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote is not null) { if (c == '\\') i++; else if (c == quote) quote = null; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i].TrimEnd();
        }
        return line;
    }

    private static bool Unquoted(string code) => code.Count(c => c == '"') % 2 == 0 && code.Count(c => c == '\'') % 2 == 0;

    // ---------------------------------------------------------------- lines

    private static readonly HashSet<string> SpacedKeywords = new(StringComparer.Ordinal) { "if", "for", "while", "switch", "return", "case", "new", "delete", "else", "do" };
    private static readonly HashSet<string> Binary = new(StringComparer.Ordinal)
        { "=", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "+", "-", "*", "/", "%", "<", ">", "&", "|", "^", "?", "<<", ">>", "<<=", ">>=" };

    /// <summary>Each line indented by where it is, and its tokens spaced.</summary>
    private static string Lines(string source, FormatOptions options)
    {
        var output = new StringBuilder();
        int depth = 0, paren = 0;
        bool inBlockComment = false, blank = false;
        string? previousCode = null;
        // the brace depth of each switch's body, to put a case's statements a level further in
        var switches = new Stack<int>();
        bool switchPending = false;
        foreach (string raw in source.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (inBlockComment)
            {
                output.Append(line).Append('\n');
                if (line.Contains("*/")) inBlockComment = false;
                continue;
            }
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                // one blank line at most, and none straight after { or before }
                if (!blank && previousCode is not null && !previousCode.EndsWith('{')) output.Append('\n');
                blank = true;
                continue;
            }
            if (blank && trimmed.StartsWith('}') && output.Length > 0 && output[^1] == '\n' && output.Length > 1 && output[^2] == '\n') output.Length--;
            blank = false;

            var tokens = Tokenize(trimmed, line: true);
            // the line's code, without its comments: what decides whether the next line carries it on
            string code = string.Join(" ", tokens.Where(t => t.Kind is not (Kind.LineComment or Kind.BlockComment)).Select(t => t.Text));
            int level = depth;
            if (trimmed.StartsWith('}')) level--;
            bool label = tokens.Count > 0 && tokens[0].Text is "case" or "default" && code.TrimEnd().EndsWith(':') || tokens.Count > 1 && tokens[0].Text == "default" && tokens[1].Text == ":";
            if (switches.Count > 0 && depth == switches.Peek() && !label && !trimmed.StartsWith('}')) level++;
            if (paren > 0) level += 1;
            else if (previousCode is not null && !trimmed.StartsWith('{') && Continues(previousCode)) level += 1;
            output.Append(IndentString(Math.Max(0, level), options)).Append(Space(tokens, trimmed, options)).Append('\n');

            // what this line leaves open
            foreach (var t in tokens)
            {
                if (t.Kind != Kind.Punct) continue;
                switch (t.Text)
                {
                    case "{":
                        depth++;
                        if (switchPending) { switches.Push(depth); switchPending = false; }
                        break;
                    case "}":
                        if (switches.Count > 0 && depth == switches.Peek()) switches.Pop();
                        depth--;
                        break;
                    case "(" or "[": paren++; break;
                    case ")" or "]": paren = Math.Max(0, paren - 1); break;
                }
            }
            if (tokens.Any(t => t.Kind == Kind.Word && t.Text == "switch")) switchPending = true;
            if (tokens.Count > 0 && tokens[^1].Kind == Kind.BlockComment && !tokens[^1].Text.EndsWith("*/")) inBlockComment = true;
            if (line.Contains("/*") && !line[line.LastIndexOf("/*", StringComparison.Ordinal)..].Contains("*/")) inBlockComment = true;
            if (code.Length > 0) previousCode = code.Trim();
        }
        return output.ToString();
    }

    /// <summary>
    /// Whether the line after this one carries it on: it ends with an operator, or it's an if, else, for or while
    /// whose single statement is on the next line.
    /// </summary>
    private static bool Continues(string code)
    {
        if (code.EndsWith(';') || code.EndsWith('{') || code.EndsWith('}') || code.EndsWith(':') || code.StartsWith('@')) return false;
        string last = code.Length > 1 && Operators.Any(o => code.EndsWith(o, StringComparison.Ordinal)) ? Operators.First(o => code.EndsWith(o, StringComparison.Ordinal)) : code[^1].ToString();
        if (Binary.Contains(last) || last == ",") return last != "," || false;
        var first = System.Text.RegularExpressions.Regex.Match(code, @"^(else\s+if|if|else|for|while)\b");
        return first.Success && (code.EndsWith(')') || code == "else");
    }

    private static string IndentString(int level, FormatOptions options) =>
        options.UseTabs ? new string('\t', level) : new string(' ', level * Math.Max(1, options.IndentSize));

    /// <summary>A line's tokens with the spaces between them as the rules say. Comments and literals are kept as they are.</summary>
    private static string Space(List<Token> tokens, string line, FormatOptions options)
    {
        if (tokens.Count == 0) return line;
        var sb = new StringBuilder();
        bool ternary = false;
        int generic = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (i > 0)
            {
                var p = tokens[i - 1];
                string between = line[(p.Start + p.Text.Length)..t.Start];
                sb.Append(Gap(tokens, i, between, ref ternary, ref generic, options));
            }
            if (t.Text == "?" && t.Kind == Kind.Punct) ternary = true;
            sb.Append(t.Kind == Kind.LineComment ? t.Text.TrimEnd() : t.Text);
        }
        return sb.ToString();
    }

    /// <summary>The space between token i-1 and token i.</summary>
    private static string Gap(List<Token> tokens, int i, string written, ref bool ternary, ref int generic, FormatOptions options)
    {
        var p = tokens[i - 1];
        var t = tokens[i];
        bool hadSpace = written.Length > 0;
        if (t.Kind is Kind.LineComment or Kind.BlockComment || p.Kind == Kind.BlockComment) return hadSpace ? " " : "";

        // array<int>: no spaces around a generic's brackets
        if (t.Text == "<" && p.Kind == Kind.Word && p.Text == "array") { generic++; return ""; }
        if (p.Text == "<" && generic > 0) return options.ParenSpacing == ParenSpacing.Spaced ? " " : options.ParenSpacing == ParenSpacing.None ? "" : hadSpace ? " " : "";
        // two generics closing together keep a space between: >> would be another operator
        if (t.Text == ">" && generic > 0 && p.Text == ">") { generic--; return " "; }
        if (t.Text == ">" && generic > 0) { generic--; return options.ParenSpacing == ParenSpacing.Spaced ? " " : options.ParenSpacing == ParenSpacing.None ? "" : hadSpace ? " " : ""; }
        if (p.Text == ">" && generic >= 0 && t.Text is ">" or ";" or ")" or "," or ".") return "";

        // inside brackets
        if (p.Text is "(" or "[" && p.Kind == Kind.Punct)
            return t.Text is ")" or "]" ? "" : options.ParenSpacing switch { ParenSpacing.None => "", ParenSpacing.Spaced => " ", _ => hadSpace ? " " : "" };
        if (t.Text is ")" or "]" && t.Kind == Kind.Punct)
            return options.ParenSpacing switch { ParenSpacing.None => "", ParenSpacing.Spaced => " ", _ => hadSpace ? " " : "" };

        if (t.Text is "," or ";") return "";
        if (p.Text is "," or ";") return " ";
        if (t.Text == "." || p.Text == ".") return "";
        if (p.Text == "@") return "";
        if (t.Text is "++" or "--") return "";
        if (p.Text is "++" or "--") return t.Kind is Kind.Word or Kind.Number ? "" : " ";
        if (p.Text == "!" ) return "";

        // ( after a keyword: if (, a call: Foo(
        if (t.Text is "(" or "[")
        {
            if (p.Kind == Kind.Word && SpacedKeywords.Contains(p.Text)) return " ";
            if (p.Kind == Kind.Word || p.Text is ")" or "]") return "";
            return Unary(tokens, i - 1) ? "" : hadSpace || IsBinary(p) ? " " : "";
        }

        // a declaration's colon, a case label's, a ternary's
        if (t.Text == ":" || p.Text == ":")
        {
            int colon = t.Text == ":" ? i : i - 1;
            bool caseLabel = tokens[0].Text is "case" or "default";
            if (caseLabel && !ternary) return t.Text == ":" ? "" : " ";
            if (ternary) return " ";
            // lined up with the declarations around it: kept, when asked
            bool aligning = written.Contains('\t') || written.Length > 1;
            if (options.KeepAlignment && aligning && !options.AlignFieldColons) return written;
            if (t.Text == ":") return options.CompactColon ? "" : " ";
            return " ";
        }

        // a cast, (CActor)target: the space after it is the writer's to choose
        if (p.Text == ")" && t.Kind is Kind.Word or Kind.Number && IsCast(tokens, i - 1)) return hadSpace ? " " : "";

        if (IsBinary(t)) return Unary(tokens, i) ? (p.Kind == Kind.Punct && p.Text is "(" or "[" ? "" : " ") : " ";
        if (IsBinary(p)) return Unary(tokens, i - 1) ? "" : " ";
        if (p.Kind is Kind.Word or Kind.Number or Kind.String or Kind.Name || p.Text is ")" or "]" or "}") return " ";
        return hadSpace ? " " : "";
    }

    private static bool IsBinary(Token t) => t.Kind == Kind.Punct && Binary.Contains(t.Text);

    /// <summary>
    /// Whether the ) at <paramref name="close"/> ends a cast, (Type): a type name alone in brackets, where a value
    /// starts (after an operator, a bracket or return), not an if's condition or a call's argument.
    /// </summary>
    private static bool IsCast(List<Token> tokens, int close)
    {
        if (close < 2 || tokens[close - 1].Kind != Kind.Word || tokens[close - 2].Text != "(") return false;
        if (close == 2) return true;
        var before = tokens[close - 3];
        return before.Kind == Kind.Punct && before.Text is not (")" or "]") || before.Kind == Kind.Word && before.Text == "return";
    }

    /// <summary>Whether a + or - (or *, &) is a sign rather than an operator: at the start, or after an operator or bracket.</summary>
    private static bool Unary(List<Token> tokens, int i)
    {
        var t = tokens[i];
        if (t.Text is not ("-" or "+")) return false;
        if (i == 0) return true;
        var p = tokens[i - 1];
        return p.Kind == Kind.Punct && p.Text is not (")" or "]") || p.Kind == Kind.Word && p.Text is "return" or "case";
    }

    // ---------------------------------------------------------------- aligning

    /// <summary>The colons of fields declared one after another in a class or struct, lined up.</summary>
    private static string AlignColons(string text, FormatOptions options)
    {
        var lines = text.Split('\n');
        var commented = InBlockComment(lines);
        var field = new System.Text.RegularExpressions.Regex(@"^(\s*(?:(?:private|protected|public|editable|saved|const|inlined|import)\s+)*var\s+[\w\s,]*?\w)\s*:\s*(.*)$");
        int i = 0;
        while (i < lines.Length)
        {
            int j = i;
            while (j < lines.Length && !commented[j] && field.IsMatch(lines[j]) && Indent(lines[j]) == Indent(lines[i])) j++;
            if (j - i >= 2)
            {
                int width = Enumerable.Range(i, j - i).Max(k => field.Match(lines[k]).Groups[1].Value.Length);
                for (int k = i; k < j; k++)
                {
                    var m = field.Match(lines[k]);
                    string head = m.Groups[1].Value.PadRight(width);
                    lines[k] = options.CompactColon ? head.TrimEnd() + ":" + new string(' ', width - m.Groups[1].Value.Length + 1) + m.Groups[2].Value : head + " : " + m.Groups[2].Value;
                }
                i = j;
            }
            else i = Math.Max(i + 1, j);
        }
        return string.Join("\n", lines);
    }
}
