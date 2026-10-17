using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Scripts.Navigation;

namespace W3ModWorkbench;

/// <summary>
/// Find in files, as in a text editor, for nothing but folders: type what to look for, say which files and where
/// (the game's scripts, the installed mods, the open projects or any folder) and every match is listed by file.
/// A line opens in a preview at the match. Needs no project or file to be open first.
/// </summary>
public partial class FindInFilesWindow : Window
{
    private const string ResultsTitle = "Find in files";

    private static FindInFilesWindow? _instance;
    private static readonly ConcurrentDictionary<string, ScriptFolder> Projects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A place to look: a name for the list, and the folders it stands for, worked out when a search starts.</summary>
    private sealed record Scope(string Label, Func<List<SearchRoot>> Roots, bool Choose = false, bool Custom = false);

    private readonly MainWindow _main;
    private readonly Settings _settings = App.Settings;
    private readonly ObservableCollection<Scope> _scopes = [];
    private readonly Scope _choose;
    private Scope? _current;
    private bool _loading;
    private CancellationTokenSource? _cts;
    private List<SearchRoot> _searched = [];

    private FindInFilesWindow(MainWindow main)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _main = main;
        Owner = main;
        Results.HideClose();
        Results.Open += OpenResult;

        string? game = GameLocator.Resolve(null);
        string? scripts = game is null ? null : Path.Combine(game, "content", "content0", "scripts");
        string? mods = game is null ? null : Path.Combine(game, "mods");
        if (scripts is not null && !Directory.Exists(scripts)) scripts = null;
        if (mods is not null && !Directory.Exists(mods)) mods = null;
        string? redkit = RedkitScripts();

        SearchRoot Game() => new(scripts!, "Game scripts", false);
        SearchRoot Mods() => new(mods!, "Installed mods", true);
        if (scripts is not null && mods is not null)
            _scopes.Add(new Scope("Game scripts and installed mods", () => [Game(), Mods()]));
        if (scripts is not null) _scopes.Add(new Scope("Game scripts", () => [Game()]));
        if (mods is not null) _scopes.Add(new Scope("Installed mods", () => [Mods()]));
        if (redkit is not null) _scopes.Add(new Scope("REDkit scripts", () => [new SearchRoot(redkit, "REDkit scripts", false)]));
        _scopes.Add(new Scope("Open script projects", () => [.. main.OpenArchives().OfType<ScriptFolder>()
            .Select(a => new SearchRoot(a.FilePath, a.Owner, !ScriptFolder.IsGameScripts(a.FilePath)))]));
        _choose = new Scope("A folder…", () => [], Choose: true);
        _scopes.Add(_choose);
        ScopeBox.DisplayMemberPath = nameof(Scope.Label);
        ScopeBox.ItemsSource = _scopes;

        FindBox.Text = _settings.FindText;
        FiltersBox.Text = _settings.FindFilters;
        MatchCaseToggle.IsChecked = _settings.FindMatchCase;
        WholeWordToggle.IsChecked = _settings.FindWholeWord;
        RegexToggle.IsChecked = _settings.FindRegex;
        SubfoldersCheck.IsChecked = _settings.FindSubfolders;
        _loading = true;
        if (Directory.Exists(_settings.FindFolder)) AddCustom(_settings.FindFolder);
        _current = _scopes.FirstOrDefault(s => s.Label == _settings.FindScope && !s.Choose) ?? _scopes[0];
        ScopeBox.SelectedItem = _current;
        _loading = false;
        UpdateScopeText(game is null);

