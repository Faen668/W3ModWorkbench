using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Input;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Localisation;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// The project's own files in the file list, handled as Explorer handles them: cut, copy and paste (with Explorer too),
/// duplicate, rename and delete to the Recycle Bin. And whether a folder lists its own files or every file under it.
/// </summary>
public partial class MainWindow
{
    // the clipboard format Explorer marks files cut or copied with
    private const string DropEffectFormat = "Preferred DropEffect"; // loc-ignore

    // the files to highlight in the list once the project has been read again, after they were made or renamed
    private List<string>? _reloadHighlight;

    /// <summary>The files among <paramref name="items"/> that are the open project's own, on disk and ours to change.</summary>
    private static List<string> ProjectFiles(IEnumerable<EntryItem> items) =>
        [.. items.Select(i => i.Entry).OfType<LooseScriptEntry>()
            .Where(l => l.Archive is ScriptFolder { Project: not null } && FileSafety.CanEdit(l.FullPath) && File.Exists(l.FullPath))
            .Select(l => l.FullPath).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The project folder picked in the tree, on disk, which a paste goes into; null when a project folder isn't picked.</summary>
    private string? PasteFolder()
    {
        if (FolderTree.SelectedItem is not FolderNode { Archive: ScriptFolder { Project: { } project } } node) return null;
        // a file from outside the project's folder is listed under "external", which is no folder on disk
        string folder = node.IsBundle ? project.Root : Path.Combine(project.Root, node.FullPath);
        return Directory.Exists(folder) && FileSafety.CanEdit(Path.Combine(folder, "file")) ? folder : null;
    }

    /// <summary>A file or count of files, for saying what was done: <c>player.ws</c>, or <c>3 files</c>.</summary>
    private static string What(IReadOnlyList<string> files) => files.Count == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : L.T("Count.File", files.Count);

    /// <summary>A folder as it's shown in the tree: its path in the project, or the project's name for its own folder.</summary>
    private static string FolderName(WorkbenchProject project, string folder) =>
        Path.GetRelativePath(project.Root, folder) is var relative && relative != "." ? relative : project.Name;

    private void UpdateProjectFileMenu()
    {
        int files = ProjectFiles(Highlighted()).Count;
        bool paste = PasteFolder() is not null;
        foreach (var item in new[] { GridCutItem, GridCopyFilesItem, GridDuplicateItem, GridDeleteItem })
            item.Visibility = files > 0 ? Visibility.Visible : Visibility.Collapsed;
        GridRenameItem.Visibility = files == 1 ? Visibility.Visible : Visibility.Collapsed;
        GridPasteItem.Visibility = paste ? Visibility.Visible : Visibility.Collapsed;
        GridPasteItem.IsEnabled = ClipboardFiles().Count > 0;
        GridFileSeparator.Visibility = files > 0 || paste ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FilesCut_Click(object sender, RoutedEventArgs e) => ClipFiles(cut: true);

    private void FilesCopy_Click(object sender, RoutedEventArgs e) => ClipFiles(cut: false);

    private void FilesPaste_Click(object sender, RoutedEventArgs e) => _ = PasteAsync();

    private void FilesDuplicate_Click(object sender, RoutedEventArgs e) => _ = DuplicateHighlightedAsync();

    private void FilesRename_Click(object sender, RoutedEventArgs e) => _ = RenameHighlightedAsync();

    private void FilesDelete_Click(object sender, RoutedEventArgs e) => _ = DeleteHighlightedAsync();

    // ---------------------------------------------------------------- the clipboard

    /// <summary>The files and folders on the clipboard, from here or from Explorer.</summary>
    private static List<string> ClipboardFiles()
    {
        try
        {
            return Clipboard.ContainsFileDropList() ? [.. Clipboard.GetFileDropList().Cast<string>()] : [];
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>Whether the files on the clipboard were cut, to be moved, rather than copied: as Explorer marks them.</summary>
    private static bool ClipboardIsCut()
    {
        try
        {
            return Clipboard.GetData(DropEffectFormat) is MemoryStream effect && effect.Length >= 4
                && (new BinaryReader(effect).ReadInt32() & (int)DragDropEffects.Move) != 0;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>Puts the highlighted project files on the clipboard as files, to paste here or in Explorer, with their paths as text.</summary>
    private void ClipFiles(bool cut)
    {
        var files = ProjectFiles(Highlighted());
        if (files.Count == 0) return;
        var list = new StringCollection();
        list.AddRange([.. files]);
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetText(string.Join(Environment.NewLine, Highlighted().Select(i => i.Path)));
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes((int)(cut ? DragDropEffects.Move : DragDropEffects.Copy))));
        try
        {
            Clipboard.SetDataObject(data, true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus(L.T("MainWindow.ClipboardUseAnotherProgram"));
            return;
        }
        SetStatus(L.T(cut ? "ProjectFiles.CutStatus" : "ProjectFiles.CopiedStatus", What(files)));
    }

    /// <summary>
    /// Pastes the files and folders on the clipboard into the project folder picked in the tree: copied, or moved when
    /// they were cut. A file pasted where it is already gets a copy's name, as in Explorer; one with the name of a file
    /// there already is asked about. What's pasted outside the project's folders is added to the project.
    /// </summary>
    private async Task PasteAsync()
    {
        if (ProjectWorkspace.Current is not { } project) return;
        if (PasteFolder() is not { } folder)
        {
            SetStatus(L.T("ProjectFiles.PickFolder"));
            return;
        }
        bool cut = ClipboardIsCut();
        static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\');
        var sources = ClipboardFiles().Where(p => File.Exists(p) || Directory.Exists(p)).Select(Full)
            // a folder can't go into itself, and a file cut and pasted where it is changes nothing
            .Where(s => !(Directory.Exists(s) && (folder + "\\").StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase)))
            .Where(s => !cut || !string.Equals(Path.GetDirectoryName(s), Full(folder), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0)
        {
            SetStatus(L.T("ProjectFiles.NothingToPaste"));
            return;
        }
        string Target(string source) => Path.Combine(folder, Path.GetFileName(source));
        bool Here(string source) => string.Equals(Path.GetDirectoryName(source), Full(folder), StringComparison.OrdinalIgnoreCase);
        var clashes = sources.Where(s => !Here(s) && (File.Exists(Target(s)) || Directory.Exists(Target(s)))).ToList();
        bool replace = false;
        if (clashes.Count > 0)
        {
            string where = FolderName(project, folder);
            var choice = await Message.Ask(
                clashes.Count == 1 ? L.T("ProjectFiles.AlreadyThereOne", Path.GetFileName(clashes[0]), where) : L.T("ProjectFiles.AlreadyThereMany", L.T("Count.File", clashes.Count), where),
                L.T("ProjectFiles.ReplaceOrKeep", clashes.Count), MessageKind.Question, L.T("ProjectFiles.Replace"), L.T("ProjectFiles.KeepBoth"));
            if (choice == MessageChoice.Cancel) return;
            replace = choice == MessageChoice.Primary;
        }
        // what's open in a tab is closed first: what's moved away, and what's replaced
        var moving = cut ? sources.Where(FileSafety.CanEdit).ToList() : [];
        if (moving.Count > 0 && !await PreviewWindow.CloseTabsUnderAsync(moving, renaming: true, Message)) return;
        if (replace && !await PreviewWindow.CloseTabsUnderAsync([.. clashes.Select(Target)], renaming: false, Message)) return;

        var made = new List<string>();
        var failed = new List<string>();
        bool listChanged = false;
        foreach (string source in sources)
        {
            bool folderToo = Directory.Exists(source);
            string target = Target(source);
            if (Here(source) || !replace && (File.Exists(target) || Directory.Exists(target))) target = FreeName(folder, Path.GetFileName(source), folderToo);
            // the game's files, REDkit's and installed mods' are copied, never moved out of where they are
            bool move = cut && FileSafety.CanEdit(source);
            try
            {
                if (folderToo)
                {
                    FileSafety.CheckFolder(target);
                    CopyFolder(source, target);
                    if (move) Directory.Delete(source, true);
                }
                else
                {
                    FileSafety.CheckWrite(target);
                    if (move) File.Move(source, target, true);
                    else File.Copy(source, target, true);
                }
                made.Add(target);
                // a file the project listed by itself is listed where it went
                if (move) listChanged |= project.Remove(source);
                listChanged |= project.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProtectedFileException)
            {
                failed.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }
        if (listChanged) SaveProject(project);
        // what was cut has gone where it was pasted, as Explorer leaves it
        if (cut && made.Count > 0)
            try { Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.COMException) { }
        _reloadHighlight = made;
        await ReloadProjectAsync();
        if (made.Count > 0) SetStatus(L.T(cut ? "ProjectFiles.Moved" : "ProjectFiles.Pasted", What(made), FolderName(project, folder)));
        if (failed.Count > 0) ShowError(L.T("ProjectFiles.CouldNotPaste", failed.Count) + "\n\n" + string.Join("\n", failed));
    }

    /// <summary>A name for a copy in a folder, free there, as Explorer gives one: <c>name - Copy.ws</c>, then <c>name - Copy (2).ws</c>.</summary>
    private static string FreeName(string folder, string name, bool isFolder)
    {
        string stem = isFolder ? name : Path.GetFileNameWithoutExtension(name), extension = isFolder ? "" : Path.GetExtension(name);
        for (int number = 1; ; number++)
        {
            string candidate = Path.Combine(folder, (number == 1 ? L.T("ProjectFiles.CopyName", stem) : L.T("ProjectFiles.CopyNameNumbered", stem, number)) + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (string sub in Directory.EnumerateDirectories(from)) CopyFolder(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    private void SaveProject(WorkbenchProject project)
    {
        try
        {
            project.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.ProjectCouldNotSaved", ex.Message));
        }
    }

    // ---------------------------------------------------------------- duplicate, rename, delete

    /// <summary>Makes a copy of each highlighted project file beside it, named as Explorer names copies.</summary>
    private async Task DuplicateHighlightedAsync()
    {
        if (ProjectWorkspace.Current is not { } project) return;
        var files = ProjectFiles(Highlighted());
        if (files.Count == 0) return;
        var made = new List<string>();
        var failed = new List<string>();
        bool listChanged = false;
        foreach (string file in files)
        {
            string target = FreeName(Path.GetDirectoryName(file)!, Path.GetFileName(file), false);
            try
            {
                File.Copy(file, target);
                made.Add(target);
                // the copy of a file the project lists by itself is listed too
                listChanged |= project.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        if (listChanged) SaveProject(project);
        _reloadHighlight = made;
        await ReloadProjectAsync();
        if (made.Count > 0) SetStatus(L.T("ProjectFiles.Duplicated", What(files)));
        if (failed.Count > 0) ShowError(L.T("ProjectFiles.CouldNotCopy", failed.Count) + "\n\n" + string.Join("\n", failed));
    }

    /// <summary>F2: renames the highlighted project file, in the bar over the list.</summary>
    private async Task RenameHighlightedAsync()
    {
        if (ProjectWorkspace.Current is not { } project || ProjectFiles(Highlighted()) is not [var path]) return;
        string folder = Path.GetDirectoryName(path)!, old = Path.GetFileName(path);
        string? name = await AskNameAsync(L.T("PreviewWindow.Rename", old), old, text =>
            text.Equals(old, StringComparison.Ordinal) ? "" : PreviewWindow.NameProblem(text)
            ?? (!text.Equals(old, StringComparison.OrdinalIgnoreCase) && (File.Exists(Path.Combine(folder, text)) || Directory.Exists(Path.Combine(folder, text)))
                ? L.T("PreviewWindow.ThereAlready2", text) : null));
        if (name is null || name == old) return;
        if (!await PreviewWindow.CloseTabsUnderAsync([path], renaming: true, Message)) return;
        string target = Path.Combine(folder, name);
        try
        {
            File.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Message.Show(L.T("PreviewWindow.CouldNotRenamed", old), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        // a file the project lists by itself is listed by its new name
        if (project.Remove(path))
        {
            project.Add(target);
            SaveProject(project);
        }
        _reloadHighlight = [target];
        await ReloadProjectAsync();
        SetStatus(L.T("ProjectFiles.Renamed", old, name));
    }

    /// <summary>Del: sends the highlighted project files to the Recycle Bin, asking first unless Settings says not to.</summary>
    private async Task DeleteHighlightedAsync()
    {
        if (ProjectWorkspace.Current is not { } project) return;
        var files = ProjectFiles(Highlighted());
        if (files.Count == 0) return;
        if (_settings.AskBeforeDelete && !await Message.Show(L.T("PreviewWindow.Delete", What(files)), L.T("ProjectFiles.RecycleBin", files.Count),
                MessageKind.Question, primary: L.T("PreviewWindow.Delete2"), secondary: L.T("Common.Cancel")))
            return;
        if (!await PreviewWindow.CloseTabsUnderAsync(files, renaming: false, Message)) return;
        var deleted = new List<string>();
        var failed = new List<string>();
        bool listChanged = false;
        foreach (string file in files)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                deleted.Add(file);
                // a file the project listed by itself isn't listed any more
                listChanged |= project.Remove(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        if (listChanged) SaveProject(project);
        await ReloadProjectAsync();
        if (deleted.Count > 0) SetStatus(L.T("ProjectFiles.Deleted", What(deleted)));
        if (failed.Count > 0) ShowError(L.T("ProjectFiles.CouldNotDelete", failed.Count) + "\n\n" + string.Join("\n", failed));
    }

    // ---------------------------------------------------------------- naming, in the bar over the list

    private TaskCompletionSource<string?>? _naming;
    private Func<string, string?>? _nameCheck;

    /// <summary>
    /// Asks for a name in the bar over the list; null when cancelled. <paramref name="check"/> says what's wrong with a
    /// name, an empty string for one that changes nothing, or null when it's fine.
    /// </summary>
    private Task<string?> AskNameAsync(string label, string initial, Func<string, string?> check)
    {
        _naming?.TrySetResult(null);
        var answer = _naming = new TaskCompletionSource<string?>();
        _nameCheck = check;
        NameLabel.Text = label;
        NameBox.Text = initial;
        NameProblem.Visibility = Visibility.Collapsed;
        NameBar.Visibility = Visibility.Visible;
        NameBox.Focus();
        // the name without its extension, as Explorer picks it
        int dot = initial.LastIndexOf('.');
        NameBox.Select(0, dot > 0 ? dot : initial.Length);
        return answer.Task;
    }

    private void NameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndNaming(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            string text = NameBox.Text.Trim();
            string? problem = _nameCheck?.Invoke(text);
            if (problem is { Length: > 0 })
            {
                NameProblem.Text = problem;
                NameProblem.Visibility = Visibility.Visible;
            }
            else EndNaming(problem is null ? text : null);
            e.Handled = true;
        }
    }

    // clicking away from the name leaves it as it was
    private void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_naming is not null && !Message.IsOpen) EndNaming(null);
    }

    private void EndNaming(string? name)
    {
        var answer = _naming;
        _naming = null;
        NameBar.Visibility = Visibility.Collapsed;
        answer?.TrySetResult(name);
        if (ThumbsMode) FileThumbs.Focus();
        else FileGrid.Focus();
    }

    // ---------------------------------------------------------------- the list after a change

    /// <summary>Highlights files in the list by their paths on disk, scrolled to the first.</summary>
    private void Highlight(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var items = _shown.Where(i => i.Entry is LooseScriptEntry l && wanted.Contains(l.FullPath)).ToList();
        if (items.Count == 0) return;
        if (ThumbsMode) FileThumbs.Select(items);
        else
        {
            FileGrid.SelectedItems.Clear();
            foreach (var item in items) FileGrid.SelectedItems.Add(item);
            FileGrid.ScrollIntoView(items[0]);
        }
    }

    // ---------------------------------------------------------------- a folder's own files, or everything under it

    private void SubfoldersToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ListSubfolderFiles = SubfoldersToggle.IsChecked == true;
        RefreshView();
    }

    private void ToggleSubfolders()
    {
        _settings.ListSubfolderFiles = !_settings.ListSubfolderFiles;
        ApplySubfolders();
    }

    private void ApplySubfolders()
    {
        SubfoldersToggle.IsChecked = _settings.ListSubfolderFiles;
        RefreshView();
    }
}
