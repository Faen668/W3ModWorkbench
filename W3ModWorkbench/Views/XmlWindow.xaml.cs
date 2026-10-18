using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Menu;
using W3ModWorkbench.Highlighting;

namespace W3ModWorkbench;

/// <summary>
/// Opens an XML file in an editor of its own: coloured, folded and checked as it is typed, with Format and
/// Save, and for a mod menu (user_config_matrix) the menu builder beside the XML it writes. Files from inside
/// an archive open as a copy that Save as writes somewhere else.
/// </summary>
public partial class XmlWindow : Window, IMessageHost
{
    private static readonly Regex Declaration = new(@"^\s*<\?xml[^>]*\?>", RegexOptions.Compiled);
    private static readonly Regex DeclaredEncoding = new("""encoding\s*=\s*["']([^"']+)["']""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int MaxFoldedLength = 3_000_000;

    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _writeTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly string _source;
    private readonly Settings _settings = App.Settings;
    private readonly FoldingManager _folding;
    private readonly XmlFoldingStrategy _foldingStrategy = new() { ShowAttributesWhenFolded = true };
    private string? _path;

    /// <summary>The file's game path, for one of the game's (in a bundle or its folders): where a copy goes in a project.</summary>
    private string? _gamePath;
    private bool _edited;
    private bool _closeConfirmed;
    private bool _syncing;
    private bool _builderHooked;
    private Encoding _encoding = new UTF8Encoding(false);
    private MenuFile? _model;
    private (int Line, int Column)? _errorAt;
    private bool _dark;

    private XmlWindow(string name, string source)
    {
        InitializeComponent();
        _source = source;
        Editor.Options.IndentationSize = 4;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.ConvertTabsToSpaces = false;
        _folding = FoldingManager.Install(Editor.TextArea);
        var search = SearchPanel.Install(Editor.TextArea);

        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            Check();
        };
        _writeTimer.Tick += (_, _) =>
        {
            _writeTimer.Stop();
            WriteModel();
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
            PositionText.Text = $"Line {Editor.TextArea.Caret.Line}, column {Editor.TextArea.Caret.Column}";
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
            W3ModWorkbench.Highlighting.FoldingStyle.Apply(Editor, this);
            ShowOutline(_settings.ShowOutline);
            Editor.TextArea.TextView.LineTransformers.Insert(0, new XmlColorizer(Editor.Document, _dark) { View = Editor.TextArea.TextView });
            Check();
            UpdateHeader();
        };
        FileNameText.Text = name;
    }

    // ---------------------------------------------------------------- opening

    /// <summary>Whether a file is something this window opens.</summary>
    public static bool CanOpen(string path) => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens an XML file on disk.</summary>
    public static void Open(string path, Window owner)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (owner is IMessageHost host) _ = host.ShowMessage($"{Path.GetFileName(path)} could not be opened", ex.Message, MessageKind.Error);
            return;
        }
        var window = new XmlWindow(Path.GetFileName(path), Path.GetDirectoryName(path) ?? "") { _path = path, _gamePath = FileSafety.GamePathOf(path) }.Standalone(owner);
        window.Load(data);
        window.Show();
    }

    /// <summary>
    /// Opens XML from inside an archive: a copy to read and edit. Saving puts it in the project at <paramref name="gamePath"/>,
    /// where the mod uses it in place of the game's, or Save as writes it anywhere of your own.
    /// </summary>
    public static void OpenData(string name, string source, byte[] data, Window owner, string? gamePath = null)
    {
        var window = new XmlWindow(name, source) { _gamePath = gamePath }.Standalone(owner);
        window.Load(data);
        window.Show();
    }

    /// <summary>A new mod menu, with a page and a few settings to start from, in the builder.</summary>
    public static void OpenNewMenu(Window owner)
    {
        var window = new XmlWindow("New mod menu.xml", "Not saved yet").Standalone(owner);
        window.Editor.Text = MenuFile.Starter().ToXml();
        window._edited = true;
        window.Loaded += (_, _) => window.EnterBuilder(askAboutLoss: false);
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
        BuilderToggle.IsEnabled = true;
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

    private void UpdateHeader()
    {
        string name = Path.GetFileName(_path ?? "") is { Length: > 0 } n ? n : FileNameText.Text.TrimStart('•', ' ');
        FileNameText.Text = (_edited ? "• " : "") + name;
        Title = $"{(_edited ? "• " : "")}{name} - XML";
        string encoding = _encoding switch
        {
            UnicodeEncoding => "UTF-16",
            UTF8Encoding { } u when u.GetPreamble().Length > 0 => "UTF-8 with BOM",
            _ => "UTF-8",
        };
        DetailText.Text = $"{_path ?? _source}  ·  {encoding}";
        SaveButton.ToolTip = _path is null ? "Save as a file (Ctrl+S)" : $"Save to {Path.GetFileName(_path)} (Ctrl+S)";
    }

    // ---------------------------------------------------------------- checking

    /// <summary>The outline side bar's elements, read again from the text.</summary>
    private void UpdateOutline()
    {
        if (OutlineHost.Visibility != Visibility.Visible) return;
        Outline.UpdateXml(Editor.Text, new W3ModWorkbench.Core.Scripts.Navigation.ScriptFile
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

    private void Check()
    {
        UpdateOutline();
        string text = Editor.Text;
        _errorAt = null;
        try
        {
            var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            int elements = doc.Descendants().Count();
            StatusText.Text = $"Well-formed  ·  <{doc.Root?.Name.LocalName}>  ·  {Format.Count(elements, "element")}";
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            StatusText.Cursor = Cursors.Arrow;
            if (text.Length < MaxFoldedLength) _foldingStrategy.UpdateFoldings(_folding, Editor.Document);
        }
        catch (XmlException ex)
        {
            _errorAt = (Math.Max(1, ex.LineNumber), Math.Max(1, ex.LinePosition));
            StatusText.Text = $"Line {ex.LineNumber}, column {ex.LinePosition}: {ex.Message}";
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            StatusText.Cursor = Cursors.Hand;
        }
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

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        if (_builderOn)
        {
            _ = ShowMessage("Formatting isn't needed here", "The builder writes the XML already indented. Switch to the code to format it yourself.", MessageKind.Info);
            return;
        }
        string text = Editor.Text;
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (XmlException ex)
        {
            _ = ShowMessage("The XML has to be well-formed first", $"Line {ex.LineNumber}, column {ex.LinePosition}: {ex.Message}", MessageKind.Warning);
            return;
        }
        string declaration = Declaration.Match(text) is { Success: true } m ? m.Value.Trim() : "";
        string formatted = MenuFile.Indent(doc, declaration);
        if (formatted == text) return;
        Editor.Document.Replace(0, Editor.Document.TextLength, formatted);
    }

    // ---------------------------------------------------------------- the menu builder

    private bool _builderOn => Builder.Visibility == Visibility.Visible;

    private void BuilderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (BuilderToggle.IsChecked == true) EnterBuilder(askAboutLoss: true);
        else LeaveBuilder();
    }

    private async void EnterBuilder(bool askAboutLoss)
    {
        MenuFile model;
        try
        {
            model = MenuFile.Parse(Editor.Text);
        }
        catch (XmlException ex)
        {
            BuilderToggle.IsChecked = false;
            await ShowMessage("The XML has to be well-formed first",
                $"Line {ex.LineNumber}, column {ex.LinePosition}: {ex.Message}\n\nFix it in the code, then switch to the builder.", MessageKind.Warning);
            return;
        }
        catch (InvalidDataException ex)
        {
            BuilderToggle.IsChecked = false;
            await ShowMessage("This isn't a mod menu", ex.Message + "\n\nA mod menu starts with <UserConfig>, and lives in bin\\config\\r4game\\user_config_matrix\\pc.", MessageKind.Info);
            return;
        }

        if (askAboutLoss && model.Warnings.Count > 0
            && !await Message.Show("The builder will change the file",
                "Writing the menu from the builder drops what it can't keep: " + string.Join("; ", model.Warnings) + ".\n\nYou can undo it in the code, or Save as a copy first.",
                MessageKind.Question, primary: "Use the builder", secondary: "Stay in the code"))
        {
            BuilderToggle.IsChecked = false;
            return;
        }

        _model = model;
        if (!_builderHooked)
        {
            _builderHooked = true;
            Builder.ModelChanged += () =>
            {
                _writeTimer.Stop();
                _writeTimer.Start();
            };
        }
        BuilderToggle.IsChecked = true;
        Builder.Visibility = Splitter.Visibility = Visibility.Visible;
        BuilderColumn.Width = new GridLength(Math.Max(620, ActualWidth * 0.56));
        SplitterColumn.Width = GridLength.Auto;
        Editor.IsReadOnly = true;
        FormatButton.IsEnabled = false;
        Builder.Load(model, _path, GameLocator.Resolve(_path));
        UpdateHeader();
    }

    private void LeaveBuilder()
    {
        if (_writeTimer.IsEnabled)
        {
            _writeTimer.Stop();
            WriteModel();
        }
        Builder.Visibility = Splitter.Visibility = Visibility.Collapsed;
        BuilderColumn.Width = new GridLength(0);
        SplitterColumn.Width = new GridLength(0);
        Editor.IsReadOnly = false;
        FormatButton.IsEnabled = true;
        _model = null;
    }

    /// <summary>Writes the builder's menu into the XML beside it, keeping the file's own XML declaration.</summary>
    private void WriteModel()
    {
        if (_model is null) return;
        string current = Editor.Text;
        string declaration = Declaration.Match(current) is { Success: true } m ? m.Value.Trim() : "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
        string xml = _model.ToXml(declaration);
        if (xml == current) return;
        _syncing = true;
        double scroll = Editor.VerticalOffset;
        Editor.Document.Replace(0, Editor.Document.TextLength, xml);
        Editor.ScrollToVerticalOffset(scroll);
        _syncing = false;
        MarkEdited();
    }

    // ---------------------------------------------------------------- saving

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(_path);

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(null);

    /// <summary>Writes the file, asking where when <paramref name="path"/> is null.</summary>
    private async Task<bool> SaveAsync(string? path)
    {
        if (_writeTimer.IsEnabled)
        {
            _writeTimer.Stop();
            WriteModel();
        }
        // the game's own file, or a copy from a bundle: it goes into the project, never back where it came from
        string? protection = path is not null ? FileSafety.Protection(path) : _gamePath is not null ? "It came from inside a bundle, which is never changed." : null;
        if (protection is not null && _gamePath is not null)
        {
            var choice = await Message.Ask($"Save {Path.GetFileName(_gamePath)} into your project?",
                $"{protection} Saved into your project at its game path ({_gamePath}), the mod uses it in place of the game's.",
                MessageKind.Question, "Save to project", "Save as…");
            if (choice == MessageChoice.Cancel) return false;
            path = null;
            if (choice == MessageChoice.Primary)
            {
                if (Application.Current.MainWindow is not MainWindow main
                    || await main.EnsureProjectAsync("Game files are changed as copies in a project, so the game's own are never touched.", Message) is not { } project)
                    return false;
                path = project.CopyPathFor(_gamePath);
                if (File.Exists(path) && !await Message.Show($"{project.Name} has {Path.GetFileName(path)} already",
                        $"Replace the project's copy, {path}, with this one?", MessageKind.Question, primary: "Replace", secondary: "Cancel"))
                    return false;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!project.Contains(path) && !project.Contains(project.WorkspacePath)) _ = main.AddToProjectAsync([project.WorkspacePath]);
            }
        }
        else if (protection is not null)
        {
            await ShowMessage($"{Path.GetFileName(path)} is read-only", protection + " Save as puts a copy somewhere of your own.", MessageKind.Info);
            path = null;
        }
        if (path is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save XML",
                Filter = "XML (*.xml)|*.xml|All files (*.*)|*.*",
                FileName = Path.GetFileName(_path) is { Length: > 0 } n ? n : FileNameText.Text.TrimStart('•', ' '),
                InitialDirectory = Directory.Exists(_path is null ? _source : Path.GetDirectoryName(_path)) ? (_path is null ? _source : Path.GetDirectoryName(_path)!) : "",
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        try
        {
            string text = Editor.Text;
            // a file that says it's UTF-16 has to be saved as UTF-16, or the game can't read it
            var declared = DeclaredEncoding.Match(Declaration.Match(text).Value);
            if (declared.Success && declared.Groups[1].Value.StartsWith("utf-16", StringComparison.OrdinalIgnoreCase) && _encoding is not UnicodeEncoding)
                _encoding = new UnicodeEncoding(false, true);
            else if (declared.Success && declared.Groups[1].Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase) && _encoding is UnicodeEncoding)
                _encoding = new UTF8Encoding(false);
            byte[] body = _encoding.GetBytes(text);
            byte[] preamble = _encoding.GetPreamble();
            byte[] data = new byte[preamble.Length + body.Length];
            preamble.CopyTo(data, 0);
            body.CopyTo(data, preamble.Length);
            FileSafety.CheckWrite(path);
            await File.WriteAllBytesAsync(path, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("The file could not be saved", ex.Message, MessageKind.Error);
            return false;
        }
        _path = path;
        _edited = false;
        UpdateHeader();
        if (ProjectPrompts.ShouldOffer(path)) _ = ProjectPrompts.OfferAsync(path, Message, $"Saved {Path.GetFileName(path)}.");
        else _ = Message.Show(null, $"Saved {Path.GetFileName(path)}.", MessageKind.Info, autoClose: true);
        return true;
    }

    // ---------------------------------------------------------------- window

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_edited || _closeConfirmed) return;
        e.Cancel = true;
        var choice = await Message.Ask("Save the changes?", $"{Path.GetFileName(_path ?? FileNameText.Text.TrimStart('•', ' '))} has changes that haven't been saved.",
            MessageKind.Question, primary: "Save", secondary: "Don't save");
        if (choice == MessageChoice.Cancel) return;
        if (choice == MessageChoice.Primary && !await SaveAsync(_path)) return;
        _closeConfirmed = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.S && mods == ModifierKeys.Control) _ = SaveAsync(_path);
        else if (e.Key == Key.S && mods == (ModifierKeys.Control | ModifierKeys.Shift)) _ = SaveAsync(null);
        else if (e.Key == Key.O && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ShowOutline(OutlineHost.Visibility != Visibility.Visible);
            e.Handled = true;
        }
        else if (e.Key == Key.B && mods == ModifierKeys.Control)
        {
            BuilderToggle.IsChecked = !_builderOn;
            BuilderToggle_Click(sender, e);
        }
        else return;
        e.Handled = true;
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");
}
