using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core.Quests;
using W3ModWorkbench.Localisation;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// A quest graph as REDkit's quest editor draws it, tidier: each block a box with a band of its group's colour, its
/// title, a line saying what it does, its inputs down the left and outputs down the right, and a curve from each
/// output to the input it leads to. A phase's inputs and outputs are arrows, a block's comment sits over it, a note is
/// a sticky note, and a cut control's link to the block it stops is a red dashed arrow. Click a block to pick it,
/// Ctrl+click to add it to the ones picked, double-click a phase to open it; drag to move about, the wheel zooms, and
/// double-clicking the background shows it all.
/// </summary>
public sealed class QuestGraphView : FrameworkElement
{
    private const double SocketRow = 17, HeaderHeight = 24, DetailHeight = 17, Pad = 9, ArrowWidth = 34;

    private readonly Dictionary<QuestBlock, Rect> _boxes = [];
    private readonly Dictionary<QuestBlock, QuestBlockItem> _items = [];
    private IReadOnlyDictionary<(QuestBlock From, QuestBlock To), List<(double X, double Y)>> _routes = new Dictionary<(QuestBlock, QuestBlock), List<(double X, double Y)>>();
    private HashSet<QuestBlock> _matches = [];
    private IReadOnlyDictionary<QuestBlock, (Color Color, string Label)> _marks = new Dictionary<QuestBlock, (Color, string)>();
    private readonly HashSet<QuestBlock> _selected = [];
    private QuestBlock? _hover;
    private double _scale = 1;
    private Vector _offset;
    private Point _lastMouse;
    private bool _dragging, _moved;

    public static readonly DependencyProperty TextBrushProperty = Brush(nameof(TextBrush));
    public static readonly DependencyProperty LineBrushProperty = Brush(nameof(LineBrush));
    public static readonly DependencyProperty NodeBrushProperty = Brush(nameof(NodeBrush));
    public static readonly DependencyProperty AccentBrushProperty = Brush(nameof(AccentBrush));
    public static readonly DependencyProperty SubtleBrushProperty = Brush(nameof(SubtleBrush));

