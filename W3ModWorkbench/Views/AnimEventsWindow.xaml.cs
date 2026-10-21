using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// An animation events file (.w2animev) as REDkit's animation events editor shows it: its animations listed, the one
/// picked playing on a character in 3D, and its events along a timeline under it, track by track, the timeline's line
/// following the animation. The animation set the events are for is found by the file's name, or by which set has
/// the most of its animations, or picked. Click an event to see its properties.
/// </summary>
public partial class AnimEventsWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private AnimEventsFile? _events;
    private List<string> _animsets = [];
    private HashSet<string> _inSet = new(StringComparer.Ordinal);
    private string? _animset;
    private string? _animation;
    private bool _syncing;

    private static readonly (string Name, string Path)[] Entities =
    [
        ("Geralt", @"characters\player_entities\geralt\geralt_player.w2ent"),
        ("Man", JobTreeFile.PreviewEntities[0]), ("Woman", JobTreeFile.PreviewEntities[1]),
        ("Child", JobTreeFile.PreviewEntities[2]), ("Dwarf", JobTreeFile.PreviewEntities[3]),
    ];

    private sealed record AnimationItem(string Name, int Events, bool InSet)
    {
        public string Note => $"{Events:N0}" + (InSet ? "" : "  no animation");
        public double Opacity => InSet ? 1 : 0.55;
        public string Tip => InSet ? $"{Name}: {Format.Count(Events, "event")}" : $"{Name} isn't in the animation set picked";
    }

    private AnimEventsWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = $"{Path.GetFileName(path)} - Animation events";
        InfoText.Text = $"{path}   ·   {source}";
        EntityBox.ItemsSource = Entities.Select(e => e.Name).ToList();
        Preview.TimeChanged += (_, time) =>
        {
            Timeline.Time = time;
            if (Preview.Duration > 0 && Math.Abs(Timeline.Duration - Preview.Duration) > 0.001f) Timeline.Duration = Preview.Duration;
        };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => Preview.Pause();
    }

    public static bool CanShow(string name) => AnimEventsFile.IsAnimEvents(name);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new AnimEventsWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new AnimEventsWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        try
        {
            _events = await Task.Run(() => AnimEventsFile.Read(_readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The animation events could not be read.\n\n" + ex.Message;
            return;
        }
        var ev = _events;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        AnimationsTab.Header = $"Animations ({ev.Animations.Count:N0})";
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {ev.File.Version}, {Format.Count(ev.Events.Count, "event")} on {Format.Count(ev.Animations.Count, "animation")}";
        ShowAnimations();
        FileProperties.Show(PropertyRow.Of(ev.Resource, ev.File));
        _ = LoadRedkitAsync();
        // the character: the kind the file's folder is for
        string lower = _path.ToLowerInvariant();
        int entity = lower.Contains("geralt") ? 0 : lower.Contains(@"\woman") || lower.Contains("woman_") ? 2 : lower.Contains(@"\child") ? 3 : lower.Contains(@"\dwarf") ? 4 : 1;
        _syncing = true;
        EntityBox.SelectedIndex = entity;
        _syncing = false;
        var loading = Preview.LoadEntityAsync(Entities[entity].Path, _locator);
        await FindSetAsync();
        await loading;
        if (AnimationList.Items.Count > 0 && AnimationList.SelectedIndex < 0)
            AnimationList.SelectedItem = AnimationList.Items.OfType<AnimationItem>().FirstOrDefault(a => a.InSet) ?? AnimationList.Items[0];
    }

    /// <summary>
    /// The animation set the events are for: the one named as the file is, or else the one with the most of its
    /// animations among those in the folders like the file's, read in the background.
    /// </summary>
    private async Task FindSetAsync()
    {
        SetNote.Text = "Finding the animation set…";
        _animsets = await Task.Run(() => _locator.AllPaths(".w2anims"));
        _syncing = true;
        SetBox.ItemsSource = _animsets;
        _syncing = false;
        string? found = AnimEventsFile.AnimsetByName(_path, _animsets);
        string how = "named as the file is";
        if (found is null)
        {
            var wanted = _events!.Animations.ToHashSet(StringComparer.Ordinal);
            // the sets in folders the events file's path shares a word with (man, woman, monsters, a creature's name)
            var words = _path.ToLowerInvariant().Split(['\\', '/', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && w is not ("sounds" or "sound" or "w2animev" or "characters" or "animations")).ToHashSet();
            var candidates = _animsets.Where(a => a.ToLowerInvariant().Split(['\\', '/', '_', '.'], StringSplitOptions.RemoveEmptyEntries).Any(words.Contains)).Take(300).ToList();
            int done = 0;
            var progress = new Progress<int>(n => SetNote.Text = $"Finding the animation set: {n:N0} of {candidates.Count:N0} looked in…");
            found = await Task.Run(() =>
            {
                string? best = null;
                int bestCount = 0;
                foreach (var set in candidates)
                {
                    ((IProgress<int>)progress).Report(++done);
                    int count = Names(set).Count(wanted.Contains);
                    if (count > bestCount) (best, bestCount) = (set, count);
                }
                return best;
            });
            how = "the one with the most of its animations";
        }
        if (found is null)
        {
            SetNote.Text = "No animation set has these animations; pick one above to play them.";
            return;
        }
        _syncing = true;
        SetBox.SelectedItem = found;
        _syncing = false;
        await UseSetAsync(found, how);
    }

    private readonly Dictionary<string, List<string>> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names of a set's animations, read once.</summary>
    private List<string> Names(string set)
    {
        lock (_names)
            if (_names.TryGetValue(set, out var known)) return known;
        var names = new List<string>();
        try
        {
            if (_locator.ReadFile(set) is { } data && Cr2wFile.Read(data) is var file)
                names = file.Exports.Where(x => x.ClassName == "CSkeletalAnimation").Select(x => file.Object(x.Index)["name"] as string ?? "").ToList();
        }
        catch (InvalidDataException) { }
        lock (_names) _names[set] = names;
        return names;
    }

    private async Task UseSetAsync(string set, string how)
    {
        _animset = set;
        var names = await Task.Run(() => Names(set));
        _inSet = names.ToHashSet(StringComparer.Ordinal);
        int have = _events!.Animations.Count(_inSet.Contains);
        SetNote.Text = $"{set} ({how}): {have:N0} of the {_events.Animations.Count:N0} animations are in it";
        ShowAnimations();
        if (_animation is { } playing) _ = PlayAsync(playing);
    }

    private void ShowAnimations()
    {
        if (_events is null) return;
        string text = FilterBox.Text.Trim();
        var counts = _events.Events.GroupBy(e => e.Animation).ToDictionary(g => g.Key, g => g.Count());
        var items = _events.Animations.Where(a => text.Length == 0 || a.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(a => new AnimationItem(a, counts[a], _animset is null || _inSet.Contains(a))).ToList();
        _syncing = true;
        AnimationList.ItemsSource = items;
        AnimationList.SelectedItem = items.FirstOrDefault(i => i.Name == _animation);
        _syncing = false;
    }

    private void AnimationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || AnimationList.SelectedItem is not AnimationItem item) return;
        _ = PlayAsync(item.Name);
    }

    /// <summary>Shows an animation's events on the timeline and plays it.</summary>
    private async Task PlayAsync(string animation)
    {
        _animation = animation;
        var events = _events!.Events.Where(e => e.Animation == animation).ToList();
        Timeline.Show(events, 0);
        TimelineTitle.Text = $"{animation}: {Format.Count(events.Count, "event")} on {Format.Count(events.Select(e => e.Track).Distinct().Count(), "track")}";
        if (_animset is null)
        {
            Preview.StopAnimation();
            Preview.Message = "Pick the animation set the events are for, above, to play the animations.";
            return;
        }
        if (await Preview.PlayAsync(_animset, animation)) Timeline.Duration = Preview.Duration;
    }

    private void SetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SetBox.SelectedItem is not string set) return;
        _ = UseSetAsync(set, "picked");
    }

    private async void EntityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || EntityBox.SelectedIndex < 0) return;
        await Preview.LoadEntityAsync(Entities[EntityBox.SelectedIndex].Path, _locator);
        if (_animation is { } playing) await PlayAsync(playing);
    }

    private void Timeline_EventClicked(object? sender, AnimEvent ev)
    {
        Preview.Pause();
        Preview.SetTime(ev.Start);
        EventTitle.Text = ev.Label;
        EventDetail.Text = $"{ev.Object.ClassName}  ·  track {ev.Track}  ·  at {ev.Start:0.000} s" + (ev.Duration > 0 ? $" for {ev.Duration:0.000} s" : "") + $"  ·  {ev.Animation}";
        EventProperties.Show(PropertyRow.Of(ev.Object, _events!.File));
        Tabs.SelectedItem = EventTab;
    }

    /// <summary>Opens the sound a sound event plays, by its name.</summary>
    private void Timeline_EventOpened(object? sender, AnimEvent ev)
    {
        if (AnimEvent.Text(ev.Object["soundEventName"]) is { Length: > 0 } sound && sound != "None") SoundWindow.OpenEvent(sound, _locator, this);
    }

    private void Timeline_TimePicked(object? sender, float time)
    {
        Preview.Pause();
        Preview.SetTime(time);
        Timeline.Time = time;
    }

    /// <summary>Says where the file's properties come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit?.First("CExtAnimEventsFile") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, "animation events file");
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowAnimations();
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Tabs.SelectedItem = AnimationsTab;
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.Space)
        {
            if (Preview.IsPlaying) Preview.Pause(); else Preview.Play();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
