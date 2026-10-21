using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Core;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// EasyAssetBrowser's categories and tags in the asset browser, credited to it wherever they show ("EAB category", "EAB
/// tags"): filters for both, columns, and a way to suggest tags to it on GitHub.
/// </summary>
public partial class AssetBrowserWindow
{
    private string? _eabCategory;
    private readonly HashSet<string> _eabInclude = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _eabExclude = new(StringComparer.OrdinalIgnoreCase);
    private bool _fillingEab;

    private static EasyAssetBrowser? Eab => EasyAssetBrowser.Current;

    /// <summary>The EAB category choice for files of the kinds EAB lists that it doesn't list yet.</summary>
    private const string NotInEab = "Not in EAB";

    /// <summary>Reads EasyAssetBrowser's lists (from the cache, or GitHub once a day or when <paramref name="refresh"/>) and shows them.</summary>
    private async Task LoadEabAsync(bool refresh)
    {
        if (!_settings.EasyAssetBrowserOn) return;
        if (Eab is null || refresh) EabStatus = "Reading EasyAssetBrowser's tags…";
        ApplyEab();
        await EasyAssetBrowser.LoadAsync(refresh);
        EabStatus = null;
        ApplyEab();
        RefreshView();
    }

    private string? EabStatus { get; set; }

    /// <summary>Shows or hides the EAB filters and columns, as it's on and its lists are read.</summary>
    private void ApplyEab()
    {
        bool on = _settings.EasyAssetBrowserOn;
        // while it's off, one button that asks to turn it on
        EabPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        EabOffButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        EabCategoryColumn.Visibility = EabTagsColumn.Visibility = on && Eab is not null ? Visibility.Visible : Visibility.Collapsed;
        EabCategoryBox.IsEnabled = EabTagsButton.IsEnabled = on && Eab is not null;
        EabButton.ToolTip = Eab is { } eab
            ? $"Categories and tags from EasyAssetBrowser by Vincent.02: {eab.Count:N0} assets, {eab.Note}, {eab.Read:g}"
            : EabStatus ?? "EasyAssetBrowser's tags couldn't be read. Click to try again.";
        int tagCount = _eabInclude.Count + _eabExclude.Count;
        EabTagsText.Text = tagCount == 0 ? "EAB tags" : $"EAB tags ({tagCount})";
    }

