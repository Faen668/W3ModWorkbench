using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// One line in the <see cref="ResultsPanel"/>: a heading (a file, or a group such as "Wrapped by") that folds
/// the lines under it away, a line of code with the match picked out, or a declaration.
/// </summary>
public sealed class ResultRow : INotifyPropertyChanged
{
    public bool IsHeader { get; init; }

    /// <summary>A line of code, rather than a declaration.</summary>
    public bool IsCode { get; init; }

    public string Glyph { get; init; } = "";

    /// <summary>The line number, for a line of code.</summary>
    public string LineText { get; init; } = "";

    /// <summary>A heading's or declaration's name; for a line of code, the text before the match.</summary>
    public string Before { get; init; } = "";
    public string Match { get; init; } = "";
    public string After { get; init; } = "";

    /// <summary>Greyed text: where a file or declaration is.</summary>
    public string Detail { get; init; } = "";

    /// <summary>A heading's count, shown in a badge.</summary>
    public string Count { get; init; } = "";

    public bool HasCount => Count.Length > 0;

    /// <summary>A tag at the end, such as "declaration".</summary>
    public string? Label { get; init; }

    public bool HasLabel => !string.IsNullOrEmpty(Label);

    public int Depth { get; init; }

    public Thickness IndentMargin => new(Depth * 20, 0, 0, 0);

    public ScriptFile? File { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }

    public bool CanOpen => File is not null && !IsHeader;

    /// <summary>The heading this line is under, so folding it hides the line.</summary>
    public ResultRow? Parent { get; set; }

    private bool _collapsed;

    /// <summary>For a heading: whether the lines under it are folded away.</summary>
    public bool IsCollapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollapsed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chevron)));
        }
    }

    public string Chevron => IsCollapsed ? "" : ""; // right, down

    public event PropertyChangedEventHandler? PropertyChanged;

    public const string FileGlyph = "";

    /// <summary>Hits grouped under a heading for each file, in the order they came.</summary>
    public static List<ResultRow> ByFile(IEnumerable<ScriptHit> hits)
    {
        var rows = new List<ResultRow>();
        foreach (var group in hits.GroupBy(h => h.File.Key))
        {
            var list = group.ToList();
            var file = list[0].File;
            string folder = Path.GetDirectoryName(file.Path) ?? "";
            rows.Add(new ResultRow
            {
                IsHeader = true,
                Glyph = FileGlyph,
                Before = Path.GetFileName(file.Path),
                Detail = $"    {(folder.Length > 0 ? folder + "  ·  " : "")}{file.Source}",
                Count = list.Count.ToString(),
            });
            rows.AddRange(list.Select(FromHit));
        }
        return rows;
    }

    public static ResultRow FromHit(ScriptHit hit)
    {
        string line = hit.LineText;
        int lead = line.Length - line.TrimStart().Length;
        int column = Math.Clamp(hit.Column, lead, line.Length);
        int end = Math.Min(line.Length, column + hit.Length);
        string before = line[lead..column];
        // keep the match in view on a long line
        if (before.Length > 70) before = "…" + before[^69..];
        return new ResultRow
        {
            IsCode = true,
            LineText = hit.Line.ToString(),
            Before = before,
            Match = line[column..end],
            After = line[end..].TrimEnd(),
            Label = hit.Label,
            File = hit.File,
            Offset = hit.Offset,
            Length = hit.Length,
        };
    }

    public static ResultRow Heading(string text, int count) =>
        new() { IsHeader = true, Glyph = "", Before = text, Count = count.ToString() };

    /// <summary>A declaration: its name, then where it is.</summary>
    public static ResultRow FromSymbol(ScriptSymbol symbol, int depth = 1, string? label = null) => new()
    {
        Depth = depth,
        Glyph = DefinitionPeek.KindGlyph(symbol.Kind),
        Before = symbol.Title,
        Detail = $"{symbol.File.Path} : {symbol.Line}  ·  {symbol.File.Source}",
        Label = label,
        File = symbol.File,
        Offset = symbol.Offset,
        Length = symbol.Name.Length,
    };

    /// <summary>A whole file, opened at its top.</summary>
    public static ResultRow FromFile(ScriptFile file, int depth = 1, string? label = null) => new()
    {
        Depth = depth,
        Glyph = FileGlyph,
        Before = file.Path,
        Detail = file.Source,
        Label = label,
        File = file,
    };
}

/// <summary>Picks the look of a <see cref="ResultRow"/>: a heading, a line of code or a declaration.</summary>
public sealed class ResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Header { get; set; }
    public DataTemplate? Line { get; set; }
    public DataTemplate? Symbol { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        ResultRow { IsHeader: true } => Header,
        ResultRow { IsCode: true } => Line,
        _ => Symbol,
    };
}

