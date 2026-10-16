using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using W3BundleExplorer.Core;
using W3BundleExplorer.Core.Scripts.Navigation;
using W3BundleExplorer.Highlighting;

namespace W3BundleExplorer;

public partial class PreviewWindow : Window, IMessageHost
{
    /// <summary>Open script previews by file, so going to a definition reuses a window that already shows it.</summary>
    private static readonly Dictionary<string, PreviewWindow> OpenScripts = [];

    private readonly string _path;
    private readonly byte[] _data;
    private readonly SearchPanel _search;

    // go to definition, for scripts
    private readonly ScriptFile? _script;
    private readonly ScriptWorkspace? _workspace;
    private readonly LinkMarker _link = new();
    private readonly Stack<int> _back = new();
    private DefinitionFinder? _finder;
    private IReadOnlyList<ScriptIndex> _finderLayers = [];
    private ParsedScript? _doc;
    private (int Start, int Length) _probed; // the last word checked for a link
    private bool _probedFound;
    private PreviewWindow? _opener;

    /// <param name="script">For a .ws file: which file it is, so its names can lead to their definitions.</param>
    /// <param name="workspace">The scripts definitions are looked up in.</param>
    /// <param name="reveal">Text to select and scroll to once the window opens.</param>
    public PreviewWindow(string path, string source, byte[] data,
        ScriptFile? script = null, ScriptWorkspace? workspace = null, (int Offset, int Length)? reveal = null)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _path = path;
        _data = data;
        string name = Path.GetFileName(path);
        Title = $"{name} - Preview";
        FileNameText.Text = name;
        DetailText.Text = $"{source}  ·  {path}  ·  {Format.Bytes(data.Length)}";

