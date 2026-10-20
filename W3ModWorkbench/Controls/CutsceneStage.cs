using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench;

/// <summary>
/// A cutscene played back in 3D, as REDkit's cutscene editor shows it: its actors put together from their entities in
/// the appearances it gives them, textured and coloured, and played on their rigs where it puts them, seen from anywhere
/// or through its camera in a picture of the aspect ratio set, with bars around it. What time it shows is set from
/// outside, so the cutscene viewer and the scene viewer's preview both play it.
/// </summary>
public sealed class CutsceneStage : Grid
{
    private const int TextureSize = 1024;

    private readonly System.Windows.Controls.Border _bars = new() { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE0, 0, 0, 0)), Visibility = Visibility.Collapsed };
    private readonly TextBlock _text = new()
    {
        Text = "Reading the actors…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560, Margin = new Thickness(16), Opacity = 0.8, IsHitTestVisible = false,
    };
    private readonly TextBlock _notes = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 10), MaxWidth = 620,
        TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Opacity = 0.7, IsHitTestVisible = false,
    };

    private CutsceneFile? _cutscene;
    private ResourceLocator? _locator;
    private List<CastMember> _cast = [];
    private readonly List<(CastMember Member, int Part)> _parts = [];
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private float _time, _ground;
    private bool _throughCamera;
    private double _aspect = 16.0 / 9;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CutsceneStage()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        ClipToBounds = true;
        View = new MeshView { ShowFloor = true, AutoLod = true, ToolTip = "Drag to turn around, right-drag to move, wheel to come closer, double-click to see everyone" };
        Children.Add(_bars);
        Children.Add(View);
        Children.Add(_text);
        Children.Add(_notes);
        SizeChanged += (_, _) => LayoutStage();
    }

    /// <summary>The 3D view, for its settings: field of view, light, textures, wireframe and floor.</summary>
    public MeshView View { get; }

    /// <summary>The cutscene's animations and rigs, once read.</summary>
    public CutscenePlayback? Playback { get; private set; }

    /// <summary>Done once the cutscene loaded last has its actors on the stage (or none could be put there), before their textures.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Whether any actor was put together to be drawn.</summary>
    public bool HasCast => _cast.Count > 0;

    /// <summary>The words shown in the middle of the view (empty hides them).</summary>
    public string Message
    {
        get => _text.Text;
        set
        {
            _text.Text = value;
            _text.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Whether it's seen through the cutscene's camera, in a picture of <see cref="Aspect"/> with bars around it.</summary>
    public bool ThroughCamera
    {
        get => _throughCamera;
        set
        {
            _throughCamera = value;
            bool through = value && Playback?.Camera?.Rig is not null;
            _bars.Visibility = through ? Visibility.Visible : Visibility.Collapsed;
            LayoutStage();
            if (through) SetTime(_time);
            else View.StopLookingThrough();
        }
    }

    /// <summary>The shape of the picture through the camera: width over height.</summary>
    public double Aspect
    {
        get => _aspect;
        set
        {
            _aspect = value;
            LayoutStage();
        }
    }

    /// <summary>
    /// Reads a cutscene's animations and finds its actors' rigs. <paramref name="path"/> is its game path, for an
    /// animation that streams its data from a buffer file. Null, with why in the view, when they can't be read.
    /// </summary>
    public async Task<CutscenePlayback?> LoadPlaybackAsync(CutsceneFile cutscene, string path, ResourceLocator locator)
    {
        Clear();
        _cutscene = cutscene;
        _locator = locator;
        try
        {
            Playback = await Task.Run(() => CutscenePlayback.Load(cutscene, locator, path, locator.Find(path)?.Archive));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            Message = "The animations could not be read.\n\n" + ex.Message;
            _ready.TrySetResult();
            return null;
        }
        Message = Playback.Actors.Count == 0 ? "There are no body animations to play." : "Putting the actors together…";
        return Playback;
    }

    /// <summary>
    /// A cutscene's actors put together and every texture they're drawn with read, ready to show at once: what the
    /// stage needs to play it smoothly from the start.
    /// </summary>
    public sealed record PreparedCast(CutsceneFile Cutscene, CutscenePlayback Playback, List<CastMember> Cast,
        List<(int Part, int Material, BitmapSource? Picture, MaterialLook? Look, bool LeaveOut)> Textures, List<string> Notes);

    /// <summary>
    /// Puts a cutscene's actors together from their entities and reads every texture they're drawn with (several at
    /// once), off the UI thread. <paramref name="texture"/> reads one texture, so a window can share what it has read.
    /// </summary>
    public static async Task<PreparedCast> PrepareAsync(CutsceneFile cutscene, CutscenePlayback playback, ResourceLocator locator,
        Func<string, Task<BitmapSource?>> texture, LoadingReporter? report, CancellationToken token, string label = "")
    {
        report?.Step($"{label}Putting the actors together", $"{playback.Actors.Count} animated");
        var notes = new List<string>(playback.Problems);
        var cast = await Task.Run(() => CutsceneCast.Load(cutscene, playback, locator, notes, token), token);
        var parts = cast.SelectMany(m => m.Parts.Select((part, i) =>
            (part.Mesh, StageTextures.Coloring(m.Entity, m.ColoredBy[i].Entity, m.ColoredBy[i].Appearance, m.DrawnBy[i].Name)))).ToList();
        report?.Step($"{label}Loading textures", string.Join(", ", cast.Select(m => m.Name)));
        var textures = await StageTextures.LoadAsync(parts, locator, texture, report?.Counter, token);
        return new PreparedCast(cutscene, playback, cast, textures, notes);
    }

    /// <summary>Reads the textures with this stage's own store of them, for a viewer of its own.</summary>
    public Func<string, Task<BitmapSource?>> TextureReader => Texture;

    /// <summary>Puts a prepared cutscene on the stage, all of it at once, in its pose at the time shown.</summary>
    public void Show(PreparedCast prepared, ResourceLocator locator)
    {
        if (!ReferenceEquals(prepared.Cutscene, _cutscene))
        {
            Clear();
            _cutscene = prepared.Cutscene;
            Playback = prepared.Playback;
        }
        _locator = locator;
        _cast = prepared.Cast;
        _parts.Clear();
        var parts = new List<(MeshFile Mesh, Matrix4x4 Place)>();
        foreach (var member in _cast)
            for (int i = 0; i < member.Parts.Count; i++)
            {
                // placed by the pose, which puts the actor where the cutscene has it
                parts.Add((member.Parts[i].Mesh, Matrix4x4.Identity));
                _parts.Add((member, i));
            }
        View.ShowParts(parts, prepared.Textures);
        PoseCast();
        SetFloor();
        FitActors();
        // again once the view has its size, as the first fit can come before it's laid out
        _ = Dispatcher.InvokeAsync(() => { if (!View.IsLookingThrough) FitActors(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        var p = prepared.Playback;
        Message = _cast.Count > 0 ? "" : p.Actors.Count == 0 ? "There are no body animations to play." : "None of the actors could be put together.";
        var notes = prepared.Notes;
        _notes.Text = string.Join("\n", notes.Take(6)) + (notes.Count > 6 ? $"\n… and {notes.Count - 6} more" : "");
        if (ThroughCamera) ThroughCamera = true; // the bars, now there's a camera to look through
        SetTime(_time);
        _ready.TrySetResult();
    }

    /// <summary>Puts the actors of the cutscene loaded with <see cref="LoadPlaybackAsync"/> together and shows them, textures and all.</summary>
    public async Task LoadCastAsync(CancellationToken token)
    {
        if (_cutscene is not { } c || Playback is not { } p || _locator is not { } locator)
        {
            _ready.TrySetResult();
            return;
        }
        try
        {
            var prepared = await PrepareAsync(c, p, locator, Texture, null, token);
            if (ReferenceEquals(c, _cutscene)) Show(prepared, locator);
        }
        catch (OperationCanceledException)
        {
            _ready.TrySetResult();
        }
    }

    /// <summary>
    /// The lip sync of the cutscene's lines, each from when it's said and by whose voice tag, for the faces to say them.
    /// A line whose speaker isn't on the stage is left out.
    /// </summary>
    public void SetLines(IEnumerable<(float Start, string Speaker, Core.Animation.LipSync Lips)> lines)
    {
        var all = lines.ToList();
        foreach (var member in _cast)
            member.Lines = all.Where(l => string.Equals(l.Speaker, member.VoiceTag, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(l.Speaker, member.Name, StringComparison.OrdinalIgnoreCase))
                .Select(l => (l.Start, l.Lips)).ToList();
        PoseCast();
    }

    /// <summary>Takes everything off the stage.</summary>
    public void Clear()
    {
        _ready.TrySetResult();
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _cutscene = null;
        Playback = null;
        _cast = [];
        _parts.Clear();
        _notes.Text = "";
        View.ShowParts([]);
    }

    /// <summary>The colour the parts of an undyed piece of gear that take a dye are shown in: no colour, a little darker.</summary>
    public static readonly ColorShift Undyed = new(0, -100, -35);

    private Task<BitmapSource?> Texture(string path)
    {
        var locator = _locator!;
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task))
                _textures[path] = task = Task.Run(() => MeshTextures.Load(locator, path, TextureSize));
            return task;
        }
    }

    /// <summary>Shows a time: the actors' pose and, through the camera, where the camera is then.</summary>
    public void SetTime(float time)
    {
        if (Playback is null) return;
        _time = time = Math.Clamp(time, 0, Playback.Duration);
        PoseCast();
        if (ThroughCamera && Playback.CameraAt(time) is { } through) View.LookThrough(through.Position, through.Forward, through.Up, through.FieldOfView, NearPlaneAt(time));
    }

    /// <summary>The near clipping plane the cutscene sets for its camera at a time: its last clipping-planes event's.</summary>
    private double? NearPlaneAt(float time) =>
        _cutscene?.SceneEvents.Where(e => e.ClassName == "CExtAnimCutsceneSetClippingPlanesEvent" && e.Start <= time + 1e-3f && e.Source is not null)
            .OrderBy(e => e.Start).LastOrDefault() is { Source: { } clipping } ? CutsceneFile.NearPlane(clipping) : null;

    /// <summary>Puts every actor's meshes in their pose at the time shown.</summary>
    private async void PoseCast()
    {
        if (_cast.Count == 0) return;
        // one pose worked out at a time, the latest time asked for next: a heavy cast shows fewer frames, still in
        // time, rather than keeping the UI thread from answering
        if (_posing)
        {
            _posePending = true;
            return;
        }
        _posing = true;
        try
        {
            do
            {
                _posePending = false;
                var (cast, parts, time) = (_cast, _parts.ToList(), _time);
                await View.PoseAsync(() =>
                {
                    var posers = cast.ToDictionary(m => m, m => CutsceneCast.Pose(m, time));
                    return (part, chunk) => part < parts.Count && posers.TryGetValue(parts[part].Member, out var pose) ? pose(parts[part].Part, chunk) : null;
                });
            }
            while (_posePending);
        }
        finally
        {
            _posing = false;
        }
    }

    private bool _posing, _posePending;

    /// <summary>The view fills the stage, or through the camera is the largest picture of the aspect ratio that fits.</summary>
    private void LayoutStage()
    {
        double width = Math.Max(1, Math.Min(ActualWidth, ActualHeight * _aspect));
        bool through = _bars.Visibility == Visibility.Visible;
        double w = through ? width : double.NaN, h = through ? Math.Max(1, width / _aspect) : double.NaN;
        // only when it changes, as a new size lays the view out again
        if (!w.Equals(View.Width)) View.Width = w;
        if (!h.Equals(View.Height)) View.Height = h;
    }

    /// <summary>
    /// The floor grid under everywhere the actors go in the whole cutscene, at the lowest any of them gets (their feet),
    /// so it stays put as they move about.
    /// </summary>
    private void SetFloor()
    {
        if (_cast.Count == 0 || Playback is null) return;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var lowest = new List<float>();
        foreach (var member in _cast)
        {
            var rig = member.Played.Rig!;
            var pose = new Core.Animation.BoneTransform[rig.Count];
            var model = new Matrix4x4[rig.Count];
            float low = float.MaxValue;
            for (int s = 0; s <= 24; s++)
            {
                rig.Pose.CopyTo(pose, 0);
                member.Played.Clip.Sample(Playback.Duration * s / 24, pose);
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
    /// parked out of sight until it comes in).
    /// </summary>
    public void FitActors()
    {
        if (_cast.Count == 0 || Playback is null)
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
}
