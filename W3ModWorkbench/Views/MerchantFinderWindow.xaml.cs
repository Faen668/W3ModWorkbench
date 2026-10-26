using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Items;
using W3ModWorkbench.Core.Loot;
using W3ModWorkbench.Core.Merchants;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.Localisation;
using Vector2 = System.Numerics.Vector2;

namespace W3ModWorkbench;

/// <summary>A row of the merchants' list: a merchant, its kind, where it's found, how many places, and the words a search matches (its items too).</summary>
public sealed record MerchantRow(Merchant Merchant, string Title, string Detail, string Group, int GroupOrder, int Count, string Search)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>A place a merchant is: its world and area, its hours, what decides whether it's there, and what spawns it.</summary>
public sealed record MerchantPlaceRow(MerchantPlace Place, string WorldText, string Area, string HoursText, string WhenText, string SourceText)
{
    public string PositionText => Place.Located ? LootLocatorWindow.Position(Place.Position) : "";
}

/// <summary>An item a merchant sells: its chance at the player level chosen, how many, its base price and its respawn time.</summary>
public sealed record MerchantStockRow(string Title, string Name, double Chance, uint QuantityMin, uint QuantityMax, int Price, uint Respawn)
{
    public string ChanceText => LootLocatorWindow.Percent(Chance);
    public string QuantityText => QuantityMin == QuantityMax ? $"×{QuantityMin}" : $"×{QuantityMin}–{QuantityMax}"; // loc-ignore: numbers
    public string PriceText => Price > 0 ? Price.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string RespawnText => Respawn > 0 ? Respawn.ToString("N0", CultureInfo.CurrentCulture) : "";
}

/// <summary>A quest that changes where a merchant is: its title, what it does, and its file.</summary>
public sealed record MerchantQuestRow(string Quest, string What, string File);

/// <summary>
/// The Merchant Finder: every merchant the worlds spawn, what each sells and how likely, its money, its prices, where it
/// stands through the day, and what decides whether it's there (story and quest phases, quest facts, quests that move it).
/// </summary>
public partial class MerchantFinderWindow : Window, IMessageHost
{
    private static MerchantFinderWindow? _open;

    private sealed record WorldChoice(string Path, string Text)
    {
        public override string ToString() => Text;
    }
    private sealed record LegendRow(Brush Brush, string Text);
    private sealed record WorldPicture(BitmapSource Picture, Vector2 Corner, float Size, GameMinimap? Minimap);

