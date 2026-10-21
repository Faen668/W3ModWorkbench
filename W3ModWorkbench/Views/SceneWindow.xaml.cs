using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Shows a scene (.w2scene) laid out as REDkit's scene editor is: the scene's own tabs above (the screenplay, every
/// part as a card in flow order with its lines in words; the starting conditions; the actors it defines; the files it
/// uses), the flow below as a graph (as the writers laid it out in REDkit when REDkit's copy is there) and its dialog
/// sets, and what's picked at the side: its properties (from REDkit's copy for one of the game's own), the scene's
/// cameras, the animations its timelines play, and a section's versions. Lines can be played, and the whole read
/// through following the flow.
/// </summary>
public partial class SceneWindow : Window, IMessageHost
{
    // the language the lines were last shown in, kept for the next scene opened
    private static string _language = "en";

    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private SceneFile? _scene;
    private Cr2wFile? _redkit;
    private PropertySource _propertySource = PropertySource.Own;
    private string? _redkitPath;
    private Dictionary<SceneNode, Point> _places = [];
    private readonly Dictionary<SceneNode, SceneSectionTimeline> _timelines = [];
    private bool _placesFromRedkit;
    private GameStringIndex? _strings;
    private List<SceneNodeItem> _items = [];
    private readonly Stack<SceneNode> _back = new();
    private bool _syncing;
    private int _stringsLoad;

    private SceneWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        StageLoading.Prompt("Preview", PreviewPrompt);
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = $"{Path.GetFileName(path)} - Scene";
        InfoText.Text = $"{path}   ·   {source}";
        LanguageBox.ItemsSource = W3Strings.Languages;
        LanguageBox.SelectedItem = W3Strings.Language(_language) ?? W3Strings.Languages[0];
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            StopReading();
            StopSection();
            _closing.Cancel();
        };
    }

    /// <summary>Whether a file's name says it's a scene this window can show.</summary>
    public static bool CanShow(string name) => SceneFile.IsScene(name);

    /// <summary>
    /// Opens a scene in a window of its own. The files it uses are looked for with <paramref name="locator"/>;
    /// <paramref name="archive"/> is where it came from, which says whether it's one of the game's own.
    /// </summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new SceneWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    /// <summary>Opens a scene file on disk: one in the project's game files, or one extracted from a bundle.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new SceneWindow(Core.Projects.FileSafety.GamePathOf(file) ?? Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    /// <summary>
    /// The words of the game's strings in the language last picked here, for the preview pane; or why there are none
    /// (no game folder, or the files couldn't be read).
    /// </summary>
    public static async Task<(Func<uint, string?>? Text, string? Note)> LinesAsync()
    {
        if (GameLocator.Resolve(null) is not { } game) return (null, "The game folder isn't set, so lines show as string IDs.");
        if (W3Strings.Language(_language) is not { } language) return (null, null);
        try
        {
            var strings = await GameStringIndex.GetAsync(game, language);
            return (id => strings.WithId(id) is { Count: > 0 } found ? found[^1].Text : null, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, "The game's strings could not be read, so lines show as string IDs.");
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            _scene = await Task.Run(() => SceneFile.Read(_readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The scene could not be read.\n\n" + ex.Message;
            return;
        }
        var s = _scene;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        LinkList.ItemsSource = s.Links.Select(l => new CutsceneLinkItem(l)).ToList();
        ActorGrid.ItemsSource = SceneActorRow.Of(s);
        StartGrid.ItemsSource = SceneVoicetagRow.Of(s);
        await ShowStagingAsync(s);
        Rebuild();
        int lines = s.Lines.Count(), choices = s.Nodes.SelectMany(n => n.Elements).Count(e => e.Kind == SceneElementKind.Choice);
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {s.File.Version}, {Format.Count(s.Nodes.Count, "part")}, "
                      + $"{Format.Count(lines, "line")}, {Format.Count(choices, "choice")}, {Format.Count(s.Actors.Count, "actor")}";
        await LoadStringsAsync();
    }

    /// <summary>Reads the game's strings in the language picked, then shows the lines in words.</summary>
    private async Task LoadStringsAsync()
    {
        if (_scene is null || LanguageBox.SelectedItem is not W3Language language) return;
        int load = ++_stringsLoad;
        string? game = GameLocator.Resolve(null);
        if (game is null)
        {
            SearchPlaceholder.Text = "The game folder isn't set, so lines show as string IDs. Set it in Settings.";
            return;
        }
        SearchPlaceholder.Text = $"Reading the {language.Handle} strings…";
        try
        {
            var index = await GameStringIndex.GetAsync(game, language);
            if (load != _stringsLoad) return;
            _strings = index;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SearchPlaceholder.Text = "The game's strings could not be read, so lines show as string IDs.";
            return;
        }
        SearchPlaceholder.Text = "Search lines, speakers and sections  (Ctrl+F)";
        Rebuild();
    }

    private string? Text(uint id) => _strings?.WithId(id) is { Count: > 0 } found ? found[^1].Text : null;

    /// <summary>Makes the cards again, after the strings arrive or the pauses are shown or hidden, keeping the one selected.</summary>
    private void Rebuild()
    {
        if (_scene is null) return;
        var selected = (Cards.SelectedItem as SceneNodeItem)?.Node;
        var speakers = SceneNodeItem.SpeakerBrushes(_scene);
        _items = _scene.Nodes.Select(n => new SceneNodeItem(n, Text, PausesToggle.IsChecked == true, speakers)).ToList();
        ApplyFilter();
        Graph.Show(_scene, _places, id => Text(id));
        if (selected is not null) Select(selected, scroll: false);
    }

    // ---------------------------------------------------------------- search

    private void ApplyFilter()
    {
        string search = SearchBox.Text.Trim();
        Cards.ItemsSource = search.Length == 0 ? _items : _items.Where(i => i.Matches(search)).ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not W3Language language) return;
        _language = language.Handle;
        if (IsLoaded) _ = LoadStringsAsync();
        if (IsLoaded) _ = RetimeAsync();
    }

    /// <summary>Times the lines again to the voices of the language picked, and shows the section shown with them.</summary>
    private async Task RetimeAsync()
    {
        if (_scene is not { } s || _timelines.Count == 0 || _reading || _sectionPlaying is not null) return;
        await ReadTimelinesAsync(s);
        // the faces say the lines in that language too
        _lipSyncs.Clear();
        if (_timelineNode is not { } node) return;
        _timelineNode = null;
        ShowTimeline(node);
    }

    private void Pauses_Click(object sender, RoutedEventArgs e) => Rebuild();

    // ---------------------------------------------------------------- following the flow

    /// <summary>Selects a part's card and scrolls to it, showing all the cards again if the search hides it.</summary>
    private void Select(SceneNode node, bool scroll = true)
    {
        var item = _items.FirstOrDefault(i => i.Node == node);
        if (item is null) return;
        if (Cards.ItemsSource is List<SceneNodeItem> shown && !shown.Contains(item)) SearchBox.Clear();
        _syncing = true;
        Cards.SelectedItem = item;
        _syncing = false;
        Graph.Select(node, scroll);
        ShowPart(node);
        if (!scroll) return;
        Cards.ScrollIntoView(item);
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        // an option's button carries its link item, a part's way out the link itself
        var link = (sender as FrameworkElement)?.Tag switch { SceneLinkItem item => item.Link, SceneLink l => l, _ => null };
        if (link?.Target is not { } target) return;
        if (FindCard(sender as DependencyObject) is { } from) _back.Push(from.Node);
        BackButton.IsEnabled = _back.Count > 0;
        Select(target);
        // reading through, the way picked is where it goes on
        if (_reading) _ = ReadFromAsync(target);
    }

    /// <summary>The card a button is on.</summary>
    private static SceneNodeItem? FindCard(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ListBoxItem { DataContext: SceneNodeItem item }) return item;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (_back.Count == 0) return;
        Select(_back.Pop());
        BackButton.IsEnabled = _back.Count > 0;
    }

    private void Cards_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Cards.SelectedItem is not SceneNodeItem item) return;
        Graph.Select(item.Node);
        ShowPart(item.Node);
    }

    private void Graph_NodeClicked(object? sender, SceneNode node) => Select(node);

    private void GraphFit_Click(object sender, RoutedEventArgs e) => Graph.FitAll();

    // ---------------------------------------------------------------- what the scene keeps besides its flow

    /// <summary>
    /// REDkit's copy (for the graph's layout and the properties), the dialog sets, the cameras, the animations the
    /// timelines play, and the starts' settings. Reads off the UI thread.
    /// </summary>
    private async Task ShowStagingAsync(SceneFile s)
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        _redkit = from.Redkit;
        _propertySource = from.Source;
        SourceNoteBar.ShowText(RedkitSource.TimelineNote(from.Source, from.RedkitPath ?? _origin, "scene"));
        _redkitPath = from.RedkitPath ?? origin;
        (_places, _placesFromRedkit) = await Task.Run(() => SceneGraphLayout.Layout(s, _redkit));
        GraphNote.Text = _placesFromRedkit
            ? "Laid out as in REDkit's copy of the scene. Ctrl and the wheel zoom; drag with the right button to move about."
            : "Laid out left to right by the flow (REDkit's layout is kept only in its own copy of the game's scenes). Ctrl and the wheel zoom; drag with the right button to move about.";

        var root = s.File.First("CStoryScene");
        var dialogsets = _dialogsets = root is null ? [] : SceneStaging.Dialogsets(s.File, root);
        DialogsetList.ItemsSource = SceneDialogsetRow.Of(dialogsets);
        if (dialogsets.Count > 0) DialogsetList.SelectedIndex = 0;
        _cameras = root is null ? [] : SceneStaging.Cameras(root);
        CameraGrid.ItemsSource = SceneCameraRow.Of(_cameras);

        var timelines = await ReadTimelinesAsync(s);
        var animations = SceneAnimationRow.Of(timelines.Select(t => t.Timeline));
        AnimationGrid.ItemsSource = animations;
        AnimationNote.Text = animations.Count == 0 ? "The scene's timelines play no animations."
            : $"{Format.Count(animations.Count, "animation")} its sections' timelines play, by actor: body, face (mimics), additive and camera.";

        var input = s.File.Exports.FirstOrDefault(x => x.ClassName == "CStorySceneInput") is { } first ? s.File.Object(first.Index) : null;
        StartProperties.Show(input is null ? [] : Rows(input, ["voicetagMappings", "nextLinkElement"]));
    }

    /// <summary>
    /// Reads the sections' timelines, each line as long as its voice in the language picked when that's longer than
    /// its approved length (voices differ in length from language to language), and keeps them.
    /// </summary>
    private async Task<List<(SceneNode Node, SceneSectionTimeline Timeline)>> ReadTimelinesAsync(SceneFile s)
    {
        var locator = _locator;
        var speech = await SpeechAsync();
        var timelines = await Task.Run(() =>
        {
            var lengths = new Dictionary<uint, float?>();
            float? VoiceLength(uint id)
            {
                if (lengths.TryGetValue(id, out var known)) return known;
                float? length = null;
                try
                {
                    // the lip sync is as long as the voice
                    if (speech.FirstOrDefault(sp => sp.Has(id))?.LipSync(id) is { } lip) length = Core.Animation.LipSync.Read(lip).Duration;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or IndexOutOfRangeException)
                {
                    // one that can't be read keeps its approved length
                }
                return lengths[id] = length;
            }
            return s.Nodes.Where(n => s.File.Object(n.Export) is { ClassName: "CStorySceneSection" or "CStorySceneCutsceneSection" })
                .Select(n => (Node: n, Timeline: SceneSectionTimeline.Read(s.File, s.File.Object(n.Export), CutsceneLines(n.Cutscene, locator), VoiceLength))).ToList();
        });
        foreach (var (node, timeline) in timelines) _timelines[node] = timeline;
        // what's worked out from them is worked out again
        _tracks.Clear();
        _propEvents.Clear();
        return timelines;
    }

    /// <summary>The dialog marks of a cutscene a section plays (when and how long each line is said), or null when it can't be read.</summary>
    private static List<(float Start, float Duration)>? CutsceneLines(string? cutscene, ResourceLocator locator)
    {
        if (cutscene is null || locator.ReadFile(cutscene) is not { } data) return null;
        try
        {
            return CutsceneFile.Read(data).SceneEvents.Where(e => e.Kind == CutsceneEventKind.Dialog).OrderBy(e => e.Start).Select(e => (e.Start, e.Duration)).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A part of the flow's properties, from REDkit's copy when it has one, with the note saying where they came from.</summary>
    private void ShowPart(SceneNode node)
    {
        if (_scene is null) return;
        var o = _scene.File.Object(node.Export);
        PartHeading.Text = $"{node.Number}  {SceneNodeItem.KindName(node.Kind)}: {node.Title}";
        PartProperties.Show(Rows(o));
        ShowTimeline(node);
        if (o.ClassName is "CStorySceneSection" or "CStorySceneCutsceneSection")
        {
            VariantGrid.ItemsSource = SceneVariantRow.Of(_scene.File, o);
            VariantNote.Text = $"The versions of {node.Title}: each language can play its own, timed to its voices; the default plays for any without one.";
        }
        else
        {
            VariantGrid.ItemsSource = null;
            VariantNote.Text = "Only sections have versions. Click a section to see them.";
        }
    }

    /// <summary>An object's properties, from REDkit's copy of it when there is one.</summary>
    private List<PropertyRow> Rows(RedObject o, IEnumerable<string>? leaveOut = null) =>
        _scene is null ? []
            : _redkit is { } redkit && RedkitSource.Match(redkit, o, _scene.File) is { } theirs ? PropertyRow.Of(theirs, redkit, leaveOut)
            : PropertyRow.Of(o, _scene.File, leaveOut);

    // ---------------------------------------------------------------- the scene played in 3D

    private List<SceneDialogset> _dialogsets = [];
    private List<SceneCameraDefinition> _cameras = [];
    private List<StageActor>? _stage;
    private Task? _stageLoad;
    private readonly TaskCompletionSource _stageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(StageActor Actor, int Part)> _stageParts = [];
    private Dictionary<ActorStance, string> _idles = [];
    private Dictionary<SceneNode, SceneDialogset?> _dialogsetOf = [];
    private readonly Dictionary<SceneNode, List<ActorTrack>> _tracks = [];
    private readonly Dictionary<string, Task<System.Windows.Media.Imaging.BitmapSource?>> _stageTextures = new(StringComparer.OrdinalIgnoreCase);
    private const int StageTextureSize = 1024;
    private readonly CancellationTokenSource _closing = new();

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // the lists inside the tabs raise this too
        if (e.Source != MainTabs || MainTabs.SelectedItem is not TabItem { Header: "Preview" }) return;
        BottomTabs.SelectedIndex = 1; // the timeline drives it
    }

    private const string PreviewPrompt = "Plays the scene in 3D with its timeline. Its actors, their textures, its props and every cutscene it "
        + "plays are all loaded first so it plays smoothly, which can take a while for a big scene. Read through loads it too.";

    private void StageLoading_LoadRequested(object? sender, EventArgs e) => StartStageLoad();

    /// <summary>Loads the preview, unless it's loaded or loading already.</summary>
    private void StartStageLoad() => _stageLoad ??= LoadStageAsync();

    /// <summary>
    /// Loads everything the preview plays the first time it's shown, as REDkit loads a scene's entities before it opens
    /// it, so it plays smoothly from the start: the scene's actors (each stood in its sections' dialog sets, with the
    /// idles, changes of stance and animations its sections play read from its animation sets) with all their
    /// textures, and every cutscene the scene plays with its actors and theirs. The view shows how it's getting on.
    /// </summary>
    private async Task LoadStageAsync()
    {
        if (_scene is not { } s) return;
        StageText.Text = "";
        var locator = _locator;
        var timelines = _timelines.ToList();
        var sets = _dialogsets;
        var cutscenes = s.Nodes.Where(n => n.Kind == SceneNodeKind.Cutscene && n.Cutscene is not null).Select(n => n.Cutscene!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var loaded = await StageLoading.RunAsync($"Loading {Path.GetFileNameWithoutExtension(_path)}", async (report, token) =>
        {
            report.Step("Putting the actors together");
            var notes = new List<string>();
            var (cast, idles, bySection) = await Task.Run(() =>
            {
                var idles = SceneStage.Idles(locator);
                var bySection = SceneStage.DialogsetsBySection(s, sets);
                // the animations each actor needs: the idle of every way it stands, its changes of stance and its body animations
                var wanted = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                void Want(string actor, string? name)
                {
                    if (!wanted.TryGetValue(actor, out var names)) wanted[actor] = names = new(StringComparer.OrdinalIgnoreCase);
                    if (name is not null) names.Add(name);
                }
                foreach (var (node, timeline) in timelines)
                    foreach (var track in SceneStage.Tracks(timeline, bySection.GetValueOrDefault(node)))
                    {
                        Want(track.Actor, SceneStage.IdleFor(idles, track.Stance));
                        foreach (var change in track.Changes)
                        {
                            Want(track.Actor, change.Transition);
                            Want(track.Actor, SceneStage.IdleFor(idles, change.Then));
                        }
                        foreach (var animation in track.Animations) Want(track.Actor, animation.Animation);
                        foreach (var additive in track.Additives) Want(track.Actor, additive.Animation);
                        // the face's: its layers' animations, and the face animations played on it
                        foreach (var clip in track.Face.Clips().Concat(track.FaceChanges.SelectMany(c => c.Then.Clips()))) Want(track.Actor, clip);
                        foreach (var animation in track.FaceAnimations) Want(track.Actor, animation.Animation);
                    }
                return (SceneStage.Load(s, wanted, locator, notes, token), idles, bySection);
            }, token);
            report.Step("Reading the camera animations");
            var cameraAnimations = await Task.Run(() => SceneStage.LoadCameraAnimations(timelines.Select(t => t.Value), locator, token), token);
            report.Step("Putting the props together");
            var props = await Task.Run(() => SceneStage.LoadProps(s, timelines.SelectMany(t => SceneStage.ItemsTaken(t.Value)), locator, notes, token), token);
            report.Step("Loading the actors' textures", string.Join(", ", cast.Select(a => a.Name)));
            var parts = cast.SelectMany(a => a.Parts.Select((part, i) =>
                (part.Mesh, StageTextures.Coloring(a.Entity, a.ColoredBy[i].Entity, a.ColoredBy[i].Appearance, a.DrawnBy[i].Name))))
                .Concat(props.SelectMany(p => p.Parts.Select((part, i) => (part.Mesh, StageTextures.Coloring(p.Entity, p.Entity, p.Appearance, p.DrawnBy[i].Name)))))
                .ToList();
            var textures = await StageTextures.LoadAsync(parts, locator, StageTexture, report.Counter, token);
            // every cutscene it plays, ready to go on when its section comes
            for (int i = 0; i < cutscenes.Count; i++)
            {
                string path = cutscenes[i], name = Path.GetFileNameWithoutExtension(path);
                report.Step($"Cutscene {i + 1} of {cutscenes.Count}: reading it", name);
                try
                {
                    var cutscene = await Task.Run(() => locator.ReadFile(path) is { } data ? CutsceneFile.Read(data) : null, token);
                    if (cutscene is null) continue;
                    var playback = await Task.Run(() => CutscenePlayback.Load(cutscene, locator, path, locator.Find(path)?.Archive), token);
                    var prepared = await CutsceneStage.PrepareAsync(cutscene, playback, locator, StageTexture, report, token, $"Cutscene {i + 1} of {cutscenes.Count}: ");
                    _preparedCutscenes[path] = prepared;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                {
                    notes.Add($"The cutscene {name} could not be read: {ex.Message}");
                }
            }
            return new LoadedStage(cast, props, cameraAnimations, idles, bySection, textures, notes);
        }, _closing.Token);
        if (loaded is null)
        {
            // stopped: it can be loaded again
            _stageLoad = null;
            StageLoading.Prompt("Loading was stopped", "Load the preview again to see the scene played in 3D. " + PreviewPrompt[(PreviewPrompt.IndexOf(". ") + 2)..]);
            return;
        }
        StageControls.IsEnabled = true;
        (_idles, _dialogsetOf) = (loaded.Idles, loaded.BySection);
        var stageCast = loaded.Cast;
        _stage = stageCast;
        _stageParts.Clear();
        var shown = new List<(Core.Meshes.MeshFile Mesh, System.Numerics.Matrix4x4 Place)>();
        foreach (var actor in stageCast)
            for (int i = 0; i < actor.Parts.Count; i++)
            {
                shown.Add((actor.Parts[i].Mesh, System.Numerics.Matrix4x4.Identity));
                _stageParts.Add((actor, i));
            }
        _cameraAnimations = loaded.CameraAnimations;
        // the props after the actors
        _props = loaded.Props;
        _propParts.Clear();
        foreach (var prop in _props)
            for (int i = 0; i < prop.Parts.Count; i++)
            {
                shown.Add((prop.Parts[i].Mesh, System.Numerics.Matrix4x4.Identity));
                _propParts.Add((prop, i));
            }
        StageView.ShowParts(shown, loaded.Textures);
        StageText.Text = stageCast.Count == 0 ? "None of the scene's actors could be put together." : "Pick a section to see it played.";
        StageText.Visibility = stageCast.Count == 0 || _timelineNode is null ? Visibility.Visible : Visibility.Collapsed;
        var stageNotes = loaded.Notes;
        StageNotes.Text = string.Join("\n", stageNotes.Take(6)) + (stageNotes.Count > 6 ? $"\n… and {stageNotes.Count - 6} more" : "");
        LayoutStage();
        PoseStage(SectionTimeline.Playhead ?? 0, fit: true);
        _stageReady.TrySetResult();
    }

    /// <summary>What loading the preview read: the actors, how they stand, the dialog sets, their textures, and what went wrong.</summary>
    private sealed record LoadedStage(List<StageActor> Cast, List<StageProp> Props, Dictionary<string, Core.Animation.AnimationClip> CameraAnimations,
        Dictionary<ActorStance, string> Idles, Dictionary<SceneNode, SceneDialogset?> BySection,
        List<(int Part, int Material, System.Windows.Media.Imaging.BitmapSource? Picture, MaterialLook? Look, bool LeaveOut)> Textures, List<string> Notes);

    // the lights the scene defines, by id, and whether each is a spot
    private Dictionary<string, bool>? _lightKinds;

    /// <summary>
    /// A scene's light as the view draws it: a spot or a point light where it is, as bright as it's set (its colour
    /// scaled), fading out to its radius, the faster the more its attenuation.
    /// </summary>
    private static System.Windows.Media.Media3D.Light StageLightOf(StageLight light)
    {
        float strength = Math.Clamp(light.Brightness / 10, 0.15f, 1.6f);
        byte Scaled(byte channel) => (byte)Math.Min(255, channel * strength);
        var color = System.Windows.Media.Color.FromRgb(Scaled(light.Color.R), Scaled(light.Color.G), Scaled(light.Color.B));
        var at = new System.Windows.Media.Media3D.Point3D(light.Position.X, light.Position.Y, light.Position.Z);
        double radius = Math.Max(light.Radius, 0.5), quadratic = (1 + 4 * Math.Clamp(light.Attenuation, 0, 1)) / (radius * radius);
        System.Windows.Media.Media3D.PointLightBase made = light.Spot
            ? new System.Windows.Media.Media3D.SpotLight(color, at, new System.Windows.Media.Media3D.Vector3D(light.Direction.X, light.Direction.Y, light.Direction.Z),
                Math.Clamp(light.OuterAngle, 1, 179), Math.Clamp(Math.Min(light.InnerAngle, light.OuterAngle), 0, 179))
            : new System.Windows.Media.Media3D.PointLight(color, at);
        made.Range = radius;
        made.ConstantAttenuation = 1;
        made.QuadraticAttenuation = quadratic;
        return made;
    }

    // the camera animations the sections play on top of their cameras (their shakes), by name
    private Dictionary<string, Core.Animation.AnimationClip> _cameraAnimations = [];

    // the props, drawn after the actors' meshes, and what each section does with them
    private List<StageProp> _props = [];
    private readonly List<(StageProp Prop, int Part)> _propParts = [];
    private readonly Dictionary<SceneNode, List<PropEvent>> _propEvents = [];

    // the scene's cutscenes, loaded with the preview, by path
    private readonly Dictionary<string, CutsceneStage.PreparedCast> _preparedCutscenes = new(StringComparer.OrdinalIgnoreCase);

    private Task<System.Windows.Media.Imaging.BitmapSource?> StageTexture(string path)
    {
        lock (_stageTextures)
        {
            if (!_stageTextures.TryGetValue(path, out var task))
                _stageTextures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, StageTextureSize));
            return task;
        }
    }

    /// <summary>What each actor does in a section, worked out once.</summary>
    private List<ActorTrack> TracksOf(SceneNode node)
    {
        if (!_tracks.TryGetValue(node, out var tracks))
            _tracks[node] = tracks = _timelines.TryGetValue(node, out var timeline) ? SceneStage.Tracks(timeline, _dialogsetOf.GetValueOrDefault(node)) : [];
        return tracks;
    }

    /// <summary>
    /// Shows the section picked at a time: its actors where its dialog set stands them, in their poses then (an actor not
    /// in it isn't drawn), seen through its camera then when Through the camera is on.
    /// </summary>
    private void PoseStage(double time, bool fit = false)
    {
        if (_stageLoad is null || _timelineNode is not { } node) return;
        bool cutscene = node.Kind == SceneNodeKind.Cutscene && node.Cutscene is not null;
        CutsceneView.Visibility = cutscene ? Visibility.Visible : Visibility.Collapsed;
        StageView.Visibility = StageNotes.Visibility = cutscene ? Visibility.Collapsed : Visibility.Visible;
        if (cutscene)
        {
            StageBars.Visibility = Visibility.Collapsed;
            StageText.Visibility = Visibility.Collapsed;
            if (_timelines.TryGetValue(node, out var lines)) ShowOverlays(lines, time);
            ShowCutscene(node.Cutscene!, time, fit);
            return;
        }
        LayoutStage();
        if (_stage is not { Count: > 0 } stage) return;
        if (!_timelines.TryGetValue(node, out var timeline))
        {
            StageView.Pose((_, chunk) => new System.Numerics.Vector3[chunk.Positions.Length]);
            StageText.Text = "Only sections are played here. Pick a section to see it.";
            StageText.Visibility = Visibility.Visible;
            StageCameraText.Text = "";
            return;
        }
        var tracks = TracksOf(node);
        if (_lipSyncsAsked.Add(node)) _ = LoadLipSyncsAsync(timeline);
        StageText.Visibility = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (tracks.Count == 0) StageText.Text = "No one stands in this section: its dialog set wasn't found.";
        // worked out in the background, the latest time next: the UI thread only puts the bent meshes in
        var (cast, parts, idles, lips, at) = (stage, _stageParts.ToList(), _idles, new Dictionary<uint, Core.Animation.LipSync?>(_lipSyncs), (float)time);
        if (!_propEvents.TryGetValue(node, out var propEvents)) _propEvents[node] = propEvents = SceneStage.PropEvents(timeline);
        var propParts = _propParts.ToList();
        RequestStagePose(() =>
        {
            var bones = StageBones(cast, tracks, idles, lips, at);
            var posers = bones.ToDictionary(b => b.Key, b => SceneStage.Pose(b.Key, b.Value));
            var byName = bones.ToDictionary(b => b.Key.Name, b => (b.Key, b.Value), StringComparer.OrdinalIgnoreCase);
            var propAt = propParts.Select(p => p.Prop).Distinct().ToDictionary(p => p, p => SceneStage.PropPlace(p, propEvents, at,
                name => byName.TryGetValue(name, out var found) ? (found.Key, found.Value) : null));
            // one not in the section, or a prop not out, is folded into a point, out of sight
            return (part, chunk) =>
            {
                if (part < parts.Count)
                    return posers.TryGetValue(parts[part].Actor, out var pose) ? pose(parts[part].Part, chunk) : new System.Numerics.Vector3[chunk.Positions.Length];
                int p = part - parts.Count;
                if (p >= propParts.Count || propAt[propParts[p].Prop] is not { } place) return new System.Numerics.Vector3[chunk.Positions.Length];
                var placed = propParts[p].Prop.Parts[propParts[p].Part].Place * place;
                var positions = new System.Numerics.Vector3[chunk.Positions.Length];
                for (int v = 0; v < positions.Length; v++) positions[v] = System.Numerics.Vector3.Transform(chunk.Positions[v], placed);
                return positions;
            };
        });

        ShowOverlays(timeline, time);
        var camera = SceneStage.CameraAt(timeline, _cameras, (float)time, _scene?.File) is { } cut ? SceneStage.Shaken(cut, timeline, _cameraAnimations, (float)time) : (StageCamera?)null;
        StageCameraText.Text = camera is { } named ? $"Camera {named.Name}   FOV {named.FieldOfView:0.#}°" : "The section has no camera.";
        _lightKinds ??= _scene is null ? [] : SceneStage.LightKinds(_scene.File);
        StageView.SetLights(SceneStage.LightsAt(timeline, _lightKinds, (float)time).Select(StageLightOf).ToList());
        if (StageCameraToggle.IsChecked == true && camera is { } through)
            StageView.LookThrough(through.Position, through.Forward, through.Up, through.FieldOfView);
        else if (fit) FitStage(StageBones(stage, tracks, _idles, _lipSyncs, (float)time).Values);
    }

    // the cutscene on the cutscene stage, and the one being read
    private string? _cutsceneShown;

    /// <summary>
    /// Plays a cutscene section's cutscene at a time on the cutscene stage, reading it the first time: its actors as
    /// the cutscene viewer draws them, through its camera when Through the camera is on.
    /// </summary>
    private void ShowCutscene(string path, double time, bool fit)
    {
        // set only when it changes: setting it lays the view out and poses everyone again
        if (CutsceneView.ThroughCamera != (StageCameraToggle.IsChecked == true)) CutsceneView.ThroughCamera = StageCameraToggle.IsChecked == true;
        if (!string.Equals(path, _cutsceneShown, StringComparison.OrdinalIgnoreCase))
        {
            _cutsceneShown = path;
            _ = LoadCutsceneAsync(path);
            return;
        }
        CutsceneView.SetTime((float)time);
        StageCameraText.Text = CutsceneView.Playback?.CameraAt((float)time) is { } camera ? $"The cutscene's camera   FOV {camera.FieldOfView:0.#}°" : "";
        if (fit && !CutsceneView.ThroughCamera) CutsceneView.FitActors();
    }

    private async Task LoadCutsceneAsync(string path)
    {
        // the preview loads the scene's cutscenes with its actors: wait for that rather than read one twice
        if (_stageLoad is { IsCompleted: false } loading)
        {
            CutsceneView.Message = "Loading the scene…";
            await loading;
            if (!string.Equals(path, _cutsceneShown, StringComparison.OrdinalIgnoreCase)) return;
            // loading was stopped: it all loads again when the preview is opened again
            if (_stageLoad is null)
            {
                _cutsceneShown = null;
                return;
            }
        }
        // loaded with the preview, or now when it wasn't (one the preview couldn't read before)
        if (!_preparedCutscenes.TryGetValue(path, out var prepared))
        {
            CutsceneView.Clear();
            var locator = _locator;
            prepared = await StageLoading.RunAsync($"Loading {Path.GetFileNameWithoutExtension(path)}", async (report, token) =>
            {
                report.Step("Reading the cutscene");
                var cutscene = await Task.Run(() => locator.ReadFile(path) is { } data ? CutsceneFile.Read(data) : throw new FileNotFoundException("The cutscene wasn't found.", path), token);
                var playback = await Task.Run(() => CutscenePlayback.Load(cutscene, locator, path, locator.Find(path)?.Archive), token);
                return await CutsceneStage.PrepareAsync(cutscene, playback, locator, StageTexture, report, token);
            }, _closing.Token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.FromCurrentSynchronizationContext());
            if (prepared is null)
            {
                CutsceneView.Message = "The cutscene could not be loaded.";
                return;
            }
            _preparedCutscenes[path] = prepared;
        }
        if (!string.Equals(path, _cutsceneShown, StringComparison.OrdinalIgnoreCase)) return;
        _ = LoadCutsceneTrackAsync(prepared.Cutscene, path);
        CutsceneView.Show(prepared, _locator);
        if (CutsceneView.ThroughCamera != (StageCameraToggle.IsChecked == true)) CutsceneView.ThroughCamera = StageCameraToggle.IsChecked == true;
        PoseStage(SectionTimeline.Playhead ?? 0);
        // its lines' lip sync, for the faces to say them where the cutscene says them
        if (_timelineNode is { } node && _timelines.TryGetValue(node, out var timeline))
        {
            await LoadLipSyncsAsync(timeline);
            if (!string.Equals(path, _cutsceneShown, StringComparison.OrdinalIgnoreCase)) return;
            CutsceneView.SetLines(timeline.Elements.Where(e => e.Kind == SceneElementKind.Line && e.StringId is { } id && e.Speaker is not null && _lipSyncs.GetValueOrDefault(id) is not null)
                .Select(e => (e.Start, e.Speaker!, _lipSyncs[e.StringId!.Value]!)));
        }
    }

    // the music and effects of the cutscene on the cutscene stage, in stereo at the voices' rate, and what's playing it
    private (string Path, float[] Stereo)? _cutsceneTrack;
    private NAudio.Wave.WaveOutEvent? _trackOutput;

    /// <summary>The cutscene's recorded sound, its music and effects, from the game's sound caches, to play with its section.</summary>
    private async Task LoadCutsceneTrackAsync(CutsceneFile cutscene, string path)
    {
        if (cutscene.AudioTrack is not { } name || GameLocator.Resolve(null) is not { } game) return;
        try
        {
            var caches = await Core.Sounds.SoundCache.ForGame(game);
            var stereo = await Task.Run(() => Core.Sounds.SoundCache.Track(caches, name) is { } track ? Stereo(track.Samples, track.Channels) : null, _closing.Token);
            if (stereo is null || !string.Equals(path, _cutsceneShown, StringComparison.OrdinalIgnoreCase)) return;
            _cutsceneTrack = (path, stereo);
            // a section already playing picks it up where it is
            if (_sectionPlaying is not null) StartTrack(_sectionFrom + _sectionClock.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            // played without
        }
    }

    /// <summary>A sound as stereo from -1 to 1: a mono one in both ears; of more channels, the front left and right.</summary>
    private static float[] Stereo(short[] samples, int channels)
    {
        int frames = samples.Length / channels;
        var stereo = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            stereo[i * 2] = samples[i * channels] / 32768f;
            stereo[i * 2 + 1] = samples[i * channels + (channels > 1 ? 1 : 0)] / 32768f;
        }
        return stereo;
    }

    /// <summary>Plays the cutscene's track from a time, when the section playing is the cutscene's.</summary>
    private void StartTrack(double time)
    {
        StopTrack();
        if (_timelineNode is not { Kind: SceneNodeKind.Cutscene, Cutscene: { } path } || _cutsceneTrack is not { } track
            || !string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase)) return;
        int from = (int)(time * WwiseOpus.SampleRate) * 2;
        if (from >= track.Stereo.Length) return;
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(WwiseOpus.SampleRate, 2);
        var buffer = new NAudio.Wave.BufferedWaveProvider(format) { BufferLength = (track.Stereo.Length - from) * 4 + 4096, ReadFully = false };
        var bytes = new byte[(track.Stereo.Length - from) * 4];
        Buffer.BlockCopy(track.Stereo, from * 4, bytes, 0, bytes.Length);
        buffer.AddSamples(bytes, 0, bytes.Length);
        try
        {
            _trackOutput = new NAudio.Wave.WaveOutEvent { DesiredLatency = 120 };
            _trackOutput.Init(buffer);
            _trackOutput.Play();
        }
        catch (NAudio.MmException)
        {
            StopTrack(); // no sound device: it plays without
        }
    }

    private void StopTrack()
    {
        _trackOutput?.Stop();
        _trackOutput?.Dispose();
        _trackOutput = null;
    }

    // each line's lip sync, by its string, read for a section the first time it's shown
    private readonly Dictionary<uint, Core.Animation.LipSync?> _lipSyncs = [];
    private readonly HashSet<SceneNode> _lipSyncsAsked = [];

    /// <summary>Reads the lip sync of each line a section says, from the speech files its voices are in.</summary>
    private async Task LoadLipSyncsAsync(SceneSectionTimeline timeline)
    {
        var ids = timeline.Elements.Where(e => e.Kind == SceneElementKind.Line && e.StringId is not null).Select(e => e.StringId!.Value)
            .Where(id => !_lipSyncs.ContainsKey(id)).Distinct().ToList();
        if (ids.Count == 0) return;
        try
        {
            var speech = await SpeechAsync();
            var read = await Task.Run(() => ids.Select(id =>
            {
                try
                {
                    return (id, speech.Select(s => s.LipSync(id)).FirstOrDefault(b => b is not null) is { } blob ? Core.Animation.LipSync.Read(blob) : null);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException)
                {
                    return (id, (Core.Animation.LipSync?)null);
                }
            }).ToList(), _closing.Token);
            foreach (var (id, lips) in read) _lipSyncs[id] = lips;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // the faces move without
        }
    }

    /// <summary>
    /// The line being said at a time as a subtitle under the picture, with who says it, as REDkit's preview shows it;
    /// and the picture faded as the section's fades fade it.
    /// </summary>
    private void ShowOverlays(SceneSectionTimeline timeline, double time)
    {
        var line = timeline.Elements.FirstOrDefault(e => e.Kind == SceneElementKind.Line && e.StringId is not null && time >= e.Start && time < e.Start + e.Duration);
        string words = line?.StringId is { } id ? Text(id) ?? "" : "";
        Subtitle.Text = words.Length == 0 ? "" : line!.Speaker is { Length: > 0 } speaker ? $"{speaker}: {words}" : words;
        SubtitleBar.Visibility = words.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (SceneStage.FadeAt(timeline, (float)time) is { } fade)
        {
            FadeCover.Background = new System.Windows.Media.SolidColorBrush(fade.Color);
            FadeCover.Opacity = fade.Amount;
            FadeCover.Visibility = Visibility.Visible;
        }
        else FadeCover.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Where each actor in a section who's shown then has its bones at a time: its body, its face (with its lines' lip
    /// sync), and turned to look at where the others' heads are. Works on what it's given only, so it runs off the UI thread.
    /// </summary>
    private static Dictionary<StageActor, System.Numerics.Matrix4x4[]> StageBones(List<StageActor> cast, List<ActorTrack> tracks,
        Dictionary<ActorStance, string> idles, IReadOnlyDictionary<uint, Core.Animation.LipSync?> lips, float time)
    {
        var bones = new Dictionary<StageActor, System.Numerics.Matrix4x4[]>();
        var trackOf = new Dictionary<StageActor, ActorTrack>();
        foreach (var actor in cast)
            if (tracks.FirstOrDefault(t => t.Actor.Equals(actor.Name, StringComparison.OrdinalIgnoreCase)) is { } track && track.ShownAt(time))
            {
                trackOf[actor] = track;
                var face = SceneStage.FaceWeights(actor, track, time, id => lips.GetValueOrDefault(id));
                bones[actor] = SceneStage.Bones(actor, track, idles, time, face);
            }
        var heads = bones.ToDictionary(b => b.Key.Name, b => SceneStage.Bone(b.Key.Rig, "head") is int h and >= 0 ? b.Value[h].Translation : (System.Numerics.Vector3?)null,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (actor, model) in bones) SceneStage.LookAt(actor, trackOf[actor], model, time, name => heads.GetValueOrDefault(name));
        return bones;
    }

    // the pose being worked out, and the latest asked for while it was
    private bool _stagePosing;
    private Func<Func<int, Core.Meshes.MeshChunk, System.Numerics.Vector3[]?>?>? _stagePending;

    /// <summary>Poses the stage in the background, one pose at a time: a heavy scene shows fewer frames, still in time, and stays answering.</summary>
    private async void RequestStagePose(Func<Func<int, Core.Meshes.MeshChunk, System.Numerics.Vector3[]?>?> makePose)
    {
        _stagePending = makePose;
        if (_stagePosing) return;
        _stagePosing = true;
        try
        {
            while (_stagePending is { } next)
            {
                _stagePending = null;
                await StageView.PoseAsync(next);
            }
        }
        finally
        {
            _stagePosing = false;
        }
    }

    /// <summary>Turns the free camera to see everyone in the section.</summary>
    private void FitStage(IEnumerable<System.Numerics.Matrix4x4[]> bones)
    {
        var points = bones.SelectMany(b => b.Select(m => m.Translation)).ToList();
        if (points.Count == 0)
        {
            StageView.FitToView();
            return;
        }
        var pad = new System.Numerics.Vector3(0.3f);
        StageView.FitTo(points.Aggregate(System.Numerics.Vector3.Min) - pad, points.Aggregate(System.Numerics.Vector3.Max) + pad);
    }

    private void StageCamera_Click(object sender, RoutedEventArgs e)
    {
        if (StageCameraToggle.IsChecked != true) StageView.StopLookingThrough();
        CutsceneView.ThroughCamera = StageCameraToggle.IsChecked == true;
        LayoutStage();
        PoseStage(SectionTimeline.Playhead ?? 0, fit: StageCameraToggle.IsChecked != true);
    }

    private void StageFit_Click(object sender, RoutedEventArgs e)
    {
        StageCameraToggle.IsChecked = false;
        StageCamera_Click(sender, e);
    }

    private void StageTextured_Click(object sender, RoutedEventArgs e) => StageView.Textured = CutsceneView.View.Textured = StageTexturedToggle.IsChecked == true;

    private void StageFloor_Click(object sender, RoutedEventArgs e) => StageView.ShowFloor = CutsceneView.View.ShowFloor = StageFloorToggle.IsChecked == true;

    private void StageArea_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutStage();

    /// <summary>The view fills the stage, or through a camera is the largest 16:9 picture that fits.</summary>
    private void LayoutStage()
    {
        bool through = StageCameraToggle.IsChecked == true;
        StageBars.Visibility = through ? Visibility.Visible : Visibility.Collapsed;
        if (through)
        {
            double width = Math.Min(StageArea.ActualWidth, StageArea.ActualHeight * 16 / 9);
            StageView.Width = Math.Max(1, width);
            StageView.Height = Math.Max(1, width * 9 / 16);
        }
        else
        {
            StageView.Width = double.NaN;
            StageView.Height = double.NaN;
        }
    }

    // ---------------------------------------------------------------- a section's timeline

    private SceneNode? _timelineNode;

    /// <summary>A section's timeline in the Timeline tab; a part that isn't a section clears it.</summary>
    private void ShowTimeline(SceneNode node)
    {
        if (node == _timelineNode) return;
        StopSection();
        _timelineNode = node;
        _timelines.TryGetValue(node, out var timeline);
        SectionTimeline.Show(timeline, id => Text(id), _scene is null ? new Dictionary<string, System.Windows.Media.Brush>() : SceneNodeItem.SpeakerBrushes(_scene));
        PlaySectionButton.IsEnabled = timeline is not null && !_reading;
        TimelineTitle.Text = timeline is null
            ? "Only sections have timelines. Click a section to see its own."
            : $"{node.Title}: {Format.Count(timeline.Elements.Count(e => e.Kind == SceneElementKind.Line), "line")}, {Format.Count(timeline.Events.Count, "event")}, "
              + $"{timeline.Duration:0.0} s. Click an event for its properties.";
        ShowSectionTime(0);
    }

    private void ShowSectionTime(double time)
    {
        var timeline = SectionTimeline.Timeline;
        SectionTimeText.Text = timeline is null ? "" : $"{time:0.00} / {timeline.Duration:0.00} s";
        SectionTimeline.Playhead = timeline is null ? null : time;
        PoseStage(time);
    }

    private void SectionTimeline_ItemClicked(object? sender, object? item)
    {
        if (_scene is null || item is null) return;
        var (heading, source) = item switch
        {
            SceneTimelineEvent e => ($"{e.Name}{(e.Actor is { Length: > 0 } a ? $" ({a})" : "")} at {e.Start:0.00} s", e.Source),
            SceneTimelineElement e => ($"{e.Kind} at {e.Start:0.00} s", e.Source),
            _ => ("", null),
        };
        if (source is null) return;
        PartHeading.Text = heading;
        PartProperties.Show(PropertyRow.Of(source, _scene.File));
        SideTabs.SelectedIndex = 0;
    }

    private void SectionTimeline_Seeked(object? sender, double time)
    {
        ShowSectionTime(time);
        if (_sectionPlaying is null || SectionTimeline.Timeline is not { } timeline) return;
        // playing on from there, with the voices from there
        _sectionPlaying.Cancel();
        StopSectionVoice();
        var stop = new CancellationTokenSource();
        _sectionPlaying = stop;
        _sectionFrom = time;
        _sectionClock.Restart();
        StartTrack(time);
        _ = SpeakSectionAsync(timeline, time, stop.Token);
    }

    private void TimelineZoom_Changed(object sender, RoutedEventArgs e)
    {
        if (SectionTimeline is null || TimelineFitToggle is null) return;
        bool fit = TimelineFitToggle.IsChecked == true;
        if (sender == TimelineZoom && fit) TimelineFitToggle.IsChecked = fit = false; // moving the slider zooms
        SectionTimeline.PixelsPerSecond = fit ? 0 : TimelineZoom.Value;
        // fitted, the timeline is given the width it has; zoomed, it scrolls
        TimelineScroll.HorizontalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        SectionTimeline.InvalidateMeasure();
        SectionTimeline.InvalidateVisual();
    }

    // the section playing: its clock, where it started, and what stops it
    private CancellationTokenSource? _sectionPlaying;
    private readonly System.Diagnostics.Stopwatch _sectionClock = new();
    private double _sectionFrom;
    // where it stops: the section's end, or for a read-through where its choice comes
    private double _sectionEnd;

    private void PlaySection_Click(object sender, RoutedEventArgs e)
    {
        if (_reading) return; // one thing speaks at a time: the read-through stops first
        if (_sectionPlaying is not null) StopSection();
        else PlaySection(SectionTimeline.Playhead is { } at && SectionTimeline.Timeline is { } t && at < t.Duration - 0.05 ? at : 0);
    }

    /// <summary>
    /// Plays the section from a time: the playhead runs along it, and each line is spoken when the timeline reaches
    /// it, a line already begun from where it had got to. The read-through can't run meanwhile.
    /// </summary>
    private void PlaySection(double from)
    {
        if (SectionTimeline.Timeline is not { } timeline) return;
        // the read-through plays its sections this way; otherwise it stops, as one thing speaks at a time
        if (!_reading)
        {
            StopReading();
            ReadButton.IsEnabled = false;
        }
        var stop = new CancellationTokenSource();
        _sectionPlaying = stop;
        _sectionFrom = from;
        _sectionEnd = timeline.Duration;
        _sectionClock.Restart();
        PlaySectionIcon.Text = "\uE769"; // pause
        StartTrack(from);
        System.Windows.Media.CompositionTarget.Rendering += SectionTick;
        _ = SpeakSectionAsync(timeline, from, stop.Token);
    }

    private void SectionTick(object? sender, EventArgs e)
    {
        if (SectionTimeline.Timeline is not { } timeline) return;
        double time = _sectionFrom + _sectionClock.Elapsed.TotalSeconds;
        ShowSectionTime(Math.Min(time, _sectionEnd));
        if (time >= _sectionEnd) StopSection(finished: true);
    }

    private async Task SpeakSectionAsync(SceneSectionTimeline timeline, double from, CancellationToken token)
    {
        try
        {
            var speech = await SpeechAsync();
            // the line under the playhead too, so a pause mid-line goes on from there
            foreach (var line in timeline.Elements.Where(e => e.Kind == SceneElementKind.Line && e.Start + e.Duration > from + 0.02 && e.StringId is not null))
            {
                // decoded ahead, played when the playhead gets there
                byte[]? wav = await Task.Run(() =>
                {
                    uint id = line.StringId!.Value;
                    if (speech.FirstOrDefault(s => s.Has(id))?.Sound(id) is not { } riff) return null;
                    var (samples, channels) = WwiseOpus.Decode(riff);
                    return WwiseOpus.ToWav(samples, channels);
                }, token);
                double wait = line.Start - (_sectionFrom + _sectionClock.Elapsed.TotalSeconds);
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), token);
                if (token.IsCancellationRequested) return;
                if (wav is null) continue;
                var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
                double into = _sectionFrom + _sectionClock.Elapsed.TotalSeconds - line.Start;
                if (into > 0.05)
                {
                    if (into >= reader.TotalTime.TotalSeconds) { reader.Dispose(); continue; }
                    reader.CurrentTime = TimeSpan.FromSeconds(into);
                }
                StopSectionVoice();
                _sectionVoice = new NAudio.Wave.WaveOutEvent { DesiredLatency = 120 };
                _sectionVoice.Init(reader);
                _sectionVoice.PlaybackStopped += (_, _) => reader.Dispose();
                _sectionVoice.Play();
                MarkSaying(line.StringId);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            // stopped, or a voice that can't be read is left silent
        }
    }

    /// <summary>Stops the section playing. A read-through waiting on it goes on when it got to its end, and stops when not.</summary>
    private void StopSection(bool finished = false)
    {
        var done = _sectionDone;
        _sectionDone = null;
        if (_sectionPlaying is not null)
        {
            _sectionPlaying.Cancel();
            _sectionPlaying = null;
            _sectionClock.Stop();
            StopSectionVoice();
            StopTrack();
            MarkSaying(null);
            System.Windows.Media.CompositionTarget.Rendering -= SectionTick;
            ReadButton.IsEnabled = true;
            PlaySectionIcon.Text = "\uE768"; // play
        }
        done?.TrySetResult(finished);
    }

    // what a read-through waits on while a section plays: whether it got to the end
    private TaskCompletionSource<bool>? _sectionDone;

    /// <summary>Marks the line being said on its card, as reading through does.</summary>
    private void MarkSaying(uint? id)
    {
        if (_timelineNode is not { } node || _items.FirstOrDefault(i => i.Node == node) is not { } item) return;
        foreach (var row in item.Rows.OfType<SceneLineRow>()) row.IsPlaying = id is not null && row.StringId == id;
    }

    /// <summary>
    /// Plays a section on its timeline for a read-through, once the preview's actors (or its cutscene's) are on the
    /// stage when the preview is open: true when it played to its end.
    /// </summary>
    private async Task<bool> RunSectionAsync(SceneNode node, CancellationToken token)
    {
        ShowTimeline(node);
        if (SectionTimeline.Timeline is null) return true;
        if (_stageLoad is not null)
        {
            // a load that fails or is overtaken doesn't hold it up for long
            await Task.WhenAny(_stageReady.Task, Task.Delay(TimeSpan.FromSeconds(600), token));
            if (node.Kind == SceneNodeKind.Cutscene) await Task.WhenAny(CutsceneView.Ready, Task.Delay(TimeSpan.FromSeconds(180), token));
        }
        if (token.IsCancellationRequested) return false;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaySection(0);
        // the choice is made when it comes, not after the time the game would wait for one
        if (SectionTimeline.Timeline.Elements.FirstOrDefault(e => e.Kind == SceneElementKind.Choice) is { } choice) _sectionEnd = Math.Max(0.1, choice.Start);
        _sectionDone = done;
        return await done.Task && !token.IsCancellationRequested;
    }

    // the voice the timeline is saying; played with NAudio so that a line can start part way through
    private NAudio.Wave.WaveOutEvent? _sectionVoice;

    private void StopSectionVoice()
    {
        _sectionVoice?.Stop();
        _sectionVoice?.Dispose();
        _sectionVoice = null;
    }

    private void DialogsetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DialogsetList.SelectedItem is not SceneDialogsetRow row || _scene is null) return;
        SlotGrid.ItemsSource = SceneSlotRow.Of(row.Set);
        DialogsetProperties.Show(Rows(row.Set.Source, ["slots"]));
    }

    private void CameraGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CameraGrid.SelectedItem is SceneCameraRow row && _scene is not null) CameraProperties.Show(PropertyRow.Of(row.Camera.Source, _scene.File));
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = ViewerLinks.OpenAsync(this, Message, _locator, path);

    // ---------------------------------------------------------------- voices

    private bool _reading;
    private CancellationTokenSource? _playing;
    private System.Media.SoundPlayer? _player;
    private readonly Random _random = new();

    /// <summary>The speech files in the language picked, then the English ones for lines it has no voice for.</summary>
    private async Task<List<W3Speech>> SpeechAsync()
    {
        if (GameLocator.Resolve(null) is not { } game) return [];
        var language = LanguageBox.SelectedItem as W3Language ?? W3Strings.Languages[0];
        var speech = await W3Speech.ForLanguage(game, language);
        // the game installs one language's voices (and a mod may bring a few lines in others), so a line not voiced in
        // the language picked is looked for in English after
        if (W3Strings.Language("en") is { } en && en != language) speech = [.. speech, .. await W3Speech.ForLanguage(game, en)];
        return speech;
    }

    /// <summary>
    /// Plays a line's voice and waits for it to end. A line with no voice is shown for a moment instead. False when
    /// it was stopped.
    /// </summary>
    private async Task<bool> PlayAsync(SceneLineRow row, CancellationToken token)
    {
        row.IsPlaying = true;
        try
        {
            var speech = await SpeechAsync();
            byte[]? wav = row.StringId is not { } id ? null : await Task.Run(() =>
            {
                if (speech.FirstOrDefault(s => s.Has(id))?.Sound(id) is not { } riff) return null;
                var (samples, channels) = WwiseOpus.Decode(riff);
                return WwiseOpus.ToWav(samples, channels);
            }, token);
            if (token.IsCancellationRequested) return false;
            if (wav is null)
            {
                await Task.Delay(1500, token);
                return true;
            }
            using var player = new System.Media.SoundPlayer(new MemoryStream(wav));
            _player = player;
            using (token.Register(player.Stop))
                await Task.Run(player.PlaySync);
            return !token.IsCancellationRequested;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
        finally
        {
            row.IsPlaying = false;
            _player = null;
        }
    }

    /// <summary>Stops what's playing; the next play starts afresh.</summary>
    private CancellationToken Restart()
    {
        _playing?.Cancel();
        _player?.Stop();
        _playing = new CancellationTokenSource();
        return _playing.Token;
    }

    /// <summary>Opens a line's voice in the sound viewer: in the language picked, or English when that one has no voice for it.</summary>
    private async void OpenLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SceneLineRow { StringId: { } id }) return;
        var language = LanguageBox.SelectedItem as W3Language ?? W3Strings.Languages[0];
        string path = $@"speech\{language.Handle}\{id}.wem";
        if (await SoundLinks.ReadAsync(path) is null && W3Strings.Language("en") is { } en && en != language) path = $@"speech\{en.Handle}\{id}.wem";
        await DataViewers.OpenLinkAsync(path, _locator, this, this);
    }

    private void CopyLineId_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SceneLineRow { StringId: { } id }) return;
        try { Clipboard.SetText(id.ToString()); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void PlayLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SceneLineRow row) return;
        StopReading();
        StopSection();
        _ = PlayAsync(row, Restart());
    }

    private void Read_Click(object sender, RoutedEventArgs e)
    {
        if (_reading) StopReading();
        else if (_sectionPlaying is not null) return; // one thing speaks at a time: the timeline stops first
        else if (((Cards.SelectedItem as SceneNodeItem)?.Node ?? _scene?.Nodes.FirstOrDefault()) is { } from) _ = ReadFromAsync(from);
    }

    /// <summary>
    /// Plays the scene from a part on, following the flow, and shows it in the preview: each section on its timeline,
    /// its lines said at their times as its actors play it (a cutscene section its cutscene), and on through scripts
    /// and links. At a choice it picks an option at random after a moment (or the one clicked meanwhile), at a
    /// condition, switch or random part a way at random, and it stops at an end.
    /// </summary>
    private async Task ReadFromAsync(SceneNode node)
    {
        var token = Restart();
        SetReading(true);
        if (MainTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == "Preview") is { } preview) MainTabs.SelectedItem = preview;
        // reading it through plays it in the preview, so it loads it
        StartStageLoad();
        for (int hops = 0; hops < 1000; hops++)
        {
            Select(node);
            if (_timelines.ContainsKey(node))
            {
                if (!await RunSectionAsync(node, token)) return;
            }
            else if (_items.FirstOrDefault(i => i.Node == node) is { } item)
                foreach (var row in item.Rows.OfType<SceneLineRow>())
                    if (!await PlayAsync(row, token)) return;
            if (token.IsCancellationRequested) return;

            var options = node.Elements.SelectMany(e => e.Options).Where(o => o.Target is not null).ToList();
            var ways = node.Next.Where(l => l.Target is not null).ToList();
            SceneLink? way;
            if (options.Count > 0)
            {
                // a moment to read the options, or to click one
                ReadLabel.Text = "Choosing…";
                try { await Task.Delay(2000, token); }
                catch (OperationCanceledException) { return; }
                // not a shop, barber, Gwent and the like: those open a service rather than going on with the talk
                var talk = options.Where(o => !o.OpensService).ToList();
                if (talk.Count > 0) options = talk;
                way = options[_random.Next(options.Count)];
                ReadLabel.Text = "Stop";
            }
            else if (node.Kind is SceneNodeKind.Condition or SceneNodeKind.Switch or SceneNodeKind.Random && ways.Count > 0)
                way = ways[_random.Next(ways.Count)];
            else
                way = ways.FirstOrDefault();
            if (way?.Target is not { } next) break;
            _back.Push(node);
            BackButton.IsEnabled = true;
            node = next;
        }
        SetReading(false);
    }

    private void StopReading()
    {
        bool reading = _reading;
        _playing?.Cancel();
        _player?.Stop();
        SetReading(false);
        // the section it was playing stops with it
        if (reading) StopSection();
    }

    private void SetReading(bool reading)
    {
        _reading = reading;
        ReadIcon.Text = char.ConvertFromUtf32(reading ? 0xE71A : 0xE768); // stop, play
        ReadLabel.Text = reading ? "Stop" : "Read through";
        PlaySectionButton.IsEnabled = !reading && SectionTimeline.Timeline is not null;
    }

    // ---------------------------------------------------------------- files

    private void File_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = OpenLinkAsync(path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => Copy((sender as FrameworkElement)?.Tag as string);

    /// <summary>
    /// Opens a file the scene uses in its viewer, found the way the game would find it. A kind of file the tool can't
    /// show has its path copied instead.
    /// </summary>
    private async Task OpenLinkAsync(string path)
    {
        bool viewable = EntityWindow.CanShow(path) || BehTreeWindow.CanShow(path) || MeshWindow.CanShow(path) || CutsceneWindow.CanShow(path) || CanShow(path) || XmlWindow.CanOpen(path) || TextureWindow.CanShow(path) || DataViewers.CanShow(path);
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
        else if (EntityWindow.CanShow(path)) EntityWindow.Open(path, source, data, _locator, this, entry?.Archive);
        else if (BehTreeWindow.CanShow(path)) BehTreeWindow.Open(path, source, data, _locator, this, entry?.Archive);
        else if (MeshWindow.CanShow(path)) MeshWindow.Open(path, source, data, entry?.Archive, _locator, this);
        else if (CutsceneWindow.CanShow(path)) CutsceneWindow.Open(path, source, data, _locator, this, entry?.Archive);
        else if (CanShow(path)) Open(path, source, data, _locator, this, entry?.Archive);
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

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1 || Message.IsOpen) return;
        GoBack();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.None && !SearchBox.IsKeyboardFocused) Read_Click(this, e);
        else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) GoBack();
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }
}
