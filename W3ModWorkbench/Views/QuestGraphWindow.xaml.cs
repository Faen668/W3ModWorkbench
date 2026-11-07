using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Quests;
using W3ModWorkbench.Localisation;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// A quest (.w2quest) or phase (.w2phase) as REDkit's quest editor shows it: its graph of blocks and their links, a
/// phase's own graph opened in place (an embedded one, or an external .w2phase it runs) with the way back over it, and
/// beside it the block picked with its GUID (as Native Quest Hook and REDkit write it), its links and every property.
/// Under the graph every block in the file is listed, to find one by its kind, a fact, a script function, a GUID or
/// any word in it. With no block picked the side says which phase blocks run the file and whether the game reaches it.
/// </summary>
public partial class QuestGraphWindow : Window, IMessageHost
{
    /// <summary>A graph shown: a file's own, or a phase's inside it, and what the way back calls it.</summary>
    private sealed record Crumb(QuestFile File, QuestGraph Graph, string Label);

    private readonly ResourceLocator _locator;
    private readonly string _path;
    private readonly string _source;
    private readonly string? _origin;
    private readonly Func<byte[]> _readData;
    private readonly Guid? _reveal;
    private List<Crumb> _crumbs = [];
    // what's been read, shared with a window opened from this one
    private readonly State _state;
    private QuestBlockItem? _shown;
    private bool _syncing;
    private List<QuestBlockItem> _listed = [];
    private int _match = -1;

    private sealed class State
    {
        public readonly Dictionary<string, QuestFile> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<QuestFile, List<QuestBlockItem>> Items = [];
        public readonly Dictionary<QuestBlock, QuestBlockItem> ItemOf = [];
        public readonly Dictionary<QuestFile, string?> Origins = [];
        public readonly Dictionary<QuestFile, (PropertyOrigin From, Dictionary<Guid, RedObject> ByGuid)> Redkit = [];
        // REDkit's copies being read, so a second window waits for the same read instead of missing it
        public readonly Dictionary<QuestFile, Task> RedkitReads = [];
        public readonly ConcurrentDictionary<string, Dictionary<Guid, string>?> Journal = new(StringComparer.OrdinalIgnoreCase);
        public QuestIndex? Index;
    }

