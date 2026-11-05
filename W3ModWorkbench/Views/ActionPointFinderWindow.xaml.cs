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

/// <summary>Which group of the views' list a view is in, in the list's order.</summary>
public enum ActionPointGroup { BuiltIn, Quest, Character, Job, JobTree, Category }

/// <summary>
/// A way of looking at the action points: which it shows, what kind each is, and what the view is. The built-in ones
/// (<see cref="ActionPointFinderWindow"/>'s <c>BuiltInViews</c>) say what a game system does with the points; the rest
/// are one for each quest and story character that places points, each job (its job trees put together), each job
/// tree, and each category the job trees accept. A job's view has the job, a job tree's its path; Keywords are more
/// words the views' search finds it by.
/// </summary>
public sealed record ActionPointView(string Key, Func<string> Title, Func<string> Detail, Func<string> Explain, ActionPointGroup Group,
    Func<ActionPoint, IReadOnlyList<string>, bool> Includes, Func<ActionPoint, IReadOnlyList<string>, ActionPointKind> KindOf, IReadOnlyList<ActionPointKind> Kinds,
    string? JobTree = null, string Keywords = "", ActionPointJob? Job = null);

/// <summary>A row of the views' list: a view, its group, where it is in the list, how many action points it shows, and the words the search finds it by.</summary>
public sealed record ActionPointViewRow(ActionPointView View, string Title, string Detail, string Group, int GroupOrder, int Order, int Count, string Search)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>A row of the points' list: an action point, what kind it is in the view, its area, quest, tags and job tree, and the words a filter matches.</summary>
public sealed record ActionPointRow(ActionPoint Point, ActionPointKind Kind, string Area, string QuestText, string TagsText, string JobTreeText, string Search)
{
    public string KindText => Kind.Text();
    public string PositionText => LootLocatorWindow.Position(Point.Position);
}

/// <summary>
/// The Action Point Finder: every action point the worlds place on the game's map, by a view (the rain shelters, the
/// quests' points or the open world's, one quest, or a job tree category), with each point's quest, tags, job tree,
/// categories and animations, and for a built-in view what the game makes of it (whether the rain reaction can pick it).
/// </summary>
public partial class ActionPointFinderWindow : Window, IMessageHost
{
    private static ActionPointFinderWindow? _open;

    private sealed record WorldChoice(string Path, string Text)
    {
        public override string ToString() => Text;
    }
    /// <summary>A line of the map's key, its tick showing or hiding its pins: a kind's dot, or the diamond of the points a mod adds.</summary>
    private sealed record LegendRow(string Key, Brush Brush, string Text, bool Diamond, bool Shown)
    {
        public bool Round => !Diamond;
    }

    /// <summary>The key of the map's key line for the points a mod adds.</summary>
    private const string ModLegendKey = "mod:"; // loc-ignore: a key

    /// <summary>The kinds (and the mods' points) whose pins are hidden, kept from view to view.</summary>
    private readonly HashSet<string> _hidden = [];
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
    private bool _ignoreGroup;
    private Dictionary<string, ActionPointJob> _jobOf = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The quests' kinds, in <see cref="QuestKind"/>'s order: what sort of quest places the point.</summary>
    private static readonly ActionPointKind[] QuestKinds =
    [
        new(nameof(QuestKind.Main), Color.FromRgb(0xE0, 0x48, 0x48), () => L.T("ActionPoints.QuestMain")),
        new(nameof(QuestKind.Side), Color.FromRgb(0x3A, 0x9B, 0xDC), () => L.T("ActionPoints.QuestSide")),
        new(nameof(QuestKind.Minor), Color.FromRgb(0x4C, 0xBB, 0x6C), () => L.T("ActionPoints.QuestMinor")),
        new(nameof(QuestKind.Contract), Color.FromRgb(0xF0, 0x8A, 0x3C), () => L.T("ActionPoints.QuestContract")),
        new(nameof(QuestKind.TreasureHunt), Color.FromRgb(0xFF, 0xD2, 0x3F), () => L.T("ActionPoints.QuestTreasureHunt")),
        new(nameof(QuestKind.Fistfight), Color.FromRgb(0xA0, 0x70, 0xE0), () => L.T("ActionPoints.QuestFistfight")),
        new(nameof(QuestKind.Gwent), Color.FromRgb(0x40, 0xC8, 0xC8), () => L.T("ActionPoints.QuestGwent")),
        new(nameof(QuestKind.HorseRace), Color.FromRgb(0xB0, 0x88, 0x50), () => L.T("ActionPoints.QuestHorseRace")),
        new(nameof(QuestKind.Other), Color.FromRgb(0xA0, 0xA0, 0xA0), () => L.T("ActionPoints.QuestOther")),
        new(nameof(QuestKind.MainCharacter), Color.FromRgb(0xE0, 0x48, 0x48), () => L.T("ActionPoints.CharacterMain")),
        new(nameof(QuestKind.SecondaryCharacter), Color.FromRgb(0x3A, 0x9B, 0xDC), () => L.T("ActionPoints.CharacterSecondary")),
        new(nameof(QuestKind.Character), Color.FromRgb(0xA0, 0x70, 0xE0), () => L.T("ActionPoints.CharacterOther")),
    ];

