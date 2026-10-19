using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace W3ModWorkbench.Highlighting;

public enum JsonSpanKind { Key, String, Number, Literal, Punctuation, Comment }

public readonly record struct JsonSpan(int Start, int Length, JsonSpanKind Kind);

/// <summary>An object or list in a JSON text: its key (or [n] in a list), where it starts and ends, and a hint of what's in it.</summary>
public sealed class JsonBlock
{
    public required string Name { get; init; }
    public required bool IsList { get; init; }
    /// <summary>Where the key starts (or the bracket, for an item of a list), and the bracket that opens it.</summary>
    public required int Start { get; init; }
    public required int Open { get; init; }
    public int End { get; set; }
    public int Count { get; set; }
    /// <summary>For an object: its "$class" (a converted CR2W file's), or another value that names it.</summary>
    public string? Label { get; set; }
    public JsonBlock? Parent { get; init; }
    public List<JsonBlock> Children { get; } = [];
}

/// <summary>
/// Reads a JSON text for colouring, folding and the outline. It reads what's there rather than checking it, so
/// half-typed or broken JSON is still coloured and folded as far as it goes. Comments (// and /* */) are allowed.
/// </summary>
public static class JsonScanner
{
    public static List<JsonSpan> Scan(string text)
    {
        var spans = new List<JsonSpan>(text.Length / 6);
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '"')
            {
                int end = StringEnd(text, i);
                spans.Add(new(i, end - i, IsKey(text, end) ? JsonSpanKind.Key : JsonSpanKind.String));
                i = end;
            }
            else if (c == '/' && i + 1 < n && text[i + 1] is '/' or '*')
            {
                int end = CommentEnd(text, i);
                spans.Add(new(i, end - i, JsonSpanKind.Comment));
                i = end;
            }
            else if (c is '-' or (>= '0' and <= '9'))
            {
                int start = i++;
                while (i < n && (char.IsAsciiDigit(text[i]) || text[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                spans.Add(new(start, i - start, JsonSpanKind.Number));
            }
            else if (char.IsAsciiLetter(c))
            {
                int start = i;
                while (i < n && char.IsAsciiLetter(text[i])) i++;
                spans.Add(new(start, i - start, JsonSpanKind.Literal));
            }
            else
            {
                if (c is '{' or '}' or '[' or ']' or ':' or ',') spans.Add(new(i, 1, JsonSpanKind.Punctuation));
                i++;
            }
        }
        return spans;
    }

    /// <summary>
    /// The objects and lists, nested as they are. One still open at the end (half-typed) runs to the end of the text.
    /// <paramref name="max"/> stops it reading more than that many.
    /// </summary>
    public static List<JsonBlock> Structure(string text, int max = int.MaxValue)
    {
        var roots = new List<JsonBlock>();
        var stack = new Stack<JsonBlock>();
        int total = 0, n = text.Length;
        string? pendingKey = null;
        int pendingAt = -1;
        // the last key read in each open object, and the item count of each open list, for naming what opens next
        for (int i = 0; i < n; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                int end = StringEnd(text, i);
                if (IsKey(text, end))
                {
                    pendingKey = Unquote(text, i, end);
                    pendingAt = i;
                    // a "$class" (or a name-like key) names the object it's in
                    if (stack.Count > 0 && !stack.Peek().IsList && stack.Peek().Label is null && pendingKey is "$class" or "$format" or "name")
                    {
                        int value = SkipSpace(text, Colon(text, end) + 1);
                        if (value < n && text[value] == '"') stack.Peek().Label = Unquote(text, value, StringEnd(text, value));
                    }
                    if (stack.Count > 0 && !stack.Peek().IsList) stack.Peek().Count++;
                }
                else if (stack.Count > 0 && stack.Peek().IsList && Before(text, i) is '[' or ',') stack.Peek().Count++;
                i = end - 1;
            }
            else if (c == '/' && i + 1 < n && text[i + 1] is '/' or '*') i = CommentEnd(text, i) - 1;
            else if (c is '{' or '[')
            {
                var parent = stack.Count > 0 ? stack.Peek() : null;
                string name;
                int start;
                if (parent is { IsList: true })
                {
                    name = $"[{parent.Count}]";
                    parent.Count++;
                    start = i;
                }
                else if (parent is not null && pendingKey is not null)
                {
                    name = pendingKey;
                    start = pendingAt;
                }
                else
                {
                    name = c == '{' ? "{ }" : "[ ]";
                    start = i;
                }
                pendingKey = null;
                var block = new JsonBlock { Name = name, IsList = c == '[', Start = start, Open = i, End = n, Parent = parent };
                if (++total <= max)
                {
                    if (parent is null) roots.Add(block);
                    else parent.Children.Add(block);
                }
                stack.Push(block);
            }
            else if (c is '}' or ']')
            {
                if (stack.Count > 0) stack.Pop().End = i + 1;
                pendingKey = null;
            }
            else if (stack.Count > 0 && stack.Peek().IsList && !char.IsWhiteSpace(c) && c != ',' && Before(text, i) is '[' or ',')
                stack.Peek().Count++; // a number or literal item
        }
        return roots;
    }

