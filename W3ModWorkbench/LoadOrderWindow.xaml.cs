using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.LoadOrder;

namespace W3ModWorkbench;

/// <summary>A row of the load order list.</summary>
public sealed class ModItem(ModEntry mod)
{
    public ModEntry Mod => mod;
    public int Order => mod.Order;
    public string Name => mod.Name;
    public int Problems => mod.Problems;
    public int Warnings => mod.Warnings;
    public Visibility ProblemsVisibility => mod.Problems > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningsVisibility => mod.Warnings > 0 ? Visibility.Visible : Visibility.Collapsed;
    public double Opacity => mod.Enabled ? 1 : 0.5;

    public string Detail
    {
        get
        {
            string state = mod.Enabled ? "" : "disabled · ";
            string kind = mod.IsDlc ? "DLC · " : mod.Priority is { } p ? $"priority {p} · " : "";
            return state + kind + mod.Contents;
        }
    }

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { mod.Folder };
            if (mod.Title.Length > 0) lines.Add(mod.Title + (mod.Version.Length > 0 ? " " + mod.Version : ""));
            if (mod.DependsOn.Count > 0) lines.Add("Needs: " + string.Join(", ", mod.DependsOn));
            if (mod.UsedBy.Count > 0) lines.Add("Needed by: " + string.Join(", ", mod.UsedBy));
            return string.Join("\n", lines);
        }
    }
}

/// <summary>A row of the findings list.</summary>
public sealed class FindingItem(Finding finding)
{
    public Finding Finding => finding;
    public Severity Severity => finding.Severity;
    public string Title => finding.Title;
    public string Caption => finding.Mods.Count == 0 ? finding.Category
        : $"{finding.Category}  ·  {string.Join(", ", finding.Mods)}";

    /// <summary>"★ modA wins": who the game ends up using, shown on the row so it reads at a glance.</summary>
    public string WinnerText => finding.Winner is null ? "" : $"\u2605 {finding.Winner} wins";
    public Visibility WinnerVisibility => finding.Winner is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Looks over every installed mod at once: which files and functions two mods both change, what a mod changes that
/// doesn't exist, and whether the mods one needs are installed, enabled and loaded before it.
/// </summary>
public partial class LoadOrderWindow : Window, IMessageHost
{
    private const string AllCategories = "All kinds";

    private static LoadOrderWindow? _open;

    private readonly Settings _settings = App.Settings;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _cancel;
    private LoadOrderReport? _report;
    private ModEntry? _picked;
    private bool _ready, _busy;

    private LoadOrderWindow()
    {
        InitializeComponent();
        DisabledLook.Apply(this);

        string? mods = _settings.LoadOrderModsFolder;
        if (string.IsNullOrWhiteSpace(mods) || !Directory.Exists(mods))
            mods = GameLocator.Resolve(null) is { } game && Directory.Exists(Path.Combine(game, "mods")) ? Path.Combine(game, "mods") : "";
        FolderBox.Text = mods;
        DlcCheck.IsChecked = _settings.LoadOrderIncludeDlc;
        CategoryFilter.ItemsSource = new[] { AllCategories };
        CategoryFilter.SelectedIndex = 0;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };
        _ready = true;
        UpdateInputs();
        Loaded += (_, _) =>
        {
            if (AnalyseButton.IsEnabled) _ = AnalyseAsync();
        };
    }

    public static void Open(Window owner)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _open = new LoadOrderWindow { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    // ---------------------------------------------------------------- input

    private string ModsFolder => FolderBox.Text.Trim().Trim('"');

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateInputs();
    }

    private void UpdateInputs()
    {
        FolderHint.Visibility = FolderBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool exists;
        try { exists = Directory.Exists(ModsFolder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { exists = false; }
        AnalyseButton.IsEnabled = exists && !_busy;
        if (!_busy && _report is null)
            StatusText.Text = ModsFolder.Length == 0 ? "Choose the game's mods folder" : exists ? "" : "That folder doesn't exist";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the game's mods folder", InitialDirectory = Directory.Exists(ModsFolder) ? ModsFolder : "" };
        if (dialog.ShowDialog(this) != true) return;
        FolderBox.Text = dialog.FolderName;
    }

    // ---------------------------------------------------------------- analysis

    private void Analyse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) _cancel?.Cancel();
        else _ = AnalyseAsync();
    }

