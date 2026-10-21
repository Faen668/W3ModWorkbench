using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Inspector;
using W3ModWorkbench.Highlighting;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A row of the matches list.</summary>
public sealed class MatchItem(MatchRow row)
{
    public MatchRow Row => row;
    public MatchLevel Level => row.Level;
    public string LevelText => row.LevelText;
    public string KindText => row.KindText;
    public string OriginalName => row.Original.Origin;
    public string SuspectName => row.Suspect.Origin;
    public double OriginalPercent => row.OriginalInSuspect * 100;
    public double SuspectPercent => row.SuspectInOriginal * 100;
    public string OriginalText => $"{OriginalPercent:0}%";
    public string SuspectText => $"{SuspectPercent:0}%";
    public string Note => row.SamePath ? L.T("SourceInspectorWindow.SamePath", row.Note) : row.Note;
}

/// <summary>A row of the content breakdown.</summary>
public sealed class CategoryItem(CategoryResult c)
{
    public string Name => c.Name;
    public int OriginalFiles => c.OriginalFiles;
    public int SuspectFiles => c.SuspectFiles;
    public string Detail => c.OriginalFiles == 0 && c.SuspectFiles == 0 ? L.T("SourceInspectorWindow.NeitherSourceHasAny") : c.Detail;
    public double OriginalPercent => (c.OriginalInSuspect ?? 0) * 100;
    public double SuspectPercent => (c.SuspectInOriginal ?? 0) * 100;
    public string OriginalText => c.OriginalInSuspect is { } v ? $"{v * 100:0}%" : L.T("SourceInspectorWindow.N");
    public string SuspectText => c.SuspectInOriginal is { } v ? $"{v * 100:0}%" : L.T("SourceInspectorWindow.N");
}

/// <summary>A row of the coding style list. Its bar is how alike the habit is.</summary>
public sealed class StyleItem(StyleFeature f)
{
    public string Name => f.Name;
    public string Habit => f.Habit;
    public string Original => f.Original;
    public string Suspect => f.Suspect;
    public double OriginalPercent => f.Similarity * 100;
    public string OriginalText => $"{f.Similarity * 100:0}%";
}

/// <summary>
/// Compares two mods (folders of loose files, bundles and compiled scripts) and reports how much of one was taken from
/// the other: scripts by their code and shape, menus by their elements, bundle contents by hash, plus the telltale
/// comments, names and coding habits that survive renaming.
/// </summary>
public partial class SourceInspectorWindow : Window, IMessageHost
{
    private static SourceInspectorWindow? _open;

    private readonly Settings _settings = App.Settings;
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DiffMarker _leftMarks = new();
    private readonly DiffMarker _rightMarks = new();
    private CancellationTokenSource? _cancel;
    private InspectionReport? _report;
    private List<MatchItem> _matches = [];
    private DiffKind[] _leftDiff = [], _rightDiff = [];
    private string[] _leftLines = [], _rightLines = [];
    private string? _gameFolder;
    private int _detailVersion;
    private bool _syncing, _ready, _busy;

    private SourceInspectorWindow()
    {
        InitializeComponent();

        OriginalBox.Text = _settings.InspectorOriginal;
        SuspectBox.Text = _settings.InspectorSuspect;
        IgnoreGameCheck.IsChecked = _settings.InspectorIgnoreGame;

        KindFilter.ItemsSource = new[] { L.T("SourceInspectorWindow.AllKinds"), L.T("SourceInspectorWindow.Scripts"), L.T("SourceInspectorWindow.MenusXML"), L.T("SourceInspectorWindow.TextStrings"), L.T("SourceInspectorWindow.BundleContentsFiles") };
        KindFilter.SelectedIndex = 0;
        LevelFilter.ItemsSource = new[] { L.T("SourceInspectorWindow.EveryMatch"), L.T("SourceInspectorWindow.CopiesAbove"), L.T("SourceInspectorWindow.LargeOverlapsAbove") };
        LevelFilter.SelectedIndex = 0;

        foreach (var (editor, marks) in new[] { (LeftEditor, _leftMarks), (RightEditor, _rightMarks) })
        {
            editor.TextArea.TextView.BackgroundRenderers.Add(marks);
            editor.TextArea.SelectionBorder = null;
        }
        LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Follow(LeftEditor, RightEditor);
        RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Follow(RightEditor, LeftEditor);

        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            ApplyMatchFilter();
        };
        _ready = true;
        UpdateInputs();

