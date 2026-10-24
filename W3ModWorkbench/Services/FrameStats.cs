using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// A 3D view's frames counted as they're drawn, summed up over the last second for the frame rate panel that
/// <b>Settings &gt; Viewers</b> turns on in every 3D view: frames a second, how long a frame takes on average and at
/// the slowest, and how long the drawing itself took when the view knows.
/// </summary>
public sealed class FrameStats
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<(double At, double Work)> _frames = new();

    /// <summary>Whether the panel is shown, from Settings.</summary>
    public static bool Enabled => App.Settings.ShowFrameStats;

    /// <summary>Raised when Settings turns the panel on or off, for the views open to follow.</summary>
    public static event Action? EnabledChanged;

    public static void RaiseEnabledChanged() => EnabledChanged?.Invoke();

    /// <summary>Notes a frame drawn now, and the milliseconds the drawing took when it's known.</summary>
    public void Frame(double workMilliseconds = double.NaN)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        _frames.Enqueue((now, workMilliseconds));
        Trim(now);
    }

    private void Trim(double now)
    {
        while (_frames.Count > 0 && now - _frames.Peek().At > 1000) _frames.Dequeue();
    }

    /// <summary>The last second in a line; a view that drew nothing in it is still, and says so.</summary>
    public string Text()
    {
        Trim(_clock.Elapsed.TotalMilliseconds);
        if (_frames.Count < 2) return L.T("FrameStats.Still");
        var frames = _frames.ToArray();
        double span = frames[^1].At - frames[0].At, slowest = 0;
        for (int i = 1; i < frames.Length; i++) slowest = Math.Max(slowest, frames[i].At - frames[i - 1].At);
        double average = span / (frames.Length - 1);
        string line = L.T("FrameStats.Line", 1000 / average, average, slowest);
        var work = frames.Select(f => f.Work).Where(w => !double.IsNaN(w)).ToList();
        return work.Count > 0 ? line + L.T("FrameStats.Drawing", work.Average()) : line;
    }

    /// <summary>The panel's look: light text on a dark plate, readable over any picture in either theme.</summary>
    public static readonly Brush Plate = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x10)));

    public static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));

    public static readonly FontFamily Font = new("Cascadia Mono, Consolas"); // loc-ignore

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>Draws the panel in a view's top-left corner, for a view that draws itself (the Direct3D ones).</summary>
    public void Draw(DrawingContext dc, Visual visual)
    {
        var text = new FormattedText(Text(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            11.5, Ink, VisualTreeHelper.GetDpi(visual).PixelsPerDip);
        dc.DrawRoundedRectangle(Plate, null, new Rect(8, 8, text.Width + 16, text.Height + 8), 4, 4);
        dc.DrawText(text, new Point(16, 12));
    }
}
