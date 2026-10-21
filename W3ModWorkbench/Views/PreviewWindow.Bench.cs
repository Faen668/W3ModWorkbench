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
    private MainWindow? _benchMain;
    private readonly ObservableCollection<BenchNode> _benchRoots = [];
    private BenchNode? _mine, _game, _mods;

    /// <summary>The scripts in the tree by the key their tab has, to find the file in front.</summary>
    private readonly Dictionary<string, BenchNode> _benchKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's scripts by their path under the scripts folder, to tell what the project's override.</summary>
    private readonly Dictionary<string, BenchNode> _gameByPath = new(StringComparer.OrdinalIgnoreCase);

    private string? _gameScripts;
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
        BenchColumn.Width = new GridLength(App.Settings.BenchTreeWidth is > 120 and < 1200 ? App.Settings.BenchTreeWidth : 300);
        BenchColumn.MinWidth = 180;
        BenchSplitterColumn.Width = GridLength.Auto;
        BenchPanel.Visibility = Visibility.Visible;
        BenchSplitter.Visibility = Visibility.Visible;
        BenchStatus.Visibility = Visibility.Visible;
        BenchTree.ItemsSource = _benchRoots;
        ProjectWorkspace.Changed += BenchProjectChanged;
        Closed += (_, _) =>
        {
            ProjectWorkspace.Changed -= BenchProjectChanged;
            App.Settings.BenchTreeWidth = BenchColumn.ActualWidth;
            if (_bench == this) _bench = null;
        };
        UpdateHeader();
        _ = BuildBenchAsync();
    }

    private void BenchProjectChanged()
    {
        UpdateBenchTitle();
        _ = BuildBenchAsync();
    }

    private static string BenchTitle => ProjectWorkspace.Current is { } project ? $"Script Bench · {project.Name}" : "Script Bench";

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

        var (game, gameByPath) = await Task.Run(() => GameGroup(gameScripts));
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
            mine = new BenchNode { Name = ProjectWorkspace.Current?.Name ?? "Your mod", Group = BenchGroup.Mine, IsHeading = true };
            mine.Add(BenchNode.Note(BenchGroup.Mine, "Couldn't be read: " + ex.Message, ex.ToString()));
        }
        var mods = ModsGroup(modsFolder);
        if (build != _benchBuild) return;

        _gameScripts = gameScripts;
        _gameByPath.Clear();
        foreach (var (path, node) in gameByPath) _gameByPath[path] = node;
        _mine = mine;
        _game = game;
        _mods = mods;
        _benchKeys.Clear();
        foreach (var node in mine.Files().Concat(game.Files()))
            if (node.Key is { } key) _benchKeys.TryAdd(key, node);

        _benchRoots.Clear();
        _benchRoots.Add(mine);
        _benchRoots.Add(game);
        _benchRoots.Add(mods);
        mine.IsExpanded = true;
        foreach (var root in _benchRoots) Restore(root, expanded, selected);
        ApplyBenchFilter();
        UpdateBenchStatus();
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
                Name = "Your mod", Group = BenchGroup.Mine, IsHeading = true,
                ToolTip = "The open project's scripts, the ones you can change and compile",
            };
            none.Add(BenchNode.Note(BenchGroup.Mine, "No project open",
                "Open or create a project from the main window's Project menu. Its scripts are listed here, and they're the ones you can change, override game scripts with and compile"));
            return none;
        }
        string scripts = Path.GetFullPath(project.ScriptsPath).TrimEnd('\\');
        var heading = new BenchNode
        {
            Name = project.Name, Group = BenchGroup.Mine, IsHeading = true, DiskPath = scripts,
            ToolTip = $"Your mod's scripts, in {scripts}. These are the ones that can be changed, and the ones compiling compiles",
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
                Badge = overrides ? "OVERRIDE" : null,
                ToolTip = overrides
                    ? $"Overrides the game's scripts\\{relative}: compiled in its place, so what it leaves out still comes from the game's. Compare with the game's from the right-click menu"
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
        if (count == 0) heading.Add(BenchNode.Note(BenchGroup.Mine, "No scripts yet", "Right-click here for New script, or override one of the game's scripts"));
        return heading;
    }

    /// <summary>The game's scripts, read-only, and each by its path under the scripts folder.</summary>
    private static (BenchNode Heading, Dictionary<string, BenchNode> ByPath) GameGroup(string? gameScripts)
    {
        var byPath = new Dictionary<string, BenchNode>(StringComparer.OrdinalIgnoreCase);
        var heading = new BenchNode
        {
            Name = "Game scripts", Group = BenchGroup.Game, IsHeading = true, DiskPath = null,
            ToolTip = gameScripts is null ? "The game's scripts weren't found. Set the game folder in Settings"
                : $"The game's own scripts, in {gameScripts}. Read-only: right-click one to override it in your mod",
        };
        if (gameScripts is null || !Directory.Exists(gameScripts))
        {
            heading.Add(BenchNode.Note(BenchGroup.Game, "The game's scripts weren't found", "Set the game folder in Settings (Ctrl+, in the main window)"));
            return (heading, byPath);
        }
        var archive = ScriptFolder.Open(gameScripts);
        var files = archive.Entries.OfType<LooseScriptEntry>().Where(e => e.IsScript)
            .Select(e => (Path.GetRelativePath(gameScripts, e.FullPath), (ArchiveEntry)e)).ToList();
        BenchNode.Fill(heading, files, (relative, entry) =>
        {
            var node = new BenchNode
            {
                Name = Path.GetFileName(relative), Group = BenchGroup.Game, Entry = entry, RelativePath = relative,
                Key = ScriptWorkspace.FileFor(entry).Key, ToolTip = $"scripts\\{relative}, read-only",
            };
            byPath[relative] = node;
            return node;
        });
        return (heading, byPath);
    }

    /// <summary>The installed mods, each read when it's expanded: a compiled one from its blob, decompiled, others from their loose scripts.</summary>
    private BenchNode ModsGroup(string? modsFolder)
    {
        var heading = new BenchNode
        {
            Name = "Installed mods", Group = BenchGroup.Mods, IsHeading = true,
            ToolTip = "The mods in the game's mods folder, read-only. The built-in compiler compiles your scripts against them, so their classes and functions can be used",
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
                Detail = (blob is not null ? "compiled" : "loose scripts") + (mine ? ", your mod's installed build" : ""),
                ToolTip = blob is not null ? $"{blob}\nIts compiled scripts, decompiled. Read-only"
                    : $"{loose}\nIts loose scripts. Read-only"
                    + (mine ? "\nThis is your mod as installed: compiling leaves it out, as it's an older build of your scripts" : ""),
            };
            node.LoadLater(() => Task.Run(() => ModFiles(node, blob, loose)));
            heading.Add(node);
        }
        if (heading.Children.Count == 0) heading.Add(BenchNode.Note(BenchGroup.Mods, modsFolder is null ? "The game folder isn't set" : "No mods with scripts"));
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
            BenchNode.Fill(holder, files, (relative, entry) => ModFile(mod, relative, entry, "decompiled from its blob"));
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
            foreach (var node in children.SelectMany(c => c.IsFile ? [c] : c.Files()))
                if (node.Key is { } key) _benchKeys.TryAdd(key, node);
        });
        return children.Count > 0 ? children : [BenchNode.Note(BenchGroup.Mods, "No scripts")];
    }

    private BenchNode ModFile(BenchNode mod, string relative, ArchiveEntry entry, string where) => new()
    {
        Name = Path.GetFileName(relative), Group = BenchGroup.Mods, Entry = entry, RelativePath = relative,
        Key = ScriptWorkspace.FileFor(entry).Key,
        Badge = _gameByPath.ContainsKey(relative) ? "REPLACES" : null,
        ToolTip = $"{mod.Name}: {relative}\n{where}. Read-only"
            + (_gameByPath.ContainsKey(relative) ? "\nShips a copy of the game's file of the same path, which replaces it" : ""),
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
            if (matches.Count > most) copy.Add(BenchNode.Note(root.Group, $"and {matches.Count - most:N0} more: type more of the name"));
            if (matches.Count == 0) copy.Add(BenchNode.Note(root.Group, root.Group == BenchGroup.Mods ? "No match in the mods expanded so far" : "No match"));
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
            await BenchMessage.Show($"{node.Name} could not be read", ex.Message, MessageKind.Warning, primary: "OK");
        }
    }

    /// <summary>A compile error's file, opened at its line, when there's no tab to open it from.</summary>
    private void OpenBenchFile(ScriptFile file, int offset, int length)
    {
        if (_benchKeys.TryGetValue(file.Key, out var node)) _ = OpenBenchNodeAsync(node, offset, length);
    }

    private void BenchLocate_Click(object sender, RoutedEventArgs e) => LocateInBench(Current);

    /// <summary>Shows a tab's file in the tree: its folders opened and the file selected and scrolled to.</summary>
    private void LocateInBench(PreviewDocument? doc)
    {
        if (doc is null) return;
        if (BenchFilter.Text.Length > 0) BenchFilter.Clear();
        if (!_benchKeys.TryGetValue(doc.Item.Key, out var node)) return;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        node.IsSelected = true;
        Dispatcher.InvokeAsync(() => ContainerOf(node)?.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
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
        foreach (var root in (IEnumerable<BenchNode>)BenchTree.ItemsSource)
            foreach (var child in root.Children) Collapse(child);
    }

    private static void Collapse(BenchNode node)
    {
        node.IsExpanded = false;
        foreach (var child in node.Children) Collapse(child);
    }

    private void BenchRefresh_Click(object sender, RoutedEventArgs e)
    {
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
            Add("Open", "", () => _ = OpenBenchNodeAsync(node), gesture: "Enter");
            if (node.Group == BenchGroup.Mine && node.Badge is not null)
                Add("Compare with the game's", "", () => _ = CompareWithGameAsync(node),
                    tip: "Opens the game's file beside yours with the lines that differ marked");
            if (node.Group == BenchGroup.Game)
            {
                Separate();
                string target = project is null ? "" : Path.Combine(project.ScriptsPath, node.RelativePath);
                if (project is not null && File.Exists(target))
                    Add($"Open your override", "", () => _ = OpenMineAsync(target));
                else
                    Add(project is null ? "Override in your mod" : $"Override in {project.Name.Replace("_", "__")}", "", () => _ = OverrideAsync(node),
                        project is not null, tip: project is null ? "Open a project first: the copy goes in its scripts folder"
                            : $"Copies the file to {project.ScriptsFolder}\\{node.RelativePath}, where it's yours to change. It's compiled in place of the game's");
            }
        }
        if (node.Group == BenchGroup.Mine && node.DiskPath is not null && !node.IsFile)
        {
            Separate();
            Add("New script…", "", () => _ = NewScriptAsync(node));
            Add("New folder…", "", () => _ = NewFolderAsync(node));
        }
        if (node.IsEditable && !node.IsHeading)
        {
            Separate();
            Add("Rename…", "", () => _ = RenameAsync(node), gesture: "F2");
            Add("Delete", "", () => _ = DeleteAsync(node), gesture: "Del", tip: "Moves it to the Recycle Bin");
        }
        Separate();
        string? reveal = node.DiskPath ?? (node.Entry as LooseScriptEntry)?.FullPath
            ?? (node.Group == BenchGroup.Game && node.IsHeading ? _gameScripts : null)
            ?? (node.Group == BenchGroup.Game && _gameScripts is not null && node.RelativePath.Length > 0 ? Path.Combine(_gameScripts, node.RelativePath) : null);
        if (reveal is not null && (File.Exists(reveal) || Directory.Exists(reveal)))
            Add("Show in Explorer", "", () => { if (!ShellReveal.Show([reveal])) ShellReveal.Open(reveal); });
        if (node.IsFile)
            Add("Copy path", "", () => Clipboard.SetText(node.DiskPath ?? (node.Entry as LooseScriptEntry)?.FullPath ?? node.RelativePath));
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

    /// <summary>Copies one of the game's scripts into the project at the same path, and opens the copy.</summary>
    private async Task OverrideAsync(BenchNode game)
    {
        if (ProjectWorkspace.Current is not { } project || _benchMain is not { } main || game.Entry is not LooseScriptEntry entry) return;
        string target = Path.Combine(project.ScriptsPath, game.RelativePath);
        try
        {
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry.FullPath, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await BenchMessage.Show($"{game.Name} could not be copied into the project", ex.Message, MessageKind.Warning, primary: "OK");
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
        string? name = await AskNameAsync($"New script in {(folder.IsHeading ? "the scripts folder" : folder.RelativePath)}", "", text =>
        {
            string file = text.EndsWith(".ws", StringComparison.OrdinalIgnoreCase) ? text : text + ".ws";
            return NameProblem(file) ?? (File.Exists(Path.Combine(dir, file)) ? $"{file} is there already" : null);
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
            await BenchMessage.Show($"{Path.GetFileName(path)} could not be made", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }
        await main.ReloadProjectAsync();
        await OpenMineAsync(path);
    }

    private async Task NewFolderAsync(BenchNode folder)
    {
        if (folder.DiskPath is not { } dir) return;
        string? name = await AskNameAsync($"New folder in {(folder.IsHeading ? "the scripts folder" : folder.RelativePath)}", "",
            text => NameProblem(text) ?? (Directory.Exists(Path.Combine(dir, text)) ? $"{text} is there already" : null));
        if (name is null) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await BenchMessage.Show($"{name} could not be made", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }
        folder.IsExpanded = true;
        await BuildBenchAsync();
    }

    private async Task RenameAsync(BenchNode node)
    {
        if (node.DiskPath is not { } path || _benchMain is not { } main) return;
        string dir = Path.GetDirectoryName(path)!;
        string? name = await AskNameAsync($"Rename {node.Name}", node.Name, text =>
            text.Equals(node.Name, StringComparison.Ordinal) ? "" : NameProblem(text)
            ?? (node.IsFile && !text.EndsWith(".ws", StringComparison.OrdinalIgnoreCase) ? "A script's name ends in .ws" : null)
            ?? (!text.Equals(node.Name, StringComparison.OrdinalIgnoreCase) && (File.Exists(Path.Combine(dir, text)) || Directory.Exists(Path.Combine(dir, text))) ? $"{text} is there already" : null));
        if (name is null || name == node.Name) return;
        if (!await CloseTabsUnderAsync(path, "renamed")) return;
        string target = Path.Combine(dir, name);
        try
        {
            if (node.IsFile) File.Move(path, target);
            else Directory.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await BenchMessage.Show($"{node.Name} could not be renamed", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }
        await main.ReloadProjectAsync();
        if (node.IsFile) await OpenMineAsync(target);
    }

    private async Task DeleteAsync(BenchNode node)
    {
        if (node.DiskPath is not { } path || _benchMain is not { } main) return;
        int files = node.IsFile ? 1 : node.Files().Count();
        string what = node.IsFile ? node.Name : files == 0 ? $"the folder {node.Name}" : $"the folder {node.Name} and its {Format.Count(files, "script")}";
        if (!await BenchMessage.Show($"Delete {what}?", "It goes to the Recycle Bin, where it can be restored from." + (node.Badge is not null ? " The game's own file is used again in its place." : ""),
                MessageKind.Question, primary: "Delete", secondary: "Cancel"))
            return;
        if (!await CloseTabsUnderAsync(path, "deleted")) return;
        try
        {
            if (node.IsFile)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await BenchMessage.Show($"{node.Name} could not be deleted", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }
        await main.ReloadProjectAsync();
    }

    /// <summary>Closes the tabs of files at or under a path before it's renamed or deleted; false when one has changes and the answer was to keep them.</summary>
    private async Task<bool> CloseTabsUnderAsync(string path, string happening)
    {
        string full = Path.GetFullPath(path);
        bool Under(PreviewDocument d) => d.Item.DiskPath is { } disk
            && (disk.Equals(full, StringComparison.OrdinalIgnoreCase) || disk.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase));
        var docs = AllOpenDocuments().Where(Under).ToList();
        var unsaved = docs.Where(d => d.IsDirty).ToList();
        if (unsaved.Count > 0)
        {
            var choice = await BenchMessage.Ask($"Save the changes to {Names(unsaved)} first?",
                $"{(unsaved.Count == 1 ? "Its tab is" : "Their tabs are")} closed before {(docs.Count == 1 ? "it's" : "they're")} {happening}.",
                MessageKind.Question, "Save", "Don't save");
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
        name.Length == 0 ? "Type a name"
        : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "A name can't have \\ / : * ? \" < > or | in it"
        : name.Trim() != name || name.EndsWith('.') ? "A name can't start or end with a space, or end with a dot"
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
            BenchStatusText.Text = "No project open. The game's and the installed mods' scripts can be read here; open or create a project from the main window to write your own.";
            BenchStatusText.ToolTip = null;
            return;
        }
        int scripts = _mine?.Files().Count() ?? 0, overrides = _mine?.Files().Count(f => f.Badge is not null) ?? 0;
        int mods = _mods?.Children.Count(c => !c.IsNote && !(c.Detail?.Contains("your mod") ?? false)) ?? 0;
        string against = compiler?.Kind == CompilerKind.WccLite
            ? "REDkit's copy of the game's scripts (r4data\\scripts, with whatever else is in it)"
            : $"the game's scripts and {Format.Count(mods, "installed mod")}";
        string with = compiler is null ? "nothing to compile with: set wcc_lite or the game folder in Settings"
            : compiler.Kind == CompilerKind.WccLite ? "REDkit's wcc_lite" : "the built-in compiler";
        BenchStatusText.Text = $"Compiles {project.ScriptsFolder} ({Format.Count(scripts, "script")}"
            + (overrides > 0 ? $", {overrides} overriding the game's" : "") + $") against {against}, with {with}.";
        BenchStatusText.ToolTip = $"Your scripts: {project.ScriptsPath}\nThe game's scripts: {(compiler?.GameScripts ?? _gameScripts ?? "not found")}"
            + (compiler?.ModsFolder is { } modsFolder && compiler.Kind == CompilerKind.BuiltIn ? $"\nInstalled mods: {modsFolder} (your own mod's installed build is left out)" : "")
            + $"\n\nCompile checks the scripts and lists what's wrong. Build puts them in the mod as content\\precompiled.rsblob"
            + (project.BuildLooseScripts == true ? ", with the .ws files beside it in content\\scripts" : "") + " (Project properties). The compiler is chosen in Settings";
    }

    private void BenchCompile_Click(object sender, RoutedEventArgs e) => _ = BenchCompileAsync();

    /// <summary>F7 or Compile: compiles the project's scripts to check them, listing what's wrong under the tabs. Nothing is kept.</summary>
    private async Task BenchCompileAsync()
    {
        if (_benchMain?.ProjectArchive is not { } project) return;
        await CompileAsync(Current is { } doc && ProjectOf(doc) is not null ? doc : null, chooseOutput: false, project, check: true);
    }
}
