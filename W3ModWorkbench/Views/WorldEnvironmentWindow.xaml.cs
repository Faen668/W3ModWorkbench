using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Curves;
using W3ModWorkbench.Core.Environments;
using W3ModWorkbench.Core.World;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// REDkit's World Environment tool for the 3D view: the weather system (a condition forced and how far it's blended
/// in, the weather cycle paused or running, the table opened, a change stabilised), the environment system (an
/// environment forced over everything, the curve tools), the fake day cycle, the editor's switches, the environments
/// mixed in now, every definition the game has, and the properties of the one picked at the time shown.
/// </summary>
public partial class WorldEnvironmentWindow : Window
{
    private readonly WorldView3D _view;
    private readonly ResourceLocator _locator;
    private readonly Func<string, byte[]?> _read;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<string> _definitions;
    private bool _filling;
    private EnvironmentFile? _shown;
    private string? _shownPath;
    private List<EnvironmentWindow.SettingRow> _rows = [];
    private EnvironmentWindow.GroupNode? _group;

    /// <summary>A folder or a definition in the Definitions tree.</summary>
    private sealed class DefinitionNode(string name, string? path)
    {
        public string Name { get; } = name;
        public string? Path { get; } = path;
        public string Icon => Path is null ? "\uE8B7" : "\uE706";
        public ObservableCollection<DefinitionNode> Children { get; } = [];
    }

    public WorldEnvironmentWindow(WorldView3D view, ResourceLocator locator, Func<string, byte[]?> read, Window owner)
    {
        InitializeComponent();
        Owner = owner;
        _view = view;
        _locator = locator;
        _read = read;
        _definitions = locator.AllPaths(".env").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Chart.EmptyText = L.T("WorldEnvironment.PickCurve");
        foreach (var (hour, text) in new[] { (2, "2:00"), (7, "7:00"), (12, "12:00"), (17, "17:00"), (22, "22:00") })
        {
            var mark = new TextBlock { Text = text, FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Left };
            DayMarks.Children.Add(mark);
            DayMarks.SizeChanged += (_, _) => mark.Margin = new Thickness(DayMarks.ActualWidth * hour / 24 - 12, 0, 0, 0);
        }
        MaxSlider.Value = 24;
        FillSwitches();
        FillWeathers();
        FillPresets();
        FillDefinitions("");
        ShowTime();
        Refresh();
        _view.LightingChanged += View_LightingChanged;
        _view.HourChanged += View_HourChanged;
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();
        Closed += (_, _) =>
        {
            _refresh.Stop();
            _view.LightingChanged -= View_LightingChanged;
            _view.HourChanged -= View_HourChanged;
        };
    }

    private void View_LightingChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void View_HourChanged(object? sender, float hour) => Dispatcher.BeginInvoke(ShowTime);

    // ---- the weather system ----

