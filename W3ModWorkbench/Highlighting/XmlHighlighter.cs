using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace W3ModWorkbench.Highlighting;

public enum XmlSpanKind { Comment, Punctuation, ElementName, AttributeName, AttributeValue, Entity, CData, Instruction }

public readonly record struct XmlSpan(int Start, int Length, XmlSpanKind Kind);

/// <summary>
/// Finds the parts of an XML text to colour: tags, names, attribute values, comments, CDATA and entities.
/// It reads what's there rather than checking it, so half-typed or broken XML still gets coloured as far as it goes.
/// </summary>
public static class XmlScanner
{
    public static List<XmlSpan> Scan(string text)
    {
        var spans = new List<XmlSpan>(text.Length / 8);
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '<')
            {
                if (At(text, i, "<!--"))
                {
                    int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    spans.Add(new(i, end - i, XmlSpanKind.Comment));
                    i = end;
                }
                else if (At(text, i, "<![CDATA["))
                {
                    int end = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    spans.Add(new(i, end - i, XmlSpanKind.CData));
                    i = end;
                }
                else if (i + 1 < n && text[i + 1] is '?' or '!')
                {
                    int end = text[i + 1] == '?' ? text.IndexOf("?>", i + 2, StringComparison.Ordinal) : text.IndexOf('>', i + 2);
                    end = end < 0 ? n : end + (text[i + 1] == '?' ? 2 : 1);
                    spans.Add(new(i, end - i, XmlSpanKind.Instruction));
                    i = end;
                }
                else
                {
                    i = Tag(text, i, spans);
                }
            }
            else if (c == '&')
            {
                int end = i + 1;
                while (end < n && end - i < 12 && (char.IsLetterOrDigit(text[end]) || text[end] == '#')) end++;
                if (end < n && text[end] == ';')
                {
                    spans.Add(new(i, end + 1 - i, XmlSpanKind.Entity));
                    i = end + 1;
                }
                else i++;
            }
            else i++;
        }
        return spans;
    }

    private static bool At(string text, int i, string what) => string.CompareOrdinal(text, i, what, 0, what.Length) == 0;

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':';

    /// <summary>Reads a start or end tag from its &lt;, returning where the text carries on.</summary>
    private static int Tag(string text, int i, List<XmlSpan> spans)
    {
        int n = text.Length;
        int open = i++;
        if (i < n && text[i] == '/') i++;
        spans.Add(new(open, i - open, XmlSpanKind.Punctuation));

        int nameStart = i;
        while (i < n && IsNameChar(text[i])) i++;
        if (i > nameStart) spans.Add(new(nameStart, i - nameStart, XmlSpanKind.ElementName));

        while (i < n)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '>')
            {
                spans.Add(new(i, 1, XmlSpanKind.Punctuation));
                return i + 1;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '>')
            {
                spans.Add(new(i, 2, XmlSpanKind.Punctuation));
                return i + 2;
            }
            if (c == '<') return i; // an unclosed tag: the next one starts here
            if (c == '=')
            {
                spans.Add(new(i, 1, XmlSpanKind.Punctuation));
                i++;
            }
            else if (c is '"' or '\'')
            {
                int end = text.IndexOf(c, i + 1);
                if (end < 0) end = n - 1;
                spans.Add(new(i, end + 1 - i, XmlSpanKind.AttributeValue));
                i = end + 1;
            }
            else if (IsNameChar(c))
            {
                int start = i;
                while (i < n && IsNameChar(text[i])) i++;
                spans.Add(new(start, i - start, XmlSpanKind.AttributeName));
            }
            else i++;
        }
        return n;
    }
}

/// <summary>Colours XML in an AvalonEdit view. The text is read again shortly after the typing stops.</summary>
public sealed class XmlColorizer : DocumentColorizingTransformer
{
    private readonly TextDocument _document;
    private readonly Palette _palette;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private List<XmlSpan> _spans = [];

    public TextView? View { get; init; }

    public XmlColorizer(TextDocument document, bool dark)
    {
        _document = document;
        _palette = dark ? Palette.Dark : Palette.Light;
        _spans = XmlScanner.Scan(document.Text);
        document.Changed += (_, _) =>
        {
            _timer.Stop();
            _timer.Start();
        };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _spans = XmlScanner.Scan(_document.Text);
            View?.Redraw();
        };
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        // a line of a document swapped in after this was made: its offsets aren't ours
        if (line.IsDeleted || _spans.Count == 0 || CurrentContext.Document != _document) return;
        // the first span that reaches this line
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
            var style = _palette.For(span.Kind);
            ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetForegroundBrush(style.Brush);
                if (style.Italic)
                    element.TextRunProperties.SetTypeface(new Typeface(
                        element.TextRunProperties.Typeface.FontFamily, System.Windows.FontStyles.Italic,
                        element.TextRunProperties.Typeface.Weight, element.TextRunProperties.Typeface.Stretch));
            });
        }
    }

    private sealed record Style(Brush Brush, bool Italic = false);

    private sealed class Palette
    {
        private readonly Dictionary<XmlSpanKind, Style> _styles;

        private Palette(Dictionary<XmlSpanKind, (string, bool)> colours) =>
            _styles = colours.ToDictionary(p => p.Key, p =>
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Value.Item1));
                brush.Freeze();
                return new Style(brush, p.Value.Item2);
            });

        public Style For(XmlSpanKind kind) => _styles[kind];

        public static Palette Dark { get; } = new(new()
        {
            [XmlSpanKind.Comment] = ("#6A9955", true),
            [XmlSpanKind.Punctuation] = ("#808080", false),
            [XmlSpanKind.ElementName] = ("#569CD6", false),
            [XmlSpanKind.AttributeName] = ("#9CDCFE", false),
            [XmlSpanKind.AttributeValue] = ("#CE9178", false),
            [XmlSpanKind.Entity] = ("#D7BA7D", false),
            [XmlSpanKind.CData] = ("#B5CEA8", false),
            [XmlSpanKind.Instruction] = ("#C586C0", false),
        });

        public static Palette Light { get; } = new(new()
        {
            [XmlSpanKind.Comment] = ("#008000", true),
            [XmlSpanKind.Punctuation] = ("#808080", false),
            [XmlSpanKind.ElementName] = ("#800000", false),
            [XmlSpanKind.AttributeName] = ("#E50000", false),
            [XmlSpanKind.AttributeValue] = ("#0451A5", false),
            [XmlSpanKind.Entity] = ("#EE0000", false),
            [XmlSpanKind.CData] = ("#098658", false),
            [XmlSpanKind.Instruction] = ("#AF00DB", false),
        });
    }
}
