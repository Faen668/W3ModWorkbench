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
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Highlighting;
using ICSharpCode.AvalonEdit.Document;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

public partial class MainWindow : Window, IMessageHost
{
    /// <summary>Every open archive, flat. The tree shows these grouped by mod: see <see cref="RebuildTree"/>.</summary>
    private readonly ObservableCollection<FolderNode> _roots = [];
    private readonly ObservableCollection<FolderNode> _tree = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _previewTimer;
    private readonly Settings _settings = App.Settings;
    private List<EntryItem> _shown = [];
    private int _checkedCount;
    private long _checkedBytes;
    private bool _statusQueued;
    private CancellationTokenSource? _extractCts;

    /// <summary>Where each file extracted this session was written, for Show extracted file.</summary>
    private readonly Dictionary<ArchiveEntry, string> _extracted = new(ReferenceEqualityComparer.Instance);

    public MainWindow(string[] args)
    {
        InitializeComponent();
        FolderTree.ItemsSource = _tree;
        _roots.CollectionChanged += (_, _) => RebuildTree();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshView(); };

        var assembly = Assembly.GetExecutingAssembly();
        string copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright © 2026 Faen90";
        AboutText.Text = $"v{assembly.GetName().Version?.ToString(3)}  ·  {copyright.Replace("Copyright ", "")}"; // loc-ignore: the version and copyright, as they are

        OutputBox.Text = _settings.OutputFolder;
        KeepStructureCheck.IsChecked = _settings.KeepStructure;
        OpenWhenDoneCheck.IsChecked = _settings.OpenWhenDone;
        CompanionsCheck.IsChecked = _settings.ExtractCompanions;
        PngCheck.IsChecked = _settings.ExtractTexturesAsPng;
        JsonCheck.IsChecked = _settings.ExtractCr2wAsJson;

