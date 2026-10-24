using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Plays a sound's samples: from any point, looping or not, at a volume, telling where it's got to. More than two
/// channels are mixed down to stereo, the centre and back to both sides.
/// </summary>
public sealed class SoundPlayer : IDisposable
{
    private readonly Source _source;
    private WaveOutEvent? _output;

    /// <summary>The sound in stereo, left and right interleaved.</summary>
    public float[] Stereo { get; }
    public int Rate { get; }
    public double Duration => Stereo.Length / 2.0 / Rate;

    public bool Loop { get => _source.Loop; set => _source.Loop = value; }
    public float Volume { get => _source.Volume; set => _source.Volume = value; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    /// <summary>Where it is, in seconds.</summary>
    public double Position
    {
        get => _source.Frame / (double)Rate;
        set => _source.Frame = Math.Clamp((long)(value * Rate), 0, Stereo.Length / 2);
    }

    /// <summary>Raised when it plays to its end without looping.</summary>
    public event EventHandler? Ended;

    public SoundPlayer(short[] samples, int channels, int rate)
    {
        var stereo = ToStereo(samples, Math.Max(1, channels));
        // a rate sound cards won't take (the controller's 2 kHz vibration waveforms) is played at 48 kHz, drawn between
        if (rate < 8000)
        {
            stereo = Resample(stereo, rate, 48000);
            rate = 48000;
        }
        Rate = rate;
        Stereo = stereo;
        _source = new Source(this);
    }

    /// <summary>Stereo samples at another rate, each new one drawn between the two old ones either side of it.</summary>
    private static float[] Resample(float[] stereo, int from, int to)
    {
        int frames = stereo.Length / 2, made = (int)((long)frames * to / from);
        var output = new float[made * 2];
        for (int i = 0; i < made; i++)
        {
            double at = (double)i * from / to;
            int a = Math.Min((int)at, frames - 1), b = Math.Min(a + 1, frames - 1);
            float t = (float)(at - a);
            output[i * 2] = stereo[a * 2] + (stereo[b * 2] - stereo[a * 2]) * t;
            output[i * 2 + 1] = stereo[a * 2 + 1] + (stereo[b * 2 + 1] - stereo[a * 2 + 1]) * t;
        }
        return output;
    }

    /// <summary>Samples of any number of channels as stereo: mono to both, more than two mixed down.</summary>
    public static float[] ToStereo(short[] samples, int channels)
    {
        int frames = samples.Length / channels;
        var stereo = new float[frames * 2];
        const float scale = 1f / 32768;
        for (int i = 0; i < frames; i++)
        {
            int at = i * channels;
            float left, right;
            switch (channels)
            {
                case 1:
                    left = right = samples[at] * scale;
                    break;
                case 2:
                    left = samples[at] * scale;
                    right = samples[at + 1] * scale;
                    break;
                default:
                    // FL FR C LFE BL BR …: the centre to both, the back to its side, the LFE left out
                    left = samples[at] * scale + 0.7f * samples[at + 2] * scale + (channels > 4 ? 0.7f * samples[at + 4] * scale : 0);
                    right = samples[at + 1] * scale + 0.7f * samples[at + 2] * scale + (channels > 5 ? 0.7f * samples[at + 5] * scale : 0);
                    break;
            }
            stereo[i * 2] = Math.Clamp(left, -1, 1);
            stereo[i * 2 + 1] = Math.Clamp(right, -1, 1);
        }
        return stereo;
    }

    public void Play()
    {
        if (IsPlaying) return;
        if (_source.Frame >= Stereo.Length / 2) _source.Frame = 0;
        if (_output is null)
        {
            _output = new WaveOutEvent { DesiredLatency = 100 };
            _output.Init(_source);
            _output.PlaybackStopped += (_, _) => { if (_source.Frame >= Stereo.Length / 2) Ended?.Invoke(this, EventArgs.Empty); };
        }
        SoundOut.Play(_output);
    }

    public void Pause() => _output?.Pause();

    public void Stop()
    {
        _output?.Stop();
        _source.Frame = 0;
    }

    public void Dispose()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
    }

    private sealed class Source(SoundPlayer player) : ISampleProvider
    {
        public long Frame;
        public bool Loop;
        public float Volume = 1;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(player.Rate, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var stereo = player.Stereo;
            int written = 0;
            while (written < count)
            {
                long at = Interlocked.Read(ref Frame) * 2;
                if (at >= stereo.Length)
                {
                    if (!Loop || stereo.Length == 0) break;
                    Interlocked.Exchange(ref Frame, 0);
                    continue;
                }
                int take = (int)Math.Min(count - written, stereo.Length - at);
                take -= take & 1;
                if (take <= 0) break;
                for (int i = 0; i < take; i++) buffer[offset + written + i] = stereo[at + i] * Volume;
                written += take;
                Interlocked.Add(ref Frame, take / 2);
            }
            return written;
        }
    }
}

