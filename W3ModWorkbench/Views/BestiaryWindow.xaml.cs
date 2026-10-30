using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Bestiary;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Items;
using W3ModWorkbench.Core.Loot;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.Localisation;
using Vector2 = System.Numerics.Vector2;

namespace W3ModWorkbench;

/// <summary>A row of the bestiary's list: an entry (or a monster the bestiary has none for), its category, how many places spawn it, and its icon once read.</summary>
public sealed class BestiaryRow(BestiaryEntry? entry, BestiaryCreature? creature, string title, string detail, string group, int groupOrder, int count) : INotifyPropertyChanged
{
    public BestiaryEntry? Entry { get; } = entry;
    public BestiaryCreature? Creature { get; } = creature;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string Group { get; } = group;
    public int GroupOrder { get; } = groupOrder;
    public int Count { get; } = count;
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }
    private ImageSource? _icon;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A place a creature spawns: its encounter or community, the kind spawned there, its level, and how many.</summary>
public sealed record BestiaryPlaceRow(BestiaryPlace Place, string Area, string Variant, int Level, int Limit, int AtOnce)
{
    public string SourceText => L.T(Place.Source == BestiarySource.Community ? "Bestiary.SourceCommunity" : "Bestiary.SourceEncounter");
    public string QuestText => Place.Quest ? L.T("LootLocator.Yes") : "";
    public string LimitText => Limit > 0 ? Limit.ToString(CultureInfo.CurrentCulture) : "";
    public string AtOnceText => AtOnce > 0 ? AtOnce.ToString(CultureInfo.CurrentCulture) : "";
    public string PositionText => LootLocatorWindow.Position(Place.Position);
}

/// <summary>A kind of a creature (one template): its level, how many places spawn it, and its abilities.</summary>
public sealed record BestiaryVariantRow(BestiaryCreature Creature, string Title, int Places)
{
    public string AbilitiesText => string.Join(", ", Creature.Abilities);
}

/// <summary>An item a creature can drop: its chance at the player level chosen, and how many.</summary>
public sealed record BestiaryDropRow(string Title, string Name, double Chance, string QuantityText)
{
    public string ChanceText => LootLocatorWindow.Percent(Chance);
}

/// <summary>
/// The Bestiary: the game's bestiary entries with their artwork and texts as the game shows them, what works against
/// each, where in the worlds its kinds spawn and at what level (from the worlds' encounters), and what they drop.
/// </summary>
public partial class BestiaryWindow : Window, IMessageHost
{
    private static BestiaryWindow? _open;

    private sealed record WorldChoice(string Path, string Text)
    {
        public override string ToString() => Text;
    }
    private sealed record LegendRow(Brush Brush, string Text);
    private sealed record WorldPicture(BitmapSource Picture, Vector2 Corner, float Size, GameMinimap? Minimap);


