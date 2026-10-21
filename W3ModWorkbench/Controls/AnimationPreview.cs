using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench;

/// <summary>
/// A 3D view of an entity playing one animation, for viewers that preview animations (a job tree's actions, an
/// animation events file's animations): the entity drawn in its default appearance, its textures read several at once,
/// on the rig its skinned meshes are bound to, and any animation of any set played on it by name, looping, with a bar
/// to play, pause, scrub and change speed. <see cref="TimeChanged"/> follows the time, for a timeline to follow.
/// </summary>
public sealed class AnimationPreview : Grid
{
    private const int TextureSize = 1024;
    private readonly MeshView _view = new();
    private readonly TextBlock _message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(20), Opacity = 0.8 };
    private readonly Button _play = new() { Padding = new Thickness(10, 4, 10, 4), IsEnabled = false, ToolTip = "Play or pause" };
    private readonly TextBlock _playIcon = new() { Text = "", VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _scrub = new() { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), IsEnabled = false };
    private readonly TextBlock _timeText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 104, TextAlignment = TextAlignment.Right, Margin = new Thickness(4, 0, 0, 0), FontFamily = new FontFamily("Consolas") };
    private readonly ComboBox _speedBox = new() { MinWidth = 70, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), ToolTip = "How fast it plays" };
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Cr2wFile?> _sets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<int, byte[]?>> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private static readonly float[] Speeds = [0.1f, 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f];

    private ResourceLocator? _locator;
    private Posing? _posing;
    private AnimationClip? _clip;
    private Matrix4x4 _placing = Matrix4x4.Identity;
    private BoneTransform[] _pose = [];
    private float _time, _playedFrom, _speed = 1;
    private bool _playing, _scrubbing;
    private (Vector3 Min, Vector3 Max)? _travel;
    private float _fit;
    private double _nextDraw;
    private CancellationTokenSource? _load;
    private int _animationVersion;

    private sealed record Posing(Skeleton Rig, Matrix4x4[] Rest, List<MeshFile> Meshes, List<(int Bone, bool ByName)[]> Maps);

    private bool _showBones, _showNames;
    private string? _traced;
    private List<Vector3>? _trail;
    private Matrix4x4[]? _posed;

    /// <summary>The rig the entity is posed with, or null.</summary>
    public Skeleton? Rig => _posing?.Rig;

    /// <summary>Whether the skeleton is drawn over the character, as it's posed.</summary>
    public bool ShowBones
    {
        get => _showBones;
        set { _showBones = value; DrawSkeleton(); }
    }

    /// <summary>Whether the bones' names are drawn beside them, with the skeleton.</summary>
    public bool ShowBoneNames
    {
        get => _showNames;
        set { _showNames = value; DrawSkeleton(); }
    }

    /// <summary>A bone whose path over the animation is drawn, as REDkit's trajectory tool draws it, or null.</summary>
    public string? TracedBone
    {
        get => _traced;
        set { _traced = value; _trail = Trail(); DrawSkeleton(); }
    }

    /// <summary>The path the traced bone takes over the whole of the animation playing, placed as the actor is.</summary>
    private List<Vector3>? Trail()
    {
        if (_traced is null || _clip is not { } clip || _posing is not { } posing) return null;
        int bone = Array.FindIndex(posing.Rig.Names, n => n.Equals(_traced, StringComparison.OrdinalIgnoreCase));
        if (bone < 0) return null;
        var pose = new BoneTransform[posing.Rig.Count];
        var model = new Matrix4x4[posing.Rig.Count];
        int samples = Math.Clamp((int)(clip.Duration * 30), 2, 600);
        var trail = new List<Vector3>(samples + 1);
        for (int s = 0; s <= samples; s++)
        {
            Sample(clip, posing.Rig, clip.Duration * s / samples, pose, _fit);
            posing.Rig.ToModel(pose, model);
            trail.Add(Vector3.Transform(model[bone].Translation, _placing));
        }
        return trail;
    }

    /// <summary>The skeleton and the traced path over the character, as asked for, or nothing.</summary>
    private void DrawSkeleton()
    {
        var rig = _posing?.Rig;
        bool bones = (_showBones || _showNames) && rig is not null && _posed is not null;
        if (!bones && _trail is null)
        {
            _view.ShowSkeleton(null, null);
            return;
        }
        _view.ShowSkeleton(bones ? _posed : null, bones ? rig!.Parents : null, rig is null || _traced is null ? -1 : Array.FindIndex(rig.Names, n => n.Equals(_traced, StringComparison.OrdinalIgnoreCase)),
            0, bones && _showNames ? rig!.Names : null, _trail);
    }

    /// <summary>Raised as the time shown changes, while playing or scrubbing.</summary>
    public event EventHandler<float>? TimeChanged;

    public AnimationPreview()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _view.StartFrom(180, 10);
        Children.Add(_view);
        Children.Add(_message);
        _playIcon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _play.Content = _playIcon;
        _play.Click += (_, _) => { if (_playing) Pause(); else Play(); };
        _scrub.ValueChanged += (_, e) =>
        {
            if (_scrubbing) return;
            Pause();
            SetTime((float)e.NewValue);
        };
        _speedBox.ItemsSource = Speeds.Select(s => $"{s * 100:0}%").ToList();
        _speedBox.SelectedIndex = Array.IndexOf(Speeds, 1f);
        _speedBox.SelectionChanged += (_, _) =>
        {
            if (_speedBox.SelectedIndex < 0) return;
            if (_playing) { _playedFrom = _time; _clock.Restart(); _nextDraw = 0; }
            _speed = Speeds[_speedBox.SelectedIndex];
        };
        var bar = new DockPanel { Margin = new Thickness(6, 6, 6, 4) };
        DockPanel.SetDock(_play, Dock.Left);
        DockPanel.SetDock(_speedBox, Dock.Right);
        DockPanel.SetDock(_timeText, Dock.Right);
        bar.Children.Add(_play);
        bar.Children.Add(_speedBox);
        bar.Children.Add(_timeText);
        bar.Children.Add(_scrub);
        SetRow(bar, 1);
        Children.Add(bar);
        Unloaded += (_, _) => Pause();
        // framed again when the view changes size, as the window is laid out or resized
        _view.SizeChanged += (_, e) => { if (e.PreviousSize.Width > 0 && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 40) Frame(); };
        Message = "Pick something to preview";
    }

    /// <summary>A note over the view (what's loading, why nothing shows), or "" for none.</summary>
    public string Message
    {
        get => _message.Text;
        set { _message.Text = value; _message.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible; }
    }

    /// <summary>The entity shown, or null.</summary>
    public string? EntityPath { get; private set; }

    /// <summary>The length of the animation playing, or 0.</summary>
    public float Duration => _clip?.Duration ?? 0;

    public float Time => _time;

    public bool IsPlaying => _playing;

    /// <summary>
    /// Shows an entity in its default appearance, its textures read several at once, ready to play animations on its
    /// rig. Says why in the view when it can't be shown or has nothing to animate.
    /// </summary>
    public async Task LoadEntityAsync(string path, ResourceLocator locator)
    {
        _load?.Cancel();
        var token = (_load = new CancellationTokenSource()).Token;
        _locator = locator;
        EntityPath = path;
        StopAnimation();
        _posing = null;
        Message = $"Loading {Path.GetFileName(path)}…";
        try
        {
            var entity = await Task.Run(() =>
            {
                var data = locator.ReadFile(path) ?? throw new FileNotFoundException($"{path} isn't in the game's files or the archives open.");
                var e = EntityFile.Read(data);
                e.FindOrigins(locator.ReadFile);
                return e;
            }, token);
            var appearance = entity.DefaultAppearance;
            var look = await Task.Run(() => entity.Look(appearance, locator.ReadFile), token);
            var (parts, drawnBy, _, _) = await Task.Run(() => EntityWindow.ReadMeshes(look, locator, token), token);
            if (token.IsCancellationRequested) return;
            var coloured = parts.Select((part, p) => (part.Mesh, LookTextures.Coloring(entity, appearance?.Name, drawnBy[p].Name))).ToList();
            var progress = new Progress<(int Done, int Total)>(n => { if (!token.IsCancellationRequested && n.Total > 0) Message = $"Loading textures, {n.Done} of {n.Total}…"; });
            var textures = await StageTextures.LoadAsync(coloured, locator, Texture, progress, token);
            if (token.IsCancellationRequested) return;
            _view.ShowParts(parts, textures);
            _view.FitToView();
            _travel = null;
            _posing = await Task.Run(() => FindRig(look, parts.Select(p => p.Mesh).ToList(), locator), token);
            _posed = _posing?.Rest;
            _trail = null;
            DrawSkeleton();
            Message = parts.Count == 0 ? "The entity has no meshes to show." : _posing is null ? "The entity has no skeleton to play animations on." : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!token.IsCancellationRequested) Message = $"{Path.GetFileName(path)} could not be shown.\n{ex.Message}";
        }
    }

    /// <summary>The rig most of the meshes' bones are named in, among those the look's components use, ready to pose them.</summary>
    private static Posing? FindRig(EntityLook look, List<MeshFile> meshes, ResourceLocator locator)
    {
        if (!meshes.Any(m => m.BoneNames.Length > 0)) return null;
        static bool IsRig(string r) => r.EndsWith(".w2rig", StringComparison.OrdinalIgnoreCase);
        var rigs = look.Components.Where(c => !c.ClassName.Contains("Dangle", StringComparison.Ordinal)).SelectMany(c => c.Resources).Where(IsRig)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rigs.Count == 0) rigs = look.Components.SelectMany(c => c.Resources).Where(IsRig).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // a template of meshes alone (the background crowd's) names no rig: the base characters' are tried
        if (rigs.Count == 0) rigs = [.. BaseRigs];
        Posing? best = null;
        int bestNamed = 0;
        foreach (var path in rigs)
        {
            try
            {
                if (locator.ReadFile(path) is not { } data) continue;
                var rig = Skeleton.Read(data);
                var names = new HashSet<string>(rig.Names, StringComparer.OrdinalIgnoreCase);
                int named = meshes.Sum(m => m.BoneNames.Count(names.Contains));
                if (named <= bestNamed) continue;
                var rest = new Matrix4x4[rig.Count];
                rig.ToModel(rig.Pose, rest);
                (best, bestNamed) = (new Posing(rig, rest, meshes, meshes.Select(m => Skinning.MapBones(m, rig, rest)).ToList()), named);
            }
            catch (InvalidDataException)
            {
            }
        }
        return best;
    }

    /// <summary>The base characters' rigs, for meshes whose entity names none: the one most of their bones are in is used.</summary>
    private static readonly string[] BaseRigs =
    [
        @"characters\base_entities\man_base\man_base.w2rig", @"characters\base_entities\woman_base\woman_base.w2rig",
        @"characters\base_entities\child_base\child_base.w2rig", @"characters\base_entities\dwarf_base\dwarf_base.w2rig",
    ];

    private Task<BitmapSource?> Texture(string path)
    {
        var locator = _locator!;
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task)) _textures[path] = task = Task.Run(() => MeshTextures.Load(locator, path, TextureSize));
            return task;
        }
    }

    /// <summary>
    /// A set already read, to play from by <paramref name="key"/>, its buffers read by <paramref name="buffers"/>: a set
    /// opened from a folder, which the game's files don't have.
    /// </summary>
    public void UseSet(string key, Cr2wFile set, Func<int, byte[]?> buffers)
    {
        lock (_sets)
        {
            _sets[key] = set;
            _buffers[key] = buffers;
        }
    }

    /// <summary>
    /// Plays an animation of a set by its name, from the start, looping. False, with why in the view, when it isn't in
    /// the set, can't be read, or is made for another skeleton.
    /// </summary>
    public async Task<bool> PlayAsync(string animset, string animation, bool play = true)
    {
        int version = ++_animationVersion;
        Pause();
        if (_locator is not { } locator) return false;
        if (_posing is not { } posing)
        {
            if (Message.Length == 0) Message = "The entity has no skeleton to play animations on.";
            return false;
        }
        var clip = await Task.Run<(AnimationClip? Clip, string Why)>(() =>
        {
            Cr2wFile? set;
            lock (_sets)
                if (!_sets.TryGetValue(animset, out set))
                {
                    try { set = locator.ReadFile(animset) is { } data ? Cr2wFile.Read(data) : null; }
                    catch (InvalidDataException) { set = null; }
                    _sets[animset] = set;
                }
            if (set is null) return (null, $"{Path.GetFileName(animset)} isn't in the game's files or couldn't be read.");
            var export = set.Exports.FirstOrDefault(x => x.ClassName == "CSkeletalAnimation" && set.Object(x.Index)["name"] as string == animation);
            if (export is null) return (null, $"{animation} isn't in {Path.GetFileName(animset)}.");
            try
            {
                Func<int, byte[]?>? buffers;
                lock (_sets) _buffers.TryGetValue(animset, out buffers);
                var archive = buffers is null ? locator.Find(animset)?.Archive : null;
                return (AnimationClip.Read(set, set.Object(export.Index), buffers ?? (n => locator.ReadBuffer(animset, n, archive))), "");
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                return (null, $"{animation} could not be read.");
            }
        });
        if (version != _animationVersion) return false;
        if (clip.Clip is null || clip.Clip.BoneCount > posing.Rig.Count)
        {
            StopAnimation();
            Message = clip.Clip is null ? clip.Why : $"{animation} is made for another skeleton than this entity's.";
            return false;
        }
        Message = "";
        _clip = clip.Clip;
        _fit = Fit(_clip, posing.Rig);
        _placing = Placing(_clip, posing, _fit);
        _scrub.Maximum = Math.Max(0.01, _clip.Duration);
        _scrub.IsEnabled = _play.IsEnabled = true;
        _travel = Travel(_clip, posing, _placing, _fit);
        _trail = Trail();
        SetTime(0);
        // each animation frames the actor over all of its moves, from the angle the view is at
        Frame();
        if (play) Play();
        return true;
    }

    /// <summary>
    /// Points the camera at the actor over the animation playing: a box round its bones, from the ground up and padded for
    /// the body round them, so the whole of it is in the picture whatever the view's shape. The meshes' own bounds are
    /// used before an animation plays.
    /// </summary>
    private void Frame()
    {
        if (_travel is not var (min, max))
        {
            _view.FitToView();
            return;
        }
        var pad = new Vector3(0.2f, 0.2f, 0.15f);
        min -= pad;
        max += pad;
        min.Z = Math.Min(min.Z, 0);
        _view.FitTo(min, max);
    }

    /// <summary>
    /// A box round the actor's bones over the whole of an animation, sampled through it, as an attack lunges and a walk
    /// travels; null when the rig has too few bones to tell.
    /// </summary>
    private static (Vector3 Min, Vector3 Max)? Travel(AnimationClip clip, Posing posing, Matrix4x4 placing, float fit)
    {
        var rig = posing.Rig;
        if (rig.Count < 2) return null;
        int pelvis = Array.FindIndex(rig.Names, n => n.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
        if (pelvis < 0) pelvis = 1;
        var pose = new BoneTransform[rig.Count];
        var model = new Matrix4x4[rig.Count];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        const int samples = 12;
        for (int s = 0; s <= samples; s++)
        {
            Sample(clip, rig, clip.Duration * s / samples, pose, fit);
            rig.ToModel(pose, model);
            var middle = Vector3.Transform(model[pelvis].Translation, placing);
            foreach (var bone in model)
            {
                var at = Vector3.Transform(bone.Translation, placing);
                // helper bones a rig keeps far off (cameras, a trajectory) aren't the body
                if (Vector3.Distance(at, middle) > 3) continue;
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
        }
        return min.X <= max.X ? (min, max) : null;
    }

    /// <summary>
    /// How much bigger the skeleton an animation was made for is than the rig it plays on, as a man's animation is than a
    /// child: the bones' lengths in its first frame over the rig's. 0 when they're the same build, to play it as it is.
    /// </summary>
    private static float Fit(AnimationClip clip, Skeleton rig)
    {
        var pose = rig.Pose.ToArray();
        clip.Sample(0, pose);
        var ratios = new List<float>();
        for (int i = 1; i < rig.Count; i++)
        {
            float rest = rig.Pose[i].Position.Length();
            if (rest > 0.05f) ratios.Add(pose[i].Position.Length() / rest);
        }
        if (ratios.Count < 5) return 0;
        ratios.Sort();
        float ratio = ratios[ratios.Count / 2];
        return Math.Abs(ratio - 1) > 0.06f ? ratio : 0;
    }

    /// <summary>
    /// The pose at a time of an animation. Played on a rig of another build (<paramref name="fit"/> not 0), the bones keep
    /// the rig's own lengths and take only the animation's turns, the hips' moves scaled to the rig's size, so a man's
    /// animation doesn't stretch a child to a man's bones.
    /// </summary>
    private static void Sample(AnimationClip clip, Skeleton rig, float time, BoneTransform[] pose, float fit)
    {
        rig.Pose.CopyTo(pose, 0);
        clip.Sample(time, pose);
        KeepRoot(rig, pose);
        if (fit == 0) return;
        int pelvis = Array.FindIndex(rig.Names, n => n.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
        for (int i = 1; i < rig.Count; i++)
            pose[i] = pose[i] with { Position = i == pelvis ? pose[i].Position / fit : rig.Pose[i].Position, Scale = rig.Pose[i].Scale };
    }

    /// <summary>Leaves the rig's root where it stands, as the game places a character's root itself.</summary>
    private static void KeepRoot(Skeleton rig, BoneTransform[] pose)
    {
        if (rig.Count > 0 && rig.Parents[0] < 0) pose[0] = rig.Pose[0];
    }

    /// <summary>Brings the animation's actor back over where the entity stands, facing its way, as the entity viewer does.</summary>
    private static Matrix4x4 Placing(AnimationClip clip, Posing posing, float fit)
    {
        var rig = posing.Rig;
        int pelvis = Array.FindIndex(rig.Names, n => n.Equals("pelvis", StringComparison.OrdinalIgnoreCase));
        if (pelvis < 0) pelvis = rig.Count > 1 ? 1 : 0;
        var pose = new BoneTransform[rig.Count];
        Sample(clip, rig, 0, pose, fit);
        var model = new Matrix4x4[rig.Count];
        rig.ToModel(pose, model);
        var rest = posing.Rest[pelvis];
        var start = model[pelvis];
        if (!Matrix4x4.Invert(rest, out var back)) return Matrix4x4.Identity;
        var turned = Vector3.TransformNormal(Vector3.UnitX, back * start);
        float yaw = turned.X * turned.X + turned.Y * turned.Y > 1e-6f ? MathF.Atan2(turned.Y, turned.X) : 0;
        return Matrix4x4.CreateTranslation(-start.M41, -start.M42, 0) * Matrix4x4.CreateRotationZ(-yaw) * Matrix4x4.CreateTranslation(rest.M41, rest.M42, 0);
    }

    /// <summary>Poses the entity at a time of the animation.</summary>
    public void SetTime(float time)
    {
        if (_clip is not { } clip || _posing is not { } posing) return;
        _time = time = Math.Clamp(time, 0, clip.Duration);
        if (_pose.Length != posing.Rig.Count) _pose = new BoneTransform[posing.Rig.Count];
        Sample(clip, posing.Rig, time, _pose, _fit);
        var model = new Matrix4x4[posing.Rig.Count];
        posing.Rig.ToModel(_pose, model);
        for (int i = 0; i < model.Length; i++) model[i] *= _placing;
        _posed = model;
        if (_showBones || _showNames) DrawSkeleton();
        var bones = posing.Meshes.Select((m, i) => Skinning.BoneMatrices(m, posing.Maps[i], posing.Rest, model)).ToList();
        _view.Pose((part, chunk) => part < bones.Count ? Skinning.Pose(chunk, bones[part]) : null);
        _timeText.Text = $"{time,5:0.00} / {clip.Duration:0.00} s";
        _scrubbing = true;
        _scrub.Value = time;
        _scrubbing = false;
        TimeChanged?.Invoke(this, time);
    }

    public void Play()
    {
        if (_clip is null || _playing) return;
        _playedFrom = _time >= _clip.Duration - 0.01f ? 0 : _time;
        _clock.Restart();
        _nextDraw = 0;
        _playing = true;
        _playIcon.Text = "";
        CompositionTarget.Rendering += Tick;
    }

    public void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _clock.Stop();
        _playIcon.Text = "";
        CompositionTarget.Rendering -= Tick;
    }

    /// <summary>Stops and stands the entity as it was bound.</summary>
    public void StopAnimation()
    {
        Pause();
        _clip = null;
        _time = 0;
        _view.Pose(null);
        _posed = _posing?.Rest;
        _trail = null;
        DrawSkeleton();
        _scrub.IsEnabled = _play.IsEnabled = false;
        _scrubbing = true;
        _scrub.Value = 0;
        _scrubbing = false;
        _timeText.Text = "";
    }

    // posed thirty times a second at most, looping, as the entity viewer plays them
    private void Tick(object? sender, EventArgs e)
    {
        if (_clip is not { } clip) return;
        double now = _clock.Elapsed.TotalSeconds;
        if (now < _nextDraw - 0.004) return;
        _nextDraw += 1.0 / 30;
        if (_nextDraw < now) _nextDraw = now + 1.0 / 30;
        float time = _playedFrom + (float)now * _speed;
        SetTime(clip.Duration > 0 ? time % clip.Duration : 0);
    }
}
