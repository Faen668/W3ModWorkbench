using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.LoadOrder;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>One choice in the class filter: a resource class and how many of the files in view hold it, or every class.</summary>
public sealed record ClassChoice(string? Class, int Count)
{
    public string Name => Class ?? "(all)";
    public string CountText => Count.ToString("N0");
}

/// <summary>What the main window's file list does to files, asked for from another window: <see cref="MainWindow.Act"/>.</summary>
public enum FileAction { Open, Preview, Properties, Json, Xml, External, CopyToProject, Extract, CopyPath, CopyName, CopyFolder, CopyLocation, Reveal }

/// <summary>
/// The asset browser, a tool window of its own as REDkit's is: every file the game has (content0, its DLC and the
/// installed mods) in one tree, each path once from where the game loads it, to search, filter by class, bookmark,
/// preview, open, copy into the project and extract. A file more than one archive has asks which copy to open, and
/// what the copy opened loads comes from its own mod and then the game. The game's files are read once, the first time
/// it opens.
/// </summary>
public partial class AssetBrowserWindow : Window
{
    private static AssetBrowserWindow? _instance;
    // read once a run: the game's indexes don't change while the tool's open
    private static FolderNode? _root;
    private static FolderNode? _modDlc;
    private static FolderNode? _mods;
    private static Dictionary<string, List<ArchiveEntry>> _copies = [];
    private static string? _rootGame;

    private readonly MainWindow _main;
    private readonly Settings _settings = App.Settings;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private List<EntryItem> _shown = [];
    private string? _classFilter;
    private string? _typeFilter;
    private bool _filling;

