using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Quests;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Native Quest Hook's log, live: every quest block the game starts, finishes (with the output it fired) or stops, as it
/// happens, each linked to the quest file and block it is, so a modder can find a block's GUID by playing the quest.
/// The hook logs every block when its ini says LogEveryBlock=1, and only the blocks mods watch otherwise; the window
/// says which, and how to turn it on. A log from before (the hook keeps the last session's) can be opened too.
/// </summary>
public partial class QuestLogWindow : Window, IMessageHost
{
    // loc-ignore-start: the hook's own file names and its page
    private const string LogName = "NativeQuestHook.log", IniName = "NativeQuestHook.ini", AsiName = "NativeQuestHook.asi";
    private const string Releases = "https://github.com/Faen668/NativeQuestHook/releases";
    // loc-ignore-end
    private const int MostRows = 20_000;

    private readonly ResourceLocator _locator;
    private readonly string? _binFolder;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private LogTail? _tail;
    private NqhLogReader _reader = new();
    private uint? _finish;
    private readonly List<Row> _all = [];
    private ObservableCollection<Row> _shown = [];
    private QuestIndex? _index;
    private bool _paused, _live;

    /// <summary>A block event as the table shows it.</summary>
    public sealed class Row(NqhLogEvent e)
    {
        public NqhLogEvent Event { get; set; } = e;
        public string Time => Event.Time.ToString(@"hh\:mm\:ss\.fff");
        public string What => KindName(Event.Kind);
        public string Kind => QuestNames.Default(Event.ClassName) ?? QuestNames.Short(Event.ClassName);
        public string Name => Event.Name;
        public string Output => Event.Kind == NqhLogKind.Finished ? Event.Output : "";
        public string Guid => Event.Guid.ToString();
        /// <summary>The quest file the block is in (the copy the game reads), once the index has found it.</summary>
        public string? Path { get; set; }
        public string File => Path is null ? "" : System.IO.Path.GetFileName(Path);
    }

