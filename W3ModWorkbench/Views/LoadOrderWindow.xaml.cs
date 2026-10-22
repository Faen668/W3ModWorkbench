using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.LoadOrder;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

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
            string state = mod.IsCandidate ? L.T("LoadOrderWindow.NotInstalled") : mod.Enabled ? "" : L.T("LoadOrderWindow.Disabled");
            string kind = mod.IsDlc ? "DLC · " : mod.IsModio ? "mod.io · " : mod.Priority is { } p ? L.T("LoadOrderWindow.PriorityLabel", p) : "";
            return state + kind + mod.Contents;
        }
    }

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { mod.Folder };
            if (mod.Title.Length > 0) lines.Add(mod.Title + (mod.Version.Length > 0 ? " " + mod.Version : ""));
            if (mod.DependsOn.Count > 0) lines.Add(L.T("LoadOrderWindow.Needs", string.Join(", ", mod.DependsOn)));
            if (mod.UsedBy.Count > 0) lines.Add(L.T("LoadOrderWindow.Needed", string.Join(", ", mod.UsedBy)));
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
    public string WinnerText => finding.Winner is null ? "" : L.T("LoadOrderWindow.Wins", finding.Winner);
    public Visibility WinnerVisibility => finding.Winner is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Looks over every installed mod at once: which files and functions two mods both change, what a mod changes that
/// doesn't exist, and whether the mods one needs are installed, enabled and loaded before it.
/// </summary>
public partial class LoadOrderWindow : Window, IMessageHost
{
    private static string AllCategories => L.T("LoadOrderWindow.AllKinds");

    private static LoadOrderWindow? _open;

    private readonly Settings _settings = App.Settings;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? _cancel;
    private LoadOrderReport? _report;
    private List<ModEntry> _picked = [];

    /// <summary>A mod being checked before it's installed: its folder or unpacked .zip.</summary>
    private ModDownload? _download;
    private bool _ready, _busy;

    private LoadOrderWindow()
    {
        InitializeComponent();

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
        _open = new LoadOrderWindow().Standalone(owner);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

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
            StatusText.Text = ModsFolder.Length == 0 ? L.T("LoadOrderWindow.ChooseGamesModsFolder") : exists ? "" : L.T("LoadOrderWindow.FolderDoesntExist");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("LoadOrderWindow.ChooseGamesModsFolder"), InitialDirectory = Directory.Exists(ModsFolder) ? ModsFolder : "" };
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
            var options = new LoadOrderOptions(folder, DlcCheck.IsChecked == true, ModScanner.FindSettingsFile(), GameLocator.Resolve(folder),
                _download?.Mods, _download?.Menus, _settings.ScriptMergerPath);
            _report = await Task.Run(() => LoadOrderAnalyzer.RunAsync(options, progress, _cancel.Token));
            ShowReport();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.T("Common.Cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowMessage(L.T("LoadOrderWindow.ModsCouldntAnalysed"), ex.Message, MessageKind.Error);
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
        AnalyseLabel.Text = busy ? L.T("Common.Cancel") : L.T("LoadOrderWindow.Analyse");
        FolderBox.IsEnabled = !busy;
        DlcCheck.IsEnabled = !busy;
        CheckButton.IsEnabled = !busy;
        if (!busy) UpdateInputs();
        else AnalyseButton.IsEnabled = true;
    }

