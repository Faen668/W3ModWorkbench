using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Script Bench: the window every script opens in, with the scripts it can reach beside the tabs. The project's own
/// scripts are the ones that can be changed and are compiled; the game's and the installed mods' are for reading,
/// for looking things up and for overriding, which copies the game's file into the project. The status bar says what
/// compiling reads and with which compiler.
/// </summary>
public partial class PreviewWindow
{
    private static PreviewWindow? _bench;

    private bool _isBench;

    /// <summary>Whether this is Script Bench, rather than a window for other files.</summary>
    public bool IsBench => _isBench;
    private MainWindow? _benchMain;
    private readonly ObservableCollection<BenchNode> _benchRoots = [];
    private BenchNode? _mine, _game, _mods;

    /// <summary>The scripts in the tree by the key their tab has, to find the file in front.</summary>
    private readonly Dictionary<string, BenchNode> _benchKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scripts in the tree by their file on disk, for a tab opened some other way (go to definition names the game's files by their path).</summary>
    private readonly Dictionary<string, BenchNode> _benchPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's scripts by their path under the scripts folder, to tell what the project's override.</summary>
    private readonly Dictionary<string, BenchNode> _gameByPath = new(StringComparer.OrdinalIgnoreCase);

    private string? _gameScripts;
    private string? _modsFolder;

    /// <summary>The game's scripts as the tree lists them, for a script opened without a tab to open it from.</summary>
    private ScriptFolder? _gameArchive;
    private int _benchBuild;
    private TaskCompletionSource<string?>? _benchName;
    private Func<string, string?>? _benchNameCheck;

    /// <summary>Opens Script Bench, or brings it forward.</summary>
    public static void OpenBench(MainWindow main) => BenchFor(main)?.BringForward();

    /// <summary>Script Bench, made when it isn't open. Null only when there's no main window to read the project from.</summary>
    private static PreviewWindow? BenchFor(Window owner)
    {
        if (_bench is { } open) return open;
        if (Application.Current.MainWindow is not MainWindow main) return null;
        var bench = new PreviewWindow().Standalone(owner);
        bench.MakeBench(main);
        bench.Show();
        return bench;
    }

    private void MakeBench(MainWindow main)
    {
        _bench = this;
        _isBench = true;
        _benchMain = main;
        Width = Math.Max(Width, 1280);
        BenchStatus.Visibility = Visibility.Visible;
        SetBenchTreeShown(App.Settings.BenchTreeShown, animate: false);
        BenchTree.ItemsSource = _benchRoots;
        ProjectWorkspace.Changed += BenchProjectChanged;
        Closed += (_, _) =>
        {
            ProjectWorkspace.Changed -= BenchProjectChanged;
            if (App.Settings.BenchTreeShown && BenchPanel.Visibility == Visibility.Visible) App.Settings.BenchTreeWidth = BenchColumn.ActualWidth;
            if (_bench == this) _bench = null;
        };
        UpdateHeader();
        SetUpBenchCheck();
        _ = BuildBenchAsync();
    }

    /// <summary>The tree's width while it's open, as it was last dragged.</summary>
    private static double BenchTreeWidth => App.Settings.BenchTreeWidth is > 120 and < 1200 ? App.Settings.BenchTreeWidth : 300;

    /// <summary>The strip the tree folds to: a thin edge with an arrow to slide it back out.</summary>
    private const double BenchStripWidth = 16;

    private void BenchHide_Click(object sender, RoutedEventArgs e) => ToggleBenchTree();

    private void BenchShow_Click(object sender, RoutedEventArgs e) => ToggleBenchTree();

    /// <summary>Ctrl+B and the pane buttons: folds the tree of scripts away, or opens it again at the width it had.</summary>
    private void ToggleBenchTree()
    {
        bool show = BenchPanel.Visibility != Visibility.Visible;
        if (!show) App.Settings.BenchTreeWidth = BenchColumn.ActualWidth;
        App.Settings.BenchTreeShown = show;
        App.Settings.Save();
        SetBenchTreeShown(show, animate: true);
        if (show) BenchTree.Focus();
        else Current?.FocusEditor();
    }

    /// <summary>
    /// Opens or folds the tree. It slides, kept at its open width and cut off by its column, so it moves out of the way rather
    /// than squeezing its folders up as the column narrows.
    /// </summary>
    private void SetBenchTreeShown(bool show, bool animate)
    {
        double open = BenchTreeWidth;
        BenchSplitterColumn.Width = show ? GridLength.Auto : new GridLength(0);
        BenchSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BenchShowButton.Visibility = Visibility.Collapsed;
        BenchPanel.Visibility = Visibility.Visible;
        BenchPanel.HorizontalAlignment = HorizontalAlignment.Right;
        BenchPanel.Width = open - BenchPanel.Margin.Left - BenchPanel.Margin.Right;
        void Done()
        {
            if (show)
            {
                BenchColumn.Width = new GridLength(open);
                BenchColumn.MinWidth = 180;
                BenchPanel.Width = double.NaN;
                BenchPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            else
            {
                BenchPanel.Visibility = Visibility.Collapsed;
                BenchShowButton.Visibility = Visibility.Visible;
            }
        }
        if (animate) ColumnAnimator.Slide(BenchColumn, show ? open : BenchStripWidth, Done);
        else
        {
            BenchColumn.MinWidth = 0;
            BenchColumn.Width = new GridLength(show ? open : BenchStripWidth);
            Done();
        }
    }

    private void BenchProjectChanged()
    {
        UpdateBenchTitle();
        _ = BuildBenchAsync();
    }

    private static string BenchTitle => ProjectWorkspace.Current is { } project ? L.T("PreviewWindow.ScriptBench", project.Name) : L.T("PreviewWindow.ScriptBench2");

    private void UpdateBenchTitle()
    {
        if (Current is null) Title = BenchTitle;
        else UpdateHeader();
    }

    // ---------------------------------------------------------------- the tree

    /// <summary>Reads the three groups again, keeping what was expanded and selected.</summary>
    private async Task BuildBenchAsync()
    {
        if (_benchMain is not { } main) return;
        int build = ++_benchBuild;
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _benchRoots) CollectExpanded(root, expanded);
        string? selected = (BenchTree.SelectedItem as BenchNode) is { } picked ? PathOf(picked) : null;

        string? gameFolder = string.IsNullOrWhiteSpace(App.Settings.GameFolder) ? GameLocator.Find() : App.Settings.GameFolder;
        string? gameScripts = Core.Scripts.Standalone.StandaloneCompiler.FindGameScripts(App.Settings.WccLitePath, gameFolder);
        string? modsFolder = gameFolder is null ? null : Path.Combine(gameFolder, "mods");

        var (game, gameByPath, gameArchive) = await Task.Run(() => GameGroup(gameScripts));
        if (build != _benchBuild) return;
        BenchNode mine;
        try
        {
            // the main window's listing of the project, so a file opened here is the same as one opened there; read
            // here when it hasn't one (yet)
            var project = ProjectWorkspace.Current;
            var archive = main.ProjectArchive is { Project: { } listed } a && project is not null
                && listed.FilePath.Equals(project.FilePath, StringComparison.OrdinalIgnoreCase) ? a
                : project is null ? null : await Task.Run(() => ScriptFolder.OpenProject(project));
            if (build != _benchBuild) return;
            mine = MineGroup(project, archive, gameByPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            mine = new BenchNode { Name = ProjectWorkspace.Current?.Name ?? L.T("PreviewWindow.YourMod"), Group = BenchGroup.Mine, IsHeading = true };
            mine.Add(BenchNode.Note(BenchGroup.Mine, L.T("PreviewWindow.CouldntRead", ex.Message), ex.ToString()));
        }
        var mods = ModsGroup(modsFolder);
        if (build != _benchBuild) return;

        _gameScripts = gameScripts;
        _gameArchive = gameArchive;
        _modsFolder = modsFolder is not null && Directory.Exists(modsFolder) ? modsFolder : null;
        _gameByPath.Clear();
        foreach (var (path, node) in gameByPath) _gameByPath[path] = node;
        _mine = mine;
        _game = game;
        _mods = mods;
        _benchKeys.Clear();
        _benchPaths.Clear();
        foreach (var node in mine.Files().Concat(game.Files())) Remember(node);

        _benchRoots.Clear();
        _benchRoots.Add(mine);
        _benchRoots.Add(game);
        _benchRoots.Add(mods);
        mine.IsExpanded = true;
        foreach (var root in _benchRoots) Restore(root, expanded, selected);
        ApplyBenchFilter();
        UpdateBenchStatus();
        // the game's scripts and the mods are read for checking as you type, ahead of the first check
        if (gameScripts is not null && ProjectWorkspace.Current is { } current && App.Settings.CheckScriptsAsYouType)
            Core.Scripts.Standalone.ScriptChecker.Prepare(gameScripts, _modsFolder, Path.GetFileName(current.BuiltModPath));
        ScheduleCheck();
        UpdateProblemsButton();
    }

    private void Remember(BenchNode node)
    {
        if (node.Key is { } key) _benchKeys.TryAdd(key, node);
        if ((node.DiskPath ?? (node.Entry as LooseScriptEntry)?.FullPath) is { } path) _benchPaths.TryAdd(Path.GetFullPath(path), node);
    }

    private static string PathOf(BenchNode node) => $"{node.Group}|{node.RelativePath}|{node.Name}";

    private static void CollectExpanded(BenchNode node, HashSet<string> into)
    {
        if (!node.IsExpanded) return;
        into.Add(PathOf(node));
        foreach (var child in node.Children) CollectExpanded(child, into);
    }

    private static void Restore(BenchNode node, HashSet<string> expanded, string? selected)
    {
        if (expanded.Contains(PathOf(node))) node.IsExpanded = true;
        if (selected is not null && PathOf(node) == selected) node.IsSelected = true;
        foreach (var child in node.Children) Restore(child, expanded, selected);
    }

    /// <summary>The project's scripts: every .ws in its scripts folder, and its empty folders, which can all be changed.</summary>
    private static BenchNode MineGroup(WorkbenchProject? project, ScriptFolder? archive, Dictionary<string, BenchNode> gameByPath)
    {
        if (project is null || archive is null)
        {
            var none = new BenchNode
            {
                Name = L.T("PreviewWindow.YourMod"), Group = BenchGroup.Mine, IsHeading = true,
                ToolTip = L.T("PreviewWindow.OpenProjectsScriptsOnes"),
            };
            none.Add(BenchNode.Note(BenchGroup.Mine, L.T("PreviewWindow.NoProjectOpen"),
                L.T("PreviewWindow.OpenCreateProjectMain")));
            return none;
        }
        string scripts = Path.GetFullPath(project.ScriptsPath).TrimEnd('\\');
        var heading = new BenchNode
        {
            Name = project.Name, Group = BenchGroup.Mine, IsHeading = true, DiskPath = scripts,
            ToolTip = L.T("PreviewWindow.YourModsScriptsThese", scripts),
        };
        var files = archive.Entries.OfType<LooseScriptEntry>()
            .Where(e => e.IsScript && e.FullPath.StartsWith(scripts + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Path.GetRelativePath(scripts, e.FullPath), (ArchiveEntry)e));
        string? Disk(string relative) => Path.Combine(scripts, relative);
        BenchNode.Fill(heading, files, (relative, entry) =>
        {
            bool overrides = gameByPath.ContainsKey(relative);
            return new BenchNode
            {
                Name = Path.GetFileName(relative), Group = BenchGroup.Mine, Entry = entry, RelativePath = relative,
                DiskPath = ((LooseScriptEntry)entry).FullPath, Key = ScriptWorkspace.FileFor(entry).Key,
                Badge = overrides ? L.T("PreviewWindow.Override") : null,
                ToolTip = overrides
                    ? L.T("PreviewWindow.OverridesGamesScriptsCompiled", relative)
                    : ((LooseScriptEntry)entry).FullPath,
            };
        }, Disk);
        try
        {
            // folders with nothing in them yet, such as one just made
            if (Directory.Exists(scripts))
                foreach (string dir in Directory.EnumerateDirectories(scripts, "*", SearchOption.AllDirectories))
                    heading.FolderAt(Path.GetRelativePath(scripts, dir), Disk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the files are listed all the same
        }
        BenchNode.Sort(heading);
        int count = heading.Files().Count();
        if (count == 0) heading.Add(BenchNode.Note(BenchGroup.Mine, L.T("PreviewWindow.NoScriptsYet"), L.T("PreviewWindow.RightClickHereNew")));
        return heading;
    }

    /// <summary>The game's scripts, read-only, and each by its path under the scripts folder.</summary>
    private static (BenchNode Heading, Dictionary<string, BenchNode> ByPath, ScriptFolder? Archive) GameGroup(string? gameScripts)
    {
        var byPath = new Dictionary<string, BenchNode>(StringComparer.OrdinalIgnoreCase);
        var heading = new BenchNode
        {
            Name = L.T("PreviewWindow.GameScripts"), Group = BenchGroup.Game, IsHeading = true, DiskPath = null,
            ToolTip = gameScripts is null ? L.T("PreviewWindow.GamesScriptsWerentFound")
                : L.T("PreviewWindow.GamesOwnScriptsRead", gameScripts),
        };
        if (gameScripts is null || !Directory.Exists(gameScripts))
        {
            heading.Add(BenchNode.Note(BenchGroup.Game, L.T("PreviewWindow.GamesScriptsWerentFound2"), L.T("PreviewWindow.SetGameFolderSettings")));
            return (heading, byPath, null);
        }
        var archive = ScriptFolder.Open(gameScripts);
        var files = archive.Entries.OfType<LooseScriptEntry>().Where(e => e.IsScript)
            .Select(e => (Path.GetRelativePath(gameScripts, e.FullPath), (ArchiveEntry)e)).ToList();
        BenchNode.Fill(heading, files, (relative, entry) =>
        {
            var node = new BenchNode
            {
                Name = Path.GetFileName(relative), Group = BenchGroup.Game, Entry = entry, RelativePath = relative,
                Key = ScriptWorkspace.FileFor(entry).Key, ToolTip = L.T("PreviewWindow.ScriptsReadOnly", relative),
            };
            byPath[relative] = node;
            return node;
        });
        return (heading, byPath, archive);
    }

    /// <summary>The installed mods, each read when it's expanded: a compiled one from its blob, decompiled, others from their loose scripts.</summary>
    private BenchNode ModsGroup(string? modsFolder)
    {
        var heading = new BenchNode
        {
            Name = L.T("Common.InstalledMods"), Group = BenchGroup.Mods, IsHeading = true,
            ToolTip = L.T("PreviewWindow.ModsGamesModsFolder"),
        };
        string[] mods;
        try
        {
            mods = modsFolder is not null && Directory.Exists(modsFolder) ? Directory.GetDirectories(modsFolder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mods = [];
        }
        string? own = ProjectWorkspace.Current is { } project ? Path.GetFileName(project.BuiltModPath) : null;
        foreach (string mod in mods.Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(mod), content = Path.Combine(mod, "content");
            string? blob = Directory.Exists(content) ? Directory.EnumerateFiles(content, "*.rsblob").FirstOrDefault() : null;
            string loose = Path.Combine(content, "scripts");
            if (blob is null && !Directory.Exists(loose)) continue;
            bool mine = own is not null && name.Equals(own, StringComparison.OrdinalIgnoreCase);
            var node = new BenchNode
            {
                Name = name, Group = BenchGroup.Mods, DiskPath = mod, RelativePath = name,
                Detail = (blob is not null ? L.T("PreviewWindow.CompiledLabel") : L.T("PreviewWindow.LooseScripts")) + (mine ? L.T("PreviewWindow.YourModsInstalledBuild") : ""),
                ToolTip = blob is not null ? L.T("PreviewWindow.CompiledScriptsDecompiledRead", blob)
                    : L.T("PreviewWindow.LooseReadOnly", loose)
                    + (mine ? L.T("PreviewWindow.YourModAsInstalled") : ""),
            };
            node.LoadLater(() => Task.Run(() => ModFiles(node, blob, loose)));
            heading.Add(node);
        }
        if (heading.Children.Count == 0) heading.Add(BenchNode.Note(BenchGroup.Mods, modsFolder is null ? L.T("Common.GameFolderIsntSet") : L.T("PreviewWindow.NoModsScripts")));
        return heading;
    }

    private List<BenchNode> ModFiles(BenchNode mod, string? blob, string loose)
    {
        var holder = new BenchNode { Name = mod.Name, Group = BenchGroup.Mods };
        if (blob is not null)
        {
            var archive = ScriptArchive.Open(blob);
            // all.ws repeats every other file
            var files = archive.Entries.Where(e => e.Path.EndsWith(".ws", StringComparison.OrdinalIgnoreCase) && !e.Path.EndsWith(@"\all.ws", StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.Path.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase) ? e.Path[8..] : e.Path, e));
            BenchNode.Fill(holder, files, (relative, entry) => ModFile(mod, relative, entry, L.T("PreviewWindow.DecompiledBlob")));
        }
        else
        {
            var archive = ScriptFolder.Open(loose);
            var files = archive.Entries.OfType<LooseScriptEntry>().Where(e => e.IsScript)
                .Select(e => (Path.GetRelativePath(loose, e.FullPath), (ArchiveEntry)e));
            BenchNode.Fill(holder, files, (relative, entry) => ModFile(mod, relative, entry, ((LooseScriptEntry)entry).FullPath));
        }
        var children = holder.Children.ToList();
        holder.Children.Clear();
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var node in children.SelectMany(c => c.IsFile ? [c] : c.Files())) Remember(node);
        });
        return children.Count > 0 ? children : [BenchNode.Note(BenchGroup.Mods, L.T("PreviewWindow.NoScripts"))];
    }

