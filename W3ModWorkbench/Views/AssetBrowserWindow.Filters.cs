using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Core;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// The asset browser's other filters: which part of the game a file comes from (the base game, Hearts of Stone, Blood
/// and Wine, each free DLC), a folder filter, regular expressions for both searches, and the files opened recently.
/// </summary>
public partial class AssetBrowserWindow
{
    /// <summary>
    /// The free DLC by folder name, as CD PROJEKT named them. Those after dlc16 came with the Remastered update and have
    /// no names of their own; theirs say what their files are (there's no dlc19).
    /// </summary>
    private static Dictionary<string, string> FreeDlcNames => new(StringComparer.OrdinalIgnoreCase)
    {
        ["dlc1"] = L.T("AssetBrowserWindow.TemerianArmorSet"),
        ["dlc2"] = L.T("AssetBrowserWindow.BeardHairstyleSet"),
        ["dlc3"] = L.T("AssetBrowserWindow.ContractMissingMiners"),
        ["dlc4"] = L.T("AssetBrowserWindow.AlternativeLookYennefer"),
        ["dlc5"] = L.T("AssetBrowserWindow.NilfgaardianArmorSet"),
        ["dlc6"] = L.T("AssetBrowserWindow.AlternativeLookTriss"),
        ["dlc7"] = L.T("AssetBrowserWindow.FoolsGold"),
        ["dlc8"] = L.T("AssetBrowserWindow.BalladHeroesGwentCards"),
        ["dlc9"] = L.T("AssetBrowserWindow.NewGame"),
        ["dlc10"] = L.T("AssetBrowserWindow.ScavengerHuntWolfSchool"),
        ["dlc11"] = L.T("AssetBrowserWindow.AlternativeLookCiri"),
        ["dlc12"] = L.T("AssetBrowserWindow.ContractSkelligesMostWanted"),
        ["dlc13"] = L.T("AssetBrowserWindow.EliteCrossbowSet"),
        ["dlc14"] = L.T("AssetBrowserWindow.SkelligeArmorSet"),
        ["dlc15"] = L.T("AssetBrowserWindow.WhereCatWolfPlay"),
        ["dlc16"] = L.T("AssetBrowserWindow.NewFinisherAnimations"),
        ["dlc17"] = L.T("AssetBrowserWindow.DandelionsNetflixOutfit"),
        ["dlc18"] = L.T("AssetBrowserWindow.NetflixArmourSwordsGeralt"),
        ["dlc20"] = L.T("AssetBrowserWindow.NetflixNilfgaardianArmour"),
    };

    private const string BaseSource = "base";
    private const string FreeDlcShared = "dlc";

    // which part of the game each of the game's files comes from, worked out once a run with the files
    private static Dictionary<ArchiveEntry, string> _sourceOf = [];

    private readonly HashSet<string> _hiddenSources = new(StringComparer.OrdinalIgnoreCase);
    private Regex? _nameRegex;
    private Regex? _folderRegex;

    /// <summary>A part of the game in words: "Blood and Wine", "dlc5: Nilfgaardian Armor Set".</summary>
    private static string SourceName(string source) => source.ToLowerInvariant() switch
    {
        BaseSource => L.T("AssetBrowserWindow.BaseGame"),
        "ep1" => L.T("AssetBrowserWindow.HeartsStone"),
        "bob" => L.T("AssetBrowserWindow.BloodWine"),
        FreeDlcShared => L.T("AssetBrowserWindow.FreeDLCSharedFiles"),
        _ => FreeDlcNames.TryGetValue(source, out string? name) ? $"{source}: {name}" : source,
    };

    private static int SourceOrder(string source) => source.ToLowerInvariant() switch
    {
        BaseSource => 0,
        "ep1" => 1,
        "bob" => 2,
        FreeDlcShared => 10000,
        _ when source.StartsWith("dlc", StringComparison.OrdinalIgnoreCase) && int.TryParse(source[3..], out int n) => 100 + n,
        _ => 5000,
    };

