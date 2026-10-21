using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.Highlighting;
using W3ModWorkbench.Localisation;

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

    /// <summary>The file on disk when it's in a script project: it can be edited and saved. Null for anything read-only.</summary>
    public string? DiskPath => Script?.DiskPath;

    private bool _checked;
    private string? _protection;

    /// <summary>Why the file on disk can't be changed (the game's, an installed mod's, REDkit's), or null when it can, or isn't on disk.</summary>
    public string? Protection
    {
        get
        {
            if (!_checked)
            {
                _protection = DiskPath is { } disk ? FileSafety.Protection(disk) : null;
                _checked = true;
            }
            return _protection;
        }
    }

    /// <summary>On disk, and not one of the game's files: it can be edited and saved.</summary>
    public bool IsEditable => DiskPath is not null && Protection is null;

    /// <summary>
    /// The file's game path, for a copy in a project: where it is in the game's folders for one on disk, or its path
    /// in its archive. Null for a file that has none, such as a log.
    /// </summary>
    public string? GamePath => DiskPath is { } disk ? FileSafety.GamePathOf(disk) : Script is not null || Source.Contains('\\') ? Path : null;
}

/// <summary>
/// One file in a <see cref="PreviewWindow"/>: the code view with its search bar, go to definition, the
/// definition popup and in-place messages. The window shows it in a tab and handles where definitions open.
/// A script project's files can be edited and saved; everything else is read-only.
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
    private bool _linkWaits; // waiting for the scripts to be read before underlining

    // every use of the word at the caret, marked
    private readonly WordMarks _marks = new();
    private readonly DispatcherTimer _marksTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private (int Offset, int Length)? _pendingReveal;
    private double? _pendingScroll;

    // the definition popup
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private (int Start, int Length) _peekWord;
    private int _peekVersion;
    private string[]? _lines;

    // editing
    private readonly DispatcherTimer _reparseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private TextDocument? _hooked;
    private Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private int _size;

    public PreviewItem Item { get; }

    /// <summary>The text was changed since it was opened or last saved, or saved again.</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>The window showing it; null once it's closed.</summary>
    public PreviewWindow? Host { get; internal set; }

    public PreviewDocument(PreviewItem item)
    {
        InitializeComponent();
        Item = item;
        _hintElements = new HintElements(this);

        EditorLook.Apply(Editor);
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);
        // a project's file is read from disk, so one opened again after a save shows what was saved
        byte[] data = item.Data;
        if (item.DiskPath is { } disk)
        {
            try { data = File.ReadAllBytes(disk); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _encoding = EncodingOf(data);
        _size = data.Length;
        Editor.Text = TextDecoding.Decode(data);
        Editor.Document.UndoStack.ClearAll();
        Editor.Document.UndoStack.MarkAsOriginalFile(); // ClearAll leaves the text loaded counting as a change
        UpdateReadOnly();
        HookDocument();
        _search = SearchPanel.Install(Editor.TextArea);
        _reparseTimer.Tick += (_, _) =>
        {
            _reparseTimer.Stop();
            UpdateOutline();
        };
        Editor.TextArea.PreviewTextInput += (_, _) =>
        {
            if (Editor.IsReadOnly && !Card.IsOpen) ShowReadOnlyHint();
        };
        Editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (Editor.IsReadOnly && IsEditable && !Card.IsOpen && e.Key is Key.Back or Key.Delete or Key.Return or Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
                ShowReadOnlyHint();
        };
        // whether the file is in the open project decides whether it can be typed in: follows the project opening, closing and changing
        Loaded += (_, _) =>
        {
            ProjectWorkspace.Changed -= OnProjectChanged;
            ProjectWorkspace.Changed += OnProjectChanged;
            UpdateReadOnly();
        };
        Unloaded += (_, _) => ProjectWorkspace.Changed -= OnProjectChanged;

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
            Outline.Picked += GoToOwn;
            Outline.CloseRequested += () => SetOutlineShown(false);
            ApplyOutline();
            Editor.TextArea.Caret.PositionChanged += (_, _) =>
            {
                // while typing, the outline waits until the typing stops
                if (!_reparseTimer.IsEnabled) UpdateOutline();
                CloseHint();
                if (_workspace is not null) _hintTimer.Start();
            };
            UpdateOutline();
            SetUpWordMarks();
            SetUpInsights();
        }
        else if (IsXml(item.Path))
        {
            // XML has an outline of its elements and folds like the XML editor
            _isXml = true;
            _folding = FoldingManager.Install(Editor.TextArea);
            Loaded += (_, _) => FoldingStyle.Apply(Editor, this);
            Outline.Picked += symbol => Reveal(symbol.Offset, symbol.Name.Length);
            Outline.CloseRequested += () => SetOutlineShown(false);
            ApplyOutline();
            Editor.TextArea.Caret.PositionChanged += (_, _) =>
            {
                if (!_reparseTimer.IsEnabled) UpdateOutline();
            };
            UpdateOutline();
        }
        else
            OutlineBar.Visibility = Visibility.Collapsed;

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
    public string Detail => $"{Item.Source}  ·  {Item.Path}  ·  {Format.Bytes(_size)}"
        + (IsDirty ? L.T("PreviewDocument.NotSaved") : Item.IsScript && !IsEditable ? L.T("PreviewDocument.ReadOnly") : IsEditable && !InProject ? L.T("PreviewDocument.ReadOnlyNotProject") : "");

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
            Editor.TextArea.TextView.LineTransformers.Insert(0, new WitcherScriptColorizer(Editor.Document, _dark) { View = Editor.TextArea.TextView });
        else if (IsXml(Item.Path))
            Editor.TextArea.TextView.LineTransformers.Insert(0, new XmlColorizer(Editor.Document, _dark) { View = Editor.TextArea.TextView });

        // the uses of the word at the caret: a grey that's quieter than the selection's accent
        var shade = _dark ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00);
        var fill = new SolidColorBrush(shade);
        fill.Freeze();
        var edge = new Pen(new SolidColorBrush(Color.FromArgb((byte)(shade.A * 2), shade.R, shade.G, shade.B)), 1);
        edge.Freeze();
        (_marks.Fill, _marks.Edge) = (fill, edge);

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
        ApplyInsightColours();
    }

    /// <summary>Whether a file is XML, by its extension, and so coloured as XML.</summary>
    public static bool IsXml(string path) =>
        System.IO.Path.GetExtension(path) is { } ext && (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xsd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".config", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the window ended up with a dark theme, judged by how bright its text colour is.</summary>
    private bool IsDarkTheme() =>
        TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
        && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;

    public void FocusEditor() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Editor.TextArea.Focus());

    public void Copy() => Clipboard.SetText(Editor.Text);

    // ---------------------------------------------------------------- editing and saving

    /// <summary>Whether the file is on disk, and so could be edited: a loose script. Whether it can be edited now also depends on <see cref="InProject"/>.</summary>
    public bool IsEditable => Item.IsEditable;

    /// <summary>Whether the file is part of the open project.</summary>
    public bool InProject => Item.DiskPath is { } disk && ProjectWorkspace.IsMember(disk);

    /// <summary>
    /// Only the open project's files can be typed in. Anything else is read-only: typing in it offers to copy it into the
    /// project (or, without one, to make one), so what's changed is always the project's own copy.
    /// </summary>
    private void UpdateReadOnly() => Editor.IsReadOnly = !(IsEditable && InProject);

    private void OnProjectChanged()
    {
        UpdateReadOnly();
        Host?.OnDirtyChanged(this);
    }

    public bool IsDirty => !Editor.Document.UndoStack.IsOriginalFile;

    /// <summary>Whether this and <paramref name="other"/> are two views of the same text, so editing one edits both.</summary>
    public bool SharesTextWith(PreviewDocument other) => other.Editor.Document == Editor.Document;

    /// <summary>Shows <paramref name="other"/>'s text, edits and all, as a second view of it. Called before it's shown.</summary>
    public void ShareTextWith(PreviewDocument other)
    {
        // fold markers belong to one document: start again on the shared one
        if (_folding is not null) FoldingManager.Uninstall(_folding);
        Editor.Document = other.Editor.Document;
        if (_folding is not null)
        {
            _folding = FoldingManager.Install(Editor.TextArea);
            if (IsLoaded) FoldingStyle.Apply(Editor, this);
        }
        _encoding = other._encoding;
        _size = other._size;
        HookDocument();
        Document_TextChanged(null, EventArgs.Empty);
    }

    private void HookDocument()
    {
        if (_hooked is not null)
        {
            _hooked.TextChanged -= Document_TextChanged;
            _hooked.UndoStack.PropertyChanged -= UndoStack_PropertyChanged;
        }
        _hooked = Editor.Document;
        _hooked.TextChanged += Document_TextChanged;
        _hooked.UndoStack.PropertyChanged += UndoStack_PropertyChanged;
    }

    /// <summary>What was read from the text is out of date: it's read again the next time it's needed.</summary>
    private void Document_TextChanged(object? sender, EventArgs e)
    {
        _doc = null;
        _finder = null;
        _finderLayers = [];
        _lines = null;
        ClearLink();
        _outlineStale = true;
        _foldStale = true;
        if (OutlineBar.Visibility == Visibility.Visible)
        {
            _reparseTimer.Stop();
            _reparseTimer.Start();
        }
    }

    private void UndoStack_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile)) DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The encoding the file came in, by its byte order mark, so it's saved the same way.</summary>
    private static Encoding EncodingOf(byte[] data) =>
        data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
        : data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF ? new UnicodeEncoding(bigEndian: true, byteOrderMark: true)
        : new UTF8Encoding(encoderShouldEmitUTF8Identifier: data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF);

    private byte[] Encode(string text) => [.. _encoding.GetPreamble(), .. _encoding.GetBytes(text)];

    /// <summary>
    /// Ctrl+S: writes a project's file back to disk and reads the project's scripts again, so go to definition
    /// sees the change. A read-only file is saved somewhere else instead. False when it wasn't saved.
    /// </summary>
    public bool Save()
    {
        if (!IsEditable || !InProject) return SaveAs();
        if (!IsDirty) return true;
        // laid out first when Settings asks; a file the formatter won't touch is saved as it is
        if (App.Settings.FormatOnSave && Item.IsScript) FormatScript(quiet: true);
        byte[] data = Encode(Editor.Text);
        try
        {
            FileSafety.CheckWrite(Item.DiskPath!);
            File.WriteAllBytes(Item.DiskPath!, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ShowCard(L.T("PreviewDocument.CouldNotSaved", FileName), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return false;
        }
        _size = data.Length;
        Editor.Document.UndoStack.MarkAsOriginalFile();
        if (Item.Script is { } script && _workspace?.ArchiveOf(script) is { } archive) ScriptWorkspace.Reindex(archive);
        AnySaved?.Invoke(this);
        return true;
    }

    /// <summary>Forgets the changes, so the tab closes without asking: for a file about to be renamed or deleted.</summary>
    public void DiscardChanges() => Editor.Document.UndoStack.MarkAsOriginalFile();

    /// <summary>
    /// Asks whether to save changes before the file closes. True when it can close: saved, or the changes
    /// are to be thrown away; false to keep it open.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty) return true;
        return await Ask(L.T("PreviewDocument.SaveChanges", FileName), L.T("PreviewDocument.TheyreLostIfYou"), MessageKind.Question, L.T("Common.Save"), L.T("Common.DontSave")) switch
        {
            MessageChoice.Primary => Save(),
            MessageChoice.Secondary => true,
            _ => false,
        };
    }

    /// <summary>Typing in a read-only file says why it can't be changed, and for a decompiled script, how it can.</summary>
    private async void ShowReadOnlyHint()
    {
        if (Host is not { } host) return;
        int at = Editor.CaretOffset;
        if (IsEditable && !InProject)
        {
            await AskToJoinProjectAsync(host);
            return;
        }
        // the game's own files, and anything in an archive, are changed by copying them into a project
        string? why = Item.Protection;
        if (why is not null || !host.CanEditAsProject(this) && Item.GamePath is not null && host.CanCopyToProject(this))
        {
            if (await ShowCard(why is not null ? L.T("PreviewDocument.FileReadOnly") : L.T("PreviewDocument.FilesArchivesReadOnly"),
                    L.T("PreviewDocument.CopyIsWhatModChanges", why ?? L.T("PreviewDocument.InsideBundleCompiledScripts")),
                    MessageKind.Info, primary: L.T("Common.CopyProject"), secondary: L.T("Common.Close"), anchor: at))
                _ = host.CopyToProjectAsync(this);
            return;
        }
        if (host.CanEditAsProject(this))
        {
            if (await ShowCard(L.T("PreviewDocument.DecompiledScriptsReadOnly"),
                    L.T("PreviewDocument.SaveModsScriptsFolder"),
                    MessageKind.Info, primary: L.T("PreviewDocument.EditProject"), secondary: L.T("Common.Close"), anchor: at))
                _ = host.EditAsProjectAsync(this);
        }
        else if (Item.Script is not null)
            _ = ShowCard(L.T("PreviewDocument.ScriptReadOnly"),
                L.T("PreviewDocument.OnlyYourProjectsScripts"),
                MessageKind.Info, primary: L.T("Common.OK"), anchor: at);
        else
            _ = ShowCard(null, L.T("PreviewDocument.FilesInsideBundlesRead"), MessageKind.Info, anchor: at, autoClose: true);
    }

    /// <summary>
    /// A loose file outside the project is typed in. Only the project's files are changed: a script is copied into the
    /// project's scripts folder (the file here is left as it is), another file can be added to the project. Without a
    /// project, a script's folder can be made into one.
    /// </summary>
    private async Task AskToJoinProjectAsync(PreviewWindow host)
    {
        if (ProjectWorkspace.Current is { } project)
        {
            if (Item.IsScript)
            {
                if (await ShowCard(L.T("PreviewDocument.Isnt", FileName, project.Name),
                        L.T("PreviewDocument.OnlyProjectsScriptsCan", project.ScriptsFolder),
                        MessageKind.Question, primary: L.T("Common.CopyProject"), secondary: L.T("Common.Close")))
                    await host.CopyToProjectAsync(this);
            }
            else if (await ShowCard(L.T("PreviewDocument.Isnt", FileName, project.Name),
                         L.T("PreviewDocument.OnlyProjectsFilesCan"),
                         MessageKind.Question, primary: L.T("Common.AddProject"), secondary: L.T("Common.Close")))
                await host.AddToProjectAsync(this);
        }
        else if (Item.IsScript)
        {
            if (await ShowCard(L.T("PreviewDocument.ScriptIsntProject"),
                    L.T("PreviewDocument.OnlyProjectsScriptsCan2"),
                    MessageKind.Question, primary: L.T("PreviewDocument.CreateProject"), secondary: L.T("Common.Close")))
                await host.CreateProjectForAsync(this);
        }
        else
            _ = ShowCard(L.T("PreviewDocument.FileReadOnly"), L.T("PreviewDocument.OnlyProjectsFilesCan2"),
                MessageKind.Info, primary: L.T("Common.OK"));
    }

    /// <summary>Saves the text somewhere else: as it is now for a project's file, otherwise exactly as it was read.</summary>
    public bool SaveAs()
    {
        string ext = Path.GetExtension(Item.Path);
        var dialog = new SaveFileDialog
        {
            Title = L.T("PreviewDocument.SaveFile"),
            FileName = Item.Name,
            Filter = ext.Length > 0 ? L.T("PreviewDocument.FileAllFiles", ext.TrimStart('.').ToUpperInvariant(), ext) : L.T("PreviewDocument.AllFiles"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return false;
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            File.WriteAllBytes(dialog.FileName, IsEditable ? Encode(Editor.Text) : Item.Data);
            return true;
        }
        catch (Exception ex)
        {
            _ = ShowCard(L.T("Common.FileCouldNotSaved"), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return false;
        }
    }

    /// <summary>Esc closes a message first, then the search bar. False when there was neither.</summary>
    public bool HandleEscape()
    {
        ClosePeek();
        if (HintPopup.IsOpen) CloseHint();
        else if (Card.IsOpen) CloseCard(false);
        else if (!_search.IsClosed) _search.Close();
        else if (_snippet is not null) EndSnippet();
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
        else if (scripts && key == Key.F2 && mods == ModifierKeys.None)
            _ = RenameAsync();
        else if (scripts && key == Key.J && mods == ModifierKeys.Control && !Editor.IsReadOnly)
            ShowSnippetPicker();
        else if (scripts && key == Key.Space && mods == ModifierKeys.Control && !Editor.IsReadOnly)
            _ = ShowCompletionAsync(explicitly: true);
        else if (key == Key.O && mods == (ModifierKeys.Control | ModifierKeys.Shift) && OutlineBar.Visibility == Visibility.Visible)
            ToggleOutline();
        else if (key == Key.F8 && mods is ModifierKeys.None or ModifierKeys.Shift && HasDiff)
            GoToDifference(mods == ModifierKeys.Shift ? -1 : 1);
        else if (scripts && key == Key.OemPeriod && mods == ModifierKeys.Control)
            ShowRefactors();
        else if (Item.Script is not null && key == Key.F && mods == (ModifierKeys.Shift | ModifierKeys.Alt))
            FormatDocument();
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

    // ---------------------------------------------------------------- the word at the caret

    /// <summary>Most uses of a word marked at once: past this it's a common word, and marking it says nothing.</summary>
    private const int MaxMarks = 2000;

    /// <summary>Marks every use of the name at the caret, or of the name selected, through the file, as code editors do.</summary>
    private void SetUpWordMarks()
    {
        Editor.TextArea.TextView.BackgroundRenderers.Add(_marks);
        _marksTimer.Tick += (_, _) =>
        {
            _marksTimer.Stop();
            UpdateWordMarks();
        };
        void Later()
        {
            _marksTimer.Stop();
            _marksTimer.Start();
        }
        Editor.TextArea.Caret.PositionChanged += (_, _) => Later();
        Editor.TextArea.SelectionChanged += (_, _) => Later();
        // the marks belong to the text as it was: gone at once, and found again once typing stops
        Editor.TextChanged += (_, _) =>
        {
            SetMarks(0, []);
            Later();
        };
    }

    private void UpdateWordMarks()
    {
        var document = Editor.Document;
        (int Start, int Length)? word = null;
        if (Editor.TextArea.Selection.IsEmpty) word = WordAround(document, Editor.CaretOffset);
        else if (Editor.SelectionLength is > 0 and < 100 && WordAround(document, Editor.SelectionStart) is { } at
                 && at.Start == Editor.SelectionStart && at.Length == Editor.SelectionLength)
            word = at;
        if (word is not { } w || DefinitionFinder.IsKeyword(document.GetText(w.Start, w.Length)))
        {
            SetMarks(0, []);
            return;
        }
        // once the scripts are read, only the uses of the same thing: not another class's member or another function's local
        if (Finder() is { } finder && Item.Script is not null)
        {
            try
            {
                if (finder.Uses(w.Start, w.Length) is { } uses)
                {
                    SetMarks(w.Length, uses.Count is > 1 and <= MaxMarks ? uses : []);
                    return;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
            {
                // the text moved on: the words spelt the same will do
            }
        }
        string code = document.Text, name = code.Substring(w.Start, w.Length);
        var found = new List<int>();
        for (int i = code.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0 && found.Count <= MaxMarks;
             i = code.IndexOf(name, i + name.Length, StringComparison.OrdinalIgnoreCase))
        {
            int end = i + name.Length;
            if (i > 0 && ScriptParser.IsWordChar(code[i - 1]) || end < code.Length && ScriptParser.IsWordChar(code[end])) continue;
            found.Add(i);
        }
        // the one at the caret alone says nothing; a word used everywhere would cover the page
        SetMarks(w.Length, found.Count is > 1 and <= MaxMarks ? found : []);
    }

    private void SetMarks(int length, List<int> offsets)
    {
        if (_marks.Offsets.Count == 0 && offsets.Count == 0) return;
        (_marks.Length, _marks.Offsets) = (length, offsets);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>The name at or just before an offset, if any.</summary>
    private static (int Start, int Length)? WordAround(TextDocument document, int offset)
    {
        int start = offset, end = offset;
        while (start > 0 && ScriptParser.IsWordChar(document.GetCharAt(start - 1))) start--;
        while (end < document.TextLength && ScriptParser.IsWordChar(document.GetCharAt(end))) end++;
        return end > start && ScriptParser.IsWordStart(document.GetCharAt(start)) ? (start, end - start) : null;
    }

    /// <summary>Draws a soft box behind each use of the word at the caret that's on screen.</summary>
    private sealed class WordMarks : IBackgroundRenderer
    {
        public List<int> Offsets { get; set; } = [];
        public int Length { get; set; }
        public Brush? Fill { get; set; }
        public Pen? Edge { get; set; }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Length == 0 || Offsets.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            int first = textView.VisualLines[0].FirstDocumentLine.Offset, last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
            int i = Offsets.BinarySearch(first);
            for (i = i < 0 ? ~i : i; i < Offsets.Count && Offsets[i] <= last; i++)
            {
                if (Offsets[i] + Length > textView.Document.TextLength) break;
                var builder = new BackgroundGeometryBuilder { CornerRadius = 3, AlignToWholePixels = true };
                builder.AddSegment(textView, new TextSegment { StartOffset = Offsets[i], Length = Length });
                if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(Fill, Edge, geometry);
            }
        }
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
        // Tab expands a snippet's name and moves between the places to type in one
        Editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && _completion is null && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift && !Editor.IsReadOnly && TabInSnippet(Keyboard.Modifiers == ModifierKeys.Shift))
                e.Handled = true;
        };
        Editor.TextArea.TextEntered += (_, e) => OnTextEntered(e.Text);
        Editor.TextArea.TextEntering += (_, e) => OnTextEntering(e);
        Editor.TextArea.Caret.PositionChanged += (_, _) => EndSnippetIfLeft();
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
        // the scripts are still being read: try again once they are, without waiting for the mouse to move
        if (_workspace is { IsReady: false } && !_linkWaits)
        {
            _linkWaits = true;
            _workspace.Ready.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                _linkWaits = false;
                UpdateLink();
            }), TaskScheduler.Default);
        }
        if (!Editor.TextArea.TextView.IsMouseOver
            || Finder() is not { } finder || OffsetAtMouse() is not { } offset || finder.WordAt(offset) is not { } word)
        {
            ClearLink();
            return;
        }
        if (_probed != word)
        {
            _probed = word;
            // a declaration leads to its uses when it changes nothing else
            _probedFound = finder.Find(word.Start, word.Length).Count > 0 || finder.IsDeclaration(word.Start)
                || NativeDeclarations.Has(Editor.Document.GetText(word.Start, word.Length));
        }
        SetLink(_probedFound ? word : null, strong: (Keyboard.Modifiers & ModifierKeys.Control) != 0);

        // a problem's message shows there instead
        if (!_probedFound || !App.Settings.PeekDefinitions || Card.IsOpen || OverProblem(offset))
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
        // after an edit the old underline may lie past the end of the text
        if (_link.Length > 0)
        {
            if (_link.Start + _link.Length <= Editor.Document.TextLength) view.Redraw(_link.Start, _link.Length);
            else view.Redraw();
        }
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
            _ = ShowCard(null, L.T("PreviewDocument.ReadingScripts"), MessageKind.Busy, anchor: offset);
            Cursor = Cursors.AppStarting;
            await _workspace.Ready;
            ClearValue(CursorProperty);
            CloseCard(false);
        }
        if (Finder() is not { } finder || finder.WordAt(offset) is not { } word) return;

        var targets = finder.Find(word.Start, word.Length);
        if (targets.Count == 0 && finder.IsDeclaration(word.Start))
        {
            // already at the declaration, and it changes nothing elsewhere: where it's used is where to go
            await FindReferencesAsync(word.Start);
        }
        else if (targets.Count == 1)
        {
            await NavigateAsync(targets[0], toSide);
        }
        else if (targets.Count > 1)
        {
            ShowChoices(targets, fromMouse, toSide);
        }
        else if (NativeDeclarations.For(Editor.Document.GetText(word.Start, word.Length), finder.OwnerOf(word.Start), finder.Layers) is { } native)
        {
            // the engine declares it in its own code: written out from its class list, to read
            var file = new ScriptFile
            {
                Key = "native:" + native.Name,
                Path = native.Path,
                Source = L.T("ScriptBench.Native.Source"),
                SourceKey = "native",
                Read = () => System.Text.Encoding.UTF8.GetBytes(native.Text),
            };
            await NavigateAsync(file, native.Offset, native.Name.Length, toSide);
        }
        else
        {
            string text = Editor.Document.GetText(word.Start, word.Length);
            if (_workspace.GameScriptsFolder is null)
                _ = ShowCard(L.T("PreviewDocument.NoDefinitionFound", text), L.T("PreviewDocument.GamesScriptsWerentFound"),
                    MessageKind.Info, primary: L.T("Common.OK"), anchor: word.Start);
            else
                ShowNotFound(finder, text, word.Start, L.T("PreviewDocument.NoDefinitionFound", text));
        }
    }

    /// <summary>
    /// Nothing declares the word anywhere searched. A type by then is often one the game declares in its
    /// engine, though it could also come from a blob that isn't installed or open, so suggest both.
    /// </summary>
    private void ShowNotFound(DefinitionFinder finder, string text, int offset, string message)
    {
        if (finder.IsTypePosition(offset))
            _ = ShowCard(L.T("PreviewDocument.MayNativeEngineType", text),
                L.T("PreviewDocument.NothingOpenArchivesGames"),
                MessageKind.Info, primary: L.T("Common.OK"), anchor: offset);
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
            menu.Items.Add(new MenuItem { Header = L.T("PreviewDocument.More", targets.Count - 30), IsEnabled = false });
        Place(menu, fromMouse);
        menu.IsOpen = true;
    }

    private Task NavigateAsync(ScriptSymbol target, bool toSide) => Host?.NavigateAsync(this, target, toSide) ?? Task.CompletedTask;

    private Task NavigateAsync(ScriptFile file, int offset, int length, bool toSide) =>
        Host?.NavigateAsync(this, file, offset, length, toSide) ?? Task.CompletedTask;

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
            _ = ShowCard(null, L.T("PreviewDocument.ReadingScripts"), MessageKind.Busy, anchor: Editor.CaretOffset);
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
    private async Task<(DefinitionFinder Finder, IReadOnlyList<ScriptSymbol> Targets, string Word, int Start)?> TargetsAtCaretAsync(int offset, bool quiet = false)
    {
        if (await FinderAsync() is not { } finder || finder.WordAt(offset) is not { } word) return null;
        string text = Editor.Document.GetText(word.Start, word.Length);
        var declared = Parsed.Symbols.FirstOrDefault(s => s.Offset == word.Start);
        IReadOnlyList<ScriptSymbol> targets = declared is null or { Annotation: "wrapMethod" or "replaceMethod" }
            ? finder.Find(word.Start, word.Length)
            : [declared];
        if (targets.Count == 0 && !quiet)
            _ = ShowCard(null, L.T("PreviewDocument.DoesntLeadDeclarationScripts", text), MessageKind.Info, anchor: word.Start, autoClose: true);
        return targets.Count == 0 ? null : (finder, targets, text, word.Start);
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
        Host?.ShowResults(this, L.T("PreviewDocument.References", what), L.T("PreviewDocument.LookingThroughEveryScript"), cancel =>
        {
            var hits = ScriptSearch.References(targets, parsed, layers, cancel);
            return (ResultRow.ByFile(hits), PreviewWindow.Summary(hits, "PreviewWindow.References"));
        }, L.T("PreviewDocument.NothingScriptsSearchedUses", what));
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
            _ = ShowCard(null, L.T("PreviewDocument.WorksFunctionsEventsTypes"), MessageKind.Info, anchor: found.Start, autoClose: true);
            return;
        }
        var finder = found.Finder;
        var workspace = _workspace!;
        Host?.ShowResults(this, L.T("PreviewDocument.Changes", target.Title), L.T("PreviewDocument.LookingWrapsOverridesReplacements"), _ =>
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
                rows.Add(ResultRow.Heading(L.T("PreviewDocument.WholeFileReplaced"), files.Count));
                rows.AddRange(files.Select(f => ResultRow.FromFile(f, label: L.T("PreviewDocument.LooseFile"))));
                count += files.Count;
            }
            return (rows, count == 1 ? L.T("PreviewDocument.Change") : L.T("PreviewDocument.Changes2", count));
        }, workspace.GameScriptsFolder is null ? L.T("PreviewDocument.ChangesNoGame") : L.T("PreviewDocument.NothingScriptsSearchedChanges"));
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
            _ = ShowCard(null, L.T("PreviewDocument.TypeHierarchyWorksClasses"), MessageKind.Info, anchor: found.Start, autoClose: true);
            return;
        }
        var finder = found.Finder;
        Host?.ShowResults(this, L.T("PreviewDocument.Hierarchy", target.Title), L.T("PreviewDocument.WorkingOutHierarchy"), _ =>
        {
            var rows = new List<ResultRow> { ResultRow.FromSymbol(target, depth: 0, label: L.T("PreviewDocument.One")) };
            var related = finder.Hierarchy(target);
            foreach (var group in related.GroupBy(r => r.Group))
            {
                var list = group.ToList();
                rows.Add(ResultRow.Heading(group.Key, list.Count));
                rows.AddRange(list.Select(r => ResultRow.FromSymbol(r.Symbol, depth: Math.Min(r.Depth, 12))));
            }
            int subtypes = related.Count(r => r.Kind is DefinitionFinder.RelatedKind.Subtypes or DefinitionFinder.RelatedKind.OverriddenBy);
            return (rows, L.T("PreviewDocument.AboveBelow", related.Count(r => r.Kind is DefinitionFinder.RelatedKind.BaseTypes or DefinitionFinder.RelatedKind.Overrides), subtypes));
        }, "");
    }

    /// <summary>A graph of what uses the name at the caret, what uses those, and so on, with the name itself at the end.</summary>
    private async Task ShowUsageGraphAsync(int? at = null)
    {
        int offset = at ?? Editor.CaretOffset;
        if (Host is null) return;
        if (await TargetsAtCaretAsync(offset) is not { } found) return;
        var targets = found.Targets.Where(t => t.Kind is not (SymbolKind.Local or SymbolKind.Parameter)).ToList();
        if (targets.Count == 0)
        {
            _ = ShowCard(null, L.T("PreviewDocument.UsageGraphWorksFunctions"), MessageKind.Info,
                anchor: found.Start, autoClose: true);
            return;
        }
        // a wrap or replace stands for the function it changes, as calls are written against that one
        var shown = targets[0].Kind is SymbolKind.Function or SymbolKind.Event
            ? UsageGraphBuilder.Targets(targets[0], Parsed, found.Finder.Layers)
            : targets;
        UsageGraphWindow.Open(Host, this, shown, Parsed, found.Finder.Layers);
    }

    // ---------------------------------------------------------------- outline

    /// <summary>
    /// The class and function the caret is in, each opening a menu of the others in the file, and in the outline
    /// side bar when it's shown, the declaration the caret is in.
    /// </summary>
    private void UpdateOutline()
    {
        if (OutlineBar.Visibility != Visibility.Visible) return;
        int offset = Editor.CaretOffset;
        if (_isXml)
        {
            UpdateXmlOutline(offset);
            return;
        }
        if (OutlineHost.Visibility == Visibility.Visible)
        {
            if (_outlineStale)
            {
                _outlineStale = false;
                Outline.Update(Parsed);
            }
            Outline.Reveal(offset);
        }
        var type = Parsed.TypeAt(offset);
        var function = Parsed.FunctionAt(offset);
        if (type == _crumbType && function == _crumbFunction && Crumbs.Children.Count > 0) return;
        (_crumbType, _crumbFunction) = (type, function);

        Crumbs.Children.Clear();
        Crumbs.Children.Add(Crumb(type?.Symbol, type is null ? L.T("PreviewDocument.File") : TypeTitle(type.Symbol), () => TypeChoices()));
        if (function is not null || type is not null)
        {
            Crumbs.Children.Add(new TextBlock { Text = "", FontSize = 9, Opacity = 0.5, Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)FindResource("IconFont") });
            Crumbs.Children.Add(Crumb(function?.Symbol, function is null ? "…" : function.Symbol.Name + "()", () => FunctionChoices(type)));
        }
    }

    private bool _isXml;
    private FoldingManager? _folding;
    private bool _foldStale = true;
    private readonly XmlFoldingStrategy _foldingStrategy = new() { ShowAttributesWhenFolded = true };

    /// <summary>An XML file's elements in the side bar, with the one the caret is in picked out, and its fold markers up to date.</summary>
    private void UpdateXmlOutline(int offset)
    {
        if (_foldStale && _folding is not null)
        {
            _foldStale = false;
            if (Editor.Document.TextLength < 3_000_000)
            {
                try { _foldingStrategy.UpdateFoldings(_folding, Editor.Document); }
                catch (System.Xml.XmlException) { }
            }
        }
        if (OutlineHost.Visibility != Visibility.Visible) return;
        if (_outlineStale)
        {
            _outlineStale = false;
            Outline.UpdateXml(Editor.Text, Item.Script ?? new ScriptFile
            {
                Key = Item.Key, Path = Item.Path, Source = Item.Source, SourceKey = Item.Source, Read = () => [],
            });
        }
        Outline.Reveal(offset);
    }

    private TypeScope? _crumbType;
    private FunctionScope? _crumbFunction;
    private bool _outlineStale = true;

    /// <summary>
    /// Ctrl+Shift+O and the Outline button: shows or hides the outline side bar. It's per view, so in a split each side has its own. Showing it puts the keyboard in its filter.
    /// </summary>
    public void ToggleOutline()
    {
        bool show = OutlineHost.Visibility != Visibility.Visible;
        SetOutlineShown(show);
        if (show) Outline.FocusFilter();
        else Editor.TextArea.Focus();
    }

    /// <summary>This view's side bar only: the other views of a split keep theirs. What's chosen last is what new files start with.</summary>
    private void SetOutlineShown(bool show)
    {
        _outlineWanted = show;
        App.Settings.ShowOutline = show;
        App.Settings.Save();
        ApplyOutline();
    }

    private bool? _outlineWanted;

    /// <summary>Shows or hides the outline side bar as Settings says, for a script.</summary>
    public void ApplyOutline()
    {
        bool show = OutlineBar.Visibility == Visibility.Visible && (_outlineWanted ?? App.Settings.ShowOutline);
        OutlineToggle.IsChecked = show;
        if (show == (OutlineHost.Visibility == Visibility.Visible))
        {
            if (show) OutlineColumn.Width = new GridLength(App.Settings.OutlineWidth);
            return;
        }
        OutlineHost.Visibility = OutlineSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OutlineSplitterColumn.Width = new GridLength(show ? 6 : 0);
        OutlineColumn.Width = new GridLength(show ? App.Settings.OutlineWidth : 0);
        OutlineColumn.MinWidth = show ? 160 : 0;
        if (show)
        {
            _outlineStale = true;
            UpdateOutline();
        }
    }

    /// <summary>The side bar's width is kept for every script.</summary>
    private void OutlineSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        App.Settings.OutlineWidth = Math.Round(OutlineColumn.ActualWidth);
        App.Settings.Save();
        PreviewWindow.ApplyOutlineSetting();
    }

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
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("PreviewDocument.NothingDeclaredHere"), IsEnabled = false });
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
        host.ShowPicker(L.T("PreviewDocument.GoSymbol", FileName), query =>
        {
            var shown = query.Length == 0 ? symbols : symbols.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (s.OwnerTitle ?? s.Owner ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));
            return [.. shown.Take(400).Select(s => new QuickPickItem(DefinitionPeek.KindGlyph(s.Kind), s.Title, L.T("PreviewDocument.Line", s.Line), s))];
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
            _ = ShowCard(null, L.T("PreviewDocument.NoDifferencesFilesMatch"), MessageKind.Info, autoClose: true);
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

    // ---------------------------------------------------------------- snippets

    /// <summary>The snippet being filled in: the places to type in it, in order, the one the caret is on, and where it ends.</summary>
    private sealed class SnippetSession
    {
        public required List<(TextAnchor Start, TextAnchor End)> Fields { get; init; }
        public required TextAnchor Caret { get; init; }
        public required TextAnchor Start { get; init; }
        public required TextAnchor End { get; init; }
        public int Current { get; set; }
    }

    private SnippetSession? _snippet;

    /// <summary>Ctrl+J: the snippets, narrowed by what's typed, to insert at the caret.</summary>
    private void ShowSnippetPicker()
    {
        if (Host is not { } host || Editor.IsReadOnly) return;
        var snippets = Snippets.All();
        host.ShowPicker(L.T("PreviewDocument.InsertSnippetTypeName"), query =>
        {
            var shown = query.Length == 0 ? snippets : snippets.Where(s => s.Prefix.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
            return [.. shown.OrderBy(s => s.Prefix.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Select(s => new QuickPickItem(s.IsOwn ? "" : "", s.Prefix, s.Title, s))];
        }, item =>
        {
            if (item.Tag is Snippet s) InsertSnippet(s, Editor.SelectionStart, Editor.SelectionLength);
        }, L.T("PreviewDocument.TypeNamePressTab", Snippets.Folder));
    }

    /// <summary>Puts a snippet in place of the text from <paramref name="offset"/>, indented like its line, and selects the first place to type.</summary>
    private void InsertSnippet(Snippet snippet, int offset, int length)
    {
        EndSnippet();
        var document = Editor.Document;
        var line = document.GetLineByOffset(offset);
        string lineText = document.GetText(line.Offset, line.Length);
        string indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];
        string newline = TextUtilities.GetNewLineFromDocument(document, line.LineNumber);
        var expanded = Snippets.Expand(snippet, indent, newline);

        document.BeginUpdate();
        document.Replace(offset, length, expanded.Text);
        document.EndUpdate();

        TextAnchor Anchor(int at, AnchorMovementType movement)
        {
            var anchor = document.CreateAnchor(at);
            anchor.MovementType = movement;
            return anchor;
        }
        var fields = expanded.Fields
            .Select(f => (Anchor(offset + f.Offset, AnchorMovementType.BeforeInsertion), Anchor(offset + f.Offset + f.Length, AnchorMovementType.AfterInsertion)))
            .ToList();
        var caret = Anchor(offset + expanded.Caret, AnchorMovementType.AfterInsertion);
        if (fields.Count == 0)
        {
            Editor.Select(caret.Offset, 0);
            Editor.TextArea.Caret.Offset = caret.Offset;
            return;
        }
        _snippet = new SnippetSession
        {
            Fields = fields,
            Caret = caret,
            Start = Anchor(offset, AnchorMovementType.BeforeInsertion),
            End = Anchor(offset + expanded.Text.Length, AnchorMovementType.AfterInsertion),
        };
        SelectField(0);
        Editor.TextArea.Focus();
    }

    private void SelectField(int index)
    {
        if (_snippet is not { } s) return;
        s.Current = index;
        var (start, end) = s.Fields[index];
        Editor.Select(start.Offset, Math.Max(0, end.Offset - start.Offset));
        Editor.TextArea.Caret.Offset = end.Offset;
    }

    /// <summary>
    /// Tab in a script: in a snippet, to the next place to type (Shift+Tab, the one before), and after the last, to where
    /// the snippet ends. Otherwise a snippet's name just before the caret is expanded. False when Tab should just indent.
    /// </summary>
    private bool TabInSnippet(bool back)
    {
        if (_snippet is { } s)
        {
            int next = s.Current + (back ? -1 : 1);
            if (next >= 0 && next < s.Fields.Count)
            {
                SelectField(next);
                return true;
            }
            if (next < 0) return true;
            int caret = s.Caret.Offset;
            EndSnippet();
            Editor.Select(caret, 0);
            Editor.TextArea.Caret.Offset = caret;
            return true;
        }
        if (back || Editor.SelectionLength > 0) return false;
        int at = Editor.CaretOffset;
        var document = Editor.Document;
        // only at the end of a word that stands alone, with nothing but space after it on the line
        var line = document.GetLineByOffset(at);
        if (document.GetText(at, line.EndOffset - at).Trim().Length > 0) return false;
        int start = at;
        while (start > line.Offset && ScriptParser.IsWordChar(document.GetCharAt(start - 1))) start--;
        if (start == at || (start > line.Offset && !char.IsWhiteSpace(document.GetCharAt(start - 1)) && document.GetCharAt(start - 1) is not ('{' or '}' or ';'))) return false;
        string word = document.GetText(start, at - start);
        if (Snippets.All().FirstOrDefault(s => s.Prefix.Equals(word, StringComparison.OrdinalIgnoreCase)) is not { } snippet) return false;
        InsertSnippet(snippet, start, at - start);
        return true;
    }

    /// <summary>The caret went outside the snippet: it's done with.</summary>
    private void EndSnippetIfLeft()
    {
        if (_snippet is not { } s) return;
        int caret = Editor.CaretOffset;
        if (s.Start.IsDeleted || s.End.IsDeleted || caret < s.Start.Offset || caret > s.End.Offset) EndSnippet();
    }

    private void EndSnippet() => _snippet = null;

    private void Snippet_Click(object sender, RoutedEventArgs e) => ShowSnippetPicker();

    /// <summary>The window's snippet button: the picker, or why there isn't one for this file.</summary>
    public void InsertSnippet()
    {
        if (!CanNavigate)
            _ = ShowCard(null, L.T("PreviewDocument.SnippetsGoIntoScripts"), MessageKind.Info, autoClose: true);
        else if (Editor.IsReadOnly)
            _ = ShowCard(null, L.T("PreviewDocument.FileReadOnlySo"), MessageKind.Info, autoClose: true);
        else
            ShowSnippetPicker();
    }

    // ---------------------------------------------------------------- rename

    /// <summary>
    /// F2: a new name for the function, variable, type or enum value at the caret, everywhere it's used. Only names the
    /// open project declares (or a local in this file) are renamed, and only in the project's files: changing a name the
    /// game or a mod declares would break what uses it there.
    /// </summary>
    private async Task RenameAsync(int? at = null)
    {
        int offset = at ?? Editor.CaretOffset;
        if (Host is not { } host || await TargetsAtCaretAsync(offset) is not { } found) return;
        var (finder, targets, word, start) = found;
        bool local = targets.All(t => t.Kind is SymbolKind.Local or SymbolKind.Parameter);
        bool Ours(ScriptFile file) => file.DiskPath is { } disk && ProjectWorkspace.IsMember(disk);
        if (local ? !IsEditable || Editor.IsReadOnly : !targets.All(t => Ours(t.File)))
        {
            var elsewhere = targets.FirstOrDefault(t => !Ours(t.File));
            _ = ShowCard(L.T("PreviewDocument.CantRenamedHere", word),
                local ? L.T("PreviewDocument.FileCantEditedOpen")
                : L.T("PreviewDocument.DeclaredElsewhere", elsewhere?.File.Source ?? L.T("PreviewDocument.FileOutsideProject")),
                MessageKind.Info, primary: L.T("Common.OK"), anchor: start);
            return;
        }

        var target = targets[0];
        string what = local ? $"'{word}'" : target.Title;
        host.ShowPicker(L.T("PreviewDocument.NewName", what), query =>
        {
            string name = query.Trim();
            bool valid = name.Length > 0 && ScriptParser.IsWordStart(name[0]) && name.All(ScriptParser.IsWordChar);
            if (!valid) return [new QuickPickItem("", name.Length == 0 ? L.T("PreviewDocument.TypeNewName") : L.T("PreviewDocument.IsntName", name), L.T("PreviewDocument.LettersDigitsStartingLetter"), null)];
            if (name == word) return [new QuickPickItem("", L.T("PreviewDocument.TypeNewName"), L.T("PreviewDocument.SameNow"), null)];
            return [new QuickPickItem("", L.T("PreviewDocument.Rename2", name), L.T("PreviewDocument.EnterSeeWhatChanges"), name)];
        }, item =>
        {
            if (item.Tag is string name) _ = ApplyRenameAsync(finder, targets, word, name, start);
        }, L.T("PreviewDocument.EveryUseProjectsFiles"), text: word);
    }

    private async Task ApplyRenameAsync(DefinitionFinder finder, IReadOnlyList<ScriptSymbol> targets, string oldName, string newName, int anchor)
    {
        var parsed = Parsed;
        var layers = finder.Layers;
        _ = ShowCard(null, L.T("PreviewDocument.FindingEveryUse"), MessageKind.Busy, anchor: anchor);
        List<ScriptHit> hits;
        try
        {
            hits = await Task.Run(() => ScriptSearch.References(targets, parsed, layers, CancellationToken.None));
        }
        finally
        {
            CloseCard(false);
        }
        bool Ours(ScriptHit h) => h.File == Item.Script ? IsEditable : h.File.DiskPath is { } disk && ProjectWorkspace.IsMember(disk);
        var ours = hits.Where(Ours).ToList();
        var maybe = ours.Where(h => h.Label == "maybe").ToList();
        int outside = hits.Count - ours.Count;
        if (hits.Count >= ScriptSearch.MaxHits)
        {
            _ = ShowCard(L.T("PreviewDocument.TooManyUsesRename"), L.T("PreviewDocument.UsedMoreThanPlaces", oldName, ScriptSearch.MaxHits), MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }

        string files = L.T("Count.File", ours.Select(h => h.File.Key).Distinct().Count());
        string note = outside > 0 ? L.T("PreviewDocument.OutsideProjectGamesMods", L.T("Count.Use", outside)) : "";
        bool includeMaybe = true;
        if (maybe.Count > 0)
        {
            var choice = await Ask(L.T("PreviewDocument.Rename3", oldName, newName),
                L.T("PreviewDocument.TypeBeforeDotCouldnt", L.T("Count.Place", ours.Count), files, L.T("Count.Place", maybe.Count), note),
                MessageKind.Question, L.T("PreviewDocument.RenameAll", ours.Count), L.T("PreviewDocument.OnlyCertain", ours.Count - maybe.Count));
            if (choice == MessageChoice.Cancel) return;
            includeMaybe = choice == MessageChoice.Primary;
        }
        else if (!await ShowCard(L.T("PreviewDocument.Rename3", oldName, newName), L.T("PreviewDocument.In", L.T("Count.Place", ours.Count), files, note), MessageKind.Question, primary: L.T("PreviewDocument.Rename4"), secondary: L.T("Common.Cancel")))
            return;

        var changes = ours.Where(h => includeMaybe || h.Label != "maybe").ToList();
        var (changed, skipped, failed) = RenameIn(changes, oldName, newName);
        if (failed is not null)
        {
            _ = ShowCard(L.T("PreviewDocument.NotEveryFileCould"), failed, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        string summary = L.T("PreviewDocument.Renamed", L.T("Count.Place", changed.Count), (skipped > 0 ? L.T("PreviewDocument.SkippedChangedSinceThey", skipped) : ""));
        Host?.ShowResults(this, L.T("PreviewDocument.Renamed2", oldName, newName), "", _ => (ResultRow.ByFile(changed), summary), L.T("PreviewDocument.NothingRenamed"));
    }

    /// <summary>
    /// Writes the new name over each hit, in open views (as one undoable change each) or on disk. A hit whose text isn't the
    /// old name any more (the file changed since it was read) is skipped.
    /// </summary>
    private (List<ScriptHit> Changed, int Skipped, string? Failed) RenameIn(List<ScriptHit> hits, string oldName, string newName)
    {
        var changed = new List<ScriptHit>();
        int skipped = 0;
        var archives = new HashSet<IArchive>();
        foreach (var group in hits.GroupBy(h => h.File.Key))
        {
            var file = group.First().File;
            var spots = group.OrderByDescending(h => h.Offset).ToList();
            var open = file == Item.Script ? this : PreviewWindow.OpenDocument(file.Key);
            if (open is not null)
            {
                var document = open.Editor.Document;
                document.BeginUpdate();
                foreach (var hit in spots)
                {
                    if (Fits(document.Text, hit.Offset, oldName))
                    {
                        document.Replace(hit.Offset, oldName.Length, newName);
                        changed.Add(hit);
                    }
                    else skipped++;
                }
                document.EndUpdate();
                continue;
            }
            if (file.DiskPath is not { } path) continue;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                var encoding = EncodingOf(data);
                string text = TextDecoding.Decode(data);
                var sb = new StringBuilder(text);
                foreach (var hit in spots)
                {
                    if (Fits(text, hit.Offset, oldName))
                    {
                        sb.Remove(hit.Offset, oldName.Length).Insert(hit.Offset, newName);
                        changed.Add(hit);
                    }
                    else skipped++;
                }
                FileSafety.CheckWrite(path);
                File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(sb.ToString())]);
                if (_workspace?.ArchiveOf(file) is { } archive) archives.Add(archive);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (changed, skipped, L.T("PreviewDocument.CouldNotChangedFiles", file.Path, ex.Message));
            }
        }
        foreach (var archive in archives) ScriptWorkspace.Reindex(archive);
        return ([.. changed.GroupBy(h => h.File.Key).SelectMany(Renamed)], skipped, null);

        // where each place is now, with the new name in its line, in file order
        IEnumerable<ScriptHit> Renamed(IEnumerable<ScriptHit> inFile)
        {
            int shift = 0, delta = newName.Length - oldName.Length;
            int lineShift = 0, line = -1;
            foreach (var hit in inFile.OrderBy(h => h.Offset))
            {
                if (hit.Line != line) (line, lineShift) = (hit.Line, 0);
                int column = hit.Column + lineShift;
                string text = hit.LineText.Length >= column + oldName.Length
                    ? hit.LineText[..column] + newName + hit.LineText[(column + oldName.Length)..]
                    : hit.LineText;
                yield return hit with { Offset = hit.Offset + shift, Length = newName.Length, Column = column, LineText = text };
                shift += delta;
                lineShift += delta;
            }
        }
    }

    /// <summary>Whether the old name is still where the search found it, as a whole word.</summary>
    private static bool Fits(string text, int offset, string name) =>
        offset >= 0 && offset + name.Length <= text.Length
        && string.Compare(text, offset, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (offset == 0 || !ScriptParser.IsWordChar(text[offset - 1]))
        && (offset + name.Length == text.Length || !ScriptParser.IsWordChar(text[offset + name.Length]));

    private void Rename_Click(object sender, RoutedEventArgs e) => _ = RenameAsync();

    // ---------------------------------------------------------------- completion

    private ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow? _completion;

    /// <summary>Typing a dot lists the members of what's before it; starting a name lists the names that fit there.</summary>
    private void OnTextEntered(string text)
    {
        if (_completion is { } open)
        {
            // once nothing in the list fits what's typed it closes, so Enter is a new line again rather than a pick of nothing;
            // and a keyword typed out in full (if, for, return…) is what was meant, so the list gets out of the way
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (open.CompletionList.SelectedItem is null || Keyword(WordBeforeCaret())) open.Close();
            });
            return;
        }
        if (Editor.IsReadOnly || text.Length != 1 || !App.Settings.SuggestNames) return;
        char c = text[0];
        if (c == '.')
        {
            _ = ShowCompletionAsync(explicitly: false);
            return;
        }
        if (!ScriptParser.IsWordChar(c)) return;
        // the first letter under @wrapMethod(Class): the class's functions straight away
        if (AnnotationAbove() is not null && WordBeforeCaret().Length == 1)
        {
            _ = ShowCompletionAsync(explicitly: false);
            return;
        }
        // a name, once three letters of it are typed: fewer and it's as likely to be if, i or a short local
        string word = WordBeforeCaret();
        if (word.Length != AutoCompleteAfter || !ScriptParser.IsWordStart(word[0]) || Keyword(word)) return;
        int start = Editor.CaretOffset - word.Length;
        if (start > 0 && Editor.Document.GetCharAt(start - 1) == '.') return; // after a dot, the list opened at the dot
        if (NamingSomething(start)) return;
        _ = ShowCompletionAsync(explicitly: false);
    }

    /// <summary>How many letters of a name open the list by themselves.</summary>
    private const int AutoCompleteAfter = 3;

    private static readonly HashSet<string> CompletionKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return", "new", "delete", "true", "false", "NULL",
        "var", "function", "event", "class", "struct", "enum", "state", "extends", "in", "this", "super", "parent", "virtual_parent",
        "int", "float", "bool", "string", "name", "byte", "array", "out", "optional", "latent", "entry", "timer", "exec", "private", "protected",
        "public", "import", "saved", "editable", "const", "final", "abstract", "statemachine", "autobind", "hint", "default", "defaults",
    };

    private static bool Keyword(string word) => CompletionKeywords.Contains(word);

    /// <summary>The part of a name just before the caret.</summary>
    private string WordBeforeCaret()
    {
        int end = Editor.CaretOffset, start = end;
        while (start > 0 && ScriptParser.IsWordChar(Editor.Document.GetCharAt(start - 1))) start--;
        return Editor.Document.GetText(start, end - start);
    }

    /// <summary>
    /// The name at <paramref name="start"/> is one being made up, not one being used: after var, function, event, class
    /// and the rest, or after the comma of var a, b. Nothing to suggest there.
    /// </summary>
    private bool NamingSomething(int start)
    {
        var line = Editor.Document.GetLineByOffset(start);
        string before = Editor.Document.GetText(line.Offset, start - line.Offset);
        return DeclaringName().IsMatch(before);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:\b(?:var|function|event|class|struct|enum|state|statemachine|autobind)\s+(?:\w+\s*,\s*)*|@\w+\s*\(\s*\w*\s*\)\s*)$")]
    private static partial System.Text.RegularExpressions.Regex DeclaringName();

    /// <summary>
    /// While the list is open, a bracket, dot or other punctuation takes the name picked in it when it starts with what's
    /// typed, then goes in after it, as in other editors. A space just closes the list.
    /// </summary>
    private void OnTextEntering(TextCompositionEventArgs e)
    {
        if (_completion is not { } list || e.Text.Length == 0 || ScriptParser.IsWordChar(e.Text[0])) return;
        if (e.Text[0] is '(' or ')' or '.' or ';' or ',' or '[' or ']' && list.CompletionList.SelectedItem is CompletionItem item
            && Editor.CaretOffset >= list.StartOffset
            && item.Text.StartsWith(Editor.Document.GetText(list.StartOffset, Editor.CaretOffset - list.StartOffset), StringComparison.OrdinalIgnoreCase)
            && Editor.CaretOffset > list.StartOffset)
            list.CompletionList.RequestInsertion(e);
        else list.Close();
    }

    /// <summary>
    /// Opens the completion list at the caret: after a dot, the members of the type before it; elsewhere, locals,
    /// the class's members, globals, types and keywords. Not inside comments and strings.
    /// </summary>
    private async Task ShowCompletionAsync(bool explicitly)
    {
        if (Editor.IsReadOnly || Item.Script is null || _workspace is null) return;
        // under @wrapMethod(Class) or @replaceMethod(Class): the class's functions, each written out whole
        if (await ShowAnnotationCompletionAsync(explicitly)) return;
        if (!_workspace.IsReady)
        {
            if (!explicitly) return;
            await _workspace.Ready;
        }
        if (Finder() is not { } finder) return;
        int caret = Editor.CaretOffset;
        string code = Parsed.Code;
        if (caret > code.Length) return;
        // inside a comment or a string the code is blanked out
        if (caret > 0 && code[caret - 1] == ' ' && Editor.Document.GetCharAt(caret - 1) != ' ') return;

        int start = caret;
        while (start > 0 && ScriptParser.IsWordChar(code[start - 1])) start--;
        int before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
        bool member = before >= 0 && code[before] == '.';
        string prefix = Editor.Document.GetText(start, caret - start);

        var items = new List<CompletionItem>();
        if (member)
        {
            if (finder.MembersAfter(before) is not { } found) return;
            items.AddRange(found.Members.Select(s => new CompletionItem(s)));
            items.AddRange(found.BuiltIn.Select(n => new CompletionItem(n, L.T("PreviewDocument.ArrayMethod"))));
        }
        else
        {
            if (!explicitly && prefix.Length == 0) return;
            var (symbols, words) = finder.NamesAt(caret);
            items.AddRange(symbols.Where(s => prefix.Length == 0 || s.Name.StartsWith(prefix[..1], StringComparison.OrdinalIgnoreCase)).Select(s => new CompletionItem(s)));
            items.AddRange(words.Where(w => prefix.Length == 0 || w.StartsWith(prefix[..1], StringComparison.OrdinalIgnoreCase)).Select(w => new CompletionItem(w, "keyword")));
            // opened by typing: only when something starts with what's there, so a new name of your own gets no list
            if (!explicitly && !items.Any(i => i.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !i.Text.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
                return;
        }
        if (items.Count == 0 || Editor.CaretOffset != caret) return;

        var window = new ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow(Editor.TextArea)
        {
            StartOffset = start,
            EndOffset = caret,
            CloseWhenCaretAtBeginning = !explicitly,
            Width = 460,
        };
        StyleCompletion(window);
        var data = window.CompletionList.CompletionData;
        foreach (var item in items.OrderBy(i => i.Rank).ThenBy(i => i.Text, StringComparer.OrdinalIgnoreCase)) data.Add(item);
        window.Closed += (_, _) =>
        {
            if (_completion == window) _completion = null;
        };
        _completion = window;
        ClosePeek();
        CloseHint();
        window.Show();
        if (prefix.Length > 0) window.CompletionList.SelectItem(prefix);
    }

    /// <summary>
    /// The completion list as a card like the app's menus and the definition popup: rounded, in the theme's colours, with a
    /// shadow, and its declaration beside it in a card too, instead of AvalonEdit's plain window and Windows' blue.
    /// </summary>
    private void StyleCompletion(ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow window)
    {
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.BorderThickness = new Thickness(0);
        // the Fluent theme gives every window its own background when it opens, which fills the shadow's margin
        // as a square behind the card
        window.SourceInitialized += (_, _) => window.Background = Brushes.Transparent;
        window.Loaded += (_, _) => window.Background = Brushes.Transparent;

        var list = window.CompletionList;
        list.ListBox.ItemContainerStyle = (Style)FindResource("CompletionItem");
        list.ListBox.Background = Brushes.Transparent;
        list.ListBox.BorderThickness = new Thickness(0);
        list.ListBox.Padding = new Thickness(0);
        list.ListBox.FontSize = 13;
        window.Content = null;
        var card = new Border
        {
            Child = list,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 14, 14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.3 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        window.Content = card;

        // AvalonEdit keeps the declaration's tooltip to itself
        if (typeof(ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow)
                .GetField("toolTip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(window) is ToolTip tip)
            tip.Style = (Style)FindResource("CompletionTip");
    }

    private void Complete_Click(object sender, RoutedEventArgs e) => _ = ShowCompletionAsync(explicitly: true);

    /// <summary>A name in the completion list, with what it is beside it and its declaration as the tooltip.</summary>
    private sealed class CompletionItem : ICSharpCode.AvalonEdit.CodeCompletion.ICompletionData
    {
        private readonly string _kind;
        private readonly ScriptSymbol? _symbol;

        public CompletionItem(ScriptSymbol symbol)
        {
            _symbol = symbol;
            Text = symbol.Name;
            _kind = symbol.Kind switch
            {
                SymbolKind.Local or SymbolKind.Parameter => symbol.Type is { } t ? $"{t}  ·  {(symbol.Kind == SymbolKind.Local ? "local" : "parameter")}" : "local",
                SymbolKind.Field => symbol.Type ?? "field",
                SymbolKind.Function => symbol.Type is { } r ? $"function : {r}" : "function",
                SymbolKind.Event => "event",
                SymbolKind.EnumMember => symbol.Owner ?? L.T("PreviewDocument.EnumValue"),
                SymbolKind.State => L.T("PreviewDocument.State", symbol.Owner),
                _ => symbol.Kind.ToString().ToLowerInvariant(),
            };
            Rank = symbol.Kind switch
            {
                SymbolKind.Local or SymbolKind.Parameter => 0,
                SymbolKind.Field or SymbolKind.Function or SymbolKind.Event when symbol.Owner is not null => 1,
                _ => 2,
            };
        }

        public CompletionItem(string word, string kind)
        {
            Text = word;
            _kind = kind;
            Rank = 3;
        }

        public string Text { get; }

        /// <summary>Locals first, then the class's members, then globals, then keywords.</summary>
        public int Rank { get; }

        public double Priority => -Rank;

        public ImageSource? Image => null;

        public object Content
        {
            get
            {
                var kind = new Run("   " + _kind) { FontSize = 11.5 };
                kind.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
                return new TextBlock { Inlines = { new Run(Text), kind }, TextTrimming = TextTrimming.CharacterEllipsis };
            }
        }

        public object Description
        {
            get
            {
                var panel = new StackPanel();
                var title = new TextBlock
                {
                    Text = _symbol?.Title ?? Text,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                panel.Children.Add(title);
                var where = new TextBlock
                {
                    Text = _symbol is null ? _kind : $"{_symbol.File.Source}  ·  {_symbol.File.Path} : {_symbol.Line}",
                    FontSize = 11.5,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
                where.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                panel.Children.Add(where);
                return panel;
            }
        }

        public void Complete(ICSharpCode.AvalonEdit.Editing.TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }

    // ---------------------------------------------------------------- menu

    private void FindReferences_Click(object sender, RoutedEventArgs e) => _ = FindReferencesAsync();

    private void Changes_Click(object sender, RoutedEventArgs e) => _ = ShowChangesAsync();

    private void Hierarchy_Click(object sender, RoutedEventArgs e) => _ = ShowHierarchyAsync();

    private void UsageGraph_Click(object sender, RoutedEventArgs e) => _ = ShowUsageGraphAsync();

    /// <summary>Opens the stub generator on the function (or class) under the caret.</summary>
    private void Stub_Click(object sender, RoutedEventArgs e)
    {
        string? query = null;
        if (Finder() is { } finder && finder.WordAt(Editor.CaretOffset) is { } word)
        {
            var found = finder.Find(word.Start, word.Length);
            var function = found.FirstOrDefault(s => s.Kind is SymbolKind.Function or SymbolKind.Event && s.Owner is not null);
            query = function is not null ? $"{function.Owner}.{function.Name}" : Editor.Document.GetText(word.Start, word.Length);
        }
        string? game = _workspace?.GameScriptsFolder is { } scripts ? System.IO.Path.GetFullPath(System.IO.Path.Combine(scripts, "..", "..", "..")) : null;
        StubWindow.Open(Window.GetWindow(this) ?? Application.Current.MainWindow, game ?? GameLocator.Resolve(Item.Script?.DiskPath), query);
    }

    private void Forward_Click(object sender, RoutedEventArgs e) => Host?.GoForward();

    private void Outline_Click(object sender, RoutedEventArgs e) => ToggleOutline();

    private void GoToSymbolInFile_Click(object sender, RoutedEventArgs e) => ShowOutlinePicker();

    private void CompareGame_Click(object sender, RoutedEventArgs e)
    {
        if (GameVersion is { } game) Host?.CompareWithFileAsync(this, game);
    }

    private void CompareOpen_Click(object sender, RoutedEventArgs e) => Host?.PickFileToCompare(this);

    private void NextDiff_Click(object sender, RoutedEventArgs e) => GoToDifference(1);

    private void PreviousDiff_Click(object sender, RoutedEventArgs e) => GoToDifference(-1);

    /// <summary>The search bar over the file, as Ctrl+F opens it, with the selected text to look for.</summary>
    private void FindInFile_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.SelectionLength > 0 && !Editor.SelectedText.Contains('\n')) _search.SearchPattern = Editor.SelectedText;
        _search.Open();
        _search.Reactivate();
    }

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

    /// <summary>A question with three answers, at the top of the text: the main one, the other one, or Cancel.</summary>
    public Task<MessageChoice> Ask(string title, string message, MessageKind kind, string primary, string secondary)
    {
        ClosePeek();
        var result = Card.Ask(title, message, kind, primary, secondary);
        Card.Measure(new Size(Math.Max(0, CardLayer.ActualWidth - 16), double.PositiveInfinity));
        PlaceCard(null);
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
        // a menu takes its font from what it's placed on, and the editor's is the code font
        menu.FontFamily = SystemFonts.MessageFontFamily;
        menu.FontSize = 14;
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
            ? L.T("PreviewDocument.OnlyModsLooseCopy")
            : L.T("PreviewDocument.OpensGamesVersionBeside");
        CompareOpenMenuItem.IsEnabled = Host?.HasOtherFiles(this) == true;
        SnippetMenuItem.IsEnabled = CompleteMenuItem.IsEnabled = RefactorMenuItem.IsEnabled = FormatMenuItem.IsEnabled = !Editor.IsReadOnly;
        NextDiffMenuItem.Visibility = PreviousDiffMenuItem.Visibility = ClearDiffMenuItem.Visibility = HasDiff ? Visibility.Visible : Visibility.Collapsed;
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
