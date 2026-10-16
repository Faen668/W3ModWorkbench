using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using W3BundleExplorer.Core;
using W3BundleExplorer.Core.Scripts.Navigation;
using W3BundleExplorer.Highlighting;

namespace W3BundleExplorer;

/// <summary>
/// The popup that shows a name's declaration when the mouse rests on it, coloured like the preview,
/// so you can tell where Ctrl+click will take you before you go.
/// </summary>
internal static class DefinitionPeek
{
    /// <summary>How many declarations the popup shows when several fit.</summary>
    private const int Shown = 3;

    /// <summary>Files read for the popup, by <see cref="ScriptFile.Key"/>, so resting on names doesn't read them again.</summary>
    private static readonly ConcurrentDictionary<string, Task<string[]>> Files = new();

    public sealed record Snippet(ScriptSymbol Symbol, string[] Lines);

    public static string[] SplitLines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    /// <summary>The code of the first few declarations. <paramref name="ownLines"/> gives the open file's own text.</summary>
    public static async Task<List<Snippet>> SnippetsAsync(IReadOnlyList<ScriptSymbol> targets, string? ownKey, Func<string[]> ownLines)
    {
        var result = new List<Snippet>();
        foreach (var target in targets.Take(Shown))
        {
            string[] lines;
            try
            {
                lines = target.File.Key == ownKey ? ownLines() : await LinesOf(target.File);
            }
            catch (Exception)
            {
                continue; // the popup just leaves out a file it can't read
            }
            if (Cut(lines, target) is { Length: > 0 } code) result.Add(new Snippet(target, code));
        }
        return result;
    }

    private static Task<string[]> LinesOf(ScriptFile file)
    {
        if (Files.Count > 200) Files.Clear();
        var task = Files.GetOrAdd(file.Key, _ => Task.Run(() => SplitLines(TextDecoding.Decode(file.Read()))));
        if (task.IsFaulted) Files.TryRemove(file.Key, out _);
        return task;
    }

    /// <summary>
    /// The lines that declare <paramref name="symbol"/>: comments and an annotation right above it, then the
    /// declaration itself; a function's whole signature, an enum's values, a variable's line.
    /// </summary>
    public static string[] Cut(string[] lines, ScriptSymbol symbol)
    {
        int line = symbol.Line - 1;
        if (line < 0 || line >= lines.Length) return [];

        int first = line;
        if (first > 0 && lines[first - 1].TrimStart().StartsWith('@')) first--;
        // comments directly above, up to a few lines, and only whole block comments
        int above = first;
        while (above > 0 && first - above < 8 && IsComment(lines[above - 1])) above--;
        if (above < first && lines[above].TrimStart().StartsWith('*')) above = first;
        first = above;

        int last = line, limit;
        switch (symbol.Kind)
        {
            case SymbolKind.Enum:
                limit = line + 16;
                while (last < lines.Length - 1 && last < limit && !lines[last].Contains('}')) last++;
                break;
            case SymbolKind.Class or SymbolKind.State or SymbolKind.Struct:
                limit = line + 4;
                while (last < lines.Length - 1 && last < limit && !lines[last].Contains('{')) last++;
                break;
            case SymbolKind.Function or SymbolKind.Event:
                limit = line + 8;
                while (last < lines.Length - 1 && last < limit && !lines[last].Contains('{') && !lines[last].Contains(';')) last++;
                break;
        }

        var code = new List<string>();
        for (int i = first; i <= last; i++)
        {
            string text = lines[i].Replace("\t", "    ").TrimEnd();
            // a class or function body starts here: the popup shows only its head
            if (i == last && symbol.Kind != SymbolKind.Enum && text.EndsWith('{'))
            {
                text = text[..^1].TrimEnd();
                if (text.Length == 0) continue;
            }
            if (text.Length > 160) text = text[..159] + "…";
            code.Add(text);
        }
        if (symbol.Kind == SymbolKind.Enum && !lines[last].Contains('}')) code.Add("    …");

        int indent = code.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return code.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToArray();
    }

    private static bool IsComment(string line)
    {
        string t = line.Trim();
        return t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith('*') || t.EndsWith("*/");
    }

    /// <summary>The popup's content: each declaration's code under where it is.</summary>
    public static FrameworkElement Build(IReadOnlyList<Snippet> snippets, int total, bool dark)
    {
        var palette = dark ? WitcherScriptColorizer.Palette.Dark : WitcherScriptColorizer.Palette.Light;
        var panel = new StackPanel();
        for (int i = 0; i < snippets.Count; i++)
        {
            var (symbol, lines) = (snippets[i].Symbol, snippets[i].Lines);
            if (i > 0)
            {
                var rule = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
                rule.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
                panel.Children.Add(rule);
            }

            var where = new TextBlock
            {
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            where.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var icon = new Run(KindGlyph(symbol.Kind) + "  ") { FontFamily = (FontFamily)Application.Current.FindResource("IconFont") };
            icon.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            where.Inlines.Add(icon);
            where.Inlines.Add(new Run(symbol.File.Path) { FontWeight = FontWeights.SemiBold });
            where.Inlines.Add(new Run($" : {symbol.Line}  ·  {symbol.File.Source}"));
            panel.Children.Add(where);

            panel.Children.Add(Code(lines, palette));
        }

        if (total > 1)
        {
            var more = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.75 };
            more.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            more.Text = total > snippets.Count
                ? $"…and {total - snippets.Count} more. Ctrl+click to choose."
                : $"{total} declarations fit. Ctrl+click to choose.";
            panel.Children.Add(more);
        }
        return panel;
    }

    private static string KindGlyph(SymbolKind kind) => kind switch
    {
        SymbolKind.Class or SymbolKind.State or SymbolKind.Struct => "", // document
        SymbolKind.Enum or SymbolKind.EnumMember => "", // list
        SymbolKind.Function or SymbolKind.Event => "", // code
        _ => "", // tag
    };

    /// <summary>The lines coloured with the WitcherScript grammar, as one text block.</summary>
    internal static TextBlock Code(string[] lines, WitcherScriptColorizer.Palette palette)
    {
        var block = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"), FontSize = 12.5 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var grammar = TextMateGrammar.WitcherScript;
        TextMateGrammar.Rule? open = null;
        var tokens = new List<Token>();
        for (int n = 0; n < lines.Length; n++)
        {
            if (n > 0) block.Inlines.Add(new LineBreak());
            string line = lines[n];
            tokens.Clear();
            open = grammar.TokenizeLine(line, open, tokens);

            // later tokens win, as they do in the preview
            var styles = new WitcherScriptColorizer.Style?[line.Length];
            foreach (var t in tokens)
            {
                if (palette.For(t.Scope) is not { } style) continue;
                for (int c = Math.Max(0, t.Start); c < Math.Min(line.Length, t.Start + t.Length); c++) styles[c] = style;
            }
            for (int start = 0; start < line.Length;)
            {
                int end = start + 1;
                while (end < line.Length && styles[end] == styles[start]) end++;
                var run = new Run(line[start..end]);
                if (styles[start] is { } s)
                {
                    run.Foreground = s.Brush;
                    if (s.Italic) run.FontStyle = FontStyles.Italic;
                }
                block.Inlines.Add(run);
                start = end;
            }
        }
        return block;
    }
}
