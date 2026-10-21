using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Highlighting;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Opens a JSON file in an editor of its own: coloured, folded, outlined and checked as it is typed, with Format and
/// Save. A CR2W file turned into JSON by <see cref="Cr2wJson"/> is checked to still convert back as it's typed, and
/// Save as CR2W writes the game file from it; a CR2W file opened as JSON saves back as CR2W. Files from inside an
/// archive open as a copy, which Save puts in the project at its game path.
/// </summary>
public partial class JsonWindow : Window, IMessageHost
{
    private const int MaxFoldedLength = 6_000_000;
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly string _source;
    private readonly Settings _settings = App.Settings;
    private readonly FoldingManager _folding;
    private string? _path;

    /// <summary>The file's game path, for one of the game's (in a bundle or its folders): where a copy goes in a project.</summary>
    private string? _gamePath;

    /// <summary>
    /// For a CR2W file opened as JSON: its name (player_horse.w2ent), and where it is on disk when it's a file there.
    /// Save writes it back as CR2W rather than writing JSON.
    /// </summary>
    private string? _cr2wName;
    private string? _cr2wPath;

    private bool _edited;
    private bool _closeConfirmed;
    private bool _syncing;
    private bool _converted;
    private Encoding _encoding = new UTF8Encoding(false);
    private (int Line, int Column)? _errorAt;
    private CancellationTokenSource? _checking;
    private bool _dark;

    /// <summary>
    /// For a CR2W file embedded in another file's buffer (a cooked effect, a streamed component): the window it came
    /// from and where its base64 is there, tracked as that text changes. Save writes it back into that place.
    /// </summary>
    private JsonWindow? _parent;
    private ICSharpCode.AvalonEdit.Document.TextAnchor? _embedStart, _embedEnd;
    private readonly List<JsonWindow> _children = [];

    /// <summary>The base64 CR2W string the caret is in, when it's in one: where its text starts and ends.</summary>
    private (int Start, int End)? _embeddedAt;

    private JsonWindow(string name, string source)
    {
        InitializeComponent();
        EditorLook.Apply(Editor, tabWidth: false);
        _source = source;
        Editor.Options.IndentationSize = 2;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        _folding = FoldingManager.Install(Editor.TextArea);
        var search = SearchPanel.Install(Editor.TextArea);

        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            Check();
        };
        Outline.Picked += symbol =>
        {
            Editor.Select(symbol.Offset, symbol.Name.Length);
            Editor.TextArea.Caret.Offset = symbol.Offset;
            Editor.ScrollTo(symbol.Line, 1);
            Editor.TextArea.Focus();
        };
        Outline.CloseRequested += () => ShowOutline(false);
        Editor.Document.TextChanged += (_, _) =>
        {
            if (!_syncing) MarkEdited();
            _checkTimer.Stop();
            _checkTimer.Start();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (OutlineHost.Visibility == Visibility.Visible) Outline.Reveal(Editor.CaretOffset);
            PositionText.Text = L.T("JsonWindow.LineColumn", Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);
            _embeddedAt = EmbeddedAt(Editor.CaretOffset);
            EmbeddedButton.IsEnabled = _embeddedAt is not null;
        };

