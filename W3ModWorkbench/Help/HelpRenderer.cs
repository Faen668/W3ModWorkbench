using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Help;

/// <summary>
/// Draws a help page's markup (see Help\Pages\README.md) as WPF elements: headings, paragraphs, lists, notes, code with
/// a Copy button, screenshots that open large, tables, cards, links to other pages, keys and search matches picked out.
/// </summary>
public sealed partial class HelpRenderer(FrameworkElement owner, Action<string> openPage, Action<BitmapSource, string> openImage)
{
    private static readonly FontFamily Mono = new(L.T("HelpRenderer.CascadiaMonoConsolasCourier"));

    /// <summary>The words a search looked for, picked out wherever they're in the text.</summary>
    public IReadOnlyList<string> Highlight { get; set; } = [];

    /// <summary>Each ## heading drawn, by its text, for "On this page" to scroll to.</summary>
    public Dictionary<string, FrameworkElement> Anchors { get; } = new(StringComparer.Ordinal);

    /// <summary>The first match of the search drawn, to scroll to.</summary>
    public FrameworkElement? FirstMatch { get; private set; }

    private bool _dark;

    public void Render(string body, Panel into)
    {
        into.Children.Clear();
        Anchors.Clear();
        FirstMatch = null;
        _dark = owner.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush text
            && 0.299 * text.Color.R + 0.587 * text.Color.G + 0.114 * text.Color.B > 128;
        var lines = body.Replace("\r", "").Split('\n');
        var paragraph = new StringBuilder();

        void Flush()
        {
            if (paragraph.Length == 0) return;
            into.Children.Add(Text(paragraph.ToString(), new Thickness(0, 0, 0, 14)));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                string info = trimmed[3..].Trim();
                var code = new StringBuilder();
                while (++i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                    code.Append(lines[i]).Append('\n');
                into.Children.Add(CodeBlock(code.ToString().TrimEnd('\n'), info));
            }
            else if (trimmed.StartsWith(":::cards", StringComparison.Ordinal))
            {
                Flush();
                var cards = new List<string>();
                while (++i < lines.Length && !lines[i].Trim().StartsWith(":::", StringComparison.Ordinal))
                    if (lines[i].Trim().StartsWith("- ", StringComparison.Ordinal)) cards.Add(lines[i].Trim()[2..]);
                into.Children.Add(Cards(cards));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                var heading = Heading(line[3..].Trim(), 21, new Thickness(0, 18, 0, 10));
                Anchors.TryAdd(line[3..].Trim(), heading);
                into.Children.Add(heading);
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                into.Children.Add(Heading(line[4..].Trim(), 16.5, new Thickness(0, 10, 0, 8)));
            }
            else if (trimmed.StartsWith("![", StringComparison.Ordinal) && Regex.Match(trimmed, @"^!\[(.*)\]\((.+)\)$") is { Success: true } image)
            {
                Flush();
                into.Children.Add(Picture(image.Groups[2].Value.Trim(), image.Groups[1].Value.Trim()));
            }
            else if (trimmed.StartsWith('|'))
            {
                Flush();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(lines[i].Trim());
                i--;
                into.Children.Add(Table(rows));
            }
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                Flush();
                var note = new StringBuilder(trimmed.Length > 1 ? trimmed[2..] : "");
                while (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith('>'))
                {
                    string more = lines[++i].Trim();
                    note.Append(more.Length > 1 ? " " + more[2..] : "\n");
                }
                into.Children.Add(Callout(note.ToString()));
            }
            else if (Regex.Match(line, @"^(\s*)(- |\d+\. )(.*)$") is { Success: true } item)
            {
                Flush();
                int depth = item.Groups[1].Value.Length / 2;
                bool numbered = item.Groups[2].Value != "- ";
                into.Children.Add(ListItem(item.Groups[3].Value, depth, numbered ? item.Groups[2].Value.TrimEnd(' ', '.') : null,
                    last: i + 1 >= lines.Length || !Regex.IsMatch(lines[i + 1], @"^\s*(- |\d+\. )")));
            }
            else if (trimmed.Length == 0) Flush();
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(trimmed);
            }
        }
        Flush();
    }

    // ---------------------------------------------------------------- blocks

    private FrameworkElement Heading(string text, double size, Thickness margin)
    {
        var block = new TextBlock { FontSize = size, FontWeight = FontWeights.SemiBold, Margin = margin, TextWrapping = TextWrapping.Wrap };
        _matched = false;
        AddInlines(block.Inlines, text);
        if (_matched) FirstMatch ??= block;
        return block;
    }

    private TextBlock Text(string markup, Thickness margin)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = margin, FontSize = 14.5, LineHeight = 23 };
        _matched = false;
        AddInlines(block.Inlines, markup);
        if (_matched) FirstMatch ??= block;
        return block;
    }

    private FrameworkElement ListItem(string markup, int depth, string? number, bool last)
    {
        var row = new Grid { Margin = new Thickness(6 + depth * 22, 0, 0, last ? 14 : 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(number is null ? 18 : 30) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        if (number is null)
            row.Children.Add(new TextBlock { Text = depth == 0 ? "•" : "◦", Opacity = 0.7, FontSize = 14.5, Margin = new Thickness(2, 1, 0, 0) });
        else
        {
            // a numbered step: its number in a small accent circle
            var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
                Child = new TextBlock { Text = number, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
            row.Children.Add(badge);
        }
        var text = Text(markup, new Thickness(0));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A note, a tip or a warning: "&gt; tip: …", "&gt; warning: …", or just "&gt; …".</summary>
    private Border Callout(string markup)
    {
        string kind = "note";
        var m = Regex.Match(markup, @"^(note|tip|warning|important):\s*", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            kind = m.Groups[1].Value.ToLowerInvariant();
            markup = markup[m.Length..];
        }
        var (glyph, title, brush) = kind switch
        {
            "tip" => ("\uE82F", L.T("HelpRenderer.Tip"), "SystemFillColorSuccessBrush"),
            "warning" or "important" => ("\uE7BA", L.T("HelpRenderer.WatchOut"), "SystemFillColorCautionBrush"),
            _ => ("\uE946", L.T("HelpRenderer.Note"), "AccentTextFillColorPrimaryBrush"),
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)owner.FindResource("IconFont"), FontSize = 17, Margin = new Thickness(0, 2, 12, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        row.Children.Add(icon);
        var stack = new StackPanel();
        var heading = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 2) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, brush);
        stack.Children.Add(heading);
        foreach (string part in markup.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            stack.Children.Add(Text(part.Trim(), new Thickness(0, 0, 0, 4)));
        Grid.SetColumn(stack, 1);
        row.Children.Add(stack);
        var border = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(3, 1, 1, 1), Padding = new Thickness(14, 10, 14, 8),
            Margin = new Thickness(0, 2, 0, 16), Child = row,
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, brush);
        return border;
    }

    /// <summary>Code, coloured for its language, with the language named above it and a Copy button.</summary>
    private Border CodeBlock(string code, string info)
    {
        string language = info.Split(' ')[0].ToLowerInvariant();
        string? title = Regex.Match(info, "title=\"([^\"]+)\"") is { Success: true } t ? t.Groups[1].Value : null;
        string label = title ?? language switch
        {
            "ws" or "witcherscript" => "WitcherScript",
            "xml" => "XML",
            "json" => "JSON",
            "cmd" or "bat" => L.T("HelpRenderer.CommandLine"),
            "ps" or "powershell" => "PowerShell",
            "py" or "python" => "Python",
            "ini" => L.T("HelpRenderer.SettingsFile"),
            "csv" => "CSV",
            _ => "",
        };
        var header = new DockPanel { Margin = new Thickness(14, 6, 6, 0) };
        var copy = new Button { Padding = new Thickness(8, 3, 8, 3), MinWidth = 0, MinHeight = 0, FontSize = 12, ToolTip = L.T("HelpRenderer.CopyClipboard") };
        var copyText = new TextBlock { Text = L.T("Common.Copy") };
        var copyRow = new StackPanel { Orientation = Orientation.Horizontal };
        copyRow.Children.Add(new TextBlock { Text = "\uE8C8", FontFamily = (FontFamily)owner.FindResource("IconFont"), Margin = new Thickness(0, 1, 6, 0), FontSize = 12 });
        copyRow.Children.Add(copyText);
        copy.Content = copyRow;
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(code); } catch (System.Runtime.InteropServices.ExternalException) { return; }
            copyText.Text = L.T("HelpRenderer.Copied");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            timer.Tick += (_, _) => { copyText.Text = L.T("Common.Copy"); timer.Stop(); };
            timer.Start();
        };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });

        var body = new TextBlock { FontFamily = Mono, FontSize = 13, Margin = new Thickness(14, 6, 14, 12) };
        Colour(body.Inlines, code, language);
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // the page scrolls with the wheel over code too
        scroll.PreviewMouseWheel += (s, e) =>
        {
            e.Handled = true;
            ((UIElement)((FrameworkElement)s).Parent).RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
        };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(scroll);
        var border = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 16), Child = stack };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    /// <summary>A screenshot with its caption under it; a click opens it at full size.</summary>
    private FrameworkElement Picture(string file, string caption)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 18) };
        if (HelpLibrary.Image(file) is not { } source)
        {
            stack.Children.Add(Callout(L.T("HelpRenderer.WarningPictureMissing", file)));
            return stack;
        }
        var image = new Image { Source = source, Stretch = Stretch.Uniform, MaxHeight = 560, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = image, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true, ToolTip = L.T("HelpRenderer.ClickSeeFullSize"),
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        frame.MouseLeftButtonUp += (_, _) => openImage(source, caption);
        stack.Children.Add(frame);
        if (caption.Length > 0)
        {
            var text = Text(caption, new Thickness(2, 6, 0, 0));
            text.FontSize = 13;
            text.Opacity = 0.7;
            stack.Children.Add(text);
        }
        return stack;
    }

    /// <summary>A table: "| a | b |" rows, the first the header, a "|---|" row skipped.</summary>
    private FrameworkElement Table(List<string> rows)
    {
        var cells = rows.Where(r => !Regex.IsMatch(r, @"^\|[\s:\-|]+\|?$"))
            .Select(r => r.Trim('|').Split('|').Select(c => c.Trim()).ToList()).ToList();
        int columns = cells.Max(c => c.Count);
        var grid = new Grid();
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == columns - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        for (int r = 0; r < cells.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            if (r > 0 && r % 2 == 0)
            {
                var shade = new Border();
                shade.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorTertiaryBrush");
                Grid.SetRow(shade, r);
                Grid.SetColumnSpan(shade, columns);
                grid.Children.Add(shade);
            }
            for (int c = 0; c < cells[r].Count; c++)
            {
                var text = Text(cells[r][c], new Thickness(12, 7, 16, 7));
                text.FontSize = 14;
                if (r == 0) text.FontWeight = FontWeights.SemiBold;
                Grid.SetRow(text, r);
                Grid.SetColumn(text, c);
                grid.Children.Add(text);
            }
        }
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        line.SetResourceReference(Border.BackgroundProperty, "CardStrokeColorDefaultBrush");
        Grid.SetColumnSpan(line, columns);
        grid.Children.Add(line);
        var border = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 16), Child = grid, ClipToBounds = true };
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        return border;
    }

    /// <summary>Cards to click: "- [[Page]] | E8E5 | what it's for", each going to its page.</summary>
    private FrameworkElement Cards(List<string> items)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 12) };
        foreach (string item in items)
        {
            // the link first, which can have a | of its own, then the icon and the text
            var link = Regex.Match(item, @"^\[\[([^\]|]+)(?:\|([^\]]+))?\]\]");
            if (!link.Success) continue;
            var parts = new[] { link.Value }.Concat(item[link.Length..].Split('|', StringSplitOptions.TrimEntries).Skip(1)).ToArray();
            string page = link.Groups[1].Value, label = link.Groups[2].Success ? link.Groups[2].Value : page;
            var icon = new TextBlock { Text = parts.Length > 1 ? char.ConvertFromUtf32(Convert.ToInt32(parts[1], 16)) : "\uE8A5", FontFamily = (FontFamily)owner.FindResource("IconFont"), FontSize = 22, Margin = new Thickness(0, 0, 0, 10) };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            var stack = new StackPanel();
            stack.Children.Add(icon);
            stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 14.5, TextWrapping = TextWrapping.Wrap });
            if (parts.Length > 2) stack.Children.Add(new TextBlock { Text = parts[2], Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var card = new Border
            {
                Width = 246, MinHeight = 128, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16, 14, 16, 14),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack, Cursor = Cursors.Hand,
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            card.MouseEnter += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            card.MouseLeave += (_, _) => card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            card.MouseLeftButtonUp += (_, _) => openPage(page);
            panel.Children.Add(card);
        }
        return panel;
    }

    // ---------------------------------------------------------------- inline marks

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|\[\[([^\]|]+)(?:\|([^\]]+))?\]\]|\{\{([^}]+)\}\}|\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex InlineMarks();

    /// <summary>**bold**, `code`, [[Page]] or [[Page|text]], {{Ctrl+S}}, and [text](https://…).</summary>
    private void AddInlines(InlineCollection inlines, string markup, bool bold = false)
    {
        int at = 0;
        foreach (Match m in InlineMarks().Matches(markup))
        {
            if (m.Index > at) Plain(inlines, markup[at..m.Index], bold);
            if (m.Groups[1].Success)
            {
                var span = new Bold();
                AddInlines(span.Inlines, m.Groups[1].Value, true);
                inlines.Add(span);
            }
            else if (m.Groups[2].Success)
            {
                var run = new Run(m.Groups[2].Value) { FontFamily = Mono, FontSize = 13 };
                run.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                inlines.Add(run);
            }
            else if (m.Groups[3].Success)
            {
                string page = m.Groups[3].Value.Trim();
                var link = new Hyperlink(new Run(m.Groups[4].Success ? m.Groups[4].Value : page)) { ToolTip = L.T("HelpRenderer.Go", page) };
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                link.Click += (_, _) => openPage(page);
                inlines.Add(link);
            }
            else if (m.Groups[5].Success)
                inlines.Add(Keys(m.Groups[5].Value));
            else
            {
                string url = m.Groups[7].Value;
                var link = new Hyperlink(new Run(m.Groups[6].Value)) { ToolTip = url };
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                link.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                inlines.Add(link);
            }
            at = m.Index + m.Length;
        }
        if (at < markup.Length) Plain(inlines, markup[at..], bold);
    }

    /// <summary>Text, with the search's words picked out.</summary>
    private void Plain(InlineCollection inlines, string text, bool bold)
    {
        if (Highlight.Count == 0)
        {
            inlines.Add(new Run(text));
            return;
        }
        var pattern = new Regex(string.Join("|", Highlight.Select(Regex.Escape)), RegexOptions.IgnoreCase);
        int at = 0;
        foreach (Match m in pattern.Matches(text))
        {
            if (m.Index > at) inlines.Add(new Run(text[at..m.Index]));
            var mark = new Run(m.Value);
            mark.SetResourceReference(TextElement.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
            mark.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
            inlines.Add(mark);
            _matched = true;
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    /// <summary>Whether the text drawn since it was last cleared had a search match in it.</summary>
    private bool _matched;

    /// <summary>{{Ctrl+Shift+S}}: each key in a little box, joined by +.</summary>
    private InlineUIContainer Keys(string keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var parts = keys.Split('+', StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string key = parts[i].Length == 0 && i > 0 ? "+" : parts[i];
            if (i > 0) row.Children.Add(new TextBlock { Text = "+", Margin = new Thickness(2, 0, 2, 0), Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(6, 0, 6, 1),
                Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            };
            chip.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
            row.Children.Add(chip);
        }
        row.Margin = new Thickness(1, 0, 1, -4);
        return new InlineUIContainer(row) { BaselineAlignment = BaselineAlignment.Bottom };
    }

    // ---------------------------------------------------------------- code colouring

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "function", "event", "class", "extends", "var", "private", "public", "protected", "default", "import", "abstract", "statemachine",
        "state", "in", "latent", "entry", "timer", "exec", "quest", "storyscene", "struct", "enum", "if", "else", "for", "while", "do",
        "switch", "case", "break", "continue", "return", "new", "delete", "this", "super", "parent", "virtual_parent", "true", "false",
        "NULL", "out", "optional", "saved", "editable", "const", "inlined", "hint", "final", "cleanup", "reward", "autobind", "single",
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "import", "from", "as", "def", "class", "return", "if", "elif", "else", "for", "while", "in", "not", "and", "or", "is",
        "with", "try", "except", "finally", "raise", "pass", "break", "continue", "lambda", "yield", "None", "True", "False", "print",
    };

    private static readonly HashSet<string> PowerShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "foreach", "if", "else", "elseif", "function", "param", "return", "in", "while", "for", "switch", "try", "catch", "finally",
    };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "int", "float", "bool", "string", "name", "array", "byte", "Uint64", "Int32" };

    private void Colour(InlineCollection inlines, string code, string language)
    {
        switch (language)
        {
            case "ws" or "witcherscript":
                foreach (var (text, kind) in WitcherScriptTokens(code)) inlines.Add(Token(text, kind));
                break;
            case "xml":
                // spaces and words one at a time, so the space before an attribute doesn't take the attribute with it
                foreach (Match m in Regex.Matches(code, @"(<!--.*?-->)|(</?[\w:.\-]+|<\?[\w:.\-]+)|([\w:.\-]+)(\s*=\s*)(""[^""]*""|'[^']*')|(/?>|\?>)|(\s+|[^<\s=/>?]+|.)", RegexOptions.Singleline))
                {
                    if (m.Groups[1].Success) inlines.Add(Token(m.Value, "comment"));
                    else if (m.Groups[2].Success) inlines.Add(Token(m.Value, "keyword"));
                    else if (m.Groups[3].Success)
                    {
                        inlines.Add(Token(m.Groups[3].Value, "type"));
                        inlines.Add(new Run(m.Groups[4].Value));
                        inlines.Add(Token(m.Groups[5].Value, "string"));
                    }
                    else if (m.Groups[6].Success) inlines.Add(Token(m.Value, "keyword"));
                    else inlines.Add(new Run(m.Value));
                }
                break;
            case "json":
                int at = 0;
                foreach (var span in Highlighting.JsonScanner.Scan(code))
                {
                    if (span.Start > at) inlines.Add(new Run(code[at..span.Start]));
                    inlines.Add(new Run(code.Substring(span.Start, span.Length)) { Foreground = Highlighting.JsonColorizer.BrushFor(span.Kind, _dark) });
                    at = span.Start + span.Length;
                }
                if (at < code.Length) inlines.Add(new Run(code[at..]));
                break;
            case "ini":
                // [Section] as a keyword, the key as a type, the value as a string, ; comments
                foreach (Match m in Regex.Matches(code, @"^([ \t]*)(?:(;[^\n]*)|(\[[^\]\n]*\])|([^=\n;]+?)([ \t]*=[ \t]*)([^\n]*)|([^\n]*))", RegexOptions.Multiline))
                {
                    if (m.Index > 0 && code[m.Index - 1] == '\n') inlines.Add(new Run("\n"));
                    inlines.Add(new Run(m.Groups[1].Value));
                    if (m.Groups[2].Success) inlines.Add(Token(m.Groups[2].Value, "comment"));
                    else if (m.Groups[3].Success) inlines.Add(Token(m.Groups[3].Value, "keyword"));
                    else if (m.Groups[4].Success)
                    {
                        inlines.Add(Token(m.Groups[4].Value, "type"));
                        inlines.Add(new Run(m.Groups[5].Value));
                        inlines.Add(Token(m.Groups[6].Value, "string"));
                    }
                    else inlines.Add(new Run(m.Groups[7].Value));
                }
                break;
            case "cmd" or "bat":
                // each line's command as a keyword, its switches as types, quoted text as strings, REM and :: comments
                foreach (Match m in Regex.Matches(code, @"(^[ \t]*(?:rem\b|::)[^\n]*)|(^[ \t]*[^\s""]+)|(""[^""\n]*"")|((?<=\s)--?[\w-]+|(?<=\s)/[A-Za-z?]\b)|(\s+|[^\s""]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                {
                    if (m.Groups[1].Success) inlines.Add(Token(m.Value, "comment"));
                    else if (m.Groups[2].Success) inlines.Add(Token(m.Value, "keyword"));
                    else if (m.Groups[3].Success) inlines.Add(Token(m.Value, "string"));
                    else if (m.Groups[4].Success) inlines.Add(Token(m.Value, "type"));
                    else inlines.Add(new Run(m.Value));
                }
                break;
            case "py" or "python" or "ps" or "powershell":
                var words = language is "py" or "python" ? PythonKeywords : PowerShellKeywords;
                foreach (Match m in Regex.Matches(code, @"(#[^\n]*)|(""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*')|(\$\w+|-[A-Za-z]\w*)|(\b\d+(?:\.\d+)?\b)|([A-Za-z_]\w*)|(\s+|.)"))
                {
                    if (m.Groups[1].Success) inlines.Add(Token(m.Value, "comment"));
                    else if (m.Groups[2].Success) inlines.Add(Token(m.Value, "string"));
                    else if (m.Groups[3].Success && language is "ps" or "powershell") inlines.Add(Token(m.Value, "type"));
                    else if (m.Groups[4].Success) inlines.Add(Token(m.Value, "number"));
                    else if (m.Groups[5].Success) inlines.Add(Token(m.Value, words.Contains(m.Value) ? "keyword" : ""));
                    else inlines.Add(new Run(m.Value));
                }
                break;
            default:
                inlines.Add(new Run(code));
                break;
        }
    }

    private static IEnumerable<(string Text, string Kind)> WitcherScriptTokens(string code)
    {
        foreach (Match m in Regex.Matches(code, @"(//[^\n]*|/\*.*?\*/)|(""(?:\\.|[^""\\])*"")|('[^'\n]*')|(@\w+)|(\b\d+(?:\.\d+)?f?\b)|([A-Za-z_]\w*)|(\s+|.)", RegexOptions.Singleline))
        {
            if (m.Groups[1].Success) yield return (m.Value, "comment");
            else if (m.Groups[2].Success || m.Groups[3].Success) yield return (m.Value, "string");
            else if (m.Groups[4].Success) yield return (m.Value, "annotation");
            else if (m.Groups[5].Success) yield return (m.Value, "number");
            else if (m.Groups[6].Success) yield return (m.Value, Keywords.Contains(m.Value) ? "keyword" : Types.Contains(m.Value) ? "type" : "");
            else yield return (m.Value, "");
        }
    }

    private Run Token(string text, string kind)
    {
        var run = new Run(text);
        string? colour = kind switch
        {
            "keyword" => _dark ? "#569CD6" : "#0033B3",
            "type" => _dark ? "#4EC9B0" : "#267F99",
            "string" => _dark ? "#CE9178" : "#A31515",
            "comment" => _dark ? "#6A9955" : "#008000",
            "annotation" => _dark ? "#C586C0" : "#AF00DB",
            "number" => _dark ? "#B5CEA8" : "#098658",
            _ => null,
        };
        if (colour is not null) run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        return run;
    }
}
