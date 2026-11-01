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
using W3ModWorkbench.Core.ActionPoints;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.Localisation;
using Vector2 = System.Numerics.Vector2;

namespace W3ModWorkbench;

/// <summary>What an action point is in a view, with its colour on the map: one of a view's kinds, or of none (Key empty).</summary>
public sealed record ActionPointKind(string Key, Color Color, Func<string> Text);

/// <summary>
/// A way of looking at the action points: which it shows, what kind each is, and what the view is. The built-in ones
/// (<see cref="ActionPointFinderWindow"/>'s <c>BuiltInViews</c>) say what a game system does with the points; the rest
/// are one for each category the job trees accept.
/// </summary>
public sealed record ActionPointView(string Key, Func<string> Title, Func<string> Detail, Func<string> Explain, bool BuiltIn,
    Func<ActionPoint, IReadOnlyList<string>, bool> Includes, Func<ActionPoint, IReadOnlyList<string>, ActionPointKind> KindOf, IReadOnlyList<ActionPointKind> Kinds);

/// <summary>A row of the views' list: a view, its group and how many action points it shows.</summary>
public sealed record ActionPointViewRow(ActionPointView View, string Title, string Detail, string Group, int GroupOrder, int Count)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>A row of the points' list: an action point, what kind it is in the view, its area, tags and job tree, and the words a filter matches.</summary>
public sealed record ActionPointRow(ActionPoint Point, ActionPointKind Kind, string Area, string TagsText, string JobTreeText, string Search)
{
    public string KindText => Kind.Text();
    public string PositionText => LootLocatorWindow.Position(Point.Position);
}

/// <summary>
/// The Action Point Finder: every action point the worlds place on the game's map, by a view (the rain shelters, or
/// a job tree category), with each point's tags, job tree, categories and animations, and for a built-in view what the
/// game makes of it (whether the rain reaction can pick it).
/// </summary>
public partial class ActionPointFinderWindow : Window, IMessageHost
{
    private static ActionPointFinderWindow? _open;

    private sealed record WorldChoice(string Path, string Text)
    {
        public override string ToString() => Text;
    }
    private sealed record LegendRow(Brush Brush, string Text);
    private sealed record WorldPicture(BitmapSource Picture, Vector2 Corner, float Size, GameMinimap? Minimap);