    private ResourceLocator _locator = null!;
    private ItemDatabase? _db;
    private bool _dbWithMods;
    private Dictionary<uint, string> _strings = [];
    private Dictionary<uint, string> _names = [];
    private BestiaryIndex? _index;
    private LootRoll? _roll;
    private List<BestiaryRow> _rows = [];
    private ICollectionView? _view;
    private BestiaryRow? _selected;
    private List<BestiaryPlaceRow> _places = [];
    private readonly Dictionary<string, Task<WorldPicture?>> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private string? _shownWorld;
    private bool _loading = true, _ignoreWorld;
    private int _version;
    private readonly MinimapDetail _detail;
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private BestiaryWindow()
    {
        InitializeComponent();
        _detail = new MinimapDetail(Map);
        ModsCheck.IsChecked = App.Settings.BestiaryWithMods;
        UnlistedCheck.IsChecked = App.Settings.BestiaryShowUnlisted;
        LegendList.ItemsSource = Bands.Select(b => new LegendRow(Frozen(new SolidColorBrush(b.Color)), b.Text())).ToList();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _view?.Refresh();
            CountText.Text = L.T("Bestiary.Count", _view?.Cast<object>().Count() ?? 0);
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
        _open = new BestiaryWindow().Remembered(owner, "Bestiary");
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

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
            bool withMods = ModsCheck.IsChecked == true;
            bool reload = _db is null || _dbWithMods != withMods;
            if (reload)
            {
                _db = await Task.Run(() => ItemDatabase.Load(game, progress, withMods: withMods));
                _dbWithMods = withMods;
            }
            if (_strings.Count == 0 || reload)
            {
                var strings = await GameStringIndex.GetAsync(game, App.GameLanguage, dlcAndMods: withMods);
                _strings = strings.All.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Text);
                _names = strings.All.GroupBy(s => s.KeyHash).ToDictionary(g => g.Key, g => g.First().Text);
            }
            var db = _db;
            (_index, _roll) = await Task.Run(() => (BestiaryIndex.Build(locator, ScriptCorpus.GameScriptsFolder(game), progress, CancellationToken.None), LootRoll.FromDatabase(db)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BusyText.Text = L.T("LootLocator.CouldNotRead", ex.Message);
            return;
        }
        BuildRows();
        Busy.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        _loading = false;
        int listed = _rows.Count(r => r.Entry is not null);
        StatusText.Text = L.T("Bestiary.Summary", listed, _index.Places.Count, _index.Worlds.Count)
            + (_index.Rules == 0 ? "  ·  " + L.T("Bestiary.NoRules") : "");
        _ = LoadIconsAsync();
        if (_selected is { } again && _rows.FirstOrDefault(r => r.Title == again.Title && r.Group == again.Group) is { } row) CreatureList.SelectedItem = row;
        else SearchBox.Focus();
    }

    /// <summary>The list: every entry with a name in the game's text, by category in the game's order, then the monsters the bestiary has no entry for.</summary>
    private void BuildRows()
    {
        var index = _index!;
        var placesOf = index.Places.GroupBy(p => p.Template, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var outsideQuests = index.Places.Where(p => !p.Quest).Select(p => p.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categories = index.Categories.Select((c, i) => (c, i)).ToDictionary(x => x.c.Guid, x => (Name: Text(x.c.Name, x.c.BaseName), Order: x.i));
        var rows = new List<BestiaryRow>();
        foreach (var e in index.Entries)
        {
            if (Text(e.Name, "") is not { Length: > 0 } name) continue;
            var creatures = index.CreaturesOf(e).ToList();
            int places = creatures.Sum(c => placesOf.GetValueOrDefault(c.Template));
            var levels = creatures.Where(c => placesOf.ContainsKey(c.Template)).Select(c => c.Level).ToList();
            string detail = places == 0 ? L.T("Bestiary.OnlyInQuests")
                : creatures.All(c => !outsideQuests.Contains(c.Template)) ? L.T("Bestiary.InQuestsLevels", LevelRange(levels))
                : L.T("Bestiary.Levels", LevelRange(levels));
            var (group, order) = categories.TryGetValue(e.Category, out var cat) ? cat : (L.T("Bestiary.OtherGroup"), categories.Count);
            rows.Add(new BestiaryRow(e, null, name, detail, group, order, places));
        }
        foreach (var c in index.Creatures.Values.Where(c => c.Entry is null && placesOf.ContainsKey(c.Template)))
            rows.Add(new BestiaryRow(null, c, LootLocatorWindow.Humanise(Path.GetFileNameWithoutExtension(c.Template)), L.T("Bestiary.Levels", LevelRange([c.Level])),
                L.T("Bestiary.Unlisted"), int.MaxValue, placesOf[c.Template]));
        _rows = rows;
        var source = new CollectionViewSource { Source = _rows };
        _view = source.View;
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BestiaryRow.Group)));
        _view.SortDescriptions.Add(new SortDescription(nameof(BestiaryRow.GroupOrder), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(BestiaryRow.Title), ListSortDirection.Ascending));
        _view.Filter = o => o is BestiaryRow r && (r.Entry is not null || UnlistedCheck.IsChecked == true)
            && (SearchBox.Text.Trim() is not { Length: > 0 } text || r.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || r.Group.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        CreatureList.ItemsSource = _view;
        CountText.Text = L.T("Bestiary.Count", _view.Cast<object>().Count());
    }