        Loaded += async (_, _) =>
        {
            if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
            {
                var selection = new SolidColorBrush(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B));
                selection.Freeze();
                foreach (var editor in new[] { LeftEditor, RightEditor })
                {
                    editor.TextArea.SelectionBrush = selection;
                    editor.TextArea.SelectionForeground = null;
                }
            }
            _gameFolder = await Task.Run(() => GameLocator.Resolve(null));
            UpdateGameNote();
        };
    }

    public static void Open(Window owner)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _open = new SourceInspectorWindow().Standalone(owner);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    // ---------------------------------------------------------------- sources

    private static string Clean(string path) => path.Trim().Trim('"');

    private void UpdateGameNote()
    {
        if (_gameFolder is null)
        {
            IgnoreGameCheck.IsEnabled = false;
            IgnoreGameCheck.IsChecked = false;
            GameNote.Text = L.T("SourceInspectorWindow.GamesFolderWasntFound");
        }
        else
        {
            GameNote.Text = "";
        }
    }

    private void Path_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateInputs();
    }

    private void UpdateInputs()
    {
        OriginalHint.Visibility = OriginalBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SuspectHint.Visibility = SuspectBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool a = Describe(OriginalBox, OriginalInfo);
        bool b = Describe(SuspectBox, SuspectInfo);
        AnalyseButton.IsEnabled = a && b && !_busy;
    }

    private static bool Describe(TextBox box, TextBlock info)
    {
        string path = Clean(box.Text);
        if (path.Length == 0)
        {
            info.Text = " ";
            return false;
        }
        bool found;
        try { found = SourceScanner.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { found = false; }
        info.Text = found ? (Directory.Exists(path) ? L.T("SourceInspectorWindow.FolderFound") : L.T("SourceInspectorWindow.FileFound")) : L.T("SourceInspectorWindow.CantFindPath");
        info.SetResourceReference(TextBlock.ForegroundProperty, found ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");
        return found;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        bool original = (string)button.Tag == "Original";
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var folder = new MenuItem { Header = L.T("SourceInspectorWindow.ChooseFolder") };
        folder.Click += (_, _) => ChooseFolder(original);
        var file = new MenuItem { Header = L.T("SourceInspectorWindow.ChooseBundleCompiledScript") };
        file.Click += (_, _) => ChooseFile(original);
        menu.Items.Add(folder);
        menu.Items.Add(file);
        menu.IsOpen = true;
    }

    private void ChooseFolder(bool original)
    {
        var box = original ? OriginalBox : SuspectBox;
        var dialog = new OpenFolderDialog
        {
            Title = original ? L.T("SourceInspectorWindow.ChooseOriginalModsFolder") : L.T("SourceInspectorWindow.ChooseFolderModCheck"),
            InitialDirectory = StartFolder(box),
        };
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }

    private void ChooseFile(bool original)
    {
        var box = original ? OriginalBox : SuspectBox;
        var dialog = new OpenFileDialog
        {
            Title = original ? L.T("SourceInspectorWindow.ChooseOriginalModsFile") : L.T("SourceInspectorWindow.ChooseFileCheck"),
            Filter = L.T("SourceInspectorWindow.BundlesCompiledScriptsBundle"),
            InitialDirectory = StartFolder(box),
        };
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
    }

    private string StartFolder(TextBox box)
    {
        string current = Clean(box.Text);
        if (Directory.Exists(current)) return current;
        if (File.Exists(current)) return Path.GetDirectoryName(current) ?? "";
        return Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "";
    }

    private void Swap_Click(object sender, RoutedEventArgs e) => (OriginalBox.Text, SuspectBox.Text) = (SuspectBox.Text, OriginalBox.Text);

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        if (files) ((Border)sender).Tag = "drop";
        e.Handled = true;
    }

    private void Card_DragLeave(object sender, DragEventArgs e) => ((Border)sender).Tag = null;

    private void Card_Drop(object sender, DragEventArgs e)
    {
        var card = (Border)sender;
        card.Tag = null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        string path = paths[0];
        // a folder, a bundle or a compiled script as it is; any other file stands for the folder it's in
        if (File.Exists(path) && !Path.GetExtension(path).Equals(".bundle", StringComparison.OrdinalIgnoreCase)
                              && !Path.GetExtension(path).Equals(".rsblob", StringComparison.OrdinalIgnoreCase))
            path = Path.GetDirectoryName(path) ?? path;
        (card == OriginalCard ? OriginalBox : SuspectBox).Text = path;
        e.Handled = true;
    }

    // ---------------------------------------------------------------- analysing

    private async void Analyse_Click(object sender, RoutedEventArgs e)
    {
        string original = Clean(OriginalBox.Text), suspect = Clean(SuspectBox.Text);
        string fullA = Path.GetFullPath(original).TrimEnd('\\'), fullB = Path.GetFullPath(suspect).TrimEnd('\\');
        if (fullA.Equals(fullB, StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessage(L.T("SourceInspectorWindow.TheseSame"), L.T("SourceInspectorWindow.OriginalSuspectSameFolder"), MessageKind.Info);
            return;
        }
        if (fullA.StartsWith(fullB + "\\", StringComparison.OrdinalIgnoreCase) || fullB.StartsWith(fullA + "\\", StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessage(L.T("SourceInspectorWindow.OneFolderInsideOther"), L.T("SourceInspectorWindow.EachModNeedsFolder"), MessageKind.Warning);
            return;
        }

        _settings.InspectorOriginal = original;
        _settings.InspectorSuspect = suspect;
        if (IgnoreGameCheck.IsEnabled) _settings.InspectorIgnoreGame = IgnoreGameCheck.IsChecked == true;
        _settings.Save();

        SetBusy(true);
        var cancel = _cancel = new CancellationTokenSource();
        var progress = new Progress<InspectProgress>(p =>
        {
            if (_cancel != cancel) return;
            PhaseText.Text = p.Phase;
            PhaseDetail.Text = p.Detail;
            Progress.Value = p.Fraction * 100;
        });
        var options = new InspectOptions(IgnoreGameCheck.IsChecked == true, _gameFolder);
        var clock = Stopwatch.StartNew();
        try
        {
            var report = await Task.Run(() => InspectorEngine.Run(original, suspect, options, progress, cancel.Token));
            Show(report);
            StatusText.Text = L.T("SourceInspectorWindow.Compared", clock.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.T("Common.Cancelled");
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            await ShowMessage(L.T("SourceInspectorWindow.ComparisonFailed"), ex.Message, MessageKind.Error);
        }
        finally
        {
            if (_cancel == cancel) _cancel = null;
            cancel.Dispose();
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancel?.Cancel();
        PhaseText.Text = L.T("SourceInspectorWindow.Cancelling");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ProgressCard.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PhaseText.Text = L.T("SourceInspectorWindow.Starting");
        PhaseDetail.Text = "";
        Progress.Value = 0;
        OriginalCard.IsEnabled = SuspectCard.IsEnabled = SwapButton.IsEnabled = !busy;
        IgnoreGameCheck.IsEnabled = !busy && _gameFolder is not null;
        ExportButton.IsEnabled = !busy && _report is not null;
        UpdateInputs();
    }

    // ---------------------------------------------------------------- showing the report

    private void Show(InspectionReport report)
    {
        _report = report;
        WelcomePanel.Visibility = Visibility.Collapsed;
        ResultTabs.Visibility = Visibility.Visible;
        ResultTabs.SelectedIndex = 0;
        ExportButton.IsEnabled = true;

        OriginalInfo.Text = report.Original.Summary;
        SuspectInfo.Text = report.Suspect.Summary;
        OriginalInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        SuspectInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        // summary
        string tone = report.Verdict switch
        {
            VerdictLevel.Extensive or VerdictLevel.Significant => "SystemFillColorCriticalBrush",
            VerdictLevel.Some => "SystemFillColorCautionBrush",
            VerdictLevel.Minor => "SystemFillColorAttentionBrush",
            _ => "SystemFillColorSuccessBrush",
        };
        HeadlineText.Text = $"{report.Headline * 100:0}%";
        VerdictTitle.Text = report.VerdictTitle;
        VerdictBody.Text = report.VerdictText;
        foreach (var element in new Control[] { OriginalGauge, SuspectGauge }) element.SetResourceReference(Control.ForegroundProperty, tone);
        foreach (var text in new TextBlock[] { HeadlineText, VerdictTitle }) text.SetResourceReference(TextBlock.ForegroundProperty, tone);
        OriginalGauge.Value = report.OriginalInSuspect * 100;
        SuspectGauge.Value = report.SuspectInOriginal * 100;
        OriginalGaugeText.Text = $"{report.OriginalInSuspect * 100:0}%";
        SuspectGaugeText.Text = $"{report.SuspectInOriginal * 100:0}%";
        FindingsList.ItemsSource = report.Findings;
        CategoryGrid.ItemsSource = report.Categories.Select(c => new CategoryItem(c)).ToList();
        GameNoteResult.Text = report.GameCodeIgnored
            ? L.T("SourceInspectorWindow.GameSetAside", report.GameScriptsRead) + (report.GameCodeFiles > 0 ? L.T("SourceInspectorWindow.AlongWith", L.T("Count.File", report.GameCodeFiles)) : ".")
            : L.T("SourceInspectorWindow.GamesOwnScriptsWere");

        // matches
        _matches = [.. report.Matches.Select(m => new MatchItem(m))];
        ApplyMatchFilter();

        // evidence
        var kinds = new List<string> { L.T("SourceInspectorWindow.AllKinds") };
        kinds.AddRange(report.Evidence.Select(x => x.Category).Distinct());
        EvidenceFilter.ItemsSource = kinds;
        EvidenceFilter.SelectedIndex = 0;

        // style
        StyleGrid.ItemsSource = report.Style.Select(f => new StyleItem(f)).ToList();
        StyleGrid.Visibility = report.Style.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StyleEmpty.Visibility = report.Style.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        StyleEmpty.Text = report.StyleNote;
        StyleScore.Text = report.StyleSimilarity is { } style ? $"{style * 100:0}%" : L.T("SourceInspectorWindow.N");
        StyleNote.Text = report.StyleNote;

        // sources
        OriginalPathText.Text = report.Original.Path;
        SuspectPathText.Text = report.Suspect.Path;
        OriginalFacts.Text = Facts(report.Original);
        SuspectFacts.Text = Facts(report.Suspect);
        var warnings = report.Original.Warnings.Select(w => L.T("SourceInspectorWindow.Original5", w)).Concat(report.Suspect.Warnings.Select(w => L.T("SourceInspectorWindow.Suspect5", w))).ToList();
        WarningsList.ItemsSource = warnings;
        WarningsCard.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Facts(SourceScan s)
    {
        var text = new StringBuilder();
        text.AppendLine(s.Summary);
        text.AppendLine(L.T("SourceInspectorWindow.Disk", L.T("Count.File", s.Files)));
        if (s.Oldest is { } oldest && s.Newest is { } newest)
            text.AppendLine(L.T("SourceInspectorWindow.FileDates", oldest, newest));
        return text.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- matches

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && _report is not null) ApplyMatchFilter();
    }

    private void MatchSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        MatchSearchHint.Visibility = MatchSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void ApplyMatchFilter()
    {
        if (_report is null) return;
        UnitKind? kind = KindFilter.SelectedIndex switch
        {
            1 => UnitKind.Script,
            2 => UnitKind.Xml,
            3 => UnitKind.Text,
            4 => UnitKind.Asset,
            _ => null,
        };
        var minimum = LevelFilter.SelectedIndex switch
        {
            1 => MatchLevel.NearCopy,
            2 => MatchLevel.Large,
            _ => MatchLevel.Partial,
        };
        string query = MatchSearch.Text.Trim();
        var shown = _matches.Where(m => (kind is null || m.Row.Kind == kind)
                                        && m.Level >= minimum
                                        && (query.Length == 0 || m.OriginalName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                                              || m.SuspectName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        MatchGrid.ItemsSource = shown;
        MatchCount.Text = shown.Count == _matches.Count ? L.T("Count.Match", shown.Count) : L.T("SourceInspectorWindow.Matches", shown.Count, _matches.Count);
        if (shown.Count > 0) MatchGrid.SelectedIndex = 0;
        else ClearDetail(L.T("SourceInspectorWindow.NoFilesMatchTry"));
    }

    private void MatchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatchGrid.SelectedItem is MatchItem item) _ = ShowDetailAsync(item.Row);
        else ClearDetail(L.T("SourceInspectorWindow.SelectMatchCompareTwo"));
    }

    private void ClearDetail(string title)
    {
        _detailVersion++;
        DetailTitle.Text = title;
        LeftHeader.Text = RightHeader.Text = "";
        LeftEditor.Document = new TextDocument();
        RightEditor.Document = new TextDocument();
        _leftMarks.Kinds = _rightMarks.Kinds = null;
        BinaryNote.Visibility = Visibility.Collapsed;
        DiffLegend.Text = "";
    }

    private async Task ShowDetailAsync(MatchRow row)
    {
        int version = ++_detailVersion;
        DetailTitle.Text = $"{row.LevelText} · {row.KindText}";
        LeftHeader.Text = L.T("SourceInspectorWindow.Original4", row.Original.Origin);
        RightHeader.Text = L.T("SourceInspectorWindow.Suspect4", row.Suspect.Origin);

        if (row.Original.Text is null || row.Suspect.Text is null)
        {
            LeftEditor.Document = new TextDocument();
            RightEditor.Document = new TextDocument();
            _leftMarks.Kinds = _rightMarks.Kinds = null;
            DiffLegend.Text = "";
            BinaryNote.Text = L.T("SourceInspectorWindow.TheseFilesNotText", Format.Bytes(row.Original.Size), row.Original.Hash);
            BinaryNote.Visibility = Visibility.Visible;
            return;
        }
        BinaryNote.Visibility = Visibility.Collapsed;

        string left = row.Original.Text, right = row.Suspect.Text;
        var (leftLines, rightLines, leftKinds, rightKinds) = await Task.Run(() =>
        {
            string[] a = left.ReplaceLineEndings("\n").Split('\n');
            string[] b = right.ReplaceLineEndings("\n").Split('\n');
            var (x, y) = LineDiff.Compare(a, b);
            return (a, b, x, y);
        });
        if (version != _detailVersion) return;

        _leftLines = leftLines;
        _rightLines = rightLines;
        _leftDiff = leftKinds;
        _rightDiff = rightKinds;

        bool dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
                    && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;
        Load(LeftEditor, left.ReplaceLineEndings("\n"), row.Kind, row.Original.Key, dark);
        Load(RightEditor, right.ReplaceLineEndings("\n"), row.Kind, row.Suspect.Key, dark);
        UpdateMarks();
    }

    private static void Load(ICSharpCode.AvalonEdit.TextEditor editor, string text, UnitKind kind, string key, bool dark)
    {
        var document = new TextDocument(text);
        editor.Document = document;
        var transformers = editor.TextArea.TextView.LineTransformers;
        for (int i = transformers.Count - 1; i >= 0; i--)
            if (transformers[i] is WitcherScriptColorizer or XmlColorizer) transformers.RemoveAt(i);
        var view = editor.TextArea.TextView;
        if (kind == UnitKind.Script) transformers.Insert(0, new WitcherScriptColorizer(document, dark) { View = view });
        else if (kind == UnitKind.Xml) transformers.Insert(0, new XmlColorizer(document, dark) { View = view });
        editor.ScrollToHome();
    }

    private void HighlightMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateMarks();
    }

    /// <summary>Shared lines: the lines both files have, apart from blank ones and lone brackets. Differences: the usual diff colours.</summary>
    private void UpdateMarks()
    {
        bool shared = HighlightMode.SelectedIndex == 0;
        if (_leftLines.Length == 0 && _rightLines.Length == 0) return;
        if (shared)
        {
            _leftMarks.Kinds = Shared(_leftDiff, _leftLines);
            _rightMarks.Kinds = Shared(_rightDiff, _rightLines);
            DiffLegend.Text = L.T("SourceInspectorWindow.GreenLinesBothFiles");
        }
        else
        {
            _leftMarks.Kinds = _leftDiff;
            _rightMarks.Kinds = _rightDiff;
            DiffLegend.Text = L.T("SourceInspectorWindow.AmberChangedRedOnly");
        }
        LeftEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        RightEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private static DiffKind[] Shared(DiffKind[] diff, string[] lines)
    {
        var kinds = new DiffKind[diff.Length];
        for (int i = 0; i < diff.Length; i++)
        {
            string t = i < lines.Length ? lines[i].Trim() : "";
            bool trivial = t.Length <= 2 || t.All(c => c is '{' or '}' or ';' or '(' or ')' or '<' or '>' or '/' or '*');
            kinds[i] = diff[i] == DiffKind.Same && !trivial ? DiffKind.Added : DiffKind.Same;
        }
        return kinds;
    }

    private void Follow(ICSharpCode.AvalonEdit.TextEditor from, ICSharpCode.AvalonEdit.TextEditor to)
    {
        if (_syncing) return;
        _syncing = true;
        to.ScrollToVerticalOffset(from.VerticalOffset);
        to.ScrollToHorizontalOffset(from.HorizontalOffset);
        _syncing = false;
    }

    // ---------------------------------------------------------------- evidence

    private void EvidenceFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_report is null) return;
        string? category = EvidenceFilter.SelectedIndex > 0 ? EvidenceFilter.SelectedItem as string : null;
        var shown = _report.Evidence.Where(x => category is null || x.Category == category).ToList();
        EvidenceGrid.ItemsSource = shown;
        EvidenceCount.Text = L.T("Count.SharedItem", shown.Count);
        EvidenceEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EvidenceGrid.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CopyEvidence_Click(object sender, RoutedEventArgs e)
    {
        var items = EvidenceGrid.SelectedItems.OfType<EvidenceItem>().Select(x => x.Text).ToList();
        if (items.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, items));
            _ = Message.Show(null, L.T("Common.CopiedClipboard"), MessageKind.Info, autoClose: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    // ---------------------------------------------------------------- report

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ExportButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var html = new MenuItem { Header = L.T("SourceInspectorWindow.WebPageHtml") };
        html.Click += (_, _) => Save(true);
        var md = new MenuItem { Header = L.T("SourceInspectorWindow.MarkdownMd") };
        md.Click += (_, _) => Save(false);
        menu.Items.Add(html);
        menu.Items.Add(md);
        menu.IsOpen = true;
    }

    private async void Save(bool html)
    {
        if (_report is null) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("SourceInspectorWindow.SaveReport2"),
            Filter = html ? L.T("SourceInspectorWindow.WebPageHtmlHtml") : L.T("SourceInspectorWindow.MarkdownMdMd"),
            FileName = $"Source inspector {_report.Original.Name} vs {_report.Suspect.Name}".Replace(' ', '_') + (html ? ".html" : ".md"),
            InitialDirectory = Directory.Exists(_settings.LastOpenFolder) ? _settings.LastOpenFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string text = html ? ReportWriter.Html(_report) : ReportWriter.Markdown(_report);
            FileSafety.CheckWrite(dialog.FileName);
            await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("SourceInspectorWindow.ReportCouldntSaved"), ex.Message, MessageKind.Error);
            return;
        }
        if (await Message.Show(L.T("SourceInspectorWindow.ReportSaved"), dialog.FileName, MessageKind.Info, L.T("Common.Open"), L.T("Common.Close")))
        {
            try { Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
    }

    // ---------------------------------------------------------------- window

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && AnalyseButton.IsEnabled)
            Analyse_Click(sender, e);
        else if (e.Key == Key.Escape && _busy)
            Cancel_Click(sender, e);
        else if (e.Key == Key.Escape && MatchSearch.IsKeyboardFocused && MatchSearch.Text.Length > 0)
            MatchSearch.Clear();
        else if (e.Key == Key.Escape)
            Close();
        else
            return;
        e.Handled = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cancel?.Cancel();

    /// <summary>Tints the lines that are marked: green, red or amber.</summary>
    private sealed class DiffMarker : IBackgroundRenderer
    {
        private static readonly Brush Added = Frozen(Color.FromArgb(0x3C, 0x2E, 0xA0, 0x43));
        private static readonly Brush Removed = Frozen(Color.FromArgb(0x3C, 0xE5, 0x48, 0x4D));
        private static readonly Brush Changed = Frozen(Color.FromArgb(0x34, 0xE0, 0xA0, 0x30));

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
}
