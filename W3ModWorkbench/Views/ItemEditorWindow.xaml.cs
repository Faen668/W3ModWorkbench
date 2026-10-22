using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Items;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A row of the item list.</summary>
public sealed class ItemRow(XmlDef def, string? title) : INotifyPropertyChanged
{
    private bool _changed;

    public XmlDef Def => def;
    public string Title => title ?? def.Name;
    public string Detail => $"{def.Name}  ·  {def.Attribute("category")}";
    public string Search { get; } = $"{title} {def.Name} {def.Attribute("category")} {string.Join(' ', def.Tags)}";

    public bool Changed
    {
        get => _changed;
        set { if (_changed != value) { _changed = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangedVisibility))); } }
    }

    public Visibility ChangedVisibility => _changed ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The item and stat editor: every item of the game, and for the one picked its attributes, its stats (the abilities
/// it's given), the schematics and recipes that make it and the loot tables it's found in, each value editable with the
/// game's beside it. Saving writes the project's changes as the Remastered's XML overrides in the project's own DLC,
/// and the schematics' and loot's in copies of their files (<see cref="ItemEdits"/>).
/// </summary>
public partial class ItemEditorWindow : Window, IMessageHost
{
    private static ItemEditorWindow? _open;

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private ItemDatabase? _db;
    private ItemEdits? _edits;
    private Dictionary<uint, string> _names = [];
    private List<ItemRow> _rows = [];
    private XmlDef? _picked;
    private bool _unsaved, _closing;

    private ItemEditorWindow()
    {
        InitializeComponent();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    public static void Open(Window owner)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _open = new ItemEditorWindow().Remembered(owner, "ItemEditor");
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    // ---------------------------------------------------------------- reading

    private async Task LoadAsync()
    {
        string? game = GameLocator.Resolve(null);
        if (game is null)
        {
            BusyText.Text = L.T("ItemEditor.NoGame");
            return;
        }
        try
        {
            var progress = new Progress<string>(file => BusyText.Text = L.T("ItemEditor.ReadingFile", file));
            var reading = Task.Run(() => ItemDatabase.Load(game, progress));
            var strings = GameStringIndex.GetAsync(game, App.GameLanguage, dlcAndMods: false);
            _db = await reading;
            var index = await strings;
            _names = index.All.GroupBy(s => s.KeyHash).ToDictionary(g => g.Key, g => g.First().Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BusyText.Text = ex.Message;
            return;
        }
        Busy.Visibility = Visibility.Collapsed;
        UseProject(ProjectWorkspace.Current);
        _rows = [.. _db.Items.Select(d => new ItemRow(d, NameOf(d))).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)];
        CategoryBox.ItemsSource = new[] { L.T("ItemEditor.AllCategories") }.Concat(_rows.Select(r => r.Def.Attribute("category") ?? "").Where(c => c.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase)).ToList();
        CategoryBox.SelectedIndex = 0;
        MarkChanged();
        ApplyFilter();
        if (_db.Problems.Count > 0) StatusText.Text = L.T("ItemEditor.SomeUnread", _db.Problems.Count);
    }

    /// <summary>The in-game name of a definition, from its localisation key: what the game shows for it.</summary>
    private string? NameOf(XmlDef def) =>
        def.Attribute("localisation_key_name") is { Length: > 0 } key && _names.TryGetValue(W3Strings.HashKey(key), out var text) && text.Length > 0 ? text : null;

    private void UseProject(WorkbenchProject? project)
    {
        _edits = project is not null && _db is not null ? new ItemEdits(_db, project) : null;
        ProjectBar.Visibility = _edits is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- the list

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_db is null) return;
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? category = CategoryBox.SelectedIndex > 0 ? CategoryBox.SelectedItem as string : null;
        bool changedOnly = ChangedToggle.IsChecked == true;
        var shown = _rows.Where(r => (category is null || r.Def.Attribute("category") == category)
            && (!changedOnly || r.Changed)
            && words.All(w => r.Search.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        var picked = _picked;
        ItemList.ItemsSource = shown;
        CountText.Text = L.T("ItemEditor.Count", shown.Count, _rows.Count);
        if (picked is not null && shown.FirstOrDefault(r => r.Def == picked) is { } again) ItemList.SelectedItem = again;
        else if (shown.Count > 0 && ItemList.SelectedItem is null) ItemList.SelectedIndex = 0;
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemList.SelectedItem is ItemRow row && row.Def != _picked) ShowItem(row.Def);
    }

    /// <summary>The list's dots: each item that has a change of its own, or in its stats, makers or loot.</summary>
    private void MarkChanged()
    {
        if (_db is null) return;
        var changes = _edits?.Changes() ?? [];
        var touched = changes.Select(c => (c.Kind, c.Name.ToLowerInvariant())).ToHashSet();
        // a loot table changes the items whose own entries in it changed, not everything it holds
        var lootItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_edits is not null)
            foreach (var table in changes.Where(c => c.Kind == DefKind.Loot).Select(c => c.Name).Distinct().Select(n => _db.Find(DefKind.Loot, n)).OfType<XmlDef>())
            {
                var game = table.Element.Elements("loot_entry").ToList();
                var now = _edits.Current(table).Elements("loot_entry").ToList();
                for (int i = 0; i < Math.Max(game.Count, now.Count); i++)
                {
                    var g = i < game.Count ? game[i] : null;
                    var n = i < now.Count ? now[i] : null;
                    if (g is not null && n is not null && XNode.DeepEquals(g, n)) continue;
                    if (g?.Attribute("name")?.Value is { } gn) lootItems.Add(gn);
                    if (n?.Attribute("name")?.Value is { } nn) lootItems.Add(nn);
                }
            }
        foreach (var row in _rows)
        {
            var item = row.Def;
            row.Changed = touched.Contains((DefKind.Item, item.Name.ToLowerInvariant()))
                || item.BaseAbilities.Any(a => touched.Contains((DefKind.Ability, a.ToLowerInvariant())))
                || _db.MadeBy(item.Name).Any(m => touched.Contains((m.Kind, m.Name.ToLowerInvariant())))
                || lootItems.Contains(item.Name);
        }
        int defs = changes.Select(c => (c.Kind, c.Name)).Distinct().Count();
        StatusText.Text = changes.Count == 0 ? L.T("ItemEditor.NoChanges")
            : L.T("ItemEditor.Changes", L.T("Count.Change", changes.Count), defs) + (_unsaved ? L.T("ItemEditor.Unsaved") : "");
        SaveButton.IsEnabled = _unsaved && _edits is not null;
    }

    // ---------------------------------------------------------------- the card

    private void ShowItem(XmlDef item)
    {
        _picked = item;
        Card.Children.Clear();
        _resets.Clear();
        if (_db is null) return;
        string? name = NameOf(item);

        // the item's heading: its in-game name, its own and where it's defined
        Card.Children.Add(new TextBlock { Text = name ?? item.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Card.Children.Add(new TextBlock
        {
            Text = $"{item.Name}  ·  {item.Attribute("category")}  ·  {item.GamePath}", Opacity = 0.6, Margin = new Thickness(0, 2, 0, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (item.Attribute("localisation_key_description") is { Length: > 0 } descKey && _names.TryGetValue(W3Strings.HashKey(descKey), out var desc))
            Card.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 4, 0, 6) });

        // general: the attributes most changed first, the rest (animations, sounds, slots) folded away beneath
        Section(L.T("ItemEditor.General"), item, L.T("ItemEditor.GeneralHint"));
        var attributes = item.Element.Attributes().Where(a => a.Name.LocalName != "name").ToList();
        foreach (var attribute in attributes.Where(a => Main.Contains(a.Name.LocalName)).OrderBy(a => Array.IndexOf(Main, a.Name.LocalName)))
            Card.Children.Add(Field(item, [], attribute.Name.LocalName, attribute.Name.LocalName));
        var more = attributes.Where(a => !Main.Contains(a.Name.LocalName)).ToList();
        if (more.Count > 0)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var attribute in more) panel.Children.Add(Field(item, [], attribute.Name.LocalName, attribute.Name.LocalName));
            Card.Children.Add(new Expander
            {
                Header = L.T("ItemEditor.MoreAttributes", more.Count), Content = panel, Margin = new Thickness(0, 6, 0, 2),
                // open when one of them is changed, so a change is never hidden
                IsExpanded = _edits is not null && more.Any(a => _edits.Current(item).Attribute(a.Name)?.Value != a.Value),
            });
        }
        if (item.Element.Element("tags") is { } tags)
            Card.Children.Add(Field(item, PathTo(item.Element, tags), null, L.T("ItemEditor.Tags"), wide: true));
        if (item.Element.Element("base_abilities") is { } abilities)
            foreach (var a in abilities.Elements("a"))
                Card.Children.Add(Field(item, PathTo(item.Element, a), null, L.T("ItemEditor.BaseAbility"), wide: true));

        // stats: each ability the item is given, with what else uses it
        foreach (string abilityName in item.BaseAbilities.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ability = _db.Find(DefKind.Ability, abilityName);
            int users = _db.ItemsUsing(abilityName).Count;
            string hint = ability is null ? L.T("ItemEditor.AbilityNotFound")
                : users > 1 ? L.T("ItemEditor.AbilityShared", L.T("Count.Item", users)) : L.T("ItemEditor.AbilityOwn");
            Section(L.T("ItemEditor.Stats", abilityName), ability, hint, warn: users > 1);
            if (ability is null) continue;
            foreach (var stat in ability.Element.Elements().Where(e => e.Name.LocalName != "tags"))
                Card.Children.Add(StatRow(ability, stat));
        }

        // how it's made, and where it's found: copies of the game's files, as these can't be overridden
        var makers = _db.MadeBy(item.Name);
        foreach (var maker in makers)
        {
            Section(maker.Kind == DefKind.Schematic ? L.T("ItemEditor.CraftedBy", maker.Name) : L.T("ItemEditor.BrewedBy", maker.Name), maker,
                L.T("ItemEditor.WholeFile", maker.GamePath));
            foreach (var attribute in maker.Element.Attributes().Where(a => a.Name.LocalName is not ("name_name" or "craftedItem_name" or "cookedItem_name")))
                Card.Children.Add(Field(maker, [], attribute.Name.LocalName, attribute.Name.LocalName));
            foreach (var ingredient in maker.Element.Element("ingredients")?.Elements("ingredient") ?? [])
                Card.Children.Add(Field(maker, PathTo(maker.Element, ingredient), "quantity",
                    L.T("ItemEditor.Ingredient", NameOfItem(ingredient.Attribute("item_name")?.Value))));
        }
        if (makers.Count == 0) Note(L.T("ItemEditor.NotMade"));

        var tables = _db.FoundIn(item.Name);
        if (tables.Count == 0) Note(L.T("ItemEditor.NotFoundAnywhere"));
        foreach (var table in tables.Take(40))
        {
            Section(L.T("ItemEditor.FoundIn", table.Name), table, L.T("ItemEditor.WholeFile", table.GamePath));
            foreach (var entry in table.Element.Elements("loot_entry").Where(e => string.Equals(e.Attribute("name")?.Value, item.Name, StringComparison.OrdinalIgnoreCase)))
                foreach (string field in new[] { "quantity_min", "quantity_max", "chance" })
                    if (entry.Attribute(field) is not null) Card.Children.Add(Field(table, PathTo(table.Element, entry), field, field));
        }
        if (tables.Count > 40) Note(L.T("ItemEditor.MoreTables", tables.Count - 40));
        CardScroller.ScrollToTop();
    }

    /// <summary>The item attributes shown first: what a mod most often changes.</summary>
    private static readonly string[] Main = ["price", "weight", "stackable", "enhancement_slots", "initial_durability", "max_durability", "quality",
        "grid_size", "category", "localisation_key_name", "localisation_key_description", "icon_path"];

    private string NameOfItem(string? name) =>
        name is null ? "" : _db?.Find(DefKind.Item, name) is { } def && NameOf(def) is { } shown ? $"{shown} ({name})" : name;

    /// <summary>The way from a definition to one of its elements: the place of each among its parent's elements.</summary>
    private static int[] PathTo(XElement root, XElement element) =>
        [.. element.AncestorsAndSelf().TakeWhile(e => e != root).Reverse().Select(e => e.ElementsBeforeSelf().Count())];

    private static XElement? Resolve(XElement root, int[] path)
    {
        XElement? e = root;
        foreach (int i in path) e = e?.Elements().ElementAtOrDefault(i);
        return e;
    }

    private void Section(string title, XmlDef? def, string hint, bool warn = false)
    {
        var header = new DockPanel { Margin = new Thickness(0, 16, 0, 4) };
        if (def is not null && _edits is not null)
        {
            var reset = new Button { Content = L.T("ItemEditor.PutBack"), Padding = new Thickness(8, 2, 8, 2), ToolTip = L.T("ItemEditor.PutBackTip") };
            reset.Visibility = _edits.IsChanged(def) ? Visibility.Visible : Visibility.Collapsed;
            reset.Click += (_, _) =>
            {
                _edits.Revert(def);
                _unsaved = true;
                MarkChanged();
                ShowItem(_picked!);
            };
            DockPanel.SetDock(reset, Dock.Right);
            header.Children.Add(reset);
            _resets[def] = reset;
        }
        header.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        Card.Children.Add(header);
        var text = new TextBlock { Text = hint, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        if (warn) text.Foreground = (Brush)FindResource("SystemFillColorCautionBrush");
        Card.Children.Add(text);
    }

    private readonly Dictionary<XmlDef, Button> _resets = [];

    private void Note(string text) =>
        Card.Children.Add(new TextBlock { Text = text, Opacity = 0.6, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });

    /// <summary>
    /// A value to edit: an attribute of one of the definition's elements, or the element's text when the attribute is
    /// null. Beside it, the game's value once it's changed.
    /// </summary>
    private FrameworkElement Field(XmlDef def, int[] path, string? attribute, string label, bool wide = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wide ? 150 : 210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = wide ? new GridLength(3, GridUnitType.Star) : new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string? Game() => Resolve(def.Element, path) is { } e ? attribute is null ? e.Value.Trim() : e.Attribute(attribute)?.Value : null;
        string? Now() => _edits is null ? Game() : Resolve(_edits.Current(def), path) is { } e ? attribute is null ? e.Value.Trim() : e.Attribute(attribute)?.Value : null;

        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label, Margin = new Thickness(0, 0, 10, 0) };
        var box = new TextBox { Text = Now() ?? "", Padding = new Thickness(6, 3, 6, 3), IsReadOnly = _edits is null, VerticalContentAlignment = VerticalAlignment.Center };
        var was = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        void ShowState()
        {
            bool changed = Now() != Game();
            was.Text = changed ? L.T("ItemEditor.GameValue", Game() ?? "–") : "";
            name.FontWeight = changed ? FontWeights.SemiBold : FontWeights.Normal;
            name.Foreground = changed ? (Brush)FindResource("AccentTextFillColorPrimaryBrush") : (Brush)FindResource("TextFillColorPrimaryBrush");
        }
        box.TextChanged += (_, _) =>
        {
            if (_edits is null || box.Text == (Now() ?? "")) return;
            var target = Resolve(_edits.Edit(def), path);
            if (target is null) return;
            if (attribute is null) target.Value = box.Text;
            else target.SetAttributeValue(attribute, box.Text);
            Edited(def);
            ShowState();
        };
        ShowState();
        Grid.SetColumn(box, 1);
        Grid.SetColumn(was, 2);
        grid.Children.Add(name);
        grid.Children.Add(box);
        grid.Children.Add(was);
        return grid;
    }

    /// <summary>A stat: its name and type, and its min and max; a stat whose two are the same changes both as one.</summary>
    private FrameworkElement StatRow(XmlDef ability, XElement stat)
    {
        int[] path = PathTo(ability.Element, stat);
        string type = stat.Attribute("type")?.Value ?? "";
        var row = new StackPanel();
        bool hasMin = stat.Attribute("min") is not null, hasMax = stat.Attribute("max") is not null;
        if (!hasMin && !hasMax)
        {
            row.Children.Add(new TextBlock { Text = $"{stat.Name.LocalName}  ({L.T("ItemEditor.NoValue")})", Opacity = 0.6, Margin = new Thickness(0, 3, 0, 3) });
            return row;
        }
        string label = type.Length > 0 ? $"{stat.Name.LocalName}  ·  {type}" : stat.Name.LocalName;
        // one value when the game gives the stat a single number (min and max alike, or max left out)
        bool single = !hasMax || stat.Attribute("min")?.Value == stat.Attribute("max")?.Value;
        if (single)
        {
            var field = Field(ability, path, "min", label);
            if (hasMax && field is Grid g && g.Children.OfType<TextBox>().FirstOrDefault() is { } box)
                box.TextChanged += (_, _) =>
                {
                    if (_edits is null) return;
                    if (Resolve(_edits.Edit(ability), path) is { } e && e.Attribute("max")?.Value != box.Text)
                    {
                        e.SetAttributeValue("max", box.Text);
                        Edited(ability);
                    }
                };
            row.Children.Add(field);
        }
        else
        {
            row.Children.Add(Field(ability, path, "min", label + "  ·  min"));
            row.Children.Add(Field(ability, path, "max", label + "  ·  max"));
        }
        return row;
    }

    private void Edited(XmlDef def)
    {
        _unsaved = true;
        if (_resets.TryGetValue(def, out var reset)) reset.Visibility = Visibility.Visible;
        MarkChanged();
    }

    // ---------------------------------------------------------------- saving

    private async void Project_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is not MainWindow main) return;
        if (await main.EnsureProjectAsync(L.T("ItemEditor.NeedsProject"), Message) is not { } project) return;
        UseProject(project);
        MarkChanged();
        if (_picked is not null) ShowItem(_picked);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (_edits is null || Application.Current.MainWindow is not MainWindow main || ProjectWorkspace.Current is not { } project) return false;
        List<string> written;
        try { written = _edits.Save(PlusCheck.IsChecked == true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowMessage(L.T("ItemEditor.CouldntSave"), ex.Message, MessageKind.Error);
            return false;
        }
        _unsaved = false;
        if (!project.Contains(project.WorkspacePath)) await main.AddToProjectAsync([project.WorkspacePath]);
        else await main.ReloadProjectAsync();
        MarkChanged();
        int overrides = written.Count(f => f.Contains(project.DlcFolderName, StringComparison.OrdinalIgnoreCase));
        _ = Message.Show(null, L.T("ItemEditor.Saved", L.T("Count.File", written.Count), overrides, project.DlcFolderName), MessageKind.Info, autoClose: true);
        return true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { if (SaveButton.IsEnabled) _ = SaveAsync(); }
        else return;
        e.Handled = true;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing || !_unsaved || _edits is null) return;
        e.Cancel = true;
        var choice = await Message.Ask(L.T("ItemEditor.UnsavedTitle"), L.T("ItemEditor.UnsavedText"), MessageKind.Question,
            L.T("ItemEditor.Save"), L.T("ItemEditor.Discard"), L.T("Common.Cancel"));
        if (choice == MessageChoice.Primary && !await SaveAsync()) return;
        if (choice is MessageChoice.Primary or MessageChoice.Secondary)
        {
            _closing = true;
            Close();
        }
    }
}
