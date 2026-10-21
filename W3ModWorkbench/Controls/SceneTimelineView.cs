using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// A scene section's timeline, as REDkit's Timeline tab lays it out: its lines and pauses on the top track (each line
/// as long as its voice, coloured by who says it, with its words), the camera's events and the lights and fades under
/// it, then a group for each actor (body animations with their blend in and out, face animations, where they look,
/// and anything else of theirs). Events that overlap on a track go one under another. A ruler of seconds runs above;
/// clicking it moves the playhead, and clicking an event picks it.
/// </summary>
public sealed class SceneTimelineView : FrameworkElement
{
    private const double RulerHeight = 22, RowHeight = 18, TrackPad = 4, LabelWidth = 150, RightPad = 12;

    private SceneSectionTimeline? _timeline;
    private Func<uint, string?> _text = _ => null;
    private IReadOnlyDictionary<string, Brush> _speakers = new Dictionary<string, Brush>();
    private readonly List<Track> _tracks = [];
    private readonly List<(Rect Box, object Item)> _hits = [];
    private object? _hover, _selected;
    private double? _playhead;
    private bool _seeking;

    /// <summary>A track: its name, the group it's in (an actor's, or none), and its items with the row each sits in.</summary>
    private sealed record Track(string Name, string? Group, List<(object Item, double Start, double Duration, int Row)> Items, int Rows)
    {
        public double Height => Math.Max(1, Rows) * RowHeight + TrackPad * 2;
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(SceneTimelineView),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(SceneTimelineView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StripeBrushProperty = DependencyProperty.Register(nameof(StripeBrush), typeof(Brush), typeof(SceneTimelineView),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(SceneTimelineView),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush StripeBrush { get => (Brush)GetValue(StripeBrushProperty); set => SetValue(StripeBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>Raised when an event or element is clicked, with it (a <see cref="SceneTimelineEvent"/> or <see cref="SceneTimelineElement"/>), or null.</summary>
    public event EventHandler<object?>? ItemClicked;

    /// <summary>Raised while the ruler is clicked or dragged, with the time under the mouse.</summary>
    public event EventHandler<double>? Seeked;

    /// <summary>How wide a second is; 0 fits the whole section to the width.</summary>
    public double PixelsPerSecond { get; set; }

    public SceneTimelineView()
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

    /// <summary>The time shown by the playhead, or null for none.</summary>
    public double? Playhead
    {
        get => _playhead;
        set { _playhead = value; InvalidateVisual(); }
    }

    public SceneSectionTimeline? Timeline => _timeline;

    /// <summary>Shows a section's timeline. <paramref name="text"/> puts a line's string into words; <paramref name="speakers"/> colours each speaker.</summary>
    public void Show(SceneSectionTimeline? timeline, Func<uint, string?> text, IReadOnlyDictionary<string, Brush> speakers)
    {
        _timeline = timeline;
        _text = text;
        _speakers = speakers;
        _selected = _hover = null;
        _playhead = timeline is null ? null : 0;
        Build();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Build()
    {
        _tracks.Clear();
        if (_timeline is not { } t) return;
        Add(L.T("SceneTimelineView.Lines"), null, t.Elements.Where(e => e.Duration > 0).Select(e => ((object)e, (double)e.Start, (double)e.Duration)));
        Add(L.T("SceneTimelineView.Camera"), null, t.Events.Where(e => e.Kind == SceneEventKind.Camera).Select(Item));
        Add(L.T("SceneTimelineView.LightsFades"), null, t.Events.Where(e => e.Kind is SceneEventKind.Light or SceneEventKind.Fade).Select(Item));
        foreach (string actor in t.Actors)
        {
            var theirs = t.Events.Where(e => e.Actor == actor).ToList();
            Add(L.T("SceneTimelineView.Body"), actor, theirs.Where(e => e.Kind == SceneEventKind.Animation).Select(Item));
            Add(L.T("SceneTimelineView.Face"), actor, theirs.Where(e => e.Kind == SceneEventKind.Mimics).Select(Item));
            Add(L.T("SceneTimelineView.Looks"), actor, theirs.Where(e => e.Kind == SceneEventKind.LookAt).Select(Item));
            Add(L.T("SceneTimelineView.Other"), actor, theirs.Where(e => e.Kind is not (SceneEventKind.Animation or SceneEventKind.Mimics or SceneEventKind.LookAt or SceneEventKind.Camera or SceneEventKind.Light or SceneEventKind.Fade)).Select(Item));
        }
        Add(L.T("SceneTimelineView.Other"), null, t.Events.Where(e => e.Actor is null or "" && e.Kind is not (SceneEventKind.Camera or SceneEventKind.Light or SceneEventKind.Fade)).Select(Item));
    }

    private static (object, double, double) Item(SceneTimelineEvent e) => (e, e.Start, e.Duration);

    /// <summary>Adds a track when it has anything, its items in rows so that none covers another.</summary>
    private void Add(string name, string? group, IEnumerable<(object Item, double Start, double Duration)> items)
    {
        var list = items.OrderBy(i => i.Start).ToList();
        if (list.Count == 0) return;
        var rowEnds = new List<double>();
        var placed = new List<(object, double, double, int)>();
        foreach (var (item, start, duration) in list)
        {
            // a tick takes a little room of its own, so ticks at the same moment don't sit on one another
            double end = start + Math.Max(duration, 0.15);
            int row = rowEnds.FindIndex(e => e <= start + 1e-4);
            if (row < 0) { row = rowEnds.Count; rowEnds.Add(end); }
            else rowEnds[row] = end;
            placed.Add((item, start, duration, row));
        }
        _tracks.Add(new Track(name, group, placed, rowEnds.Count));
    }

    private double Duration => Math.Max(0.5, _timeline?.Duration ?? 0);

    private double Scale(double width) => PixelsPerSecond > 0 ? PixelsPerSecond : Math.Max(4, (width - LabelWidth - RightPad) / Duration);

    protected override Size MeasureOverride(Size available)
    {
        double height = RulerHeight + _tracks.Sum(t => t.Height) + GroupCount() * 20 + 6;
        double width = PixelsPerSecond > 0 ? LabelWidth + Duration * PixelsPerSecond + RightPad
            : double.IsInfinity(available.Width) ? LabelWidth + Duration * 80 + RightPad : available.Width;
        return new Size(width, height);
    }

    private int GroupCount() => _tracks.Select(t => t.Group).Where(g => g is not null).Distinct().Count();

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_timeline is null) return;
        double scale = Scale(ActualWidth);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var line = new Pen(LineBrush, 1);
        line.Freeze();
        double X(double seconds) => LabelWidth + seconds * scale;
        double right = X(Duration);
        FormattedText Text(string text, double size, double opacity, Typeface? face = null, Brush? brush = null)
        {
            var b = (brush ?? TextBrush).CloneCurrentValue();
            b.Opacity = opacity;
            return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face ?? typeface, size, b, dip);
        }

        double total = RulerHeight + _tracks.Sum(t => t.Height) + GroupCount() * 20;
        double step = new[] { 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60 }.FirstOrDefault(s => s * scale >= 56, 120);
        for (double s = 0; s <= Duration + 1e-3; s += step)
        {
            double x = Math.Round(X(s)) + 0.5;
            dc.DrawLine(line, new Point(x, RulerHeight - 6), new Point(x, total));
            var label = Text($"{s:0.##} s", 10.5, 0.6);
            dc.DrawText(label, new Point(Math.Min(x + 3, right - label.Width), 3));
        }
        dc.DrawLine(line, new Point(LabelWidth, RulerHeight - 0.5), new Point(right, RulerHeight - 0.5));

        double top = RulerHeight;
        string? group = null;
        int stripe = 0;
        foreach (var track in _tracks)
        {
            // an actor's group gets a heading strip
            if (track.Group is { } g && g != group)
            {
                group = g;
                var heading = Text(g, 12, 0.95, bold);
                dc.DrawRectangle(Faded(AccentBrush, 0.18), null, new Rect(0, top, right, 20));
                dc.DrawText(heading, new Point(8, top + (20 - heading.Height) / 2));
                top += 20;
            }
            double height = track.Height;
            if (stripe++ % 2 == 0) dc.DrawRectangle(StripeBrush, null, new Rect(0, top, right, height));
            var name = Text(track.Name, 12, 0.85);
            name.MaxTextWidth = LabelWidth - (track.Group is null ? 12 : 24);
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(track.Group is null ? 8 : 20, top + TrackPad + (RowHeight - name.Height) / 2));

            for (int index = 0; index < track.Items.Count; index++)
            {
                var (item, start, duration, row) = track.Items[index];
                double y = top + TrackPad + row * RowHeight;
                bool picked = ReferenceEquals(item, _selected), hovered = ReferenceEquals(item, _hover);
                var (fill, label) = Look(item);
                if (duration > 0.01)
                {
                    var box = new Rect(X(start), y + 1, Math.Max(3, duration * scale), RowHeight - 2);
                    dc.DrawRoundedRectangle(Faded(fill, picked ? 1 : hovered ? 0.85 : 0.65), picked ? new Pen(TextBrush, 1.5) : null, box, 3, 3);
                    // an animation's blend in and out, as REDkit draws them: a slope at each end
                    if (item is SceneTimelineEvent { Kind: SceneEventKind.Animation } anim)
                    {
                        var ramp = new Pen(Faded(Brushes.Black, 0.45), 1);
                        double blendIn = Math.Min(anim.Source.Float("blendIn"), duration / 2) * scale, blendOut = Math.Min(anim.Source.Float("blendOut"), duration / 2) * scale;
                        if (blendIn > 2) dc.DrawLine(ramp, box.BottomLeft, new Point(box.Left + blendIn, box.Top));
                        if (blendOut > 2) dc.DrawLine(ramp, new Point(box.Right - blendOut, box.Top), box.BottomRight);
                    }
                    var text = Text(label, 11, 1, null, Brushes.Black);
                    text.MaxTextWidth = Math.Max(1, box.Width - 8);
                    text.MaxLineCount = 1;
                    text.Trimming = TextTrimming.CharacterEllipsis;
                    if (box.Width > 18) dc.DrawText(text, new Point(box.Left + 4, box.Top + (box.Height - text.Height) / 2));
                    _hits.Add((box, item));
                }
                else
                {
                    // a moment: a diamond, and its name beside it when there's room
                    double x = X(start), cy = y + RowHeight / 2;
                    var diamond = new StreamGeometry();
                    using (var g2 = diamond.Open())
                    {
                        g2.BeginFigure(new Point(x, cy - 6), true, true);
                        g2.PolyLineTo([new Point(x + 5, cy), new Point(x, cy + 6), new Point(x - 5, cy)], true, false);
                    }
                    dc.DrawGeometry(Faded(fill, picked || hovered ? 1 : 0.85), picked ? new Pen(TextBrush, 1.2) : null, diamond);
                    // its name runs up to the next item in its row, and is left out when there's no room for it
                    double room = 140;
                    for (int next = index + 1; next < track.Items.Count; next++)
                        if (track.Items[next].Row == row) { room = Math.Min(room, X(track.Items[next].Start) - x - 16); break; }
                    room = Math.Min(room, right - x - 8);
                    double width = 0;
                    if (room >= 24)
                    {
                        var text = Text(label, 10.5, 0.8);
                        text.MaxTextWidth = room;
                        text.MaxLineCount = 1;
                        text.Trimming = TextTrimming.CharacterEllipsis;
                        dc.DrawText(text, new Point(x + 8, cy - text.Height / 2));
                        width = Math.Min(text.Width, room);
                    }
                    _hits.Add((new Rect(x - 7, y, 14 + width, RowHeight), item));
                }
            }
            top += height;
        }

        if (_playhead is { } at)
        {
            double x = Math.Round(X(Math.Clamp(at, 0, Duration))) + 0.5;
            var pen = new Pen(AccentBrush, 2);
            dc.DrawLine(pen, new Point(x, 0), new Point(x, total));
        }
    }

    /// <summary>An item's colour and label: a line in its speaker's colour with its words, an event by what it does.</summary>
    private (Brush Fill, string Label) Look(object item) => item switch
    {
        SceneTimelineElement { Kind: SceneElementKind.Line } l => (l.Speaker is { } s && _speakers.TryGetValue(s, out var b) ? b : Brushes.SteelBlue,
            $"{l.Speaker}: {(l.StringId is { } id ? _text(id) ?? L.T("SceneTimelineView.String", id) : "")}"),
        SceneTimelineElement { Kind: SceneElementKind.Pause } p => (Brushes.Gray, L.T("SceneTimelineView.Pause", p.Duration)),
        SceneTimelineElement { Kind: SceneElementKind.Choice } => (Brushes.Goldenrod, L.T("SceneTimelineView.Choice")),
        SceneTimelineElement { Kind: SceneElementKind.Cutscene } c => (Brushes.MediumTurquoise, L.T("SceneTimelineView.CutsceneOwnEventsCutscene", c.Duration)),
        SceneTimelineElement e => (Brushes.Gray, e.Kind.ToString()),
        SceneTimelineEvent e => (KindBrush(e.Kind), Label(e)),
        _ => (Brushes.Gray, ""),
    };

    private static string Label(SceneTimelineEvent e) => e.Name;

    private static Brush KindBrush(SceneEventKind kind) => kind switch
    {
        SceneEventKind.Camera => Brushes.MediumPurple,
        SceneEventKind.Animation => Brushes.CornflowerBlue,
        SceneEventKind.Mimics => Brushes.LightSalmon,
        SceneEventKind.LookAt => Brushes.MediumAquamarine,
        SceneEventKind.Light => Brushes.Khaki,
        SceneEventKind.Fade => Brushes.LightGray,
        SceneEventKind.Placement => Brushes.Tan,
        SceneEventKind.Sound => Brushes.MediumSeaGreen,
        _ => Brushes.Silver,
    };

    private static Brush Faded(Brush brush, double opacity)
    {
        // the theme's brushes follow the system's accent, so they're copied as they are now to be frozen
        var b = brush.CloneCurrentValue();
        b.Opacity = opacity;
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    private double TimeAt(double x) => Math.Clamp((x - LabelWidth) / Scale(ActualWidth), 0, Duration);

    private object? ItemAt(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Box.Contains(p)) return _hits[i].Item;
        return null;
    }

    /// <summary>An item in a line or two, for its tooltip.</summary>
    private string Describe(object item) => item switch
    {
        SceneTimelineElement e => $"{Look(e).Label}\n{e.Start:0.00} s to {e.Start + e.Duration:0.00} s",
        SceneTimelineEvent e => $"{Label(e)}{(e.Actor is { Length: > 0 } a ? $" ({a})" : "")}\n{CutsceneTimeline_Humanize(e.ClassName)} at {e.Start:0.00} s"
            + (e.Duration > 0 ? L.T("SceneTimelineView.ForS", e.Duration) : ""),
        _ => "",
    };

    private static string CutsceneTimeline_Humanize(string className) =>
        System.Text.RegularExpressions.Regex.Replace(className.Replace("CStorySceneEvent", "").Replace("CStoryScene", ""), "(?<=[a-z])(?=[A-Z])", " ");

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_seeking)
        {
            Seeked?.Invoke(this, TimeAt(e.GetPosition(this).X));
            return;
        }
        var at = ItemAt(e.GetPosition(this));
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
        if (point.Y < RulerHeight && point.X >= LabelWidth && _timeline is not null)
        {
            _seeking = CaptureMouse();
            Seeked?.Invoke(this, TimeAt(point.X));
            e.Handled = true;
            return;
        }
        _selected = ItemAt(point);
        ItemClicked?.Invoke(this, _selected);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_seeking) return;
        _seeking = false;
        ReleaseMouseCapture();
    }
}
