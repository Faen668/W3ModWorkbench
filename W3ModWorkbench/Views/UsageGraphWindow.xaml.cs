using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// A picture of how a function, variable or type is used: the thing itself on the right, what uses it to its
/// left, what uses those further left, and so on. Boxes open the place they stand for.
/// </summary>
public partial class UsageGraphWindow : Window
{
    private const double NodeWidth = 270, NodeHeight = 70, MinColumnGap = 96, RowGap = 14, Margin_ = 24;

    private readonly PreviewWindow _host;
    private readonly PreviewDocument _from;
    private IReadOnlyList<ScriptIndex> _layers;
    private IReadOnlyList<ScriptSymbol> _targets;
    private ParsedScript _parsed;
    private CancellationTokenSource? _run;
    private UsageGraph? _graph;
    private bool _ready;
    private double _columnGap = MinColumnGap;

    private readonly Dictionary<UsageNode, Border> _boxes = [];
    private readonly Dictionary<UsageEdge, (Path Line, Polygon Arrow, Border Label)> _lines = [];
    private UsageNode? _selected;
    private Point? _panFrom;
    private Point _panStart;
    private bool _panned;

    /// <summary>
    /// Empty room around the graph, so there is always something to scroll: without it a graph that fits the
    /// window couldn't be dragged anywhere.
    /// </summary>
    private const double PanPad = 600;
    private MouseButton _panButton;

    private UsageGraphWindow(PreviewWindow host, PreviewDocument from, IReadOnlyList<ScriptSymbol> targets, ParsedScript parsed,
        IReadOnlyList<ScriptIndex> layers)
    {
        InitializeComponent();
        _host = host;
        _from = from;
        _targets = targets;
        _parsed = parsed;
        _layers = layers;
        for (int i = 1; i <= 6; i++) LevelsBox.Items.Add(i);
        LevelsBox.SelectedItem = 3;
        _ready = true;
        Loaded += (_, _) => Refresh();
    }

    /// <summary>Opens the graph for a declaration, drawn from the scripts in <paramref name="layers"/>.</summary>
    public static void Open(PreviewWindow host, PreviewDocument from, IReadOnlyList<ScriptSymbol> targets, ParsedScript parsed,
        IReadOnlyList<ScriptIndex> layers) =>
        new UsageGraphWindow(host, from, targets, parsed, layers).Standalone(host).Show();

    private int Levels => LevelsBox.SelectedItem is int n ? n : 3;

    // ---------------------------------------------------------------- building

