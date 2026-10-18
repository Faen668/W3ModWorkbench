using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using W3ModWorkbench.Core.Projects;

namespace W3ModWorkbench;

/// <summary>
/// Makes a new project (its name, its folder and its scripts folder), or edits the open one's properties, including the
/// other files that belong to it. Either way the result is a saved .w3bench file.
/// </summary>
public partial class ProjectWindow : Window
{
    private readonly WorkbenchProject? _existing;
    private readonly ObservableCollection<string> _files = [];
    private bool _scriptsTouched;
    private bool _rootTouched;
    private bool _modTouched;
    private bool _setting;

    /// <summary>The project made or changed, once OK is clicked.</summary>
    public WorkbenchProject? Result { get; private set; }

    /// <summary>Whether the properties changed what the project's files are (its scripts folder or its other files), so the file list needs reading again.</summary>
    public bool FilesChanged { get; private set; }

    /// <summary>A new project. The suggestions fill the fields, such as when it's made from a folder of loose scripts.</summary>
    public static ProjectWindow ForNew(Window owner, string? root = null, string? name = null, string? scripts = null) =>
        new(null, root, name, scripts) { Owner = owner };

    /// <summary>The properties of an existing project.</summary>
    public static ProjectWindow ForProperties(Window owner, WorkbenchProject project) => new(project, null, null, null) { Owner = owner };

