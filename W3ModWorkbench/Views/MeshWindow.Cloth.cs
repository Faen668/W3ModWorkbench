using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// The mesh viewer's Apex Cloth tab, for a cloth resource: its simulation settings as REDkit's Apex Cloth tab shows them,
/// which can be changed to see what they do in a live preview (the resource itself isn't changed), and what the
/// simulation is made of: each level of detail's particles, constraints and tethers, and the spheres and capsules it
/// collides with. The preview simulates the cloth as the game does, hanging from its bones as bound; it can be swayed
/// or spun to see it swing, and blown by a wind.
/// </summary>
public partial class MeshWindow
{
    private ClothRig? _clothRig;
    private ClothMaterial? _clothMaterial;
    private float _clothThickness;
    // the settings as the resource has them saved, to tell whether they've been changed
    private ClothMaterial? _clothSavedMaterial;
    private float _clothSavedThickness;
    private bool _simulating;
    private float _clothTime;
    private TimeSpan _lastRender;
    private int _clothMotion;
    private double _windYaw, _windPitch, _windStrength;
    private TextBlock? _clothStatus;
    private Button? _simulateButton;
    private readonly List<Action> _clothRefresh = [];

    /// <summary>Whether it's showing a cloth resource, which F1 opens the cloth page for.</summary>
    public bool IsCloth => ClothFile.IsCloth(_path);

    /// <summary>The motions the preview can put the cloth through, by the key of their name.</summary>
    private static readonly string[] Motions = ["MeshWindow.ClothStill", "MeshWindow.ClothSway", "MeshWindow.ClothSpin", "MeshWindow.ClothWalk"];

    /// <summary>Fills the Apex Cloth tab for a cloth resource, and starts its preview; for a mesh, keeps the tab hidden.</summary>
    private void ShowCloth()
    {
        if (_mesh?.Cloth is not { } cloth) return;
        ClothTab.Visibility = Visibility.Visible;
        _clothRig = ClothRig.For([_mesh], always: true);
        _clothMaterial = _clothSavedMaterial = cloth.Material;
        _clothThickness = _clothSavedThickness = cloth.Thickness;
        BuildClothPanel(cloth);
        Closed += (_, _) => StopSimulating();
        SetSimulating(true);
    }

    private void BuildClothPanel(ClothAsset cloth)
    {
        ClothPanel.Children.Clear();
        _clothRefresh.Clear();
        var m = _clothMaterial!;

        // the preview: play or pause it, start it over, move it, blow on it
        var preview = Section(L.T("MeshWindow.ClothPreview"), L.T("MeshWindow.ClothPreviewHint"));
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        _simulateButton = new Button { Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("MeshWindow.ClothSimulateTip") };
        _simulateButton.Click += (_, _) => SetSimulating(!_simulating);
        var reset = new Button { Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0), Content = IconLabel("", L.T("MeshWindow.ClothReset")), ToolTip = L.T("MeshWindow.ClothResetTip") };
        reset.Click += (_, _) => RestartCloth();
        var motion = new ComboBox { MinWidth = 120, ToolTip = L.T("MeshWindow.ClothMotionTip"), ItemsSource = Motions.Select(k => L.T(k)).ToList(), SelectedIndex = _clothMotion };
        motion.SelectionChanged += (_, _) => _clothMotion = Math.Max(0, motion.SelectedIndex);
        buttons.Children.Add(_simulateButton);
        buttons.Children.Add(reset);
        buttons.Children.Add(new TextBlock { Text = L.T("MeshWindow.ClothMotion"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), Opacity = 0.75 });
        buttons.Children.Add(motion);
        preview.Children.Add(buttons);
        preview.Children.Add(Slider(L.T("MeshWindow.ClothWindYaw"), -180, 180, () => _windYaw, v => _windYaw = v, "0", 0));
        preview.Children.Add(Slider(L.T("MeshWindow.ClothWindPitch"), -90, 90, () => _windPitch, v => _windPitch = v, "0", 0));
        preview.Children.Add(Slider(L.T("MeshWindow.ClothWindStrength"), 0, 30, () => _windStrength, v => _windStrength = v, "0.0", 0));
        _clothStatus = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        preview.Children.Add(_clothStatus);
        UpdateSimulateButton();