    private QuestGraphWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin, Guid? reveal = null, State? state = null)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        _reveal = reveal;
        _state = state ?? new State();
        MakeModBtn.Show(path, origin, readData);
        Title = $"{Path.GetFileName(path)} - {L.T("QuestGraph.Title")}";
        InfoText.Text = $"{path}   ·   {source}";
        Loaded += async (_, _) => await StartAsync();
    }

    /// <summary>Reads the file, or, for a window opened from another, starts where that one went with what it read.</summary>
    private async Task StartAsync()
    {
        if (_startCrumbs is not { } start)
        {
            await LoadAsync();
            return;
        }
        _crumbs = start;
        _startCrumbs = null;
        Title = $"{Current.Label} - {L.T("QuestGraph.Title")}";
        InfoText.Text = Current.File.Path;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        ShowGraph();
        _ = LoadRedkitAsync(Current.File, Current.File.Path, _state.Origins.GetValueOrDefault(Current.File));
        // the index may still be being built: this window asks for it too, so it hears when it's done
        await LoadIndexAsync();
    }

    public static bool CanShow(string name) =>
        name.EndsWith(".w2phase", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".w2quest", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a quest or phase file's data in a window of its own.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new QuestGraphWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    /// <summary>Opens a quest or phase file on disk.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new QuestGraphWindow(GamePath(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    /// <summary>Opens a game path from wherever the locator finds it, with a block picked.</summary>
    public static void OpenAt(string path, Guid? block, ResourceLocator locator, Window owner)
    {
        var entry = locator.Find(path);
        new QuestGraphWindow(path, entry?.Archive.Name ?? path, () => locator.ReadFile(path) ?? throw new FileNotFoundException(path), locator, entry?.Archive.FilePath, block)
            .Standalone(owner).Show();
    }

    /// <summary>A file on disk's game path, when it's under a game folder's layout (r4data, a mod's content); else its name.</summary>
    private static string GamePath(string file)
    {
        string path = file.Replace('/', '\\');
        foreach (string root in new[] { @"\r4data\", @"\content\", @"\files\Mod\Bundle\", @"\files\DLC\Bundle\", @"\cooked\" })
            if (path.IndexOf(root, StringComparison.OrdinalIgnoreCase) is int at and >= 0) return path[(at + root.Length)..];
        return Path.GetFileName(file);
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    // ---------------------------------------------------------------- reading

    private async Task LoadAsync()
    {
        QuestFile file;
        try
        {
            file = await Task.Run(() => Read(_path, _readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            BusyText.Text = L.T("QuestGraph.CouldNotRead", ex.Message);
            return;
        }
        _state.Origins[file] = _origin;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        _crumbs = [new Crumb(file, file.Graph, Path.GetFileName(_path))];
        ShowGraph();
        InfoText.Text = L.T("QuestGraph.Info", _path, _source, file.Cr2w.Version,
            file.Cooked ? L.T("QuestGraph.Cooked") : L.T("QuestGraph.Uncooked"), L.T("Count.Block", _state.Items[file].Count));
        _ = LoadRedkitAsync(file, _path, _origin);
        _ = LoadIndexAsync();
        if (_reveal is { } guid && _state.Items[file].FirstOrDefault(i => i.Block.Guid == guid) is { } item) Pick(item, reveal: true);
    }

    /// <summary>Reads and describes a quest file, kept for the window. Off the UI thread.</summary>
    private QuestFile Read(string path, byte[] data)
    {
        var file = QuestFile.Read(data, path);
        var items = QuestBlockItem.Describe(file, JournalName);
        lock (_state)
        {
            _state.Files[path] = file;
            _state.Items[file] = items;
            foreach (var item in items) _state.ItemOf[item.Block] = item;
        }
        return file;
    }

    /// <summary>
    /// A journal path's entries by their names ("minorquests › mq4005_sword › Objective"), read from the journal files
    /// it names; null when none can be found.
    /// </summary>
    private string? JournalName(RedObject path, Cr2wFile file)
    {
        var names = new List<string>();
        var at = path;
        for (int depth = 0; depth < 12; depth++)
        {
            if (at["resource"] is RedHandle r && file.PathOf(r) is { } journal && at["guid"] is Guid g
                && _state.Journal.GetOrAdd(journal, JournalEntries) is { } entries && entries.TryGetValue(g, out var name))
                names.Add(name);
            if (at["child"] is RedHandle c && file.Object(c) is { } next) at = next;
            else break;
        }
        return names.Count == 0 ? null : string.Join(" › ", names);
    }

    /// <summary>A journal file's entries' names by their GUIDs; null when it can't be read.</summary>
    private Dictionary<Guid, string>? JournalEntries(string path)
    {
        try
        {
            if (_locator.ReadFile(path) is not { } data) return null;
            var journal = Cr2wFile.Read(data);
            var names = new Dictionary<Guid, string>();
            for (int i = 0; i < journal.Exports.Count; i++)
                if (journal.Object(i) is { } o && o["guid"] is Guid g && (o["baseName"] as string ?? o["name"] as string) is { Length: > 0 } name)
                    names.TryAdd(g, name);
            return names;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>Reads REDkit's copy of a file, whose properties are shown when it has the block (by its GUID).</summary>
    private async Task LoadRedkitAsync(QuestFile file, string path, string? origin)
    {
        if (!_state.RedkitReads.TryGetValue(file, out var read)) _state.RedkitReads[file] = read = ReadRedkitAsync(file, path, origin);
        await read;
        if (_crumbs.Count > 0 && Current.File == file) Refresh();
    }

    private async Task ReadRedkitAsync(QuestFile file, string path, string? origin)
    {
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        var byGuid = new Dictionary<Guid, RedObject>();
        if (from.Redkit is { } redkit)
            await Task.Run(() =>
            {
                try
                {
                    foreach (var block in QuestFile.Read(redkit, path).AllBlocks())
                        if (block.Guid is { } g) byGuid.TryAdd(g, block.Object);
                }
                catch (InvalidDataException) { }
            });
        _state.Redkit[file] = (from, byGuid);
    }

    private async Task LoadIndexAsync()
    {
        _state.Index ??= await QuestIndex.GetAsync(_locator);
        if (_crumbs.Count == 0) return;
        MarkDuplicates();
        FillKinds();
        Filter();
        if (_pendingGuid is { } guid) { _pendingGuid = null; FindGuid(guid); }
        else Refresh();
    }

    // ---------------------------------------------------------------- comparing with another copy

    /// <summary>A comparison of the file shown with another copy of it: the copy, its blocks, and what changed, block by block.</summary>
    private sealed record Comparison(QuestFileCopy Other, QuestFile OtherFile, QuestFile Shown, List<QuestBlockChange> Changes, List<QuestBlockItem> Elsewhere);

    private Comparison? _compare;

    private static readonly Color NewColor = Color.FromRgb(0x3C, 0xB3, 0x71), ChangedColor = Color.FromRgb(0xE0, 0x9A, 0x2B);

    /// <summary>Offers the file's other copies (the game's, each mod's) to compare it with.</summary>
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Index is not { } index)
        {
            _ = ShowMessage(L.T("QuestGraph.Compare"), L.T("QuestGraph.FindingCopies"), MessageKind.Info);
            return;
        }
        var file = Current.File;
        string? origin = _state.Origins.GetValueOrDefault(file);
        var others = index.CopiesOf(file.Path).Where(c => !string.Equals(c.Origin, origin, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count == 0)
        {
            _ = ShowMessage(L.T("QuestGraph.Compare"), L.T("QuestGraph.NoOtherCopy", Path.GetFileName(file.Path)), MessageKind.Info);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = CompareButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var copy in others)
        {
            var item = new MenuItem { Header = L.T("QuestGraph.CompareWith", copy.Source) + (copy.Effective ? "  " + L.T("QuestGraph.TheGameReadsIt") : "") };
            item.Click += (_, _) => _ = CompareWithAsync(copy);
            menu.Items.Add(item);
        }
        if (_compare is not null)
        {
            menu.Items.Add(new Separator());
            var stop = new MenuItem { Header = L.T("QuestGraph.StopCompare") };
            stop.Click += StopCompare_Click;
            menu.Items.Add(stop);
        }
        menu.IsOpen = true;
    }

    /// <summary>Compares the file shown with another copy of it, by GUID, and marks what's new here and what changed.</summary>
    private async Task CompareWithAsync(QuestFileCopy copy)
    {
        var shown = Current.File;
        try
        {
            _compare = await Task.Run(() =>
            {
                var other = QuestFile.Read(copy.Read() ?? throw new FileNotFoundException(copy.File), copy.File);
                var changes = QuestCompare.Compare(other, shown);
                var elsewhere = changes.Where(c => c.Change.HasFlag(QuestChange.Removed)).Select(c =>
                {
                    var described = QuestBlockItem.Describe(c.Before!, other.Cr2w);
                    var item = new QuestBlockItem
                    {
                        Block = described.Block, Title = described.Title, Detail = described.Detail, Kind = described.Kind, Category = described.Category,
                        Phase = described.Phase, Inputs = described.Inputs, Outputs = described.Outputs, SearchText = described.SearchText, Elsewhere = true,
                    };
                    item.ChangeKind = QuestChange.Removed;
                    item.Change = L.T("QuestGraph.Change.OnlyThere", copy.Source);
                    return item;
                }).ToList();
                return new Comparison(copy, other, shown, changes, elsewhere);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            await ShowMessage(L.T("QuestGraph.Compare"), L.T("QuestGraph.PhaseUnreadableText", copy.File, ex.Message), MessageKind.Warning);
            return;
        }
        ApplyCompare();
        FillKinds();
        Filter();
        Refresh();
    }

    /// <summary>Puts the comparison on the file shown: each block's change in words, the marks on the graph, the counts.</summary>
    private void ApplyCompare()
    {
        var file = Current.File;
        bool on = _compare is { } c && c.Shown == file;
        foreach (var item in _state.Items[file])
        {
            item.Change = "";
            item.ChangeKind = QuestChange.None;
        }
        ChangeColumn.Visibility = CompareBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            Graph.Mark(new Dictionary<QuestBlock, (Color, string)>());
            return;
        }
        var compare = _compare!;
        var byBlock = compare.Changes.Where(ch => ch.After is not null).ToDictionary(ch => ch.After!);
        var marks = new Dictionary<QuestBlock, (Color, string)>();
        foreach (var item in _state.Items[file])
        {
            if (!byBlock.TryGetValue(item.Block, out var change) || change.Change == QuestChange.None) continue;
            item.ChangeKind = change.Change;
            item.Change = Describe(change, compare.Other.Source);
            marks[item.Block] = change.Change.HasFlag(QuestChange.Added)
                ? (NewColor, L.T("QuestGraph.Mark.New"))
                : (ChangedColor, L.T("QuestGraph.Mark.Changed"));
        }
        Graph.Mark(marks);
        int Count(QuestChange flag) => compare.Changes.Count(ch => ch.Change.HasFlag(flag));
        CompareText.Text = L.T("QuestGraph.CompareSummary", compare.Other.Source, Count(QuestChange.Added), Count(QuestChange.Removed),
            Count(QuestChange.Relinked), Count(QuestChange.Moved), Count(QuestChange.Changed));
    }

    /// <summary>What happened to a block, in words: new here, relinked, moved, the properties changed.</summary>
    private static string Describe(QuestBlockChange change, string other)
    {
        if (change.Change.HasFlag(QuestChange.Added)) return L.T("QuestGraph.Change.New");
        if (change.Change.HasFlag(QuestChange.Removed)) return L.T("QuestGraph.Change.OnlyThere", other);
        var parts = new List<string>();
        if (change.Change.HasFlag(QuestChange.Relinked)) parts.Add(L.T("QuestGraph.Change.Relinked"));
        if (change.Change.HasFlag(QuestChange.Moved)) parts.Add(L.T("QuestGraph.Change.Moved"));
        if (change.Change.HasFlag(QuestChange.Changed)) parts.Add(L.T("QuestGraph.Change.Properties", string.Join(", ", change.Properties)));
        return string.Join("; ", parts);
    }

    private void StopCompare_Click(object sender, RoutedEventArgs e)
    {
        _compare = null;
        ApplyCompare();
        FillKinds();
        Filter();
        Refresh();
    }

    private void ShowChanges_Click(object sender, RoutedEventArgs e)
    {
        if (KindBox.ItemsSource is List<KindChoice> kinds && kinds.FirstOrDefault(k => k.Changes) is { } changes) KindBox.SelectedItem = changes;
    }

    /// <summary>The comparison's lines for a block beside the graph: what changed, and how its links went in the other copy.</summary>
    private void ShowCompare(QuestBlockItem item)
    {
        CompareInfo.Inlines.Clear();
        if (_compare is not { } compare || compare.Shown != (item.Elsewhere ? compare.Shown : FileOf(item)) || item.ChangeKind == QuestChange.None)
        {
            CompareTitle.Visibility = CompareInfo.Visibility = Visibility.Collapsed;
            return;
        }
        CompareTitle.Text = L.T("QuestGraph.ComparedWith", compare.Other.Source);
        CompareInfo.Inlines.Add(new Run(item.Change));
        var change = compare.Changes.FirstOrDefault(c => c.Guid == item.Block.Guid);
        if (change?.Before is { } before && change.Change.HasFlag(QuestChange.Relinked))
        {
            CompareInfo.Inlines.Add(new LineBreak());
            CompareInfo.Inlines.Add(new Run(L.T("QuestGraph.LinksThere", compare.Other.Source)) { FontWeight = FontWeights.SemiBold });
            foreach (var link in before.Links)
            {
                CompareInfo.Inlines.Add(new LineBreak());
                CompareInfo.Inlines.Add(new Run($"{QuestBlockItem.Socket(link.Output)} → {link.To.Name}  ({QuestBlockItem.Socket(link.Input)})"));
            }
            if (before.Links.Count == 0)
            {
                CompareInfo.Inlines.Add(new LineBreak());
                CompareInfo.Inlines.Add(new Run(L.T("QuestGraph.NoLinks")) { FontStyle = FontStyles.Italic });
            }
        }
        if (item.Elsewhere)
        {
            CompareInfo.Inlines.Add(new LineBreak());
            var open = new Hyperlink(new Run(L.T("QuestGraph.OpenThere", compare.Other.Source)));
            open.Click += (_, _) => OpenOther(item);
            CompareInfo.Inlines.Add(open);
        }
        CompareTitle.Visibility = CompareInfo.Visibility = Visibility.Visible;
    }

    /// <summary>Opens the copy compared with at a block only it has.</summary>
    private void OpenOther(QuestBlockItem item)
    {
        if (_compare is not { } compare || item.Block.Guid is not { } guid) return;
        new QuestGraphWindow(compare.Other.File, compare.Other.Source, () => compare.Other.Read() ?? throw new FileNotFoundException(compare.Other.File),
            _locator, compare.Other.Origin, guid).Standalone(this).Show();
    }

    // ---------------------------------------------------------------- GUIDs

    // a GUID asked for before the index was ready
    private Guid? _pendingGuid;

    /// <summary>
    /// Marks the blocks of the file shown whose GUID another block has too: in the same file, or in another file the
    /// game reaches (the game's copy of a file and a mod's copy of it aren't two). Says how many over the graph.
    /// </summary>
    private void MarkDuplicates()
    {
        var file = Current.File;
        var items = _state.Items[file];
        var inFile = items.Where(i => i.Block.Guid is not null).GroupBy(i => i.Block.Guid!.Value).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        foreach (var item in items)
            item.Duplicate = item.Block.Guid is { } g && (inFile.Contains(item) || _state.Index?.ReachedElsewhere(g, file.Path).Count > 0);
        int count = items.Count(i => i.Duplicate);
        DuplicateText.Text = L.T("QuestGraph.DuplicateBanner", count);
        DuplicateBanner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (KindBox.ItemsSource is List<KindChoice> kinds && kinds.FirstOrDefault(k => k.Duplicates) is { } duplicates) KindBox.SelectedItem = duplicates;
    }

    private void FindGuidBox_TextChanged(object sender, TextChangedEventArgs e) =>
        FindGuidPlaceholder.Visibility = FindGuidBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void FindGuidBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (QuestBlockItem.ParseGuid(FindGuidBox.Text) is not { } guid)
        {
            _ = ShowMessage(L.T("QuestGraph.FindGuid"), L.T("QuestGraph.NotAGuid"), MessageKind.Warning);
            return;
        }
        FindGuid(guid);
    }

    /// <summary>
    /// Finds a GUID: picks its block when it's in a file this window has read, and beside lists every copy of it in the
    /// game, its DLC, the installed mods and the project.
    /// </summary>
    private void FindGuid(Guid guid)
    {
        QuestBlockItem? here;
        lock (_state) here = _state.ItemOf.Values.FirstOrDefault(i => i.Block.Guid == guid && _crumbs.Any(c => c.File == FileOf(i)));
        if (here is not null)
        {
            Pick(here, reveal: true);
            return;
        }
        _shown = null;
        Graph.Select([]);
        SelectRows([]);
        BlockTitle.Text = L.T("QuestGraph.FindGuid");
        BlockDetail.Text = "";
        BlockComment.Visibility = NoGuidText.Visibility = OpenPhaseButton.Visibility = Visibility.Collapsed;
        GuidText.Text = guid.ToString();
        RedkitGuidText.Text = QuestBlockItem.RedkitForm(guid);
        GuidPanel.Visibility = Visibility.Visible;
        LinksTitle.Text = L.T("QuestGraph.WhereGuidIs");
        LinksText.Inlines.Clear();
        CopiesTitle.Visibility = CopiesText.Visibility = Visibility.Collapsed;
        if (_state.Index is null)
        {
            _pendingGuid = guid;
            LinksText.Inlines.Add(new Run(L.T("QuestGraph.FindingCopies")) { FontStyle = FontStyles.Italic });
        }
        else
        {
            var copies = _state.Index.Find(guid);
            BlockDetail.Text = copies.Count == 0 ? "" : L.T("Count.Copy", copies.Count);
            if (copies.Count == 0) LinksText.Inlines.Add(new Run(L.T("QuestGraph.GuidNotFound")) { FontStyle = FontStyles.Italic });
            else WriteCopies(LinksText, copies, guid, null);
        }
        // what's found is no block of the file shown, so the file's properties and where they come from aren't shown
        PropertiesHeader.Visibility = Properties.Visibility = Visibility.Collapsed;
    }

    private QuestFile FileOf(QuestBlockItem item) => _state.Items.First(f => f.Value.Contains(item)).Key;

    /// <summary>Lists copies of a GUID as links that open each; the copy of <paramref name="file"/> shown here is said to be this one.</summary>
    private void WriteCopies(TextBlock text, List<QuestGuidCopy> copies, Guid guid, QuestFile? file)
    {
        var faint = (Brush)FindResource("TextFillColorTertiaryBrush");
        foreach (var copy in copies)
        {
            if (text.Inlines.Count > 0) text.Inlines.Add(new LineBreak());
            text.Inlines.Add(new Run(copy.Source + "  ") { FontWeight = FontWeights.SemiBold });
            bool self = file is not null && string.Equals(copy.File, file.Path, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(copy.Origin, _state.Origins.GetValueOrDefault(file), StringComparison.OrdinalIgnoreCase);
            var link = new Hyperlink(new Run($"{Path.GetFileName(copy.File)} › {copy.BlockName}")) { ToolTip = copy.File };
            link.Click += (_, _) => OpenCopy(copy, guid);
            text.Inlines.Add(link);
            var notes = new List<string>();
            if (self) notes.Add(L.T("QuestGraph.ThisCopy"));
            if (!copy.Effective && copy.Kind != SourceKind.Project) notes.Add(L.T("QuestGraph.Replaced"));
            else if (!copy.Reachable) notes.Add(L.T("QuestGraph.NotReachedShort").TrimEnd('.'));
            if (notes.Count > 0) text.Inlines.Add(new Run($"  ({string.Join(", ", notes)})") { Foreground = faint });
        }
    }

    /// <summary>Opens a copy of a quest file with the GUID's block picked: here when it's the file shown, else in a window of its own.</summary>
    private void OpenCopy(QuestGuidCopy copy, Guid guid)
    {
        QuestBlockItem? here;
        lock (_state)
            here = _state.ItemOf.Values.FirstOrDefault(i => i.Block.Guid == guid && string.Equals(FileOf(i).Path, copy.File, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_state.Origins.GetValueOrDefault(FileOf(i)), copy.Origin, StringComparison.OrdinalIgnoreCase));
        if (here is not null && _crumbs.Any(c => c.File == FileOf(here)))
        {
            Pick(here, reveal: true);
            return;
        }
        new QuestGraphWindow(copy.File, copy.Source, () => copy.Read() ?? throw new FileNotFoundException(copy.File), _locator, copy.Origin, guid)
            .Standalone(this).Show();
    }

    /// <summary>The blocks picked to copy: the list's picked rows, else the graph's picked blocks.</summary>
    private List<QuestBlockItem> Picked()
    {
        var rows = BlockList.SelectedItems.OfType<QuestBlockItem>().ToList();
        if (rows.Count == 0) rows = [.. Graph.Selected.Where(_state.ItemOf.ContainsKey).Select(b => _state.ItemOf[b])];
        return [.. rows.Where(r => r.Block.Guid is not null && !r.Elsewhere)];
    }

    private void CopyGuids_Click(object sender, RoutedEventArgs e) => CopyPicked(redkit: false);
    private void CopyRedkitGuids_Click(object sender, RoutedEventArgs e) => CopyPicked(redkit: true);

    /// <summary>Copies the picked blocks' GUIDs, one a line, as Native Quest Hook takes them or as REDkit shows them.</summary>
    private void CopyPicked(bool redkit)
    {
        var picked = Picked();
        if (picked.Count == 0)
        {
            InfoText.Text = L.T("QuestGraph.NothingToCopy");
            return;
        }
        string text = string.Join(Environment.NewLine, picked.Select(p => redkit ? QuestBlockItem.RedkitForm(p.Block.Guid!.Value) : p.Block.Guid!.Value.ToString()));
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        InfoText.Text = L.T("QuestGraph.CopiedGuids", picked.Count);
    }

    /// <summary>Opens the Native Quest Hook listener maker for the blocks picked.</summary>
    private void MakeListener_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked.Count == 0)
        {
            _ = ShowMessage(L.T("QuestGraph.MakeListener"), L.T("QuestGraph.PickBlocksFirst"), MessageKind.Info);
            return;
        }
        QuestStubWindow.Open([.. picked.Select(p => new NqhWatchedBlock(p.Block.Guid!.Value, p.ClassName, p.Title, p.Outputs, FileOf(p).Path))], this);
    }

    private void BlockList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        CopyPicked(redkit: false);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- the graph shown

    private Crumb Current => _crumbs[^1];

    /// <summary>Draws the current graph, the way back to the file's own over it, and the list for its file.</summary>
    private void ShowGraph()
    {
        var crumb = Current;
        var items = crumb.Graph.Blocks.Select(b => _state.ItemOf[b]).ToList();
        var placement = Places(crumb, items, out bool downwards);
        Graph.Downwards = downwards;
        Graph.Show(items, placement);
        EmptyGraph.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Legend.ItemsSource = items.Select(i => i.Category).Distinct().OrderBy(c => c).Select(c =>
        {
            var brush = new SolidColorBrush(QuestGraphView.ColorOf(c));
            brush.Freeze();
            return new { Name = QuestBlockItem.CategoryName(c), Brush = brush };
        }).ToList();
        ShowCrumbs();
        ApplyCompare();
        MarkDuplicates();
        FillKinds();
        Filter();
        _shown = null;
        ShowFile();
    }

    /// <summary>Where each block goes: REDkit's places, when the file keeps them (uncooked) and Settings say to use them; else laid out.</summary>
    private QuestPlacement Places(Crumb crumb, List<QuestBlockItem> items, out bool downwards)
    {
        downwards = false;
        if (!crumb.File.Cooked && App.Settings.QuestGraphRedkitPlaces && items.Count > 0 && items.All(i => i.Block.Position is not null))
            // our boxes are a little wider than REDkit's, so its places are spread out to keep them apart
            return new QuestPlacement(items.ToDictionary(i => i.Block, i => (i.Block.Position!.Value.X * RedkitSpread, i.Block.Position!.Value.Y * 1.1)),
                new Dictionary<(QuestBlock, QuestBlock), List<(double X, double Y)>>());
        var sizes = items.ToDictionary(i => i.Block, i => Graph.BoxSize(i));
        downwards = App.Settings.QuestGraphDownwards;
        return QuestLayout.Place([.. items.Select(i => i.Block)], b => (sizes[b].Width, sizes[b].Height), downwards);
    }

    private const double RedkitSpread = 1.35;

    private void ShowCrumbs()
    {
        var parts = new List<UIElement>();
        for (int i = 0; i < _crumbs.Count; i++)
        {
            if (i > 0) parts.Add(new TextBlock { Text = "›", Opacity = 0.5, Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            var crumb = _crumbs[i];
            int at = i;
            bool file = crumb.Graph == crumb.File.Graph;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (file) content.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = crumb.Label, FontWeight = at == _crumbs.Count - 1 ? FontWeights.SemiBold : FontWeights.Normal });
            var button = new Button { Content = content, Style = (Style)FindResource("Crumb"), ToolTip = file ? crumb.File.Path : L.T("QuestGraph.EmbeddedPhaseTip") };
            button.Click += (_, _) => GoTo(at);
            parts.Add(button);
        }
        Crumbs.ItemsSource = parts;
        UpButton.IsEnabled = _crumbs.Count > 1;
    }

    private void GoTo(int index)
    {
        if (index >= _crumbs.Count - 1) return;
        var left = _crumbs[index + 1];
        _crumbs = _crumbs.Take(index + 1).ToList();
        ShowGraph();
        // the phase just left picked, so it's easy to see where it was
        if (left.Graph.Owner is { } owner && _state.ItemOf.TryGetValue(owner, out var item) && Graph.Contains(owner)) Pick(item, reveal: true);
        else if (FindRunner(left) is { } runner) Pick(runner, reveal: true);
    }

    /// <summary>The phase block in the graph shown that runs an external phase left (the one opened from it).</summary>
    private QuestBlockItem? FindRunner(Crumb left) =>
        Current.Graph.Blocks.FirstOrDefault(b => string.Equals(b.ExternalPhase, left.File.Path, StringComparison.OrdinalIgnoreCase)) is { } b ? _state.ItemOf[b] : null;

    private void Up_Click(object sender, RoutedEventArgs e) => GoTo(_crumbs.Count - 2);

    private void LiveLog_Click(object sender, RoutedEventArgs e) => QuestLogWindow.Open(this, _locator);

    private void Fit_Click(object sender, RoutedEventArgs e) => Graph.FitToView();

    private void Json_Click(object sender, RoutedEventArgs e)
    {
        var file = Current.File;
        bool root = file == _crumbs[0].File;
        JsonWindow.OpenCr2w(Path.GetFileName(file.Path), root ? _source : file.Path, file.Cr2w.Data, this, file.Path, root && _origin is { } o && File.Exists(o) && !o.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ? o : null);
    }

    private void Graph_PhaseOpened(object? sender, QuestBlock block) => _ = OpenPhaseAsync(block);

    private void OpenPhase_Click(object sender, RoutedEventArgs e)
    {
        if (_shown?.Block is { ClassName: "CQuestPhaseBlock" } block) _ = OpenPhaseAsync(block);
    }

    /// <summary>Opens the graph a phase block runs: its embedded one, or the external .w2phase, in place or in a new window as Settings say.</summary>
    private async Task OpenPhaseAsync(QuestBlock block)
    {
        if (await PhaseOf(block) is not { } next) return;
        if (App.Settings.QuestGraphPhasesInNewWindow)
        {
            PhaseWindow(next).Standalone(this).Show();
            return;
        }
        _crumbs.Add(next);
        ShowGraph();
    }

    /// <summary>A window of its own for a phase, sharing what this one has read, with the way back to here over its graph. Not shown yet.</summary>
    private QuestGraphWindow PhaseWindow(Crumb next) =>
        new(_path, _source, _readData, _locator, _origin, state: _state) { _startCrumbs = [.. _crumbs, next] };

    /// <summary>The graph a phase block runs, read when it's another file; null (having said why) when it can't be.</summary>
    private async Task<Crumb?> PhaseOf(QuestBlock block)
    {
        Crumb next;
        if (block.Embedded is { } inner) next = new Crumb(Current.File, inner, block.Name);
        else if (block.ExternalPhase is { } path)
        {
            QuestFile? file;
            lock (_state) _state.Files.TryGetValue(path, out file);
            if (file is null)
            {
                try
                {
                    file = await Task.Run(() => _locator.ReadFile(path) is { } data ? Read(path, data) : null);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
                {
                    await ShowMessage(L.T("QuestGraph.PhaseUnreadable"), L.T("QuestGraph.PhaseUnreadableText", path, ex.Message), MessageKind.Warning);
                    return null;
                }
                if (file is null)
                {
                    await ShowMessage(L.T("QuestGraph.PhaseMissing"), L.T("QuestGraph.PhaseMissingText", path), MessageKind.Warning);
                    return null;
                }
                _state.Origins[file] = _locator.Find(path)?.Archive.FilePath;
            }
            next = new Crumb(file, file.Graph, Path.GetFileName(path));
            _ = LoadRedkitAsync(file, path, _state.Origins.GetValueOrDefault(file));
        }
        else return null;
        return next;
    }

    // a window opened from another starts where that one went, sharing what it read
    private List<Crumb>? _startCrumbs;

    // ---------------------------------------------------------------- picking blocks

    private void Graph_BlockClicked(object? sender, (QuestBlock? Block, bool Add) e)
    {
        if (e.Block is null) { _shown = null; ShowFile(); SelectRows([]); return; }
        if (!_state.ItemOf.TryGetValue(e.Block, out var item)) return;
        ShowBlock(item);
        SelectRows(Graph.Selected.Select(b => _state.ItemOf[b]));
    }

    /// <summary>Picks a block: opens the graph it's in when another is shown, puts it in the middle, and shows it beside.</summary>
    private void Pick(QuestBlockItem item, bool reveal)
    {
        if (!Graph.Contains(item.Block)) ShowGraphOf(item.Block);
        if (reveal) Graph.Reveal(item.Block);
        else Graph.Select([item.Block]);
        ShowBlock(item);
        SelectRows([item]);
    }

    /// <summary>Opens the graph a block of the current file is in, from the file's own graph through the phases it's in.</summary>
    private void ShowGraphOf(QuestBlock block)
    {
        var file = _state.Items.First(f => f.Value.Any(i => i.Block == block)).Key;
        // keep the way here up to where this file starts
        int start = _crumbs.FindLastIndex(c => c.File == file && c.Graph == file.Graph);
        var path = new List<Crumb>();
        for (var owner = block.Graph.Owner; owner is not null; owner = owner.Graph.Owner) path.Insert(0, new Crumb(file, owner.Embedded!, owner.Name));
        _crumbs = start >= 0 ? [.. _crumbs.Take(start + 1), .. path] : [new Crumb(file, file.Graph, Path.GetFileName(file.Path)), .. path];
        ShowGraph();
    }

    private void SelectRows(IEnumerable<QuestBlockItem> items)
    {
        _syncing = true;
        BlockList.SelectedItems.Clear();
        foreach (var item in items.Where(_listed.Contains)) BlockList.SelectedItems.Add(item);
        if (BlockList.SelectedItems.Count > 0) BlockList.ScrollIntoView(BlockList.SelectedItems[^1]);
        _syncing = false;
    }

    private void BlockList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || BlockList.SelectedItem is not QuestBlockItem item) return;
        if (item.Elsewhere)
        {
            ShowElsewhere(item);
            return;
        }
        if (!Graph.Contains(item.Block)) ShowGraphOf(item.Block);
        Graph.Reveal(item.Block);
        ShowBlock(item);
    }

    /// <summary>A block only the copy compared with has, beside the graph: what it is, its GUID, and a way to open it there.</summary>
    private void ShowElsewhere(QuestBlockItem item)
    {
        _shown = null;
        Graph.Select([]);
        BlockTitle.Text = item.Title;
        BlockDetail.Text = string.Join("  ·  ", new[] { item.Kind, item.ClassName, item.Phase, item.Detail }.Where(s => s.Length > 0));
        BlockComment.Text = item.Comment;
        BlockComment.Visibility = item.Comment.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoGuidText.Visibility = OpenPhaseButton.Visibility = Visibility.Collapsed;
        GuidText.Text = item.Guid;
        RedkitGuidText.Text = item.Block.Guid is { } g ? QuestBlockItem.RedkitForm(g) : "";
        GuidPanel.Visibility = Visibility.Visible;
        LinksTitle.Text = L.T("QuestGraph.Links");
        LinksText.Inlines.Clear();
        foreach (var link in item.Block.Links)
        {
            if (LinksText.Inlines.Count > 0) LinksText.Inlines.Add(new LineBreak());
            LinksText.Inlines.Add(new Run($"{QuestBlockItem.Socket(link.Output)} → {link.To.Name}  ({QuestBlockItem.Socket(link.Input)})"));
        }
        if (LinksText.Inlines.Count == 0) LinksText.Inlines.Add(new Run(L.T("QuestGraph.NoLinks")) { FontStyle = FontStyles.Italic });
        CopiesTitle.Visibility = CopiesText.Visibility = Visibility.Collapsed;
        ShowCompare(item);
        if (_compare is { } compare)
        {
            PropertiesHeader.Visibility = Properties.Visibility = Visibility.Visible;
            Properties.Show(PropertyRow.Of(item.Block.Object, compare.OtherFile.Cr2w, ["cachedConnections", "sockets", "embeddedGraph", "position"]));
        }
    }

    private void BlockList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BlockList.SelectedItem is QuestBlockItem { Elsewhere: true } other)
        {
            OpenOther(other);
            return;
        }
        if (BlockList.SelectedItem is QuestBlockItem { Block.ClassName: "CQuestPhaseBlock" } item) _ = OpenPhaseAsync(item.Block);
    }

    private void Refresh()
    {
        if (_shown is { } item) ShowBlock(item);
        else ShowFile();
    }

    /// <summary>The file shown, with no block picked: what it is, the phase blocks that run it, and its own properties.</summary>
    private void ShowFile()
    {
        var file = Current.File;
        BlockTitle.Text = Path.GetFileName(file.Path);
        BlockDetail.Text = string.Join("  ·  ", new[]
        {
            file.IsQuest ? L.T("QuestGraph.Quest") : L.T("QuestGraph.Phase"),
            file.Cooked ? L.T("QuestGraph.Cooked") : L.T("QuestGraph.Uncooked"),
            L.T("Count.Block", _state.Items[file].Count),
        });
        BlockComment.Visibility = GuidPanel.Visibility = NoGuidText.Visibility = OpenPhaseButton.Visibility = Visibility.Collapsed;
        CopiesTitle.Visibility = CopiesText.Visibility = CompareTitle.Visibility = CompareInfo.Visibility = Visibility.Collapsed;
        LinksTitle.Text = L.T("QuestGraph.UsedBy");
        LinksText.Inlines.Clear();
        if (_state.Index is not { } index) LinksText.Inlines.Add(new Run(L.T("QuestGraph.FindingUses")) { FontStyle = FontStyles.Italic });
        else
        {
            bool reached = index.Reachable.Contains(file.Path);
            bool isRoot = index.Roots.Contains(file.Path, StringComparer.OrdinalIgnoreCase);
            LinksText.Inlines.Add(new Run(isRoot ? L.T("QuestGraph.IsRoot") : reached ? L.T("QuestGraph.Reached") : L.T("QuestGraph.NotReached")));
            if (index.UsedBy.TryGetValue(file.Path, out var uses))
            {
                LinksTitle.Text = L.T("QuestGraph.UsedByCount", uses.Count);
                foreach (var use in uses)
                {
                    LinksText.Inlines.Add(new LineBreak());
                    var link = new Hyperlink(new Run($"{Path.GetFileName(use.File)} › {use.BlockName}")) { ToolTip = use.File + (use.Reachable ? "" : "\n" + L.T("QuestGraph.NotReachedShort")) };
                    if (!use.Reachable) link.Foreground = (Brush)FindResource("TextFillColorTertiaryBrush");
                    link.Click += (_, _) => OpenAt(use.File, use.Guid, _locator, this);
                    LinksText.Inlines.Add(link);
                }
            }
            else if (!isRoot)
            {
                LinksText.Inlines.Add(new LineBreak());
                LinksText.Inlines.Add(new Run(L.T("QuestGraph.NoUses")) { FontStyle = FontStyles.Italic });
            }
        }
        var top = file.Cr2w.Object(0);
        ShowProperties(file, top, null, ["graph"]);
    }

    /// <summary>A block beside the graph: what it is, its GUID, its links in and out, and its properties.</summary>
    private void ShowBlock(QuestBlockItem item)
    {
        _shown = item;
        var block = item.Block;
        BlockTitle.Text = item.Title;
        var said = new List<string> { item.Kind, block.ClassName };
        if (item.Phase.Length > 0) said.Add(item.Phase);
        if (item.Detail.Length > 0 && item.Category != QuestCategory.Notes) said.Add(item.Detail);
        BlockDetail.Text = string.Join("  ·  ", said);
        BlockComment.Text = block.Comment;
        BlockComment.Visibility = block.Comment.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (block.Guid is { } guid)
        {
            GuidText.Text = guid.ToString();
            RedkitGuidText.Text = QuestBlockItem.RedkitForm(guid);
            GuidPanel.Visibility = Visibility.Visible;
            NoGuidText.Visibility = Visibility.Collapsed;
        }
        else
        {
            GuidPanel.Visibility = Visibility.Collapsed;
            NoGuidText.Visibility = Visibility.Visible;
        }
        OpenPhaseButton.Visibility = block.Embedded is not null || block.ExternalPhase is not null ? Visibility.Visible : Visibility.Collapsed;

        LinksTitle.Text = L.T("QuestGraph.Links");
        LinksText.Inlines.Clear();
        void Line(string socket, string arrow, QuestBlock other, string otherSocket, bool cut)
        {
            if (LinksText.Inlines.Count > 0) LinksText.Inlines.Add(new LineBreak());
            LinksText.Inlines.Add(new Run($"{QuestBlockItem.Socket(socket)} {arrow} ") { FontWeight = FontWeights.SemiBold });
            var target = _state.ItemOf.GetValueOrDefault(other);
            var link = new Hyperlink(new Run(target?.Title ?? other.Name)) { ToolTip = other.Guid?.ToString() };
            if (target is not null) link.Click += (_, _) => Pick(target, reveal: true);
            LinksText.Inlines.Add(link);
            LinksText.Inlines.Add(new Run(cut ? $"  ({L.T("QuestGraph.Cuts")})" : $"  ({QuestBlockItem.Socket(otherSocket)})") { Foreground = (Brush)FindResource("TextFillColorTertiaryBrush") });
        }
        foreach (var link in block.Links) Line(link.Output, "→", link.To, link.Input, QuestBlock.IsCut(link));
        foreach (var link in block.Incoming) Line(link.Input, "←", link.From, link.Output, QuestBlock.IsCut(link));
        foreach (string output in item.Outputs.Where(o => !block.Links.Any(l => QuestBlockItem.Socket(l.Output) == o)))
        {
            if (LinksText.Inlines.Count > 0) LinksText.Inlines.Add(new LineBreak());
            LinksText.Inlines.Add(new Run($"{output} → ") { FontWeight = FontWeights.SemiBold });
            LinksText.Inlines.Add(new Run(L.T("QuestGraph.Nothing")) { FontStyle = FontStyles.Italic, Foreground = (Brush)FindResource("TextFillColorTertiaryBrush") });
        }
        if (LinksText.Inlines.Count == 0) LinksText.Inlines.Add(new Run(L.T("QuestGraph.NoLinks")) { FontStyle = FontStyles.Italic });

        ShowCompare(item);

        // where else its GUID is: other blocks of the file, other files the game runs, and every copy
        CopiesText.Inlines.Clear();
        var file = FileOf(item);
        if (block.Guid is { } g)
        {
            var twins = _state.Items[file].Where(i => i != item && i.Block.Guid == g).ToList();
            foreach (var twin in twins)
            {
                if (CopiesText.Inlines.Count > 0) CopiesText.Inlines.Add(new LineBreak());
                CopiesText.Inlines.Add(new Run(L.T("QuestGraph.AlsoHere") + " ") { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("SystemFillColorCautionBrush") });
                var link = new Hyperlink(new Run(twin.Title + (twin.Phase.Length > 0 ? $" ({twin.Phase})" : "")));
                link.Click += (_, _) => Pick(twin, reveal: true);
                CopiesText.Inlines.Add(link);
            }
            if (_state.Index is null) CopiesText.Inlines.Add(new Run(L.T("QuestGraph.FindingCopies")) { FontStyle = FontStyles.Italic });
            else
            {
                foreach (var other in _state.Index.ReachedElsewhere(g, file.Path))
                {
                    if (CopiesText.Inlines.Count > 0) CopiesText.Inlines.Add(new LineBreak());
                    CopiesText.Inlines.Add(new Run(L.T("QuestGraph.AlsoElsewhere") + " ") { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("SystemFillColorCautionBrush") });
                    var link = new Hyperlink(new Run($"{Path.GetFileName(other.File)} › {other.BlockName}")) { ToolTip = other.File };
                    var copy = other;
                    link.Click += (_, _) => OpenCopy(copy, g);
                    CopiesText.Inlines.Add(link);
                }
                var copies = _state.Index.Find(g);
                if (copies.Count > 0)
                {
                    if (CopiesText.Inlines.Count > 0) CopiesText.Inlines.Add(new LineBreak());
                    WriteCopies(CopiesText, copies, g, file);
                }
            }
        }
        CopiesTitle.Visibility = CopiesText.Visibility = CopiesText.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowProperties(file, block.Object, block.Guid,
            ["cachedConnections", "sockets", "embeddedGraph", "position"]);
    }

    /// <summary>An object's properties: from REDkit's copy of its file when that has it (by GUID), else from the file itself.</summary>
    private void ShowProperties(QuestFile file, RedObject o, Guid? guid, string[] leaveOut)
    {
        PropertiesHeader.Visibility = Properties.Visibility = Visibility.Visible;
        if (_state.Redkit.TryGetValue(file, out var redkit))
        {
            SourceNoteBar.Show(redkit.From.Source, redkit.From.RedkitPath ?? _state.Origins.GetValueOrDefault(file), L.T("QuestGraph.QuestFileWord"));
            if (redkit.From.Redkit is { } copy)
            {
                var own = guid is { } g && redkit.ByGuid.TryGetValue(g, out var byGuid) ? byGuid : RedkitSource.Match(copy, o, file.Cr2w);
                if (own is not null)
                {
                    Properties.Show(PropertyRow.Of(own, copy, leaveOut));
                    return;
                }
            }
        }
        Properties.Show(PropertyRow.Of(o, file.Cr2w, leaveOut));
    }

    private void CopyGuid_Click(object sender, RoutedEventArgs e) => Copy(GuidText.Text);
    private void CopyRedkitGuid_Click(object sender, RoutedEventArgs e) => Copy(RedkitGuidText.Text);

    private void Copy(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        InfoText.Text = L.T("QuestGraph.Copied", text);
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    // ---------------------------------------------------------------- the list

    /// <summary>The kinds of block in the file shown, for the list's filter, with how many of each.</summary>
    /// <summary>The blocks listed for the file shown: its own, and with a comparison on, those only the other copy has.</summary>
    private List<QuestBlockItem> ListedItems()
    {
        var items = _state.Items[Current.File];
        return _compare is { } c && c.Shown == Current.File ? [.. items, .. c.Elsewhere] : items;
    }

    private void FillKinds()
    {
        var items = ListedItems();
        string? was = (KindBox.SelectedItem as KindChoice)?.Kind;
        bool wasDuplicates = (KindBox.SelectedItem as KindChoice)?.Duplicates == true;
        var kinds = items.GroupBy(i => i.Kind).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new KindChoice(g.Key, $"{g.Key} ({g.Count():N0})")).Prepend(new KindChoice(null, L.T("QuestGraph.AllKinds"))).ToList();
        int duplicates = items.Count(i => i.Duplicate);
        if (duplicates > 0) kinds.Insert(1, new KindChoice(null, L.T("QuestGraph.DuplicateKind", duplicates)) { Duplicates = true });
        int changed = items.Count(i => i.ChangeKind != QuestChange.None);
        if (changed > 0) kinds.Insert(1, new KindChoice(null, L.T("QuestGraph.ChangesKind", changed)) { Changes = true });
        bool wasChanges = (KindBox.SelectedItem as KindChoice)?.Changes == true;
        _syncing = true;
        KindBox.ItemsSource = kinds;
        KindBox.SelectedItem = (wasDuplicates ? kinds.FirstOrDefault(k => k.Duplicates) : null) ?? (wasChanges ? kinds.FirstOrDefault(k => k.Changes) : null)
            ?? kinds.FirstOrDefault(k => k.Kind == was && !k.Duplicates && !k.Changes) ?? kinds[0];
        _syncing = false;
    }

    private sealed record KindChoice(string? Kind, string Text)
    {
        /// <summary>The choice that lists the blocks whose GUID another block has too.</summary>
        public bool Duplicates { get; init; }
        /// <summary>The choice that lists what a comparison found.</summary>
        public bool Changes { get; init; }

        public override string ToString() => Text;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _crumbs.Count == 0) return;
        Filter();
    }

    /// <summary>The list as the filters have it: every word typed must be in a block, as must its kind; matches in the graph shown are marked.</summary>
    private void Filter()
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var items = ListedItems();
        string? kind = (KindBox.SelectedItem as KindChoice)?.Kind;
        bool changesOnly = (KindBox.SelectedItem as KindChoice)?.Changes == true;
        bool duplicatesOnly = (KindBox.SelectedItem as KindChoice)?.Duplicates == true;
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // a GUID pasted in REDkit's form finds the block too
        if (words.Length == 1 && QuestBlockItem.ParseGuid(words[0]) is { } guid) words = [guid.ToString()];
        var graph = ThisGraphCheck.IsChecked == true ? Current.Graph : null;
        _listed = items.Where(i => (kind is null || i.Kind == kind) && (!duplicatesOnly || i.Duplicate) && (!changesOnly || i.ChangeKind != QuestChange.None)
                                   && (graph is null || i.Elsewhere || i.Block.Graph == graph)
                                   && words.All(w => i.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        _syncing = true;
        BlockList.ItemsSource = _listed;
        _syncing = false;
        CountText.Text = _listed.Count == items.Count ? L.T("Count.Block", items.Count) : L.T("QuestGraph.CountOf", _listed.Count, items.Count);
        Graph.Highlight(words.Length == 0 && kind is null && !duplicatesOnly && !changesOnly ? [] : _listed.Select(i => i.Block).Where(Graph.Contains));
        _match = -1;
        if (_shown is { } shown) SelectRows([shown]);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _listed.Count == 0) return;
        _match = Keyboard.Modifiers == ModifierKeys.Shift ? (_match - 1 + _listed.Count) % _listed.Count : (_match + 1) % _listed.Count;
        Pick(_listed[_match], reveal: true);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Back when _crumbs.Count > 1: GoTo(_crumbs.Count - 2); break;
            case Key.System when e.SystemKey == Key.Up && _crumbs.Count > 1: GoTo(_crumbs.Count - 2); break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not DataGridCell: Graph.FitToView(); break;
            default: return;
        }
        e.Handled = true;
    }
}
