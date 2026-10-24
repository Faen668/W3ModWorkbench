using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.Particles;
using W3ModWorkbench.Localisation;
using Point = System.Windows.Point;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace W3ModWorkbench;

/// <summary>
/// A particle system playing: its simulation drawn a frame at a time into a picture of its own, each particle a quad
/// facing the camera with its texture's atlas frame, tinted by its colour and alpha and its material's colour, added
/// to what's behind for a shader that adds light (fire, sparks) and laid over it for the rest, back to front, over a
/// grid on the ground a metre a square. Drag to turn round it, drag with the right button to move, use the wheel to
/// come closer; double-click frames what's there.
/// </summary>
public sealed class ParticlePreview : FrameworkElement
{
    private ParticleSystemFile? _system;
    private ParticleSimulation? _simulation;
    private WriteableBitmap? _bitmap;
    private float[] _colour = [];
    private int[] _pixels = [];
    private readonly Dictionary<int, (int[] Pixels, int Width, int Height)> _textures = [];
    private double _yaw = 35, _pitch = 18, _distance = 6;
    private Vector3 _target = new(0, 0, 1);
    private Point _lastMouse;
    private bool _turning, _moving, _playing = true;
    private TimeSpan _lastFrame;
    private readonly System.Diagnostics.Stopwatch _frameClock = new();

    public static readonly DependencyProperty TextBrushProperty =
        DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(ParticlePreview), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    /// <summary>How fast the effect plays: 1 as the game does.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>Whether the grid on the ground is drawn.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>The colour behind the effect.</summary>
    public Color Background { get; set; } = Color.FromRgb(0x22, 0x24, 0x28);

    /// <summary>Raised a few times a second with how long it's played and how many particles there are.</summary>
    public event EventHandler<(float Time, int Particles)>? Ticked;

    public ParticlePreview()
    {
        ClipToBounds = true;
        Focusable = true;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        _frameClock.Start();
    }

    /// <summary>Plays an effect from its start, after its prewarming time, reading each emitter's texture with <paramref name="texture"/>.</summary>
    public async Task ShowAsync(ParticleSystemFile system, Func<string, Task<BitmapSource?>> texture)
    {
        _system = system;
        _simulation = new ParticleSimulation(system);
        _textures.Clear();
        Restart();
        for (int i = 0; i < system.Emitters.Count; i++)
            if (system.Emitters[i].Texture is { } path && await texture(path) is { } picture)
                _textures[i] = Pixels(picture);
    }

    public ParticleSimulation? Simulation => _simulation;

    public bool IsPlaying
    {
        get => _playing;
        set => _playing = value;
    }

    public int Lod
    {
        get => _simulation?.Lod ?? 0;
        set { if (_simulation is not null) _simulation.Lod = value; }
    }

    /// <summary>Starts again from the beginning, run on by the effect's prewarming time as the game does.</summary>
    public void Restart()
    {
        if (_simulation is null || _system is null) return;
        _simulation.Reset();
        if (_system.Prewarm > 0) _simulation.Advance(Math.Min(_system.Prewarm, 30));
        InvalidateVisual();
    }

    /// <summary>A picture as premultiplied BGRA, at most 512 a side.</summary>
    internal static (int[] Pixels, int Width, int Height) Pixels(BitmapSource picture)
    {
        BitmapSource source = picture;
        double scale = Math.Min(1, 512.0 / Math.Max(picture.PixelWidth, picture.PixelHeight));
        if (scale < 1) source = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        var pixels = new int[width * height];
        converted.CopyPixels(pixels, width * 4, 0);
        // a texture with no alpha (smoke's grey puffs) is covered by its brightness, as its shader reads it
        if (pixels.All(p => (uint)p >> 24 == 255))
            for (int i = 0; i < pixels.Length; i++)
            {
                int p = pixels[i], r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255, a = Math.Max(r, Math.Max(g, b));
                pixels[i] = (a << 24) | (r * a / 255 << 16) | (g * a / 255 << 8) | b * a / 255;
            }
        return (pixels, width, height);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _frameClock.Elapsed;
        double seconds = Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;
        if (_simulation is null || !IsVisible) return;
        if (_playing) _simulation.Advance((float)(seconds * Speed));
        InvalidateVisual();
        Ticked?.Invoke(this, (_simulation.Time, _simulation.Count));
    }

