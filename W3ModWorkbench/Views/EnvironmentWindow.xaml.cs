using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Curves;
using W3ModWorkbench.Core.Environments;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// An environment definition (.env) as REDkit's Environment System panel shows it, made easier to read: its groups of
/// settings as a tree (lit when turned on, dim when the file leaves them at the engine's defaults), the picked group's
/// settings with their values at a time of day, and the picked setting's curve over the 24 hours. A slider picks the
/// time, as REDkit's fake day cycle does, and the day can be played through; the sun, sky and fog colours then are
/// shown beside it.
/// </summary>
public partial class EnvironmentWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private EnvironmentFile? _env;
    private GroupNode? _group;
    private List<SettingRow> _rows = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(40) };

    /// <summary>A group in the tree: its settings and groups, how many it sets, and whether it's turned on.</summary>
    private sealed class GroupNode(EnvGroup group) : INotifyPropertyChanged
    {
        private bool _expanded, _selected;
        public EnvGroup Group { get; } = group;
        public string Title => Group.Title;
        public ObservableCollection<GroupNode> Children { get; } = [];
        public string CountText
        {
            get
            {
                var all = Group.All().ToList();
                return $"{all.Count(s => s.Set)}/{all.Count}";
            }
        }
        public double Opacity => Group.Set ? 1 : 0.5;
        public Brush StateBrush => Group.Activated switch
        {
            true => Brushes.MediumSeaGreen,
            false => Brushes.IndianRed,
            _ => Brushes.Transparent,
        };
        public string? StateText => Group.Activated switch
        {
            true => L.T("EnvironmentWindow.TurnedOn"),
            false => L.T("EnvironmentWindow.TurnedOff"),
            _ => null,
        };
        public bool Expanded { get => _expanded; set { _expanded = value; Changed(nameof(Expanded)); } }
        public bool Selected { get => _selected; set { _selected = value; Changed(nameof(Selected)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A setting in the grid, with its value at the time shown.</summary>
    private sealed class SettingRow(EnvSetting setting, Cr2wFile file) : INotifyPropertyChanged
    {
        public EnvSetting Setting { get; } = setting;
        public string Title => Setting.Title;
        public string TypeText => Setting.Curve is { } c ? L.T("EnvironmentWindow.CurveOf", c.Kind) : Setting.Type;
        public string KeysText => Setting.Curve is { } c ? c.Keys.Count.ToString(CultureInfo.CurrentCulture) : "";
        public double Opacity => Setting.Set ? 1 : 0.5;
        public string ValueText { get; private set; } = "";
        public Brush? Swatch { get; private set; }
        public Visibility SwatchVisibility => Swatch is null ? Visibility.Collapsed : Visibility.Visible;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void At(double time)
        {
            (ValueText, Swatch) = Describe(Setting, time, file);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Swatch)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SwatchVisibility)));
        }
    }

    private EnvironmentWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        MakeModBtn.Show(path, origin, readData);
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = L.T("EnvironmentWindow.Environment2", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        Chart.EmptyText = L.T("EnvironmentWindow.PickCurve");
        _clock.Tick += (_, _) => TimeSlider.Value = (TimeSlider.Value + 1 / 360.0) % 1;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _clock.Stop();
    }

    public static bool CanShow(string name) => EnvironmentFile.IsEnvironment(name);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new EnvironmentWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new EnvironmentWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        try
        {
            var read = _readData;
            _env = await Task.Run(() => EnvironmentFile.Read(read()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = L.T("EnvironmentWindow.CouldNotRead", ex.Message);
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        var env = _env;
        ShowTree();
        var all = env.Root.All().ToList();
        InfoText.Text = L.T("EnvironmentWindow.Info", _path, _source, env.File.Version, L.T("Count.Group", env.Root.Groups.Count),
            all.Count(s => s.Set), all.Count, all.Count(s => s.Set && s.Curve is not null));
        FileProperties.Show(PropertyRow.Of(env.Resource, env.File));
        _ = LoadRedkitAsync();
        TimeSlider.Value = 0.5;
        ShowTime();
    }

    /// <summary>The groups as a tree, those the file sets nothing in left out unless asked for, filtered by the box.</summary>
    private void ShowTree()
    {
        if (_env is null) return;
        bool setOnly = SetOnlyCheck.IsChecked == true;
        string filter = FilterBox.Text.Trim();
        GroupNode? Node(EnvGroup group)
        {
            if (setOnly && !group.Set) return null;
            var node = new GroupNode(group);
            foreach (var child in group.Groups.Select(Node).OfType<GroupNode>()) node.Children.Add(child);
            bool matches = filter.Length == 0 || group.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || group.Settings.Any(s => (!setOnly || s.Set) && (s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
            if (!matches && node.Children.Count == 0) return null;
            node.Expanded = filter.Length > 0;
            return node;
        }
        var roots = _env.Root.Groups.Select(Node).OfType<GroupNode>().ToList();
        GroupTree.ItemsSource = roots;
        GroupsTab.Header = L.T("EnvironmentWindow.Groups2", roots.Count);
        var first = _group is { } g ? Find(roots, g.Group) : null;
        if ((first ?? roots.FirstOrDefault()) is { } pick) pick.Selected = true;
    }

    private static GroupNode? Find(IEnumerable<GroupNode> nodes, EnvGroup group)
    {
        foreach (var node in nodes)
        {
            if (node.Group == group) return node;
            if (Find(node.Children, group) is { } found)
            {
                node.Expanded = true;
                return found;
            }
        }
        return null;
    }

    private void GroupTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not GroupNode node || _env is null) return;
        _group = node;
        bool setOnly = SetOnlyCheck.IsChecked == true;
        string filter = FilterBox.Text.Trim();
        _rows = node.Group.Settings
            .Where(s => !setOnly || s.Set)
            .Where(s => filter.Length == 0 || node.Group.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(s => new SettingRow(s, _env.File)).ToList();
        foreach (var row in _rows) row.At(TimeSlider.Value);
        SettingGrid.ItemsSource = _rows;
        GroupTitle.Text = L.T("EnvironmentWindow.GroupTitle", node.Group.Title, node.Group.Name, node.Group.Type,
            node.Group.Activated switch { true => L.T("EnvironmentWindow.TurnedOn"), false => L.T("EnvironmentWindow.TurnedOff"), _ => "" });
        SettingGrid.SelectedItem = _rows.FirstOrDefault(r => r.Setting.Curve is { Keys.Count: > 0 }) ?? _rows.FirstOrDefault();
    }

    private void SettingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingGrid.SelectedItem is not SettingRow row)
        {
            Chart.Show([], true);
            CurveTitle.Text = CurveDetail.Text = "";
            return;
        }
        var setting = row.Setting;
        CurveTitle.Text = setting.Title;
        if (setting.Curve is not { } curve)
        {
            Chart.Show([], true);
            CurveDetail.Text = $"{setting.Path}   ·   {setting.Type}";
            Chart.EmptyText = setting.Set ? L.T("EnvironmentWindow.NotCurve", row.ValueText) : L.T("EnvironmentWindow.NotSetDefault");
            return;
        }
        Chart.EmptyText = curve.Keys.Count == 0 ? L.T("EnvironmentWindow.CurveNoKeys") : L.T("EnvironmentWindow.PickCurve");
        var series = curve.Kind switch
        {
            CurveKind.Color => [Channel(curve, 0, "R"), Channel(curve, 1, "G"), Channel(curve, 2, "B")],
            CurveKind.ColorScaled => [Channel(curve, 0, "R"), Channel(curve, 1, "G"), Channel(curve, 2, "B")],
            CurveKind.Vector => [Channel(curve, 0, "X"), Channel(curve, 1, "Y"), Channel(curve, 2, "Z"), Channel(curve, 3, "W")],
            _ => new List<CurveSeries> { new(setting.Title, curve, 0, CurveChart.SeriesColor(3)) },
        };
        Chart.Show(series, day: true);
        Chart.Time = TimeSlider.Value;
        string scale = curve.Kind == CurveKind.ColorScaled && curve.Keys.Count > 0
            ? "   ·   " + L.T("EnvironmentWindow.Scale", curve.Keys.Min(k => k.Value.W), curve.Keys.Max(k => k.Value.W)) : "";
        CurveDetail.Text = $"{setting.Path}   ·   {L.T("EnvironmentWindow.CurveOf", curve.Kind)}   ·   {L.T("Count.Key", curve.Keys.Count)}{scale}";
    }

    private static CurveSeries Channel(RedCurve curve, int channel, string name) => new(name, curve, channel, CurveChart.SeriesColor(channel));

    /// <summary>A setting's value at a time in words, with a swatch for a colour.</summary>
    private static (string Text, Brush? Swatch) Describe(EnvSetting setting, double time, Cr2wFile file)
    {
        if (!setting.Set) return (L.T("EnvironmentWindow.EngineDefault"), null);
        if (setting.Curve is { } curve)
        {
            if (curve.Keys.Count == 0) return (L.T("EnvironmentWindow.NoKeys"), null);
            var v = curve.Evaluate((float)time);
            switch (curve.Kind)
            {
                case CurveKind.Color or CurveKind.ColorScaled:
                    var brush = new SolidColorBrush(Color.FromRgb(Byte(v.X), Byte(v.Y), Byte(v.Z)));
                    brush.Freeze();
                    return ($"{v.X:0} {v.Y:0} {v.Z:0}" + (curve.Kind == CurveKind.ColorScaled ? $"   ×{v.W:0.###}" : $"   α {v.W:0.##}"), brush);
                case CurveKind.Vector:
                    return ($"{v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}, {v.W:0.###}", null);
                default:
                    return (v.X.ToString("0.####", CultureInfo.CurrentCulture), null);
            }
        }
        return (setting.Value switch
        {
            bool b => b ? L.T("EnvironmentWindow.Yes") : L.T("EnvironmentWindow.No"),
            float f => f.ToString("0.####", CultureInfo.CurrentCulture),
            RedHandle h => file.PathOf(h) ?? "",
            RedEnum e => e.ToString(),
            RedObject o => string.Join(", ", o.Properties.Select(p => $"{p.Name} {p.Value}")),
            null => "",
            var other => other.ToString() ?? "",
        }, null);
    }

    private static byte Byte(float v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ShowTime();

    /// <summary>Everything at the time picked: the clock, the colours beside it, the grid's values and the chart's line.</summary>
    private void ShowTime()
    {
        double time = TimeSlider.Value;
        ClockText.Text = CurveChart.Clock(time);
        Chart.Time = time;
        foreach (var row in _rows) row.At(time);
        if (_env is null) return;
        Swatch(SunSwatch, "m_globalLight", "sunColor", L.T("EnvironmentWindow.Sun"), time);
        Swatch(SkySwatch, "m_sky", "skyColor", L.T("EnvironmentWindow.Sky"), time);
        Swatch(FogSwatch, "m_globalFog", "fogColorMiddle", L.T("EnvironmentWindow.Fog"), time);
    }

    private void Swatch(Border swatch, string group, string name, string what, double time)
    {
        var setting = _env!.Root.Groups.FirstOrDefault(g => g.Name == group)?.Settings.FirstOrDefault(s => s.Name == name);
        if (setting is { Set: true, Curve: { Keys.Count: > 0 } curve })
        {
            var v = curve.Evaluate((float)time);
            swatch.Background = new SolidColorBrush(Color.FromRgb(Byte(v.X), Byte(v.Y), Byte(v.Z)));
            swatch.ToolTip = L.T("EnvironmentWindow.SwatchTip", what, CurveChart.Clock(time), $"{v.X:0} {v.Y:0} {v.Z:0}");
        }
        else
        {
            swatch.Background = Brushes.Transparent;
            swatch.ToolTip = L.T("EnvironmentWindow.SwatchNotSet", what);
        }
    }

    private void Chart_TimePicked(object? sender, double time) => TimeSlider.Value = time;

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_clock.IsEnabled) _clock.Stop(); else _clock.Start();
        PlayIcon.Text = _clock.IsEnabled ? "" : "";
    }

    private void SetOnlyCheck_Click(object sender, RoutedEventArgs e) => ShowTree();

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTree();
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (SettingGrid.SelectedItem is SettingRow row) Copy(row.ValueText);
    }

    private void CopyKeys_Click(object sender, RoutedEventArgs e)
    {
        if (SettingGrid.SelectedItem is not SettingRow { Setting.Curve: { } curve }) return;
        Copy(string.Join(Environment.NewLine, curve.Keys.Select(k => curve.Kind == CurveKind.Float
            ? $"{CurveChart.Clock(k.Time)}\t{k.Value.X.ToString(CultureInfo.InvariantCulture)}"
            : $"{CurveChart.Clock(k.Time)}\t{k.Value.X:0.###}\t{k.Value.Y:0.###}\t{k.Value.Z:0.###}\t{k.Value.W:0.###}")));
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SettingGrid.SelectedItem is SettingRow row) Copy(row.Setting.Path);
    }

    /// <summary>Shows REDkit's own copy's settings when there is one, and says where they come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit?.First("CEnvironmentDefinition") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, L.T("EnvironmentWindow.Environment3"));
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            LeftTabs.SelectedItem = GroupsTab;
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.Space)
        {
            TogglePlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
