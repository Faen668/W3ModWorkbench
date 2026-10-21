using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Scripts.Formatting;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// What the script editor shows about the code beyond its colours: the problems a check found, underlined where they
/// are; locals and private fields nobody uses, faded; the names of a call's parameters before its arguments; and the
/// refactors Ctrl+. offers and Format document.
/// </summary>
public partial class PreviewDocument
{
    /// <summary>Any script's text changed: Script Bench checks your project's scripts again once typing stops.</summary>
    public static event Action<PreviewDocument>? AnyTextEdited;

    /// <summary>A script was saved.</summary>
    public static event Action<PreviewDocument>? AnySaved;

    private readonly ProblemMarks _problemMarks = new();
    private readonly UnusedMarks _unusedMarks = new();
    private readonly HintElements _hintElements;
    private readonly DispatcherTimer _insightsTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly ToolTip _problemTip = new() { Placement = PlacementMode.Mouse, MaxWidth = 520 };
    private int _insightsVersion;
    private bool _hintsHeld;

    /// <summary>The problems a check found in this file, by line, with the part of the line each is about.</summary>
    public IReadOnlyList<(int Offset, int Length, int Line, string Text)> Problems => _problemMarks.Problems;

    private void SetUpInsights()
    {
        var view = Editor.TextArea.TextView;
        view.BackgroundRenderers.Add(_problemMarks);
        view.LineTransformers.Add(_unusedMarks);
        view.ElementGenerators.Add(_hintElements);
        _insightsTimer.Tick += (_, _) =>
        {
            _insightsTimer.Stop();
            _ = UpdateInsightsAsync();
        };
        Editor.TextChanged += (_, _) =>
        {
            // what was found belongs to the text as it was: problems follow their lines, the rest is worked out again
            _unusedMarks.Ranges = [];
            _hintElements.Hints = [];
            _insightsTimer.Stop();
            _insightsTimer.Start();
            AnyTextEdited?.Invoke(this);
        };
        view.ScrollOffsetChanged += (_, _) =>
        {
            _insightsTimer.Stop();
            _insightsTimer.Start();
        };
        view.MouseHover += (_, e) => ShowProblemTip(e);
        view.MouseHoverStopped += (_, _) => _problemTip.IsOpen = false;
        view.MouseLeave += (_, _) => _problemTip.IsOpen = false;
        Editor.TextArea.PreviewKeyDown += (_, e) => HoldHints(true);
        Editor.TextArea.PreviewKeyUp += (_, e) => HoldHints(false);
        Editor.TextArea.LostKeyboardFocus += (_, _) => HoldHints(false);
        if (_workspace is not null) _workspace.Ready.ContinueWith(_ => Dispatcher.BeginInvoke(() => _ = UpdateInsightsAsync()), TaskScheduler.Default);
        ApplyInsightColours();
    }

    private void ApplyInsightColours()
    {
        _problemMarks.Pen = new Pen(new SolidColorBrush(_dark ? Color.FromRgb(0xF1, 0x70, 0x70) : Color.FromRgb(0xC4, 0x2B, 0x1C)), 1.2);
        _problemMarks.Pen.Freeze();
        _unusedMarks.Brush = TryFindResource("TextFillColorDisabledBrush") as Brush ?? Brushes.Gray;
        _hintElements.Foreground = TryFindResource("TextFillColorTertiaryBrush") as Brush ?? Brushes.Gray;
        _hintElements.Background = TryFindResource("SubtleFillColorSecondaryBrush") as Brush ?? Brushes.Transparent;
    }

    // ---------------------------------------------------------------- problems

