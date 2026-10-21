using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Communities;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Shows a community (.w2comm) as REDkit's community editor lays it out: its spawnset, an entry with its entities and
/// its story phases with when, how many and where they spawn, a row each; and its timetables, which layer's action
/// points each goes to at each time of day and what it does there. Rows can be narrowed to those with some words, and
/// the one picked shows every property of its entry, entity, story phase and spawn time beside them.
/// </summary>
public partial class CommunityWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private CommunityFile? _community;
    // REDkit's copy, read as the opened one is, which the properties come from when its rows line up with the opened file's
    private CommunityFile? _redkit;
    private SpawnsetRow? _shownRow;

    private CommunityWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = L.T("CommunityWindow.Community2", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        Loaded += async (_, _) => await LoadAsync();
    }

    public static bool CanShow(string name) => CommunityFile.IsCommunity(name);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new CommunityWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new CommunityWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        try
        {
            _community = await Task.Run(() => CommunityFile.Read(_readData()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = L.T("CommunityWindow.CommunityCouldNotRead", ex.Message);
            return;
        }
        var c = _community;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        TypeText.Text = c.SpawnsetType;
        int phases = c.Spawnset.Count(r => r.StoryPhase.Length > 0);
        CountsText.Text = $"{L.T("Count.Entry", c.Entries)}  ·  {L.T("Count.StoryPhase", phases)}  ·  {L.T("Count.Timetable", c.TimetableCount)}";
        SpawnsetTab.Header = L.T("CommunityWindow.Spawnset2", c.Entries);
        TimetablesTab.Header = L.T("CommunityWindow.Timetables2", c.TimetableCount);
        SpawnsetGrid.ItemsSource = c.Spawnset;
        TimetableGrid.ItemsSource = c.Timetables;
        InfoText.Text = L.T("CommunityWindow.CR2W", _path, _source, c.File.Version, L.T("Count.Entry", c.Entries), L.T("Count.Timetable", c.TimetableCount));
        ShowCommunity();
        _ = LoadRedkitAsync();
    }

    /// <summary>Reads REDkit's copy of the community, for one of the game's own, and says above the properties where they come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit is { } file)
            _redkit = await Task.Run(() =>
            {
                try { return CommunityFile.Read(file); }
                catch (InvalidDataException) { return null; }
            });
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, L.T("CommunityWindow.Community3"));
        if (_shownRow is { } row) ShowRow(row);
        else ShowCommunity();
    }

    /// <summary>The same row in REDkit's copy, when its rows line up with the opened file's; else the row itself.</summary>
    private (SpawnsetRow Row, Cr2wFile File) Source(SpawnsetRow row)
    {
        if (_redkit is { } redkit && redkit.Spawnset.Count == _community!.Spawnset.Count)
        {
            int at = _community.Spawnset.IndexOf(row);
            if (at >= 0 && redkit.Spawnset[at].EntryId == row.EntryId) return (redkit.Spawnset[at], redkit.File);
        }
        return (row, _community!.File);
    }

    /// <summary>The community's own properties, with no row picked.</summary>
    private void ShowCommunity()
    {
        if (_community is null) return;
        _shownRow = null;
        PickedTitle.Text = L.T("CommunityWindow.Community4");
        PickedDetail.Text = L.T("CommunityWindow.ClickRowSeeEntry2");
        OpenTemplateButton.IsEnabled = TimetableButton.IsEnabled = false;
        var (o, file) = _redkit is { } r ? (r.Community, r.File) : (_community.Community, _community.File);
        Properties.Show(PropertyRow.Of(o, file, leaveOut: ["communityTable", "storyPhaseTimetable"]));
    }

    private void SpawnsetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpawnsetGrid.SelectedItem is SpawnsetRow row) ShowRow(row);
    }

    /// <summary>A row's entry, entity, story phase and spawn time, each under a heading.</summary>
    private void ShowRow(SpawnsetRow picked)
    {
        _shownRow = picked;
        var (row, file) = Source(picked);
        var rows = new List<PropertyRow>();
        string id = row.Entry["entryID"] as string ?? "";
        PickedTitle.Text = id.Length > 0 ? L.T("CommunityWindow.Entry", id) : L.T("CommunityWindow.Entry2");
        var said = new List<string>();
        if (picked.Template.Length > 0 || row.Entity is not null) said.Add(Path.GetFileName(TemplateOf(picked)));
        var phase = PhaseOf(picked);
        if (phase is not null && Phase(phase) is { Length: > 0 } names) said.Add(L.T("CommunityWindow.StoryPhase3", names));
        PickedDetail.Text = string.Join("  ·  ", said);
        rows.Add(new PropertyRow(L.T("CommunityWindow.Entry2"), "", "", 0, null, heading: true));
        rows.AddRange(PropertyRow.Of(row.Entry, file, leaveOut: ["entities", "storyPhases"]));
        if (row.Entity is { } entity)
        {
            rows.Add(new PropertyRow(L.T("CommunityWindow.Entity"), "", "", 0, null, heading: true));
            rows.AddRange(PropertyRow.Of(entity, file));
        }
        if (row.Phase is { } p)
        {
            rows.Add(new PropertyRow(L.T("CommunityWindow.StoryPhase"), "", "", 0, null, heading: true));
            rows.AddRange(PropertyRow.Of(p, file, leaveOut: ["spawnTimetable"]));
        }
        if (row.Spawn is { } spawn)
        {
            rows.Add(new PropertyRow(L.T("CommunityWindow.SpawnTime"), "", "", 0, null, heading: true));
            rows.AddRange(PropertyRow.Of(spawn, file));
        }
        Properties.Show(rows);
        OpenTemplateButton.IsEnabled = TemplateOf(picked).Length > 0;
        TimetableButton.IsEnabled = phase?["timetableName"] is string t && t.Length > 0 && t != "None";
    }

    /// <summary>The template of the row's entity, or the entry's first when the row is one of its later phases.</summary>
    private string TemplateOf(SpawnsetRow row) =>
        row.Template.Length > 0 ? row.Template : _community!.Spawnset.FirstOrDefault(r => r.Group == row.Group && r.Template.Length > 0)?.Template ?? "";

    /// <summary>The story phase a row is of: its own, or the one above it its spawn time belongs to.</summary>
    private RedObject? PhaseOf(SpawnsetRow row) => row.Phase;

    private string Phase(RedObject phase) =>
        phase.Struct("storyPhaseName")?["tags"] is byte[] raw ? string.Join(", ", _community!.File.Tags(raw)) : "";

    private void SpawnsetGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SpawnsetGrid.SelectedItem is SpawnsetRow row && TemplateOf(row) is { Length: > 0 } template)
            _ = DataViewers.OpenLinkAsync(template, _locator, this, this);
    }

    private void OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_shownRow is { } row && TemplateOf(row) is { Length: > 0 } template) _ = DataViewers.OpenLinkAsync(template, _locator, this, this);
    }

    /// <summary>Goes to the picked story phase's timetable.</summary>
    private void ShowTimetable_Click(object sender, RoutedEventArgs e)
    {
        if (_shownRow?.Phase?["timetableName"] is not string name || _community is null) return;
        SearchBox.Text = "";
        Tabs.SelectedItem = TimetablesTab;
        var first = _community.Timetables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (first is null)
        {
            _ = Message.Show(null, L.T("CommunityWindow.TimetableIsntCommunityAnother", name), MessageKind.Info, primary: L.T("Common.OK"));
            return;
        }
        TimetableGrid.SelectedItem = first;
        TimetableGrid.ScrollIntoView(first);
    }

    private void TimetableGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimetableGrid.SelectedItem is not TimetableRow row || _community is null) return;
        var name = _community.Timetables.Where(t => t.Group == row.Group).Select(t => t.Name).FirstOrDefault(n => n.Length > 0) ?? "";
        PickedTitle.Text = name.Length > 0 ? L.T("CommunityWindow.Timetable3", name) : L.T("CommunityWindow.Timetable");
        var used = _community.Spawnset.Where(r => r.Timetable.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(r => r.StoryPhase).Distinct().ToList();
        PickedDetail.Text = used.Count > 0 ? L.T("CommunityWindow.KeptStoryPhase", string.Join(", ", used)) : L.T("CommunityWindow.NoStoryPhaseHere");
        OpenTemplateButton.IsEnabled = TimetableButton.IsEnabled = false;
        var timetable = _community.Community.Array("storyPhaseTimetable").OfType<RedObject>().ElementAtOrDefault(row.Group);
        Properties.Show(timetable is null ? [] : PropertyRow.Of(timetable, _community.File));
    }

    // ---------------------------------------------------------------- finding rows

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_community is null) return;
        string text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            SpawnsetGrid.ItemsSource = _community.Spawnset;
            TimetableGrid.ItemsSource = _community.Timetables;
            MatchText.Text = "";
            return;
        }
        // a whole entry or timetable is kept when any of its rows has the words, so its rows stay together
        var entries = _community.Spawnset.Where(r => r.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(r => r.Group).ToHashSet();
        var tables = _community.Timetables.Where(r => r.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(r => r.Group).ToHashSet();
        SpawnsetGrid.ItemsSource = _community.Spawnset.Where(r => entries.Contains(r.Group)).ToList();
        TimetableGrid.ItemsSource = _community.Timetables.Where(r => tables.Contains(r.Group)).ToList();
        MatchText.Text = $"{L.T("Count.Entry", entries.Count)}, {L.T("Count.Timetable", tables.Count)}";
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

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