    private ResourceLocator _locator = null!;
    private ItemDatabase? _db;
    private Dictionary<uint, string> _strings = [];
    private Dictionary<uint, string> _names = [];
    private MerchantIndex? _index;
    private LootRoll? _roll;
    private List<MerchantRow> _rows = [];
    private ICollectionView? _view;
    private MerchantRow? _selected;
    private List<MerchantPlaceRow> _places = [];
    private readonly Dictionary<string, Task<WorldPicture?>> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private string? _shownWorld;
    private bool _ignoreWorld, _ignoreLevel;
    private int _version;
    private readonly MinimapDetail _detail;
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private MerchantFinderWindow()
    {
        InitializeComponent();
        _detail = new MinimapDetail(Map);
        ModsCheck.IsChecked = App.Settings.MerchantWithMods;
        _ignoreLevel = true;
        LevelBox.ItemsSource = Enumerable.Range(1, LootLocatorWindow.MaxLevel).ToList();
        LevelBox.SelectedItem = Math.Clamp(App.Settings.LootPlayerLevel, 1, LootLocatorWindow.MaxLevel);
        _ignoreLevel = false;
        LegendList.ItemsSource = StandKinds.Select(k => new LegendRow(Frozen(new SolidColorBrush(k.Color)), k.Text())).ToList();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _view?.Refresh();
            CountText.Text = L.T("Merchant.Count", _view?.Cast<object>().Count() ?? 0);
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
        _open = new MerchantFinderWindow().Remembered(owner, "MerchantFinder");
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private uint Level => (uint)Math.Clamp(App.Settings.LootPlayerLevel, 1, LootLocatorWindow.MaxLevel);

    // ---------------------------------------------------------------- reading

    private async Task LoadAsync()
    {
        string? game = GameLocator.Resolve(null);
        if (game is null)
        {
            BusyText.Text = L.T("LootLocator.NoGame");
            return;
        }
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
            if (_strings.Count == 0)
            {
                var strings = await GameStringIndex.GetAsync(game, App.GameLanguage, dlcAndMods: false);
                _strings = strings.All.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Text);
                _names = strings.All.GroupBy(s => s.KeyHash).ToDictionary(g => g.Key, g => g.First().Text);
            }
            var db = _db;
            (_index, _roll) = await Task.Run(() => (MerchantIndex.Build(locator, progress, CancellationToken.None), LootRoll.FromDatabase(db)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BusyText.Text = L.T("LootLocator.CouldNotRead", ex.Message);
            return;
        }
        BuildRows();
        Busy.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        ExportButton.IsEnabled = _rows.Count > 0;
        StatusText.Text = L.T("Merchant.Summary", _rows.Count, _index.Places.Count, _index.Places.Select(p => p.World).Distinct().Count());
        if (_selected is { } again && _rows.FirstOrDefault(r => r.Merchant.Template == again.Merchant.Template) is { } row) MerchantList.SelectedItem = row;
        else SearchBox.Focus();
    }

    /// <summary>The list: every merchant the worlds place, by kind, with where it is and every item it can sell for searching.</summary>
    private void BuildRows()
    {
        var index = _index!;
        var placesOf = index.Places.GroupBy(p => p.Template, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<MerchantRow>();
        foreach (var m in index.Merchants.Values)
        {
            if (!placesOf.TryGetValue(m.Template, out var places)) continue;
            string title = TitleOf(m);
            var first = places.OrderByDescending(p => p.Located).First();
            string detail = $"{LootLocatorWindow.WorldName(first.World)} · {ShortArea(first)}";
            var items = StockItems(m).Select(i => (ItemName(i) ?? "") + " " + i);
            string search = string.Join(" ", [title, KindText(m.Kind), .. places.Select(AreaText).Distinct(), .. items]);
            rows.Add(new MerchantRow(m, title, detail, KindText(m.Kind), KindOrder(m.Kind), places.Count, search));
        }
        _rows = rows;
        var source = new CollectionViewSource { Source = _rows };
        _view = source.View;
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MerchantRow.Group)));
        _view.SortDescriptions.Add(new SortDescription(nameof(MerchantRow.GroupOrder), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(MerchantRow.Title), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(MerchantRow.Detail), ListSortDirection.Ascending));
        _view.Filter = o => o is MerchantRow r && (SearchBox.Text.Trim() is not { Length: > 0 } text || r.Search.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        MerchantList.ItemsSource = _view;
        CountText.Text = L.T("Merchant.Count", _view.Cast<object>().Count());
    }

    /// <summary>A merchant's name in the game's text, else its template's file name as words.</summary>
    private string TitleOf(Merchant m) => Text(m.DisplayName, "") is { Length: > 0 } name ? name : LootLocatorWindow.Humanise(Path.GetFileNameWithoutExtension(m.Template));

