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

    public MainWindow(string[] args)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
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

        UpdateProjectUi();
        FileThumbs.ContextMenu = FileGrid.ContextMenu;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = UpdatePreviewAsync(); };
        ApplyView();
        ApplyPreviewPane();
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
            Filter = "Projects, bundles, scripts, strings and XML (*.w3bench;*.bundle;*.rsblob;*.cache;*.ws;*.w3strings;*.csv;*.xml)|*.w3bench;*.bundle;*.rsblob;*.cache;*.ws;*.w3strings;*.csv;*.xml|"
                   + "Projects (*.w3bench)|*.w3bench|Bundles (*.bundle)|*.bundle|Compiled scripts (*.rsblob)|*.rsblob|Texture caches (*.cache)|*.cache|Loose scripts (*.ws)|*.ws|"
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
        if ((strings.Count > 0 || xml.Count > 0) && !paths.Any()) return;

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

    private void ProjectAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectWorkspace.Current is not { } project) return;
        var dialog = new OpenFileDialog { Title = "Add files to the project", Multiselect = true, InitialDirectory = Directory.Exists(project.Root) ? project.Root : "" };
        if (dialog.ShowDialog(this) == true) _ = AddToProjectAsync(dialog.FileNames);
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

    private void GridAddToProject_Click(object sender, RoutedEventArgs e)
    {
        var files = Highlighted().Select(i => i.Entry).OfType<LooseScriptEntry>().Select(l => l.FullPath).ToList();
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
        else if (item.Entry is LooseScriptEntry { IsScript: false } && XmlWindow.CanOpen(item.Name))
            _ = OpenXmlAsync(item);
        else if (item.IsPreviewable)
            _ = PreviewAsync(item);
        else if (XmlWindow.CanOpen(item.Name))
            _ = OpenXmlAsync(item);
        else
            item.IsChecked = !item.IsChecked;
    }

    /// <summary>An .xbm in a bundle: the texture's description, whose pixels are in a texture.cache under the same path.</summary>
    private static bool IsTextureHeader(EntryItem item) =>
        item.Entry is not TextureEntry and not LooseScriptEntry && item.Name.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase);

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
            return caches.Select(c => c.Entries.OfType<TextureEntry>().FirstOrDefault(e => e.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(e => e is not null);
        });
    }

    private void GridCopyPath_Click(object sender, RoutedEventArgs e) => CopyHighlightedPaths();

    private void GridPreview_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault(i => i.IsPreviewable || i.Entry is StringsEntry or TextureEntry || IsTextureHeader(i) || TextureWindow.CanShow(i.Name)) is not { } item) return;
        if (item.Entry is StringsEntry strings) StringsWindow.Open(strings.FullPath, this);
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

    private void FileGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        GridExternalItem.IsEnabled = Highlighted().Any(i => i.IsPreviewable);
        GridXmlItem.Visibility = Highlighted().Any(i => XmlWindow.CanOpen(i.Name)) ? Visibility.Visible : Visibility.Collapsed;
        var project = ProjectWorkspace.Current;
        var loose = Highlighted().Select(i => i.Entry).OfType<LooseScriptEntry>().ToList();
        GridAddToProjectItem.Visibility = project is not null && loose.Any(l => !project.Contains(l.FullPath)) ? Visibility.Visible : Visibility.Collapsed;
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
            XmlWindow.OpenData(item.Name, $"{item.BundleName}  ·  {entry.Path}", data, this);
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
        var picture = await EntryImages.LoadAsync(item.Entry, 192, wanted: Wanted);
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

    private void ShowPreview(string glyph, string? message, EntryPicture? picture = null, string? text = null, string? name = null)
    {
        PreviewImageHost.Visibility = picture is not null ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Source = picture?.Image;
        PreviewText.Visibility = text is not null ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) SetPreviewText(text, name);
        else SetPreviewDocument(new TextDocument());
        PreviewEmpty.Visibility = picture is null && text is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewEmptyGlyph.Text = glyph;
        PreviewEmptyText.Text = message ?? "";
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
        string detail = $"{item.Entry.FormatText}  ·  {item.SizeText}  ·  {item.Folder}";
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
            else
                ShowPreview(item.Glyph, "No preview for this kind of file. Check it and extract it, or double-click for what the tool can do with it.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
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
        Add("Browse game scripts", "", () => _ = BrowseGameScriptsAsync());
        Add("Browse installed mods", "", () => _ = BrowseModsAsync());
        menu.Items.Add(new Separator());
        Add("Search game strings…", "Ctrl+Shift+G", OpenGameStrings);
        Add("Stub generator…", "Ctrl+Shift+W", OpenStubGenerator);
        Add("Source inspector…", "Ctrl+Shift+I", () => SourceInspectorWindow.Open(this));
        Add("Load order analyser…", "Ctrl+Shift+L", () => LoadOrderWindow.Open(this));
        Add("View XML overrides…", "", () => XmlOverridesWindow.Open(this));
        menu.Items.Add(new Separator());
        Add("New mod menu…", "", () => XmlWindow.OpenNewMenu(this));
        Add("Open an XML file…", "", OpenXmlFile);
        menu.IsOpen = true;
    }

    /// <summary>The archives open in the main window, for script lookups and for Find in files.</summary>
    public IEnumerable<IArchive> OpenArchives() => _roots.Select(r => r.Archive);

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
        if (GameLocator.Resolve(null) is { } folder) GameStringsWindow.Open(this, folder);
        else ShowError("The game folder isn't set.\n\nSearching the game's strings needs The Witcher 3's folder. Set it in Settings.");
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
