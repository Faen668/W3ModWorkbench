using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Journals;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>An entry in the journal viewer's tree, which opens and is picked from code too.</summary>
public sealed class JournalItem : INotifyPropertyChanged
{
    public required JournalNode Node { get; init; }
    public JournalItem? Parent { get; init; }
    public List<JournalItem> Children { get; } = [];
    public string Name => Node.Name;
    public string Glyph { get; init; } = "";
    public Brush? Brush { get; init; }
    public FontWeight Weight => Node.Kind == "Category" || Node.IsFile ? FontWeights.SemiBold : FontWeights.Normal;
    /// <summary>How many it holds, for a category or a group.</summary>
    public string Note => Node.Kind == "Category" || Node.ClassName.EndsWith("Group", StringComparison.Ordinal) && Node.IsFile ? $"{Children.Count:N0}" : "";
    public string Tip { get; set; } = "";
    /// <summary>Where it is, for a search's results: its parents' names.</summary>
    public string Where { get; set; } = "";

    private bool _expanded, _selected;
    public bool IsExpanded { get => _expanded; set { if (_expanded == value) return; _expanded = value; Changed(nameof(IsExpanded)); } }
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Changed(nameof(IsSelected)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public IEnumerable<JournalItem> All()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var item in child.All()) yield return item;
    }
}

/// <summary>
/// The journal as REDkit's journal editor shows it: every journal file's entries in one tree (quests in their groups,
/// with their descriptions, objectives and map pins; characters; the bestiary; the glossary…), and the entry picked
/// with its texts in words and every property it has. A journal file opened from anywhere opens here with its entry
/// picked; one window serves them all. Entries can be found by name, script tag or what they say, and the journal
/// checked for duplicated script tags and GUIDs as the editor checks it.
/// </summary>
public partial class JournalWindow : Window, IMessageHost
{
    private static JournalWindow? _open;
    private readonly ResourceLocator _locator;
    private List<JournalItem> _roots = [];
    private List<JournalItem> _all = [];
    private Func<uint, string?>? _text;
    private JournalItem? _shown;
    // the file to pick once loaded, and a copy of it to use rather than the game's (a mod's, a project's)
    private string? _pending;
    private readonly Dictionary<string, byte[]> _own = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Cr2wFile?> _redkit = new(StringComparer.OrdinalIgnoreCase);

    private JournalWindow(ResourceLocator locator)
    {
        InitializeComponent();
        _locator = locator;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { if (ReferenceEquals(_open, this)) _open = null; };
    }

    public static bool CanShow(string name) => JournalTree.IsJournal(name);

    /// <summary>Shows a journal file's entry in the journal window, opening it when it isn't open. Its data is used rather than the game's copy.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        Show(path, data, locator, owner);

