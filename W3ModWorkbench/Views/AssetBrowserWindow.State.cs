using System.Windows;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>Where the asset browser was and what it was filtered by, kept between runs.</summary>
public sealed class AssetBrowserState
{
    /// <summary>The folder picked, as the names from its heading down ("Game files", "characters", "models").</summary>
    public List<string> Folder { get; set; } = [];
    public string Search { get; set; } = "";
    public string FolderFilter { get; set; } = "";
    public string? FileType { get; set; }
    public string? ResourceClass { get; set; }
    public List<string> HiddenSources { get; set; } = [];
    public string? EabCategory { get; set; }
    public List<string> EabInclude { get; set; } = [];
    public List<string> EabExclude { get; set; } = [];
}

/// <summary>Clearing every filter at once, and coming back to the folder and filters the asset browser was left with.</summary>
public partial class AssetBrowserWindow
{
    /// <summary>Whether any filter narrows the list, so Clear filters shows only when there's something to clear.</summary>
    private bool AnyFilter => SearchBox.Text.Length > 0 || FolderBox.Text.Length > 0 || _typeFilter is not null || _classFilter is not null
        || _hiddenSources.Count > 0 || _eabCategory is not null || _eabInclude.Count + _eabExclude.Count > 0;

    private void UpdateClearButton() => ClearFiltersButton.Visibility = AnyFilter ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Takes off every filter (search, folder, file type, class, parts of the game, EAB), keeping the folder picked and how the search reads.</summary>
    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        SearchBox.Clear();
        FolderBox.Clear();
        _typeFilter = _classFilter = _eabCategory = null;
        _eabInclude.Clear();
        _eabExclude.Clear();
        _hiddenSources.Clear();
        AfterSourcesChanged();
        ApplyEab();
        _searchTimer.Stop();
        RefreshView();
        SearchBox.Focus();
    }

    // ---------------------------------------------------------------- kept between runs

    private void SaveState()
    {
        var state = _settings.AssetBrowser;
        state.Folder = FolderTree.SelectedItem is FolderNode node && PathTo(node) is { } path ? path : [];
        state.Search = SearchBox.Text;
        state.FolderFilter = FolderBox.Text;
        state.FileType = _typeFilter;
        state.ResourceClass = _classFilter;
        state.HiddenSources = [.. _hiddenSources];
        state.EabCategory = _eabCategory;
        state.EabInclude = [.. _eabInclude];
        state.EabExclude = [.. _eabExclude];
        _settings.Save();
    }

    /// <summary>Picks the folder and puts back the filters the browser was closed with; false when the folder isn't there now (a mod since removed).</summary>
    private bool RestoreState()
    {
        var state = _settings.AssetBrowser;
        SearchBox.Text = state.Search;
        FolderBox.Text = state.FolderFilter;
        // the list is made once, when the folder's picked
        _searchTimer.Stop();
        (_typeFilter, _classFilter) = (state.FileType, state.ResourceClass);
        _hiddenSources.UnionWith(state.HiddenSources);
        AfterSourcesChangedQuietly();
        if (_settings.EasyAssetBrowserOn)
        {
            _eabCategory = state.EabCategory;
            _eabInclude.UnionWith(state.EabInclude);
            _eabExclude.UnionWith(state.EabExclude);
        }
        if (Find(state.Folder) is not { } node) return false;
        for (var up = Parent(node); up is not null; up = Parent(up)) up.IsExpanded = true;
        node.IsSelected = true;
        return true;
    }

    /// <summary>The names from a heading down to a folder, found by looking (a mod's archives aren't linked to their mod's entry).</summary>
    private static List<string>? PathTo(FolderNode target)
    {
        foreach (var heading in Headings)
            if (Search(heading, [HeadingKey(heading)]) is { } found) return found;
        return null;

        List<string>? Search(FolderNode node, List<string> names)
        {
            if (node == target) return names;
            foreach (var child in node.Children)
                if (Search(child, [.. names, child.Name]) is { } found) return found;
            return null;
        }
    }

    private static FolderNode? Find(List<string> names)
    {
        // by key, or by the heading's name for a state saved before keys (in the language it was then)
        if (names.Count == 0 || Headings.FirstOrDefault(h => HeadingKey(h) == names[0] || h.Name == names[0]) is not { } node) return null;
        foreach (string name in names.Skip(1))
            if (node.Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } child) node = child;
            else return null;
        return node;
    }

    /// <summary>A heading's name in the saved state, the same in every language (its title is translated).</summary>
    private static string HeadingKey(FolderNode heading) =>
        heading == _root ? "game" : heading == _modDlc ? "moddlc" : heading == _mods ? "mods" : heading == _redkit ? "redkit" : heading.Name; // loc-ignore

    /// <summary>A folder's parent in the tree, a mod's archive's being its mod's entry.</summary>
    private static FolderNode? Parent(FolderNode node) =>
        node.Parent ?? Headings.SelectMany(h => h.Children.Concat(h.Children.SelectMany(c => c.Children)).Prepend(h))
            .FirstOrDefault(n => n.Children.Contains(node));
}