    /// <summary>
    /// Which part of the game each of the game's files is from. A file under dlc\&lt;name&gt; is that DLC's. Any other
    /// is the base game's when a base game bundle has it; if only DLC bundles have it, it's the first of those DLC's:
    /// Remastered packs the DLC into content0 as bob, world_bob_*, ep1 and dlc0 (the free DLC), and older installs keep
    /// each in the dlc folder.
    /// </summary>
    private static Dictionary<ArchiveEntry, string> ReadSources(string game, GameFilesArchive files, Dictionary<string, List<ArchiveEntry>> copies)
    {
        var gameParts = files.Parts.ToHashSet();
        var ofArchive = new Dictionary<IArchive, string>();
        string dlcFolder = Path.Combine(game, "dlc") + Path.DirectorySeparatorChar;
        string ArchiveSource(IArchive archive)
        {
            if (ofArchive.TryGetValue(archive, out string? known)) return known;
            string source = BaseSource;
            string name = Path.GetFileNameWithoutExtension(archive.FilePath);
            if (archive.FilePath.StartsWith(dlcFolder, StringComparison.OrdinalIgnoreCase))
                source = archive.FilePath[dlcFolder.Length..].Split(Path.DirectorySeparatorChar)[0].ToLowerInvariant();
            else if (archive is Bundle)
                source = name.ToLowerInvariant() switch
                {
                    "bob" => "bob",
                    "ep1" => "ep1",
                    "dlc0" => FreeDlcShared,
                    var n when n.StartsWith("world_bob_") => "bob",
                    _ => BaseSource,
                };
            return ofArchive[archive] = source;
        }

        var sources = new Dictionary<ArchiveEntry, string>(files.Entries.Count);
        foreach (var entry in files.Entries)
        {
            string path = entry.Path;
            if (path.StartsWith("dlc\\", StringComparison.OrdinalIgnoreCase) && path.IndexOf('\\', 4) is > 4 and var end)
            {
                sources[entry] = path[4..end].ToLowerInvariant();
                continue;
            }
            string source = ArchiveSource(entry.Archive);
            if (source != BaseSource && copies.TryGetValue((entry is Core.Textures.TextureEntry ? "cache:" : "") + path, out var list))
                foreach (var copy in list)
                    if (gameParts.Contains(copy.Archive) && ArchiveSource(copy.Archive) == BaseSource)
                    {
                        source = BaseSource;
                        break;
                    }
            sources[entry] = source;
        }
        return sources;
    }

    /// <summary>The part of the game a file is from, or null for a mod's.</summary>
    private static string? SourceOf(EntryItem item) => _sourceOf.GetValueOrDefault(item.Entry);