        Results.ShowRows(ResultsTitle, "", [], "Type what to look for and press Enter. Files are read straight from disk, so nothing needs to be open.");
        Loaded += (_, _) =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        };
        Closing += (_, _) => Store();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _instance = null;
        };
    }

    /// <summary>Opens the window, or brings it forward when it's open. <paramref name="text"/> fills the box when given.</summary>
    public static void Open(MainWindow owner, string? text = null)
    {
        if (_instance is null)
        {
            _instance = new FindInFilesWindow(owner);
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
        if (!string.IsNullOrEmpty(text)) _instance.FindBox.Text = text;
        _instance.FindBox.Focus();
        _instance.FindBox.SelectAll();
    }

    /// <summary>REDkit's own scripts (r4data\scripts), next to the wcc_lite.exe set in Settings.</summary>
    private string? RedkitScripts()
    {
        if (string.IsNullOrWhiteSpace(_settings.WccLitePath)) return null;
        try
        {
            string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_settings.WccLitePath)!, "..", "..", "r4data", "scripts"));
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private void Store()
    {
        _settings.FindText = FindBox.Text;
        _settings.FindFilters = FiltersBox.Text;
        _settings.FindMatchCase = MatchCaseToggle.IsChecked == true;
        _settings.FindWholeWord = WholeWordToggle.IsChecked == true;
        _settings.FindRegex = RegexToggle.IsChecked == true;
        _settings.FindSubfolders = SubfoldersCheck.IsChecked == true;
        _settings.FindScope = _current is { Custom: false } ? _current.Label : "";
        _settings.FindFolder = _scopes.FirstOrDefault(s => s.Custom)?.Label ?? "";
    }

    // ---------------------------------------------------------------- where to look

    private void AddCustom(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\');
        foreach (var old in _scopes.Where(s => s.Custom).ToList()) _scopes.Remove(old);
        string source = Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;
        _scopes.Insert(_scopes.IndexOf(_choose), new Scope(folder, () => [new SearchRoot(folder, source, !ScriptFolder.IsGameScripts(folder))], Custom: true));
    }

    private void ScopeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || ScopeBox.SelectedItem is not Scope picked) return;
        if (picked.Choose)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Search the files in a folder",
                InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
            };
            _loading = true;
            if (dialog.ShowDialog(this) == true)
            {
                _settings.LastOpenFolder = dialog.FolderName;
                AddCustom(dialog.FolderName);
                _current = _scopes.First(s => s.Custom);
            }
            ScopeBox.SelectedItem = _current;
            _loading = false;
        }
        else _current = picked;
        UpdateScopeText(false);
    }

    private void UpdateScopeText(bool noGame)
    {
        var roots = _current?.Roots() ?? [];
        ScopeText.Text = roots.Count > 0 ? string.Join("   ·   ", roots.Select(r => r.Folder))
            : _current?.Label == "Open script projects" ? "No script project is open: open a folder of scripts in the main window first."
            : noGame ? "The game folder isn't set. Set it in Settings, or choose a folder."
            : "";
    }

    // ---------------------------------------------------------------- searching

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Find_Click(sender, e);
        e.Handled = true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        FindButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private Regex? BuildPattern(string text)
    {
        string pattern = RegexToggle.IsChecked == true ? text : Regex.Escape(text);
        if (WholeWordToggle.IsChecked == true) pattern = $@"\b(?:{pattern})\b";
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline
            | (MatchCaseToggle.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            Results.ShowRows(ResultsTitle, "", [], "That isn't a valid regular expression: " + ex.Message);
            return null;
        }
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        string text = FindBox.Text;
        if (text.Length == 0 || _current is null || BuildPattern(text) is not { } pattern) return;
        var roots = _current.Roots();
        if (roots.Count == 0)
        {
            Results.ShowRows(ResultsTitle, "", [], "There's no folder to look in. Pick one from the Look in list.");
            return;
        }
        string filters = FiltersBox.Text;
        bool subfolders = SubfoldersCheck.IsChecked == true;

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        SetBusy(true);
        Results.ShowBusy(ResultsTitle, "Listing the files…");
        StatusText.Text = "Listing the files…";
        try
        {
            var files = await Task.Run(() => FolderSearch.Files(roots, filters, subfolders, token), token);
            Results.ShowBusy(ResultsTitle, $"Looking for '{text}' in {files.Count:N0} files…");
            var progress = new Progress<int>(n =>
            {
                if (!token.IsCancellationRequested) StatusText.Text = $"Searched {n:N0} of {files.Count:N0} files…";
            });
            var result = await Task.Run(() => FolderSearch.Text(pattern, files, token, progress), token);
            if (token.IsCancellationRequested) return;

            _searched = roots;
            string summary = PreviewWindow.Summary(result.Hits, "match", "matches");
            Results.ShowRows(ResultsTitle, summary, ResultRow.ByFile(result.Hits),
                files.Count == 0 ? "No files there pass the filters." : $"Nothing matches '{text}' in {Format.Count(files.Count, "file")}.");
            StatusText.Text = $"{summary}  ·  searched {Format.Count(result.FilesSearched - result.FilesSkipped, "file")}"
                + (result.FilesSkipped > 0 ? $"  ·  {result.FilesSkipped:N0} skipped (too big or not text)" : "");
        }
        catch (OperationCanceledException)
        {
            if (_cts == cts)
            {
                Results.ShowRows(ResultsTitle, "", [], "The search was stopped.");
                StatusText.Text = "Stopped";
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            var inner = ex is AggregateException { InnerException: { } first } ? first : ex;
            Results.ShowRows(ResultsTitle, "", [], inner is RegexMatchTimeoutException
                ? "The search took too long and was stopped. Try a simpler pattern."
                : "The search failed: " + inner.Message);
            StatusText.Text = "Failed";
        }
        finally
        {
            if (_cts == cts)
            {
                _cts = null;
                SetBusy(false);
            }
            cts.Dispose();
        }
    }

    // ---------------------------------------------------------------- opening a match

    private async void OpenResult(ResultRow row, bool toSide)
    {
        if (row.File is not { } file) return;
        try
        {
            var root = _searched.FirstOrDefault(r => r.Folder.Equals(file.SourceKey, StringComparison.OrdinalIgnoreCase));
            var open = _main.OpenArchives().ToList();
            var (data, archive, entry) = await Task.Run(() =>
            {
                byte[] bytes = file.Read();
                if (root is null || !file.Path.EndsWith(".ws", StringComparison.OrdinalIgnoreCase)) return (bytes, (ScriptFolder?)null, (LooseScriptEntry?)null);
                // a project's script is opened as the main window would: from its scripts folder, so it can be edited and saved
                string project = root.Editable ? ScriptFolder.RootOf(file.Key, root.Folder) : root.Folder;
                var folder = open.OfType<ScriptFolder>().FirstOrDefault(a => a.FilePath.Equals(project, StringComparison.OrdinalIgnoreCase))
                    ?? (root.Editable ? Projects.GetOrAdd(project, ScriptFolder.Open) : Projects.GetOrAdd(project, ScriptFolder.Empty));
                var loose = folder.Entries.OfType<LooseScriptEntry>()
                    .FirstOrDefault(en => en.FullPath.Equals(file.Key, StringComparison.OrdinalIgnoreCase));
                return (bytes, folder, loose);
            });

            PreviewItem item = entry is not null ? _main.PreviewItemFor(entry, data)
                : archive is not null ? new PreviewItem(file.Path, file.Source, data, file, ScriptWorkspace.For(archive, _main.OpenArchives))
                : new PreviewItem(file.Path, file.Source, data);
            PreviewWindow.ShowAt(item, _main, row.Offset, row.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"{file.Path} could not be read: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- keys

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers == ModifierKeys.Shift;
        if (e.Key == Key.Escape)
        {
            if (_cts is not null) _cts.Cancel();
            else Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F4 && (Keyboard.Modifiers == ModifierKeys.None || shift))
        {
            if (Results.Step(shift ? -1 : 1) is { } row) OpenResult(row, toSide: false);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F)
        {
            FindBox.Focus();
            FindBox.SelectAll();
            e.Handled = true;
        }
    }
}