    private static ActionPointKind QuestKindOf(QuestKind kind) => QuestKinds[(int)kind];

    /// <summary>What a point is there for, in a job tree's or a category's view: the open world, a quest, or a story character.</summary>
    private static readonly ActionPointKind[] ContextKinds =
    [
        new("open_world", Color.FromRgb(0x3A, 0x9B, 0xDC), () => L.T("ActionPoints.KindOpenWorld")), // loc-ignore: a key
        new("quest", Color.FromRgb(0xE0, 0x48, 0x48), () => L.T("ActionPoints.KindQuest")), // loc-ignore: a key
        new("character", Color.FromRgb(0xA0, 0x70, 0xE0), () => L.T("ActionPoints.KindCharacter")), // loc-ignore: a key
    ];

    private static Func<ActionPoint, IReadOnlyList<string>, ActionPointKind> ContextOf(ActionPointIndex index) =>
        (p, _) => index.QuestOf(p) switch { null => ContextKinds[0], { IsCharacter: true } => ContextKinds[2], _ => ContextKinds[1] };

    private const string RainView = "rain"; // loc-ignore: a key
    private const string QuestViewPrefix = "quest:"; // loc-ignore: a key
    private const string CharacterViewPrefix = "character:"; // loc-ignore: a key

    /// <summary>
    /// The views that say what a game system does with the points, first in the list. A new one (sleeping spots, say)
    /// is another entry here: which points it shows, and what kind each is.
    /// </summary>
    private static IEnumerable<ActionPointView> BuiltInViews(ActionPointIndex index) =>
    [
        new(RainView, () => L.T("ActionPoints.RainTitle"), () => L.T("ActionPoints.RainDetail"), () => L.T("ActionPoints.RainExplain"), ActionPointGroup.BuiltIn,
            (p, _) => RainShelters.IsTagged(p),
            (p, categories) => RainShelters.UsableFor(p, categories).FirstOrDefault() is { } usable ? ShelterKinds.First(k => k.Key == usable) : ShelterKinds[^1],
            ShelterKinds),
        new("quests", () => L.T("ActionPoints.QuestsTitle"), () => L.T("ActionPoints.QuestsDetail"), () => L.T("ActionPoints.QuestsExplain"), ActionPointGroup.BuiltIn, // loc-ignore: a key
            (p, _) => index.QuestOf(p) is { IsCharacter: false }, (p, _) => QuestKindOf(index.QuestOf(p)!.Kind), QuestKinds),
        new("characters", () => L.T("ActionPoints.CharactersTitle"), () => L.T("ActionPoints.CharactersDetail"), () => L.T("ActionPoints.CharactersExplain"), ActionPointGroup.BuiltIn, // loc-ignore: a key
            (p, _) => index.QuestOf(p) is { IsCharacter: true }, (p, _) => QuestKindOf(index.QuestOf(p)!.Kind), QuestKinds[(int)QuestKind.MainCharacter..]),
        new("mods", () => L.T("ActionPoints.ModsTitle"), () => L.T("ActionPoints.ModsDetail"), () => L.T("ActionPoints.ModsExplain"), ActionPointGroup.BuiltIn, // loc-ignore: a key
            (p, _) => p.Mod is not null, ContextOf(index), ContextKinds),
        new("open_world", () => L.T("ActionPoints.OpenWorldTitle"), () => L.T("ActionPoints.OpenWorldDetail"), () => L.T("ActionPoints.OpenWorldExplain"), ActionPointGroup.BuiltIn, // loc-ignore: a key
            (p, _) => index.QuestOf(p) is null, (_, _) => PlainKind, [PlainKind]),
    ];

