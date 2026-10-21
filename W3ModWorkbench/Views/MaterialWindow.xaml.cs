using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// A material instance (.w2mi) or a shader graph (.w2mg), to look at and change: every parameter it has, its own and
/// those its bases and the shader give it, grouped (textures with their pictures, colours as swatches, numbers on
/// sliders), drawn live on a sphere, a box or a mesh that uses it as the game draws it, what it's built on, what uses it,
/// and its properties. Changes go into the open project, at the material's own game path or as a new material.
/// </summary>
public partial class MaterialWindow : Window, IMessageHost
{
    private const int PreviewTextureSize = 1024, ThumbnailSize = 128;

    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly string? _origin;
    private readonly IArchive? _archive;
    private readonly string? _diskFile;
    private byte[] _data = [];
    private Cr2wFile? _file;
    private MeshMaterial? _material;
    private List<MaterialParameter> _own = [];
    private List<MaterialParameter> _savedOwn = [];
    private List<MaterialValueRow> _rows = [];
    private ListCollectionView? _view;
    private MeshFile? _previewMesh;
    private HashSet<int> _ours = [];
    private int _previewVersion, _lookVersion;
    private readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _relook = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<BitmapSource?>> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _allTextures;
    private bool _syncing;

    public static readonly IValueConverter Not = new NotConverter();

    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }

    /// <summary>A shape to preview the material on: one of the game's, or a mesh that uses it.</summary>
    private sealed record Shape(string Label, string Path, bool UsesIt);

    private sealed record ChainItem(string Name, string Detail, string Count, string Path, bool CanOpen);

    private sealed record UserRow(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public string Kind => FileKinds.Of(Path)?.Name ?? System.IO.Path.GetExtension(Path);
    }

    // shapes the game itself has, so the preview needs nothing but the game
    private static readonly Shape[] Shapes =
    [
        new("Sphere", @"engine\reference_level\level_assets\tags\materials\sphere.w2mesh", false),
        new("Box", @"engine\meshes\editor\box.w2mesh", false),
    ];

    private MaterialWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin, IArchive? archive, string? diskFile)
    {
        InitializeComponent();
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        _archive = archive;
        _diskFile = diskFile;
        Title = $"{Path.GetFileName(path)} - Material";
        InfoText.Text = $"{path}   ·   {source}";
        View.StartFrom(35, 20);
        _spin.Tick += (_, _) => View.Orbit(0.6);
        _relook.Tick += async (_, _) =>
        {
            _relook.Stop();
            await ApplyLookAsync();
        };
        Loaded += async (_, _) => await LoadAsync();
        Closing += Window_Closing;
        Closed += (_, _) => _spin.Stop();
    }

    public static bool CanShow(string name) =>
        name.EndsWith(".w2mi", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new MaterialWindow(path, source, () => data, locator, archive?.FilePath, archive, null).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new MaterialWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator, file, null, file).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private bool IsGraph => _path.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The game path the material is at: as opened, or below the content folder of a file opened from a folder.</summary>
    private string GamePath
    {
        get
        {
            if (_diskFile is null) return _path;
            string full = _diskFile.Replace('/', '\\');
            foreach (var root in new[] { @"\r4data\", @"\content\", @"\files\", @"\workspace\" })
            {
                int at = full.LastIndexOf(root, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) return full[(at + root.Length)..];
            }
            return _path;
        }
    }

    // ---------------------------------------------------------------- reading

    private async Task LoadAsync()
    {
        try
        {
            _data = await Task.Run(_readData);
            string self = GamePath;
            (_file, _material) = await Task.Run(() =>
            {
                var file = Cr2wFile.Read(_data);
                // the material read from the bytes opened, its bases from the game
                var material = MeshMaterial.FromFile(self, p => p.Equals(self, StringComparison.OrdinalIgnoreCase) ? _data : _locator.ReadFile(p));
                return (file, material);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The material could not be read.\n\n" + ex.Message;
            return;
        }
        var m = _material;
        _own = [.. m.Own];
        _savedOwn = [.. m.Own];
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        TitleText.Text = Path.GetFileName(_path);
        string kind = IsGraph ? "Shader graph" : "Material instance";
        string based = m.BasePath is { } b && !IsGraph ? $"  ·  based on {Path.GetFileName(b)}" : "";
        DetailText.Text = $"{kind}  ·  shader {m.Shader}{based}  ·  "
            + (IsGraph ? $"{Format.Count(m.Inherited.Count, "parameter")} it declares, with their defaults"
                       : $"{Format.Count(m.Own.Count, "parameter")} set here, {m.Inherited.Count:N0} from its bases and the shader");
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {_file!.Version}";
        SaveButton.ToolTip = IsGraph ? "A shader graph isn't changed: save the changes as a new material based on it" : SaveButton.ToolTip;
        ShowRows();
        ShowChain();
        var resource = _file.First(IsGraph ? "CMaterialGraph" : "CMaterialInstance");
        if (resource is not null) FileProperties.Show(PropertyRow.Of(resource, _file));
        _ = LoadRedkitAsync();
        _syncing = true;
        ShapeBox.ItemsSource = Shapes.ToList();
        ShapeBox.SelectedIndex = 0;
        _syncing = false;
        await ShowShapeAsync(Shapes[0]);
        _ = FindUsersAsync();
    }

    /// <summary>The parameters as rows, own then inherited in each group, the textures' pictures read in the background.</summary>
    private void ShowRows()
    {
        var inherited = _material!.Inherited;
        bool InBase(string name) => inherited.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _rows = [.. _own.Select(p => new MaterialValueRow(p, true, InBase(p.Name), ParameterChanged).WithRange())
            .Concat(inherited.Where(i => !_own.Any(o => o.Name.Equals(i.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new MaterialValueRow(p, false, true, ParameterChanged).WithRange()))
            .OrderBy(r => GroupOrder(r.Group)).ThenBy(r => r.IsOwn ? 0 : 1)];
        _view = new ListCollectionView(_rows) { Filter = Shown };
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MaterialValueRow.Group)));
        ParameterList.ItemsSource = _view;
        ParametersTab.Header = $"Parameters ({_rows.Count:N0})";
        UpdateNote();
        foreach (var row in _rows) _ = ThumbnailAsync(row);
    }

    private static int GroupOrder(string group) => group switch { "Textures" => 0, "Colours" => 1, "Numbers" => 2, "Vectors" => 3, _ => 4 };

    private void UpdateNote()
    {
        int own = _rows.Count(r => r.IsOwn);
        ParametersNote.Text = IsGraph
            ? "The parameters the shader declares, with their defaults. Change any to make a new material based on it (Save as new material)."
            : $"{own:N0} set by this material (in bold), the rest from its bases and the shader. Change any to set it here; the preview follows.";
    }

    private bool Shown(object item)
    {
        if (item is not MaterialValueRow row) return false;
        if (InheritedCheck.IsChecked != true && !row.IsOwn) return false;
        string text = FilterBox.Text.Trim();
        return text.Length == 0 || row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.Text.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ThumbnailAsync(MaterialValueRow row)
    {
        if (row.TexturePath is not { } path)
        {
            row.Thumbnail = null;
            return;
        }
        if (!_thumbnails.TryGetValue(path, out var task)) _thumbnails[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, ThumbnailSize));
        var picture = await task;
        if (row.TexturePath == path) row.Thumbnail = picture;
    }

    /// <summary>What the material is built on, nearest first, with how many of its parameters each gives.</summary>
    private void ShowChain()
    {
        var m = _material!;
        var items = new List<ChainItem>
        {
            new(Path.GetFileName(_path), IsGraph ? "This shader graph" : "This material", Format.Count(_own.Count, "parameter") + (IsGraph ? "" : " set here"), _path, false),
        };
        foreach (var basePath in m.Bases)
        {
            string name = Path.GetFileName(basePath);
            int gives = m.Inherited.Count(p => p.From.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool graph = basePath.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase);
            items.Add(new(name, (graph ? "Shader graph: declares every parameter and its default" : "Material instance") + $"  ·  {basePath}",
                $"gives {gives:N0}", basePath, true));
        }
        ChainList.ItemsSource = items;
        ChainTab.Header = $"Based on ({m.Bases.Count:N0})";
    }

    /// <summary>Says where the material's properties come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (GamePath, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit?.First(IsGraph ? "CMaterialGraph" : "CMaterialInstance") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, IsGraph ? "shader graph" : "material");
    }

    // ---------------------------------------------------------------- the preview

    private Task<BitmapSource?> Texture(string path)
    {
        lock (_textures)
        {
            if (!_textures.TryGetValue(path, out var task)) _textures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, PreviewTextureSize));
            return task;
        }
    }

    /// <summary>
    /// Draws a shape with the material on it: a sphere or a box with it on every chunk, or a mesh that uses it with it
    /// on the chunks it's on (the rest in their own materials).
    /// </summary>
    private async Task ShowShapeAsync(Shape shape)
    {
        int version = ++_previewVersion;
        ViewMessage.Visibility = Visibility.Collapsed;
        PreviewNote.Text = $"Reading {Path.GetFileName(shape.Path)}…";
        MeshFile? mesh;
        try
        {
            mesh = await Task.Run(() =>
            {
                if (_locator.ReadFile(shape.Path) is not { } data) return null;
                var archive = _locator.Find(shape.Path)?.Archive;
                return MeshFile.Read(data, n => _locator.ReadBuffer(shape.Path, n, archive));
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            mesh = null;
            PreviewNote.Text = $"{Path.GetFileName(shape.Path)} could not be read: {ex.Message}";
        }
        if (version != _previewVersion) return;
        if (mesh is null)
        {
            ViewMessage.Text = $"{shape.Label} isn't in the game's files or the archives open.";
            ViewMessage.Visibility = Visibility.Visible;
            return;
        }
        _previewMesh = mesh;
        View.Show(mesh);
        View.FitToView();
        string self = GamePath;
        _ours = shape.UsesIt
            ? [.. Enumerable.Range(0, mesh.Materials.Count).Where(i => mesh.File.Import(mesh.Materials[i])?.Path is { } p && p.Equals(self, StringComparison.OrdinalIgnoreCase))]
            : [.. Enumerable.Range(0, mesh.Materials.Count)];
        PreviewNote.Text = shape.UsesIt
            ? $"On {Path.GetFileName(shape.Path)}: the material on {Format.Count(_ours.Count, "of its materials", "of its materials")}, the rest in their own. Drag to turn it, the wheel to zoom."
            : $"On the game's {shape.Label.ToLowerInvariant()}, as the game draws the material. Drag to turn it, the wheel to zoom.";
        // a mesh's other materials as they are
        if (shape.UsesIt)
            for (int i = 0; i < mesh.Materials.Count; i++)
            {
                if (_ours.Contains(i)) continue;
                var (picture, look, _) = await LookTextures.ForMaterial(mesh, i, _locator, Texture);
                if (version != _previewVersion) return;
                View.SetTexture(i, picture, look);
            }
        await ApplyLookAsync();
    }

    /// <summary>Puts the material, as it is with its changes, on the preview's chunks that have it.</summary>
    private async Task ApplyLookAsync()
    {
        if (_material is null || _previewMesh is null) return;
        int version = ++_lookVersion;
        var current = _material.WithOwn([.. _own]);
        var picture = current.Diffuse?.Path is { } diffuse ? await Texture(diffuse) : null;
        var look = await LookTextures.LookOf(current, _locator, Texture);
        if (version != _lookVersion) return;
        foreach (int i in _ours) View.SetTexture(i, picture, look);
        if (current.Diffuse is null) PreviewNote.Text = PreviewNote.Text.Split("  ·  ")[0] + "  ·  It has no colour texture.";
    }

    private void ShapeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ShapeBox.SelectedItem is not Shape shape) return;
        _ = ShowShapeAsync(shape);
    }

    private void SpinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SpinToggle.IsChecked == true) _spin.Start(); else _spin.Stop();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => View.FitToView();

    // ---------------------------------------------------------------- what uses it

    /// <summary>
    /// The files that use the material: from the Asset Browser's index of what uses what when one is open, else the
    /// meshes and materials in the folders round it, where a material's meshes nearly always are. Each that's a mesh is
    /// added to the shapes to preview on.
    /// </summary>
    private async Task FindUsersAsync()
    {
        string self = GamePath;
        UsedByNote.Text = "Looking for the files that use it…";
        var users = new List<string>();
        bool everywhere = false;
        if (Application.Current.Windows.OfType<AssetBrowserWindow>().FirstOrDefault() is { } browser)
        {
            try
            {
                var index = await browser.ReferencesAsync(false);
                users = [.. index.UsedBy(self).Select(u => u.Path).Distinct(StringComparer.OrdinalIgnoreCase)];
                everywhere = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        if (!everywhere)
        {
            // the folders round it: its own, its parent (a mesh beside its materials folder), and theirs
            string folder = Path.GetDirectoryName(self) ?? "";
            var near = new[] { folder, Path.GetDirectoryName(folder) ?? "", Path.GetDirectoryName(Path.GetDirectoryName(folder) ?? "") ?? "" }
                .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            users = await Task.Run(() =>
            {
                var found = new List<string>();
                var candidates = _locator.AllPaths(".w2mesh").Concat(_locator.AllPaths(".w2mi"))
                    .Where(p => near.Any(f => p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) && !p.Equals(self, StringComparison.OrdinalIgnoreCase))
                    .Take(600).ToList();
                foreach (var path in candidates)
                    try
                    {
                        if (_locator.ReadFile(path) is { } data && ReferenceIndex.LinksOf(data).Any(l => l.Equals(self, StringComparison.OrdinalIgnoreCase))) found.Add(path);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
                return found;
            });
        }
        UsedByGrid.ItemsSource = users.Order(StringComparer.OrdinalIgnoreCase).Select(u => new UserRow(u)).ToList();
        UsedByTab.Header = $"Used by ({users.Count:N0})";
        UsedByNote.Text = (users.Count == 0 ? "Nothing was found that uses it" : $"{Format.Count(users.Count, "file")} that use it")
            + (everywhere ? ", from the Asset Browser's index of the whole game." : ", among the meshes and materials in the folders round it (open the Asset Browser to look through the whole game).")
            + (users.Count > 0 ? " Double-click one to open it, or pick a mesh above to preview the material on it." : "");
        var meshes = users.Where(u => u.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase)).Take(40)
            .Select(u => new Shape($"On {Path.GetFileNameWithoutExtension(u)}", u, true));
        _syncing = true;
        var selected = ShapeBox.SelectedItem;
        ShapeBox.ItemsSource = Shapes.Concat(meshes).ToList();
        ShapeBox.SelectedItem = selected;
        _syncing = false;
    }

    private UserRow? PickedUser => UsedByGrid.SelectedItem as UserRow;

    private void UsedByGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PickedUser is { } user) _ = DataViewers.OpenLinkAsync(user.Path, _locator, this, this);
    }

    private void OpenUser_Click(object sender, RoutedEventArgs e)
    {
        if (PickedUser is { } user) _ = DataViewers.OpenLinkAsync(user.Path, _locator, this, this);
    }

    private void PreviewOnUser_Click(object sender, RoutedEventArgs e)
    {
        if (PickedUser is not { } user) return;
        if (!user.Path.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowMessage(null, "Only a mesh can be previewed on: this is another material based on this one.", MessageKind.Info);
            return;
        }
        var shape = (ShapeBox.ItemsSource as IEnumerable<Shape>)?.FirstOrDefault(s => s.Path.Equals(user.Path, StringComparison.OrdinalIgnoreCase))
                    ?? new Shape($"On {Path.GetFileNameWithoutExtension(user.Path)}", user.Path, true);
        ShapeBox.SelectedItem = shape;
        if (!ReferenceEquals(ShapeBox.SelectedItem, shape)) _ = ShowShapeAsync(shape);
    }

    private void CopyUser_Click(object sender, RoutedEventArgs e)
    {
        if (PickedUser is { } user) Copy(user.Path);
    }

    // ---------------------------------------------------------------- changes

    private string OwnLabel => Path.GetFileName(_path);

    /// <summary>A parameter changed: it becomes the material's own, and the preview follows.</summary>
    private void ParameterChanged(MaterialValueRow row, object value)
    {
        var parameter = row.Parameter with { Value = value, From = OwnLabel };
        int at = _own.FindIndex(o => o.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _own[at] = parameter; else _own.Add(parameter);
        row.Show(parameter, true);
        if (row.IsTexture) _ = ThumbnailAsync(row);
        Changed();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MaterialValueRow row) return;
        _own.RemoveAll(o => o.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
        if (_material!.Inherited.FirstOrDefault(i => i.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase)) is { } inherited)
        {
            row.Show(inherited, false);
            if (row.IsTexture) _ = ThumbnailAsync(row);
        }
        else
        {
            _rows.Remove(row);
            _view?.Refresh();
        }
        Changed();
    }

    private bool IsChanged => _own.Count != _savedOwn.Count
        || _own.Any(o => _savedOwn.FirstOrDefault(s => s.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase)) is not { } s
                         || MaterialParameterItem.Format(s.Value) != MaterialParameterItem.Format(o.Value));

    private void Changed()
    {
        bool changed = IsChanged;
        SaveButton.IsEnabled = changed && !IsGraph;
        RevertButton.IsEnabled = changed;
        Title = $"{(changed ? "• " : "")}{Path.GetFileName(_path)} - Material";
        UpdateNote();
        _relook.Stop();
        _relook.Start();
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => Revert();

    private void Revert()
    {
        _own = [.. _savedOwn];
        ShowRows();
        Changed();
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MaterialValueRow row) row.ColourOpen = !row.ColourOpen;
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private async void PickTexture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MaterialValueRow row) return;
        PreviewNote.Text = "Listing the textures…";
        _allTextures ??= await Task.Run(() => _locator.AllPaths(".xbm"));
        PreviewNote.Text = "";
        var near = new[] { Path.GetDirectoryName(GamePath) ?? "" }
            .Concat(_rows.Select(r => r.TexturePath).OfType<string>().Select(p => Path.GetDirectoryName(p) ?? ""))
            .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Picker.Show($"{row.Name}: type part of a texture's path", query =>
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> found = words.Length == 0
                ? _allTextures.Where(p => near.Any(f => (Path.GetDirectoryName(p) ?? "").Equals(f, StringComparison.OrdinalIgnoreCase)))
                : _allTextures.Where(p => words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(p => near.Any(f => (Path.GetDirectoryName(p) ?? "").Equals(f, StringComparison.OrdinalIgnoreCase)));
            return [.. found.Take(400).Select(p => new QuickPickItem("", Path.GetFileName(p), Path.GetDirectoryName(p) ?? "", p))];
        }, chosen =>
        {
            if (chosen.Tag is string path) ParameterChanged(row, new TextureRef(path));
        }, "With nothing typed, the textures beside the material and those it uses.");
    }

    private void OpenTexture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = DataViewers.OpenLinkAsync(path, _locator, this, this);
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = DataViewers.OpenLinkAsync(path, _locator, this, this);
    }

    private void Properties_LinkClicked(object? sender, string path) => _ = DataViewers.OpenLinkAsync(path, _locator, this, this);

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

    // ---------------------------------------------------------------- saving into the project

    private async Task<WorkbenchProject?> ProjectAsync() =>
        Application.Current.MainWindow is MainWindow main
            ? await main.EnsureProjectAsync("Changes are saved into a mod's project: each file at its game path in the project's workspace, which Build packs into the mod, so the mod uses it in place of the game's. The files they came from are never changed.", Message)
            : ProjectWorkspace.Current;

    /// <summary>Saves the changes at the material's own game path in the project, so every mesh that uses it changes in the mod.</summary>
    private async Task<bool> SaveAsync()
    {
        if (IsGraph || !IsChanged) return true;
        if (await ProjectAsync() is not { } project) return false;
        string gamePath = GamePath;
        try
        {
            var bytes = await Task.Run(() => MaterialWriter.EditFile(Cr2wFile.Read(_data), _own));
            string file = project.GameFilePath(gamePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            FileSafety.CheckWrite(file);
            await File.WriteAllBytesAsync(file, bytes);
            _data = bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            await ShowMessage("The changes could not be saved", ex.Message, MessageKind.Warning);
            return false;
        }
        _savedOwn = [.. _own];
        Changed();
        await RefreshProjectAsync(project);
        await ShowMessage($"Saved to {project.Name}", $"{gamePath}\n\nin {project.WorkspacePath}, which Build packs into the mod's bundle: every mesh that uses it changes in the mod.", MessageKind.Info);
        return true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>Saves the material, with its changes, as a new one in the project: based on what this one is (a shader graph's on the graph).</summary>
    private async void SaveNew_Click(object sender, RoutedEventArgs e)
    {
        if (_material is not { } m || _file is null || await ProjectAsync() is not { } project) return;
        string folder = project.GameFilePath(Path.GetDirectoryName(GamePath) ?? "");
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folder = project.Root; }
        var dialog = new SaveFileDialog
        {
            Title = "Save as a new material in the project",
            InitialDirectory = folder,
            FileName = $"{Path.GetFileNameWithoutExtension(_path)}_new.w2mi",
            Filter = "Material instance (*.w2mi)|*.w2mi",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (project.GamePathOf(dialog.FileName) is not { } gamePath)
        {
            await ShowMessage("Pick a place in the project's workspace",
                $"A material goes in the project's workspace, {project.WorkspacePath}, where its place under the folder is its game path.", MessageKind.Warning);
            return;
        }
        try
        {
            // a new instance with this one's parameters: on the same base, or on the graph itself
            var bytes = IsGraph
                ? MaterialWriter.NewFile(_file.Version, m.ExportFlags, GamePath, "CMaterialGraph", _own)
                : MaterialWriter.NewFile(_file.Version, m.ExportFlags, m.BasePath, m.BaseClass, _own);
            FileSafety.CheckWrite(dialog.FileName);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            await ShowMessage("The material could not be saved", ex.Message, MessageKind.Warning);
            return;
        }
        await RefreshProjectAsync(project);
        await ShowMessage($"Saved to {project.Name}", $"{gamePath}\n\nA new material, which meshes can be pointed at (the mesh viewer's Other material…).", MessageKind.Info);
    }

    /// <summary>Lists the project's game files in the tree, adding the workspace to the project the first time.</summary>
    private static async Task RefreshProjectAsync(WorkbenchProject project)
    {
        if (Application.Current.MainWindow is not MainWindow main) return;
        if (!project.Contains(project.WorkspacePath)) await main.AddToProjectAsync([project.WorkspacePath]);
        else await main.ReloadProjectAsync();
    }

    private bool _closeAsked;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAsked || !IsChanged || IsGraph) return;
        e.Cancel = true;
        var choice = await Message.Ask("Save the changes?", "The material has changes that aren't saved into the project.",
            MessageKind.Question, "Save to project", "Leave them");
        if (choice == MessageChoice.Cancel) return;
        if (choice == MessageChoice.Primary && !await SaveAsync()) return;
        _closeAsked = true;
        Close();
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen || Picker.IsOpen) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.F:
                    Tabs.SelectedItem = ParametersTab;
                    FilterBox.Focus();
                    FilterBox.SelectAll();
                    e.Handled = true;
                    return;
                case Key.S:
                    _ = SaveAsync();
                    e.Handled = true;
                    return;
                case Key.Z when e.OriginalSource is not TextBox:
                    Revert();
                    e.Handled = true;
                    return;
            }
        }
        if (e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.F:
                View.FitToView();
                break;
            case Key.P:
                SpinToggle.IsChecked = SpinToggle.IsChecked != true;
                SpinToggle_Click(this, new RoutedEventArgs());
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