    private IEnumerable<string> StockItems(Merchant m) => m.Stock is { } stock && _roll is { } roll
        ? stock.Params.SelectMany(p => p.Tables).Where(t => roll.Tables.ContainsKey(t.Table)).SelectMany(t => roll.Tables[t.Table].Items).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase)
        : [];

    private string Text(uint id, string otherwise) => id != 0 && _strings.TryGetValue(id, out var text) && text.Length > 0 ? text : otherwise;

    private string? ItemName(string item) =>
        _db?.Find(DefKind.Item, item)?.Attribute("localisation_key_name") is { Length: > 0 } key && _names.TryGetValue(W3Strings.HashKey(key), out var n) && n.Length > 0 ? n : null;

    private static string AreaText(MerchantPlace p) => p.Layer.Length > 0 ? LootLocatorWindow.AreaOf(p.World, p.Layer) : LootLocatorWindow.Humanise(Path.GetFileNameWithoutExtension(p.File));

    /// <summary>An area's last two parts that name a place (not "exterior", "ap" and the like), enough to tell places apart in the list.</summary>
    private static string ShortArea(MerchantPlace p)
    {
        var parts = AreaText(p).Split(" › ").ToList();
        var named = parts.Where(x => !Generic.Contains(x)).ToList();
        return string.Join(" › ", (named.Count > 0 ? named : parts).TakeLast(2));
    }

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
        { "ap", "aps", "action points", "exterior", "interior", "area", "areas", "community", "encounter", "encounters", "spawners", "waypoints", "merchants", "npcs", "civil", "communities", "actionpoints", "default" }; // loc-ignore: layer folder names

    // ---------------------------------------------------------------- the list

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void ModsCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.MerchantWithMods = ModsCheck.IsChecked == true;
        App.Settings.Save();
        await LoadAsync();
    }

    private void LevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreLevel || LevelBox.SelectedItem is not int level || level == App.Settings.LootPlayerLevel) return;
        App.Settings.LootPlayerLevel = level;
        App.Settings.Save();
        if (_selected is { } row) ShowStock(row.Merchant);
    }

    private void MerchantList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MerchantList.SelectedItem is MerchantRow row && row != _selected) _ = ShowAsync(row);
    }

    // ---------------------------------------------------------------- the merchant

    private async Task ShowAsync(MerchantRow row)
    {
        _selected = row;
        if (_index is not { } index) return;
        int version = ++_version;
        var m = row.Merchant;
        _places = [.. index.Places.Where(p => p.Template.Equals(m.Template, StringComparison.OrdinalIgnoreCase))
            .Select(p => new MerchantPlaceRow(p, LootLocatorWindow.WorldName(p.World), AreaText(p), HoursText(p), WhenText(p), SourceText(p)))
            .OrderBy(p => p.WorldText, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Area, StringComparer.CurrentCultureIgnoreCase)];
        ShowCard(m, null);
        ShowStock(m);
        var quests = _places.SelectMany(p => QuestRows(p.Place)).Distinct().OrderBy(q => q.Quest, StringComparer.CurrentCultureIgnoreCase).ToList();
        QuestGrid.ItemsSource = quests;
        QuestsNote.Text = quests.Count > 0 ? L.T("Merchant.QuestsNote") : L.T("Merchant.NoQuests");

        var worlds = _places.Where(p => p.Place.Located).GroupBy(p => p.Place.World).OrderByDescending(g => g.Count())
            .Select(g => new WorldChoice(g.Key, L.T("LootLocator.WorldCount", LootLocatorWindow.WorldName(g.Key), g.Count()))).ToList();
        string? keep = _shownWorld is { } shown && worlds.Any(w => w.Path == shown) ? shown : worlds.FirstOrDefault()?.Path;
        _ignoreWorld = true;
        WorldBox.ItemsSource = worlds;
        WorldBox.SelectedItem = worlds.FirstOrDefault(w => w.Path == keep);
        _ignoreWorld = false;
        MapEmpty.Text = L.T("Merchant.NotLocated");
        MapEmpty.Visibility = worlds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Legend.Visibility = worlds.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = L.T("Merchant.Found", row.Title, _places.Count);
        if (worlds.Count == 0)
        {
            _shownWorld = null;
            _detail.Show(null, null);
            Map.SetTerrain(null, default, 0);
            Map.SetMarkers([]);
        }
        PlaceGrid.ItemsSource = _places;
        if (version != _version) return;
        await ShowWorldAsync(keep, true);
    }

    /// <summary>The middle column: who the merchant is, its money, prices and crafts, and (for a place picked) when and where it's there.</summary>
    private void ShowCard(Merchant m, MerchantPlaceRow? place)
    {
        double scroll = CardScroller.VerticalOffset;
        Card.Children.Clear();
        CardEmpty.Visibility = Visibility.Collapsed;
        Card.Children.Add(new TextBlock { Text = TitleOf(m), FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Card.Children.Add(Hint(KindText(m.Kind)));

        if (m.Crafts.Count > 0)
        {
            Card.Children.Add(Title(L.T("Merchant.Crafts")));
            foreach (var c in m.Crafts) Card.Children.Add(Para(L.T("Merchant.CraftLine", CraftText(c.Type), CraftLevelText(c.Level))));
        }

        Card.Children.Add(Title(L.T("Merchant.Money")));
        Card.Children.Add(Para(FundsText(m.FundsType)));

        var prices = PriceLines(m).ToList();
        if (prices.Count > 0)
        {
            Card.Children.Add(Title(L.T("Merchant.Prices")));
            foreach (string line in prices) Card.Children.Add(Para(line));
        }

        Card.Children.Add(Title(L.T("Merchant.WhereWhen")));
        if (place is null)
        {
            Card.Children.Add(Para(L.T("Merchant.PlacesLine", _places.Count, string.Join(", ", _places.Select(p => p.WorldText).Distinct()))));
            Card.Children.Add(Hint(L.T("Merchant.PickPlace")));
        }
        else
        {
            Card.Children.Add(Para($"{place.WorldText} · {place.Area}"));
            foreach (var hours in place.Place.Stands.GroupBy(s => (s.From, s.To, Kind: StandKindOf(s.Category).Text())).OrderBy(g => g.Key.From ?? -1))
                Card.Children.Add(Para(L.T("Merchant.StandLine", TimeText(hours.Key.From, hours.Key.To), hours.Key.Kind, hours.Count())));
            if (!place.Place.Located) Card.Children.Add(Hint(L.T("Merchant.NotLocated")));
            foreach (string when in WhenLines(place.Place)) Card.Children.Add(Para(when));
            if (WhenLines(place.Place).Count == 0) Card.Children.Add(Para(L.T("Merchant.Always")));
        }

        Card.Children.Add(Title(L.T("Merchant.Files")));
        var buttons = new WrapPanel();
        buttons.Children.Add(SmallButton(L.T("LootLocator.OpenTemplate"), () => _ = DataViewers.OpenLinkAsync(m.Template, _locator, this, this)));
        if (m.Scene is { } scene) buttons.Children.Add(SmallButton(L.T("Merchant.OpenScene"), () => _ = DataViewers.OpenLinkAsync(scene, _locator, this, this)));
        if (place is not null)
            buttons.Children.Add(SmallButton(place.Place.Source == MerchantSource.Community ? L.T("Merchant.OpenCommunity") : L.T("Merchant.OpenEncounter"),
                () => _ = DataViewers.OpenLinkAsync(place.Place.File, _locator, this, this)));
        Card.Children.Add(buttons);
        Card.Children.Add(Hint(m.Template));
        Card.UpdateLayout();
        CardScroller.ScrollToVerticalOffset(place is null ? 0 : scroll);
    }

    /// <summary>What the merchant sells at the player level chosen: each item's chance when the shop fills, how many, its base price.</summary>
    private void ShowStock(Merchant m)
    {
        uint level = Level;
        if (m.Stock is not { } stock || _roll is not { } roll)
        {
            StockNote.Text = L.T("Merchant.NoStock");
            StockGrid.ItemsSource = null;
            return;
        }
        var odds = roll.Odds(stock, level);
        var refs = stock.Params.SelectMany(p => p.Tables).Where(t => roll.Tables.ContainsKey(t.Table)).ToList();
        var amounts = refs.SelectMany(t => roll.Tables[t.Table].Items.Select(i => (Item: i, Respawn: t.Respawn is { } own && own != 0 ? own : roll.Tables[t.Table].Respawn)))
            .GroupBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        StockNote.Text = L.T("Merchant.StockOf", level);
        List<MerchantStockRow> rows;
        StockGrid.ItemsSource = rows = odds.Items.OrderByDescending(p => p.Value).ThenBy(p => ItemName(p.Key) ?? p.Key, StringComparer.CurrentCultureIgnoreCase).Select(p =>
        {
            var a = amounts.TryGetValue(p.Key, out var found) ? found : [];
            int price = int.TryParse(_db?.Find(DefKind.Item, p.Key)?.Attribute("price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            return new MerchantStockRow(ItemName(p.Key) ?? p.Key, p.Key, p.Value, a.Count > 0 ? a.Min(x => x.Item.QuantityMin) : 1, a.Count > 0 ? (uint)a.Sum(x => x.Item.QuantityMax) : 1, price,
                a.Count > 0 ? a.Max(x => x.Respawn) : 0);
        }).ToList();
        // the item looked for, when the search names one it sells
        if (SearchBox.Text.Trim() is { Length: > 0 } text
            && rows.FirstOrDefault(r => r.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) is { } found)
        {
            StockGrid.SelectedItem = found;
            StockGrid.ScrollIntoView(found);
        }
    }

    private void StockGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StockGrid.SelectedItem is MerchantStockRow row) LootLocatorWindow.OpenItem(this, row.Name, _shownWorld);
    }

    private void QuestGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QuestGrid.SelectedItem is MerchantQuestRow row) _ = DataViewers.OpenLinkAsync(row.File, _locator, this, this);
    }

    // ---------------------------------------------------------------- the words

    private static readonly string[] Kinds =
        ["Shopkeeper", "Blacksmith", "Armorer", "Herbalist", "Alchemist", "Innkeeper", "WineTrader", "Enchanter", "Hairdresser", "DyeTrader", "BookSeller", "Collector", "BoatBuilder", "Cammerlengo"];

    private static int KindOrder(string kind) => Array.IndexOf(Kinds, kind) is int i and >= 0 ? i : Kinds.Length;

    private static string KindText(string kind) => kind switch
    {
        "Shopkeeper" => L.T("Merchant.KindShopkeeper"),
        "Blacksmith" => L.T("Merchant.KindBlacksmith"),
        "Armorer" => L.T("Merchant.KindArmorer"),
        "Herbalist" => L.T("Merchant.KindHerbalist"),
        "Alchemist" => L.T("Merchant.KindAlchemist"),
        "Innkeeper" => L.T("Merchant.KindInnkeeper"),
        "WineTrader" => L.T("Merchant.KindWineTrader"),
        "Enchanter" => L.T("Merchant.KindEnchanter"),
        "Hairdresser" => L.T("Merchant.KindHairdresser"),
        "DyeTrader" => L.T("Merchant.KindDyeTrader"),
        "BookSeller" => L.T("Merchant.KindBookSeller"),
        "Collector" => L.T("Merchant.KindCollector"),
        "BoatBuilder" => L.T("Merchant.KindBoatBuilder"),
        "Cammerlengo" => L.T("Merchant.KindCammerlengo"),
        _ => LootLocatorWindow.Humanise(kind),
    };

    private static string CraftText(string type) => type switch
    {
        "ECT_Smith" => L.T("Merchant.KindBlacksmith"),
        "ECT_Armorer" => L.T("Merchant.KindArmorer"),
        "ECT_Crafter" => L.T("Merchant.CraftCrafter"),
        "ECT_Enchanter" => L.T("Merchant.KindEnchanter"),
        _ => LootLocatorWindow.Humanise(type.Replace("ECT_", "")),
    };

    private static string CraftLevelText(string level) => level switch
    {
        "ECL_Journeyman" or "" => L.T("Merchant.LevelJourneyman"),
        "ECL_Master" => L.T("Merchant.LevelMaster"),
        "ECL_Grand_Master" => L.T("Merchant.LevelGrandmaster"),
        "ECL_Arch_Master" => L.T("Merchant.LevelArchmaster"),
        _ => LootLocatorWindow.Humanise(level.Replace("ECL_", "")),
    };

    /// <summary>A merchant's money, by its funds type: what it starts with, what it gains every 5 days it's talked to, and its most (the game's own amounts, scaled by its funds modifier).</summary>
    private static string FundsText(string funds) => funds switch
    {
        "Broke" => L.T("Merchant.FundsBroke"),
        "Poor" => L.T("Merchant.Funds", 200, 100, 2500),
        "Rich" => L.T("Merchant.Funds", 1000, 1000, 7500),
        "RichQuickStart" => L.T("Merchant.Funds", 5000, 3500, 15000),
        _ => L.T("Merchant.Funds", 500, 150, 5000),
    };

    /// <summary>The merchant's prices: goods from its own region cheaper (its area ability), and its own price multiplier.</summary>
    private IEnumerable<string> PriceLines(Merchant m)
    {
        foreach (string tag in m.InventoryTags)
            if (_db?.Find(DefKind.Ability, tag) is { } ability)
                foreach (var a in ability.Element.Elements().Where(e => e.Name.LocalName.EndsWith("_price_mult", StringComparison.Ordinal)))
                    if (double.TryParse(a.Attribute("min")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mult))
                        yield return L.T("Merchant.RegionGoods", RegionText(a.Name.LocalName[..^"_price_mult".Length]), mult);
        if (Math.Abs(m.PriceMult - 1) > 0.001) yield return L.T("Merchant.PriceMult", m.PriceMult);
    }

    private static string RegionText(string region) => region switch
    {
        "nomansland" => L.T("Merchant.RegionNoMansLand"),
        "novigrad" => L.T("Merchant.RegionNovigrad"),
        "skellige" => L.T("LootLocator.WorldSkellige"),
        "nilfgard" => L.T("Merchant.RegionNilfgaard"),
        _ => LootLocatorWindow.Humanise(region),
    };

    private static string TimeText(int? from, int? to) => from is { } f && to is { } t
        ? $"{f / 3600}:{f / 60 % 60:00}–{t / 3600}:{t / 60 % 60:00}" // loc-ignore: times
        : L.T("Merchant.AllDay");

    /// <summary>A place's hours: each time it's somewhere, joined.</summary>
    private static string HoursText(MerchantPlace p) => p.Stands.Count == 0 ? ""
        : string.Join(", ", p.Stands.Select(s => (s.From, s.To)).Distinct().OrderBy(s => s.From ?? -1).Select(s => TimeText(s.From, s.To)));

    private static string SourceText(MerchantPlace p) => p.Source == MerchantSource.Community ? L.T("Merchant.SourceCommunity") : L.T("Merchant.SourceEncounter");

    private string WhenText(MerchantPlace p) => WhenLines(p) is { Count: > 0 } lines ? string.Join(" · ", lines) : L.T("Merchant.Always");

    /// <summary>What decides whether a merchant is at a place: its phase, the quest facts it needs, and whether quests can send it away.</summary>
    private List<string> WhenLines(MerchantPlace p)
    {
        var lines = new List<string>();
        foreach (string phase in p.Phases.Where(x => x.Length > 0 && !x.Equals("default", StringComparison.OrdinalIgnoreCase)))
            lines.Add(L.T("Merchant.InPhase", phase));
        foreach (var c in p.Conditions) lines.Add(ConditionText(c));
        if (p.Groups.Count > 0) lines.Add(L.T("Merchant.GroupNote", string.Join(", ", p.Groups)));
        return lines;
    }

    private string ConditionText(MerchantCondition c)
    {
        if (c.Fact.Length == 0) return L.T("Merchant.OtherCondition", c.Other);
        string quest = QuestName(c.Fact);
        bool done = c.Fact.EndsWith("_finished", StringComparison.OrdinalIgnoreCase) || c.Fact.EndsWith("_done", StringComparison.OrdinalIgnoreCase)
            || c.Fact.EndsWith("_completed", StringComparison.OrdinalIgnoreCase);
        if (done && _index!.QuestTitle(c.Fact) != 0)
        {
            if ((c.Op is "GreaterEqual" && c.Value == 1) || (c.Op is "Greater" && c.Value == 0) || (c.Op is "Equal" && c.Value == 1)) return L.T("Merchant.AfterQuest", quest);
            if ((c.Op is "Less" && c.Value == 1) || (c.Op is "Equal" && c.Value == 0) || (c.Op is "LessEqual" && c.Value == 0)) return L.T("Merchant.BeforeQuest", quest);
        }
        string op = c.Op switch { "NotEqual" => "≠", "Less" => "<", "LessEqual" => "≤", "Greater" => ">", "GreaterEqual" => "≥", _ => "=" }; // loc-ignore: symbols
        return _index!.QuestTitle(c.Fact) != 0 ? L.T("Merchant.FactOfQuest", c.Fact, op, c.Value, quest) : L.T("Merchant.Fact", c.Fact, op, c.Value);
    }

    /// <summary>A quest's title from the journal, for a fact or a quest file, else its file name as words.</summary>
    private string QuestName(string name) => _index!.QuestTitle(name) is var id and not 0 && Text(id, "") is { Length: > 0 } title ? title
        : LootLocatorWindow.Humanise(Path.GetFileNameWithoutExtension(name));

    /// <summary>The quest blocks that change a place, as rows: a community's phase starting or the community going, an encounter's phase, an encounter on or off.</summary>
    private IEnumerable<MerchantQuestRow> QuestRows(MerchantPlace p)
    {
        foreach (var link in _index!.QuestsOf(p))
        {
            string? what = link.Action switch
            {
                MerchantQuestAction.StoryPhase or MerchantQuestAction.EncounterPhase when link.Phase.Length == 0 && p.Phases.All(x => x.Length == 0) => L.T("Merchant.Brings"),
                MerchantQuestAction.StoryPhase when p.Phases.Contains(link.Phase, StringComparer.OrdinalIgnoreCase) => L.T("Merchant.StartsPhase", link.Phase),
                MerchantQuestAction.CommunityOff => L.T("Merchant.SendsAway"),
                MerchantQuestAction.EncounterPhase when p.Phases.Count == 0 || p.Phases.Contains(link.Phase, StringComparer.OrdinalIgnoreCase) => L.T("Merchant.StartsPhase", link.Phase),
                MerchantQuestAction.EncounterOn => L.T("Merchant.TurnsOn"),
                MerchantQuestAction.EncounterOff => L.T("Merchant.TurnsOff"),
                _ => null,
            };
            if (what is not null) yield return new MerchantQuestRow(QuestName(link.QuestFile), what, link.QuestFile);
        }
    }

    private TextBlock Title(string text) => new() { Text = text, Style = (Style)FindResource("SectionTitle"), TextWrapping = TextWrapping.Wrap };

    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 0) };

    private static TextBlock Para(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 19 };

    private static Button SmallButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6) };
        b.Click += (_, _) => click();
        return b;
    }

    // ---------------------------------------------------------------- the map

    private async void WorldBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreWorld || WorldBox.SelectedItem is not WorldChoice world) return;
        await ShowWorldAsync(world.Path, true);
    }

    /// <summary>A world's places: each spot a merchant goes to, coloured by what it does there, or the place itself when it has no spots.</summary>
    private async Task ShowWorldAsync(string? world, bool fit)
    {
        var rows = _places.Where(p => p.Place.World == world && p.Place.Located).ToList();
        var markers = new List<MapMarker>();
        foreach (var row in rows)
            if (row.Place.Stands.Count == 0) markers.Add(new MapMarker(At(row.Place.Position), OtherColor, null, 0, false, 4.5f, row));
            else markers.AddRange(row.Place.Stands.Select(s => new MapMarker(At(s.Position), StandKindOf(s.Category).Color, null, 0, false, 4.5f, row)));
        Map.SetMarkers(markers);
        Map.SetShapes([]);
        Map.PickedMarker = null;
        if (world is null) return;
        if (world != _shownWorld)
        {
            _shownWorld = world;
            Map.SetTerrain(null, default, 0);
            _detail.Show(null, null);
            MapProgress.Text = L.T("LootLocator.ReadingMap", LootLocatorWindow.WorldName(world));
            MapProgress.Visibility = Visibility.Visible;
            var picture = await PictureOf(world);
            if (_shownWorld != world) return;
            MapProgress.Visibility = Visibility.Collapsed;
            if (picture is not null) Map.SetTerrain(picture.Picture, picture.Corner, picture.Size);
            _detail.Show(picture?.Minimap, _locator);
        }
        if (fit) await Dispatcher.InvokeAsync(() => FitTo(rows), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static Vector2 At(System.Numerics.Vector3 p) => new(p.X, p.Y);

    private void FitTo(IEnumerable<MerchantPlaceRow> rows)
    {
        var points = rows.SelectMany(r => r.Place.Stands.Count > 0 ? r.Place.Stands.Select(s => At(s.Position)) : [At(r.Place.Position)]).ToList();
        if (points.Count == 0)
        {
            Map.FitWorld();
            return;
        }
        var min = points.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
        var max = points.Aggregate(new Vector2(float.MinValue), Vector2.Max);
        Map.FitTo(min - new Vector2(80), max + new Vector2(80));
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitTo(_places.Where(p => p.Place.World == _shownWorld && p.Place.Located));

    /// <summary>A world's picture: the game's own minimap, or the terrain's relief for a world without one. Read once a world.</summary>
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
        HoverText.Text = at is { } p ? L.T("LootLocator.HoverAt", p.X, p.Y) : L.T("Merchant.MouseHint");

    private void Map_MarkerPicked(object? sender, MapMarker? marker)
    {
        if (marker?.Tag is not MerchantPlaceRow row) return;
        ListTabs.SelectedItem = PlacesTab;
        PlaceGrid.SelectedItem = row;
        PlaceGrid.ScrollIntoView(row);
    }

    /// <summary>A place picked: its spots ringed and its encounter's area drawn on the map, and its hours and conditions shown on the card.</summary>
    private void PlaceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is not { } merchant) return;
        if (PlaceGrid.SelectedItem is not MerchantPlaceRow row)
        {
            Map.SetShapes([]);
            ShowCard(merchant.Merchant, null);
            return;
        }
        ShowCard(merchant.Merchant, row);
        if (!row.Place.Located) return;
        if (row.Place.World != _shownWorld)
        {
            WorldBox.SelectedItem = (WorldBox.ItemsSource as IEnumerable<WorldChoice>)?.FirstOrDefault(w => w.Path == row.Place.World);
            return;
        }
        var at = At(row.Place.Position);
        if (Map.PickedMarker?.Tag != row) Map.PickedMarker = new MapMarker(at, OtherColor, null, 0, false, 4.5f, row);
        Map.SetShapes(row.Place.Area is { Count: > 2 } area ? [new MapShape(area, OtherColor, true, true, true)] : []);
        if (PlaceGrid.IsKeyboardFocusWithin) FitTo([row]);
    }

    // ---------------------------------------------------------------- exporting

    /// <summary>Saves every merchant's every place, with its hours, what decides it, and its spots, as a CSV file.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_index is not { } index) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("LootLocator.ExportTitle"),
            Filter = L.T("LootLocator.CsvFilter"),
            FileName = "merchants.csv", // loc-ignore: a file name
            InitialDirectory = Directory.Exists(App.Settings.OutputFolder) ? App.Settings.OutputFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        var csv = new StringBuilder();
        static string Cell(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        static string N(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
        csv.AppendLine("merchant,kind,world,area,hours,when,spawned by,x,y,z,template,file"); // loc-ignore: a file's column names
        int count = 0;
        foreach (var row in _rows.OrderBy(r => r.GroupOrder).ThenBy(r => r.Title))
            foreach (var p in index.Places.Where(p => p.Template.Equals(row.Merchant.Template, StringComparison.OrdinalIgnoreCase)))
            {
                csv.AppendLine(string.Join(",", Cell(row.Title), Cell(row.Group), Cell(LootLocatorWindow.WorldName(p.World)), Cell(AreaText(p)), Cell(HoursText(p)), Cell(WhenText(p)),
                    Cell(SourceText(p)), p.Located ? N(p.Position.X) : "", p.Located ? N(p.Position.Y) : "", p.Located ? N(p.Position.Z) : "", Cell(p.Template), Cell(p.File)));
                count++;
            }
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            await File.WriteAllTextAsync(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            _ = Message.Show(null, L.T("LootLocator.Exported", count), MessageKind.Info, autoClose: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("LootLocator.CouldNotExport"), ex.Message, MessageKind.Error);
        }
    }

    // ---------------------------------------------------------------- spots

    private static readonly Color OtherColor = Color.FromRgb(0xC8, 0xC8, 0xC8);

    /// <summary>What a merchant does at a spot, by its action point's category, with its colour on the map.</summary>
    private static readonly (string Prefix, Color Color, Func<string> Text)[] StandKinds =
    [
        ("work", Color.FromRgb(0xFF, 0xD2, 0x3F), () => L.T("Merchant.StandWork")),
        ("guarding", Color.FromRgb(0xF0, 0x8A, 0x3C), () => L.T("Merchant.StandGuard")),
        ("dining", Color.FromRgb(0xA6, 0xD8, 0x54), () => L.T("Merchant.StandEat")),
        ("household", Color.FromRgb(0x3A, 0xC4, 0xB4), () => L.T("Merchant.StandHome")),
        ("resting", Color.FromRgb(0x3A, 0x9B, 0xDC), () => L.T("Merchant.StandRest")),
        ("sleeping", Color.FromRgb(0x8E, 0x7C, 0xE6), () => L.T("Merchant.StandSleep")),
        ("", OtherColor, () => L.T("Merchant.StandOther")),
    ];

    private static (string Prefix, Color Color, Func<string> Text) StandKindOf(string category) =>
        StandKinds.First(k => category.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else return;
        e.Handled = true;
    }
}
