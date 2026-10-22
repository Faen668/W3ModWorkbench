using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Curves;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A line on a <see cref="CurveChart"/>: a curve, which of its channels (0 to 3) and the colour it's drawn in.</summary>
public sealed record CurveSeries(string Name, RedCurve Curve, int Channel, Color Color);

/// <summary>
/// Curves drawn over their span: a day (0:00 to 24:00) for an environment, a particle's life (0 to 1) for an effect.
/// Number curves are lines with their keys marked; a colour curve is a band of its colours over the span, its channels
/// drawn under it (and its scale, for a scaled one). A line marks the time shown; hovering reads the values off; a click
/// or drag moves the time.
/// </summary>
public sealed class CurveChart : FrameworkElement
{
    private IReadOnlyList<CurveSeries> _series = [];
    private bool _day = true;
    private double _time = -1;
    private double? _hover;

    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty LineBrushProperty = Brush(nameof(LineBrush));
    public static readonly DependencyProperty GridBrushProperty = Brush(nameof(GridBrush));
    public static readonly DependencyProperty AccentBrushProperty = Brush(nameof(AccentBrush));
    public static readonly DependencyProperty BackBrushProperty = Brush(nameof(BackBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(CurveChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public Brush BackBrush { get => (Brush)GetValue(BackBrushProperty); set => SetValue(BackBrushProperty, value); }

    /// <summary>What's written when there's nothing to draw.</summary>
    public string EmptyText { get => _emptyText; set { _emptyText = value; InvalidateVisual(); } }
    private string _emptyText = "";

    /// <summary>Raised when the time is moved by a click or a drag, with the time (0 to 1).</summary>
    public event EventHandler<double>? TimePicked;

    public CurveChart()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(GridBrushProperty, "DividerStrokeColorDefaultBrush");
        SetResourceReference(AccentBrushProperty, "AccentFillColorDefaultBrush");
        SetResourceReference(BackBrushProperty, "SolidBackgroundFillColorBaseBrush");
        ClipToBounds = true;
    }

    /// <summary>Shows curves over a day (times as hours) or over a span of 0 to 1.</summary>
    public void Show(IReadOnlyList<CurveSeries> series, bool day)
    {
        _series = series;
        _day = day;
        InvalidateVisual();
    }

    /// <summary>The time marked, 0 to 1, or below 0 for none.</summary>
    public double Time
    {
        get => _time;
        set { _time = value; InvalidateVisual(); }
    }

    private const double Left = 56, Right = 14, Top = 10, Bottom = 24, Band = 26;

    private bool HasColor => _series.Any(s => s.Curve.IsColor);

    private Rect PlotArea
    {
        get
        {
            double top = Top + (HasColor ? Band + 8 : 0);
            return new Rect(Left, top, Math.Max(10, ActualWidth - Left - Right), Math.Max(10, ActualHeight - top - Bottom));
        }
    }

    /// <summary>The values a series takes: a colour's channel 0 to 255, its scale, or the number.</summary>
    private static double Value(CurveSeries s, double time) => s.Curve.Evaluate((float)time) switch
    {
        var v => s.Channel switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W },
    };

    private (double Min, double Max) Range()
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in _series)
        {
            for (int i = 0; i <= 240; i++)
            {
                double v = Value(s, i / 240.0);
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            foreach (var key in s.Curve.Keys)
            {
                double v = s.Channel switch { 0 => key.Value.X, 1 => key.Value.Y, 2 => key.Value.Z, _ => key.Value.W };
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }
        if (min == double.MaxValue) return (0, 1);
        if (max - min < 1e-6)
        {
            double pad = Math.Max(Math.Abs(max) * 0.1, 0.5);
            return (min - pad, max + pad);
        }
        double margin = (max - min) * 0.08;
        return (min - margin, max + margin);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var area = PlotArea;
        if (_series.Count == 0 || _series.All(s => s.Curve.Keys.Count == 0))
        {
            if (EmptyText.Length > 0)
            {
                var text = Text(EmptyText, 13, LineBrush, dpi);
                text.MaxTextWidth = Math.Max(50, ActualWidth - 40);
                text.TextAlignment = TextAlignment.Center;
                dc.DrawText(text, new Point(20, ActualHeight / 2 - text.Height / 2));
            }
            if (_series.Count == 0) return;
        }
        var (min, max) = Range();
        double X(double t) => area.Left + t * area.Width;
        double Y(double v) => area.Bottom - (v - min) / (max - min) * area.Height;

        var grid = new Pen(GridBrush, 1);
        var axis = new Pen(LineBrush, 1);

        // the times across: every 2 hours of a day, or tenths of a life
        int steps = _day ? 12 : 10;
        for (int i = 0; i <= steps; i++)
        {
            double x = X(i / (double)steps);
            dc.DrawLine(grid, new Point(x, area.Top), new Point(x, area.Bottom));
            string label = _day ? $"{i * 2:00}:00" : (i / (double)steps).ToString("0.0", CultureInfo.CurrentCulture);
            var text = Text(label, 10, LineBrush, dpi);
            if (i == steps) x -= text.Width / 2 + 2;
            else if (i > 0) x -= text.Width / 2;
            dc.DrawText(text, new Point(x, area.Bottom + 4));
        }
        // the values up: about every 40 pixels, at round steps
        double step = (max - min) / Math.Max(1, area.Height / 40), power = Math.Pow(10, Math.Floor(Math.Log10(step)));
        step = step / power < 2 ? 2 * power : step / power < 5 ? 5 * power : 10 * power;
        for (double v = Math.Ceiling(min / step) * step; v <= max; v += step)
        {
            double y = Y(v);
            dc.DrawLine(Math.Abs(v) < step / 2 ? axis : grid, new Point(area.Left, y), new Point(area.Right, y));
            var text = Text(Format(Math.Abs(v) < step / 2 ? 0 : v), 10, LineBrush, dpi);
            dc.DrawText(text, new Point(area.Left - text.Width - 6, y - text.Height / 2));
        }
        dc.DrawRectangle(null, axis, area);

        // a colour curve's colours over the span, in a band above
        if (_series.FirstOrDefault(s => s.Curve.IsColor) is { } colour)
        {
            var band = new Rect(area.Left, Top, area.Width, Band);
            var stops = new GradientStopCollection();
            for (int i = 0; i <= 96; i++)
            {
                var v = colour.Curve.Evaluate(i / 96f);
                stops.Add(new GradientStop(Color.FromRgb(Channel(v.X), Channel(v.Y), Channel(v.Z)), i / 96.0));
            }
            var gradient = new LinearGradientBrush(stops, 0);
            gradient.Freeze();
            dc.DrawRoundedRectangle(gradient, axis, band, 4, 4);
            foreach (var key in colour.Curve.Keys)
                dc.DrawLine(new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dot }, new Point(X(key.Time), band.Bottom - 6), new Point(X(key.Time), band.Bottom));
        }

        // each series as a line, its keys as dots
        foreach (var s in _series)
        {
            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            var pen = new Pen(brush, 2);
            pen.Freeze();
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                int samples = (int)Math.Max(60, area.Width / 2);
                for (int i = 0; i <= samples; i++)
                {
                    var p = new Point(X(i / (double)samples), Y(Value(s, i / (double)samples)));
                    if (i == 0) g.BeginFigure(p, false, false);
                    else g.LineTo(p, true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            foreach (var key in s.Curve.Keys)
            {
                if (key.Time < -0.001 || key.Time > 1.001) continue;
                double v = s.Channel switch { 0 => key.Value.X, 1 => key.Value.Y, 2 => key.Value.Z, _ => key.Value.W };
                dc.DrawEllipse(BackBrush, pen, new Point(X(key.Time), Y(v)), 3.5, 3.5);
            }
        }

        // the time shown, and where the mouse is with each line's value there
        if (_time >= 0)
        {
            var pen = new Pen(AccentBrush, 2);
            dc.DrawLine(pen, new Point(X(_time), Top), new Point(X(_time), area.Bottom));
        }
        if (_hover is { } h)
        {
            double x = X(h);
            dc.DrawLine(new Pen(LineBrush, 1) { DashStyle = DashStyles.Dash }, new Point(x, Top), new Point(x, area.Bottom));
            var lines = new List<string> { _day ? Clock(h) : h.ToString("0.000", CultureInfo.CurrentCulture) };
            foreach (var s in _series.Take(5)) lines.Add($"{s.Name}  {Format(Value(s, h))}");
            if (_series.FirstOrDefault(s => s.Curve.IsColor) is { } c)
            {
                var v = c.Curve.Evaluate((float)h);
                lines.Add(L.T("CurveChart.Rgb", $"{v.X:0} {v.Y:0} {v.Z:0}") + (c.Curve.Kind == CurveKind.ColorScaled ? $"  ×{v.W:0.###}" : ""));
            }
            var text = Text(string.Join("\n", lines), 11, TextBrush, dpi);
            double tx = x + 10 + text.Width + 12 > ActualWidth ? x - text.Width - 22 : x + 10;
            dc.DrawRoundedRectangle(BackBrush, new Pen(GridBrush, 1), new Rect(tx, area.Top + 6, text.Width + 12, text.Height + 8), 4, 4);
            dc.DrawText(text, new Point(tx + 6, area.Top + 10));
        }
    }

    private static byte Channel(float v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>A day's time (0 to 1) as a clock, 13:45.</summary>
    public static string Clock(double day)
    {
        int minutes = (int)Math.Round(((day % 1) + 1) % 1 * 24 * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private static string Format(double v) => Math.Abs(v) >= 1000 ? v.ToString("0", CultureInfo.CurrentCulture)
        : Math.Abs(v) >= 10 ? v.ToString("0.#", CultureInfo.CurrentCulture) : v.ToString("0.###", CultureInfo.CurrentCulture);

    private static FormattedText Text(string text, double size, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi); // loc-ignore

    private double TimeAt(Point p)
    {
        var area = PlotArea;
        return Math.Clamp((p.X - area.Left) / area.Width, 0, 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _hover = p.X >= PlotArea.Left && p.X <= PlotArea.Right && _series.Count > 0 ? TimeAt(p) : null;
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured) Pick(p);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    private void Pick(Point p)
    {
        _time = TimeAt(p);
        TimePicked?.Invoke(this, _time);
        InvalidateVisual();
    }

    /// <summary>The colours a series is drawn in: red, green, blue and a fourth, then others for more lines.</summary>
    public static Color SeriesColor(int index) => index switch
    {
        0 => Color.FromRgb(0xE5, 0x5A, 0x5A),
        1 => Color.FromRgb(0x4C, 0xC0, 0x5A),
        2 => Color.FromRgb(0x4C, 0x8A, 0xFF),
        3 => Color.FromRgb(0xC9, 0xA2, 0x3A),
        _ => SimplexMap.ColorOf(index),
    };
}