        Loaded += (_, _) =>
        {
            _dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
                    && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;
            if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
            {
                var selection = new SolidColorBrush(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B));
                selection.Freeze();
                Editor.TextArea.SelectionBrush = selection;
                Editor.TextArea.SelectionBorder = null;
                Editor.TextArea.SelectionForeground = null;
                var marker = new SolidColorBrush(Color.FromArgb(0x55, accent.Color.R, accent.Color.G, accent.Color.B));
                marker.Freeze();
                search.MarkerBrush = marker;
            }
            FoldingStyle.Apply(Editor, this);
            ShowOutline(_settings.ShowOutline);
            Editor.TextArea.TextView.LineTransformers.Insert(0, new JsonColorizer(Editor.Document, _dark) { View = Editor.TextArea.TextView });
            Check();
            UpdateHeader();
        };
        Closed += (_, _) =>
        {
            _checking?.Cancel();
            // windows of files embedded in this one have nowhere to save back to now: they save as files of their own
            foreach (var child in _children.ToList()) child.Detach();
            _parent?._children.Remove(this);
        };
        FileNameText.Text = name;
    }

    // ---------------------------------------------------------------- opening

    /// <summary>Whether a file is something this window opens.</summary>
    public static bool CanOpen(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a JSON file on disk.</summary>
    public static void Open(string path, Window owner)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (owner is IMessageHost host) _ = host.ShowMessage(L.T("JsonWindow.CouldNotOpened", Path.GetFileName(path)), ex.Message, MessageKind.Error);
            return;
        }
        var window = new JsonWindow(Path.GetFileName(path), Path.GetDirectoryName(path) ?? "") { _path = path, _gamePath = FileSafety.GamePathOf(path) }.Standalone(owner);
        window.Load(data);
        window.Show();
    }

    /// <summary>Opens JSON from inside an archive: a copy to read and edit, which Save puts in the project at <paramref name="gamePath"/>.</summary>
    public static void OpenData(string name, string source, byte[] data, Window owner, string? gamePath = null)
    {
        var window = new JsonWindow(name, source) { _gamePath = gamePath }.Standalone(owner);
        window.Load(data);
        window.Show();
    }

    /// <summary>
    /// Opens a CR2W file (an .env, a .w2ent…) as its JSON. Save writes it back as CR2W: over the file when
    /// <paramref name="diskPath"/> is one of your own, else into the project at <paramref name="gamePath"/>.
    /// </summary>
    public static void OpenCr2w(string name, string source, byte[] data, Window owner, string? gamePath = null, string? diskPath = null)
    {
        string json;
        string? note;
        try
        {
            json = Cr2wJsonBatch.ToJsonText(data, gamePath, out note);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FormatException or InvalidOperationException)
        {
            if (owner is IMessageHost host) _ = host.ShowMessage(L.T("JsonWindow.CantShownJson", name), ex.Message, MessageKind.Warning);
            return;
        }
        var window = new JsonWindow(name + Cr2wJsonBatch.Suffix, source)
        {
            _gamePath = gamePath ?? (diskPath is null ? null : FileSafety.GamePathOf(diskPath)),
            _cr2wName = name,
            _cr2wPath = diskPath,
        }.Standalone(owner);
        window.Load(Encoding.UTF8.GetBytes(json));
        if (note is not null) window.Loaded += (_, _) => _ = window.Message.Show(null, L.T("JsonWindow.ThoseCantEditedBut", name, note), MessageKind.Info, autoClose: true);
        window.Show();
    }

    private void Load(byte[] data)
    {
        _encoding = EncodingOf(data);
        _syncing = true;
        Editor.Text = TextDecoding.Decode(data);
        _syncing = false;
        Editor.Document.UndoStack.ClearAll();
        _edited = false;
        UpdateHeader();
    }

    private static Encoding EncodingOf(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return new UnicodeEncoding(false, true);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return new UnicodeEncoding(true, true);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return new UTF8Encoding(true);
        return new UTF8Encoding(false);
    }

    private void MarkEdited()
    {
        if (_edited) return;
        _edited = true;
        UpdateHeader();
    }

    private string ShownName => Path.GetFileName(_path ?? "") is { Length: > 0 } n ? n : FileNameText.Text.TrimStart('•', ' ');

    private void UpdateHeader()
    {
        string name = ShownName;
        FileNameText.Text = (_edited ? "• " : "") + name;
        Title = L.T("JsonWindow.Json2", (_edited ? "• " : ""), name);
        string encoding = _encoding switch
        {
            UnicodeEncoding => "UTF-16",
            UTF8Encoding { } u when u.GetPreamble().Length > 0 => L.T("JsonWindow.UTFBOM"),
            _ => "UTF-8",
        };
        string kind = _cr2wName is not null ? L.T("JsonWindow.JsonSavedBackCR2W", _cr2wName) : _converted ? L.T("JsonWindow.ConvertedCR2WFile") : encoding;
        DetailText.Text = _parent is not null ? L.T("JsonWindow.EmbeddedSaveWritesBack", _parent.ShownName) : $"{_path ?? _cr2wPath ?? _source}  ·  {kind}";
        SaveButton.ToolTip = _parent is not null ? L.T("JsonWindow.WriteBackIntoCtrl", _parent.ShownName)
            : _cr2wName is not null
            ? (_cr2wPath is not null && FileSafety.CanEdit(_cr2wPath) ? L.T("JsonWindow.SaveBackCtrl", _cr2wName) : L.T("JsonWindow.SaveIntoYourProject", _cr2wName))
            : _path is null ? L.T("JsonWindow.SaveFileCtrl") : L.T("JsonWindow.SaveCtrl", Path.GetFileName(_path));
        SaveCr2wButton.Visibility = _converted && _cr2wName is null ? Visibility.Visible : Visibility.Collapsed;
        SaveCr2wText.Text = L.T("JsonWindow.Save", Cr2wTargetName());
    }

    /// <summary>The game file a converted JSON file makes: its name without .json.</summary>
    private string Cr2wTargetName() => _cr2wName ?? Cr2wJsonBatch.Cr2wName(ShownName);

    // ---------------------------------------------------------------- checking

    private void UpdateOutline()
    {
        if (OutlineHost.Visibility != Visibility.Visible) return;
        Outline.UpdateJson(Editor.Text, new W3ModWorkbench.Core.Scripts.Navigation.ScriptFile
        {
            Key = _path ?? _source, Path = _path ?? _source, Source = _source, SourceKey = _source, Read = () => [],
        });
        Outline.Reveal(Editor.CaretOffset);
    }

    private void ShowOutline(bool show)
    {
        _settings.ShowOutline = show;
        OutlineToggle.IsChecked = show;
        OutlineHost.Visibility = OutlineSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OutlineSplitterColumn.Width = new GridLength(show ? 6 : 0);
        OutlineColumn.Width = new GridLength(show ? _settings.OutlineWidth : 0);
        OutlineColumn.MinWidth = show ? 160 : 0;
        UpdateOutline();
    }

    private void OutlineToggle_Click(object sender, RoutedEventArgs e) => ShowOutline(OutlineToggle.IsChecked == true);

    private void OutlineSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _settings.OutlineWidth = Math.Round(OutlineColumn.ActualWidth);

    /// <summary>
    /// Checks the text is JSON, folds it and redraws the outline. A converted CR2W file is then built back, in the
    /// background, to say whether it still converts and if not what's wrong.
    /// </summary>
    private void Check()
    {
        UpdateOutline();
        string text = Editor.Text;
        if (text.Length < MaxFoldedLength) UpdateFoldings(text);
        _errorAt = null;
        _checking?.Cancel();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: Lenient);
        }
        catch (JsonException ex)
        {
            int line = (int)(ex.LineNumber ?? 0) + 1, column = (int)(ex.BytePositionInLine ?? 0) + 1;
            _errorAt = (line, column);
            ShowStatus(L.T("JsonWindow.LineColumn2", line, column, Plain(ex.Message)), problem: true, jumps: true);
            return;
        }
        bool converted = root is JsonObject o && (string?)o["$format"] == Cr2wJson.FormatName;
        if (converted != _converted)
        {
            _converted = converted;
            UpdateHeader();
        }
        string summary = L.T("JsonWindow.ValidJson", L.T("Count.Line", Editor.Document.LineCount));
        if (!converted || root is not JsonObject document)
        {
            ShowStatus(summary, problem: false);
            return;
        }
        string classes = string.Join(", ", (document["objects"] as JsonArray ?? []).OfType<JsonObject>().Select(x => (string?)x["$class"]).Where(c => c is not null).Distinct().Take(3));
        ShowStatus(L.T("JsonWindow.CheckingConvertsBack", summary, classes), problem: false);
        var cts = _checking = new CancellationTokenSource();
        Task.Run(() =>
        {
            try { return (Size: Cr2wJson.FromJson(document).Length, Error: (string?)null); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
            {
                return (Size: 0, Error: ex.Message);
            }
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsCanceled || t.IsFaulted) return;
            var (size, error) = t.Result;
            if (error is null) ShowStatus(L.T("JsonWindow.ConvertsBackCR2W", summary, classes, Format.Bytes(size)), problem: false);
            else ShowStatus(L.T("JsonWindow.WontConvertBackCR2W", error), problem: true);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string Plain(string message)
    {
        // System.Text.Json says where in its own terms after the reason; the line and column are given already
        int cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (cut > 0 ? message[..cut] : message).TrimEnd('.', ' ') + ".";
    }

    private void ShowStatus(string text, bool problem, bool jumps = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, problem ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        StatusText.Cursor = jumps ? Cursors.Hand : Cursors.Arrow;
        StatusText.ToolTip = problem ? text : null;
    }

    private void UpdateFoldings(string text)
    {
        var doc = Editor.Document;
        var foldings = new List<NewFolding>();
        void Add(JsonBlock block)
        {
            int end = Math.Min(block.End, doc.TextLength);
            if (end > block.Open + 1 && doc.GetLineByOffset(block.Open).LineNumber != doc.GetLineByOffset(Math.Max(block.Open, end - 1)).LineNumber)
            {
                string inside = block.Label is { } label ? $" {label} " : block.IsList ? $" {block.Count} " : " … ";
                foldings.Add(new NewFolding(block.Open, end) { Name = block.IsList ? $"[{inside}]" : $"{{{inside}}}" });
            }
            foreach (var child in block.Children) Add(child);
        }
        foreach (var block in JsonScanner.Structure(text)) Add(block);
        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        _folding.UpdateFoldings(foldings, -1);
    }

    private void Status_Click(object sender, MouseButtonEventArgs e)
    {
        if (_errorAt is not { } at || at.Line > Editor.Document.LineCount) return;
        var line = Editor.Document.GetLineByNumber(at.Line);
        Editor.CaretOffset = line.Offset + Math.Min(at.Column - 1, line.Length);
        Editor.ScrollToLine(at.Line);
        Editor.Focus();
    }

    // ---------------------------------------------------------------- formatting

    private async void Format_Click(object sender, RoutedEventArgs e) => await FormatAsync();

    private async Task FormatAsync()
    {
        string text = Editor.Text;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: Lenient);
        }
        catch (JsonException ex)
        {
            await ShowMessage(L.T("JsonWindow.JsonHasValidFirst"), L.T("JsonWindow.Line", (ex.LineNumber ?? 0) + 1, Plain(ex.Message)), MessageKind.Warning);
            return;
        }
        if (node is null) return;
        if (JsonScanner.Scan(text).Any(s => s.Kind == JsonSpanKind.Comment)
            && !await Message.Show(L.T("JsonWindow.FormatDropsComments"), L.T("JsonWindow.JsonHasNoComments"),
                MessageKind.Question, primary: L.T("Common.Format"), secondary: L.T("Common.Cancel")))
            return;
        string formatted = Cr2wJson.Format(node);
        if (formatted == text) return;
        Editor.Document.Replace(0, Editor.Document.TextLength, formatted);
    }

    // ---------------------------------------------------------------- saving

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveCurrentAsync();

    /// <summary>What Save does: back into the file this is embedded in, back to the CR2W file, or to the JSON file.</summary>
    private Task<bool> SaveCurrentAsync() =>
        _parent is not null ? SaveEmbeddedAsync() : SaveAsync(_cr2wName is not null ? _cr2wPath : _path, asCr2w: _cr2wName is not null);

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(null, asCr2w: false);

    private async void SaveCr2w_Click(object sender, RoutedEventArgs e)
    {
        // name.env.json on disk: name.env beside it, asked where when that's somewhere that can't be written
        string? beside = _path is not null ? Path.Combine(Path.GetDirectoryName(_path)!, Cr2wTargetName()) : null;
        if (beside is not null && File.Exists(beside) && FileSafety.CanEdit(beside)
            && !await Message.Show(L.T("JsonWindow.Replace", Path.GetFileName(beside)), L.T("JsonWindow.ThereAlreadySaveJson", beside),
                MessageKind.Question, primary: L.T("Common.Replace"), secondary: L.T("JsonWindow.ChooseWhere")))
            beside = null;
        await SaveAsync(beside, asCr2w: true);
    }

    /// <summary>The bytes to write: the text in its encoding, or the CR2W file it builds. Null (and said why) when it doesn't build.</summary>
    private async Task<byte[]?> BytesAsync(bool asCr2w)
    {
        string text = Editor.Text;
        if (!asCr2w)
        {
            byte[] body = _encoding.GetBytes(text), preamble = _encoding.GetPreamble();
            return [.. preamble, .. body];
        }
        try
        {
            return await Task.Run(() => Cr2wJson.FromJsonText(text));
        }
        catch (InvalidDataException ex)
        {
            await ShowMessage(L.T("JsonWindow.CantSavedCR2W"), ex.Message, MessageKind.Warning);
            return null;
        }
    }

    /// <summary>Writes the file, asking where when <paramref name="path"/> is null.</summary>
    private async Task<bool> SaveAsync(string? path, bool asCr2w)
    {
        string targetName = asCr2w ? Cr2wTargetName() : ShownName;
        // the game's own file, or a copy from a bundle: it goes into the project, never back where it came from
        string? protection = path is not null ? FileSafety.Protection(path) : _gamePath is not null && _path is null && _cr2wPath is null ? L.T("JsonWindow.CameInsideBundleWhich") : null;
        string? gamePath = asCr2w ? (_cr2wName is not null ? _gamePath : _gamePath is { } g && g.EndsWith(Cr2wJsonBatch.Suffix, StringComparison.OrdinalIgnoreCase) ? g[..^Cr2wJsonBatch.Suffix.Length] : null)
            : _cr2wName is null ? _gamePath : null;
        if (protection is not null && gamePath is not null)
        {
            var choice = await Message.Ask(L.T("JsonWindow.SaveIntoYourProject2", Path.GetFileName(gamePath)),
                L.T("JsonWindow.SavedIntoYourProject", protection, gamePath),
                MessageKind.Question, L.T("Common.SaveProject"), L.T("Common.Save3"));
            if (choice == MessageChoice.Cancel) return false;
            path = null;
            if (choice == MessageChoice.Primary)
            {
                if (Application.Current.MainWindow is not MainWindow main
                    || await main.EnsureProjectAsync(L.T("JsonWindow.GameFilesChangedCopies"), Message) is not { } project)
                    return false;
                path = project.CopyPathFor(gamePath);
                if (File.Exists(path) && !await Message.Show(L.T("JsonWindow.HasAlready", project.Name, Path.GetFileName(path)),
                        L.T("JsonWindow.ReplaceProjectsCopyOne", path), MessageKind.Question, primary: L.T("Common.Replace"), secondary: L.T("Common.Cancel")))
                    return false;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!project.Contains(path) && !project.Contains(project.WorkspacePath)) _ = main.AddToProjectAsync([project.WorkspacePath]);
            }
        }
        else if (protection is not null)
        {
            await ShowMessage(L.T("JsonWindow.ReadOnly", Path.GetFileName(path ?? targetName)), L.T("JsonWindow.SavePutsCopySomewhere", protection), MessageKind.Info);
            path = null;
        }
        if (path is null)
        {
            string extension = Path.GetExtension(targetName);
            string folder = Path.GetDirectoryName(_path ?? _cr2wPath ?? "") is { Length: > 0 } d ? d : _source;
            var dialog = new SaveFileDialog
            {
                Title = asCr2w ? L.T("JsonWindow.Save2", targetName) : L.T("JsonWindow.SaveJson"),
                Filter = asCr2w && extension.Length > 1 && !extension.Contains(' ') ? L.T("JsonWindow.FileAllFiles", extension.TrimStart('.').ToUpperInvariant(), extension)
                    : asCr2w ? L.T("JsonWindow.CR2WFiles") : L.T("JsonWindow.JsonJsonJsonAll"),
                FileName = targetName,
                InitialDirectory = Directory.Exists(folder) ? folder : "",
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        if (await BytesAsync(asCr2w) is not { } data) return false;
        try
        {
            FileSafety.CheckWrite(path);
            await File.WriteAllBytesAsync(path, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("Common.FileCouldNotSaved"), ex.Message, MessageKind.Error);
            return false;
        }
        // the window now edits what was saved: a CR2W file as JSON, or the JSON file
        if (asCr2w && _cr2wName is not null) _cr2wPath = path;
        else if (!asCr2w)
        {
            _path = path;
            _cr2wName = null;
            _cr2wPath = null;
        }
        if (!asCr2w || _cr2wName is not null) _edited = false;
        UpdateHeader();
        if (ProjectPrompts.ShouldOffer(path)) _ = ProjectPrompts.OfferAsync(path, Message, L.T("JsonWindow.Saved", Path.GetFileName(path)));
        else _ = Message.Show(null, L.T("JsonWindow.Saved", Path.GetFileName(path)), MessageKind.Info, autoClose: true);
        return true;
    }

    // ---------------------------------------------------------------- files embedded in this one

    /// <summary>
    /// The base64 string around an offset, when it holds a CR2W file ("Q1IyV" is "CR2W" in base64): where its text
    /// starts and ends, without the quotes. Base64 has no quotes, so they're looked for on the line alone.
    /// </summary>
    private (int Start, int End)? EmbeddedAt(int offset)
    {
        var doc = Editor.Document;
        var line = doc.GetLineByOffset(offset);
        string text = doc.GetText(line);
        int at = offset - line.Offset;
        int open = text.LastIndexOf('"', Math.Clamp(at - 1, 0, Math.Max(0, text.Length - 1)));
        if (open < 0 || at > text.Length) return null;
        // the caret on the opening quote itself
        if (open == at && at > 0 && !text.AsSpan(at + 1).StartsWith("Q1IyV")) open = text.LastIndexOf('"', at - 1);
        if (open < 0) return null;
        int close = text.IndexOf('"', open + 1);
        if (close < 0 || close + 1 < at || close - open < 160 || !text.AsSpan(open + 1).StartsWith("Q1IyV")) return null;
        return (line.Offset + open + 1, line.Offset + close);
    }

    private async void Embedded_Click(object sender, RoutedEventArgs e) => await OpenEmbeddedAsync();

    /// <summary>Opens the CR2W file in the buffer the caret is on, in a window of its own that saves back here.</summary>
    private async Task OpenEmbeddedAsync()
    {
        if (EmbeddedAt(Editor.CaretOffset) is not { } at) return;
        byte[] data;
        try { data = Convert.FromBase64String(Editor.Document.GetText(at.Start, at.End - at.Start)); }
        catch (FormatException)
        {
            await ShowMessage(L.T("JsonWindow.IsntWholeFile"), L.T("JsonWindow.BuffersTextIsntValid"), MessageKind.Warning);
            return;
        }
        // one window for each buffer: the same one comes forward when it's open already
        if (_children.FirstOrDefault(c => c._embedStart is { IsDeleted: false } s && s.Offset == at.Start) is { } open)
        {
            open.Activate();
            return;
        }
        string label = EmbeddedName(at.Start);
        string json;
        try
        {
            json = await Task.Run(() => Cr2wJsonBatch.ToJsonText(data, null, out _));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FormatException or InvalidOperationException)
        {
            await ShowMessage(L.T("JsonWindow.CantShownJson2", label), ex.Message, MessageKind.Warning);
            return;
        }
        var doc = Editor.Document;
        // the anchors stay around the embedded text as the rest of the document is edited
        var start = doc.CreateAnchor(at.Start);
        var end = doc.CreateAnchor(at.End);
        start.MovementType = ICSharpCode.AvalonEdit.Document.AnchorMovementType.BeforeInsertion;
        end.MovementType = ICSharpCode.AvalonEdit.Document.AnchorMovementType.AfterInsertion;
        var child = new JsonWindow(label, L.T("JsonWindow.Embedded", ShownName))
        {
            _parent = this,
            _cr2wName = label,
            _embedStart = start,
            _embedEnd = end,
        }.Standalone(this);
        child.Load(Encoding.UTF8.GetBytes(json));
        _children.Add(child);
        child.Show();
    }

    /// <summary>
    /// A name for an embedded file: the "name" (or childName) of the object its buffer is in, with the buffer's own key,
    /// such as "fx_steps_grass · buffer". Looked for in the lines just above it.
    /// </summary>
    private string EmbeddedName(int start)
    {
        var doc = Editor.Document;
        var line = doc.GetLineByOffset(start);
        string own = doc.GetText(line);
        string key = System.Text.RegularExpressions.Regex.Match(own, "\"([^\"]+)\"\\s*:").Groups[1].Value;
        string? name = null;
        for (var l = line.PreviousLine; l is not null && name is null && line.LineNumber - l.LineNumber <= 8; l = l.PreviousLine)
        {
            string text = doc.GetText(l);
            if (text.TrimEnd().EndsWith('{') || text.TrimEnd().EndsWith('[')) break;
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"(?:name|childName|effectName)\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success) name = m.Groups[1].Value;
        }
        return name is { Length: > 0 } ? $"{name} · {(key.Length > 0 ? key : "buffer")}" : key.Length > 0 ? key : L.T("JsonWindow.EmbeddedFile");
    }

    /// <summary>Builds the file and puts it back, as base64, where it came from in the parent window's text.</summary>
    private async Task<bool> SaveEmbeddedAsync()
    {
        if (_parent is null || _embedStart is not { IsDeleted: false } start || _embedEnd is not { IsDeleted: false } end || end.Offset < start.Offset)
        {
            await ShowMessage(L.T("JsonWindow.TheresNowherePutBack"), L.T("JsonWindow.BufferCameRemovedFile"), MessageKind.Warning);
            return false;
        }
        if (await BytesAsync(asCr2w: true) is not { } data) return false;
        string base64 = Convert.ToBase64String(data);
        var doc = _parent.Editor.Document;
        if (doc.GetText(start.Offset, end.Offset - start.Offset) != base64) doc.Replace(start.Offset, end.Offset - start.Offset, base64);
        _edited = false;
        UpdateHeader();
        _ = Message.Show(null, L.T("JsonWindow.WrittenBackIntoSave", _parent.ShownName), MessageKind.Info, autoClose: true);
        return true;
    }

    /// <summary>The window this was embedded in closed: from now on this saves as a file of its own.</summary>
    private void Detach()
    {
        _parent = null;
        _embedStart = _embedEnd = null;
        UpdateHeader();
    }

    // ---------------------------------------------------------------- window

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_edited || _closeConfirmed) return;
        e.Cancel = true;
        var choice = await Message.Ask(L.T("Common.SaveChanges"), L.T("JsonWindow.HasChangesHaventBeen", ShownName),
            MessageKind.Question, primary: L.T("Common.Save"), secondary: L.T("Common.DontSave"));
        if (choice == MessageChoice.Cancel) return;
        if (choice == MessageChoice.Primary && !await SaveCurrentAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.S && mods == ModifierKeys.Control) _ = SaveCurrentAsync();
        else if (e.Key == Key.E && mods == ModifierKeys.Control) _ = OpenEmbeddedAsync();
        else if (e.Key == Key.S && mods == (ModifierKeys.Control | ModifierKeys.Shift)) _ = SaveAsync(null, asCr2w: false);
        else if (e.Key == Key.O && mods == (ModifierKeys.Control | ModifierKeys.Shift)) ShowOutline(OutlineHost.Visibility != Visibility.Visible);
        else if (e.Key == Key.F && mods == (ModifierKeys.Control | ModifierKeys.Shift)) _ = FormatAsync();
        else return;
        e.Handled = true;
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));
}