/// <summary>
/// The list under the files in a preview window: references, the declarations that change or extend one,
/// and text found in every script. Clicking a line, or Enter, opens it; Ctrl opens it in the other group.
/// Clicking a heading folds the lines under it away.
/// </summary>
public partial class ResultsPanel : UserControl
{
    private List<ResultRow> _rows = [];

    /// <summary>A line to open, and whether in the other group.</summary>
    public event Action<ResultRow, bool>? Open;

    public event Action? Closed;

    /// <summary>The find box asks for a search: the pattern, and the text typed for the title.</summary>
    public event Action<Regex, string>? FindRequested;

    /// <summary>A place to look was picked from the list (not set from code).</summary>
    public event Action<string>? ScopePicked;

    private bool _settingScopes;

    /// <summary>Fills the Look in list with the places a search can look, picking <paramref name="selected"/> (or the first).</summary>
    public void SetScopes(IReadOnlyList<string> scopes, string? selected)
    {
        _settingScopes = true;
        ScopeBox.ItemsSource = scopes;
        ScopeBox.SelectedItem = selected is not null && scopes.Contains(selected) ? selected : scopes.FirstOrDefault();
        _settingScopes = false;
    }

    /// <summary>The place picked in the Look in list.</summary>
    public string? Scope => ScopeBox.SelectedItem as string;

    /// <summary>Which files, by name: wildcards, with ! to leave some out. Empty is every file.</summary>
    public string FileFilter
    {
        get => FilesBox.Text;
        set => FilesBox.Text = value;
    }

    public string FindText => FindBox.Text;

    public bool MatchCase
    {
        get => MatchCaseToggle.IsChecked == true;
        set => MatchCaseToggle.IsChecked = value;
    }

    public bool WholeWord
    {
        get => WholeWordToggle.IsChecked == true;
        set => WholeWordToggle.IsChecked = value;
    }

    public bool UseRegex
    {
        get => RegexToggle.IsChecked == true;
        set => RegexToggle.IsChecked = value;
    }

