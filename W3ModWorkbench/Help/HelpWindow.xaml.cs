using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Help;

/// <summary>
/// The guide: its categories down the left, each folding open to its pages, with a search over every page; the page
/// picked on the right, with its sections to jump to, its screenshots to open large, and the pages either side.
/// </summary>
public partial class HelpWindow : Window
{
    private static HelpWindow? _open;

    /// <summary>Categories folded open, kept while the app runs.</summary>
    private static readonly HashSet<string> Expanded = new(StringComparer.Ordinal);

    private readonly HelpRenderer _renderer;
    private readonly Stack<HelpPage> _back = new(), _forward = new();
    private HelpPage? _page;
    private IReadOnlyList<string> _highlight = [];

    /// <summary>Shows the guide, reusing the window when it's open, at a page when one is named.</summary>
    public static void ShowHelp(Window owner, string? page = null)
    {
        if (_open is null)
        {
            _open = new HelpWindow().Standalone(owner);
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
        }
        if (page is not null) _open.Open(page);
    }

    /// <summary>F1 in a window: the guide's page about it, or the start when there's none.</summary>
    public static void ShowHelpFor(Window window) =>
        ShowHelp(window, HelpLibrary.ForWindow(window is PreviewWindow { IsBench: true } ? "PreviewWindow:bench" : window.GetType().Name)?.Title);

    private HelpWindow()
    {
        InitializeComponent();
        _renderer = new HelpRenderer(this, title => Open(title), ShowImage);
        if (Expanded.Count == 0 && HelpLibrary.Categories.FirstOrDefault() is { } first) Expanded.Add(first.Title);
        if (HelpLibrary.Categories.FirstOrDefault()?.Pages.FirstOrDefault() is { } home) Show(home, remember: false);
        BuildContents();
    }

    // ---------------------------------------------------------------- the contents