        Editor.Options.IndentationSize = 4; // tab width
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);
        Editor.Text = TextDecoding.Decode(data);
        if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
        {
            var selection = new SolidColorBrush(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B));
            selection.Freeze();
            Editor.TextArea.SelectionBrush = selection;
            Editor.TextArea.SelectionBorder = null;
            Editor.TextArea.SelectionForeground = null;
        }

        // WitcherScript is coloured with the grammar from WebSpam's witcherscript-vscode extension
        if (Path.GetExtension(path).Equals(".ws", StringComparison.OrdinalIgnoreCase))
            Editor.TextArea.TextView.LineTransformers.Add(new WitcherScriptColorizer(Editor.Document, IsDarkTheme()));

        _search = SearchPanel.Install(Editor.TextArea);
        if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush a)
        {
            var marker = new SolidColorBrush(Color.FromArgb(0x55, a.Color.R, a.Color.G, a.Color.B));
            marker.Freeze();
            _search.MarkerBrush = marker;
        }

        _script = script;
        _workspace = workspace;
        if (script is not null && workspace is not null)
            SetUpDefinitions(script);
        else
            Editor.ContextMenu = null;

        // without this, Windows can hand the focus to another app when an owned window closes,
        // and the explorer drops behind it as if it had been minimized
        Closing += (_, _) => (_opener is { IsVisible: true } opener ? opener : Owner)?.Activate();

        Loaded += (_, _) =>
        {
            Editor.TextArea.Focus();
            if (reveal is { } r)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Reveal(r.Offset, r.Length));
        };
    }

    /// <summary>Whether the window ended up with a dark theme, judged by how bright its text colour is.</summary>
    private bool IsDarkTheme() =>
        TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
        && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(Editor.Text);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string ext = Path.GetExtension(_path);
        var dialog = new SaveFileDialog
        {
            Title = "Save file",
            FileName = Path.GetFileName(_path),
            Filter = ext.Length > 0 ? $"{ext.TrimStart('.').ToUpperInvariant()} file (*{ext})|*{ext}|All files (*.*)|*.*" : "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, _data);
        }
        catch (Exception ex)
        {
            _ = ShowCard("The file could not be saved", ex.Message, MessageKind.Warning, primary: "OK");
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc closes a message first, then the search bar, then the window
            if (Card.IsOpen) CloseCard(false);
            else if (_search.IsClosed) Close();
            else _search.Close();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
        else if (_workspace is not null && e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _ = GoToDefinitionAsync(Editor.CaretOffset, fromMouse: false);
            e.Handled = true;
        }
        else if (_workspace is not null && e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            GoBack();
            e.Handled = true;
        }
        else if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            UpdateLink();
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) UpdateLink();
    }

    // ---------------------------------------------------------------- go to definition

    private void SetUpDefinitions(ScriptFile script)
    {
        OpenScripts[script.Key] = this;
        Closed += (_, _) =>
        {
            if (OpenScripts.TryGetValue(script.Key, out var w) && w == this) OpenScripts.Remove(script.Key);
            CloseCard(false);
        };

        var view = Editor.TextArea.TextView;
        _link.Brush = TryFindResource("AccentTextFillColorPrimaryBrush") as Brush;
        if (_link.Brush is SolidColorBrush accent)
        {
            var faint = new SolidColorBrush(Color.FromArgb(0x8C, accent.Color.R, accent.Color.G, accent.Color.B));
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
        view.LineTransformers.Add(_link);
        view.MouseMove += (_, _) => UpdateLink();
        view.MouseLeave += (_, _) => ClearLink();
        view.ScrollOffsetChanged += (_, _) => CloseCard(false);
        Editor.TextArea.PreviewMouseDown += (_, _) => CloseCard(false);
        Editor.TextArea.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || OffsetAtMouse() is not { } offset) return;
            e.Handled = true;
            _ = GoToDefinitionAsync(offset, fromMouse: true);
        };
        Editor.TextArea.PreviewMouseRightButtonDown += (_, _) =>
        {
            // right-click moves the caret, so the menu acts on the word under the mouse
            if (OffsetAtMouse() is { } offset && !IsInSelection(offset)) Editor.TextArea.Caret.Offset = offset;
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.XButton1) return;
            GoBack();
            e.Handled = true;
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
        _doc ??= ScriptParser.Parse(Editor.Text, _script!);
        return _finder = new DefinitionFinder(_doc, layers);
    }

    /// <summary>
    /// Underlines the name under the mouse when it leads somewhere: faintly on hover, and as a link,
    /// with the hand cursor, while Ctrl is held.
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
        SetLink(_probedFound ? word : null, strong: Keyboard.Modifiers == ModifierKeys.Control);
    }

    private void ClearLink()
    {
        _probed = default;
        SetLink(null, strong: false);
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

    private async Task GoToDefinitionAsync(int offset, bool fromMouse)
    {
        if (_workspace is null) return;
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
            await NavigateAsync(targets[0]);
        }
        else if (targets.Count > 1)
        {
            ShowChoices(targets, fromMouse);
        }
        else
        {
            string text = Editor.Document.GetText(word.Start, word.Length);
            if (_workspace.GameScriptsFolder is null)
                _ = ShowCard($"No definition found for '{text}'", "The game's scripts weren't found. Set the game folder in Settings to look there too.",
                    MessageKind.Info, primary: "OK", anchor: word.Start, fromMouse: fromMouse);
            else if (_workspace.ModsFolder is not null && !_workspace.IncludesLooseMods)
                await SearchLooseModsAsync(text, word.Start, fromMouse);
            else
                _ = ShowCard(null, $"No definition found for '{text}'", MessageKind.Info, anchor: word.Start, fromMouse: fromMouse, autoClose: true);
        }
    }

    /// <summary>
    /// Mods like Shared Utils install as loose scripts, with no archive to open. Offers to read them all, asks
    /// first, then looks again. Once searched, they're part of every lookup until the app closes.
    /// </summary>
    private async Task SearchLooseModsAsync(string text, int offset, bool fromMouse)
    {
        if (!await ShowCard($"No definition found for '{text}'", "It isn't declared in the open archives or the game's scripts.",
                MessageKind.Info, primary: "Search Now…", secondary: "Close", anchor: offset, fromMouse: fromMouse))
            return;
        if (!await ShowCard("Search loose mod scripts?",
                "Some mods, such as Shared Utils, install as loose scripts with no archive to open. "
                + "This reads every mod in the game's mods folder, and keeps searching them for every definition until the explorer is closed.",
                MessageKind.Question, primary: "Search", secondary: "Cancel", anchor: offset, fromMouse: fromMouse))
            return;

        _ = ShowCard(null, "Reading the mods' loose scripts…", MessageKind.Busy, anchor: offset, fromMouse: fromMouse);
        Cursor = Cursors.AppStarting;
        await _workspace!.IncludeLooseModsAsync();
        ClearValue(CursorProperty);
        CloseCard(false);

        if (Finder() is not { } finder) return;
        var targets = finder.Find(offset, text.Length);
        if (targets.Count == 1) await NavigateAsync(targets[0]);
        else if (targets.Count > 1) ShowChoices(targets, fromMouse);
        else _ = ShowCard(null, $"No definition found for '{text}' in the mods' loose scripts either", MessageKind.Info,
            anchor: offset, fromMouse: fromMouse, autoClose: true);
    }

    /// <summary>Several declarations fit (an overridden method, a wrapped one, or an unknown type before the dot): let the user pick.</summary>
    private void ShowChoices(IReadOnlyList<ScriptSymbol> targets, bool fromMouse)
    {
        var menu = new ContextMenu();
        foreach (var target in targets.Take(30))
        {
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = target.Title, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = $"{target.File.Path} : {target.Line}  ·  {target.File.Source}", Opacity = 0.65, FontSize = 12 });
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await NavigateAsync(target);
            menu.Items.Add(item);
        }
        if (targets.Count > 30)
            menu.Items.Add(new MenuItem { Header = $"…and {targets.Count - 30} more", IsEnabled = false });
        Place(menu, fromMouse);
        menu.IsOpen = true;
    }

    private async Task NavigateAsync(ScriptSymbol target)
    {
        int length = target.Name.Length;
        if (target.File.Key == _script?.Key)
        {
            _back.Push(Editor.CaretOffset);
            Reveal(target.Offset, length);
            return;
        }
        if (OpenScripts.TryGetValue(target.File.Key, out var open))
        {
            open.Activate();
            open._back.Push(open.Editor.CaretOffset);
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
            _ = ShowCard($"{target.File.Path} could not be read", ex.Message, MessageKind.Warning, primary: "OK");
            return;
        }
        var window = new PreviewWindow(target.File.Path, target.File.Source, data, target.File, _workspace, (target.Offset, length))
        {
            Owner = Owner,
            _opener = this,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + 32,
            Top = Top + 32,
            Width = Width,
            Height = Height,
        };
        window.Show();
    }

    private void GoBack()
    {
        if (_back.Count == 0) return;
        int offset = Math.Min(_back.Pop(), Editor.Document.TextLength);
        Editor.Select(offset, 0);
        Editor.TextArea.Caret.Offset = offset;
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void Reveal(int offset, int length)
    {
        offset = Math.Min(offset, Editor.Document.TextLength);
        Editor.Select(offset, Math.Min(length, Editor.Document.TextLength - offset));
        Editor.TextArea.Caret.Offset = offset;
        var location = Editor.Document.GetLocation(offset);
        Editor.ScrollTo(location.Line, location.Column);
        Editor.TextArea.Focus();
    }

    // ---------------------------------------------------------------- message card

    /// <summary>
    /// Shows a message over the text, under the name at <paramref name="anchor"/>, or at the top when there's none.
    /// True when its main button is clicked; false when it's closed any other way: the other button, Esc,
    /// a click in the text, scrolling, another message, or the timer of an <paramref name="autoClose"/> one.
    /// </summary>
    private Task<bool> ShowCard(string? title, string message, MessageKind kind, string? primary = null, string? secondary = null,
        int? anchor = null, bool fromMouse = false, bool autoClose = false)
    {
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
            var position = new ICSharpCode.AvalonEdit.TextViewPosition(Editor.Document.GetLocation(offset));
            var top = view.TranslatePoint((Point)(view.GetVisualPosition(position, VisualYPosition.LineTop) - view.ScrollOffset), CardLayer);
            var bottom = view.TranslatePoint((Point)(view.GetVisualPosition(position, VisualYPosition.LineBottom) - view.ScrollOffset), CardLayer);
            x = (fromMouse ? Mouse.GetPosition(CardLayer).X : bottom.X) - 16;
            y = bottom.Y + 6;
            if (y + size.Height > height - 8 && top.Y - size.Height - 6 >= 8) y = top.Y - size.Height - 6;
        }
        Canvas.SetLeft(Card, Math.Max(8, Math.Min(x, width - size.Width - 8)));
        Canvas.SetTop(Card, Math.Max(8, Math.Min(y, height - size.Height - 8)));
    }

    private void CloseCard(bool result) => Card.Close(result);

    public Task ShowMessage(string? title, string message, MessageKind kind) => ShowCard(title, message, kind, primary: "OK");

    /// <summary>Puts a popup at the mouse, or under the caret when the keyboard asked for it.</summary>
    private void Place(ContextMenu menu, bool fromMouse)
    {
        var (target, placement, rect) = PlaceAt(fromMouse);
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.PlacementRectangle = rect;
    }

    private (UIElement Target, PlacementMode Placement, Rect Rect) PlaceAt(bool fromMouse)
    {
        var view = Editor.TextArea.TextView;
        if (fromMouse) return (view, PlacementMode.MousePoint, Rect.Empty);
        var caret = Editor.TextArea.Caret.CalculateCaretRectangle();
        caret.Offset(-view.ScrollOffset.X, -view.ScrollOffset.Y);
        return (view, PlacementMode.Bottom, caret);
    }

    private void GoToDefinition_Click(object sender, RoutedEventArgs e) => _ = GoToDefinitionAsync(Editor.CaretOffset, fromMouse: false);

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void ContextMenu_Opened(object sender, RoutedEventArgs e) => BackMenuItem.IsEnabled = _back.Count > 0;

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