    private void ShowReport()
    {
        var report = _report;
        if (report is null) return;
        _picked = [];
        var items = report.Mods.Select(m => new ModItem(m)).ToList();
        ModList.ItemsSource = items;
        ProblemsText.Text = L.T("LoadOrderWindow.Problems", report.Count(Severity.Error));
        WarningsText.Text = L.T("LoadOrderWindow.Warnings2", report.Count(Severity.Warning));
        NotesText.Text = L.T("LoadOrderWindow.Notes2", report.Count(Severity.Info));
        CategoryFilter.ItemsSource = new[] { AllCategories }.Concat(report.Findings.Select(f => f.Category).Distinct().Order());
        CategoryFilter.SelectedIndex = 0;
        ExportButton.IsEnabled = true;
        ApplyFilter();

        string game = report.GameScriptsFolder is null ? L.T("LoadOrderWindow.GamesScriptsWerentFound") : L.T("LoadOrderWindow.CheckedAgainstGameTypes", report.VanillaTypes);
        string priorities = report.SettingsFile is null ? L.T("LoadOrderWindow.NoModsSettings") : L.T("LoadOrderWindow.PrioritiesModsSettings");
        StatusText.Text = L.T("LoadOrderWindow.Read", L.T("Count.Mod", report.Mods.Count), report.Elapsed.TotalSeconds, game, priorities, (report.Warnings.Count > 0 ? "  ·  " + report.Warnings[0] : ""));
        StatusText.ToolTip = string.Join("\n", report.Warnings);

        // a mod checked before it's installed: show what it touches
        CandidateBar.Visibility = _download is null ? Visibility.Collapsed : Visibility.Visible;
        if (_download is not null)
        {
            var candidates = items.Where(i => i.Mod.IsCandidate).ToList();
            var names = candidates.Select(i => i.Name).ToList();
            int problems = report.Findings.Count(f => f.Severity == Severity.Error && f.Mods.Intersect(names, StringComparer.OrdinalIgnoreCase).Any());
            int warnings = report.Findings.Count(f => f.Severity == Severity.Warning && f.Mods.Intersect(names, StringComparer.OrdinalIgnoreCase).Any());
            CandidateText.Text = L.T("LoadOrderWindow.Checking", _download.Name, string.Join(", ", candidates.Select(i => $"{i.Name} (#{i.Order})")),
                _download.Menus.Count, L.T("LoadOrderWindow.Problems", problems), L.T("LoadOrderWindow.Warnings2", warnings));
            foreach (var c in candidates) ModList.SelectedItems.Add(c);
            if (candidates.Count > 0) ModList.ScrollIntoView(candidates[0]);
        }
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
        _picked = [.. ModList.SelectedItems.Cast<ModItem>().Select(i => i.Mod).OrderBy(m => m.Order)];
        OpenFolderButton.IsEnabled = _picked.Count == 1;
        ClearModButton.Visibility = _picked.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearModButton.Content = _picked.Count switch
        {
            0 => L.T("LoadOrderWindow.AllMods"),
            1 => L.T("LoadOrderWindow.OnlyShowAll", _picked[0].Name),
            _ when PickedCandidates => L.T("LoadOrderWindow.OnlyCandidateShowAll", _download!.Name),
            _ => L.T("LoadOrderWindow.BetweenShowAll", _picked.Count),
        };
        ApplyFilter();
    }

    /// <summary>The mods picked are just the ones being checked before they're installed.</summary>
    private bool PickedCandidates => _download is not null && _picked.Count > 0 && _picked.All(m => m.IsCandidate)
        && _picked.Count == _report?.Mods.Count(m => m.IsCandidate);

    /// <summary>
    /// Whether a finding is about the mods picked: one mod's, anything a mod being checked touches, or for several
    /// mods, what's between them (findings two or more of them share).
    /// </summary>
    private bool AboutPicked(Finding f)
    {
        if (_picked.Count == 0) return true;
        int shared = _picked.Count(m => f.Mods.Contains(m.Name, StringComparer.OrdinalIgnoreCase));
        return _picked.Count == 1 || PickedCandidates ? shared > 0 : shared >= 2;
    }

    private void ClearMod_Click(object sender, RoutedEventArgs e) => ModList.UnselectAll();

