using System.Windows;
using W3ModWorkbench.Core;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// REDkit's own files in the asset browser (its r4data, a heading of its own, greyed out until REDkit is set in
/// Settings), and what uses each file: the reference index over the game's, the mods' and REDkit's files.
/// </summary>
public partial class AssetBrowserWindow
{
    private const string RedkitTitle = "REDkit";

    private const string RedkitNotSet = "REDkit's own files, from before they're cooked. Set REDkit's wcc_lite in Settings to list them here "
        + "(Find automatically looks in your Steam and GOG libraries). REDkit is free on Steam and GOG for owners of the game.";

    // read once a run, like the game's, and again when Settings points at another REDkit
    private static FolderNode? _redkit;
    private static string? _redkitFolder;
    private static List<IArchive> _order = [];

    /// <summary>Lists REDkit's r4data under its own heading when Settings has REDkit; otherwise the heading greyed out, saying how.</summary>
    private async Task EnsureRedkitAsync()
    {
        string? folder = RedkitDepot.FolderFromSettings();
        if (folder is null)
        {
            if (_redkit is not { IsUnavailable: true }) (_redkit, _redkitFolder) = (FolderNode.Placeholder(RedkitTitle, RedkitNotSet), null);
            return;
        }
        if (_redkit is { IsUnavailable: false } && string.Equals(_redkitFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        var node = await Loading.RunAsync("Reading REDkit's files", async (report, token) =>
        {
            report.Step($"Listing REDkit's files in {folder}");
            var depot = await Task.Run(() => RedkitDepot.Open(folder, name => report.Detail(name), token), token);
            report.Step("Sorting them into folders");
            var built = await Task.Run(() => FolderNode.Build(depot, (_, _) => { }), token);
            built.IsHeading = true;
            built.IsExpanded = false;
            return built;
        });
        if (node is null) return;
        (_redkit, _redkitFolder) = (node, folder);
        _byPath = null;
        _byArchive = null;
    }

    /// <summary>The tree's headings: Game files, Mod DLC, Mods and REDkit.</summary>
    private void ShowTree() => FolderTree.ItemsSource = new[] { _root, _modDlc, _mods, _redkit }.OfType<FolderNode>().ToList();

    /// <summary>Opens Settings for REDkit's wcc_lite, and lists REDkit's files once it's set.</summary>
    private async void RedkitSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.OpenSettings(this)) return;
        await EnsureRedkitAsync();
        ShowTree();
        if (_redkit is { IsUnavailable: false } redkit) redkit.IsSelected = true;
        RefreshView();
    }

    // ---------------------------------------------------------------- finding the browser's item for a file

    // every file the browser lists, by archive and path, and the game's (or else REDkit's) by path
    private static Dictionary<string, EntryItem>? _byArchive;
    private static Dictionary<string, EntryItem>? _byPath;

    private static IEnumerable<FolderNode> Headings => new[] { _root, _modDlc, _mods, _redkit }.OfType<FolderNode>();

    private static void IndexItems()
    {
        if (_byArchive is not null && _byPath is not null) return;
        var byArchive = new Dictionary<string, EntryItem>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, EntryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var heading in Headings)
            foreach (var item in heading.AllFiles())
            {
                byArchive.TryAdd(item.Entry.Archive.FilePath + "|" + item.Path, item);
                // the game's copy first, REDkit's when the game has none; a mod's only shows for its own archive
                if (heading == _root || heading == _redkit) byPath.TryAdd(item.Path, item);
            }
        (_byArchive, _byPath) = (byArchive, byPath);
    }

    /// <summary>The browser's item for a file in an archive, or null when it isn't listed.</summary>
    internal EntryItem? ItemFor(ReferenceSource source)
    {
        IndexItems();
        return _byArchive!.GetValueOrDefault(source.ArchivePath + "|" + source.Path);
    }

    /// <summary>The browser's item for a game path: the game's copy, or REDkit's when the game has none; null when neither has it.</summary>
    internal EntryItem? ItemForPath(string path)
    {
        IndexItems();
        return _byPath!.GetValueOrDefault(path.Replace('/', '\\'));
    }

    /// <summary>Opens a file for another window (what uses a file), as double-clicking it here does.</summary>
    internal void OpenFromElsewhere(EntryItem item) => OpenItem(item);

    /// <summary>Goes to a file in the tree, highlighted, under the heading it's from (REDkit's or the game's).</summary>
    internal async Task GoToAsync(string path, EntryItem? item)
    {
        Activate();
        await GoToBookmarkAsync(path, item?.Entry.Archive is RedkitDepot ? _redkit : null);
    }

    // ---------------------------------------------------------------- what uses a file

    private static Task<ReferenceIndex>? _references;

    /// <summary>How the reference index is coming along: files done, of how many, and the archive being read.</summary>
    internal static event Action<int, int, string>? IndexProgress;

    /// <summary>
    /// The reference index over the game's bundles, the mods' and REDkit's r4data: made the first time it's asked for
    /// (each archive from its cache when it hasn't changed), or again when <paramref name="rebuild"/>.
    /// </summary>
    internal Task<ReferenceIndex> ReferencesAsync(bool rebuild)
    {
        if (_references is null || rebuild || _references.IsFaulted || _references.IsCanceled)
        {
            var archives = _order.Concat(Game?.Parts ?? []).Concat(_redkit is { IsUnavailable: false } redkit ? [redkit.Archive] : []);
            var progress = new Progress<(int Done, int Total, string Name)>(p => IndexProgress?.Invoke(p.Done, p.Total, p.Name));
            _references = ReferenceIndex.BuildAsync(archives, progress, CancellationToken.None);
        }
        return _references;
    }

    private void WhatUses_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted().FirstOrDefault() is { } item) UsesWindow.Show(this, item);
    }
}
