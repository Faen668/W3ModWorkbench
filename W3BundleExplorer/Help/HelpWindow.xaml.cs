using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace W3BundleExplorer.Help;

public partial class HelpWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    private static HelpWindow? _open;

    /// <summary>Shows the help window, reusing it if it is already open.</summary>
    public static void ShowHelp(Window owner, string? topic = null)
    {
        if (_open is null)
        {
            _open = new HelpWindow { Owner = owner };
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
        }
        if (topic is not null) _open.Select(topic);
    }

    private HelpWindow()
    {
        InitializeComponent();
        W3BundleExplorer.DisabledLook.Apply(this);
        Fill("");
        TopicList.SelectedIndex = 1; // first topic, after the group header
    }

    private void Select(string title)
    {
        foreach (var item in TopicList.Items)
            if (item is ListBoxItem { Tag: HelpTopic t } li && t.Title == title)
                TopicList.SelectedItem = li;
    }

    // ------------------------------------------------------------------ topic list

    private void Fill(string filter)
    {
        var selected = (TopicList.SelectedItem as ListBoxItem)?.Tag as HelpTopic;
        TopicList.Items.Clear();
        foreach (var group in HelpContent.Topics.GroupBy(t => t.Group))
        {
            var topics = group.Where(t => filter.Length == 0
                || t.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || t.Body.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (topics.Count == 0) continue;

            TopicList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.6 },
                IsEnabled = false,
                Focusable = false,
                Margin = new Thickness(0, TopicList.Items.Count == 0 ? 0 : 12, 0, 2),
            });
            foreach (var t in topics)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = t.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), Width = 26, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = t.Title, VerticalAlignment = VerticalAlignment.Center });
                var item = new ListBoxItem { Content = row, Tag = t };
                TopicList.Items.Add(item);
                if (t == selected) TopicList.SelectedItem = item;
            }
        }
        if (TopicList.SelectedItem is null)
            TopicList.SelectedItem = TopicList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is HelpTopic);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Fill(FilterBox.Text.Trim());
    }

    private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((TopicList.SelectedItem as ListBoxItem)?.Tag is not HelpTopic topic) return;
        GroupText.Text = topic.Group;
        TitleText.Text = topic.Title;
        Render(topic.Body);
        PageScroller.ScrollToTop();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (FilterBox.Text.Length > 0) FilterBox.Clear();
            else Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ page rendering

    private void Render(string body)
    {
        PageBody.Children.Clear();
        var lines = body.Replace("\r", "").Split('\n');
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            PageBody.Children.Add(Text(paragraph.ToString(), new Thickness(0, 0, 0, 12)));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new StringBuilder();
                while (++i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                    code.AppendLine(lines[i]);
                PageBody.Children.Add(CodeBlock(code.ToString().TrimEnd()));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                PageBody.Children.Add(new TextBlock
                {
                    Text = line[3..],
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 14, 0, 8),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var row = new Grid { Margin = new Thickness(6, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = "•", Opacity = 0.7 });
                var text = Text(line[2..], new Thickness(0));
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                PageBody.Children.Add(row);
                if (i + 1 >= lines.Length || !lines[i + 1].StartsWith("- ", StringComparison.Ordinal))
                    row.Margin = new Thickness(6, 0, 0, 12);
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                PageBody.Children.Add(Note(line[2..]));
            }
            else if (line.Trim().Length == 0)
            {
                FlushParagraph();
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line.Trim());
            }
        }
        FlushParagraph();
    }

    private TextBlock Text(string markup, Thickness margin)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = margin, FontSize = 14, LineHeight = 22 };
        AddInlines(tb.Inlines, markup);
        return tb;
    }

    /// <summary>Handles **bold** and `code` spans.</summary>
    private static void AddInlines(InlineCollection inlines, string markup)
    {
        int i = 0;
        while (i < markup.Length)
        {
            int bold = markup.IndexOf("**", i, StringComparison.Ordinal);
            int code = markup.IndexOf('`', i);
            int next = bold < 0 ? code : code < 0 ? bold : Math.Min(bold, code);
            if (next < 0)
            {
                inlines.Add(new Run(markup[i..]));
                break;
            }
            if (next > i) inlines.Add(new Run(markup[i..next]));

            if (next == bold)
            {
                int end = markup.IndexOf("**", bold + 2, StringComparison.Ordinal);
                if (end < 0) { inlines.Add(new Run(markup[bold..])); break; }
                inlines.Add(new Bold(new Run(markup[(bold + 2)..end])));
                i = end + 2;
            }
            else
            {
                int end = markup.IndexOf('`', code + 1);
                if (end < 0) { inlines.Add(new Run(markup[code..])); break; }
                var run = new Run(markup[(code + 1)..end]) { FontFamily = Mono, FontSize = 13 };
                run.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                inlines.Add(run);
                i = end + 1;
            }
        }
    }

    private static Border CodeBlock(string code)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 2, 0, 14),
            Child = new TextBlock { Text = code, FontFamily = Mono, FontSize = 13 },
        };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    private Border Note(string markup)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16, Margin = new Thickness(0, 2, 12, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        row.Children.Add(icon);
        var text = Text(markup, new Thickness(0));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 2, 0, 14),
            Child = row,
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }
}
