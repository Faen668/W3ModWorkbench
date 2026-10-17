using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace W3ModWorkbench.Highlighting;

/// <summary>
/// Colours WitcherScript in an AvalonEdit view using the TextMate grammar. Lines are tokenized from the top as
/// they're shown, carrying block comments and strings across lines. An edit throws away the lines from the one
/// changed down, and when it opens or closes a comment or string there, the view is drawn again below it.
/// </summary>
public sealed class WitcherScriptColorizer : DocumentColorizingTransformer
{
    private readonly TextDocument _document;
    private readonly List<List<Token>> _lines = [];

    /// <summary>The comment or string still open at the end of each line read so far.</summary>
    private readonly List<TextMateGrammar.Rule?> _open = [];
    private readonly Palette _palette;

    /// <summary>The view to draw again when an edit changes the colours of the lines below it.</summary>
    public TextView? View { get; init; }

    public WitcherScriptColorizer(TextDocument document, bool dark)
    {
        _palette = dark ? Palette.Dark : Palette.Light;
        _document = document;
        document.Changed += Document_Changed;
    }

    /// <summary>Reads lines from the last one read down to <paramref name="lineNumber"/>.</summary>
    private void Tokenize(int lineNumber)
    {
        var grammar = TextMateGrammar.WitcherScript;
        while (_lines.Count < lineNumber && _lines.Count < _document.LineCount)
        {
            var line = _document.GetLineByNumber(_lines.Count + 1);
            var tokens = new List<Token>();
            _open.Add(grammar.TokenizeLine(_document.GetText(line), _open.Count > 0 ? _open[^1] : null, tokens));
            _lines.Add(tokens);
        }
    }

    private void Document_Changed(object? sender, DocumentChangeEventArgs e)
    {
        int first = _document.GetLineByOffset(Math.Min(e.Offset, _document.TextLength)).LineNumber;
        if (first > _lines.Count) return; // not read yet
        var openBefore = _open[first - 1];
        _lines.RemoveRange(first - 1, _lines.Count - first + 1);
        _open.RemoveRange(first - 1, _open.Count - first + 1);
        Tokenize(first);
        // a comment or string opened or closed on the line, or lines added or taken away: everything below changes
        bool lines = e.InsertedText.IndexOf('\n', 0, e.InsertionLength) >= 0 || e.RemovedText.IndexOf('\n', 0, e.RemovalLength) >= 0;
        if (lines || _open.Count < first || _open[first - 1] != openBefore) View?.Redraw();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.IsDeleted) return;
        Tokenize(line.LineNumber);
        if (line.LineNumber > _lines.Count) return;
        foreach (var t in _lines[line.LineNumber - 1])
        {
            if (_palette.For(t.Scope) is not { } style) continue;
            int start = line.Offset + t.Start;
            ChangeLinePart(start, start + t.Length, element =>
            {
                element.TextRunProperties.SetForegroundBrush(style.Brush);
                if (style.Italic)
                    element.TextRunProperties.SetTypeface(new Typeface(
                        element.TextRunProperties.Typeface.FontFamily, FontStyles.Italic,
                        element.TextRunProperties.Typeface.Weight, element.TextRunProperties.Typeface.Stretch));
            });
        }
    }

    public sealed record Style(Brush Brush, bool Italic = false);

    /// <summary>Colours keyed by scope prefix, after VS Code's Dark+ and Light+ themes. The longest matching prefix wins.</summary>
    public sealed class Palette
    {
        private readonly (string Prefix, Style Style)[] _rules;
        private readonly Dictionary<string, Style?> _cache = [];

        private Palette(params (string, string, bool)[] rules) =>
            _rules = [.. rules.Select(r => (r.Item1, new Style(Freeze(r.Item2), r.Item3))).OrderByDescending(r => r.Item1.Length)];

        public Style? For(string scope)
        {
            if (_cache.TryGetValue(scope, out var s)) return s;
            s = _rules.FirstOrDefault(r => scope.StartsWith(r.Prefix, StringComparison.Ordinal)).Style;
            _cache[scope] = s;
            return s;
        }

        private static SolidColorBrush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static Palette Dark { get; } = new(
            ("comment", "#6A9955", true),
            ("string", "#CE9178", false),
            ("constant.character.escape", "#D7BA7D", false),
            ("constant.other.symbol", "#D7BA7D", false),
            ("constant.numeric", "#B5CEA8", false),
            ("constant.language", "#569CD6", false),
            ("keyword.control", "#C586C0", false),
            ("keyword.other", "#569CD6", false),
            ("storage", "#569CD6", false),
            ("variable.language", "#569CD6", false),
            ("entity.name.function", "#DCDCAA", false),
            ("entity.name.type", "#4EC9B0", false),
            ("support.type", "#4EC9B0", false),
            ("meta.attribute", "#C586C0", false));

        public static Palette Light { get; } = new(
            ("comment", "#008000", true),
            ("string", "#A31515", false),
            ("constant.character.escape", "#EE0000", false),
            ("constant.other.symbol", "#811F3F", false),
            ("constant.numeric", "#098658", false),
            ("constant.language", "#0000FF", false),
            ("keyword.control", "#AF00DB", false),
            ("keyword.other", "#0000FF", false),
            ("storage", "#0000FF", false),
            ("variable.language", "#0000FF", false),
            ("entity.name.function", "#795E26", false),
            ("entity.name.type", "#267F99", false),
            ("support.type", "#267F99", false),
            ("meta.attribute", "#AF00DB", false));
    }
}