/// <summary>
/// A sound's waveform, left channel over right, with a ruler of seconds, its loop and marked points, and a line where it
/// is. Click or drag to go to a moment.
/// </summary>
public sealed class Waveform : FrameworkElement
{
    private const double RulerHeight = 18;
    private float[] _stereo = [];
    private int _rate = 48000;
    private double _position;
    private (double Start, double End)? _loop;
    private IReadOnlyList<(double Time, string Label)> _markers = [];
    private (float Min, float Max)[,]? _peaks;
    private int _peaksWidth;

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(nameof(WaveBrush), typeof(Brush), typeof(Waveform),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(Waveform),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(Waveform),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(Waveform),
        new FrameworkPropertyMetadata(Brushes.Orchid, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush WaveBrush { get => (Brush)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>Raised when a moment is clicked or dragged to, in seconds.</summary>
    public event EventHandler<double>? Seek;

    public Waveform()
    {
        SetResourceReference(WaveBrushProperty, "AccentFillColorSecondaryBrush");
        SetResourceReference(LineBrushProperty, "CardStrokeColorDefaultBrush");
        SetResourceReference(TextBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(AccentBrushProperty, "AccentFillColorDefaultBrush");
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
    }

    private double Duration => _stereo.Length / 2.0 / _rate;

    /// <summary>Shows a sound, its loop and marked points (in seconds); null clears it.</summary>
    public void Show(float[]? stereo, int rate, (double, double)? loop = null, IReadOnlyList<(double, string)>? markers = null)
    {
        _stereo = stereo ?? [];
        _rate = Math.Max(1, rate);
        _loop = loop;
        _markers = markers ?? [];
        _position = 0;
        _peaks = null;
        InvalidateVisual();
    }

    public double Position
    {
        get => _position;
        set { _position = value; InvalidateVisual(); }
    }

    /// <summary>Each column's lowest and highest sample, per channel, for the width it's drawn at.</summary>
    private void MakePeaks(int width)
    {
        _peaksWidth = width;
        _peaks = new (float, float)[2, width];
        int frames = _stereo.Length / 2;
        for (int x = 0; x < width; x++)
        {
            long from = (long)frames * x / width, to = Math.Max(from + 1, (long)frames * (x + 1) / width);
            for (int c = 0; c < 2; c++)
            {
                float min = 0, max = 0;
                // a long sound's columns are sampled, every frame read only for short ones
                long step = Math.Max(1, (to - from) / 2000);
                for (long f = from; f < to && f < frames; f += step)
                {
                    float v = _stereo[f * 2 + c];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                _peaks[c, x] = (min, max);
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (width < 2 || height < RulerHeight + 10) return;
        var line = new Pen(LineBrush, 1);
        double top = RulerHeight, band = (height - top) / 2;
        double duration = Duration;
        if (_stereo.Length == 0 || duration <= 0)
        {
            dc.DrawLine(line, new Point(0, top + band), new Point(width, top + band));
            return;
        }
        double X(double time) => time / duration * width;
        // the loop, shaded
        if (_loop is var (loopStart, loopEnd) && loopEnd > loopStart)
        {
            var shade = AccentBrush.Clone();
            shade.Opacity = 0.12;
            dc.DrawRectangle(shade, null, new Rect(X(loopStart), top, Math.Max(1, X(loopEnd) - X(loopStart)), height - top));
        }
        // the ruler: a tick a sensible number of seconds apart
        double[] steps = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        double step = steps.FirstOrDefault(s => s / duration * width >= 70, 600);
        var typeface = new Typeface(L.T("SoundPlayback.SegoeUI"));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double t = 0; t <= duration + 1e-6; t += step)
        {
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(line, new Point(x, top - 5), new Point(x, height));
            string label = duration >= 60 ? TimeSpan.FromSeconds(t).ToString(@"m\:ss") : t.ToString(step < 1 ? "0.0#" : "0", CultureInfo.CurrentCulture) + " s";
            dc.DrawText(new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, TextBrush, dpi), new Point(x + 3, 1));
        }
        // each channel's waveform about its middle
        int columns = (int)Math.Ceiling(width);
        if (_peaks is null || _peaksWidth != columns) MakePeaks(columns);
        for (int c = 0; c < 2; c++)
        {
            double middle = top + band * c + band / 2;
            dc.DrawLine(line, new Point(0, middle), new Point(width, middle));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
                for (int x = 0; x < columns; x++)
                {
                    var (min, max) = _peaks![c, x];
                    double y1 = middle - max * band / 2 * 0.95, y2 = middle - min * band / 2 * 0.95;
                    if (y2 - y1 < 1) y2 = y1 + 1;
                    g.BeginFigure(new Point(x, y1), false, false);
                    g.LineTo(new Point(x, y2), true, false);
                }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(WaveBrush, 1), geometry);
        }
        // marked points, with their labels
        var marker = new Pen(TextBrush, 1) { DashStyle = DashStyles.Dash };
        foreach (var (time, label) in _markers)
        {
            double x = X(time);
            dc.DrawLine(marker, new Point(x, top), new Point(x, height));
            if (label.Length > 0)
                dc.DrawText(new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10.5, TextBrush, dpi), new Point(x + 3, top + 2));
        }
        // where it is
        double at = X(_position);
        dc.DrawLine(new Pen(AccentBrush, 2), new Point(at, 0), new Point(at, height));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        SeekTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) SeekTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    private void SeekTo(double x)
    {
        if (Duration <= 0 || ActualWidth <= 0) return;
        double time = Math.Clamp(x / ActualWidth, 0, 1) * Duration;
        Position = time;
        Seek?.Invoke(this, time);
    }
}
