using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Sounds.Banks;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// The Objects tab of a bank: every object the bank keeps, read in full for any bank version from 2013 on. A tree of
/// its events down to what they play, then each kind of object; the picked object's settings in words and all its
/// values; names wherever the game's, its mods' or the bank's own files give them; the bank as text to copy or save.
/// </summary>
public partial class SoundWindow
{
    private BankView? _view;
    private List<ObjectNode> _objectRoots = [];
    private bool _playWhenReady;

    /// <summary>A line of the objects tree: a group, or an object with what it leads to.</summary>
    internal sealed class ObjectNode : INotifyPropertyChanged
    {
        private bool _expanded, _selected;
        private Visibility _visibility = Visibility.Visible;

        public ObjectNode(BankTreeNode node)
        {
            Title = node.Title;
            Kind = node.Kind;
            Object = node.Object;
            Rows = node.Rows;
            Fields = node.Fields;
            Children = node.Children.Select(c => new ObjectNode(c)).ToList();
        }

        public string Title { get; }
        public string Kind { get; }
        public BankObject? Object { get; }
        public List<BankSummaryRow>? Rows { get; }
        public BankField? Fields { get; }
        public List<ObjectNode> Children { get; }
        public FontWeight Weight => Object is null ? FontWeights.SemiBold : FontWeights.Normal;
        public bool Expanded { get => _expanded; set { _expanded = value; Changed(nameof(Expanded)); } }
        public bool Selected { get => _selected; set { _selected = value; Changed(nameof(Selected)); } }
        public Visibility Visibility { get => _visibility; set { _visibility = value; Changed(nameof(Visibility)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public IEnumerable<ObjectNode> All()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var d in c.All()) yield return d;
        }
    }

    /// <summary>A value of an object, under its Wwise name, with the name of what an ID is when it's known.</summary>
    internal sealed class FieldNode
    {
        public required string Name { get; init; }
        public required string Value { get; init; }
        public required List<FieldNode> Children { get; init; }
        public bool Expanded { get; set; }
    }

    /// <summary>Reads the bank's objects and their names, and fills the tree.</summary>
    private async Task ShowObjectsAsync(byte[] data)
    {
        ObjectsNote.Text = L.T("SoundWindow.ReadingObjects");
        BankObjects objects;
        try
        {
            objects = await Task.Run(() => BankObjects.Read(data));
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            ObjectsNote.Text = L.T("SoundWindow.ObjectsCouldNotRead", ex.Message);
            return;
        }
        if (objects.Version < BankFormat.Oldest)
        {
            ObjectsNote.Text = L.T("SoundWindow.ObjectsTooOld", objects.Version, BankFormat.Oldest);
            return;
        }
        ObjectsNote.Text = L.T("SoundWindow.FindingNames");
        string? game = GameLocator.Resolve(null);
        var names = await BankNames.ForAsync(game, Path.IsPathRooted(_source) ? _source : null);
        _view = new BankView(objects, names);
        var view = _view;
        var tree = await Task.Run(view.Tree);
        tree.Insert(0, BankNode(view));
        _objectRoots = tree.Select(n => new ObjectNode(n)).ToList();
        if (_objectRoots.Count > 0) _objectRoots[0].Expanded = true;
        ObjectTree.ItemsSource = _objectRoots;
        ObjectsTab.Header = L.T("SoundWindow.Objects2", objects.Objects.Count);
        var (named, all) = view.NameCount();
        int unread = objects.Objects.Count(o => !o.ReadWhole);
        ObjectsNote.Text = L.T("SoundWindow.ObjectsNote", L.T("Count.Object", objects.Objects.Count), named, all)
            + (unread > 0 ? "  ·  " + L.T("SoundWindow.ObjectsUnread", unread) : "");
        // the first event, or the first object, shows at once
        var first = _objectRoots.SelectMany(r => r.Children).FirstOrDefault(n => n.Object is not null);
        if (first is not null) first.Selected = true;
    }

    /// <summary>The bank itself, first in the tree: its version, ID and name, its objects by kind, its sections.</summary>
    private BankTreeNode BankNode(BankView view)
    {
        var rows = new List<BankSummaryRow>
        {
            new(L.T("SoundWindow.BankVersion"), $"{view.Bank.Version}" + (view.Bank.Version != Core.Packing.SoundCacheWriter.GameBankVersion
                ? "  ·  " + L.T("SoundWindow.GameWontLoadBank", Core.Packing.SoundCacheWriter.GameBankVersion) : "")),
            new(L.T("BankView.Id"), view.NameOf(view.Bank.BankId) is { } name ? $"{view.Bank.BankId}  ·  {name}" : $"{view.Bank.BankId}"),
        };
        if (_bank is { } bank)
        {
            rows.AddRange(bank.Objects.OrderByDescending(o => o.Value).Select(o => new BankSummaryRow(o.Key, $"{o.Value:N0}")));
            rows.AddRange(bank.Sections.Select(s => new BankSummaryRow(L.T("SoundWindow.Section", s.Id), L.T("SoundWindow.Bytes4", s.Size))));
        }
        return new BankTreeNode(Path.GetFileName(_path), L.T("SoundWindow.TheBank"), null, []) { Rows = rows, Fields = view.SectionsField() };
    }