    private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingScopes && ScopeBox.SelectedItem is string scope) ScopePicked?.Invoke(scope);
    }

    public ResultsPanel()
    {
        InitializeComponent();
        // the match in a line of code gets a tint of the accent colour
        Loaded += (_, _) =>
        {
            if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
            {
                var tint = new SolidColorBrush(Color.FromArgb(0x55, accent.Color.R, accent.Color.G, accent.Color.B));
                tint.Freeze();
                Resources["ResultMatchBrush"] = tint;
            }
        };
    }

    /// <summary>For a panel in a window of its own, which has no panel to close.</summary>
    public void HideClose() => CloseButton.Visibility = Visibility.Collapsed;

    public bool IsFind => FindBar.Visibility == Visibility.Visible;

    /// <summary>A search is running: the title, and what it's doing.</summary>
    public void ShowBusy(string title, string status)
    {
        SetTitle(title, "");
        _rows = [];
        List.ItemsSource = null;
        FoldButton.Visibility = Visibility.Collapsed;
        StatusText.Text = status;
        Busy.Visibility = Visibility.Visible;
        StatusPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Shows a list. When it's empty, <paramref name="empty"/> says so instead.</summary>
    public void ShowRows(string title, string summary, List<ResultRow> rows, string empty)
    {
        SetTitle(title, summary);
        ResultRow? heading = null;
        foreach (var row in rows)
        {
            if (row.IsHeader) heading = row;
            else row.Parent = heading;
        }
        _rows = rows;
        Busy.Visibility = Visibility.Collapsed;
        StatusText.Text = empty;
        StatusPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FoldButton.Visibility = rows.Count(r => r.IsHeader) > 1 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
        if (rows.Count > 0) List.ScrollIntoView(rows[0]);
    }

    /// <summary>Whether a title is one made from a string of the tables: it starts as that string does, before its first placeholder.</summary>
    private static bool Starts(string title, string key)
    {
        string text = L.T(key, "\u0001");
        int at = text.IndexOf('\u0001');
        return title.StartsWith(at < 0 ? text : text[..at], StringComparison.Ordinal);
    }

    private void SetTitle(string title, string summary)
    {
        TitleText.Text = title;
        TitleIcon.Text = Starts(title, "PreviewDocument.References") ? "" // link
            : Starts(title, "PreviewDocument.Changes") ? "" // edit
            : Starts(title, "PreviewDocument.Hierarchy") ? "" // folder tree
            : Starts(title, "PreviewWindow.Compile") ? "" // play
            : ""; // search
        SummaryText.Text = summary;
        SummaryBadge.Visibility = summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the lines that aren't folded away under a heading.</summary>
    private void Refresh()
    {
        var selected = List.SelectedItem;
        List.ItemsSource = _rows.Where(r => r.Parent is not { IsCollapsed: true }).ToList();
        if (selected is not null) List.SelectedItem = selected;
        bool anyOpen = _rows.Any(r => r.IsHeader && !r.IsCollapsed);
        FoldIcon.Text = anyOpen ? "" : ""; // collapse, expand
        FoldButton.ToolTip = anyOpen ? L.T("ResultsPanel.FoldEveryGroup") : L.T("ResultsPanel.UnfoldEveryGroup");
    }

    private void Toggle(ResultRow heading)
    {
        heading.IsCollapsed = !heading.IsCollapsed;
        Refresh();
    }

    private void Fold_Click(object sender, RoutedEventArgs e)
    {
        bool fold = _rows.Any(r => r.IsHeader && !r.IsCollapsed);
        foreach (var row in _rows.Where(r => r.IsHeader)) row.IsCollapsed = fold;
        Refresh();
    }

    /// <summary>
    /// Buttons that add the installed mods' scripts to the search and run it again, in a strip under the list
    /// that says what they're for. Hidden when there are none.
    /// </summary>
    public void SetActions(params (string Label, Action Run)[] actions) => SetActions(ModsNote, actions);

    private static string ModsNote => L.T("ResultsPanel.ModsNote");

    /// <summary>Buttons in the strip under the list, after <paramref name="note"/> saying what they're for.</summary>
    public void SetActions(string note, params (string Label, Action Run)[] actions)
    {
        StripText.Text = note;
        ActionBar.Children.Clear();
        foreach (var (label, run) in actions)
        {
            var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 4) };
            button.Click += (_, _) => run();
            ActionBar.Children.Add(button);
        }
        ModsStrip.Visibility = actions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the find box, filled with <paramref name="text"/> when given, ready to type in.</summary>
    public void ShowFind(string? text)
    {
        FindBar.Visibility = Visibility.Visible;
        if (!string.IsNullOrEmpty(text)) FindBox.Text = text;
        if (_rows.Count == 0 && Busy.Visibility != Visibility.Visible)
            ShowRows(L.T("ResultsPanel.FindFiles"), "", [], L.T("ResultsPanel.TypeWhatLookPress"));
        Dispatcher.BeginInvoke(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void HideFind() => FindBar.Visibility = Visibility.Collapsed;

    /// <summary>F4 and Shift+F4: the next or previous place in the list, unfolding its group, which is opened.</summary>
    public ResultRow? Step(int step)
    {
        var places = _rows.Where(r => r.CanOpen).ToList();
        if (places.Count == 0) return null;
        int i = List.SelectedItem is ResultRow current ? places.IndexOf(current) : -1;
        i = i < 0 ? (step > 0 ? 0 : places.Count - 1) : ((i + step) % places.Count + places.Count) % places.Count;
        var row = places[i];
        if (row.Parent is { IsCollapsed: true } parent)
        {
            parent.IsCollapsed = false;
            Refresh();
        }
        List.SelectedItem = row;
        List.ScrollIntoView(row);
        return row;
    }

    private void Activate(ResultRow row)
    {
        if (row.IsHeader) Toggle(row);
        else if (row.CanOpen) Open?.Invoke(row, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    private void List_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(List, source) is ListBoxItem { DataContext: ResultRow row })
            Activate(row);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not ResultRow row) return;
        switch (e.Key)
        {
            case Key.Enter:
                Activate(row);
                break;
            case Key.Left when row.IsHeader && !row.IsCollapsed:
            case Key.Right when row.IsHeader && row.IsCollapsed:
                Toggle(row);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Closed?.Invoke();

    private void Find_Click(object sender, RoutedEventArgs e) => RequestFind();

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        RequestFind();
        e.Handled = true;
    }

    private void RequestFind()
    {
        string text = FindBox.Text;
        if (text.Length == 0) return;
        string pattern = RegexToggle.IsChecked == true ? text : Regex.Escape(text);
        if (WholeWordToggle.IsChecked == true) pattern = $@"\b(?:{pattern})\b";
        var options = RegexOptions.CultureInvariant | (MatchCaseToggle.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase);
        Regex regex;
        try
        {
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            ShowRows(L.T("ResultsPanel.FindFiles"), "", [], L.T("ResultsPanel.IsntValidRegularExpression", ex.Message));
            return;
        }
        FindRequested?.Invoke(regex, text);
    }
}