    private void FillWeathers()
    {
        _filling = true;
        WeatherBox.Items.Clear();
        WeatherBox.Items.Add(new ComboBoxItem { Content = L.T("WorldWindow.NoWeather"), Tag = null });
        var lighting = _view.Lighting;
        foreach (var weather in lighting?.Weathers ?? []) WeatherBox.Items.Add(new ComboBoxItem { Content = weather.Name, Tag = weather.Name });
        WeatherBox.SelectedItem = WeatherBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == lighting?.Weather) ?? WeatherBox.Items[0];
        WeatherTableButton.IsEnabled = lighting?.WeatherTable is not null;
        _filling = false;
    }

    private void WeatherBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || WeatherBox.SelectedItem is not ComboBoxItem item) return;
        // as REDkit's: the forced condition blends in over its blend time
        _view.ChangeWeather(item.Tag as string, instantly: false);
    }

    private void PauseCheck_Click(object sender, RoutedEventArgs e) => _view.WeatherPaused = PauseCheck.IsChecked == true;

    private void StabiliseButton_Click(object sender, RoutedEventArgs e) => _view.StabiliseWeather();

    private void WeatherTableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Lighting?.WeatherTable is not { } table || _read(table) is not { } data) return;
        var entry = _locator.Find(table);
        DataViewers.Open(table, entry?.Archive.Name ?? "game", data, _locator, this, entry?.Archive);
    }

    // ---- the environment system ----

    /// <summary>REDkit's Force preset: none, or one of the engine's own environments (engine\environments).</summary>
    private void FillPresets()
    {
        _filling = true;
        PresetBox.Items.Clear();
        PresetBox.Items.Add(new ComboBoxItem { Content = L.T("WorldEnvironment.PresetDefault"), Tag = null });
        foreach (var path in _definitions.Where(p => p.StartsWith(@"engine\environments\", StringComparison.OrdinalIgnoreCase)))
            PresetBox.Items.Add(new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(path), Tag = path, ToolTip = path });
        PresetBox.SelectedItem = PresetBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string?)i.Tag, _view.Lighting?.Preset,
            StringComparison.OrdinalIgnoreCase)) ?? PresetBox.Items[0];
        _filling = false;
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || PresetBox.SelectedItem is not ComboBoxItem item) return;
        _view.ForcePreset(item.Tag as string);
    }

    private void CurveToolsToggle_Click(object sender, RoutedEventArgs e) =>
        CurveTools.Visibility = CurveToolsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    // ---- the fake day cycle ----

    private void FakeDayCheck_Click(object sender, RoutedEventArgs e) => _view.FakeDayCycle = FakeDayCheck.IsChecked == true;

    private bool _settingTime;

    private void ShowTime()
    {
        _settingTime = true;
        DaySlider.Value = _view.Hour;
        if (!TimeBox.IsKeyboardFocusWithin) TimeBox.Text = Clock(_view.Hour);
        _settingTime = false;
        foreach (var row in _rows) row.At(_view.Hour / 24);
        Chart.Time = _view.Hour / 24;
    }

    private static string Clock(double hour)
    {
        // the end of the day stays 24:00 rather than wrapping to midnight
        if (hour >= 24) return "24:00:00";
        var time = TimeSpan.FromHours(((hour % 24) + 24) % 24);
        return time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private void DaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingTime) return;
        _view.Hour = (float)DaySlider.Value;
        ShowTime();
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TakeTime();
    }

    private void TimeBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => TakeTime();

    private void TakeTime()
    {
        if (TimeSpan.TryParse(TimeBox.Text, CultureInfo.InvariantCulture, out var time)) _view.Hour = (float)time.TotalHours;
        ShowTime();
    }

    private void Chart_TimePicked(object? sender, double day)
    {
        _view.Hour = (float)(day * 24);
        ShowTime();
    }

    // ---- the editor's switches ----

    private void FillSwitches()
    {
        InstantAdaptationCheck.IsChecked = _view.InstantAdaptation;
        TrajectoryCheck.IsChecked = _view.ShowTrajectory;
        ProbeInstantCheck.IsChecked = _view.EnvProbeInstantUpdate;
        ProbeUpdateCheck.IsChecked = _view.AllowEnvProbeUpdate;
        BloomCheck.IsChecked = _view.AllowBloom;
        ColorBalanceCheck.IsChecked = _view.AllowColorBalance;
        AntialiasingCheck.IsChecked = _view.AllowAntialiasing;
        FogCheck.IsChecked = _view.AllowGlobalFog;
        DepthOfFieldCheck.IsChecked = _view.AllowDepthOfField;
        SsaoCheck.IsChecked = _view.AllowSsao;
        CloudsShadowCheck.IsChecked = _view.AllowCloudsShadow;
        VignetteCheck.IsChecked = _view.AllowVignette;
        WaterCheck.IsChecked = _view.AllowWaterShader;
        CutsceneDofCheck.IsChecked = _view.ForceCutsceneDepthOfField;
        InteriorCheck.IsChecked = _view.Lighting?.Parameters == EnvironmentParameters.Interiors;
        CaveCheck.IsChecked = _view.Lighting?.Parameters == EnvironmentParameters.Caves;
        PauseCheck.IsChecked = _view.WeatherPaused;
        FakeDayCheck.IsChecked = _view.FakeDayCycle;
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        _view.InstantAdaptation = InstantAdaptationCheck.IsChecked == true;
        _view.ShowTrajectory = TrajectoryCheck.IsChecked == true;
        _view.EnvProbeInstantUpdate = ProbeInstantCheck.IsChecked == true;
        _view.AllowEnvProbeUpdate = ProbeUpdateCheck.IsChecked == true;
        _view.AllowBloom = BloomCheck.IsChecked == true;
        _view.AllowColorBalance = ColorBalanceCheck.IsChecked == true;
        _view.AllowAntialiasing = AntialiasingCheck.IsChecked == true;
        _view.AllowGlobalFog = FogCheck.IsChecked == true;
        _view.AllowDepthOfField = DepthOfFieldCheck.IsChecked == true;
        _view.AllowSsao = SsaoCheck.IsChecked == true;
        _view.AllowCloudsShadow = CloudsShadowCheck.IsChecked == true;
        _view.AllowVignette = VignetteCheck.IsChecked == true;
        _view.AllowWaterShader = WaterCheck.IsChecked == true;
        _view.ForceCutsceneDepthOfField = CutsceneDofCheck.IsChecked == true;
        _view.Redraw();
    }

    private void Parameters_Click(object sender, RoutedEventArgs e)
    {
        // REDkit has two switches; with both on, the caves' parameters win
        if (sender == CaveCheck && CaveCheck.IsChecked == true) InteriorCheck.IsChecked = false;
        if (sender == InteriorCheck && InteriorCheck.IsChecked == true) CaveCheck.IsChecked = false;
        _view.UseParameters(CaveCheck.IsChecked == true ? EnvironmentParameters.Caves
            : InteriorCheck.IsChecked == true ? EnvironmentParameters.Interiors : EnvironmentParameters.Outdoors);
        if (_shownPath is not null) ShowProperties(_shownPath);
    }

    // ---- the environments mixed in now, and the weather's state ----

    private void Refresh()
    {
        if (_view.Lighting is not { } lighting) return;
        var previous = lighting.PreviousWeather;
        CurrentWeatherText.Text = previous is null ? lighting.Weather ?? L.T("WorldWindow.NoWeather")
            : L.T("WorldEnvironment.Blending", previous, lighting.Weather ?? L.T("WorldWindow.NoWeather"), (int)(lighting.Transition * 100));
        WeatherProgress.Value = previous is null ? 1 : lighting.Transition;
        StabiliseButton.IsEnabled = previous is not null;
        var lines = lighting.Active.Select(a => (a, Text: a.Forced
            ? L.T("WorldEnvironment.ForcedLine", Path.GetFileNameWithoutExtension(a.Path))
            : $"[P{a.Priority}] {a.Weight * 100:0}% {Path.GetFileNameWithoutExtension(a.Path)}")).ToList();
        if (!lines.Select(l => l.Text).SequenceEqual(ActiveList.Items.OfType<ListBoxItem>().Select(i => (string)i.Content)))
        {
            string? picked = (ActiveList.SelectedItem as ListBoxItem)?.Tag as string;
            _filling = true;
            ActiveList.Items.Clear();
            foreach (var (a, text) in lines) ActiveList.Items.Add(new ListBoxItem { Content = text, Tag = a.Path, ToolTip = a.Path });
            ActiveList.SelectedItem = ActiveList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == picked);
            _filling = false;
            FillAreas(lighting);
        }
        if (_shownPath is null && lighting.Active.LastOrDefault(a => a.Weight > 0) is { Path: { } strongest }) ShowProperties(strongest);
        InfoText.Text = L.T("WorldEnvironment.Info", lighting.Environment ?? "", lighting.WeatherTable ?? "");
    }

    private void ActiveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || ActiveList.SelectedItem is not ListBoxItem { Tag: string path }) return;
        ShowProperties(path);
    }

    // ---- every definition ----

    private void FillDefinitions(string filter)
    {
        var root = new DefinitionNode("", null);
        foreach (var path in _definitions.Where(p => filter.Length == 0 || p.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            // under environment\definitions the folders REDkit shows; others keep their whole folder
            string shown = path.StartsWith(@"environment\definitions\", StringComparison.OrdinalIgnoreCase) ? path[@"environment\definitions\".Length..] : path;
            var parts = shown.Split('\\');
            var node = root;
            foreach (var folder in parts[..^1])
            {
                var child = node.Children.FirstOrDefault(c => c.Path is null && c.Name.Equals(folder, StringComparison.OrdinalIgnoreCase));
                if (child is null) node.Children.Add(child = new DefinitionNode(folder, null));
                node = child;
            }
            node.Children.Add(new DefinitionNode(Path.GetFileNameWithoutExtension(parts[^1]), path));
        }
        DefinitionTree.ItemsSource = root.Children;
    }

    private void DefinitionFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        DefinitionPlaceholder.Visibility = DefinitionFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FillDefinitions(DefinitionFilter.Text.Trim());
    }

    private void DefinitionTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DefinitionTree.SelectedItem is DefinitionNode { Path: { } path }) ShowProperties(path);
    }

    private void ForceDefinitionItem_Click(object sender, RoutedEventArgs e)
    {
        if (DefinitionTree.SelectedItem is not DefinitionNode { Path: { } path }) return;
        _view.ForcePreset(path);
        if (!PresetBox.Items.OfType<ComboBoxItem>().Any(i => string.Equals((string?)i.Tag, path, StringComparison.OrdinalIgnoreCase)))
            PresetBox.Items.Add(new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(path), Tag = path, ToolTip = path });
        _filling = true;
        PresetBox.SelectedItem = PresetBox.Items.OfType<ComboBoxItem>().First(i => string.Equals((string?)i.Tag, path, StringComparison.OrdinalIgnoreCase));
        _filling = false;
    }

    private void OpenDefinitionItem_Click(object sender, RoutedEventArgs e)
    {
        if (DefinitionTree.SelectedItem is not DefinitionNode { Path: { } path } || _read(path) is not { } data) return;
        var entry = _locator.Find(path);
        EnvironmentWindow.Open(path, entry?.Archive.Name ?? "game", data, _locator, this, entry?.Archive);
    }

    // ---- the properties of the environment picked ----

    private void ShowProperties(string path)
    {
        _shownPath = path;
        PropertiesSource.Text = path;
        try
        {
            if (_read(path) is not { } data) return;
            var parameters = _view.Lighting?.Parameters ?? EnvironmentParameters.Outdoors;
            _shown = EnvironmentFile.Read(data, parameters switch
            {
                EnvironmentParameters.Interiors => "envParamsInteriors",
                EnvironmentParameters.Caves => "envParamsCaves",
                _ => "envParams",
            });
        }
        catch (InvalidDataException ex)
        {
            PropertiesSource.Text = $"{path}   ·   {ex.Message}";
            return;
        }
        var nodes = new ObservableCollection<EnvironmentWindow.GroupNode>();
        var top = new EnvironmentWindow.GroupNode(_shown.Root) { Expanded = true };
        foreach (var group in _shown.Root.Groups.Where(g => g.All().Any(s => s.Set)).OrderBy(g => g.Title, StringComparer.CurrentCulture))
            top.Children.Add(Node(group));
        nodes.Add(top);
        GroupTree.ItemsSource = nodes;
        (top.Children.FirstOrDefault() ?? top).Selected = true;
    }

    private static EnvironmentWindow.GroupNode Node(EnvGroup group)
    {
        var node = new EnvironmentWindow.GroupNode(group);
        foreach (var child in group.Groups.Where(g => g.All().Any(s => s.Set))) node.Children.Add(Node(child));
        return node;
    }

    private void GroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (GroupTree.SelectedItem is not EnvironmentWindow.GroupNode node || _shown is null) return;
        _group = node;
        _rows = node.Group.Settings.Where(s => s.Set).Select(s => new EnvironmentWindow.SettingRow(s, _shown.File)).ToList();
        foreach (var row in _rows) row.At(_view.Hour / 24);
        SettingGrid.ItemsSource = _rows;
    }

    private void SettingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingGrid.SelectedItem is not EnvironmentWindow.SettingRow { Setting: { Curve: { } curve } setting })
        {
            Chart.Show([], true);
            CurveTitle.Text = CurveDetail.Text = "";
            return;
        }
        CurveTitle.Text = setting.Title;
        CurveDetail.Text = $"{setting.Path}   ·   {L.T("Count.Key", curve.Keys.Count)}";
        var series = curve.Kind switch
        {
            CurveKind.Color or CurveKind.ColorScaled => [Channel(curve, 0, "R"), Channel(curve, 1, "G"), Channel(curve, 2, "B")],
            CurveKind.Vector => [Channel(curve, 0, "X"), Channel(curve, 1, "Y"), Channel(curve, 2, "Z"), Channel(curve, 3, "W")],
            _ => new List<CurveSeries> { new(setting.Title, curve, 0, CurveChart.SeriesColor(3)) },
        };
        Chart.Show(series, day: true);
        Chart.Time = _view.Hour / 24;
    }

    private static CurveSeries Channel(RedCurve curve, int channel, string name) => new(name, curve, channel, CurveChart.SeriesColor(channel));

    // ---- the curve tools ----

    private void FillAreas(WorldLighting lighting)
    {
        _filling = true;
        string? picked = (AreaBox.SelectedItem as ComboBoxItem)?.Tag as string;
        AreaBox.Items.Clear();
        AreaBox.Items.Add(new ComboBoxItem { Content = L.T("WorldEnvironment.AreaAll"), Tag = null });
        foreach (var a in lighting.Active) AreaBox.Items.Add(new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(a.Path), Tag = a.Path, ToolTip = a.Path });
        AreaBox.SelectedItem = AreaBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == picked) ?? AreaBox.Items[0];
        _filling = false;
    }

    private void AreaBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || AreaBox.SelectedItem is not ComboBoxItem { Tag: string path }) return;
        ShowProperties(path);
    }

    private void Range_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinText is null || MaxText is null) return;
        MinText.Text = Clock(MinSlider.Value);
        MaxText.Text = Clock(MaxSlider.Value);
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        MinSlider.Value = 0;
        MaxSlider.Value = 24;
        SettingGrid.SelectedItem = null;
    }

    /// <summary>Copies the picked setting's keys between Min and Max (all the group's settings when none is picked) as text.</summary>
    private void CopyKeysButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingGrid.SelectedItem is EnvironmentWindow.SettingRow row ? [row.Setting] : _rows.Select(r => r.Setting).ToList();
        double from = Math.Min(MinSlider.Value, MaxSlider.Value) / 24, to = Math.Max(MinSlider.Value, MaxSlider.Value) / 24;
        var text = new StringBuilder();
        foreach (var setting in settings)
        {
            if (setting.Curve is not { } curve) continue;
            foreach (var key in curve.Keys.Where(k => k.Time >= from - 1e-6 && k.Time <= to + 1e-6))
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{setting.Path}\t{Clock(key.Time * 24)}\t{key.Value.X:0.####}\t{key.Value.Y:0.####}\t{key.Value.Z:0.####}\t{key.Value.W:0.####}"));
        }
        if (text.Length > 0) Clipboard.SetText(text.ToString());
    }
}
