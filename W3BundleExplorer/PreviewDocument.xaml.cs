using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using W3BundleExplorer.Core;
using W3BundleExplorer.Core.Scripts.Navigation;
using W3BundleExplorer.Highlighting;

namespace W3BundleExplorer;

/// <summary>A file to preview: its bytes, and for a .ws file, where its names' definitions are looked up.</summary>
/// <param name="Script">For a .ws file: which file it is, so its names can lead to their definitions.</param>
/// <param name="Workspace">The scripts definitions are looked up in.</param>
public sealed record PreviewItem(string Path, string Source, byte[] Data, ScriptFile? Script = null, ScriptWorkspace? Workspace = null)
{
    /// <summary>Tells apart open files, so opening one that's already open shows that one.</summary>
    public string Key => Script?.Key ?? $"{Source}|{Path}";

    public string Name => System.IO.Path.GetFileName(Path);

    public bool IsScript => System.IO.Path.GetExtension(Path).Equals(".ws", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One file in a <see cref="PreviewWindow"/>: the code view with its search bar, go to definition, the
/// definition popup and in-place messages. The window shows it in a tab and handles where definitions open.
/// </summary>
public partial class PreviewDocument : UserControl
{
    private readonly SearchPanel _search;
    private bool _dark;

    // go to definition, for scripts
    private readonly ScriptWorkspace? _workspace;
    private readonly LinkMarker _link = new();
    private DefinitionFinder? _finder;
    private IReadOnlyList<ScriptIndex> _finderLayers = [];
    private ParsedScript? _doc;
    private (int Start, int Length) _probed; // the last word checked for a link
    private bool _probedFound;
    private (int Offset, int Length)? _pendingReveal;
    private double? _pendingScroll;

    // the definition popup
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private (int Start, int Length) _peekWord;
    private int _peekVersion;
    private string[]? _lines;

    public PreviewItem Item { get; }

    /// <summary>The window showing it; null once it's closed.</summary>
    public PreviewWindow? Host { get; internal set; }

    public PreviewDocument(PreviewItem item)
    {
        InitializeComponent();
        Item = item;

        Editor.Options.IndentationSize = 4; // tab width
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);
        Editor.Text = TextDecoding.Decode(item.Data);
        _search = SearchPanel.Install(Editor.TextArea);

        _workspace = item.Workspace;
        if (item.Script is not null && _workspace is not null)
            SetUpDefinitions();
        else
            Editor.ContextMenu = null;

        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            _ = ShowPeekAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseOverlays();
        };

        // the theme's brushes are only found once it's in a window
        bool styled = false;
        Loaded += (_, _) =>
        {
            if (!styled)
            {
                styled = true;
                ApplyThemeLook();
            }
            if (_pendingReveal is { } r)
            {
                _pendingReveal = null;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealNow(r.Offset, r.Length));
            }
            if (_pendingScroll is { } y)
            {
                _pendingScroll = null;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Editor.ScrollToVerticalOffset(y));
            }
        };
    }

    public string FileName => Item.Name;

    /// <summary>The line under the file name in the window: where it comes from, its path and its size.</summary>
    public string Detail => $"{Item.Source}  ·  {Item.Path}  ·  {Format.Bytes(Item.Data.Length)}";

    public string Text => Editor.Text;

    public int CaretOffset => Editor.CaretOffset;

    private void ApplyThemeLook()
    {
        _dark = IsDarkTheme();
        if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
        {
            var selection = new SolidColorBrush(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B));
            selection.Freeze();
            Editor.TextArea.SelectionBrush = selection;
            Editor.TextArea.SelectionBorder = null;
            Editor.TextArea.SelectionForeground = null;

            var marker = new SolidColorBrush(Color.FromArgb(0x55, accent.Color.R, accent.Color.G, accent.Color.B));
            marker.Freeze();
            _search.MarkerBrush = marker;
        }

        // WitcherScript is coloured with the grammar from WebSpam's witcherscript-vscode extension
        if (Item.IsScript)
            Editor.TextArea.TextView.LineTransformers.Insert(0, new WitcherScriptColorizer(Editor.Document, _dark));

        _link.Brush = TryFindResource("AccentTextFillColorPrimaryBrush") as Brush;
        if (_link.Brush is SolidColorBrush link)
        {
            var faint = new SolidColorBrush(Color.FromArgb(0x8C, link.Color.R, link.Color.G, link.Color.B));
            faint.Freeze();
            var pen = new Pen(faint, 1);
            pen.Freeze();
            var underline = new TextDecorationCollection
            {
                new TextDecoration(TextDecorationLocation.Underline, pen, 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel),
            };
            underline.Freeze();
            _link.Faint = underline;
        }
    }

    /// <summary>Whether the window ended up with a dark theme, judged by how bright its text colour is.</summary>
    private bool IsDarkTheme() =>
        TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
        && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;

    public void FocusEditor() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Editor.TextArea.Focus());

    public void Copy() => Clipboard.SetText(Editor.Text);

    public void Save()
    {
        string ext = Path.GetExtension(Item.Path);
        var dialog = new SaveFileDialog
        {
            Title = "Save file",
            FileName = Item.Name,
            Filter = ext.Length > 0 ? $"{ext.TrimStart('.').ToUpperInvariant()} file (*{ext})|*{ext}|All files (*.*)|*.*" : "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, Item.Data);
        }
        catch (Exception ex)
        {
            _ = ShowCard("The file could not be saved", ex.Message, MessageKind.Warning, primary: "OK");
        }
    }

    /// <summary>Esc closes a message first, then the search bar. False when there was neither.</summary>
    public bool HandleEscape()
    {
        ClosePeek();
        if (Card.IsOpen) CloseCard(false);
        else if (!_search.IsClosed) _search.Close();
        else return false;
        return true;
    }

    /// <summary>The keys that act on this file. The window passes them on.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.System)) ClosePeek();
        if (_workspace is not null && e.Key == Key.F12 && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control)
        {
            _ = GoToDefinitionAsync(Editor.CaretOffset, fromMouse: false, toSide: Keyboard.Modifiers == ModifierKeys.Control);
            e.Handled = true;
        }
        else if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            UpdateLink();
        }
    }

    public void HandleKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) UpdateLink();
    }

    /// <summary>Closes the message and the definition popup, which belong to where the view is now.</summary>
    public void CloseOverlays()
    {
        ClosePeek();
        CloseCard(false);
    }

    /// <summary>Opens at the same place as <paramref name="other"/>, for a second view of the same file.</summary>
    public void CopyViewFrom(PreviewDocument other)
    {
        _pendingReveal = (other.Editor.CaretOffset, 0);
        _pendingScroll = other.Editor.VerticalOffset;
    }

    // ---------------------------------------------------------------- go to definition

    private void SetUpDefinitions()
    {
        var view = Editor.TextArea.TextView;
        view.LineTransformers.Add(_link);
        view.MouseMove += (_, _) => UpdateLink();
        view.MouseLeave += (_, _) => ClearLink();
        view.ScrollOffsetChanged += (_, _) => CloseOverlays();
        Editor.TextArea.PreviewMouseDown += (_, _) => CloseOverlays();
        Editor.TextArea.PreviewMouseWheel += (_, _) => ClosePeek();
        Editor.TextArea.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Ctrl+click goes there, Ctrl+Alt+click opens it beside this one
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                || OffsetAtMouse() is not { } offset) return;
            e.Handled = true;
            _ = GoToDefinitionAsync(offset, fromMouse: true, toSide: (Keyboard.Modifiers & ModifierKeys.Alt) != 0);
        };
        Editor.TextArea.PreviewMouseRightButtonDown += (_, _) =>
        {
            // right-click moves the caret, so the menu acts on the word under the mouse
            if (OffsetAtMouse() is { } offset && !IsInSelection(offset)) Editor.TextArea.Caret.Offset = offset;
        };
    }

    private bool IsInSelection(int offset) =>
        !Editor.TextArea.Selection.IsEmpty && offset >= Editor.SelectionStart && offset <= Editor.SelectionStart + Editor.SelectionLength;

    private int? OffsetAtMouse()
    {
        var pos = Editor.GetPositionFromPoint(Mouse.GetPosition(Editor));
        return pos is { } p ? Editor.Document.GetOffset(p.Location) : null;
    }

    /// <summary>
    /// The finder for this file, once the open archives and game scripts are indexed. Null until then.
    /// Built again when an archive is opened or closed in the main window.
    /// </summary>
    private DefinitionFinder? Finder()
    {
        if (_workspace is not { IsReady: true }) return _finder;
        var layers = _workspace.Layers;
        if (_finder is not null && layers.SequenceEqual(_finderLayers)) return _finder;
        _finderLayers = layers;
        _doc ??= ScriptParser.Parse(Editor.Text, Item.Script!);
        return _finder = new DefinitionFinder(_doc, layers);
    }

    /// <summary>
    /// Underlines the name under the mouse when it leads somewhere: faintly on hover, and as a link,
    /// with the hand cursor, while Ctrl is held. Resting on it shows its declaration.
    /// </summary>
    private void UpdateLink()
    {
        if (!Editor.TextArea.TextView.IsMouseOver
            || Finder() is not { } finder || OffsetAtMouse() is not { } offset || finder.WordAt(offset) is not { } word)
        {
            ClearLink();
            return;
        }
        if (_probed != word)
        {
            _probed = word;
            _probedFound = finder.Find(word.Start, word.Length).Count > 0;
        }
        SetLink(_probedFound ? word : null, strong: (Keyboard.Modifiers & ModifierKeys.Control) != 0);

        if (!_probedFound || !App.Settings.PeekDefinitions || Card.IsOpen)
        {
            ClosePeek();
        }
        else if (_peekWord != word)
        {
            ClosePeek();
            _peekWord = word;
            _peekTimer.Start();
        }
    }

    private void ClearLink()
    {
        _probed = default;
        SetLink(null, strong: false);
        ClosePeek();
    }

    private void SetLink((int Start, int Length)? word, bool strong)
    {
        var (start, length) = word ?? (0, 0);
        if (_link.Start == start && _link.Length == length && _link.Strong == strong) return;
        var view = Editor.TextArea.TextView;
        if (_link.Length > 0) view.Redraw(_link.Start, _link.Length);
        (_link.Start, _link.Length, _link.Strong) = (start, length, strong);
        if (_link.Length > 0) view.Redraw(_link.Start, _link.Length);
        if (word is not null && strong) view.Cursor = Cursors.Hand;
        else view.ClearValue(CursorProperty);
    }

    private async Task GoToDefinitionAsync(int offset, bool fromMouse, bool toSide)
    {
        if (_workspace is null || Host is null) return;
        ClosePeek();
        if (!_workspace.IsReady)
        {
            _ = ShowCard(null, "Reading the scripts…", MessageKind.Busy, anchor: offset, fromMouse: fromMouse);
            Cursor = Cursors.AppStarting;
            await _workspace.Ready;
            ClearValue(CursorProperty);
            CloseCard(false);
        }
        if (Finder() is not { } finder || finder.WordAt(offset) is not { } word) return;

        var targets = finder.Find(word.Start, word.Length);
        if (targets.Count == 1)
        {
            await NavigateAsync(targets[0], toSide);
        }
        else if (targets.Count > 1)
        {
            ShowChoices(targets, fromMouse, toSide);
        }
        else
        {
            string text = Editor.Document.GetText(word.Start, word.Length);
            if (_workspace.GameScriptsFolder is null)
                _ = ShowCard($"No definition found for '{text}'", "The game's scripts weren't found. Set the game folder in Settings to look there too.",
                    MessageKind.Info, primary: "OK", anchor: word.Start, fromMouse: fromMouse);
            else if (_workspace.ModsFolder is not null && !_workspace.IncludesLooseMods)
                await SearchLooseModsAsync(text, word.Start, fromMouse, toSide);
            else
                _ = ShowCard(null, $"No definition found for '{text}'", MessageKind.Info, anchor: word.Start, fromMouse: fromMouse, autoClose: true);
        }
    }

    /// <summary>
    /// Mods like Shared Utils install as loose scripts, with no archive to open. Offers to read them all,
    /// then looks again. Once searched, they're part of every lookup until the app closes.
    /// </summary>
    private async Task SearchLooseModsAsync(string text, int offset, bool fromMouse, bool toSide)
    {
        if (!await ShowCard($"No definition found for '{text}'",
                "It isn't declared in the open archives or the game's scripts. Search the loose scripts of the mods in the game's mods folder too? "
                + "Once read, they're searched for every definition until the explorer is closed.",
                MessageKind.Info, primary: "Search Now", secondary: "Close", anchor: offset, fromMouse: fromMouse))
            return;

        _ = ShowCard(null, "Reading the mods' loose scripts…", MessageKind.Busy, anchor: offset, fromMouse: fromMouse);
        Cursor = Cursors.AppStarting;
        await _workspace!.IncludeLooseModsAsync();
        ClearValue(CursorProperty);
        CloseCard(false);

        if (Finder() is not { } finder) return;
        var targets = finder.Find(offset, text.Length);
        if (targets.Count == 1) await NavigateAsync(targets[0], toSide);
        else if (targets.Count > 1) ShowChoices(targets, fromMouse, toSide);
        else _ = ShowCard(null, $"No definition found for '{text}' in the mods' loose scripts either", MessageKind.Info,
            anchor: offset, fromMouse: fromMouse, autoClose: true);
    }

    /// <summary>Several declarations fit (an overridden method, a wrapped one, or an unknown type before the dot): let the user pick.</summary>
    private void ShowChoices(IReadOnlyList<ScriptSymbol> targets, bool fromMouse, bool toSide)
    {
        var menu = new ContextMenu();
        foreach (var target in targets.Take(30))
        {
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = target.Title, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = $"{target.File.Path} : {target.Line}  ·  {target.File.Source}", Opacity = 0.65, FontSize = 12 });
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await NavigateAsync(target, toSide);
            menu.Items.Add(item);
        }
        if (targets.Count > 30)
            menu.Items.Add(new MenuItem { Header = $"…and {targets.Count - 30} more", IsEnabled = false });
        Place(menu, fromMouse);
        menu.IsOpen = true;
    }

    private Task NavigateAsync(ScriptSymbol target, bool toSide) => Host?.NavigateAsync(this, target, toSide) ?? Task.CompletedTask;

    /// <summary>Puts the caret back where it was, for going back.</summary>
    public void MoveCaret(int offset)
    {
        offset = Math.Min(offset, Editor.Document.TextLength);
        Editor.Select(offset, 0);
        Editor.TextArea.Caret.Offset = offset;
        if (IsLoaded) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Editor.TextArea.Caret.BringCaretToView());
        else _pendingReveal = (offset, 0);
        FocusEditor();
    }

    /// <summary>Selects text and scrolls to it, now or once the view is shown.</summary>
    public void Reveal(int offset, int length)
    {
        if (IsLoaded) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealNow(offset, length));
        else _pendingReveal = (offset, length);
    }

    private void RevealNow(int offset, int length)
    {
        offset = Math.Min(offset, Editor.Document.TextLength);
        Editor.Select(offset, Math.Min(length, Editor.Document.TextLength - offset));
        Editor.TextArea.Caret.Offset = offset;
        var location = Editor.Document.GetLocation(offset);
        Editor.ScrollTo(location.Line, location.Column);
        Editor.TextArea.Focus();
    }

    // ---------------------------------------------------------------- definition popup

    private async Task ShowPeekAsync()
    {
        var word = _peekWord;
        if (word.Length == 0 || !Editor.TextArea.TextView.IsMouseOver || Finder() is not { } finder) return;
        var targets = finder.Find(word.Start, word.Length);
        if (targets.Count == 0) return;

        int version = ++_peekVersion;
        var snippets = await DefinitionPeek.SnippetsAsync(targets, Item.Script?.Key, () => _lines ??= DefinitionPeek.SplitLines(Editor.Text));
        if (version != _peekVersion || _peekWord != word || snippets.Count == 0) return;

        PeekContent.Content = DefinitionPeek.Build(snippets, targets.Count, _dark);
        var view = Editor.TextArea.TextView;
        var position = new TextViewPosition(Editor.Document.GetLocation(word.Start));
        var top = view.GetVisualPosition(position, VisualYPosition.LineTop) - view.ScrollOffset;
        var bottom = view.GetVisualPosition(position, VisualYPosition.LineBottom) - view.ScrollOffset;
        PeekPopup.PlacementTarget = view;
        PeekPopup.PlacementRectangle = new Rect(top.X, top.Y, 1, Math.Max(1, bottom.Y - top.Y));
        PeekPopup.IsOpen = true;
    }

    public void ClosePeek()
    {
        _peekTimer.Stop();
        _peekVersion++;
        _peekWord = default;
        if (PeekPopup.IsOpen) PeekPopup.IsOpen = false;
    }

    // ---------------------------------------------------------------- message card

    /// <summary>
    /// Shows a message over the text, under the name at <paramref name="anchor"/>, or at the top when there's none.
    /// True when its main button is clicked; false when it's closed any other way: the other button, Esc,
    /// a click in the text, scrolling, another message, or the timer of an <paramref name="autoClose"/> one.
    /// </summary>
    public Task<bool> ShowCard(string? title, string message, MessageKind kind, string? primary = null, string? secondary = null,
        int? anchor = null, bool fromMouse = false, bool autoClose = false)
    {
        ClosePeek();
        var result = Card.Show(title, message, kind, primary, secondary, autoClose);
        Card.Measure(new Size(Math.Max(0, CardLayer.ActualWidth - 16), double.PositiveInfinity));
        PlaceCard(anchor, fromMouse);
        return result;
    }

    /// <summary>Just under the name's line, at the mouse or the name; above the line when there's no room below.</summary>
    private void PlaceCard(int? anchor, bool fromMouse)
    {
        var size = Card.DesiredSize;
        double width = CardLayer.ActualWidth, height = CardLayer.ActualHeight;
        double x = (width - size.Width) / 2, y = 12;
        if (anchor is { } offset && offset <= Editor.Document.TextLength)
        {
            var view = Editor.TextArea.TextView;
            var position = new TextViewPosition(Editor.Document.GetLocation(offset));
            var top = view.TranslatePoint(view.GetVisualPosition(position, VisualYPosition.LineTop) - view.ScrollOffset, CardLayer);
            var bottom = view.TranslatePoint(view.GetVisualPosition(position, VisualYPosition.LineBottom) - view.ScrollOffset, CardLayer);
            x = (fromMouse ? Mouse.GetPosition(CardLayer).X : bottom.X) - 16;
            y = bottom.Y + 6;
            if (y + size.Height > height - 8 && top.Y - size.Height - 6 >= 8) y = top.Y - size.Height - 6;
        }
        Canvas.SetLeft(Card, Math.Max(8, Math.Min(x, width - size.Width - 8)));
        Canvas.SetTop(Card, Math.Max(8, Math.Min(y, height - size.Height - 8)));
    }

    private void CloseCard(bool result) => Card.Close(result);

    /// <summary>Puts a popup at the mouse, or under the caret when the keyboard asked for it.</summary>
    private void Place(ContextMenu menu, bool fromMouse)
    {
        var view = Editor.TextArea.TextView;
        menu.PlacementTarget = view;
        if (fromMouse)
        {
            menu.Placement = PlacementMode.MousePoint;
            return;
        }
        var caret = Editor.TextArea.Caret.CalculateCaretRectangle();
        caret.Offset(-view.ScrollOffset.X, -view.ScrollOffset.Y);
        menu.Placement = PlacementMode.Bottom;
        menu.PlacementRectangle = caret;
    }

    private void GoToDefinition_Click(object sender, RoutedEventArgs e) => _ = GoToDefinitionAsync(Editor.CaretOffset, fromMouse: false, toSide: false);

    private void GoToDefinitionSide_Click(object sender, RoutedEventArgs e) => _ = GoToDefinitionAsync(Editor.CaretOffset, fromMouse: false, toSide: true);

    private void Back_Click(object sender, RoutedEventArgs e) => Host?.GoBack();

    private void ContextMenu_Opened(object sender, RoutedEventArgs e) => BackMenuItem.IsEnabled = Host?.CanGoBack == true;

    /// <summary>Underlines the name a Ctrl+click would follow.</summary>
    private sealed class LinkMarker : DocumentColorizingTransformer
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public Brush? Brush { get; set; }

        /// <summary>A link to follow (Ctrl held), not just a hover.</summary>
        public bool Strong { get; set; }

        /// <summary>The hover underline.</summary>
        public TextDecorationCollection? Faint { get; set; }

        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            if (Length <= 0) return;
            int start = Math.Max(Start, line.Offset), end = Math.Min(Start + Length, line.EndOffset);
            if (start >= end) return;
            ChangeLinePart(start, end, element =>
            {
                if (!Strong)
                {
                    element.TextRunProperties.SetTextDecorations(Faint ?? TextDecorations.Underline);
                    return;
                }
                element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                if (Brush is not null) element.TextRunProperties.SetForegroundBrush(Brush);
            });
        }
    }
}