    private QuestLogWindow(ResourceLocator? locator)
    {
        InitializeComponent();
        string? game = locator?.GameFolder ?? GameLocator.Resolve(null);
        _locator = locator ?? new ResourceLocator([], null, game);
        _binFolder = game is null ? null : Path.Combine(game, "bin", "x64_dx12");
        EventGrid.ItemsSource = _shown;
        _timer.Tick += (_, _) => Poll();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Starts following the game's log, then finds the quest files its blocks are in.</summary>
    private async Task StartAsync()
    {
        CheckSetup();
        if (_binFolder is not null) Follow(Path.Combine(_binFolder, LogName), live: true);
        _timer.Start();
        InfoText.Text = L.T("QuestLog.Indexing");
        _index = await QuestIndex.GetAsync(_locator);
        foreach (var row in _all) row.Path ??= FileOf(row.Event.Guid);
        Rebuild();
        Status();
    }

    /// <summary>Opens the live log. <paramref name="locator"/> finds the quest files; null for the game and its installed mods.</summary>
    public static void Open(Window owner, ResourceLocator? locator = null) => new QuestLogWindow(locator).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    public static string KindName(NqhLogKind kind) => kind switch
    {
        NqhLogKind.Started => L.T("QuestLog.Kind.Started"),
        NqhLogKind.Replayed => L.T("QuestLog.Kind.Replayed"),
        NqhLogKind.Finished => L.T("QuestLog.Kind.Finished"),
        NqhLogKind.Stopped => L.T("QuestLog.Kind.Stopped"),
        _ => L.T("QuestLog.Kind.InputAgain"),
    };

    /// <summary>Says what's missing for the log to show every block: the hook itself, or its ini's LogEveryBlock=1.</summary>
    private void CheckSetup()
    {
        if (_binFolder is null || !File.Exists(Path.Combine(_binFolder, AsiName)))
        {
            SetupTitle.Text = L.T("QuestLog.NoHook");
            SetupText.Text = L.T("QuestLog.NoHookText");
            IniText.Visibility = CopyIniButton.Visibility = Visibility.Collapsed;
            ReleasesButton.Visibility = Visibility.Visible;
            SetupCard.Visibility = Visibility.Visible;
            return;
        }
        ReleasesButton.Visibility = Visibility.Collapsed;
        if (LogsEveryBlock(Path.Combine(_binFolder, IniName)))
        {
            SetupCard.Visibility = Visibility.Collapsed;
            return;
        }
        SetupTitle.Text = L.T("QuestLog.OnlyWatchedTitle");
        SetupText.Text = L.T("QuestLog.OnlyWatchedText", Path.Combine(_binFolder, IniName));
        IniText.Visibility = CopyIniButton.Visibility = Visibility.Visible;
        SetupCard.Visibility = Visibility.Visible;
    }

    /// <summary>Whether the hook's ini turns on logging every block: LogEveryBlock=1 under [Debug].</summary>
    private static bool LogsEveryBlock(string ini)
    {
        try
        {
            if (!File.Exists(ini)) return false;
            string section = "";
            foreach (string raw in File.ReadLines(ini))
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
                else if (section.Equals("Debug", StringComparison.OrdinalIgnoreCase) && line.Split('=', 2) is [var key, var value]
                         && key.Trim().Equals("LogEveryBlock", StringComparison.OrdinalIgnoreCase))
                    return value.Trim() != "0" && value.Trim().Length > 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>Reads a log from the top, and keeps following it when it's the one the game writes.</summary>
    private void Follow(string path, bool live)
    {
        _tail = new LogTail(path);
        _live = live;
        _reader = new NqhLogReader();
        _finish = null;
        _all.Clear();
        Poll();
        Rebuild();
        Status();
    }

    /// <summary>Reads what the log has added since the last look and adds its block events.</summary>
    private void Poll()
    {
        if (_tail is null) return;
        var lines = _tail.ReadNew();
        if (_tail.Restarted)
        {
            // the game was started again: a new log, and a new build of the finish's caller to learn
            _reader = new NqhLogReader();
            _finish = null;
        }
        if (lines.Count == 0) return;
        var added = new List<Row>();
        foreach (string line in lines)
            if (_reader.Read(line) is { } e)
            {
                var row = new Row(e) { Path = FileOf(e.Guid) };
                _all.Add(row);
                added.Add(row);
            }
        if (_reader.FinishCaller != _finish)
        {
            // where a finish comes from was learnt (or changed): read the deactivations before it again
            _finish = _reader.FinishCaller;
            foreach (var row in _all)
                if (row.Event.Kind is NqhLogKind.Finished or NqhLogKind.Stopped)
                    row.Event = row.Event with { Kind = row.Event.Caller == _finish ? NqhLogKind.Finished : NqhLogKind.Stopped };
            if (!_paused) Rebuild();
        }
        if (_all.Count > MostRows)
        {
            _all.RemoveRange(0, _all.Count - MostRows);
            if (!_paused) Rebuild();
        }
        else if (!_paused)
            foreach (var row in added.Where(Shows)) _shown.Add(row);
        if (!_paused && FollowCheck.IsChecked == true && _shown.Count > 0) EventGrid.ScrollIntoView(_shown[^1]);
        Status();
    }

    /// <summary>The quest file a GUID's block is in: the copy the game reads, a reachable one first.</summary>
    private string? FileOf(Guid guid) =>
        _index?.Guids.TryGetValue(guid, out var copies) == true
            ? copies.Where(c => c.Effective).OrderByDescending(c => c.Reachable).Select(c => c.File).FirstOrDefault() ?? copies[0].File
            : null;

    private bool Shows(Row row)
    {
        bool kind = row.Event.Kind switch
        {
            NqhLogKind.Started => StartedCheck.IsChecked == true,
            NqhLogKind.Finished => FinishedCheck.IsChecked == true,
            NqhLogKind.Stopped => StoppedCheck.IsChecked == true,
            NqhLogKind.Replayed => ReplayedCheck.IsChecked == true,
            _ => InputAgainCheck.IsChecked == true,
        };
        if (!kind || WatchedCheck.IsChecked == true && !row.Event.Watched) return false;
        string search = SearchBox.Text.Trim();
        if (search.Length == 0) return true;
        if (W3ModWorkbench.ViewModels.QuestBlockItem.ParseGuid(search) is { } guid) return row.Event.Guid == guid;
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
            row.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || row.Kind.Contains(w, StringComparison.OrdinalIgnoreCase)
            || row.Event.ClassName.Contains(w, StringComparison.OrdinalIgnoreCase) || row.Event.Output.Contains(w, StringComparison.OrdinalIgnoreCase)
            || row.Guid.Contains(w, StringComparison.OrdinalIgnoreCase) || (row.Path?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private void Rebuild()
    {
        _shown = new ObservableCollection<Row>(_all.Where(Shows));
        EventGrid.ItemsSource = _shown;
        if (FollowCheck.IsChecked == true && _shown.Count > 0) EventGrid.ScrollIntoView(_shown[^1]);
    }

    private void Status()
    {
        if (_tail is null)
        {
            InfoText.Text = L.T("QuestLog.NoGame");
            return;
        }
        string what = _live
            ? File.Exists(_tail.Path) ? L.T("QuestLog.Following", _tail.Path) : L.T("QuestLog.Waiting", _tail.Path)
            : L.T("QuestLog.Reading", _tail.Path);
        InfoText.Text = $"{what}   ·   {L.T("QuestLog.Shown", _shown.Count, _all.Count)}" + (_paused ? "   ·   " + L.T("QuestLog.Paused") : "");
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        Rebuild();
        Status();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
        Status();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseText.Text = _paused ? L.T("QuestLog.Resume") : L.T("QuestLog.Pause");
        PauseIcon.Text = _paused ? "" : "";
        if (!_paused) Rebuild();
        Status();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _all.Clear();
        Rebuild();
        Status();
    }

    /// <summary>Reads a log from before: the last session's (NativeQuestHook.prev.log), or one kept elsewhere.</summary>
    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L.T("QuestLog.OpenLog"), Filter = L.T("QuestLog.LogFilter") };
        if (_binFolder is not null && Directory.Exists(_binFolder)) dialog.InitialDirectory = _binFolder;
        if (dialog.ShowDialog(this) != true) return;
        bool live = _binFolder is not null && string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(Path.Combine(_binFolder, LogName)), StringComparison.OrdinalIgnoreCase);
        Follow(dialog.FileName, live);
    }

    private void CopyIni_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(IniText.Text.Replace("\n", Environment.NewLine)); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        InfoText.Text = L.T("QuestLog.IniCopied");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_binFolder is null || !Directory.Exists(_binFolder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_binFolder}\"") { UseShellExecute = true });
    }

    private void Releases_Click(object sender, RoutedEventArgs e) => AssetBrowserWindow.OpenLink(Releases);

    private List<Row> Picked() => [.. EventGrid.SelectedItems.OfType<Row>()];

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenInGraph();

    private void OpenInGraph_Click(object sender, RoutedEventArgs e) => OpenInGraph();

    /// <summary>Opens the picked event's block in the quest graph viewer, in the file it's in.</summary>
    private void OpenInGraph()
    {
        if (EventGrid.SelectedItem is not Row row) return;
        if (row.Path is null)
        {
            _ = ShowMessage(L.T("QuestLog.OpenInGraph"), _index is null ? L.T("QuestLog.Indexing") : L.T("QuestLog.NotFound", row.Guid), MessageKind.Info);
            return;
        }
        QuestGraphWindow.OpenAt(row.Path, row.Event.Guid, _locator, this);
    }

    private void CopyGuids_Click(object sender, RoutedEventArgs e) => CopyGuids();

    private void CopyGuids()
    {
        var rows = Picked();
        if (rows.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.Guid).Distinct())); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        InfoText.Text = L.T("QuestGraph.CopiedGuids", rows.Select(r => r.Guid).Distinct().Count());
    }

    private void CopyLines_Click(object sender, RoutedEventArgs e)
    {
        var rows = Picked();
        if (rows.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.Event.Line))); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyGuids();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenInGraph();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && e.OriginalSource is not TextBox)
        {
            Close();
            e.Handled = true;
        }
    }
}
