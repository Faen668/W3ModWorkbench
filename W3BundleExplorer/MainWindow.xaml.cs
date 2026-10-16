using System.Collections.ObjectModel;
using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using W3BundleExplorer.Core;
using W3BundleExplorer.Core.Scripts;
using W3BundleExplorer.ViewModels;

namespace W3BundleExplorer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FolderNode> _roots = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly Settings _settings = App.Settings;
    private List<EntryItem> _shown = [];
    private int _checkedCount;
    private long _checkedBytes;
    private bool _statusQueued;
    private CancellationTokenSource? _extractCts;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        FolderTree.ItemsSource = _roots;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshView(); };

        var assembly = Assembly.GetExecutingAssembly();
        string copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright © 2026 Faen90";
        AboutText.Text = $"v{assembly.GetName().Version?.ToString(3)}  ·  {copyright.Replace("Copyright ", "")}";

        OutputBox.Text = _settings.OutputFolder;
        KeepStructureCheck.IsChecked = _settings.KeepStructure;
        OpenWhenDoneCheck.IsChecked = _settings.OpenWhenDone;

        if (args.Length > 0)
            Loaded += async (_, _) => await LoadPathsAsync(args);
    }

    // ---------------------------------------------------------------- loading

    private async void OpenBundles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open bundles or compiled scripts",
            Filter = "Bundles and compiled scripts (*.bundle;*.rsblob)|*.bundle;*.rsblob|Bundles (*.bundle)|*.bundle|"
                   + "Compiled scripts (*.rsblob)|*.rsblob|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastOpenFolder = Path.GetDirectoryName(dialog.FileNames[0]) ?? "";
        await LoadPathsAsync(dialog.FileNames);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Open every bundle and compiled script in a folder",
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastOpenFolder = dialog.FolderName;
        await LoadPathsAsync([dialog.FolderName]);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await LoadPathsAsync(paths);
    }

    private async Task LoadPathsAsync(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || ScriptBlob.LooksLikeBlob(f))
                        .Order(StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    ShowError($"Could not read the folder {path}.\n\n{ex.Message}");
                }
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        var loaded = _roots.Select(r => r.Archive.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        files = files.Select(Path.GetFullPath).Where(loaded.Add).ToList();
        if (files.Count == 0)
        {
            if (!paths.Any(File.Exists)) SetStatus("No .bundle or .rsblob files found there.");
            return;
        }

        LoadingOverlay.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Collapsed;
        LoadingProgress.Maximum = files.Count;
        var failures = new List<string>();
        FolderNode? first = null;

        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            LoadingText.Text = files.Count == 1 ? $"Loading {Path.GetFileName(file)}…" : $"Loading {Path.GetFileName(file)}  ({i + 1} of {files.Count})";
            LoadingProgress.Value = i;
            try
            {
                var node = await Task.Run(() => FolderNode.Build(OpenArchive(file), OnEntryChecked));
                node.IsExpanded = files.Count == 1;
                _roots.Add(node);
                first ??= node;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        LoadingOverlay.Visibility = Visibility.Collapsed;
        UpdateEmptyState();

        if (first is not null)
        {
            first.IsSelected = true;
            if (FolderTree.SelectedItem is null) RefreshView();
        }
        int totalFiles = _roots.Sum(r => r.TotalFiles);
        SetStatus($"{Format.Count(_roots.Count, "archive")} open, {Format.Count(totalFiles, "file")}");

        if (failures.Count > 0)
            ShowError("Some files could not be opened:\n\n" + string.Join("\n", failures.Take(15)) +
                      (failures.Count > 15 ? $"\n…and {failures.Count - 15} more" : ""));
    }

    private static IArchive OpenArchive(string file) =>
        ScriptBlob.LooksLikeBlob(file) ? ScriptArchive.Open(file) : Bundle.Open(file);

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        _roots.Clear();
        AfterBundlesClosed();
    }

    private void TreeCloseBundle_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode node) return;
        while (node.Parent is not null) node = node.Parent;
        _roots.Remove(node);
        AfterBundlesClosed();
    }

    private void AfterBundlesClosed()
    {
        _checkedCount = 0;
        _checkedBytes = 0;
        foreach (var item in _roots.SelectMany(r => r.AllFiles()).Where(i => i.IsChecked))
        {
            _checkedCount++;
            _checkedBytes += item.Size;
        }
        UpdateEmptyState();
        RefreshView();
        SetStatus(_roots.Count == 0 ? "Ready" : $"{Format.Count(_roots.Count, "archive")} open");
    }

    private void UpdateEmptyState()
    {
        bool any = _roots.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        BrowserPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        CloseAllButton.IsEnabled = any;
    }

    // ---------------------------------------------------------------- view

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => RefreshView();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = SearchBox.Text.Length == 0;
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void RefreshView()
    {
        IEnumerable<EntryItem> items = FolderTree.SelectedItem is FolderNode node
            ? node.AllFiles()
            : _roots.SelectMany(r => r.AllFiles());

        var match = BuildMatcher(SearchBox.Text);
        if (match is not null)
            items = items.Where(match);

        _shown = items.ToList();
        FileGrid.ItemsSource = _shown;
        NoMatchesText.Visibility = _shown.Count == 0 && _roots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtractShownButton.IsEnabled = _shown.Count > 0;
        ExtractShownText.Text = _shown.Count > 0 ? $"Extract shown ({_shown.Count:N0})" : "Extract shown";
        QueueStatusUpdate();
    }

    /// <summary>Every word must appear in the path; words with * or ? are wildcards on the file name.</summary>
    private static Func<EntryItem, bool>? BuildMatcher(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return null;

        var tests = words.Select<string, Func<EntryItem, bool>>(w =>
        {
            if (w.Contains('*') || w.Contains('?'))
            {
                var regex = new Regex("^" + Regex.Escape(w).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return i => regex.IsMatch(i.Name);
            }
            return i => i.Path.Contains(w, StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        return i => tests.All(t => t(i));
    }

    // ---------------------------------------------------------------- checking

    private void OnEntryChecked(EntryItem item, bool isChecked)
    {
        _checkedCount += isChecked ? 1 : -1;
        _checkedBytes += isChecked ? item.Size : -item.Size;
        QueueStatusUpdate();
    }

    private void SetChecked(IEnumerable<EntryItem> items, bool value)
    {
        foreach (var item in items)
            item.IsChecked = value;
    }

    private void HeaderCheck_Click(object sender, RoutedEventArgs e)
    {
        bool allChecked = _shown.Count > 0 && _shown.All(i => i.IsChecked);
        SetChecked(_shown, !allChecked);
    }

    private void GridCheck_Click(object sender, RoutedEventArgs e) => SetChecked(Highlighted(), true);

    private void GridUncheck_Click(object sender, RoutedEventArgs e) => SetChecked(Highlighted(), false);

    private void TreeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is FolderNode node) SetChecked(node.AllFiles(), true);
    }

    private void TreeUncheck_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is FolderNode node) SetChecked(node.AllFiles(), false);
    }

    private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not EntryItem item) return;
        if (item.IsPreviewable)
            _ = PreviewAsync(item);
        else
            item.IsChecked = !item.IsChecked;
    }

    private void GridCopyPath_Click(object sender, RoutedEventArgs e) => CopyHighlightedPaths();

    private void GridPreview_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault(i => i.IsPreviewable) is { } item)
            _ = PreviewAsync(item);
    }

    private async Task PreviewAsync(EntryItem item)
    {
        try
        {
            var entry = item.Entry;
            byte[] data = await Task.Run(() =>
            {
                if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
                using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return entry.Read(fs);
            });
            new PreviewWindow(entry.Path, item.BundleName, data) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            ShowError($"{item.Name} could not be read.\n\n{ex.Message}");
        }
    }

    private void CopyHighlightedPaths()
    {
        var paths = Highlighted().Select(i => i.Path).ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private List<EntryItem> Highlighted() => FileGrid.SelectedItems.OfType<EntryItem>().ToList();

    private void QueueStatusUpdate()
    {
        if (_statusQueued) return;
        _statusQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _statusQueued = false;
            CheckedText.Text = _checkedCount > 0 ? $"{_checkedCount:N0} checked · {Format.Bytes(_checkedBytes)}" : "";
            bool canExtract = _checkedCount > 0 && _extractCts is null;
            ExtractCheckedButton.IsEnabled = canExtract;
            // the Fluent accent style is near invisible when disabled, so only use it while enabled.
            // ClearValue (not Style = null) falls back to the theme's normal button style.
            if (canExtract)
                ExtractCheckedButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
            else
                ExtractCheckedButton.ClearValue(StyleProperty);
            ExtractCheckedText.Text = _checkedCount > 0 ? $"Extract checked ({_checkedCount:N0})" : "Extract checked";

            int checkedShown = _shown.Count(i => i.IsChecked);
            HeaderCheck.IsChecked = checkedShown == 0 ? false : checkedShown == _shown.Count ? true : null;
        });
    }

    // ---------------------------------------------------------------- extraction

    private async void ExtractChecked_Click(object sender, RoutedEventArgs e) =>
        await ExtractAsync(_roots.SelectMany(r => r.AllFiles()).Where(i => i.IsChecked).ToList());

    private async void ExtractShown_Click(object sender, RoutedEventArgs e) => await ExtractAsync(_shown);

    private async void GridExtract_Click(object sender, RoutedEventArgs e) => await ExtractAsync(Highlighted());

    private async void TreeExtract_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is FolderNode node) await ExtractAsync(node.AllFiles().ToList());
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e) => PickOutputFolder();

    private bool PickOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to extract files",
            InitialDirectory = Directory.Exists(OutputBox.Text) ? OutputBox.Text : "",
        };
        if (dialog.ShowDialog(this) != true) return false;
        OutputBox.Text = dialog.FolderName;
        return true;
    }

    private async Task ExtractAsync(List<EntryItem> items)
    {
        if (items.Count == 0 || _extractCts is not null) return;
        if (string.IsNullOrWhiteSpace(OutputBox.Text) && !PickOutputFolder()) return;

        string output = OutputBox.Text.Trim();
        bool keepStructure = KeepStructureCheck.IsChecked == true;
        bool openWhenDone = OpenWhenDoneCheck.IsChecked == true;
        try
        {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
        }
        catch (Exception ex)
        {
            ShowError($"The output folder can't be used.\n\n{ex.Message}");
            return;
        }

        _extractCts = new CancellationTokenSource();
        SetBusy(true);
        ExtractProgressBar.Maximum = items.Count;
        ExtractProgressBar.Value = 0;
        var progress = new Progress<ExtractProgress>(p =>
        {
            ExtractProgressBar.Value = p.Done;
            SetStatus($"Extracting {p.Done:N0} of {p.Total:N0}  ·  {p.CurrentPath}");
        });

        var entries = items.Select(i => i.Entry).ToList();
        var token = _extractCts.Token;
        ExtractResult result;
        try
        {
            result = await Task.Run(() => Extractor.Extract(entries, output, keepStructure, progress, token));
        }
        finally
        {
            _extractCts.Dispose();
            _extractCts = null;
            SetBusy(false);
        }

        string summary = $"{(result.Cancelled ? "Cancelled after extracting" : "Extracted")} {Format.Count(result.Extracted, "file")} ({Format.Bytes(result.Bytes)}) to {output}";
        SetStatus(summary);

        if (result.Failures.Count > 0)
        {
            ShowError($"{Format.Count(result.Failures.Count, "file")} could not be extracted:\n\n" +
                      string.Join("\n", result.Failures.Take(15).Select(f => $"{f.Path}\n    {f.Error}")) +
                      (result.Failures.Count > 15 ? $"\n…and {result.Failures.Count - 15} more" : ""));
        }

        if (openWhenDone && result.Extracted > 0)
        {
            string target = output;
            if (entries.Count == 1 && keepStructure)
                target = Path.GetDirectoryName(Path.Combine(output, entries[0].Path)) ?? output;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _extractCts?.Cancel();

    private void SetBusy(bool busy)
    {
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ExtractShownButton.IsEnabled = !busy && _shown.Count > 0;
        FolderTree.IsEnabled = !busy;
        CloseAllButton.IsEnabled = !busy && _roots.Count > 0;
        QueueStatusUpdate();
    }

    // ---------------------------------------------------------------- misc

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            Help_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemComma)
        {
            Settings_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.O)
        {
            OpenBundles_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.E && ExtractCheckedButton.IsEnabled)
        {
            ExtractChecked_Click(sender, e);
            e.Handled = true;
        }
        else if (FileGrid.IsKeyboardFocusWithin && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            var items = Highlighted();
            SetChecked(items, !items.All(i => i.IsChecked));
            e.Handled = true;
        }
        else if (FileGrid.IsKeyboardFocusWithin && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            GridPreview_Click(sender, e);
            e.Handled = true;
        }
        else if (FileGrid.IsKeyboardFocusWithin && ctrl && e.Key == Key.C)
        {
            CopyHighlightedPaths();
            e.Handled = true;
        }
        else if (SearchBox.IsKeyboardFocusWithin && e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void FolderTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // select the item under the mouse so the context menu acts on it
        if (FindParent<TreeViewItem>(e.OriginalSource as DependencyObject) is { DataContext: FolderNode node } item)
        {
            node.IsSelected = true;
            item.Focus();
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e) => Help.HelpWindow.ShowHelp(this);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        StoreExtractOptions();
        if (new SettingsWindow(_settings) { Owner = this }.ShowDialog() == true)
        {
            OutputBox.Text = _settings.OutputFolder;
            KeepStructureCheck.IsChecked = _settings.KeepStructure;
            OpenWhenDoneCheck.IsChecked = _settings.OpenWhenDone;
        }
    }

    private void StoreExtractOptions()
    {
        _settings.OutputFolder = OutputBox.Text.Trim();
        _settings.KeepStructure = KeepStructureCheck.IsChecked == true;
        _settings.OpenWhenDone = OpenWhenDoneCheck.IsChecked == true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _extractCts?.Cancel();
        StoreExtractOptions();
        _settings.Save();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "W3 Bundle Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not T)
            child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        return child as T;
    }
}