    private void ApplyFilter()
    {
        if (_report is null) return;
        string query = SearchBox.Text.Trim();
        string category = CategoryFilter.SelectedItem as string ?? AllCategories;
        var shown = _report.Findings.Where(f =>
            (f.Severity switch { Severity.Error => ProblemsToggle.IsChecked == true, Severity.Warning => WarningsToggle.IsChecked == true, _ => NotesToggle.IsChecked == true })
            && (category == AllCategories || f.Category == category)
            && AboutPicked(f)
            && (query.Length == 0 || Matches(f, query))).Select(f => new FindingItem(f)).ToList();
        FindingList.ItemsSource = shown;
        if (shown.Count > 0) FindingList.SelectedIndex = 0;
        else ShowDetail(null);

        EmptyState.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count == 0)
        {
            bool anyHidden = _report.Findings.Count > 0;
            EmptyTitle.Text = anyHidden ? L.T("Common.NothingMatches") : L.T("LoadOrderWindow.NoConflictsFound");
            EmptyText.Text = anyHidden ? L.T("LoadOrderWindow.TurnMoreProblemsWarnings")
                : L.T("LoadOrderWindow.NoTwoModsCollide");
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
            DetailWinnerWhy.Text = winner is null ? "" : L.T("LoadOrderWindow.WinnerWhy", winner.Order, winner.Priority is { } p ? L.T("LoadOrderWindow.PriorityOf", p) : "");
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
            _ = ShowMessage(L.T("LoadOrderWindow.ScriptMergerIsntSet"), L.T("LoadOrderWindow.SetPathScriptMerger"), MessageKind.Info);
            return;
        }
        try { ScriptMerger.Start(_settings.ScriptMergerPath); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _ = ShowMessage(L.T("LoadOrderWindow.ScriptMergerCouldntStarted"), ex.Message, MessageKind.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_picked is not [var mod] || !Directory.Exists(mod.Folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{mod.Folder}\"") { UseShellExecute = true });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("LoadOrderWindow.SaveLoadOrderReport"),
            Filter = L.T("LoadOrderWindow.MarkdownMdMd"),
            FileName = $"load-order-{DateTime.Now:yyyy-MM-dd}.md",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            await File.WriteAllTextAsync(dialog.FileName, LoadOrderAnalyzer.ToMarkdown(_report));
            _ = Message.Show(null, L.T("LoadOrderWindow.ReportSaved"), MessageKind.Info, autoClose: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("LoadOrderWindow.ReportCouldntSaved"), ex.Message, MessageKind.Error);
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

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cancel?.Cancel();
        _download?.Dispose();
    }

    // ---------------------------------------------------------------- checking a mod before it's installed

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = CheckButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var folder = new MenuItem { Header = L.T("LoadOrderWindow.CheckFolder") };
        folder.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = L.T("LoadOrderWindow.CheckFolderTitle") };
            if (dialog.ShowDialog(this) == true) _ = CheckAsync(dialog.FolderName);
        };
        var zip = new MenuItem { Header = L.T("LoadOrderWindow.CheckZip") };
        zip.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = L.T("LoadOrderWindow.CheckZipTitle"), Filter = L.T("LoadOrderWindow.ZipFilter") };
            if (dialog.ShowDialog(this) == true) _ = CheckAsync(dialog.FileName);
        };
        menu.Items.Add(folder);
        menu.Items.Add(zip);
        menu.IsOpen = true;
    }

    /// <summary>Reads a downloaded mod (a folder or .zip) and analyses the installed mods with it in its place.</summary>
    private async Task CheckAsync(string path)
    {
        if (_busy || !Directory.Exists(ModsFolder)) return;
        StatusText.Text = L.T("LoadOrderWindow.Unpacking", Path.GetFileName(path.TrimEnd('\\', '/')));
        ModDownload download;
        try { download = await Task.Run(() => ModDownload.Open(path)); }
        catch (InvalidDataException ex)
        {
            await ShowMessage(L.T("LoadOrderWindow.CantCheck"), ex.Message, MessageKind.Warning);
            StatusText.Text = "";
            return;
        }
        _download?.Dispose();
        _download = download;
        await AnalyseAsync();
    }

    private void StopChecking_Click(object sender, RoutedEventArgs e)
    {
        _download?.Dispose();
        _download = null;
        CandidateBar.Visibility = Visibility.Collapsed;
        _ = AnalyseAsync();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths
            && (Directory.Exists(paths[0]) || paths[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths) _ = CheckAsync(paths[0]);
    }
}
