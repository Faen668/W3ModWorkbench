using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;

namespace W3ModWorkbench;

/// <summary>A line in the fact list: a fact, or the calls whose fact name the code builds.</summary>
public sealed record FactRow(string Title, string Detail, string? Name, IReadOnlyList<FactUse> Uses);

/// <summary>
/// The quest facts scripts use: every name passed to FactsAdd, FactsQuerySum and the rest, with each place that sets or
/// reads it, in the game, the installed mods and the open project. Handy for finding a quest's state, or checking a fact
/// a mod sets is read anywhere.
/// </summary>
public partial class FactsWindow : Window, IMessageHost
{
    private const int MaxShown = 1000;

    private static FactsWindow? _instance;

    private readonly string? _gameFolder;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private List<FactUse> _uses = [];
    private List<FactSummary> _facts = [];
    private CancellationTokenSource? _cts;

    private FactsWindow()
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _gameFolder = GameLocator.Resolve(null);
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshList();
        };
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            _ = LoadAsync();
        };
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _instance = null;
        };
    }

    /// <summary>Opens the window, or brings it forward, with <paramref name="query"/> typed in the search when given.</summary>
    public static void Open(Window owner, string? query = null)
    {
        if (_instance is null)
        {
            _instance = new FactsWindow().Standalone(owner);
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
        if (query is not null) _instance.SearchBox.Text = query;
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var options = new ScriptCorpus.Options(_gameFolder, ModsCheck.IsChecked == true);
        StatusText.Text = "Reading every script…";
        ModsCheck.IsEnabled = false;
        try
        {
            _uses = await Task.Run(() => FactUsages.Find(ScriptCorpus.Gather(options, cts.Token), cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            ModsCheck.IsEnabled = true;
        }
        if (cts != _cts) return;
        _facts = FactUsages.Summarise(_uses);
        DetailText.Text = _gameFolder is null
            ? "The game folder isn't set, so only the open project's facts are listed. Set it in Settings to see the game's and the mods'."
            : $"{Format.Count(_facts.Count, "fact")} in {Format.Count(_uses.Count, "call")} to the facts functions";
        RefreshList();
    }

    private void RefreshList()
    {
        if (!IsLoaded) return;
        string[] words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int filter = FilterBox.SelectedIndex;
        List<FactRow> rows;
        if (filter == 4)
        {
            // calls whose name the code builds, each its own row, grouped by what's written
            rows = [.. _uses.Where(u => u.Fact is null && words.All(w => u.Expression.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(u => u.Expression)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new FactRow(g.Key, $"{Count(g.Count(u => u.Access == FactAccess.Write), g.Count(u => u.Access == FactAccess.Read))}  ·  {Sources(g)}", null, [.. g]))];
        }
        else
        {
            rows = [.. _facts.Where(f => words.All(w => f.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Where(f => filter switch
                {
                    1 => f.Uses.Any(u => u.File.Origin != CorpusOrigin.Game),
                    2 => f.OnlyWritten,
                    3 => f.OnlyRead,
                    _ => true,
                })
                .Select(f => new FactRow(f.Name, $"{Count(f.Writes, f.Reads)}  ·  {f.Sources}", f.Name, f.Uses))];
        }
        bool more = rows.Count > MaxShown;
        var shown = rows.Take(MaxShown).ToList();
        string? before = (FactList.SelectedItem as FactRow)?.Title;
        FactList.ItemsSource = shown;
        int keep = before is null ? -1 : shown.FindIndex(r => r.Title == before);
        if (shown.Count > 0) FactList.SelectedIndex = Math.Max(keep, 0);
        else ShowFact(null);
        StatusText.Text = shown.Count == 0 ? (_uses.Count == 0 ? "No facts found" : "Nothing matches")
            : more ? $"First {MaxShown} of {rows.Count:N0} shown: type more to narrow it down"
            : filter == 4 ? Format.Count(shown.Count, "expression") : Format.Count(shown.Count, "fact");
    }

    private static string Count(int writes, int reads) => $"set in {writes}, read in {reads}";

    private static string Sources(IEnumerable<FactUse> uses) => string.Join(", ", uses.Select(u => u.File.Label).Distinct(StringComparer.OrdinalIgnoreCase));

    private void ShowFact(FactRow? row)
    {
        FactHeader.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        UseList.ItemsSource = row?.Uses;
        if (row is null) return;
        FactName.Text = row.Title;
        int writes = row.Uses.Count(u => u.Access == FactAccess.Write), reads = row.Uses.Count - writes;
        string note = writes == 0 ? "  ·  no script sets it" : reads == 0 ? "  ·  no script reads it" : "";
        FactSummary.Text = $"{Count(writes, reads)}{note}";
    }

    private void Sources_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshList();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void FactList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowFact(FactList.SelectedItem as FactRow);

    private void UseList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenUse();

    private void UseList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenUse();
        e.Handled = true;
    }

    private void OpenUse()
    {
        if (UseList.SelectedItem is FactUse use) _ = ScriptOpener.OpenAsync(use.File, use.Hit.Offset, use.Hit.Length, this);
    }

    private void CopyName_Click(object sender, RoutedEventArgs e) => Copy((FactList.SelectedItem as FactRow)?.Title);

    private void CopyQuery_Click(object sender, RoutedEventArgs e)
    {
        if (FactList.SelectedItem is FactRow row) Copy(row.Name is null ? $"FactsQuerySum({row.Title})" : $"FactsQuerySum(\"{row.Name}\")");
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _ = Message.Show(null, $"Copied {text}", MessageKind.Info, autoClose: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocused && FactList.SelectedIndex < FactList.Items.Count - 1) FactList.SelectedIndex++;
        else if (e.Key == Key.Up && SearchBox.IsKeyboardFocused && FactList.SelectedIndex > 0) FactList.SelectedIndex--;
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");
}
