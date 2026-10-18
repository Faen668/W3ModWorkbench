using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace W3ModWorkbench;

/// <summary>
/// A group of tabs in a <see cref="PreviewWindow"/>: the tab strip and the files under it. Every file stays
/// in the view, hidden while another tab is selected, so each keeps its place, selection and search.
/// </summary>
public partial class PreviewPane : UserControl
{
    /// <summary>Most recently selected first, so closing a tab goes back to the one used before it.</summary>
    private readonly List<PreviewDocument> _recent = [];

    /// <summary>Pinned files: their tabs stay at the front, and closing others or all leaves them open.</summary>
    private readonly HashSet<PreviewDocument> _pinned = [];
    private bool _active;
    private DropZone _drop;

    private enum DropZone { None, Here, Beside }

    public PreviewWindow Host { get; }

    public PreviewDocument? Selected { get; private set; }

    public PreviewPane(PreviewWindow host)
    {
        InitializeComponent();
        Host = host;
        Loaded += (_, _) =>
        {
            if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
            {
                var fill = new SolidColorBrush(Color.FromArgb(0x2E, accent.Color.R, accent.Color.G, accent.Color.B));
                fill.Freeze();
                DropHint.Background = fill;
            }
        };
    }

    public IEnumerable<PreviewTab> Tabs => TabStrip.Children.OfType<PreviewTab>();

    public List<PreviewDocument> Documents => [.. Tabs.Select(t => t.Document)];

    public int Count => TabStrip.Children.Count;