    private static DependencyProperty Brush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(QuestGraphView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush NodeBrush { get => (Brush)GetValue(NodeBrushProperty); set => SetValue(NodeBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    /// <summary>The second line of a box and the socket names.</summary>
    public Brush SubtleBrush { get => (Brush)GetValue(SubtleBrushProperty); set => SetValue(SubtleBrushProperty, value); }

    /// <summary>A block was clicked (null for the background), with whether Ctrl was down to add it to the picked ones.</summary>
    public event EventHandler<(QuestBlock? Block, bool Add)>? BlockClicked;
    /// <summary>A phase block was double-clicked: open the phase it runs.</summary>
    public event EventHandler<QuestBlock>? PhaseOpened;

    public QuestGraphView()
    {
        SetResourceReference(TextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(LineBrushProperty, "TextFillColorTertiaryBrush");
        SetResourceReference(SubtleBrushProperty, "TextFillColorSecondaryBrush");
        SetResourceReference(NodeBrushProperty, "SubtleFillColorSecondaryBrush");
        SetResourceReference(AccentBrushProperty, "AccentFillColorDefaultBrush");
        ClipToBounds = true;
        Focusable = true;
        SizeChanged += (_, e) => { if (e.PreviousSize.Width == 0) FitToView(); };
    }

    /// <summary>Whether the graph runs top to bottom: links then leave a block's bottom and meet the next one's top.</summary>
    public bool Downwards { get; set; }

    /// <summary>The blocks picked, the last picked last.</summary>
    public IReadOnlyCollection<QuestBlock> Selected => _selected;

    /// <summary>Picks just these blocks (none to pick nothing).</summary>
    public void Select(IEnumerable<QuestBlock> blocks)
    {
        _selected.Clear();
        foreach (var b in blocks) _selected.Add(b);
        InvalidateVisual();
    }

    /// <summary>A group's colour: REDkit's for the blocks in it, as near as reads on light and dark alike.</summary>
    public static Color ColorOf(QuestCategory category) => category switch
    {
        QuestCategory.Phase => Color.FromRgb(0x9A, 0xA0, 0xA8),
        QuestCategory.PhaseIO => Color.FromRgb(0xE5, 0x48, 0x4D),
        QuestCategory.Flow => Color.FromRgb(0x8D, 0xB8, 0x4A),
        QuestCategory.Logical => Color.FromRgb(0x9B, 0x7B, 0xF0),
        QuestCategory.Scenes => Color.FromRgb(0x4C, 0x8F, 0xD8),
        QuestCategory.Scripting => Color.FromRgb(0xA0, 0x6C, 0xFF),
        QuestCategory.GameSystems => Color.FromRgb(0x2F, 0xB5, 0xC9),
        QuestCategory.Gameplay => Color.FromRgb(0xF2, 0x8A, 0x3C),
        QuestCategory.Journal => Color.FromRgb(0xE3, 0x7F, 0xA3),
        QuestCategory.Camera => Color.FromRgb(0xD9, 0xB8, 0x2E),
        QuestCategory.Notes => Color.FromRgb(0x5E, 0xA8, 0x6B),
        _ => Color.FromRgb(0x8E, 0x8E, 0x93),
    };

    // a cut control's link: REDkit draws it red
    private static readonly Color CutColor = Color.FromRgb(0xE0, 0x3C, 0x3C);

    /// <summary>A block's size as drawn, at full size: wide enough for its title, its line and its sockets' names.</summary>
    public Size BoxSize(QuestBlockItem item)
    {
        var family = TextElement.GetFontFamily(this);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Width(string text, double size, FontWeight weight) =>
            new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal), size, Brushes.Black, dip).Width;
        if (IsArrow(item))
            return new Size(Math.Max(70, Width(item.Title, 12, FontWeights.SemiBold) + ArrowWidth + 22), 34);
        if (item.Category == QuestCategory.Notes)
        {
            double h = HeaderHeight + Math.Min(7, Math.Ceiling(Width(item.Detail, 11, FontWeights.Normal) / 210) + item.Detail.Count(c => c == '\n')) * 15 + Pad;
            return new Size(230, Math.Max(HeaderHeight + 20, h));
        }
        double sockets = (item.Inputs.Count == 0 ? 0 : item.Inputs.Max(s => Width(s, 10.5, FontWeights.Normal)))
                       + (item.Outputs.Count == 0 ? 0 : item.Outputs.Max(s => Width(s, 10.5, FontWeights.Normal))) + 44;
        double w = Math.Max(Width(item.Title, 12, FontWeights.SemiBold) + 2 * Pad + (IsPhase(item) ? 18 : 0), Math.Min(Width(item.Detail, 10.5, FontWeights.Normal) + 2 * Pad, 300));
        w = Math.Clamp(Math.Max(w, sockets), 120, 340);
        int rows = Math.Max(1, Math.Max(item.Inputs.Count, item.Outputs.Count));
        return new Size(Math.Round(w), HeaderHeight + (item.Detail.Length > 0 ? DetailHeight : 0) + rows * SocketRow + 6);
    }

    private static bool IsArrow(QuestBlockItem item) => item.Category == QuestCategory.PhaseIO;
    private static bool IsPhase(QuestBlockItem item) => item.Block.ClassName == "CQuestPhaseBlock";

    /// <summary>Shows a graph's blocks at the places given (each box's top left corner, at full size).</summary>
    public void Show(IEnumerable<QuestBlockItem> items, QuestPlacement placement)
    {
        var places = placement.Places;
        _routes = placement.Routes;
        _boxes.Clear();
        _items.Clear();
        _selected.Clear();
        _matches = [];
        _hover = null;
        foreach (var item in items)
        {
            if (!places.TryGetValue(item.Block, out var at)) continue;
            _items[item.Block] = item;
            var size = BoxSize(item);
            _boxes[item.Block] = new Rect(at.X, at.Y, size.Width, size.Height);
        }
        FitToView();
    }

    /// <summary>Tags blocks with a coloured label at their top right corner, as a comparison marks what a mod changed.</summary>
    public void Mark(IReadOnlyDictionary<QuestBlock, (Color Color, string Label)> marks)
    {
        _marks = marks;
        InvalidateVisual();
    }

    /// <summary>Marks the blocks a search found.</summary>
    public void Highlight(IEnumerable<QuestBlock> blocks)
    {
        _matches = [.. blocks];
        InvalidateVisual();
    }

    public bool Contains(QuestBlock block) => _boxes.ContainsKey(block);

    private const double SmallestFit = 0.45;

    /// <summary>Fits the graph to the view, or, when that would be too small to read, shows its start at a readable size.</summary>
    public void FitToView()
    {
        if (_boxes.Count == 0 || ActualWidth < 10 || ActualHeight < 10) { InvalidateVisual(); return; }
        var all = Bounds();
        all.Inflate(40, 40);
        double fit = Math.Min(ActualWidth / all.Width, ActualHeight / all.Height);
        _scale = Math.Clamp(fit, SmallestFit, 1.2);
        if (fit >= SmallestFit)
            _offset = new Vector(ActualWidth / 2 - (all.X + all.Width / 2) * _scale, ActualHeight / 2 - (all.Y + all.Height / 2) * _scale);
        else
        {
            // the inputs' end, in the middle of the height
            var start = _boxes.Where(b => b.Key.IsPhaseInput || b.Key.ClassName == "CQuestStartBlock").Select(b => b.Value).DefaultIfEmpty(all).Aggregate(Rect.Union);
            _offset = new Vector(40 - all.X * _scale, ActualHeight / 2 - (start.Y + start.Height / 2) * _scale);
        }
        InvalidateVisual();
    }

    private Rect Bounds() => _boxes.Values.Aggregate(Rect.Union);

    /// <summary>Moves the view so a block is in the middle at a readable size, and picks it.</summary>
    public void Reveal(QuestBlock block, bool add = false)
    {
        if (!_boxes.TryGetValue(block, out var rect)) return;
        if (_scale < 0.8) _scale = 1;
        var box = OnScreen(rect);
        _offset += new Vector(ActualWidth / 2 - (box.X + box.Width / 2), ActualHeight / 2 - (box.Y + box.Height / 2));
        if (!add) _selected.Clear();
        _selected.Add(block);
        InvalidateVisual();
    }

    private Rect OnScreen(Rect box) => new(box.X * _scale + _offset.X, box.Y * _scale + _offset.Y, box.Width * _scale, box.Height * _scale);

    // where a socket is on a box, at full size: inputs down the left, outputs down the right
    private Point SocketAt(QuestBlock block, string name, bool output)
    {
        var box = _boxes[block];
        var item = _items[block];
        if (Downwards)
        {
            // along the bottom for outputs and the top for inputs, spread evenly
            var sockets = output ? item.Outputs : item.Inputs;
            int at = Math.Max(0, IndexOf(sockets, QuestBlockItem.Socket(name)));
            double x = box.X + box.Width * (at + 1) / (Math.Max(1, sockets.Count) + 1);
            return new Point(x, output ? box.Bottom : box.Y);
        }
        if (IsArrow(item)) return output ? new Point(box.Right, box.Y + box.Height / 2) : new Point(box.X, box.Y + box.Height / 2);
        if (item.Category == QuestCategory.Notes) return new Point(output ? box.Right : box.X, box.Y + HeaderHeight / 2);
        var list = output ? item.Outputs : item.Inputs;
        int i = Math.Max(0, IndexOf(list, QuestBlockItem.Socket(name)));
        double top = box.Y + HeaderHeight + (item.Detail.Length > 0 ? DetailHeight : 0) + 3;
        return new Point(output ? box.Right : box.X, top + (i + 0.5) * SocketRow);
    }

    private static int IndexOf(IReadOnlyList<string> list, string name)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == name) return i;
        return -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_boxes.Count == 0) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = TextElement.GetFontFamily(this);
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var plain = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var italic = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        var view = new Rect(0, 0, ActualWidth, ActualHeight);
        bool words = _scale >= 0.35;