    /// <summary>Each entry's icon from the game's own icons, read in the background.</summary>
    private async Task LoadIconsAsync()
    {
        var locator = _locator;
        foreach (var row in _rows.Where(r => r.Entry is not null))
        {
            var entry = row.Entry!;
            var icon = await Task.Run(() => BestiaryIndex.ImagePaths(entry).Select(p => locator.FindTexture(p.Icon) is not null ? MeshTextures.LoadWithAlpha(locator, p.Icon, 64) : null)
                .FirstOrDefault(b => b is not null));
            if (locator != _locator) return;
            row.Icon = icon;
        }
    }

    private string Text(uint id, string otherwise) => id != 0 && _strings.TryGetValue(id, out var text) && text.Length > 0 ? text : otherwise;

    /// <summary>The game's journal text as plain text: its line breaks kept, its other markup taken out.</summary>
    private static string Plain(string text) =>
        Regex.Replace(Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase), "<[^>]+>", "").Trim();

    private static string LevelRange(IReadOnlyCollection<int> levels) => levels.Count == 0 ? "" : levels.Min() == levels.Max()
        ? levels.Min().ToString(CultureInfo.CurrentCulture) : $"{levels.Min()}–{levels.Max()}"; // loc-ignore: numbers

    // ---------------------------------------------------------------- the list

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void UnlistedCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.BestiaryShowUnlisted = UnlistedCheck.IsChecked == true;
        App.Settings.Save();
        _view?.Refresh();
        CountText.Text = L.T("Bestiary.Count", _view?.Cast<object>().Count() ?? 0);
    }

    private async void ModsCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.BestiaryWithMods = ModsCheck.IsChecked == true;
        App.Settings.Save();
        await LoadAsync();
    }

    private void CreatureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CreatureList.SelectedItem is BestiaryRow row && row != _selected) _ = ShowAsync(row);
    }

    // ---------------------------------------------------------------- the entry

    private async Task ShowAsync(BestiaryRow row)
    {
        _selected = row;
        if (_index is not { } index) return;
        int version = ++_version;
        var creatures = row.Entry is { } entry ? index.CreaturesOf(entry).ToList() : [row.Creature!];
        var templates = creatures.Select(c => c.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byTemplate = creatures.ToDictionary(c => c.Template, StringComparer.OrdinalIgnoreCase);
        _places = [.. index.Places.Where(p => templates.Contains(p.Template)).Select(p => new BestiaryPlaceRow(p, LootLocatorWindow.AreaOf(p.World, p.Layer),
            VariantTitle(byTemplate[p.Template]), byTemplate[p.Template].Level, p.TotalSpawnLimit, p.MaxSpawnedAtOnce))];
        ExportButton.IsEnabled = _places.Count > 0;
        await ShowEntryAsync(row, version);
        if (version != _version) return;

        var variants = creatures.Select(c => new BestiaryVariantRow(c, VariantTitle(c), _places.Count(p => p.Place.Template.Equals(c.Template, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(v => v.Places).ThenBy(v => v.Creature.Level).ToList();
        VariantGrid.ItemsSource = variants;
        VariantGrid.SelectedItem = variants.FirstOrDefault(v => v.Creature.Loot is not null) ?? variants.FirstOrDefault();
        if (VariantGrid.SelectedItem is null) ShowDrops(null);

        var worlds = _places.GroupBy(p => p.Place.World).OrderByDescending(g => g.Count())
            .Select(g => new WorldChoice(g.Key, L.T("LootLocator.WorldCount", LootLocatorWindow.WorldName(g.Key), g.Count()))).ToList();
        string? keep = _shownWorld is { } shown && worlds.Any(w => w.Path == shown) ? shown : worlds.FirstOrDefault()?.Path;
        _ignoreWorld = true;
        WorldBox.ItemsSource = worlds;
        WorldBox.SelectedItem = worlds.FirstOrDefault(w => w.Path == keep);
        _ignoreWorld = false;
        MapEmpty.Text = L.T("Bestiary.NotPlaced");
        MapEmpty.Visibility = _places.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Legend.Visibility = _places.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = _places.Count == 0 ? L.T("Bestiary.NotPlaced")
            : L.T("Bestiary.Found", _places.Count, worlds.Count, LevelRange([.. _places.Select(p => p.Level)]));
        if (_places.Count == 0)
        {
            // nothing placed: no world to show
            _shownWorld = null;
            _detail.Show(null, null);
            Map.SetTerrain(null, default, 0);
        }
        // a new creature's places fitted, even in the world already shown
        await ShowWorldAsync(keep, true);
    }

    private static string VariantTitle(BestiaryCreature c) => LootLocatorWindow.Humanise(Path.GetFileNameWithoutExtension(c.Template));

    /// <summary>The middle column: the entry's artwork and texts, as the game's bestiary shows them, and what works against it.</summary>
    private async Task ShowEntryAsync(BestiaryRow row, int version)
    {
        Entry.Children.Clear();
        EntryEmpty.Visibility = Visibility.Collapsed;
        EntryScroller.ScrollToTop();
        var entry = row.Entry;
        if (entry is not null)
        {
            var locator = _locator;
            var picture = await Task.Run(() => BestiaryIndex.ImagePaths(entry).Select(p => locator.FindTexture(p.Picture) is not null ? MeshTextures.LoadWithAlpha(locator, p.Picture, 1024) : null)
                .FirstOrDefault(b => b is not null));
            if (version != _version) return;
            if (picture is not null)
                Entry.Children.Add(new Image { Source = picture, MaxHeight = 420, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        }
        Entry.Children.Add(new TextBlock { Text = row.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Entry.Children.Add(Hint(row.Group));
        if (entry is null)
        {
            Entry.Children.Add(Hint(L.T("Bestiary.UnlistedNote")));
            Entry.Children.Add(Hint(row.Creature!.Template));
            return;
        }
        if (entry.ItemsAgainst.Count > 0)
        {
            Entry.Children.Add(Title(L.T("Bestiary.WeakTo")));
            var chips = new WrapPanel();
            foreach (string item in entry.ItemsAgainst) chips.Children.Add(await ChipAsync(item));
            Entry.Children.Add(chips);
        }
        foreach (uint id in entry.Descriptions)
            if (Text(id, "") is { Length: > 0 } text) Entry.Children.Add(Para(Plain(text)));
        if (entry.VitalSpots.Count > 0)
        {
            Entry.Children.Add(Title(L.T("Bestiary.VitalSpots")));
            foreach (var spot in entry.VitalSpots)
            {
                Entry.Children.Add(new TextBlock { Text = Plain(Text(spot.Title, "")), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                Entry.Children.Add(Para(Plain(Text(spot.Text, ""))));
            }
        }
        if (entry.Hints.Count > 0)
        {
            Entry.Children.Add(Title(L.T("Bestiary.Hints")));
            foreach (uint id in entry.Hints) Entry.Children.Add(Para(Plain(Text(id, ""))));
        }
    }

    /// <summary>A thing that works against a creature: an item with its icon and in-game name, or a sign by its name.</summary>
    private async Task<FrameworkElement> ChipAsync(string item)
    {
        var def = _db?.Find(DefKind.Item, item);
        string name = def?.Attribute("localisation_key_name") is { Length: > 0 } key && _names.TryGetValue(W3Strings.HashKey(key), out var n) && n.Length > 0 ? n
            : item;
        BitmapSource? icon = null;
        if (def?.Attribute("icon_path") is { Length: > 0 } iconPath)
        {
            var locator = _locator;
            string path = @"gameplay\gui_new\" + iconPath.Replace('/', '\\');
            icon = await Task.Run(() => MeshTextures.LoadWithAlpha(locator, path, 64));
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null) panel.Children.Add(new Image { Source = icon, Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 0) });
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border { Child = panel, Padding = new Thickness(6, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(5) };
        chip.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        if (def is not null) chip.ToolTip = item;
        return chip;
    }

    private TextBlock Title(string text) => new() { Text = text, Style = (Style)FindResource("SectionTitle"), TextWrapping = TextWrapping.Wrap };

    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 0) };

    private static TextBlock Para(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 19 };

    // ---------------------------------------------------------------- kinds and drops

    private void VariantGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDrops(VariantGrid.SelectedItem as BestiaryVariantRow);

    /// <summary>What a kind drops, with each item's chance at the player level the Loot Locator uses.</summary>
    private void ShowDrops(BestiaryVariantRow? variant)
    {
        uint level = (uint)Math.Clamp(App.Settings.LootPlayerLevel, 1, LootLocatorWindow.MaxLevel);
        if (variant?.Creature.Loot is not { } loot || _roll is not { } roll)
        {
            DropsNote.Text = variant is null ? "" : L.T("Bestiary.NoDrops", variant.Title);
            DropGrid.ItemsSource = null;
            return;
        }
        var odds = roll.Odds(loot, level);
        var amounts = loot.Params.SelectMany(p => p.Tables).Where(t => roll.Tables.ContainsKey(t.Table)).SelectMany(t => roll.Tables[t.Table].Items)
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        DropsNote.Text = L.T("Bestiary.DropsOf", variant.Title, level);
        DropGrid.ItemsSource = odds.Items.OrderByDescending(p => p.Value).Select(p => new BestiaryDropRow(ItemName(p.Key) ?? p.Key, p.Key, p.Value,
            amounts.TryGetValue(p.Key, out var a) ? (a.QuantityMin == a.QuantityMax ? $"×{a.QuantityMin}" : $"×{a.QuantityMin}–{a.QuantityMax}") : "")).ToList(); // loc-ignore: numbers
    }

    private string? ItemName(string item) =>
        _db?.Find(DefKind.Item, item)?.Attribute("localisation_key_name") is { Length: > 0 } key && _names.TryGetValue(W3Strings.HashKey(key), out var n) && n.Length > 0 ? n : null;

    private void DropGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DropGrid.SelectedItem is BestiaryDropRow row) LootLocatorWindow.OpenItem(this, row.Name, _shownWorld);
    }

    // ---------------------------------------------------------------- the map

    private async void WorldBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreWorld || WorldBox.SelectedItem is not WorldChoice world) return;
        await ShowWorldAsync(world.Path, true);
    }

    private async Task ShowWorldAsync(string? world, bool fit)
    {
        var rows = _places.Where(p => p.Place.World == world).ToList();
        // a quest's spawn is a diamond, drawn over the rest
        Map.SetMarkers([.. rows.OrderBy(p => p.Place.Quest).ThenBy(p => p.Level).Select(p => new MapMarker(new Vector2(p.Place.Position.X, p.Place.Position.Y), BandOf(p.Level).Color, null, 0, p.Place.Quest, 4.5f, p))]);
        QuestLegend.Visibility = rows.Any(p => p.Place.Quest) ? Visibility.Visible : Visibility.Collapsed;
        Map.SetShapes([]);
        Map.PickedMarker = null;
        PlaceGrid.ItemsSource = rows.OrderBy(p => p.Area, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Level).ToList();
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
        if (fit) await Dispatcher.InvokeAsync(FitToPlaces, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FitToPlaces()
    {
        var points = _places.Where(p => p.Place.World == _shownWorld).Select(p => new Vector2(p.Place.Position.X, p.Place.Position.Y)).ToList();
        if (points.Count == 0)
        {
            Map.FitWorld();
            return;
        }
        var min = points.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
        var max = points.Aggregate(new Vector2(float.MinValue), Vector2.Max);
        Map.FitTo(min - new Vector2(60), max + new Vector2(60));
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToPlaces();

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
        HoverText.Text = at is { } p ? L.T("LootLocator.HoverAt", p.X, p.Y) : L.T("Bestiary.MouseHint");

    private void Map_MarkerPicked(object? sender, MapMarker? marker)
    {
        if (marker?.Tag is not BestiaryPlaceRow row) return;
        ListTabs.SelectedItem = PlacesTab;
        PlaceGrid.SelectedItem = row;
        PlaceGrid.ScrollIntoView(row);
    }

    /// <summary>A place picked: ringed on the map with its encounter's area drawn, and the kind spawned there shown in Kinds and Drops.</summary>
    private void PlaceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaceGrid.SelectedItem is not BestiaryPlaceRow row)
        {
            Map.SetShapes([]);
            return;
        }
        var at = new Vector2(row.Place.Position.X, row.Place.Position.Y);
        if (Map.PickedMarker?.Tag != row) Map.PickedMarker = new MapMarker(at, BandOf(row.Level).Color, null, 0, row.Place.Quest, 4.5f, row);
        Map.SetShapes(row.Place.Area is { Count: > 2 } area ? [new MapShape(area, BandOf(row.Level).Color, true, true, true)] : []);
        if (PlaceGrid.IsKeyboardFocusWithin) Map.FitTo(at - new Vector2(90), at + new Vector2(90));
        if (VariantGrid.ItemsSource is IEnumerable<BestiaryVariantRow> variants
            && variants.FirstOrDefault(v => v.Creature.Template.Equals(row.Place.Template, StringComparison.OrdinalIgnoreCase)) is { } variant)
            VariantGrid.SelectedItem = variant;
    }

    // ---------------------------------------------------------------- exporting

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row || _places.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("LootLocator.ExportTitle"),
            Filter = L.T("LootLocator.CsvFilter"),
            FileName = $"bestiary-{string.Concat(row.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c))}.csv",
            InitialDirectory = Directory.Exists(App.Settings.OutputFolder) ? App.Settings.OutputFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        var csv = new StringBuilder();
        static string Cell(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        csv.AppendLine("world,area,kind,template,spawned by,quest,level,spawn limit,at once,x,y,z,layer"); // loc-ignore: a file's column names
        foreach (var p in _places.OrderBy(p => p.Place.World).ThenBy(p => p.Area))
            csv.AppendLine(string.Join(",", Cell(LootLocatorWindow.WorldName(p.Place.World)), Cell(p.Area), Cell(p.Variant), Cell(p.Place.Template),
                p.Place.Source == BestiarySource.Community ? "community" : "encounter", p.Place.Quest ? "yes" : "no", p.Level, p.Limit, p.AtOnce, // loc-ignore: a file's values
                p.Place.Position.X.ToString("0.###", CultureInfo.InvariantCulture), p.Place.Position.Y.ToString("0.###", CultureInfo.InvariantCulture),
                p.Place.Position.Z.ToString("0.###", CultureInfo.InvariantCulture), Cell(p.Place.Layer)));
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

    // ---------------------------------------------------------------- levels

    /// <summary>The colour bands levels are drawn in, highest first; each is the lowest level it holds.</summary>
    private static readonly (int From, Color Color, Func<string> Text)[] Bands =
    [
        (31, Color.FromRgb(0xE5, 0x48, 0x48), () => L.T("Bestiary.Band31")),
        (21, Color.FromRgb(0xF0, 0x8A, 0x3C), () => L.T("Bestiary.Band21")),
        (11, Color.FromRgb(0xFF, 0xD2, 0x3F), () => L.T("Bestiary.Band11")),
        (6, Color.FromRgb(0xA6, 0xD8, 0x54), () => L.T("Bestiary.Band6")),
        (int.MinValue, Color.FromRgb(0x3A, 0x9B, 0xDC), () => L.T("Bestiary.Band1")),
    ];

    private static (int From, Color Color, Func<string> Text) BandOf(int level) => Bands.First(b => level >= b.From);

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
