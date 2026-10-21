using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Menu;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Highlighting;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A key the menu needs text for, with the text typed for it and whether the strings CSV has it.</summary>
public sealed class KeyRow(string key, string use, string text, uint? existingId) : INotifyPropertyChanged
{
    private string _text = text;

    public string Key { get; } = key;
    public string Use { get; } = use;
    public uint? ExistingId { get; set; } = existingId;
    public string InFile => ExistingId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "—";

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            TextChanged?.Invoke(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<KeyRow>? TextChanged;
}

/// <summary>
/// Builds and edits a mod menu (the XML in user_config_matrix) with forms instead of tags: pages, settings of each
/// kind, options and presets, with the text each key needs, the script that reads the settings, and a check for mistakes.
/// </summary>
public partial class MenuBuilderPanel : UserControl
{
    private sealed record KindChoice(MenuVarKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly KindChoice[] Kinds =
    [
        new(MenuVarKind.Toggle, L.T("MenuBuilderPanel.ToggleOff")),
        new(MenuVarKind.Slider, L.T("MenuBuilderPanel.Slider")),
        new(MenuVarKind.Options, L.T("MenuBuilderPanel.OptionsListChoices")),
        new(MenuVarKind.Separator, L.T("MenuBuilderPanel.Separator")),
        new(MenuVarKind.Other, L.T("MenuBuilderPanel.OtherKeptWritten")),
    ];

    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ObservableCollection<KeyRow> _keys = [];
    private MenuFile? _model;
    private MenuGroup? _group;
    private MenuVar? _var;
    private string _prevVarId = "";
    private bool _loading;
    private string? _xmlPath;
    private string? _gameFolder;
    private string? _csvPath;
    private Dictionary<string, uint> _csvKeys = new(StringComparer.Ordinal);
    private uint _csvMax;

    /// <summary>The model changed: the XML needs writing again.</summary>
    public event Action? ModelChanged;

    private bool _highlighted;

    public MenuBuilderPanel()
    {
        InitializeComponent();
        KindBox.ItemsSource = Kinds;
        KeyGrid.ItemsSource = _keys;
        Loaded += (_, _) =>
        {
            if (_highlighted) return;
            _highlighted = true;
            bool dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b
                        && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;
            ScriptBox.TextArea.TextView.LineTransformers.Insert(0, new WitcherScriptColorizer(ScriptBox.Document, dark) { View = ScriptBox.TextArea.TextView });
            if (TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush accent)
            {
                var selection = new SolidColorBrush(Color.FromArgb(0x66, accent.Color.R, accent.Color.G, accent.Color.B));
                selection.Freeze();
                ScriptBox.TextArea.SelectionBrush = selection;
                ScriptBox.TextArea.SelectionBorder = null;
                ScriptBox.TextArea.SelectionForeground = null;
            }
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshTab();
        };
    }

    public MenuFile? Model => _model;

    private IMessageHost? Host => Window.GetWindow(this) as IMessageHost;

    private void Tell(string title, string message, MessageKind kind = MessageKind.Info) => _ = Host?.ShowMessage(title, message, kind);

    /// <summary>Shows a menu to edit. <paramref name="xmlPath"/> is where its file is, to look for the strings CSV beside it.</summary>
    public void Load(MenuFile model, string? xmlPath, string? gameFolder)
    {
        if (_model is not null) _model.Changed -= Model_Changed;
        _model = model;
        _xmlPath = xmlPath;
        _gameFolder = gameFolder;
        model.Changed += Model_Changed;
        _csvPath = null;
        _csvKeys.Clear();
        _csvMax = 0;
        FindCsv();

        _loading = true;
        GroupList.ItemsSource = model.Groups;
        _loading = false;
        GroupList.SelectedIndex = model.Groups.Count > 0 ? 0 : -1;
        if (model.Groups.Count == 0) ShowDetails();
        RefreshTab();
    }

    private void Model_Changed()
    {
        ModelChanged?.Invoke();
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    // ---------------------------------------------------------------- pages

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _group = GroupList.SelectedItem as MenuGroup;
        VarList.ItemsSource = _group?.Vars;
        PresetGrid.ItemsSource = _group?.Presets;
        PresetsTitle.Text = _group is null ? L.T("MenuBuilderPanel.PresetsPage") : L.T("MenuBuilderPanel.Presets2", _group.Summary);
        VarList.SelectedIndex = -1;
        PresetGrid.SelectedIndex = _group?.Presets.Count > 0 ? 0 : -1;
        ShowDetails();
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        string mod = _model.Groups.Count > 0 ? CommonPrefix(_model.Groups.Select(g => g.Id)) : "MyMod";
        int n = _model.Groups.Count + 1;
        string id = $"{mod}Page{n}";
        string name = _model.Groups.FirstOrDefault()?.DisplayName is { Length: > 0 } first && first.LastIndexOf('.') is var dot and > 0
            ? first[..(dot + 1)] + id
            : $"Mods.{mod}.{id}";
        var group = new MenuGroup { Id = id, DisplayName = name };
        group.Presets.Add(new MenuPreset { Id = "0", DisplayName = id + "_Preset_Default" });
        _model.Groups.Add(group);
        GroupList.SelectedItem = group;
    }

    private static string CommonPrefix(IEnumerable<string> ids)
    {
        var list = ids.Where(i => i.Length > 0).ToList();
        if (list.Count == 0) return "MyMod";
        string prefix = list[0];
        foreach (string id in list.Skip(1))
        {
            int n = 0;
            while (n < prefix.Length && n < id.Length && prefix[n] == id[n]) n++;
            prefix = prefix[..n];
        }
        prefix = prefix.TrimEnd('_');
        return prefix.Length >= 3 ? prefix : "MyMod";
    }

    private void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || _group is null) return;
        int at = _model.Groups.IndexOf(_group);
        _model.Groups.Remove(_group);
        GroupList.SelectedIndex = Math.Min(at, _model.Groups.Count - 1);
    }