    /// <summary>The group the keyboard is in: its selected tab is marked with the accent colour.</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            foreach (var tab in Tabs) tab.UpdateLook();
        }
    }

    public PreviewDocument? Find(string key) => Tabs.FirstOrDefault(t => t.Document.Item.Key == key)?.Document;

    public bool Contains(PreviewDocument doc) => TabOf(doc) is not null;

    private PreviewTab? TabOf(PreviewDocument doc) => Tabs.FirstOrDefault(t => t.Document == doc);

    public void Insert(PreviewDocument doc, int index = -1)
    {
        doc.Host = Host;
        doc.Visibility = Visibility.Collapsed;
        DocumentHost.Children.Add(doc);
        var tab = new PreviewTab(doc, this);
        if (index < 0 || index > Count) TabStrip.Children.Add(tab);
        else TabStrip.Children.Insert(Math.Max(index, _pinned.Count), tab);
    }

    /// <summary>Puts <paramref name="doc"/> in place of <paramref name="old"/>: in its tab's place, pinned and selected as it was.</summary>
    public void Replace(PreviewDocument old, PreviewDocument doc)
    {
        if (TabOf(old) is not { } tab) return;
        int index = TabStrip.Children.IndexOf(tab);
        bool pinned = _pinned.Contains(old), selected = Selected == old;
        Remove(old);
        old.Host = null;
        Insert(doc, index);
        if (pinned) SetPinned(doc, true);
        if (selected) Select(doc, focus: false);
    }

    public bool IsPinned(PreviewDocument doc) => _pinned.Contains(doc);

    /// <summary>Pins a tab, moving it to the end of the pinned ones at the front, or unpins it, moving it just after them.</summary>
    public void SetPinned(PreviewDocument doc, bool pinned)
    {
        if (TabOf(doc) is not { } tab) return;
        if (pinned) _pinned.Add(doc);
        else _pinned.Remove(doc);
        TabStrip.Children.Remove(tab);
        TabStrip.Children.Insert(Math.Min(pinned ? _pinned.Count - 1 : _pinned.Count, Count), tab);
        tab.UpdateLook();
        tab.BringIntoView();
    }

    /// <summary>Moves a tab along the strip.</summary>
    public void Reorder(PreviewDocument doc, int index)
    {
        if (TabOf(doc) is not { } tab) return;
        int from = TabStrip.Children.IndexOf(tab);
        if (index > from) index--; // its own place is gone once it's taken out
        TabStrip.Children.RemoveAt(from);
        // pinned tabs stay among themselves at the front, the others after them
        int pinned = _pinned.Count - (_pinned.Contains(doc) ? 1 : 0);
        index = _pinned.Contains(doc) ? Math.Min(index, pinned) : Math.Max(index, pinned);
        TabStrip.Children.Insert(Math.Clamp(index, 0, Count), tab);
        tab.BringIntoView();
    }

    /// <summary>Takes a file out of this group, and selects the tab used before it.</summary>
    public void Remove(PreviewDocument doc)
    {
        if (TabOf(doc) is not { } tab) return;
        doc.CloseOverlays();
        TabStrip.Children.Remove(tab);
        DocumentHost.Children.Remove(doc);
        _recent.Remove(doc);
        _pinned.Remove(doc);
        doc.Visibility = Visibility.Visible;
        if (Selected != doc) return;
        Selected = null;
        if (_recent.FirstOrDefault() is { } next) Select(next, focus: IsKeyboardFocusWithin || IsActive);
        else Host.OnSelectionChanged(this);
    }

    public void Select(PreviewDocument doc, bool focus = true)
    {
        if (TabOf(doc) is not { } tab) return;
        if (Selected != doc)
        {
            if (Selected is { } old) old.Visibility = Visibility.Collapsed;
            Selected = doc;
            doc.Visibility = Visibility.Visible;
            foreach (var t in Tabs) t.UpdateLook();
            Host.OnSelectionChanged(this);
        }
        _recent.Remove(doc);
        _recent.Insert(0, doc);
        tab.BringIntoView();
        if (focus) doc.FocusEditor();
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab: the next or previous tab along the strip, wrapping round.</summary>
    public void SelectNext(int step)
    {
        var docs = Documents;
        if (docs.Count < 2 || Selected is null) return;
        int i = docs.IndexOf(Selected);
        Select(docs[((i + step) % docs.Count + docs.Count) % docs.Count]);
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // the strip only scrolls sideways
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- dropping tabs

    private static PreviewTab? Dragged(DragEventArgs e) =>
        e.Data.GetDataPresent(typeof(PreviewTab)) ? e.Data.GetData(typeof(PreviewTab)) as PreviewTab : null;

    /// <summary>Where along the strip a tab dropped at <paramref name="x"/> goes.</summary>
    private int InsertIndex(double x)
    {
        int i = 0;
        foreach (var tab in Tabs)
        {
            double left = tab.TranslatePoint(new Point(0, 0), TabStrip).X;
            if (x < left + tab.ActualWidth / 2) return i;
            i++;
        }
        return i;
    }

    private void TabBar_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Dragged(e) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        int index = InsertIndex(e.GetPosition(TabStrip).X);
        double x = index < Count
            ? ((FrameworkElement)TabStrip.Children[index]).TranslatePoint(new Point(0, 0), TabStrip).X - 3
            : TabStrip.ActualWidth - 3;
        InsertMark.Margin = new Thickness(Math.Max(0, x), 4, 0, 4);
        InsertMark.Visibility = Visibility.Visible;
    }

    private void TabBar_DragLeave(object sender, DragEventArgs e) => InsertMark.Visibility = Visibility.Collapsed;

    private void TabBar_Drop(object sender, DragEventArgs e)
    {
        InsertMark.Visibility = Visibility.Collapsed;
        if (Dragged(e) is not { } tab) return;
        e.Handled = true;
        e.Effects = DragDropEffects.Move;
        Host.MoveDocument(tab.Document, this, InsertIndex(e.GetPosition(TabStrip).X));
    }

    /// <summary>
    /// Over the text: the far side (right or bottom, as Settings has it) opens a new group there when the
    /// window isn't split yet; anywhere else moves the tab into this group.
    /// </summary>
    private DropZone ZoneAt(DragEventArgs e, PreviewTab tab)
    {
        bool fromHere = tab.Pane == this;
        if (Host.CanSplit && !(fromHere && Count == 1))
        {
            var p = e.GetPosition(Body);
            bool down = PreviewWindow.SplitsDown;
            double along = down ? p.Y / Math.Max(1, Body.ActualHeight) : p.X / Math.Max(1, Body.ActualWidth);
            if (along > 0.6) return DropZone.Beside;
        }
        return fromHere ? DropZone.None : DropZone.Here;
    }

    private void Body_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (Dragged(e) is not { } tab) return;
        e.Handled = true;
        _drop = ZoneAt(e, tab);
        e.Effects = _drop == DropZone.None ? DragDropEffects.None : DragDropEffects.Move;
        if (_drop == DropZone.None)
        {
            DropHint.Visibility = Visibility.Collapsed;
            return;
        }
        bool down = PreviewWindow.SplitsDown;
        bool half = _drop == DropZone.Beside;
        DropHint.HorizontalAlignment = half && !down ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        DropHint.VerticalAlignment = half && down ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        DropHint.Width = half && !down ? Body.ActualWidth / 2 : double.NaN;
        DropHint.Height = half && down ? Body.ActualHeight / 2 : double.NaN;
        DropHint.Visibility = Visibility.Visible;
    }

    private void Body_PreviewDragLeave(object sender, DragEventArgs e)
    {
        // moving between the text's own parts raises this too
        var p = e.GetPosition(Body);
        if (p.X > 0 && p.Y > 0 && p.X < Body.ActualWidth && p.Y < Body.ActualHeight) return;
        DropHint.Visibility = Visibility.Collapsed;
    }

    private void Body_PreviewDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (Dragged(e) is not { } tab) return;
        e.Handled = true;
        switch (ZoneAt(e, tab))
        {
            case DropZone.Beside:
                e.Effects = DragDropEffects.Move;
                Host.MoveToNewGroup(tab.Document);
                break;
            case DropZone.Here:
                e.Effects = DragDropEffects.Move;
                Host.MoveDocument(tab.Document, this, -1);
                break;
            default:
                e.Effects = DragDropEffects.None;
                break;
        }
    }

    /// <summary>Called when a drag ends, wherever it ended, to clear what it showed.</summary>
    internal void ClearDropMarks()
    {
        InsertMark.Visibility = Visibility.Collapsed;
        DropHint.Visibility = Visibility.Collapsed;
    }
}

