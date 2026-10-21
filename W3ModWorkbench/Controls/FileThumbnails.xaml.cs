using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Files as pictures, in rows, for the main window's list and the asset browser's. Thumbnails keep their own selection
/// on each file (<see cref="EntryItem.IsSelected"/>): click, Ctrl and Shift as in Explorer, the arrow keys, Ctrl+A.
/// </summary>
public partial class FileThumbnails : UserControl
{
    // Thumbnails are placed by hand: every cell is the same size, so where a file goes follows from its number, and only
    // the cells in view are made, from a pool. Nothing in the layout depends on what a cell holds, so it can't loop.
    private readonly Dictionary<int, ContentControl> _liveCells = [];
    private readonly Stack<ContentControl> _cellPool = new();
    private IReadOnlyList<EntryItem> _items = [];
    private int _perRow = 1;
    private int _size = 96;

    private const double ThumbPadding = 8;

    public FileThumbnails() => InitializeComponent();

    /// <summary>Whether each file has a check box, for lists whose files can be checked to extract.</summary>
    public bool ShowChecks { get; set; } = true;

    /// <summary>The texture to make an .xbm's (or other texture header's) thumbnail from, which is in a cache; null when none has it.</summary>
    public Func<EntryItem, Task<ArchiveEntry?>>? TextureFor { get; set; }

    /// <summary>A file was double-clicked.</summary>
    public event Action<EntryItem>? ItemOpened;

    /// <summary>Which files are highlighted changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>The file clicked last, which Shift extends from.</summary>
    public EntryItem? Anchor { get; private set; }

    /// <summary>The highlighted files, in the order shown.</summary>
    public List<EntryItem> Selected => [.. _items.Where(i => i.IsSelected)];

