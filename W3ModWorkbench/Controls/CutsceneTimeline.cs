using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Cutscenes;

namespace W3ModWorkbench;

/// <summary>
/// A cutscene's events along its length: a lane for the whole scene's (lines, music, fades, effects) and one for each
/// actor's, with a ruler of seconds above. An event that lasts is a bar, one that doesn't a tick. It fits its width,
/// or is as wide as <see cref="PixelsPerSecond"/> makes it. Hovering an event says what it is; clicking selects it.
/// With a <see cref="Playhead"/>, a line shows the time being played, and clicking or dragging on the ruler moves it.
/// </summary>
public sealed class CutsceneTimeline : FrameworkElement
{
    private const double RulerHeight = 22;
    private const double LaneHeight = 22;
    private const double RowHeight = 11;
    private const double LabelWidth = 112;
    private const double RightPad = 10;

    private CutsceneFile? _cutscene;
    private readonly List<Lane> _lanes = [];
    private readonly List<(Rect Box, CutsceneEvent Event)> _hits = [];
    private CutsceneEvent? _hover;

    /// <summary>
    /// A lane of events. Bars that overlap go in rows one under another, so none hides another; ticks go across
    /// the whole lane.
    /// </summary>
    private sealed record Lane(string Name, List<(CutsceneEvent Event, int Row)> Events, int Rows)
    {
        public double Height => Math.Max(LaneHeight, Rows * RowHeight + 8);
    }

    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty LineBrushProperty = Brush(nameof(LineBrush));
    public static readonly DependencyProperty StripeBrushProperty = Brush(nameof(StripeBrush));
    public static readonly DependencyProperty AccentBrushProperty = Brush(nameof(AccentBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(CutsceneTimeline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush StripeBrush { get => (Brush)GetValue(StripeBrushProperty); set => SetValue(StripeBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>How wide a second is; 0 fits the cutscene to the width there is.</summary>
    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set { _pixelsPerSecond = Math.Max(0, value); InvalidateMeasure(); }
    }
    private double _pixelsPerSecond;

    /// <summary>Only actors with events get a lane, for the narrow preview; otherwise every actor does.</summary>
    public bool OnlyBusyLanes { get; set; }

    /// <summary>Kinds of event not drawn. Cloth resets are thousands of ticks that say little, so they start hidden.</summary>
    public HashSet<CutsceneEventKind> HiddenKinds { get; } = [CutsceneEventKind.Cloth];

    public CutsceneEvent? Selected
    {
        get => _selected;
        set { _selected = value; InvalidateVisual(); }
    }
    private CutsceneEvent? _selected;

    /// <summary>Raised when an event is clicked, with it, or null for a click on nothing.</summary>
    public event EventHandler<CutsceneEvent?>? EventClicked;

    /// <summary>The time being played, drawn as a line; null for none, when the ruler doesn't seek either.</summary>
    public double? Playhead
    {
        get => _playhead;
        set { _playhead = value; InvalidateVisual(); }
    }
    private double? _playhead;

    /// <summary>Raised with the time clicked or dragged to on the ruler, when there's a playhead.</summary>
    public event EventHandler<double>? Seeked;
    private bool _seeking;

    public CutsceneTimeline()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "CardStrokeColorDefaultBrush");
        SetResourceReference(StripeBrushProperty, "SubtleFillColorSecondaryBrush");
        SetResourceReference(AccentBrushProperty, "AccentFillColorDefaultBrush");
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetBetweenShowDelay(this, 0);
        ToolTip = "";
        ToolTipOpening += (_, e) => { if (_hover is null) e.Handled = true; };
    }

    public void Show(CutsceneFile? cutscene)
    {
        _cutscene = cutscene;
        Refresh();
    }

    /// <summary>Lays the lanes out again, after <see cref="HiddenKinds"/> or <see cref="OnlyBusyLanes"/> changes.</summary>
    public void Refresh()
    {
        _lanes.Clear();
        _hover = null;
        if (_cutscene is { } c)
        {
            bool Shown(CutsceneEvent e) => !HiddenKinds.Contains(e.Kind);
            var scene = c.SceneEvents.Where(Shown).ToList();
            if (scene.Count > 0 || !OnlyBusyLanes) _lanes.Add(Pack("Scene", scene));
            foreach (var actor in c.Actors)
            {
                var events = actor.Events.Where(Shown).ToList();
                if (events.Count > 0 || !OnlyBusyLanes) _lanes.Add(Pack(actor.Name, events));
            }
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Puts each bar in the first row that's free by its start, or a new row; past the most rows a lane has, in the
    /// row that frees up first.
    /// </summary>
    private Lane Pack(string name, List<CutsceneEvent> events)
    {
        int most = OnlyBusyLanes ? 3 : 6;
        var ends = new List<float>();
        var placed = new List<(CutsceneEvent, int)>();
        foreach (var e in events.OrderBy(e => e.Start))
        {
            if (e.Duration <= 0)
            {
                placed.Add((e, -1));
                continue;
            }
            int row = ends.FindIndex(end => end <= e.Start);
            if (row < 0 && ends.Count < most)
            {
                ends.Add(0);
                row = ends.Count - 1;
            }
            if (row < 0) row = ends.IndexOf(ends.Min());
            ends[row] = Math.Max(ends[row], e.End);
            placed.Add((e, row));
        }
        // bars under ticks, so a tick on a bar shows
        return new Lane(name, [.. placed.OrderBy(p => p.Item2 < 0)], Math.Max(1, ends.Count));
    }

    /// <summary>A colour for each kind of event, the same in the list, the legend and the timeline.</summary>
    public static SolidColorBrush BrushOf(CutsceneEventKind kind) => KindBrushes[(int)kind];

    private static readonly SolidColorBrush[] KindBrushes = new[]
    {
        "#4C9AFF", // dialog
        "#2EB67D", // sound
        "#F2994A", // effect
        "#B57BE0", // item
        "#E5675F", // appearance
        "#3DB8D8", // camera
        "#E6BE3A", // light
        "#8E8E93", // cloth
        "#E06FAE", // morph
        "#7CC49A", // world
        "#D9A441", // quest
        "#A0A0A0", // other
    }.Select(c =>
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c));
        b.Freeze();
        return b;
    }).ToArray();

    private double Scale(double width) =>
        PixelsPerSecond > 0 ? PixelsPerSecond : Math.Max(1, width - LabelWidth - RightPad) / Math.Max(0.1, _cutscene?.Duration ?? 1);

    protected override Size MeasureOverride(Size available)
    {
        double height = RulerHeight + _lanes.Sum(l => l.Height) + 4;
        double duration = _cutscene?.Duration ?? 0;
        double width = PixelsPerSecond > 0 ? LabelWidth + duration * PixelsPerSecond + RightPad
            : double.IsInfinity(available.Width) ? LabelWidth + duration * 40 + RightPad : available.Width;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // so the whole of it takes the mouse
        if (_cutscene is not { } c) return;
        double scale = Scale(ActualWidth);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var line = new Pen(LineBrush, 1);
        line.Freeze();
        double X(double seconds) => LabelWidth + seconds * scale;
        double right = X(c.Duration);

        FormattedText Text(string text, double size, double opacity)
        {
            var brush = TextBrush.Clone();
            brush.Opacity = opacity;
            return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush, dip);
        }

        // the ruler: a tick every so many seconds, as many as fit
        double step = new[] { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 }.FirstOrDefault(s => s * scale >= 56, 600);
        for (double t = 0; t <= c.Duration + 0.001; t += step)
        {
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(line, new Point(x, RulerHeight - 6), new Point(x, RulerHeight + _lanes.Sum(l => l.Height)));
            var label = Text(FormatTime(t), 10.5, 0.6);
            dc.DrawText(label, new Point(Math.Min(x + 3, right - label.Width), 3));
        }
        dc.DrawLine(line, new Point(LabelWidth, RulerHeight - 0.5), new Point(right, RulerHeight - 0.5));

        double top = RulerHeight;
        for (int i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            double height = lane.Height;
            if (i % 2 == 0) dc.DrawRectangle(StripeBrush, null, new Rect(0, top, right, height));
            var label = Text(lane.Name, 12, lane.Events.Count > 0 ? 0.9 : 0.5);
            label.MaxTextWidth = LabelWidth - 12;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(label, new Point(6, top + Math.Min((height - label.Height) / 2, 4)));

            // the rows sit in the middle of the lane
            double first = top + (height - lane.Rows * RowHeight) / 2;
            foreach (var (e, row) in lane.Events)
            {
                var brush = BrushOf(e.Kind);
                double x = X(e.Start);
                Rect box;
                if (row >= 0 && e.Duration * scale >= 3)
                {
                    box = new Rect(x, first + row * RowHeight + (RowHeight - 8) / 2, Math.Min(e.Duration, c.Duration - e.Start) * scale, 8);
                    dc.PushOpacity(0.75);
                    dc.DrawRoundedRectangle(brush, null, box, 3, 3);
                    dc.Pop();
                }
                else
                {
                    box = new Rect(x - 1.5, top + 4, 3, height - 8);
                    dc.DrawRoundedRectangle(brush, null, box, 1.5, 1.5);
                }
                if (ReferenceEquals(e, _selected) || ReferenceEquals(e, _hover))
                {
                    var ring = new Pen(ReferenceEquals(e, _selected) ? AccentBrush : TextBrush, 1.5);
                    var outline = box;
                    outline.Inflate(2, 2);
                    dc.DrawRoundedRectangle(null, ring, outline, 3, 3);
                }
                // a little wider than drawn, so a tick is easy to point at
                var hit = box;
                hit.Inflate(Math.Max(0, 3 - box.Width / 2), 3);
                _hits.Add((hit, e));
            }
            top += height;
        }

        if (_playhead is { } playhead)
        {
            double x = Math.Round(X(Math.Clamp(playhead, 0, c.Duration))) + 0.5;
            var pen = new Pen(AccentBrush, 1.5);
            if (pen.CanFreeze) pen.Freeze(); // the accent brush follows the system's, so it may not freeze
            dc.DrawLine(pen, new Point(x, RulerHeight - 8), new Point(x, top));
            var knob = new StreamGeometry();
            using (var g = knob.Open())
            {
                g.BeginFigure(new Point(x - 5, RulerHeight - 10), true, true);
                g.LineTo(new Point(x + 5, RulerHeight - 10), true, false);
                g.LineTo(new Point(x, RulerHeight - 3), true, false);
            }
            knob.Freeze();
            dc.DrawGeometry(AccentBrush, null, knob);
        }
    }

