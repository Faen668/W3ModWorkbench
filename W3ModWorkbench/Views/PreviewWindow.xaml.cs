using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Scripts.Navigation;

namespace W3ModWorkbench;

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
    private Window? _opener;

    /// <summary>Where go to definition jumped from, for Alt+Left. The files can be in any window by now.</summary>
    private readonly Stack<(PreviewDocument Doc, int Offset)> _back = new();

    /// <summary>Where Alt+Left came back from, for Alt+Right. Emptied by a new jump.</summary>
    private readonly Stack<(PreviewDocument Doc, int Offset)> _forward = new();

    /// <summary>Files closed lately, newest last, with where the caret was, for Ctrl+Shift+T.</summary>
    private static readonly List<(PreviewItem Item, int Offset)> ClosedFiles = [];

    /// <param name="takesTabs">False for a window of its own, like the licence, that other files don't open in.</param>
    private PreviewWindow(bool takesTabs = true)
    {
        InitializeComponent();
        _takesTabs = takesTabs;
        _active = AddPane();
        _active.IsActive = true;
        Layout();
        UpdateSplitButton();
        UpdateHistoryButtons();
        OpenWindows.Add(this);
        ProjectWorkspace.Changed += UpdateHeader;
        ProjectWorkspace.Changed += RefreshTabs;
        Results.Open += OpenResult;
        Results.Closed += CloseResultsPanel;
        Results.FindRequested += Results_FindRequested;

        Activated += (_, _) =>
        {
            OpenWindows.Remove(this);
            OpenWindows.Insert(0, this);
        };
        Deactivated += (_, _) =>
        {
            foreach (var doc in AllDocuments) doc.ClosePeek();
        };
        Closing += (_, e) =>
        {
            // changes that would be lost: asked about first, and the window closes again once answered
            if (!_closeConfirmed && !SkipUnsavedCheck && UnsavedHere().Count > 0)
            {
                e.Cancel = true;
                _ = ConfirmCloseWindowAsync();
                return;
            }
            // without this, Windows can hand the focus to another app when a window closes,
            // and the explorer drops behind it as if it had been minimized
            (_opener is { IsVisible: true } opener ? opener : Application.Current.MainWindow)?.Activate();
        };
        Closed += (_, _) =>
        {
            OpenWindows.Remove(this);
            ProjectWorkspace.Changed -= UpdateHeader;
            ProjectWorkspace.Changed -= RefreshTabs;
            _search?.Cancel();
            _compile?.Cancel();
            foreach (var doc in AllDocuments)
            {
                Remember(doc);
                doc.CloseOverlays();
                doc.Host = null;
            }
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) GoBack();
            else if (e.ChangedButton == MouseButton.XButton2) GoForward();
            else return;
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
        // scripts are written and read in Script Bench
        if (item.IsScript && BenchFor(owner) is { } bench)
        {
            bench.Add(CreateDocument(item));
            bench.BringForward();
            return;
        }
        if (OpensTabs && OpenWindows.FirstOrDefault(w => w._takesTabs && !w._isBench) is { } window)
        {
            window.Add(CreateDocument(item));
            window.BringForward();
            return;
        }
        var created = new PreviewWindow().Standalone(owner);
        created.Add(CreateDocument(item));
        created.Show();
    }

    /// <summary>
    /// Shows a file as <see cref="Show"/> does, with a place in it selected. Closing the window afterwards hands the
    /// focus back to <paramref name="owner"/>, such as the tool window the file was opened from.
    /// </summary>
    public static void ShowAt(PreviewItem item, Window owner, int offset, int length)
    {
        if (FindOpen(item.Key) is { Host: { } host } open)
        {
            host._opener = owner;
            host.ShowDocument(open);
            open.Reveal(offset, length);
            return;
        }
        var doc = CreateDocument(item);
        doc.Reveal(offset, length);
        if (item.IsScript && BenchFor(owner) is { } bench)
        {
            bench._opener = owner;
            bench.Add(doc);
            bench.BringForward();
            return;
        }
        if (OpensTabs && OpenWindows.FirstOrDefault(w => w._takesTabs && !w._isBench) is { } window)
        {
            window._opener = owner;
            window.Add(doc);
            window.BringForward();
            return;
        }
        var created = new PreviewWindow().Standalone(owner);
        created._opener = owner;
        created.Add(doc);
        created.Show();
    }

    /// <summary>A file in a window of its own that other files don't open in, like the licence from Settings.</summary>
    public static void ShowAlone(PreviewItem item, Window owner)
    {
        var window = new PreviewWindow(takesTabs: false).Standalone(owner);
        window._opener = owner; // closing it hands the focus back to the window that opened it
        window.Add(new PreviewDocument(item));
        window.Show();
    }

    /// <summary>Lays the open windows out again after Settings changed the split direction.</summary>
    /// <summary>Every open script shows or hides its outline side bar, at its width, as Settings now says.</summary>
    public static void ApplyOutlineSetting()
    {
        foreach (var window in OpenWindows)
            foreach (var doc in window.AllDocuments) doc.ApplyOutline();
    }

    public static void ApplySettings()
    {
        foreach (var window in OpenWindows)
        {
            window.Layout();
            window.UpdateSplitButton();
            window.UpdateHeader();
        }
    }

    /// <summary>
    /// A new view of a file. A project's file that's open somewhere already shares that view's text, so the
    /// two can't drift apart with different edits.
    /// </summary>
    private static PreviewDocument CreateDocument(PreviewItem item)
    {
        var doc = new PreviewDocument(item);
        if (item.IsEditable && FindOpen(item.Key) is { } open) doc.ShareTextWith(open);
        return doc;
    }

    /// <summary>The open view of a file, by its <see cref="PreviewItem.Key"/>, in any preview window.</summary>
    public static PreviewDocument? OpenDocument(string key) => FindOpen(key);

    private static PreviewDocument? FindOpen(string key) =>
        OpenWindows.SelectMany(w => w.AllDocuments).FirstOrDefault(d => d.Item.Key == key);

    private void RefreshTabs()
    {
        foreach (var pane in _panes) pane.RefreshTabs();
    }

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

    public static bool SplitsDown => App.Settings.PreviewSplit == "Down";

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
        copy.ShareTextWith(doc);
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

    public async void CloseDocument(PreviewDocument doc) => await CloseDocumentAsync(doc);

    /// <summary>Closes tabs one after another, stopping if one with unsaved changes is kept open.</summary>
    private async void CloseDocuments(IEnumerable<PreviewDocument> docs)
    {
        foreach (var doc in docs.ToList())
            if (!await CloseDocumentAsync(doc)) return;
    }

    /// <summary>Closes a tab, first asking about changes that would be lost. False when it was kept open.</summary>
    public async Task<bool> CloseDocumentAsync(PreviewDocument doc)
    {
        if (PaneOf(doc) is null) return true;
        if (doc.IsDirty && !HasOtherView(doc))
        {
            ShowDocument(doc);
            if (!await doc.ConfirmCloseAsync()) return false;
        }
        if (PaneOf(doc) is not { } pane) return true;
        Remember(doc);
        pane.Remove(doc);
        doc.Host = null;
        AfterRemoval(pane);
        UpdateHistoryButtons();
        return true;
    }

    /// <summary>Keeps a closing file for Ctrl+Shift+T, unless the same file is still open somewhere.</summary>
    private static void Remember(PreviewDocument doc)
    {
        ClosedFiles.RemoveAll(c => c.Item.Key == doc.Item.Key);
        ClosedFiles.Add((doc.Item, doc.CaretOffset));
        if (ClosedFiles.Count > 25) ClosedFiles.RemoveAt(0);
    }

    public static bool CanReopen => ClosedFiles.Any(c => FindOpen(c.Item.Key) is null);

    /// <summary>Ctrl+Shift+T: opens the file closed last again, here, where its caret was.</summary>
    public void ReopenClosed()
    {
        for (int i = ClosedFiles.Count - 1; i >= 0; i--)
        {
            var (item, offset) = ClosedFiles[i];
            ClosedFiles.RemoveAt(i);
            if (FindOpen(item.Key) is not null) continue;
            var doc = CreateDocument(item);
            doc.MoveCaret(offset);
            Add(doc);
            BringForward();
            return;
        }
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

        // pinned tabs stay when the others are closed in bulk
        var unpinned = docs.Where(d => !pane.IsPinned(d)).ToList();
        Add("Close", () => CloseDocument(doc), gesture: "Ctrl+W");
        Add("Close others", () => CloseDocuments(unpinned.Where(d => d != doc)), unpinned.Any(d => d != doc));
        Add("Close to the right", () => CloseDocuments(docs.Skip(index + 1).Where(unpinned.Contains)),
            docs.Skip(index + 1).Any(unpinned.Contains));
        Add("Close all", () => CloseDocuments(unpinned), unpinned.Count > 0);
        Add("Reopen closed tab", ReopenClosed, CanReopen, "Ctrl+Shift+T");
        menu.Items.Add(new Separator());
        Add(pane.IsPinned(doc) ? "Unpin" : "Pin", () => pane.SetPinned(doc, !pane.IsPinned(doc)));
        menu.Items.Add(new Separator());
        Add(_panes.Count > 1 ? "Open in the other group" : SplitsDown ? "Open below" : "Open to the side", () => OpenBeside(doc));
        Add(_panes.Count > 1 ? "Move to the other group" : SplitsDown ? "Move below" : "Move to the side",
            () => MoveDocument(doc, OtherPane(pane), -1), _panes.Count > 1 || docs.Count > 1);
        Add("Move to a new window", () => MoveToNewWindow(doc), AllDocuments.Count() > 1);
        menu.Items.Add(new Separator());
        Add("Copy path", () => Clipboard.SetText(doc.Item.Path));
        Add("Open in external editor", () => OpenExternally(doc));
        if (doc.IsEditable) Add("Save", () => doc.Save(), doc.IsDirty, "Ctrl+S");
        Add("Save as…", () => doc.SaveAs());
    }

    /// <summary>
    /// Opens the file in the app Windows uses for its type: a project's file itself, anything else as a read-only copy.
    /// A project's file with changes not saved here opens as it is on disk, so that's said.
    /// </summary>
    private static void OpenExternally(PreviewDocument doc)
    {
        try
        {
            if (doc.Item.DiskPath is { } disk)
            {
                ExternalEditor.Open(disk);
                if (doc.IsDirty)
                    _ = doc.ShowCard("Opened the saved file", $"The changes to {doc.FileName} here aren't saved yet, so the other editor shows the file as it was last saved.",
                        MessageKind.Info, primary: "OK");
            }
            else
                ExternalEditor.OpenCopy(doc.Item.Source, doc.Item.Path, doc.Item.Data);
        }
        catch (Exception ex)
        {
            _ = doc.ShowCard($"{doc.FileName} could not be opened in an external editor", ex.Message, MessageKind.Warning, primary: "OK");
        }
    }

    // ---------------------------------------------------------------- go to definition and history

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>
    /// Opens a definition: in place for the same file, in its tab when that group has it open already,
    /// otherwise in a new tab, or a new window when Settings says so. <paramref name="toSide"/> uses the other group.
    /// </summary>
    public Task NavigateAsync(PreviewDocument from, ScriptSymbol target, bool toSide) =>
        NavigateAsync(from, target.File, target.Offset, target.Name.Length, toSide);

    /// <summary>Opens a place in a script, as <see cref="NavigateAsync(PreviewDocument, ScriptSymbol, bool)"/> does, and selects what's there.</summary>
    public async Task NavigateAsync(PreviewDocument from, ScriptFile file, int offset, int length, bool toSide)
    {
        if (PaneOf(from) is not { } pane) return;
        var jumpedFrom = (from, from.CaretOffset);

        if (!toSide && file.Key == from.Item.Key)
        {
            Jumped(jumpedFrom);
            from.Reveal(offset, length);
            return;
        }
        bool inWindow = !toSide && !OpensTabs;
        var targetPane = toSide ? OtherPane(pane) : pane;
        if ((inWindow ? FindOpen(file.Key) : targetPane.Find(file.Key)) is { Host: { } host } open)
        {
            host.Jumped(jumpedFrom);
            host.ShowDocument(open);
            open.Reveal(offset, length);
            return;
        }

        byte[] data;
        try
        {
            data = await Task.Run(file.Read);
        }
        catch (Exception ex)
        {
            _ = from.ShowCard($"{file.Path} could not be read", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }
        var doc = CreateDocument(new PreviewItem(file.Path, file.Source, data, file, from.Item.Workspace));
        doc.Reveal(offset, length);
        if (inWindow)
        {
            var window = new PreviewWindow
            {
                _opener = this,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = Left + 32,
                Top = Top + 32,
                Width = Width,
                Height = Height,
            };
            window.Jumped(jumpedFrom);
            window.Add(doc);
            window.Show();
            return;
        }
        if (!_panes.Contains(targetPane)) return; // closed while the file was read
        Jumped(jumpedFrom);
        targetPane.Insert(doc, targetPane.Selected is { } selected ? targetPane.Documents.IndexOf(selected) + 1 : -1);
        SetActive(targetPane);
        targetPane.Select(doc);
    }

    /// <summary>A jump was made from here: it's where Back goes, and Forward starts over.</summary>
    private void Jumped((PreviewDocument Doc, int Offset) from)
    {
        _back.Push(from);
        _forward.Clear();
        UpdateHistoryButtons();
    }

    /// <summary>Alt+Left: back to where the last jump started, in whichever tab or window that file is now.</summary>
    public void GoBack() => Step(_back, _forward);

    /// <summary>Alt+Right: to where Back came from.</summary>
    public void GoForward() => Step(_forward, _back);

    private void Step(Stack<(PreviewDocument Doc, int Offset)> from, Stack<(PreviewDocument Doc, int Offset)> to)
    {
        if (from.Count > 0)
        {
            var (doc, offset) = from.Pop();
            if (Current is { } here) to.Push((here, here.CaretOffset));
            GoTo(doc, offset);
        }
        UpdateHistoryButtons();
    }

    /// <summary>
    /// Shows a place in a file: in its tab, wherever that is now, or, when the file has been closed since,
    /// opened again here.
    /// </summary>
    private void GoTo(PreviewDocument doc, int offset)
    {
        var open = doc.Host is not null ? doc : FindOpen(doc.Item.Key);
        if (open?.Host is { } host)
        {
            host.ShowDocument(open);
            open.MoveCaret(offset);
            return;
        }
        var reopened = CreateDocument(doc.Item);
        reopened.MoveCaret(offset);
        Add(reopened);
        BringForward();
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = CanGoBack;
        ForwardButton.IsEnabled = CanGoForward;
        HistoryButton.IsEnabled = CanGoBack || CanGoForward;
    }

    /// <summary>
    /// The places jumped from, newest first, then the ones Back has passed, to pick one to go to. A file closed
    /// since opens again.
    /// </summary>
    private void ShowHistory()
    {
        var entries = _back.Select(b => (Place: b, Back: true)).Concat(_forward.Select(f => (Place: f, Back: false)))
            .Take(40)
            .Select(e =>
            {
                var (doc, offset) = e.Place;
                var (line, text) = doc.LineAt(offset);
                string closed = doc.Host is null && FindOpen(doc.Item.Key) is null ? "  ·  closed, opens again" : "";
                return new QuickPickItem(e.Back ? "" : "", $"{doc.FileName} : {line}", $"{text.Trim()}  ·  {doc.Item.Source}{closed}", e.Place);
            })
            .ToList();
        if (entries.Count == 0) return;
        ShowPicker("Go back to…", query => [.. entries.Where(i => Matches(i, query))], item =>
        {
            var (doc, offset) = ((PreviewDocument Doc, int Offset))item.Tag!;
            if (Current is { } here && here != doc) Jumped((here, here.CaretOffset));
            GoTo(doc, offset);
        });

        static bool Matches(QuickPickItem item, string query) =>
            query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Detail.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Shows the box to type in over the top of the window.</summary>
    /// <param name="text">What the box starts with, selected so typing replaces it.</param>
    public void ShowPicker(string placeholder, Func<string, IReadOnlyList<QuickPickItem>> filter, Action<QuickPickItem> chosen, string? note = null,
        string? text = null)
    {
        foreach (var doc in AllDocuments) doc.CloseOverlays();
        Picker.Show(placeholder, filter, chosen, note, text);
    }

    // ---------------------------------------------------------------- go to symbol

    /// <summary>Ctrl+T: every class, function, field and enum value in the scripts searched, narrowed by what's typed.</summary>
    private async Task GoToSymbolAsync()
    {
        if (ScriptDocument() is not { } doc)
        {
            _ = Current?.ShowCard(null, "Go to symbol works from a script. Open a .ws file first.", MessageKind.Info, autoClose: true);
            return;
        }
        if (await doc.FinderAsync() is not { } finder) return;
        var symbols = finder.Layers.SelectMany(l => l.All)
            .Where(s => s.Kind is not (SymbolKind.Local or SymbolKind.Parameter))
            .DistinctBy(s => (s.File.Key, s.Offset))
            .ToList();
        ShowPicker("Go to symbol: a name, or Class.name", query => SymbolMatches(symbols, query), item =>
        {
            if (item.Tag is ScriptSymbol symbol && doc.Host is not null) _ = NavigateAsync(doc, symbol, toSide: false);
        }, "Searches the open archives, the game's scripts and any mod scripts already read.");
    }

    /// <summary>The best matches for a name, or for Class.name: the whole name first, then names starting with it, then the rest.</summary>
    internal static IReadOnlyList<QuickPickItem> SymbolMatches(IEnumerable<ScriptSymbol> symbols, string query)
    {
        if (query.Length == 0) return [];
        string? owner = null;
        string name = query;
        int dot = query.LastIndexOf('.');
        if (dot >= 0)
        {
            owner = query[..dot];
            name = query[(dot + 1)..];
        }
        var matches = new List<(int Score, ScriptSymbol Symbol)>();
        foreach (var s in symbols)
        {
            if (owner is not null && !(s.OwnerTitle ?? s.Owner ?? "").Contains(owner, StringComparison.OrdinalIgnoreCase)) continue;
            int score = name.Length == 0 ? 2
                : s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? 0
                : s.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? 1
                : s.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ? 2
                : InOrder(s.Name, name) ? 3
                : -1;
            if (score < 0) continue;
            // types and plain declarations before what annotations add
            matches.Add((score * 4 + (s.IsType ? 0 : s.Annotation is null ? 1 : 2), s));
        }
        return [.. matches
            .OrderBy(m => m.Score).ThenBy(m => m.Symbol.Name.Length).ThenBy(m => m.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .Select(m => new QuickPickItem(DefinitionPeek.KindGlyph(m.Symbol.Kind), m.Symbol.Title,
                $"{m.Symbol.File.Path} : {m.Symbol.Line}  ·  {m.Symbol.File.Source}", m.Symbol))];
    }

    /// <summary>Whether the letters typed appear in the name in the same order, so CrMenu finds CR4CraftingMenu.</summary>
    private static bool InOrder(string name, string typed)
    {
        int at = 0;
        foreach (char c in typed)
        {
            at = name.IndexOf(c.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            at++;
        }
        return true;
    }

    /// <summary>The script in use, or another one open here, for searches that need a script to start from.</summary>
    private PreviewDocument? ScriptDocument() =>
        Current is { CanNavigate: true } doc ? doc : AllDocuments.FirstOrDefault(d => d.CanNavigate);

    // ---------------------------------------------------------------- results

    private CancellationTokenSource? _search;
    private PreviewDocument? _resultsFrom;

    /// <summary>
    /// Fills the results panel from a search that runs in the background. A new search stops the one before.
    /// <paramref name="work"/> returns the lines and a short summary such as "12 references in 4 files".
    /// </summary>
    public async void ShowResults(PreviewDocument from, string title, string busy, Func<CancellationToken, (List<ResultRow> Rows, string Summary)> work,
        string empty, params (string Label, Action Run)[] actions)
    {
        _search?.Cancel();
        var cancel = new CancellationTokenSource();
        _search = cancel;
        _resultsShowing = cancel;
        _resultsFrom = from;
        OpenResultsPanel();
        if (Results.IsFind && title != FindTitle) Results.HideFind();
        Results.SetActions();
        Results.ShowBusy(title, busy);
        try
        {
            var (rows, summary) = await Task.Run(() => work(cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Results.ShowRows(title, summary, rows, empty);
            Results.SetActions(actions);
        }
        catch (OperationCanceledException)
        {
            // a newer search took over
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            var inner = ex is AggregateException { InnerException: { } e } ? e : ex;
            Results.ShowRows(title, "", [], inner is System.Text.RegularExpressions.RegexMatchTimeoutException
                ? "The search took too long and was stopped. Try a simpler pattern."
                : "The search failed: " + inner.Message);
        }
    }

    private const string FindTitle = "Find in files";

    private void OpenResultsPanel()
    {
        if (Results.Visibility == Visibility.Visible) return;
        Results.Visibility = Visibility.Visible;
        ResultsSplitter.Visibility = Visibility.Visible;
        ResultsSplitterRow.Height = new GridLength(12);
        ResultsRow.Height = new GridLength(Math.Max(180, ActualHeight * 0.3));
        ResultsRow.MinHeight = 90;
    }

    private void CloseResultsPanel()
    {
        _search?.Cancel();
        Results.Visibility = Visibility.Collapsed;
        ResultsSplitter.Visibility = Visibility.Collapsed;
        ResultsSplitterRow.Height = new GridLength(0);
        ResultsRow.MinHeight = 0;
        ResultsRow.Height = new GridLength(0);
        Current?.FocusEditor();
    }

    private void OpenResult(ResultRow row, bool toSide)
    {
        if (row.File is not { } file) return;
        var from = _resultsFrom is { } d && d.Host == this && PaneOf(d) is not null ? d : Current;
        if (from is null)
        {
            if (_isBench) OpenBenchFile(file, row.Offset, row.Length);
            return;
        }
        _ = NavigateAsync(from, file, row.Offset, row.Length, toSide);
    }

    /// <summary>Ctrl+Shift+F: the find box in the results panel, filled with the selected text.</summary>
    private void FindInFiles()
    {
        OpenResultsPanel();
        Results.ShowFind(Current?.SelectedText is { Length: > 0 and < 200 } selected && !selected.Contains('\n') ? selected : null);
    }

    private async void Results_FindRequested(System.Text.RegularExpressions.Regex pattern, string text)
    {
        if (ScriptDocument() is not { } doc)
        {
            Results.ShowRows(FindTitle, "", [], "Find in files searches from a script. Open a .ws file first.");
            return;
        }
        if (await doc.FinderAsync() is not { } finder) return;
        var layers = finder.Layers;
        var file = doc.Item.Script!;
        ShowResults(doc, FindTitle, $"Looking for '{text}'…", cancel =>
        {
            var hits = ScriptSearch.Text(pattern, file, layers, cancel);
            return (ResultRow.ByFile(hits), Summary(hits, "match", "matches"));
        }, $"Nothing matches '{text}' in the scripts searched.");
    }

    /// <summary>"12 references in 4 files", noting when the list stopped at the most it shows.</summary>
    public static string Summary(IReadOnlyCollection<ScriptHit> hits, string one, string many)
    {
        int files = hits.Select(h => h.File.Key).Distinct().Count();
        string text = $"{hits.Count} {(hits.Count == 1 ? one : many)} in {files} {(files == 1 ? "file" : "files")}";
        return hits.Count >= ScriptSearch.MaxHits ? $"{text} (stopped at the first {ScriptSearch.MaxHits})" : text;
    }

    // ---------------------------------------------------------------- comparing

    /// <summary>Files being compared, each with the file it's compared with, both ways round.</summary>
    private static readonly Dictionary<PreviewDocument, PreviewDocument> Compared = [];

    private static IEnumerable<PreviewDocument> OtherFiles(PreviewDocument doc) =>
        OpenWindows.SelectMany(w => w.AllDocuments).Where(d => d != doc && d.Item.Key != doc.Item.Key).DistinctBy(d => d.Item.Key);

    public bool HasOtherFiles(PreviewDocument doc) => OtherFiles(doc).Any();

    /// <summary>Picks another open file to compare with, as the older version.</summary>
    public void PickFileToCompare(PreviewDocument doc)
    {
        var others = OtherFiles(doc).ToList();
        ShowPicker($"Compare {doc.FileName} with…", query =>
            [.. others.Where(d => query.Length == 0 || d.Item.Path.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || d.Item.Source.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(d => new QuickPickItem("", d.FileName, $"{d.Item.Path}  ·  {d.Item.Source}", d))],
            item =>
            {
                if (item.Tag is PreviewDocument other) _ = CompareAsync(doc, other);
            }, "The file picked is shown as the older version, on the other side.");
    }

    /// <summary>Opens <paramref name="older"/> beside <paramref name="doc"/>, or finds it there, and compares the two.</summary>
    public async Task CompareWithFileAsync(PreviewDocument doc, ScriptFile older)
    {
        if (PaneOf(doc) is not { } pane) return;
        var target = OtherPane(pane);
        if (target.Find(older.Key) is not { } open)
        {
            byte[] data;
            try
            {
                data = await Task.Run(older.Read);
            }
            catch (Exception ex)
            {
                _ = doc.ShowCard($"{older.Path} could not be read", ex.Message, MessageKind.Warning, primary: "OK");
                return;
            }
            if (!_panes.Contains(target)) return;
            open = CreateDocument(new PreviewItem(older.Path, older.Source, data, older, doc.Item.Workspace));
            target.Insert(open);
        }
        target.Select(open, focus: false);
        await CompareAsync(doc, open);
    }

    /// <summary>
    /// Opens two files side by side, <paramref name="older"/> on the other side of the split, with the lines that differ
    /// marked: Compare in the main window's file list.
    /// </summary>
    public static async Task ShowComparedAsync(PreviewItem newer, PreviewItem older, Window owner)
    {
        Show(newer, owner);
        if (FindOpen(newer.Key) is not { Host: { } host } doc || host.PaneOf(doc) is not { } pane) return;
        if (FindOpen(older.Key) is not { } open)
        {
            open = CreateDocument(older);
            host.OtherPane(pane).Insert(open);
        }
        if (open.Host == host && host.PaneOf(open) is { } other) other.Select(open, focus: false);
        await host.CompareAsync(doc, open);
    }

    /// <summary>Marks the lines that differ in both files, side by side when they're in the same window.</summary>
    private async Task CompareAsync(PreviewDocument newer, PreviewDocument older)
    {
        StopComparing(newer);
        StopComparing(older);
        if (older.Host == this && PaneOf(older) == PaneOf(newer) && PaneOf(newer) is { } pane)
            MoveDocument(older, OtherPane(pane), -1);
        if (newer.Host is { } host) host.ShowDocument(newer);

        static string[] Lines(string text) => [.. text.Split('\n').Select(l => l.TrimEnd('\r'))];
        string oldText = older.Text, newText = newer.Text;
        var (oldKinds, newKinds) = await Task.Run(() => LineDiff.Compare(Lines(oldText), Lines(newText)));
        if (older.Host is null || newer.Host is null) return;
        older.SetDiff(oldKinds);
        newer.SetDiff(newKinds);
        Compared[newer] = older;
        Compared[older] = newer;

        int changed = newKinds.Count(k => k == DiffKind.Changed), added = newKinds.Count(k => k == DiffKind.Added);
        int removed = oldKinds.Count(k => k == DiffKind.Removed);
        string message = changed + added + removed == 0
            ? "They're the same, apart from spaces at the ends of lines."
            : $"{Count(changed, "changed")}, {Count(added, "added")} and {Count(removed, "removed")} compared with {older.FileName}. "
              + "F8 and Shift+F8 step through the differences.";
        _ = newer.ShowCard(changed + added + removed == 0 ? $"Same as {older.FileName}" : "Differences marked", message, MessageKind.Info, primary: "OK");

        static string Count(int n, string what) => $"{n} {(n == 1 ? "line" : "lines")} {what}";
    }

    /// <summary>Takes the marks off a file and the file it was compared with.</summary>
    public void StopComparing(PreviewDocument doc)
    {
        doc.ClearDiff();
        if (!Compared.Remove(doc, out var other)) return;
        other.ClearDiff();
        Compared.Remove(other);
    }

    // ---------------------------------------------------------------- header and keys

    public void OnSelectionChanged(PreviewPane pane)
    {
        if (pane == _active) UpdateHeader();
    }

    private void UpdateHeader()
    {
        if (_isBench) BenchEmpty.Visibility = Current is null ? Visibility.Visible : Visibility.Collapsed;
        if (Current is not { } doc)
        {
            if (_isBench) UpdateBenchTitle();
            return;
        }
        Title = $"{(doc.IsDirty ? "● " : "")}{doc.FileName} - {(_isBench ? BenchTitle : "Preview")}";
        FileNameText.Text = doc.FileName;
        DetailText.Text = doc.Detail;

        SaveText.Text = doc.IsEditable ? "Save" : "Save as";
        SaveButton.IsEnabled = !doc.IsEditable || doc.IsDirty;
        SaveButton.ToolTip = !doc.IsEditable ? "Save a copy of this file (Ctrl+S)"
            : doc.IsDirty ? "Save the changes to this file (Ctrl+S). Ctrl+Shift+S saves every file"
            : "No changes to save";
        EditButton.Visibility = CanEditAsProject(doc) ? Visibility.Visible : Visibility.Collapsed;
        GoToSymbolButton.IsEnabled = ScriptDocument() is not null;
        SnippetButton.IsEnabled = doc.CanNavigate;
        BuildButton.IsEnabled = ProjectWorkspace.Current is not null;
        BuildButton.ToolTip = ProjectWorkspace.Current is null ? "Build the mod (Ctrl+Shift+B). Open a project to build it" : "Build the mod (Ctrl+Shift+B)";

        // a project's file says so, with the project's name, beside the file name
        var owner = ProjectOf(doc);
        bool project = owner is not null;
        ProjectBadge.Visibility = project ? Visibility.Visible : Visibility.Collapsed;
        ProjectBadgeText.Text = owner is null ? "" : $"PROJECT · {owner.Owner.ToUpperInvariant()}";
        ProjectBadge.ToolTip = owner is null ? null : $"In the project {owner.Owner}: its files can be edited, saved and compiled\n{owner.Project?.FilePath}";
    }

    /// <summary>A file's changes were saved, or it was changed after a save: its tab and, when it's in use, the header show it.</summary>
    public void OnDirtyChanged(PreviewDocument doc)
    {
        if (doc == Current) UpdateHeader();
    }

    // ---------------------------------------------------------------- unsaved changes

    /// <summary>Set once the main window has asked about unsaved changes, so the previews close without asking again.</summary>
    public static bool SkipUnsavedCheck { get; set; }

    private bool _closeConfirmed;

    private static IEnumerable<PreviewDocument> AllOpenDocuments() => OpenWindows.SelectMany(w => w.AllDocuments);

    /// <summary>Whether another open view shows the same text, so closing this one loses nothing.</summary>
    private static bool HasOtherView(PreviewDocument doc) => AllOpenDocuments().Any(d => d != doc && d.SharesTextWith(doc));

    /// <summary>Files with unsaved changes that closing this window would lose: not also shown in another window.</summary>
    private List<PreviewDocument> UnsavedHere()
    {
        var elsewhere = OpenWindows.Where(w => w != this).SelectMany(w => w.AllDocuments).ToList();
        var unsaved = new List<PreviewDocument>();
        foreach (var doc in AllDocuments.Where(d => d.IsDirty))
            if (!elsewhere.Any(d => d.SharesTextWith(doc)) && !unsaved.Any(d => d.SharesTextWith(doc))) unsaved.Add(doc);
        return unsaved;
    }

    /// <summary>Every file with unsaved changes in any preview window, one view of each.</summary>
    public static List<PreviewDocument> UnsavedDocuments()
    {
        var unsaved = new List<PreviewDocument>();
        foreach (var doc in AllOpenDocuments().Where(d => d.IsDirty))
            if (!unsaved.Any(d => d.SharesTextWith(doc))) unsaved.Add(doc);
        return unsaved;
    }

    /// <summary>Saves each file, stopping at one that couldn't be saved. False when one wasn't.</summary>
    public static bool SaveAll(IEnumerable<PreviewDocument> docs)
    {
        var saved = new List<PreviewDocument>();
        foreach (var doc in docs.ToList())
        {
            if (saved.Any(d => d.SharesTextWith(doc))) continue;
            if (!doc.Save()) return false;
            saved.Add(doc);
        }
        return true;
    }

    /// <summary>The names of some files, for a message: "a.ws, b.ws and 3 more".</summary>
    public static string Names(IReadOnlyList<PreviewDocument> docs) =>
        docs.Count <= 4 ? string.Join(", ", docs.Select(d => d.FileName))
            : string.Join(", ", docs.Take(3).Select(d => d.FileName)) + $" and {docs.Count - 3} more";

    /// <summary>The window is closing with changes not saved: one file asks for itself, several are asked about together.</summary>
    private async Task ConfirmCloseWindowAsync()
    {
        var unsaved = UnsavedHere();
        bool close;
        if (unsaved.Count == 1)
        {
            ShowDocument(unsaved[0]);
            close = await unsaved[0].ConfirmCloseAsync();
        }
        else
        {
            if (Current is not { } doc) return;
            var choice = await doc.Ask($"Save the changes to {unsaved.Count} files?", Names(unsaved) + ". The changes are lost if you don't save them.",
                MessageKind.Question, "Save all", "Don't save");
            close = choice == MessageChoice.Secondary || choice == MessageChoice.Primary && SaveAll(unsaved);
        }
        if (!close) return;
        _closeConfirmed = true;
        Close();
    }

    // ---------------------------------------------------------------- script projects

    /// <summary>The open project's files, when the file is one of the project's. Null for anything else, including a script that is loose.</summary>
    public static ScriptFolder? ProjectOf(PreviewDocument doc) =>
        doc.Item.DiskPath is { } disk && ProjectWorkspace.IsMember(disk) ? (Application.Current.MainWindow as MainWindow)?.ProjectArchive : null;

    /// <summary>The files open in previews that <paramref name="member"/> accepts, in the order they were opened, and the one in front.</summary>
    public static (List<string> Files, string? Active) OpenFilesIn(Func<string, bool> member)
    {
        var files = AllOpenDocuments().Select(d => d.Item.DiskPath).OfType<string>().Where(member)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? active = OpenWindows.FirstOrDefault()?.Current?.Item.DiskPath;
        return (files, active is not null && member(active) ? active : null);
    }

    /// <summary>Adds the file to the open project, asked for by the file's own prompt.</summary>
    public Task<bool> AddToProjectAsync(PreviewDocument doc) =>
        Application.Current.MainWindow is MainWindow main && doc.Item.DiskPath is { } disk ? main.AddToProjectAsync([disk]) : Task.FromResult(false);

    /// <summary>Whether the file can be copied into a project to change it: it has a game path, and the main window is there to do it.</summary>
    public bool CanCopyToProject(PreviewDocument doc) => doc.Item.GamePath is not null && Application.Current.MainWindow is MainWindow;

    /// <summary>Copies a read-only file (the game's, an installed mod's, or one in an archive) into the project at its game path, and opens the copy.</summary>
    public Task<bool> CopyToProjectAsync(PreviewDocument doc) =>
        Application.Current.MainWindow is MainWindow main && doc.Item.GamePath is { } gamePath
            ? main.CopyToProjectAsync([(gamePath, () => doc.Item.Data)], open: true)
            : Task.FromResult(false);

    /// <summary>Starts a project for the scripts the file is in. True once there is a project open that has the file.</summary>
    public async Task<bool> CreateProjectForAsync(PreviewDocument doc)
    {
        if (Application.Current.MainWindow is not MainWindow main || doc.Item.DiskPath is not { } disk) return false;
        string drive = Path.GetPathRoot(disk)!;
        string scripts = ScriptFolder.RootOf(disk, drive);
        if (scripts.Equals(drive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) scripts = Path.GetDirectoryName(disk)!;
        await main.CreateProjectFromScriptsAsync(scripts);
        return ProjectWorkspace.IsMember(disk);
    }

    /// <summary>Whether the file is a decompiled script, whose mod's scripts can be saved as a project to edit.</summary>
    public bool CanEditAsProject(PreviewDocument doc) =>
        doc.Item.Script is { } script && doc.Item.Workspace?.ArchiveOf(script) is ScriptArchive && Application.Current.MainWindow is MainWindow;

    /// <summary>
    /// Saves a decompiled mod's scripts, all but all.ws, to a scripts folder in a folder the user picks, opens that as a
    /// script project in the main window in place of the blob, and swaps every open tab of the blob for the project's file.
    /// </summary>
    public async Task EditAsProjectAsync(PreviewDocument doc)
    {
        if (doc.Item.Script is not { } script || doc.Item.Workspace?.ArchiveOf(script) is not ScriptArchive blob
            || Application.Current.MainWindow is not MainWindow main) return;

        var dialog = new OpenFolderDialog { Title = $"Choose the project folder for {blob.Owner}. Its scripts go in a scripts folder inside it" };
        if (dialog.ShowDialog(this) != true) return;
        await SaveAsProjectAsync(doc, blob, main, dialog.FolderName);
    }

    /// <summary>The work of <see cref="EditAsProjectAsync"/> once the folder is chosen.</summary>
    internal async Task SaveAsProjectAsync(PreviewDocument doc, ScriptArchive blob, MainWindow main, string folder)
    {
        if (FileSafety.Protection(Path.Combine(folder, "_")) is { } protection)
        {
            _ = doc.ShowCard("Choose a folder of your own", protection + " A project goes somewhere of your own, such as Documents.", MessageKind.Warning, primary: "OK");
            return;
        }
        string scripts = Path.Combine(folder, "scripts");
        if (Directory.Exists(scripts) && Directory.EnumerateFileSystemEntries(scripts).Any())
        {
            _ = doc.ShowCard("That folder has a scripts folder already",
                $"Choose an empty folder, or one without a scripts folder, so nothing in {scripts} is overwritten.", MessageKind.Warning, primary: "OK");
            return;
        }
        if (WorkbenchProject.FileIn(folder) is { } existing)
        {
            _ = doc.ShowCard("That folder is a project already",
                $"{Path.GetFileName(existing)} is in it. Choose another folder for {blob.Owner}.", MessageKind.Warning, primary: "OK");
            return;
        }

        var entries = blob.Entries.Where(e => !e.Path.Equals(@"scripts\all.ws", StringComparison.OrdinalIgnoreCase)).ToList();
        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    string path = Path.Combine(folder, entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    FileSafety.CheckWrite(path);
                    File.WriteAllBytes(path, entry.Read(null));
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = doc.ShowCard("The scripts could not be saved", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }

        WorkbenchProject made;
        try
        {
            made = WorkbenchProject.Create(folder, blob.Owner, "scripts");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = doc.ShowCard("The project could not be made", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }
        if (!await main.OpenProjectAsync(made.FilePath) || main.ProjectArchive is not { } project) return;
        main.CloseArchive(blob.FilePath);
        foreach (var window in OpenWindows.ToList()) window.SwapForProject(blob, project, main);
        _ = Current?.ShowCard($"{blob.Owner} is a project now",
            $"{entries.Count} scripts were saved to {scripts} and {Path.GetFileName(made.FilePath)} keeps track of them. The tabs show the project's files: they can be edited and saved. "
            + "Compile builds them into a .rsblob with REDkit's wcc_lite, or the built-in compiler.", MessageKind.Info, primary: "OK");
    }

    /// <summary>Puts the project's version of each of the blob's files open here in its place, keeping the caret and scroll.</summary>
    private void SwapForProject(IArchive blob, ScriptFolder project, MainWindow main)
    {
        foreach (var pane in _panes.ToList())
            foreach (var old in pane.Documents)
            {
                if (!string.Equals(old.Item.Script?.SourceKey, blob.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
                var entry = project.Entries.FirstOrDefault(e => e.Path.Equals(old.Item.Path, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue; // all.ws has no file of its own
                var doc = CreateDocument(main.PreviewItemFor(entry, entry.Read(null)));
                doc.CopyViewFrom(old);
                pane.Replace(old, doc);
            }
        UpdateHeader();
    }

    // ---------------------------------------------------------------- compiling

    /// <summary>The compile running from this window, if one is.</summary>
    private CancellationTokenSource? _compile;

    /// <summary>The compiler that would build a project now: wcc_lite when it's set, else the built-in one when the game's scripts are found.</summary>
    public static CompilerChoice? CurrentCompiler() =>
        CompilerChoice.Pick(App.Settings.WccLitePath, App.Settings.GameFolder, App.Settings.PreferBuiltInCompiler);

    /// <summary>What the results panel shows now: a search, or a compile. Progress from an older one is dropped.</summary>
    private object? _resultsShowing;

    /// <summary>
    /// Saves the project's changed files, then compiles the project with wcc_lite (or the built-in compiler) into the file chosen for it
    /// (asked the first time, or when <paramref name="chooseOutput"/>), and lists its warnings and errors under the files. A
    /// <paramref name="check"/> compiles into a file of its own that isn't kept, only to say what's wrong: Script Bench's Compile.
    /// </summary>
    /// <param name="doc">The tab it's compiled from, which messages show in; null in Script Bench with no tab of the project's open.</param>
    /// <param name="folder">The project's files, when there's no tab to tell them by.</param>
    private async Task CompileAsync(PreviewDocument? doc, bool chooseOutput, ScriptFolder? folder = null, bool check = false)
    {
        if ((doc is null ? folder : ProjectOf(doc)) is not { } project || _compile is not null) return;
        Task<bool> Card(string title, string message, MessageKind kind, string primary, string? secondary = null) =>
            doc?.ShowCard(title, message, kind, primary: primary, secondary: secondary) ?? BenchMessage.Show(title, message, kind, primary, secondary);
        if (CurrentCompiler() is not { } compiler)
        {
            _ = Card("Nothing to compile with", "Set REDkit's wcc_lite.exe in Settings (it's in REDkit's bin\\x64_RedKit folder), or set the game folder there so the built-in compiler can read the game's scripts.",
                MessageKind.Info, "OK");
            return;
        }

        var unsaved = UnsavedDocuments().Where(d => ProjectOf(d)?.FilePath.Equals(project.FilePath, StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (unsaved.Count > 0)
        {
            if (!await Card(unsaved.Count == 1 ? $"Save {unsaved[0].FileName} first?" : $"Save {unsaved.Count} files first?",
                    $"{(compiler.Kind == CompilerKind.WccLite ? "wcc_lite" : "The compiler")} compiles the files as they are on disk. " + (unsaved.Count == 1 ? "" : Names(unsaved) + " have changes."),
                    MessageKind.Question, "Save and compile", "Cancel"))
                return;
            if (!SaveAll(unsaved)) return;
        }

        var definition = project.Project!;
        string? output = check ? Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "check-" + Guid.NewGuid().ToString("N"), "blob.rsblob")
            : definition.CompileOutputPath;
        if (!check && (chooseOutput || output is null || !Directory.Exists(Path.GetDirectoryName(output))))
        {
            string start = definition.CompileOutputPath is { } before && Path.GetDirectoryName(before) is { } dir
                && Directory.Exists(dir) ? dir : definition.Root;
            var dialog = new SaveFileDialog
            {
                Title = $"Where should {project.Owner}'s compiled scripts go?",
                FileName = "precompiled.rsblob",
                InitialDirectory = start,
                Filter = "Compiled scripts (*.rsblob)|*.rsblob",
            };
            if (dialog.ShowDialog(this) != true) return;
            output = dialog.FileName;
            definition.CompileOutput = definition.ToStored(output);
            try
            {
                definition.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = Card("The project could not be saved", $"Compiling goes ahead, but {Path.GetFileName(definition.FilePath)} wasn't updated with where the scripts go. {ex.Message}", MessageKind.Warning, "OK");
            }
        }

        var cancel = _compile = new CancellationTokenSource();
        _resultsShowing = cancel;
        _resultsFrom = doc;
        foreach (var window in OpenWindows) window.UpdateHeader();
        UpdateBenchStatus();
        string title = $"Compile {project.Owner}";
        OpenResultsPanel();
        if (Results.IsFind) Results.HideFind();
        Results.SetActions();
        Results.ShowBusy(title, compiler.Kind == CompilerKind.WccLite ? "Starting wcc_lite…" : "Starting the built-in compiler…");
        string slow = compiler.Kind == CompilerKind.WccLite ? "  It takes about a minute." : "";
        var progress = new Progress<string>(text =>
        {
            if (_resultsShowing == cancel) Results.ShowBusy(title, text + slow);
        });
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await compiler.CompileAsync(project.FilePath, output!, progress, cancel.Token, Path.GetFileName(definition.BuiltModPath));
            ShowCompileResult(title, project, result, output!, watch.Elapsed, compiler, check);
        }
        catch (OperationCanceledException)
        {
            // the window closed
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (_resultsShowing == cancel) Results.ShowRows(title, "Failed", [], $"{compiler.Name} couldn't be run: " + ex.Message);
        }
        finally
        {
            _compile = null;
            foreach (var window in OpenWindows) window.UpdateHeader();
            UpdateBenchStatus();
            if (check) TryDelete(Path.GetDirectoryName(output!)!);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a check's left-over in the temp folder
        }
    }

    /// <summary>
    /// Project > Compile scripts: compiles the open project in a preview window, where its warnings and errors are listed, each
    /// opening its line. One of the project's files that's open already is used, or its first script is opened for it.
    /// </summary>
    public static async Task CompileProjectAsync(MainWindow main, bool chooseOutput)
    {
        if (!chooseOutput)
        {
            if (BenchFor(main) is { } bench)
            {
                bench.BringForward();
                await bench.BenchCompileAsync();
            }
            return;
        }
        if (await ProjectDocumentAsync(main) is not { Host: { } host } doc) return;
        host.BringForward();
        await host.CompileAsync(doc, chooseOutput);
    }

    /// <summary>A build whose scripts didn't compile: the errors in a preview window, as Compile scripts lists them.</summary>
    public static async Task ShowCompileErrorsAsync(MainWindow main, CompileResult result, string output, CompilerChoice compiler)
    {
        if (await ProjectDocumentAsync(main) is not { Host: { } host } doc || ProjectOf(doc) is not { } project) return;
        host.BringForward();
        host._resultsShowing = result;
        host._resultsFrom = doc;
        host.ShowCompileResult($"Build {project.Owner}", project, result, output, TimeSpan.Zero, compiler);
    }

    /// <summary>A view of one of the open project's scripts: an open one, or its first script opened for it. Null when it has none.</summary>
    private static async Task<PreviewDocument?> ProjectDocumentAsync(MainWindow main)
    {
        if (main.ProjectArchive is not { } archive) return null;
        var open = OpenWindows.SelectMany(w => w.AllDocuments)
            .FirstOrDefault(d => ProjectOf(d) is { } p && p.FilePath.Equals(archive.FilePath, StringComparison.OrdinalIgnoreCase));
        if (open is not null) return open;
        if (archive.Entries.OfType<LooseScriptEntry>().FirstOrDefault(e => e.IsScript) is not { } entry)
        {
            await main.ShowMessage("The project has no scripts yet", $"Add .ws files to {archive.FilePath} and they're compiled with the project.", MessageKind.Info);
            return null;
        }
        var item = main.PreviewItemFor(entry, await Task.Run(() => entry.Read(null)));
        Show(item, main);
        return FindOpen(item.Key);
    }

    /// <summary>The compiler's warnings and errors, grouped by file, each opening its line. The log is a button away.</summary>
    private void ShowCompileResult(string title, ScriptFolder project, CompileResult result, string output, TimeSpan took, CompilerChoice compiler, bool check = false)
    {
        if (Results.Visibility != Visibility.Visible) OpenResultsPanel();
        int errors = result.Messages.Count(m => m.IsError), warnings = result.Messages.Count - errors;
        var rows = new List<ResultRow>();
        foreach (var group in result.Messages.GroupBy(m => m.File ?? "").OrderBy(g => g.Key.Length == 0))
        {
            var list = group.OrderBy(m => m.Line).ToList();
            var entry = group.Key.Length == 0 ? null : project.Entries.FirstOrDefault(e =>
                ((LooseScriptEntry)e).FullPath.Equals(Path.GetFullPath(Path.Combine(project.FilePath, group.Key)), StringComparison.OrdinalIgnoreCase));
            var file = entry is null ? null : ScriptWorkspace.FileFor(entry);
            string folder = Path.GetDirectoryName(group.Key) ?? "";
            rows.Add(new ResultRow
            {
                IsHeader = true,
                Glyph = ResultRow.FileGlyph,
                Before = group.Key.Length == 0 ? "File not named" : Path.GetFileName(group.Key),
                Detail = group.Key.Length == 0 ? $"    {compiler.Name} didn't say which file, and it couldn't be worked out. The line numbers are right"
                    : folder.Length > 0 ? "    " + folder : "",
                Count = list.Count.ToString(),
            });
            string[] lines = file is null ? [] : file.Text.Split('\n');
            foreach (var m in list)
            {
                int offset = 0;
                for (int i = 0; i < m.Line - 1 && i < lines.Length; i++) offset += lines[i].Length + 1;
                int length = m.Line - 1 < lines.Length ? lines[m.Line - 1].TrimEnd('\r').Length : 0;
                rows.Add(new ResultRow
                {
                    IsCode = true,
                    LineText = m.Line.ToString(),
                    Before = m.Text.Replace("\n", "  "),
                    Label = (m.IsError ? "error" : "warning") + (m.Guessed ? ", file guessed" : ""),
                    File = file,
                    Offset = offset,
                    Length = length,
                });
            }
        }

        string seconds = took.TotalSeconds < 10 ? $"{took.TotalSeconds:0.0} s" : $"{took.TotalSeconds:0} s";
        string summary = result.Success
            ? warnings == 0 ? $"Compiled in {seconds}" : $"Compiled in {seconds}, {Format.Count(warnings, "warning")}"
            : errors > 0 ? $"Failed: {Format.Count(errors, "error")}" + (warnings > 0 ? $", {Format.Count(warnings, "warning")}" : "")
            : "Failed";
        string empty = result.Success ? check ? "Compiled with no warnings. Build puts the compiled scripts in the mod." : $"Compiled with no warnings, and saved to {output}."
            : result.Failure ?? $"{compiler.Name} didn't compile the scripts.";
        Results.ShowRows(title, summary, rows, empty);

        var actions = new List<(string, Action)>();
        if (result.Success && !check)
            actions.Add(("Show in folder", () => Process.Start("explorer.exe", $"/select,\"{output}\"")));
        string logName = compiler.Kind == CompilerKind.WccLite ? "wcc_lite.log" : "compiler.log";
        actions.Add(("Show log", () => ShowAlone(new PreviewItem(logName, title, System.Text.Encoding.UTF8.GetBytes(result.Log)), this)));
        Results.SetActions(result.Success ? (check ? "Checked, nothing kept" : $"Saved to {output}") + (compiler.Kind == CompilerKind.BuiltIn ? ", compiled by the built-in compiler" : "")
            : compiler.Kind == CompilerKind.WccLite ? "Nothing was saved. wcc_lite's own log has every line it wrote:" : "Nothing was saved. The compiler's log lists any game scripts it couldn't read:", [.. actions]);
    }


    private void Split_Click(object sender, RoutedEventArgs e) => ToggleSplit();

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();

    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();

    private void SplitRight_Click(object sender, RoutedEventArgs e) => SetSplitDirection("Right");

    private void SplitDown_Click(object sender, RoutedEventArgs e) => SetSplitDirection("Down");

    private static void SetSplitDirection(string direction)
    {
        App.Settings.PreviewSplit = direction;
        App.Settings.Save();
        ApplySettings();
    }

    private void GoToSymbol_Click(object sender, RoutedEventArgs e) => _ = GoToSymbolAsync();

    private void FindInFiles_Click(object sender, RoutedEventArgs e) => FindInFiles();

    private void Snippet_Click(object sender, RoutedEventArgs e) => Current?.InsertSnippet();

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow main && ProjectWorkspace.Current is not null) _ = main.BuildModAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Current?.Copy();

    private void Save_Click(object sender, RoutedEventArgs e) => Current?.Save();

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } doc) _ = EditAsProjectAsync(doc);
    }

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
        if (Picker.IsOpen) return; // it has the keys while it's open
        if (_isBench && BenchPanel.IsKeyboardFocusWithin && key is Key.Escape or Key.Enter or Key.F2 or Key.Delete) return; // the tree's and its boxes'
        if (_isBench && key == Key.F7 && mods == ModifierKeys.None)
        {
            _ = BenchCompileAsync();
            e.Handled = true;
            return;
        }
        if (key == Key.Escape)
        {
            // in the results: closes them. In a file: a message first, then the search bar, then a window showing just one file (not Script Bench)
            if (Results.IsKeyboardFocusWithin) CloseResultsPanel();
            else if (doc?.HandleEscape() != true && AllDocuments.Count() == 1 && !_isBench) Close();
        }
        else if (key == Key.S && mods == ModifierKeys.Control)
            doc?.Save();
        else if (key == Key.S && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            SaveAll(AllOpenDocuments().Where(d => d.IsDirty));
        else if (key == Key.B && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            // builds the mod, as Project > Build does in the main window
            if (Application.Current.MainWindow is MainWindow main && ProjectWorkspace.Current is not null) _ = main.BuildModAsync();
        }
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
        else if (key == Key.Right && mods == ModifierKeys.Alt)
            GoForward();
        else if (key == Key.T && mods == ModifierKeys.Control)
            _ = GoToSymbolAsync();
        else if (key == Key.T && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            ReopenClosed();
        else if (key == Key.F && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            FindInFiles();
        else if (key == Key.F4 && mods is ModifierKeys.None or ModifierKeys.Shift && Results.IsVisible)
        {
            if (Results.Step(mods == ModifierKeys.Shift ? -1 : 1) is { } row) OpenResult(row, toSide: false);
        }
        else
        {
            doc?.HandleKey(e);
            return;
        }
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e) => Current?.HandleKeyUp(e);

    public Task ShowMessage(string? title, string message, MessageKind kind) =>
        Current?.ShowCard(title, message, kind, primary: "OK") ?? (_isBench ? BenchMessage.Show(title, message, kind, primary: "OK") : Task.CompletedTask);
}
