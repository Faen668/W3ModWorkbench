using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Numerics;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Shows an entity template (.w2ent): its meshes put together in 3D in the appearance picked (the entity's own and
/// those of the templates the appearance includes), its components laid out as the entity editor has them, and every
/// property of the component picked, the entity and the template, with its appearances, slots, effects and the files
/// it uses. Files open in their viewers where the tool has one.
/// </summary>
public partial class EntityWindow : Window, IMessageHost
{
    /// <summary>The largest a texture is read, from Settings.</summary>
    private static int TextureSize => App.TextureDetail;

    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private EntityFile? _entity;
    private EntityLook? _look;
    private EntityAppearance? _lookWanted;

    /// <summary>Whether it's one of the player's entities, which get a look of Geralt dressed.</summary>
    private bool _player;

    private const string DressedLook = "Geralt, dressed (Workbench)";
    private List<EntityComponent> _partComponents = [];
    private CancellationTokenSource? _meshLoad;
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncing;

    /// <summary>The appearance a scene or cutscene shows the entity in, when it was opened from one.</summary>
    private LookAs? _lookAs;

    /// <summary>
    /// How a scene or cutscene asks for the entity to look: the appearances it names, first that the entity has wins,
    /// else the one the entity gives its voice tag, as the game picks it. <paramref name="From"/> says which, for the window.
    /// </summary>
    public sealed record LookAs(IReadOnlyList<string> Appearances, string? VoiceTag, string From);

    private EntityWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        _origin = origin;
        InitializeComponent();
        // characters face +Y, so the view starts on that side, facing them, a little from above
        View.StartFrom(180, 10);
        Resources["PropertyMenu"] = Menu(("Copy value", CopyValue_Click), ("Copy name", CopyName_Click), ("Copy name and value", CopyBoth_Click));
        Resources["FileMenu"] = Menu(("Copy path", CopyPath_Click));
        FloorToggle.IsChecked = View.ShowFloor = App.Settings.ShowEntityFloor;
        InfoPanel.Visibility = App.Settings.ShowEntityInfo ? Visibility.Visible : Visibility.Collapsed;
        SpeedBox.ItemsSource = Speeds.Select(s => $"{s * 100:0}%").ToList();
        SpeedBox.SelectedIndex = Array.IndexOf(Speeds, 1f);
        FpsBox.ItemsSource = FrameRates.Select(f => f == 0 ? "Max" : $"{f} fps").ToList();
        int remembered = Array.IndexOf(FrameRates, App.Settings.AnimationFps);
        FpsBox.SelectedIndex = remembered >= 0 ? remembered : Array.IndexOf(FrameRates, 30);
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        Title = $"{Path.GetFileName(path)} - Entity";
        InfoText.Text = $"{path}   ·   {source}";
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _meshLoad?.Cancel();
            Pause();
        };
    }

    /// <summary>Whether a file's name says it's an entity this window can show.</summary>
    public static bool CanShow(string name) => EntityFile.IsEntity(name);

    /// <summary>Opens an entity in a window of its own. The files it uses are looked for with <paramref name="locator"/>.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null, LookAs? lookAs = null) =>
        new EntityWindow(path, source, () => data, locator, archive?.FilePath) { _lookAs = lookAs }.Standalone(owner).Show();

    /// <summary>Opens an entity file on disk: one in the project's game files, or one extracted from a bundle.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new EntityWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        try
        {
            var lookAs = _lookAs;
            (_entity, _lookWanted) = await Task.Run(() =>
            {
                var entity = EntityFile.Read(_readData());
                entity.FindOrigins(_locator.ReadFile);
                var wanted = lookAs is null ? null
                    : lookAs.Appearances.Select(n => entity.Appearances.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(a => a is not null)
                      ?? SceneStage.Appearance(entity, lookAs.VoiceTag, _locator);
                return (entity, wanted);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The entity could not be read.\n\n" + ex.Message;
            return;
        }
        var e = _entity;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;

        ShowLook(new EntityLook([.. e.Components], [.. e.Links]));
        ShowEntityAndTemplate(e);
        _ = LoadRedkitAsync(e);
        EntityClassRun.Text = e.EntityClass ?? "(none)";
        IncludeList.ItemsSource = e.Includes.Select(i => new CutsceneLinkItem(new CutsceneLink(i, "Included template"))).ToList();
        NoIncludesText.Visibility = e.Includes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadDependenciesAsync(e);
        AddListTabs(e);
        EffectList.ItemsSource = e.Effects.Select(x => new PropertyRow(x, "effect", "", 0, null)).ToList();
        FileList.ItemsSource = Files(e).Select(l => new CutsceneLinkItem(l)).ToList();

        var looks = new List<object> { "The entity alone" };
        looks.AddRange(e.Appearances.Select(a => a.Name));
        // the player's entities are only a neck seam: the game dresses him in what's equipped, so offer him dressed, and start so
        _player = CutsceneCast.IsPlayer(_path);
        if (_player) looks.Add(DressedLook);
        AppearanceBox.ItemsSource = looks;
        AppearanceBox.SelectedIndex = _player && _lookWanted is null ? looks.Count - 1
            : (_lookWanted ?? e.DefaultAppearance) is { } shown ? e.Appearances.IndexOf(shown) + 1 : 0;
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {e.File.Version}, {Format.Count(e.Components.Count, "component")}, "
                      + $"{Format.Count(e.Appearances.Count, "appearance")}, {Format.Count(e.Includes.Count, "include")}"
                      + (_lookWanted is { } asked && _lookAs is { } from ? $"   ·   shown as {from.From} shows it: {asked.Name}" : "");
    }

    /// <summary>Every file the entity refers to: its includes, the templates of its appearances, and its components' resources.</summary>
    private static IEnumerable<CutsceneLink> Files(EntityFile e) =>
        e.Includes.Select(i => new CutsceneLink(i, "Included template"))
            .Concat(e.Appearances.SelectMany(a => a.Templates.Select(t => new CutsceneLink(t, $"In appearance {a.Name}"))))
            .Concat(e.Components.SelectMany(c => c.Resources.Select(r => new CutsceneLink(r, $"{FileKinds.Of(r)?.Name ?? "File"}, used by {c.Name}"))))
            .Concat(e.File.Imports.Select(i => new CutsceneLink(i.Path, FileKinds.Of(i.Path)?.Name ?? "File")))
            .Where(l => l.Path.Length > 0)
            .DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- properties from REDkit's source files

    // where the entity was opened from (its archive, or the file on disk), which says whether it's one of the game's own
    private readonly string? _origin;
    // REDkit's copies of the entity and of the templates it includes, by game path, to take properties from
    private readonly Dictionary<string, Cr2wFile> _redkit = new(StringComparer.OrdinalIgnoreCase);
    private RedObject? _redkitTemplate;
    private EntityComponent? _shownComponent;

    /// <summary>
    /// An object's properties: from REDkit's copy of the file it's in (<paramref name="gamePath"/>, the entity or the
    /// template it's included from) when that has the same object, otherwise from the file opened.
    /// </summary>
    private List<PropertyRow> Rows(RedObject o, Cr2wFile file, string gamePath, string[] leaveOut)
    {
        if (_redkit.TryGetValue(gamePath, out var redkit))
        {
            var own = o.Export is not null ? RedkitSource.Match(redkit, o, file) : RedkitSource.MatchStruct(_redkitTemplate, o);
            if (own is not null) return PropertyRow.Of(own, redkit, leaveOut: leaveOut);
        }
        return PropertyRow.Of(o, file, leaveOut: leaveOut);
    }

    private void ShowEntityAndTemplate(EntityFile e)
    {
        if (e.Entity is { } entity) ShowRows(EntityProperties, Rows(entity, e.Components.FirstOrDefault()?.File ?? e.File, _path, []));
        ShowRows(TemplateProperties, Rows(e.Template, e.File, _path, ["flatCompiledData"]));
    }

    /// <summary>
    /// Reads REDkit's copies of the entity and the templates its components come from, for one of the game's own, then
    /// shows the properties again from them and says above them where they come from.
    /// </summary>
    private async Task LoadRedkitAsync(EntityFile e)
    {
        var (path, origin) = (_path, _origin);
        var includes = e.Components.Select(c => c.Origin).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (from, files) = await Task.Run(() =>
        {
            var from = RedkitSource.For(path, origin);
            var files = new Dictionary<string, Cr2wFile>(StringComparer.OrdinalIgnoreCase);
            if (from.Redkit is { } own)
            {
                files[path] = own;
                foreach (string include in includes)
                    if (RedkitSource.Read(include) is { } read) files[include] = read.File;
            }
            return (from, files);
        });
        foreach (var (key, file) in files) _redkit[key] = file;
        _redkitTemplate = from.Redkit is { } redkit ? RedkitSource.Match(redkit, e.Template, e.File) : null;
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, "entity");
        if (files.Count == 0) return;
        ShowEntityAndTemplate(e);
        if (_shownComponent is { } c) ShowComponent(c);
    }

    // ---------------------------------------------------------------- the template's lists

    /// <summary>One of a list tab's entries: what it's called, whether it comes from an include, and its properties.</summary>
    private sealed record ListEntry(string Label, bool Included, Func<List<PropertyRow>> Rows, IReadOnlyList<string>? Files = null)
    {
        public override string ToString() => Included ? $"{Label}  (included)" : Label;
    }

    /// <summary>
    /// The tabs REDkit's entity editor has for what a template holds, each a list with the properties of the entry
    /// picked beside it: appearances and colours, slots, AI, inventory, voice tags, animation, gameplay, animation
    /// slots and dismemberment. Most are the template's params, sorted by kind.
    /// </summary>
    private void AddListTabs(EntityFile e)
    {
        var t = e.Template;
        var f = e.File;
        var parameters = t.Array("templateParams").OfType<RedHandle>().Select(f.Object).OfType<RedObject>().ToList();
        static bool Included(RedObject o) => o["wasIncluded"] is true;
        string Named(RedObject o, string otherwise) => o["name"] is string n && n.Length > 0 && n != "None" ? n : otherwise;
        ListEntry Entry(RedObject o, string label, bool included, IReadOnlyList<string>? files = null) => new(label, included, () => Rows(o, f, _path, ["wasIncluded"]), files);
        string Kind(RedObject o) => CutsceneFile.Humanize(o.ClassName.Replace("Param", "").Replace("Template", ""), "C");

        bool IsAnimation(RedObject o) => o.ClassName.StartsWith("CAnim", StringComparison.Ordinal) && !IsAnimationSlots(o);
        bool IsAnimationSlots(RedObject o) => o.ClassName.Contains("Slot", StringComparison.Ordinal);
        bool IsAi(RedObject o) => o.ClassName.StartsWith("CAI", StringComparison.Ordinal) || o.ClassName == "CAttackRangeParam";
        bool IsInventory(RedObject o) => o.ClassName == "CInventoryDefinition";
        bool IsDismemberment(RedObject o) => o.ClassName == "CEntityDismemberment";

        var appearances = t.Array("appearances").OfType<RedObject>().Select(a => Entry(a, a["name"] as string ?? "", Included(a),
            a.Array("includedTemplates").OfType<RedHandle>().Select(f.Import).OfType<Cr2wImport>().Select(i => i.Path).ToList())).ToList();
        var colours = t.Array("coloringEntries").OfType<RedObject>()
            .Select(c => Entry(c, $"{c["appearance"] as string}  ·  {c["componentName"] as string}", false))
            .Concat(e.IncludedColorings.Select(c => new ListEntry($"{c.Entry["appearance"] as string}  ·  {c.Entry["componentName"] as string}   from {c.From}", true,
                () => PropertyRow.Of(c.Entry, c.File)))).ToList();
        var slots = t.Array("slots").OfType<RedObject>().Select(s => Entry(s, s["name"] as string ?? "", Included(s))).ToList();
        var ai = parameters.Where(IsAi).Select(p => Entry(p, Named(p, Kind(p)), Included(p))).ToList();
        var inventory = parameters.Where(IsInventory)
            .SelectMany(p => p.Array("entries").OfType<RedHandle>().Select(f.Object).OfType<RedObject>().Select(x => (Entry: x, Included: Included(p))))
            .Select(x => Entry(x.Entry, $"{x.Entry["category"] as string}:{(x.Entry["initializer"] is RedHandle h && f.Object(h) is { } i ? i["itemName"] as string : null) ?? x.Entry["itemName"] as string ?? "any"}", x.Included)).ToList();
        var voicetags = t.Array("voicetagAppearances").OfType<RedObject>()
            .Select(v => Entry(v, $"{v["voicetag"] as string ?? "None"}  →  {v["appearance"] as string}", false)).ToList();
        var animation = parameters.Where(IsAnimation).Select(p => Entry(p, p.ClassName switch
        {
            "CAnimAnimsetsParam" => $"Animsets: {Named(p, "")}",
            "CAnimBehaviorsParam" => $"Behaviors: {Named(p, "")}",
            "CAnimConstraintsParam" => "Constraints",
            "CAnimMimicParam" => "Mimics",
            "CAnimGlobalParam" => "Global",
            _ => Named(p, Kind(p)),
        }, Included(p))).ToList();
        var gameplay = parameters.Where(p => !IsAi(p) && !IsAnimation(p) && !IsAnimationSlots(p) && !IsInventory(p) && !IsDismemberment(p))
            .Select(p => Entry(p, Named(p, Kind(p)), Included(p))).ToList();
        var animationSlots = parameters.Where(IsAnimationSlots).Select(p => Entry(p, Named(p, Kind(p)), Included(p))).ToList();
        var wounds = parameters.Where(IsDismemberment)
            .SelectMany(p => p.Array("wounds").OfType<RedHandle>().Select(f.Object).OfType<RedObject>().Select(w => Entry(w, w["name"] as string ?? "", Included(p)))).ToList();

        int at = Tabs.Items.IndexOf(EffectsTab);
        Tabs.Items.Insert(at++, ListTab("Appearances", appearances, "It has no appearances."));
        Tabs.Items.Insert(at++, ListTab("Colors", colours, "Neither it nor the templates it includes have colour entries."));
        at++; // after Effects
        Tabs.Items.Insert(at++, ListTab("Slots", slots, "It has no slots."));
        Tabs.Items.Insert(at++, ListTab("AI", ai, "It has no AI settings."));
        Tabs.Items.Insert(at++, ListTab("Inventory", inventory, "It has no inventory."));
        Tabs.Items.Insert(at++, ListTab("Voicetags", voicetags, "It has no voice tags."));
        Tabs.Items.Insert(at++, ListTab("Animation", animation, "It has no animation settings."));
        Tabs.Items.Insert(at++, ListTab("Gameplay", gameplay, "It has no gameplay params."));
        Tabs.Items.Insert(at++, ListTab("Animation Slots", animationSlots, "It has no animation slots."));
        Tabs.Items.Insert(at, ListTab("Dismemberment", wounds, "It has no wounds."));
    }

    /// <summary>A tab with a list on the left and the properties of the entry picked on the right, with the files it uses.</summary>
    private TabItem ListTab(string header, List<ListEntry> entries, string empty)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        var list = new ListBox
        {
            ItemsSource = entries,
            ItemContainerStyle = (Style)FindResource("CompactItem"),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var right = new DockPanel();
        var files = new ItemsControl { ItemTemplate = (DataTemplate)FindResource("FileTemplate"), Margin = new Thickness(4, 0, 0, 6) };
        DockPanel.SetDock(files, Dock.Top);
        var details = new ListBox { Style = (Style)FindResource("PropertyList") };
        right.Children.Add(files);
        right.Children.Add(details);
        Grid.SetColumn(right, 2);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListEntry entry) return;
            ShowRows(details, entry.Rows());
            files.ItemsSource = entry.Files?.Select(p => new CutsceneLinkItem(new CutsceneLink(p, "Template"))).ToList();
        };
        if (entries.Count == 0)
            grid.Children.Add(new TextBlock { Text = empty, Opacity = 0.6, Margin = new Thickness(6, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        else
        {
            grid.Children.Add(list);
            grid.Children.Add(right);
            list.SelectedIndex = 0;
        }
        return new TabItem { Header = entries.Count > 0 ? $"{header} ({entries.Count:N0})" : header, Content = grid };
    }

    // the rows of each property list, so a search goes through them all
    private readonly Dictionary<ListBox, List<PropertyRow>> _shownRows = [];

    /// <summary>Shows property rows in a list, searched as the search box says.</summary>
    private void ShowRows(ListBox list, List<PropertyRow> rows)
    {
        _shownRows[list] = rows;
        PropertyRow.Search(rows, PropertySearch.Text.Trim());
        list.ItemsSource = PropertyRow.View(rows, () => PropertySearch.Text.Trim());
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        string text = PropertySearch.Text.Trim();
        foreach (var (list, rows) in _shownRows)
        {
            PropertyRow.Search(rows, text);
            (list.ItemsSource as System.ComponentModel.ICollectionView)?.Refresh();
        }
    }

    private PropertyRow? RowOf(object sender) =>
        ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is ListBox { SelectedItem: PropertyRow row } ? row : null;

    private void CopyValue_Click(object sender, RoutedEventArgs e) => Copy(RowOf(sender)?.Value);

    private void CopyName_Click(object sender, RoutedEventArgs e) => Copy(RowOf(sender)?.Name);

    private void CopyBoth_Click(object sender, RoutedEventArgs e) => Copy(RowOf(sender) is { } row ? $"{row.Name} = {row.Value}" : null);

    /// <summary>Folds or opens a property's rows.</summary>
    private void Fold_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PropertyRow { HasChildren: true } row) return;
        row.IsExpanded = !row.IsExpanded;
        for (DependencyObject? d = sender as DependencyObject; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is ListBox list)
            {
                (list.ItemsSource as System.ComponentModel.ICollectionView)?.Refresh();
                break;
            }
    }

    // ---------------------------------------------------------------- dependencies

    private List<string> _dependencies = [];

    /// <summary>
    /// Every file the entity depends on, as REDkit lists them: what it and its compiled components refer to, and the
    /// same for every template it includes or its appearances use, followed as far as they go.
    /// </summary>
    private async Task LoadDependenciesAsync(EntityFile entity)
    {
        DependenciesHeading.Text = "Dependencies (finding them…)";
        _dependencies = await Task.Run(() =>
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(EntityFile e)
            {
                foreach (string path in e.File.Imports.Select(i => i.Path).Concat(e.Components.SelectMany(c => c.Resources))
                             .Concat(e.Appearances.SelectMany(a => a.Templates)).Where(p => p.Length > 0))
                {
                    found.Add(path);
                    if (!EntityFile.IsEntity(path) || !seen.Add(path) || seen.Count > 400) continue;
                    try
                    {
                        if (_locator.ReadFile(path) is { } data) Add(EntityFile.Read(data));
                    }
                    catch (InvalidDataException) { }
                }
            }
            Add(entity);
            return found.ToList();
        });
        ShowDependencies();
    }

    private void ShowDependencies()
    {
        var shown = OnlyTemplatesCheck.IsChecked == true ? _dependencies.Where(EntityFile.IsEntity).ToList() : _dependencies;
        DependencyList.ItemsSource = shown;
        DependenciesHeading.Text = $"Dependencies ({shown.Count:N0})";
    }

    private void OnlyTemplates_Click(object sender, RoutedEventArgs e) => ShowDependencies();

    // ---------------------------------------------------------------- the meshes

    private void AppearanceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_entity is null || AppearanceBox.SelectedIndex < 0) return;
        if (_player && AppearanceBox.SelectedIndex == _entity.Appearances.Count + 1)
        {
            _ = ShowMeshesAsync(_entity.DefaultAppearance, dressed: true);
            return;
        }
        var appearance = AppearanceBox.SelectedIndex == 0 ? null : _entity.Appearances[AppearanceBox.SelectedIndex - 1];
        _ = ShowMeshesAsync(appearance);
    }

    /// <summary>
    /// Reads the meshes of an appearance (or the entity alone) and their textures, several at once, and shows them
    /// together. <paramref name="dressed"/> puts Geralt's default gear on the player's entity, as the scene and cutscene
    /// previews dress him: his head, armour, hair and swords, each coloured from its own item's entity.
    /// </summary>
    private async Task ShowMeshesAsync(EntityAppearance? appearance, bool dressed = false)
    {
        _meshLoad?.Cancel();
        var cts = _meshLoad = new CancellationTokenSource();
        if (dressed)
            GeneratedNote.ShowText($"{DressedLook} is put together by the app, not an appearance in the game's files: the player's entity is only "
                + "a neck seam, and the game dresses him in what's equipped. This is his default gear (his head, starting armour and both swords), "
                + "as the scene and cutscene previews dress him. It isn't saved anywhere and changes nothing.");
        else GeneratedNote.Visibility = Visibility.Collapsed;
        var token = cts.Token;
        var e = _entity!;
        ViewText.Text = "Reading the meshes…";
        ViewText.Visibility = Visibility.Visible;
        List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts;
        List<EntityComponent> drawnBy;
        int wanted, missing;
        EntityLook look;
        List<(int Part, int Material, BitmapSource? Picture, MaterialLook? Look, bool LeaveOut)> textures;
        try
        {
            look = await Task.Run(() => e.Look(appearance, _locator.ReadFile), token);
            if (token.IsCancellationRequested) return;
            ShowLook(look);
            (parts, drawnBy, wanted, missing) = await Task.Run(() => ReadMeshes(look, _locator, token), token);
            if (token.IsCancellationRequested) return;
            var coloredBy = parts.Select(_ => (e, appearance?.Name)).ToList();
            if (dressed)
            {
                ViewText.Text = "Dressing Geralt…";
                (parts, drawnBy, coloredBy) = await Task.Run(() => Dress(e, look, parts, drawnBy, coloredBy, token), token);
                if (token.IsCancellationRequested) return;
            }
            // every material's texture read several at once, then put on with the meshes, rather than popping in one by one
            var coloured = parts.Select((part, p) => (part.Mesh, dressed
                ? StageTextures.Coloring(e, coloredBy[p].Item1, coloredBy[p].Item2, drawnBy[p].Name)
                : LookTextures.Coloring(e, appearance?.Name, drawnBy[p].Name))).ToList();
            var progress = new Progress<(int Done, int Total)>(n =>
            {
                if (!token.IsCancellationRequested && n.Total > 0) ViewText.Text = $"Loading textures, {n.Done} of {n.Total}…";
            });
            textures = await StageTextures.LoadAsync(coloured, _locator, Texture, progress, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested) return;
        _partComponents = drawnBy;
        // the animation playing carries on in the new look, from where it was
        (_resumeAt, _resumePlaying) = _clip is null ? (0f, true) : (_time, _playing);
        StopAnimation();
        View.ShowParts(parts, textures);
        _meshesNote = $"{Format.Count(parts.Count, "mesh", "meshes")} shown" + (missing > 0 ? $", {missing:N0} not found or not read" : "")
                    + (appearance is null ? "" : $"  ·  {Format.Count(appearance.Templates.Count, "template")} in {appearance.Name}");
        _syncing = true;
        LodBox.ItemsSource = Enumerable.Range(0, View.LodCount).Select(i => $"LOD {i}").ToList();
        LodBox.SelectedIndex = 0;
        _syncing = false;
        ShowStats();
        if (Graph.Selected is { } selected) Highlight(selected);
        ViewText.Text = parts.Count == 0 ? (wanted == 0 ? "It has no meshes to show." : "None of its meshes could be read.") : "";
        ViewText.Visibility = parts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = FindRigAsync(look, parts, token);
    }

    /// <summary>
    /// The player's look with Geralt's default gear added: what he wears is skinned to his body's rig, and what hangs
    /// on a slot (his swords and their scabbards) is placed where its bone stands at rest.
    /// </summary>
    private (List<(MeshFile Mesh, Matrix4x4 Place)>, List<EntityComponent>, List<(EntityFile, string?)>) Dress(EntityFile entity, EntityLook look,
        List<(MeshFile Mesh, Matrix4x4 Place)> parts, List<EntityComponent> drawnBy, List<(EntityFile, string?)> coloredBy, CancellationToken token)
    {
        parts = [.. parts];
        drawnBy = [.. drawnBy];
        coloredBy = [.. coloredBy];
        if (SceneStage.BodyRig(look, parts, _locator) is not { } rig) return (parts, drawnBy, coloredBy);
        var attachedTo = parts.Select(_ => (-1, Matrix4x4.Identity)).ToList();
        CutsceneCast.Dress(entity, look, rig, _locator, parts, drawnBy, coloredBy, attachedTo, token);
        var rest = new Matrix4x4[rig.Count];
        rig.ToModel(rig.Pose, rest);
        for (int i = 0; i < parts.Count; i++)
            if (attachedTo[i] is { Item1: >= 0 } hung) parts[i] = (parts[i].Mesh, hung.Item2 * rest[hung.Item1]);
        return (parts, drawnBy, coloredBy);
    }

    /// <summary>
    /// The meshes of a look, each read with its buffers and placed where it goes, with the component that draws it;
    /// how many there were to read, and how many weren't found or couldn't be read.
    /// </summary>
    public static (List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> Parts, List<EntityComponent> DrawnBy, int Wanted, int Missing) ReadMeshes(
        EntityLook look, ResourceLocator locator, CancellationToken token)
    {
        var list = EntityFile.MeshesOf(look);
        var read = new List<(MeshFile, System.Numerics.Matrix4x4)>();
        var drawnBy = new List<EntityComponent>();
        int missing = 0;
        foreach (var (path, place, component) in list)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (locator.ReadFile(path) is not { } data)
                {
                    missing++;
                    continue;
                }
                var archive = locator.Find(path)?.Archive;
                read.Add((ClothFile.IsCloth(path) ? ClothFile.Read(data, () => locator.ReadCollision(path))
                    : MeshFile.Read(data, n => locator.ReadBuffer(path, n, archive)), place));
                drawnBy.Add(component);
            }
            catch (InvalidDataException)
            {
                missing++;
            }
        }
        return (read, drawnBy, list.Count, missing);
    }

    /// <summary>
    /// Shows the components of a look in the graph and the list, with a summary like the entity editor's: how many
    /// are streamed, the streaming distance, and the resources they use.
    /// </summary>
    private void ShowLook(EntityLook look)
    {
        _look = look;
        var selected = Graph.Selected;
        Graph.Show(look);
        ComponentList.ItemsSource = look.Components.Select(c => new EntityComponentItem(c)).ToList();
        int streamed = look.Components.Count(c => c.Streamed);
        var resources = look.Components.SelectMany(c => c.Resources).ToList();
        int unique = resources.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int duplicates = resources.GroupBy(r => r, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
        var lines = new List<string>
        {
            $"Streamed components: {streamed:N0}",
            $"Not streamed components: {look.Components.Count - streamed:N0}",
            $"All components: {look.Components.Count:N0}" + (look.Components.Any(c => c.FromAppearance) ? $" ({look.Components.Count(c => c.FromAppearance):N0} from the appearance)" : ""),
        };
        if (_entity?.Entity?["streamingDistance"] is not null) lines.Add($"Streaming distance: {_entity.Entity.Integer("streamingDistance"):N0}");
        lines.Add($"Resources: {unique:N0} unique" + (duplicates > 0 ? $", {duplicates:N0} used by more than one component" : ""));
        if (_entity?.EntityClass is { } cls) lines.Add(cls);
        SummaryText.Text = string.Join("   ·   ", lines);
        // the component picked stays picked when it's in the new look too
        if (selected is not null && look.Components.FirstOrDefault(c => c.Name == selected.Name) is { } again) Graph.Selected = again;
    }

    /// <summary>Marks the meshes a component draws in the 3D view, and says how many triangles they have.</summary>
    private void Highlight(EntityComponent? component)
    {
        var parts = Enumerable.Range(0, _partComponents.Count).Where(i => component is not null && _partComponents[i] == component).ToList();
        int triangles = View.Highlight(parts);
        SelectedText.Text = parts.Count == 0 ? "" : $"Selected: {component!.Name}, {triangles:N0} triangles";
        SelectedText.Visibility = parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Task<BitmapSource?> Texture(string path)
    {
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task))
                _textures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, TextureSize));
            return task;
        }
    }

    private string _meshesNote = "";

    /// <summary>How many meshes are shown and how much they draw at the LOD picked, as the entity editor says it.</summary>
    private void ShowStats()
    {
        var (triangles, vertices, chunks) = View.Stats();
        MeshesText.Text = $"{_meshesNote}  ·  {triangles:N0} triangles, {vertices:N0} vertices, {Format.Count(chunks, "chunk")}";
        ShowInfo();
    }

    private readonly System.Diagnostics.Stopwatch _infoClock = System.Diagnostics.Stopwatch.StartNew();
    private int _framesDrawn;
    private double _framesSince, _framesPerSecond, _poseMilliseconds;

    /// <summary>
    /// The overlay at the top left of the view: how much is drawn, at which LOD and how big it is, the skeleton, and while
    /// an animation plays how often it's drawn and how long a pose takes to work out.
    /// </summary>
    private void ShowInfo()
    {
        InfoPanel.Visibility = App.Settings.ShowEntityInfo ? Visibility.Visible : Visibility.Collapsed;
        if (!App.Settings.ShowEntityInfo) return;
        var (triangles, vertices, chunks) = View.Stats();
        var size = View.Size();
        var lines = new List<string>
        {
            $"Triangles  {triangles,10:N0}",
            $"Vertices   {vertices,10:N0}",
            $"Meshes     {_partComponents.Count,10:N0}",
            $"Chunks     {chunks,10:N0}",
            $"LOD        {View.Lod,10}",
            $"Size  {size.X:0.00} x {size.Y:0.00} x {size.Z:0.00} m",
        };
        if (_posing is { } posing) lines.Add($"Skeleton  {Path.GetFileNameWithoutExtension(posing.RigPath)}, {posing.Rig.Count} bones");
        if (_playing) lines.Add($"Drawing  {_framesPerSecond,5:0} fps, pose {_poseMilliseconds:0.0} ms");
        InfoOverlay.Text = string.Join("\n", lines);
    }

    /// <summary>Counts a frame drawn, and every half second works out the frame rate and puts it in the overlay.</summary>
    private void CountFrame(double poseMilliseconds)
    {
        _framesDrawn++;
        _poseMilliseconds = _poseMilliseconds == 0 ? poseMilliseconds : _poseMilliseconds * 0.8 + poseMilliseconds * 0.2;
        double now = _infoClock.Elapsed.TotalSeconds;
        if (now - _framesSince < 0.5) return;
        _framesPerSecond = _framesDrawn / (now - _framesSince);
        (_framesDrawn, _framesSince) = (0, now);
        ShowStats();
    }

    private void LodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || LodBox.SelectedIndex < 0) return;
        View.Lod = LodBox.SelectedIndex;
        if (Graph.Selected is { } selected) Highlight(selected);
        ShowStats();
    }

    /// <summary>A part clicked in the 3D view picks its component, as clicking it in the graph does.</summary>
    private void View_PartClicked(object? sender, int part)
    {
        var component = part >= 0 && part < _partComponents.Count ? _partComponents[part] : null;
        Graph.Selected = component;
        Graph_ComponentClicked(this, component);
        if (component is not null) Graph.Reveal(component);
    }

    private void Textured_Click(object sender, RoutedEventArgs e) => View.Textured = TexturedToggle.IsChecked == true;

    private void Wire_Click(object sender, RoutedEventArgs e) => View.Wireframe = WireToggle.IsChecked == true;

    private void Fit_Click(object sender, RoutedEventArgs e) => View.FitToView();

    private void Floor_Click(object sender, RoutedEventArgs e) => SetFloor(FloorToggle.IsChecked == true);

    private void SetFloor(bool on)
    {
        FloorToggle.IsChecked = View.ShowFloor = on;
        App.Settings.ShowEntityFloor = on;
        App.Settings.Save();
    }

    // ---------------------------------------------------------------- animation

    private sealed record Posing(string RigPath, Skeleton Rig, Matrix4x4[] Rest, List<MeshFile> Meshes, List<(int Bone, bool ByName)[]> Maps);

    private sealed record AnimationEntry(string Name, int Export)
    {
        public override string ToString() => Name;
    }

    private Posing? _posing;
    private Cr2wFile? _animset;
    private string? _animsetPath;
    private AnimationClip? _clip;
    private BoneTransform[] _pose = [];
    private bool _playing, _scrubbing;
    private float _time, _playedFrom;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private CancellationTokenSource? _animationLoad;

    /// <summary>
    /// Finds the rig the look's skinned meshes are bound to, among the rigs its components use (the one most of their
    /// bones are named in), and the animation sets the entity names, so an animation can be played on it. The bar
    /// stays hidden when there's no rig or nothing skinned.
    /// </summary>
    private async Task FindRigAsync(EntityLook look, List<(MeshFile Mesh, Matrix4x4 Place)> parts, CancellationToken token)
    {
        AnimationBar.Visibility = PlayBar.Visibility = Visibility.Collapsed;
        _posing = null;
        var meshes = parts.Select(p => p.Mesh).ToList();
        if (!meshes.Any(m => m.BoneNames.Length > 0)) return;
        // the body's skeleton, which animations play on, is the moving or animated component's; a dangle component's
        // (a hood's or hair's) also names most of the body's bones, so it's only tried when there's no other
        static bool IsRig(string r) => r.EndsWith(".w2rig", StringComparison.OrdinalIgnoreCase);
        var main = look.Components.Where(c => !c.ClassName.Contains("Dangle", StringComparison.Ordinal)).SelectMany(c => c.Resources).Where(IsRig);
        var rigs = main.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rigs.Count == 0) rigs = look.Components.SelectMany(c => c.Resources).Where(IsRig).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sets = _entity!.File.Imports.Select(i => i.Path).Concat(look.Components.SelectMany(c => c.Resources))
            .Where(r => r.EndsWith(".w2anims", StringComparison.OrdinalIgnoreCase))
            // a face's sets move the face's own rig, not the body's
            .Where(r => !r.Contains(@"\mimics\", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        if (rigs.Count == 0 || sets.Count == 0) return;
        var posing = await Task.Run(() =>
        {
            Posing? best = null;
            int bestNamed = 0;
            foreach (var path in rigs)
            {
                try
                {
                    if (_locator.ReadFile(path) is not { } data) continue;
                    var rig = Skeleton.Read(data);
                    var names = new HashSet<string>(rig.Names, StringComparer.OrdinalIgnoreCase);
                    int named = meshes.Sum(m => m.BoneNames.Count(names.Contains));
                    if (named <= bestNamed) continue;
                    var rest = new Matrix4x4[rig.Count];
                    rig.ToModel(rig.Pose, rest);
                    (best, bestNamed) = (new Posing(path, rig, rest, meshes, meshes.Select(m => Skinning.MapBones(m, rig, rest)).ToList()), named);
                }
                catch (InvalidDataException)
                {
                    // not a rig that can be read; another may do
                }
            }
            return best;
        }, token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
        if (posing is null || token.IsCancellationRequested) return;
        _posing = posing;
        AnimsetBox.ItemsSource = sets.Select(s => new CutsceneLinkItem(new CutsceneLink(s, "Animation set"))).ToList();
        AnimsetBox.DisplayMemberPath = nameof(CutsceneLinkItem.Name);
        AnimationBar.Visibility = PlayBar.Visibility = Visibility.Visible;
        ShowInfo();
        if (_wanted is null && !_stopped) _wanted = await Task.Run(() => StandingIdle(sets), token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
        if (token.IsCancellationRequested) return;
        int index = _wanted is { } wanted ? sets.FindIndex(s => s.Equals(wanted.Set, StringComparison.OrdinalIgnoreCase)) : -1;
        AnimsetBox.SelectedIndex = Math.Max(0, index);
    }

    /// <summary>The animation to show, by its set and name: the one picked last, kept when the look changes.</summary>
    private (string Set, string Name)? _wanted;

    /// <summary>Whether the stop button was pressed, so nothing plays until an animation is picked again.</summary>
    private bool _stopped;

    private float _resumeAt;
    private bool _resumePlaying = true;

    /// <summary>The animation sets read so far, by path, as reading one again is slow.</summary>
    private readonly Dictionary<string, (Cr2wFile File, List<AnimationEntry> List)?> _sets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A standing idle to show the entity in when nothing has been picked: from its dialog sets (the standing ones
    /// first), the neutral standing idle when there's one, else another standing idle, or else "idle" alone, but not one that sits, lies,
    /// kneels or leans, nor a look-at or an additive layer. Null when there's none.
    /// </summary>
    private (string Set, string Name)? StandingIdle(List<string> sets)
    {
        static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        string[] notStanding = ["sit", "lying", "lie_", "kneel", "lean", "lookat", "additive", "mimic", "ground", "table", "horse", "boat"];
        var candidates = sets.Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
            .Where(s => !Has(s.Name, notStanding))
            .Select(s => (s.Path, Rank: Has(s.Name, "dialog_standing") ? 0 : Has(s.Name, "dialog") ? 1 : Has(s.Name, "exploration", "movement", "community") ? 2 : 3))
            .Where(s => s.Rank < 3).OrderBy(s => s.Rank).Take(4);
        foreach (var (path, _) in candidates)
        {
            if (ReadSet(path) is not { } set) continue;
            var names = set.List.Select(a => a.Name).Where(n => Has(n, "idle") && !Has(n, notStanding)).ToList();
            // "determined" is the dialogs' neutral stance, so a plain standing one is the likeliest to look at rest
            var name = names.FirstOrDefault(n => n.Equals("high_standing_determined_idle", StringComparison.OrdinalIgnoreCase))
                ?? names.FirstOrDefault(n => Has(n, "standing_determined_idle"))
                ?? names.FirstOrDefault(n => Has(n, "standing")) ?? names.FirstOrDefault();
            if (name is not null) return (path, name);
        }
        return null;
    }

    /// <summary>An animation set and its animations by name, read once. Null when it can't be read.</summary>
    private (Cr2wFile File, List<AnimationEntry> List)? ReadSet(string path)
    {
        lock (_sets)
            if (_sets.TryGetValue(path, out var known)) return known;
        (Cr2wFile File, List<AnimationEntry> List)? read = null;
        try
        {
            if (_locator.ReadFile(path) is { } data)
            {
                var file = Cr2wFile.Read(data);
                read = (file, file.Exports.Where(x => x.ClassName == "CSkeletalAnimation")
                    .Select(x => new AnimationEntry(file.Object(x.Index)["name"] as string ?? $"#{x.Index}", x.Index))
                    .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());
            }
        }
        catch (InvalidDataException)
        {
        }
        lock (_sets) _sets[path] = read;
        return read;
    }

    /// <summary>Reads the animation set picked and lists its animations by name.</summary>
    private async void AnimsetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnimsetBox.SelectedItem is not CutsceneLinkItem item) return;
        var path = item.Link.Path;
        _animationLoad?.Cancel();
        var token = (_animationLoad = new CancellationTokenSource()).Token;
        AnimationBox.ItemsSource = null;
        var read = await Task.Run(() => ReadSet(path));
        if (token.IsCancellationRequested) return;
        if (read is not { } r || r.List.Count == 0)
        {
            TimeText.Text = read is null ? "not read" : "none in it";
            return;
        }
        (_animset, _animsetPath) = (r.File, path);
        AnimationBox.ItemsSource = r.List;
        // the animation wanted, when it's in this set, plays again
        if (_wanted is { } wanted && wanted.Set.Equals(path, StringComparison.OrdinalIgnoreCase))
            AnimationBox.SelectedItem = r.List.FirstOrDefault(a => a.Name == wanted.Name);
    }

    /// <summary>Reads the animation picked and plays it from the start.</summary>
    private async void AnimationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnimationBox.SelectedItem is not AnimationEntry entry || _animset is not { } set || _animsetPath is not { } path) return;
        bool again = _wanted is { } before && before.Set.Equals(path, StringComparison.OrdinalIgnoreCase) && before.Name == entry.Name;
        var (from, playing) = again ? (_resumeAt, _resumePlaying) : (0f, true);
        (_resumeAt, _resumePlaying) = (0f, true);
        _wanted = (path, entry.Name);
        _stopped = false;
        _animationLoad?.Cancel();
        var token = (_animationLoad = new CancellationTokenSource()).Token;
        Pause();
        var archive = _locator.Find(path)?.Archive;
        var clip = await Task.Run(() =>
        {
            try
            {
                return AnimationClip.Read(set, set.Object(entry.Export), n => _locator.ReadBuffer(path, n, archive));
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                return null;
            }
        });
        if (token.IsCancellationRequested) return;
        // an animation made for another rig moves bones this one hasn't got, or the wrong ones
        bool otherRig = clip is not null && _posing is { } posing && clip.BoneCount > posing.Rig.Count;
        _clip = otherRig ? null : clip;
        ScrubSlider.IsEnabled = PlayButton.IsEnabled = _clip is not null;
        if (_clip is null)
        {
            TimeText.Text = otherRig ? "other skeleton" : "not read";
            TimeText.ToolTip = otherRig ? "This animation is made for a skeleton with more bones than this entity's, such as another kind of creature's, so it can't be played on it" : "The animation could not be read";
            _scrubbing = true;
            ScrubSlider.Value = 0;
            _scrubbing = false;
            return;
        }
        ScrubSlider.Maximum = Math.Max(0.01, _clip.Duration);
        ScrubSlider.SmallChange = 1 / 30.0;
        ScrubSlider.LargeChange = 1;
        _placing = Placing(_clip, _posing!);
        SetTime(from);
        if (playing) Play();
    }

    private Matrix4x4 _placing = Matrix4x4.Identity;

    /// <summary>
    /// Leaves the rig's root as it stands. The game places a character's root itself, where the entity is, and doesn't
    /// take it from the animation; some animations have it turned (a dialog idle exported lying on its back), which
    /// would lay the character down.
    /// </summary>
    private static void KeepRoot(Skeleton rig, BoneTransform[] pose)
    {
        if (rig.Count > 0 && rig.Parents[0] < 0) pose[0] = rig.Pose[0];
    }

    /// <summary>
    /// What brings an animation's actor back to where the entity stands, facing the way it faces: many are made for a
    /// place in a scene, off to one side or turned. The pelvis at the animation's start is moved over where it stands
    /// and turned about the up axis to face the way it does in the rig, and the whole animation moves with it, so an
    /// actor that walks still walks away.
    /// </summary>
    private static Matrix4x4 Placing(AnimationClip clip, Posing posing)
    {
        var rig = posing.Rig;
        int pelvis = Array.FindIndex(rig.Names, n => n.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
        if (pelvis < 0) pelvis = rig.Count > 1 ? 1 : 0;
        var pose = rig.Pose.ToArray();
        clip.Sample(0, pose);
        KeepRoot(posing.Rig, pose);
        var model = new Matrix4x4[rig.Count];
        rig.ToModel(pose, model);
        var rest = posing.Rest[pelvis];
        var start = model[pelvis];
        if (!Matrix4x4.Invert(rest, out var back)) return Matrix4x4.Identity;
        // how far the pelvis is turned about the up axis from how it stands in the rig
        var turned = Vector3.TransformNormal(Vector3.UnitX, back * start);
        float yaw = turned.X * turned.X + turned.Y * turned.Y > 1e-6f ? MathF.Atan2(turned.Y, turned.X) : 0;
        return Matrix4x4.CreateTranslation(-start.M41, -start.M42, 0)
             * Matrix4x4.CreateRotationZ(-yaw)
             * Matrix4x4.CreateTranslation(rest.M41, rest.M42, 0);
    }

    /// <summary>Puts the meshes in the animation's pose at a time, and shows the time on the bar.</summary>
    private void SetTime(float time)
    {
        if (_clip is not { } clip || _posing is not { } posing) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _time = time = Math.Clamp(time, 0, clip.Duration);
        if (_pose.Length != posing.Rig.Count) _pose = new BoneTransform[posing.Rig.Count];
        posing.Rig.Pose.CopyTo(_pose, 0);
        clip.Sample(time, _pose);
        KeepRoot(posing.Rig, _pose);
        var model = new Matrix4x4[posing.Rig.Count];
        posing.Rig.ToModel(_pose, model);
        for (int i = 0; i < model.Length; i++) model[i] *= _placing;
        var bones = posing.Meshes.Select((m, i) => Skinning.BoneMatrices(m, posing.Maps[i], posing.Rest, model)).ToList();
        View.Pose((part, chunk) => part < bones.Count ? Skinning.Pose(chunk, bones[part]) : null);
        if (_playing) CountFrame(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        TimeText.Text = $"{time,5:0.00} / {clip.Duration:0.00} s";
        TimeText.ToolTip = null;
        _scrubbing = true;
        ScrubSlider.Value = time;
        _scrubbing = false;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (_clip is null || _playing) return;
        _playedFrom = _time >= _clip.Duration - 0.01f ? 0 : _time;
        _clock.Restart();
        _nextDraw = 0;
        _playing = true;
        PlayIcon.Text = ""; // pause
        System.Windows.Media.CompositionTarget.Rendering += Tick;
    }

    private void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _clock.Stop();
        PlayIcon.Text = ""; // play
        System.Windows.Media.CompositionTarget.Rendering -= Tick;
        ShowInfo();
    }

    /// <summary>When the next frame is due, on the clock, for the frame rate picked.</summary>
    private double _nextDraw;

    /// <summary>How fast it plays: 1 is the game's speed.</summary>
    private float _speed = 1;

    /// <summary>The speeds offered.</summary>
    private static readonly float[] Speeds = [0.1f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f];

    private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedIndex < 0) return;
        // carries on from where it is, at the new speed
        if (_playing)
        {
            _playedFrom = _time;
            _clock.Restart();
            _nextDraw = 0;
        }
        _speed = Speeds[SpeedBox.SelectedIndex];
    }

    /// <summary>The frame rates offered; 0 is as often as the screen draws.</summary>
    private static readonly int[] FrameRates = [15, 24, 30, 60, 0];

    private void FpsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FpsBox.SelectedIndex < 0) return;
        App.Settings.AnimationFps = FrameRates[FpsBox.SelectedIndex];
        App.Settings.Save();
    }

    // an animation loops, as most of an entity's do in the game; it's posed as often as the frame rate picked, as
    // posing every vertex is work, and fewer frames leave the window more time to answer
    private void Tick(object? sender, EventArgs e)
    {
        if (_clip is not { } clip) return;
        double now = _clock.Elapsed.TotalSeconds;
        int fps = App.Settings.AnimationFps;
        if (fps > 0)
        {
            // frames are due on a schedule, a little early allowed: the screen's frames come about every 16.6 ms, and
            // waiting for a whole 1/60 s from the last one would skip every other and halve the rate
            if (now < _nextDraw - 0.004) return;
            _nextDraw += 1.0 / fps;
            if (_nextDraw < now) _nextDraw = now + 1.0 / fps;
        }
        float time = _playedFrom + (float)now * _speed;
        SetTime(clip.Duration > 0 ? time % clip.Duration : 0);
    }

    private void Scrub_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_scrubbing) return;
        Pause();
        SetTime((float)e.NewValue);
    }

    private void RestPose_Click(object sender, RoutedEventArgs e)
    {
        _wanted = null;
        _stopped = true;
        StopAnimation();
    }

    /// <summary>Stops playing and stands the meshes as they were bound.</summary>
    private void StopAnimation()
    {
        Pause();
        _clip = null;
        _placing = Matrix4x4.Identity;
        _time = 0;
        View.Pose(null);
        ScrubSlider.IsEnabled = PlayButton.IsEnabled = false;
        _scrubbing = true;
        ScrubSlider.Value = 0;
        _scrubbing = false;
        TimeText.Text = "";
        AnimationBox.SelectedItem = null;
    }

    // ---------------------------------------------------------------- components

    private void Graph_ComponentClicked(object? sender, EntityComponent? component)
    {
        Highlight(component);
        if (component is null) return;
        ShowComponent(component);
        _syncing = true;
        ComponentList.SelectedItem = (ComponentList.ItemsSource as List<EntityComponentItem>)?.FirstOrDefault(i => i.Component == component);
        _syncing = false;
        Tabs.SelectedIndex = 0;
    }

    private void ComponentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ComponentList.SelectedItem is not EntityComponentItem item) return;
        Graph.Reveal(item.Component);
        Highlight(item.Component);
        ShowComponent(item.Component);
    }

    /// <summary>A component's properties, and what it hangs from and what hangs from it.</summary>
    private void ShowComponent(EntityComponent c)
    {
        ComponentTitle.Text = $"{c.Name}  ·  {c.ClassName}";
        var said = new List<string>();
        if (c.Origin is { } origin) said.Add((c.FromAppearance ? "from the appearance's " : "included from ") + origin);
        if (c.Overridden) said.Add("its properties are overridden here");
        if (c.Streamed) said.Add("streamed");
        var links = _look?.Links ?? _entity!.Links;
        foreach (var link in links.Where(l => l.Child == c))
            said.Add($"{(link.Kind == "Skinned" ? "skinned to" : "attached to")} {link.Parent.Name}" + (link.Slot is { } slot ? $" at {slot}" : ""));
        var children = links.Where(l => l.Parent == c).Select(l => l.Child.Name).ToList();
        if (children.Count > 0) said.Add($"holds {string.Join(", ", children)}");
        ComponentDetail.Text = string.Join("  ·  ", said);
        _shownComponent = c;
        ShowRows(ComponentProperties, Rows(c.Object, c.File, c.Origin ?? _path, []));
    }

    // ---------------------------------------------------------------- files

    private void File_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = OpenLinkAsync(path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) =>
        Copy(((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement { Tag: string path } ? path : null);

    /// <summary>A right-click menu, shared by every list or link that uses it; each item knows what it's on from where it opened.</summary>
    private static ContextMenu Menu(params (string Header, RoutedEventHandler Click)[] items)
    {
        var menu = new ContextMenu();
        foreach (var (header, click) in items)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>
    /// Opens a file the entity uses in its viewer, found the way the game would find it. A kind of file the tool can't
    /// show has its path copied instead.
    /// </summary>
    private async Task OpenLinkAsync(string path)
    {
        bool viewable = MeshWindow.CanShow(path) || CutsceneWindow.CanShow(path) || SceneWindow.CanShow(path) || CanShow(path) || BehTreeWindow.CanShow(path) || XmlWindow.CanOpen(path) || TextureWindow.CanShow(path) || DataViewers.CanShow(path);
        if (!viewable)
        {
            string kind = FileKinds.Of(path)?.Name.ToLowerInvariant() ?? "this kind of";
            Copy(path, $"There's no viewer for {kind} files yet, so its path is copied:\n{path}");
            return;
        }
        byte[]? data;
        ArchiveEntry? entry;
        try
        {
            (data, entry) = await Task.Run(() => (_locator.ReadFile(path), _locator.Find(path)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            await ShowMessage("It could not be read", $"{path}\n\n{ex.Message}", MessageKind.Error);
            return;
        }
        if (data is null)
        {
            await ShowMessage("Not found", $"{path}\n\nIt isn't in the open project, the archives open in the tool, or the game's bundles.", MessageKind.Warning);
            return;
        }
        string source = entry is null ? "project" : entry.Archive.Name;
        string name = Path.GetFileName(path);
        if (DataViewers.CanShow(path)) DataViewers.Open(path, source, data, _locator, this, entry?.Archive);
        else if (MeshWindow.CanShow(path)) MeshWindow.Open(path, source, data, entry?.Archive, _locator, this);
        else if (CutsceneWindow.CanShow(path)) CutsceneWindow.Open(path, source, data, _locator, this, entry?.Archive);
        else if (SceneWindow.CanShow(path)) SceneWindow.Open(path, source, data, _locator, this, entry?.Archive);
        else if (CanShow(path)) Open(path, source, data, _locator, this, entry?.Archive);
        else if (BehTreeWindow.CanShow(path)) BehTreeWindow.Open(path, source, data, _locator, this, entry?.Archive);
        else if (XmlWindow.CanOpen(path)) XmlWindow.OpenData(name, $"{source}  ·  {path}", data, this, path);
        else if (!TextureWindow.Open(name, $"{source}  ·  {path}", data, this))
            await ShowMessage("It can't be shown", $"{path}\n\nIt isn't a picture the texture viewer understands.", MessageKind.Warning);
    }

    private void Copy(string? text, string? message = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            // a short "Copied" goes by itself; a longer message waits for OK
            _ = Message.Show(null, message ?? $"Copied {text}", MessageKind.Info, primary: message is null ? null : "OK", autoClose: message is null);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen || e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control: PropertySearch.Focus(); break;
            case Key.Escape: Close(); break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.None: View.FitToView(); break;
            case Key.G when Keyboard.Modifiers == ModifierKeys.None: SetFloor(!View.ShowFloor); break;
            case Key.I when Keyboard.Modifiers == ModifierKeys.None:
                App.Settings.ShowEntityInfo = !App.Settings.ShowEntityInfo;
                App.Settings.Save();
                ShowInfo();
                break;
            case Key.Space when Keyboard.Modifiers == ModifierKeys.None && PlayButton.IsEnabled:
                if (_playing) Pause();
                else Play();
                break;
            case Key.W when Keyboard.Modifiers == ModifierKeys.None:
                WireToggle.IsChecked = WireToggle.IsChecked != true;
                View.Wireframe = WireToggle.IsChecked == true;
                break;
            case Key.T when Keyboard.Modifiers == ModifierKeys.None:
                TexturedToggle.IsChecked = TexturedToggle.IsChecked != true;
                View.Textured = TexturedToggle.IsChecked == true;
                break;
            default: return;
        }
        e.Handled = true;
    }
}
