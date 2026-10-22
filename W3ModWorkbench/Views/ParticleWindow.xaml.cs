using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Curves;
using W3ModWorkbench.Core.Particles;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// A particle system (.w2p) as REDkit's particle editor shows it: the effect playing (paused, restarted, slowed, at any
/// LOD), the picked module's values over a particle's life as curves, each emitter as a box of its modules in its own
/// colour with its texture (each emitter and module switched off and on at will, the preview following), and the
/// properties of the module, emitter, material and system. The game's cooked effects compile their modules away, so
/// those come from REDkit's own copy when it has one.
/// </summary>
public partial class ParticleWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private ParticleSystemFile? _system;
    private List<EmitterCard> _cards = [];
    private EmitterCard? _emitter;
    private ModuleRow? _module;
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private double _lastStatus;

    /// <summary>A module in an emitter's box: its name, switched on or off, picked or not.</summary>
    private sealed class ModuleRow(ParticleModule module, EmitterCard card, Action changed) : INotifyPropertyChanged
    {
        private bool _picked;
        public ParticleModule Module { get; } = module;
        public EmitterCard Card { get; } = card;
        public string Title => Module.Title;
        public bool Enabled
        {
            get => Module.Enabled;
            set
            {
                Module.Enabled = value;
                Changed(nameof(Enabled));
                Changed(nameof(TextOpacity));
                changed();
            }
        }
        public double TextOpacity => Module.Enabled ? 1 : 0.45;
        public bool Picked { get => _picked; set { _picked = value; Changed(nameof(Back)); } }
        public Brush Back => _picked ? PickedBrush : Brushes.Transparent;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static readonly Brush PickedBrush = Frozen(Color.FromArgb(0x55, 0x9A, 0x6B, 0xD8));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>An emitter's box: its name and colour, its texture, what draws it, its modules.</summary>
    private sealed class EmitterCard : INotifyPropertyChanged
    {
        private BitmapSource? _thumb;
        private bool _picked;
        private readonly Action _changed;
        public ParticleEmitter Emitter { get; }
        public List<ModuleRow> Modules { get; }
        public string Name => Emitter.Name;
        public Brush HeaderBrush { get; }
        public Brush Edge => _picked ? PickedBrush : Brushes.Transparent;
        public string Info { get; }
        public bool Showing
        {
            get => Emitter.Showing;
            set
            {
                Emitter.Showing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Showing)));
                _changed();
            }
        }
        public BitmapSource? Thumb { get => _thumb; set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); } }
        public bool Picked { get => _picked; set { _picked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Edge))); } }
        public event PropertyChangedEventHandler? PropertyChanged;

        public EmitterCard(ParticleEmitter emitter, Action changed)
        {
            Emitter = emitter;
            _changed = changed;
            var c = emitter.EditorColor;
            // the editor's colour, darkened enough for white text on it
            HeaderBrush = Frozen(Color.FromRgb((byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7)));
            Modules = emitter.Modules.Select(m => new ModuleRow(m, this, changed)).ToList();
            Info = string.Join("\n", new[]
            {
                ParticleSystemFile.ModuleTitle(emitter.Drawer),
                L.T("ParticleWindow.AtMost", emitter.MaxParticles),
                emitter.Additive ? L.T("ParticleWindow.Adds") : L.T("ParticleWindow.Covers"),
                emitter.Cooked ? L.T("ParticleWindow.Cooked") : "",
            }.Where(s => s.Length > 0));
        }
    }

    /// <summary>A value of a module, its evaluator in words.</summary>
    private sealed record ValueRow(string Name, ParticleEvaluator Evaluator)
    {
        public string Kind => Evaluator.ClassName.Replace("CEvaluator", "");
        public string Text => Evaluator.Describe();
    }

    /// <summary>A parameter of an emitter's material.</summary>
    private sealed record ParameterRow(string Name, string Type, string Text, string From, string? Link);

    private ParticleWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        MakeModBtn.Show(path, origin, readData);
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = L.T("ParticleWindow.ParticleSystem2", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        SpeedBox.ItemsSource = new[] { "×0.1", "×0.25", "×0.5", "×1", "×2" };
        SpeedBox.SelectedIndex = 3;
        Chart.EmptyText = L.T("ParticleWindow.PickModule");
        Loaded += async (_, _) => await LoadAsync();
    }

    public static bool CanShow(string name) => ParticleSystemFile.IsParticleSystem(name);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new ParticleWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new ParticleWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        PropertySource sourceKind = PropertySource.Own;
        string? redkitPath = null;
        bool fromRedkit = false;
        try
        {
            var (read, path, origin, locator) = (_readData, _path, _origin, _locator);
            (_system, sourceKind, redkitPath, fromRedkit) = await Task.Run(() =>
            {
                var system = ParticleSystemFile.Read(read(), Shaders(locator));
                var from = RedkitSource.For(path, origin);
                // cooked emitters keep their modules compiled: play REDkit's copy, which has them, when there is one
                if (system.Emitters.Any(e => e.Cooked) && from.Redkit is not null)
                {
                    try { return (ParticleSystemFile.Read(from.Redkit.Data, Shaders(locator)), from.Source, from.RedkitPath, true); }
                    catch (InvalidDataException) { }
                }
                return (system, from.Source, from.RedkitPath, false);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = L.T("ParticleWindow.CouldNotRead", ex.Message);
            return;
        }
        var system = _system;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        _cards = system.Emitters.OrderBy(e => e.GraphPosition.X).ThenBy(e => e.GraphPosition.Y).Select(e => new EmitterCard(e, Changed)).ToList();
        EmitterList.ItemsSource = _cards;
        EmittersTitle.Text = L.T("ParticleWindow.Emitters", system.Emitters.Count, system.Emitters.Sum(e => e.Modules.Count));
        LodBox.ItemsSource = system.LodDistances.Select((d, i) => L.T("ParticleWindow.LodFrom", i, d)).ToList();
        LodBox.SelectedIndex = 0;
        if (system.Background is { } back && back.A > 0) Preview.Background = back;
        SystemProperties.Show(PropertyRow.Of(system.Resource, system.File));
        SourceNoteBar.Show(fromRedkit ? PropertySource.Redkit : sourceKind, redkitPath ?? _origin, L.T("ParticleWindow.ParticleSystem3"));
        int cooked = system.Emitters.Count(e => e.Cooked);
        InfoText.Text = L.T("ParticleWindow.Info", _path, _source, system.File.Version, L.T("Count.Emitter", system.Emitters.Count),
            system.Emitters.Sum(e => e.Modules.Count), system.Prewarm,
            fromRedkit ? L.T("ParticleWindow.PlayedFromRedkit") : cooked > 0 ? L.T("ParticleWindow.CookedNoRedkit", cooked) : "");
        await Preview.ShowAsync(system, Texture);
        Preview.Fit();
        if (_cards.FirstOrDefault() is { } first) PickEmitter(first, first.Modules.FirstOrDefault(m => m.Module.Values.Any(v => v.Evaluator.Kind == EvaluatorKind.Curve)) ?? first.Modules.FirstOrDefault());
        foreach (var card in _cards)
            if (card.Emitter.Texture is { } texture) card.Thumb = await Texture(texture);
    }

    /// <summary>
    /// Reads a material's files: a shader graph from REDkit's own copy when it has one, as the game's cooked graphs leave
    /// out the parameters and blocks that say which texture and how many atlas frames; the rest as the game has them.
    /// </summary>
    private static Func<string, byte[]?> Shaders(ResourceLocator locator) => path =>
        path.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase) && RedkitSource.Read(path) is { } redkit ? redkit.File.Data : locator.ReadFile(path);

    private Task<BitmapSource?> Texture(string path)
    {
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task)) _textures[path] = task = Task.Run(() => MeshTextures.LoadWithAlpha(_locator, path, 512));
            return task;
        }
    }

    /// <summary>An emitter or module switched on or off: the preview starts again with it so.</summary>
    private void Changed() => Preview.Restart();

    private void PickEmitter(EmitterCard card, ModuleRow? module)
    {
        foreach (var c in _cards)
        {
            c.Picked = c == card;
            foreach (var m in c.Modules) m.Picked = m == module;
        }
        _emitter = card;
        _module = module;
        EmitterProperties.Show(PropertyRow.Of(card.Emitter.Object, _system!.File));
        EmitterTab.Header = L.T("ParticleWindow.EmitterNamed", card.Name);
        ShowMaterial(card.Emitter);
        ShowModule(module);
    }

    private void ShowModule(ModuleRow? module)
    {
        if (module is null)
        {
            ValueGrid.ItemsSource = null;
            ValueBox.ItemsSource = null;
            Chart.Show([], false);
            CurveTitle.Text = _emitter?.Name ?? "";
            CurveDetail.Text = "";
            return;
        }
        var values = module.Module.Values.Select(v => new ValueRow(v.Name, v.Evaluator)).ToList();
        ValueGrid.ItemsSource = values;
        ModuleTab.Header = L.T("ParticleWindow.ModuleNamed", module.Title);
        ValueBox.ItemsSource = values.Select(v => v.Name).ToList();
        ValueBox.Visibility = values.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ValueBox.SelectedIndex = Math.Max(0, values.FindIndex(v => v.Evaluator.Kind == EvaluatorKind.Curve));
        if (values.Count == 0)
        {
            CurveTitle.Text = module.Title;
            CurveDetail.Text = module.Card.Name;
            Chart.EmptyText = L.T("ParticleWindow.NoValues");
            Chart.Show([], false);
        }
    }

    /// <summary>A value of the module picked over a particle's life: a line for each of its axes, or the ends of a random one.</summary>
    private void ShowValue(ValueRow value)
    {
        var e = value.Evaluator;
        CurveTitle.Text = $"{_module?.Title} › {value.Name}";
        bool overLife = !(_module?.Module.IsInitializer ?? false);
        CurveDetail.Text = $"{_module?.Card.Name}   ·   {value.Kind}   ·   {value.Text}   ·   "
            + (overLife ? L.T("ParticleWindow.OverLife") : L.T("ParticleWindow.OverEmitter"));
        int axes = e.Channels == 1 ? 1 : e.IsColor ? 4 : e.FreeAxes;
        string[] names = e.IsColor ? ["R", "G", "B", "A"] : ["X", "Y", "Z", "W"];
        var series = new List<CurveSeries>();
        for (int axis = 0; axis < axes; axis++)
        {
            string name = e.Channels == 1 ? value.Name : names[axis];
            switch (e.Kind)
            {
                case EvaluatorKind.Curve when axis < Math.Max(1, e.Curves.Count) && e.Curves.Count > 0:
                    series.Add(new CurveSeries(name, e.Curves[Math.Min(axis, e.Curves.Count - 1)], 0, CurveChart.SeriesColor(e.Channels == 1 ? 3 : axis)));
                    break;
                case EvaluatorKind.Constant or EvaluatorKind.StartEnd:
                    series.Add(new CurveSeries(name, Line(Axis(e.A, axis), Axis(e.Kind == EvaluatorKind.Constant ? e.A : e.B, axis)), 0, CurveChart.SeriesColor(e.Channels == 1 ? 3 : axis)));
                    break;
                case EvaluatorKind.Random:
                    series.Add(new CurveSeries(L.T("ParticleWindow.Low", name), Line(Axis(e.A, axis), Axis(e.A, axis)), 0, CurveChart.SeriesColor(e.Channels == 1 ? 3 : axis)));
                    series.Add(new CurveSeries(L.T("ParticleWindow.High", name), Line(Axis(e.B, axis), Axis(e.B, axis)), 0, CurveChart.SeriesColor(e.Channels == 1 ? 3 : axis)));
                    break;
            }
        }
        Chart.EmptyText = L.T("ParticleWindow.PickModule");
        Chart.Show(series, false);
        Chart.Time = -1;
    }

    private static float Axis(System.Numerics.Vector4 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };

    /// <summary>A straight line from one value to another over 0 to 1, as a curve, for a constant or start-to-end value.</summary>
    private static RedCurve Line(float from, float to) => new()
    {
        Kind = CurveKind.Float,
        Keys = [new CurveKey(0, new(from, 0, 0, 0), default, default, CurveSegment.Interpolate, CurveSegment.Interpolate),
                new CurveKey(1, new(to, 0, 0, 0), default, default, CurveSegment.Interpolate, CurveSegment.Interpolate)],
    };

    private void ShowMaterial(ParticleEmitter emitter)
    {
        if (emitter.Material is null)
        {
            MaterialText.Text = L.T("ParticleWindow.NoMaterial");
            MaterialGrid.ItemsSource = null;
            return;
        }
        MaterialText.Text = L.T("ParticleWindow.MaterialText", emitter.Shader ?? "—", emitter.Additive ? L.T("ParticleWindow.Adds") : L.T("ParticleWindow.Covers"),
            emitter.Atlas.Columns, emitter.Atlas.Rows, emitter.ColorGroup ?? "—");
        string own = L.T("MeshMaterial.Mesh");
        MaterialGrid.ItemsSource = emitter.Parameters.Select(p => p with { From = p.From == own ? L.T("ParticleWindow.TheEmitter") : p.From }).Select(p => p.Value switch
        {
            Core.Meshes.TextureRef t => new ParameterRow(p.Name, p.Type, t.Path ?? "", p.From, t.Path),
            float f => new ParameterRow(p.Name, p.Type, f.ToString("0.###", CultureInfo.CurrentCulture), p.From, null),
            System.Numerics.Vector4 v => new ParameterRow(p.Name, p.Type, $"{v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}, {v.W:0.###}", p.From, null),
            var other => new ParameterRow(p.Name, p.Type, other?.ToString() ?? "", p.From, null),
        }).ToList();
    }

    private void EmitterHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EmitterCard card) PickEmitter(card, null);
    }

    private void Module_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModuleRow row) PickEmitter(row.Card, row);
    }

    private void ValueBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ValueGrid.ItemsSource is List<ValueRow> values && ValueBox.SelectedIndex >= 0 && ValueBox.SelectedIndex < values.Count)
        {
            ValueGrid.SelectedIndex = ValueBox.SelectedIndex;
            ShowValue(values[ValueBox.SelectedIndex]);
        }
    }

    private void ValueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ValueGrid.SelectedIndex >= 0 && ValueBox.SelectedIndex != ValueGrid.SelectedIndex) ValueBox.SelectedIndex = ValueGrid.SelectedIndex;
    }

    private void MaterialGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MaterialGrid.SelectedItem is ParameterRow { Link: { } link }) _ = DataViewers.OpenLinkAsync(link, _locator, this, this);
    }

    private void Preview_Ticked(object? sender, (float Time, int Particles) e)
    {
        if (Math.Abs(e.Time - _lastStatus) < 0.2 && Preview.IsPlaying) return;
        _lastStatus = e.Time;
        StatusText.Text = ParticlePreview.Status(e.Time, e.Particles) + "   ·   " + L.T("ParticleWindow.MouseHint");
    }

    private void TogglePlay()
    {
        Preview.IsPlaying = !Preview.IsPlaying;
        PlayIcon.Text = Preview.IsPlaying ? "" : "";
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void RestartButton_Click(object sender, RoutedEventArgs e) => Preview.Restart();

    private void FitButton_Click(object sender, RoutedEventArgs e) => Preview.Fit();

    private void GridCheck_Click(object sender, RoutedEventArgs e) => Preview.ShowGrid = GridCheck.IsChecked == true;

    private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Preview.Speed = SpeedBox.SelectedIndex switch { 0 => 0.1, 1 => 0.25, 2 => 0.5, 4 => 2, _ => 1 };

    private void LodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LodBox.SelectedIndex < 0) return;
        Preview.Lod = LodBox.SelectedIndex;
        Preview.Restart();
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen || e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.R:
                Preview.Restart();
                break;
            case Key.F:
                Preview.Fit();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