    /// <summary>Whether the part of the game, folder filter and regular expression leave a file. A mod's file is left by the first.</summary>
    private Func<EntryItem, bool>? ExtraFilter()
    {
        var tests = new List<Func<EntryItem, bool>>();
        if (_hiddenSources.Count > 0) tests.Add(i => SourceOf(i) is not { } source || !_hiddenSources.Contains(source));
        if (RegexMode)
        {
            if (_nameRegex is { } name) tests.Add(i => name.IsMatch(i.Name));
            // a backslash starts an escape in a regex, so folders can be separated with / as well
            if (_folderRegex is { } folder) tests.Add(i => folder.IsMatch(i.Folder) || folder.IsMatch(i.Folder.Replace('\\', '/')));
        }
        else if (FolderBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } words)
            tests.Add(i => words.All(w => i.Folder.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (EabFilter(withCategory: false) is { } eab) tests.Add(eab);
        return tests.Count == 0 ? null : i => tests.All(t => t(i));
    }

    /// <summary>The other filters on, in words, for the status line.</summary>
    private List<string> DescribeExtra()
    {
        var on = new List<string>();
        if (RegexMode && _nameRegex is not null) on.Add(L.T("AssetBrowserWindow.Name", SearchBox.Text));
        if (FolderBox.Text.Trim() is { Length: > 0 } folder) on.Add(RegexMode ? L.T("AssetBrowserWindow.Folder", folder) : L.T("AssetBrowserWindow.Folder2", folder));
        if (_hiddenSources.Count > 0) on.Add(L.T("AssetBrowserWindow.PartsHidden", _hiddenSources.Count));
        on.AddRange(DescribeEab());
        return on;
    }

    // ---------------------------------------------------------------- regular expressions

    private bool RegexMode => RegexToggle.IsChecked == true;

    /// <summary>Reads both boxes as regular expressions when that's on, marking one that isn't valid; false when one isn't.</summary>
    private bool ReadRegexes()
    {
        _nameRegex = _folderRegex = null;
        if (!RegexMode)
        {
            SearchBox.ClearValue(Control.BorderBrushProperty);
            FolderBox.ClearValue(Control.BorderBrushProperty);
            return true;
        }
        bool ok = Parse(SearchBox, out _nameRegex);
        return Parse(FolderBox, out _folderRegex) && ok;

        bool Parse(TextBox box, out Regex? regex)
        {
            regex = null;
            box.ClearValue(Control.BorderBrushProperty);
            if (box.Text.Length == 0) return true;
            try
            {
                regex = new Regex(box.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                return true;
            }
            catch (ArgumentException ex)
            {
                box.BorderBrush = (System.Windows.Media.Brush)FindResource("SystemFillColorCriticalBrush");
                StatusText.Text = L.T("AssetBrowserWindow.IsntRegularExpression", box.Text, ex.Message);
                return false;
            }
        }
    }

    private void Regex_Click(object sender, RoutedEventArgs e)
    {
        _settings.AssetSearchRegex = RegexMode;
        _settings.Save();
        ApplySearchHints();
        RefreshView();
    }

    /// <summary>Says in the boxes what they match, which depends on whether they're regular expressions.</summary>
    private void ApplySearchHints()
    {
        SearchPlaceholder.Text = RegexMode ? L.T("AssetBrowserWindow.FileNameRegularExpression") : L.T("AssetBrowserWindow.SearchGamesFilesCtrl");
        SearchBox.ToolTip = RegexMode ? L.T("AssetBrowserWindow.RegularExpressionFilesName")
            : L.T("AssetBrowserWindow.WordsMustAllAppear");
        FolderPlaceholder.Text = RegexMode ? L.T("AssetBrowserWindow.FolderRegularExpression") : L.T("Common.Folder");
        FolderBox.ToolTip = RegexMode ? L.T("AssetBrowserWindow.RegularExpressionFilesFolder")
            : L.T("AssetBrowserWindow.WordsMustAllAppear2");
    }

    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FolderPlaceholder.Visibility = FolderBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    // ---------------------------------------------------------------- parts of the game

    private void Sources_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SourcesButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        // how many of the folder's files each part has, the others' filters aside
        var folder = FolderTree.SelectedItem as FolderNode ?? _root;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in folder?.AllFiles() ?? [])
            if (SourceOf(item) is { } source) counts[source] = counts.GetValueOrDefault(source) + 1;
        if (counts.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = L.T("AssetBrowserWindow.FilesHereModsNot"), IsEnabled = false });
            menu.IsOpen = true;
            return;
        }
        var all = new MenuItem { Header = L.T("AssetBrowserWindow.ShowEveryPart"), Icon = Glyph(""), IsEnabled = _hiddenSources.Count > 0 };
        all.Click += (_, _) => SetHiddenSources([]);
        var only = new MenuItem { Header = L.T("AssetBrowserWindow.BaseGameOnly"), Icon = Glyph("") };
        only.Click += (_, _) => SetHiddenSources(counts.Keys.Where(k => k != BaseSource));
        var noDlc = new MenuItem { Header = L.T("AssetBrowserWindow.ExpansionsOnly"), ToolTip = L.T("AssetBrowserWindow.HeartsStoneBloodWine") };
        noDlc.Click += (_, _) => SetHiddenSources(counts.Keys.Where(k => k is not ("ep1" or "bob")));
        menu.Items.Add(all);
        menu.Items.Add(only);
        menu.Items.Add(noDlc);
        menu.Items.Add(new Separator());

        var free = new MenuItem { Header = L.T("AssetBrowserWindow.FreeDLC") };
        foreach (var (source, count) in counts.OrderBy(c => SourceOrder(c.Key)))
        {
            var item = new MenuItem
            {
                Header = Counted(SourceName(source), count),
                IsCheckable = true,
                IsChecked = !_hiddenSources.Contains(source),
                StaysOpenOnClick = true,
            };
            string picked = source;
            item.Click += (_, _) =>
            {
                if (item.IsChecked) _hiddenSources.Remove(picked);
                else _hiddenSources.Add(picked);
                AfterSourcesChanged();
            };
            if (source is BaseSource or "ep1" or "bob") menu.Items.Add(item);
            else free.Items.Add(item);
        }
        if (free.Items.Count > 0)
        {
            int freeCount = counts.Where(c => c.Key is not (BaseSource or "ep1" or "bob")).Sum(c => c.Value);
            free.Header = Counted(L.T("AssetBrowserWindow.FreeDLC"), freeCount);
            var freeAll = new MenuItem { Header = L.T("AssetBrowserWindow.ShowThemAll"), StaysOpenOnClick = false };
            var freeNone = new MenuItem { Header = L.T("AssetBrowserWindow.HideThemAll"), StaysOpenOnClick = false };
            var freeSources = counts.Keys.Where(k => k is not (BaseSource or "ep1" or "bob")).ToList();
            freeAll.Click += (_, _) => SetHiddenSources(_hiddenSources.Except(freeSources, StringComparer.OrdinalIgnoreCase));
            freeNone.Click += (_, _) => SetHiddenSources(_hiddenSources.Union(freeSources, StringComparer.OrdinalIgnoreCase));
            free.Items.Insert(0, freeAll);
            free.Items.Insert(1, freeNone);
            free.Items.Insert(2, new Separator());
            menu.Items.Add(free);
        }
        menu.IsOpen = true;
    }

    private static StackPanel Counted(string text, int count)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = text });
        header.Children.Add(new TextBlock { Text = count.ToString("N0"), Opacity = 0.5, Margin = new Thickness(8, 0, 0, 0) });
        return header;
    }

    private void SetHiddenSources(IEnumerable<string> hidden)
    {
        var list = hidden.ToList();
        _hiddenSources.Clear();
        _hiddenSources.UnionWith(list);
        AfterSourcesChanged();
    }

    private void AfterSourcesChanged()
    {
        AfterSourcesChangedQuietly();
        RefreshView();
    }

    /// <summary>Says on the button which parts of the game are hidden, without listing the files again.</summary>
    private void AfterSourcesChangedQuietly()
    {
        SourcesText.Text = _hiddenSources.Count == 0 ? L.T("AssetBrowserWindow.WholeGame") : L.T("AssetBrowserWindow.Hidden", _hiddenSources.Count);
        SourcesButton.ToolTip = _hiddenSources.Count == 0 ? L.T("AssetBrowserWindow.ShowOnlySomeParts")
            : L.T("AssetBrowserWindow.Hidden2", string.Join(", ", _hiddenSources.OrderBy(SourceOrder).Select(SourceName)));
    }

    // ---------------------------------------------------------------- recent files

    private const int RecentKept = 30;

    /// <summary>Puts a file at the top of the files opened recently.</summary>
    private void RememberOpened(string path)
    {
        _settings.AssetRecent.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.AssetRecent.Insert(0, path);
        if (_settings.AssetRecent.Count > RecentKept) _settings.AssetRecent.RemoveRange(RecentKept, _settings.AssetRecent.Count - RecentKept);
        _settings.Save();
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RecentButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (_settings.AssetRecent.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = L.T("AssetBrowserWindow.NoFilesOpenedYet"), IsEnabled = false });
            menu.IsOpen = true;
            return;
        }
        foreach (string recent in _settings.AssetRecent)
        {
            string path = recent;
            menu.Items.Add(BookmarkMenuItem(path, () => _ = GoToBookmarkAsync(path)));
        }
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = L.T("AssetBrowserWindow.ClearList"), Icon = Glyph("") };
        clear.Click += (_, _) =>
        {
            _settings.AssetRecent.Clear();
            _settings.Save();
        };
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }
}
