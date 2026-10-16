using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace W3BundleExplorer.Highlighting;

/// <summary>
/// Colours WitcherScript in an AvalonEdit view using the TextMate grammar. The text is read-only,
/// so every line is tokenized once up front, carrying block comments and strings across lines.
/// </summary>
public sealed class WitcherScriptColorizer : DocumentColorizingTransformer
{
    private readonly List<Token>[] _lines;
    private readonly Palette _palette;

    public WitcherScriptColorizer(TextDocument document, bool dark)
    {
        _palette = dark ? Palette.Dark : Palette.Light;
        var grammar = TextMateGrammar.WitcherScript;
        _lines = new List<Token>[document.LineCount];
        TextMateGrammar.Rule? open = null;
        foreach (var line in document.Lines)
        {
            var tokens = new List<Token>();
            open = grammar.TokenizeLine(document.GetText(line), open, tokens);
            _lines[line.LineNumber - 1] = tokens;
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.LineNumber > _lines.Length) return;
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
            _rules = rules.Select(r => (r.Item1, new Style(Freeze(r.Item2), r.Item3)))
                          .OrderByDescending(r => r.Item1.Length).ToArray();

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