        FormattedText Text(string text, Typeface face, double size, Brush brush, double maxWidth) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size * _scale, brush, dip)
            { MaxTextWidth = Math.Max(1, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

        // the links first, so the boxes sit on them
        // a theme's brush can be one that can't be frozen (an accent the theme changes), so a pen of it isn't frozen then
        var line = Frozen(new Pen(LineBrush, Math.Max(0.7, 1.3 * _scale)));
        var picked = Frozen(new Pen(AccentBrush, Math.Max(1.2, 2.2 * _scale)));
        var cutBrush = new SolidColorBrush(CutColor);
        cutBrush.Freeze();
        var cut = new Pen(cutBrush, Math.Max(1, 1.8 * _scale)) { DashStyle = new DashStyle([4, 3], 0) };
        cut.Freeze();
        foreach (var (block, _) in _boxes)
            foreach (var link in block.Links)
            {
                if (!_boxes.ContainsKey(link.To)) continue;
                bool isCut = QuestBlock.IsCut(link);
                var from = SocketAt(block, link.Output, true);
                Point to;
                if (isCut)
                {
                    var target = _boxes[link.To];
                    to = new Point(target.X + target.Width / 2, target.Y);
                }
                else to = SocketAt(link.To, link.Input, false);
                var a = Scr(from);
                var b = Scr(to);
                if (!view.IntersectsWith(new Rect(a, b)) && !view.Contains(a) && !view.Contains(b)) continue;
                double reach = Math.Max(40 * _scale, Math.Abs(b.X - a.X) / 2);
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(a, false, false);
                    if (Downwards && !isCut)
                    {
                        // down the page: each stretch leaves going down and arrives coming down
                        var stops = (_routes.TryGetValue((block, link.To), out var down) ? down.Select(p => Scr(new Point(p.X, p.Y))) : []).Append(b).ToList();
                        var at = a;
                        foreach (var p in stops)
                        {
                            double half = Math.Max(30 * _scale, Math.Abs(p.Y - at.Y) / 2);
                            g.BezierTo(new Point(at.X, at.Y + half), new Point(p.X, p.Y - half), p, true, false);
                            at = p;
                        }
                    }
                    else if (isCut) g.BezierTo(new Point(a.X + reach, a.Y), new Point(b.X, b.Y - reach), b, true, false);
                    else if (_routes.TryGetValue((block, link.To), out var route) && route.Count > 0)
                    {
                        // a long link goes through the places it holds in the columns it crosses
                        var at = a;
                        foreach (var p in route.Select(p => Scr(new Point(p.X, p.Y))).Append(b))
                        {
                            double half = Math.Max(20 * _scale, (p.X - at.X) / 2);
                            g.BezierTo(new Point(at.X + half, at.Y), new Point(p.X - half, p.Y), p, true, false);
                            at = p;
                        }
                    }
                    else if (b.X < a.X - 40 * _scale)
                    {
                        // a link back to an earlier block loops out, under both blocks and back in, rather than across the graph
                        var fromBox = OnScreen(_boxes[block]);
                        var toBox = OnScreen(_boxes[link.To]);
                        double under = Math.Max(fromBox.Bottom, toBox.Bottom) + 18 * _scale, r = 16 * _scale;
                        g.BezierTo(new Point(a.X + 2 * r, a.Y), new Point(a.X + 2 * r, under), new Point(a.X, under), true, false);
                        g.LineTo(new Point(b.X, under), true, false);
                        g.BezierTo(new Point(b.X - 2 * r, under), new Point(b.X - 2 * r, b.Y), b, true, false);
                    }
                    else g.BezierTo(new Point(a.X + reach, a.Y), new Point(b.X - reach, b.Y), b, true, false);
                }
                geometry.Freeze();
                bool hot = _selected.Contains(block) || _selected.Contains(link.To);
                dc.DrawGeometry(null, isCut ? cut : hot ? picked : line, geometry);
                if (isCut) Arrowhead(dc, b, new Vector(0, 1), cutBrush);
            }

        foreach (var (block, rect) in _boxes)
        {
            var box = OnScreen(rect);
            var item = _items[block];
            // the comment over a box reaches above it
            if (!view.IntersectsWith(new Rect(box.X, box.Y - 18 * _scale, box.Width, box.Height + 18 * _scale))) continue;
            var color = new SolidColorBrush(ColorOf(item.Category));
            color.Freeze();
            bool selected = _selected.Contains(block), match = _matches.Contains(block), hover = ReferenceEquals(block, _hover);
            var border = new Pen(selected || match ? AccentBrush : hover ? TextBrush : color, selected ? 2.6 : match ? 2 : 1.1);

            if (IsArrow(item))
            {
                // a phase's input or output: an arrow pointing the way the flow goes, its socket's name in it
                var arrow = new StreamGeometry();
                using (var g = arrow.Open())
                {
                    double tip = Math.Min(ArrowWidth * _scale, box.Width / 2);
                    g.BeginFigure(box.TopLeft, true, true);
                    g.LineTo(new Point(box.Right - tip, box.Top), true, true);
                    g.LineTo(new Point(box.Right, box.Top + box.Height / 2), true, true);
                    g.LineTo(new Point(box.Right - tip, box.Bottom), true, true);
                    g.LineTo(box.BottomLeft, true, true);
                }
                arrow.Freeze();
                dc.DrawGeometry(color, border, arrow);
                if (words)
                {
                    var t = Text(item.Title, bold, 12, Brushes.White, box.Width - ArrowWidth * _scale - 8 * _scale);
                    dc.DrawText(t, new Point(box.X + 9 * _scale, box.Y + box.Height / 2 - t.Height / 2));
                }
            }
            else if (item.Category == QuestCategory.Notes)
            {
                dc.DrawRoundedRectangle(NodeBrush, border, box, 4 * _scale, 4 * _scale);
                dc.DrawRectangle(color, null, new Rect(box.X, box.Y, box.Width, HeaderHeight * _scale));
                if (words)
                {
                    dc.DrawText(Text(item.Title, bold, 12, Brushes.White, box.Width - 2 * Pad * _scale), new Point(box.X + Pad * _scale, box.Y + 4 * _scale));
                    var body = new FormattedText(item.Detail, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 11 * _scale, TextBrush, dip)
                    { MaxTextWidth = Math.Max(1, box.Width - 2 * Pad * _scale), MaxTextHeight = Math.Max(1, box.Height - (HeaderHeight + 6) * _scale), Trimming = TextTrimming.WordEllipsis };
                    dc.DrawText(body, new Point(box.X + Pad * _scale, box.Y + (HeaderHeight + 3) * _scale));
                }
            }
            else
            {
                dc.DrawRoundedRectangle(NodeBrush, border, box, 6 * _scale, 6 * _scale);
                // the group's colour as a band along the top
                dc.DrawRectangle(color, null, new Rect(box.X + 6 * _scale, box.Y, Math.Max(1, box.Width - 12 * _scale), Math.Max(1, 3 * _scale)));
                if (words)
                {
                    double y = box.Y + 5 * _scale;
                    var title = Text(item.Title, bold, 12, TextBrush, box.Width - (2 * Pad + (IsPhase(item) ? 18 : 0)) * _scale);
                    dc.DrawText(title, new Point(box.X + Pad * _scale, y));
                    if (IsPhase(item))
                    {
                        // an external phase is a file of its own (REDkit's padlock); an embedded one is a graph inside
                        var glyph = new FormattedText(block.ExternalPhase is null ? "" : "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            new Typeface((FontFamily)FindResource("IconFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11 * _scale, color, dip);
                        dc.DrawText(glyph, new Point(box.Right - (Pad + 12) * _scale, y + 2 * _scale));
                    }
                    y = box.Y + HeaderHeight * _scale;
                    if (item.Detail.Length > 0)
                    {
                        dc.DrawText(Text(item.Detail, plain, 10.5, SubtleBrush, box.Width - 2 * Pad * _scale), new Point(box.X + Pad * _scale, y - 2 * _scale));
                        y += DetailHeight * _scale;
                    }
                    y += 3 * _scale;
                    for (int i = 0; i < item.Inputs.Count; i++)
                        dc.DrawText(Text(item.Inputs[i], plain, 10.5, SubtleBrush, box.Width / 2 - Pad * _scale), new Point(box.X + Pad * _scale, y + i * SocketRow * _scale + 1 * _scale));
                    for (int i = 0; i < item.Outputs.Count; i++)
                    {
                        var t = Text(item.Outputs[i], plain, 10.5, SubtleBrush, box.Width / 2 - Pad * _scale);
                        dc.DrawText(t, new Point(box.Right - Pad * _scale - t.Width, y + i * SocketRow * _scale + 1 * _scale));
                    }
                }
                // the socket ends, where the links meet the box
                double end = Math.Max(1.5, 3 * _scale);
                foreach (string input in item.Inputs) dc.DrawRectangle(LineBrush, null, Square(Scr(SocketAt(block, input, false)), end));
                foreach (string output in item.Outputs) dc.DrawRectangle(LineBrush, null, Square(Scr(SocketAt(block, output, true)), end));
            }

            // a comparison's tag
            if (words && _marks.TryGetValue(block, out var mark))
            {
                var tagBrush = new SolidColorBrush(mark.Color);
                tagBrush.Freeze();
                var tag = new FormattedText(mark.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold, 9.5 * _scale, Brushes.White, dip);
                var pill = new Rect(box.Right - tag.Width - 14 * _scale, box.Y - tag.Height / 2 - 2 * _scale, tag.Width + 10 * _scale, tag.Height + 3 * _scale);
                dc.DrawRoundedRectangle(tagBrush, null, pill, pill.Height / 2, pill.Height / 2);
                dc.DrawText(tag, new Point(pill.X + 5 * _scale, pill.Y + 1.5 * _scale));
                dc.DrawRoundedRectangle(null, new Pen(tagBrush, 1.6), box, 6 * _scale, 6 * _scale);
            }

            // the comment over it, as REDkit puts it
            if (words && item.Comment.Length > 0 && item.Category != QuestCategory.Notes)
            {
                var note = Text(item.Comment, italic, 11, AccentBrush, Math.Max(box.Width, 220 * _scale));
                dc.DrawText(note, new Point(box.X + 2 * _scale, box.Y - note.Height - 2 * _scale));
            }
        }
    }

    private static Pen Frozen(Pen pen)
    {
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private Point Scr(Point p) => new(p.X * _scale + _offset.X, p.Y * _scale + _offset.Y);
    private static Rect Square(Point p, double half) => new(p.X - half, p.Y - half, half * 2, half * 2);

    private void Arrowhead(DrawingContext dc, Point tip, Vector way, Brush brush)
    {
        way.Normalize();
        var side = new Vector(-way.Y, way.X);
        double l = 9 * _scale, w = 5 * _scale;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(tip, true, true);
            c.LineTo(tip - way * l + side * w, true, false);
            c.LineTo(tip - way * l - side * w, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    private QuestBlock? BlockAt(Point p)
    {
        // the last drawn is on top
        QuestBlock? found = null;
        foreach (var (block, box) in _boxes)
            if (OnScreen(box).Contains(p)) found = block;
        return found;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            if (BlockAt(e.GetPosition(this)) is { ClassName: "CQuestPhaseBlock" } phase) PhaseOpened?.Invoke(this, phase);
            else if (BlockAt(e.GetPosition(this)) is null) FitToView();
            return;
        }
        _lastMouse = e.GetPosition(this);
        _dragging = CaptureMouse();
        _moved = false;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var now = e.GetPosition(this);
        if (_dragging)
        {
            var delta = now - _lastMouse;
            if (!_moved && delta.Length < 3) return;
            _moved = true;
            _offset += delta;
            _lastMouse = now;
            InvalidateVisual();
            return;
        }
        var at = BlockAt(now);
        if (ReferenceEquals(at, _hover)) return;
        _hover = at;
        Cursor = at is null ? null : Cursors.Hand;
        ToolTip = at is null ? null : Tip(_items[at]);
        InvalidateVisual();
    }

    private static string Tip(QuestBlockItem item)
    {
        var lines = new List<string> { item.Title, $"{item.Kind} · {item.ClassName}" };
        if (item.Detail.Length > 0 && item.Category != QuestCategory.Notes) lines.Add(item.Detail);
        if (item.Block.Guid is { } g) lines.Add(g.ToString());
        if (item.Comment.Length > 0) lines.Add(item.Comment);
        if (item.Block.ClassName == "CQuestPhaseBlock") lines.Add(L.T("QuestGraph.DoubleClickOpen"));
        return string.Join("\n", lines);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_moved) return;
        var at = BlockAt(e.GetPosition(this));
        bool add = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (at is null) { if (!add) _selected.Clear(); }
        else if (add) { if (!_selected.Remove(at)) _selected.Add(at); }
        else { _selected.Clear(); _selected.Add(at); }
        InvalidateVisual();
        BlockClicked?.Invoke(this, (at, add));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var at = e.GetPosition(this);
        double before = _scale;
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.05, 3);
        _offset = (Vector)at - ((Vector)at - _offset) * (_scale / before);
        InvalidateVisual();
        e.Handled = true;
    }
}