    private async Task AnalyseAsync()
    {
        string folder = ModsFolder;
        if (!Directory.Exists(folder)) return;
        _settings.LoadOrderModsFolder = folder;
        _settings.LoadOrderIncludeDlc = DlcCheck.IsChecked == true;
        _settings.Save();

        _cancel = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<string>(text => StatusText.Text = text);
        try
        {
            var options = new LoadOrderOptions(folder, DlcCheck.IsChecked == true, ModScanner.FindSettingsFile(), GameLocator.Resolve(folder));
            _report = await Task.Run(() => LoadOrderAnalyzer.RunAsync(options, progress, _cancel.Token));
            ShowReport();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowMessage("The mods couldn't be analysed", ex.Message, MessageKind.Error);
            StatusText.Text = "";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AnalyseLabel.Text = busy ? "Cancel" : "Analyse";
        FolderBox.IsEnabled = !busy;
        DlcCheck.IsEnabled = !busy;
        if (!busy) UpdateInputs();
        else AnalyseButton.IsEnabled = true;
    }

    private void ShowReport()
    {
        var report = _report;
        if (report is null) return;
        _picked = null;
        ModList.ItemsSource = report.Mods.Select(m => new ModItem(m)).ToList();
        ProblemsText.Text = $"{report.Count(Severity.Error):N0} problems";
        WarningsText.Text = $"{report.Count(Severity.Warning):N0} warnings";
        NotesText.Text = $"{report.Count(Severity.Info):N0} notes";
        CategoryFilter.ItemsSource = new[] { AllCategories }.Concat(report.Findings.Select(f => f.Category).Distinct().Order());
        CategoryFilter.SelectedIndex = 0;
        ExportButton.IsEnabled = true;
        ApplyFilter();

        string game = report.GameScriptsFolder is null ? "the game's scripts weren't found" : $"checked against {report.VanillaTypes:N0} game types";
        string priorities = report.SettingsFile is null ? "no mods.settings" : "priorities from mods.settings";
        StatusText.Text = $"{Format.Count(report.Mods.Count, "mod")} read in {report.Elapsed.TotalSeconds:0.0} s · {game} · {priorities}"
                          + (report.Warnings.Count > 0 ? "  ·  " + report.Warnings[0] : "");
        StatusText.ToolTip = string.Join("\n", report.Warnings);
    }

    // ---------------------------------------------------------------- lists

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _picked = (ModList.SelectedItem as ModItem)?.Mod;
        OpenFolderButton.IsEnabled = _picked is not null;
        ClearModButton.Visibility = _picked is null ? Visibility.Collapsed : Visibility.Visible;
        ClearModButton.Content = _picked is null ? "All mods" : $"Only {_picked.Name}: show all";
        ApplyFilter();
    }

    private void ClearMod_Click(object sender, RoutedEventArgs e) => ModList.SelectedItem = null;

    private void ApplyFilter()
    {
        if (_report is null) return;
        string query = SearchBox.Text.Trim();
        string category = CategoryFilter.SelectedItem as string ?? AllCategories;
        var shown = _report.Findings.Where(f =>
            (f.Severity switch { Severity.Error => ProblemsToggle.IsChecked == true, Severity.Warning => WarningsToggle.IsChecked == true, _ => NotesToggle.IsChecked == true })
            && (category == AllCategories || f.Category == category)
            && (_picked is null || f.Mods.Contains(_picked.Name, StringComparer.OrdinalIgnoreCase))
            && (query.Length == 0 || Matches(f, query))).Select(f => new FindingItem(f)).ToList();
        FindingList.ItemsSource = shown;
        if (shown.Count > 0) FindingList.SelectedIndex = 0;
        else ShowDetail(null);

        EmptyState.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count == 0)
        {
            bool anyHidden = _report.Findings.Count > 0;
            EmptyTitle.Text = anyHidden ? "Nothing matches" : "No conflicts found";
            EmptyText.Text = anyHidden ? "Turn on more of Problems, Warnings and Notes, or clear the search."
                : "No two mods collide and nothing points at a missing function or mod.";
        }
    }

    private static bool Matches(Finding f, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
            f.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || f.Summary.Contains(w, StringComparison.OrdinalIgnoreCase)
            || f.Category.Contains(w, StringComparison.OrdinalIgnoreCase) || f.Mods.Any(m => m.Contains(w, StringComparison.OrdinalIgnoreCase))
            || f.Items.Any(i => i.Contains(w, StringComparison.OrdinalIgnoreCase)));

    private void FindingList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail((FindingList.SelectedItem as FindingItem)?.Finding);

    private void ShowDetail(Finding? f)
    {
        DetailPanel.Visibility = f is null ? Visibility.Collapsed : Visibility.Visible;
        if (f is null) return;
        DetailTitle.Text = f.Title;
        DetailMods.Text = f.Mods.Count == 0 ? f.Category : $"{f.SeverityText.ToUpperInvariant()}  ·  {f.Category}  ·  {string.Join("  ›  ", f.Mods)}";
        DetailSummary.Text = f.Summary;
        DetailWinner.Visibility = f.Winner is null ? Visibility.Collapsed : Visibility.Visible;
        if (f.Winner is not null)
        {
            var winner = _report?.Mods.FirstOrDefault(m => m.Name == f.Winner);
            DetailWinnerName.Text = f.Winner;
            DetailWinnerWhy.Text = winner is null ? "" : $"  ·  #{winner.Order} in the load order{(winner.Priority is { } p ? $", priority {p}" : "")}: the game uses its version. The others lose.";
        }
        DetailItems.Text = string.Join(Environment.NewLine, f.Items);
        DetailItems.Visibility = ItemsCaption.Visibility = f.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailAdvice.Text = f.Advice;
    }

    // ---------------------------------------------------------------- actions

    private void Merger_Click(object sender, RoutedEventArgs e)
    {
        if (!ScriptMerger.IsSet(_settings.ScriptMergerPath))
        {
            _ = ShowMessage("Script Merger isn't set", "Set the path to Script Merger Remastered (by relapse12) in Settings, under Script merger.", MessageKind.Info);
            return;
        }
        try { ScriptMerger.Start(_settings.ScriptMergerPath); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _ = ShowMessage("Script Merger couldn't be started", ex.Message, MessageKind.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_picked is null || !Directory.Exists(_picked.Folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_picked.Folder}\"") { UseShellExecute = true });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save the load order report",
            Filter = "Markdown (*.md)|*.md",
            FileName = $"load-order-{DateTime.Now:yyyy-MM-dd}.md",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, LoadOrderAnalyzer.ToMarkdown(_report));
            _ = Message.Show(null, "Report saved.", MessageKind.Info, autoClose: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("The report couldn't be saved", ex.Message, MessageKind.Error);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.F5) { if (!_busy && AnalyseButton.IsEnabled) _ = AnalyseAsync(); }
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cancel?.Cancel();
}
