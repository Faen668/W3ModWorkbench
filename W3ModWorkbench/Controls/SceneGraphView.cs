using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// A scene's flow as a graph, as REDkit's Graph tab draws it: each part a box (coloured by what it is, with its
/// title and a taste of what it says or checks), and a curve from each way out to where it goes, labelled when the
/// way has a name (a choice's option, a condition's yes and no). Click a box to pick the part; the wheel scrolls,
/// Ctrl and the wheel zooms, and the right or middle button drags the graph about.
/// </summary>
public sealed class SceneGraphView : Border
{
    private const double BoxWidth = 210;
    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly Dictionary<SceneNode, Border> _boxes = [];
    private SceneNode? _selected;
    private Point? _dragFrom;

    /// <summary>A part was picked by clicking its box.</summary>
    public event EventHandler<SceneNode>? NodeClicked;

    public SceneGraphView()
    {
        _canvas.LayoutTransform = _zoom;
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _canvas,
        };
        Child = _scroll;
        _scroll.PreviewMouseWheel += Scroll_PreviewMouseWheel;
        _scroll.PreviewMouseDown += Scroll_PreviewMouseDown;
        _scroll.PreviewMouseMove += Scroll_PreviewMouseMove;
        _scroll.PreviewMouseUp += (_, _) => { _dragFrom = null; _scroll.ReleaseMouseCapture(); };
    }

    /// <summary>Draws a scene's flow with its parts at the places given. <paramref name="text"/> puts a line's string into words.</summary>
    public void Show(SceneFile scene, IReadOnlyDictionary<SceneNode, Point> places, Func<uint, string?> text)
    {
        _canvas.Children.Clear();
        _boxes.Clear();
        if (places.Count == 0) return;
        double left = places.Values.Min(p => p.X) - 40, top = places.Values.Min(p => p.Y) - 40;

        foreach (var node in scene.Nodes)
        {
            if (!places.TryGetValue(node, out var place)) continue;
            var box = Box(node, text);
            box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(box, place.X - left);
            Canvas.SetTop(box, place.Y - top);
            Panel.SetZIndex(box, 1);
            _boxes[node] = box;
            _canvas.Children.Add(box);
        }

        // a curve for each way out, from the box's right edge to the left edge of where it goes
        var line = (Brush)FindBrush("TextFillColorTertiaryBrush", Brushes.Gray);
        foreach (var node in scene.Nodes)
        {
            if (!_boxes.TryGetValue(node, out var from)) continue;
            var links = node.AllLinks.Where(l => l.Target is not null && _boxes.ContainsKey(l.Target)).ToList();
            for (int i = 0; i < links.Count; i++)
            {
                var to = _boxes[links[i].Target!];
                // several ways out leave from spread points down the box's edge, so they can be told apart
                double fy = Canvas.GetTop(from) + from.DesiredSize.Height * (i + 1) / (links.Count + 1);
                var a = new Point(Canvas.GetLeft(from) + from.DesiredSize.Width, fy);
                var b = new Point(Canvas.GetLeft(to), Canvas.GetTop(to) + Math.Min(24, to.DesiredSize.Height / 2));
                double bend = Math.Max(60, Math.Abs(b.X - a.X) / 2);
                var figure = new PathFigure { StartPoint = a };
                figure.Segments.Add(new BezierSegment(new Point(a.X + bend, a.Y), new Point(b.X - bend, b.Y), b, true));
                _canvas.Children.Add(new Path { Data = new PathGeometry([figure]), Stroke = line, StrokeThickness = 1.4, IsHitTestVisible = false });
                if (Label(links[i]) is { } label)
                {
                    var tag = new TextBlock { Text = label, FontSize = 10.5, Opacity = 0.85, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
                    Canvas.SetLeft(tag, a.X + 6);
                    Canvas.SetTop(tag, a.Y - 15);
                    Panel.SetZIndex(tag, 2);
                    _canvas.Children.Add(tag);
                }
            }
        }
        _canvas.Width = _boxes.Values.Max(b => Canvas.GetLeft(b) + b.DesiredSize.Width) + 60;
        _canvas.Height = _boxes.Values.Max(b => Canvas.GetTop(b) + b.DesiredSize.Height) + 60;
        if (_selected is not null) Select(_selected, scroll: false);
    }

    /// <summary>Marks a part's box as picked and, when asked, scrolls it into view.</summary>
    public void Select(SceneNode? node, bool scroll = true)
    {
        if (_selected is not null && _boxes.TryGetValue(_selected, out var old)) old.BorderBrush = (Brush)FindBrush("CardStrokeColorDefaultBrush", Brushes.Gray);
        _selected = node;
        if (node is null || !_boxes.TryGetValue(node, out var box)) return;
        box.BorderBrush = (Brush)FindBrush("AccentFillColorDefaultBrush", Brushes.DodgerBlue);
        if (!scroll) return;
        // centred, as far as the scrolling allows
        double x = (Canvas.GetLeft(box) + box.DesiredSize.Width / 2) * _zoom.ScaleX - _scroll.ViewportWidth / 2;
        double y = (Canvas.GetTop(box) + box.DesiredSize.Height / 2) * _zoom.ScaleY - _scroll.ViewportHeight / 2;
        _scroll.ScrollToHorizontalOffset(Math.Max(0, x));
        _scroll.ScrollToVerticalOffset(Math.Max(0, y));
    }

    /// <summary>Zooms to show the whole graph, as far as it can be read.</summary>
    public void FitAll()
    {
        if (_canvas.Width is double.NaN or <= 0 || _scroll.ViewportWidth <= 0) return;
        double scale = Math.Clamp(Math.Min(_scroll.ViewportWidth / _canvas.Width, _scroll.ViewportHeight / _canvas.Height), 0.35, 1);
        _zoom.ScaleX = _zoom.ScaleY = scale;
    }

    private static string? Label(SceneLink link) =>
        link.Label is { Length: > 0 } label && !label.Equals("Out", StringComparison.OrdinalIgnoreCase) && !label.Equals("next", StringComparison.OrdinalIgnoreCase) ? label : null;

    /// <summary>A part's box: a strip of its colour with what it is, its title, and what it says or checks in brief.</summary>
    private Border Box(SceneNode node, Func<uint, string?> text)
    {
        var stack = new StackPanel();
        var head = new Border { Background = SceneNodeItem.BrushOf(node.Kind), CornerRadius = new CornerRadius(5, 5, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        head.Child = new TextBlock { Text = $"{node.Number}  {SceneNodeItem.KindName(node.Kind)}", Foreground = Brushes.Black, FontSize = 11, FontWeight = FontWeights.SemiBold };
        stack.Children.Add(head);
        var body = new StackPanel { Margin = new Thickness(8, 5, 8, 7) };
        body.Children.Add(new TextBlock { Text = node.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        foreach (string row in Summary(node, text).Take(4))
            body.Children.Add(new TextBlock { Text = row, FontSize = 11, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        stack.Children.Add(body);
        var box = new Border
        {
            Width = BoxWidth,
            Child = stack,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            BorderBrush = (Brush)FindBrush("CardStrokeColorDefaultBrush", Brushes.Gray),
            Background = (Brush)FindBrush("SolidBackgroundFillColorTertiaryBrush", Brushes.DimGray),
            Cursor = Cursors.Hand,
            Tag = node,
            ToolTip = node.Detail,
        };
        box.MouseLeftButtonDown += (_, e) =>
        {
            Select(node, scroll: false);
            NodeClicked?.Invoke(this, node);
            e.Handled = true;
        };
        return box;
    }

    /// <summary>What a part says or checks, a few short rows: a section's first lines, a choice's options, a branch's condition.</summary>
    private static IEnumerable<string> Summary(SceneNode node, Func<uint, string?> text)
    {
        if (node.Cutscene is { } cutscene) yield return System.IO.Path.GetFileNameWithoutExtension(cutscene);
        if (node.Kind is not SceneNodeKind.Section && node.Detail is { Length: > 0 } detail) yield return detail;
        foreach (var element in node.Elements)
        {
            if (element.Kind == SceneElementKind.Line && element.StringId is { } id)
                yield return $"{element.Speaker}: {text(id) ?? $"(string {id})"}";
            else if (element.Kind == SceneElementKind.Choice)
                foreach (var option in element.Options)
                    yield return "▸ " + (option.StringId is { } o && text(o) is { } words ? words : option.Label);
        }
    }

    private object FindBrush(string key, Brush fallback) => TryFindResource(key) ?? fallback;

    private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        // zoom about the mouse, keeping the point under it where it is
        var at = e.GetPosition(_canvas);
        double before = _zoom.ScaleX, after = Math.Clamp(before * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.3, 2.5);
        _zoom.ScaleX = _zoom.ScaleY = after;
        _scroll.UpdateLayout();
        var mouse = e.GetPosition(_scroll);
        _scroll.ScrollToHorizontalOffset(at.X * after - mouse.X);
        _scroll.ScrollToVerticalOffset(at.Y * after - mouse.Y);
        e.Handled = true;
    }

    private void Scroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle)) return;
        _dragFrom = e.GetPosition(_scroll);
        _scroll.CaptureMouse();
        e.Handled = true;
    }

    private void Scroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(_scroll);
        _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - (now.X - from.X));
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - (now.Y - from.Y));
        _dragFrom = now;
    }
}
