using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Items;
using W3ModWorkbench.Core.Loot;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.Localisation;
using Vector2 = System.Numerics.Vector2;

namespace W3ModWorkbench;

/// <summary>A row of the Loot Locator's lists: an item or a loot table, how many places can give it, and a warning for a table the game doesn't define.</summary>
public sealed record LootFoundRow(string Key, string Title, string Detail, int Count, bool IsTable, string? Warning = null)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
    public Visibility WarnVisibility => Warning is null ? Visibility.Collapsed : Visibility.Visible;
    public string? WarnTip => Warning;
}

/// <summary>A place that can give what was looked for: how likely at the level chosen, whether the game's roll for that exact spot gives it, and its respawn time.</summary>
public sealed record LootPlaceRow(LootSpot Spot, string Title, string Area, double Chance, bool? Predicted, uint? Respawn)
{
    public string ChanceText => LootLocatorWindow.Percent(Chance);
    public string PredictedText => Predicted switch { true => L.T("LootLocator.Yes"), false => L.T("LootLocator.No"), _ => "" };
    public int PredictedSort => Predicted switch { true => 1, false => 0, _ => -1 };
    public string RespawnText => Respawn is { } r && r > 0 ? r.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string PositionText => LootLocatorWindow.Position(Spot.Position);
}

/// <summary>An item of the loot table looked at: how often it comes when the table is used, its weight, how many and the levels it's for.</summary>
public sealed record LootTableItemRow(string Title, string Name, double Chance, float Weight, string QuantityText, string LevelsText)
{
    /// <summary>How many times the table lists the item.</summary>
    public int Listed { get; init; } = 1;

    public string ChanceText => LootLocatorWindow.Percent(Chance);
    public string WeightText
    {
        get
        {
            string weight = Weight == -1f ? L.T("LootLocator.WeightFirst") : Weight.ToString("0.##", CultureInfo.CurrentCulture);
            return Listed > 1 ? L.T("LootLocator.Listed", weight, Listed) : weight;
        }
    }
}

/// <summary>
/// The Loot Locator: where in the game's worlds an item or a loot table can be found, and how likely, on the game's own
/// map. It reads every placed container, herb and hive from the worlds' layers with the loot tables their templates
/// roll, and works the chances out with the game's roll re-created from witcher3.exe (<see cref="LootRoll"/>).
/// </summary>
public partial class LootLocatorWindow : Window, IMessageHost
{
    public const int MaxLevel = 100;

    private static LootLocatorWindow? _open;

    /// <summary>A world's map: the whole of it in one picture, and the game's minimap it's from (null for a world shown by its relief).</summary>
    private sealed record WorldPicture(BitmapSource Picture, Vector2 Corner, float Size, GameMinimap? Minimap);
    private sealed record WorldChoice(string Path, string Text)
    {
        public override string ToString() => Text;
    }
    private sealed record LegendRow(Brush Brush, string Text);
    private sealed record Setup(List<LootSpot> Spots, HashSet<string> Tables, HashSet<string> Items);