    /// <summary>Whether the EAB category and tags picked leave a file, or null when none is picked.</summary>
    private Func<EntryItem, bool>? EabFilter(bool withCategory)
    {
        if (!_settings.EasyAssetBrowserOn || Eab is not { } eab) return null;
        string? category = withCategory ? _eabCategory : null;
        if (category is null && _eabInclude.Count == 0 && _eabExclude.Count == 0) return null;
        var include = _eabInclude.ToList();
        var exclude = _eabExclude.ToList();
        return i =>
        {
            var asset = eab.Find(i.Path);
            if (category == NotInEab) { if (asset is not null || EasyAssetBrowser.CategoryFor(i.Path) is null || ItemForPath(i.Path) is null) return false; }
            else if (category is not null && asset?.Category != category) return false;
            if (include.Count > 0 && (asset is null || !include.All(t => asset.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))) return false;
            return exclude.Count == 0 || asset is null || !exclude.Any(t => asset.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
        };
    }

    private IEnumerable<string> DescribeEab()
    {
        if (!_settings.EasyAssetBrowserOn || Eab is null) yield break;
        if (_eabCategory == NotInEab) yield return "not in EAB yet";
        else if (_eabCategory is not null) yield return $"EAB category {_eabCategory}";
        if (_eabInclude.Count > 0) yield return "EAB tags " + string.Join(" + ", _eabInclude.Order(StringComparer.OrdinalIgnoreCase));
        if (_eabExclude.Count > 0) yield return "no EAB tag " + string.Join(" or ", _eabExclude.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Lists the EAB categories with how many of the files the other filters leave each has, keeping the one picked.</summary>
    private void FillEabCategories(IEnumerable<EntryItem> leftByOthers)
    {
        if (Eab is not { } eab) return;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int any = 0, missing = 0;
        foreach (var item in leftByOthers)
        {
            any++;
            if (eab.Find(item.Path) is { } asset) counts[asset.Category] = counts.GetValueOrDefault(asset.Category) + 1;
            // the game's own files only: a mod's can't be suggested
            else if (EasyAssetBrowser.CategoryFor(item.Path) is not null && ItemForPath(item.Path) is not null) missing++;
        }
        // EAB's own order of its categories
        var choices = new List<ClassChoice> { new(null, any) };
        choices.AddRange(eab.Categories.Select(c => new ClassChoice(c.Name, counts.GetValueOrDefault(c.Name))));
        choices.AddRange(counts.Keys.Where(k => eab.Categories.All(c => c.Name != k)).Order().Select(k => new ClassChoice(k, counts[k])));
        // the kinds of file EAB lists that it doesn't list yet, to suggest adding
        choices.Add(new ClassChoice(NotInEab, missing));
        _fillingEab = true;
        try
        {
            EabCategoryBox.ItemsSource = choices;
            EabCategoryBox.SelectedItem = choices.FirstOrDefault(c => c.Class == _eabCategory) ?? choices[0];
        }
        finally
        {
            _fillingEab = false;
        }
    }

    private void EabCategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingEab || EabCategoryBox.SelectedItem is not ClassChoice choice || choice.Class == _eabCategory) return;
        _eabCategory = choice.Class;
        RefreshView();
    }

    /// <summary>
    /// The EAB tags, each clicked through showing only files with it, leaving out files with it, and neither, as
    /// EasyAssetBrowser's include and exclude lists do; with how many of the files listed have each.
    /// </summary>
    private void EabTags_Click(object sender, RoutedEventArgs e)
    {
        if (Eab is not { } eab) return;
        var menu = new ContextMenu { PlacementTarget = EabTagsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 640 };
        menu.Items.Add(new MenuItem { Header = "Click a tag to show only files with it, again to leave out files with it, and again to stop.", IsEnabled = false });
        var clear = new MenuItem { Header = "Clear the tag filters", Icon = Glyph(""), IsEnabled = _eabInclude.Count + _eabExclude.Count > 0 };
        clear.Click += (_, _) =>
        {
            _eabInclude.Clear();
            _eabExclude.Clear();
            ApplyEab();
            RefreshView();
        };
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _shown)
            if (eab.Find(item.Path) is { } asset)
                foreach (string tag in asset.Tags) counts[tag] = counts.GetValueOrDefault(tag) + 1;
        // the category's own tags when one is picked, otherwise all of them
        var tags = (_eabCategory is not null && eab.Categories.FirstOrDefault(c => c.Name == _eabCategory) is { } category ? category.Tags : eab.Tags)
            .Concat(_eabInclude).Concat(_eabExclude).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (string tag in tags)
        {
            var item = new MenuItem { StaysOpenOnClick = true };
            string name = tag;
            void Show()
            {
                bool include = _eabInclude.Contains(name), exclude = _eabExclude.Contains(name);
                item.Header = Counted(exclude ? $"not {name}" : name, counts.GetValueOrDefault(name));
                item.Icon = include ? Glyph("") : exclude ? Glyph("") : null;
                item.FontWeight = include || exclude ? FontWeights.SemiBold : FontWeights.Normal;
            }
            Show();
            item.Click += (_, _) =>
            {
                if (_eabInclude.Remove(name)) _eabExclude.Add(name);
                else if (!_eabExclude.Remove(name)) _eabInclude.Add(name);
                Show();
                ApplyEab();
                RefreshView();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>What EasyAssetBrowser is, where its tags come from, and the ways to use it: its site, its repository, suggesting tags, reading them again.</summary>
    private void EabMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = EabButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        string about = Eab is { } eab
            ? $"EAB categories and tags come from EasyAssetBrowser by Vincent.02: {eab.Count:N0} assets, {eab.Note} ({eab.Read:g})."
            : "EAB categories and tags come from EasyAssetBrowser by Vincent.02. They haven't been read yet.";
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = about, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("View EAB details of the highlighted files…", "\uE946", EabDetails_Click, Eab is not null && Highlighted().Count > 0));
        menu.Items.Add(Item("Open EasyAssetBrowser", "", (_, _) => OpenLink(EasyAssetBrowser.Site)));
        menu.Items.Add(Item("Open its GitHub repository", "", (_, _) => OpenLink(EasyAssetBrowser.Repository)));
        menu.Items.Add(Item("Read the tags again from GitHub", "", (_, _) => _ = LoadEabAsync(true), _settings.EasyAssetBrowserOn));
        menu.Items.Add(new Separator());
        var links = new MenuItem
        {
            Header = "Open files from EasyAssetBrowser's site here",
            IsCheckable = true,
            IsChecked = AppLinks.IsRegistered,
            ToolTip = "Lets a link on EasyAssetBrowser's site (w3modworkbench://…) open the file in this Asset Browser. Windows asks the first time.",
        };
        links.Click += (_, _) => SiteLinks_Click(links.IsChecked);
        menu.Items.Add(links);
        menu.Items.Add(Item("Privacy notice", "", (_, _) => SettingsWindow.ShowPrivacy(this)));
        menu.Items.Add(Item("Turn EasyAssetBrowser tags off", "", (_, _) => TurnEabOff(deleteLists: false)));
        menu.Items.Add(Item("Turn them off and delete the downloaded lists", "", (_, _) => TurnEabOff(deleteLists: true)));
        menu.IsOpen = true;

        MenuItem Item(string header, string glyph, RoutedEventHandler click, bool enabled = true)
        {
            var item = new MenuItem { Header = header, Icon = Glyph(glyph), IsEnabled = enabled };
            item.Click += click;
            return item;
        }
    }

    /// <summary>The button EasyAssetBrowser shows as while it's off: asks to turn it on, saying what that means.</summary>
    private void EabOff_Click(object sender, RoutedEventArgs e)
    {
        if (!EabConsentWindow.Ask(this)) return;
        ApplyEab();
        _ = LoadEabAsync(false);
    }

    /// <summary>Settings changed what the asset browser asks and whether EasyAssetBrowser's tags are on: the open one follows.</summary>
    public static void SettingsChanged()
    {
        if (_instance is not { } browser) return;
        browser.AskToggle.IsChecked = browser._settings.AskWhichCopy;
        if (!browser._settings.EasyAssetBrowserOn)
        {
            browser._eabCategory = null;
            browser._eabInclude.Clear();
            browser._eabExclude.Clear();
            browser.ApplyEab();
            browser.RefreshView();
        }
        else
        {
            browser.ApplyEab();
            _ = browser.LoadEabAsync(false);
        }
    }

    /// <summary>Turns EasyAssetBrowser's tags off, so the tool doesn't go online, and when asked deletes the lists it downloaded.</summary>
    private void TurnEabOff(bool deleteLists)
    {
        _settings.EasyAssetBrowserOn = false;
        _settings.Save();
        _eabCategory = null;
        _eabInclude.Clear();
        _eabExclude.Clear();
        EasyAssetBrowser.Forget(deleteLists);
        ApplyEab();
        RefreshView();
    }

    /// <summary>Opens the EAB window for the files highlighted: what EasyAssetBrowser has for them, and their tags to suggest.</summary>
    private void EabDetails_Click(object sender, RoutedEventArgs e)
    {
        if (Eab is not { } eab) return;
        var files = Highlighted();
        if (files.Count == 0) return;
        EabWindow.Show(this, eab, files, OriginOf, NotTheGames);
    }

    /// <summary>
    /// Which part of the game a file is from, as EasyAssetBrowser names it, from the game's own copy (a mod's copy of a
    /// game file is the game's file); REDkit's by its folder.
    /// </summary>
    private string OriginOf(EntryItem item)
    {
        var own = ItemForPath(item.Path);
        if (own is not null && SourceOf(own) is { } source) return EabOrigin(source);
        // REDkit's copy: dlc\<name>\ is that DLC's, anything else the base game's
        string path = item.Path;
        return path.StartsWith("dlc\\", StringComparison.OrdinalIgnoreCase) && path.IndexOf('\\', 4) is > 4 and var end
            ? EabOrigin(path[4..end].ToLowerInvariant()) : "BaseGame";
    }

    /// <summary>
    /// Why a file can't be suggested to EasyAssetBrowser, or null when it can. Only the game's own files are its to list:
    /// content0, the official DLC, and REDkit's copies of them. A file only a mod has never is, wherever it's opened from.
    /// </summary>
    private string? NotTheGames(EntryItem item)
    {
        if (ItemForPath(item.Path) is not null) return null;
        string mod = item.Entry.Archive is Core.RedkitDepot ? "" : $" ({item.Entry.Archive.Owner})";
        return $"Only a mod{mod} has this file; the game and its official DLC don't. EasyAssetBrowser lists only the game's own files, so it can't be suggested.";
    }

    /// <summary>A part of the game as EasyAssetBrowser names it: BaseGame, HoS, BnW, dlc5.</summary>
    private static string EabOrigin(string source) => source switch
    {
        BaseSource => "BaseGame",
        "ep1" => "HoS",
        "bob" => "BnW",
        _ => source,
    };

    // ---------------------------------------------------------------- links from EasyAssetBrowser's site

    private AppLink? _pendingLink;

    /// <summary>Opens the asset browser at a file a link names (<see cref="AppLinks"/>), highlighted, and opens it when the link says to.</summary>
    public static void ShowLink(MainWindow main, AppLink link)
    {
        Open(main);
        if (_instance is { } browser) _ = browser.ShowLinkAsync(link);
    }

    private async Task ShowLinkAsync(AppLink link)
    {
        // the game's files are still being read: once they are
        if (FolderTree.ItemsSource is null)
        {
            _pendingLink = link;
            return;
        }
        Activate();
        await GoToBookmarkAsync(link.Path);
        if (link.Open && _shown.FirstOrDefault(i => i.Path.Equals(link.Path, StringComparison.OrdinalIgnoreCase)) is { } file) OpenItem(file);
    }

    private async Task ShowPendingLinkAsync()
    {
        if (_pendingLink is not { } link) return;
        _pendingLink = null;
        await ShowLinkAsync(link);
    }

    /// <summary>Has Windows open the tool's links (as EasyAssetBrowser's site has) with this copy of the tool, or stops it.</summary>
    private void SiteLinks_Click(bool on)
    {
        try
        {
            if (on) AppLinks.Register();
            else AppLinks.Unregister();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException)
        {
            MessageBox.Show(this, $"Windows wouldn't let the tool {(on ? "register" : "remove")} its links: {ex.Message}", "W3 Mod Workbench", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyAppLink_Click(object sender, RoutedEventArgs e)
    {
        if (Highlighted() is { Count: > 0 } items) Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => AppLinks.For(i.Path))));
    }

    internal static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"The browser couldn't be opened: {ex.Message}\n\n{url}", "W3 Mod Workbench", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
