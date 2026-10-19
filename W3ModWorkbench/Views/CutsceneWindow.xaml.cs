using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.Core.Sounds;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>A dialog line in the Dialogs tab: when, who, what's said, the sound event that says it, and which line it is.</summary>
public sealed record CutsceneDialogRow(string Time, string Actor, string Text, string Sound, int Line, CutsceneEvent Event);

/// <summary>
/// Shows a cutscene (.w2cutscene) as REDkit's cutscene editor does: its actors put together from their entities in the
/// appearances it gives them and played on their rigs, seen from anywhere or through the cutscene's camera; a timeline of
/// its events, the scene's and each actor's; and beside it the picked event's properties, the actors and their
/// animations, the cutscene's own properties, its effects, each animation with its event track, the dialog lines, the
/// scenes that play it and what the camera does. Its events can also be searched in a list, and the files it uses open
/// in their viewers.
/// </summary>
public partial class CutsceneWindow : Window, IMessageHost
{
    // the zoom slider runs from 4 to 400 pixels a second, evenly in its logarithm
    private const double MinZoom = 4, MaxZoom = 400;
    private const int TextureSize = 1024;

    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    // where the cutscene was opened from (its archive, or the file on disk): it says whether it's one of the game's own
    private readonly string? _origin;
    private CutsceneFile? _cutscene;
    private List<CutsceneEventItem> _events = [];
    private bool _syncing;
    private CutscenePlayback? _playback;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private float _playedFrom, _time;
    private bool _playing, _scrubbing;

    // the actors drawn, and for each part of the 3D view the actor it belongs to and which of its meshes it is
    private List<CastMember> _cast = [];
    private readonly List<(CastMember Member, int Part)> _parts = [];
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _closing = new();