        // the settings REDkit's Apex Cloth tab has; changed here they show in the preview only
        var simulation = Section(L.T("MeshWindow.ClothSimulation"), "");
        simulation.Children.Add(Slider(L.T("MeshWindow.ClothThickness"), 0, 0.1, () => _clothThickness, v => { _clothThickness = (float)v; ApplyCloth(); }, "0.000", cloth.Thickness));
        simulation.Children.Add(Fact(L.T("MeshWindow.ClothCcd"), cloth.DisableCcd ? L.T("MeshWindow.ClothNo") : L.T("MeshWindow.ClothYes"), L.T("MeshWindow.ClothNotSimulated")));
        simulation.Children.Add(Fact(L.T("MeshWindow.ClothVirtualParticles"), $"{cloth.VirtualParticleDensity:0.##}", L.T("MeshWindow.ClothNotSimulated")));
        simulation.Children.Add(Fact(L.T("MeshWindow.ClothGravityDirection"), $"{cloth.GravityDirection.X:0.##}, {cloth.GravityDirection.Y:0.##}, {cloth.GravityDirection.Z:0.##}", ""));

        var material = Section(L.T("MeshWindow.ClothMaterial"), m.Name.Length > 0 ? L.T("MeshWindow.ClothMaterialName", m.Name) : "");
        var d = cloth.Material;
        void Add(string key, double min, double max, Func<ClothMaterial, float> get, Func<ClothMaterial, float, ClothMaterial> set, string format = "0.00") =>
            material.Children.Add(Slider(L.T(key), min, max, () => get(_clothMaterial!), v => { _clothMaterial = set(_clothMaterial!, (float)v); ApplyCloth(); }, format, get(d)));
        // one only the APEX asset keeps, which the resource has no property for: changed here, it shows in the preview only
        void AddPreview(string key, double min, double max, Func<ClothMaterial, float> get, Func<ClothMaterial, float, ClothMaterial> set, string format = "0.00") =>
            material.Children.Add(Slider(L.T(key), min, max, () => get(_clothMaterial!), v => { _clothMaterial = set(_clothMaterial!, (float)v); ApplyCloth(); }, format, get(d), previewOnly: true));
        Add("MeshWindow.ClothGravityScale", 0, 3, x => x.GravityScale, (x, v) => x with { GravityScale = v });
        Add("MeshWindow.ClothFriction", 0, 1, x => x.Friction, (x, v) => x with { Friction = v });
        AddPreview("MeshWindow.ClothVertical", 0, 1, x => x.VerticalStretching, (x, v) => x with { VerticalStretching = v });
        AddPreview("MeshWindow.ClothHorizontal", 0, 1, x => x.HorizontalStretching, (x, v) => x with { HorizontalStretching = v });
        Add("MeshWindow.ClothBending", 0, 1, x => x.Bending, (x, v) => x with { Bending = v });
        Add("MeshWindow.ClothShearing", 0, 1, x => x.Shearing, (x, v) => x with { Shearing = v });
        Add("MeshWindow.ClothTetherStiffness", 0, 1, x => x.TetherStiffness, (x, v) => x with { TetherStiffness = v });
        Add("MeshWindow.ClothTetherLimit", 1, 2, x => x.TetherLimit, (x, v) => x with { TetherLimit = v });
        Add("MeshWindow.ClothDamping", 0, 0.99, x => x.Damping, (x, v) => x with { Damping = v });
        Add("MeshWindow.ClothDrag", 0, 0.99, x => x.Drag, (x, v) => x with { Drag = v });
        Add("MeshWindow.ClothInertia", 0, 1, x => x.InertiaScale, (x, v) => x with { InertiaScale = v });
        Add("MeshWindow.ClothMassScale", 0, 100, x => x.MassScale, (x, v) => x with { MassScale = v }, "0.0");
        Add("MeshWindow.ClothMaxDistanceBias", -1, 1, x => x.MaxDistanceBias, (x, v) => x with { MaxDistanceBias = v });
        AddPreview("MeshWindow.ClothStiffnessFrequency", 10, 500, x => x.StiffnessFrequency, (x, v) => x with { StiffnessFrequency = v }, "0");
        AddPreview("MeshWindow.ClothSolverFrequency", 30, 500, x => x.SolverFrequency, (x, v) => x with { SolverFrequency = v }, "0");
        material.Children.Add(Fact(L.T("MeshWindow.ClothAverageEdge"), $"{cloth.Lods[0].Physical.AverageEdgeLength:0.0000} m", ""));
        material.Children.Add(Fact(L.T("MeshWindow.ClothHardStretch"), $"{d.HardStretchLimitation:0.##}", L.T("MeshWindow.ClothNotSimulated")));
        material.Children.Add(Fact(L.T("MeshWindow.ClothComDamping"), d.ComDamping ? L.T("MeshWindow.ClothYes") : L.T("MeshWindow.ClothNo"), L.T("MeshWindow.ClothNotSimulated")));
        material.Children.Add(Fact(L.T("MeshWindow.ClothSelfCollision"), L.T("MeshWindow.ClothSelfCollisionValue", d.SelfcollisionThickness, d.SelfcollisionStiffness), L.T("MeshWindow.ClothNotSimulated")));
        var restore = new Button { Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 10, 0, 2), HorizontalAlignment = HorizontalAlignment.Left,
            Content = L.T("MeshWindow.ClothRestoreDefaults"), ToolTip = L.T("MeshWindow.ClothRestoreDefaultsTip") };
        restore.Click += (_, _) =>
        {
            _clothMaterial = cloth.Material;
            _clothThickness = cloth.Thickness;
            ApplyCloth();
            foreach (var refresh in _clothRefresh) refresh();
        };
        material.Children.Add(restore);

        // what the simulation is made of
        var made = Section(L.T("MeshWindow.ClothMadeOf"), L.T("MeshWindow.ClothMadeOfHint"));
        var rows = new List<string[]>();
        for (int lod = 0; lod < cloth.Lods.Count; lod++)
        {
            var l = cloth.Lods[lod];
            var f = l.Fabric;
            int Kind(int kind) => f is null ? 0 : Enumerable.Range(0, f.PhaseTypes.Length).Where(p => f.PhaseTypes[p] == kind)
                .Sum(p => f.SetStarts[f.PhaseSets[p] + 1] - f.SetStarts[f.PhaseSets[p]]);
            int particles = f?.Particles ?? 0;
            rows.Add([
                $"{lod}",
                $"{particles:N0}",
                $"{l.Physical.MaxDistance.Take(particles).Count(x => x <= 0):N0}",
                $"{Kind(1) + Kind(2):N0}",
                $"{Kind(3):N0}",
                $"{Kind(4):N0}",
                f is null || particles == 0 ? "0" : $"{f.TetherAnchors.Length / particles}",
                $"{l.Physical.BackstopRadius.Take(particles).Count(r => r > 0):N0}",
                $"{l.Physical.MaxDistance.DefaultIfEmpty(0).Max():0.00} m",
            ]);
        }
        made.Children.Add(Table([L.T("MeshWindow.ClothLod"), L.T("MeshWindow.ClothParticles"), L.T("MeshWindow.ClothHeld"), L.T("MeshWindow.ClothStretch"),
            L.T("MeshWindow.ClothBendingShort"), L.T("MeshWindow.ClothShearingShort"), L.T("MeshWindow.ClothTethers"), L.T("MeshWindow.ClothBackstops"), L.T("MeshWindow.ClothFurthest")], rows));
        var shapes = ShapeRows(cloth);
        made.Children.Add(Heading(L.T("MeshWindow.ClothCollidesWith"), shapes.Count == 0 ? L.T("MeshWindow.ClothCollidesNothing") : L.T("MeshWindow.ClothShapes", cloth.Spheres.Length, cloth.Capsules.Length / 2)));
        if (shapes.Count > 0) made.Children.Add(Table([L.T("Common.Kind"), L.T("Common.Bones"), L.T("MeshWindow.ClothRadius")], shapes));
    }

    /// <summary>
    /// The Collision tab for a cloth resource: it has no collision of its own (its entry in the collision cache is its
    /// APEX asset), but collides with the spheres and capsules on its bones, which Show › Collision draws.
    /// </summary>
    private void ShowClothCollision(ClothAsset cloth)
    {
        CollisionPanel.Children.Clear();
        CollisionPanel.Children.Add(Lines(L.T("MeshWindow.ClothCollisionAbout")));
        CollisionPanel.Children.Add(Heading(L.T("MeshWindow.ClothCollidesWith"), cloth.Spheres.Length == 0 ? L.T("MeshWindow.ClothCollidesNothing")
            : L.T("MeshWindow.ClothShapes", cloth.Spheres.Length, cloth.Capsules.Length / 2)));
        if (cloth.Spheres.Length > 0) CollisionPanel.Children.Add(Table([L.T("Common.Kind"), L.T("Common.Bones"), L.T("MeshWindow.ClothRadius")], ShapeRows(cloth)));
        CollisionItem.IsEnabled = cloth.Spheres.Length > 0;
        _collision = null;
        ShowInfo();
    }

    /// <summary>The spheres and capsules a cloth collides with, as rows: their kind, their bones and their radius.</summary>
    private List<string[]> ShapeRows(ClothAsset cloth)
    {
        var bones = _mesh!.BoneNames;
        string Bone(int b) => b >= 0 && b < bones.Length ? bones[b] : $"{b}";
        var rows = new List<string[]>();
        for (int k = 0; k + 1 < cloth.Capsules.Length; k += 2)
        {
            var (a, b) = (cloth.Spheres[cloth.Capsules[k]], cloth.Spheres[cloth.Capsules[k + 1]]);
            rows.Add([L.T("MeshWindow.ClothCapsule"), a.Bone == b.Bone ? Bone(a.Bone) : $"{Bone(a.Bone)} – {Bone(b.Bone)}", $"{a.Radius:0.000} – {b.Radius:0.000} m"]);
        }
        var inCapsule = new HashSet<int>(cloth.Capsules);
        for (int s = 0; s < cloth.Spheres.Length; s++)
            if (!inCapsule.Contains(s)) rows.Add([L.T("MeshWindow.ClothSphere"), Bone(cloth.Spheres[s].Bone), $"{cloth.Spheres[s].Radius:0.000} m"]);
        return rows;
    }

    /// <summary>Draws the cloth's collision spheres and capsules where they are now (with the cloth's thickness), or takes them away.</summary>
    private void ShowClothShapes(bool on)
    {
        if (!on || _clothRig?.Of(0).FirstOrDefault(s => s is { Started: true }) is not { } sim || _mesh?.Cloth is not { } cloth)
        {
            View.ShowCollision([]);
            return;
        }
        var spheres = sim.Spheres.ToArray();
        var shapes = new List<(Vector3[], int[])>();
        foreach (var s in spheres) shapes.Add(Ball(new Vector3(s.X, s.Y, s.Z), s.W));
        for (int k = 0; k + 1 < cloth.Capsules.Length; k += 2)
        {
            var (a, b) = (spheres[cloth.Capsules[k]], spheres[cloth.Capsules[k + 1]]);
            shapes.Add(Tube(new Vector3(a.X, a.Y, a.Z), a.W, new Vector3(b.X, b.Y, b.Z), b.W));
        }
        View.ShowCollision(shapes);
    }

    /// <summary>A sphere as triangles, in rings and segments.</summary>
    private static (Vector3[], int[]) Ball(Vector3 centre, float radius)
    {
        const int rings = 8, segments = 12;
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int r = 0; r <= rings; r++)
        {
            double up = Math.PI * r / rings;
            for (int s = 0; s <= segments; s++)
            {
                double round = 2 * Math.PI * s / segments;
                vertices.Add(centre + radius * new Vector3((float)(Math.Sin(up) * Math.Cos(round)), (float)(Math.Sin(up) * Math.Sin(round)), (float)Math.Cos(up)));
            }
        }
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int a = r * (segments + 1) + s, b = a + segments + 1;
                triangles.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        return ([.. vertices], [.. triangles]);
    }

    /// <summary>The side of a capsule: a tube from one sphere to the other, narrowing from one's radius to the other's.</summary>
    private static (Vector3[], int[]) Tube(Vector3 from, float fromRadius, Vector3 to, float toRadius)
    {
        const int segments = 12;
        var axis = to - from;
        if (axis.LengthSquared() < 1e-10f) return ([], []);
        axis = Vector3.Normalize(axis);
        var side = Vector3.Normalize(Vector3.Cross(axis, Math.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var other = Vector3.Cross(axis, side);
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int s = 0; s <= segments; s++)
        {
            double round = 2 * Math.PI * s / segments;
            var outward = side * (float)Math.Cos(round) + other * (float)Math.Sin(round);
            vertices.Add(from + outward * fromRadius);
            vertices.Add(to + outward * toRadius);
        }
        for (int s = 0; s < segments; s++)
        {
            int a = s * 2;
            triangles.AddRange([a, a + 1, a + 2, a + 2, a + 1, a + 3]);
        }
        return ([.. vertices], [.. triangles]);
    }

    /// <summary>A card on the tab with a title and, under it, a hint; what's added to the returned panel goes in it.</summary>
    private StackPanel Section(string title, string hint)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        if (hint.Length > 0) panel.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        ClothPanel.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = panel });
        return panel;
    }

    private UIElement IconLabel(string icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Style = (Style)FindResource("IconText"), Text = icon });
        row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0) });
        return row;
    }

    /// <summary>
    /// A setting as a label, a slider and its value, which can be typed; double-clicking the label puts back what the
    /// resource has, which the label's tooltip says.
    /// </summary>
    private FrameworkElement Slider(string label, double min, double max, Func<double> get, Action<double> set, string format, double original, bool previewOnly = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = L.T(previewOnly ? "MeshWindow.ClothDefaultPreview" : "MeshWindow.ClothDefault", original.ToString(format)), Background = Brushes.Transparent,
            FontStyle = previewOnly ? FontStyles.Italic : FontStyles.Normal };
        var slider = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
        var box = new TextBox { Text = get().ToString(format), Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
        bool syncing = false;
        void Show()
        {
            syncing = true;
            slider.Value = Math.Clamp(get(), min, max);
            box.Text = get().ToString(format);
            syncing = false;
        }
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            set(e.NewValue);
            syncing = true;
            box.Text = e.NewValue.ToString(format);
            syncing = false;
        };
        void Typed()
        {
            if (syncing || !double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v)
                && !double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return;
            set(v);
            Show();
        }
        box.LostFocus += (_, _) => Typed();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Typed(); };
        name.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            set(original);
            Show();
        };
        _clothRefresh.Add(Show);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(box, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(box);
        return grid;
    }

    /// <summary>A setting that's shown but not changed here, with a note on why.</summary>
    private static FrameworkElement Fact(string label, string value, string note)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = note.Length > 0 ? $"{value}   ·   {note}" : value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>
    /// Whether the cloth's settings that the resource keeps have been changed since it was opened or saved. Those
    /// that only the APEX asset keeps (its stretching stiffness and frequencies) only change the preview.
    /// </summary>
    private bool ClothNeedsSave => _clothMaterial is { } m && _clothSavedMaterial is { } saved
        && (_clothThickness != _clothSavedThickness || !ClothSettings.Of(m, 0).SequenceEqual(ClothSettings.Of(saved, 0)));

    /// <summary>Takes the settings as they are now as saved.</summary>
    private void ClothSaved()
    {
        _clothSavedMaterial = _clothMaterial;
        _clothSavedThickness = _clothThickness;
    }

    /// <summary>Gives the preview's simulations the settings as they are on the tab, and says whether there's something to save.</summary>
    private void ApplyCloth()
    {
        UpdateSave();
        if (_clothRig is null || _clothMaterial is null) return;
        foreach (int part in _clothRig.Parts)
            foreach (var sim in _clothRig.Of(part))
                if (sim is not null)
                {
                    sim.Material = _clothMaterial;
                    sim.Thickness = _clothThickness;
                }
        if (!_simulating) RestartCloth();
    }

    private void SetSimulating(bool on)
    {
        if (on == _simulating) return;
        _simulating = on;
        if (on)
        {
            _lastRender = TimeSpan.Zero;
            CompositionTarget.Rendering += RenderCloth;
        }
        else CompositionTarget.Rendering -= RenderCloth;
        UpdateSimulateButton();
    }

    private void StopSimulating() => SetSimulating(false);

    private void UpdateSimulateButton()
    {
        if (_simulateButton is null) return;
        _simulateButton.Content = IconLabel(_simulating ? "" : "", _simulating ? L.T("MeshWindow.ClothPause") : L.T("MeshWindow.ClothSimulate"));
    }

    /// <summary>Starts the cloth over where its bones hang it, at rest.</summary>
    private void RestartCloth()
    {
        if (_clothRig is not { } rig) return;
        _clothTime = 0;
        var bones = Bones(0);
        var pose = rig.Restart(_ => bones);
        View.Pose((part, chunk) => part == 0 ? ClothPose.Skin(pose, part, chunk, bones) : null);
    }

    /// <summary>Each frame the screen draws: the cloth simulated on by the time that's passed, moved as the motion picked says.</summary>
    private void RenderCloth(object? sender, EventArgs e)
    {
        if (_clothRig is not { } rig || e is not RenderingEventArgs r || r.RenderingTime == _lastRender) return;
        float dt = _lastRender == TimeSpan.Zero ? 1 / 60f : (float)Math.Clamp((r.RenderingTime - _lastRender).TotalSeconds, 0, 0.1);
        _lastRender = r.RenderingTime;
        _clothTime += dt;
        var bones = Bones(_clothTime);
        var yaw = _windYaw * Math.PI / 180;
        var pitch = _windPitch * Math.PI / 180;
        var wind = new Vector3((float)(Math.Cos(pitch) * Math.Cos(yaw)), (float)(Math.Cos(pitch) * Math.Sin(yaw)), (float)Math.Sin(pitch)) * (float)_windStrength;
        foreach (int part in rig.Parts)
            foreach (var sim in rig.Of(part))
                if (sim is not null) sim.Wind = wind;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pose = rig.Advance(dt, _ => bones);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        View.Pose((part, chunk) => part == 0 ? ClothPose.Skin(pose, part, chunk, bones) : null);
        // the collision shapes drawn go with the bones as they move
        if (CollisionItem.IsChecked && _clothMotion != 0) ShowClothShapes(true);
        if (_clothStatus is not null && rig.Of(0).ElementAtOrDefault(View.Lod) is { Started: true } shown)
            _clothStatus.Text = L.T("MeshWindow.ClothStatus", shown.Particles.Length, View.Lod, ms);
    }

    /// <summary>
    /// The skinning matrix every bone has at a time of the motion picked: still; swaying side to side and back; spinning
    /// round; or bobbing on as a walk does. It turns about the top of the cloth, where it hangs from.
    /// </summary>
    private Matrix4x4[] Bones(float time)
    {
        var mesh = _mesh!;
        var pivot = new Vector3((mesh.BoundsMin.X + mesh.BoundsMax.X) / 2, (mesh.BoundsMin.Y + mesh.BoundsMax.Y) / 2, mesh.BoundsMax.Z);
        var move = _clothMotion switch
        {
            1 => Matrix4x4.CreateRotationY(0.35f * MathF.Sin(time * 2.4f)) * Matrix4x4.CreateRotationX(0.2f * MathF.Sin(time * 1.7f)),
            2 => Matrix4x4.CreateRotationZ(time * 3f),
            3 => Matrix4x4.CreateTranslation(0, 0.25f * MathF.Sin(time * 3.1f), 0.04f * MathF.Abs(MathF.Sin(time * 6.2f)))
                 * Matrix4x4.CreateRotationZ(0.15f * MathF.Sin(time * 3.1f)),
            _ => Matrix4x4.Identity,
        };
        var matrix = Matrix4x4.CreateTranslation(-pivot) * move * Matrix4x4.CreateTranslation(pivot);
        return Enumerable.Repeat(matrix, Math.Max(1, mesh.BoneNames.Length)).ToArray();
    }
}
