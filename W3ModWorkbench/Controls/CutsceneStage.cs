using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Cutscenes;

namespace W3ModWorkbench;

/// <summary>
/// A cutscene's actors as stick figures, a line from each bone to its parent, at the time <see cref="Time"/> says, on a
/// grid of metres. Seen from a camera you can move (drag to turn around, right-drag or Shift-drag to move, wheel to come
/// closer, double-click to see everyone again) or through the cutscene's own camera. The game is Z-up.
/// </summary>
public sealed class CutsceneStage : FrameworkElement
{
    private CutscenePlayback? _playback;
    private readonly List<(PlayedActor Actor, int[] Lines, Color Color)> _figures = [];
    private float _ground;

    // the free camera turns around this point, at this distance, at these angles (degrees)
    private Vector3 _target;
    private double _distance = 8, _yaw = 30, _pitch = 20;
    private Point _lastMouse;
    private bool _orbiting, _panning;

    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty GridBrushProperty = Brush(nameof(GridBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(CutsceneStage), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public CutsceneStage()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(GridBrushProperty, "CardStrokeColorDefaultBrush");
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>The time shown, in seconds.</summary>
    public float Time
    {
        get => _time;
        set { _time = value; InvalidateVisual(); }
    }
    private float _time;

    /// <summary>Whether it's seen through the cutscene's camera instead of the free one.</summary>
    public bool ThroughCamera
    {
        get => _throughCamera;
        set { _throughCamera = value; InvalidateVisual(); }
    }
    private bool _throughCamera;

    public bool HasCamera => _playback?.Camera?.Rig is not null;

    /// <summary>The colour an actor's figure is drawn in, for its name in a list.</summary>
    public Color ColorOf(string actor) => _figures.FirstOrDefault(f => f.Actor.Name == actor).Color;

    public void Show(CutscenePlayback? playback)
    {
        _playback = playback;
        _figures.Clear();
        if (playback is not null)
        {
            int n = 0;
            foreach (var actor in playback.Actors.Where(a => a.Rig is not null))
                _figures.Add((actor, Lines(actor.Rig!), Palette[n++ % Palette.Length]));
        }
        FitToView();
    }

    private static readonly Brush Bars = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static readonly Color[] Palette =
        new[] { "#4C9AFF", "#F2994A", "#2EB67D", "#E06FAE", "#B57BE0", "#E6BE3A", "#3DB8D8", "#E5675F" }
            .Select(c => (Color)ColorConverter.ConvertFromString(c)).ToArray();

    /// <summary>
    /// The bones drawn as lines to their parents. The roots and the bones the engine uses as handles (Trajectory,
    /// Reference, IK_…) sit away from the body, so they and the lines to them are left out.
    /// </summary>
    private static int[] Lines(Core.Animation.Skeleton rig)
    {
        bool Handle(int i) => i < 0 || rig.Parents[i] < 0 || rig.Names[i] is "Trajectory" or "Reference" || rig.Names[i].StartsWith("IK_", StringComparison.OrdinalIgnoreCase);
        return Enumerable.Range(0, rig.Count).Where(i => !Handle(i) && !Handle(rig.Parents[i])).ToArray();
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>A way of putting points of the world on the screen.</summary>
    private readonly record struct View(Vector3 Eye, Vector3 Forward, Vector3 Right, Vector3 Up, double Focal, Point Centre)
    {
        public Point? Project(Vector3 p)
        {
            var d = p - Eye;
            float z = Vector3.Dot(d, Forward);
            if (z < 0.05f) return null;
            return new Point(Centre.X + Vector3.Dot(d, Right) / z * Focal, Centre.Y - Vector3.Dot(d, Up) / z * Focal);
        }

        public static View Looking(Vector3 eye, Vector3 forward, Vector3 up, double fieldOfView, Rect frame)
        {
            forward = Vector3.Normalize(forward);
            var right = Vector3.Cross(forward, up);
            right = right.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(right);
            up = Vector3.Cross(right, forward);
            double focal = frame.Width / 2 / Math.Tan(Math.Clamp(fieldOfView, 5, 150) * Math.PI / 360);
            return new View(eye, forward, right, up, focal, new Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var all = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, all); // so the whole of it takes the mouse
        if (_playback is null || all.Width < 4 || all.Height < 4) return;

        View view;
        var frame = all;
        if (_throughCamera && _playback.CameraAt(_time) is { } camera)
        {
            // the picture is 16:9 inside the view, with bars around it
            double width = Math.Min(all.Width, all.Height * 16 / 9);
            frame = new Rect((all.Width - width) / 2, (all.Height - width * 9 / 16) / 2, width, width * 9 / 16);
            view = View.Looking(camera.Position, camera.Forward, camera.Up, camera.FieldOfView, frame);
            var bars = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), new RectangleGeometry(frame));
            dc.DrawGeometry(Bars, null, bars);
        }
        else view = FreeView(all);

        dc.PushClip(new RectangleGeometry(frame));
        DrawGrid(dc, view);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (var (actor, lines, color) in _figures)
        {
            var bones = actor.Bones(_time)!;
            var pen = new Pen(new SolidColorBrush(color), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            Vector3? top = null;
            foreach (int i in lines)
            {
                if (view.Project(bones[i]) is not { } a || view.Project(bones[actor.Rig!.Parents[i]]) is not { } b) continue;
                dc.DrawLine(pen, a, b);
                if (top is null || bones[i].Z > top.Value.Z) top = bones[i];
            }
            if (top is { } head && view.Project(head + new Vector3(0, 0, 0.15f)) is { } at)
            {
                var label = new FormattedText(actor.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, pen.Brush, dip);
                dc.DrawText(label, new Point(at.X - label.Width / 2, at.Y - label.Height));
            }
        }
        if (!_throughCamera) DrawCamera(dc, view);
        dc.Pop();
    }

    /// <summary>Lines a metre apart on the ground, out to twenty metres around what's shown.</summary>
    private void DrawGrid(DrawingContext dc, View view)
    {
        var pen = new Pen(GridBrush, 1);
        if (pen.CanFreeze) pen.Freeze(); // a theme brush that follows the system colours can't be frozen
        float cx = MathF.Round(_target.X), cy = MathF.Round(_target.Y);
        const int reach = 20;
        for (int i = -reach; i <= reach; i++)
        {
            Line(new Vector3(cx + i, cy - reach, _ground), new Vector3(cx + i, cy + reach, _ground));
            Line(new Vector3(cx - reach, cy + i, _ground), new Vector3(cx + reach, cy + i, _ground));
        }

        // a line behind the eye is cut where it passes it, so it doesn't flip across the screen
        void Line(Vector3 a, Vector3 b)
        {
            float da = Vector3.Dot(a - view.Eye, view.Forward), db = Vector3.Dot(b - view.Eye, view.Forward);
            const float near = 0.1f;
            if (da < near && db < near) return;
            if (da < near) a = Vector3.Lerp(a, b, (near - da) / (db - da));
            else if (db < near) b = Vector3.Lerp(b, a, (near - db) / (da - db));
            if (view.Project(a) is { } pa && view.Project(b) is { } pb) dc.DrawLine(pen, pa, pb);
        }
    }

    /// <summary>The cutscene's camera, as a small pyramid pointing where it looks.</summary>
    private void DrawCamera(DrawingContext dc, View view)
    {
        if (_playback?.CameraAt(_time) is not { } camera) return;
        var right = Vector3.Normalize(Vector3.Cross(camera.Forward, camera.Up));
        float half = 0.25f * MathF.Tan(camera.FieldOfView * MathF.PI / 360);
        var tip = camera.Position;
        var ahead = tip + camera.Forward * 0.25f;
        var corners = new[] { ahead + right * half + camera.Up * half * 9 / 16, ahead - right * half + camera.Up * half * 9 / 16,
                              ahead - right * half - camera.Up * half * 9 / 16, ahead + right * half - camera.Up * half * 9 / 16 };
        var pen = new Pen(TextBrush, 1.2);
        if (pen.CanFreeze) pen.Freeze();
        for (int i = 0; i < 4; i++)
        {
            if (view.Project(tip) is { } a && view.Project(corners[i]) is { } b) dc.DrawLine(pen, a, b);
            if (view.Project(corners[i]) is { } c && view.Project(corners[(i + 1) % 4]) is { } d) dc.DrawLine(pen, c, d);
        }
    }

    // ---------------------------------------------------------------- the free camera

    private View FreeView(Rect frame)
    {
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        var forward = new Vector3((float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)(Math.Cos(yaw) * Math.Cos(pitch)), (float)-Math.Sin(pitch));
        double fov = 2 * Math.Atan(Math.Tan(45 * Math.PI / 360) * frame.Width / Math.Max(1, frame.Height)) * 180 / Math.PI; // 45° high
        return View.Looking(_target - forward * (float)_distance, forward, Vector3.UnitZ, fov, frame);
    }

    /// <summary>
    /// Turns the free camera to see the actors where they are at the time shown, leaving out any far from the rest (a
    /// prop parked out of sight). The grid goes under the lowest anyone gets in the whole cutscene, so it stays put.
    /// </summary>
    public void FitToView()
    {
        var figures = new List<Vector3[]>();
        float ground = float.MaxValue;
        foreach (var (actor, lines, _) in _figures.Where(f => f.Lines.Length > 0))
        {
            var now = actor.Bones(_time)!;
            figures.Add(lines.Select(i => now[i]).ToArray());
            for (float t = 0; t <= actor.Clip.Duration; t += Math.Max(0.5f, actor.Clip.Duration / 16))
            {
                var bones = actor.Bones(t)!;
                ground = Math.Min(ground, lines.Min(i => bones[i].Z));
            }
        }
        var centres = figures.Select(f => f.Aggregate(Vector3.Add) / f.Length).ToList();
        var middle = new Vector3(Median(centres.Select(c => c.X)), Median(centres.Select(c => c.Y)), Median(centres.Select(c => c.Z)));
        float spread = Math.Max(5, 3 * Median(centres.Select(c => Vector3.Distance(c, middle))));
        var points = figures.Where((_, i) => Vector3.Distance(centres[i], middle) <= spread).SelectMany(f => f).ToList();
        if (points.Count == 0)
        {
            _target = Vector3.Zero;
            _ground = 0;
            InvalidateVisual();
            return;
        }
        var min = points.Aggregate(Vector3.Min);
        var max = points.Aggregate(Vector3.Max);
        _ground = ground;
        _target = (min + max) / 2;
        float radius = Math.Max(1.5f, (max - min).Length() / 2);
        _distance = Math.Min(radius / Math.Sin(22.5 * Math.PI / 180) * 1.1, 200);
        InvalidateVisual();
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ThroughCamera = false;
            FitToView();
            return;
        }
        _lastMouse = e.GetPosition(this);
        _panning = e.ChangedButton is MouseButton.Right or MouseButton.Middle || e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _orbiting = !_panning && e.ChangedButton == MouseButton.Left;
        if (_orbiting || _panning)
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_orbiting && !_panning) return;
        var now = e.GetPosition(this);
        var delta = now - _lastMouse;
        _lastMouse = now;
        if (_throughCamera) return; // the cutscene's camera isn't moved by hand
        if (_orbiting)
        {
            _yaw = (_yaw + delta.X * 0.4) % 360;
            _pitch = Math.Clamp(_pitch + delta.Y * 0.4, -89, 89);
        }
        else
        {
            // moves the point turned around, by as much as the ground moves under the mouse
            var view = FreeView(new Rect(RenderSize));
            float perPixel = (float)(_distance / view.Focal);
            _target += (-view.Right * (float)delta.X + view.Up * (float)delta.Y) * perPixel;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_orbiting && !_panning) return;
        _orbiting = _panning = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_throughCamera) return;
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1 / 0.85), 0.5, 500);
        InvalidateVisual();
        e.Handled = true;
    }
}