/// <summary>A tab: the file's name, a close button, and the selection mark. It can be dragged to move the file.</summary>
public sealed class PreviewTab : Border
{
    private readonly TextBlock _name;
    private readonly TextBlock _pin;
    private readonly Button _close;
    private readonly TextBlock _cross;
    private readonly Border _dot;
    private readonly Border _mark;
    private Point? _dragStart;
    private bool _hover;

    public PreviewDocument Document { get; }

    public PreviewPane Pane { get; }

    public PreviewTab(PreviewDocument doc, PreviewPane pane)
    {
        Document = doc;
        Pane = pane;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 3, 4, 3);
        Margin = new Thickness(0, 0, 4, 0);
        Background = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        ToolTip = $"{doc.Item.Path}\n{doc.Item.Source}";
        ContextMenu = new ContextMenu(); // filled when it opens
        ContextMenuOpening += (_, _) => Pane.Host.FillTabMenu(ContextMenu, doc);

        var icon = new TextBlock
        {
            Text = doc.Item.IsScript ? "" : "", // code, document
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _name = new TextBlock
        {
            Text = doc.FileName,
            MaxWidth = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var cross = new TextBlock { Text = "", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cross.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        // a file with changes not saved shows a dot here, which turns into the cross under the mouse
        _dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed };
        _dot.SetResourceReference(BackgroundProperty, "TextFillColorPrimaryBrush");
        _cross = cross;
        var face = new Grid();
        face.Children.Add(cross);
        face.Children.Add(_dot);
        _close = new Button
        {
            Content = face,
            Width = 22,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            ToolTip = "Close (Ctrl+W)",
            Visibility = Visibility.Hidden,
        };
        _close.Click += (_, _) => Pane.Host.CloseDocument(Document);
        _close.MouseEnter += (_, _) => UpdateLook();
        _close.MouseLeave += (_, _) => UpdateLook();
        doc.DirtyChanged += (_, _) =>
        {
            if (!Pane.Contains(doc)) return; // moved to another tab since
            UpdateLook();
            Pane.Host.OnDirtyChanged(doc);
        };

        _pin = new TextBlock
        {
            Text = "\uE840", // pinned
            FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
            ToolTip = "Pinned: stays open when others are closed. Right-click to unpin",
        };
        _pin.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_pin);
        row.Children.Add(icon);
        row.Children.Add(_name);
        row.Children.Add(_close);

        _mark = new Border
        {
            Height = 3,
            Width = 18,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-6, 0, 0, -4),
            Visibility = Visibility.Hidden,
        };
        var grid = new Grid();
        grid.Children.Add(row);
        grid.Children.Add(_mark);
        Child = grid;

        MouseEnter += (_, _) => { _hover = true; UpdateLook(); };
        MouseLeave += (_, _) => { _hover = false; UpdateLook(); };
        UpdateLook();
    }

    private bool IsSelected => Pane.Selected == Document;

    public void UpdateLook()
    {
        bool selected = IsSelected;
        if (selected) SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        else if (_hover) SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        else Background = Brushes.Transparent;
        if (selected) SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        else BorderBrush = Brushes.Transparent;
        _name.Opacity = selected ? 1 : 0.72;
        _pin.Visibility = Pane.IsPinned(Document) ? Visibility.Visible : Visibility.Collapsed;
        bool dirty = Document.IsDirty;
        _close.Visibility = selected || _hover || dirty ? Visibility.Visible : Visibility.Hidden;
        _dot.Visibility = dirty && !_close.IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
        _cross.Visibility = _dot.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        _close.ToolTip = dirty ? "Close (Ctrl+W). It has changes that aren't saved" : "Close (Ctrl+W)";
        _mark.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        _mark.SetResourceReference(BackgroundProperty, Pane.IsActive ? "AccentFillColorDefaultBrush" : "TextFillColorDisabledBrush");
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle)
        {
            Pane.Host.CloseDocument(Document);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            Pane.Select(Document);
            _dragStart = e.GetPosition(this);
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Left) _dragStart = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        Pane.Host.DragTab(this);
    }
}