    /// <summary>
    /// The problems a check found in this file (lines from 1), underlined in the text: each under the name its message
    /// quotes when the line has it, else under the line's code.
    /// </summary>
    public void SetProblems(IEnumerable<CompileMessage> problems)
    {
        var document = Editor.Document;
        var list = new List<(int, int, int, string)>();
        foreach (var p in problems)
        {
            if (p.Line < 1 || p.Line > document.LineCount + 1) continue;
            // one at the end of the file, on an empty line, goes on the last line that has code to underline
            var line = document.GetLineByNumber(Math.Min(p.Line, document.LineCount));
            while (line.PreviousLine is not null && document.GetText(line).Trim().Length == 0) line = line.PreviousLine;
            var (start, length) = Core.Scripts.Standalone.ScriptChecker.Span(document.GetText(line), p.Text);
            list.Add((line.Offset + start, Math.Max(1, length), p.Line, p.Text));
        }
        _problemMarks.Set(document, list);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void ShowProblemTip(MouseEventArgs e)
    {
        var view = Editor.TextArea.TextView;
        if (view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset) is not { } position) return;
        int offset = Editor.Document.GetOffset(position.Location);
        var messages = _problemMarks.At(offset).Select(m => m).ToList();
        if (_unusedMarks.Ranges.FirstOrDefault(u => offset >= u.Offset && offset <= u.Offset + u.Length) is { Length: > 0 } unused)
            messages.Add(L.T(unused.Kind switch
            {
                SymbolKind.Parameter => "ScriptBench.Unused.Parameter",
                SymbolKind.Field => "ScriptBench.Unused.Field",
                _ => "ScriptBench.Unused.Local",
            }, Editor.Document.GetText(unused.Offset, unused.Length)));
        if (messages.Count == 0) return;
        ClosePeek();
        _problemTip.Content = new TextBlock { Text = string.Join("\n", messages), TextWrapping = TextWrapping.Wrap };
        _problemTip.PlacementTarget = view;
        _problemTip.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Whether the mouse is over a problem, which shows its message instead of the declaration popup.</summary>
    private bool OverProblem(int offset) => _problemMarks.At(offset).Any();

    /// <summary>Underlines each problem with a wavy line in the error colour.</summary>
    private sealed class ProblemMarks : IBackgroundRenderer
    {
        private readonly List<(TextAnchor Start, TextAnchor End, int Line, string Text)> _marks = [];

        public Pen? Pen { get; set; }

        public KnownLayer Layer => KnownLayer.Selection;

        public IReadOnlyList<(int Offset, int Length, int Line, string Text)> Problems =>
            [.. _marks.Where(m => !m.Start.IsDeleted && !m.End.IsDeleted).Select(m => (m.Start.Offset, m.End.Offset - m.Start.Offset, m.Start.Line, m.Text))];

        public void Set(TextDocument document, List<(int Offset, int Length, int Line, string Text)> problems)
        {
            _marks.Clear();
            foreach (var (offset, length, line, text) in problems)
            {
                // anchored, so a problem follows its code while the next check is waited for
                var start = document.CreateAnchor(Math.Min(offset, document.TextLength));
                var end = document.CreateAnchor(Math.Min(offset + length, document.TextLength));
                start.MovementType = AnchorMovementType.AfterInsertion;
                end.MovementType = AnchorMovementType.BeforeInsertion;
                _marks.Add((start, end, line, text));
            }
        }

        public IEnumerable<string> At(int offset) =>
            _marks.Where(m => !m.Start.IsDeleted && !m.End.IsDeleted && offset >= m.Start.Offset && offset <= m.End.Offset).Select(m => m.Text);

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Pen is null || _marks.Count == 0 || !textView.VisualLinesValid) return;
            foreach (var (start, end, _, _) in _marks)
            {
                if (start.IsDeleted || end.IsDeleted || end.Offset <= start.Offset) continue;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start.Offset, EndOffset = end.Offset }))
                {
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        double y = rect.Bottom - 1.5, x = rect.Left;
                        context.BeginFigure(new Point(x, y), false, false);
                        for (bool up = true; x < rect.Right; up = !up)
                        {
                            x = Math.Min(rect.Right, x + 2.5);
                            context.LineTo(new Point(x, up ? y - 2 : y), true, false);
                        }
                    }
                    geometry.Freeze();
                    drawingContext.DrawGeometry(null, Pen, geometry);
                }
            }
        }
    }

    // ---------------------------------------------------------------- unused names, parameter hints

    /// <summary>Works out what's unused in the file, and the parameter hints for the lines on screen, away from the UI.</summary>
    private async Task UpdateInsightsAsync()
    {
        if (Item.Script is null || !IsVisible || _workspace is not { IsReady: true } || Finder() is not { } finder) return;
        int version = ++_insightsVersion;
        bool dim = App.Settings.DimUnusedNames;
        bool hints = App.Settings.ParameterHints != "Off";
        var view = Editor.TextArea.TextView;
        int from = 0, to = 0;
        if (hints)
        {
            // the lines on screen, and a screen's worth either side for scrolling, by where the view is scrolled to
            var document = Editor.Document;
            double top = view.ScrollOffset.Y, height = Math.Max(view.ActualHeight, 600);
            int first = view.GetDocumentLineByVisualTop(Math.Max(0, top - height))?.LineNumber ?? 1;
            int last = view.GetDocumentLineByVisualTop(top + 2 * height)?.LineNumber ?? document.LineCount;
            from = document.GetLineByNumber(Math.Clamp(first, 1, document.LineCount)).Offset;
            to = document.GetLineByNumber(Math.Clamp(last, 1, document.LineCount)).EndOffset;
        }
        var (unused, found) = await Task.Run(() =>
        {
            try
            {
                return (dim ? finder.UnusedNames() : [], hints && to > from ? finder.ParameterHints(from, to) : []);
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                // the text changed under the work: the next round has it
                return ([], []);
            }
        });
        if (version != _insightsVersion) return;
        _unusedMarks.Ranges = unused;
        _hintElements.Hints = found;
        view.Redraw();
    }

    /// <summary>Ctrl+Alt held shows the parameter hints when they're set to show only then.</summary>
    private void HoldHints(bool down)
    {
        bool held = down && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt);
        if (held == _hintsHeld) return;
        _hintsHeld = held;
        if (App.Settings.ParameterHints == "Held") Editor.TextArea.TextView.Redraw();
    }

    /// <summary>Settings changed what's shown: drawn again.</summary>
    public void ApplyInsightSettings()
    {
        if (Item.Script is null) return;
        if (!App.Settings.DimUnusedNames) _unusedMarks.Ranges = [];
        if (App.Settings.ParameterHints == "Off") _hintElements.Hints = [];
        if (!App.Settings.CheckScriptsAsYouType) SetProblems([]);
        Editor.TextArea.TextView.Redraw();
        _ = UpdateInsightsAsync();
    }

    /// <summary>Unused names drawn in the disabled text colour.</summary>
    private sealed class UnusedMarks : DocumentColorizingTransformer
    {
        public IReadOnlyList<UnusedName> Ranges { get; set; } = [];
        public Brush? Brush { get; set; }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (Brush is null) return;
            foreach (var u in Ranges)
            {
                if (u.Offset < line.Offset || u.Offset + u.Length > line.EndOffset) continue;
                ChangeLinePart(u.Offset, u.Offset + u.Length, e => e.TextRunProperties.SetForegroundBrush(Brush));
            }
        }
    }

    /// <summary>The parameter names put before a call's arguments, as small rounded labels that aren't part of the text.</summary>
    private sealed class HintElements(PreviewDocument owner) : VisualLineElementGenerator
    {
        public IReadOnlyList<ParameterHint> Hints { get; set; } = [];
        public Brush? Foreground { get; set; }
        public Brush? Background { get; set; }

        private bool Showing => App.Settings.ParameterHints == "Always" || App.Settings.ParameterHints == "Held" && owner._hintsHeld;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            if (!Showing) return -1;
            int end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            foreach (var h in Hints)
                if (h.Offset >= startOffset && h.Offset < end) return h.Offset;
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var hint = Hints.FirstOrDefault(h => h.Offset == offset);
            if (hint is null) return null;
            var label = new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, 1, 3, 1),
                Background = Background,
                Child = new TextBlock
                {
                    Text = hint.Name + ":",
                    FontSize = Math.Max(8, CurrentContext.TextView.DefaultLineHeight * 0.62),
                    Foreground = Foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            return new InlineObjectElement(0, label);
        }
    }

    // ---------------------------------------------------------------- @wrapMethod completion

    /// <summary>
    /// The annotation the cursor's line is written under, @wrapMethod(Class) or @replaceMethod(Class), on the line above or
    /// earlier on this one, while only the first word of the function is typed. Null otherwise.
    /// </summary>
    private (StubKind Kind, string Class)? AnnotationAbove()
    {
        var document = Editor.Document;
        int caret = Editor.CaretOffset;
        var line = document.GetLineByOffset(caret);
        string before = document.GetText(line.Offset, caret - line.Offset);
        static (StubKind, string)? From(System.Text.RegularExpressions.Match m) =>
            m.Success ? (m.Groups[1].Value == "wrapMethod" ? StubKind.Wrap : StubKind.Replace, m.Groups[2].Value) : null;
        var same = System.Text.RegularExpressions.Regex.Match(before, @"@(wrapMethod|replaceMethod)\s*\(\s*(\w+)\s*\)\s*\w*$");
        if (same.Success) return From(same);
        if (!System.Text.RegularExpressions.Regex.IsMatch(before, @"^\s*\w*$")) return null;
        for (var previous = line.PreviousLine; previous is not null; previous = previous.PreviousLine)
        {
            string text = document.GetText(previous).Trim();
            if (text.Length == 0) continue;
            return From(System.Text.RegularExpressions.Regex.Match(text, @"^@(wrapMethod|replaceMethod)\s*\(\s*(\w+)\s*\)$"));
        }
        return null;
    }

    /// <summary>
    /// Under @wrapMethod(Class) or @replaceMethod(Class): the class's functions in the completion list, each going in
    /// whole: its signature, and a body that calls the original or is the original's. True when it's that place.
    /// </summary>
    private async Task<bool> ShowAnnotationCompletionAsync(bool explicitly)
    {
        if (AnnotationAbove() is not { } annotation || _workspace?.GameScriptsFolder is not { } scripts) return false;
        int caret = Editor.CaretOffset;
        var generator = await StubGenerator.LoadAsync(scripts);
        if (Editor.CaretOffset != caret || _completion is not null) return true;
        var functions = generator.Functions.Where(f => f.Owner.Equals(annotation.Class, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (functions.Count == 0)
        {
            if (explicitly) _ = ShowCard(null, L.T("ScriptBench.Wrap.NoFunctions", annotation.Class), MessageKind.Info, anchor: caret, autoClose: true);
            return true;
        }
        string prefix = WordBeforeCaret();
        var window = new ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow(Editor.TextArea)
        {
            StartOffset = caret - prefix.Length,
            EndOffset = caret,
            CloseWhenCaretAtBeginning = !explicitly,
            Width = 560,
        };
        StyleCompletion(window);
        foreach (var f in functions) window.CompletionList.CompletionData.Add(new AnnotationItem(f, annotation.Kind, this));
        window.Closed += (_, _) =>
        {
            if (_completion == window) _completion = null;
        };
        _completion = window;
        ClosePeek();
        CloseHint();
        window.Show();
        if (prefix.Length > 0) window.CompletionList.SelectItem(prefix);
        return true;
    }

    /// <summary>One of a class's functions under its annotation: its name and signature, put in as a snippet.</summary>
    private sealed class AnnotationItem(GameFunction function, StubKind kind, PreviewDocument owner) : ICSharpCode.AvalonEdit.CodeCompletion.ICompletionData
    {
        public string Text => function.Name;
        public double Priority => 0;
        public ImageSource? Image => null;

        public object Content
        {
            get
            {
                var detail = new System.Windows.Documents.Run("   " + function.Signature) { FontSize = 11.5 };
                detail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
                return new TextBlock { Inlines = { new System.Windows.Documents.Run(function.Name), detail }, TextTrimming = TextTrimming.CharacterEllipsis };
            }
        }

        public object Description
        {
            get
            {
                var panel = new StackPanel();
                var header = new TextBlock { Text = function.Header, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                panel.Children.Add(header);
                var where = new TextBlock
                {
                    Text = L.T(kind == StubKind.Wrap ? "ScriptBench.Wrap.ItemWrap" : "ScriptBench.Wrap.ItemReplace", function.File, function.Line),
                    FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                };
                where.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                panel.Children.Add(where);
                return panel;
            }
        }

        public void Complete(ICSharpCode.AvalonEdit.Editing.TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            // what's typed of the line is replaced by the whole function, as a snippet, so Tab and the cursor work as in one
            var document = textArea.Document;
            var line = document.GetLineByOffset(completionSegment.Offset);
            int start = line.Offset;
            while (start < completionSegment.Offset && document.GetCharAt(start) is ' ' or '\t') start++;
            int length = completionSegment.EndOffset - start;
            string body = StubGenerator.WriteUnderAnnotation(function, kind).Replace("\r\n", "\n").TrimEnd('\n');
            owner.InsertSnippet(new Snippet(function.Name, function.Title, body), start, length);
        }
    }

    // ---------------------------------------------------------------- refactors and formatting

    /// <summary>Ctrl+.: the refactors there are where the cursor or selection is, in a menu at the cursor.</summary>
    public void ShowRefactors()
    {
        if (Item.Script is null) return;
        if (Editor.IsReadOnly)
        {
            _ = ShowCard(null, L.T("ScriptBench.Refactor.ReadOnly"), MessageKind.Info, autoClose: true);
            return;
        }
        if (Finder() is not { } finder)
        {
            _ = ShowCard(null, L.T("ScriptBench.Refactor.Reading"), MessageKind.Busy, anchor: Editor.CaretOffset, autoClose: true);
            return;
        }
        string text = Editor.Text;
        var found = ScriptRefactors.At(text, Parsed, finder, Editor.SelectionStart, Editor.SelectionLength);
        if (found.Count == 0)
        {
            _ = ShowCard(null, L.T(Editor.SelectionLength > 0 ? "ScriptBench.Refactor.NoneForSelection" : "ScriptBench.Refactor.NoneHere"),
                MessageKind.Info, anchor: Editor.CaretOffset, autoClose: true);
            return;
        }
        var menu = new ContextMenu();
        foreach (var refactor in found)
        {
            var item = new MenuItem { Header = L.T("ScriptBench.Refactor." + refactor.Kind), ToolTip = L.T("ScriptBench.Refactor." + refactor.Kind + "Tip") };
            item.Click += (_, _) => ApplyRefactor(refactor, text);
            menu.Items.Add(item);
        }
        Place(menu, fromMouse: false);
        menu.IsOpen = true;
    }

    /// <summary>Makes a refactor's edits as one change to undo, then renames its new name when it has one.</summary>
    private void ApplyRefactor(Refactor refactor, string expected)
    {
        if (Editor.Text != expected) return;
        var document = Editor.Document;
        using (document.RunUpdate())
            foreach (var edit in refactor.Edits.OrderByDescending(e => e.Offset).ThenByDescending(e => e.Length))
                document.Replace(edit.Offset, edit.Length, edit.Text);
        if (refactor.RenameAt is { } at && at <= document.TextLength)
        {
            Editor.Select(at, 0);
            Editor.TextArea.Caret.Offset = at;
            Editor.TextArea.Caret.BringCaretToView();
            _ = RenameAsync(at);
        }
        Editor.TextArea.Focus();
    }

    /// <summary>Shift+Alt+F: lays the script out as Settings asks, changing only spaces and line breaks.</summary>
    public void FormatDocument() => FormatScript(quiet: false);

    /// <summary>Formats the script as one change to undo, keeping the cursor on its line. False when it couldn't be.</summary>
    private bool FormatScript(bool quiet)
    {
        if (Item.Script is null) return false;
        if (Editor.IsReadOnly)
        {
            if (!quiet) _ = ShowCard(null, L.T("ScriptBench.Format.ReadOnly"), MessageKind.Info, autoClose: true);
            return false;
        }
        var result = ScriptFormatter.Format(Editor.Text, App.Settings.FormatOptions);
        if (result.Refused is not null)
        {
            string why = result.Reason == RefusedReason.WouldChangeCode ? L.T("ScriptBench.Format.WouldChangeCode", result.Line) : L.T("ScriptBench.Format.Unreadable");
            _ = ShowCard(L.T("ScriptBench.Format.Refused"), why, MessageKind.Warning, primary: L.T("Common.OK"));
            return false;
        }
        if (!result.Changed)
        {
            if (!quiet) _ = ShowCard(null, L.T("ScriptBench.Format.AlreadyFormatted"), MessageKind.Info, autoClose: true);
            return true;
        }
        int line = Editor.TextArea.Caret.Line;
        double scroll = Editor.VerticalOffset;
        var document = Editor.Document;
        using (document.RunUpdate()) document.Replace(0, document.TextLength, result.Text);
        int target = Math.Min(line, document.LineCount);
        Editor.TextArea.Caret.Offset = document.GetLineByNumber(target).Offset;
        Editor.ScrollToVerticalOffset(scroll);
        return true;
    }

    private void Refactor_Click(object sender, RoutedEventArgs e) => ShowRefactors();

    private void Format_Click(object sender, RoutedEventArgs e) => FormatDocument();
}