    private AssetBrowserWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        AskToggle.IsChecked = _settings.AskWhichCopy;
        RegexToggle.IsChecked = _settings.AssetSearchRegex;
        ApplySearchHints();
        ApplyEab();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshView();
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdatePreview();
        };
        FileThumbs.ContextMenu = FileGrid.ContextMenu;
        FileThumbs.TextureFor = async item => await main.FindTextureForHeaderAsync(item);
        FileThumbs.ItemOpened += item => OpenItem(item);
        FileThumbs.SelectionChanged += QueuePreview;
        PreviewPane.LocatorFor = main.LocatorFor;
        PreviewPane.TextureFor = main.FindTextureForHeaderAsync;
        ApplyView();
        ApplyPreviewPane();
        Closed += (_, _) =>
        {
            _instance = null;
            PreviewPane.Stop();
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Opens the asset browser, or brings it to the front when it's open.</summary>
    public static void Open(MainWindow main)
    {
        if (_instance is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            open.SearchBox.Focus();
            return;
        }
        _instance = new AssetBrowserWindow(main);
        _instance.Standalone(main).Show();
    }

    private GameFilesArchive? Game => _root?.Archive as GameFilesArchive;

    private async Task LoadAsync()
    {
        string? game = GameLocator.Resolve(null);
        if (game is null || !Directory.Exists(Path.Combine(game, "content")))
        {
            StatusText.Text = "The game folder isn't set. The asset browser lists the files of The Witcher 3's content, DLC and mods: set its folder in Settings.";
            return;
        }
        if (_root is null || !string.Equals(_rootGame, game, StringComparison.OrdinalIgnoreCase))
        {
            (FolderNode? ModDlc, FolderNode? Mods, List<IArchive> Order) read = default;
            var root = await Loading.RunAsync("Reading the game's files", async (report, token) =>
            {
                report.Step("Reading the game's bundles and texture caches");
                var archive = await Task.Run(() => GameFilesArchive.Open(game, name => report.Detail(name)), token);
                var files = await Task.Run(() => FolderNode.Build(archive, (_, _) => { }), token);
                files.IsHeading = true;
                report.Step("Reading the installed mods");
                read = await Task.Run(() => ReadMods(game, archive, report, token), token);
                report.Step("Finding every copy of each file");
                _copies = await Task.Run(() => IndexCopies(read.Order), token);
                report.Step("Finding which part of the game each file is from");
                _sourceOf = await Task.Run(() => ReadSources(game, archive, _copies), token);
                return files;
            });
            if (root is null)
            {
                StatusText.Text = "Reading the game's files was stopped. Close the asset browser and open it again to read them.";
                return;
            }
            (_root, _modDlc, _mods, _rootGame) = (root, read.ModDlc, read.Mods, game);
        }
        FolderTree.ItemsSource = new[] { _root, _modDlc, _mods }.OfType<FolderNode>().ToList();
        _root.IsExpanded = true;
        _root.IsSelected = true;
        RefreshView();
        SearchBox.Focus();
        await ShowPendingLinkAsync();
        await LoadEabAsync(false);
    }

    /// <summary>
    /// The mods, each its own entry with what it holds, as Open folder opens a mod (its bundles, texture caches,
    /// compiled scripts, loose scripts and strings): those installed as DLC under <b>Mod DLC</b>, and the rest (in
    /// mods, and mod.io's) under <b>Mods</b>, by name. Also every bundle and texture cache of theirs and the game's in
    /// the order the game looks in them, as the load order analyser orders them, for which copy of a file wins.
    /// </summary>
    private static (FolderNode? ModDlc, FolderNode? Mods, List<IArchive> Order) ReadMods(string game, GameFilesArchive files, LoadingReporter report, CancellationToken token)
    {
        string modsFolder = Path.Combine(game, "mods");
        var options = new LoadOrderOptions(modsFolder, true, ModScanner.FindSettingsFile(), game);
        // in load order, the first loaded first; the official DLC are the game's
        var entries = LoadOrderAnalyzer.DiscoverMods(options).Where(e => !(e.IsDlc && OfficialDlc.Is(game, e.Name))).ToList();
        var dlc = new List<FolderNode>();
        var mods = new List<FolderNode>();
        var order = new List<IArchive>();
        for (int i = 0; i < entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            report.Count(i, entries.Count);
            report.Detail(entries[i].Name);
            var members = new List<FolderNode>();
            try
            {
                foreach (string file in MainWindow.ArchivesIn(entries[i].Folder))
                    try { members.Add(FolderNode.Build(MainWindow.OpenArchive(file), (_, _) => { })); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (members.Count == 0) continue;
            (entries[i].IsDlc ? dlc : mods).Add(FolderNode.Group(entries[i].Name, members));
            order.AddRange(members.Select(m => m.Archive).Where(a => a is Bundle or Core.Textures.TextureCacheArchive));
        }
        order.AddRange(files.Parts);
        FolderNode? Group(string title, List<FolderNode> members)
        {
            if (members.Count == 0) return null;
            var group = FolderNode.Group(title, [.. members.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)], membersInGroup: false, heading: true);
            group.IsExpanded = false;
            return group;
        }
        return (Group("Mod DLC", dlc), Group("Mods", mods), order);
    }

    /// <summary>Whether a file is from Game files (the game and its official DLC), rather than a mod's under Mod DLC or Mods.</summary>
    private bool InGameFiles(EntryItem item) => Game is { } game && game.Parts.Contains(item.Entry.Archive);

    /// <summary>Every copy of each file in the game's and the mods' archives, by path, in the order the game looks for them.</summary>
    private static Dictionary<string, List<ArchiveEntry>> IndexCopies(List<IArchive> order)
    {
        var copies = new Dictionary<string, List<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in order)
            foreach (var e in archive.Entries)
            {
                // a texture in a cache and an .xbm in a bundle share a path but aren't copies of each other
                string key = (e is Core.Textures.TextureEntry ? "cache:" : "") + e.Path;
                if (!copies.TryGetValue(key, out var list)) copies[key] = list = [];
                list.Add(e);
            }
        return copies;
    }

    // ---------------------------------------------------------------- the list

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        RefreshView();
        UpdateBookmarkButton();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>Shows the files of the folder picked (all of them under it), matching the search, of the type and the class picked.</summary>
    private void RefreshView()
    {
        if (_root is null) return;
        var folder = FolderTree.SelectedItem as FolderNode ?? _root;
        if (!ReadRegexes()) return;
        List<EntryItem> scope = [.. folder.AllFiles()];
        // the part of the game, folder and regular expressions first; the search box is one of those when it's a regex
        var extra = ExtraFilter();
        string search = RegexMode ? "" : SearchBox.Text;
        List<EntryItem> left = extra is null ? scope : [.. scope.Where(extra)];
        var result = FileFilterResult.Apply(left, search, _typeFilter, _classFilter);
        // the EAB categories count what the other filters leave; the one picked narrows the list, type and class
        if (Eab is not null && _settings.EasyAssetBrowserOn)
        {
            FillEabCategories(result.Shown);
            if (EabFilter(withCategory: true) is { } inCategory) result = FileFilterResult.Apply([.. left.Where(inCategory)], search, _typeFilter, _classFilter);
        }
        FillChoices(result);
        _shown = result.Shown;
        BindFiles();
        NoMatchesText.Text = result.NoMatches(_typeFilter, _classFilter);
        NoMatchesText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        string where = folder == _root ? "the game's files" : folder == _mods ? "the installed mods" : folder == _modDlc ? "the mods installed as DLC" : folder.FullPath;
        string describe = string.Join(" and ", DescribeExtra().Concat(result.Describe.Length > 0 ? [result.Describe] : []));
        StatusText.Text = $"{_shown.Count:N0} of {scope.Count:N0} files in {where}" + (describe.Length > 0 ? $" match {describe}" : "")
            + $"   ·   {_root.Archive.Name}   ·   Double-click to open; what it loads comes from its own mod, then the game.";
        QueuePreview();
    }

    /// <summary>Lists the file types and the classes the other filters leave, with how many of each, keeping those picked.</summary>
    private void FillChoices(FileFilterResult result)
    {
        var types = FileFilterResult.Choices(result.Types, result.AnyType, _typeFilter);
        var classes = FileFilterResult.Choices(result.Classes, result.AnyClass, _classFilter);
        _filling = true;
        try
        {
            FileTypeBox.ItemsSource = types;
            FileTypeBox.SelectedItem = types.First(c => string.Equals(c.Class, _typeFilter, StringComparison.OrdinalIgnoreCase));
            ClassBox.ItemsSource = classes;
            ClassBox.SelectedItem = classes.First(c => c.Class == _classFilter);
        }
        finally
        {
            _filling = false;
        }
    }

    private void FileTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || FileTypeBox.SelectedItem is not ClassChoice choice || choice.Class == _typeFilter) return;
        _typeFilter = choice.Class;
        RefreshView();
    }

    private void ClassBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || ClassBox.SelectedItem is not ClassChoice choice || choice.Class == _classFilter) return;
        _classFilter = choice.Class;
        RefreshView();
    }

    private List<EntryItem> Highlighted() => ThumbsMode ? FileThumbs.Selected : [.. FileGrid.SelectedItems.OfType<EntryItem>()];

    private bool FilesFocused => FileGrid.IsKeyboardFocusWithin || FileThumbs.IsKeyboardFocusWithin;

    // ---------------------------------------------------------------- view and preview pane

    private bool ThumbsMode => _settings.AssetFileView is "Small" or "Medium" or "Large";

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
        FileThumbs.ThumbnailSize = FileThumbnails.SizeOf(_settings.AssetFileView);
        BindFiles();
    }

    private void SetView(string view)
    {
        if (_settings.AssetFileView == view) return;
        var selected = Highlighted();
        _settings.AssetFileView = view;
        _settings.Save();
        ApplyView();
        if (ThumbsMode) FileThumbs.Select(selected);
        else
            foreach (var item in selected) FileGrid.SelectedItems.Add(item);
        QueuePreview();
    }

    private void View_Click(object sender, RoutedEventArgs e) =>
        MainWindow.ShowViewMenu(ViewButton, _settings.AssetFileView, _settings.ShowAssetPreview, SetView, TogglePreviewPane);

    private void TogglePreviewPane()
    {
        _settings.ShowAssetPreview = !_settings.ShowAssetPreview;
        _settings.Save();
        ApplyPreviewPane();
    }

    private void ApplyPreviewPane()
    {
        bool show = _settings.ShowAssetPreview;
        PreviewPane.Visibility = PreviewSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitterColumn.Width = new GridLength(show ? 8 : 0);
        PreviewColumn.MinWidth = show ? 240 : 0;
        PreviewColumn.Width = new GridLength(show ? Math.Max(240, _settings.AssetPreviewWidth) : 0);
        UpdatePreview();
    }

    private void PreviewSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings.AssetPreviewWidth = PreviewColumn.ActualWidth;
        _settings.Save();
    }

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ThumbsMode) QueuePreview();
    }

    /// <summary>Shows what's highlighted in the preview pane once the highlight settles, when the pane is on.</summary>
    private void QueuePreview()
    {
        _previewTimer.Stop();
        if (_settings.ShowAssetPreview) _previewTimer.Start();
    }

    private void UpdatePreview()
    {
        if (_settings.ShowAssetPreview) _ = PreviewPane.ShowAsync(Highlighted());
        else PreviewPane.Stop();
    }

    // ---------------------------------------------------------------- opening

    /// <summary>
    /// Every copy of a file in the game's and the mods' archives, the one the game loads first, each as an item to act
    /// on: for one in Game files. One under Mod DLC or Mods is that mod's copy, and opens as it is.
    /// </summary>
    private List<EntryItem> CopiesOf(EntryItem item) =>
        InGameFiles(item) && _copies.TryGetValue((item.Entry is Core.Textures.TextureEntry ? "cache:" : "") + item.Path, out var copies)
            ? [.. copies.Select(e => ReferenceEquals(e, item.Entry) ? item : new EntryItem(e, (_, _) => { }))] : [item];

    /// <summary>
    /// Opens a file in its viewer: the copy the game loads, or when more than one archive has it and asking is on (or
    /// <paramref name="ask"/> says to), the copy picked.
    /// </summary>
    private void OpenItem(EntryItem item, bool ask = false)
    {
        var copies = CopiesOf(item);
        var open = item;
        if (copies.Count > 1 && (ask || _settings.AskWhichCopy))
        {
            if (PickCopyWindow.Pick(this, item.Name, [.. copies.Select(c => c.Entry)], item.Entry, out bool always) is not { } picked) return;
            open = copies.First(c => ReferenceEquals(c.Entry, picked));
            if (always)
            {
                _settings.AskWhichCopy = false;
                _settings.Save();
                AskToggle.IsChecked = false;
            }
        }
        RememberOpened(item.Path);
        _main.Act(FileAction.Open, [open]);
    }

    private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is EntryItem item) OpenItem(item);
    }

    private void Ask_Click(object sender, RoutedEventArgs e)
    {
        _settings.AskWhichCopy = AskToggle.IsChecked == true;
        _settings.Save();
    }

    private void FileGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var items = Highlighted();
        if (items.Count == 0)
        {
            e.Handled = true;
            return;
        }
        OpenCopyItem.Visibility = items.Count == 1 && CopiesOf(items[0]).Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        EabDetailsItem.Visibility = _settings.EasyAssetBrowserOn && Eab is not null ? Visibility.Visible : Visibility.Collapsed;
        BookmarkItem.Header = items.All(i => IsBookmarked(i.Path)) ? "Remove bookmark" : "Bookmark";
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault() is { } item) OpenItem(item);
    }

    private void OpenCopy_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault() is { } item) OpenItem(item, ask: true);
    }

    private void Copies_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault() is { } item) OtherCopiesWindow.Open(_main, item, CopiesOf(item));
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.Preview, Highlighted());
    private void Properties_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.Properties, Highlighted());
    private void Json_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.Json, Highlighted());
    private void CopyToProject_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.CopyToProject, Highlighted());
    private void Extract_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.Extract, Highlighted());
    private void CopyPath_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.CopyPath, Highlighted());
    private void CopyName_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.CopyName, Highlighted());
    private void CopyFolder_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.CopyFolder, Highlighted());
    private void CopyLocation_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.CopyLocation, Highlighted());
    private void Reveal_Click(object sender, RoutedEventArgs e) => _main.Act(FileAction.Reveal, Highlighted());

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt && (e.Key == Key.System ? e.SystemKey : e.Key) == Key.P)
        {
            TogglePreviewPane();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.FocusedElement is TextBox { Text.Length: > 0 } box && (box == SearchBox || box == FolderBox))
        {
            box.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && FilesFocused && Highlighted().FirstOrDefault() is { } item)
        {
            OpenItem(item);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && FilesFocused)
        {
            _main.Act(FileAction.CopyPath, Highlighted());
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not T) child = VisualTreeHelper.GetParent(child);
        return child as T;
    }

    // ---------------------------------------------------------------- bookmarks

    /// <summary>The folder picked in the tree, as a bookmark (its game path and a backslash), or null.</summary>
    private string? CurrentFolderBookmark => FolderTree.SelectedItem is FolderNode { IsBundle: false } node && node.Archive is GameFilesArchive ? node.FullPath + "\\" : null;

    private bool IsBookmarked(string path) => _settings.AssetBookmarks.Contains(path, StringComparer.OrdinalIgnoreCase);

    private void UpdateBookmarkButton()
    {
        string? folder = CurrentFolderBookmark;
        BookmarkButton.IsEnabled = folder is not null;
        bool marked = folder is not null && IsBookmarked(folder);
        BookmarkGlyph.Text = marked ? "" : "";
        BookmarkButton.ToolTip = folder is null ? "Pick a folder to bookmark it"
            : marked ? $"Take {folder} off the bookmarks" : $"Bookmark {folder}, to come back to it from the bookmarks list";
    }

    private void ToggleBookmark(string path)
    {
        if (IsBookmarked(path)) _settings.AssetBookmarks.RemoveAll(b => b.Equals(path, StringComparison.OrdinalIgnoreCase));
        else _settings.AssetBookmarks.Add(path);
        _settings.Save();
        UpdateBookmarkButton();
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFolderBookmark is { } folder) ToggleBookmark(folder);
    }

    private void BookmarkFiles_Click(object sender, RoutedEventArgs e)
    {
        // off when they all are bookmarked, otherwise on for those that aren't
        var paths = Highlighted().Take(50).Select(i => i.Path).ToList();
        bool remove = paths.All(IsBookmarked);
        foreach (string path in paths.Where(p => IsBookmarked(p) == remove)) ToggleBookmark(path);
    }

    private void Bookmarks_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BookmarksButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var bookmarks = _settings.AssetBookmarks.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
        if (bookmarks.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No bookmarks yet. Pick a folder and press the star, or right-click a file and choose Bookmark.", IsEnabled = false });
            menu.IsOpen = true;
            return;
        }
        foreach (string bookmark in bookmarks)
        {
            string path = bookmark;
            menu.Items.Add(BookmarkMenuItem(path, () => _ = GoToBookmarkAsync(path)));
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Remove a bookmark", Icon = Glyph("") };
        foreach (string bookmark in bookmarks)
        {
            string path = bookmark;
            remove.Items.Add(BookmarkMenuItem(path, () => ToggleBookmark(path)));
        }
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private MenuItem BookmarkMenuItem(string path, Action click)
    {
        bool folder = path.EndsWith('\\');
        string trimmed = path.TrimEnd('\\');
        int slash = trimmed.LastIndexOf('\\');
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = slash < 0 ? trimmed : trimmed[(slash + 1)..], VerticalAlignment = VerticalAlignment.Center });
        if (slash > 0)
            header.Children.Add(new TextBlock { Text = trimmed[..slash], Opacity = 0.55, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), MaxWidth = 420,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var item = new MenuItem { Header = header, ToolTip = path, Icon = Glyph(folder ? "" : "") };
        item.Click += (_, _) => click();
        return item;
    }

    private TextBlock Glyph(string glyph) => new() { Style = (Style)FindResource("IconText"), Text = glyph };

    /// <summary>Goes to a bookmarked folder, or to a file's folder with the file highlighted, clearing the search and class filter so it shows.</summary>
    public async Task GoToBookmarkAsync(string path)
    {
        if (_root is null) return;
        bool folder = path.EndsWith('\\');
        int slash = path.LastIndexOf('\\');
        string folderPath = folder ? path.TrimEnd('\\') : slash > 0 ? path[..slash] : "";
        var node = _root;
        foreach (string part in folderPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) is not { } child)
            {
                StatusText.Text = $"{path} isn't in the game's files. It may have come from a mod that's no longer installed.";
                return;
            }
            node = child;
        }
        SearchBox.Clear();
        FolderBox.Clear();
        _classFilter = null;
        _typeFilter = null;
        if (_hiddenSources.Count > 0) SetHiddenSources([]);
        for (var up = node.Parent; up is not null; up = up.Parent) up.IsExpanded = true;
        // the tree makes the items of folders just expanded when it next lays out; select once they're there
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        node.IsSelected = true;
        RefreshView();
        if (folder) return;
        if (_shown.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is not { } file)
        {
            StatusText.Text = $"{path} isn't in the game's files. It may have come from a mod that's no longer installed.";
            return;
        }
        if (ThumbsMode)
        {
            FileThumbs.Select([file]);
            FileThumbs.Focus();
            return;
        }
        FileGrid.SelectedItem = file;
        FileGrid.ScrollIntoView(file);
        FileGrid.Focus();
    }
}