    /// <summary>The time at a point across the timeline.</summary>
    private double TimeAt(double x) => Math.Clamp((x - LabelWidth) / Scale(ActualWidth), 0, _cutscene?.Duration ?? 0);

    /// <summary>Scrolls an event into view, when the timeline is wider than its scroller.</summary>
    public void Reveal(CutsceneEvent e)
    {
        foreach (var (box, at) in _hits)
            if (ReferenceEquals(at, e))
            {
                var around = box;
                around.Inflate(60, 0);
                BringIntoView(around);
                return;
            }
    }

    /// <summary>"1:05.2" past a minute, "8.5 s" before.</summary>
    public static string FormatTime(double seconds) =>
        seconds >= 60 ? $"{(int)(seconds / 60)}:{seconds % 60:00.#}" : $"{seconds:0.#} s";

    private CutsceneEvent? EventAt(Point p)
    {
        // the last drawn is on top
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Box.Contains(p)) return _hits[i].Event;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_seeking)
        {
            Seeked?.Invoke(this, TimeAt(e.GetPosition(this).X));
            return;
        }
        var at = EventAt(e.GetPosition(this));
        if (ReferenceEquals(at, _hover)) return;
        _hover = at;
        Cursor = at is null ? null : Cursors.Hand;
        ToolTip = at is null ? "" : Describe(at);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (_playhead is not null && point.Y < RulerHeight && point.X >= LabelWidth)
        {
            _seeking = CaptureMouse();
            Seeked?.Invoke(this, TimeAt(point.X));
            e.Handled = true;
            return;
        }
        var at = EventAt(e.GetPosition(this));
        Selected = at;
        EventClicked?.Invoke(this, at);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_seeking) return;
        _seeking = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _seeking = false;
    }

    /// <summary>An event in a line or two, for its tooltip.</summary>
    public static string Describe(CutsceneEvent e)
    {
        string when = e.Duration > 0 ? $"{FormatTime(e.Start)} to {FormatTime(e.End)}" : FormatTime(e.Start);
        string who = e.Actor is null ? "" : $"  ·  {e.Actor}";
        return $"{e.Title}\n{e.Kind}{who}  ·  {when}" + (e.Detail.Length > 0 ? $"\n{e.Detail}" : "");
    }
}