    /// <summary>The character before a position that isn't white space or a comment's.</summary>
    private static char Before(string text, int i)
    {
        for (int k = i - 1; k >= 0; k--)
            if (!char.IsWhiteSpace(text[k])) return text[k];
        return '\0';
    }

    private static int StringEnd(string text, int i)
    {
        int n = text.Length;
        for (int k = i + 1; k < n; k++)
        {
            if (text[k] == '\\') { k++; continue; }
            if (text[k] == '"') return k + 1;
            if (text[k] == '\n') return k; // unclosed: ends with its line
        }
        return n;
    }

    private static int CommentEnd(string text, int i)
    {
        if (text[i + 1] == '/')
        {
            int end = text.IndexOf('\n', i);
            return end < 0 ? text.Length : end;
        }
        int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + 2;
    }

    private static int SkipSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private static int Colon(string text, int afterString)
    {
        int i = SkipSpace(text, afterString);
        return i < text.Length && text[i] == ':' ? i : afterString - 1;
    }

    private static bool IsKey(string text, int afterString)
    {
        int i = SkipSpace(text, afterString);
        return i < text.Length && text[i] == ':';
    }

    private static string Unquote(string text, int start, int end)
    {
        int last = end > start + 1 && text[end - 1] == '"' ? end - 1 : end;
        string inner = text[(start + 1)..last];
        return inner.Contains('\\') ? System.Text.RegularExpressions.Regex.Unescape(inner) : inner;
    }
}

/// <summary>Colours JSON in an AvalonEdit view. The text is read again shortly after the typing stops.</summary>
public sealed class JsonColorizer : DocumentColorizingTransformer
{
    private readonly TextDocument _document;
    private readonly Dictionary<JsonSpanKind, Brush> _brushes;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private List<JsonSpan> _spans;

    public TextView? View { get; init; }

    public JsonColorizer(TextDocument document, bool dark)
    {
        _document = document;
        _brushes = (dark ? Dark : Light).ToDictionary(p => p.Key, p =>
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Value));
            brush.Freeze();
            return (Brush)brush;
        });
        _spans = JsonScanner.Scan(document.Text);
        document.Changed += (_, _) =>
        {
            _timer.Stop();
            _timer.Start();
        };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _spans = JsonScanner.Scan(_document.Text);
            View?.Redraw();
        };
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.IsDeleted || _spans.Count == 0 || CurrentContext.Document != _document) return;
        int lo = 0, hi = _spans.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_spans[mid].Start + _spans[mid].Length <= line.Offset) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < _spans.Count && _spans[i].Start < line.EndOffset; i++)
        {
            var span = _spans[i];
            int start = Math.Max(span.Start, line.Offset), end = Math.Min(span.Start + span.Length, line.EndOffset);
            if (start >= end) continue;
            var brush = _brushes[span.Kind];
            bool italic = span.Kind == JsonSpanKind.Comment;
            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                if (italic)
                    element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, System.Windows.FontStyles.Italic,
                        element.TextRunProperties.Typeface.Weight, element.TextRunProperties.Typeface.Stretch));
            });
        }
    }

    private static readonly Dictionary<JsonSpanKind, string> Dark = new()
    {
        [JsonSpanKind.Key] = "#9CDCFE",
        [JsonSpanKind.String] = "#CE9178",
        [JsonSpanKind.Number] = "#B5CEA8",
        [JsonSpanKind.Literal] = "#569CD6",
        [JsonSpanKind.Punctuation] = "#808080",
        [JsonSpanKind.Comment] = "#6A9955",
    };

    private static readonly Dictionary<JsonSpanKind, string> Light = new()
    {
        [JsonSpanKind.Key] = "#0451A5",
        [JsonSpanKind.String] = "#A31515",
        [JsonSpanKind.Number] = "#098658",
        [JsonSpanKind.Literal] = "#0000FF",
        [JsonSpanKind.Punctuation] = "#808080",
        [JsonSpanKind.Comment] = "#008000",
    };
}