    private ResourceLocator _locator = null!;
    private ActionPointIndex? _index;
    private List<ActionPointViewRow> _views = [];
    private ICollectionView? _viewList;
    private ActionPointViewRow? _selected;
    private List<ActionPointRow> _rows = [];
    private List<ActionPointRow> _shownRows = [];
    private readonly Dictionary<string, Task<WorldPicture?>> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private string? _shownWorld;
    private bool _ignoreWorld;
    private int _version;
    private readonly MinimapDetail _detail;
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly System.Windows.Threading.DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private ActionPointFinderWindow()
    {
        InitializeComponent();
        _detail = new MinimapDetail(Map);
        ModsCheck.IsChecked = App.Settings.ActionPointsWithMods;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _viewList?.Refresh();
            CountText.Text = L.T("ActionPoints.ViewCount", _viewList?.Cast<object>().Count() ?? 0);
        };
        _filterTimer.Tick += async (_, _) =>
        {
            _filterTimer.Stop();
            await ShowWorldAsync(_shownWorld, false);
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
        _open = new ActionPointFinderWindow().Remembered(owner, "ActionPointFinder");
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    // ---------------------------------------------------------------- the views

    private static readonly ActionPointKind PlainKind = new("", Color.FromRgb(0x3A, 0x9B, 0xDC), () => L.T("ActionPoints.KindPoint"));

    /// <summary>The rain shelters' kinds: who the rain reaction can send there, and the points it never picks.</summary>
    private static readonly ActionPointKind[] ShelterKinds =
    [
        new(RainShelters.Man, Color.FromRgb(0x3A, 0x9B, 0xDC), () => L.T("ActionPoints.KindMan")),
        new(RainShelters.Woman, Color.FromRgb(0xE6, 0x6F, 0xB4), () => L.T("ActionPoints.KindWoman")),
        new(RainShelters.Child, Color.FromRgb(0xFF, 0xD2, 0x3F), () => L.T("ActionPoints.KindChild")),
        new(RainShelters.Dwarf, Color.FromRgb(0xF0, 0x8A, 0x3C), () => L.T("ActionPoints.KindDwarf")),
        new("unusable", Color.FromRgb(0xE0, 0x40, 0x40), () => L.T("ActionPoints.KindUnusable")), // loc-ignore: a key
    ];

    private const string RainView = "rain"; // loc-ignore: a key

    /// <summary>
    /// The views that say what a game system does with the points, first in the list. A new one (sleeping spots, a
    /// quest's points) is another entry here: which points it shows, and what kind each is.
    /// </summary>
    private static IEnumerable<ActionPointView> BuiltInViews() =>
    [
        new(RainView, () => L.T("ActionPoints.RainTitle"), () => L.T("ActionPoints.RainDetail"), () => L.T("ActionPoints.RainExplain"), true,
            (p, _) => RainShelters.IsTagged(p),
            (p, categories) => RainShelters.UsableFor(p, categories).FirstOrDefault() is { } usable ? ShelterKinds.First(k => k.Key == usable) : ShelterKinds[^1],
            ShelterKinds),
    ];

    /// <summary>A view of the action points whose job tree accepts a category.</summary>
    private static ActionPointView CategoryView(string category) =>
        new("category:" + category, () => category, () => L.T("ActionPoints.CategoryDetail"), () => L.T("ActionPoints.CategoryExplain", category), false, // loc-ignore: a key
            (_, categories) => categories.Contains(category, StringComparer.OrdinalIgnoreCase), (_, _) => PlainKind, [PlainKind]);

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
            _index = await Task.Run(() => ActionPointIndex.Build(locator, progress, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BusyText.Text = L.T("LootLocator.CouldNotRead", ex.Message);
            return;
        }
        BuildViews();
        Busy.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        StatusText.Text = L.T("ActionPoints.Summary", _index.Points.Count, _index.Points.Select(p => p.World).Distinct().Count(), _index.JobTrees.Count);
        // the view picked before reading again, else the rain shelters
        var again = _selected is { } was ? _views.FirstOrDefault(v => v.View.Key == was.View.Key) : null;
        ViewList.SelectedItem = again ?? _views.FirstOrDefault();
    }

    /// <summary>The views' list: the built-in views, then one for each category the job trees accept, each with how many points it shows.</summary>
    private void BuildViews()
    {
        var index = _index!;
        var views = BuiltInViews().ToList();
        views.AddRange(index.JobTrees.Values.SelectMany(j => j.Categories).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(CategoryView));
        var categories = index.Points.Select(index.CategoriesOf).ToArray();
        _views = [.. views.Select(v =>
        {
            int count = 0;
            for (int i = 0; i < categories.Length; i++)
                if (v.Includes(index.Points[i], categories[i])) count++;
            return new ActionPointViewRow(v, v.Title(), v.Detail(), v.BuiltIn ? L.T("ActionPoints.GroupBuiltIn") : L.T("ActionPoints.GroupCategories"), v.BuiltIn ? 0 : 1, count);
        }).Where(r => r.Count > 0)];
        var source = new CollectionViewSource { Source = _views };
        _viewList = source.View;
        _viewList.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActionPointViewRow.Group)));
        _viewList.SortDescriptions.Add(new SortDescription(nameof(ActionPointViewRow.GroupOrder), ListSortDirection.Ascending));
        _viewList.Filter = o => o is ActionPointViewRow r && (SearchBox.Text.Trim() is not { Length: > 0 } text
            || r.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || r.Detail.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        ViewList.ItemsSource = _viewList;
        CountText.Text = L.T("ActionPoints.ViewCount", _viewList.Cast<object>().Count());
    }

    // ---------------------------------------------------------------- the list

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private async void ModsCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.ActionPointsWithMods = ModsCheck.IsChecked == true;
        App.Settings.Save();
        await LoadAsync();
    }

    private void ViewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewList.SelectedItem is ActionPointViewRow row && row != _selected) _ = ShowAsync(row);
    }

    // ---------------------------------------------------------------- a view

    private async Task ShowAsync(ActionPointViewRow row)
    {
        _selected = row;
        if (_index is not { } index) return;
        int version = ++_version;
        var view = row.View;
        _rows = [.. index.Points.Where(p => view.Includes(p, index.CategoriesOf(p))).Select(p => RowOf(p, view))];
        LegendList.ItemsSource = view.Kinds.Where(k => _rows.Any(r => r.Kind == k)).Select(k => new LegendRow(Frozen(new SolidColorBrush(k.Color)), k.Text())).ToList();
        ShowCard(null);

        var worlds = _rows.GroupBy(r => r.Point.World).OrderByDescending(g => g.Count())
            .Select(g => new WorldChoice(g.Key, L.T("LootLocator.WorldCount", LootLocatorWindow.WorldName(g.Key), g.Count()))).ToList();
        string? keep = _shownWorld is { } shown && worlds.Any(w => w.Path == shown) ? shown : worlds.FirstOrDefault()?.Path;
        _ignoreWorld = true;
        WorldBox.ItemsSource = worlds;
        WorldBox.SelectedItem = worlds.FirstOrDefault(w => w.Path == keep);
        _ignoreWorld = false;
        ExportButton.IsEnabled = _rows.Count > 0;
        MapEmpty.Text = L.T("ActionPoints.NoneHere");
        MapEmpty.Visibility = worlds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Legend.Visibility = worlds.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = L.T("ActionPoints.Found", row.Title, _rows.Count);
        if (worlds.Count == 0)
        {
            _shownWorld = null;
            _detail.Show(null, null);
            Map.SetTerrain(null, default, 0);
            Map.SetMarkers([]);
            PointGrid.ItemsSource = null;
        }
        if (version != _version) return;
        await ShowWorldAsync(keep, true);
    }

    private ActionPointRow RowOf(ActionPoint p, ActionPointView view)
    {
        var categories = _index!.CategoriesOf(p);
        string area = LootLocatorWindow.AreaOf(p.World, p.Layer);
        string tags = string.Join(", ", p.Tags);
        string jobTree = p.JobTree is { } j ? Path.GetFileNameWithoutExtension(j) : "";
        return new ActionPointRow(p, view.KindOf(p, categories), area, tags, jobTree, string.Join(" ", [area, tags, p.JobTree ?? "", .. categories, p.Layer]));
    }

    /// <summary>The middle column: what the view is and where its points are, and the point picked (its tags, job tree, categories and animations).</summary>
    private void ShowCard(ActionPointRow? point)
    {
        if (_selected is not { } row || _index is not { } index) return;
        double scroll = CardScroller.VerticalOffset;
        Card.Children.Clear();
        CardEmpty.Visibility = Visibility.Collapsed;
        Card.Children.Add(new TextBlock { Text = row.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Card.Children.Add(Hint(row.Detail));
        Card.Children.Add(Para(row.View.Explain()));

        Card.Children.Add(Section(L.T("ActionPoints.ByWorld")));
        foreach (var world in _rows.GroupBy(r => r.Point.World).OrderByDescending(g => g.Count()))
            Card.Children.Add(Para(L.T("ActionPoints.CountLine", LootLocatorWindow.WorldName(world.Key), world.Count())));
        if (row.View.Kinds.Count > 1)
        {
            Card.Children.Add(Section(L.T("ActionPoints.ByKind")));
            foreach (var kind in row.View.Kinds)
                if (_rows.Count(r => r.Kind == kind) is var n and > 0) Card.Children.Add(Para(L.T("ActionPoints.CountLine", kind.Text(), n)));
        }

        Card.Children.Add(Section(L.T("ActionPoints.ThePoint")));
        if (point is null)
            Card.Children.Add(Hint(L.T("ActionPoints.PickPoint")));
        else
        {
            var p = point.Point;
            var categories = index.CategoriesOf(p);
            Card.Children.Add(Para($"{LootLocatorWindow.WorldName(p.World)} · {point.Area}"));
            Card.Children.Add(Hint(point.PositionText));
            if (row.View.Key == RainView) Card.Children.Add(Para(RainLine(p, categories)));
            Card.Children.Add(Para(p.Tags.Count > 0 ? L.T("ActionPoints.TagsLine", point.TagsText) : L.T("ActionPoints.NoTags")));
            Card.Children.Add(Para(L.T("ActionPoints.CategoriesLine", categories.Count > 0 ? string.Join(", ", categories) : "–"))); // loc-ignore: a dash for none
            if (p.Component.Length > 0) Card.Children.Add(Para(L.T("ActionPoints.ComponentLine", p.Component)));
            var animations = index.JobTreeOf(p)?.Animations ?? [];
            Card.Children.Add(Section(L.T("ActionPoints.Animations")));
            Card.Children.Add(animations.Count > 0 ? Para(string.Join(", ", animations)) : Hint(L.T("ActionPoints.NoAnimations")));

            Card.Children.Add(Section(L.T("ActionPoints.Files")));
            var buttons = new WrapPanel();
            buttons.Children.Add(SmallButton(L.T("ActionPoints.OpenLayer"), () => _ = DataViewers.OpenLinkAsync(p.Layer, _locator, this, this)));
            if (p.JobTree is { } jobTree) buttons.Children.Add(SmallButton(L.T("ActionPoints.OpenJobTree"), () => _ = DataViewers.OpenLinkAsync(jobTree, _locator, this, this)));
            if (p.Template is { } template) buttons.Children.Add(SmallButton(L.T("LootLocator.OpenTemplate"), () => _ = DataViewers.OpenLinkAsync(template, _locator, this, this)));
            Card.Children.Add(buttons);
            Card.Children.Add(Hint(p.Layer));
            if (p.JobTree is { } path) Card.Children.Add(Hint(path));
        }
        Card.UpdateLayout();
        CardScroller.ScrollToVerticalOffset(point is null ? 0 : scroll);
    }

    /// <summary>Whom the rain reaction can send to a shelter, or why it never picks it.</summary>
    private static string RainLine(ActionPoint p, IReadOnlyList<string> categories)
    {
        var usable = RainShelters.UsableFor(p, categories).ToList();
        if (usable.Count > 0) return L.T("ActionPoints.UsableFor", string.Join(", ", usable.Select(c => ShelterKinds.First(k => k.Key == c).Text())));
        var tagged = p.Tags.Where(t => t.StartsWith("rain_shelter", StringComparison.OrdinalIgnoreCase));
        return L.T("ActionPoints.NotUsable", string.Join(", ", tagged), categories.Count > 0 ? string.Join(", ", categories) : "–"); // loc-ignore: a dash for none
    }

    private TextBlock Section(string text) => new() { Text = text, Style = (Style)FindResource("SectionTitle"), TextWrapping = TextWrapping.Wrap };

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

    /// <summary>A world's points in the view, those the filter leaves: on the map in their kinds' colours (the points with no kind and the odd ones out on top), and in the list.</summary>
    private async Task ShowWorldAsync(string? world, bool fit)
    {
        string filter = FilterBox.Text.Trim();
        _shownRows = [.. _rows.Where(r => r.Point.World == world && (filter.Length == 0 || r.Search.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Area, StringComparer.CurrentCultureIgnoreCase)];
        var kinds = _selected?.View.Kinds ?? [];
        Map.SetMarkers([.. _shownRows.OrderBy(r => kinds.Count > 1 && r.Kind == kinds[^1] ? 1 : 0)
            .Select(r => new MapMarker(At(r.Point.Position), r.Kind.Color, null, 0, false, 4f, r))]);
        Map.SetShapes([]);
        Map.PickedMarker = null;
        PointGrid.ItemsSource = _shownRows;
        PointCountText.Text = L.T("ActionPoints.PointCount", _shownRows.Count);
        ShowCard(null);
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
        if (fit) await Dispatcher.InvokeAsync(() => FitTo(_shownRows), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static Vector2 At(System.Numerics.Vector3 p) => new(p.X, p.Y);

    private void FitTo(IEnumerable<ActionPointRow> rows)
    {
        var points = rows.Select(r => At(r.Point.Position)).ToList();
        if (points.Count == 0)
        {
            Map.FitWorld();
            return;
        }
        var min = points.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
        var max = points.Aggregate(new Vector2(float.MinValue), Vector2.Max);
        Map.FitTo(min - new Vector2(40), max + new Vector2(40));
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitTo(_shownRows);

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
        HoverText.Text = at is { } p ? L.T("LootLocator.HoverAt", p.X, p.Y) : L.T("ActionPoints.MouseHint");

    private void Map_MarkerPicked(object? sender, MapMarker? marker)
    {
        if (marker?.Tag is not ActionPointRow row) return;
        PointGrid.SelectedItem = row;
        PointGrid.ScrollIntoView(row);
    }

    /// <summary>A point picked in the list: ringed on the map and shown on the card.</summary>
    private void PointGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PointGrid.SelectedItem is not ActionPointRow row)
        {
            ShowCard(null);
            return;
        }
        ShowCard(row);
        if (!ReferenceEquals(Map.PickedMarker?.Tag, row)) Map.PickedMarker = new MapMarker(At(row.Point.Position), row.Kind.Color, null, 0, false, 4f, row);
    }

    /// <summary>Double-click: the map moved in close around the point.</summary>
    private void PointGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PointGrid.SelectedItem is not ActionPointRow row) return;
        var at = At(row.Point.Position);
        Map.FitTo(at - new Vector2(30), at + new Vector2(30));
    }

    // ---------------------------------------------------------------- exporting

    /// <summary>Saves every point of the view, in every world, with its kind, tags, categories and job tree, as a CSV file.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_index is not { } index || _selected is not { } view) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("ActionPoints.ExportTitle"),
            Filter = L.T("LootLocator.CsvFilter"),
            FileName = "action_points_" + view.View.Key.Replace(':', '_') + ".csv", // loc-ignore: a file name
            InitialDirectory = Directory.Exists(App.Settings.OutputFolder) ? App.Settings.OutputFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        var csv = new StringBuilder();
        static string Cell(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        static string N(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
        csv.AppendLine("world,area,kind,tags,categories,job tree,component,x,y,z,layer,template"); // loc-ignore: a file's column names
        foreach (var row in _rows.OrderBy(r => r.Point.World).ThenBy(r => r.Area, StringComparer.CurrentCultureIgnoreCase))
        {
            var p = row.Point;
            csv.AppendLine(string.Join(",", Cell(LootLocatorWindow.WorldName(p.World)), Cell(row.Area), Cell(row.KindText), Cell(string.Join(" ", p.Tags)),
                Cell(string.Join(" ", index.CategoriesOf(p))), Cell(p.JobTree ?? ""), Cell(p.Component), N(p.Position.X), N(p.Position.Y), N(p.Position.Z), Cell(p.Layer), Cell(p.Template ?? "")));
        }
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            await File.WriteAllTextAsync(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            _ = Message.Show(null, L.T("ActionPoints.Exported", _rows.Count), MessageKind.Info, autoClose: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("LootLocator.CouldNotExport"), ex.Message, MessageKind.Error);
        }
    }

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
