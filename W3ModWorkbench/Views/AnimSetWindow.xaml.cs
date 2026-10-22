using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// An animation set (.w2anims) as REDkit's animation browser shows it: its animations listed with their lengths,
/// frames, events and how far they move the character, the one picked playing on a character in 3D (the skeleton
/// drawn over it, a bone's path traced, as asked), its properties and motion extraction, and its events along a
/// timeline, both those the set keeps and those of the events files it names.
/// </summary>
public partial class AnimSetWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private readonly IArchive? _archive;
    private readonly string? _diskFile;
    private AnimSetFile? _set;
    private readonly Dictionary<RedObject, (Cr2wFile File, string Name)> _eventFiles = [];
    private List<AnimEvent> _outside = [];
    private List<(string Name, string Path)> _entities = [];
    private AnimSetAnimation? _animation;
    private Cr2wFile? _redkit;
    private bool _syncing;

    private static (string Name, string Path)[] People =>
    [
        ("Geralt", @"characters\player_entities\geralt\geralt_player.w2ent"),
        (L.T("AnimSetWindow.Man"), JobTreeFile.PreviewEntities[0]), (L.T("AnimSetWindow.Woman"), JobTreeFile.PreviewEntities[1]),
        (L.T("AnimSetWindow.Child"), JobTreeFile.PreviewEntities[2]), (L.T("AnimSetWindow.Dwarf"), JobTreeFile.PreviewEntities[3]),
    ];

    private sealed record AnimationItem(AnimSetAnimation Animation, int Events)
    {
        public string Name => Animation.Name;
        public float Length => Animation.Duration;
        public string LengthText => $"{Animation.Duration:0.00} s";
        public int Frames => Animation.Frames;
        public float Moves => Animation.Travel.Distance;
        public string MovesText => Animation.Motion is null ? "" : $"{Moves:0.00} m";
        public float Turns => Math.Abs(Animation.Travel.TurnDegrees);
        public string TurnsText => Animation.Motion is null ? "" : $"{Animation.Travel.TurnDegrees:0}°";
        public int Bones => Animation.Bones;
        public long Size => Animation.KeptSize;
        public string SizeText => Animation.KeptSize > 0 ? Format.Bytes(Animation.KeptSize) : "";
    }

    private AnimSetWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin, IArchive? archive = null, string? diskFile = null)
    {
        InitializeComponent();
        MakeModBtn.Show(path, diskFile ?? origin, readData);
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        _archive = archive;
        _diskFile = diskFile;
        Title = L.T("AnimSetWindow.AnimationSet", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        Preview.TimeChanged += (_, time) =>
        {
            Timeline.Time = time;
            if (Preview.Duration > 0 && Math.Abs(Timeline.Duration - Preview.Duration) > 0.001f) Timeline.Duration = Preview.Duration;
            ShowMotionAt(time);
        };
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => Preview.Pause();
    }

    public static bool CanShow(string name) => AnimSetFile.IsAnimSet(name);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new AnimSetWindow(path, source, () => data, locator, archive?.FilePath, archive).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new AnimSetWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file, diskFile: file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        try
        {
            _set = await Task.Run(() => AnimSetFile.Read(_readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = L.T("AnimSetWindow.AnimationSetCouldNot", ex.Message);
            return;
        }
        var set = _set;
        // played from as read, its buffers beside it on disk, or where the game keeps it
        Preview.UseSet(_path, set.File, n => _diskFile is { } disk && File.Exists($"{disk}.{n}.buffer") ? File.ReadAllBytes($"{disk}.{n}.buffer") : _locator.ReadBuffer(_path, n, _archive));
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        AnimationsTab.Header = L.T("AnimSetWindow.Animations", set.Animations.Count);
        int events = set.Animations.Sum(a => a.Events.Count);
        InfoText.Text = L.T("AnimSetWindow.CR2WAll", _path, _source, set.File.Version, L.T("Count.Animation", set.Animations.Count), L.T("Count.Event", events), set.Animations.Sum(a => a.Duration));
        SetNote.Text = L.T("AnimSetWindow.ReadingEventsFilesNames");
        ShowAnimations();
        ShowBlendChoices();
        FileProperties.Show(PropertyRow.Of(set.Resource, set.File));
        _ = LoadRedkitAsync();
        _ = LoadOutsideEventsAsync();

        // the character: the kind the set's folder is for, or a creature of its name
        _entities = [.. People];
        int pick = await Task.Run(() => PickEntity());
        _syncing = true;
        EntityBox.ItemsSource = _entities.Select(e => e.Name).ToList();
        EntityBox.SelectedIndex = pick;
        _syncing = false;
        await LoadEntityAsync();
        if (AnimationGrid.Items.Count > 0 && AnimationGrid.SelectedIndex < 0) AnimationGrid.SelectedIndex = 0;
    }

    /// <summary>
    /// The character the set is most likely for: Geralt, a woman, a child, a dwarf or a man by the words of its folder,
    /// else a creature whose template is named as the set's folder is (wolf for animations\monsters\wolf), added to the list.
    /// </summary>
    private int PickEntity()
    {
        // the words of its game path, folders and name (man_geralt_carry_item: man, geralt, carry, item)
        var folders = GamePath().ToLowerInvariant().Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).ToList();
        var words = folders.SelectMany(f => Path.GetFileNameWithoutExtension(f).Split('_', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
        if (words.Contains("geralt") || words.Contains("player") || words.Contains("ger")) return 0;
        if (words.Contains("woman") || words.Contains("women")) return 2;
        if (words.Contains("child") || words.Contains("children") || words.Contains("kid")) return 3;
        if (words.Contains("dwarf")) return 4;
        // a creature's set (animations\animals\bear, animations\monsters\wolf): a creature's template named as its folder is
        int kind = folders.FindIndex(f => f is "monsters" or "animals");
        if (kind >= 0 && kind + 1 < folders.Count - 1)
        {
            string creature = folders[kind + 1];
            var found = _locator.AllPaths(".w2ent")
                .Where(e => e.StartsWith(@"characters\npc_entities\monsters\", StringComparison.OrdinalIgnoreCase) || e.StartsWith(@"characters\npc_entities\animals\", StringComparison.OrdinalIgnoreCase))
                .Where(e => Path.GetFileNameWithoutExtension(e).Split('_').Contains(creature, StringComparer.OrdinalIgnoreCase)
                    || e.Contains($@"\{creature}\", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Length).FirstOrDefault();
            if (found is not null)
            {
                _entities.Add((Path.GetFileNameWithoutExtension(found), found));
                return _entities.Count - 1;
            }
        }
        return 1;
    }

    private async Task LoadEntityAsync()
    {
        if (EntityBox.SelectedIndex < 0) return;
        await Preview.LoadEntityAsync(_entities[EntityBox.SelectedIndex].Path, _locator);
        ShowBones();
        ShowEntitySets();
        await ApplyBlendAsync();
        if (_animation is { } playing) await PlayAsync(playing);
    }

    private sealed record SetItem(string Path, bool This)
    {
        public FontWeight Weight => This ? FontWeights.SemiBold : FontWeights.Normal;
        public string Note => This ? L.T("AnimSetWindow.Set") : "";
    }

    /// <summary>The animation sets the character plays from, as REDkit's Entity AnimSets tab lists them, this one marked.</summary>
    private void ShowEntitySets()
    {
        string who = EntityBox.SelectedIndex >= 0 ? _entities[EntityBox.SelectedIndex].Name : L.T("AnimSetWindow.Character");
        var sets = Preview.EntityAnimsets;
        string own = GamePath();
        bool has = sets.Any(s => s.Equals(own, StringComparison.OrdinalIgnoreCase));
        EntitySetsTab.Header = L.T("AnimSetWindow.CharactersSets2", sets.Count);
        EntitySetsTitle.Text = L.T("AnimSetWindow.AnimationSetsPlays", who);
        EntitySetsNote.Text = sets.Count == 0 ? L.T("AnimSetWindow.TemplatesNameNoAnimation", who)
            : L.T("AnimSetWindow.DoubleClickOneOpen", (has ? L.T("AnimSetWindow.SetOneThem") : L.T("AnimSetWindow.SetIsntOneThem")));
        EntitySetsList.ItemsSource = sets.Select(s => new SetItem(s, s.Equals(own, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>The set's path in the game's files: as opened, or below the content folder of a file opened from a folder.</summary>
    private string GamePath()
    {
        if (_diskFile is null) return _path;
        string full = _diskFile.Replace('/', '\\');
        foreach (var root in new[] { @"\r4data\", @"\content\", @"\files\" })
        {
            int at = full.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return full[(at + root.Length)..];
        }
        return _path;
    }

    private void OpenSet(string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void EntitySetsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntitySetsList.SelectedItem is SetItem item) OpenSet(item.Path);
    }

    private void EntitySetsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || EntitySetsList.SelectedItem is not SetItem item) return;
        OpenSet(item.Path);
        e.Handled = true;
    }

    private void OpenEntitySet_Click(object sender, RoutedEventArgs e)
    {
        if (EntitySetsList.SelectedItem is SetItem item) OpenSet(item.Path);
    }

    private void CopyEntitySet_Click(object sender, RoutedEventArgs e)
    {
        if (EntitySetsList.SelectedItem is SetItem item) Copy(item.Path);
    }

    private static string NoBlend => L.T("AnimSetWindow.NoBlend");
    private static string NoTrace => L.T("EntityWindow.None");
    private static string RestPose => L.T("AnimSetWindow.RestPose");

    /// <summary>
    /// What the animations blend from: the set's idle when it has one, as a character stands in it before it acts, at
    /// REDkit's 0.2 seconds; else none.
    /// </summary>
    private void ShowBlendChoices()
    {
        var names = _set!.Animations.Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        string? idle = names.Where(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase) && !n.Contains("additive", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Length).FirstOrDefault();
        _syncing = true;
        BlendBox.ItemsSource = new[] { NoBlend, RestPose }.Concat(names).ToList();
        BlendBox.SelectedItem = idle ?? NoBlend;
        _syncing = false;
    }

    /// <summary>Seconds typed in a box (a point or the machine's own decimal mark, an "s" after it or not), or null.</summary>
    private static float? Seconds(TextBox box)
    {
        string text = box.Text.Trim().TrimEnd('s', 'S').Trim();
        return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || float.TryParse(text, out v)
            ? Math.Clamp(v, 0, 10) : null;
    }

    /// <summary>Gives the preview what the blending fields say.</summary>
    private async Task ApplyBlendAsync()
    {
        string choice = BlendBox.SelectedItem as string ?? NoBlend;
        bool blend = choice != NoBlend;
        BlendInBox.IsEnabled = BlendOutBox.IsEnabled = blend;
        Preview.BlendIn = blend ? Seconds(BlendInBox) ?? 0.2f : 0;
        Preview.BlendOut = blend ? Seconds(BlendOutBox) ?? 0.2f : 0;
        bool fromAnimation = choice != NoBlend && choice != RestPose;
        if (!await Preview.BlendFromAsync(fromAnimation ? _path : null, fromAnimation ? choice : null))
            Preview.Message = L.T("AnimSetWindow.CantBlendedAnotherSkeleton", choice);
    }

    private void BlendBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) _ = ApplyBlendAsync();
    }

    private void BlendBox_LostFocus(object sender, RoutedEventArgs e) => _ = ApplyBlendAsync();

    private void BlendText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = ApplyBlendAsync();
        e.Handled = true;
    }

    private void LoopCheck_Click(object sender, RoutedEventArgs e)
    {
        Preview.Loop = LoopCheck.IsChecked == true;
        if (Preview.Loop && !Preview.IsPlaying) Preview.Play();
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void AnimationGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        BlendFromMenu.IsEnabled = AnimationGrid.SelectedItem is AnimationItem;
        int files = _set?.EventFiles.Count ?? 0;
        OpenEventsMenu.IsEnabled = files > 0;
        OpenEventsMenu.Header = files == 1 ? L.T("AnimSetWindow.Open", Path.GetFileName(_set!.EventFiles[0])) : L.T("AnimSetWindow.OpenEventsFiles");
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (AnimationGrid.SelectedItem is AnimationItem item) Copy(item.Name);
    }

    private void CopyNames_Click(object sender, RoutedEventArgs e) =>
        Copy(string.Join(Environment.NewLine, AnimationGrid.Items.OfType<AnimationItem>().Select(i => i.Name)));

    private void BlendFromThis_Click(object sender, RoutedEventArgs e)
    {
        if (AnimationGrid.SelectedItem is AnimationItem item) BlendBox.SelectedItem = item.Name;
    }

    private void OpenEvents_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _set?.EventFiles ?? []) OpenSet(file);
    }

    /// <summary>The bones of the character's rig, to trace one of.</summary>
    private void ShowBones()
    {
        var names = Preview.Rig?.Names ?? [];
        string? traced = TraceBox.SelectedItem as string;
        _syncing = true;
        TraceBox.ItemsSource = new[] { NoTrace }.Concat(names).ToList();
        TraceBox.SelectedItem = traced is not null && names.Contains(traced) ? traced : NoTrace;
        _syncing = false;
    }

    /// <summary>The events of the events files the set names, on its animations, each track named with its file.</summary>
    private async Task LoadOutsideEventsAsync()
    {
        var files = _set!.EventFiles;
        var owners = new Dictionary<RedObject, (Cr2wFile, string)>();
        _outside = await Task.Run(() =>
        {
            var events = new List<AnimEvent>();
            foreach (var path in files)
            {
                try
                {
                    // REDkit's copy first: cooking leaves out the events' track names
                    var redkit = RedkitSource.Read(path)?.Path is { } source ? File.ReadAllBytes(source) : null;
                    if ((redkit ?? _locator.ReadFile(path)) is not { } data) continue;
                    var read = AnimEventsFile.Read(data);
                    foreach (var e in read.Events) owners[e.Object] = (read.File, Path.GetFileName(path));
                    events.AddRange(read.Events);
                }
                catch (InvalidDataException) { }
            }
            return events;
        });
        foreach (var (o, f) in owners) _eventFiles[o] = f;
        SetNote.Text = files.Count == 0 ? L.T("AnimSetWindow.SetNamesNoEvents")
            : L.T("AnimSetWindow.EventsSet", string.Join(", ", files.Select(Path.GetFileName)), L.T("Count.Event", _outside.Count));
        ShowAnimations();
        if (_animation is { } shown) ShowEvents(shown);
    }

    private List<AnimEvent> EventsOf(AnimSetAnimation animation) =>
        [.. animation.Events, .. _outside.Where(e => e.Animation == animation.Name)];

    private void ShowAnimations()
    {
        if (_set is null) return;
        string text = FilterBox.Text.Trim();
        var counts = _outside.GroupBy(e => e.Animation).ToDictionary(g => g.Key, g => g.Count());
        var items = _set.Animations.Where(a => text.Length == 0 || a.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(a => new AnimationItem(a, a.Events.Count + counts.GetValueOrDefault(a.Name))).ToList();
        _syncing = true;
        AnimationGrid.ItemsSource = items;
        AnimationGrid.SelectedItem = items.FirstOrDefault(i => i.Animation == _animation);
        _syncing = false;
    }

    private void AnimationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || AnimationGrid.SelectedItem is not AnimationItem item) return;
        _ = PlayAsync(item.Animation);
    }

    /// <summary>Shows an animation's properties and events and plays it.</summary>
    private async Task PlayAsync(AnimSetAnimation animation)
    {
        _animation = animation;
        var a = animation;
        AnimationTitle.Text = a.Name;
        AnimationDetail.Text = L.T("AnimSetWindow.AnimationDetail", a.Duration, a.Frames, a.FramesPerSecond, L.T("Count.Bone", a.Bones), a.BufferKind)
            + (a.SourceSize > 0 ? L.T("AnimSetWindow.KeptSize", Format.Bytes(a.KeptSize), Format.Bytes(a.SourceSize)) : "");
        // REDkit's copy, when it has the animation, for what cooking left out
        var own = a.Animation;
        var file = _set!.File;
        if (_redkit is { } redkit && redkit.Exports.FirstOrDefault(x => x.ClassName == "CSkeletalAnimation" && redkit.Object(x.Index)["name"] as string == a.Name) is { } kept)
            (own, file) = (redkit.Object(kept.Index), redkit);
        AnimationProperties.Show(own is null ? [] : PropertyRow.Of(own, file));
        var (distance, turn) = a.Travel;
        MotionEnd.Text = a.Motion is null ? L.T("AnimSetWindow.NoneAnimationDoesntMove") : L.T("AnimSetWindow.MOverGroundTurned", Motion(a.Duration), distance, turn);
        ShowMotionAt(0);
        ShowEvents(a);
        if (await Preview.PlayAsync(_path, a.Name)) Timeline.Duration = Preview.Duration;
    }

    private void ShowEvents(AnimSetAnimation animation)
    {
        var events = EventsOf(animation);
        Timeline.Show(events, animation.Duration);
        TimelineTitle.Text = L.T("AnimSetWindow.ClickEventPropertiesDouble", animation.Name, L.T("Count.Event", events.Count), L.T("Count.Track", events.Select(e => e.Track).Distinct().Count()));
    }

    /// <summary>Where the motion extraction has the character at a time: its move and its turn.</summary>
    private string Motion(float time)
    {
        if (_animation?.Motion is not { } motion) return "";
        var (p, yaw) = motion.At(time);
        return L.T("AnimSetWindow.TranslationRotation", p.X, p.Y, p.Z, yaw * 180 / MathF.PI);
    }

    private void ShowMotionAt(float time)
    {
        if (_animation is not { } a) return;
        MotionNow.Text = a.Motion is null ? "" : L.T("AnimSetWindow.AtS", Motion(time), time);
        var (distance, turn) = a.Travel;
        MotionText.Text = a.Motion is null ? L.T("AnimSetWindow.DoesntMoveCharacterWhole", a.Name)
            : L.T("AnimSetWindow.MovesCharacterMTurns", a.Name, distance, turn);
    }

    private void EntityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || EntityBox.SelectedIndex < 0) return;
        _ = LoadEntityAsync();
    }

    private void Overlay_Click(object sender, RoutedEventArgs e)
    {
        Preview.ShowBones = BonesCheck.IsChecked == true;
        Preview.ShowBoneNames = NamesCheck.IsChecked == true;
    }

    private void TraceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        Preview.TracedBone = TraceBox.SelectedItem is string bone && TraceBox.SelectedIndex > 0 ? bone : null;
    }

    private void Timeline_EventClicked(object? sender, AnimEvent ev)
    {
        Preview.Pause();
        Preview.SetTime(ev.Start);
        EventTitle.Text = ev.Label;
        var from = _eventFiles.TryGetValue(ev.Object, out var outside) ? outside : ((Cr2wFile, string)?)null;
        EventDetail.Text = L.T("AnimSetWindow.EventDetail", ev.Object.ClassName, ev.Track, ev.Start) + (ev.Duration > 0 ? L.T("EventTimeline.EventFor", ev.Duration) : "") + $"  ·  {ev.Animation}"
            + (from is { } f ? L.T("AnimSetWindow.EventFrom", f.Item2) : L.T("AnimSetWindow.KeptInSet"));
        EventProperties.Show(PropertyRow.Of(ev.Object, from?.Item1 ?? _set!.File));
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

    /// <summary>Says where the set's properties come from, and keeps REDkit's copy for its animations' properties.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        _redkit = from.Redkit;
        if (from.Redkit?.First("CSkeletalAnimationSet") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, L.T("AnimSetWindow.AnimationSet2"));
        if (_animation is { } shown && _redkit is not null) _ = PlayAsync(shown);
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
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && AnimationGrid.IsKeyboardFocusWithin && AnimationGrid.SelectedItem is AnimationItem picked)
        {
            Copy(picked.Name);
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Left or Key.Right && Keyboard.Modifiers == ModifierKeys.None && Preview.Duration > 0 && !AnimationGrid.IsKeyboardFocusWithin)
        {
            // a frame back or on, paused
            Preview.Pause();
            float frame = 1f / (_animation?.FramesPerSecond ?? 30);
            Preview.SetTime(Preview.Time + (e.Key == Key.Right ? frame : -frame));
            e.Handled = true;
            return;
        }
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
