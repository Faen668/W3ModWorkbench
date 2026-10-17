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
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.Highlighting;

namespace W3ModWorkbench;

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
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _ = ShowHintAsync();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseOverlays();
        };
        if (item.Script is not null && item.IsScript)
        {
            OutlineBar.Visibility = Visibility.Visible;
            Editor.TextArea.Caret.PositionChanged += (_, _) =>
            {
                UpdateOutline();
                CloseHint();
                if (_workspace is not null) _hintTimer.Start();
            };
            UpdateOutline();
        }

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
        if (HintPopup.IsOpen) CloseHint();
        else if (Card.IsOpen) CloseCard(false);
        else if (!_search.IsClosed) _search.Close();
        else return false;
        return true;
    }

    /// <summary>The keys that act on this file. The window passes them on.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)) ClosePeek();
        if (key == Key.Escape) CloseHint();
        bool scripts = CanNavigate;
        if (scripts && key == Key.F12 && mods is ModifierKeys.None or ModifierKeys.Control)
            _ = GoToDefinitionAsync(Editor.CaretOffset, fromMouse: false, toSide: mods == ModifierKeys.Control);
        else if (scripts && key == Key.F12 && mods == ModifierKeys.Shift)
            _ = FindReferencesAsync();
        else if (scripts && key == Key.F12 && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            _ = ShowChangesAsync();
        else if (scripts && key == Key.H && mods == (ModifierKeys.Shift | ModifierKeys.Alt))
            _ = ShowHierarchyAsync();
        else if (key == Key.O && mods == (ModifierKeys.Control | ModifierKeys.Shift) && OutlineBar.Visibility == Visibility.Visible)
            ShowOutlinePicker();
        else if (key == Key.F8 && mods is ModifierKeys.None or ModifierKeys.Shift && HasDiff)
            GoToDifference(mods == ModifierKeys.Shift ? -1 : 1);
        else
        {
            if (key is Key.LeftCtrl or Key.RightCtrl) UpdateLink();
            return;
        }
        e.Handled = true;
    }

    public void HandleKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) UpdateLink();
    }

    /// <summary>Closes the message and the definition popup, which belong to where the view is now.</summary>
    public void CloseOverlays()
    {
        ClosePeek();
        CloseHint();
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
        Editor.TextArea.LostKeyboardFocus += (_, _) => CloseHint();
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
            _ = ShowCard(null, "Reading the scripts…", MessageKind.Busy, anchor: offset);
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
                    MessageKind.Info, primary: "OK", anchor: word.Start);
            else
                await SearchFurtherAsync(text, word.Start, fromMouse, toSide);
        }
    }

    /// <summary>
    /// Nothing open declares the word. Offers the next place to look, one at a time: the installed mods'
    /// loose scripts (Shared Utils installs that way, with no archive to open), then the compiled scripts
    /// of every installed mod. Each is read once and stays in every lookup until the app closes.
    /// </summary>
    private async Task SearchFurtherAsync(string text, int offset, bool fromMouse, bool toSide)
    {
        var workspace = _workspace!;
        while (workspace.ModsFolder is not null)
        {
            bool loose = !workspace.IncludesLooseMods;
            if (!loose && workspace.IncludesCompiledMods) break;

            string searched = loose ? "the open archives or the game's scripts" : "the open archives, the game's scripts or the mods' loose scripts";
            string question = loose
                ? "Search the loose scripts of the mods in the game's mods folder too?"
                : "Search inside the compiled scripts of every mod in the game's mods folder too? They're decompiled first, which can take a little while.";
            if (!await ShowCard($"No definition found for '{text}'",
                    $"It isn't declared in {searched}. {question} Once read, they're searched for every definition until the explorer is closed.",
                    MessageKind.Info, primary: "Search Now", secondary: "Close", anchor: offset))
                return;

            _ = ShowCard(null, loose ? "Reading the mods' loose scripts…" : "Decompiling the mods' scripts…", MessageKind.Busy, anchor: offset);
            Cursor = Cursors.AppStarting;
            await (loose ? workspace.IncludeLooseModsAsync() : workspace.IncludeCompiledModsAsync());
            ClearValue(CursorProperty);
            CloseCard(false);

            if (Finder() is not { } finder) return;
            var targets = finder.Find(offset, text.Length);
            if (targets.Count == 1)
            {
                await NavigateAsync(targets[0], toSide);
                return;
            }
            if (targets.Count > 1)
            {
                ShowChoices(targets, fromMouse, toSide);
                return;
            }
        }
        if (Finder() is { } last) ShowNotFound(last, text, offset, $"No definition found for '{text}'");
    }

    /// <summary>
    /// Nothing declares the word anywhere searched. A type by then is often one the game declares in its
    /// engine, though it could also come from a blob that isn't installed or open, so suggest both.
    /// </summary>
    private void ShowNotFound(DefinitionFinder finder, string text, int offset, string message)
    {
        if (finder.IsTypePosition(offset))
            _ = ShowCard($"'{text}' may be a native engine type",
                "Nothing in the open archives, the game's scripts or the installed mods declares it. The game may declare it in its engine code, "
                + "or it may be in a script blob that isn't open yet.",
                MessageKind.Info, primary: "OK", anchor: offset);
        else
            _ = ShowCard(null, message, MessageKind.Info, anchor: offset, autoClose: true);
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

    // ---------------------------------------------------------------- references, changes and hierarchy

    /// <summary>Whether this file's names lead somewhere: it's a script with scripts around it to look in.</summary>
    public bool CanNavigate => Item.Script is not null && _workspace is not null;

    public string SelectedText => Editor.SelectedText;

    /// <summary>The file parsed: its declarations, and where each class and function starts and ends.</summary>
    private ParsedScript Parsed => _doc ??= ScriptParser.Parse(Editor.Text, Item.Script!);

    /// <summary>The line an offset is on, 1-based, and its text.</summary>
    public (int Line, string Text) LineAt(int offset)
    {
        var line = Editor.Document.GetLineByOffset(Math.Clamp(offset, 0, Editor.Document.TextLength));
        return (line.LineNumber, Editor.Document.GetText(line.Offset, line.Length));
    }

    /// <summary>The finder, once the scripts are read. Shows that it's reading them while it waits.</summary>
    public async Task<DefinitionFinder?> FinderAsync()
    {
        if (_workspace is null || Item.Script is null) return null;
        if (!_workspace.IsReady)
        {
            _ = ShowCard(null, "Reading the scripts…", MessageKind.Busy, anchor: Editor.CaretOffset);
            Cursor = Cursors.AppStarting;
            await _workspace.Ready;
            ClearValue(CursorProperty);
            CloseCard(false);
        }
        return Finder();
    }

    /// <summary>
    /// What the name at an offset stands for. On a declaration it's that declaration, except on a @wrapMethod or
    /// @replaceMethod, where it's the function being changed; elsewhere it's what go to definition would open.
    /// </summary>
    private async Task<(DefinitionFinder Finder, IReadOnlyList<ScriptSymbol> Targets, string Word, int Start)?> TargetsAtCaretAsync(int offset)
    {
        if (await FinderAsync() is not { } finder || finder.WordAt(offset) is not { } word) return null;
        string text = Editor.Document.GetText(word.Start, word.Length);
        var declared = Parsed.Symbols.FirstOrDefault(s => s.Offset == word.Start);
        IReadOnlyList<ScriptSymbol> targets = declared is null or { Annotation: "wrapMethod" or "replaceMethod" }
            ? finder.Find(word.Start, word.Length)
            : [declared];
        if (targets.Count == 0)
            _ = ShowCard(null, $"'{text}' doesn't lead to a declaration in the scripts searched.", MessageKind.Info, anchor: word.Start, autoClose: true);
        return targets.Count == 0 ? null : (finder, targets, text, word.Start);
    }

    /// <summary>
    /// Buttons for the results panel that add the installed mods' scripts to the search and run it again,
    /// for whichever of them aren't searched yet.
    /// </summary>
    private (string Label, Action Run)[] IncludeModsActions(Func<Task> again)
    {
        if (_workspace is not { ModsFolder: not null } workspace) return [];
        var actions = new List<(string, Action)>();
        if (!workspace.IncludesLooseMods)
            actions.Add(("Loose scripts", async () =>
            {
                await workspace.IncludeLooseModsAsync();
                await again();
            }));
        if (!workspace.IncludesCompiledMods)
            actions.Add(("Compiled scripts", async () =>
            {
                await workspace.IncludeCompiledModsAsync();
                await again();
            }));
        return [.. actions];
    }

    /// <summary>Shift+F12: every place that uses the name at the caret, in the results under the files.</summary>
    private async Task FindReferencesAsync(int? at = null)
    {
        int offset = at ?? Editor.CaretOffset;
        if (Host is null || await TargetsAtCaretAsync(offset) is not { } found) return;
        var (finder, targets, word, _) = found;
        var parsed = Parsed;
        var layers = finder.Layers;
        string what = targets[0].Kind is SymbolKind.Local or SymbolKind.Parameter ? word : targets[0].Title;
        Host?.ShowResults(this, $"References to {what}", "Looking through every script…", cancel =>
        {
            var hits = ScriptSearch.References(targets, parsed, layers, cancel);
            return (ResultRow.ByFile(hits), PreviewWindow.Summary(hits, "reference", "references"));
        }, $"Nothing in the scripts searched uses {what}.", IncludeModsActions(() => FindReferencesAsync(offset)));
    }

    /// <summary>
    /// Ctrl+Shift+F12: what changes the function or type at the caret from elsewhere: wraps, replacements and
    /// overrides of a function, annotations adding to a class, and loose files that replace the game's file outright.
    /// </summary>
    private async Task ShowChangesAsync(int? at = null)
    {
        int offset = at ?? Editor.CaretOffset;
        if (Host is null || await TargetsAtCaretAsync(offset) is not { } found) return;
        var target = found.Targets.FirstOrDefault(t => t.Kind is SymbolKind.Function or SymbolKind.Event || t.IsType);
        if (target is null)
        {
            _ = ShowCard(null, "This works on functions, events and types.", MessageKind.Info, anchor: found.Start, autoClose: true);
            return;
        }
        var finder = found.Finder;
        var workspace = _workspace!;
        Host?.ShowResults(this, $"Changes to {target.Title}", "Looking for wraps, overrides and replacements…", _ =>
        {
            var rows = new List<ResultRow>();
            int count = 0;
            foreach (var group in finder.ChangesTo(target).GroupBy(c => c.Group))
            {
                var list = group.ToList();
                rows.Add(ResultRow.Heading(group.Key, list.Count));
                rows.AddRange(list.Select(c => ResultRow.FromSymbol(c.Symbol)));
                count += list.Count;
            }
            var files = workspace.LooseReplacementsOf(target.File);
            if (files.Count > 0)
            {
                rows.Add(ResultRow.Heading("Whole file replaced by", files.Count));
                rows.AddRange(files.Select(f => ResultRow.FromFile(f, label: "loose file")));
                count += files.Count;
            }
            return (rows, count == 1 ? "1 change" : $"{count} changes");
        }, "Nothing in the scripts searched changes it.", IncludeModsActions(() => ShowChangesAsync(offset)));
    }

    /// <summary>Shift+Alt+H: a class's bases, subtypes and states, or a function's base versions and overrides.</summary>
    private async Task ShowHierarchyAsync(int? at = null)
    {
        int offset = at ?? Editor.CaretOffset;
        if (Host is null || await TargetsAtCaretAsync(offset) is not { } found) return;
        var target = found.Targets.FirstOrDefault(t => t.Kind is SymbolKind.Class or SymbolKind.State
            || t.Kind is SymbolKind.Function or SymbolKind.Event && t.Owner is not null);
        if (target is null)
        {
            _ = ShowCard(null, "The type hierarchy works on classes, states and the functions in them.", MessageKind.Info, anchor: found.Start, autoClose: true);
            return;
        }
        var finder = found.Finder;
        Host?.ShowResults(this, $"Hierarchy of {target.Title}", "Working out the hierarchy…", _ =>
        {
            var rows = new List<ResultRow> { ResultRow.FromSymbol(target, depth: 0, label: "this one") };
            var related = finder.Hierarchy(target);
            foreach (var group in related.GroupBy(r => r.Group))
            {
                var list = group.ToList();
                rows.Add(ResultRow.Heading(group.Key, list.Count));
                rows.AddRange(list.Select(r => ResultRow.FromSymbol(r.Symbol, depth: Math.Min(r.Depth, 12))));
            }
            int subtypes = related.Count(r => r.Group is "Subtypes" or "Overridden by");
            return (rows, $"{related.Count(r => r.Group is "Base types" or "Overrides")} above, {subtypes} below");
        }, "", IncludeModsActions(() => ShowHierarchyAsync(offset)));
    }

    // ---------------------------------------------------------------- outline

    /// <summary>The class and function the caret is in, each opening a menu of the others in the file.</summary>
    private void UpdateOutline()
    {
        if (OutlineBar.Visibility != Visibility.Visible) return;
        int offset = Editor.CaretOffset;
        var type = Parsed.TypeAt(offset);
        var function = Parsed.FunctionAt(offset);
        if (type == _crumbType && function == _crumbFunction && Crumbs.Children.Count > 0) return;
        (_crumbType, _crumbFunction) = (type, function);

        Crumbs.Children.Clear();
        Crumbs.Children.Add(Crumb(type?.Symbol, type is null ? "(file)" : TypeTitle(type.Symbol), () => TypeChoices()));
        if (function is not null || type is not null)
        {
            Crumbs.Children.Add(new TextBlock { Text = "", FontSize = 9, Opacity = 0.5, Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("IconFont") });
            Crumbs.Children.Add(Crumb(function?.Symbol, function is null ? "…" : function.Symbol.Name + "()", () => FunctionChoices(type)));
        }
    }

    private TypeScope? _crumbType;
    private FunctionScope? _crumbFunction;

    private static string TypeTitle(ScriptSymbol s) => s.Kind == SymbolKind.State ? $"{s.Owner}.{s.Name}" : s.Name;

    private Button Crumb(ScriptSymbol? symbol, string text, Func<IEnumerable<ScriptSymbol>> choices)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (symbol is not null)
        {
            var icon = new TextBlock { Text = DefinitionPeek.KindGlyph(symbol.Kind), FontSize = 11.5, Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            row.Children.Add(icon);
        }
        row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = row,
            Padding = new Thickness(6, 2, 6, 2),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
        };
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (var s in choices())
            {
                var item = new MenuItem { Header = s.Kind is SymbolKind.Function or SymbolKind.Event ? s.Name + "()" : TypeTitle(s) };
                var icon = new TextBlock { Text = DefinitionPeek.KindGlyph(s.Kind), FontSize = 12 };
                icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
                item.Icon = icon;
                item.Click += (_, _) => GoToOwn(s);
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Nothing declared here", IsEnabled = false });
            menu.IsOpen = true;
        };
        return button;
    }

    private IEnumerable<ScriptSymbol> TypeChoices() => Parsed.Types.OrderBy(t => t.Start).Select(t => t.Symbol);

    /// <summary>The functions of a class, or the file's global functions when the caret isn't in one.</summary>
    private IEnumerable<ScriptSymbol> FunctionChoices(TypeScope? type) =>
        Parsed.Functions
            .Where(f => type is null ? Parsed.TypeAt(f.BodyStart) is null : f.BodyStart > type.Start && f.BodyEnd <= type.End)
            .OrderBy(f => f.BodyStart)
            .Select(f => f.Symbol);

    /// <summary>Goes to a declaration in this file, so Back can come back.</summary>
    private void GoToOwn(ScriptSymbol symbol)
    {
        if (Host is { } host) _ = host.NavigateAsync(this, Item.Script!, symbol.Offset, symbol.Name.Length, toSide: false);
        else Reveal(symbol.Offset, symbol.Name.Length);
    }

    /// <summary>Ctrl+Shift+O: everything declared in this file, narrowed by what's typed.</summary>
    public void ShowOutlinePicker()
    {
        if (Host is not { } host || Item.Script is null || !Item.IsScript) return;
        var symbols = Parsed.Symbols.OrderBy(s => s.Offset).ToList();
        host.ShowPicker($"Go to symbol in {FileName}", query =>
        {
            var shown = query.Length == 0 ? symbols : symbols.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (s.OwnerTitle ?? s.Owner ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));
            return [.. shown.Take(400).Select(s => new QuickPickItem(DefinitionPeek.KindGlyph(s.Kind), s.Title, $"line {s.Line}", s))];
        }, item =>
        {
            if (item.Tag is ScriptSymbol s) GoToOwn(s);
        });
    }

    // ---------------------------------------------------------------- parameter hints

    private readonly DispatcherTimer _hintTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>
    /// When the caret is inside a call's brackets: the called function's parameters, over the line, with the one
    /// the caret is on in bold. Uses the same setting as the definition popup.
    /// </summary>
    private async Task ShowHintAsync()
    {
        if (!App.Settings.PeekDefinitions || !IsVisible || Finder() is not { } finder) { CloseHint(); return; }
        string code = Parsed.Code;
        int caret = Math.Min(Editor.CaretOffset, code.Length);
        if (CallAt(code, caret) is not { } call) { CloseHint(); return; }

        var target = finder.Find(call.NameStart, call.NameLength).FirstOrDefault(s => s.Kind is SymbolKind.Function or SymbolKind.Event);
        if (target is null) { CloseHint(); return; }
        int version = ++_hintVersion;
        var snippets = await DefinitionPeek.SnippetsAsync([target], Item.Script?.Key, () => _lines ??= DefinitionPeek.SplitLines(Editor.Text));
        if (version != _hintVersion || snippets.Count == 0) return;
        if (Signature(string.Join(" ", snippets[0].Lines.Select(l => l.Trim())), target.Name) is not { } signature) { CloseHint(); return; }

        HintText.Inlines.Clear();
        HintText.Inlines.Add(new Run(target.Name + "("));
        for (int i = 0; i < signature.Parameters.Count; i++)
        {
            if (i > 0) HintText.Inlines.Add(new Run(", "));
            var run = new Run(signature.Parameters[i]);
            if (i == Math.Min(call.Argument, signature.Parameters.Count - 1))
            {
                run.FontWeight = FontWeights.Bold;
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            }
            HintText.Inlines.Add(run);
        }
        HintText.Inlines.Add(new Run(")" + signature.Returns));

        var view = Editor.TextArea.TextView;
        var position = new TextViewPosition(Editor.Document.GetLocation(call.NameStart));
        var top = view.GetVisualPosition(position, VisualYPosition.LineTop) - view.ScrollOffset;
        HintPopup.PlacementTarget = view;
        HintPopup.PlacementRectangle = new Rect(top.X, top.Y, 1, 1);
        HintPopup.IsOpen = true;
    }

    private int _hintVersion;

    private void CloseHint()
    {
        _hintTimer.Stop();
        _hintVersion++;
        if (HintPopup.IsOpen) HintPopup.IsOpen = false;
    }

    private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal) { "if", "while", "for", "switch", "return", "function", "event", "new" };

    /// <summary>The call whose brackets the caret is in: where its name is, and which argument the caret is on.</summary>
    private static (int NameStart, int NameLength, int Argument)? CallAt(string code, int caret)
    {
        int depth = 0, commas = 0, i = caret - 1;
        for (; i >= 0 && caret - i < 3000; i--)
        {
            char c = code[i];
            if (c is ')' or ']') depth++;
            else if (c is '(' or '[')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (c == ',' && depth == 0) commas++;
            else if (c is ';' or '{' or '}') return null;
        }
        if (i < 0 || code[i] != '(') return null;
        int end = i - 1;
        while (end >= 0 && char.IsWhiteSpace(code[end])) end--;
        if (end < 0 || !ScriptParser.IsWordChar(code[end])) return null;
        int start = end;
        while (start > 0 && ScriptParser.IsWordChar(code[start - 1])) start--;
        if (NotCalls.Contains(code[start..(end + 1)])) return null;
        return (start, end + 1 - start, commas);
    }

    /// <summary>A function's parameters, one per item, and what follows the brackets, from its declaration's text.</summary>
    private static (List<string> Parameters, string Returns)? Signature(string declaration, string name)
    {
        int at = declaration.IndexOf(name + "(", StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = declaration.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        int open = at < 0 ? -1 : declaration.IndexOf('(', at);
        if (open < 0) return null;
        var parameters = new List<string>();
        int depth = 0, from = open + 1, close = -1;
        for (int i = open + 1; i < declaration.Length; i++)
        {
            char c = declaration[i];
            if (c is '(' or '<') depth++;
            else if (c is '>') depth--;
            else if (c == ')' && depth-- == 0) { close = i; break; }
            else if (c == ',' && depth == 0)
            {
                parameters.Add(declaration[from..i].Trim());
                from = i + 1;
            }
        }
        if (close < 0) return null;
        string last = declaration[from..close].Trim();
        if (last.Length > 0 || parameters.Count > 0) parameters.Add(last);
        string rest = declaration[(close + 1)..].Trim().TrimEnd('{', ';').TrimEnd();
        return (parameters, rest.Length > 0 ? " " + rest : "");
    }

    // ---------------------------------------------------------------- comparing

    private readonly DiffMarker _diff = new();

    /// <summary>Marks the lines that differ from another version of the file, until <see cref="ClearDiff"/>.</summary>
    public void SetDiff(DiffKind[] kinds)
    {
        _diff.Kinds = kinds;
        var view = Editor.TextArea.TextView;
        if (!view.BackgroundRenderers.Contains(_diff)) view.BackgroundRenderers.Add(_diff);
        view.InvalidateLayer(_diff.Layer);
    }

    public bool HasDiff => _diff.Kinds is not null;

    public void ClearDiff()
    {
        _diff.Kinds = null;
        Editor.TextArea.TextView.BackgroundRenderers.Remove(_diff);
        Editor.TextArea.TextView.InvalidateLayer(_diff.Layer);
    }

    /// <summary>F8 and Shift+F8: the first line of the next or previous block of differences, wrapping round.</summary>
    public void GoToDifference(int step)
    {
        if (_diff.Kinds is not { Length: > 0 } kinds) return;
        var blocks = new List<int>();
        for (int i = 0; i < kinds.Length; i++)
            if (kinds[i] != DiffKind.Same && (i == 0 || kinds[i - 1] == DiffKind.Same)) blocks.Add(i);
        if (blocks.Count == 0)
        {
            _ = ShowCard(null, "No differences: the files match.", MessageKind.Info, autoClose: true);
            return;
        }
        int line = Editor.TextArea.Caret.Line - 1;
        int next = step > 0
            ? blocks.FirstOrDefault(b => b > line, blocks[0])
            : blocks.LastOrDefault(b => b < line, blocks[^1]);
        var target = Editor.Document.GetLineByNumber(Math.Min(next + 1, Editor.Document.LineCount));
        Editor.Select(target.Offset, 0);
        Editor.TextArea.Caret.Offset = target.Offset;
        Editor.ScrollTo(target.LineNumber, 0);
        Editor.TextArea.Focus();
    }

    /// <summary>The game's version of this file, when it's a mod's loose copy of one of the game's scripts.</summary>
    private ScriptFile? GameVersion => Item.Script is { } script ? _workspace?.GameVersionOf(script) : null;

    /// <summary>Tints the lines that differ: green for added, red for removed, amber for changed.</summary>
    private sealed class DiffMarker : IBackgroundRenderer
    {
        private static readonly Brush Added = Frozen(Color.FromArgb(0x38, 0x2E, 0xA0, 0x43));
        private static readonly Brush Removed = Frozen(Color.FromArgb(0x38, 0xE5, 0x48, 0x4D));
        private static readonly Brush Changed = Frozen(Color.FromArgb(0x30, 0xE0, 0xA0, 0x30));

        public DiffKind[]? Kinds { get; set; }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Kinds is not { } kinds) return;
            foreach (var line in textView.VisualLines)
            {
                int index = line.FirstDocumentLine.LineNumber - 1;
                if (index >= kinds.Length || kinds[index] == DiffKind.Same) continue;
                var brush = kinds[index] switch { DiffKind.Added => Added, DiffKind.Removed => Removed, _ => Changed };
                double top = line.VisualTop - textView.ScrollOffset.Y;
                drawingContext.DrawRectangle(brush, null, new Rect(0, top, Math.Max(textView.ActualWidth, 1), line.Height));
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    // ---------------------------------------------------------------- menu

    private void FindReferences_Click(object sender, RoutedEventArgs e) => _ = FindReferencesAsync();

    private void Changes_Click(object sender, RoutedEventArgs e) => _ = ShowChangesAsync();

    private void Hierarchy_Click(object sender, RoutedEventArgs e) => _ = ShowHierarchyAsync();

    private void Forward_Click(object sender, RoutedEventArgs e) => Host?.GoForward();

    private void Outline_Click(object sender, RoutedEventArgs e) => ShowOutlinePicker();

    private void CompareGame_Click(object sender, RoutedEventArgs e)
    {
        if (GameVersion is { } game) Host?.CompareWithFileAsync(this, game);
    }

    private void CompareOpen_Click(object sender, RoutedEventArgs e) => Host?.PickFileToCompare(this);

    private void NextDiff_Click(object sender, RoutedEventArgs e) => GoToDifference(1);

    private void ClearDiff_Click(object sender, RoutedEventArgs e) => Host?.StopComparing(this);

    // ---------------------------------------------------------------- message card

    /// <summary>
    /// Shows a message over the text, under the name at <paramref name="anchor"/>, or at the top when there's none.
    /// True when its main button is clicked; false when it's closed any other way: the other button, Esc,
    /// a click in the text, scrolling, another message, or the timer of an <paramref name="autoClose"/> one.
    /// </summary>
    public Task<bool> ShowCard(string? title, string message, MessageKind kind, string? primary = null, string? secondary = null,
        int? anchor = null, bool autoClose = false)
    {
        ClosePeek();
        var result = Card.Show(title, message, kind, primary, secondary, autoClose);
        Card.Measure(new Size(Math.Max(0, CardLayer.ActualWidth - 16), double.PositiveInfinity));
        PlaceCard(anchor);
        return result;
    }

    /// <summary>
    /// Just under the name's line, lined up with the start of the name; above the line when there's no room below.
    /// It follows the name rather than the mouse, so a card that leads to another keeps its place.
    /// </summary>
    private void PlaceCard(int? anchor)
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
            x = bottom.X - 16;
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

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        BackMenuItem.IsEnabled = Host?.CanGoBack == true;
        ForwardMenuItem.IsEnabled = Host?.CanGoForward == true;
        CompareGameMenuItem.IsEnabled = GameVersion is not null;
        CompareGameMenuItem.ToolTip = GameVersion is null
            ? "Only for a mod's loose copy of one of the game's scripts"
            : "Opens the game's version beside this one and marks what the mod changed";
        CompareOpenMenuItem.IsEnabled = Host?.HasOtherFiles(this) == true;
        NextDiffMenuItem.Visibility = ClearDiffMenuItem.Visibility = HasDiff ? Visibility.Visible : Visibility.Collapsed;
    }

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
