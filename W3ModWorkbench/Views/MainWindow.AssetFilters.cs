using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>One choice in the class filter: a resource class and how many of the files in view hold it, or every class.</summary>
public sealed record ClassChoice(string? Class, int Count)
{
    public string Name => Class ?? "(all)";
    public string CountText => Count.ToString("N0");
}

/// <summary>The file list's class filter, and the asset browser's bookmarks, as REDkit's asset browser has them.</summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- class filter

    /// <summary>The class the list is narrowed to, or null for every file.</summary>
    private string? _classFilter;

    private bool _fillingClasses;

    /// <summary>Lists the classes the files in view hold, with how many of each, keeping the one picked.</summary>
    private void FillClassChoices(IReadOnlyList<EntryItem> scope)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in scope)
            if (item.ResourceClass is { } name) counts[name] = counts.GetValueOrDefault(name) + 1;
        // the class picked stays in the list where there are none of it, so moving to another folder doesn't drop it
        if (_classFilter is not null) counts.TryAdd(_classFilter, 0);
        var choices = new List<ClassChoice> { new(null, scope.Count) };
        choices.AddRange(counts.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => new ClassChoice(c.Key, c.Value)));
        _fillingClasses = true;
        try
        {
            ClassBox.ItemsSource = choices;
            ClassBox.SelectedItem = choices.First(c => c.Class == _classFilter);
        }
        finally
        {
            _fillingClasses = false;
        }
    }

    private void ClassBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingClasses || ClassBox.SelectedItem is not ClassChoice choice || choice.Class == _classFilter) return;
        _classFilter = choice.Class;
        RefreshView();
    }

    // ---------------------------------------------------------------- bookmarks

    private FolderNode? AssetBrowserNode => _roots.FirstOrDefault(r => r.Archive is GameFilesArchive);

    /// <summary>Whether the list shows the asset browser's files. They're each from their own bundle, so the tree says, not the file.</summary>
    private bool InAssetBrowser => FolderTree.SelectedItem is FolderNode { Archive: GameFilesArchive };

    /// <summary>The asset browser folder selected in the tree, as a bookmark (its game path and a backslash), or null.</summary>
    private string? CurrentFolderBookmark =>
        FolderTree.SelectedItem is FolderNode { IsBundle: false, Archive: GameFilesArchive } node ? node.FullPath + "\\" : null;

    private bool IsBookmarked(string path) => _settings.AssetBookmarks.Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fills or empties the star for the folder selected, and greys it out away from the asset browser.</summary>
    private void UpdateBookmarkButton()
    {
        string? folder = CurrentFolderBookmark;
        BookmarkButton.IsEnabled = folder is not null;
        bool marked = folder is not null && IsBookmarked(folder);
        BookmarkGlyph.Text = marked ? "" : "";
        BookmarkButton.ToolTip = folder is null ? "Pick a folder in the asset browser to bookmark it"
            : marked ? $"Take {folder} off the bookmarks" : $"Bookmark {folder}, to come back to it from the bookmarks list";
    }

    private void ToggleBookmark(string path)
    {
        if (IsBookmarked(path)) _settings.AssetBookmarks.RemoveAll(b => b.Equals(path, StringComparison.OrdinalIgnoreCase));
        else _settings.AssetBookmarks.Add(path);
        _settings.Save();
        UpdateBookmarkButton();
        SetStatus(IsBookmarked(path) ? $"Bookmarked {path}" : $"Took {path} off the bookmarks");
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFolderBookmark is { } folder) ToggleBookmark(folder);
    }

    private void GridBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (!InAssetBrowser) return;
        // as the menu said: off when they all are bookmarked, otherwise on for those that aren't
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
            menu.Items.Add(new MenuItem
            {
                Header = "No bookmarks yet. Pick a folder in the asset browser and press the star, or right-click a file and choose Bookmark.",
                IsEnabled = false,
            });
            menu.IsOpen = true;
            return;
        }
        foreach (string bookmark in bookmarks)
        {
            string path = bookmark;
            menu.Items.Add(BookmarkItem(path, () => _ = GoToBookmarkAsync(path)));
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Remove a bookmark", Icon = Glyph("") };
        foreach (string bookmark in bookmarks)
        {
            string path = bookmark;
            remove.Items.Add(BookmarkItem(path, () => ToggleBookmark(path)));
        }
        menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    private MenuItem BookmarkItem(string path, Action click)
    {
        bool folder = path.EndsWith('\\');
        string trimmed = path.TrimEnd('\\');
        int slash = trimmed.LastIndexOf('\\');
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = slash < 0 ? trimmed : trimmed[(slash + 1)..], VerticalAlignment = VerticalAlignment.Center });
        if (slash > 0)
            header.Children.Add(new TextBlock { Text = trimmed[..slash], Opacity = 0.55, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), MaxWidth = 420,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var item = new MenuItem { Header = header, ToolTip = path, Icon = Glyph(folder ? "" : "") };
        item.Click += (_, _) => click();
        return item;
    }

    private TextBlock Glyph(string glyph) => new() { Style = (Style)FindResource("IconText"), Text = glyph };

    /// <summary>
    /// Opens the asset browser (reading it first if it isn't open) at a bookmarked folder, or at a file's folder with the
    /// file highlighted. The search and class filter are cleared, so the file shows.
    /// </summary>
    private async Task GoToBookmarkAsync(string path)
    {
        await BrowseGameFilesAsync();
        if (AssetBrowserNode is not { } root) return;
        bool folder = path.EndsWith('\\');
        int slash = path.LastIndexOf('\\');
        string folderPath = folder ? path.TrimEnd('\\') : slash > 0 ? path[..slash] : "";
        var node = root;
        foreach (string part in folderPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) is not { } child)
            {
                ShowError($"{path} isn't in the game's files.\n\nIt may have come from a mod that's no longer installed. Remove it from the bookmarks list if it's gone for good.");
                return;
            }
            node = child;
        }
        SearchBox.Clear();
        _classFilter = null;
        for (var up = node.Parent; up is not null; up = up.Parent) up.IsExpanded = true;
        // the tree makes the items of folders just expanded when it next lays out; select once they're there
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        node.IsSelected = true;
        RefreshView();
        (ContainerFor(node) as FrameworkElement)?.BringIntoView();
        if (folder) return;
        if (_shown.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is not { } file)
        {
            ShowError($"{path} isn't in the game's files.\n\nIt may have come from a mod that's no longer installed. Remove it from the bookmarks list if it's gone for good.");
            return;
        }
        if (ThumbsMode)
        {
            foreach (var item in _shown) item.IsSelected = false;
            file.IsSelected = true;
        }
        else
        {
            FileGrid.SelectedItem = file;
            FileGrid.ScrollIntoView(file);
            FileGrid.Focus();
        }
    }

    /// <summary>The tree's item for a folder, found down from its root through the items already made.</summary>
    private TreeViewItem? ContainerFor(FolderNode node)
    {
        var chain = new Stack<FolderNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Push(n);
        ItemsControl parent = FolderTree;
        TreeViewItem? item = null;
        foreach (var n in chain)
        {
            item = parent.ItemContainerGenerator.ContainerFromItem(n) as TreeViewItem;
            if (item is null) return null;
            parent = item;
        }
        return item;
    }
}