    /// <summary>Frames the particles there are now, or the space round the effect's middle when there are none.</summary>
    public void Fit()
    {
        if (_simulation is null || _system is null) return;
        var points = Enumerable.Range(0, _system.Emitters.Count).SelectMany(i => _simulation.Particles(i)).Select(p => p.Position).ToList();
        if (points.Count == 0)
        {
            _target = new Vector3(0, 0, 0.5f);
            _distance = 5;
        }
        else
        {
            var min = points.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = points.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            _target = (min + max) / 2;
            _distance = Math.Clamp((max - min).Length() * 1.4 + 1, 1.5, 400);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int width = Math.Max(1, (int)ActualWidth), height = Math.Max(1, (int)ActualHeight);
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            _colour = new float[width * height * 3];
            _pixels = new int[width * height];
        }
        Draw(width, height);
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixels, width * 4, 0);
        dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    // the camera, worked out for each frame
    private Vector3 _eye, _right, _up, _forward;
    private float _focal, _halfWidth, _halfHeight;

    private bool Project(Vector3 p, out float x, out float y, out float depth)
    {
        var d = p - _eye;
        depth = Vector3.Dot(d, _forward);
        x = y = 0;
        if (depth < 0.05f) return false;
        x = _halfWidth + Vector3.Dot(d, _right) * _focal / depth;
        y = _halfHeight - Vector3.Dot(d, _up) * _focal / depth;
        return true;
    }