    private CutsceneWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = $"{Path.GetFileName(path)} - Cutscene";
        InfoText.Text = $"{path}   ·   {source}";
        ZoomSlider.Value = ZoomToSlider(40);
        View.FieldOfView = FovSlider.Value; // REDkit's preview starts at 60°
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            Pause();
            _closing.Cancel();
        };
    }

    /// <summary>Whether a file's name says it's a cutscene this window can show.</summary>
    public static bool CanShow(string name) => CutsceneFile.IsCutscene(name);

    /// <summary>
    /// Opens a cutscene in a window of its own. The files it uses are looked for with <paramref name="locator"/>;
    /// <paramref name="archive"/> is where it came from, which says whether it's one of the game's own.
    /// </summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new CutsceneWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    /// <summary>Opens a cutscene file on disk: one in the project's game files, or one extracted from a bundle.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new CutsceneWindow(Core.Projects.FileSafety.GamePathOf(file) ?? Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file)
            .Standalone(owner).Show();

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
        NoEffectsText.Visibility = c.Effects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EffectList.Visibility = c.Effects.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LinkList.ItemsSource = c.Links.Select(l => new CutsceneLinkItem(l)).ToList();
        SceneGrid.ItemsSource = c.UsedIn.Select(p => new CutsceneLinkItem(new CutsceneLink(p, "Scene that plays it"))).ToList();
        var animations = c.Actors.SelectMany(a => a.Animations).ToList();
        AnimationBox.ItemsSource = animations;
        if (animations.Count > 0) AnimationBox.SelectedIndex = 0;
        CameraName.Text = c.Actors.FirstOrDefault(a => a.Type == "Camera")?.Name ?? "none";

        var more = new List<string>();
        if (c.AudioTrack is { } audio) more.Add($"Its sound and voices are the audio event {audio}.");
        if (c.PlaceTags.Count > 0) more.Add($"Played at the place tagged {string.Join(", ", c.PlaceTags)}.");
        if (c.HiddenTags.Count > 0) more.Add($"Hides what's tagged {string.Join(", ", c.HiddenTags)} while it plays.");
        if (c.FadeBefore > 0 || c.FadeAfter > 0) more.Add($"Fades {c.FadeBefore:0.##} s before and {c.FadeAfter:0.##} s after.");
        if (c.Level is { } level) more.Add($"Made in {level}.");
        MoreText.Text = string.Join("\n", more);
        MoreText.Visibility = more.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplyFilter();
        ShowDialogs(false);
        int shown = _events.Count(e => e.Event.Kind != CutsceneEventKind.Cloth);
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {c.File.Version}, {CutsceneTimeline.FormatTime(c.Duration)}, "
                      + $"{Format.Count(c.Actors.Count, "actor")}, {Format.Count(_events.Count, "event")}, {Format.Count(c.Effects.Count, "effect")}";
        if (shown == 0) SearchPlaceholder.Text = "No events: the actors' animations play on their own";
        await Task.WhenAll(LoadPlaybackAsync(c), LoadDialogLinesAsync(c), ShowTemplateAsync(c), LoadTrackAsync(c));
    }

    /// <summary>The cutscene's own properties, from REDkit's source file for one of the game's own when REDkit has it.</summary>
    private async Task ShowTemplateAsync(CutsceneFile c)
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        TemplateProperties.Show(from.Redkit is { } redkit && RedkitSource.Match(redkit, c.Template, c.File) is { } own
            ? PropertyRow.Of(own, redkit)
            : PropertyRow.Of(c.Template, c.File));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, "cutscene");
    }

    /// <summary>
    /// Finds the lines of the dialog events in the scene that plays the cutscene, and shows them in words on the
    /// timeline, in the list and in the Dialogs tab. Nothing changes when the scene isn't found or its lines don't match.
    /// </summary>
    private async Task LoadDialogLinesAsync(CutsceneFile c)
    {
        if (!c.SceneEvents.Any(e => e.Kind == CutsceneEventKind.Dialog) || GameLocator.Resolve(null) is not { } game) return;
        string name = Path.GetFileName(_path);
        List<(string, string)>? lines = null;
        List<uint?> ids = [];
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
                    var said = section.Elements.Where(e => e.Kind == SceneElementKind.Line).ToList();
                    ids = said.Select(e => e.StringId).ToList();
                    return said.Select(e => (e.Speaker ?? "", e.StringId is { } id && strings.WithId(id) is { Count: > 0 } found ? found[^1].Text : $"(string {e.StringId})"))
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
        ShowDialogs(true);
        await LoadVoicesAsync(game, c, ids);
    }

    // ---------------------------------------------------------------- sound

    /// <summary>A dialog line's voice: when it's said, and its sound in stereo.</summary>
    private sealed record Voice(float Start, float[] Stereo, string Name);

    private List<Voice> _voices = [];
    // the cutscene's recorded music and effects, in stereo at 48 kHz
    private float[]? _track;
    private NAudio.Wave.WaveOutEvent? _output;
    private NAudio.Wave.SampleProviders.VolumeSampleProvider? _trackVolume, _voicesVolume;

    /// <summary>
    /// The dialog lines' voices, from the game's speech files (in English, as the words are): the cutscene marks when
    /// each line is said, and the scene that plays it says which line it is. Lines with no voice are left silent.
    /// </summary>
    private async Task LoadVoicesAsync(string game, CutsceneFile c, List<uint?> ids)
    {
        if (W3Strings.Language("en") is not { } en) return;
        var marks = c.SceneEvents.Where(e => e.Kind == CutsceneEventKind.Dialog).OrderBy(e => e.Start).ToList();
        if (marks.Count != ids.Count) return;
        try
        {
            var speech = await W3Speech.ForLanguage(game, en);
            var token = _closing.Token;
            _voices = await Task.Run(() =>
            {
                var voices = new List<Voice>();
                for (int i = 0; i < marks.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (ids[i] is not { } id || speech.FirstOrDefault(s => s.Has(id))?.Sound(id) is not { } riff) continue;
                    var (samples, channels) = WwiseOpus.Decode(riff);
                    if (channels > 0 && samples.Length > 0) voices.Add(new Voice(marks[i].Start, Stereo(samples, channels), $"{i + 1:00} {marks[i].Detail} {id}"));
                }
                return voices;
            }, token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            return;
        }
        SoundLoaded();
    }

    /// <summary>The cutscene's recorded sound, its music and effects: the audio track it names, from the game's sound caches.</summary>
    private async Task LoadTrackAsync(CutsceneFile c)
    {
        if (c.AudioTrack is not { } name || GameLocator.Resolve(null) is not { } game) return;
        try
        {
            var caches = await SoundCache.ForGame(game);
            _track = await Task.Run(() => SoundCache.Track(caches, name) is { } track ? Stereo(track.Samples, track.Channels) : null, _closing.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            return;
        }
        SoundLoaded();
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

    private void SoundLoaded()
    {
        TrackToggle.IsEnabled = _track is not null;
        SaveSoundButton.IsEnabled = _track is not null || _voices.Count > 0;
        VoicesToggle.IsEnabled = _voices.Count > 0;
        if (_voices.Count > 0) VoicesToggle.ToolTip = $"Play the dialog lines' voices (V): {Format.Count(_voices.Count, "voiced line")}, in English";
        if (_playing) StartSound(_time);
    }

    /// <summary>
    /// Plays the sound from a time, as the cutscene plays from there: the track and the voices each go into the mixer
    /// on their own, so either can be turned off while it plays, and each voice comes in when its line is said.
    /// </summary>
    private void StartSound(float time)
    {
        StopSound();
        const int Rate = WwiseOpus.SampleRate;
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        var mixer = new NAudio.Wave.SampleProviders.MixingSampleProvider(format);
        int at = (int)(time * Rate);
        if (_track is { } track && at * 2 < track.Length)
        {
            _trackVolume = new NAudio.Wave.SampleProviders.VolumeSampleProvider(new StereoSamples(track, at, format)) { Volume = TrackToggle.IsChecked == true ? 1 : 0 };
            mixer.AddMixerInput(_trackVolume);
        }
        var voices = new NAudio.Wave.SampleProviders.MixingSampleProvider(format);
        foreach (var voice in _voices)
        {
            int from = at - (int)(voice.Start * Rate);
            if (from * 2 >= voice.Stereo.Length) continue; // said already
            ISampleProvider line = new StereoSamples(voice.Stereo, Math.Max(0, from), format);
            if (from < 0) line = new NAudio.Wave.SampleProviders.OffsetSampleProvider(line) { DelayBySamples = -from * 2 };
            voices.AddMixerInput(line);
        }
        if (voices.MixerInputs.Any())
        {
            _voicesVolume = new NAudio.Wave.SampleProviders.VolumeSampleProvider(voices) { Volume = VoicesToggle.IsChecked == true ? 1 : 0 };
            mixer.AddMixerInput(_voicesVolume);
        }
        if (!mixer.MixerInputs.Any()) return;
        try
        {
            _output = new NAudio.Wave.WaveOutEvent { DesiredLatency = 120 };
            _output.Init(mixer);
            _output.Play();
        }
        catch (NAudio.MmException)
        {
            // no sound device: it plays without
            StopSound();
        }
    }

    private void StopSound()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _trackVolume = _voicesVolume = null;
    }

    /// <summary>
    /// Saves the cutscene's sound as WAV files, as REDkit's "Dump all voiceovers to file" does: its music and effects,
    /// each voiced line (numbered, with who says it and its string ID), and everything mixed as it plays.
    /// </summary>
    private void SaveSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for the cutscene's sound" };
        if (dialog.ShowDialog(this) != true) return;
        string name = Path.GetFileNameWithoutExtension(_path);
        var saved = new List<string>();
        try
        {
            void Save(string file, float[] stereo)
            {
                string path = Path.Combine(dialog.FolderName, string.Concat(file.Split(Path.GetInvalidFileNameChars())) + ".wav");
                File.WriteAllBytes(path, WwiseOpus.ToWav(Pcm(stereo), 2));
                saved.Add(path);
            }
            if (_track is { } track) Save($"{name} music and effects", track);
            foreach (var voice in _voices) Save($"{name} {voice.Name}", voice.Stereo);
            if (_track is not null && _voices.Count > 0) Save($"{name} mixed", Mixed());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The sound couldn't be saved: {ex.Message}", "Save sound", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (saved.Count > 0) ShellReveal.Show(saved);
    }

    /// <summary>The track and the voices mixed, each voice where its line is said.</summary>
    private float[] Mixed()
    {
        const int Rate = WwiseOpus.SampleRate;
        int length = Math.Max(_track?.Length ?? 0, _voices.Count == 0 ? 0 : _voices.Max(v => (int)(v.Start * Rate) * 2 + v.Stereo.Length));
        var mix = new float[length];
        _track?.CopyTo(mix, 0);
        foreach (var voice in _voices)
        {
            int from = (int)(voice.Start * Rate) * 2;
            for (int i = 0; i < voice.Stereo.Length && from + i < length; i++) mix[from + i] += voice.Stereo[i];
        }
        return mix;
    }

    private static short[] Pcm(float[] samples) => samples.Select(s => (short)Math.Clamp(s * 32767, short.MinValue, short.MaxValue)).ToArray();

    private void TrackToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_trackVolume is not null) _trackVolume.Volume = TrackToggle.IsChecked == true ? 1 : 0;
    }

    private void VoicesToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_voicesVolume is not null) _voicesVolume.Volume = VoicesToggle.IsChecked == true ? 1 : 0;
    }

    /// <summary>Stereo samples from a point on, for the mixer.</summary>
    private sealed class StereoSamples(float[] samples, int fromFrame, NAudio.Wave.WaveFormat format) : ISampleProvider
    {
        private int _at = fromFrame * 2;

        public NAudio.Wave.WaveFormat WaveFormat => format;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Max(0, Math.Min(count, samples.Length - _at));
            Array.Copy(samples, _at, buffer, offset, n);
            _at += n;
            return n;
        }
    }

    /// <summary>The Dialogs tab: each dialog line, with its words when they were found in the scene that plays it.</summary>
    private void ShowDialogs(bool named)
    {
        if (_cutscene is not { } c) return;
        var marks = c.SceneEvents.Where(e => e.Kind == CutsceneEventKind.Dialog).OrderBy(e => e.Start).ToList();
        DialogGrid.ItemsSource = marks.Select((e, i) => new CutsceneDialogRow(CutsceneTimeline.FormatTime(e.Start),
            named ? e.Detail : "", named ? e.Title : "(the words are in the scene that plays it)",
            e.Source?["eventName"] as string ?? "", i + 1, e)).ToList();
        DialogNote.Text = marks.Count == 0 ? "This cutscene has no dialog lines."
            : named ? $"{Format.Count(marks.Count, "line")}, their words and speakers from the scene that plays it."
            : $"{Format.Count(marks.Count, "line")}. The cutscene only marks when each is said; their words are read from the scene that plays it, when it's found.";
    }

    // ---------------------------------------------------------------- the actors, drawn and played

    /// <summary>
    /// Reads the actors' animations and finds their rigs, then puts each actor together from its entity and plays it.
    /// That can take a moment the first time the game's bundles are looked in.
    /// </summary>
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
        ScrubSlider.Maximum = Math.Max(0.01, p.Duration);
        ScrubSlider.SmallChange = 1 / 30.0;
        ScrubSlider.LargeChange = 1;
        ScrubSlider.IsEnabled = PlayButton.IsEnabled = StartButton.IsEnabled = true;
        CameraToggle.IsEnabled = p.Camera?.Rig is not null;
        CameraBox.ItemsSource = p.Cameras.Where(c => c.Rig is not null).Select(c => c.Name).ToList();
        CameraBox.SelectedItem = p.Camera?.Name;
        CameraBox.IsEnabled = p.Cameras.Count(c => c.Rig is not null) > 1;
        if (!CameraToggle.IsEnabled) CameraToggle.ToolTip = "The cutscene has no camera animation, or its rig wasn't found";
        StageText.Text = p.Actors.Count == 0 ? "There are no body animations to play." : "Putting the actors together…";
        SetTime(0);
        await LoadCastAsync(c, p);
    }

    /// <summary>Reads each actor's entity and meshes, shows them all in their pose at the time shown, then puts their textures on.</summary>
    private async Task LoadCastAsync(CutsceneFile c, CutscenePlayback p)
    {
        var token = _closing.Token;
        var notes = new List<string>(p.Problems);
        List<CastMember> cast;
        try
        {
            cast = await Task.Run(() => CutsceneCast.Load(c, p, _locator, notes, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _cast = cast;
        _parts.Clear();
        var parts = new List<(MeshFile Mesh, Matrix4x4 Place)>();
        foreach (var member in cast)
            for (int i = 0; i < member.Parts.Count; i++)
            {
                // placed by the pose, which puts the actor where the cutscene has it
                parts.Add((member.Parts[i].Mesh, Matrix4x4.Identity));
                _parts.Add((member, i));
            }
        View.ShowParts(parts);
        PoseCast();
        SetFloor(cast);
        FitActors();
        // again once the view has its size, as the first fit can come before it's laid out
        _ = Dispatcher.InvokeAsync(() => { if (!View.IsLookingThrough) FitActors(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        StageText.Text = cast.Count > 0 ? "" : p.Actors.Count == 0 ? "There are no body animations to play." : "None of the actors could be put together.";
        StageText.Visibility = cast.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        StageNotes.Text = string.Join("\n", notes.Take(6)) + (notes.Count > 6 ? $"\n… and {notes.Count - 6} more" : "");
        if (CameraToggle.IsChecked == true) SetTime(_time);

        // the colour textures, put on as each is read, in the colours each actor's appearance gives each component
        for (int part = 0; part < _parts.Count; part++)
        {
            var (member, local) = _parts[part];
            var mesh = member.Parts[local].Mesh;
            var (colorEntity, colorAppearance) = member.ColoredBy[local];
            var coloring = LookTextures.Coloring(colorEntity, colorAppearance, member.DrawnBy[local].Name);
            // a stand-in piece of the player's gear is undyed: its dye's red and blue parts as plain dark leather
            if (coloring is null && !ReferenceEquals(colorEntity, member.Entity)) coloring = (Undyed, Undyed);
            for (int m = 0; m < mesh.Materials.Count; m++)
            {
                int at = part, material = m;
                var (picture, leaveOut) = await Task.Run(() => LookTextures.ForMaterial(mesh, material, _locator, Texture, coloring), token)
                    .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : ((BitmapSource?)null, false), TaskScheduler.Default);
                if (token.IsCancellationRequested) return;
                if (leaveOut) View.LeaveOut(at, material);
                else if (picture is not null) View.SetTexture(at, material, picture);
            }
        }
    }

    /// <summary>The colour the parts of an undyed piece of gear that take a dye are shown in: no colour, a little darker.</summary>
    private static readonly ColorShift Undyed = new(0, -100, -35);

    private Task<BitmapSource?> Texture(string path)
    {
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task))
                _textures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, TextureSize));
            return task;
        }
    }

    /// <summary>
    /// The floor grid under everywhere the actors go in the whole cutscene, at the lowest any of them gets (their feet),
    /// so it stays put as they move about.
    /// </summary>
    private float _ground;

    private void SetFloor(List<CastMember> cast)
    {
        if (cast.Count == 0 || _playback is null) return;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var lowest = new List<float>();
        foreach (var member in cast)
        {
            var rig = member.Played.Rig!;
            var pose = new Core.Animation.BoneTransform[rig.Count];
            var model = new Matrix4x4[rig.Count];
            float low = float.MaxValue;
            for (int s = 0; s <= 24; s++)
            {
                rig.Pose.CopyTo(pose, 0);
                member.Played.Clip.Sample(_playback.Duration * s / 24, pose);
                rig.ToModel(pose, model);
                foreach (var m in model)
                {
                    min = Vector3.Min(min, m.Translation);
                    max = Vector3.Max(max, m.Translation);
                    low = Math.Min(low, m.Translation.Z);
                }
            }
            lowest.Add(low);
        }
        // the ground is where most of the actors' feet get to: one parked below it out of sight (Ciri, before she comes in) doesn't pull it down
        var sorted = lowest.Order().ToList();
        _ground = sorted[sorted.Count / 2];
        // an actor parked far from the rest (a prop out of sight) would spread the grid thin: kept to 200 m across
        var middle = (min + max) / 2;
        var half = Vector3.Min((max - min) / 2, new Vector3(100));
        View.SetFloorArea(middle - half, middle + half, _ground);
    }

    /// <summary>
    /// Turns the free camera to see the actors where they are at the time shown, leaving out any far from the rest (one
    /// parked out of sight until it comes in), as the stage the actors were drawn on before did.
    /// </summary>
    private void FitActors()
    {
        if (_cast.Count == 0 || _playback is null)
        {
            View.FitToView();
            return;
        }
        var boxes = new List<(Vector3 Min, Vector3 Max)>();
        foreach (var member in _cast)
        {
            var rig = member.Played.Rig!;
            var pose = rig.Pose.ToArray();
            member.Played.Clip.Sample(_time, pose);
            var model = new Matrix4x4[rig.Count];
            rig.ToModel(pose, model);
            // the body's bones, not the handles the engine keeps away from it (Trajectory, Trajectory01…, Reference, IK_…)
            var points = model.Where((_, i) => rig.Parents[i] >= 0 && rig.Names[i] != "Reference"
                    && !rig.Names[i].StartsWith("Trajectory", StringComparison.OrdinalIgnoreCase)
                    && !rig.Names[i].StartsWith("IK_", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Translation).ToList();
            // a helper with no body, or an actor parked under the floor out of sight, isn't what's being looked at
            if (points.Count == 0 || points.Max(q => q.Z) < _ground - 0.2f) continue;
            // a little round the bones, for the hair, gear and swords beyond them
            boxes.Add((points.Aggregate(Vector3.Min) - new Vector3(0.3f), points.Aggregate(Vector3.Max) + new Vector3(0.3f)));
        }
        if (boxes.Count == 0)
        {
            View.FitToView();
            return;
        }
        var centres = boxes.Select(b => (b.Min + b.Max) / 2).ToList();
        float Median(IEnumerable<float> values) { var sorted = values.Order().ToList(); return sorted[sorted.Count / 2]; }
        var middle = new Vector3(Median(centres.Select(c => c.X)), Median(centres.Select(c => c.Y)), Median(centres.Select(c => c.Z)));
        float spread = Math.Max(5, 3 * Median(centres.Select(c => Vector3.Distance(c, middle))));
        var kept = boxes.Where((_, i) => Vector3.Distance(centres[i], middle) <= spread).ToList();
        if (kept.Count == 0) kept = boxes;
        View.FitTo(kept.Select(b => b.Min).Aggregate(Vector3.Min), kept.Select(b => b.Max).Aggregate(Vector3.Max));
    }

    /// <summary>Puts every actor's meshes in their pose at the time shown.</summary>
    private void PoseCast()
    {
        if (_cast.Count == 0) return;
        var posers = _cast.ToDictionary(m => m, m => CutsceneCast.Pose(m, _time));
        View.Pose((part, chunk) => part < _parts.Count && posers[_parts[part].Member] is { } pose ? pose(_parts[part].Part, chunk) : null);
    }

    /// <summary>Shows a time everywhere: the actors' pose, the camera, the scrub bar, the timeline's playhead and the Camera tab.</summary>
    private void SetTime(float time)
    {
        if (_playback is null) return;
        _time = time = Math.Clamp(time, 0, _playback.Duration);
        PoseCast();
        var camera = _playback.CameraAt(time);
        if (CameraToggle.IsChecked == true && camera is { } through) View.LookThrough(through.Position, through.Forward, through.Up, through.FieldOfView);
        Timeline.Playhead = time;
        TimeText.Text = $"{CutsceneTimeline.FormatTime(time)} / {CutsceneTimeline.FormatTime(_playback.Duration)}";
        _scrubbing = true;
        ScrubSlider.Value = time;
        _scrubbing = false;
        ShowCamera(time, camera);
        LensText.Text = camera is { } lens ? $"FOV {lens.FieldOfView:0.0}°   DOF {CameraDof.Text}" : "";
    }

    /// <summary>The Camera tab: where the camera is, its field of view, and the depth of field, clipping planes and fade at a time.</summary>
    private void ShowCamera(float time, CameraPose? camera)
    {
        if (_cutscene is not { } c) return;
        CameraFov.Text = camera is { } cam ? $"{cam.FieldOfView:0.#}°" : "—";
        CameraPosition.Text = camera is { } at ? $"{at.Position.X:0.00}, {at.Position.Y:0.00}, {at.Position.Z:0.00}" : "—";
        CameraLooking.Text = camera is { } looking ? $"{looking.Forward.X:0.00}, {looking.Forward.Y:0.00}, {looking.Forward.Z:0.00}" : "—";
        // what's in force at the time: the last of each kind that has started
        CutsceneEvent? Last(Func<CutsceneEvent, bool> which) => c.SceneEvents.Where(e => which(e) && e.Start <= time + 1e-3f).OrderBy(e => e.Start).LastOrDefault();
        CameraDof.Text = Last(e => e.ClassName.Contains("Dof", StringComparison.Ordinal)) is { } dof ? $"{dof.Detail}  (from {CutsceneTimeline.FormatTime(dof.Start)})" : "the default";
        CameraPlanes.Text = Last(e => e.ClassName == "CExtAnimCutsceneSetClippingPlanesEvent") is { } planes ? $"{planes.Detail}  (from {CutsceneTimeline.FormatTime(planes.Start)})" : "the default";
        CameraFade.Text = Last(e => e.ClassName == "CExtAnimCutsceneFadeEvent") is { } fade
            ? $"{fade.Title}{(fade.Detail.Length > 0 ? ", " + fade.Detail : "")}  (at {CutsceneTimeline.FormatTime(fade.Start)})" : "none yet";
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) Pause();
        else Play();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        SetTime(0);
    }

    private void Play()
    {
        if (_playback is null || _playing) return;
        // from the start again when it's at the end
        _playedFrom = _time >= _playback.Duration - 0.01f ? 0 : _time;
        _clock.Restart();
        _playing = true;
        StartSound(_playedFrom);
        PlayIcon.Text = ""; // pause
        System.Windows.Media.CompositionTarget.Rendering += Tick;
    }

    private void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _clock.Stop();
        StopSound();
        PlayIcon.Text = ""; // play
        System.Windows.Media.CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_playback is null) return;
        float time = _playedFrom + (float)_clock.Elapsed.TotalSeconds;
        if (time >= _playback.Duration && LoopToggle.IsChecked == true)
        {
            // round again from the start
            _playedFrom = 0;
            _clock.Restart();
            time = 0;
            StartSound(0);
        }
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

    private void CameraToggle_Click(object sender, RoutedEventArgs e) => ApplyCameraMode();

    /// <summary>Looks through the cutscene's camera, in a 16:9 picture with bars around it, or gives the view back to the mouse.</summary>
    private void ApplyCameraMode()
    {
        bool through = CameraToggle.IsChecked == true && _playback?.Camera?.Rig is not null;
        Bars.Visibility = through ? Visibility.Visible : Visibility.Collapsed;
        LayoutStage();
        if (through) SetTime(_time);
        else View.StopLookingThrough();
    }

    private void StageArea_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutStage();

    /// <summary>The view fills the stage, or through the camera is the largest 16:9 picture that fits.</summary>
    private void LayoutStage()
    {
        if (Bars.Visibility == Visibility.Visible)
        {
            double width = Math.Min(StageArea.ActualWidth, StageArea.ActualHeight * Aspect);
            View.Width = Math.Max(1, width);
            View.Height = Math.Max(1, width / Aspect);
        }
        else
        {
            View.Width = double.NaN;
            View.Height = double.NaN;
        }
    }

    private void FitView_Click(object sender, RoutedEventArgs e)
    {
        CameraToggle.IsChecked = false;
        ApplyCameraMode();
        FitActors();
    }

    private void Fov_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FovText is null) return;
        FovText.Text = $"{e.NewValue:0}°";
        View.FieldOfView = e.NewValue;
    }

    private void Light_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => View.LightAngle = e.NewValue;

    /// <summary>The shape of the picture through the camera: width over height.</summary>
    private double Aspect => AspectBox?.SelectedIndex switch { 1 => 21.0 / 9, 2 => 2.39, 3 => 4.0 / 3, _ => 16.0 / 9 };

    private void Aspect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        LayoutStage();
    }

    private void CameraBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_playback is null || CameraBox.SelectedItem is not string name || _playback.Cameras.FirstOrDefault(c => c.Name == name) is not { } camera) return;
        _playback.Camera = camera;
        SetTime(_time);
    }

    private void Textured_Click(object sender, RoutedEventArgs e) => View.Textured = TexturedToggle.IsChecked == true;

    private void Wire_Click(object sender, RoutedEventArgs e) => View.Wireframe = WireToggle.IsChecked == true;

    private void Floor_Click(object sender, RoutedEventArgs e) => View.ShowFloor = FloorToggle.IsChecked == true;

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

    private void TemplateSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TemplateSearchPlaceholder.Visibility = TemplateSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TemplateProperties.Search = TemplateSearch.Text;
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

    /// <summary>An event clicked on the timeline is picked in the list too, and its properties shown, as REDkit shows them.</summary>
    private void Timeline_EventClicked(object? sender, CutsceneEvent? clicked)
    {
        if (clicked is null) return;
        ShowEvent(clicked);
        Tabs.SelectedItem = PropertiesTab;
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
        ShowEvent(item.Event);
        Timeline.Selected = item.Event;
        if (!_syncing) Timeline.Reveal(item.Event);
    }

    /// <summary>An event's properties in the Properties tab: all it keeps, as REDkit lists them.</summary>
    private void ShowEvent(CutsceneEvent e)
    {
        EventTitle.Text = e.Title;
        EventDetail.Text = string.Join("  ·  ", new[] { CutsceneTimeline.FormatTime(e.Start) + (e.Duration > 0 ? $" for {CutsceneTimeline.FormatTime(e.Duration)}" : ""),
            e.Actor ?? "the whole cutscene", e.ClassName, e.Detail }.Where(s => s.Length > 0));
        EventProperties.Show(e.Source is { } source && _cutscene is { } c ? PropertyRow.Of(source, c.File) : []);
    }

    // ---------------------------------------------------------------- the side tabs

    private void EffectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EffectList.SelectedItem is not CutsceneEffectItem item || item.Effect.Source is not { } fx || _cutscene is not { } c) return;
        EffectProperties.Show(PropertyRow.Of(fx, c.File));
    }

    /// <summary>The animation picked: its properties and its set entry's, and the events along it.</summary>
    private void AnimationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnimationBox.SelectedItem is not CutsceneAnimation animation || _cutscene is not { } c) return;
        var rows = new List<PropertyRow>();
        if (animation.Object is { } o) rows.AddRange(PropertyRow.Of(o, c.File, leaveOut: ["animBuffer"]));
        if (animation.Entry is { } entry)
        {
            rows.Add(new PropertyRow("Set entry", "", "", 0, null, heading: true));
            rows.AddRange(PropertyRow.Of(entry, c.File, leaveOut: ["animation", "events"]));
        }
        AnimationProperties.Show(rows);
        AnimationEvents.ItemsSource = animation.Events.Select(x => new CutsceneEventItem(x)).ToList();
    }

    private void AnimationEvents_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnimationEvents.SelectedItem is not CutsceneEventItem item) return;
        ShowEvent(item.Event);
        Timeline.Selected = item.Event;
        Timeline.Reveal(item.Event);
    }

    private void DialogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DialogGrid.SelectedItem is not CutsceneDialogRow row) return;
        ShowEvent(row.Event);
        Timeline.Selected = row.Event;
        Timeline.Reveal(row.Event);
        Pause();
        SetTime(row.Event.Start);
    }

    private void SceneGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SceneGrid.SelectedItem is CutsceneLinkItem item) _ = ViewerLinks.OpenAsync(this, Message, _locator, item.GamePath);
    }

    // ---------------------------------------------------------------- files

    private void ActorTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = ViewerLinks.OpenAsync(this, Message, _locator, path);
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = ViewerLinks.OpenAsync(this, Message, _locator, path);
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = ViewerLinks.OpenAsync(this, Message, _locator, path);

    private void CopyLink_Click(object sender, RoutedEventArgs e) => Copy((sender as FrameworkElement)?.Tag as string);

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

    /// <summary>A list inside a scrolling tab passes the wheel on to the tab, as it never scrolls itself.</summary>
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
        bool typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Tabs.SelectedIndex = Tabs.Items.Count - 2; // Events
            SearchBox.Focus();
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && !typing && PlayButton.IsEnabled)
        {
            if (_playing) Pause();
            else Play();
        }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.None && !typing)
            LoopToggle.IsChecked = LoopToggle.IsChecked != true;
        else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None && !typing)
        {
            // both on or both off
            bool on = TrackToggle.IsChecked != true || VoicesToggle.IsChecked != true;
            TrackToggle.IsChecked = VoicesToggle.IsChecked = on;
            TrackToggle_Click(TrackToggle, e);
            VoicesToggle_Click(VoicesToggle, e);
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.None && !typing && VoicesToggle.IsEnabled)
        {
            VoicesToggle.IsChecked = VoicesToggle.IsChecked != true;
            VoicesToggle_Click(VoicesToggle, e);
        }
        else if (e.Key == Key.Home && !typing && StartButton.IsEnabled)
        {
            Pause();
            SetTime(0);
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.None && !typing && CameraToggle.IsEnabled)
        {
            CameraToggle.IsChecked = CameraToggle.IsChecked != true;
            ApplyCameraMode();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None && !typing)
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