        UpdateProjectUi();
        FileThumbs.ContextMenu = FileGrid.ContextMenu;
        FileThumbs.TextureFor = async item => await FindTextureForHeaderAsync(item);
        FileThumbs.ItemOpened += OpenItem;
        FileThumbs.SelectionChanged += FileThumbs_SelectionChanged;
        PreviewPane.LocatorFor = LocatorFor;
        PreviewPane.TextureFor = FindTextureForHeaderAsync;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = UpdatePreviewAsync(); };
        ApplyView();
        ApplyPreviewPane();
        SubfoldersToggle.IsChecked = _settings.ListSubfolderFiles;
        UpdateRecentList();
        // started by a link to a game file: the tool starts as it would, then shows the file in the Asset Browser
        var link = args is [var arg] ? AppLinks.Parse(arg) : null;
        if (link is not null) args = [];
        if (args.Length > 0)
            Loaded += async (_, _) => await LoadPathsAsync(args);
        else if (_settings.ReopenLastProject && File.Exists(_settings.LastProject))
            Loaded += async (_, _) => await OpenProjectAsync(_settings.LastProject, quiet: true);
        if (link is not null) Loaded += (_, _) => AssetBrowserWindow.ShowLink(this, link);
    }

    /// <summary>Shows a game file a link names (<see cref="AppLinks"/>) in the Asset Browser, handed over by a copy of the tool started with it.</summary>
    public void OpenLink(string text)
    {
        if (AppLinks.Parse(text) is not { } link) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        AssetBrowserWindow.ShowLink(this, link);
    }

    // ---------------------------------------------------------------- loading

    private async void OpenBundles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("MainWindow.OpenProjectsBundlesScripts"),
            Filter = L.T("MainWindow.ProjectsBundlesScriptsStrings"),
            Multiselect = true,
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastOpenFolder = Path.GetDirectoryName(dialog.FileNames[0]) ?? "";
        RememberRecent(dialog.FileNames);
        await LoadPathsAsync(dialog.FileNames);
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L.T("MainWindow.OpenEveryBundleCompiled"),
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastOpenFolder = dialog.FolderName;
        RememberRecent([dialog.FolderName]);
        await LoadPathsAsync([dialog.FolderName]);
    }

    // ---------------------------------------------------------------- recent

    private void RememberRecent(IEnumerable<string> paths)
    {
        RecentItem.Remember(_settings.RecentPaths, paths);
        UpdateRecentList();
    }

    private List<RecentItem> RecentItems() => [.. _settings.RecentPaths.Select(p => new RecentItem(p)).Where(r => r.Exists)];

    /// <summary>The start screen's list of what was opened before.</summary>
    private void UpdateRecentList()
    {
        var items = RecentItems().Take(6).ToList();
        RecentList.ItemsSource = items;
        RecentPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentButton.IsEnabled = items.Count > 0;
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RecentButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var recent in RecentItems())
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = recent.Name, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = recent.Folder, Opacity = 0.55, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), MaxWidth = 380,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = header, ToolTip = recent.Path, Icon = new TextBlock { Style = (Style)FindResource("IconText"), Text = recent.Glyph } };
            string path = recent.Path;
            item.Click += (_, _) => _ = OpenRecentAsync(path);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = L.T("MainWindow.ClearList") };
        clear.Click += (_, _) =>
        {
            _settings.RecentPaths.Clear();
            UpdateRecentList();
        };
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    private void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = OpenRecentAsync(path);
    }

    private async Task OpenRecentAsync(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _settings.RecentPaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            UpdateRecentList();
            ShowError(L.T("MainWindow.IsntThereAnyMore", path));
            return;
        }
        RememberRecent([path]);
        await LoadPathsAsync([path]);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            RememberRecent(paths);
            await LoadPathsAsync(paths);
        }
    }

    /// <param name="asFolders">Folders open as they are, as one archive each, instead of being searched for what's in them.</param>
    private async Task LoadPathsAsync(IEnumerable<string> paths, bool asFolders = false)
    {
        // a project file opens the project; so does a folder that is one project's folder
        paths = [.. paths];
        var projects = paths.Where(p => File.Exists(p) && p.EndsWith(WorkbenchProject.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        var projectFolders = asFolders ? [] : paths.Where(Directory.Exists).Where(d => WorkbenchProject.FileIn(d) is not null).ToList();
        if (projects.Count > 0 || projectFolders.Count > 0)
        {
            await OpenProjectAsync(projects.Count > 0 ? projects[0] : WorkbenchProject.FileIn(projectFolders[0])!);
            paths = [.. paths.Except(projects).Except(projectFolders)];
            if (!paths.Any()) return;
        }

        // string tables open in an editor of their own rather than as archives
        var strings = paths.Where(p => File.Exists(p) && StringsWindow.CanOpen(p)).ToList();
        foreach (string path in strings) StringsWindow.Open(path, this);
        paths = [.. paths.Except(strings)];
        // so do XML files, which open in the XML editor
        var xml = paths.Where(p => File.Exists(p) && XmlWindow.CanOpen(p)).ToList();
        foreach (string path in xml) XmlWindow.Open(path, this);
        paths = [.. paths.Except(xml)];
        // and JSON, in the JSON editor (a converted CR2W file can be saved back as one from there)
        var json = paths.Where(p => File.Exists(p) && JsonWindow.CanOpen(p)).ToList();
        foreach (string path in json) JsonWindow.Open(path, this);
        paths = [.. paths.Except(json)];
        // and meshes, which open in the mesh viewer
        var meshes = paths.Where(p => File.Exists(p) && MeshWindow.CanShow(p)).ToList();
        foreach (string path in meshes) MeshWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        paths = [.. paths.Except(meshes)];
        // and cutscenes, which open in the cutscene viewer
        var cutscenes = paths.Where(p => File.Exists(p) && CutsceneWindow.CanShow(p)).ToList();
        foreach (string path in cutscenes) CutsceneWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        paths = [.. paths.Except(cutscenes)];
        // and scenes, which open in the scene viewer
        var scenes = paths.Where(p => File.Exists(p) && SceneWindow.CanShow(p)).ToList();
        foreach (string path in scenes) SceneWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        paths = [.. paths.Except(scenes)];
        // and entities, which open in the entity viewer
        var entities = paths.Where(p => File.Exists(p) && EntityWindow.CanShow(p)).ToList();
        foreach (string path in entities) EntityWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        foreach (string path in paths.Where(p => File.Exists(p) && BehTreeWindow.CanShow(p)))
            BehTreeWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        foreach (string path in paths.Where(p => File.Exists(p) && DataViewers.CanShow(p)))
            DataViewers.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        paths = [.. paths.Except(entities)];
        if ((strings.Count > 0 || xml.Count > 0 || meshes.Count > 0 || cutscenes.Count > 0 || scenes.Count > 0 || entities.Count > 0) && !paths.Any()) return;

        var files = new List<string>();
        foreach (var path in paths)
        {
            if (asFolders && Directory.Exists(path))
            {
                files.Add(path);
            }
            else if (Directory.Exists(path))
            {
                try
                {
                    files.AddRange(ArchivesIn(path));
                }
                catch (Exception ex)
                {
                    ShowError(L.T("MainWindow.CouldNotReadFolder", path, ex.Message));
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
            if (!paths.Any(File.Exists)) SetStatus(L.T("MainWindow.NoBundleRsblobWs"));
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
            LoadingText.Text = files.Count == 1 ? L.T("MainWindow.Loading2", Path.GetFileName(file)) : L.T("MainWindow.Loading3", Path.GetFileName(file), i + 1, files.Count);
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
        SetStatus(L.T("MainWindow.Open", L.T("Count.Archive", _roots.Count), L.T("Count.File", totalFiles)));

        if (failures.Count > 0)
            ShowError(L.T("MainWindow.SomeNotOpened", string.Join("\n", failures.Take(15))) +
                      (failures.Count > 15 ? L.T("MainWindow.AndMore", failures.Count - 15) : ""));
    }

    /// <summary>A folder is a folder of strings, or else a script project.</summary>
    /// <summary>
    /// What a folder holds that opens as an archive, as Open folder finds it: its bundles, compiled scripts and texture
    /// caches, each folder of .w3strings files, and each scripts folder of loose scripts (as a script project).
    /// </summary>
    internal static List<string> ArchivesIn(string folder)
    {
        var found = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories).ToList();
        var files = new List<string>();
        files.AddRange(found
            .Where(f => f.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || ScriptBlob.LooksLikeBlob(f) || TextureCacheArchive.LooksLikeCache(f))
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
            .Select(f => ScriptFolder.RootOf(f, folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(root => !ScriptFolder.IsGameScripts(root))
            .Order(StringComparer.OrdinalIgnoreCase));
        return files;
    }

    internal static IArchive OpenArchive(string file) =>
        Directory.Exists(file) ? Directory.EnumerateFiles(file, "*.w3strings").Any() ? StringsFolder.Open(file) : ScriptFolder.Open(file)
        : ScriptBlob.LooksLikeBlob(file) ? ScriptArchive.Open(file)
        : TextureCacheArchive.LooksLikeCache(file) ? TextureCacheArchive.Open(file)
        : Bundle.Open(file);

    // ---------------------------------------------------------------- projects

    /// <summary>The open project's files, as listed in the tree. Null when no project is open.</summary>
    public ScriptFolder? ProjectArchive => _roots.Select(r => r.Archive).OfType<ScriptFolder>().FirstOrDefault(a => a.Project is not null);

    private FolderNode? ProjectNode => _roots.FirstOrDefault(r => r.Archive is ScriptFolder { Project: not null });

    private void UpdateProjectUi()
    {
        var project = ProjectWorkspace.Current;
        Title = project is null ? "W3 Mod Workbench" : L.T("MainWindow.W3ModWorkbench", project.Name);
        ProjectButtonText.Text = project is null ? L.T("MainWindow.Project") : project.Name;
        ProjectButton.ToolTip = project is null ? L.T("MainWindow.NewProjectOpenProject2")
            : L.T("MainWindow.ProjectPropertiesAddFiles", project.FilePath);
    }

    private void NewProject_Click(object sender, RoutedEventArgs e) => _ = NewProjectAsync();

    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenProjectDialog();

    /// <summary>New project: asks for its name and folder, makes the .w3bench file and opens it.</summary>
    public async Task NewProjectAsync(string? root = null, string? name = null, string? scripts = null)
    {
        var dialog = ProjectWindow.ForNew(this, root, name, scripts);
        if (dialog.ShowDialog() == true && dialog.Result is { } project)
        {
            _settings.LastOpenFolder = project.Root;
            await OpenProjectAsync(project.FilePath);
        }
    }

    private void OpenProjectDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("MainWindow.OpenProject"),
            Filter = L.T("MainWindow.W3ModWorkbenchProjects"),
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastOpenFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        _ = OpenProjectAsync(dialog.FileName);
    }

    /// <summary>
    /// Opens a project in the tree, as the workspace's project: the one open before it is closed first (asking about
    /// unsaved changes), and the files that were open last time are opened again. False when it wasn't opened.
    /// </summary>
    /// <param name="quiet">Opened by the tool on its own at the start: a project that is gone isn't reported.</param>
    public async Task<bool> OpenProjectAsync(string file, bool quiet = false)
    {
        file = Path.GetFullPath(file);
        WorkbenchProject project;
        try
        {
            project = WorkbenchProject.Load(file);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ProjectWorkspace.ForgetRecent(file);
            if (!quiet) ShowError(L.T("MainWindow.CouldNotOpened", Path.GetFileName(file), ex.Message));
            return false;
        }

        if (ProjectWorkspace.Current is { } current)
        {
            if (current.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                if (ProjectNode is { } open) open.IsSelected = true;
                return true;
            }
            if (!await CloseProjectAsync()) return false;
        }

        // loose scripts open from the project's scripts folder are replaced by the project's own listing of them
        string scriptsPath = project.ScriptsPath.TrimEnd('\\');
        foreach (var loose in _roots.Where(r => r.Archive is ScriptFolder { Project: null } f && f.FilePath.Equals(scriptsPath, StringComparison.OrdinalIgnoreCase)).ToList())
            _roots.Remove(loose);

        LoadingOverlay.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Collapsed;
        LoadingText.Text = L.T("MainWindow.Opening", project.Name);
        LoadingProgress.IsIndeterminate = true;
        FolderNode node;
        try
        {
            node = await Task.Run(() => FolderNode.Build(ScriptFolder.OpenProject(project), OnEntryChecked));
        }
        finally
        {
            LoadingProgress.IsIndeterminate = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        node.IsExpanded = true;
        _roots.Insert(0, node);
        ProjectWorkspace.Open(project);
        UpdateProjectUi();
        UpdateEmptyState();
        node.IsSelected = true;
        if (FolderTree.SelectedItem is null) RefreshView();
        // a project kept inside the game's folders can't be changed there: say so once, now
        if (FileSafety.Protection(project.FilePath) is { } protection)
            ShowError(L.T("MainWindow.InsideGamesFoldersFiles", project.Name, protection));
        SetStatus(Directory.Exists(project.ScriptsPath)
            ? L.T("MainWindow.Opened", project.Name, L.T("Count.File", node.TotalFiles))
            : L.T("MainWindow.OpenedButScriptsFolder", project.Name, project.ScriptsFolder));
        await RestoreWorkspaceAsync(project);
        return true;
    }

    /// <summary>Opens again the scripts that were open in the project when it was closed, the one in front last.</summary>
    private async Task RestoreWorkspaceAsync(WorkbenchProject project)
    {
        var state = ProjectWorkspace.LoadState(project);
        if (state.OpenFiles.Count == 0 || ProjectNode is not { } node) return;
        var items = node.AllFiles().Where(i => i.Entry is LooseScriptEntry { IsScript: true }).ToList();
        IEnumerable<string> order = state.OpenFiles.Where(f => !f.Equals(state.ActiveFile, StringComparison.OrdinalIgnoreCase));
        if (state.ActiveFile is not null) order = order.Append(state.ActiveFile);
        foreach (string path in order.Take(25))
            if (items.FirstOrDefault(i => ((LooseScriptEntry)i.Entry).FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } item)
                await PreviewAsync(item);
    }

    private void SaveWorkspaceState()
    {
        if (ProjectWorkspace.Current is not { } project) return;
        var (files, active) = PreviewWindow.OpenFilesIn(project.Contains);
        ProjectWorkspace.SaveState(project, new WorkspaceState { OpenFiles = files, ActiveFile = active });
    }

    /// <summary>
    /// Closes the project. Files in it with unsaved changes are asked about first; false when the answer was to keep
    /// working. Their tabs stay open, as files that are no longer in a project.
    /// </summary>
    public async Task<bool> CloseProjectAsync()
    {
        if (ProjectWorkspace.Current is not { } project) return true;
        var unsaved = PreviewWindow.UnsavedDocuments().Where(d => d.Item.DiskPath is { } p && project.Contains(p)).ToList();
        if (unsaved.Count > 0)
        {
            var choice = await Message.Ask(unsaved.Count == 1 ? L.T("MainWindow.SaveChanges", unsaved[0].FileName) : L.T("MainWindow.SaveChangesFiles", unsaved.Count),
                L.T("MainWindow.ChangesLostBefore", PreviewWindow.Names(unsaved), project.Name, unsaved.Count),
                MessageKind.Question, unsaved.Count == 1 ? L.T("Common.Save") : L.T("MainWindow.SaveAll"), L.T("Common.DontSave"));
            if (choice == MessageChoice.Cancel || choice == MessageChoice.Primary && !PreviewWindow.SaveAll(unsaved)) return false;
        }
        SaveWorkspaceState();
        foreach (var node in _roots.Where(r => r.Archive is ScriptFolder { Project: not null }).ToList()) _roots.Remove(node);
        ProjectWorkspace.Open(null);
        UpdateProjectUi();
        AfterBundlesClosed();
        return true;
    }

    /// <summary>Reads the project's files again, after they were added to or removed from it or changed outside the tool.</summary>
    public async Task ReloadProjectAsync()
    {
        if (ProjectWorkspace.Current is not { } project || ProjectNode is not { } old) return;
        // the folders open and the one picked, by their paths, to open and pick them again in the new tree
        var open = new HashSet<string>(old.Folders().Where(f => f.IsExpanded).Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
        var picked = old.Folders().FirstOrDefault(f => f.IsSelected);
        var highlighted = Highlighted().Select(i => i.Entry).OfType<LooseScriptEntry>().Select(l => l.FullPath).ToList();
        var node = await Task.Run(() => FolderNode.Build(ScriptFolder.OpenProject(project), OnEntryChecked));
        int index = _roots.IndexOf(old);
        if (index < 0) return; // closed while it was being read
        foreach (var folder in node.Folders().Where(f => !f.IsBundle && open.Contains(f.FullPath))) folder.IsExpanded = true;
        node.IsExpanded = true;
        _roots[index] = node;
        if (picked is not null)
        {
            // the folder picked, or the nearest one above it that's still there (a folder whose last file went is gone)
            var again = picked.IsBundle ? node : null;
            for (string? path = picked.IsBundle ? null : picked.FullPath; again is null && path is not null; path = Path.GetDirectoryName(path) is { Length: > 0 } up ? up : null)
                again = node.Folders().FirstOrDefault(f => !f.IsBundle && f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            (again ?? node).IsSelected = true;
        }
        // once the folder picked again has filled the list
        var highlight = _reloadHighlight ?? highlighted;
        _reloadHighlight = null;
        _ = Dispatcher.InvokeAsync(() => Highlight(highlight), System.Windows.Threading.DispatcherPriority.Background);
        W3ModWorkbench.Core.Scripts.Navigation.ScriptWorkspace.Reindex(node.Archive);
        UpdateProjectUi();
        AfterBundlesClosed();
        ProjectWorkspace.NotifyChanged();
    }

    /// <summary>Adds files or folders to the open project.</summary>
    public async Task<bool> AddToProjectAsync(IEnumerable<string> paths)
    {
        if (ProjectWorkspace.Current is not { } project)
        {
            ShowError(L.T("MainWindow.NoProjectOpenMake"));
            return false;
        }
        int added = 0;
        bool outside = false;
        foreach (string path in paths)
        {
            if (!project.Add(path)) continue;
            added++;
            outside |= path.EndsWith(".ws", StringComparison.OrdinalIgnoreCase);
        }
        if (added == 0) return true;
        try
        {
            project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.ProjectCouldNotSaved", ex.Message));
            return false;
        }
        await ReloadProjectAsync();
        SetStatus(L.T("MainWindow.AddedTo", added, project.Name)
            + (outside ? L.T("MainWindow.OutsideNotCompiled") : ""));
        return true;
    }

    /// <summary>A project for a folder of loose scripts: asks for its name, in the mod's folder above the scripts.</summary>
    public async Task CreateProjectFromScriptsAsync(string scriptsFolder)
    {
        scriptsFolder = Path.GetFullPath(scriptsFolder).TrimEnd('\\');
        string root = ScriptFolder.SuggestedRoot(scriptsFolder);
        if (WorkbenchProject.FileIn(root) is { } existing)
        {
            if (await Message.Ask(L.T("MainWindow.FolderProjectAlready"), L.T("MainWindow.OpenScriptsPartProject", Path.GetFileName(existing), root),
                    MessageKind.Question, L.T("MainWindow.OpenProject2"), L.T("Common.Close")) == MessageChoice.Primary)
                await OpenProjectAsync(existing);
            return;
        }
        string relative = Path.GetRelativePath(root, scriptsFolder).Replace('\\', '/');
        await NewProjectAsync(root, ScriptFolder.SuggestedName(scriptsFolder), relative == "." ? "scripts" : relative);
    }

    private void Project_Click(object sender, RoutedEventArgs e)
    {
        var project = ProjectWorkspace.Current;
        var menu = new ContextMenu { PlacementTarget = ProjectButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Add(string header, string gesture, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Add(L.T("MainWindow.ScriptBench"), "Ctrl+Shift+E", () => PreviewWindow.OpenBench(this))
            .ToolTip = L.T("MainWindow.WhereScriptsWrittenRead");
        menu.Items.Add(new Separator());
        Add(L.T("MainWindow.NewProject"), "Ctrl+Shift+N", () => _ = NewProjectAsync());
        Add(L.T("MainWindow.OpenProject3"), "Ctrl+Shift+O", OpenProjectDialog);
        Add(L.T("MainWindow.NewModMenu"), "", () => XmlWindow.OpenNewMenu(this))
            .ToolTip = L.T("MainWindow.MenuYourModsOptions");
        var recent = new MenuItem { Header = L.T("MainWindow.RecentProjects") };
        var recents = ProjectWorkspace.Recent().Where(p => project is null || !p.Equals(project.FilePath, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string path in recents)
        {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path).Replace("_", "__"), ToolTip = path };
            item.Click += (_, _) => _ = OpenProjectAsync(path);
            recent.Items.Add(item);
        }
        recent.IsEnabled = recents.Count > 0;
        menu.Items.Add(recent);
        menu.Items.Add(new Separator());
        Add(L.T("MainWindow.ProjectProperties"), "", () => ProjectProperties_Click(this, new RoutedEventArgs()), project is not null);
        Add(L.T("MainWindow.AddFilesProject"), "", () => ProjectAddFiles_Click(this, new RoutedEventArgs()), project is not null);
        Add(L.T("MainWindow.AddFolderProject"), "", AddFolderToProject, project is not null);
        Add(L.T("MainWindow.RefreshProject"), "", () => _ = ReloadProjectAsync(), project is not null);
        Add(L.T("MainWindow.OpenProjectFolder"), "", () => ProjectReveal_Click(this, new RoutedEventArgs()), project is not null);
        menu.Items.Add(new Separator());
        Add(L.T("MainWindow.CompileScripts"), "", () => _ = PreviewWindow.CompileProjectAsync(this), project is not null)
            .ToolTip = L.T("MainWindow.CompileProjectsScriptsScript");
        Add(project is null ? L.T("MainWindow.BuildMod") : L.T("MainWindow.Build2", project.ModFolderName.Replace("_", "__")), "Ctrl+Shift+B", () => _ = BuildModAsync(), project is not null);
        Add(project is null ? L.T("MainWindow.InstallMod") : L.T("MainWindow.Install4", project.ModFolderName.Replace("_", "__")), "", () => _ = InstallModAsync(), project is not null);
        menu.Items.Add(new Separator());
        Add(L.T("MainWindow.CloseProject"), "", () => _ = CloseProjectAsync(), project is not null);
        menu.IsOpen = true;
    }

    private void ProjectProperties_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectWorkspace.Current is not { } project) return;
        var dialog = ProjectWindow.ForProperties(this, project);
        if (dialog.ShowDialog() != true) return;
        UpdateProjectUi();
        if (dialog.FilesChanged) _ = ReloadProjectAsync();
        else ProjectWorkspace.NotifyChanged();
    }

    private async void ProjectAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectWorkspace.Current is not { } project) return;
        var dialog = new OpenFileDialog { Title = L.T("MainWindow.AddFilesProject2"), Multiselect = true, InitialDirectory = Directory.Exists(project.Root) ? project.Root : "" };
        if (dialog.ShowDialog(this) != true) return;

        // a script only compiles with the project from inside its scripts folder: one from anywhere else is copied in, when you agree
        string scripts = Path.GetFullPath(project.ScriptsPath).TrimEnd('\\') + "\\";
        var outside = dialog.FileNames.Where(f => f.EndsWith(".ws", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFullPath(f).StartsWith(scripts, StringComparison.OrdinalIgnoreCase)).ToList();
        var rest = dialog.FileNames.Except(outside).ToList();
        if (outside.Count > 0)
        {
            string which = outside.Count == 1 ? Path.GetFileName(outside[0]) : L.T("MainWindow.Scripts", outside.Count);
            var choice = await Message.Ask(L.T("MainWindow.CopyIntoProjectsScripts", which),
                L.T("MainWindow.CopyOrAddWhere", project.ScriptsPath, outside.Count),
                MessageKind.Question, L.T("MainWindow.CopyIntoScripts"), L.T("MainWindow.AddWhere"));
            if (choice == MessageChoice.Cancel) return;
            if (choice == MessageChoice.Secondary) rest.AddRange(outside);
            else if (!await CopyScriptsInAsync(project, outside)) return;
        }
        if (rest.Count > 0) await AddToProjectAsync(rest);
    }

    /// <summary>
    /// Copies scripts into the root of the project's scripts folder, where they're compiled with it. A script with the same
    /// name there already is left alone and said so, since two copies of a class wouldn't compile.
    /// </summary>
    private async Task<bool> CopyScriptsInAsync(WorkbenchProject project, List<string> files)
    {
        var copied = new List<string>();
        var skipped = new List<string>();
        try
        {
            Directory.CreateDirectory(project.ScriptsPath);
            foreach (string file in files)
            {
                string target = Path.Combine(project.ScriptsPath, Path.GetFileName(file));
                if (File.Exists(target)) skipped.Add(Path.GetFileName(file));
                else
                {
                    File.Copy(file, target);
                    copied.Add(Path.GetFileName(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.ScriptsCouldNotCopied", ex.Message));
            return false;
        }
        if (copied.Count > 0) await ReloadProjectAsync();
        if (skipped.Count > 0)
            await ShowMessage(L.T("MainWindow.SomeScriptsWerentCopied"), L.T("MainWindow.ScriptsFolderHasFile", string.Join(", ", skipped)), MessageKind.Warning);
        if (copied.Count > 0)
            SetStatus(L.T("MainWindow.CopiedIntoScriptsFolder", string.Join(", ", copied), project.Name));
        return true;
    }

    private void AddFolderToProject()
    {
        if (ProjectWorkspace.Current is not { } project) return;
        var dialog = new OpenFolderDialog { Title = L.T("MainWindow.AddFolderProject2"), InitialDirectory = Directory.Exists(project.Root) ? project.Root : "" };
        if (dialog.ShowDialog(this) == true) _ = AddToProjectAsync([dialog.FolderName]);
    }

    private void ProjectRefresh_Click(object sender, RoutedEventArgs e) => _ = ReloadProjectAsync();

    private void ProjectReveal_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectWorkspace.Current is { } project && Directory.Exists(project.Root))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{project.Root}\"") { UseShellExecute = true });
    }

    private void TreeCreateProject_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode node) return;
        while (node.Parent is not null) node = node.Parent;
        if (node.Archive is ScriptFolder { Project: null } folder) _ = CreateProjectFromScriptsAsync(folder.FilePath);
    }

    private void TreeCompile_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode node) return;
        while (node.Parent is not null) node = node.Parent;
        if (node is { IsLooseScripts: true, Archive: ScriptFolder folder }) CompileWindow.Show(this, folder);
    }

    /// <summary>The right-click menu on the tree shows what applies to the folder under it: the project's, loose scripts', or a bundle's.</summary>
    private void TreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        FolderNode? root = FolderTree.SelectedItem as FolderNode;
        while (root?.Parent is not null) root = root.Parent;
        bool project = root is { IsProject: true };
        bool loose = root is { IsLooseScripts: true };
        TreeCreateProjectItem.Visibility = loose ? Visibility.Visible : Visibility.Collapsed;
        TreeCompileItem.Visibility = loose ? Visibility.Visible : Visibility.Collapsed;
        if (loose)
        {
            bool canCompile = PreviewWindow.CurrentCompiler() is not null;
            TreeCompileItem.IsEnabled = canCompile;
            TreeCompileItem.ToolTip = canCompile
                ? L.T("MainWindow.CompileTheseScriptsTip", Path.Combine(Path.GetDirectoryName(root!.Archive.FilePath.TrimEnd('\\')) ?? "", "precompiled.rsblob"))
                : CompilerChoice.NothingToCompileWith;
        }
        foreach (var item in new[] { TreeProjectPropertiesItem, TreeAddFilesItem, TreeNewMeshItem, TreeRefreshItem, TreeRevealItem })
            item.Visibility = project ? Visibility.Visible : Visibility.Collapsed;
        TreePasteItem.Visibility = project && PasteFolder() is not null ? Visibility.Visible : Visibility.Collapsed;
        TreePasteItem.IsEnabled = ClipboardFiles().Count > 0;
        bool writable = DiskFolder(FolderTree.SelectedItem as FolderNode) is not null;
        TreeToJsonItem.Visibility = TreeFromJsonItem.Visibility = writable ? Visibility.Visible : Visibility.Collapsed;
        TreeProjectSeparator.Visibility = project || loose ? Visibility.Visible : Visibility.Collapsed;
        TreeCloseItem.Header = project ? L.T("MainWindow.CloseProject") : loose ? L.T("MainWindow.CloseScripts") : root is { IsGroup: true } ? L.T("MainWindow.CloseMod") : L.T("MainWindow.CloseBundle");
    }

    // ---------------------------------------------------------------- copying into the project

    /// <summary>
    /// What a file in the list would be copied into a project as: its game path, and how to read it. Null for a file that
    /// isn't copied: one already yours to edit (a project's, or a loose file outside the game), a texture from a cache,
    /// whose pixels aren't a game file, or strings, which the strings editor saves where you choose.
    /// </summary>
    private static (string GamePath, Func<byte[]> Read)? CopySource(EntryItem item)
    {
        var entry = item.Entry;
        if (entry is TextureEntry or StringsEntry) return null;
        if (entry is LooseScriptEntry loose)
        {
            if (FileSafety.CanEdit(loose.FullPath)) return null;
            // a loose game file keeps its place under the game's content; a game script its place under the scripts folder
            string? gamePath = FileSafety.GamePathOf(loose.FullPath)
                ?? (loose.IsScript && entry.Archive is ScriptFolder ? entry.Path : null);
            return gamePath is null ? null : (gamePath, () => File.ReadAllBytes(loose.FullPath));
        }
        return (entry.Path, () =>
        {
            if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
            using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return entry.Read(fs);
        });
    }

    private async void GridCopyToProject_Click(object sender, RoutedEventArgs e)
    {
        var items = Highlighted();
        // a mesh is no use without its buffers, so they come along, as when extracting
        var entries = items.Select(i => i.Entry).Where(en => en is not LooseScriptEntry).ToList();
        var withCompanions = await Task.Run(() => Companions.With(entries, OpenArchives(), out _));
        var sources = items.Where(i => i.Entry is LooseScriptEntry).Select(CopySource)
            .Concat(withCompanions.Select(en => CopySource(new EntryItem(en, (_, _) => { }))))
            .OfType<(string, Func<byte[]>)>().ToList();
        if (sources.Count > 0) await CopyToProjectAsync(sources, open: items.Count == 1);
    }

    /// <summary>
    /// What "Make a mod of this" starts from: one game file's path, and how to read it (a texture listed from its cache
    /// is found by its path). Null for scripts and strings, which are copied as they are, and for files already yours.
    /// </summary>
    private static (string GamePath, Func<byte[]>? Read)? MakeModSource(EntryItem item)
    {
        if (item.Entry is TextureEntry texture) return (texture.Path, null);
        if (CopySource(item) is not { } source || Path.GetExtension(source.GamePath).ToLowerInvariant() is ".ws" or ".w3strings" or "") return null;
        return source;
    }

    private void GridMakeMod_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault() is not { } item || MakeModSource(item) is not { } source) return;
        byte[]? data = null;
        try { data = source.Read?.Invoke(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // the game's own copy is read instead
        }
        ModMakerWindow.Open(this, source.GamePath, data);
    }

    /// <summary>
    /// The open project, to put files in. With none open, asks to make one or open one: the game's files are changed only
    /// as copies in a project. Null when there's still none, or the project is somewhere files can't be written.
    /// </summary>
    public async Task<WorkbenchProject?> EnsureProjectAsync(string why, MessageCard? card = null)
    {
        if (ProjectWorkspace.Current is null)
        {
            var choice = await (card ?? Message).Ask(L.T("MainWindow.NeedsProject"), L.T("MainWindow.MakeProjectYourMod", why),
                MessageKind.Question, L.T("MainWindow.NewProject"), L.T("MainWindow.OpenProject3"));
            if (choice == MessageChoice.Primary) await NewProjectAsync();
            else if (choice == MessageChoice.Secondary)
            {
                var dialog = new OpenFileDialog { Title = L.T("MainWindow.OpenProject"), Filter = L.T("MainWindow.W3ModWorkbenchProjects") };
                if (dialog.ShowDialog() == true) await OpenProjectAsync(dialog.FileName);
            }
        }
        if (ProjectWorkspace.Current is not { } project) return null;
        if (FileSafety.Protection(project.FilePath) is { } protection)
        {
            ShowError(L.T("MainWindow.InsideGamesFoldersWhere", project.Name, protection));
            return null;
        }
        return project;
    }

    /// <summary>
    /// Copies files into the open project at their game paths: scripts into its scripts folder, everything else into its
    /// workspace. A file the project has already is left as it is, so nothing of yours is overwritten. With <paramref name="open"/>
    /// and one file, the copy opens. False when nothing was copied.
    /// </summary>
    public async Task<bool> CopyToProjectAsync(IReadOnlyList<(string GamePath, Func<byte[]> Read)> files, bool open = false)
    {
        if (await EnsureProjectAsync(L.T("MainWindow.GameFilesChangedCopies")) is not { } project)
            return false;
        var copied = new List<string>();
        var existing = new List<string>();
        var failed = new List<string>();
        await Task.Run(() =>
        {
            foreach (var (gamePath, read) in files)
            {
                string target = project.CopyPathFor(gamePath);
                if (File.Exists(target)) { existing.Add(target); continue; }
                try
                {
                    FileSafety.CheckWrite(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, read());
                    copied.Add(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    failed.Add($"{gamePath}: {ex.Message}");
                }
            }
        });
        // the workspace is part of the project, so what's copied there is listed with it
        if (copied.Concat(existing).Any(p => project.GamePathOf(p) is not null) && !project.Contains(project.WorkspacePath))
            await AddToProjectAsync([project.WorkspacePath]);
        else if (copied.Count > 0)
            await ReloadProjectAsync();

        SetStatus(copied.Count > 0
            ? L.T("MainWindow.CopiedInto", L.T("Count.File", copied.Count), project.Name) + (existing.Count > 0 ? L.T("MainWindow.HadAlready", L.T("Count.File", existing.Count)) : "")
            : existing.Count > 0 ? L.T("MainWindow.HasAlready", project.Name, existing.Count) : L.T("MainWindow.NothingCopied"));
        if (failed.Count > 0) ShowError(L.T("MainWindow.SomeFilesCouldNot", string.Join("\n", failed.Take(15))));
        // REDkit's source files can be copied, but the game can't load them uncooked: said now, not at the first build
        // (textures excepted, which Build cooks)
        var uncooked = copied.Where(p => project.GamePathOf(p) is not null && ModBuilder.IsUncooked(p) && !ModBuilder.BuildCooks(p)).ToList();
        if (uncooked.Count > 0)
            ShowError(L.T("MainWindow.CookedTheyreREDkitSource", (uncooked.Count == 1 ? L.T("MainWindow.CopyIsnt") : L.T("MainWindow.CopiesArent", uncooked.Count)), string.Join("\n", uncooked.Take(10).Select(p => "• " + project.GamePathOf(p)))));
        if (open && copied.Concat(existing).FirstOrDefault() is { } first) OpenProjectFile(first);
        return copied.Count > 0;
    }

    /// <summary>Opens one of the project's files the way double-clicking it in the list does.</summary>
    public void OpenProjectFile(string path)
    {
        var item = ProjectNode?.AllFiles().FirstOrDefault(i => i.Entry is LooseScriptEntry l && l.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item is not null) OpenItem(item);
        else if (MeshWindow.CanShow(path)) MeshWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        else if (CutsceneWindow.CanShow(path)) CutsceneWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        else if (SceneWindow.CanShow(path)) SceneWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        else if (EntityWindow.CanShow(path)) EntityWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        else if (BehTreeWindow.CanShow(path)) BehTreeWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        else if (DataViewers.CanShow(path)) DataViewers.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
        else if (XmlWindow.CanOpen(path)) XmlWindow.Open(path, this);
        else if (JsonWindow.CanOpen(path)) JsonWindow.Open(path, this);
        else if (PropertiesWindow.CanShow(path)) PropertiesWindow.OpenFile(path, new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles), this);
    }

    private void GridAddToProject_Click(object sender, RoutedEventArgs e)
    {
        var files = Highlighted().Select(i => i.Entry).OfType<LooseScriptEntry>().Select(l => l.FullPath).Where(FileSafety.CanEdit).ToList();
        if (files.Count > 0) _ = AddToProjectAsync(files);
    }

    private void GridRemoveFromProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectWorkspace.Current is not { } project) return;
        int removed = Highlighted().Select(i => i.Entry).OfType<LooseScriptEntry>().Count(l => project.Remove(l.FullPath));
        if (removed == 0) return;
        try { project.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.ProjectCouldNotSaved", ex.Message));
            return;
        }
        _ = ReloadProjectAsync();
        SetStatus(L.T("MainWindow.RemovedFrom", removed, project.Name));
    }

    /// <summary>Closes an open archive, by its path.</summary>
    public void CloseArchive(string filePath)
    {
        foreach (var node in _roots.Where(r => r.Archive.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)).ToList())
            _roots.Remove(node);
        AfterBundlesClosed();
    }

    private async void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        if (!await CloseProjectAsync()) return;
        _roots.Clear();
        AfterBundlesClosed();
    }

    private void TreeCloseBundle_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode node) return;
        while (node.Parent is not null) node = node.Parent;
        if (node.IsProject)
        {
            _ = CloseProjectAsync();
            return;
        }
        if (node.IsGroup)
            foreach (var member in node.Children.ToList()) _roots.Remove(member);
        else _roots.Remove(node);
        AfterBundlesClosed();
    }

    /// <summary>
    /// What the tree lists: the open archives, with the bundles, compiled scripts, strings and loose scripts of one mod
    /// gathered under that mod's name. A mod with only one archive, projects and the game's scripts stay as they are.
    /// </summary>
    private void RebuildTree()
    {
        static bool Groups(FolderNode root) => root.Archive is not ScriptFolder || root.IsLooseScripts;
        var old = _tree.Where(n => n.IsGroup).ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
        var byOwner = _roots.Where(Groups).GroupBy(r => r.Archive.Owner, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var shown = new List<FolderNode>();
        var made = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots)
        {
            if (Groups(root) && byOwner.TryGetValue(root.Archive.Owner, out var members))
            {
                if (!made.Add(root.Archive.Owner)) continue;
                var group = FolderNode.Group(root.Archive.Owner, members);
                if (old.TryGetValue(root.Archive.Owner, out var before))
                {
                    group.IsExpanded = before.IsExpanded;
                    group.IsSelected = before.IsSelected;
                }
                else group.IsExpanded = false;
                shown.Add(group);
            }
            else
            {
                root.SetInGroup(false);
                shown.Add(root);
            }
        }
        _tree.Clear();
        foreach (var node in shown) _tree.Add(node);
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
        SetStatus(_roots.Count == 0 ? L.T("MainWindow.Ready") : L.T("MainWindow.Open2", L.T("Count.Archive", _roots.Count)));
    }

    private void UpdateEmptyState()
    {
        bool any = _roots.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        BrowserPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        CloseAllButton.IsEnabled = any;
    }

    // ---------------------------------------------------------------- view

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        RefreshView();
    }

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
        // a folder's own files only, when that's the view, until there's a search: that looks in every folder under it
        bool own = !_settings.ListSubfolderFiles && SearchBox.Text.Trim().Length == 0;
        List<EntryItem> scope = FolderTree.SelectedItem is FolderNode node
            ? own ? [.. node.Files] : [.. node.AllFiles()]
            : own ? [.. _roots.SelectMany(r => r.Files)] : [.. _roots.SelectMany(r => r.AllFiles())];
        var result = FileFilterResult.Apply(scope, SearchBox.Text, _typeFilter, _classFilter);
        FillChoices(result);

        _shown = result.Shown;
        BindFiles();
        NoMatchesText.Text = result.NoMatches(_typeFilter, _classFilter);
        NoMatchesText.Visibility = _shown.Count == 0 && _roots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtractShownButton.IsEnabled = _shown.Count > 0;
        ExtractShownText.Text = _shown.Count > 0 ? L.T("MainWindow.ExtractShown2", _shown.Count) : L.T("MainWindow.ExtractShown");
        QueueStatusUpdate();
    }

    /// <summary>Every word must appear in the path; words with * or ? are wildcards on the file name.</summary>
    internal static Func<EntryItem, bool>? BuildMatcher(string text)
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
        OpenItem(item);
    }

    /// <summary>What double-clicking a file does: opens it in the editor or viewer for its kind, or ticks it when there's none.</summary>
    private void OpenItem(EntryItem item)
    {
        if (item.Entry is StringsEntry strings)
            StringsWindow.Open(strings.FullPath, this);
        else if (item.Entry is TextureEntry texture)
            OpenTexture(texture);
        else if (IsTextureHeader(item))
            _ = OpenTextureHeaderAsync(item);
        else if (TextureWindow.CanShow(item.Name))
            _ = OpenPictureAsync(item);
        else if (MeshWindow.CanShow(item.Name))
            _ = OpenMeshAsync(item);
        else if (CutsceneWindow.CanShow(item.Name))
            _ = OpenCutsceneAsync(item);
        else if (SceneWindow.CanShow(item.Name))
            _ = OpenSceneAsync(item);
        else if (EntityWindow.CanShow(item.Name))
            _ = OpenEntityAsync(item);
        else if (BehTreeWindow.CanShow(item.Name))
            _ = OpenBehTreeAsync(item);
        else if (DataViewers.CanShow(item.Name))
            _ = OpenDataViewerAsync(item);
        else if (item.Entry is LooseScriptEntry { IsScript: false } && XmlWindow.CanOpen(item.Name))
            _ = OpenXmlAsync(item);
        else if (item.Entry is LooseScriptEntry { IsScript: false } && JsonWindow.CanOpen(item.Name))
            _ = OpenJsonAsync(item, asCr2w: false);
        else if (item.IsPreviewable)
            _ = PreviewAsync(item);
        else if (XmlWindow.CanOpen(item.Name))
            _ = OpenXmlAsync(item);
        else if (JsonWindow.CanOpen(item.Name))
            _ = OpenJsonAsync(item, asCr2w: false);
        else if (PropertiesWindow.CanShow(item.Name))
            _ = OpenPropertiesAsync(item);
        else
            item.IsChecked = !item.IsChecked;
    }

    /// <summary>An .xbm, .texarray or .w2cube in a bundle: the texture's description, whose pixels are in a texture.cache under the same path.</summary>
    private static bool IsTextureHeader(EntryItem item) => item.IsTextureHeader;

    private static TextureEntry? FindInCache(TextureCacheArchive cache, string path) => ResourceLocator.FindInCache(cache, path);

    /// <summary>
    /// What finds a file's buffers, materials and textures: the archives open here, those beside it, and the game's.
    /// Of the installed mods only the file's own is looked in, so what it uses comes from its mod and then the game.
    /// </summary>
    internal ResourceLocator LocatorFor(ArchiveEntry entry) => new(OpenArchives(), entry.Archive, GameLocator.Resolve(null), ProjectFiles, otherMods: false);

    /// <summary>Reads a cached texture's resource (its .xbm, .w2cube or .texarray) from the bundles, for the texture viewer's properties.</summary>
    private Func<byte[]?> ResourceReader(TextureEntry texture)
    {
        var locator = LocatorFor(texture);
        string path = texture.Path;
        return () => locator.ReadFile(path);
    }

    /// <summary>Opens a texture from a cache in its viewer, with its resource for the properties and the archives open to follow its links.</summary>
    private void OpenTexture(TextureEntry texture, Func<byte[]?>? resource = null) =>
        TextureWindow.Open(texture, this, resource ?? ResourceReader(texture), LocatorFor(texture));

    /// <summary>The open project's workspace, which is looked in before anything else; null with no project open.</summary>
    private static string? ProjectFiles() => ProjectWorkspace.Current?.WorkspacePath;

    /// <summary>Shows a mesh in the mesh viewer, with its buffers, materials and textures found the way the game would.</summary>
    private async Task OpenMeshAsync(EntryItem item)
    {
        // a mesh on disk (the project's own, or a loose one) opens from its file, so it knows where it is
        if (item.Entry is LooseScriptEntry loose)
        {
            MeshWindow.OpenFile(loose.FullPath, LocatorFor(item.Entry), this);
            return;
        }
        try
        {
            var entry = item.Entry;
            byte[] data = await Task.Run(() =>
            {
                if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
                using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return entry.Read(fs);
            });
            MeshWindow.Open(entry.Path, item.BundleName, data, entry.Archive, LocatorFor(entry), this);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
        }
    }

    /// <summary>Shows an entity in the entity viewer, with the files it uses found the way the game would.</summary>
    private async Task OpenEntityAsync(EntryItem item)
    {
        if (item.Entry is LooseScriptEntry loose)
        {
            EntityWindow.OpenFile(loose.FullPath, LocatorFor(item.Entry), this);
            return;
        }
        try
        {
            var entry = item.Entry;
            byte[] data = await ReadAsync(entry);
            EntityWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this, entry.Archive);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
        }
    }

    /// <summary>Shows a spawn tree, steering graph or another of the smaller kinds of file in its viewer.</summary>
    private async Task OpenDataViewerAsync(EntryItem item)
    {
        if (item.Entry is LooseScriptEntry loose)
        {
            DataViewers.OpenFile(loose.FullPath, LocatorFor(item.Entry), this);
            return;
        }
        try
        {
            var entry = item.Entry;
            byte[] data = await ReadAsync(entry);
            DataViewers.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this, entry.Archive);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
        }
    }

    /// <summary>Shows a behaviour tree in its viewer.</summary>
    private async Task OpenBehTreeAsync(EntryItem item)
    {
        if (item.Entry is LooseScriptEntry loose)
        {
            BehTreeWindow.OpenFile(loose.FullPath, LocatorFor(item.Entry), this);
            return;
        }
        try
        {
            var entry = item.Entry;
            byte[] data = await ReadAsync(entry);
            BehTreeWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this, entry.Archive);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
        }
    }

    /// <summary>Shows a scene in the scene viewer, with the files it uses found the way the game would.</summary>
    private async Task OpenSceneAsync(EntryItem item)
    {
        if (item.Entry is LooseScriptEntry loose)
        {
            SceneWindow.OpenFile(loose.FullPath, LocatorFor(item.Entry), this);
            return;
        }
        try
        {
            var entry = item.Entry;
            byte[] data = await ReadAsync(entry);
            SceneWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this, entry.Archive);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
        }
    }

    /// <summary>Shows a cutscene in the cutscene viewer, with the files it uses found the way the game would.</summary>
    private async Task OpenCutsceneAsync(EntryItem item)
    {
        if (item.Entry is LooseScriptEntry loose)
        {
            CutsceneWindow.OpenFile(loose.FullPath, LocatorFor(item.Entry), this);
            return;
        }
        try
        {
            var entry = item.Entry;
            byte[] data = await ReadAsync(entry);
            CutsceneWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this, entry.Archive);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
        }
    }

    /// <summary>Shows a picture file from a bundle or a project (PNG, JPEG, BMP, GIF, TIFF, DDS) in the texture viewer.</summary>
    private async Task OpenPictureAsync(EntryItem item)
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
            if (!TextureWindow.Open(item.Name, $"{item.BundleName}  ·  {entry.Path}", data, this))
                ShowError(L.T("MainWindow.CantShownIsntPicture", item.Name));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
        }
    }

    /// <summary>Texture caches found beside a bundle or in the game folder, kept so the next texture doesn't read the index again.</summary>
    private static readonly Dictionary<string, TextureCacheArchive?> SideCaches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Shows the texture an .xbm in a bundle describes. Its pixels are in a texture.cache, under the same path: one that's
    /// open, else the one beside the bundle, else the game's own.
    /// </summary>
    private async Task OpenTextureHeaderAsync(EntryItem item)
    {
        var found = await FindTextureForHeaderAsync(item);
        var header = item.Entry;
        // the .xbm (or .w2cube, .texarray) double-clicked is the texture's resource, with its properties
        if (found is not null) OpenTexture(found, () => ReadAsync(header).GetAwaiter().GetResult());
        else ShowError(L.T("MainWindow.TexturesPixelsWerentFound", item.Name));
    }

    /// <summary>The texture an .xbm in a bundle describes, from a cache that's open, the one beside the bundle or the game's. Null when none has it.</summary>
    internal async Task<TextureEntry?> FindTextureForHeaderAsync(EntryItem item)
    {
        var open = _roots.Select(r => r.Archive).OfType<TextureCacheArchive>().ToList();
        string owner = item.Entry.Archive.Owner;
        string? beside = Path.GetDirectoryName(item.Entry.Archive.FilePath) is { } folder ? Path.Combine(folder, "texture.cache") : null;
        string? game = GameLocator.Resolve(null) is { } g ? Path.Combine(g, "content", "content0", "texture.cache") : null;
        return await Task.Run(() =>
        {
            var caches = new List<TextureCacheArchive>(open.OrderByDescending(c => c.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase)));
            foreach (string? path in new[] { beside, game })
            {
                if (path is null || !File.Exists(path) || caches.Any(c => c.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                lock (SideCaches)
                {
                    if (!SideCaches.TryGetValue(path, out var cache))
                    {
                        try { cache = TextureCacheArchive.LooksLikeCache(path) ? TextureCacheArchive.Open(path) : null; }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { cache = null; }
                        SideCaches[path] = cache;
                    }
                    if (cache is not null) caches.Add(cache);
                }
            }
            return caches.Select(c => FindInCache(c, item.Path)).FirstOrDefault(e => e is not null);
        });
    }

    private void GridCopyPath_Click(object sender, RoutedEventArgs e) => CopyHighlightedPaths();

    private void GridPreview_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault(i => i.IsPreviewable || i.Entry is StringsEntry or TextureEntry || IsTextureHeader(i) || TextureWindow.CanShow(i.Name) || MeshWindow.CanShow(i.Name) || CutsceneWindow.CanShow(i.Name) || SceneWindow.CanShow(i.Name) || EntityWindow.CanShow(i.Name) || BehTreeWindow.CanShow(i.Name) || DataViewers.CanShow(i.Name) || PropertiesWindow.CanShow(i.Name)) is not { } item) return;
        if (item.Entry is StringsEntry strings) StringsWindow.Open(strings.FullPath, this);
        else if (MeshWindow.CanShow(item.Name)) _ = OpenMeshAsync(item);
        else if (CutsceneWindow.CanShow(item.Name)) _ = OpenCutsceneAsync(item);
        else if (SceneWindow.CanShow(item.Name)) _ = OpenSceneAsync(item);
        else if (EntityWindow.CanShow(item.Name)) _ = OpenEntityAsync(item);
        else if (BehTreeWindow.CanShow(item.Name)) _ = OpenBehTreeAsync(item);
        else if (DataViewers.CanShow(item.Name)) _ = OpenDataViewerAsync(item);
        else if (item.Entry is TextureEntry texture) OpenTexture(texture);
        else if (IsTextureHeader(item)) _ = OpenTextureHeaderAsync(item);
        else if (TextureWindow.CanShow(item.Name)) _ = OpenPictureAsync(item);
        else if (!item.IsPreviewable && PropertiesWindow.CanShow(item.Name)) _ = OpenPropertiesAsync(item);
        else _ = PreviewAsync(item);
    }

    private void GridXmlEditor_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Highlighted().Where(i => XmlWindow.CanOpen(i.Name)).Take(10))
            _ = OpenXmlAsync(item);
    }

    /// <summary>Any CR2W file's properties, in a window of its own, as REDkit shows every resource.</summary>
    private void GridProperties_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Highlighted().Where(i => PropertiesWindow.CanShow(i.Name)).Take(10))
            _ = OpenPropertiesAsync(item);
    }

    /// <summary>Shows a CR2W file's properties, with the files they name found the way the game would.</summary>
    private async Task OpenPropertiesAsync(EntryItem item)
    {
        if (item.Entry is LooseScriptEntry loose)
        {
            PropertiesWindow.OpenFile(loose.FullPath, LocatorFor(item.Entry), this);
            return;
        }
        try
        {
            var entry = item.Entry;
            byte[] data = await ReadAsync(entry);
            PropertiesWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this, entry.Archive);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
        }
    }

    /// <summary>JSON files in the JSON editor, and CR2W files (an .env, a .w2ent…) as their JSON.</summary>
    private void GridJson_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Highlighted().Where(i => JsonWindow.CanOpen(i.Name) || Cr2wJsonBatch.LooksLikeCr2w(i.Name)).Take(10))
            _ = OpenJsonAsync(item, asCr2w: !JsonWindow.CanOpen(item.Name));
    }

    /// <summary>Opens a JSON file, or a CR2W file as its JSON, from a bundle or the project.</summary>
    private async Task OpenJsonAsync(EntryItem item, bool asCr2w)
    {
        try
        {
            var entry = item.Entry;
            if (entry is LooseScriptEntry loose && !asCr2w)
            {
                JsonWindow.Open(loose.FullPath, this);
                return;
            }
            byte[] data = await Task.Run(() =>
            {
                if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
                using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return entry.Read(fs);
            });
            string source = $"{item.BundleName}  ·  {entry.Path}";
            string? disk = entry is LooseScriptEntry l ? l.FullPath : null;
            if (asCr2w) JsonWindow.OpenCr2w(item.Name, source, data, this, disk is null ? entry.Path : null, disk);
            else JsonWindow.OpenData(item.Name, source, data, this, entry.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotOpened2", item.Name, ex.Message));
        }
    }

    private void FileGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        UpdateFileMenu();
        GridExternalItem.IsEnabled = Highlighted().Any(i => i.IsPreviewable);
        GridXmlItem.Visibility = Highlighted().Any(i => XmlWindow.CanOpen(i.Name)) ? Visibility.Visible : Visibility.Collapsed;
        bool anyJson = Highlighted().Any(i => JsonWindow.CanOpen(i.Name)), anyCr2w = Highlighted().Any(i => Cr2wJsonBatch.LooksLikeCr2w(i.Name));
        GridJsonItem.Visibility = anyJson || anyCr2w ? Visibility.Visible : Visibility.Collapsed;
        GridPropertiesItem.Visibility = anyCr2w ? Visibility.Visible : Visibility.Collapsed;
        GridJsonItem.Header = anyJson && !anyCr2w ? L.T("MainWindow.OpenJsonEditor") : L.T("Common.OpenJson");
        var project = ProjectWorkspace.Current;
        var loose = Highlighted().Select(i => i.Entry).OfType<LooseScriptEntry>().ToList();
        // the game's files and installed mods' are copied into a project, not added to it: added, they'd still be read-only
        GridAddToProjectItem.Visibility = project is not null && loose.Any(l => !project.Contains(l.FullPath) && FileSafety.CanEdit(l.FullPath)) ? Visibility.Visible : Visibility.Collapsed;
        GridCopyToProjectItem.Visibility = Highlighted().Any(i => CopySource(i) is not null) ? Visibility.Visible : Visibility.Collapsed;
        GridMakeModItem.Visibility = Highlighted() is [var one] && MakeModSource(one) is not null ? Visibility.Visible : Visibility.Collapsed;
        GridRemoveFromProjectItem.Visibility = project is not null && loose.Any(l => project.Include.Any(s => project.Resolve(s).Equals(l.FullPath, StringComparison.OrdinalIgnoreCase)))
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateProjectFileMenu();
    }

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
                ShowError(L.T("MainWindow.CouldNotOpenedExternal", item.Name, ex.Message));
                return;
            }
        }
        if (copies > 0)
            SetStatus(copies == 1 ? L.T("MainWindow.OpenedReadOnlyCopy", copied)
                : L.T("MainWindow.OpenedReadOnlyCopies", copies));
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
            XmlWindow.OpenData(item.Name, $"{item.BundleName}  ·  {entry.Path}", data, this, entry.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotOpened2", item.Name, ex.Message));
        }
    }

    // ---------------------------------------------------------------- view and preview pane

    private bool ThumbsMode => _settings.FileView is "Small" or "Medium" or "Large";

    private bool FilesFocused => FileGrid.IsKeyboardFocusWithin || FileThumbs.IsKeyboardFocusWithin;

    /// <summary>Gives the list that's showing the files, and none to the other, so a hidden list isn't kept up to date.</summary>
    private void BindFiles()
    {
        if (ThumbsMode)
        {
            FileGrid.ItemsSource = null;
            FileThumbs.Show(_shown);
        }
        else
        {
            FileThumbs.Show([]);
            FileGrid.ItemsSource = _shown;
        }
    }

    private void ApplyView()
    {
        FileGrid.Visibility = ThumbsMode ? Visibility.Collapsed : Visibility.Visible;
        FileThumbs.Visibility = ThumbsMode ? Visibility.Visible : Visibility.Collapsed;
        FileThumbs.ThumbnailSize = FileThumbnails.SizeOf(_settings.FileView);
        BindFiles();
    }

    private void SetView(string view)
    {
        if (_settings.FileView == view) return;
        var selected = Highlighted();
        _settings.FileView = view;
        ApplyView();
        if (ThumbsMode) FileThumbs.Select(selected);
        else
            foreach (var item in selected) FileGrid.SelectedItems.Add(item);
        QueueStatusUpdate();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void FileThumbs_SelectionChanged()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void View_Click(object sender, RoutedEventArgs e) =>
        ShowViewMenu(ViewButton, _settings.FileView, _settings.ShowFilePreview, SetView, TogglePreviewPane, _settings.ListSubfolderFiles, ToggleSubfolders);

    /// <summary>
    /// The View menu, here and in the asset browser: the files as a list or as thumbnails, the preview pane on or off,
    /// and here whether a folder lists the files in its subfolders too.
    /// </summary>
    internal static void ShowViewMenu(UIElement button, string current, bool preview, Action<string> setView, Action togglePreview,
        bool subfolders = false, Action? toggleSubfolders = null)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Choice(string header, string view)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = current == view };
            item.Click += (_, _) => setView(view);
            menu.Items.Add(item);
        }
        Choice(L.T("MainWindow.Details"), "Details");
        Choice(L.T("MainWindow.SmallThumbnails"), "Small");
        Choice(L.T("MainWindow.MediumThumbnails"), "Medium");
        Choice(L.T("MainWindow.LargeThumbnails"), "Large");
        menu.Items.Add(new Separator());
        var pane = new MenuItem { Header = L.T("MainWindow.PreviewPane"), InputGestureText = L.T("MainWindow.AltP"), IsCheckable = true, IsChecked = preview };
        pane.Click += (_, _) => togglePreview();
        menu.Items.Add(pane);
        if (toggleSubfolders is not null)
        {
            var below = new MenuItem { Header = L.T("ProjectFiles.IncludeSubfolders"), IsCheckable = true, IsChecked = subfolders, ToolTip = L.T("ProjectFiles.SubfoldersTip") };
            below.Click += (_, _) => toggleSubfolders();
            menu.Items.Add(below);
        }
        menu.IsOpen = true;
    }

    private void TogglePreviewPane()
    {
        _settings.ShowFilePreview = !_settings.ShowFilePreview;
        ApplyPreviewPane();
    }

    private void ApplyPreviewPane()
    {
        bool show = _settings.ShowFilePreview;
        PreviewPane.Visibility = PreviewSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitterColumn.Width = new GridLength(show ? 8 : 0);
        PreviewColumn.MinWidth = show ? 240 : 0;
        PreviewColumn.Width = new GridLength(show ? Math.Max(240, _settings.FilePreviewWidth) : 0);
        if (show) _ = UpdatePreviewAsync();
        else PreviewPane.Stop();
    }

    private void PreviewSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _settings.FilePreviewWidth = PreviewColumn.ActualWidth;

    private void FileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbsMode) return;
        _previewTimer.Stop();
        if (_settings.ShowFilePreview) _previewTimer.Start();
    }

    /// <summary>Shows the file highlighted in the list in the preview pane, when it's on.</summary>
    private Task UpdatePreviewAsync()
    {
        if (!_settings.ShowFilePreview)
        {
            PreviewPane.Stop();
            return Task.CompletedTask;
        }
        return PreviewPane.ShowAsync(Highlighted());
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
        Add(L.T("MainWindow.AssetBrowser"), "Ctrl+Shift+A", () => _ = BrowseGameFilesAsync());
        menu.Items.Add(new Separator());
        Add(L.T("MainWindow.SearchGameStrings"), "Ctrl+Shift+G", OpenGameStrings);
        Add(L.T("MainWindow.SourceInspector"), "Ctrl+Shift+I", () => SourceInspectorWindow.Open(this));
        Add(L.T("MainWindow.LoadOrderAnalyser"), "Ctrl+Shift+L", () => LoadOrderWindow.Open(this));
        Add(L.T("MainWindow.ViewXMLOverrides"), "", () => XmlOverridesWindow.Open(this));
        Add(L.T("ItemEditor.MenuItem"), "", () => ItemEditorWindow.Open(this));
        Add(L.T("LootLocator.MenuItem"), "", () => LootLocatorWindow.Open(this));
        Add(L.T("Bestiary.MenuItem"), "", () => BestiaryWindow.Open(this));
        Add(L.T("ComparePick.MenuItem"), "", () => ComparePickWindow.Open(this));
        menu.Items.Add(new Separator());
        Add(L.T("MainWindow.ConsoleCommands"), "", () => ConsoleCommandsWindow.Open(this));
        Add(L.T("MainWindow.QuestFacts"), "", () => FactsWindow.Open(this));
        Add(L.T("MainWindow.ScriptsLog"), "", () => LogWindow.Open(this));
        Add(L.T("MainWindow.KeyBindingHelper"), "", () => InputBindingsWindow.Open(this));
        Add(L.T("MainWindow.HashesIDs"), "", () => HashToolsWindow.Open(this));
        Add(L.T("MainWindow.CR2WJson"), "", () => Cr2wJsonWindow.Open(this));
        Add(L.T("SoundUpgrade.MenuItem"), "", () => SoundUpgradeWindow.Open(this));
        Add(L.T("SpeechWindow.MenuItem"), "", () => SpeechWindow.Open(this));
        menu.IsOpen = true;
    }

    /// <summary>The archives open in the main window, for script lookups.</summary>
    /// <summary>The archives open in the tree; the asset browser's game files count as the bundles and caches they're from.</summary>
    public IEnumerable<IArchive> OpenArchives() => _roots.SelectMany(r => r.Archive is GameFilesArchive game ? game.Parts : [r.Archive])
        // and those of files another window is acting on, so a mesh copied from the asset browser brings its buffers
        .Concat(_actOn?.Select(i => i.Entry.Archive) ?? []).Distinct();

    /// <summary>Opens the game's own scripts (content\content0\scripts) in the file list, to read, with no project needed.</summary>
    /// <summary>The asset browser, a tool window of its own: every file the game has (content0, DLC and installed mods).</summary>
    private Task BrowseGameFilesAsync()
    {
        AssetBrowserWindow.Open(this);
        return Task.CompletedTask;
    }

    private async Task BrowseAsync(string[] paths, string? select)
    {
        bool open = select is not null && _roots.Any(r => r.Archive.FilePath.Equals(select, StringComparison.OrdinalIgnoreCase));
        if (!open)
        {
            await LoadPathsAsync(paths, asFolders: select is not null);
            Activate();
        }
        if (select is not null && _roots.FirstOrDefault(r => r.Archive.FilePath.Equals(select, StringComparison.OrdinalIgnoreCase)) is { } node)
        {
            node.IsExpanded = true;
            node.IsSelected = true;
        }
    }

    private void OpenGameStrings()
    {
        // without a game folder it can still read REDkit's strings database, and says so when there's neither
        GameStringsWindow.Open(this, GameLocator.Resolve(null));
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
            ShowError(L.T("MainWindow.CouldNotRead", item.Name, ex.Message));
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

    private List<EntryItem> Highlighted() => _actOn is { } acting ? [.. acting]
        : ThumbsMode ? [.. _shown.Where(i => i.IsSelected)] : [.. FileGrid.SelectedItems.OfType<EntryItem>()];

    // the files another window (the asset browser) asked to act on, in place of those highlighted in the list
    private List<EntryItem>? _actOn;

    /// <summary>
    /// Does what the file list's menu does to some files from another window, such as the asset browser: preview, open,
    /// copy into the project, extract, copy their paths, show them in Explorer.
    /// </summary>
    public void Act(FileAction action, IReadOnlyList<EntryItem> items)
    {
        if (items.Count == 0) return;
        _actOn = [.. items];
        try
        {
            var none = new RoutedEventArgs();
            switch (action)
            {
                case FileAction.Open: OpenItem(items[0]); break;
                case FileAction.Preview: GridPreview_Click(this, none); break;
                case FileAction.Properties: GridProperties_Click(this, none); break;
                case FileAction.Json: GridJson_Click(this, none); break;
                case FileAction.Xml: GridXmlEditor_Click(this, none); break;
                case FileAction.External: GridExternal_Click(this, none); break;
                case FileAction.CopyToProject: GridCopyToProject_Click(this, none); break;
                case FileAction.MakeMod: GridMakeMod_Click(this, none); break;
                case FileAction.Compare: _ = CompareHighlightedAsync(); break;
                case FileAction.Extract: GridExtract_Click(this, none); break;
                case FileAction.CopyPath: CopyHighlightedPaths(); break;
                case FileAction.CopyName: GridCopyName_Click(this, none); break;
                case FileAction.CopyFolder: GridCopyFolder_Click(this, none); break;
                case FileAction.CopyLocation: GridCopyLocation_Click(this, none); break;
                case FileAction.Reveal: RevealHighlighted(); break;
            }
        }
        finally
        {
            _actOn = null;
        }
    }

    private void QueueStatusUpdate()
    {
        if (_statusQueued) return;
        _statusQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _statusQueued = false;
            CheckedText.Text = _checkedCount > 0 ? L.T("MainWindow.Checked", _checkedCount, Format.Bytes(_checkedBytes)) : "";
            bool canExtract = _checkedCount > 0 && _extractCts is null;
            ExtractCheckedButton.IsEnabled = canExtract;
            // the Fluent accent style is near invisible when disabled, so only use it while enabled.
            // ClearValue (not Style = null) falls back to the theme's normal button style.
            if (canExtract)
                ExtractCheckedButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
            else
                ExtractCheckedButton.ClearValue(StyleProperty);
            ExtractCheckedText.Text = _checkedCount > 0 ? L.T("MainWindow.ExtractChecked2", _checkedCount) : L.T("MainWindow.ExtractChecked");

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
            Title = L.T("MainWindow.ChooseWhereExtractFiles"),
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
        bool asPng = PngCheck.IsChecked == true;
        bool asJson = JsonCheck.IsChecked == true;
        ExtractConverter? convert = asPng || asJson
            ? (entry, data) => (asPng ? EntryImages.ToPng(entry, data) : null) ?? (asJson ? Cr2wJsonBatch.ExtractAsJson(entry, data) : null)
            : null;
        try
        {
            output = Path.GetFullPath(output);
            // extracting into the game's folders would put files where the game reads them, or over its own
            FileSafety.CheckFolder(output);
            Directory.CreateDirectory(output);
        }
        catch (Exception ex)
        {
            ShowError(L.T("MainWindow.OutputFolderCantUsed", ex.Message));
            return;
        }

        var entries = items.Select(i => i.Entry).ToList();
        int companions = 0;
        var open = OpenArchives().ToList();
        if (CompanionsCheck.IsChecked == true) entries = await Task.Run(() => Companions.With(entries, open, out companions));

        _extractCts = new CancellationTokenSource();
        SetBusy(true);
        ExtractProgressBar.Maximum = entries.Count;
        ExtractProgressBar.Value = 0;
        var progress = new Progress<ExtractProgress>(p =>
        {
            ExtractProgressBar.Value = p.Done;
            SetStatus(L.T("MainWindow.Extracting", p.Done, p.Total, p.CurrentPath));
        });

        var token = _extractCts.Token;
        ExtractResult result;
        try
        {
            result = await Task.Run(() => Extractor.Extract(entries, output, keepStructure, progress, token, convert));
        }
        finally
        {
            _extractCts.Dispose();
            _extractCts = null;
            SetBusy(false);
        }

        foreach (var (entry, path) in result.Written) _extracted[entry] = path;
        string summary = L.T("MainWindow.ExtractedTo", result.Cancelled ? L.T("MainWindow.CancelledAfterExtracting") : L.T("MainWindow.Extracted"), L.T("Count.File", result.Extracted), Format.Bytes(result.Bytes), output)
            + (companions > 0 ? L.T("MainWindow.OthersNeed", L.T("Count.CompanionFile", companions)) : "");
        SetStatus(summary);

        if (result.Failures.Count > 0)
        {
            ShowError(L.T("MainWindow.NotExtracted", L.T("Count.File", result.Failures.Count), string.Join("\n", result.Failures.Take(15).Select(f => $"{f.Path}\n    {f.Error}"))) +
                      (result.Failures.Count > 15 ? L.T("MainWindow.AndMore", result.Failures.Count - 15) : ""));
        }

        if (openWhenDone && result.Extracted > 0)
        {
            // the files just written, selected, in their folder (the output folder when they're spread over many)
            var written = result.Written.Values.ToList();
            var folders = written.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (folders.Count == 1 && written.Count <= 200) ShellReveal.Show(written);
            else ShellReveal.Open(output);
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
        if (Keyboard.Modifiers == ModifierKeys.Alt && (e.Key == Key.System ? e.SystemKey : e.Key) == Key.P)
        {
            TogglePreviewPane();
            e.Handled = true;
        }
        else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
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
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.A)
        {
            _ = BrowseGameFilesAsync();
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
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.E)
        {
            PreviewWindow.OpenBench(this);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.B && ProjectWorkspace.Current is not null)
        {
            _ = BuildModAsync();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.O)
        {
            OpenProjectDialog();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N)
        {
            _ = NewProjectAsync();
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
        else if (FilesFocused && e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            var items = Highlighted();
            SetChecked(items, !items.All(i => i.IsChecked));
            e.Handled = true;
        }
        else if (FilesFocused && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            GridPreview_Click(sender, e);
            e.Handled = true;
        }
        else if (FilesFocused && ctrl && e.Key == Key.C)
        {
            // a project's files go on the clipboard as files, for pasting here or in Explorer, with their paths as text
            if (ProjectFiles(Highlighted()).Count > 0) ClipFiles(cut: false);
            else CopyHighlightedPaths();
            e.Handled = true;
        }
        else if (FilesFocused && ctrl && e.Key == Key.X)
        {
            ClipFiles(cut: true);
            e.Handled = true;
        }
        else if ((FilesFocused || FolderTree.IsKeyboardFocusWithin) && ctrl && e.Key == Key.V)
        {
            _ = PasteAsync();
            e.Handled = true;
        }
        else if (FilesFocused && e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            _ = DeleteHighlightedAsync();
            e.Handled = true;
        }
        else if (FilesFocused && e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _ = RenameHighlightedAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None && ProjectNode is not null && !NameBox.IsKeyboardFocusWithin)
        {
            _ = ReloadProjectAsync();
            e.Handled = true;
        }
        else if (FilesFocused && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
        {
            GridCopyName_Click(sender, e);
            e.Handled = true;
        }
        else if (FilesFocused && ctrl && e.Key == Key.D)
        {
            _ = CompareHighlightedAsync();
            e.Handled = true;
        }
        else if (FilesFocused && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.D)
        {
            ShowOtherCopies();
            e.Handled = true;
        }
        else if (FilesFocused && Keyboard.Modifiers == (ModifierKeys.Shift | ModifierKeys.Alt) && (e.Key == Key.System ? e.SystemKey : e.Key) == Key.R)
        {
            RevealHighlighted();
            e.Handled = true;
        }
        else if (SearchBox.IsKeyboardFocusWithin && e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => FitToolbar(e.NewSize.Width);

    /// <summary>
    /// A narrow window keeps the toolbar on one line: the buttons whose icons say enough drop their labels (their
    /// tooltips still name them), so the search box keeps its room.
    /// </summary>
    private void FitToolbar(double width)
    {
        var narrow = width < 1320 ? Visibility.Collapsed : Visibility.Visible;
        var narrower = width < 1080 ? Visibility.Collapsed : Visibility.Visible;
        SettingsLabel.Visibility = HelpLabel.Visibility = narrow;
        CloseAllLabel.Visibility = ToolsLabel.Visibility = ViewLabel.Visibility = narrower;
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
        AboutVersionText.Text = L.T("MainWindow.VersionFaen90", assembly.GetName().Version?.ToString(3));
        AboutCopyrightText.Text = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? L.T("MainWindow.CopyrightFaen90");
        AboutSettingsText.Text = L.T("MainWindow.SettingsSaved", Settings.FilePath);
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

    private void AboutPrivacy_Click(object sender, RoutedEventArgs e)
    {
        AboutPopup.IsOpen = false;
        SettingsWindow.ShowPrivacy(this);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(this);

    /// <summary>Opens Settings over a window (this one, or a tool window such as the asset browser); true when they were saved.</summary>
    internal bool OpenSettings(Window owner)
    {
        StoreExtractOptions();
        if (new SettingsWindow(_settings) { Owner = owner }.ShowDialog() != true) return false;
        OutputBox.Text = _settings.OutputFolder;
        KeepStructureCheck.IsChecked = _settings.KeepStructure;
        OpenWhenDoneCheck.IsChecked = _settings.OpenWhenDone;
        ApplySubfolders();
        return true;
    }

    private void StoreExtractOptions()
    {
        _settings.OutputFolder = OutputBox.Text.Trim();
        _settings.KeepStructure = KeepStructureCheck.IsChecked == true;
        _settings.OpenWhenDone = OpenWhenDoneCheck.IsChecked == true;
        _settings.ExtractCompanions = CompanionsCheck.IsChecked == true;
        _settings.ExtractTexturesAsPng = PngCheck.IsChecked == true;
        _settings.ExtractCr2wAsJson = JsonCheck.IsChecked == true;
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
        SaveWorkspaceState();
        _extractCts?.Cancel();
        StoreExtractOptions();
        _settings.Save();
    }

    private async Task ConfirmCloseAsync(List<PreviewDocument> unsaved)
    {
        var choice = await Message.Ask(unsaved.Count == 1 ? L.T("MainWindow.SaveChanges", unsaved[0].FileName) : L.T("MainWindow.SaveChangesScripts", unsaved.Count),
            L.T("MainWindow.ChangesLost", PreviewWindow.Names(unsaved), unsaved.Count),
            MessageKind.Question, unsaved.Count == 1 ? L.T("Common.Save") : L.T("MainWindow.SaveAll"), L.T("Common.DontSave"));
        if (choice == MessageChoice.Cancel || choice == MessageChoice.Primary && !PreviewWindow.SaveAll(unsaved)) return;
        _closeConfirmed = true;
        Close();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---------------------------------------------------------------- building and installing the mod

    private bool _building;

    /// <summary>Builds the open project's mod into its build folder, ready to install. True when it was built.</summary>
    public async Task<bool> BuildModAsync()
    {
        if (_building || ProjectWorkspace.Current is not { } project) return false;
        // the build uses what's on disk: unsaved files of the project's are asked about first
        var unsaved = PreviewWindow.UnsavedDocuments().Where(d => d.Item.DiskPath is { } p && project.Contains(p)).ToList();
        if (unsaved.Count > 0)
        {
            var choice = await Message.Ask(unsaved.Count == 1 ? L.T("MainWindow.SaveFirst", unsaved[0].FileName) : L.T("MainWindow.SaveFilesFirst", unsaved.Count),
                L.T("MainWindow.BuildUsesFilesThey"), MessageKind.Question, L.T("MainWindow.SaveBuild"), L.T("MainWindow.BuildWithoutThem"));
            if (choice == MessageChoice.Cancel || choice == MessageChoice.Primary && !PreviewWindow.SaveAll(unsaved)) return false;
        }
        // files the build would pack but the game wouldn't use: said before, not found out in the game
        var issues = await Task.Run(() => ModBuilder.CheckWorkspace(project));
        if (issues.Count > 0)
        {
            string list = string.Join("\n", issues.Take(10).Select(i => $"• {i.GamePath} {i.Problem}"))
                + (issues.Count > 10 ? L.T("MainWindow.More", issues.Count - 10) : "");
            if (!await Message.Show(issues.Count == 1 ? L.T("MainWindow.FileWorkspaceWontWork") : L.T("MainWindow.FilesWorkspaceWontWork", issues.Count),
                    L.T("MainWindow.RestModBuildsUsual", list),
                    MessageKind.Warning, primary: L.T("MainWindow.BuildAnyway"), secondary: L.T("Common.Cancel")))
                return false;
        }
        var compiler = PreviewWindow.CurrentCompiler();
        _building = true;
        try
        {
            var progress = new Progress<string>(text => SetStatus(L.T("MainWindow.Building", project.ModFolderName, text)));
            // the game's files, for the textures pictures in the workspace take the place of
            var game = new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null));
            var result = await ModBuilder.BuildAsync(project, compiler, game.ReadFile, progress, CancellationToken.None,
                path => Core.Packing.TextureCooker.GameLevels(game, path));
            if (!result.Success)
            {
                SetStatus(L.T("MainWindow.WasntBuilt", project.ModFolderName));
                if (result.Compiled is { Success: false } compiled && compiler is not null && compiled.Messages.Count > 0)
                {
                    // the errors are listed where each opens its line, as Compile scripts lists them
                    if (await Message.Show(L.T("MainWindow.WasntBuilt", project.ModFolderName), result.Failure ?? "", MessageKind.Warning, primary: L.T("MainWindow.ShowErrors"), secondary: L.T("Common.Close")))
                        await PreviewWindow.ShowCompileErrorsAsync(this, compiled, Path.Combine(result.ModFolder, "content", "precompiled.rsblob"), compiler);
                    return false;
                }
                bool ok = await Message.Show(L.T("MainWindow.WasntBuilt", project.ModFolderName), result.Failure ?? "", MessageKind.Warning,
                    primary: L.T("Common.OK"), secondary: result.Log.Length > 0 ? L.T("MainWindow.ShowLog") : null);
                if (!ok && result.Log.Length > 0)
                    PreviewWindow.ShowAlone(new PreviewItem("build.log", project.Name, System.Text.Encoding.UTF8.GetBytes(result.Log)), this);
                return false;
            }
            SetStatus(L.T("MainWindow.Built", project.ModFolderName, result.ModFolder));
            bool install = await Message.Show(L.T("MainWindow.Built2", project.ModFolderName),
                L.T("MainWindow.ReadyInstall", string.Join("\n", result.Made.Select(m => "• " + m)), result.ModFolder),
                MessageKind.Info, primary: L.T("MainWindow.Install"), secondary: L.T("MainWindow.Done"));
            if (install) await InstallModAsync();
            return true;
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>
    /// Copies the built mod into the game's mods folder, after saying where. Replacing a folder this project didn't
    /// install (a mod of the same name, or one put there by hand) is asked about on its own.
    /// </summary>
    public async Task InstallModAsync()
    {
        if (ProjectWorkspace.Current is not { } project) return;
        if (GameLocator.Resolve(null) is not { } game)
        {
            ShowError(L.T("MainWindow.GameFolderIsntSet"));
            return;
        }
        if (!Directory.Exists(Path.Combine(project.BuiltModPath, "content")))
        {
            if (await Message.Show(L.T("MainWindow.TheresNoBuildInstall"), L.T("MainWindow.BuildFirstThenInstall", project.ModFolderName),
                    MessageKind.Info, primary: L.T("MainWindow.Build"), secondary: L.T("Common.Cancel")))
                await BuildModAsync();
            return;
        }
        string target = ModBuilder.InstallPath(project, game);
        // the project's own DLC goes in the game's dlc folder beside it
        if (Directory.Exists(Path.Combine(project.BuiltDlcPath, "content"))) target += "\n" + ModBuilder.InstallDlcPath(project, game);
        bool go = ModBuilder.Installed(project, game) switch
        {
            InstalledState.Someone => await Message.Show(L.T("MainWindow.ReplaceThatsInstalled", project.ModFolderName),
                L.T("MainWindow.ThereAlreadyWasntInstalled", target),
                MessageKind.Warning, primary: L.T("MainWindow.Replace"), secondary: L.T("Common.Cancel")),
            InstalledState.Ours => await Message.Show(L.T("MainWindow.InstallAgain", project.ModFolderName),
                L.T("MainWindow.BuildReplacesOneInstalled", target), MessageKind.Question, primary: L.T("MainWindow.Install2"), secondary: L.T("Common.Cancel")),
            _ => await Message.Show(L.T("MainWindow.Install3", project.ModFolderName),
                L.T("MainWindow.BuildCopiedThatsOnly", target), MessageKind.Question, primary: L.T("MainWindow.Install2"), secondary: L.T("Common.Cancel")),
        };
        if (!go) return;
        try
        {
            string installed = await ModBuilder.InstallAsync(project, game);
            SetStatus(L.T("MainWindow.Installed", project.ModFolderName, installed));
            await ShowMessage(L.T("MainWindow.Installed2", project.ModFolderName), L.T("MainWindow.StartGameTryBuild", installed), MessageKind.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.CouldNotInstalled", project.ModFolderName, ex.Message));
        }
    }

    /// <summary>A warning; its first paragraph, when there's more than one, is its title.</summary>
    private void ShowError(string message)
    {
        var parts = message.Split("\n\n", 2);
        _ = parts.Length == 2
            ? ShowMessage(parts[0], parts[1], MessageKind.Warning)
            : ShowMessage(null, message, MessageKind.Warning);
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not T)
            child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        return child as T;
    }
}