    private void BuildContents()
    {
        NavPanel.Children.Clear();
        NavHeading.Text = L.T("HelpWindow.Contents");
        ExpandAllButton.Visibility = Visibility.Visible;
        ExpandAllText.Text = HelpLibrary.Categories.All(c => Expanded.Contains(c.Title)) ? L.T("HelpWindow.CollapseAll") : L.T("HelpWindow.ExpandAll");
        foreach (var category in HelpLibrary.Categories)
        {
            bool open = Expanded.Contains(category.Title);
            var chevron = new TextBlock { Text = open ? "" : "", FontFamily = Icons, FontSize = 10, Width = 18, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            var icon = new TextBlock { Text = category.Glyph, FontFamily = Icons, FontSize = 15, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            var title = new TextBlock { Text = category.Title, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var count = new TextBlock { Text = category.Pages.Count.ToString(), FontSize = 12, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            var row = new DockPanel();
            DockPanel.SetDock(chevron, Dock.Left);
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(chevron);
            row.Children.Add(icon);
            row.Children.Add(count);
            row.Children.Add(title);
            var header = Row(row, new Thickness(4, 7, 4, 7), category.Summary);
            header.Margin = new Thickness(0, NavPanel.Children.Count == 0 ? 0 : 4, 0, 0);
            header.MouseLeftButtonUp += (_, _) =>
            {
                if (!Expanded.Remove(category.Title)) Expanded.Add(category.Title);
                BuildContents();
            };
            NavPanel.Children.Add(header);
            if (!open) continue;
            foreach (var page in category.Pages)
            {
                var label = new TextBlock { Text = page.Title, TextWrapping = TextWrapping.Wrap, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
                var pageIcon = new TextBlock { Text = page.Glyph, FontFamily = Icons, FontSize = 13, Width = 26, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                var line = new DockPanel();
                DockPanel.SetDock(pageIcon, Dock.Left);
                line.Children.Add(pageIcon);
                line.Children.Add(label);
                var item = Row(line, new Thickness(28, 6, 6, 6), page.Summary, selected: page == _page);
                item.MouseLeftButtonUp += (_, _) => Open(page);
                NavPanel.Children.Add(item);
            }
        }
    }

    private static readonly FontFamily Icons = (FontFamily)Application.Current.FindResource("IconFont");

    /// <summary>A line in the list that lights up under the mouse, and in the accent when it's the page shown.</summary>
    private Border Row(UIElement content, Thickness padding, string tip, bool selected = false)
    {
        var border = new Border { Child = content, Padding = padding, CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        if (tip.Length > 0) border.ToolTip = tip;
        if (selected)
        {
            border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-padding.Left + 3, 3, 0, 3) };
            bar.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            var holder = new Grid();
            border.Child = holder;
            holder.Children.Add(content);
            holder.Children.Add(bar);
        }
        border.MouseEnter += (_, _) => { if (!selected) border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorTertiaryBrush"); };
        border.MouseLeave += (_, _) => { if (!selected) border.Background = Brushes.Transparent; };
        return border;
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        bool all = HelpLibrary.Categories.All(c => Expanded.Contains(c.Title));
        Expanded.Clear();
        if (!all) foreach (var c in HelpLibrary.Categories) Expanded.Add(c.Title);
        BuildContents();
    }

    // ---------------------------------------------------------------- searching

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (query.Length == 0)
        {
            _highlight = [];
            if (_page is not null) Show(_page, remember: false);
            BuildContents();
            return;
        }
        ShowResults(query);
    }

    private List<HelpHit> _hits = [];

    private void ShowResults(string query)
    {
        _hits = HelpLibrary.Search(query);
        var words = HelpLibrary.Words(query);
        NavPanel.Children.Clear();
        ExpandAllButton.Visibility = Visibility.Collapsed;
        NavHeading.Text = _hits.Count == 0 ? L.T("HelpWindow.NOPagesMatch") : _hits.Count == 1 ? L.T("HelpWindow.PageMatches") : L.T("HelpWindow.PagesMatch", _hits.Count);
        if (_hits.Count == 0)
        {
            NavPanel.Children.Add(new TextBlock
            {
                Text = L.T("HelpWindow.TryFewerWordsAnother"),
                TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(4, 6, 4, 0),
            });
            return;
        }
        foreach (var hit in _hits)
        {
            var stack = new StackPanel();
            var title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
            Mark(title.Inlines, hit.Page.Title, words);
            stack.Children.Add(title);
            stack.Children.Add(new TextBlock
            {
                Text = $"{hit.Page.Category.Title}" + (hit.Count > 0 ? $"  ·  {hit.Count} {(hit.Count == 1 ? L.T("HelpWindow.Match") : L.T("HelpWindow.Matches"))}" : ""),
                FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(0, 1, 0, 3),
            });
            var snippet = new TextBlock { FontSize = 12.5, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            Mark(snippet.Inlines, hit.Snippet, words);
            stack.Children.Add(snippet);
            var item = Row(stack, new Thickness(8, 8, 8, 8), "", selected: hit.Page == _page);
            item.MouseLeftButtonUp += (_, _) => Open(hit.Page, words);
            NavPanel.Children.Add(item);
        }
    }

    /// <summary>Text with the words picked out.</summary>
    private static void Mark(InlineCollection inlines, string text, List<string> words)
    {
        var pattern = new System.Text.RegularExpressions.Regex(string.Join("|", words.Select(System.Text.RegularExpressions.Regex.Escape)),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int at = 0;
        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(text))
        {
            if (m.Index > at) inlines.Add(new Run(text[at..m.Index]));
            var run = new Run(m.Value) { FontWeight = FontWeights.SemiBold };
            run.SetResourceReference(TextElement.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
            inlines.Add(run);
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _hits.Count > 0)
        {
            Open(_hits[0].Page, HelpLibrary.Words(SearchBox.Text));
            e.Handled = true;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    // ---------------------------------------------------------------- the page

    /// <summary>Opens a page by its title; a missing one says so rather than doing nothing.</summary>
    public void Open(string title)
    {
        if (HelpLibrary.Find(title) is { } page) Open(page);
        else MessageBox.Show(this, L.T("HelpWindow.TheresNoPageCalled", title), L.T("Common.Help"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Open(HelpPage page, List<string>? highlight = null)
    {
        _highlight = highlight ?? (SearchBox.Text.Trim().Length > 0 ? HelpLibrary.Words(SearchBox.Text) : []);
        Show(page, remember: true);
    }

    private void Show(HelpPage page, bool remember)
    {
        if (remember && _page is not null && _page != page)
        {
            _back.Push(_page);
            _forward.Clear();
        }
        _page = page;
        Expanded.Add(page.Category.Title);
        Breadcrumb.Text = $"{page.Category.Title}  ›  {page.Title}";
        TitleIcon.Text = page.Glyph;
        TitleText.Text = page.Title;
        SummaryText.Text = page.Summary;
        SummaryText.Visibility = page.Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnglishNote.Visibility = page.Language != Localisation.Loc.Instance.Language && Localisation.Loc.Instance.Language != Localisation.Loc.Pseudo
            ? Visibility.Visible : Visibility.Collapsed;
        Title = L.T("HelpWindow.W3ModWorkbenchHelp2", page.Title);

        _renderer.Highlight = _highlight;
        _renderer.Render(page.Body, PageBody);

        SectionLinks.Children.Clear();
        foreach (string section in page.Sections)
        {
            var link = new Hyperlink(new Run(section));
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            string target = section;
            link.Click += (_, _) => ScrollTo(_renderer.Anchors.GetValueOrDefault(target));
            SectionLinks.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 0, 20, 6), FontSize = 13.5 });
        }
        OnThisPage.Visibility = page.Sections.Count >= 3 ? Visibility.Visible : Visibility.Collapsed;

        var all = HelpLibrary.Pages.ToList();
        int index = all.IndexOf(page);
        _previous = index > 0 ? all[index - 1] : null;
        _next = index >= 0 && index + 1 < all.Count ? all[index + 1] : null;
        PreviousCard.Visibility = _previous is null ? Visibility.Hidden : Visibility.Visible;
        NextCard.Visibility = _next is null ? Visibility.Hidden : Visibility.Visible;
        PreviousTitle.Text = _previous?.Title ?? "";
        NextTitle.Text = _next?.Title ?? "";

        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
        if (SearchBox.Text.Trim().Length == 0) BuildContents();
        else ShowResults(SearchBox.Text.Trim());

        PageScroller.ScrollToTop();
        if (_renderer.FirstMatch is { } match)
            Dispatcher.BeginInvoke(() => ScrollTo(match, 80), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private HelpPage? _previous, _next;

    private void ScrollTo(FrameworkElement? element, double above = 12)
    {
        if (element is null) return;
        var point = element.TransformToAncestor(PageColumn).Transform(new Point(0, 0));
        PageScroller.ScrollToVerticalOffset(Math.Max(0, point.Y - above));
    }

    private void PreviousCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_previous is not null) Open(_previous, []);
    }

    private void NextCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_next is not null) Open(_next, []);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (HelpLibrary.Categories.FirstOrDefault()?.Pages.FirstOrDefault() is { } home) Open(home, []);
    }

    private void GoBack()
    {
        if (_back.Count == 0 || _page is null) return;
        _forward.Push(_page);
        _page = null;
        Show(_back.Pop(), remember: false);
    }

    private void GoForward()
    {
        if (_forward.Count == 0 || _page is null) return;
        _back.Push(_page);
        _page = null;
        Show(_forward.Pop(), remember: false);
    }

    // ---------------------------------------------------------------- screenshots at full size

    private void ShowImage(BitmapSource image, string caption)
    {
        LightboxImage.Source = image;
        LightboxCaption.Text = HelpLibrary.Plain(caption);
        Lightbox.Visibility = Visibility.Visible;
    }

    private void Lightbox_Click(object sender, MouseButtonEventArgs e) => Lightbox.Visibility = Visibility.Collapsed;

    // ---------------------------------------------------------------- keys

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            if (Lightbox.Visibility == Visibility.Visible) Lightbox.Visibility = Visibility.Collapsed;
            else if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else if (key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) GoBack();
        else if (key == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt) GoForward();
        else if (key == Key.Home && Keyboard.Modifiers == ModifierKeys.Alt) Home_Click(this, e);
        else return;
        e.Handled = true;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) GoBack();
        else if (e.ChangedButton == MouseButton.XButton2) GoForward();
        else return;
        e.Handled = true;
    }
}