    private async void Refresh()
    {
        _run?.Cancel();
        var cancel = _run = new CancellationTokenSource();
        var targets = _targets;
        var parsed = _parsed;
        int levels = Levels;
        string name = targets[0].Title;
        TitleText.Text = L.T("UsageGraphWindow.Usage", name);
        Title = L.T("UsageGraphWindow.UsageGraph2", name);
        _columnGap = MinColumnGap;
        Show(null);
        BusyText.Text = L.T("UsageGraphWindow.LookingThroughEveryScript");
        BusyText.Visibility = Visibility.Visible;
        StatusText.Text = "";
        var progress = new Progress<string>(text =>
        {
            if (!cancel.IsCancellationRequested) BusyText.Text = text;
        });
        try
        {
            var graph = await Task.Run(() => UsageGraphBuilder.Build(targets, parsed, _layers, levels, cancel.Token, ((IProgress<string>)progress).Report), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            BusyText.Visibility = Visibility.Collapsed;
            Show(graph);
        }
        catch (OperationCanceledException)
        {
            // a newer graph took over
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            BusyText.Text = L.T("UsageGraphWindow.GraphCouldntBuilt", ex.Message);
        }
    }

    // ---------------------------------------------------------------- drawing

    private void Show(UsageGraph? graph, bool keepView = false)
    {
        _graph = graph;
        _selected = null;
        Surface.Children.Clear();
        _boxes.Clear();
        _lines.Clear();
        Surface.Width = Surface.Height = 0;
        if (graph is null) return;

        var at = Layout(graph, _columnGap);
        double right = at.Values.Max(p => p.X) + NodeWidth + Margin_, bottom = at.Values.Max(p => p.Y) + NodeHeight + Margin_;
        Surface.Width = right;
        Surface.Height = bottom;

        // lines first, so the boxes sit on top of them
        foreach (var edge in graph.Edges)
        {
            var from = at[edge.From];
            var to = at[edge.To];
            var start = new Point(from.X + NodeWidth, from.Y + NodeHeight / 2);
            var end = new Point(to.X, to.Y + NodeHeight / 2);
            double reach = end.X > start.X + 40 ? (end.X - start.X) / 2 : 70;
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new BezierSegment(new Point(start.X + reach, start.Y), new Point(end.X - reach, end.Y), end, true));
            var line = new Path { Data = new PathGeometry([figure]), StrokeThickness = 1.4, IsHitTestVisible = false };
            var arrow = new Polygon
            {
                Points = [new Point(end.X, end.Y), new Point(end.X - 9, end.Y - 4.5), new Point(end.X - 9, end.Y + 4.5)],
                IsHitTestVisible = true,
            };
            Surface.Children.Add(line);
            Surface.Children.Add(arrow);

            string text = edge.Sites.Count == 1 ? L.T("UsageGraphWindow.Line", edge.Sites[0].Line) : L.T("UsageGraphWindow.Calls", edge.Sites.Count);
            var label = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = text, FontSize = 10.5 },
                ToolTip = SitesTip(edge),
            };
            label.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
            label.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            label.MouseLeftButtonUp += (_, e) =>
            {
                if (!_panned && edge.Sites.Count > 0) OpenAt(edge.Sites[0].File, edge.Sites[0].Offset, edge.Sites[0].Length, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
            Canvas.SetLeft(label, mid.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, mid.Y - label.DesiredSize.Height / 2);
            Surface.Children.Add(label);
            _lines[edge] = (line, arrow, label);
        }
        foreach (var node in graph.Nodes)
        {
            var box = Box(node);
            Canvas.SetLeft(box, at[node].X);
            Canvas.SetTop(box, at[node].Y);
            Surface.Children.Add(box);
            _boxes[node] = box;
        }
        Select(graph.Target);

        int functions = graph.Nodes.Count - 1;
        int calls = graph.Edges.Sum(e => e.Sites.Count);
        StatusText.Text = functions == 0
            ? L.T("UsageGraphWindow.NothingScriptsSearchedUses", graph.Target.Title)
            : L.T("UsageGraphWindow.DragLeftRightButton", functions, (functions == 1 ? L.T("UsageGraphWindow.Caller") : L.T("UsageGraphWindow.Callers")), calls, (calls == 1 ? L.T("UsageGraphWindow.Use") : L.T("UsageGraphWindow.Uses")), (graph.Truncated ? L.T("UsageGraphWindow.StoppedBoxesTryFewer", UsageGraphBuilder.MaxNodes) : ""), (graph.Nodes.Any(n => n.MoreCallers > 0) ? L.T("UsageGraphWindow.BoxesMarkedNMore") : ""));
        if (!keepView) Dispatcher.BeginInvoke(() => FitView(onlyIfLarger: true), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The most boxes in one column; a longer one wraps into more columns side by side.</summary>
    private const int MaxPerColumn = 12;

    /// <summary>How many columns the last layout used, the target's included.</summary>
    private int _columns = 1;

    /// <summary>
    /// A column for each step from the target, the target on the right. Each box is as high as the boxes it
    /// calls, pushed down as far as needed so none overlap. A step with many boxes wraps into several columns.
    /// </summary>
    private Dictionary<UsageNode, Point> Layout(UsageGraph graph, double columnGap)
    {
        int deepest = graph.Nodes.Max(n => n.Depth);
        var callees = graph.Edges.ToLookup(e => e.From, e => e.To);
        var y = new Dictionary<UsageNode, double> { [graph.Target] = 0 };
        var unit = new Dictionary<UsageNode, int> { [graph.Target] = 0 };
        int nextUnit = 1;
        for (int depth = 1; depth <= deepest; depth++)
        {
            var column = graph.Nodes.Where(n => n.Depth == depth)
                .Select(n => (Node: n, Want: callees[n].Where(c => y.ContainsKey(c)).Select(c => y[c]).DefaultIfEmpty(0).Average()))
                .OrderBy(n => n.Want).ToList();
            if (column.Count == 0) continue;
            int parts = (column.Count + MaxPerColumn - 1) / MaxPerColumn;
            int size = (column.Count + parts - 1) / parts;
            for (int part = 0; part < parts; part++)
            {
                var chunk = column.Skip(part * size).Take(size).ToList();
                double floor = double.NegativeInfinity;
                foreach (var (node, want) in chunk)
                {
                    double place = Math.Max(want, floor);
                    y[node] = place;
                    unit[node] = nextUnit;
                    floor = place + NodeHeight + RowGap;
                }
                // recentre on what it feeds, so a long column doesn't run off the bottom
                double shift = chunk.Average(n => n.Want) - chunk.Average(n => y[n.Node]);
                if (shift < 0) foreach (var (node, _) in chunk) y[node] += shift;
                nextUnit++;
            }
        }
        _columns = nextUnit - 1;
        double top = y.Values.Min();
        return graph.Nodes.ToDictionary(n => n, n => new Point(Margin_ + (_columns - unit[n]) * (NodeWidth + columnGap), Margin_ + y[n] - top));
    }

    private Border Box(UsageNode node)
    {
        var glyph = new TextBlock
        {
            Text = node.Symbol is null ? ResultRow.FileGlyph : DefinitionPeek.KindGlyph(node.Symbol.Kind),
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, node.IsTarget ? "TextOnAccentFillColorPrimaryBrush" : "AccentTextFillColorPrimaryBrush");

        var title = new TextBlock { Text = node.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        string note = (node.IsRecursive ? L.T("UsageGraphWindow.CallsItself") : "") + (node.MoreCallers > 0 ? L.T("UsageGraphWindow.More2", (node.IsRecursive ? "  ·  " : ""), node.MoreCallers) : "");
        var detail = new TextBlock { Text = node.Detail + (note.Length > 0 ? "  ·  " + note : ""), FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(detail);
        var origin = new TextBlock { Text = OriginOf(node.File), FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        origin.SetResourceReference(TextBlock.ForegroundProperty, node.IsTarget ? "TextOnAccentFillColorPrimaryBrush" : "AccentTextFillColorPrimaryBrush");
        text.Children.Add(origin);
        var row = new DockPanel { Margin = new Thickness(12, 0, 10, 0) };
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(text);

        var box = new Border
        {
            Width = NodeWidth, Height = NodeHeight, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5),
            Child = row, Cursor = Cursors.Hand, ContextMenu = MenuFor(node),
            ToolTip = L.T("UsageGraphWindow.DoubleClickOpenCtrl", node.Title, node.File.Path, node.Line, node.File.Source),
        };
        if (node.IsTarget) box.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        else box.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        if (node.IsTarget) title.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
        box.MouseLeftButtonUp += (_, e) =>
        {
            if (_panned) return;
            Select(node);
            e.Handled = true;
        };
        box.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            OpenAt(node.File, node.Offset, node.Length, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        };
        return box;
    }

    /// <summary>The mod a box's file belongs to, or "Game Script" for the game's own scripts.</summary>
    private static string OriginOf(ScriptFile file)
    {
        if (file.Source.Equals("Game scripts", StringComparison.OrdinalIgnoreCase) || W3ModWorkbench.Core.Scripts.ScriptFolder.IsGameScripts(file.SourceKey)) return L.T("UsageGraphWindow.GameScript");
        string name = file.Source;
        int slash = name.IndexOf('\\');
        if (slash > 0) name = name[..slash];
        const string loose = " (loose scripts)"; // loc-ignore: how the scripts index names a mod's loose scripts, not shown
        return name.EndsWith(loose, StringComparison.OrdinalIgnoreCase) ? name[..^loose.Length] : name;
    }

    private static ToolTip SitesTip(UsageEdge edge)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = L.T("UsageGraphWindow.Uses2", edge.From.Title, edge.To.Title), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var site in edge.Sites.Take(12))
            panel.Children.Add(new TextBlock { Text = $"{site.Line,5}  {site.LineText.Trim()}", FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5 });
        if (edge.Sites.Count > 12) panel.Children.Add(new TextBlock { Text = L.T("UsageGraphWindow.More", edge.Sites.Count - 12), Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(new TextBlock { Text = L.T("UsageGraphWindow.ClickOpenFirst"), Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        return new ToolTip { Content = panel };
    }

    private ContextMenu MenuFor(UsageNode node)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = L.T("Common.Open") };
        open.Click += (_, _) => OpenAt(node.File, node.Offset, node.Length, false);
        var side = new MenuItem { Header = L.T("UsageGraphWindow.OpenSide") };
        side.Click += (_, _) => OpenAt(node.File, node.Offset, node.Length, true);
        menu.Items.Add(open);
        menu.Items.Add(side);
        if (node.Symbol is { Kind: SymbolKind.Function or SymbolKind.Event } symbol && !node.IsTarget)
        {
            var again = new MenuItem { Header = L.T("UsageGraphWindow.ShowUsageGraph") };
            again.Click += (_, _) => Reroot(symbol);
            menu.Items.Add(new Separator());
            menu.Items.Add(again);
        }
        var copy = new MenuItem { Header = L.T("Common.CopyName") };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(node.Symbol?.Name ?? node.File.Path); }
            catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard is busy */ }
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        return menu;
    }

    /// <summary>Starts the graph again from another function in it.</summary>
    private void Reroot(ScriptSymbol symbol)
    {
        try
        {
            _parsed = ScriptParser.Parse(symbol.File.Text, symbol.File);
        }
        catch (Exception)
        {
            return;
        }
        _targets = UsageGraphBuilder.Targets(symbol, _parsed, _layers);
        Refresh();
    }

    // ---------------------------------------------------------------- selection

    /// <summary>Marks a box and the chain of calls from it down to the target.</summary>
    private void Select(UsageNode node)
    {
        if (_graph is null) return;
        _selected = node;
        var chain = new HashSet<UsageEdge>();
        var seen = new HashSet<UsageNode> { node };
        var queue = new Queue<UsageNode>([node]);
        var outgoing = _graph.Edges.ToLookup(e => e.From);
        while (queue.Count > 0)
            foreach (var edge in outgoing[queue.Dequeue()])
            {
                chain.Add(edge);
                if (seen.Add(edge.To)) queue.Enqueue(edge.To);
            }

        foreach (var (edge, visual) in _lines)
        {
            bool on = chain.Contains(edge);
            visual.Line.SetResourceReference(Shape.StrokeProperty, on ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush");
            visual.Arrow.SetResourceReference(Shape.FillProperty, on ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush");
            visual.Line.StrokeThickness = on ? 2.4 : 1.2;
            visual.Line.Opacity = visual.Arrow.Opacity = on || chain.Count == 0 ? 1 : 0.55;
            Panel.SetZIndex(visual.Line, on ? 1 : 0);
        }
        foreach (var (n, box) in _boxes)
        {
            bool picked = n == node;
            if (picked) box.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            else if (seen.Contains(n)) box.SetResourceReference(Border.BorderBrushProperty, "AccentTextFillColorPrimaryBrush");
            else box.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            box.BorderThickness = new Thickness(picked ? 2.5 : 1.5);
        }
        DetailText.Text = node.IsTarget
            ? L.T("UsageGraphWindow.WhatGraphAboutCallers")
            : $"{node.Title}  ·  {node.File.Path} : {node.Line}  ·  {node.File.Source}";
    }

    private void OpenAt(ScriptFile file, int offset, int length, bool toSide) =>
        _ = _host.NavigateAsync(_from, file, offset, length, toSide);

    // ---------------------------------------------------------------- view

    private void FitView(bool onlyIfLarger)
    {
        if (Surface.Width <= 0 || Surface.Height <= 0 || Scroller.ViewportWidth <= 0) return;
        double fit = Math.Min((Scroller.ViewportWidth - 14) / Surface.Width, (Scroller.ViewportHeight - 14) / Surface.Height);
        if (onlyIfLarger && fit >= 1) fit = 1;
        SetZoom(Math.Clamp(fit, 0.25, 1.5));
        ScrollHome();
    }

    /// <summary>Scrolls so the graph's top left corner is at the window's.</summary>
    private void ScrollHome()
    {
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(PanPad);
        Scroller.ScrollToVerticalOffset(PanPad);
    }

    private void SetZoom(double scale) => Zoom.ScaleX = Zoom.ScaleY = scale;

    private void Scroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        double scale = Math.Clamp(Zoom.ScaleX * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.25, 2.5);
        var at = e.GetPosition(Scroller);
        double x = (Scroller.HorizontalOffset + at.X - PanPad) / Zoom.ScaleX, y = (Scroller.VerticalOffset + at.Y - PanPad) / Zoom.ScaleY;
        SetZoom(scale);
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(x * scale + PanPad - at.X);
        Scroller.ScrollToVerticalOffset(y * scale + PanPad - at.Y);
        e.Handled = true;
    }

    private void Scroller_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is ScrollBar || e.OriginalSource is DependencyObject d && IsInScrollBar(d)) return;
        _panButton = e.ChangedButton;
        _panFrom = e.GetPosition(Scroller);
        _panStart = new Point(Scroller.HorizontalOffset, Scroller.VerticalOffset);
        _panned = false;
    }

    private static bool IsInScrollBar(DependencyObject d)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollBar) return true;
        return false;
    }

    private void Scroller_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is not { } from) return;
        if ((_panButton == MouseButton.Right ? e.RightButton : e.LeftButton) != MouseButtonState.Pressed)
        {
            _panFrom = null;
            return;
        }
        var now = e.GetPosition(Scroller);
        if (!_panned && (Math.Abs(now.X - from.X) > 4 || Math.Abs(now.Y - from.Y) > 4))
        {
            _panned = true;
            Scroller.CaptureMouse();
            Scroller.Cursor = Cursors.SizeAll;
        }
        if (!_panned) return;
        Scroller.ScrollToHorizontalOffset(_panStart.X - (now.X - from.X));
        Scroller.ScrollToVerticalOffset(_panStart.Y - (now.Y - from.Y));
    }

    /// <summary>A right-button drag pans, so it must not end by opening the menu of the box under the mouse.</summary>
    private void Scroller_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_panned) e.Handled = true;
    }

    private void Scroller_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != _panButton) return;
        bool wasPan = _panned;
        _panFrom = null;
        Scroller.ReleaseMouseCapture();
        Scroller.ClearValue(CursorProperty);
        // a drag that ended on a box is not a click on it
        if (wasPan) Dispatcher.BeginInvoke(() => _panned = false, System.Windows.Threading.DispatcherPriority.Input);
        else _panned = false;
    }

    // ---------------------------------------------------------------- toolbar

    private void Levels_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && IsLoaded) Refresh();
    }

    /// <summary>Fits the height, then spreads the columns out until the width is used too.</summary>
    private void Stretch_Click(object sender, RoutedEventArgs e)
    {
        if (_graph is not { } graph) return;
        _columnGap = MinColumnGap;
        Show(graph, keepView: true);
        int columns = _columns;
        Scroller.UpdateLayout();
        if (Surface.Height <= 0 || Scroller.ViewportWidth <= 0) return;
        double scale = Math.Clamp((Scroller.ViewportHeight - 14) / Surface.Height, 0.25, 1.5);
        if (columns > 0)
        {
            double room = (Scroller.ViewportWidth - 14) / scale - 2 * Margin_ - (columns + 1) * NodeWidth;
            _columnGap = Math.Clamp(room / columns, MinColumnGap, 700);
            Show(graph, keepView: true);
        }
        // the spread may have made it wider than the window at this zoom
        scale = Math.Min(scale, (Scroller.ViewportWidth - 14) / Surface.Width);
        SetZoom(Math.Clamp(scale, 0.25, 1.5));
        ScrollHome();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitView(onlyIfLarger: false);

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.F5) Refresh();
        else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control) FitView(onlyIfLarger: false);
        else return;
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _run?.Cancel();
        base.OnClosed(e);
    }
}