    private void Draw(int width, int height)
    {
        float yaw = (float)(_yaw * Math.PI / 180), pitch = (float)(_pitch * Math.PI / 180);
        _eye = _target + new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch)) * (float)_distance;
        _forward = Vector3.Normalize(_target - _eye);
        _right = Vector3.Normalize(Vector3.Cross(_forward, Vector3.UnitZ));
        _up = Vector3.Cross(_right, _forward);
        _halfWidth = width / 2f;
        _halfHeight = height / 2f;
        _focal = _halfHeight / MathF.Tan(30 * MathF.PI / 180);

        var back = Background;
        float br = back.R / 255f, bg = back.G / 255f, bb = back.B / 255f;
        for (int i = 0; i < width * height; i++)
        {
            _colour[i * 3] = br;
            _colour[i * 3 + 1] = bg;
            _colour[i * 3 + 2] = bb;
        }
        if (ShowGrid) DrawGrid(width, height);

        if (_simulation is not null && _system is not null)
        {
            // every particle, furthest first, so the ones laid over the rest cover what's behind
            var all = new List<(int Emitter, Particle Particle, float Depth)>();
            for (int e = 0; e < _system.Emitters.Count; e++)
                if (_system.Emitters[e].DrawnAsSprites)
                    foreach (var p in _simulation.Particles(e))
                    all.Add((e, p, Vector3.Dot(p.Position - _eye, _forward)));
            all.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            foreach (var (e, p, _) in all) DrawParticle(_system.Emitters[e], _textures.TryGetValue(e, out var t) ? t : null, p, width, height);
        }

        for (int i = 0; i < width * height; i++)
        {
            int r = (int)(Math.Clamp(_colour[i * 3], 0, 1) * 255), g = (int)(Math.Clamp(_colour[i * 3 + 1], 0, 1) * 255), b = (int)(Math.Clamp(_colour[i * 3 + 2], 0, 1) * 255);
            _pixels[i] = (r << 16) | (g << 8) | b;
        }
    }

    private void DrawGrid(int width, int height)
    {
        int reach = (int)Math.Clamp(Math.Ceiling(_distance * 1.5), 5, 60);
        for (int i = -reach; i <= reach; i++)
        {
            float shade = i == 0 ? 0.22f : 0.08f;
            Line(new Vector3(i, -reach, 0), new Vector3(i, reach, 0), shade, width, height);
            Line(new Vector3(-reach, i, 0), new Vector3(reach, i, 0), shade, width, height);
        }
    }

    /// <summary>A faint line on the ground, cut where it goes behind the camera.</summary>
    private void Line(Vector3 a, Vector3 b, float shade, int width, int height)
    {
        float da = Vector3.Dot(a - _eye, _forward), db = Vector3.Dot(b - _eye, _forward);
        const float near = 0.1f;
        if (da < near && db < near) return;
        if (da < near) a = Vector3.Lerp(a, b, (near - da) / (db - da));
        else if (db < near) b = Vector3.Lerp(b, a, (near - db) / (da - db));
        Project(a, out float x0, out float y0, out _);
        Project(b, out float x1, out float y1, out _);
        int steps = (int)Math.Min(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)), 6000);
        for (int s = 0; s <= steps; s++)
        {
            float t = steps == 0 ? 0 : s / (float)steps;
            int x = (int)(x0 + (x1 - x0) * t), y = (int)(y0 + (y1 - y0) * t);
            if (x < 0 || y < 0 || x >= width || y >= height) continue;
            int i = (y * width + x) * 3;
            _colour[i] += shade;
            _colour[i + 1] += shade;
            _colour[i + 2] += shade;
        }
    }

    private void DrawParticle(ParticleEmitter emitter, (int[] Pixels, int Width, int Height)? texture, Particle p, int width, int height)
    {
        if (!Project(p.Position, out float cx, out float cy, out float depth)) return;
        float hx = MathF.Abs(p.Size.X) / 2 * _focal / depth, hy = MathF.Abs(p.Size.Y) / 2 * _focal / depth;
        if (hx < 0.3f || hy < 0.3f || p.Color.W <= 0.002f) return;
        float reach = MathF.Min(MathF.Sqrt(hx * hx + hy * hy), 2000);
        int x0 = Math.Max(0, (int)(cx - reach)), x1 = Math.Min(width - 1, (int)(cx + reach));
        int y0 = Math.Max(0, (int)(cy - reach)), y1 = Math.Min(height - 1, (int)(cy + reach));
        if (x0 > x1 || y0 > y1) return;
        float angle = p.Rotation * MathF.Tau, cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        var tint = emitter.Tint;
        var colour = new Vector4(p.Color.X * tint.R / 255f, p.Color.Y * tint.G / 255f, p.Color.Z * tint.B / 255f, Math.Clamp(p.Color.W, 0, 1) * tint.A / 255f);
        var (columns, rows) = emitter.Atlas;
        int frames = Math.Max(1, columns * rows);
        int frame = ((int)MathF.Floor(p.Frame) % frames + frames) % frames;
        float frameU = frame % columns / (float)columns, frameV = frame / columns / (float)rows;
        bool additive = emitter.Additive;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float lx = (dx * cos + dy * sin) / hx, ly = (-dx * sin + dy * cos) / hy;
                if (lx < -1 || lx > 1 || ly < -1 || ly > 1) continue;
                float r, g, b, a;
                if (texture is { } t)
                {
                    float u = frameU + (lx + 1) / 2 / columns, v = frameV + (ly + 1) / 2 / rows;
                    int tx = Math.Clamp((int)(u * t.Width), 0, t.Width - 1), ty = Math.Clamp((int)(v * t.Height), 0, t.Height - 1);
                    int texel = t.Pixels[ty * t.Width + tx];
                    a = ((texel >> 24) & 255) / 255f;
                    r = ((texel >> 16) & 255) / 255f;
                    g = ((texel >> 8) & 255) / 255f;
                    b = (texel & 255) / 255f;
                }
                else
                {
                    // no texture: a soft round spot
                    float fall = Math.Max(0, 1 - (lx * lx + ly * ly));
                    a = fall * fall;
                    r = g = b = a;
                }
                int i = (y * width + x) * 3;
                if (additive)
                {
                    float k = colour.W;
                    _colour[i] += r * colour.X * k;
                    _colour[i + 1] += g * colour.Y * k;
                    _colour[i + 2] += b * colour.Z * k;
                }
                else
                {
                    float cover = a * colour.W;
                    _colour[i] = _colour[i] * (1 - cover) + r * colour.X * colour.W;
                    _colour[i + 1] = _colour[i + 1] * (1 - cover) + g * colour.Y * colour.W;
                    _colour[i + 2] = _colour[i + 2] * (1 - cover) + b * colour.Z * colour.W;
                }
            }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        _lastMouse = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            Fit();
            return;
        }
        _turning = e.ChangedButton == MouseButton.Left && Keyboard.Modifiers != ModifierKeys.Shift;
        _moving = !_turning;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var at = e.GetPosition(this);
        var moved = at - _lastMouse;
        _lastMouse = at;
        if (_turning)
        {
            _yaw -= moved.X * 0.4;
            _pitch = Math.Clamp(_pitch + moved.Y * 0.4, -85, 85);
        }
        else if (_moving)
        {
            float step = (float)(_distance / Math.Max(1, ActualHeight) * 1.2);
            _target += (-_right * (float)moved.X + _up * (float)moved.Y) * step;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        _turning = _moving = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1 / 0.85), 0.3, 1000);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>The time and count in words, for the bar under the view.</summary>
    public static string Status(float time, int particles) =>
        L.T("ParticlePreview.Status", time.ToString("0.0", CultureInfo.CurrentCulture), particles);
}