    private void ObjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_view is null || ObjectTree.SelectedItem is not ObjectNode { Object: { } o } node)
        {
            ObjectTitle.Text = (ObjectTree.SelectedItem as ObjectNode)?.Title ?? "";
            ObjectSummary.ItemsSource = (ObjectTree.SelectedItem as ObjectNode)?.Rows;
            FieldTree.ItemsSource = (ObjectTree.SelectedItem as ObjectNode)?.Fields is { } fields ? new[] { Field(fields, 0) } : null;
            PlayObjectButton.Visibility = Visibility.Collapsed;
            return;
        }
        ObjectTitle.Text = _view.Title(o);
        ObjectSummary.ItemsSource = _view.Summary(o);
        FieldTree.ItemsSource = new[] { Field(o.Fields, 0) };
        PlayObjectButton.Visibility = RowOf(o) is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private FieldNode Field(BankField f, int depth)
    {
        string value = f.IsGroup ? (f.Type == "list" ? L.T("Count.Item", f.Children.Count) : "") : f.Text;
        if (!f.IsGroup && (f.Type == "tid" || f.Hash != BankHash.None) && _view?.NameOf(f.Long) is { } name) value += $"  ({name})";
        if (f.Error is not null) value += "  !! " + f.Error;
        return new FieldNode { Name = f.Name, Value = value, Children = f.Children.Select(c => Field(c, depth + 1)).ToList(), Expanded = depth < 3 };
    }

    /// <summary>The row of the Sounds tab that plays an object's sound, when it has one.</summary>
    private SoundRow? RowOf(BankObject o)
    {
        if (SoundGrid.ItemsSource is not IEnumerable<SoundRow> rows) return null;
        var sources = o.Fields.FindAll("sourceId").Select(s => (uint)s.Long).ToHashSet();
        return rows.FirstOrDefault(r => sources.Contains(r.Id));
    }

    private void PlayObject_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectTree.SelectedItem is not ObjectNode { Object: { } o } || RowOf(o) is not { } row) return;
        if (SoundGrid.SelectedItem == row && _player is not null)
        {
            _player.Position = 0;
            if (!_player.IsPlaying) TogglePlay();
            return;
        }
        _playWhenReady = true;
        SoundGrid.SelectedItem = row;
    }

    /// <summary>The waveform shrinks to a strip while the objects are shown, so they have the room.</summary>
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        WaveArea.Height = Tabs.SelectedItem == ObjectsTab ? 70 : 170;
    }

    private void ObjectSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = ObjectSearch.Text.Trim();
        ObjectSearchPlaceholder.Visibility = ObjectSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var root in _objectRoots) Filter(root, text);
    }

    /// <summary>Shows the lines that match (by name, kind or ID) and the lines above them; true when any does.</summary>
    private static bool Filter(ObjectNode node, string text)
    {
        bool self = text.Length == 0 || node.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || node.Kind.Contains(text, StringComparison.OrdinalIgnoreCase)
            || node.Object?.Id.ToString() == text;
        bool any = false;
        foreach (var c in node.Children) any |= Filter(c, self ? "" : text);
        bool shown = self || any;
        node.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0 && any) node.Expanded = true;
        return shown;
    }

    private void CopyObjects_Click(object sender, RoutedEventArgs e)
    {
        if (_view is not null) Copy(_view.Text());
    }

    private void SaveObjects_Click(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_path) + ".txt",
            Filter = L.T("SoundWindow.TextFilesTxt"),
            Title = L.T("SoundWindow.SaveObjectsTitle"),
        };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, _view.Text()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ShowMessage(L.T("SoundWindow.CouldNotSaved"), ex.Message, MessageKind.Error); }
    }

    private void CopyObjectId_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectTree.SelectedItem is ObjectNode { Object: { } o }) Copy(o.Id.ToString());
    }

    private void CopyObjectName_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectTree.SelectedItem is ObjectNode node) Copy(node.Title);
    }

    private void CopyObjectText_Click(object sender, RoutedEventArgs e)
    {
        if (_view is not null && ObjectTree.SelectedItem is ObjectNode { Object: { } o }) Copy(_view.Text(o));
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectSummary.ItemsSource is IEnumerable<BankSummaryRow> rows)
            Copy(string.Join("\n", rows.Select(r => $"{r.Label}: {r.Value.Replace("\n", "\n    ")}")));
    }

    private void CopyField_Click(object sender, RoutedEventArgs e)
    {
        if (FieldTree.SelectedItem is FieldNode f) Copy(f.Value.Length > 0 ? f.Value : f.Name);
    }
}
