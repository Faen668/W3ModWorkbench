using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Shows a cutscene (.w2cutscene): a timeline of its events, the scene's and each actor's, the actors with the entities
/// they're made from, its effects and the files it uses, and every event in a list that can be searched and narrowed to
/// an actor or a kind. The files it uses open in their viewers where the tool has one. The actors' body animations play
/// as stick figures on their rigs, seen from anywhere or through the cutscene's camera.
/// </summary>
public partial class CutsceneWindow : Window, IMessageHost
{
    // the zoom slider runs from 4 to 400 pixels a second, evenly in its logarithm
    private const double MinZoom = 4, MaxZoom = 400;

    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private CutsceneFile? _cutscene;
    private List<CutsceneEventItem> _events = [];
    private bool _syncing;
    private CutscenePlayback? _playback;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private float _playedFrom;
    private bool _playing, _scrubbing;

    private CutsceneWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        Title = $"{Path.GetFileName(path)} - Cutscene";
        InfoText.Text = $"{path}   ·   {source}";
        ZoomSlider.Value = ZoomToSlider(40);
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => Pause();
    }

    /// <summary>Whether a file's name says it's a cutscene this window can show.</summary>
    public static bool CanShow(string name) => CutsceneFile.IsCutscene(name);

    /// <summary>Opens a cutscene in a window of its own. The files it uses are looked for with <paramref name="locator"/>.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner) =>
        new CutsceneWindow(path, source, () => data, locator).Standalone(owner).Show();

    /// <summary>Opens a cutscene file on disk: one in the project's game files, or one extracted from a bundle.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new CutsceneWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        try
        {
            _cutscene = await Task.Run(() => CutsceneFile.Read(_readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The cutscene could not be read.\n\n" + ex.Message;
            return;
        }
        var c = _cutscene;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;

        Timeline.Show(c);
        ApplyZoom();
        _events = c.AllEvents.Select(e => new CutsceneEventItem(e)).ToList();
        KindFilters.ItemsSource = _events.GroupBy(e => e.Event.Kind).OrderBy(g => g.Key).Select(g => new CutsceneKindItem(g.Key, g.Count())).ToList();
        ActorList.ItemsSource = c.Actors.Select(a => new CutsceneActorItem(a)).ToList();
        EffectList.ItemsSource = c.Effects.Select(f => new CutsceneEffectItem(f)).ToList();
        EffectsPanel.Visibility = c.Effects.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LinkList.ItemsSource = c.Links.Select(l => new CutsceneLinkItem(l)).ToList();

        var more = new List<string>();
        if (c.AudioTrack is { } audio) more.Add($"Its sound and voices are the audio event {audio}.");
        if (c.PlaceTags.Count > 0) more.Add($"Played at the place tagged {string.Join(", ", c.PlaceTags)}.");
        if (c.HiddenTags.Count > 0) more.Add($"Hides what's tagged {string.Join(", ", c.HiddenTags)} while it plays.");
        if (c.FadeBefore > 0 || c.FadeAfter > 0) more.Add($"Fades {c.FadeBefore:0.##} s before and {c.FadeAfter:0.##} s after.");
        MoreText.Text = string.Join("\n", more);
        MoreText.Visibility = more.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyFilter();
        int shown = _events.Count(e => e.Event.Kind != CutsceneEventKind.Cloth);
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {c.File.Version}, {CutsceneTimeline.FormatTime(c.Duration)}, "
                      + $"{Format.Count(c.Actors.Count, "actor")}, {Format.Count(_events.Count, "event")}, {Format.Count(c.Effects.Count, "effect")}";
        if (shown == 0) SearchPlaceholder.Text = "No events: the actors' animations play on their own";
        await Task.WhenAll(LoadPlaybackAsync(c), LoadDialogLinesAsync(c));
    }

    /// <summary>
    /// Finds the lines of the dialog events in the scene that plays the cutscene, and shows them in words on the
    /// timeline and in the list. Nothing changes when the scene isn't found or its lines don't match the marks.
    /// </summary>
    private async Task LoadDialogLinesAsync(CutsceneFile c)
    {
        if (!c.SceneEvents.Any(e => e.Kind == CutsceneEventKind.Dialog) || GameLocator.Resolve(null) is not { } game) return;
        string name = Path.GetFileName(_path);
        List<(string, string)>? lines = null;
        try
        {
            var strings = await GameStringIndex.GetAsync(game, W3Strings.Language("en")!);
            lines = await Task.Run(() =>
            {
                foreach (string scenePath in c.UsedIn)
                {
                    if (_locator.ReadFile(scenePath) is not { } data) continue;
                    var section = SceneFile.Read(data).Nodes.FirstOrDefault(n => n.Cutscene is { } path && Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (section is null) continue;
                    return section.Elements.Where(e => e.Kind == SceneElementKind.Line)
                        .Select(e => (e.Speaker ?? "", e.StringId is { } id && strings.WithId(id) is { Count: > 0 } found ? found[^1].Text : $"(string {e.StringId})"))
                        .ToList();
                }
                return null;
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return;
        }
        if (lines is null || !c.NameDialogLines(lines)) return;
        _events = c.AllEvents.Select(e => new CutsceneEventItem(e)).ToList();
        Timeline.Refresh();
        ApplyFilter();
    }

    // ---------------------------------------------------------------- playing

    /// <summary>Reads the actors' animations and finds their rigs, which can take a moment the first time the game's bundles are looked in.</summary>
    private async Task LoadPlaybackAsync(CutsceneFile c)
    {
        try
        {
            _playback = await Task.Run(() => CutscenePlayback.Load(c, _locator, _path, _locator.Find(_path)?.Archive));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            StageText.Text = "The animations could not be read.\n\n" + ex.Message;
            return;
        }
        var p = _playback;
        Stage.Show(p);
        int drawn = p.Actors.Count(a => a.Rig is not null);
        var notes = new List<string>();
        if (drawn == 0) notes.Add(p.Actors.Count == 0 ? "There are no body animations to play." : "No actor's rig was found, so there's nothing to draw.");
        foreach (var a in p.Actors.Where(a => a.Rig is null)) notes.Add($"{a.Name} isn't drawn: {a.RigNote}");
        notes.AddRange(p.Problems);
        StageText.Text = drawn == 0 ? string.Join("\n", notes) : "";
        StageText.Visibility = drawn == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (drawn > 0 && notes.Count > 0) Stage.ToolTip = $"{Stage.ToolTip}\n\n{string.Join("\n", notes)}";
        if (drawn == 0 && p.Camera?.Rig is null) return;

        ScrubSlider.Maximum = Math.Max(0.01, p.Duration);
        ScrubSlider.SmallChange = 1 / 30.0;
        ScrubSlider.LargeChange = 1;
        ScrubSlider.IsEnabled = PlayButton.IsEnabled = true;
        CameraToggle.IsEnabled = Stage.HasCamera;
        if (!Stage.HasCamera) CameraToggle.ToolTip = "The cutscene has no camera animation, or its rig wasn't found";
        SetTime(0);
    }

    /// <summary>Shows a time everywhere: on the stage, the scrub bar and the timeline's playhead.</summary>
    private void SetTime(float time)
    {
        if (_playback is null) return;
        time = Math.Clamp(time, 0, _playback.Duration);
        Stage.Time = time;
        Timeline.Playhead = time;
        TimeText.Text = $"{CutsceneTimeline.FormatTime(time)} / {CutsceneTimeline.FormatTime(_playback.Duration)}";
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
        if (_playback is null || _playing) return;
        // from the start again when it's at the end
        _playedFrom = Stage.Time >= _playback.Duration - 0.01f ? 0 : Stage.Time;
        _clock.Restart();
        _playing = true;
        PlayIcon.Text = "\uE769"; // pause
        System.Windows.Media.CompositionTarget.Rendering += Tick;
    }

    private void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _clock.Stop();
        PlayIcon.Text = "\uE768"; // play
        System.Windows.Media.CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_playback is null) return;
        float time = _playedFrom + (float)_clock.Elapsed.TotalSeconds;
        SetTime(time);
        if (time >= _playback.Duration) Pause();
    }

    private void Scrub_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_scrubbing) return;
        Pause();
        SetTime((float)e.NewValue);
    }

    private void Timeline_Seeked(object? sender, double time)
    {
        Pause();
        SetTime((float)time);
    }

    private void CameraToggle_Click(object sender, RoutedEventArgs e) => Stage.ThroughCamera = CameraToggle.IsChecked == true;

    // ---------------------------------------------------------------- filters

    private CutsceneActorItem? SelectedActor => ActorList.SelectedItem as CutsceneActorItem;

    /// <summary>Shows the events of the kinds ticked, of the actor picked (or all), that match the search.</summary>
    private void ApplyFilter()
    {
        string search = SearchBox.Text.Trim();
        string? actor = SelectedActor?.Name;
        var shown = _events.Where(e => !Timeline.HiddenKinds.Contains(e.Event.Kind)
                                    && (actor is null || e.Event.Actor == actor)
                                    && (search.Length == 0 || Matches(e, search))).ToList();
        var selected = EventGrid.SelectedItem;
        EventGrid.ItemsSource = shown;
        if (selected is not null && shown.Contains(selected)) EventGrid.SelectedItem = selected;
        AllActorsButton.Visibility = actor is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool Matches(CutsceneEventItem e, string search) =>
        e.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Detail.Contains(search, StringComparison.OrdinalIgnoreCase)
        || e.Actor.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Kind.Contains(search, StringComparison.OrdinalIgnoreCase)
        || e.Event.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void KindFilter_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: CutsceneKindItem kind } toggle) toggle.IsChecked = !Timeline.HiddenKinds.Contains(kind.Kind);
    }

    private void KindFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: CutsceneKindItem kind } toggle) return;
        if (toggle.IsChecked == true) Timeline.HiddenKinds.Remove(kind.Kind);
        else Timeline.HiddenKinds.Add(kind.Kind);
        Timeline.Refresh();
        ApplyFilter();
    }

    private void ActorList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void AllActors_Click(object sender, RoutedEventArgs e) => ActorList.SelectedItem = null;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    // ---------------------------------------------------------------- timeline

    private static double SliderToZoom(double value) => MinZoom * Math.Pow(MaxZoom / MinZoom, value);

    private static double ZoomToSlider(double zoom) => Math.Log(zoom / MinZoom) / Math.Log(MaxZoom / MinZoom);

    private void Fit_Click(object sender, RoutedEventArgs e) => ApplyZoom();

    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        FitToggle.IsChecked = false;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        Timeline.PixelsPerSecond = FitToggle.IsChecked == true ? 0 : SliderToZoom(ZoomSlider.Value);
        // fitting needs the width there is, not the scroller's endless width
        TimelineScroller.HorizontalScrollBarVisibility = FitToggle.IsChecked == true ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void Timeline_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        // zoom about the point under the mouse
        double x = e.GetPosition(Timeline).X;
        double before = Timeline.PixelsPerSecond > 0 ? Timeline.PixelsPerSecond : Timeline.ActualWidth / Math.Max(1, _cutscene?.Duration ?? 1);
        double after = Math.Clamp(before * (e.Delta > 0 ? 1.25 : 0.8), MinZoom, MaxZoom);
        double offset = TimelineScroller.HorizontalOffset;
        ZoomSlider.Value = ZoomToSlider(after);
        TimelineScroller.UpdateLayout();
        TimelineScroller.ScrollToHorizontalOffset(offset + x * (after / before - 1));
    }

    private void Timeline_EventClicked(object? sender, CutsceneEvent? clicked)
    {
        if (clicked is null) return;
        if (EventGrid.ItemsSource is not List<CutsceneEventItem> shown || shown.FirstOrDefault(i => ReferenceEquals(i.Event, clicked)) is not { } item)
        {
            // it's of an actor not picked or doesn't match the search: show them all again
            ActorList.SelectedItem = null;
            SearchBox.Clear();
            item = _events.First(i => ReferenceEquals(i.Event, clicked));
        }
        _syncing = true;
        EventGrid.SelectedItem = item;
        EventGrid.ScrollIntoView(item);
        _syncing = false;
    }

    private void EventGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventGrid.SelectedItem is not CutsceneEventItem item) return;
        Timeline.Selected = item.Event;
        if (!_syncing) Timeline.Reveal(item.Event);
    }

    // ---------------------------------------------------------------- files

    private void ActorTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = OpenLinkAsync(path);
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = OpenLinkAsync(path);
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e) => Copy((sender as FrameworkElement)?.Tag as string);

    /// <summary>
    /// Opens a file the cutscene uses in its viewer, found the way the game would find it. A kind of file the tool can't
    /// show has its path copied instead.
    /// </summary>
    private async Task OpenLinkAsync(string path)
    {
        bool viewable = EntityWindow.CanShow(path) || MeshWindow.CanShow(path) || CanShow(path) || XmlWindow.CanOpen(path) || TextureWindow.CanShow(path);
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
        string source = entry is null ? "project" : $"{entry.Archive.Name}";
        string name = Path.GetFileName(path);
        if (EntityWindow.CanShow(path)) EntityWindow.Open(path, source, data, _locator, this);
        else if (MeshWindow.CanShow(path)) MeshWindow.Open(path, source, data, entry?.Archive, _locator, this);
        else if (CanShow(path)) Open(path, source, data, _locator, this);
        else if (XmlWindow.CanOpen(path)) XmlWindow.OpenData(name, $"{source}  ·  {path}", data, this, path);
        else if (!TextureWindow.Open(name, $"{source}  ·  {path}", data, this))
            await ShowMessage("It can't be shown", $"{path}\n\nIt isn't a picture the texture viewer understands.", MessageKind.Warning);
    }

    // ---------------------------------------------------------------- copying

    private void CopyEvent_Click(object sender, RoutedEventArgs e)
    {
        if (EventGrid.SelectedItem is CutsceneEventItem item)
            Copy(string.Join("\t", item.Time, item.Length, item.Actor, item.Kind, item.Title, item.Detail));
    }

    private void CopyEventTitle_Click(object sender, RoutedEventArgs e) => Copy((EventGrid.SelectedItem as CutsceneEventItem)?.Title);

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

    /// <summary>A list inside the scrolling side panel passes the wheel on to the panel, as it never scrolls itself.</summary>
    private void Nested_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var parent = (UIElement)((FrameworkElement)sender).Parent;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && !SearchBox.IsKeyboardFocused && PlayButton.IsEnabled)
        {
            if (_playing) Pause();
            else Play();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.None && !SearchBox.IsKeyboardFocused && CameraToggle.IsEnabled)
        {
            CameraToggle.IsChecked = CameraToggle.IsChecked != true;
            Stage.ThroughCamera = CameraToggle.IsChecked == true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None && !SearchBox.IsKeyboardFocused)
        {
            FitToggle.IsChecked = true;
            ApplyZoom();
        }
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }
}