    private ResourceLocator _locator = null!;
    private ItemDatabase? _db;
    private Dictionary<uint, string> _names = [];
    private LootIndex? _index;
    private LootRoll? _roll;
    private Dictionary<string, Setup> _setups = [];
    private Dictionary<string, List<string>> _itemSetups = new(StringComparer.OrdinalIgnoreCase), _tableSetups = new(StringComparer.OrdinalIgnoreCase);
    private List<LootFoundRow> _itemRows = [], _tableRows = [];
    private readonly Dictionary<string, Task<WorldPicture?>> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private string? _shownWorld;
    private readonly MinimapDetail _detail;
    private LootFoundRow? _selected;
    private List<LootPlaceRow> _places = [];
    private int _version;
    private bool _loading = true;
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private LootLocatorWindow()
    {
        InitializeComponent();
        _detail = new MinimapDetail(Map);
        Body.Visibility = Visibility.Hidden;
        LevelBox.ItemsSource = Enumerable.Range(1, MaxLevel).ToList();
        LevelBox.SelectedItem = Math.Clamp(App.Settings.LootPlayerLevel, 1, MaxLevel);
        ModsCheck.IsChecked = App.Settings.LootWithMods;
        PredictedCheck.IsChecked = App.Settings.LootShowPredicted;
        PredictedColumn.Visibility = App.Settings.LootShowPredicted ? Visibility.Visible : Visibility.Collapsed;
        LegendPredicted.Visibility = PredictedColumn.Visibility;
        LegendList.ItemsSource = Bands.Select(b => new LegendRow(Frozen(new SolidColorBrush(b.Color)), b.Text())).ToList();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Filter();
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    public static void Open(Window owner)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _open = new LootLocatorWindow().Remembered(owner, "LootLocator");
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    /// <summary>
    /// Opens the window on an item (by its name in the files), as if looked for: its name in the search box, a player
    /// level it can be found at, and the world asked for when it's found there. At once when the window is read, else once it is.
    /// </summary>
    public static void OpenItem(Window owner, string item, string? world = null)
    {
        Open(owner);
        if (_open is not { } window) return;
        window._preferWorld = world;
        if (window._loading) window._pendingItem = item;
        else window.Select(item, false, true);
    }

    private string? _pendingItem, _preferWorld;

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private uint Level => LevelBox.SelectedItem is int level ? (uint)level : 1;

    // ---------------------------------------------------------------- reading

    private async Task LoadAsync()
    {
        string? game = GameLocator.Resolve(null);
        if (game is null)
        {
            BusyText.Text = L.T("LootLocator.NoGame");
            return;
        }
        _loading = true;
        Body.Visibility = Visibility.Hidden;
        Busy.Visibility = Visibility.Visible;
        _locator = new ResourceLocator([], null, game, otherMods: ModsCheck.IsChecked == true);
        _pictures.Clear();
        _shownWorld = null;
        try
        {
            var progress = new Progress<string>(what => BusyText.Text = L.T("LootLocator.ReadingWhat", what));
            var locator = _locator;
            _db ??= await Task.Run(() => ItemDatabase.Load(game, progress));
            if (_names.Count == 0)
            {
                var strings = await GameStringIndex.GetAsync(game, App.GameLanguage, dlcAndMods: false);
                _names = strings.All.GroupBy(s => s.KeyHash).ToDictionary(g => g.Key, g => g.First().Text);
            }
            var db = _db;
            (_index, _roll) = await Task.Run(() => (WithMonsters(LootIndex.Build(locator, progress, CancellationToken.None), locator, game, progress), LootRoll.FromDatabase(db)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BusyText.Text = L.T("LootLocator.CouldNotRead", ex.Message);
            return;
        }
        BuildLookups();
        Busy.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        _loading = false;
        StatusText.Text = L.T("LootLocator.Summary", _index.Spots.Count, _index.Worlds.Count, _roll.Tables.Count)
            + (_index.Problems.Count > 0 ? "  ·  " + L.T("LootLocator.SomeUnread", _index.Problems.Count) : "");
        Filter();
        if (_pendingItem is { } pending)
        {
            _pendingItem = null;
            Select(pending, false, true);
        }
        else if (_selected is { } again) Select(again.Key, again.IsTable);
        else ItemSearch.Focus();
    }

    /// <summary>
    /// The places with the monsters added: every encounter that spawns a creature with drops is a place of its own, its
    /// loot the creature's, rolled again at each kill.
    /// </summary>
    private static LootIndex WithMonsters(LootIndex index, ResourceLocator locator, string game, IProgress<string> progress)
    {
        var bestiary = Core.Bestiary.BestiaryIndex.Build(locator, Core.Scripts.ScriptCorpus.GameScriptsFolder(game), progress, CancellationToken.None);
        var monsters = bestiary.Places.Where(p => bestiary.Creatures.TryGetValue(p.Template, out var c) && c.Loot is { Params.Count: > 0 })
            .Select(p => bestiary.Creatures[p.Template].Loot! with { World = p.World, Layer = p.Layer, ClassName = "CEncounter", Name = "", Position = p.Position });
        return new LootIndex { Spots = [.. index.Spots, .. monsters], Worlds = index.Worlds, Problems = index.Problems };
    }

    /// <summary>The places by setup (the same loot whatever the place), and which setups can give each item and use each table.</summary>
    private void BuildLookups()
    {
        var roll = _roll!;
        _setups = _index!.Spots.GroupBy(s => s.SetupKey).ToDictionary(g => g.Key, g =>
        {
            var tables = g.First().Params.SelectMany(p => p.Tables.Select(t => t.Table)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = tables.SelectMany(t => roll.Tables.TryGetValue(t, out var def) ? def.Items.Select(i => i.Name) : [])
                .Concat(g.First().Fixed.Where(f => f.Lootable && f.Item.Length > 0).Select(f => f.Item)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new Setup([.. g], tables, items);
        });
        _itemSetups = new(StringComparer.OrdinalIgnoreCase);
        _tableSetups = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, setup) in _setups)
        {
            foreach (string item in setup.Items) (_itemSetups.TryGetValue(item, out var list) ? list : _itemSetups[item] = []).Add(key);
            foreach (string table in setup.Tables) (_tableSetups.TryGetValue(table, out var list) ? list : _tableSetups[table] = []).Add(key);
        }
        _itemRows = [.. _itemSetups.Select(p =>
        {
            string title = NameOf(p.Key) ?? p.Key;
            // the name in the files when the game shows another; else how many tables give it
            int tables = roll.Tables.Values.Count(d => d.Items.Any(i => i.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase)));
            string detail = !title.Equals(p.Key, StringComparison.OrdinalIgnoreCase) ? p.Key
                : NameOf(p.Key) is null ? L.T("LootLocator.ItemDetailPlain") : L.T("LootLocator.InTables", tables);
            return new LootFoundRow(p.Key, title, detail, p.Value.Sum(k => _setups[k].Spots.Count), false);
        }).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)];
        _tableRows = [.. roll.Tables.Values.Select(t => new LootFoundRow(t.Name, t.Name, L.T("LootLocator.TableDetail", t.Items.Count, Path.GetFileName(t.GamePath)),
                _tableSetups.TryGetValue(t.Name, out var keys) ? keys.Sum(k => _setups[k].Spots.Count) : 0, true))
            .Concat(_tableSetups.Keys.Where(t => !roll.Tables.ContainsKey(t)).Select(t => new LootFoundRow(t, t.Length == 0 ? L.T("LootLocator.NoName") : t,
                L.T("LootLocator.TableUndefined"), _tableSetups[t].Sum(k => _setups[k].Spots.Count), true, L.T("LootLocator.TableUndefinedTip"))))
            .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>An item's name in the game's language, from its definition's localisation key, or null.</summary>
    private string? NameOf(string item) =>
        _db?.Find(DefKind.Item, item)?.Attribute("localisation_key_name") is { Length: > 0 } key && _names.TryGetValue(W3Strings.HashKey(key), out var text) && text.Length > 0
            ? text : null;

    private string Display(string item) => NameOf(item) is { } name && !name.Equals(item, StringComparison.OrdinalIgnoreCase) ? $"{name} ({item})" : item;

    // ---------------------------------------------------------------- the lists

    private bool TableMode => ModeTabs.SelectedItem == TablesTab;

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        ItemSearchHint.Visibility = ItemSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TableSearchHint.Visibility = TableSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void UnusedCheck_Click(object sender, RoutedEventArgs e) => Filter();

    private void Filter()
    {
        if (_loading) return;
        static bool Matches(LootFoundRow r, string text) => text.Length == 0
            || r.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || r.Key.Contains(text, StringComparison.OrdinalIgnoreCase);
        string items = ItemSearch.Text.Trim(), tables = TableSearch.Text.Trim();
        var itemRows = _itemRows.Where(r => Matches(r, items)).ToList();
        var tableRows = _tableRows.Where(r => (UnusedCheck.IsChecked == true || r.Count > 0) && Matches(r, tables)).ToList();
        ItemList.ItemsSource = itemRows;
        TableList.ItemsSource = tableRows;
        ItemCount.Text = L.T("LootLocator.ItemCount", itemRows.Count);
        TableCount.Text = L.T("LootLocator.TableCount", tableRows.Count);
        if (_selected is { IsTable: false } si) ItemList.SelectedItem = itemRows.FirstOrDefault(r => r.Key.Equals(si.Key, StringComparison.OrdinalIgnoreCase));
        if (_selected is { IsTable: true } st) TableList.SelectedItem = tableRows.FirstOrDefault(r => r.Key.Equals(st.Key, StringComparison.OrdinalIgnoreCase));
    }

    private void ModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != ModeTabs || _loading) return;
        var list = TableMode ? TableList : ItemList;
        if (list.SelectedItem is LootFoundRow row && row != _selected) _ = ShowAsync(row);
    }

    private void FoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: LootFoundRow row } && row != _selected) _ = ShowAsync(row);
    }

    /// <summary>
    /// Picks an item or a table in its list, switching to it and clearing a search that hides it; <paramref name="find"/>
    /// fills the search box with its name instead, and moves the player level to one it can be found at.
    /// </summary>
    private void Select(string key, bool table, bool find = false)
    {
        var rows = table ? _tableRows : _itemRows;
        if (rows.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is not { } row) return;
        ModeTabs.SelectedItem = table ? TablesTab : ItemsTab;
        var list = table ? TableList : ItemList;
        var search = table ? TableSearch : ItemSearch;
        if (find)
        {
            search.Text = row.Title;
            _searchTimer.Stop();
            Filter();
            if (!table && LowestLevel(row.Key) is { } lowest && Level < lowest) LevelBox.SelectedItem = (int)lowest;
        }
        else if (list.ItemsSource is not IEnumerable<LootFoundRow> shown || !shown.Contains(row))
        {
            search.Text = "";
            if (table && row.Count == 0) UnusedCheck.IsChecked = true;
            _searchTimer.Stop();
            Filter();
        }
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        _ = ShowAsync(row);
    }

    /// <summary>The lowest player level an item can be found at: the lowest of its tables' and its own level limits where a place can give it.</summary>
    private uint? LowestLevel(string item)
    {
        if (_roll is not { } roll || !_itemSetups.TryGetValue(item, out var keys)) return null;
        uint? lowest = null;
        foreach (string table in keys.SelectMany(k => _setups[k].Tables).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!roll.Tables.TryGetValue(table, out var def)) continue;
            foreach (var i in def.Items.Where(i => i.Name.Equals(item, StringComparison.OrdinalIgnoreCase) && (i.Chance == -1f || i.Chance > 0)))
            {
                uint at = Math.Max(1, Math.Max(def.LevelMin, i.LevelMin));
                if (lowest is null || at < lowest) lowest = at;
            }
        }
        return lowest;
    }

    // ---------------------------------------------------------------- what was picked

    private void LevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LevelBox.SelectedItem is int level && level != App.Settings.LootPlayerLevel)
        {
            App.Settings.LootPlayerLevel = level;
            App.Settings.Save();
        }
        if (!_loading && _selected is { } row) _ = ShowAsync(row);
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } row) _ = ShowAsync(row);
    }

    private async void ModsCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.LootWithMods = ModsCheck.IsChecked == true;
        App.Settings.Save();
        await LoadAsync();
    }

    private void PredictedCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.LootShowPredicted = PredictedCheck.IsChecked == true;
        App.Settings.Save();
        PredictedColumn.Visibility = App.Settings.LootShowPredicted ? Visibility.Visible : Visibility.Collapsed;
        LegendPredicted.Visibility = PredictedColumn.Visibility;
        if (_selected is { } row) _ = ShowAsync(row);
    }

    private bool KindShown(LootKind kind) => kind switch
    {
        LootKind.Container => ContainersCheck.IsChecked == true,
        LootKind.Herb => HerbsCheck.IsChecked == true,
        LootKind.Beehive => BeehivesCheck.IsChecked == true,
        LootKind.Monster => MonstersCheck.IsChecked == true,
        _ => OtherCheck.IsChecked == true,
    };

    /// <summary>Works out every place's chance of what was picked, at the level chosen, then shows them on the map and in the list.</summary>
    private async Task ShowAsync(LootFoundRow row)
    {
        _selected = row;
        if (_roll is not { } roll) return;
        int version = ++_version;
        uint level = Level;
        bool predicted = App.Settings.LootShowPredicted;
        var lookup = row.IsTable ? _tableSetups : _itemSetups;
        var keys = lookup.TryGetValue(row.Key, out var k) ? k : [];
        var spots = keys.SelectMany(key => _setups[key].Spots).Where(s => KindShown(s.Kind)).ToList();
        MapProgress.Text = L.T("LootLocator.WorkingOut", spots.Count);
        MapProgress.Visibility = Visibility.Visible;
        string name = row.Key;
        bool table = row.IsTable;
        var rows = await Task.Run(() =>
        {
            Parallel.ForEach(spots.DistinctBy(s => s.SetupKey), s => roll.Odds(s, level));
            return spots.AsParallel().AsOrdered().Select(s =>
            {
                var odds = roll.Odds(s, level);
                double chance = table ? odds.Tables.GetValueOrDefault(name) : odds.Items.GetValueOrDefault(name);
                // what the game's roll gives this spot: its respawn time is the game's own for it (see LootRoll.Roll)
                var rolled = roll.Roll(s, level, LootRoll.Seed(s.Position));
                var hit = rolled.FirstOrDefault(i => table ? i.Table.Equals(name, StringComparison.OrdinalIgnoreCase) : i.Item.Equals(name, StringComparison.OrdinalIgnoreCase));
                // a seed of 0 is no seed: the game rolls that spot at random each time, so nothing is fixed there
                // a monster's drops are rolled at each kill, where it falls: nothing is fixed at its encounter
                bool? given = predicted && s.Kind != LootKind.Monster && LootRoll.Seed(s.Position) != 0 ? hit is not null : null;
                return new LootPlaceRow(s, Humanise(s.Title), AreaOf(s), chance, given, s.Kind == LootKind.Monster ? null : hit is { Table.Length: > 0 } ? hit.Respawn : RespawnOf(s, name, table));
            }).ToList();
        });
        if (version != _version) return;
        MapProgress.Visibility = Visibility.Collapsed;
        var possible = rows.Where(r => r.Chance > 0 || r.Predicted == true).ToList();
        _places = possible;
        ExportButton.IsEnabled = possible.Count > 0;
        int elsewhere = rows.Count - possible.Count;
        StatusText.Text = L.T("LootLocator.Found", possible.Count, level, possible.Select(p => p.Spot.World).Distinct().Count())
            + (elsewhere > 0 ? "  ·  " + L.T("LootLocator.OtherLevels", elsewhere) : "")
            + (possible.Any(p => p.Predicted is not null) ? "  ·  " + L.T("LootLocator.PredictedCount", possible.Count(p => p.Predicted == true)) : "");
        LegendTitle.Text = table ? L.T("LootLocator.LegendTable") : L.T("LootLocator.LegendItem");
        ShowTableItems(row, level);
        // the worlds that have any, most first; the one shown kept when it has some
        var worlds = possible.GroupBy(p => p.Spot.World).OrderByDescending(g => g.Count())
            .Select(g => new WorldChoice(g.Key, L.T("LootLocator.WorldCount", WorldName(g.Key), g.Count()))).ToList();
        string? keep = _preferWorld is { } preferred && worlds.Any(w => w.Path == preferred) ? preferred
            : _shownWorld is { } shown && worlds.Any(w => w.Path == shown) ? shown : worlds.FirstOrDefault()?.Path;
        _preferWorld = null;
        _ignoreWorld = true;
        WorldBox.ItemsSource = worlds;
        WorldBox.SelectedItem = worlds.FirstOrDefault(w => w.Path == keep);
        _ignoreWorld = false;
        MapEmpty.Text = possible.Count == 0 ? L.T("LootLocator.NowhereAtLevel", level) : L.T("LootLocator.PickSomething");
        MapEmpty.Visibility = possible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Legend.Visibility = possible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        await ShowWorldAsync(keep, keep != _shownWorld);
    }

    private void ShowTableItems(LootFoundRow row, uint level)
    {
        TableItemsTab.Visibility = row.IsTable ? Visibility.Visible : Visibility.Collapsed;
        if (!row.IsTable)
        {
            if (ListTabs.SelectedItem == TableItemsTab) ListTabs.SelectedItem = PlacesTab;
            return;
        }
        if (_roll!.Tables.TryGetValue(row.Key, out var def))
        {
            var odds = _roll.TableOdds(def, level);
            TableSummary.Text = L.T("LootLocator.TableSummary", def.PicksMin == def.PicksMax ? $"{def.PicksMin}" : $"{def.PicksMin}–{def.PicksMax}", def.Items.Count, def.GamePath) // loc-ignore: numbers
                + (def.LevelMin > 0 || def.LevelMax > 0 ? "  " + L.T("LootLocator.TableLevels", Levels(def.LevelMin, def.LevelMax)) : "");
            // an item the table lists more than once is one row: its chance is already all of them together
            TableItemGrid.ItemsSource = def.Items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                var i = g.First();
                return new LootTableItemRow(NameOf(i.Name) ?? i.Name, i.Name, odds.Items.GetValueOrDefault(i.Name), i.Chance,
                    Range(i.QuantityMin, Math.Max(i.QuantityMin, i.QuantityMax)), Levels(i.LevelMin, i.LevelMax)) { Listed = g.Count() };
            }).OrderByDescending(r => r.Chance).ToList();
        }
        else
        {
            TableSummary.Text = L.T("LootLocator.TableUndefinedTip");
            TableItemGrid.ItemsSource = null;
        }
    }

    private void TableItemGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TableItemGrid.SelectedItem is LootTableItemRow row) Select(row.Name, false);
    }

    /// <summary>The respawn time of what was looked for at a place: the param's own when set, else its table's (the shortest if several give it).</summary>
    private uint? RespawnOf(LootSpot spot, string name, bool table)
    {
        uint? best = null;
        foreach (var p in spot.Params)
            foreach (var t in p.Tables)
            {
                if (!_roll!.Tables.TryGetValue(t.Table, out var def)) continue;
                if (table ? !t.Table.Equals(name, StringComparison.OrdinalIgnoreCase) : !def.Items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                uint respawn = t.Respawn is { } own && own != 0 ? own : def.Respawn;
                if (best is null || respawn < best) best = respawn;
            }
        return best;
    }

    // ---------------------------------------------------------------- the map

    private bool _ignoreWorld;

    private async void WorldBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreWorld || WorldBox.SelectedItem is not WorldChoice world) return;
        await ShowWorldAsync(world.Path, true);
    }

    private async Task ShowWorldAsync(string? world, bool fit)
    {
        var markers = _places.Where(p => p.Spot.World == world)
            .OrderBy(p => p.Chance).Select(p => new MapMarker(new Vector2(p.Spot.Position.X, p.Spot.Position.Y), BandOf(p.Chance).Color, null, 0,
                p.Predicted == true, 4.5f, p)).ToList();
        Map.SetMarkers(markers);
        Map.PickedMarker = null;
        PlaceGrid.ItemsSource = _places.Where(p => p.Spot.World == world).OrderByDescending(p => p.Chance).ThenBy(p => p.Area, StringComparer.OrdinalIgnoreCase).ToList();
        ShowDetails(null);
        if (world is null) return;
        if (world != _shownWorld)
        {
            _shownWorld = world;
            Map.SetTerrain(null, default, 0);
            MapProgress.Text = L.T("LootLocator.ReadingMap", WorldName(world));
            MapProgress.Visibility = Visibility.Visible;
            _detail.Show(null, null);
            var picture = await PictureOf(world);
            if (_shownWorld != world) return;
            MapProgress.Visibility = Visibility.Collapsed;
            if (picture is not null) Map.SetTerrain(picture.Picture, picture.Corner, picture.Size);
            _detail.Show(picture?.Minimap, _locator);
        }
        if (fit) await Dispatcher.InvokeAsync(FitToPlaces, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FitToPlaces()
    {
        var points = _places.Where(p => p.Spot.World == _shownWorld).Select(p => new Vector2(p.Spot.Position.X, p.Spot.Position.Y)).ToList();
        if (points.Count == 0)
        {
            Map.FitWorld();
            return;
        }
        var min = points.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
        var max = points.Aggregate(new Vector2(float.MinValue), Vector2.Max);
        Map.FitTo(min - new Vector2(40), max + new Vector2(40));
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToPlaces();

    /// <summary>
    /// A world's picture under the places: the game's own minimap (<see cref="GameMinimap"/>), its full-size tiles drawn
    /// as the map zooms in, or the terrain's relief for a world the game has none for. Read once a world.
    /// </summary>
    private Task<WorldPicture?> PictureOf(string worldPath)
    {
        if (_pictures.TryGetValue(worldPath, out var known)) return known;
        var locator = _locator;
        return _pictures[worldPath] = Task.Run(() =>
        {
            try
            {
                var world = WorldFile.Read(locator.ReadFile(worldPath) ?? [], worldPath);
                if (world.Terrain is not { } terrain) return null;
                if (GameMinimap.Read(locator, world, worldPath) is { } minimap) return new WorldPicture(minimap.Picture(), terrain.Corner, terrain.Size, minimap);
                var relief = WorldTerrainPictures.Load(terrain, p => locator.ReadFile(p), null, CancellationToken.None).Picture(TerrainLook.Relief);
                relief.Freeze();
                return new WorldPicture(relief, terrain.Corner, terrain.Size, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
        });
    }

    private void Map_PointerMoved(object? sender, Vector2? at) =>
        HoverText.Text = at is { } p ? L.T("LootLocator.HoverAt", p.X, p.Y) : L.T("LootLocator.MouseHint");

    private void Map_MarkerPicked(object? sender, MapMarker? marker)
    {
        if (marker?.Tag is not LootPlaceRow row) return;
        PlaceGrid.SelectedItem = row;
        PlaceGrid.ScrollIntoView(row);
        ListTabs.SelectedItem = PlacesTab;
    }

    private void PlaceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaceGrid.SelectedItem is not LootPlaceRow row)
        {
            ShowDetails(null);
            return;
        }
        var marker = Map.PickedMarker?.Tag == row ? Map.PickedMarker : null;
        if (marker is null)
        {
            // picked in the list: show it on the map, moving there if it's out of view
            Map.PickedMarker = new MapMarker(new Vector2(row.Spot.Position.X, row.Spot.Position.Y), BandOf(row.Chance).Color, null, 0, false, 4.5f, row);
            if (PlaceGrid.IsKeyboardFocusWithin) Map.FitTo(new Vector2(row.Spot.Position.X, row.Spot.Position.Y) - new Vector2(70), new Vector2(row.Spot.Position.X, row.Spot.Position.Y) + new Vector2(70));
        }
        ShowDetails(row);
    }

    // ---------------------------------------------------------------- the place picked

    /// <summary>What a place can give at the level chosen: its tables and items with their chances, what the game's roll gives it there, what it always holds.</summary>
    private void ShowDetails(LootPlaceRow? row)
    {
        Details.Children.Clear();
        DetailsEmpty.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (row is null || _roll is not { } roll) return;
        var spot = row.Spot;
        uint level = Level;
        Details.Children.Add(new TextBlock { Text = row.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Details.Children.Add(Hint(L.T("LootLocator.PlaceIn", WorldName(spot.World), row.Area)));
        Details.Children.Add(Hint(L.T("LootLocator.PlaceAt", Position(spot.Position), spot.ClassName)));
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        buttons.Children.Add(SmallButton(L.T("LootLocator.CopyPosition"), () => Clipboard.SetText(Position(spot.Position))));
        buttons.Children.Add(SmallButton(L.T("LootLocator.OpenTemplate"), () => _ = DataViewers.OpenLinkAsync(spot.Template, _locator, this, this)));
        buttons.Children.Add(SmallButton(L.T("LootLocator.OpenLayer"), () => _ = DataViewers.OpenLinkAsync(spot.Layer, _locator, this, this)));
        Details.Children.Add(buttons);

        if (spot.Kind == LootKind.Monster)
        {
            Details.Children.Add(Title(L.T("LootLocator.MonsterTitle")));
            Details.Children.Add(Hint(L.T("LootLocator.MonsterNote")));
        }
        else if (App.Settings.LootShowPredicted && LootRoll.Seed(spot.Position) == 0)
        {
            Details.Children.Add(Title(L.T("LootLocator.PredictedTitle", level)));
            Details.Children.Add(Hint(L.T("LootLocator.PredictedRandom")));
        }
        else if (App.Settings.LootShowPredicted)
        {
            Details.Children.Add(Title(L.T("LootLocator.PredictedTitle", level)));
            var rolled = roll.Roll(spot, level, LootRoll.Seed(spot.Position));
            if (rolled.Count == 0) Details.Children.Add(Hint(L.T("LootLocator.PredictedNothing")));
            foreach (var i in rolled) Details.Children.Add(ItemLine(i.Item, Range(i.QuantityMin, i.QuantityMax)));
            Details.Children.Add(Hint(L.T("LootLocator.PredictedNote")));
        }

        var odds = roll.Odds(spot, level);
        foreach (var p in spot.Params)
        {
            Details.Children.Add(Title(p.UsedMin == p.UsedMax ? L.T("LootLocator.ParamUses", p.UsedMin) : L.T("LootLocator.ParamUsesRange", p.UsedMin, p.UsedMax)));
            foreach (var t in p.Tables)
            {
                bool defined = roll.Tables.ContainsKey(t.Table);
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                var link = new Hyperlink(new Run(t.Table.Length == 0 ? L.T("LootLocator.NoName") : t.Table));
                string table = t.Table;
                link.Click += (_, _) => Select(table, true);
                line.Inlines.Add(link);
                line.Inlines.Add(Quiet("  " + (defined ? Percent(odds.Tables.GetValueOrDefault(t.Table)) : L.T("LootLocator.TableUndefined"))));
                Details.Children.Add(line);
            }
        }
        if (spot.MinParams > -1 && spot.MaxParams > -1) Details.Children.Add(Hint(L.T("LootLocator.ParamLimit", spot.MinParams, spot.MaxParams)));
        if (odds.Items.Count > 0)
        {
            Details.Children.Add(Title(L.T("LootLocator.CanGive")));
            foreach (var (item, chance) in odds.Items.OrderByDescending(p => p.Value)) Details.Children.Add(ItemLine(item, Percent(chance)));
        }
        if (spot.Fixed.Count > 0)
        {
            Details.Children.Add(Title(L.T("LootLocator.AlwaysHolds")));
            foreach (var f in spot.Fixed)
                Details.Children.Add(f.Item.Length > 0 ? ItemLine(f.Item, Range(f.Min, Math.Max(f.Min, f.Max))) : Hint(L.T("LootLocator.RandomOf", f.Category, Range(f.Min, Math.Max(f.Min, f.Max)))));
        }
    }

    private TextBlock ItemLine(string item, string note)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        var link = new Hyperlink(new Run(Display(item)));
        link.Click += (_, _) => Select(item, false);
        line.Inlines.Add(link);
        line.Inlines.Add(Quiet("  " + note));
        return line;
    }

    /// <summary>Text in the theme's quiet colour, following the theme as it changes.</summary>
    private static Run Quiet(string text)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
        return run;
    }

    private TextBlock Title(string text) => new() { Text = text, Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0, 12, 0, 4), TextWrapping = TextWrapping.Wrap };

    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 0) };

    private static Button SmallButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 4) };
        button.Click += (_, _) => action();
        return button;
    }

    // ---------------------------------------------------------------- exporting

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row || _places.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("LootLocator.ExportTitle"),
            Filter = L.T("LootLocator.CsvFilter"),
            FileName = $"loot-{string.Concat(row.Key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c))}-level-{Level}.csv",
            InitialDirectory = Directory.Exists(App.Settings.OutputFolder) ? App.Settings.OutputFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        var csv = new StringBuilder();
        static string Cell(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        csv.AppendLine("world,area,place,class,template,layer,x,y,z,chance,predicted,respawn"); // loc-ignore: a file's column names
        foreach (var p in _places.OrderBy(p => p.Spot.World).ThenByDescending(p => p.Chance))
            csv.AppendLine(string.Join(",", Cell(WorldName(p.Spot.World)), Cell(p.Area), Cell(p.Title), p.Spot.ClassName, Cell(p.Spot.Template), Cell(p.Spot.Layer),
                p.Spot.Position.X.ToString("0.###", CultureInfo.InvariantCulture), p.Spot.Position.Y.ToString("0.###", CultureInfo.InvariantCulture),
                p.Spot.Position.Z.ToString("0.###", CultureInfo.InvariantCulture), p.Chance.ToString("0.####", CultureInfo.InvariantCulture),
                p.Predicted switch { true => "yes", false => "no", _ => "" }, p.Respawn?.ToString(CultureInfo.InvariantCulture) ?? "")); // loc-ignore: a file's values
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            await File.WriteAllTextAsync(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            _ = Message.Show(null, L.T("LootLocator.Exported", _places.Count), MessageKind.Info, autoClose: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("LootLocator.CouldNotExport"), ex.Message, MessageKind.Error);
        }
    }

    // ---------------------------------------------------------------- text

    /// <summary>The colour bands chances are drawn in, highest first; each is the lowest chance it holds.</summary>
    private static readonly (double From, Color Color, Func<string> Text)[] Bands =
    [
        (0.9, Color.FromRgb(0xFF, 0xD2, 0x3F), () => L.T("LootLocator.Band90")),
        (0.5, Color.FromRgb(0xA6, 0xD8, 0x54), () => L.T("LootLocator.Band50")),
        (0.2, Color.FromRgb(0x2E, 0xC4, 0xA6), () => L.T("LootLocator.Band20")),
        (0.05, Color.FromRgb(0x3A, 0x9B, 0xDC), () => L.T("LootLocator.Band5")),
        (0, Color.FromRgb(0x8E, 0x7C, 0xE8), () => L.T("LootLocator.BandLow")),
    ];

    private static (double From, Color Color, Func<string> Text) BandOf(double chance) => Bands.First(b => chance >= b.From);

    public static string Percent(double chance) => chance <= 0 ? "0%" : chance < 0.001 ? L.T("LootLocator.UnderTenth") : L.T("LootLocator.PercentValue", chance * 100);

    public static string Position(System.Numerics.Vector3 at) => string.Format(CultureInfo.InvariantCulture, "{0:0.00}, {1:0.00}, {2:0.00}", at.X, at.Y, at.Z);

    private static string Range(uint min, uint max) => min == max ? $"×{min}" : $"×{min}–{max}"; // loc-ignore: numbers

    private static string Levels(uint min, uint max) => (min, max) switch
    {
        (0, 0) => "",
        (_, 0) => L.T("LootLocator.LevelFrom", min),
        _ => L.T("LootLocator.LevelRange", min, max),
    };

    /// <summary>A template's or layer's file name as words: "sack__herbalist_area__prolog" as "sack herbalist area prolog".</summary>
    internal static string Humanise(string name) => string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Where a place is in its world: its layer's folders under the world's own.</summary>
    internal static string AreaOf(LootSpot spot) => AreaOf(spot.World, spot.Layer);

    /// <summary>Where a layer is in its world: its folders under the world's own, as words.</summary>
    internal static string AreaOf(string world, string layerPath)
    {
        string folder = Path.GetDirectoryName(world) ?? "";
        string layer = layerPath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) ? layerPath[(folder.Length + 1)..] : layerPath;
        if (layer.StartsWith(@"data\levels\", StringComparison.OrdinalIgnoreCase)) layer = layer[12..];
        layer = Path.ChangeExtension(layer, null);
        var parts = layer.Split('\\').Select(Humanise).ToList();
        // "loot" and the like name the layer, not the place
        if (parts.Count > 1 && parts[^1] is "loot" or "gameplay" or "containers") parts.RemoveAt(parts.Count - 1);
        return string.Join(" › ", parts);
    }

    /// <summary>A world's name as players know it.</summary>
    internal static string WorldName(string world) => Path.GetFileName(Path.GetDirectoryName(world)) switch
    {
        "novigrad" => L.T("LootLocator.WorldNovigrad"),
        "skellige" => L.T("LootLocator.WorldSkellige"),
        "kaer_morhen" => L.T("LootLocator.WorldKaerMorhen"),
        "prolog_village" => L.T("LootLocator.WorldWhiteOrchard"),
        "prolog_village_winter" => L.T("LootLocator.WorldWhiteOrchardWinter"),
        "bob" => L.T("LootLocator.WorldToussaint"),
        "island_of_mist" => L.T("LootLocator.WorldIsleOfMists"),
        "the_spiral" => L.T("LootLocator.WorldSpiral"),
        "wyzima_castle" => L.T("LootLocator.WorldVizima"),
        _ => world,
    };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) (TableMode ? TableSearch : ItemSearch).Focus();
        else return;
        e.Handled = true;
    }
}