    private ProjectWindow(WorkbenchProject? existing, string? root, string? name, string? scripts)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _existing = existing;
        FilesList.ItemsSource = _files;
        _setting = true;
        if (existing is null)
        {
            RootBox.Text = root ?? "";
            _rootTouched = root is not null;
            NameBox.Text = name ?? "";
            ModBox.Text = WorkbenchProject.ModFolderFor(NameBox.Text);
            ScriptsBox.Text = scripts ?? (root is not null ? WorkbenchProject.DetectScriptsFolder(root) : null) ?? "scripts";
            _scriptsTouched = scripts is not null;
            FilesPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "Project properties";
            HeadingText.Text = existing.Name;
            IntroText.Text = existing.FilePath;
            OkButton.Content = "Save";
            RootBox.Text = existing.Root;
            RootBox.IsReadOnly = true;
            RootBrowse.Visibility = Visibility.Collapsed;
            RootHint.Text = "Where the .w3bench file is. To move the project, move its folder and open the .w3bench file again.";
            NameBox.Text = existing.Name;
            ModBox.Text = existing.ModFolderName;
            _modTouched = true;
            ScriptsBox.Text = existing.ScriptsFolder;
            _scriptsTouched = true;
            OutputBox.Text = existing.CompileOutput ?? "";
            BuildCompiledCheck.IsChecked = existing.BuildCompiledScripts ?? true;
            BuildLooseCheck.IsChecked = existing.BuildLooseScripts ?? existing.BuildCompiledScripts == false;
            AuthorBox.Text = existing.Author;
            VersionBox.Text = existing.Version;
            DescriptionBox.Text = existing.Description;
            foreach (string file in existing.Include) _files.Add(file);
        }
        _setting = false;
        Validate();
        Loaded += (_, _) => (existing is null && NameBox.Text.Length == 0 ? NameBox : (IInputElement)OkButton).Focus();
    }

    private string RootText => RootBox.Text.Trim();

    private string? FullRoot()
    {
        try { return RootText.Length == 0 ? null : Path.GetFullPath(RootText); }
        catch (ArgumentException) { return null; }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (_setting) return;
        if (sender == ScriptsBox) _scriptsTouched = true;
        if (sender == RootBox) _rootTouched = true;
        if (sender == ModBox) _modTouched = true;
        if (sender == NameBox && _existing is null)
        {
            // a new project's folder and mod name follow its name until they're changed
            _setting = true;
            if (!_modTouched) ModBox.Text = WorkbenchProject.ModFolderFor(NameBox.Text);
            if (!_rootTouched) RootBox.Text = NameBox.Text.Trim().Length == 0 ? "" : Path.Combine(DefaultProjectsFolder, SafeFolderName(NameBox.Text));
            _setting = false;
        }
        if (sender == RootBox && !_scriptsTouched && _existing is null)
        {
            _setting = true;
            ScriptsBox.Text = FullRoot() is { } root ? WorkbenchProject.DetectScriptsFolder(root) ?? "scripts" : "scripts";
            _setting = false;
        }
        Validate();
    }

    /// <summary>Where new projects go unless you choose: Documents, in W3 Mod Workbench, beside your other work and outside the game.</summary>
    private static string DefaultProjectsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "W3 Mod Workbench");

    private static string SafeFolderName(string name) =>
        string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>What is wrong with the fields, or null. Shown under the buttons; OK waits for it to be null.</summary>
    private void Validate()
    {
        string? problem = null;
        string mod = WorkbenchProject.ModFolderFor(ModBox.Text.Trim().Length > 0 ? ModBox.Text : NameBox.Text);
        ModHint.Text = $"Build makes {mod}, ready to install in the game's mods folder.";
        if (NameBox.Text.Trim().Length == 0) problem = "Give the project a name.";
        else if (FullRoot() is not { } root) problem = "Choose the project's folder.";
        else if (_existing is null && FileSafety.Protection(Path.Combine(root, "_")) is not null)
            problem = "That folder is in the game's folders (or REDkit's), where nothing is written. Choose a folder of your own, such as Documents.";
        else if (!ScriptsInside(root, out _)) problem = "The scripts folder has to be inside the project folder.";
        else if (_existing is null && Directory.Exists(root) && WorkbenchProject.FileIn(root) is { } there)
            problem = $"{Path.GetFileName(there)} is in that folder already. Open it from Project > Open project.";
        ProblemText.Text = problem ?? "";
        OkButton.IsEnabled = problem is null;
    }

    private bool ScriptsInside(string root, out string stored)
    {
        stored = "";
        string text = ScriptsBox.Text.Trim();
        if (text.Length == 0) return false;
        try
        {
            string full = Path.GetFullPath(Path.Combine(root, text));
            string relative = Path.GetRelativePath(root, full);
            if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
            stored = relative.Replace('\\', '/');
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- browsing

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the mod's folder", InitialDirectory = Directory.Exists(RootText) ? RootText : "" };
        if (dialog.ShowDialog(this) != true) return;
        RootBox.Text = dialog.FolderName;
        if (NameBox.Text.Trim().Length == 0) NameBox.Text = Path.GetFileName(dialog.FolderName.TrimEnd('\\'));
    }

    private void BrowseScripts_Click(object sender, RoutedEventArgs e)
    {
        if (FullRoot() is not { } root) return;
        var dialog = new OpenFolderDialog { Title = "Choose the scripts folder", InitialDirectory = Directory.Exists(root) ? root : "" };
        if (dialog.ShowDialog(this) != true) return;
        string relative = Path.GetRelativePath(root, dialog.FolderName);
        ScriptsBox.Text = relative.Replace('\\', '/');
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        string start = FullRoot() is { } root ? root : "";
        var dialog = new SaveFileDialog
        {
            Title = "Where should the compiled scripts go?",
            FileName = "precompiled.rsblob",
            Filter = "Compiled scripts (*.rsblob)|*.rsblob",
            InitialDirectory = Directory.Exists(start) ? start : "",
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        OutputBox.Text = Stored(dialog.FileName);
    }

    /// <summary>A path the way the project file keeps it: relative to the project folder when it's inside it.</summary>
    private string Stored(string path)
    {
        if (FullRoot() is not { } root) return path;
        string relative = Path.GetRelativePath(root, path);
        return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFullPath(path) : relative).Replace('\\', '/');
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Add files to the project", Multiselect = true, InitialDirectory = Directory.Exists(RootText) ? RootText : "" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string file in dialog.FileNames) AddMember(file);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder to the project", InitialDirectory = Directory.Exists(RootText) ? RootText : "" };
        if (dialog.ShowDialog(this) != true) return;
        AddMember(dialog.FolderName);
    }

    private void AddMember(string path)
    {
        if (_existing is null || FullRoot() is null) return;
        // a file in the scripts folder, or one already listed, is part of the project already
        if (ScriptsInside(_existing.Root, out string scripts) && path.StartsWith(Path.GetFullPath(Path.Combine(_existing.Root, scripts)) + "\\", StringComparison.OrdinalIgnoreCase)) return;
        string stored = Stored(path);
        if (!_files.Contains(stored, StringComparer.OrdinalIgnoreCase)) _files.Add(stored);
    }

    private void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (string file in FilesList.SelectedItems.OfType<string>().ToList()) _files.Remove(file);
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RemoveFilesButton.IsEnabled = FilesList.SelectedItems.Count > 0;

    // ---------------------------------------------------------------- saving

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (FullRoot() is not { } root || !ScriptsInside(root, out string scripts)) return;
        try
        {
            var project = _existing;
            if (project is null)
            {
                project = WorkbenchProject.Create(root, NameBox.Text, scripts);
                // the workspace holds the game files the mod changes; it's part of the project from the start
                Directory.CreateDirectory(project.WorkspacePath);
                project.Add(project.WorkspacePath);
                // a folder that was compiled before projects had files keeps the output it was given
                if (OutputBox.Text.Trim().Length == 0 && App.Settings.CompileOutputs.TryGetValue(project.ScriptsPath, out string? before))
                    OutputBox.Text = Stored(before);
            }
            else
            {
                FilesChanged = !scripts.Equals(project.ScriptsFolder, StringComparison.OrdinalIgnoreCase)
                    || !_files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).SequenceEqual(project.Include.OrderBy(f => f, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                project.Name = NameBox.Text.Trim();
                project.ScriptsFolder = scripts;
                project.Include = [.. _files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
                Directory.CreateDirectory(project.ScriptsPath);
            }
            project.CompileOutput = OutputBox.Text.Trim().Length == 0 ? null : OutputBox.Text.Trim().Replace('\\', '/');
            project.BuildCompiledScripts = BuildCompiledCheck.IsChecked == true;
            project.BuildLooseScripts = BuildLooseCheck.IsChecked == true;
            project.Author = AuthorBox.Text.Trim();
            // kept only when it isn't what the name gives anyway
            string modName = WorkbenchProject.ModFolderFor(ModBox.Text.Trim().Length > 0 ? ModBox.Text : project.Name);
            project.ModName = modName == WorkbenchProject.ModFolderFor(project.Name) ? null : modName;
            project.Version = VersionBox.Text.Trim();
            project.Description = DescriptionBox.Text.Trim();
            project.Save();
            Result = project;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ProblemText.Text = ex.Message;
        }
    }
}