    private BenchNode ModFile(BenchNode mod, string relative, ArchiveEntry entry, string where) => new()
    {
        Name = Path.GetFileName(relative), Group = BenchGroup.Mods, Entry = entry, RelativePath = relative,
        Key = ScriptWorkspace.FileFor(entry).Key,
        Badge = _gameByPath.ContainsKey(relative) ? L.T("PreviewWindow.Replaces") : null,
        ToolTip = L.T("PreviewWindow.ModFileReadOnly", mod.Name, relative, where)
            + (_gameByPath.ContainsKey(relative) ? L.T("PreviewWindow.ShipsGameCopy") : ""),
    };

    // ---------------------------------------------------------------- the filter

    private void BenchFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyBenchFilter();

    private void BenchFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && BenchFilter.Text.Length > 0)
        {
            BenchFilter.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            BenchTree.Focus();
            if (BenchTree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first) first.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// With text in the filter, each group shows only its scripts whose name or folder has it, with their folders open;
    /// without, the whole tree. A mod's scripts are only searched once it's been expanded.
    /// </summary>
    private void ApplyBenchFilter()
    {
        if (_mine is null || _game is null || _mods is null) return;
        string query = BenchFilter.Text.Trim();
        if (query.Length == 0)
        {
            if (BenchTree.ItemsSource != _benchRoots) BenchTree.ItemsSource = _benchRoots;
            return;
        }
        var filtered = new ObservableCollection<BenchNode>();
        foreach (var root in _benchRoots)
        {
            var copy = new BenchNode { Name = root.Name, Group = root.Group, IsHeading = true, DiskPath = root.DiskPath, ToolTip = root.ToolTip };
            var matches = root.Files().Where(f => f.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            const int most = 400;
            foreach (var match in matches.Take(most))
            {
                // a mod's scripts sit under the mod's own folder
                string folder = root.Group == BenchGroup.Mods ? Path.Combine(ModOf(match)?.Name ?? "", Path.GetDirectoryName(match.RelativePath) ?? "")
                    : Path.GetDirectoryName(match.RelativePath) ?? "";
                copy.FolderAt(folder).Add(match.CopyFile());
            }
            BenchNode.Sort(copy);
            if (matches.Count > most) copy.Add(BenchNode.Note(root.Group, L.T("PreviewWindow.MoreTypeMoreName", matches.Count - most)));
            if (matches.Count == 0) copy.Add(BenchNode.Note(root.Group, root.Group == BenchGroup.Mods ? L.T("PreviewWindow.NoMatchModsExpanded") : L.T("PreviewWindow.NoMatch")));
            ExpandAll(copy);
            filtered.Add(copy);
        }
        BenchTree.ItemsSource = filtered;
    }

    private static BenchNode? ModOf(BenchNode node)
    {
        var current = node;
        while (current.Parent is { } parent && !parent.IsHeading) current = parent;
        return current.Parent is { IsHeading: true } ? current : null;
    }

    private static void ExpandAll(BenchNode node)
    {
        node.IsExpanded = true;
        foreach (var child in node.Children) ExpandAll(child);
    }

    // ---------------------------------------------------------------- opening, locating

    private BenchNode? PickedBenchNode => BenchTree.SelectedItem as BenchNode;

    private void BenchTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemUnder(source) is { DataContext: BenchNode { IsFile: true } node })
        {
            _ = OpenBenchNodeAsync(node);
            e.Handled = true;
        }
    }

    private void BenchTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PickedBenchNode is not { } node) return;
        if (e.Key == Key.Enter && node.IsFile)
        {
            _ = OpenBenchNodeAsync(node);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && node.IsEditable && !node.IsHeading)
        {
            _ = RenameAsync(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && node.IsEditable && !node.IsHeading)
        {
            _ = DeleteAsync(node);
            e.Handled = true;
        }
    }

    private static TreeViewItem? ItemUnder(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is TreeViewItem item) return item;
        return null;
    }

    private void BenchTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemUnder(source) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>Opens a script from the tree, or brings its tab forward.</summary>
    private async Task OpenBenchNodeAsync(BenchNode node, int offset = -1, int length = 0)
    {
        if (node.Entry is not { } entry || _benchMain is not { } main) return;
        if (node.Key is { } key && FindOpen(key) is { Host: { } host } open)
        {
            host.ShowDocument(open);
            if (offset >= 0) open.Reveal(offset, length);
            return;
        }
        try
        {
            byte[] data = await Task.Run(() =>
            {
                if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
                using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return entry.Read(fs);
            });
            var item = main.PreviewItemFor(entry, data);
            if (offset >= 0) ShowAt(item, this, offset, length);
            else Show(item, this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await BenchMessage.Show(L.T("PreviewWindow.CouldNotRead", node.Name), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
        }
    }

    /// <summary>
    /// A compile error's or a search's file, opened at the place, when there's no tab to open it from: from the tree
    /// when it lists the file, so it opens as the tree would, else read as it is (an installed mod's not unfolded yet).
    /// </summary>
    private async void OpenBenchFile(ScriptFile file, int offset, int length)
    {
        if (_benchKeys.TryGetValue(file.Key, out var node))
        {
            _ = OpenBenchNodeAsync(node, offset, length);
            return;
        }
        if (FindOpen(file.Key) is { Host: { } host } open)
        {
            host.ShowDocument(open);
            open.Reveal(offset, length);
            return;
        }
        byte[] data;
        try
        {
            data = await Task.Run(file.Read);
        }
        catch (Exception ex)
        {
            await BenchMessage.Show(L.T("PreviewWindow.CouldNotRead2", file.Path), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        var workspace = _gameArchive is { } game && _benchMain is { } main ? ScriptWorkspace.For(game, main.OpenArchives) : null;
        ShowAt(new PreviewItem(file.Path, file.Source, data, file, workspace), this, offset, length);
    }

    private void BenchLocate_Click(object sender, RoutedEventArgs e) => LocateInBench(Current);

    /// <summary>Shows a tab's file in the tree: its folders opened and the file selected and scrolled to.</summary>
    private void LocateInBench(PreviewDocument? doc)
    {
        if (doc is null) return;
        if (BenchFilter.Text.Length > 0) BenchFilter.Clear();
        if (FindNode(doc) is not { } node) return;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        node.IsSelected = true;
        Dispatcher.InvokeAsync(() => ContainerOf(node)?.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>A tab's file in the tree: by its key, or by its file on disk.</summary>
    private BenchNode? FindNode(PreviewDocument doc)
    {
        if (_benchKeys.TryGetValue(doc.Item.Key, out var node)) return node;
        foreach (string? path in new[] { doc.Item.DiskPath, doc.Item.Script?.Key })
            if (path is not null && Path.IsPathFullyQualified(path) && _benchPaths.TryGetValue(Path.GetFullPath(path), out node)) return node;
        return null;
    }

    private TreeViewItem? ContainerOf(BenchNode node)
    {
        var chain = new Stack<BenchNode>();
        for (var current = node; current is not null; current = current.Parent) chain.Push(current);
        ItemsControl parent = BenchTree;
        TreeViewItem? item = null;
        foreach (var step in chain)
        {
            parent.UpdateLayout();
            item = parent.ItemContainerGenerator.ContainerFromItem(step) as TreeViewItem;
            if (item is null) return null;
            parent = item;
        }
        return item;
    }

    private void BenchCollapse_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in (IEnumerable<BenchNode>)BenchTree.ItemsSource) Collapse(root);
    }

    private static void Collapse(BenchNode node)
    {
        node.IsExpanded = false;
        foreach (var child in node.Children) Collapse(child);
    }

    private void BenchRefresh_Click(object sender, RoutedEventArgs e)
    {
        // a mod installed or changed since: checking reads the mods again too
        Core.Scripts.Standalone.ScriptChecker.Forget();
        if (_benchMain is { } main && ProjectWorkspace.Current is not null) _ = main.ReloadProjectAsync(); // the project's change rebuilds the tree
        else _ = BuildBenchAsync();
    }

    // ---------------------------------------------------------------- the right-click menu

    private void BenchTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        BenchMenu.Items.Clear();
        if (PickedBenchNode is not { IsNote: false } node)
        {
            e.Handled = true;
            return;
        }
        void Add(string header, string glyph, Action action, bool enabled = true, string? gesture = null, string? tip = null)
        {
            var item = new MenuItem
            {
                Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "", ToolTip = tip,
                Icon = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), Text = glyph },
            };
            item.Click += (_, _) => action();
            BenchMenu.Items.Add(item);
        }
        void Separate()
        {
            if (BenchMenu.Items.Count > 0 && BenchMenu.Items[^1] is not Separator) BenchMenu.Items.Add(new Separator());
        }

        var project = ProjectWorkspace.Current;
        if (node.IsFile)
        {
            Add(L.T("Common.Open"), "", () => _ = OpenBenchNodeAsync(node), gesture: "Enter");
            if (node.Group == BenchGroup.Mine && node.Badge is not null)
                Add(L.T("PreviewWindow.CompareGames"), "", () => _ = CompareWithGameAsync(node),
                    tip: L.T("PreviewWindow.OpensGamesFileBeside"));
            if (node.Group is BenchGroup.Game or BenchGroup.Mods)
            {
                Separate();
                string target = project is null ? "" : Path.Combine(project.ScriptsPath, node.RelativePath);
                if (project is not null && File.Exists(target))
                    Add(L.T("PreviewWindow.OpenYourCopy"), "", () => _ = OpenMineAsync(target), tip: L.T("PreviewWindow.YourModHasAlready", project.ScriptsFolder, node.RelativePath));
                else
                    Add(L.T("Common.CopyProject"), "", () => _ = CopyNodeToProjectAsync(node), project is not null,
                        tip: project is null ? L.T("PreviewWindow.OpenProjectFirstCopy")
                            : L.T("PreviewWindow.CopiesFileTo", project.ScriptsFolder, node.RelativePath, project.Name)
                            + (node.Group == BenchGroup.Game || _gameByPath.ContainsKey(node.RelativePath) ? L.T("PreviewWindow.CompiledInPlace") : ""));
            }
        }
        if (node.Group == BenchGroup.Mine && node.DiskPath is not null && !node.IsFile)
        {
            Separate();
            Add(L.T("PreviewWindow.NewScript"), "", () => _ = NewScriptAsync(node));
            Add(L.T("PreviewWindow.NewFolder"), "", () => _ = NewFolderAsync(node));
        }
        if (node.IsEditable && !node.IsHeading)
        {
            Separate();
            Add(L.T("PreviewWindow.Rename2"), "", () => _ = RenameAsync(node), gesture: "F2");
            Add(L.T("PreviewWindow.Delete2"), "", () => _ = DeleteAsync(node), gesture: "Del", tip: L.T("PreviewWindow.MovesRecycleBin"));
        }
        Separate();
        string? reveal = node.DiskPath ?? (node.Entry as LooseScriptEntry)?.FullPath
            ?? (node.Group == BenchGroup.Game && node.IsHeading ? _gameScripts : null)
            ?? (node.Group == BenchGroup.Game && _gameScripts is not null && node.RelativePath.Length > 0 ? Path.Combine(_gameScripts, node.RelativePath) : null);
        if (reveal is not null && (File.Exists(reveal) || Directory.Exists(reveal)))
            Add(L.T("Common.ShowExplorer"), "", () => { if (!ShellReveal.Show([reveal])) ShellReveal.Open(reveal); });
        if (node.IsFile)
            Add(L.T("Common.CopyPath"), "", () => Clipboard.SetText(node.DiskPath ?? (node.Entry as LooseScriptEntry)?.FullPath ?? node.RelativePath));
        if (BenchMenu.Items.Count == 0) e.Handled = true;
        else if (BenchMenu.Items[^1] is Separator last) BenchMenu.Items.Remove(last);
    }

    private async Task CompareWithGameAsync(BenchNode mine)
    {
        if (!_gameByPath.TryGetValue(mine.RelativePath, out var game) || game.Entry is null) return;
        await OpenBenchNodeAsync(mine);
        if (mine.Key is { } key && FindOpen(key) is { Host: { } host } doc)
            await host.CompareWithFileAsync(doc, ScriptWorkspace.FileFor(game.Entry));
    }

    /// <summary>
    /// Copies one of the game's scripts, or a mod's, into the project at the same path, and opens the copy. A game
    /// script's copy overrides it; a decompiled one is copied as it reads.
    /// </summary>
    private async Task CopyNodeToProjectAsync(BenchNode source)
    {
        if (ProjectWorkspace.Current is not { } project || _benchMain is not { } main || source.Entry is not { } entry) return;
        string target = Path.Combine(project.ScriptsPath, source.RelativePath);
        try
        {
            if (!File.Exists(target))
            {
                byte[] data = await Task.Run(() =>
                {
                    if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
                    using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return entry.Read(fs);
                });
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Core.Projects.FileSafety.CheckWrite(target);
                File.WriteAllBytes(target, data);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Core.Projects.ProtectedFileException)
        {
            await BenchMessage.Show(L.T("PreviewWindow.CouldNotCopiedInto", source.Name), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        await main.ReloadProjectAsync();
        await OpenMineAsync(target);
    }

    /// <summary>Opens one of the project's scripts by its file on disk, once the tree has it.</summary>
    private async Task OpenMineAsync(string path)
    {
        await BuildBenchAsync();
        var node = _mine?.Files().FirstOrDefault(n => n.DiskPath?.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) == true);
        if (node is null) return;
        await OpenBenchNodeAsync(node);
        LocateInBench(Current);
    }

    private async Task NewScriptAsync(BenchNode folder)
    {
        if (folder.DiskPath is not { } dir || _benchMain is not { } main) return;
        string? name = await AskNameAsync(L.T("PreviewWindow.NewScriptIn", folder.IsHeading ? L.T("PreviewWindow.TheScriptsFolder") : folder.RelativePath), "", text =>
        {
            string file = text.EndsWith(".ws", StringComparison.OrdinalIgnoreCase) ? text : text + ".ws";
            return NameProblem(file) ?? (File.Exists(Path.Combine(dir, file)) ? L.T("PreviewWindow.ThereAlready", file) : null);
        });
        if (name is null) return;
        string path = Path.Combine(dir, name.EndsWith(".ws", StringComparison.OrdinalIgnoreCase) ? name : name + ".ws");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await BenchMessage.Show(L.T("PreviewWindow.CouldNotMade", Path.GetFileName(path)), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        await main.ReloadProjectAsync();
        await OpenMineAsync(path);
    }

    private async Task NewFolderAsync(BenchNode folder)
    {
        if (folder.DiskPath is not { } dir) return;
        string? name = await AskNameAsync(L.T("PreviewWindow.NewFolderIn", folder.IsHeading ? L.T("PreviewWindow.TheScriptsFolder") : folder.RelativePath), "",
            text => NameProblem(text) ?? (Directory.Exists(Path.Combine(dir, text)) ? L.T("PreviewWindow.ThereAlready2", text) : null));
        if (name is null) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await BenchMessage.Show(L.T("PreviewWindow.CouldNotMade2", name), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        folder.IsExpanded = true;
        await BuildBenchAsync();
    }

    private async Task RenameAsync(BenchNode node)
    {
        if (node.DiskPath is not { } path || _benchMain is not { } main) return;
        string dir = Path.GetDirectoryName(path)!;
        string? name = await AskNameAsync(L.T("PreviewWindow.Rename", node.Name), node.Name, text =>
            text.Equals(node.Name, StringComparison.Ordinal) ? "" : NameProblem(text)
            ?? (node.IsFile && !text.EndsWith(".ws", StringComparison.OrdinalIgnoreCase) ? L.T("PreviewWindow.ScriptsNameEndsWs") : null)
            ?? (!text.Equals(node.Name, StringComparison.OrdinalIgnoreCase) && (File.Exists(Path.Combine(dir, text)) || Directory.Exists(Path.Combine(dir, text))) ? L.T("PreviewWindow.ThereAlready2", text) : null));
        if (name is null || name == node.Name) return;
        if (!await CloseTabsUnderAsync(path, renaming: true)) return;
        string target = Path.Combine(dir, name);
        try
        {
            if (node.IsFile) File.Move(path, target);
            else Directory.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await BenchMessage.Show(L.T("PreviewWindow.CouldNotRenamed", node.Name), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        await main.ReloadProjectAsync();
        if (node.IsFile) await OpenMineAsync(target);
    }

    private async Task DeleteAsync(BenchNode node)
    {
        if (node.DiskPath is not { } path || _benchMain is not { } main) return;
        int files = node.IsFile ? 1 : node.Files().Count();
        string what = node.IsFile ? node.Name : files == 0 ? L.T("PreviewWindow.Folder", node.Name) : L.T("PreviewWindow.Folder2", node.Name, L.T("Count.Script", files));
        if (!await BenchMessage.Show(L.T("PreviewWindow.Delete", what), L.T("PreviewWindow.RecycleBin") + (node.Badge is not null ? L.T("PreviewWindow.GameFileAgain") : ""),
                MessageKind.Question, primary: L.T("PreviewWindow.Delete2"), secondary: L.T("Common.Cancel")))
            return;
        if (!await CloseTabsUnderAsync(path, renaming: false)) return;
        try
        {
            if (node.IsFile)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await BenchMessage.Show(L.T("PreviewWindow.CouldNotDeleted", node.Name), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        RemoveEmptyFolders(Path.GetDirectoryName(path));
        await main.ReloadProjectAsync();
    }

    /// <summary>Removes the folders a delete left empty, up to (not including) the project's scripts folder.</summary>
    private static void RemoveEmptyFolders(string? folder)
    {
        if (ProjectWorkspace.Current is not { } project) return;
        string scripts = Path.GetFullPath(project.ScriptsPath).TrimEnd('\\');
        try
        {
            while (folder is not null && Path.GetFullPath(folder).StartsWith(scripts + "\\", StringComparison.OrdinalIgnoreCase)
                   && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                folder = Path.GetDirectoryName(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a folder that can't go stays in the tree
        }
    }

    /// <summary>Closes the tabs of files at or under a path before it's renamed or deleted; false when one has changes and the answer was to keep them.</summary>
    private async Task<bool> CloseTabsUnderAsync(string path, bool renaming)
    {
        string full = Path.GetFullPath(path);
        bool Under(PreviewDocument d) => d.Item.DiskPath is { } disk
            && (disk.Equals(full, StringComparison.OrdinalIgnoreCase) || disk.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase));
        var docs = AllOpenDocuments().Where(Under).ToList();
        var unsaved = docs.Where(d => d.IsDirty).ToList();
        if (unsaved.Count > 0)
        {
            var choice = await BenchMessage.Ask(L.T("PreviewWindow.SaveChangesFirst", Names(unsaved)),
                (unsaved.Count == 1 ? L.T("PreviewWindow.ItsTabIsClosed") : L.T("PreviewWindow.TheirTabsAreClosed"))
                + L.T(renaming ? "PreviewWindow.BeforeRenamed" : "PreviewWindow.BeforeDeleted", docs.Count),
                MessageKind.Question, L.T("Common.Save"), L.T("Common.DontSave"));
            if (choice == MessageChoice.Cancel || choice == MessageChoice.Primary && !SaveAll(unsaved)) return false;
        }
        foreach (var doc in docs)
        {
            doc.DiscardChanges();
            if (doc.Host is { } host) await host.CloseDocumentAsync(doc);
        }
        return true;
    }

    private static string? NameProblem(string name) =>
        name.Length == 0 ? L.T("PreviewWindow.TypeName")
        : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? L.T("PreviewWindow.NameCantHave")
        : name.Trim() != name || name.EndsWith('.') ? L.T("PreviewWindow.NameCantStartEnd")
        : null;

    // ---------------------------------------------------------------- naming, in the bar above the tree

    /// <summary>
    /// Asks for a name in the bar above the tree; null when cancelled. <paramref name="check"/> says what's wrong with
    /// a name, an empty string for one that changes nothing, or null when it's fine.
    /// </summary>
    private Task<string?> AskNameAsync(string label, string initial, Func<string, string?> check)
    {
        _benchName?.TrySetResult(null);
        var answer = _benchName = new TaskCompletionSource<string?>();
        _benchNameCheck = check;
        BenchNameLabel.Text = label;
        BenchNameBox.Text = initial;
        BenchNameProblem.Visibility = Visibility.Collapsed;
        BenchNameBar.Visibility = Visibility.Visible;
        BenchNameBox.Focus();
        int dot = initial.LastIndexOf('.');
        BenchNameBox.Select(0, dot > 0 ? dot : initial.Length);
        return answer.Task;
    }

    private void BenchNameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndNaming(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            string text = BenchNameBox.Text.Trim();
            string? problem = _benchNameCheck?.Invoke(text);
            if (problem is { Length: > 0 })
            {
                BenchNameProblem.Text = problem;
                BenchNameProblem.Visibility = Visibility.Visible;
            }
            else EndNaming(problem is null ? text : null);
            e.Handled = true;
        }
    }

    private void EndNaming(string? name)
    {
        BenchNameBar.Visibility = Visibility.Collapsed;
        var answer = _benchName;
        _benchName = null;
        answer?.TrySetResult(name);
        BenchTree.Focus();
    }

    // ---------------------------------------------------------------- the status bar and compiling

    /// <summary>Says what compiling reads: the project's scripts, the game's and the installed mods', and which compiler.</summary>
    private void UpdateBenchStatus()
    {
        if (!_isBench) return;
        var project = ProjectWorkspace.Current;
        var compiler = CurrentCompiler();
        BenchCompileButton.IsEnabled = project is not null && compiler is not null && _compile is null;
        if (project is null)
        {
            BenchStatusText.Text = L.T("PreviewWindow.NoProjectOpenGames");
            BenchStatusText.ToolTip = null;
            return;
        }
        int scripts = _mine?.Files().Count() ?? 0, overrides = _mine?.Files().Count(f => f.Badge is not null) ?? 0;
        int mods = _mods?.Children.Count(c => !c.IsNote && !(c.Detail?.Contains("your mod") ?? false)) ?? 0;
        string against = compiler?.Kind == CompilerKind.WccLite
            ? L.T("PreviewWindow.REDkitsCopyGamesScripts")
            : L.T("PreviewWindow.GamesScripts", L.T("Count.InstalledMod", mods));
        string with = compiler is null ? L.T("PreviewWindow.NothingCompileSetWcc")
            : compiler.Kind == CompilerKind.WccLite ? L.T("PreviewWindow.REDkitsWccLite") : L.T("PreviewWindow.BuiltCompiler");
        BenchStatusText.Text = L.T("PreviewWindow.Compiles", project.ScriptsFolder, L.T("Count.Script", scripts),
            overrides > 0 ? L.T("PreviewWindow.Overriding", overrides) : "", against, with);
        BenchStatusText.ToolTip = L.T("PreviewWindow.CompileFolders", project.ScriptsPath, compiler?.GameScripts ?? _gameScripts ?? L.T("PreviewWindow.NotFound"))
            + (compiler?.ModsFolder is { } modsFolder && compiler.Kind == CompilerKind.BuiltIn ? L.T("PreviewWindow.InstalledModsFolder", modsFolder) : "")
            + L.T("PreviewWindow.CompileChecks")
            + (project.BuildLooseScripts == true ? L.T("PreviewWindow.WithLooseScripts") : "") + L.T("PreviewWindow.CompilerInSettings");
    }

    private void BenchCompile_Click(object sender, RoutedEventArgs e) => _ = BenchCompileAsync();

    /// <summary>F7 or Compile: compiles the project's scripts into its mod's content folder, listing what's wrong under the tabs.</summary>
    private async Task BenchCompileAsync()
    {
        if (_benchMain?.ProjectArchive is not { } project) return;
        await CompileAsync(Current is { } doc && ProjectOf(doc) is not null ? doc : null, project);
    }
}