    /// <summary>
    /// A view of one quest's action points (its code, the name its folders give it, and what sort of quest it is), or of
    /// one story character's (their name, and whether they're a main or a secondary character).
    /// </summary>
    private static ActionPointView QuestView(ActionPointIndex index, QuestLink quest, string? name)
    {
        var colour = QuestKindOf(quest.Kind);
        string code = quest.Code;
        Func<string> explain = quest.IsCharacter ? () => L.T("ActionPoints.CharacterExplain", code, colour.Text())
            : quest.Kind == QuestKind.Other ? () => L.T("ActionPoints.QuestFolderExplain", code)
            : () => L.T("ActionPoints.QuestExplain", code, colour.Text());
        return new((quest.IsCharacter ? CharacterViewPrefix : QuestViewPrefix) + code, () => code, () => name is null ? colour.Text() : $"{name} · {colour.Text()}",
            explain, quest.IsCharacter ? ActionPointGroup.Character : ActionPointGroup.Quest,
            (p, _) => index.QuestOf(p) is { } q && q.Code == code && q.IsCharacter == quest.IsCharacter, (_, _) => colour, [colour]);
    }

    /// <summary>
    /// A view of the action points that use one job tree: one job, such as sit_m_eating_fish, coloured by whether each
    /// point is the open world's, a quest's or a story character's. Found by its animations and categories too.
    /// </summary>
    private static ActionPointView JobTreeView(ActionPointIndex index, string path)
    {
        var info = index.JobTrees.GetValueOrDefault(path);
        var categories = info?.Categories ?? [];
        string name = JobTreeName(path), folder = JobTreeFolder(path);
        return new("jobtree:" + path.ToLowerInvariant(), () => name, // loc-ignore: a key
            () => categories.Count > 0 ? $"{folder} · {string.Join(", ", categories)}" : folder,
            () => L.T("ActionPoints.JobTreeExplain", name, categories.Count > 0 ? string.Join(", ", categories) : "–"), ActionPointGroup.JobTree, // loc-ignore: a dash for none
            (p, _) => string.Equals(p.JobTree, path, StringComparison.OrdinalIgnoreCase), ContextOf(index), ContextKinds,
            path, string.Join(" ", [path, .. categories, .. info?.Animations ?? []]));
    }

    /// <summary>
    /// A view of the action points of one job: the job trees that differ only by who they're for, or by a variant
    /// playing the same animations (<see cref="ActionPointJobs"/>), coloured like a job tree's.
    /// </summary>
    private static ActionPointView JobView(ActionPointIndex index, ActionPointJob job)
    {
        var trees = job.JobTrees.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string title = JobTitle(job), who = BodiesText(job.Bodies), does = string.Join(", ", job.Activities.Take(3));
        string detail = who.Length > 0 && does.Length > 0 ? $"{who} · {does}" : who + does;
        return new("job:" + job.Key, () => title, () => detail, // loc-ignore: a key
            () => L.T("ActionPoints.JobExplain", title, job.JobTrees.Count), ActionPointGroup.Job,
            (p, _) => p.JobTree is { } tree && trees.Contains(tree), ContextOf(index), ContextKinds,
            Keywords: string.Join(" ", [.. job.JobTrees, .. job.Activities, .. job.JobTrees.SelectMany(t => index.JobTrees.GetValueOrDefault(t)?.Categories ?? [])]), Job: job);
    }

    /// <summary>A job's name, from its trees' names: the pose (Sitting), what's done there (eating fish), and the rain's.</summary>
    private static string JobTitle(ActionPointJob job)
    {
        string? pose = job.Pose switch
        {
            "stand" => L.T("ActionPoints.PoseStand"),
            "sit" => L.T("ActionPoints.PoseSit"),
            "kneel" => L.T("ActionPoints.PoseKneel"),
            "lie" => L.T("ActionPoints.PoseLie"),
            "lean" => L.T("ActionPoints.PoseLean"),
            _ => null,
        };
        string what = pose is null ? Capitalised(job.Words) : job.Words.Length > 0 ? L.T("ActionPoints.JobPosed", pose, job.Words) : pose;
        return job.Rain ? L.T("ActionPoints.JobInRain", what) : what;
    }

