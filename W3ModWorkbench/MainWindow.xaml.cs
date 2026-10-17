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
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

public partial class MainWindow : Window, IMessageHost
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
            Title = "Open bundles, scripts or strings",
            Filter = "Bundles, scripts, strings and XML (*.bundle;*.rsblob;*.ws;*.w3strings;*.csv;*.xml)|*.bundle;*.rsblob;*.ws;*.w3strings;*.csv;*.xml|"
                   + "Bundles (*.bundle)|*.bundle|Compiled scripts (*.rsblob)|*.rsblob|Scripts, opened with their project (*.ws)|*.ws|"
                   + "Strings (*.w3strings;*.csv)|*.w3strings;*.csv|XML, opened in the XML editor (*.xml)|*.xml|All files (*.*)|*.*",
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
            Title = "Open every bundle, compiled script and script project in a folder",
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
        // string tables open in an editor of their own rather than as archives
        var strings = paths.Where(p => File.Exists(p) && StringsWindow.CanOpen(p)).ToList();
        foreach (string path in strings) StringsWindow.Open(path, this);
        paths = [.. paths.Except(strings)];
        // so do XML files, which open in the XML editor
        var xml = paths.Where(p => File.Exists(p) && XmlWindow.CanOpen(p)).ToList();
        foreach (string path in xml) XmlWindow.Open(path, this);
        paths = [.. paths.Except(xml)];
        if ((strings.Count > 0 || xml.Count > 0) && !paths.Any()) return;

        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    var found = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).ToList();
                    files.AddRange(found
                        .Where(f => f.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || ScriptBlob.LooksLikeBlob(f))
                        .Order(StringComparer.OrdinalIgnoreCase));
                    // each folder of .w3strings files is listed as one entry, beside the bundles
                    files.AddRange(found
                        .Where(f => f.EndsWith(".w3strings", StringComparison.OrdinalIgnoreCase))
                        .Select(f => Path.GetDirectoryName(f)!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase));
                    // loose scripts open as script projects, one per scripts folder, which can be edited and compiled
                    files.AddRange(found
                        .Where(f => f.EndsWith(".ws", StringComparison.OrdinalIgnoreCase))
                        .Select(f => ScriptFolder.RootOf(f, path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(root => !ScriptFolder.IsGameScripts(root))
                        .Order(StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    ShowError($"Could not read the folder {path}.\n\n{ex.Message}");
                }
            }
            else if (File.Exists(path) && path.EndsWith(".ws", StringComparison.OrdinalIgnoreCase))
            {
                // a script opens with the rest of its project: the scripts folder it's in, or its own folder
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                string drive = Path.GetPathRoot(folder)!;
                string root = ScriptFolder.RootOf(path, drive);
                files.Add(root.Equals(drive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? folder : root);
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        var loaded = _roots.Select(r => r.Archive.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        files = [.. files.Select(Path.GetFullPath).Where(loaded.Add)];
        if (files.Count == 0)
        {
            if (!paths.Any(File.Exists)) SetStatus("No .bundle, .rsblob, .ws or .w3strings files found there.");
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

    /// <summary>A folder is a folder of strings, or else a script project.</summary>
    private static IArchive OpenArchive(string file) =>
        Directory.Exists(file) ? Directory.EnumerateFiles(file, "*.w3strings").Any() ? StringsFolder.Open(file) : ScriptFolder.Open(file)
        : ScriptBlob.LooksLikeBlob(file) ? ScriptArchive.Open(file)
        : Bundle.Open(file);

    /// <summary>Opens a folder of scripts as a project, or finds it open already. Null when it couldn't be opened.</summary>
    public async Task<ScriptFolder?> OpenScriptProjectAsync(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\');
        await LoadPathsAsync([folder]);
        return _roots.Select(r => r.Archive).OfType<ScriptFolder>()
            .FirstOrDefault(a => a.FilePath.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Closes an open archive, by its path.</summary>
    public void CloseArchive(string filePath)
    {
        foreach (var node in _roots.Where(r => r.Archive.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).ToList())
            _roots.Remove(node);
        AfterBundlesClosed();
    }

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

        _shown = [.. items];
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

    private static void SetChecked(IEnumerable<EntryItem> items, bool value)
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
        if (item.Entry is StringsEntry strings)
            StringsWindow.Open(strings.FullPath, this);
        else if (XmlWindow.CanOpen(item.Name))
            _ = OpenXmlAsync(item);
        else if (item.IsPreviewable)
            _ = PreviewAsync(item);
        else
            item.IsChecked = !item.IsChecked;
    }

    private void GridCopyPath_Click(object sender, RoutedEventArgs e) => CopyHighlightedPaths();

    private void GridPreview_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault(i => i.IsPreviewable || i.Entry is StringsEntry) is not { } item) return;
        if (item.Entry is StringsEntry strings) StringsWindow.Open(strings.FullPath, this);
        else _ = PreviewAsync(item);
    }

    private void FileGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        GridExternalItem.IsEnabled = Highlighted().Any(i => i.IsPreviewable);

    /// <summary>
    /// Opens the highlighted text files in the app Windows uses for their type: a project's file itself, or a
    /// read-only copy of one inside a bundle or compiled scripts.
    /// </summary>
    private async void GridExternal_Click(object sender, RoutedEventArgs e)
    {
        var items = Highlighted().Where(i => i.IsPreviewable).Take(20).ToList();
        int copies = 0;
        string copied = "";
        foreach (var item in items)
        {
            try
            {
                if (item.Entry is LooseScriptEntry loose)
                {
                    ExternalEditor.Open(loose.FullPath);
                    continue;
                }
                var entry = item.Entry;
                byte[] data = await Task.Run(() =>
                {
                    if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
                    using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return entry.Read(fs);
                });
                ExternalEditor.OpenCopy(item.BundleName, entry.Path, data);
                copies++;
                copied = item.Name;
            }
            catch (Exception ex)
            {
                ShowError($"{item.Name} could not be opened in an external editor.\n\n{ex.Message}");
                return;
            }
        }
        if (copies > 0)
            SetStatus(copies == 1 ? $"Opened a read-only copy of {copied}: files inside archives can't be changed in place"
                : $"Opened read-only copies of {copies} files: files inside archives can't be changed in place");
    }

    /// <summary>Opens an XML file from a bundle (or a loose one in a project) in the XML editor.</summary>
    private async Task OpenXmlAsync(EntryItem item)
    {
        try
        {
            var entry = item.Entry;
            if (entry is LooseScriptEntry loose)
            {
                XmlWindow.Open(loose.FullPath, this);
                return;
            }
            byte[] data = await Task.Run(() =>
            {
                if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
                using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return entry.Read(fs);
            });
            XmlWindow.OpenData(item.Name, $"{item.BundleName}  ·  {entry.Path}", data, this);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"{item.Name} could not be opened.\n\n{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- tools

    private void Tools_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ToolsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Add(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Add("Search game strings…", "Ctrl+Shift+G", OpenGameStrings);
        Add("Stub generator…", "Ctrl+Shift+W", OpenStubGenerator);
        Add("Source inspector…", "Ctrl+Shift+I", () => SourceInspectorWindow.Open(this));
        Add("Load order analyser…", "Ctrl+Shift+L", () => LoadOrderWindow.Open(this));
        menu.Items.Add(new Separator());
        Add("New mod menu…", "", () => XmlWindow.OpenNewMenu(this));
        Add("Open an XML file…", "", OpenXmlFile);
        menu.IsOpen = true;
    }

    private void OpenGameStrings()
    {
        if (GameLocator.Resolve(null) is { } folder) GameStringsWindow.Open(this, folder);
        else ShowError("The game folder isn't set.\n\nSearching the game's strings needs The Witcher 3's folder. Set it in Settings.");
    }

    private void OpenStubGenerator() => StubWindow.Open(this, GameLocator.Resolve(null));

    private void OpenXmlFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open an XML file",
            Filter = "XML (*.xml)|*.xml|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastOpenFolder = Path.GetDirectoryName(dialog.FileNames[0]) ?? "";
        foreach (string path in dialog.FileNames) XmlWindow.Open(path, this);
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
            PreviewWindow.Show(PreviewItemFor(entry, data), this);
        }
        catch (Exception ex)
        {
            ShowError($"{item.Name} could not be read.\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// A file to preview. Scripts can follow names to their definitions in the same archive, the other open ones
    /// and the game's scripts, and a script project's files can be edited.
    /// </summary>
    public PreviewItem PreviewItemFor(ArchiveEntry entry, byte[] data)
    {
        bool script = Path.GetExtension(entry.Path).Equals(".ws", StringComparison.OrdinalIgnoreCase);
        return new PreviewItem(entry.Path, entry.Archive.Owner + "\\" + entry.Archive.Name, data,
            script ? ScriptWorkspace.FileFor(entry) : null,
            script ? ScriptWorkspace.For(entry.Archive, () => _roots.Select(r => r.Archive)) : null);
    }

    private void CopyHighlightedPaths()
    {
        var paths = Highlighted().Select(i => i.Path).ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private List<EntryItem> Highlighted() => [.. FileGrid.SelectedItems.OfType<EntryItem>()];

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
        await ExtractAsync([.. _roots.SelectMany(r => r.AllFiles()).Where(i => i.IsChecked)]);

    private async void ExtractShown_Click(object sender, RoutedEventArgs e) => await ExtractAsync(_shown);

    private async void GridExtract_Click(object sender, RoutedEventArgs e) => await ExtractAsync(Highlighted());

    private async void TreeExtract_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is FolderNode node) await ExtractAsync([.. node.AllFiles()]);
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
        if (Message.IsOpen) return; // a message is up: it takes the keys, and Esc closes it
        if (e.Key == Key.Escape && AboutPopup.IsOpen)
        {
            AboutPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
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
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.G)
        {
            OpenGameStrings();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.I)
        {
            SourceInspectorWindow.Open(this);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.L)
        {
            LoadOrderWindow.Open(this);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.W)
        {
            OpenStubGenerator();
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

    // ---------------------------------------------------------------- about

    /// <summary>Opens the About card under the i button, its right edge in line with the button's. It stays until you click elsewhere, press Esc, or click the button again.</summary>
    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (AboutPopup.IsOpen)
        {
            AboutPopup.IsOpen = false;
            return;
        }
        var assembly = Assembly.GetExecutingAssembly();
        AboutVersionText.Text = $"Version {assembly.GetName().Version?.ToString(3)}  ·  by Faen90";
        AboutCopyrightText.Text = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright © 2026 Faen90";
        AboutSettingsText.Text = $"Settings are saved in {Settings.FilePath}";
        // the card sits inside a 12px margin that leaves room for its shadow
        AboutPopup.HorizontalOffset = AboutButton.ActualWidth - AboutCard.Width - 12;
        AboutPopup.VerticalOffset = AboutButton.ActualHeight + 2;
        AboutPopup.IsOpen = true;
    }

    /// <summary>A click anywhere but the card or its button closes the card. The click carries on to whatever it landed on.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (AboutPopup.IsOpen && !AboutCard.IsMouseOver && !AboutButton.IsMouseOver) AboutPopup.IsOpen = false;
    }

    private void Window_Deactivated(object? sender, EventArgs e) => AboutPopup.IsOpen = false;

    private void AboutLicence_Click(object sender, RoutedEventArgs e)
    {
        AboutPopup.IsOpen = false;
        SettingsWindow.ShowLicence(this);
    }

    private void AboutNotices_Click(object sender, RoutedEventArgs e)
    {
        AboutPopup.IsOpen = false;
        SettingsWindow.ShowNotices(this);
    }

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

    private bool _closeConfirmed;

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // scripts edited in the previews and not saved: asked about here, once, before the previews close with this window
        if (!_closeConfirmed && PreviewWindow.UnsavedDocuments() is { Count: > 0 } unsaved)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync(unsaved);
            return;
        }
        PreviewWindow.SkipUnsavedCheck = true;
        _extractCts?.Cancel();
        StoreExtractOptions();
        _settings.Save();
    }

    private async Task ConfirmCloseAsync(List<PreviewDocument> unsaved)
    {
        var choice = await Message.Ask(unsaved.Count == 1 ? $"Save the changes to {unsaved[0].FileName}?" : $"Save the changes to {unsaved.Count} scripts?",
            PreviewWindow.Names(unsaved) + (unsaved.Count == 1 ? " has" : " have") + " changes that are lost if you don't save them.",
            MessageKind.Question, unsaved.Count == 1 ? "Save" : "Save all", "Don't save");
        if (choice == MessageChoice.Cancel || choice == MessageChoice.Primary && !PreviewWindow.SaveAll(unsaved)) return;
        _closeConfirmed = true;
        Close();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>A warning; its first paragraph, when there's more than one, is its title.</summary>
    private void ShowError(string message)
    {
        var parts = message.Split("\n\n", 2);
        _ = parts.Length == 2
            ? ShowMessage(parts[0], parts[1], MessageKind.Warning)
            : ShowMessage(null, message, MessageKind.Warning);
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not T)
            child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        return child as T;
    }
}