    public static void OpenFile(string file, ResourceLocator locator, Window owner)
    {
        // a file on disk under a game folder layout is known by its path from there; else by its name
        string path = file.Replace('/', '\\');
        foreach (string root in new[] { @"\r4data\", @"\content\", @"\files\Mod\Bundle\", @"\files\DLC\Bundle\" })
            if (path.IndexOf(root, StringComparison.OrdinalIgnoreCase) is int at and >= 0) { path = path[(at + root.Length)..]; break; }
        Show(path, File.ReadAllBytes(file), locator, owner);
    }

    private static void Show(string path, byte[] data, ResourceLocator locator, Window owner)
    {
        if (_open is { } window)
        {
            window._own[path] = data;
            window.Activate();
            if (window._roots.Count > 0)
            {
                // a copy of a file read before replaces it: read the journal again with it
                if (window._all.Any(i => string.Equals(i.Node.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    window._pending = path;
                    _ = window.LoadAsync();
                }
                else window.Pick(path);
            }
            else window._pending = path;
            return;
        }
        _open = new JournalWindow(locator) { _pending = path };
        _open._own[path] = data;
        _open.Standalone(owner).Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    /// <summary>Reads every journal file the game, the archives open and the project have, and builds the tree.</summary>
    private async Task LoadAsync()
    {
        BusyText.Visibility = Visibility.Visible;
        Body.Visibility = Visibility.Hidden;
        BusyText.Text = "Reading the journal…";
        var progress = new Progress<string>(text => BusyText.Text = text);
        var own = _own.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase);
        List<JournalNode> roots;
        int files, unread;
        try
        {
            (roots, files, unread) = await Task.Run(() => Read(own, progress));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            BusyText.Text = "The journal could not be read.\n\n" + ex.Message;
            return;
        }
        (_text, _) = await SceneWindow.LinesAsync();
        _roots = roots.Select(r => Item(r, null)).ToList();
        _all = _roots.SelectMany(r => r.All()).ToList();
        foreach (var item in _all) item.Tip = Tip(item);
        Tree.ItemsSource = _roots;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        int entries = _all.Count(i => i.Node.IsFile);
        CountText.Text = $"{Format.Count(files, "journal file")}, {Format.Count(_all.Count - _roots.Count, "entry", "entries")}"
                         + (unread > 0 ? $"  ·  {Format.Count(unread, "file")} couldn't be read" : "");
        InfoText.Text = $"Journal   ·   {Format.Count(files, "file")} from the game, the archives open and the project   ·   {Format.Count(entries, "top entry", "top entries")}";
        foreach (var root in _roots) root.IsExpanded = true;
        if (_pending is { } path) Pick(path);
    }

    private (List<JournalNode> Roots, int Files, int Unread) Read(Dictionary<string, byte[]> own, IProgress<string> progress)
    {
        var paths = _locator.AllPaths(".journal").Union(own.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var read = new List<(string, Cr2wFile)>();
        int unread = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            if (i % 50 == 0) progress.Report($"Reading the journal…\n{i:N0} of {paths.Count:N0} files");
            try
            {
                var data = own.TryGetValue(paths[i], out var mine) ? mine : _locator.ReadFile(paths[i]);
                if (data is null) { unread++; continue; }
                read.Add((paths[i], Cr2wFile.Read(data)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                unread++;
            }
        }
        return (JournalTree.Build(read), read.Count, unread);
    }

    /// <summary>The colour and icon of each kind of entry.</summary>
    private static (string Glyph, Color Color) Look(JournalNode node)
    {
        string c = node.ClassName;
        if (node.Kind == "Category") return ("", Color.FromRgb(0xD9, 0xA7, 0x3A));
        if (c.EndsWith("Group", StringComparison.Ordinal) || c.EndsWith("Root", StringComparison.Ordinal)) return ("", Color.FromRgb(0x6B, 0x9B, 0xD8));
        if (c.Contains("Description", StringComparison.Ordinal)) return ("", Color.FromRgb(0x9E, 0x9E, 0xA6));
        if (c.Contains("Objective", StringComparison.Ordinal)) return ("", Color.FromRgb(0x3F, 0xAE, 0x4F));
        if (c.Contains("MapPin", StringComparison.Ordinal)) return ("", Color.FromRgb(0xD0, 0x61, 0x4A));
        if (c.Contains("Phase", StringComparison.Ordinal)) return ("", Color.FromRgb(0x8E, 0x6B, 0xB0));
        if (c.StartsWith("CJournalCharacter", StringComparison.Ordinal)) return ("", Color.FromRgb(0x4C, 0x9A, 0xFF));
        if (c.StartsWith("CJournalCreature", StringComparison.Ordinal)) return ("", Color.FromRgb(0xC7, 0x5B, 0x7A));
        if (c.StartsWith("CJournalQuest", StringComparison.Ordinal)) return ("", Color.FromRgb(0xF2, 0x7A, 0x4A));
        return ("", Color.FromRgb(0x9E, 0x9E, 0xA6));
    }

    private static JournalItem Item(JournalNode node, JournalItem? parent)
    {
        var (glyph, color) = Look(node);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var item = new JournalItem { Node = node, Parent = parent, Glyph = glyph, Brush = brush };
        foreach (var child in node.Children) item.Children.Add(Item(child, item));
        return item;
    }

    /// <summary>An entry's tooltip: its kind and class, and its title in words.</summary>
    private string Tip(JournalItem item)
    {
        var node = item.Node;
        if (node.Object is null) return $"{node.Name}: {Format.Count(item.Children.Count, "entry", "entries")}";
        var lines = new List<string> { $"{node.Kind}  ·  {node.ClassName}" };
        foreach (var (property, id) in JournalTree.Strings(node.Object).Take(1))
            if (_text?.Invoke(id) is { Length: > 0 } text) lines.Add(text.Length > 120 ? text[..120] + "…" : text);
        if (node.IsFile && node.Path is { } path) lines.Add(path);
        return string.Join("\n", lines);
    }

    /// <summary>Picks a journal file's top entry, opening the branches it's in.</summary>
    private void Pick(string path)
    {
        var item = _all.FirstOrDefault(i => i.Node.IsFile && string.Equals(i.Node.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        Reveal(item);
    }

    private void Reveal(JournalItem item)
    {
        for (var p = item.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        item.IsExpanded = true;
        item.IsSelected = true;
        if (!ReferenceEquals(_shown, item)) _ = ShowAsync(item);
        // once the opened branches are laid out, bring it into view
        Dispatcher.BeginInvoke(() =>
        {
            Tree.UpdateLayout();
            if (Find(Tree, item) is { } container) container.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The tree's container of an item, when it's been made.</summary>
    private static TreeViewItem? Find(ItemsControl parent, JournalItem item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct) return direct;
        var path = new Stack<JournalItem>();
        for (var p = item.Parent; p is not null; p = p.Parent) path.Push(p);
        ItemsControl at = parent;
        while (path.Count > 0)
        {
            if (at.ItemContainerGenerator.ContainerFromItem(path.Pop()) is not TreeViewItem next) return null;
            at = next;
        }
        return at.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
    }

    // ---------------------------------------------------------------- the entry picked

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Tree.SelectedItem is JournalItem item) _ = ShowAsync(item);
    }

    private async Task ShowAsync(JournalItem item)
    {
        _shown = item;
        var node = item.Node;
        EntryTitle.Text = node.Name;
        if (node.Object is not { } o)
        {
            EntryDetail.Text = $"{Format.Count(item.Children.Count, "entry", "entries")} in the journal's {node.Name.ToLowerInvariant()}";
            EntryButtons.Visibility = Visibility.Collapsed;
            Texts.ItemsSource = null;
            Properties.Show([]);
            SourceNoteBar.Show(PropertySource.None, null, "journal entry");
            return;
        }
        var said = new List<string> { node.Kind, o.ClassName };
        if (o["uniqueScriptIdentifier"] is string tag && tag.Length > 0) said.Add($"script tag {tag}");
        if (node.Path is { } p) said.Add(p);
        EntryDetail.Text = string.Join("  ·  ", said);
        EntryButtons.Visibility = Visibility.Visible;
        // its texts in words, the title larger
        string Words(uint id) => _text?.Invoke(id) is { } text ? text.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase) : $"(string {id})";
        var texts = JournalTree.Strings(o).Select(s => new
        {
            Label = s.Property switch { "title" => "Title", "description" => "Description", "name" => "Name", var other => other },
            Text = Words(s.Id),
            Size = s.Property == "title" ? 16.0 : 13.0,
        }).ToList();
        // a quest's objectives, in words, so it reads at a glance
        var objectives = item.All().Skip(1).Where(i => i.Node.ClassName == "CJournalQuestObjective").ToList();
        if (objectives.Count > 0)
            texts.Add(new
            {
                Label = $"Objectives ({objectives.Count})",
                Text = string.Join("\n", objectives.Select(i => "•  " + (JournalTree.Strings(i.Node.Object!).Select(s => Words(s.Id)).FirstOrDefault() ?? i.Name))),
                Size = 13.0,
            });
        Texts.ItemsSource = texts;
        string? phase = o["questPhase"] is RedHandle h ? node.File!.PathOf(h) : null;
        OpenPhaseButton.Visibility = phase is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        OpenPhaseButton.Tag = phase;
        OpenPhaseText.Text = "Open the quest phase";
        Properties.Show(PropertyRow.Of(o, node.File!, leaveOut: ["children"]));
        // the properties from REDkit's copy of the file, when it has the same entry
        if (node.Path is not { } path) return;
        var redkit = await Task.Run(() =>
        {
            lock (_redkit)
                if (_redkit.TryGetValue(path, out var known)) return known;
            var read = RedkitSource.Read(path)?.File;
            lock (_redkit) _redkit[path] = read;
            return read;
        });
        if (!ReferenceEquals(_shown, item)) return;
        var guid = node.Guid;
        var same = redkit is null || guid == Guid.Empty ? null
            : Enumerable.Range(0, redkit.Exports.Count).Select(redkit.Object).FirstOrDefault(x => x["guid"] is Guid g && g == guid);
        if (same is not null)
        {
            Properties.Show(PropertyRow.Of(same, redkit!, leaveOut: ["children"]));
            SourceNoteBar.Show(PropertySource.Redkit, FileSafety.RedkitFile(path), "journal entry");
        }
        else SourceNoteBar.Show(_own.ContainsKey(path) ? PropertySource.Own : PropertySource.Cooked, null, "journal entry");
    }

    private void OpenPhase_Click(object sender, RoutedEventArgs e)
    {
        if (OpenPhaseButton.Tag is string phase) _ = DataViewers.OpenLinkAsync(phase, _locator, this, this);
    }

    private void CopyTag_Click(object sender, RoutedEventArgs e) => Copy(_shown?.Node.Object?["uniqueScriptIdentifier"] as string);

    private void CopyPath_Click(object sender, RoutedEventArgs e) => Copy(_shown?.Node.Path);

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _ = Message.Show(null, $"Copied {text}", MessageKind.Info, autoClose: true);
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    // ---------------------------------------------------------------- finding entries

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        string text = SearchBox.Text.Trim();
        if (text.Length < 2)
        {
            Results.Visibility = Visibility.Collapsed;
            Results.ItemsSource = null;
            return;
        }
        var found = _all.Where(i => i.Node.Object is not null && Matches(i, text)).Take(500).ToList();
        foreach (var item in found) item.Where = string.Join(" › ", Parents(item).Select(p => p.Name));
        Results.ItemsSource = found;
        Results.Visibility = Visibility.Visible;
        CountText.Text = found.Count == 0 ? "Nothing has those words" : found.Count == 500 ? "The first 500 found" : Format.Count(found.Count, "entry", "entries") + " found";
    }

    private static IEnumerable<JournalItem> Parents(JournalItem item)
    {
        var list = new List<JournalItem>();
        for (var p = item.Parent; p is not null; p = p.Parent) list.Insert(0, p);
        return list;
    }

    private bool Matches(JournalItem item, string text)
    {
        var o = item.Node.Object!;
        if (item.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (o["uniqueScriptIdentifier"] is string tag && tag.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var (_, id) in JournalTree.Strings(o))
            if (_text?.Invoke(id) is { } words && words.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is not JournalItem item) return;
        Reveal(item);
        _ = ShowAsync(item);
    }

    // ---------------------------------------------------------------- checks

    private void CheckTags_Click(object sender, RoutedEventArgs e) =>
        Report("Duplicated unique script tags", "unique script tag", i => i.Node.Object?["uniqueScriptIdentifier"] as string is { Length: > 0 } t && t != "None" ? t : null);

    private void CheckGuids_Click(object sender, RoutedEventArgs e) =>
        Report("Duplicated GUIDs", "GUID", i => i.Node.Guid != Guid.Empty ? i.Node.Guid.ToString() : null);

    /// <summary>Lists the values more than one entry has, and the entries, as the editor's checks do.</summary>
    private void Report(string title, string what, Func<JournalItem, string?> key)
    {
        var groups = _all.Select(i => (Item: i, Key: key(i))).Where(x => x.Key is not null).GroupBy(x => x.Key!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToList();
        if (groups.Count == 0)
        {
            _ = Message.Show(title, $"No two entries have the same {what}.", MessageKind.Info, primary: "OK");
            return;
        }
        var lines = groups.Take(40).Select(g => $"{g.Key}\n    " + string.Join("\n    ", g.Select(x => $"{x.Item.Name}  ({x.Item.Node.Path})")));
        _ = Message.Show(title, $"{Format.Count(groups.Count, "duplicated " + what)}:\n\n" + string.Join("\n", lines)
            + (groups.Count > 40 ? $"\n\n… and {groups.Count - 40:N0} more" : ""), MessageKind.Warning, primary: "OK");
    }

    private void MissingTags_Click(object sender, RoutedEventArgs e)
    {
        var missing = _all.Where(i => i.Node.Object is { } o && !(o["uniqueScriptIdentifier"] is string t && t.Length > 0 && t != "None")).ToList();
        if (missing.Count == 0)
        {
            _ = Message.Show("Unique script tags", "Every entry has a unique script tag.", MessageKind.Info, primary: "OK");
            return;
        }
        Results.ItemsSource = missing.Take(500).Select(i => { i.Where = string.Join(" › ", Parents(i).Select(p => p.Name)); return i; }).ToList();
        Results.Visibility = Visibility.Visible;
        CountText.Text = $"{Format.Count(missing.Count, "entry", "entries")} without a unique script tag (clear the search to see the tree again)";
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
        if (e.Key == Key.Escape && e.OriginalSource is not TextBox)
        {
            Close();
            e.Handled = true;
        }
    }
}
