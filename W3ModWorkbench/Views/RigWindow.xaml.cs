using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Core.Trees;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// A rig (.w2rig, a CSkeleton) as REDkit's skeleton editor shows it: its skeleton in 3D, in the pose it rests in (its
/// bones' names and axes drawn as asked, inside a character made on it if wanted), the bones as a tree from the root,
/// each numbered, the one picked shown in the view with where it is from its parent and in the model, its float
/// tracks, and its properties (speeds, LODs, the control rig).
/// </summary>
public partial class RigWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private Skeleton? _rig;
    private Matrix4x4[] _model = [];
    private List<BoneNode> _all = [];
    private List<BoneNode> _roots = [];
    private int _selected = -1;
    private bool _characterShown;
    private CancellationTokenSource? _characterLoad;

    /// <summary>A bone in the tree: its name and number, and the bones under it.</summary>
    private sealed class BoneNode(string name, int index) : INotifyPropertyChanged
    {
        private bool _expanded = true, _selected;
        public string Name { get; } = name;
        public int Index { get; } = index;
        public string IndexText => $"({Index})";
        public ObservableCollection<BoneNode> Children { get; } = [];
        public bool Expanded { get => _expanded; set { _expanded = value; Changed(nameof(Expanded)); } }
        public bool Selected { get => _selected; set { _selected = value; Changed(nameof(Selected)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Characters to show the base rigs inside, by the rig's name.</summary>
    private static readonly (string Rig, string Entity)[] BaseCharacters =
    [
        ("woman_base", JobTreeFile.PreviewEntities[1]), ("child_base", JobTreeFile.PreviewEntities[2]),
        ("dwarf_base", JobTreeFile.PreviewEntities[3]), ("man_base", JobTreeFile.PreviewEntities[0]),
    ];

    private RigWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        Title = $"{Path.GetFileName(path)} - Rig";
        InfoText.Text = $"{path}   ·   {source}";
        View.StartFrom(180, 10);
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _characterLoad?.Cancel();
    }

    public static bool CanShow(string name) => name.EndsWith(".w2rig", StringComparison.OrdinalIgnoreCase);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new RigWindow(path, source, () => data, locator, archive?.FilePath).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new RigWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        Cr2wFile file;
        RedObject skeleton;
        try
        {
            (file, skeleton, _rig) = await Task.Run(() =>
            {
                var f = Cr2wFile.Read(_readData());
                var s = f.First("CSkeleton") ?? throw new InvalidDataException("There's no skeleton in it: it has no CSkeleton.");
                return (f, s, Skeleton.FromObject(s, f));
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The rig could not be read.\n\n" + ex.Message;
            return;
        }
        var rig = _rig;
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        _model = new Matrix4x4[rig.Count];
        rig.ToModel(rig.Pose, _model);

        // the tree, from the roots: a parent comes before its children
        _all = rig.Names.Select((n, i) => new BoneNode(n, i)).ToList();
        _roots = [];
        for (int i = 0; i < rig.Count; i++)
        {
            int parent = rig.Parents[i];
            if (parent >= 0 && parent < i) _all[parent].Children.Add(_all[i]); else _roots.Add(_all[i]);
        }
        ShowTree();
        BonesTab.Header = $"Bones ({rig.Count:N0})";
        var tracks = file.Array(skeleton, "tracks").OfType<RedObject>().Select(t => t["nameAsCName"] as string ?? t["name"] as string ?? "").ToList();
        TrackList.ItemsSource = tracks.Count > 0 ? tracks : ["The rig has no float tracks."];
        TracksTab.Header = $"Tracks ({tracks.Count:N0})";
        int depth = Enumerable.Range(0, rig.Count).Max(Depth);
        var (min, max) = Box();
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {file.Version}, {Format.Count(rig.Count, "bone")} up to {depth} deep, {Format.Count(tracks.Count, "track")}"
            + $", {max.Z - Math.Min(0, min.Z):0.00} m tall";
        FileProperties.Show(PropertyRow.Of(skeleton, file));
        _ = LoadRedkitAsync();
        CharacterCheck.IsEnabled = CharacterFor() is not null;
        if (!CharacterCheck.IsEnabled) CharacterCheck.ToolTip = "No character made on this rig was found beside it";
        Draw();
        await Dispatcher.InvokeAsync(Fit, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private int Depth(int bone)
    {
        int depth = 0;
        for (int at = bone; at >= 0 && at < _rig!.Count; at = _rig.Parents[at] < at ? _rig.Parents[at] : -1) depth++;
        return depth;
    }

    /// <summary>The box round the bones where they rest, leaving out helper bones far from the rest.</summary>
    private (Vector3 Min, Vector3 Max) Box()
    {
        var points = _model.Select(m => m.Translation).ToList();
        if (points.Count == 0) return (-Vector3.One, Vector3.One);
        var middle = new Vector3(points.Select(p => p.X).Order().ElementAt(points.Count / 2), points.Select(p => p.Y).Order().ElementAt(points.Count / 2), points.Select(p => p.Z).Order().ElementAt(points.Count / 2));
        var spread = points.Select(p => Vector3.Distance(p, middle)).Order().ElementAt(points.Count * 9 / 10);
        var near = points.Where(p => Vector3.Distance(p, middle) <= Math.Max(spread * 3, 0.5f)).ToList();
        var min = near.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = near.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        return (min, max);
    }

    private void Fit()
    {
        var (min, max) = Box();
        var pad = Vector3.One * Math.Max(0.05f, (max - min).Length() * 0.05f);
        View.FitTo(min - pad, max + pad);
    }

    private void Draw()
    {
        if (_rig is null) return;
        var (min, max) = Box();
        float axis = Math.Clamp((max - min).Length() * 0.025f, 0.01f, 0.5f);
        View.ShowSkeleton(_model, _rig.Parents, _selected, AxesCheck.IsChecked == true ? axis : 0, NamesCheck.IsChecked == true ? _rig.Names : null);
    }

    private void ShowTree()
    {
        string text = FilterBox.Text.Trim();
        BoneTree.ItemsSource = text.Length == 0 ? _roots
            : _all.Where(b => b.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(b => new BoneNode(b.Name, b.Index)).ToList();
    }

    private void BoneTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not BoneNode node || _rig is not { } rig) return;
        _selected = node.Index;
        var pose = rig.Pose[node.Index];
        int parent = rig.Parents[node.Index];
        var children = _all[node.Index].Children;
        BoneTitle.Text = $"{node.Name}  ({node.Index})";
        BoneDetail.Text = (parent >= 0 ? $"under {rig.Names[parent]} ({parent})" : "a root") + $"  ·  {Format.Count(children.Count, "bone")} under it  ·  {Depth(node.Index)} deep"
            + (children.Count > 0 ? $": {string.Join(", ", children.Take(8).Select(c => c.Name))}{(children.Count > 8 ? ", …" : "")}" : "")
            + $"\n{Chain(node.Index)}";
        BonePosition.Text = $"{pose.Position.X,8:0.0000} {pose.Position.Y,8:0.0000} {pose.Position.Z,8:0.0000}";
        var (x, y, z) = Euler(pose.Rotation);
        BoneRotation.Text = $"{x,8:0.00}° {y,8:0.00}° {z,8:0.00}°";
        BoneQuaternion.Text = $"{pose.Rotation.X,8:0.0000} {pose.Rotation.Y,8:0.0000} {pose.Rotation.Z,8:0.0000} {pose.Rotation.W,8:0.0000}";
        BoneScale.Text = $"{pose.Scale.X,8:0.000} {pose.Scale.Y,8:0.000} {pose.Scale.Z,8:0.000}";
        var at = _model[node.Index].Translation;
        BoneModel.Text = $"{at.X,8:0.0000} {at.Y,8:0.0000} {at.Z,8:0.0000}";
        BonePanel.Visibility = Visibility.Visible;
        Draw();
    }

    /// <summary>A rotation as turns about X, then Y, then Z, in degrees.</summary>
    private static (float X, float Y, float Z) Euler(Quaternion q)
    {
        float sinX = 2 * (q.W * q.X + q.Y * q.Z), cosX = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        float sinY = Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1);
        float sinZ = 2 * (q.W * q.Z + q.X * q.Y), cosZ = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        const float degrees = 180 / MathF.PI;
        return (MathF.Atan2(sinX, cosX) * degrees, MathF.Asin(sinY) * degrees, MathF.Atan2(sinZ, cosZ) * degrees);
    }

    /// <summary>
    /// A character made on the rig: one of the base characters for theirs, else a template beside the rig or named as
    /// it is. Null when none is found.
    /// </summary>
    private string? CharacterFor()
    {
        string name = Path.GetFileNameWithoutExtension(_path);
        foreach (var (rig, entity) in BaseCharacters)
            if (name.Equals(rig, StringComparison.OrdinalIgnoreCase)) return entity;
        string folder = Path.GetDirectoryName(_path) ?? "";
        var entities = _locator.AllPaths(".w2ent");
        return entities.Where(e => folder.Length > 0 && e.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Length).FirstOrDefault()
            ?? entities.Where(e => Path.GetFileNameWithoutExtension(e).Equals(name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
    }

    private async void CharacterCheck_Click(object sender, RoutedEventArgs e)
    {
        _characterLoad?.Cancel();
        if (CharacterCheck.IsChecked != true)
        {
            View.ShowParts([]);
            _characterShown = false;
            Draw();
            return;
        }
        if (_characterShown || CharacterFor() is not { } path) return;
        var token = (_characterLoad = new CancellationTokenSource()).Token;
        ViewNote.Text = $"Loading {Path.GetFileName(path)}…";
        try
        {
            var entity = await Task.Run(() =>
            {
                var read = EntityFile.Read(_locator.ReadFile(path) ?? throw new FileNotFoundException($"{path} isn't in the game's files."));
                read.FindOrigins(_locator.ReadFile);
                return read;
            }, token);
            var appearance = entity.DefaultAppearance;
            var look = await Task.Run(() => entity.Look(appearance, _locator.ReadFile), token);
            var (parts, drawnBy, _, _) = await Task.Run(() => EntityWindow.ReadMeshes(look, _locator, token), token);
            var coloured = parts.Select((part, p) => (part.Mesh, LookTextures.Coloring(entity, appearance?.Name, drawnBy[p].Name))).ToList();
            var progress = new Progress<(int Done, int Total)>(n => { if (!token.IsCancellationRequested && n.Total > 0) ViewNote.Text = $"Loading textures, {n.Done} of {n.Total}…"; });
            var textures = await StageTextures.LoadAsync(coloured, _locator, Texture, progress, token);
            if (token.IsCancellationRequested || CharacterCheck.IsChecked != true) return;
            View.ShowParts(parts, textures);
            _characterShown = true;
            ViewNote.Text = $"Inside {Path.GetFileNameWithoutExtension(path)}, as it's modelled. Click a bone in the list to pick it out.";
            Draw();
            Fit();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            ViewNote.Text = $"{Path.GetFileName(path)} could not be shown: {ex.Message}";
        }
    }

    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);

    private Task<BitmapSource?> Texture(string path)
    {
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task)) _textures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, 1024));
            return task;
        }
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) => Draw();

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>A bone's chain from the root down to it: Root > pelvis > torso > ….</summary>
    private string Chain(int bone)
    {
        var names = new List<string>();
        for (int at = bone; at >= 0 && at < _rig!.Count; at = _rig.Parents[at] < at ? _rig.Parents[at] : -1) names.Add(_rig.Names[at]);
        names.Reverse();
        return string.Join(" > ", names);
    }

    private void CopyBone_Click(object sender, RoutedEventArgs e)
    {
        if (_selected >= 0) Copy(_rig!.Names[_selected]);
    }

    private void CopyChain_Click(object sender, RoutedEventArgs e)
    {
        if (_selected >= 0) Copy(Chain(_selected));
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_rig is { } rig) Copy(string.Join(Environment.NewLine, rig.Names.Select((n, i) => $"{i}\t{n}\t{(rig.Parents[i] >= 0 ? rig.Names[rig.Parents[i]] : "")}")));
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var bone in _all) bone.Expanded = true;
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var bone in _all) bone.Expanded = false;
    }

    private void FitButton_Click(object sender, RoutedEventArgs e) => Fit();

    /// <summary>Says where the rig's properties come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (_path, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit?.First("CSkeleton") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, "rig");
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowTree();
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Tabs.SelectedItem = BonesTab;
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _selected >= 0)
        {
            Copy(_rig!.Names[_selected]);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F)
        {
            Fit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