    private static string Capitalised(string s) => s.Length > 0 ? char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..] : s;

    /// <summary>Whom a job's trees are for, from their names' m, w, d and c: "Men, Women".</summary>
    private static string BodiesText(string bodies)
    {
        var who = bodies.Select(c => c switch
        {
            'm' => L.T("ActionPoints.KindMan"),
            'w' => L.T("ActionPoints.KindWoman"),
            'd' => L.T("ActionPoints.KindDwarf"),
            _ => L.T("ActionPoints.KindChild"),
        }).ToList();
        return string.Join(", ", who);
    }

    /// <summary>A job tree's name: its file's, without the _jt the game's mostly end in.</summary>
    private static string JobTreeName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("_jt", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }

    /// <summary>
    /// The folder a job tree is in under the action points' (dining, work, quest\q702), with the DLC it's from
    /// (bob\work), so two with the same name tell apart.
    /// </summary>
    private static string JobTreeFolder(string path)
    {
        string folder = Path.GetDirectoryName(path) ?? "";
        int at = folder.IndexOf(@"actionpoints\", StringComparison.OrdinalIgnoreCase);
        string under = at >= 0 ? folder[(at + @"actionpoints\".Length)..] : folder;
        var parts = path.Split('\\');
        return parts.Length > 2 && parts[0].Equals("dlc", StringComparison.OrdinalIgnoreCase) ? $"{parts[1]}\\{under}" : under; // loc-ignore: a path
    }

    /// <summary>A view of the action points whose job tree accepts a category, coloured like a job tree's.</summary>
    private static ActionPointView CategoryView(ActionPointIndex index, string category) =>
        new("category:" + category, () => category, () => L.T("ActionPoints.CategoryDetail"), () => L.T("ActionPoints.CategoryExplain", category), ActionPointGroup.Category, // loc-ignore: a key
            (_, categories) => categories.Contains(category, StringComparer.OrdinalIgnoreCase), ContextOf(index), ContextKinds);

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
            // with the mods, the game's own layers too, to tell the points a mod adds from those it leaves be
            var vanilla = ModsCheck.IsChecked == true ? new ResourceLocator([], null, game, otherMods: false) : null;
            _index = await Task.Run(() => ActionPointIndex.Build(locator, progress, CancellationToken.None, vanilla));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BusyText.Text = L.T("LootLocator.CouldNotRead", ex.Message);
            return;
        }
        BuildViews();
        UsageButton.IsEnabled = true;
        Busy.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        StatusText.Text = L.T("ActionPoints.Summary", _index.Points.Count, _index.Points.Select(p => p.World).Distinct().Count(), _index.JobTrees.Count);
        // the view picked before reading again, else the rain shelters
        var again = _selected is { } was ? _views.FirstOrDefault(v => v.View.Key == was.View.Key) : null;
        ViewList.SelectedItem = again ?? _views.FirstOrDefault();
    }

    /// <summary>
    /// The views' list: the built-in views, then one for each quest that places points (by sort, then code), one for
    /// each story character (main, then secondary, by name), one for each job tree (the most used first), and one for
    /// each category the job trees accept, each with how many points it shows.
    /// </summary>
    private void BuildViews()
    {
        var index = _index!;
        var views = BuiltInViews(index).ToList();
        var counts = new Dictionary<ActionPointView, int>(ReferenceEqualityComparer.Instance);
        // a quest's, a character's and a job tree's points counted by grouping, not by asking each of a thousand views of every point
        foreach (var quest in index.Quests.GroupBy(q => (q.Value.Code, q.Value.IsCharacter))
            .OrderBy(g => g.Key.IsCharacter).ThenBy(g => g.First().Value.Kind).ThenBy(g => g.Key.Code.TrimEnd("0123456789".ToCharArray()))
            .ThenBy(g => NumberIn(g.Key.Code)).ThenBy(g => g.Key.Code, StringComparer.Ordinal))
        {
            string? name = quest.Key.IsCharacter
                ? string.Join(", ", quest.Select(q => q.Key.World).Distinct().Select(LootLocatorWindow.WorldName))
                : QuestPoints.NamesIn(quest.Key.Code, quest.Select(q => q.Key.Layer).Distinct()).FirstOrDefault();
            var view = QuestView(index, quest.First().Value, name);
            views.Add(view);
            counts[view] = quest.Count();
        }
        var used = index.Points.Where(p => p.JobTree is not null).GroupBy(p => p.JobTree!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => JobTreeName(g.Key), StringComparer.OrdinalIgnoreCase).ToList();
        var uses = used.ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var jobs = ActionPointJobs.Build(used.Select(g => g.Key), index.JobTrees);
        _jobOf = jobs.SelectMany(j => j.JobTrees.Select(t => (t, j))).ToDictionary(x => x.t, x => x.j, StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs.OrderByDescending(j => j.JobTrees.Sum(t => uses[t])))
        {
            var view = JobView(index, job);
            views.Add(view);
            counts[view] = job.JobTrees.Sum(t => uses[t]);
        }
        foreach (var jobTree in used)
        {
            var view = JobTreeView(index, jobTree.Key);
            views.Add(view);
            counts[view] = jobTree.Count();
        }
        views.AddRange(index.JobTrees.Values.SelectMany(j => j.Categories).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(c => CategoryView(index, c)));
        var categories = index.Points.Select(index.CategoriesOf).ToArray();
        _views = [.. views.Select((v, order) =>
        {
            if (!counts.TryGetValue(v, out int count))
                for (int i = 0; i < categories.Length; i++)
                    if (v.Includes(index.Points[i], categories[i])) count++;
            string group = v.Group switch
            {
                ActionPointGroup.BuiltIn => L.T("ActionPoints.GroupBuiltIn"),
                ActionPointGroup.Quest => L.T("ActionPoints.GroupQuests"),
                ActionPointGroup.Character => L.T("ActionPoints.GroupCharacters"),
                ActionPointGroup.Job => L.T("ActionPoints.GroupJobs"),
                ActionPointGroup.JobTree => L.T("ActionPoints.GroupJobTrees"),
                _ => L.T("ActionPoints.GroupCategories"),
            };
            string title = v.Title(), detail = v.Detail();
            return new ActionPointViewRow(v, title, detail, group, (int)v.Group, order, count, $"{title} {detail} {v.Keywords}");
        }).Where(r => r.Count > 0)];
        var source = new CollectionViewSource { Source = _views };
        _viewList = source.View;
        _viewList.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActionPointViewRow.Group)));
        _viewList.SortDescriptions.Add(new SortDescription(nameof(ActionPointViewRow.GroupOrder), ListSortDirection.Ascending));
        _viewList.SortDescriptions.Add(new SortDescription(nameof(ActionPointViewRow.Order), ListSortDirection.Ascending));
        FillGroupBox();
        // the built-in views always; the rest in the group picked, with every word typed anywhere in the view's name,
        // detail or keywords: "sit eat" finds sitting and eating fish
        _viewList.Filter = o => o is ActionPointViewRow r && (r.View.Group == ActionPointGroup.BuiltIn
            || (ShownGroup() is not { } group || r.View.Group == group)
                && SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => r.Search.Contains(word, StringComparison.CurrentCultureIgnoreCase)));
        ViewList.ItemsSource = _viewList;
        CountText.Text = L.T("ActionPoints.ViewCount", _viewList.Cast<object>().Count());
    }

    private sealed record GroupChoice(ActionPointGroup? Group, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>The groups the list can show under the built-in views, each with how many views it has, and the one picked last.</summary>
    private void FillGroupBox()
    {
        string Count(ActionPointGroup g, string key) => L.T(key, _views.Count(v => v.View.Group == g));
        List<GroupChoice> choices =
        [
            new(null, L.T("ActionPoints.GroupAll")),
            new(ActionPointGroup.Job, Count(ActionPointGroup.Job, "ActionPoints.ShowJobs")),
            new(ActionPointGroup.JobTree, Count(ActionPointGroup.JobTree, "ActionPoints.ShowJobTrees")),
            new(ActionPointGroup.Quest, Count(ActionPointGroup.Quest, "ActionPoints.ShowQuests")),
            new(ActionPointGroup.Character, Count(ActionPointGroup.Character, "ActionPoints.ShowCharacters")),
            new(ActionPointGroup.Category, Count(ActionPointGroup.Category, "ActionPoints.ShowCategories")),
        ];
        _ignoreGroup = true;
        GroupBox.ItemsSource = choices;
        GroupBox.SelectedItem = choices.FirstOrDefault(c => (c.Group?.ToString() ?? "") == App.Settings.ActionPointsGroup) ?? choices[0];
        _ignoreGroup = false;
    }

    private ActionPointGroup? ShownGroup() => (GroupBox.SelectedItem as GroupChoice)?.Group;

    private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreGroup || _viewList is null) return;
        App.Settings.ActionPointsGroup = ShownGroup()?.ToString() ?? "";
        App.Settings.Save();
        _viewList.Refresh();
        CountText.Text = L.T("ActionPoints.ViewCount", _viewList.Cast<object>().Count());
    }

    /// <summary>A quest code's number (309 for q309), to put q99 before q100.</summary>
    private static int NumberIn(string code) => int.TryParse(code.AsSpan(code.TrimEnd("0123456789".ToCharArray()).Length), out int n) ? n : 0;

    /// <summary>A view picked in the list by its key (a quest's, a character's, a job tree's), the search cleared first when it hides it.</summary>
    private void ShowView(string key)
    {
        if (_views.FirstOrDefault(v => v.View.Key == key) is not { } row || _viewList is null) return;
        if (!_viewList.Filter(row))
        {
            if (ShownGroup() is { } group && group != row.View.Group)
            {
                _ignoreGroup = true;
                GroupBox.SelectedItem = GroupBox.Items.Cast<GroupChoice>().First(c => c.Group == row.View.Group);
                _ignoreGroup = false;
                App.Settings.ActionPointsGroup = row.View.Group.ToString();
                App.Settings.Save();
            }
            SearchBox.Text = "";
            _searchTimer.Stop();
            _viewList.Refresh();
            CountText.Text = L.T("ActionPoints.ViewCount", _viewList.Cast<object>().Count());
        }
        ViewList.SelectedItem = row;
        ViewList.ScrollIntoView(row);
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
        ShowLegend();
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

    /// <summary>The map's key: the view's kinds it has points of, and the mods' diamond when a mod adds any, each ticked unless hidden.</summary>
    private void ShowLegend()
    {
        var kinds = _selected?.View.Kinds ?? [];
        var rows = kinds.Where(k => _rows.Any(r => r.Kind == k))
            .Select(k => new LegendRow(k.Key, Frozen(new SolidColorBrush(k.Color)), k.Text(), false, !_hidden.Contains(k.Key))).ToList();
        if (_rows.Any(r => r.Point.Mod is not null))
            rows.Add(new LegendRow(ModLegendKey, Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8))), L.T("ActionPoints.LegendMod"), true, !_hidden.Contains(ModLegendKey)));
        LegendList.ItemsSource = rows;
    }

    /// <summary>A key line's tick: its pins shown or hidden, on the map and in the list, in this view and the next.</summary>
    private async void Legend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: LegendRow row } box) return;
        if (box.IsChecked == true) _hidden.Remove(row.Key);
        else _hidden.Add(row.Key);
        await ShowWorldAsync(_shownWorld, false);
    }

    private bool IsShown(ActionPointRow r) => !_hidden.Contains(r.Kind.Key) && (r.Point.Mod is null || !_hidden.Contains(ModLegendKey));

    private ActionPointRow RowOf(ActionPoint p, ActionPointView view)
    {
        var categories = _index!.CategoriesOf(p);
        string area = LootLocatorWindow.AreaOf(p.World, p.Layer);
        string tags = string.Join(", ", p.Tags);
        string jobTree = p.JobTree is { } j ? Path.GetFileNameWithoutExtension(j) : "";
        string quest = _index.QuestOf(p)?.Code ?? "";
        string job = p.JobTree is { } tree && _jobOf.TryGetValue(tree, out var pointJob) ? JobTitle(pointJob) : "";
        return new ActionPointRow(p, view.KindOf(p, categories), area, quest, tags, jobTree, string.Join(" ", [area, quest, tags, job, p.JobTree ?? "", .. categories, p.Layer, p.Mod ?? ""]));
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
        var areas = _rows.GroupBy(r => (r.Point.World, r.Area)).OrderByDescending(g => g.Count()).ToList();
        if (areas.Count > 1)
        {
            Card.Children.Add(Section(L.T("ActionPoints.TopAreas", areas.Count)));
            foreach (var area in areas.Take(8))
                Card.Children.Add(Para(L.T("ActionPoints.CountLine", $"{area.Key.Area} ({LootLocatorWindow.WorldName(area.Key.World)})", area.Count())));
        }
        if (row.View.Job is { } job)
        {
            Card.Children.Add(Section(L.T("ActionPoints.WhatTheyDo")));
            Card.Children.Add(Para(job.Activities.Count > 0 ? string.Join(", ", job.Activities) : "–")); // loc-ignore: a dash for none
            if (BodiesText(job.Bodies) is { Length: > 0 } who) Card.Children.Add(Para(L.T("ActionPoints.JobWho", who)));
            Card.Children.Add(Section(L.T("ActionPoints.JobTreesOf", job.JobTrees.Count)));
            foreach (var tree in job.JobTrees)
            {
                var categories = index.JobTrees.GetValueOrDefault(tree)?.Categories ?? [];
                int uses = _rows.Count(r => string.Equals(r.Point.JobTree, tree, StringComparison.OrdinalIgnoreCase));
                var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var show = SmallButton(L.T("ActionPoints.ShowTree"), () => ShowView("jobtree:" + tree.ToLowerInvariant())); // loc-ignore: a key
                show.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(show, Dock.Right);
                line.Children.Add(show);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = L.T("ActionPoints.CountLine", JobTreeName(tree), uses), TextWrapping = TextWrapping.Wrap });
                text.Children.Add(Hint($"{JobTreeFolder(tree)} · {(categories.Count > 0 ? string.Join(", ", categories) : "–")}")); // loc-ignore: a dash for none
                line.Children.Add(text);
                Card.Children.Add(line);
            }
        }
        if (row.View.JobTree is { } viewTree)
        {
            var info = index.JobTrees.GetValueOrDefault(viewTree);
            Card.Children.Add(Section(L.T("ActionPoints.TheJobTree")));
            Card.Children.Add(Para(L.T("ActionPoints.CategoriesLine", info?.Categories.Count > 0 ? string.Join(", ", info.Categories) : "–"))); // loc-ignore: a dash for none
            Card.Children.Add(info?.Animations.Count > 0 ? Para(L.T("ActionPoints.AnimationsLine", string.Join(", ", info.Animations))) : Hint(L.T("ActionPoints.NoAnimations")));
            Card.Children.Add(SmallButton(L.T("ActionPoints.OpenJobTree"), () => _ = DataViewers.OpenLinkAsync(viewTree, _locator, this, this)));
            Card.Children.Add(Hint(viewTree));
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
            if (p.Mod is { } mod) Card.Children.Add(Para(L.T("ActionPoints.AddedByMod", mod)));
            else if (p.ModChanged is { } changer) Card.Children.Add(Para(L.T("ActionPoints.ChangedByMod", changer)));
            if (row.View.Key == RainView) Card.Children.Add(Para(RainLine(p, categories)));
            var jumps = new WrapPanel();
            if (index.QuestOf(p) is { } quest)
            {
                Card.Children.Add(Para(QuestLine(quest)));
                string key = (quest.IsCharacter ? CharacterViewPrefix : QuestViewPrefix) + quest.Code;
                if (row.View.Key != key) jumps.Children.Add(SmallButton(L.T("ActionPoints.ShowQuest", quest.Code), () => ShowView(key)));
            }
            else Card.Children.Add(Para(L.T("ActionPoints.NoQuest")));
            if (p.JobTree is { } pointTree && _jobOf.TryGetValue(pointTree, out var pointJob) && row.View.Job != pointJob)
                jumps.Children.Add(SmallButton(L.T("ActionPoints.ShowJob", JobTitle(pointJob)), () => ShowView("job:" + pointJob.Key))); // loc-ignore: a key
            if (p.JobTree is { } ownTree && row.View.JobTree is null)
                jumps.Children.Add(SmallButton(L.T("ActionPoints.ShowJobTree", JobTreeName(ownTree)), () => ShowView("jobtree:" + ownTree.ToLowerInvariant()))); // loc-ignore: a key
            if (jumps.Children.Count > 0) Card.Children.Add(jumps);
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

    /// <summary>The quest a point is placed for, and where that was read from.</summary>
    private static string QuestLine(QuestLink quest) => quest.Source switch
    {
        QuestSource.Tag => L.T("ActionPoints.QuestFromTag", quest.Code, QuestKindOf(quest.Kind).Text(), quest.From ?? ""),
        QuestSource.Folder => L.T("ActionPoints.QuestFromFolder", quest.Code),
        QuestSource.Character => L.T("ActionPoints.QuestFromCharacter", quest.Code, QuestKindOf(quest.Kind).Text()),
        QuestSource.NamedLayer => L.T("ActionPoints.QuestFromNamedLayer", quest.Code, QuestKindOf(quest.Kind).Text()),
        _ => L.T("ActionPoints.QuestFromPath", quest.Code, QuestKindOf(quest.Kind).Text()),
    };

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
        var filter = FilterBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _shownRows = [.. _rows.Where(r => r.Point.World == world && IsShown(r) && filter.All(word => r.Search.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Area, StringComparer.CurrentCultureIgnoreCase)];
        var kinds = _selected?.View.Kinds ?? [];
        // a mod's points are diamonds, drawn over the game's
        Map.SetMarkers([.. _shownRows.OrderBy(r => r.Point.Mod is not null).ThenBy(r => kinds.Count > 1 && r.Kind == kinds[^1] ? 1 : 0)
            .Select(r => new MapMarker(At(r.Point.Position), r.Kind.Color, null, 0, r.Point.Mod is not null, 4f, r))]);
        Map.SetShapes([]);
        Map.PickedMarker = null;
        PointGrid.ItemsSource = _shownRows;
        int hidden = _rows.Count(r => r.Point.World == world && !IsShown(r));
        PointCountText.Text = hidden > 0 ? L.T("ActionPoints.PointCountHidden", _shownRows.Count, hidden) : L.T("ActionPoints.PointCount", _shownRows.Count);
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
        if (!ReferenceEquals(Map.PickedMarker?.Tag, row)) Map.PickedMarker = new MapMarker(At(row.Point.Position), row.Kind.Color, null, 0, row.Point.Mod is not null, 4f, row);
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
        csv.AppendLine("world,area,kind,quest,tags,categories,job tree,component,x,y,z,layer,template,mod"); // loc-ignore: a file's column names
        foreach (var row in _rows.OrderBy(r => r.Point.World).ThenBy(r => r.Area, StringComparer.CurrentCultureIgnoreCase))
        {
            var p = row.Point;
            csv.AppendLine(string.Join(",", Cell(LootLocatorWindow.WorldName(p.World)), Cell(row.Area), Cell(row.KindText), Cell(row.QuestText), Cell(string.Join(" ", p.Tags)),
                Cell(string.Join(" ", index.CategoriesOf(p))), Cell(p.JobTree ?? ""), Cell(p.Component), N(p.Position.X), N(p.Position.Y), N(p.Position.Z), Cell(p.Layer), Cell(p.Template ?? ""), Cell(p.Mod ?? "")));
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

    /// <summary>
    /// Saves every job tree's use as a CSV file, the most used first: how many points use it, of the open world's, the
    /// quests' and the story characters', in how many areas and which worlds, with its categories and animations.
    /// </summary>
    private async void ExportUsage_Click(object sender, RoutedEventArgs e)
    {
        if (_index is not { } index) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("ActionPoints.ExportUsageTitle"),
            Filter = L.T("LootLocator.CsvFilter"),
            FileName = "job_tree_usage.csv", // loc-ignore: a file name
            InitialDirectory = Directory.Exists(App.Settings.OutputFolder) ? App.Settings.OutputFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        var csv = new StringBuilder();
        static string Cell(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        csv.AppendLine("job,job tree,name,folder,categories,uses,open world,quest,story character,areas,worlds,animations,path"); // loc-ignore: a file's column names
        var trees = index.Points.Where(p => p.JobTree is not null).GroupBy(p => p.JobTree!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ToList();
        foreach (var tree in trees)
        {
            var info = index.JobTrees.GetValueOrDefault(tree.Key);
            int quest = tree.Count(p => index.QuestOf(p) is { IsCharacter: false }), character = tree.Count(p => index.QuestOf(p) is { IsCharacter: true });
            csv.AppendLine(string.Join(",", Cell(_jobOf.TryGetValue(tree.Key, out var job) ? JobTitle(job) : ""), Cell(Path.GetFileNameWithoutExtension(tree.Key)), Cell(JobTreeName(tree.Key)), Cell(JobTreeFolder(tree.Key)),
                Cell(string.Join(" ", info?.Categories ?? [])), tree.Count(), tree.Count() - quest - character, quest, character,
                tree.Select(p => (p.World, LootLocatorWindow.AreaOf(p.World, p.Layer))).Distinct().Count(),
                Cell(string.Join(" ", tree.Select(p => LootLocatorWindow.WorldName(p.World)).Distinct())), Cell(string.Join(" ", info?.Animations ?? [])), Cell(tree.Key)));
        }
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            await File.WriteAllTextAsync(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            _ = Message.Show(null, L.T("ActionPoints.ExportedUsage", trees.Count), MessageKind.Info, autoClose: true);
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