    /// <summary>How big each picture is: 64, 96 or 160.</summary>
    public int ThumbnailSize
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            Rebuild();
        }
    }

    /// <summary>Small, Medium or Large: the thumbnail sizes of the View menu.</summary>
    public static int SizeOf(string view) => view switch { "Small" => 64, "Large" => 160, _ => 96 };

    private int CellWidth => _size + 22;

    private int CellHeight => _size + 60;

    /// <summary>Shows these files from the top, highlighting none of them but the one clicked last.</summary>
    public void Show(IReadOnlyList<EntryItem> items)
    {
        _items = items;
        foreach (var item in items.Where(i => i.IsSelected && !ReferenceEquals(Anchor, i))) item.IsSelected = false;
        Rebuild();
    }

    /// <summary>Highlights the files: just these, or these added to those that are.</summary>
    public void Select(IEnumerable<EntryItem> items, bool add = false)
    {
        var set = items.ToHashSet();
        foreach (var item in _items)
            if (set.Contains(item)) item.IsSelected = true;
            else if (!add && item.IsSelected) item.IsSelected = false;
        SelectionChanged?.Invoke();
    }

    /// <summary>Lays the cells out again from the top, once it's showing.</summary>
    public void Rebuild()
    {
        foreach (var cell in _liveCells.Values) ReleaseCell(cell);
        _liveCells.Clear();
        Scroller.ScrollToVerticalOffset(0);
        if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, LayoutThumbs);
    }

    public new bool Focus() => Scroller.Focus();

    private void ReleaseCell(ContentControl cell)
    {
        ThumbCanvas.Children.Remove(cell);
        cell.Content = null;
        _cellPool.Push(cell);
    }

    private ContentControl NewCell()
    {
        if (_cellPool.Count > 0) return _cellPool.Pop();
        var cell = new ContentControl { ContentTemplate = (DataTemplate)FindResource("ThumbCell"), Focusable = false };
        cell.MouseLeftButtonDown += Cell_MouseLeftButtonDown;
        cell.MouseRightButtonDown += Cell_MouseRightButtonDown;
        return cell;
    }

    /// <summary>Puts each file in view where it goes, makes the cells that are missing and gives back the ones that scrolled away.</summary>
    private void LayoutThumbs()
    {
        if (!IsVisible) return;
        double width = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Math.Max(0, Scroller.ActualWidth - 18);
        double height = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        _perRow = Math.Max(1, (int)((width - 2 * ThumbPadding) / CellWidth));
        int count = _items.Count;
        int rows = (count + _perRow - 1) / _perRow;
        ThumbCanvas.Width = _perRow * CellWidth + 2 * ThumbPadding;
        ThumbCanvas.Height = rows * CellHeight + 2 * ThumbPadding;

        double top = Scroller.VerticalOffset;
        int firstRow = Math.Max(0, (int)((top - ThumbPadding) / CellHeight) - 1);
        int lastRow = Math.Min(rows - 1, (int)((top + height) / CellHeight) + 1);
        int first = firstRow * _perRow, last = Math.Min(count - 1, (lastRow + 1) * _perRow - 1);
        if (count == 0 || last < first) { first = 0; last = -1; }

        foreach (var (index, cell) in _liveCells.Where(c => c.Key < first || c.Key > last).ToList())
        {
            _liveCells.Remove(index);
            ReleaseCell(cell);
        }
        for (int i = first; i <= last; i++)
        {
            if (!_liveCells.TryGetValue(i, out var cell))
            {
                cell = NewCell();
                cell.Tag = (double)_size;
                cell.Width = CellWidth;
                cell.Height = CellHeight;
                cell.Content = _items[i];
                ThumbCanvas.Children.Add(cell);
                _liveCells[i] = cell;
                RequestThumbnail(_items[i], cell);
            }
            Canvas.SetLeft(cell, ThumbPadding + i % _perRow * CellWidth);
            Canvas.SetTop(cell, ThumbPadding + i / _perRow * CellHeight);
        }
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => LayoutThumbs();

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutThumbs();

    private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) Rebuild();
    }

    /// <summary>Makes a thumbnail for a picture file the first time its cell is built, and only while the cell is still showing it.</summary>
    private async void RequestThumbnail(EntryItem item, ContentControl cell)
    {
        if (item.ThumbnailRequested || !item.CanHaveThumbnail) return;
        item.ThumbnailRequested = true;
        bool Wanted() => ReferenceEquals(cell.Content, item);
        // an .xbm in a bundle shows the texture it describes, found in a cache under the same path
        ArchiveEntry? source = !item.IsTextureHeader ? item.Entry : TextureFor is { } find ? await find(item) : null;
        if (source is null) return;
        var picture = await EntryImages.LoadAsync(source, 192, wanted: Wanted);
        if (picture is not null) item.Thumbnail = picture.Image;
        else if (!Wanted()) item.ThumbnailRequested = false;
    }

    private void Cell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ContentControl)?.Content is not EntryItem item || FindParent<CheckBox>(e.OriginalSource as DependencyObject) is not null) return;
        Scroller.Focus();
        if (e.ClickCount == 2)
        {
            ItemOpened?.Invoke(item);
            e.Handled = true;
            return;
        }
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            item.IsSelected = !item.IsSelected;
            Anchor = item;
            SelectionChanged?.Invoke();
        }
        else if (mods == ModifierKeys.Shift && Anchor is not null)
        {
            int a = IndexOf(Anchor), b = IndexOf(item);
            if (a < 0) a = b;
            Select(_items.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1));
        }
        else
        {
            Select([item]);
            Anchor = item;
        }
        e.Handled = true;
    }

    /// <summary>A right-click on a file that isn't highlighted highlights just it, so the menu acts on what was clicked.</summary>
    private void Cell_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ContentControl)?.Content is not EntryItem item) return;
        Scroller.Focus();
        if (!item.IsSelected)
        {
            Select([item]);
            Anchor = item;
        }
    }

    private int IndexOf(EntryItem item)
    {
        for (int i = 0; i < _items.Count; i++)
            if (ReferenceEquals(_items[i], item)) return i;
        return -1;
    }

    /// <summary>The keys of the thumbnail view: arrows move by a file or a row, Shift extends, Ctrl+A selects all.</summary>
    private void Scroller_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_items.Count == 0) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Select(_items);
            e.Handled = true;
            return;
        }
        int step = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -_perRow, Key.Down => _perRow, _ => 0 };
        if (step == 0 && e.Key is not (Key.Home or Key.End)) return;
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        int current = Anchor is null ? -1 : IndexOf(Anchor);
        int target = e.Key == Key.Home ? 0 : e.Key == Key.End ? _items.Count - 1 : Math.Clamp(current < 0 ? 0 : current + step, 0, _items.Count - 1);
        if (Keyboard.Modifiers == ModifierKeys.Shift && current >= 0)
            Select(_items.Skip(Math.Min(current, target)).Take(Math.Abs(current - target) + 1));
        else
        {
            Select([_items[target]]);
            Anchor = _items[target];
        }
        if (Keyboard.Modifiers == ModifierKeys.None) Anchor = _items[target];
        double rowTop = ThumbPadding + target / _perRow * CellHeight;
        if (rowTop < Scroller.VerticalOffset) Scroller.ScrollToVerticalOffset(rowTop - ThumbPadding);
        else if (rowTop + CellHeight > Scroller.VerticalOffset + Scroller.ViewportHeight) Scroller.ScrollToVerticalOffset(rowTop + CellHeight - Scroller.ViewportHeight + ThumbPadding);
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not T) child = VisualTreeHelper.GetParent(child);
        return child as T;
    }
}
