using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.Core.Trees;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;
using Vector3 = System.Numerics.Vector3;

namespace W3ModWorkbench;

/// <summary>
/// A dialog set (.w2dset) as REDkit's scene editor shows one: in 3D, a stand-in actor in each slot in the stance and
/// idle the slot gives it, each slot named, each camera drawn as a small frustum with its name, and any camera looked
/// through as the game frames its shot. Beside it the slots (where, facing, stance, face) and the cameras (shot, from
/// and to, lens, depth of field, tags), filtered by name or by the slot they film, and the file's properties.
/// </summary>
public partial class DialogsetWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private DialogsetFile? _set;
    private StageActor? _actor;
    private List<ActorTrack> _tracks = [];
    private Dictionary<ActorStance, string> _idles = [];
    private CancellationTokenSource? _load;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly System.Diagnostics.Stopwatch _playing = new();
    private int _slotPicked = -1;
    private DialogsetCamera? _cameraPicked;
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A slot in the list: where it is, which way it faces, the stance and face its actor starts in.</summary>
    private sealed record SlotRow(SceneDialogsetSlot Slot)
    {
        public int Number => Slot.Number;
        public string Name => Slot.Name;
        public string WhereText => $"{Slot.Position.X:0.00}, {Slot.Position.Y:0.00}, {Slot.Position.Z:0.00}";
        public string FacingText => $"{Slot.Rotation.Z:0}°";
        public string StanceText
        {
            get
            {
                var stance = SceneStage.StanceOf(Slot);
                return $"{stance.Status} · {stance.Emotion} · {stance.Pose}";
            }
        }
        public string FaceText => string.Join(" · ", new[] { "actorMimicsEmotionalState", "actorMimicsLayer_Eyes", "actorMimicsLayer_Pose", "actorMimicsLayer_Animation" }
            .Select(p => Slot.Source[p] as string).Where(v => v is { Length: > 0 } && v != "None"));
    }

    /// <summary>A camera in the list, with its shot, its slots and lens in words.</summary>
    private sealed record CameraRow(DialogsetCamera Camera)
    {
        public string Name => Camera.Name;
        public string Plane => Camera.Plane ?? "";
        public string FromToText => Camera.Source is null && Camera.Target is null ? "" : $"{Camera.Source ?? "?"} → {Camera.Target ?? "?"}";
        public string FovText => Camera.FieldOfView.ToString("0.#", CultureInfo.CurrentCulture);
        public string TagsText => string.Join(", ", Camera.Tags);
    }

    private DialogsetWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = L.T("DialogsetWindow.Dialogset2", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        StandInBox.ItemsSource = new[] { L.T("DialogsetWindow.Man"), L.T("DialogsetWindow.Woman"), L.T("DialogsetWindow.Child"), L.T("DialogsetWindow.Dwarf") };
        View.StartFrom(200, 25);
        _clock.Tick += (_, _) => Pose((float)_playing.Elapsed.TotalSeconds);
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _load?.Cancel();
        };
    }

    public static bool CanShow(string name) => DialogsetFile.IsDialogset(name);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new DialogsetWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new DialogsetWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        try
        {
            var read = _readData;
            _set = await Task.Run(() => DialogsetFile.Read(read()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = L.T("DialogsetWindow.CouldNotRead", ex.Message);
            return;
        }
        var set = _set;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        SlotGrid.ItemsSource = set.Slots.Select(s => new SlotRow(s)).ToList();
        SlotsTab.Header = L.T("DialogsetWindow.Slots2", set.Slots.Count);
        SlotFilter.ItemsSource = new[] { L.T("DialogsetWindow.EverySlot") }.Concat(set.Slots.Select(s => s.Name)).ToList();
        SlotFilter.SelectedIndex = 0;
        ShowCameras();
        InfoText.Text = L.T("DialogsetWindow.Info", _path, _source, set.File.Version, set.Name.Length > 0 ? set.Name : "—",
            L.T("Count.Slot", set.Slots.Count), L.T("Count.Camera", set.Cameras.Count));
        FileProperties.Show(PropertyRow.Of(set.Resource, set.File));
        _ = LoadRedkitAsync();
        DrawMarks();
        StandInBox.SelectedIndex = 0;
    }

    private void StandInBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_set is not null && StandInBox.SelectedIndex >= 0) _ = LoadStandInAsync(JobTreeFile.PreviewEntities[StandInBox.SelectedIndex]);
    }

    /// <summary>
    /// Puts the stand-in together with the idles of every stance the slots start in, and stands a copy in each slot.
    /// </summary>
    private async Task LoadStandInAsync(string entity)
    {
        _load?.Cancel();
        var token = (_load = new CancellationTokenSource()).Token;
        var set = _set!;
        ViewNote.Text = L.T("DialogsetWindow.Loading", Path.GetFileNameWithoutExtension(entity));
        try
        {
            var notes = new List<string>();
            var (actor, idles) = await Task.Run(() =>
            {
                var idles = SceneStage.Idles(_locator);
                var names = set.Slots.Select(s => SceneStage.IdleFor(idles, SceneStage.StanceOf(s))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var data = _locator.ReadFile(entity) ?? throw new FileNotFoundException(L.T("DialogsetWindow.NotInGame", entity));
                var actor = SceneStage.LoadActor(Path.GetFileNameWithoutExtension(entity), entity, data, [], names, _locator, notes,
                    new Dictionary<string, Cr2wFile?>(StringComparer.OrdinalIgnoreCase), token);
                return (actor, idles);
            }, token);
            if (actor is null)
            {
                ViewNote.Text = string.Join("\n", notes);
                return;
            }
            var coloured = actor.Parts.Select((part, i) =>
                (part.Mesh, StageTextures.Coloring(actor.Entity, actor.ColoredBy[i].Entity, actor.ColoredBy[i].Appearance, actor.DrawnBy[i].Name))).ToList();
            var progress = new Progress<(int Done, int Total)>(n => { if (!token.IsCancellationRequested && n.Total > 0) ViewNote.Text = L.T("DialogsetWindow.LoadingTextures", n.Done, n.Total); });
            var textures = await StageTextures.LoadAsync(coloured, _locator, Texture, progress, token);
            if (token.IsCancellationRequested) return;
            _actor = actor;
            _idles = idles;
            _tracks = set.Slots.Select(s => SceneStage.Standing(s, s.Name)).ToList();
            // a copy of the stand-in for each slot, its textures copied too
            var parts = new List<(Core.Meshes.MeshFile Mesh, Matrix4x4 Place)>();
            var allTextures = new List<(int Part, int Material, BitmapSource? Picture, MaterialLook? Look, bool LeaveOut)>();
            for (int s = 0; s < _tracks.Count; s++)
            {
                foreach (var t in textures) allTextures.Add((t.Part + parts.Count, t.Material, t.Picture, t.Look, t.LeaveOut));
                parts.AddRange(actor.Parts.Select(p => (p.Mesh, Matrix4x4.Identity)));
            }
            View.ShowParts(parts, allTextures);
            Pose(0);
            Fit();
            ViewNote.Text = L.T("DialogsetWindow.StandInNote", Path.GetFileNameWithoutExtension(entity)) + (notes.Count > 0 ? "\n" + string.Join("\n", notes.Take(3)) : "");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            ViewNote.Text = L.T("DialogsetWindow.StandInFailed", ex.Message);
        }
    }

    private Task<BitmapSource?> Texture(string path)
    {
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task)) _textures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, App.TextureDetail));
            return task;
        }
    }

    /// <summary>Each slot's stand-in in its idle at a time, and the marks where they stand.</summary>
    private void Pose(float time)
    {
        if (_actor is not { } actor || _tracks.Count == 0) return;
        int count = actor.Parts.Count;
        var posers = _tracks.Select(t => SceneStage.Pose(actor, SceneStage.Bones(actor, t, _idles, time))).ToList();
        View.Pose((part, chunk) => part / count < posers.Count ? posers[part / count](part % count, chunk) : null);
    }

    /// <summary>The slots' names on the ground where they stand, and the cameras as small frustums with their names.</summary>
    private void DrawMarks()
    {
        if (_set is null) return;
        var lines = new List<(Vector3, Vector3, Color, double)>();
        var labels = new List<(string, Vector3, Color)>();
        var slotColor = Color.FromRgb(0x5A, 0xA9, 0xF2);
        foreach (var slot in _set.Slots)
        {
            bool picked = slot.Number == _slotPicked;
            var color = picked ? Color.FromRgb(0xC7, 0x7D, 0xFF) : slotColor;
            // a ring on the ground and an arrow the way it faces
            float yaw = slot.Rotation.Z * MathF.PI / 180;
            var facing = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0);
            for (int i = 0; i < 24; i++)
            {
                float a = i * MathF.Tau / 24, b = (i + 1) * MathF.Tau / 24;
                lines.Add((slot.Position + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * 0.35f, slot.Position + new Vector3(MathF.Cos(b), MathF.Sin(b), 0) * 0.35f, color, picked ? 3 : 2));
            }
            lines.Add((slot.Position, slot.Position + facing * 0.6f, color, picked ? 3 : 2));
            if (NamesCheck.IsChecked == true) labels.Add(($"{slot.Number}  {slot.Name}", slot.Position + new Vector3(0, 0, 2.05f), color));
        }
        var shown = (CameraGrid.ItemsSource as List<CameraRow>) ?? [];
        // every camera named only when there are few; else just the one picked, as dozens of names hide the set
        bool nameAll = shown.Count <= 12;
        if (CamerasCheck.IsChecked == true)
            foreach (var row in shown)
            {
                var camera = row.Camera;
                bool picked = camera == _cameraPicked;
                var color = picked ? Color.FromRgb(0xC7, 0x7D, 0xFF) : Color.FromRgb(0xF2, 0xC9, 0x4C);
                var view = SceneStage.Camera(camera.Name, camera.Position, camera.Pitch, camera.Yaw, camera.FieldOfView);
                var right = Vector3.Normalize(Vector3.Cross(view.Forward, Vector3.UnitZ));
                var up = Vector3.Cross(right, view.Forward);
                float depth = picked ? 0.45f : 0.25f, half = depth * MathF.Tan(camera.FieldOfView * MathF.PI / 360);
                var centre = camera.Position + view.Forward * depth;
                var corners = new[] { centre + right * half * 16 / 9 + up * half, centre - right * half * 16 / 9 + up * half, centre - right * half * 16 / 9 - up * half, centre + right * half * 16 / 9 - up * half };
                double thick = picked ? 2.6 : 1.4;
                for (int i = 0; i < 4; i++)
                {
                    lines.Add((camera.Position, corners[i], color, thick));
                    lines.Add((corners[i], corners[(i + 1) % 4], color, thick));
                }
                if (NamesCheck.IsChecked == true && (picked || nameAll && _cameraPicked is null)) labels.Add((camera.Name, camera.Position + new Vector3(0, 0, 0.12f), color));
            }
        View.ShowMarks(lines, labels);
    }

    private void Fit()
    {
        if (_set is null || _set.Slots.Count == 0) return;
        var points = _set.Slots.Select(s => s.Position).ToList();
        var min = points.Aggregate(new Vector3(float.MaxValue), Vector3.Min) - new Vector3(0.8f, 0.8f, 0);
        var max = points.Aggregate(new Vector3(float.MinValue), Vector3.Max) + new Vector3(0.8f, 0.8f, 2);
        View.StopLookingThrough();
        ThroughToggle.IsChecked = false;
        View.FitTo(min, max);
    }

    private void ShowCameras()
    {
        if (_set is null) return;
        string filter = FilterBox.Text.Trim();
        string? slot = SlotFilter.SelectedIndex > 0 ? SlotFilter.SelectedItem as string : null;
        var rows = _set.Cameras
            .Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (c.Plane ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                || c.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .Where(c => slot is null || string.Equals(c.Target, slot, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Source, slot, StringComparison.OrdinalIgnoreCase))
            .Select(c => new CameraRow(c)).ToList();
        CameraGrid.ItemsSource = rows;
        CamerasTab.Header = rows.Count == _set.Cameras.Count ? L.T("DialogsetWindow.Cameras2", rows.Count) : L.T("DialogsetWindow.CamerasOf", rows.Count, _set.Cameras.Count);
        DrawMarks();
    }

    private void SlotGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _slotPicked = SlotGrid.SelectedItem is SlotRow row ? row.Number : -1;
        DrawMarks();
    }

    private void CameraGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _cameraPicked = (CameraGrid.SelectedItem as CameraRow)?.Camera;
        if (_cameraPicked is { } c)
        {
            CameraTitle.Text = c.Name;
            CameraDetail.Text = L.T("DialogsetWindow.CameraDetail", $"{c.Position.X:0.00}, {c.Position.Y:0.00}, {c.Position.Z:0.00}", c.Pitch, c.Yaw, c.Roll,
                c.FieldOfView, c.Zoom, c.FocusNear, c.FocusFar, c.BlurNear, c.BlurFar, c.DofIntensity);
            CameraPanel.Visibility = Visibility.Visible;
        }
        else CameraPanel.Visibility = Visibility.Collapsed;
        DrawMarks();
        if (ThroughToggle.IsChecked == true) LookThrough();
    }

    private void CameraGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ThroughToggle.IsChecked = true;
        LookThrough();
    }

    private void LookThrough_Click(object sender, RoutedEventArgs e)
    {
        ThroughToggle.IsChecked = true;
        LookThrough();
    }

    /// <summary>Looks through the camera picked (or the first) as the game frames it; or gives the view back.</summary>
    private void LookThrough()
    {
        var camera = _cameraPicked ?? (CameraGrid.ItemsSource as List<CameraRow>)?.FirstOrDefault()?.Camera;
        if (ThroughToggle.IsChecked != true || camera is null)
        {
            View.StopLookingThrough();
            ThroughToggle.IsChecked = false;
            return;
        }
        var view = SceneStage.Camera(camera.Name, camera.Position, camera.Pitch, camera.Yaw, camera.FieldOfView);
        View.LookThrough(view.Position, view.Forward, view.Up, view.FieldOfView);
    }

    private void ThroughToggle_Click(object sender, RoutedEventArgs e) => LookThrough();

    private void PlayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (PlayToggle.IsChecked == true)
        {
            _playing.Start();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
            _playing.Stop();
        }
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) => DrawMarks();

    private void FitButton_Click(object sender, RoutedEventArgs e) => Fit();

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowCameras();
    }

    private void SlotFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowCameras();

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void CopyCameraName_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraPicked is { } c) Copy(c.Name);
    }

    private void CopyTransform_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraPicked is { } c)
            Copy(string.Create(CultureInfo.InvariantCulture, $"{c.Position.X} {c.Position.Y} {c.Position.Z}  {c.Pitch} {c.Yaw} {c.Roll}  fov {c.FieldOfView}")); // loc-ignore: numbers to paste
    }

    /// <summary>Shows REDkit's own copy's properties when there is one, and says where they come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit?.First("CStorySceneDialogset") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, L.T("DialogsetWindow.Dialogset3"));
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Tabs.SelectedItem = CamerasTab;
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.F)
        {
            Fit();
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            ThroughToggle.IsChecked = ThroughToggle.IsChecked != true;
            LookThrough();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
