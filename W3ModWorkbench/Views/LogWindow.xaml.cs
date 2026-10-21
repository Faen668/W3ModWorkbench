using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;

namespace W3ModWorkbench;

/// <summary>
/// Watches the game's scripts log (Documents\The Witcher 3\scriptslog.txt) while the game runs: new lines show as they're
/// written, can be narrowed by words or channel, and a line leads to the LogChannel call in the scripts that wrote it.
/// </summary>
public partial class LogWindow : Window, IMessageHost
{
    private const int MaxKept = 300_000;
    private const string AllChannels = "All channels";

    private static LogWindow? _instance;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly List<LogLine> _all = [];
    private readonly ObservableCollection<LogLine> _shown = [];
    private readonly Dictionary<string, int> _channels = new(StringComparer.OrdinalIgnoreCase);
    private LogTail _tail;
    private int _clearedAt; // lines up to this number are hidden by Clear
    private bool _channelsChanged;
    private CancellationTokenSource? _findCts;

    /// <summary>Documents\The Witcher 3\scriptslog.txt, where the game writes the log.</summary>
    public static string DefaultPath => GameDocuments.FileIn("scriptslog.txt");

    private LogWindow(string path)
    {
        InitializeComponent();
        _tail = new LogTail(path);
        LineList.ItemsSource = _shown;
        ChannelBox.ItemsSource = new List<string> { AllChannels };
        ChannelBox.SelectedIndex = 0;
        _timer.Tick += (_, _) => ReadNew();
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            Refilter();
        };
        Loaded += (_, _) =>
        {
            ReadNew();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _findCts?.Cancel();
            _instance = null;
        };
        UpdateFileText();
    }

    public static void Open(Window owner, string? path = null)
    {
        if (_instance is null)
        {
            _instance = new LogWindow(path ?? DefaultPath).Standalone(owner);
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            if (path is not null) _instance.Watch(path);
        }
    }

    private void Watch(string path)
    {
        _tail = new LogTail(path);
        _all.Clear();
        _shown.Clear();
        _channels.Clear();
        _clearedAt = 0;
        _channelsChanged = true;
        UpdateFileText();
        ReadNew();
    }

    private void UpdateFileText() =>
        FileText.Text = File.Exists(_tail.Path) ? _tail.Path : $"{_tail.Path} doesn't exist yet: it's made when the game starts with debug scripts on (-debugscripts).";

    // ---------------------------------------------------------------- reading

    private void ReadNew()
    {
        if (PauseToggle.IsChecked == true) return;
        List<LogLine> lines;
        try
        {
            lines = _tail.ReadNew();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"The log can't be read right now: {ex.Message}";
            return;
        }
        if (_tail.Restarted)
        {
            _all.Clear();
            _shown.Clear();
            _channels.Clear();
            _clearedAt = 0;
            _channelsChanged = true;
            UpdateFileText();
        }
        if (lines.Count == 0)
        {
            if (_all.Count == 0) UpdateStatus();
            return;
        }
        UpdateFileText();
        _all.AddRange(lines);
        foreach (var line in lines)
        {
            if (!_channels.TryGetValue(line.Channel, out int n)) _channelsChanged = true;
            _channels[line.Channel] = n + 1;
        }
        if (_all.Count > MaxKept)
        {
            int drop = _all.Count - MaxKept;
            _all.RemoveRange(0, drop);
            Refilter();
        }
        else
        {
            var filter = Filter();
            foreach (var line in lines)
                if (filter(line)) _shown.Add(line);
        }
        if (_channelsChanged) UpdateChannels();
        UpdateStatus();
        if (FollowToggle.IsChecked == true && _shown.Count > 0) LineList.ScrollIntoView(_shown[^1]);
    }

    private void UpdateChannels()
    {
        _channelsChanged = false;
        string? picked = ChannelBox.SelectedItem as string;
        var items = new List<string> { AllChannels };
        items.AddRange(_channels.Where(c => c.Key.Length > 0).OrderByDescending(c => c.Value).Select(c => c.Key));
        ChannelBox.SelectionChanged -= Filter_Changed;
        ChannelBox.ItemsSource = items;
        ChannelBox.SelectedItem = picked is not null && items.Contains(picked) ? picked : AllChannels;
        ChannelBox.SelectionChanged += Filter_Changed;
    }

    private void UpdateStatus()
    {
        int total = _all.Count(l => l.Number > _clearedAt);
        StatusText.Text = total == 0
            ? (PauseToggle.IsChecked == true ? "Paused" : "Waiting for the game to write…")
            : $"{_shown.Count:N0} of {total:N0} lines" + (PauseToggle.IsChecked == true ? "  ·  paused" : "");
    }

    // ---------------------------------------------------------------- filtering

    private Func<LogLine, bool> Filter()
    {
        string? channel = ChannelBox.SelectedItem as string is { } c && c != AllChannels ? c : null;
        bool problems = ProblemsToggle.IsChecked == true;
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wanted = words.Where(w => !w.StartsWith('!')).ToArray();
        var unwanted = words.Where(w => w.StartsWith('!') && w.Length > 1).Select(w => w[1..]).ToArray();
        int cleared = _clearedAt;
        return line => line.Number > cleared
            && (channel is null || line.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))
            && (!problems || line.IsProblem)
            && wanted.All(w => line.Text.Contains(w, StringComparison.OrdinalIgnoreCase))
            && !unwanted.Any(w => line.Text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void Refilter()
    {
        var filter = Filter();
        _shown.Clear();
        foreach (var line in _all)
            if (filter(line)) _shown.Add(line);
        UpdateStatus();
        if (FollowToggle.IsChecked == true && _shown.Count > 0) LineList.ScrollIntoView(_shown[^1]);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Refilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (FollowToggle.IsChecked == true && _shown.Count > 0) LineList.ScrollIntoView(_shown[^1]);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (PauseToggle.IsChecked != true) ReadNew();
        UpdateStatus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _clearedAt = _all.Count > 0 ? _all[^1].Number : 0;
        Refilter();
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Watch a log file",
            Filter = "Logs (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_tail.Path) is { } dir && Directory.Exists(dir) ? dir : "",
        };
        if (dialog.ShowDialog(this) == true) Watch(dialog.FileName);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_tail.Path)) ShellReveal.Show([_tail.Path]);
        else if (Path.GetDirectoryName(_tail.Path) is { } dir && Directory.Exists(dir)) ShellReveal.Open(dir);
    }

    private void OnlyChannel_Click(object sender, RoutedEventArgs e)
    {
        if (LineList.SelectedItem is LogLine { Channel.Length: > 0 } line) ChannelBox.SelectedItem = line.Channel;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void CopySelected()
    {
        var lines = LineList.SelectedItems.OfType<LogLine>().OrderBy(l => l.Number).Select(l => l.Text).ToList();
        if (lines.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    // ---------------------------------------------------------------- finding the script

    private void LineList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _ = FindSourceAsync();

    private void FindSource_Click(object sender, RoutedEventArgs e) => _ = FindSourceAsync();

    private void LineList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = FindSourceAsync();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopySelected();
        else return;
        e.Handled = true;
    }

    /// <summary>Looks for the call that wrote the line in the project, the installed mods and the game: opens it when there's one, offers a choice when there are several.</summary>
    private async Task FindSourceAsync()
    {
        if (LineList.SelectedItem is not LogLine line) return;
        _findCts?.Cancel();
        var cts = _findCts = new CancellationTokenSource();
        string? game = GameLocator.Resolve(null);
        StatusText.Text = $"Looking for the LogChannel call on '{line.Channel}'…";
        List<LogSource> found;
        try
        {
            found = await Task.Run(() => LogSources.Find(line, ScriptCorpus.Gather(new ScriptCorpus.Options(game), cts.Token), cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        UpdateStatus();
        if (cts != _findCts) return;
        if (found.Count == 0)
        {
            string where = game is null ? "the open project" : "the open project, the installed mods (loose and compiled) or the game's scripts";
            string channel = line.Channel.Length == 0 ? "Log(…)" : $"LogChannel('{line.Channel}', …)";
            await ShowMessage("No script was found that writes this line",
                $"Nothing in {where} calls {channel}. It may come from the engine itself, or a mod that isn't installed.", MessageKind.Info);
            return;
        }
        if (found.Count == 1)
        {
            Open(found[0]);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = LineList, Placement = PlacementMode.MousePoint };
        foreach (var source in found.Take(25))
        {
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = source.Hit.LineText.Trim(), FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), MaxWidth = 760,
                TextTrimming = TextTrimming.CharacterEllipsis });
            header.Children.Add(new TextBlock { Text = $"{source.File.File.Source}  ·  {source.File.File.Path} : {source.Hit.Line}", Opacity = 0.65, FontSize = 12 });
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Open(source);
            menu.Items.Add(item);
        }
        if (found.Count > 25) menu.Items.Add(new MenuItem { Header = $"…and {found.Count - 25} more", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void Open(LogSource source) => _ = ScriptOpener.OpenAsync(source.File, source.Hit.Offset, source.Hit.Length, this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
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
