using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3BundleExplorer.Core.Scripts.Navigation;

namespace W3BundleExplorer;

/// <summary>
/// Shows files as tabs, in one group or two side by side. Files open here as tabs or in windows of their
/// own, as Settings has it; tabs can be dragged between groups and windows, or out into a new window.
/// </summary>
public partial class PreviewWindow : Window, IMessageHost
{
    /// <summary>Open preview windows, the one used last first. New tabs open in it.</summary>
    private static readonly List<PreviewWindow> OpenWindows = [];

    private readonly List<PreviewPane> _panes = [];
    private PreviewPane _active;
    private readonly bool _takesTabs;
    private PreviewWindow? _opener;

    /// <summary>Where go to definition jumped from, for Alt+Left. The files can be in any window by now.</summary>
    private readonly Stack<(PreviewDocument Doc, int Offset)> _back = new();

    /// <param name="takesTabs">False for a window of its own, like the licence, that other files don't open in.</param>
    private PreviewWindow(bool takesTabs = true)
    {
        InitializeComponent();
        _takesTabs = takesTabs;
        _active = AddPane();
        _active.IsActive = true;
        Layout();
        UpdateSplitButton();
        DisabledLook.Apply(this);
        OpenWindows.Add(this);

        Activated += (_, _) =>
        {
            OpenWindows.Remove(this);
            OpenWindows.Insert(0, this);
        };
        Deactivated += (_, _) =>
        {
            foreach (var doc in AllDocuments) doc.ClosePeek();
        };
        // without this, Windows can hand the focus to another app when an owned window closes,
        // and the explorer drops behind it as if it had been minimized
        Closing += (_, _) => (_opener is { IsVisible: true } opener ? opener : Owner)?.Activate();
        Closed += (_, _) =>
        {
            OpenWindows.Remove(this);
            foreach (var doc in AllDocuments)
            {
                doc.CloseOverlays();
                doc.Host = null;
            }
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.XButton1) return;
            GoBack();
            e.Handled = true;
        };
    }

    private static bool OpensTabs => App.Settings.PreviewOpenIn != "Windows";

    /// <summary>
    /// Shows a file: brings it forward if it's open already, otherwise opens it as a tab in the preview
    /// window used last, or in a new window, as Settings has it.
    /// </summary>
    public static void Show(PreviewItem item, Window owner)
    {
        if (FindOpen(item.Key) is { Host: { } host } open)
        {
            host.ShowDocument(open);
            return;
        }
        if (OpensTabs && OpenWindows.FirstOrDefault(w => w._takesTabs) is { } window)
        {
            window.Add(new PreviewDocument(item));
            window.BringForward();
            return;
        }
        var created = new PreviewWindow { Owner = owner };
        created.Add(new PreviewDocument(item));
        created.Show();
    }

    /// <summary>A file in a window of its own that other files don't open in, like the licence from Settings.</summary>
    public static void ShowAlone(PreviewItem item, Window owner)
    {
        var window = new PreviewWindow(takesTabs: false) { Owner = owner };
        window.Add(new PreviewDocument(item));
        window.Show();
    }

    /// <summary>Lays the open windows out again after Settings changed the split direction.</summary>
    public static void ApplySettings()
    {
        foreach (var window in OpenWindows)
        {
            window.Layout();
            window.UpdateSplitButton();
        }
    }

    private static PreviewDocument? FindOpen(string key) =>
        OpenWindows.SelectMany(w => w.AllDocuments).FirstOrDefault(d => d.Item.Key == key);

    private IEnumerable<PreviewDocument> AllDocuments => _panes.SelectMany(p => p.Documents);

    private PreviewDocument? Current => _active.Selected;

    private void Add(PreviewDocument doc)
    {
        _active.Insert(doc);
        _active.Select(doc);
    }

    private void BringForward()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Selects a file's tab and brings its window forward.</summary>
    public void ShowDocument(PreviewDocument doc)
    {
        if (PaneOf(doc) is not { } pane) return;
        SetActive(pane);
        pane.Select(doc);
        BringForward();
    }

    private PreviewPane? PaneOf(PreviewDocument doc) => _panes.FirstOrDefault(p => p.Contains(doc));

    // ---------------------------------------------------------------- groups and split view

    public bool CanSplit => _panes.Count < 2;

    public bool SplitsDown => App.Settings.PreviewSplit == "Down";

    private PreviewPane AddPane()
    {
        var pane = new PreviewPane(this);
        pane.PreviewGotKeyboardFocus += (_, _) => SetActive(pane);
        pane.PreviewMouseDown += (_, _) => SetActive(pane);
        _panes.Add(pane);
        return pane;
    }

    private void SetActive(PreviewPane pane)
    {
        if (_active == pane) return;
        _active.IsActive = false;
        _active = pane;
        pane.IsActive = true;
        UpdateHeader();
    }

    /// <summary>The other group, made when the window isn't split yet.</summary>
    private PreviewPane OtherPane(PreviewPane pane)
    {
        if (_panes.FirstOrDefault(p => p != pane) is { } other) return other;
        other = AddPane();
        Layout();
        UpdateSplitButton();
        return other;
    }

    /// <summary>Puts the groups in the window: one, or two with a splitter between, side by side or one above the other.</summary>
    private void Layout()
    {
        PaneGrid.Children.Clear();
        PaneGrid.RowDefinitions.Clear();
        PaneGrid.ColumnDefinitions.Clear();
        if (_panes.Count == 1)
        {
            PaneGrid.Children.Add(_panes[0]);
            return;
        }

        bool down = SplitsDown;
        for (int i = 0; i < 3; i++)
        {
            var size = i == 1 ? new GridLength(12) : new GridLength(1, GridUnitType.Star);
            if (down) PaneGrid.RowDefinitions.Add(new RowDefinition { Height = size, MinHeight = i == 1 ? 0 : 80 });
            else PaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = size, MinWidth = i == 1 ? 0 : 160 });
        }
        var splitter = new GridSplitter
        {
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = down ? GridResizeDirection.Rows : GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            Focusable = false,
            ToolTip = "Drag to resize",
        };
        for (int i = 0; i < 3; i++)
        {
            UIElement part = i == 1 ? splitter : _panes[i / 2];
            if (down) Grid.SetRow(part, i);
            else Grid.SetColumn(part, i);
            PaneGrid.Children.Add(part);
        }
    }

    private void UpdateSplitButton()
    {
        bool split = _panes.Count > 1, down = SplitsDown;
        SplitIcon.Text = split ? "" : down ? "" : ""; // close pane, dock bottom, dock right
        SplitText.Text = split ? "Unsplit" : "Split";
        SplitButton.ToolTip = split
            ? "Put every tab back in one group (Ctrl+\\)"
            : $"Move this file into a second group of tabs {(down ? "below" : "beside")} the others (Ctrl+\\). Right-click to choose the direction";
        SplitRightItem.IsChecked = !down;
        SplitDownItem.IsChecked = down;
    }

    /// <summary>
    /// Ctrl+\: splits the window, moving the file in use into a second group (or showing it there too
    /// when it's the only tab), or puts the second group's tabs back in the first.
    /// </summary>
    private void ToggleSplit()
    {
        if (_panes.Count == 1)
        {
            if (Current is not { } doc) return;
            if (_active.Count > 1) MoveToNewGroup(doc);
            else OpenBeside(doc);
            return;
        }

        var keep = _panes[0];
        var other = _panes[1];
        string? current = Current?.Item.Key;
        foreach (var doc in other.Documents)
        {
            other.Remove(doc);
            if (keep.Find(doc.Item.Key) is null) keep.Insert(doc);
            else doc.Host = null; // the same file is already there
        }
        RemovePane(other);
        if (current is not null && keep.Find(current) is { } selected) keep.Select(selected);
    }

    /// <summary>Shows a file in the other group too, keeping its place, as a second view of it.</summary>
    private void OpenBeside(PreviewDocument doc)
    {
        if (PaneOf(doc) is not { } pane) return;
        var target = OtherPane(pane);
        if (target.Find(doc.Item.Key) is { } there)
        {
            SetActive(target);
            target.Select(there);
            return;
        }
        var copy = new PreviewDocument(doc.Item);
        copy.CopyViewFrom(doc);
        target.Insert(copy);
        SetActive(target);
        target.Select(copy);
    }

    private void RemovePane(PreviewPane pane)
    {
        _panes.Remove(pane);
        if (_active == pane)
        {
            _active = _panes[0];
            _active.IsActive = true;
            _active.Selected?.FocusEditor();
        }
        Layout();
        UpdateSplitButton();
        UpdateHeader();
    }

    /// <summary>After a tab leaves a group: an empty group goes, and an empty window closes.</summary>
    private void AfterRemoval(PreviewPane pane)
    {
        if (pane.Count > 0 || !_panes.Contains(pane)) return;
        if (_panes.Count > 1) RemovePane(pane);
        else Close();
    }

    public void CloseDocument(PreviewDocument doc)
    {
        if (PaneOf(doc) is not { } pane) return;
        pane.Remove(doc);
        doc.Host = null;
        AfterRemoval(pane);
    }

    /// <summary>Moves a tab, from this window or another, into a group of this one.</summary>
    public void MoveDocument(PreviewDocument doc, PreviewPane target, int index)
    {
        if (doc.Host is not { } from || from.PaneOf(doc) is not { } source) return;
        if (source == target)
        {
            if (index >= 0) target.Reorder(doc, index);
            target.Select(doc);
            return;
        }

        source.Remove(doc);
        if (target.Find(doc.Item.Key) is { } there)
        {
            // already open in that group: show that one
            doc.Host = null;
            SetActive(target);
            target.Select(there);
        }
        else
        {
            target.Insert(doc, index);
            SetActive(target);
            target.Select(doc);
        }
        if (from != this) BringForward();
        from.AfterRemoval(source);
    }

    /// <summary>Moves a tab into a new group beside the one it's in, splitting the window.</summary>
    public void MoveToNewGroup(PreviewDocument doc)
    {
        if (!CanSplit || doc.Host is not { } from || from.PaneOf(doc) is not { } source) return;
        var target = OtherPane(_panes[0]);
        source.Remove(doc);
        target.Insert(doc);
        SetActive(target);
        target.Select(doc);
        if (from != this) BringForward();
        from.AfterRemoval(source);
    }

    /// <summary>Moves a tab out into a window of its own, its top left corner at <paramref name="at"/> (screen pixels) when given.</summary>
    private void MoveToNewWindow(PreviewDocument doc, Point? at = null)
    {
        if (PaneOf(doc) is not { } source) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var window = new PreviewWindow
        {
            Owner = Owner,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = at is { } p ? p.X / dpi.DpiScaleX - 120 : Left + 32,
            Top = at is { } q ? q.Y / dpi.DpiScaleY - 24 : Top + 32,
            Width = Width,
            Height = Height,
        };
        source.Remove(doc);
        window.Add(doc);
        window.Show();
        AfterRemoval(source);
    }

    /// <summary>Drags a tab to another group or window. Dropped anywhere else, it gets a window of its own.</summary>
    public void DragTab(PreviewTab tab)
    {
        var doc = tab.Document;
        var result = DragDrop.DoDragDrop(tab, new DataObject(typeof(PreviewTab), tab), DragDropEffects.Move);
        foreach (var pane in OpenWindows.SelectMany(w => w._panes)) pane.ClearDropMarks();
        if (result != DragDropEffects.None || doc.Host != this || AllDocuments.Count() < 2) return;
        if (!GetCursorPos(out var cursor)) return;
        var point = new Point(cursor.X, cursor.Y);
        if (OpenWindows.Any(w => w.IsVisible && w.ContainsScreenPoint(point))) return;
        MoveToNewWindow(doc, point);
    }

    private bool ContainsScreenPoint(Point point)
    {
        var topLeft = PointToScreen(new Point(0, 0));
        var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        return new Rect(topLeft, bottomRight).Contains(point);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    // ---------------------------------------------------------------- tab menu

    /// <summary>The right-click menu of a tab, made as it opens.</summary>
    public void FillTabMenu(ContextMenu menu, PreviewDocument doc)
    {
        menu.Items.Clear();
        if (PaneOf(doc) is not { } pane) return;
        var docs = pane.Documents;
        int index = docs.IndexOf(doc);

        void Add(string header, Action action, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Close", () => CloseDocument(doc), gesture: "Ctrl+W");
        Add("Close others", () => { foreach (var d in docs.Where(d => d != doc)) CloseDocument(d); }, docs.Count > 1);
        Add("Close to the right", () => { foreach (var d in docs.Skip(index + 1)) CloseDocument(d); }, index < docs.Count - 1);
        Add("Close all", () => { foreach (var d in docs) CloseDocument(d); });
        menu.Items.Add(new Separator());
        Add(_panes.Count > 1 ? "Open in the other group" : SplitsDown ? "Open below" : "Open to the side", () => OpenBeside(doc));
        Add(_panes.Count > 1 ? "Move to the other group" : SplitsDown ? "Move below" : "Move to the side",
            () => MoveDocument(doc, OtherPane(pane), -1), _panes.Count > 1 || docs.Count > 1);
        Add("Move to a new window", () => MoveToNewWindow(doc), AllDocuments.Count() > 1);
        menu.Items.Add(new Separator());
        Add("Copy path", () => Clipboard.SetText(doc.Item.Path));
        Add("Save as…", doc.Save);
    }

    // ---------------------------------------------------------------- go to definition

    public bool CanGoBack => _back.Any(b => b.Doc.Host is not null);

    /// <summary>
    /// Opens a definition: in place for the same file, in its tab when that group has it open already,
    /// otherwise in a new tab, or a new window when Settings says so. <paramref name="toSide"/> uses the other group.
    /// </summary>
    public async Task NavigateAsync(PreviewDocument from, ScriptSymbol target, bool toSide)
    {
        if (PaneOf(from) is not { } pane) return;
        int length = target.Name.Length;
        var jumpedFrom = (from, from.CaretOffset);

        if (!toSide && target.File.Key == from.Item.Key)
        {
            _back.Push(jumpedFrom);
            from.Reveal(target.Offset, length);
            return;
        }
        bool inWindow = !toSide && !OpensTabs;
        var targetPane = toSide ? OtherPane(pane) : pane;
        if ((inWindow ? FindOpen(target.File.Key) : targetPane.Find(target.File.Key)) is { Host: { } host } open)
        {
            host._back.Push(jumpedFrom);
            host.ShowDocument(open);
            open.Reveal(target.Offset, length);
            return;
        }

        byte[] data;
        try
        {
            data = await Task.Run(target.File.Read);
        }
        catch (Exception ex)
        {
            _ = from.ShowCard($"{target.File.Path} could not be read", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }
        var doc = new PreviewDocument(new PreviewItem(target.File.Path, target.File.Source, data, target.File, from.Item.Workspace));
        doc.Reveal(target.Offset, length);
        if (inWindow)
        {
            var window = new PreviewWindow
            {
                Owner = Owner,
                _opener = this,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = Left + 32,
                Top = Top + 32,
                Width = Width,
                Height = Height,
            };
            window._back.Push(jumpedFrom);
            window.Add(doc);
            window.Show();
            return;
        }
        if (!_panes.Contains(targetPane)) return; // closed while the file was read
        _back.Push(jumpedFrom);
        targetPane.Insert(doc, targetPane.Selected is { } selected ? targetPane.Documents.IndexOf(selected) + 1 : -1);
        SetActive(targetPane);
        targetPane.Select(doc);
    }

    /// <summary>Alt+Left: back to where the last jump started, in whichever tab or window that file is now.</summary>
    public void GoBack()
    {
        while (_back.Count > 0)
        {
            var (doc, offset) = _back.Pop();
            if (doc.Host is not { } host) continue;
            host.ShowDocument(doc);
            doc.MoveCaret(offset);
            return;
        }
    }

    // ---------------------------------------------------------------- header and keys

    public void OnSelectionChanged(PreviewPane pane)
    {
        if (pane == _active) UpdateHeader();
    }

    private void UpdateHeader()
    {
        if (Current is not { } doc) return;
        Title = $"{doc.FileName} - Preview";
        FileNameText.Text = doc.FileName;
        DetailText.Text = doc.Detail;
    }

    private void Split_Click(object sender, RoutedEventArgs e) => ToggleSplit();

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SetSplitDirection("Right");

    private void SplitDown_Click(object sender, RoutedEventArgs e) => SetSplitDirection("Down");

    private static void SetSplitDirection(string direction)
    {
        App.Settings.PreviewSplit = direction;
        App.Settings.Save();
        ApplySettings();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Current?.Copy();

    private void Save_Click(object sender, RoutedEventArgs e) => Current?.Save();

    private void FocusPane(int index)
    {
        if (index >= _panes.Count || _panes[index].Selected is not { } doc) return;
        SetActive(_panes[index]);
        doc.FocusEditor();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var doc = Current;
        if (key == Key.Escape)
        {
            // Esc closes a message first, then the search bar, then a window showing just one file
            if (doc?.HandleEscape() != true && AllDocuments.Count() == 1) Close();
        }
        else if (key == Key.S && mods == ModifierKeys.Control)
            doc?.Save();
        else if (key is Key.W or Key.F4 && mods == ModifierKeys.Control)
        {
            if (doc is not null) CloseDocument(doc);
        }
        else if (key == Key.Tab && (mods & ModifierKeys.Control) != 0)
            _active.SelectNext((mods & ModifierKeys.Shift) != 0 ? -1 : 1);
        else if (key is Key.Oem5 or Key.OemBackslash && mods == ModifierKeys.Control)
            ToggleSplit();
        else if (key is Key.D1 or Key.NumPad1 && mods == ModifierKeys.Control)
            FocusPane(0);
        else if (key is Key.D2 or Key.NumPad2 && mods == ModifierKeys.Control)
            FocusPane(1);
        else if (key == Key.Left && mods == ModifierKeys.Alt)
            GoBack();
        else
        {
            doc?.HandleKey(e);
            return;
        }
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e) => Current?.HandleKeyUp(e);

    public Task ShowMessage(string? title, string message, MessageKind kind) =>
        Current?.ShowCard(title, message, kind, primary: "OK") ?? Task.CompletedTask;
}
