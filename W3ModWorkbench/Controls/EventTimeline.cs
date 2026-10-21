using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Animation;

namespace W3ModWorkbench;

/// <summary>
/// An animation's events along its length, as REDkit's animation events editor shows them: a row for each track,
/// named on the left, each event marked where it starts (a note for a sound, a footprint for a footstep, a bar for one
/// that lasts), a ruler of seconds along the bottom and a line where the animation is. Click an event to pick it, or
/// the ruler or an empty place to go to that moment; the wheel zooms the time about the mouse.
/// </summary>
public sealed class EventTimeline : FrameworkElement
{
    private const double HeaderWidth = 170, RowHeight = 34, RulerHeight = 26;

    private List<(string Track, List<AnimEvent> Events)> _tracks = [];
    private float _duration = 1, _time;
    private double _zoom = 1, _scroll;
    private AnimEvent? _selected, _hover;
    private bool _dragging;

    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty LineBrushProperty = Brush(nameof(LineBrush));
    public static readonly DependencyProperty RowBrushProperty = Brush(nameof(RowBrush));
    public static readonly DependencyProperty AccentBrushProperty = Brush(nameof(AccentBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(EventTimeline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush RowBrush { get => (Brush)GetValue(RowBrushProperty); set => SetValue(RowBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>Raised when an event is clicked, with it.</summary>
    public event EventHandler<AnimEvent>? EventClicked;

    /// <summary>Raised when an event is double-clicked, to open what it plays (its sound).</summary>
    public event EventHandler<AnimEvent>? EventOpened;

    /// <summary>Raised when a moment is clicked or dragged to, with the time.</summary>
    public event EventHandler<float>? TimePicked;

    public EventTimeline()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(RowBrushProperty, "SubtleFillColorSecondaryBrush");
        SetResourceReference(AccentBrushProperty, "AccentFillColorDefaultBrush");
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Shows an animation's events, on tracks in their order, over its length (or the last event's end when it isn't known).</summary>
    public void Show(IEnumerable<AnimEvent> events, float duration)
    {
        var list = events.ToList();
        _tracks = list.GroupBy(e => e.Track).OrderBy(g => g.Key == "__Default" ? "" : g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.OrderBy(e => e.Start).ToList())).ToList();
        _duration = duration > 0 ? duration : Math.Max(1, list.Count == 0 ? 1 : list.Max(e => e.Start + e.Duration) * 1.1f);
        _zoom = 1;
        _scroll = 0;
        _selected = null;
        _time = 0;
        Height = RulerHeight + Math.Max(1, _tracks.Count) * RowHeight + 4;
        InvalidateVisual();
    }

    /// <summary>The animation's length, once the preview knows it.</summary>
    public float Duration
    {
        get => _duration;
        set { if (value > 0) { _duration = value; InvalidateVisual(); } }
    }

    /// <summary>Where the line is.</summary>
    public float Time
    {
        get => _time;
        set { _time = value; InvalidateVisual(); }
    }

    public AnimEvent? Selected
    {
        get => _selected;
        set { _selected = value; InvalidateVisual(); }
    }

    private double Width0 => Math.Max(10, ActualWidth - HeaderWidth - 12);
    private double X(double t) => HeaderWidth + 6 + (t / _duration * _zoom * Width0) - _scroll;
    private double T(double x) => Math.Clamp((x - HeaderWidth - 6 + _scroll) / (_zoom * Width0) * _duration, 0, _duration);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = TextElement.GetFontFamily(this);
        var plain = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var line = new Pen(LineBrush, 0.6);
        double rulerTop = RulerHeight - 2;

        // the ruler: a tick every so many seconds, as fits
        double secondsPerPixel = _duration / (_zoom * Width0);
        double step = new[] { 0.01, 0.02, 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 5, 10, 30, 60 }.FirstOrDefault(s => s / secondsPerPixel >= 60, 60);
        for (double t = 0; t <= _duration + 1e-6; t += step)
        {
            double x = X(t);
            if (x < HeaderWidth || x > ActualWidth) continue;
            dc.DrawLine(line, new Point(x, rulerTop - 6), new Point(x, ActualHeight));
            var label = new FormattedText(t.ToString(step < 0.1 ? "0.00" : "0.0#", CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 10.5, LineBrush, dip);
            dc.DrawText(label, new Point(x + 3, 3));
        }
        dc.DrawLine(new Pen(LineBrush, 1), new Point(HeaderWidth, rulerTop), new Point(ActualWidth, rulerTop));

        for (int r = 0; r < _tracks.Count; r++)
        {
            double top = RulerHeight + r * RowHeight;
            var (track, events) = _tracks[r];
            if (r % 2 == 0) dc.DrawRectangle(RowBrush, null, new Rect(0, top, ActualWidth, RowHeight));
            var name = new FormattedText(track, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 12, TextBrush, dip)
            { MaxTextWidth = HeaderWidth - 16, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(name, new Point(10, top + (RowHeight - name.Height) / 2));
            dc.PushClip(new RectangleGeometry(new Rect(HeaderWidth, top, Math.Max(0, ActualWidth - HeaderWidth), RowHeight)));
            foreach (var e in events)
            {
                double x = X(e.Start);
                var color = new SolidColorBrush(ColorOf(e));
                color.Freeze();
                bool picked = ReferenceEquals(e, _selected), hover = ReferenceEquals(e, _hover);
                if (e.Duration > 0)
                {
                    var bar = new Rect(x, top + 8, Math.Max(3, X(e.Start + e.Duration) - x), RowHeight - 16);
                    dc.DrawRoundedRectangle(color, picked ? new Pen(AccentBrush, 2) : null, bar, 3, 3);
                }
                // a marker where it starts: a diamond, larger when picked or under the mouse
                double size = picked || hover ? 7 : 5.5;
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(x, top + RowHeight / 2 - size), true, true);
                    g.LineTo(new Point(x + size, top + RowHeight / 2), true, false);
                    g.LineTo(new Point(x, top + RowHeight / 2 + size), true, false);
                    g.LineTo(new Point(x - size, top + RowHeight / 2), true, false);
                }
                diamond.Freeze();
                dc.DrawGeometry(color, picked ? new Pen(AccentBrush, 2) : new Pen(TextBrush, 0.6), diamond);
                if (_zoom * Width0 / Math.Max(1, events.Count) > 50 || picked || hover)
                {
                    var text = new FormattedText(e.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 10.5, TextBrush, dip)
                    { MaxTextWidth = 220, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(text, new Point(x + size + 3, top + (RowHeight - text.Height) / 2));
                }
            }
            dc.Pop();
            dc.DrawLine(line, new Point(0, top + RowHeight), new Point(ActualWidth, top + RowHeight));
        }
        dc.DrawLine(new Pen(LineBrush, 1), new Point(HeaderWidth, 0), new Point(HeaderWidth, ActualHeight));

        // where the animation is
        double now = X(_time);
        if (now >= HeaderWidth)
        {
            dc.DrawLine(new Pen(AccentBrush, 2), new Point(now, 0), new Point(now, ActualHeight));
            var label = new FormattedText(_time.ToString("0.00", CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 10.5, AccentBrush, dip);
            dc.DrawText(label, new Point(now + 3, RulerHeight - label.Height - 1));
        }
        if (_tracks.Count == 0)
        {
            var none = new FormattedText("This animation has no events.", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 12, LineBrush, dip);
            dc.DrawText(none, new Point(HeaderWidth + 12, RulerHeight + 8));
        }
    }

    /// <summary>The colour of an event's marker: sounds blue, footsteps brown, effects orange, the rest grey.</summary>
    private static Color ColorOf(AnimEvent e) => e.Kind switch
    {
        "sound" => Color.FromRgb(0x4C, 0x9A, 0xFF),
        "footstep" => Color.FromRgb(0xB0, 0x86, 0x5A),
        "effect" => Color.FromRgb(0xF2, 0x7A, 0x4A),
        "duration" => Color.FromRgb(0x8E, 0x6B, 0xB0),
        _ => Color.FromRgb(0x9E, 0x9E, 0xA6),
    };

    private AnimEvent? EventAt(Point p)
    {
        int r = (int)((p.Y - RulerHeight) / RowHeight);
        if (p.Y < RulerHeight || r < 0 || r >= _tracks.Count || p.X < HeaderWidth) return null;
        AnimEvent? best = null;
        double nearest = 9;
        foreach (var e in _tracks[r].Events)
        {
            double x = X(e.Start), end = X(e.Start + e.Duration);
            double d = e.Duration > 0 && p.X >= x && p.X <= end ? 0 : Math.Abs(p.X - x);
            if (d < nearest) (best, nearest) = (e, d);
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        if (EventAt(p) is { } picked)
        {
            Selected = picked;
            EventClicked?.Invoke(this, picked);
            if (e.ClickCount == 2) EventOpened?.Invoke(this, picked);
            return;
        }
        if (p.X < HeaderWidth) return;
        _dragging = CaptureMouse();
        TimePicked?.Invoke(this, (float)T(p.X));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            TimePicked?.Invoke(this, (float)T(p.X));
            return;
        }
        var at = EventAt(p);
        if (ReferenceEquals(at, _hover)) return;
        _hover = at;
        Cursor = at is null ? null : Cursors.Hand;
        ToolTip = at is null ? null : $"{at.Label}\n{at.Object.ClassName}  ·  {at.Track}\nat {at.Start:0.000} s" + (at.Duration > 0 ? $" for {at.Duration:0.000} s" : "");
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // zoom the time about the mouse
        var p = e.GetPosition(this);
        double at = T(p.X);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 1 / 1.25), 1, 40);
        _scroll = Math.Max(0, HeaderWidth + 6 + at / _duration * _zoom * Width0 - p.X);
        InvalidateVisual();
        e.Handled = true;
    }
}