    private void GroupUp_Click(object sender, RoutedEventArgs e) => Move(_model?.Groups, _group, -1, GroupList);

    private void GroupDown_Click(object sender, RoutedEventArgs e) => Move(_model?.Groups, _group, 1, GroupList);

    private static void Move<T>(ObservableCollection<T>? list, T? item, int by, ListBox box) where T : class
    {
        if (list is null || item is null) return;
        int from = list.IndexOf(item), to = from + by;
        if (from < 0 || to < 0 || to >= list.Count) return;
        list.Move(from, to);
        box.SelectedItem = item;
    }

    // ---------------------------------------------------------------- settings

    private void VarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_var is not null) _var.PropertyChanged -= Var_PropertyChanged;
        _var = VarList.SelectedItem as MenuVar;
        if (_var is not null)
        {
            _prevVarId = _var.Id;
            _var.PropertyChanged += Var_PropertyChanged;
        }
        ShowDetails();
    }

    /// <summary>Most mods name a setting's text after its id, so the key follows the id while the two are the same.</summary>
    private void Var_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MenuVar v || e.PropertyName != nameof(MenuVar.Id) || v.IsSeparator) return;
        if (v.DisplayName.Length == 0 || v.DisplayName == _prevVarId) v.DisplayName = v.Id;
        // presets that set the setting by its old id follow it
        if (_group is not null && _prevVarId.Length > 0)
            foreach (var p in _group.Presets)
                foreach (var entry in p.Entries.Where(en => en.VarId == _prevVarId)) entry.VarId = v.Id;
        _prevVarId = v.Id;
    }

    private void AddVar_Click(object sender, RoutedEventArgs e)
    {
        if (_group is null) return;
        var menu = new ContextMenu { PlacementTarget = AddVarButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var kind in Kinds.Where(k => k.Kind != MenuVarKind.Other))
        {
            var item = new MenuItem { Header = kind.Label };
            item.Click += (_, _) => AddVar(kind.Kind);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AddVar(MenuVarKind kind)
    {
        if (_group is null) return;
        var v = new MenuVar { Kind = kind };
        if (kind == MenuVarKind.Separator)
        {
            v.Id = "Separator";
        }
        else
        {
            string prefix = CommonPrefix(_group.Vars.Where(x => !x.IsSeparator).Select(x => x.Id));
            if (prefix == "MyMod" && _group.Vars.All(x => x.IsSeparator)) prefix = CommonPrefix([_group.Id]);
            string stem = kind switch { MenuVarKind.Slider => "Amount", MenuVarKind.Options => "Mode", _ => "Option" };
            string id = $"{prefix}_{stem}";
            for (int n = 2; _group.Vars.Any(x => x.Id == id); n++) id = $"{prefix}_{stem}{n}";
            v.Id = id;
            v.DisplayName = id;
            if (kind == MenuVarKind.Toggle) v.Default = "true";
            if (kind == MenuVarKind.Slider) (v.Min, v.Max, v.Steps, v.Default) = ("0", "10", "10", "5");
            if (kind == MenuVarKind.Options)
            {
                v.Options.Add(new MenuOption { Id = "0", DisplayName = id + "_00" });
                v.Options.Add(new MenuOption { Id = "1", DisplayName = id + "_01" });
                v.Default = "0";
            }
        }
        int at = _var is null ? _group.Vars.Count : _group.Vars.IndexOf(_var) + 1;
        _group.Vars.Insert(at, v);
        VarList.SelectedItem = v;
        VarList.ScrollIntoView(v);
    }

    private void RemoveVar_Click(object sender, RoutedEventArgs e)
    {
        if (_group is null || _var is null) return;
        int at = _group.Vars.IndexOf(_var);
        _group.Vars.Remove(_var);
        VarList.SelectedIndex = Math.Min(at, _group.Vars.Count - 1);
    }

    private void VarUp_Click(object sender, RoutedEventArgs e) => Move(_group?.Vars, _var, -1, VarList);

    private void VarDown_Click(object sender, RoutedEventArgs e) => Move(_group?.Vars, _var, 1, VarList);

    // ---------------------------------------------------------------- details

    /// <summary>Shows the form for what's picked: the setting, else the page.</summary>
    private void ShowDetails()
    {
        _loading = true;
        try
        {
            NothingText.Visibility = _group is null ? Visibility.Visible : Visibility.Collapsed;
            GroupForm.Visibility = _group is not null && _var is null ? Visibility.Visible : Visibility.Collapsed;
            GroupForm.DataContext = _group;
            VarForm.Visibility = _var is not null ? Visibility.Visible : Visibility.Collapsed;
            VarForm.DataContext = _var;
            if (_var is null) return;

            KindBox.SelectedItem = Kinds.First(k => k.Kind == _var.Kind);
            ApplyKindLook(_var);
            OptionGrid.ItemsSource = _var.Options;
            DefaultHint.Text = _var.Kind switch
            {
                MenuVarKind.Toggle => L.T("MenuBuilderPanel.TrueFalse"),
                MenuVarKind.Slider => L.T("MenuBuilderPanel.NumberBetweenMinimumMaximum"),
                MenuVarKind.Options => L.T("MenuBuilderPanel.ValueOptionPickedFirst"),
                _ => "",
            };
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyKindLook(MenuVar v)
    {
        IdFields.Visibility = v.IsSeparator ? Visibility.Collapsed : Visibility.Visible;
        DefaultFields.Visibility = v.HasDefault ? Visibility.Visible : Visibility.Collapsed;
        SliderFields.Visibility = v.IsSlider ? Visibility.Visible : Visibility.Collapsed;
        OptionFields.Visibility = v.IsOptions ? Visibility.Visible : Visibility.Collapsed;
        SeparatorHint.Visibility = v.IsSeparator ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _var is null || KindBox.SelectedItem is not KindChoice choice || choice.Kind == _var.Kind) return;
        _var.Kind = choice.Kind;
        if (choice.Kind == MenuVarKind.Separator)
        {
            _var.Id = "Separator";
            _var.DisplayName = "";
            _var.Default = "";
        }
        else if (choice.Kind == MenuVarKind.Options && _var.Options.Count == 0)
        {
            _var.Options.Add(new MenuOption { Id = "0", DisplayName = _var.DisplayName + "_00" });
            _var.Options.Add(new MenuOption { Id = "1", DisplayName = _var.DisplayName + "_01" });
        }
        ShowDetails();
    }

    private void WholeNumbers_Click(object sender, RoutedEventArgs e)
    {
        if (_var is null) return;
        if (double.TryParse(_var.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
            && double.TryParse(_var.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out double hi))
            _var.Steps = Math.Max(1, hi - lo).ToString(CultureInfo.InvariantCulture);
    }

    private void AddOption_Click(object sender, RoutedEventArgs e)
    {
        if (_var is null) return;
        int next = _var.Options.Select(o => int.TryParse(o.Id, out int n) ? n : -1).DefaultIfEmpty(-1).Max() + 1;
        _var.Options.Add(new MenuOption { Id = next.ToString(CultureInfo.InvariantCulture), DisplayName = $"{_var.DisplayName}_{next:00}" });
    }

    private void RemoveOption_Click(object sender, RoutedEventArgs e)
    {
        if (_var is null) return;
        foreach (var option in OptionGrid.SelectedItems.OfType<MenuOption>().ToList()) _var.Options.Remove(option);
    }

    // ---------------------------------------------------------------- presets

    private MenuPreset? SelectedPreset => PresetGrid.SelectedItem as MenuPreset;

    private void PresetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EntryGrid.ItemsSource = SelectedPreset?.Entries;
        EntriesTitle.Text = SelectedPreset is null ? L.T("MenuBuilderPanel.Values") : L.T("MenuBuilderPanel.ValuesPreset", SelectedPreset.Id);
    }

    private void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_group is null) return;
        int next = _group.Presets.Select(p => int.TryParse(p.Id, out int n) ? n : -1).DefaultIfEmpty(-1).Max() + 1;
        string prefix = CommonPrefix([_group.Id]);
        var preset = new MenuPreset
        {
            Id = next.ToString(CultureInfo.InvariantCulture),
            DisplayName = next == 0 ? $"{prefix}_Preset_Default" : $"{prefix}_Preset_{next:00}",
        };
        _group.Presets.Add(preset);
        PresetGrid.SelectedItem = preset;
        if (next > 0) FillPreset(preset);
    }

    private void RemovePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_group is not null && SelectedPreset is { } preset) _group.Presets.Remove(preset);
    }

    private void FillPreset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPreset is { } preset) FillPreset(preset);
    }

    private void FillPreset(MenuPreset preset)
    {
        if (_group is null) return;
        foreach (var v in _group.Vars.Where(v => v.HasDefault && v.Id.Length > 0 && preset.Entries.All(en => en.VarId != v.Id)))
        {
            string value = v.Default.Length > 0 ? v.Default : v.Kind switch
            {
                MenuVarKind.Toggle => "false",
                MenuVarKind.Slider => v.Min,
                MenuVarKind.Options => v.Options.FirstOrDefault()?.Id ?? "0",
                _ => "",
            };
            preset.Entries.Add(new MenuEntry { VarId = v.Id, Value = value });
        }
    }

    private void AddEntry_Click(object sender, RoutedEventArgs e) => SelectedPreset?.Entries.Add(new MenuEntry());

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPreset is not { } preset) return;
        foreach (var entry in EntryGrid.SelectedItems.OfType<MenuEntry>().ToList()) preset.Entries.Remove(entry);
    }

    // ---------------------------------------------------------------- tabs: strings, script, problems

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs) RefreshTab();
    }

    private void RefreshTab()
    {
        if (_model is null) return;
        RefreshProblems();
        switch (Tabs.SelectedIndex)
        {
            case 2: RefreshKeys(); break;
            case 3: ScriptBox.Text = _model.ToScript(); break;
        }
    }

    private void RefreshProblems()
    {
        if (_model is null) return;
        var problems = _model.Check();
        ProblemList.ItemsSource = problems;
        ProblemsTitle.Text = problems.Count == 0 ? L.T("MenuBuilderPanel.NoProblemsFound") : L.T("MenuBuilderPanel.DoubleClickOneGo", L.T("Count.Problem", problems.Count));
        ((TabItem)Tabs.Items[4]).Header = problems.Count == 0 ? L.T("Common.Problems") : L.T("MenuBuilderPanel.Problems", problems.Count);
    }

    private void ProblemList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemList.SelectedItem is not MenuFile.Problem p || p.Group is null) return;
        GroupList.SelectedItem = p.Group;
        if (p.Var is not null) VarList.SelectedItem = p.Var;
        Tabs.SelectedIndex = 0;
    }

    // ---- strings

    private static string Prettify(string key)
    {
        foreach (string prefix in new[] { "preset_value_", "option_", "panel_", "preset_" })
            if (key.StartsWith(prefix, StringComparison.Ordinal)) { key = key[prefix.Length..]; break; }
        var words = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words);
    }

    private void RefreshKeys()
    {
        if (_model is null) return;
        var wanted = _model.RequiredKeys();
        // keep the rows already there, so what's being typed isn't lost
        var existing = _keys.ToDictionary(k => k.Key);
        _keys.Clear();
        foreach (var (key, use) in wanted)
        {
            if (!existing.TryGetValue(key, out var row))
            {
                string text = _model.Texts.TryGetValue(key, out var t) ? t : Prettify(key);
                row = new KeyRow(key, use, text, null);
                row.TextChanged += r => _model.Texts[r.Key] = r.Text;
            }
            row.ExistingId = _csvKeys.TryGetValue(key, out uint id) ? id : null;
            _keys.Add(row);
            _model.Texts[key] = row.Text;
        }
        KeyGrid.Items.Refresh();
        CsvText.Text = _csvPath is null ? L.T("MenuBuilderPanel.NoStringsCSVYet") : L.T("MenuBuilderPanel.CSVNotYet", _csvPath, L.T("Count.Key", _keys.Count(k => k.ExistingId is null)));
        if (FirstIdBox.Text.Length == 0) SuggestFirstId();
    }

    /// <summary>Looks for the mod's English strings CSV beside the menu file or in a strings folder next to it.</summary>
    private void FindCsv()
    {
        if (_xmlPath is null) return;
        string? dir = Path.GetDirectoryName(_xmlPath);
        if (dir is null) return;
        foreach (string folder in new[] { dir, Path.Combine(dir, "strings"), Path.Combine(dir, "..", "strings"), Path.Combine(dir, "..", "..", "strings") })
        {
            string path = Path.GetFullPath(Path.Combine(folder, "en.csv"));
            if (File.Exists(path) && StringsWindow.CanOpen(path))
            {
                ReadCsv(path);
                return;
            }
        }
    }

    private void ReadCsv(string path)
    {
        try
        {
            var csv = W3Strings.FromCsv(File.ReadAllText(path, Encoding.UTF8));
            _csvPath = path;
            _csvKeys = [];
            _csvMax = 0;
            foreach (var s in csv.Strings)
            {
                if (s.KeyName is not null) _csvKeys.TryAdd(s.KeyName, s.Id);
                _csvMax = Math.Max(_csvMax, s.Id);
                if (s.KeyName is not null && _model is not null) _model.Texts[s.KeyName] = s.Text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell(L.T("MenuBuilderPanel.CSVCouldNotRead"), ex.Message, MessageKind.Error);
        }
    }

    private void SuggestFirstId()
    {
        if (_csvMax > 0)
        {
            FirstIdBox.Text = (_csvMax + 1).ToString(CultureInfo.InvariantCulture);
            return;
        }
        FirstIdBox.Text = ((uint)Random.Shared.Next(2110000, 2120000) * 1000).ToString(CultureInfo.InvariantCulture);
        if (_gameFolder is not null) _ = FindFreeBlockAsync(quiet: true);
    }

    private async void FreeBlock_Click(object sender, RoutedEventArgs e) => await FindFreeBlockAsync(quiet: false);

    private async Task FindFreeBlockAsync(bool quiet)
    {
        string? folder = _gameFolder ?? GameLocator.Resolve(_xmlPath);
        if (folder is null)
        {
            if (!quiet) Tell(L.T("Common.GameFolderIsntSet"), L.T("MenuBuilderPanel.FindingUnusedIDsNeeds"));
            return;
        }
        try
        {
            var index = await GameStringIndex.GetAsync(folder, W3Strings.Language("en")!);
            uint block = index.FreeBlock();
            FirstIdBox.Text = block.ToString(CultureInfo.InvariantCulture);
            if (!quiet) Tell(L.T("MenuBuilderPanel.FreeBlock"), L.T("MenuBuilderPanel.IsntUsedGameDLC", block, block + 999));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!quiet) Tell(L.T("MenuBuilderPanel.GamesStringsCouldNot"), ex.Message, MessageKind.Error);
        }
    }

    private void LoadCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("MenuBuilderPanel.LoadTextsStringsCSV"),
            Filter = L.T("MenuBuilderPanel.StringsCSVCsvCsv"),
            InitialDirectory = Path.GetDirectoryName(_csvPath ?? _xmlPath) ?? "",
        };
        if (dialog.ShowDialog() != true) return;
        ReadCsv(dialog.FileName);
        // texts in the CSV win over the guesses
        _keys.Clear();
        RefreshKeys();
    }

    private string CsvLines(IEnumerable<KeyRow> rows, uint first)
    {
        var sb = new StringBuilder();
        uint id = first;
        foreach (var row in rows)
            sb.Append(id++).Append("||").Append(row.Key).Append('|').Append(row.Text.ReplaceLineEndings(" ")).Append('\n');
        return sb.ToString();
    }

    private bool TryFirstId(out uint first)
    {
        if (uint.TryParse(FirstIdBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out first)) return true;
        Tell(L.T("MenuBuilderPanel.FirstIDIsntNumber"), L.T("MenuBuilderPanel.TypeIDGiveFirst"), MessageKind.Warning);
        return false;
    }

    private void CopyCsv_Click(object sender, RoutedEventArgs e)
    {
        if (!TryFirstId(out uint first)) return;
        var missing = _keys.Where(k => k.ExistingId is null).ToList();
        if (missing.Count == 0)
        {
            Tell(L.T("MenuBuilderPanel.NothingCopy"), L.T("MenuBuilderPanel.CSVAlreadyHasEvery"));
            return;
        }
        try
        {
            Clipboard.SetText(CsvLines(missing, first));
            Tell(L.T("MenuBuilderPanel.Copied"), L.T("MenuBuilderPanel.CopiedID", L.T("Count.Line", missing.Count), first));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }

    private void AddToCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || !TryFirstId(out uint first)) return;
        var dialog = new SaveFileDialog
        {
            Title = L.T("MenuBuilderPanel.AddMenusStringsCSV"),
            Filter = L.T("MenuBuilderPanel.StringsCSVCsvCsv"),
            FileName = Path.GetFileName(_csvPath ?? "en.csv"),
            InitialDirectory = Path.GetDirectoryName(_csvPath ?? _xmlPath) ?? "",
            OverwritePrompt = false,
            CheckFileExists = false,
        };
        if (dialog.ShowDialog() != true) return;
        string path = dialog.FileName;

        try
        {
            string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
            var known = W3Strings.FromCsv(existing).Strings;
            var haveKeys = new HashSet<string>(known.Where(s => s.KeyName is not null).Select(s => s.KeyName!), StringComparer.Ordinal);
            var haveHashes = known.Where(s => s.KeyHash != 0).Select(s => s.KeyHash).ToHashSet();
            var used = known.Select(s => s.Id).ToHashSet();
            var rows = _keys.Where(k => !haveKeys.Contains(k.Key) && !haveHashes.Contains(W3Strings.HashKey(k.Key))).ToList();
            if (rows.Count == 0)
            {
                Tell(L.T("MenuBuilderPanel.NothingAdd"), L.T("MenuBuilderPanel.AlreadyHasEveryKey", Path.GetFileName(path)));
                ReadCsv(path);
                RefreshKeys();
                return;
            }

            string eol = existing.Contains("\r\n") ? "\r\n" : "\n";
            var sb = new StringBuilder(existing);
            if (existing.Length == 0)
                sb.Append(";meta[language=en]").Append(eol).Append("; id      |key(hex)|key(str)| text").Append(eol);
            else if (!existing.EndsWith('\n'))
                sb.Append(eol);
            sb.Append(L.T("MenuBuilderPanel.ModMenu")).Append(eol);
            uint id = first;
            int clashes = 0;
            foreach (var row in rows)
            {
                while (used.Contains(id)) { id++; clashes++; }
                sb.Append(id).Append("||").Append(row.Key).Append('|').Append(row.Text.ReplaceLineEndings(" ")).Append(eol);
                used.Add(id);
                id++;
            }
            FileSafety.CheckWrite(path);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            ReadCsv(path);
            _keys.Clear();
            RefreshKeys();
            Tell(L.T("MenuBuilderPanel.AddedCSV"), L.T("MenuBuilderPanel.AddedTo", L.T("Count.String", rows.Count), Path.GetFileName(path))
                + (clashes > 0 ? L.T("MenuBuilderPanel.SkippingIds", L.T("Count.ID", clashes)) : "")
                + L.T("MenuBuilderPanel.OpenInStringsEditor"));
            if (Window.GetWindow(this) is { } owner && StringsWindow.CanOpen(path)) StringsWindow.Open(path, owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell(L.T("MenuBuilderPanel.CSVCouldNotSaved"), ex.Message, MessageKind.Error);
        }
    }

    private void CopyScript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ScriptBox.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }
}
