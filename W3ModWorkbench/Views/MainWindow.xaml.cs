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

namespace W3ModWorkbench;

public partial class MainWindow : Window, IMessageHost
{
    /// <summary>Every open archive, flat. The tree shows these grouped by mod: see <see cref="RebuildTree"/>.</summary>
    private readonly ObservableCollection<FolderNode> _roots = [];
    private readonly ObservableCollection<FolderNode> _tree = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _previewTimer;
    private CancellationTokenSource? _previewCts;
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
        AboutText.Text = $"v{assembly.GetName().Version?.ToString(3)}  ·  {copyright.Replace("Copyright ", "")}";

        OutputBox.Text = _settings.OutputFolder;
        KeepStructureCheck.IsChecked = _settings.KeepStructure;
        OpenWhenDoneCheck.IsChecked = _settings.OpenWhenDone;
        CompanionsCheck.IsChecked = _settings.ExtractCompanions;
        PngCheck.IsChecked = _settings.ExtractTexturesAsPng;
        JsonCheck.IsChecked = _settings.ExtractCr2wAsJson;

        UpdateProjectUi();
        FileThumbs.ContextMenu = FileGrid.ContextMenu;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = UpdatePreviewAsync(); };
        ApplyView();
        ApplyPreviewPane();
        UpdateRecentList();
        if (args.Length > 0)
            Loaded += async (_, _) => await LoadPathsAsync(args);
        else if (_settings.ReopenLastProject && File.Exists(_settings.LastProject))
            Loaded += async (_, _) => await OpenProjectAsync(_settings.LastProject, quiet: true);
    }

    // ---------------------------------------------------------------- loading

    private async void OpenBundles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open projects, bundles, scripts or strings",
            Filter = "Projects, bundles, scripts, strings, XML, meshes, entities, cutscenes, scenes and behaviour trees (*.w3bench;*.bundle;*.rsblob;*.cache;*.ws;*.w3strings;*.csv;*.xml;*.json;*.w2mesh;*.w2ent;*.w2cutscene;*.w2scene;*.w2behtree)|*.w3bench;*.bundle;*.rsblob;*.cache;*.ws;*.w3strings;*.csv;*.xml;*.json;*.w2mesh;*.w2ent;*.w2cutscene;*.w2scene;*.w2behtree|"
                   + "Projects (*.w3bench)|*.w3bench|Bundles (*.bundle)|*.bundle|Compiled scripts (*.rsblob)|*.rsblob|Texture caches (*.cache)|*.cache|Loose scripts (*.ws)|*.ws|"
                   + "Strings (*.w3strings;*.csv)|*.w3strings;*.csv|XML, opened in the XML editor (*.xml)|*.xml|JSON, opened in the JSON editor (*.json)|*.json|Meshes, opened in the mesh viewer (*.w2mesh)|*.w2mesh|Entities, opened in the entity viewer (*.w2ent)|*.w2ent|Cutscenes, opened in the cutscene viewer (*.w2cutscene)|*.w2cutscene|Scenes, opened in the scene viewer (*.w2scene)|*.w2scene|Behaviour trees, opened in the behaviour tree viewer (*.w2behtree)|*.w2behtree|All files (*.*)|*.*",
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
            Title = "Open every bundle, compiled script and script project in a folder",
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
        var clear = new MenuItem { Header = "Clear the list" };
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
            ShowError($"{path} isn't there any more.\n\nIt was moved or deleted, so it's been taken off the list.");
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
                    var found = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).ToList();
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
        : TextureCacheArchive.LooksLikeCache(file) ? TextureCacheArchive.Open(file)
        : Bundle.Open(file);

    // ---------------------------------------------------------------- projects

    /// <summary>The open project's files, as listed in the tree. Null when no project is open.</summary>
    public ScriptFolder? ProjectArchive => _roots.Select(r => r.Archive).OfType<ScriptFolder>().FirstOrDefault(a => a.Project is not null);

    private FolderNode? ProjectNode => _roots.FirstOrDefault(r => r.Archive is ScriptFolder { Project: not null });

    private void UpdateProjectUi()
    {
        var project = ProjectWorkspace.Current;
        Title = project is null ? "W3 Mod Workbench" : $"{project.Name} - W3 Mod Workbench";
        ProjectButtonText.Text = project is null ? "Project" : project.Name;
        ProjectButton.ToolTip = project is null ? "New project, open a project (Ctrl+Shift+O)"
            : $"{project.FilePath}\nProject properties, add files, recent projects";
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
            Title = "Open a project",
            Filter = "W3 Mod Workbench projects (*.w3bench)|*.w3bench",
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
            if (!quiet) ShowError($"{Path.GetFileName(file)} could not be opened.\n\n{ex.Message}");
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
        LoadingText.Text = $"Opening {project.Name}…";
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
            ShowError($"{project.Name} is inside the game's folders\n\n{protection}\n\nIts files open read-only there. Move the project's folder somewhere of your own, such as Documents, and open it from there to work on it.");
        SetStatus(Directory.Exists(project.ScriptsPath)
            ? $"Opened {project.Name}: {Format.Count(node.TotalFiles, "file")}"
            : $"Opened {project.Name}, but its scripts folder {project.ScriptsFolder} isn't there. Project properties can point it somewhere else.");
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
            var choice = await Message.Ask(unsaved.Count == 1 ? $"Save the changes to {unsaved[0].FileName}?" : $"Save the changes to {unsaved.Count} files?",
                PreviewWindow.Names(unsaved) + (unsaved.Count == 1 ? " has" : " have") + $" changes that are lost if you don't save them before {project.Name} closes.",
                MessageKind.Question, unsaved.Count == 1 ? "Save" : "Save all", "Don't save");
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
        bool selected = old.IsSelected;
        var node = await Task.Run(() => FolderNode.Build(ScriptFolder.OpenProject(project), OnEntryChecked));
        node.IsExpanded = true;
        int index = _roots.IndexOf(old);
        if (index < 0) return; // closed while it was being read
        _roots[index] = node;
        if (selected) node.IsSelected = true;
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
            ShowError("No project is open.\n\nMake or open a project first with Project > New project.");
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
            ShowError($"The project could not be saved.\n\n{ex.Message}");
            return false;
        }
        await ReloadProjectAsync();
        SetStatus($"Added {added:N0} {(added == 1 ? "file or folder" : "files and folders")} to {project.Name}"
            + (outside ? ". Scripts outside the project's scripts folder are part of the project but aren't compiled with it" : ""));
        return true;
    }

    /// <summary>A project for a folder of loose scripts: asks for its name, in the mod's folder above the scripts.</summary>
    public async Task CreateProjectFromScriptsAsync(string scriptsFolder)
    {
        scriptsFolder = Path.GetFullPath(scriptsFolder).TrimEnd('\\');
        string root = ScriptFolder.SuggestedRoot(scriptsFolder);
        if (WorkbenchProject.FileIn(root) is { } existing)
        {
            if (await Message.Ask("This folder is a project already", $"{Path.GetFileName(existing)} is in {root}. Open it, and its scripts are part of the project.",
                    MessageKind.Question, "Open project", "Close") == MessageChoice.Primary)
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
        Add("New project…", "Ctrl+Shift+N", () => _ = NewProjectAsync());
        Add("Open project…", "Ctrl+Shift+O", OpenProjectDialog);
        var recent = new MenuItem { Header = "Recent projects" };
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
        Add("Project properties…", "", () => ProjectProperties_Click(this, new RoutedEventArgs()), project is not null);
        Add("Add files to the project…", "", () => ProjectAddFiles_Click(this, new RoutedEventArgs()), project is not null);
        Add("Add a folder to the project…", "", AddFolderToProject, project is not null);
        Add("Refresh the project", "", () => _ = ReloadProjectAsync(), project is not null);
        Add("Open the project folder", "", () => ProjectReveal_Click(this, new RoutedEventArgs()), project is not null);
        menu.Items.Add(new Separator());
        Add("Compile scripts", "", () => _ = PreviewWindow.CompileProjectAsync(this, chooseOutput: false), project is not null)
            .ToolTip = "Compile the project's scripts to check them, listing any errors with links to their lines. Build compiles them into the mod as well";
        Add("Compile scripts to…", "", () => _ = PreviewWindow.CompileProjectAsync(this, chooseOutput: true), project is not null);
        Add(project is null ? "Build mod" : $"Build {project.ModFolderName.Replace("_", "__")}", "Ctrl+Shift+B", () => _ = BuildModAsync(), project is not null);
        Add(project is null ? "Install mod…" : $"Install {project.ModFolderName.Replace("_", "__")}…", "", () => _ = InstallModAsync(), project is not null);
        menu.Items.Add(new Separator());
        Add("Close project", "", () => _ = CloseProjectAsync(), project is not null);
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
        var dialog = new OpenFileDialog { Title = "Add files to the project", Multiselect = true, InitialDirectory = Directory.Exists(project.Root) ? project.Root : "" };
        if (dialog.ShowDialog(this) != true) return;

        // a script only compiles with the project from inside its scripts folder: one from anywhere else is copied in, when you agree
        string scripts = Path.GetFullPath(project.ScriptsPath).TrimEnd('\\') + "\\";
        var outside = dialog.FileNames.Where(f => f.EndsWith(".ws", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFullPath(f).StartsWith(scripts, StringComparison.OrdinalIgnoreCase)).ToList();
        var rest = dialog.FileNames.Except(outside).ToList();
        if (outside.Count > 0)
        {
            string which = outside.Count == 1 ? Path.GetFileName(outside[0]) : $"{outside.Count} scripts";
            var choice = await Message.Ask($"Copy {which} into the project's scripts folder?",
                $"Scripts are compiled with the project from its scripts folder ({project.ScriptsPath}). Copied there, {(outside.Count == 1 ? "it's" : "they're")} "
                + "part of the mod and compiled with it, and the original is left as it is. Added where "
                + (outside.Count == 1 ? "it is, it's" : "they are, they're") + " listed with the project but not compiled.",
                MessageKind.Question, "Copy into scripts", "Add where it is");
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
            ShowError($"The scripts could not be copied into the project.\n\n{ex.Message}");
            return false;
        }
        if (copied.Count > 0) await ReloadProjectAsync();
        if (skipped.Count > 0)
            await ShowMessage("Some scripts weren't copied", $"The scripts folder has a file with the same name already: {string.Join(", ", skipped)}. "
                + "Rename the file you're adding, or open the one in the project.", MessageKind.Warning);
        if (copied.Count > 0)
            SetStatus($"Copied {string.Join(", ", copied)} into {project.Name}'s scripts folder");
        return true;
    }

    private void AddFolderToProject()
    {
        if (ProjectWorkspace.Current is not { } project) return;
        var dialog = new OpenFolderDialog { Title = "Add a folder to the project", InitialDirectory = Directory.Exists(project.Root) ? project.Root : "" };
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

    /// <summary>The right-click menu on the tree shows what applies to the folder under it: the project's, loose scripts', or a bundle's.</summary>
    private void TreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        FolderNode? root = FolderTree.SelectedItem as FolderNode;
        while (root?.Parent is not null) root = root.Parent;
        bool project = root is { IsProject: true };
        bool loose = root is { IsLooseScripts: true };
        TreeCreateProjectItem.Visibility = loose ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in new[] { TreeProjectPropertiesItem, TreeAddFilesItem, TreeRefreshItem, TreeRevealItem })
            item.Visibility = project ? Visibility.Visible : Visibility.Collapsed;
        TreeProjectSeparator.Visibility = project || loose ? Visibility.Visible : Visibility.Collapsed;
        TreeCloseItem.Header = project ? "Close project" : loose ? "Close scripts" : root is { IsGroup: true } ? "Close mod" : "Close bundle";
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
    /// The open project, to put files in. With none open, asks to make one or open one: the game's files are changed only
    /// as copies in a project. Null when there's still none, or the project is somewhere files can't be written.
    /// </summary>
    public async Task<WorkbenchProject?> EnsureProjectAsync(string why, MessageCard? card = null)
    {
        if (ProjectWorkspace.Current is null)
        {
            var choice = await (card ?? Message).Ask("This needs a project", why + "\n\nMake a project for your mod, or open one.",
                MessageKind.Question, "New project…", "Open project…");
            if (choice == MessageChoice.Primary) await NewProjectAsync();
            else if (choice == MessageChoice.Secondary)
            {
                var dialog = new OpenFileDialog { Title = "Open a project", Filter = "W3 Mod Workbench projects (*.w3bench)|*.w3bench" };
                if (dialog.ShowDialog() == true) await OpenProjectAsync(dialog.FileName);
            }
        }
        if (ProjectWorkspace.Current is not { } project) return null;
        if (FileSafety.Protection(project.FilePath) is { } protection)
        {
            ShowError($"{project.Name} is inside the game's folders, where nothing is written.\n\n{protection}\n\nMove the project's folder somewhere of your own, such as Documents, and open it from there.");
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
        if (await EnsureProjectAsync("Game files are changed as copies in a project, at the same game path, so the game's own are never touched.") is not { } project)
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
            ? $"Copied {Format.Count(copied.Count, "file")} into {project.Name}" + (existing.Count > 0 ? $"; {Format.Count(existing.Count, "file")} it had already were left as they are" : "")
            : existing.Count > 0 ? $"{project.Name} has {(existing.Count == 1 ? "that file" : "those files")} already; nothing was copied" : "Nothing was copied");
        if (failed.Count > 0) ShowError("Some files could not be copied:\n\n" + string.Join("\n", failed.Take(15)));
        // REDkit's source files can be copied, but the game can't load them uncooked: said now, not at the first build
        var uncooked = copied.Where(p => project.GamePathOf(p) is not null && ModBuilder.IsUncooked(p)).ToList();
        if (uncooked.Count > 0)
            ShowError($"{(uncooked.Count == 1 ? "The copy isn't" : $"{uncooked.Count} of the copies aren't")} cooked\n\n"
                + string.Join("\n", uncooked.Take(10).Select(p => "• " + project.GamePathOf(p)))
                + "\n\nThey're REDkit source files, which the game can't load until they're cooked, and Build doesn't cook yet. The same files from the game's bundles are cooked already.");
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
        else if (XmlWindow.CanOpen(path)) XmlWindow.Open(path, this);
        else if (JsonWindow.CanOpen(path)) JsonWindow.Open(path, this);
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
            ShowError($"The project could not be saved.\n\n{ex.Message}");
            return;
        }
        _ = ReloadProjectAsync();
        SetStatus($"Removed {removed:N0} {(removed == 1 ? "file" : "files")} from {project.Name}. The files themselves are untouched");
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

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        RefreshView();
        UpdateBookmarkButton();
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
        List<EntryItem> scope = FolderTree.SelectedItem is FolderNode node
            ? [.. node.AllFiles()]
            : [.. _roots.SelectMany(r => r.AllFiles())];
        FillClassChoices(scope);

        IEnumerable<EntryItem> items = scope;
        if (_classFilter is { } resourceClass)
            items = items.Where(i => i.ResourceClass == resourceClass);
        var match = BuildMatcher(SearchBox.Text);
        if (match is not null)
            items = items.Where(match);

        _shown = [.. items];
        BindFiles();
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
        OpenItem(item);
    }

    /// <summary>What double-clicking a file does: opens it in the editor or viewer for its kind, or ticks it when there's none.</summary>
    private void OpenItem(EntryItem item)
    {
        if (item.Entry is StringsEntry strings)
            StringsWindow.Open(strings.FullPath, this);
        else if (item.Entry is TextureEntry texture)
            TextureWindow.Open(texture, this);
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
        else
            item.IsChecked = !item.IsChecked;
    }

    /// <summary>An .xbm, .texarray or .w2cube in a bundle: the texture's description, whose pixels are in a texture.cache under the same path.</summary>
    private static bool IsTextureHeader(EntryItem item) => item.IsTextureHeader;

    private static TextureEntry? FindInCache(TextureCacheArchive cache, string path) => ResourceLocator.FindInCache(cache, path);

    /// <summary>What finds a file's buffers, materials and textures: the archives open here, those beside it, and the game's.</summary>
    private ResourceLocator LocatorFor(ArchiveEntry entry) => new(OpenArchives(), entry.Archive, GameLocator.Resolve(null), ProjectFiles);

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
            ShowError($"{item.Name} could not be read.\n\n{ex.Message}");
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
            EntityWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"{item.Name} could not be read.\n\n{ex.Message}");
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
            BehTreeWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"{item.Name} could not be read.\n\n{ex.Message}");
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
            SceneWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"{item.Name} could not be read.\n\n{ex.Message}");
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
            CutsceneWindow.Open(entry.Path, item.BundleName, data, LocatorFor(entry), this);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"{item.Name} could not be read.\n\n{ex.Message}");
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
                ShowError($"{item.Name} can't be shown.\n\nIt isn't a picture this viewer understands. It may be a .dds in a format that isn't supported yet (cube maps, BC2, and some others).");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"{item.Name} could not be read.\n\n{ex.Message}");
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
        if (found is not null) TextureWindow.Open(found, this);
        else ShowError($"The texture's pixels weren't found.\n\n{item.Name} only describes the texture; its pixels are in a texture.cache under the same path. Open the mod's texture.cache (or the game's, in content\\content0), or put one beside this bundle.");
    }

    /// <summary>The texture an .xbm in a bundle describes, from a cache that's open, the one beside the bundle or the game's. Null when none has it.</summary>
    private async Task<TextureEntry?> FindTextureForHeaderAsync(EntryItem item)
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
        if (Highlighted().FirstOrDefault(i => i.IsPreviewable || i.Entry is StringsEntry or TextureEntry || IsTextureHeader(i) || TextureWindow.CanShow(i.Name) || MeshWindow.CanShow(i.Name) || CutsceneWindow.CanShow(i.Name) || SceneWindow.CanShow(i.Name) || EntityWindow.CanShow(i.Name) || BehTreeWindow.CanShow(i.Name)) is not { } item) return;
        if (item.Entry is StringsEntry strings) StringsWindow.Open(strings.FullPath, this);
        else if (MeshWindow.CanShow(item.Name)) _ = OpenMeshAsync(item);
        else if (CutsceneWindow.CanShow(item.Name)) _ = OpenCutsceneAsync(item);
        else if (SceneWindow.CanShow(item.Name)) _ = OpenSceneAsync(item);
        else if (EntityWindow.CanShow(item.Name)) _ = OpenEntityAsync(item);
        else if (BehTreeWindow.CanShow(item.Name)) _ = OpenBehTreeAsync(item);
        else if (item.Entry is TextureEntry texture) TextureWindow.Open(texture, this);
        else if (IsTextureHeader(item)) _ = OpenTextureHeaderAsync(item);
        else if (TextureWindow.CanShow(item.Name)) _ = OpenPictureAsync(item);
        else _ = PreviewAsync(item);
    }

    private void GridXmlEditor_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Highlighted().Where(i => XmlWindow.CanOpen(i.Name)).Take(10))
            _ = OpenXmlAsync(item);
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
            ShowError($"{item.Name} could not be opened.\n\n{ex.Message}");
        }
    }

    private void FileGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        UpdateFileMenu();
        GridExternalItem.IsEnabled = Highlighted().Any(i => i.IsPreviewable);
        GridXmlItem.Visibility = Highlighted().Any(i => XmlWindow.CanOpen(i.Name)) ? Visibility.Visible : Visibility.Collapsed;
        bool anyJson = Highlighted().Any(i => JsonWindow.CanOpen(i.Name)), anyCr2w = Highlighted().Any(i => Cr2wJsonBatch.LooksLikeCr2w(i.Name));
        GridJsonItem.Visibility = anyJson || anyCr2w ? Visibility.Visible : Visibility.Collapsed;
        GridJsonItem.Header = anyJson && !anyCr2w ? "Open in JSON editor" : "Open as JSON";
        var project = ProjectWorkspace.Current;
        var loose = Highlighted().Select(i => i.Entry).OfType<LooseScriptEntry>().ToList();
        // the game's files and installed mods' are copied into a project, not added to it: added, they'd still be read-only
        GridAddToProjectItem.Visibility = project is not null && loose.Any(l => !project.Contains(l.FullPath) && FileSafety.CanEdit(l.FullPath)) ? Visibility.Visible : Visibility.Collapsed;
        GridCopyToProjectItem.Visibility = Highlighted().Any(i => CopySource(i) is not null) ? Visibility.Visible : Visibility.Collapsed;
        var game = InAssetBrowser ? Highlighted() : [];
        GridBookmarkItem.Visibility = game.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GridBookmarkItem.Header = game.Count > 0 && game.All(i => IsBookmarked(i.Path)) ? "Remove bookmark" : "Bookmark";
        GridRemoveFromProjectItem.Visibility = project is not null && loose.Any(l => project.Include.Any(s => project.Resolve(s).Equals(l.FullPath, StringComparison.OrdinalIgnoreCase)))
            ? Visibility.Visible : Visibility.Collapsed;
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
            XmlWindow.OpenData(item.Name, $"{item.BundleName}  ·  {entry.Path}", data, this, entry.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"{item.Name} could not be opened.\n\n{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- view and preview pane

    private bool ThumbsMode => _settings.FileView is "Small" or "Medium" or "Large";

    private bool FilesFocused => FileGrid.IsKeyboardFocusWithin || FileThumbs.IsKeyboardFocusWithin;

    private int _perRow = 1;
    private EntryItem? _anchor;

    private int ThumbnailSize => _settings.FileView switch { "Small" => 64, "Large" => 160, _ => 96 };

    /// <summary>Gives the list that's showing the files, and none to the other, so a hidden list isn't kept up to date.</summary>
    private void BindFiles()
    {
        if (ThumbsMode)
        {
            FileGrid.ItemsSource = null;
            foreach (var item in _shown.Where(i => i.IsSelected && !ReferenceEquals(_anchor, i))) item.IsSelected = false;
            if (FileThumbs.IsVisible) RebuildRows();
        }
        else
        {
            FileGrid.ItemsSource = _shown;
        }
    }

    // Thumbnails are placed by hand: every cell is the same size, so where a file goes follows from its number, and only
    // the cells in view are made, from a pool. Nothing in the layout depends on what a cell holds, so it can't loop.
    private readonly Dictionary<int, ContentControl> _liveCells = [];
    private readonly Stack<ContentControl> _cellPool = new();

    private const double ThumbPadding = 8;

    private int CellWidth => ThumbnailSize + 22;

    private int CellHeight => ThumbnailSize + 60;

    private void RebuildRows()
    {
        foreach (var cell in _liveCells.Values) ReleaseCell(cell);
        _liveCells.Clear();
        FileThumbs.ScrollToVerticalOffset(0);
        LayoutThumbs();
    }

    private void ReleaseCell(ContentControl cell)
    {
        ThumbCanvas.Children.Remove(cell);
        cell.Content = null;
        _cellPool.Push(cell);
    }

    private ContentControl NewCell()
    {
        if (_cellPool.Count > 0) return _cellPool.Pop();
        var cell = new ContentControl { ContentTemplate = (DataTemplate)FindResource("ThumbCell"), Focusable = false };
        cell.MouseLeftButtonDown += Cell_MouseLeftButtonDown;
        cell.MouseRightButtonDown += Cell_MouseRightButtonDown;
        return cell;
    }

    /// <summary>Puts each file in view where it goes, makes the cells that are missing and gives back the ones that scrolled away.</summary>
    private void LayoutThumbs()
    {
        if (!ThumbsMode || !FileThumbs.IsVisible) return;
        double width = FileThumbs.ViewportWidth > 0 ? FileThumbs.ViewportWidth : Math.Max(0, FileThumbs.ActualWidth - 18);
        double height = FileThumbs.ViewportHeight > 0 ? FileThumbs.ViewportHeight : FileThumbs.ActualHeight;
        _perRow = Math.Max(1, (int)((width - 2 * ThumbPadding) / CellWidth));
        int count = _shown.Count;
        int rows = (count + _perRow - 1) / _perRow;
        ThumbCanvas.Width = _perRow * CellWidth + 2 * ThumbPadding;
        ThumbCanvas.Height = rows * CellHeight + 2 * ThumbPadding;

        double top = FileThumbs.VerticalOffset;
        int firstRow = Math.Max(0, (int)((top - ThumbPadding) / CellHeight) - 1);
        int lastRow = Math.Min(rows - 1, (int)((top + height) / CellHeight) + 1);
        int first = firstRow * _perRow, last = Math.Min(count - 1, (lastRow + 1) * _perRow - 1);
        if (count == 0 || last < first) { first = 0; last = -1; }

        foreach (var (index, cell) in _liveCells.Where(c => c.Key < first || c.Key > last).ToList())
        {
            _liveCells.Remove(index);
            ReleaseCell(cell);
        }
        for (int i = first; i <= last; i++)
        {
            if (!_liveCells.TryGetValue(i, out var cell))
            {
                cell = NewCell();
                cell.Tag = (double)ThumbnailSize;
                cell.Width = CellWidth;
                cell.Height = CellHeight;
                cell.Content = _shown[i];
                ThumbCanvas.Children.Add(cell);
                _liveCells[i] = cell;
                RequestThumbnail(_shown[i], cell);
            }
            Canvas.SetLeft(cell, ThumbPadding + i % _perRow * CellWidth);
            Canvas.SetTop(cell, ThumbPadding + i / _perRow * CellHeight);
        }
    }

    private void FileThumbs_ScrollChanged(object sender, ScrollChangedEventArgs e) => LayoutThumbs();

    private void FileThumbs_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutThumbs();

    private void ApplyView()
    {
        FileGrid.Visibility = ThumbsMode ? Visibility.Collapsed : Visibility.Visible;
        FileThumbs.Visibility = ThumbsMode ? Visibility.Visible : Visibility.Collapsed;
        FileThumbs.Tag = (double)ThumbnailSize;
        BindFiles();
        if (ThumbsMode) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RebuildRows);
    }

    private void SetView(string view)
    {
        if (_settings.FileView == view) return;
        var selected = Highlighted();
        _settings.FileView = view;
        ApplyView();
        if (ThumbsMode)
        {
            foreach (var item in _shown) item.IsSelected = false;
            foreach (var item in selected) item.IsSelected = true;
        }
        else
            foreach (var item in selected) FileGrid.SelectedItems.Add(item);
        QueueStatusUpdate();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    // ---------------------------------------------------------------- thumbnail selection

    /// <summary>Highlights the files for thumbnails, which keep their own selection: just these, or these added to it.</summary>
    private void SelectThumbs(IEnumerable<EntryItem> items, bool add = false)
    {
        var set = items.ToHashSet();
        foreach (var item in _shown)
            if (set.Contains(item)) item.IsSelected = true;
            else if (!add && item.IsSelected) item.IsSelected = false;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Cell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ContentControl)?.Content is not EntryItem item || e.OriginalSource is CheckBox || FindParent<CheckBox>(e.OriginalSource as DependencyObject) is not null) return;
        FileThumbs.Focus();
        if (e.ClickCount == 2)
        {
            OpenItem(item);
            e.Handled = true;
            return;
        }
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            item.IsSelected = !item.IsSelected;
            _anchor = item;
            _previewTimer.Stop();
            _previewTimer.Start();
        }
        else if (mods == ModifierKeys.Shift && _anchor is not null)
        {
            int a = _shown.IndexOf(_anchor), b = _shown.IndexOf(item);
            if (a < 0) a = b;
            SelectThumbs(_shown.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1));
        }
        else
        {
            SelectThumbs([item]);
            _anchor = item;
        }
        e.Handled = true;
    }

    /// <summary>A right-click on a file that isn't highlighted highlights just it, so the menu acts on what was clicked.</summary>
    private void Cell_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ContentControl)?.Content is not EntryItem item) return;
        FileThumbs.Focus();
        if (!item.IsSelected)
        {
            SelectThumbs([item]);
            _anchor = item;
        }
    }

    /// <summary>The keys of the thumbnail view: arrows move by a file or a row, Shift extends, Ctrl+A selects all.</summary>
    private void FileThumbs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_shown.Count == 0) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SelectThumbs(_shown);
            e.Handled = true;
            return;
        }
        int step = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -_perRow, Key.Down => _perRow, _ => 0 };
        if (step == 0 && e.Key is not (Key.Home or Key.End)) return;
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        int current = _anchor is null ? -1 : _shown.IndexOf(_anchor);
        int target = e.Key == Key.Home ? 0 : e.Key == Key.End ? _shown.Count - 1 : Math.Clamp(current < 0 ? 0 : current + step, 0, _shown.Count - 1);
        if (Keyboard.Modifiers == ModifierKeys.Shift && current >= 0)
            SelectThumbs(_shown.Skip(Math.Min(current, target)).Take(Math.Abs(current - target) + 1));
        else
        {
            SelectThumbs([_shown[target]]);
            _anchor = _shown[target];
        }
        if (Keyboard.Modifiers == ModifierKeys.None) _anchor = _shown[target];
        double rowTop = ThumbPadding + target / _perRow * CellHeight;
        if (rowTop < FileThumbs.VerticalOffset) FileThumbs.ScrollToVerticalOffset(rowTop - ThumbPadding);
        else if (rowTop + CellHeight > FileThumbs.VerticalOffset + FileThumbs.ViewportHeight) FileThumbs.ScrollToVerticalOffset(rowTop + CellHeight - FileThumbs.ViewportHeight + ThumbPadding);
        e.Handled = true;
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ViewButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Choice(string header, string view)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = _settings.FileView == view };
            item.Click += (_, _) => SetView(view);
            menu.Items.Add(item);
        }
        Choice("Details", "Details");
        Choice("Small thumbnails", "Small");
        Choice("Medium thumbnails", "Medium");
        Choice("Large thumbnails", "Large");
        menu.Items.Add(new Separator());
        var pane = new MenuItem { Header = "Preview pane", InputGestureText = "Alt+P", IsCheckable = true, IsChecked = _settings.ShowFilePreview };
        pane.Click += (_, _) => TogglePreviewPane();
        menu.Items.Add(pane);
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
        else _previewCts?.Cancel();
    }

    private void PreviewSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _settings.FilePreviewWidth = PreviewColumn.ActualWidth;

    private void FileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbsMode) return;
        _previewTimer.Stop();
        if (_settings.ShowFilePreview) _previewTimer.Start();
    }

    /// <summary>Makes a thumbnail for a picture file the first time its cell is built, and only while the cell is still showing it.</summary>
    private async void RequestThumbnail(EntryItem item, ContentControl cell)
    {
        if (item.ThumbnailRequested || !item.CanHaveThumbnail) return;
        item.ThumbnailRequested = true;
        bool Wanted() => ReferenceEquals(cell.Content, item);
        // an .xbm in a bundle shows the texture it describes, found in a cache under the same path
        ArchiveEntry? source = item.IsTextureHeader ? await FindTextureForHeaderAsync(item) : item.Entry;
        if (source is null) return;
        var picture = await EntryImages.LoadAsync(source, 192, wanted: Wanted);
        if (picture is not null) item.Thumbnail = picture.Image;
        else if (!Wanted()) item.ThumbnailRequested = false;
    }

    private const int MaxPreviewText = 200_000;

    /// <summary>Puts text in the preview editor, coloured as WitcherScript or XML when the file is one.</summary>
    private void SetPreviewText(string text, string? name)
    {
        var view = PreviewText.TextArea.TextView;
        var document = SetPreviewDocument(new TextDocument(text));
        bool dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;
        string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
        if (ext == ".ws") view.LineTransformers.Insert(0, new WitcherScriptColorizer(document, dark) { View = view });
        else if (ext is ".xml" or ".xsd" or ".config") view.LineTransformers.Insert(0, new XmlColorizer(document, dark) { View = view });
        PreviewText.ScrollToHome();
    }

    /// <summary>
    /// Swaps the preview editor's document, taking the colourers off with the old one: a colourer reads its own
    /// document, and left on it colours the new one's lines with the old one's offsets.
    /// </summary>
    private TextDocument SetPreviewDocument(TextDocument document)
    {
        var view = PreviewText.TextArea.TextView;
        foreach (var old in view.LineTransformers.Where(t => t is WitcherScriptColorizer or XmlColorizer).ToList()) view.LineTransformers.Remove(old);
        PreviewText.Document = document;
        return document;
    }

    private void ShowPreview(string glyph, string? message, EntryPicture? picture = null, string? text = null, string? name = null, MeshFile? mesh = null,
        CutsceneFile? cutscene = null, Core.Scenes.SceneFile? scene = null)
    {
        PreviewScene.Visibility = scene is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(PreviewScene.Scene, scene)) PreviewScene.Show(scene);
        PreviewCutscene.Visibility = cutscene is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(PreviewCutscene.Cutscene, cutscene)) PreviewCutscene.Show(cutscene);
        PreviewMeshHost.Visibility = mesh is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(PreviewMesh.Mesh, mesh)) PreviewMesh.Show(mesh);
        PreviewImageHost.Visibility = picture is not null ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Source = picture?.Image;
        PreviewText.Visibility = text is not null ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) SetPreviewText(text, name);
        else SetPreviewDocument(new TextDocument());
        PreviewEmpty.Visibility = picture is null && text is null && mesh is null && cutscene is null && scene is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewEmptyGlyph.Text = glyph;
        PreviewEmptyText.Text = message ?? "";
    }

    /// <summary>The preview's textures, smaller than the viewers', each read once for the file shown.</summary>
    private static Func<string, Task<System.Windows.Media.Imaging.BitmapSource?>> PreviewTexture(ResourceLocator locator)
    {
        var read = new Dictionary<string, Task<System.Windows.Media.Imaging.BitmapSource?>>(StringComparer.OrdinalIgnoreCase);
        return path =>
        {
            lock (read)
            {
                if (!read.TryGetValue(path, out var task)) read[path] = task = Task.Run(() => MeshTextures.Load(locator, path, 512));
                return task;
            }
        };
    }

    /// <summary>Shows the file highlighted in the list, when there's just one: a picture for textures and picture files, text for text files, and what it is for the rest.</summary>
    private async Task UpdatePreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts = null;
        if (!_settings.ShowFilePreview) return;
        var items = Highlighted();
        if (items.Count != 1)
        {
            PreviewName.Text = items.Count > 1 ? $"{items.Count:N0} files selected" : "";
            PreviewDetail.Text = items.Count > 1 ? Format.Bytes(items.Sum(i => (long)i.Size)) + " in all" : "";
            ShowPreview("\uE8A5", items.Count == 0 ? "Highlight a file to see it here." : "Highlight a single file to see it here.");
            return;
        }
        var item = items[0];
        var cts = _previewCts = new CancellationTokenSource();
        var token = cts.Token;
        PreviewName.Text = item.Name;
        string detail = (item.Kind is { } fileKind ? fileKind.Name + "  ·  " : "") + $"{item.Entry.FormatText}  ·  {item.SizeText}  ·  {item.Folder}";
        PreviewDetail.Text = detail;
        try
        {
            if (item.Entry is TextureEntry || TextureWindow.CanShow(item.Name))
            {
                var picture = await EntryImages.LoadAsync(item.Entry, 1024, token);
                if (token.IsCancellationRequested) return;
                if (picture is null) ShowPreview(item.Glyph, "This picture can't be shown yet.");
                else
                {
                    ShowPreview(item.Glyph, null, picture);
                    PreviewDetail.Text = detail + "  ·  " + picture.Info;
                }
            }
            else if (IsTextureHeader(item))
            {
                ShowPreview(item.Glyph, "Looking for the texture's pixels…");
                var texture = await FindTextureForHeaderAsync(item);
                if (token.IsCancellationRequested) return;
                var picture = texture is null ? null : await Task.Run(() => EntryImages.FromTexture(texture, 1024));
                if (token.IsCancellationRequested) return;
                if (picture is null) ShowPreview(item.Glyph, "Its pixels are in a texture.cache under the same path. Open the mod's texture.cache, or the game's, and they show here.");
                else
                {
                    ShowPreview(item.Glyph, null, picture);
                    PreviewDetail.Text = detail + "  ·  " + picture.Info;
                }
            }
            else if (MeshWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, "Reading the mesh…");
                var entry = item.Entry;
                var locator = LocatorFor(entry);
                var mesh = await Task.Run(() =>
                {
                    byte[] data;
                    if (!entry.Archive.ReadsFromDisk) data = entry.Read(null);
                    else
                    {
                        using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        data = entry.Read(fs);
                    }
                    return MeshFile.Read(data, n => locator.ReadBuffer(entry.Path, n, entry.Archive));
                }, token);
                if (token.IsCancellationRequested) return;
                ShowPreview(item.Glyph, null, mesh: mesh);
                int triangles = MeshView.Drawn(mesh, 0).Sum(c => c.Triangles);
                PreviewDetail.Text = detail + $"  ·  {triangles:N0} triangles, {mesh.LodCount} LOD{(mesh.LodCount == 1 ? "" : "s")}  ·  double-click to open";
                // the colour textures, smaller than the viewer's, each put on as soon as it's read
                for (int i = 0; i < mesh.Materials.Count; i++)
                {
                    int material = i;
                    var (picture, leaveOut) = await Task.Run(() => LookTextures.ForMaterial(mesh, material, locator, PreviewTexture(locator)), token);
                    if (token.IsCancellationRequested || !ReferenceEquals(PreviewMesh.Mesh, mesh)) return;
                    if (leaveOut) PreviewMesh.LeaveOut(0, material);
                    else PreviewMesh.SetTexture(material, picture);
                }
            }
            else if (CutsceneWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, "Reading the cutscene…");
                var entry = item.Entry;
                CutsceneFile cutscene;
                try
                {
                    byte[] data = await ReadAsync(entry);
                    cutscene = await Task.Run(() => CutsceneFile.Read(data), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, $"This cutscene could not be read.\n\n{ex.Message}\n\nIt can still be checked and extracted.");
                    return;
                }
                if (token.IsCancellationRequested) return;
                ShowPreview(item.Glyph, null, cutscene: cutscene);
                PreviewDetail.Text = detail + "  ·  double-click to open";
            }
            else if (EntityWindow.CanShow(item.Name))
            {
                // its meshes put together, in its first appearance
                ShowPreview(item.Glyph, "Reading the entity…");
                var locator = LocatorFor(item.Entry);
                Core.Entities.EntityFile entity;
                List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts;
                List<Core.Entities.EntityComponent> drawnBy;
                try
                {
                    byte[] data = await ReadAsync(item.Entry);
                    entity = await Task.Run(() => Core.Entities.EntityFile.Read(data), token);
                    var look = await Task.Run(() => entity.Look(entity.Appearances.FirstOrDefault(), locator.ReadFile), token);
                    (parts, drawnBy, _, _) = await Task.Run(() => EntityWindow.ReadMeshes(look, locator, token), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, $"This entity could not be read.\n\n{ex.Message}\n\nIt can still be checked and extracted.");
                    return;
                }
                if (token.IsCancellationRequested) return;
                string what = $"{entity.EntityClass ?? "Entity"}  ·  {Format.Count(entity.Components.Count, "component")}"
                            + (entity.Appearances.Count > 0 ? $", {Format.Count(entity.Appearances.Count, "appearance")}" : "");
                if (parts.Count == 0)
                {
                    ShowPreview(item.Glyph, $"{what}\n\nIt has no meshes to show. Double-click to see its components.");
                    return;
                }
                ShowPreview(item.Glyph, null, mesh: parts[0].Mesh);
                PreviewMesh.ShowParts(parts);
                PreviewDetail.Text = detail + $"  ·  {what}, {Format.Count(parts.Count, "mesh", "meshes")}  ·  double-click to open";
                var texture = PreviewTexture(locator);
                for (int p = 0; p < parts.Count; p++)
                {
                    var coloring = LookTextures.Coloring(entity, entity.Appearances.FirstOrDefault()?.Name, drawnBy[p].Name);
                    for (int m = 0; m < parts[p].Mesh.Materials.Count; m++)
                    {
                        int part = p, material = m;
                        var mesh = parts[p].Mesh;
                        var (picture, leaveOut) = await Task.Run(() => LookTextures.ForMaterial(mesh, material, locator, texture, coloring), token);
                        if (token.IsCancellationRequested || !ReferenceEquals(PreviewMesh.Mesh, parts[0].Mesh)) return;
                        if (leaveOut) PreviewMesh.LeaveOut(part, material);
                        else PreviewMesh.SetTexture(part, material, picture);
                    }
                }
            }
            else if (SceneWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, "Reading the scene…");
                Core.Scenes.SceneFile scene;
                try
                {
                    byte[] data = await ReadAsync(item.Entry);
                    scene = await Task.Run(() => Core.Scenes.SceneFile.Read(data), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, $"This scene could not be read.\n\n{ex.Message}\n\nIt can still be checked and extracted.");
                    return;
                }
                if (token.IsCancellationRequested) return;
                // shown at once, then again with its lines in words once the game's strings are read
                ShowPreview(item.Glyph, null, scene: scene);
                PreviewDetail.Text = detail + "  ·  double-click to open";
                var (text, note) = await SceneWindow.LinesAsync();
                if (!token.IsCancellationRequested && ReferenceEquals(PreviewScene.Scene, scene)) PreviewScene.Show(scene, text ?? (_ => null), note);
            }
            else if (item.Entry is StringsEntry)
                ShowPreview(item.Glyph, "Double-click to open it in the strings editor.");
            else if (item.IsPreviewable)
            {
                if (item.Size > 4_000_000)
                {
                    ShowPreview(item.Glyph, "Too large to read here. Double-click to open it.");
                    return;
                }
                var entry = item.Entry;
                string text = await Task.Run(() =>
                {
                    byte[] data;
                    if (!entry.Archive.ReadsFromDisk) data = entry.Read(null);
                    else
                    {
                        using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        data = entry.Read(fs);
                    }
                    string decoded = TextDecoding.Decode(data);
                    return decoded.Length > MaxPreviewText ? decoded[..MaxPreviewText] + "\n…" : decoded;
                }, token);
                if (token.IsCancellationRequested) return;
                ShowPreview(item.Glyph, null, text: text, name: item.Name);
            }
            else if (item.Kind is { } kind)
                ShowPreview(item.Glyph, $"{kind.Name}\n\n{kind.Description}\n\nThere's no preview for this kind of file; check it and extract it.");
            else
                ShowPreview(item.Glyph, "No preview for this kind of file. Check it and extract it, or double-click for what the tool can do with it.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!token.IsCancellationRequested) ShowPreview(item.Glyph, "It could not be read: " + ex.Message);
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
        Add("Find in files…", "Ctrl+Shift+F", () => FindInFilesWindow.Open(this));
        Add("Asset Browser", "Ctrl+Shift+A", () => _ = BrowseGameFilesAsync());
        Add("Browse game scripts", "", () => _ = BrowseGameScriptsAsync());
        Add("Browse installed mods", "", () => _ = BrowseModsAsync());
        menu.Items.Add(new Separator());
        Add("Search game strings…", "Ctrl+Shift+G", OpenGameStrings);
        Add("Stub generator…", "Ctrl+Shift+W", OpenStubGenerator);
        Add("Source inspector…", "Ctrl+Shift+I", () => SourceInspectorWindow.Open(this));
        Add("Load order analyser…", "Ctrl+Shift+L", () => LoadOrderWindow.Open(this));
        Add("View XML overrides…", "", () => XmlOverridesWindow.Open(this));
        menu.Items.Add(new Separator());
        Add("Console commands…", "", () => ConsoleCommandsWindow.Open(this));
        Add("Quest facts…", "", () => FactsWindow.Open(this));
        Add("Scripts log…", "", () => LogWindow.Open(this));
        Add("Key binding helper…", "", () => InputBindingsWindow.Open(this));
        Add("Hashes and IDs…", "", () => HashToolsWindow.Open(this));
        Add("CR2W and JSON…", "", () => Cr2wJsonWindow.Open(this));
        menu.Items.Add(new Separator());
        Add("New mod menu…", "", () => XmlWindow.OpenNewMenu(this));
        Add("Open an XML file…", "", OpenXmlFile);
        Add("Open a JSON file…", "", OpenJsonFile);
        menu.IsOpen = true;
    }

    /// <summary>The archives open in the main window, for script lookups and for Find in files.</summary>
    /// <summary>The archives open in the tree; the asset browser's game files count as the bundles and caches they're from.</summary>
    public IEnumerable<IArchive> OpenArchives() => _roots.SelectMany(r => r.Archive is GameFilesArchive game ? game.Parts : [r.Archive]);

    /// <summary>Opens the game's own scripts (content\content0\scripts) in the file list, to read, with no project needed.</summary>
    private async Task BrowseGameScriptsAsync()
    {
        string? game = GameLocator.Resolve(null);
        string? scripts = game is null ? null : Path.Combine(game, "content", "content0", "scripts");
        if (scripts is null || !Directory.Exists(scripts))
        {
            ShowError("The game folder isn't set.\n\nBrowsing the game's scripts needs The Witcher 3's folder. Set it in Settings.");
            return;
        }
        await BrowseAsync([scripts], scripts);
    }

    private void AssetBrowser_Click(object sender, RoutedEventArgs e) => _ = BrowseGameFilesAsync();

    /// <summary>
    /// The asset browser: every file the game has (content0, DLC and installed mods) as one tree, each path from where
    /// the game loads it, to search, preview, open and copy into the project like any open archive's. It's read once and
    /// kept at the top of the tree; asking again goes back to it.
    /// </summary>
    private async Task BrowseGameFilesAsync()
    {
        if (_roots.FirstOrDefault(r => r.Archive is GameFilesArchive) is { } open)
        {
            open.IsExpanded = true;
            open.IsSelected = true;
            SearchBox.Focus();
            return;
        }
        string? game = GameLocator.Resolve(null);
        if (game is null || !Directory.Exists(Path.Combine(game, "content")))
        {
            ShowError("The game folder isn't set.\n\nThe asset browser lists the files of The Witcher 3's content, DLC and mods. Set the game's folder in Settings.");
            return;
        }
        LoadingOverlay.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        BrowserPanel.Visibility = Visibility.Collapsed;
        LoadingProgress.IsIndeterminate = true;
        LoadingText.Text = "Reading the game's bundles…";
        FolderNode node;
        try
        {
            node = await Task.Run(() => FolderNode.Build(GameFilesArchive.Open(game,
                name => Dispatcher.BeginInvoke(() => LoadingText.Text = $"Reading {name}…")), OnEntryChecked));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The game's files could not be read.\n\n{ex.Message}");
            return;
        }
        finally
        {
            LoadingProgress.IsIndeterminate = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            UpdateEmptyState();
        }
        // below the project, which stays at the top
        _roots.Insert(ProjectNode is null ? 0 : 1, node);
        UpdateEmptyState();
        node.IsSelected = true;
        SearchBox.Focus();
        SetStatus($"Asset browser: {Format.Count(node.TotalFiles, "file")} from {node.Archive.Name}");
    }

    /// <summary>Opens everything in the game's mods folder, as Open folder does: loose scripts, compiled scripts, bundles and strings.</summary>
    private async Task BrowseModsAsync()
    {
        string? game = GameLocator.Resolve(null);
        string? mods = game is null ? null : Path.Combine(game, "mods");
        if (mods is null || !Directory.Exists(mods))
        {
            ShowError("The mods folder wasn't found.\n\nBrowsing the installed mods needs The Witcher 3's folder, and a mods folder in it. Set the game folder in Settings.");
            return;
        }
        await BrowseAsync([mods], null);
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

    private void OpenStubGenerator() => StubWindow.Open(this, GameLocator.Resolve(null));

    private void FindInFiles_Click(object sender, RoutedEventArgs e) => FindInFilesWindow.Open(this);

    private void BrowseGame_Click(object sender, RoutedEventArgs e) => _ = BrowseGameScriptsAsync();

    private void BrowseMods_Click(object sender, RoutedEventArgs e) => _ = BrowseModsAsync();

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

    private void OpenJsonFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a JSON file",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.LastOpenFolder = Path.GetDirectoryName(dialog.FileNames[0]) ?? "";
        foreach (string path in dialog.FileNames) JsonWindow.Open(path, this);
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

    private List<EntryItem> Highlighted() => ThumbsMode ? [.. _shown.Where(i => i.IsSelected)] : [.. FileGrid.SelectedItems.OfType<EntryItem>()];

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
            ShowError($"The output folder can't be used.\n\n{ex.Message}");
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
            SetStatus($"Extracting {p.Done:N0} of {p.Total:N0}  ·  {p.CurrentPath}");
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
        string summary = $"{(result.Cancelled ? "Cancelled after extracting" : "Extracted")} {Format.Count(result.Extracted, "file")} ({Format.Bytes(result.Bytes)}) to {output}"
            + (companions > 0 ? $", with {Format.Count(companions, "companion file")} the others need" : "");
        SetStatus(summary);

        if (result.Failures.Count > 0)
        {
            ShowError($"{Format.Count(result.Failures.Count, "file")} could not be extracted:\n\n" +
                      string.Join("\n", result.Failures.Take(15).Select(f => $"{f.Path}\n    {f.Error}")) +
                      (result.Failures.Count > 15 ? $"\n…and {result.Failures.Count - 15} more" : ""));
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
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.F)
        {
            FindInFilesWindow.Open(this);
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
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.B && ProjectWorkspace.Current is not null)
        {
            _ = BuildModAsync();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.W)
        {
            OpenStubGenerator();
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
            CopyHighlightedPaths();
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
        var choice = await Message.Ask(unsaved.Count == 1 ? $"Save the changes to {unsaved[0].FileName}?" : $"Save the changes to {unsaved.Count} scripts?",
            PreviewWindow.Names(unsaved) + (unsaved.Count == 1 ? " has" : " have") + " changes that are lost if you don't save them.",
            MessageKind.Question, unsaved.Count == 1 ? "Save" : "Save all", "Don't save");
        if (choice == MessageChoice.Cancel || choice == MessageChoice.Primary && !PreviewWindow.SaveAll(unsaved)) return;
        _closeConfirmed = true;
        Close();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---------------------------------------------------------------- building and installing the mod

    private bool _building;

    /// <summary>wcc_lite, for packing the workspace: the one in Settings, or REDkit's where it's usually installed.</summary>
    private string? WccLite() =>
        ScriptCompiler.IsWccLite(_settings.WccLitePath) ? _settings.WccLitePath : ScriptCompiler.Find();

    /// <summary>Builds the open project's mod into its build folder, ready to install. True when it was built.</summary>
    public async Task<bool> BuildModAsync()
    {
        if (_building || ProjectWorkspace.Current is not { } project) return false;
        // the build uses what's on disk: unsaved files of the project's are asked about first
        var unsaved = PreviewWindow.UnsavedDocuments().Where(d => d.Item.DiskPath is { } p && project.Contains(p)).ToList();
        if (unsaved.Count > 0)
        {
            var choice = await Message.Ask(unsaved.Count == 1 ? $"Save {unsaved[0].FileName} first?" : $"Save {unsaved.Count} files first?",
                "The build uses the files as they are on disk.", MessageKind.Question, "Save and build", "Build without them");
            if (choice == MessageChoice.Cancel || choice == MessageChoice.Primary && !PreviewWindow.SaveAll(unsaved)) return false;
        }
        // files the build would pack but the game wouldn't use: said before, not found out in the game
        var issues = await Task.Run(() => ModBuilder.CheckWorkspace(project));
        if (issues.Count > 0)
        {
            string list = string.Join("\n", issues.Take(10).Select(i => $"• {i.GamePath} {i.Problem}"))
                + (issues.Count > 10 ? $"\n…and {issues.Count - 10} more" : "");
            if (!await Message.Show(issues.Count == 1 ? "A file in the workspace won't work in the game" : $"{issues.Count} files in the workspace won't work in the game",
                    list + "\n\nThe rest of the mod builds as usual. Files copied from the game's bundles with Copy to project are cooked already and work.",
                    MessageKind.Warning, primary: "Build anyway", secondary: "Cancel"))
                return false;
        }
        if (!await ChooseScriptsBuildAsync(project)) return false;
        var compiler = PreviewWindow.CurrentCompiler();
        _building = true;
        try
        {
            var progress = new Progress<string>(text => SetStatus($"Building {project.ModFolderName}: {text}"));
            var result = await ModBuilder.BuildAsync(project, WccLite(), compiler, progress, CancellationToken.None);
            if (!result.Success)
            {
                SetStatus($"{project.ModFolderName} wasn't built");
                if (result.Compiled is { Success: false } compiled && compiler is not null && compiled.Messages.Count > 0)
                {
                    // the errors are listed where each opens its line, as Compile scripts lists them
                    if (await Message.Show($"{project.ModFolderName} wasn't built", result.Failure ?? "", MessageKind.Warning, primary: "Show errors", secondary: "Close"))
                        await PreviewWindow.ShowCompileErrorsAsync(this, compiled, Path.Combine(result.ModFolder, "content", "precompiled.rsblob"), compiler);
                    return false;
                }
                bool ok = await Message.Show($"{project.ModFolderName} wasn't built", result.Failure ?? "", MessageKind.Warning,
                    primary: "OK", secondary: result.Log.Length > 0 ? "Show log" : null);
                if (!ok && result.Log.Length > 0)
                    PreviewWindow.ShowAlone(new PreviewItem("build.log", project.Name, System.Text.Encoding.UTF8.GetBytes(result.Log)), this);
                return false;
            }
            SetStatus($"Built {project.ModFolderName} in {result.ModFolder}");
            bool install = await Message.Show($"Built {project.ModFolderName}",
                string.Join("\n", result.Made.Select(m => "• " + m)) + $"\n\nin {result.ModFolder}, ready to install.",
                MessageKind.Info, primary: "Install…", secondary: "Done");
            if (install) await InstallModAsync();
            return true;
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>
    /// The first build of a project with scripts asks how they go in the mod: compiled into content\precompiled.rsblob,
    /// as REDkit publishes, and whether the loose scripts go too. The answers are kept in the project (Project properties
    /// changes them). False when it was cancelled.
    /// </summary>
    private async Task<bool> ChooseScriptsBuildAsync(WorkbenchProject project)
    {
        if (project.BuildCompiledScripts is not null && project.BuildLooseScripts is not null) return true;
        if (!Directory.Exists(project.ScriptsPath) || !Directory.EnumerateFiles(project.ScriptsPath, "*.ws", SearchOption.AllDirectories).Any()) return true;

        if (project.BuildCompiledScripts is null)
        {
            var compile = await Message.Ask("Compile the scripts into the mod?",
                "Compiled, the scripts go in the mod as content\\precompiled.rsblob, the way REDkit publishes a mod: they're checked as the mod is built, "
                + "and the game loads them as they are. Not compiled, they go in content\\scripts for the game to compile when it starts.\n\n"
                + "This is kept in the project: Project properties changes it.",
                MessageKind.Question, "Compile them", "Don't compile");
            if (compile == MessageChoice.Cancel) return false;
            project.BuildCompiledScripts = compile == MessageChoice.Primary;
        }
        if (project.BuildLooseScripts is null)
        {
            if (project.BuildCompiledScripts == true)
            {
                var loose = await Message.Ask("Ship the loose scripts too?",
                    "Beside the compiled scripts, the .ws files can go in content\\scripts as the mod's source, for others to read or merge. "
                    + "An info.json tells the game to run the compiled ones and leave the loose ones alone.",
                    MessageKind.Question, "Compiled only", "Compiled and loose");
                if (loose == MessageChoice.Cancel) return false;
                project.BuildLooseScripts = loose == MessageChoice.Secondary;
            }
            else project.BuildLooseScripts = true;
        }
        try
        {
            project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The project could not be saved\n\nThe build goes ahead, but the choice wasn't kept. {ex.Message}");
        }
        return true;
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
            ShowError("The game folder isn't set\n\nInstalling puts the mod in the game's mods folder. Set where the game is in Settings.");
            return;
        }
        if (!Directory.Exists(Path.Combine(project.BuiltModPath, "content")))
        {
            if (await Message.Show("There's no build to install yet", $"Build {project.ModFolderName} first, then install it.",
                    MessageKind.Info, primary: "Build", secondary: "Cancel"))
                await BuildModAsync();
            return;
        }
        string target = ModBuilder.InstallPath(project, game);
        bool go = ModBuilder.Installed(project, game) switch
        {
            InstalledState.Someone => await Message.Show($"Replace the {project.ModFolderName} that's installed?",
                $"{target} is there already, and wasn't installed from this project: it may be someone else's mod of the same name, or one copied in by hand. Installing deletes it and puts this build in its place.",
                MessageKind.Warning, primary: "Replace it", secondary: "Cancel"),
            InstalledState.Ours => await Message.Show($"Install {project.ModFolderName} again?",
                $"The build replaces the one installed before, in {target}.", MessageKind.Question, primary: "Install", secondary: "Cancel"),
            _ => await Message.Show($"Install {project.ModFolderName}?",
                $"The build is copied to {target}. That's the only place in the game's folder the tool writes.", MessageKind.Question, primary: "Install", secondary: "Cancel"),
        };
        if (!go) return;
        try
        {
            string installed = await ModBuilder.InstallAsync(project, game);
            SetStatus($"Installed {project.ModFolderName} in {installed}");
            await ShowMessage($"Installed {project.ModFolderName}", $"It's in {installed}. Start the game to try it; build and install again after changes.", MessageKind.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"{project.ModFolderName} could not be installed\n\n{ex.Message}");
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

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not T)
            child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        return child as T;
    }
}
