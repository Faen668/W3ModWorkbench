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
        View = new MeshView { ShowFloor = true, ToolTip = "Drag to turn around, right-drag to move, wheel to come closer, double-click to see everyone" };
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

    /// <summary>Reads each actor's entity and meshes, shows them all in their pose at the time shown, then puts their textures on.</summary>
    public async Task LoadCastAsync(CancellationToken token)
    {
        if (_cutscene is not { } c || Playback is not { } p || _locator is not { } locator)
        {
            _ready.TrySetResult();
            return;
        }
        var notes = new List<string>(p.Problems);
        List<CastMember> cast;
        try
        {
            cast = await Task.Run(() => CutsceneCast.Load(c, p, locator, notes, token), token);
        }
        catch (OperationCanceledException)
        {
            _ready.TrySetResult();
            return;
        }
        if (!ReferenceEquals(c, _cutscene)) return; // another was loaded meanwhile
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
        SetFloor();
        FitActors();
        // again once the view has its size, as the first fit can come before it's laid out
        _ = Dispatcher.InvokeAsync(() => { if (!View.IsLookingThrough) FitActors(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        Message = cast.Count > 0 ? "" : p.Actors.Count == 0 ? "There are no body animations to play." : "None of the actors could be put together.";
        _notes.Text = string.Join("\n", notes.Take(6)) + (notes.Count > 6 ? $"\n… and {notes.Count - 6} more" : "");
        if (ThroughCamera) SetTime(_time);
        _ready.TrySetResult();

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
                var (picture, leaveOut) = await Task.Run(() => LookTextures.ForMaterial(mesh, material, locator, Texture, coloring), token)
                    .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : ((BitmapSource?)null, false), TaskScheduler.Default);
                if (token.IsCancellationRequested || !ReferenceEquals(c, _cutscene)) return;
                if (leaveOut) View.LeaveOut(at, material);
                else if (picture is not null) View.SetTexture(at, material, picture);
            }
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
    private void PoseCast()
    {
        if (_cast.Count == 0) return;
        var posers = _cast.ToDictionary(m => m, m => CutsceneCast.Pose(m, _time));
        View.Pose((part, chunk) => part < _parts.Count && posers[_parts[part].Member] is { } pose ? pose(_parts[part].Part, chunk) : null);
    }

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
