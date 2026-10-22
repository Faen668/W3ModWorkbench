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
using W3ModWorkbench.Localisation;

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
    private List<(MeshFile Mesh, Matrix4x4 Place)> _previewParts = [];
    private HashSet<(int Part, int Material)> _ours = [];
    private List<string> _users = [], _entities = [];

    // where the index found each user (a mod's or a DLC's bundle, REDkit's r4data), and the browser that indexed them,
    // to read one the game's own bundles haven't got
    private readonly Dictionary<string, ReferenceSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private AssetBrowserWindow? _browser;
    private List<string>? _allModels;
    private Shape? _shown;
    private CancellationTokenSource? _shapeLoad;
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

    private enum ShapeKind { Shape, Model, Pick }

    /// <summary>What to preview the material on: one of the game's shapes, a mesh or an entity, or picking one.</summary>
    private sealed record Shape(string Label, string Path, ShapeKind Kind);

    private sealed record ChainItem(string Name, string Detail, string Count, string Path, bool CanOpen);

    private sealed record UserRow(string Path, string? Archive = null)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => (System.IO.Path.GetDirectoryName(Path) ?? "") + (Archive is { } a ? L.T("MaterialWindow.In", a) : "");
        public string Kind => FileKinds.Of(Path)?.Name ?? System.IO.Path.GetExtension(Path);
    }

    // shapes the game itself has, so the preview needs nothing but the game
    private static Shape[] Shapes =>
    [
        new(L.T("MaterialWindow.Sphere"), @"engine\reference_level\level_assets\tags\materials\sphere.w2mesh", ShapeKind.Shape),
        new(L.T("MaterialWindow.Box"), @"engine\meshes\editor\box.w2mesh", ShapeKind.Shape),
    ];

    private MaterialWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator, string? origin, IArchive? archive, string? diskFile)
    {
        InitializeComponent();
        MakeModBtn.Show(path, diskFile ?? origin, readData);
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        _origin = origin;
        _archive = archive;
        _diskFile = diskFile;
        Title = L.T("MaterialWindow.Material2", Path.GetFileName(path));
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

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

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
            BusyText.Text = L.T("MaterialWindow.MaterialCouldNotRead", ex.Message);
            return;
        }
        var m = _material;
        _own = [.. m.Own];
        _savedOwn = [.. m.Own];
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        TitleText.Text = Path.GetFileName(_path);
        string kind = IsGraph ? L.T("MaterialWindow.ShaderGraph") : L.T("MaterialWindow.MaterialInstance");
        string based = m.BasePath is { } b && !IsGraph ? L.T("MaterialWindow.Based2", Path.GetFileName(b)) : "";
        DetailText.Text = L.T("MaterialWindow.Detail", kind, m.Shader, based)
            + (IsGraph ? L.T("MaterialWindow.GraphParameters", L.T("Count.Parameter", m.Inherited.Count))
                       : L.T("MaterialWindow.OwnParameters", L.T("Count.Parameter", m.Own.Count), m.Inherited.Count));
        InfoText.Text = L.T("MaterialWindow.CR2W", _path, _source, _file!.Version);
        SaveButton.ToolTip = IsGraph ? L.T("MaterialWindow.ShaderGraphIsntChanged") : SaveButton.ToolTip;
        ShowRows();
        ShowChain();
        var resource = _file.First(IsGraph ? "CMaterialGraph" : "CMaterialInstance");
        if (resource is not null) FileProperties.Show(PropertyRow.Of(resource, _file));
        _ = LoadRedkitAsync();
        ShowShapes(Shapes[0]);
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
            .OrderBy(r => r.GroupOrder).ThenBy(r => r.IsOwn ? 0 : 1)];
        _view = new ListCollectionView(_rows) { Filter = Shown };
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MaterialValueRow.Group)));
        ParameterList.ItemsSource = _view;
        ParametersTab.Header = L.T("MaterialWindow.Parameters2", _rows.Count);
        UpdateNote();
        foreach (var row in _rows) _ = ThumbnailAsync(row);
    }


    private void UpdateNote()
    {
        int own = _rows.Count(r => r.IsOwn);
        ParametersNote.Text = IsGraph
            ? L.T("MaterialWindow.ParametersShaderDeclaresTheir")
            : L.T("MaterialWindow.SetMaterialBoldRest", own);
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
            new(Path.GetFileName(_path), IsGraph ? L.T("MaterialWindow.ShaderGraph2") : L.T("MaterialWindow.Material3"), L.T("Count.Parameter", _own.Count) + (IsGraph ? "" : L.T("MaterialWindow.SetHere")), _path, false),
        };
        foreach (var basePath in m.Bases)
        {
            string name = Path.GetFileName(basePath);
            int gives = m.Inherited.Count(p => p.From.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool graph = basePath.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase);
            items.Add(new(name, (graph ? L.T("MaterialWindow.ShaderGraphDeclaresEvery") : L.T("MaterialWindow.MaterialInstance")) + $"  ·  {basePath}",
                L.T("MaterialWindow.Gives", gives), basePath, true));
        }
        ChainList.ItemsSource = items;
        ChainTab.Header = L.T("MaterialWindow.Based3", m.Bases.Count);
    }

    /// <summary>Says where the material's properties come from.</summary>
    private async Task LoadRedkitAsync()
    {
        var (path, origin) = (GamePath, _origin);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit?.First(IsGraph ? "CMaterialGraph" : "CMaterialInstance") is { } own) FileProperties.Show(PropertyRow.Of(own, from.Redkit));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, IsGraph ? L.T("MaterialWindow.ShaderGraph3") : L.T("MaterialWindow.Material4"));
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
    /// A file's bytes: from the project, the archives open or the game, else from the mod's or DLC's archive the index
    /// found it in. Null when none has it. Reads from disk, so it belongs off the UI thread.
    /// </summary>
    private byte[]? ReadAnywhere(string path)
    {
        if (_locator.ReadFile(path) is { } data) return data;
        if (!_sources.TryGetValue(path, out var source) || _browser?.Dispatcher.Invoke(() => _browser.ItemFor(source)) is not { } item) return null;
        var entry = item.Entry;
        if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
        using var stream = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return entry.Read(stream);
    }

    /// <summary>The meshes a model is drawn with: a mesh alone, or an entity's in its default appearance, dyed as the game dyes them.</summary>
    private async Task<(List<(MeshFile Mesh, Matrix4x4 Place)> Parts, List<(MeshFile Mesh, (ColorShift? First, ColorShift? Second)? Coloring)> Coloured)?> ReadModelAsync(string path, CancellationToken token)
    {
        if (path.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase))
        {
            var entity = await Task.Run(() =>
            {
                var read = Core.Entities.EntityFile.Read(ReadAnywhere(path) ?? throw new FileNotFoundException(L.T("MaterialWindow.IsntGamesFilesArchives", path)));
                read.FindOrigins(_locator.ReadFile);
                return read;
            }, token);
            var appearance = entity.DefaultAppearance;
            var look = await Task.Run(() => entity.Look(appearance, _locator.ReadFile), token);
            var (parts, drawnBy, _, _) = await Task.Run(() => EntityWindow.ReadMeshes(look, _locator, token), token);
            return (parts, parts.Select((part, p) => (part.Mesh, LookTextures.Coloring(entity, appearance?.Name, drawnBy[p].Name))).ToList());
        }
        var mesh = await Task.Run(() =>
        {
            if (ReadAnywhere(path) is not { } data) return null;
            var archive = _locator.Find(path)?.Archive;
            return MeshFile.Read(data, n => _locator.ReadBuffer(path, n, archive));
        }, token);
        return mesh is null ? null : ([(mesh, Matrix4x4.Identity)], [(mesh, null)]);
    }

    /// <summary>
    /// Draws a model with the material on it: a sphere or a box with it on every chunk; a mesh or an entity that uses it
    /// with it where it's used (the rest in their own materials, dyed as the game dyes them); any other model picked with
    /// it on everything.
    /// </summary>
    private async Task ShowShapeAsync(Shape shape)
    {
        if (shape.Kind == ShapeKind.Pick)
        {
            await PickModelAsync();
            return;
        }
        _shapeLoad?.Cancel();
        var token = (_shapeLoad = new CancellationTokenSource()).Token;
        int version = ++_previewVersion;
        ViewMessage.Visibility = Visibility.Collapsed;
        PreviewNote.Text = L.T("MaterialWindow.Reading", Path.GetFileName(shape.Path));
        List<(MeshFile Mesh, Matrix4x4 Place)> parts;
        List<(MeshFile Mesh, (ColorShift? First, ColorShift? Second)? Coloring)> coloured;
        try
        {
            if (await ReadModelAsync(shape.Path, token) is not { } model)
            {
                ViewMessage.Text = L.T("MaterialWindow.IsntGamesFilesArchives2", Path.GetFileName(shape.Path));
                ViewMessage.Visibility = Visibility.Visible;
                return;
            }
            (parts, coloured) = model;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            PreviewNote.Text = L.T("MaterialWindow.CouldNotRead", Path.GetFileName(shape.Path), ex.Message);
            return;
        }
        if (version != _previewVersion) return;
        if (parts.Count == 0)
        {
            ViewMessage.Text = L.T("MaterialWindow.HasNoMeshesDraw", Path.GetFileName(shape.Path));
            ViewMessage.Visibility = Visibility.Visible;
            return;
        }
        // which of its parts' materials are this one: where it's used, or, on a shape or a model that doesn't use it, everywhere
        string self = GamePath;
        var uses = parts.SelectMany((part, p) => Enumerable.Range(0, part.Mesh.Materials.Count)
            .Where(i => part.Mesh.File.Import(part.Mesh.Materials[i])?.Path is { } m && m.Equals(self, StringComparison.OrdinalIgnoreCase)).Select(i => (p, i))).ToHashSet();
        bool everywhere = shape.Kind == ShapeKind.Shape || uses.Count == 0;
        _ours = everywhere ? [.. parts.SelectMany((part, p) => Enumerable.Range(0, part.Mesh.Materials.Count).Select(i => (p, i)))] : uses;
        _previewParts = parts;
        string name = Path.GetFileName(shape.Path);
        PreviewNote.Text = shape.Kind == ShapeKind.Shape ? L.T("MaterialWindow.GamesGameDrawsMaterial", shape.Label.ToLowerInvariant())
            : everywhere ? L.T("MaterialWindow.WhichDoesntUseMaterial", name)
            : L.T("MaterialWindow.MaterialWhereUsedRest", name, L.T("Count.Materials", uses.Count));
        // the model's own materials first, as the game draws them, then this one over them where it goes
        string note = PreviewNote.Text;
        var progress = new Progress<(int Done, int Total)>(n => { if (version == _previewVersion && n.Total > 0) PreviewNote.Text = L.T("MaterialWindow.LoadingTextures", n.Done, n.Total); });
        var textures = shape.Kind == ShapeKind.Shape ? [] : await StageTextures.LoadAsync(coloured, _locator, Texture, progress, token);
        if (version != _previewVersion) return;
        View.ShowParts(parts, textures);
        View.FitToView();
        await ApplyLookAsync();
        if (version == _previewVersion) PreviewNote.Text = note;
    }

    /// <summary>Puts the material, as it is with its changes, on the preview's chunks that have it.</summary>
    private async Task ApplyLookAsync()
    {
        if (_material is null || _previewParts.Count == 0) return;
        int version = ++_lookVersion;
        var current = _material.WithOwn([.. _own]);
        var picture = current.Diffuse?.Path is { } diffuse ? await Texture(diffuse) : null;
        var look = await LookTextures.LookOf(current, _locator, Texture);
        if (version != _lookVersion) return;
        foreach (var (part, material) in _ours) View.SetTexture(part, material, picture, look);
    }

    private void ShapeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ShapeBox.SelectedItem is not Shape shape) return;
        if (shape.Kind != ShapeKind.Pick) _shown = shape;
        _ = ShowShapeAsync(shape);
    }

    /// <summary>The shapes to preview on: the game's sphere and box, picking any model, then the meshes and entities that use the material.</summary>
    private void ShowShapes(Shape? select = null)
    {
        var items = new List<Shape>(Shapes) { new(L.T("MaterialWindow.ViewModel"), "", ShapeKind.Pick) };
        items.AddRange(_users.Where(u => u.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase)).Take(60)
            .Select(u => new Shape(L.T("MaterialWindow.Mesh", Path.GetFileNameWithoutExtension(u)), u, ShapeKind.Model)));
        items.AddRange(_entities.Take(60).Select(u => new Shape(L.T("MaterialWindow.Entity", Path.GetFileNameWithoutExtension(u)), u, ShapeKind.Model)));
        if (select is not null && !items.Contains(select)) items.Add(select);
        _syncing = true;
        var selected = select ?? ShapeBox.SelectedItem as Shape;
        ShapeBox.ItemsSource = items;
        ShapeBox.SelectedItem = selected is null ? items[0] : items.FirstOrDefault(i => i.Path == selected.Path && i.Kind == selected.Kind) ?? items[0];
        _syncing = false;
    }

    /// <summary>Picks any mesh or entity to see the material on, those that use it first.</summary>
    private async Task PickModelAsync()
    {
        // back to what was shown in the list while the pick is open
        ShowShapes(_shown);
        PreviewNote.Text = L.T("MaterialWindow.ListingModels");
        _allModels ??= await Task.Run(() => _locator.AllPaths(".w2mesh").Concat(_locator.AllPaths(".w2ent")).ToList());
        PreviewNote.Text = "";
        var users = _users.Concat(_entities).ToList();
        Picker.Show(L.T("MaterialWindow.MeshEntitySeeMaterial"), query =>
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> found = words.Length == 0 ? users
                : _allModels.Where(p => words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(p => users.Contains(p, StringComparer.OrdinalIgnoreCase));
            return [.. found.Take(400).Select(p => new QuickPickItem(p.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase) ? "" : "", Path.GetFileName(p), Path.GetDirectoryName(p) ?? "", p))];
        }, chosen =>
        {
            if (chosen.Tag is not string path) return;
            var shape = new Shape($"{(path.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase) ? L.T("MaterialWindow.Entity2") : L.T("MaterialWindow.Mesh2"))}: {Path.GetFileNameWithoutExtension(path)}", path, ShapeKind.Model);
            _shown = shape;
            ShowShapes(shape);
            _ = ShowShapeAsync(shape);
        }, L.T("MaterialWindow.NothingTypedMeshesEntities"));
    }

    private void ShapeBox_DropDownOpened(object? sender, EventArgs e) => ShowShapes();

    private void SpinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SpinToggle.IsChecked == true) _spin.Start(); else _spin.Stop();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => View.FitToView();

    // ---------------------------------------------------------------- what uses it

    /// <summary>
    /// The files that use the material: from the Asset Browser's index of what uses what when one is open (and the
    /// entities that use those meshes), else the meshes and materials in the folders round it, where a material's meshes
    /// nearly always are. The meshes and entities are added to the models to preview on.
    /// </summary>
    private async Task FindUsersAsync()
    {
        string self = GamePath;
        UsedByNote.Text = L.T("MaterialWindow.LookingFilesUse");
        var users = new List<string>();
        var entities = new List<string>();
        bool everywhere = false;
        if (Application.Current.Windows.OfType<AssetBrowserWindow>().FirstOrDefault() is { } browser)
        {
            try
            {
                var index = await browser.ReferencesAsync(false);
                _browser = browser;
                foreach (var source in index.UsedBy(self)) _sources.TryAdd(source.Path, source);
                users = [.. index.UsedBy(self).Select(u => u.Path).Distinct(StringComparer.OrdinalIgnoreCase)];
                // the entities that are drawn with those meshes
                var drawnWith = users.Where(u => u.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase)).Take(60)
                    .SelectMany(m => index.UsedBy(m)).Where(u => u.Path.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var source in drawnWith) _sources.TryAdd(source.Path, source);
                entities = [.. drawnWith.Select(u => u.Path).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
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
        _users = [.. users.Order(StringComparer.OrdinalIgnoreCase)];
        _entities = entities;
        UsedByGrid.ItemsSource = _users.Concat(_entities).Select(u => new UserRow(u, ArchiveOf(u))).ToList();
        UsedByTab.Header = L.T("MaterialWindow.Used2", _users.Count + _entities.Count);
        UsedByNote.Text = (users.Count == 0 ? L.T("MaterialWindow.NothingFoundUses") : L.T("MaterialWindow.Use", L.T("Count.File", users.Count)))
            + (entities.Count > 0 ? L.T("MaterialWindow.DrawnThoseMeshes", L.T("Count.Entity", entities.Count)) : "")
            + (everywhere ? L.T("MaterialWindow.AssetBrowsersIndexWhole") : L.T("MaterialWindow.AmongMeshesMaterialsFolders"))
            + (users.Count > 0 ? L.T("MaterialWindow.DoubleClickOneOpen") : "");
        ShowShapes();
    }

    private UserRow? PickedUser => UsedByGrid.SelectedItem as UserRow;

    /// <summary>The archive a user is in, when it isn't one of the game's own bundles (a mod's, a DLC's, REDkit's): its name.</summary>
    private string? ArchiveOf(string path)
    {
        if (!_sources.TryGetValue(path, out var source)) return null;
        string game = GameLocator.Resolve(null) ?? "";
        string content0 = Path.Combine(game, "content", "content0");
        return game.Length > 0 && source.ArchivePath.StartsWith(content0, StringComparison.OrdinalIgnoreCase) ? null
            : source.ArchivePath.StartsWith(game, StringComparison.OrdinalIgnoreCase) && game.Length > 0 ? Path.GetRelativePath(game, source.ArchivePath)
            : Path.GetFileName(source.ArchivePath.TrimEnd('\\', '/'));
    }

    /// <summary>
    /// Opens a file that uses the material: from the game, or, for one only a mod or a DLC has (the index looks in those
    /// too), as the Asset Browser opens it from its archive.
    /// </summary>
    private async Task OpenUserAsync(string path)
    {
        bool inGame = await Task.Run(() => _locator.Find(path) is not null || _locator.ReadFile(path) is not null);
        if (!inGame && _sources.TryGetValue(path, out var source) && _browser?.ItemFor(source) is { } item)
        {
            _browser.OpenFromElsewhere(item);
            return;
        }
        await DataViewers.OpenLinkAsync(path, _locator, this, this);
    }

    private void UsedByGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PickedUser is { } user) _ = OpenUserAsync(user.Path);
    }

    private void OpenUser_Click(object sender, RoutedEventArgs e)
    {
        if (PickedUser is { } user) _ = OpenUserAsync(user.Path);
    }

    private void PreviewOnUser_Click(object sender, RoutedEventArgs e)
    {
        if (PickedUser is not { } user) return;
        if (!user.Path.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase) && !user.Path.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowMessage(null, L.T("MaterialWindow.OnlyMeshEntityCan"), MessageKind.Info);
            return;
        }
        var shape = new Shape($"{(user.Path.EndsWith(".w2ent", StringComparison.OrdinalIgnoreCase) ? L.T("MaterialWindow.Entity2") : L.T("MaterialWindow.Mesh2"))}: {Path.GetFileNameWithoutExtension(user.Path)}", user.Path, ShapeKind.Model);
        _shown = shape;
        ShowShapes(shape);
        _ = ShowShapeAsync(shape);
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
        Title = L.T("MaterialWindow.Material5", (changed ? "• " : ""), Path.GetFileName(_path));
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
        PreviewNote.Text = L.T("MaterialWindow.ListingTextures");
        _allTextures ??= await Task.Run(() => _locator.AllPaths(".xbm"));
        PreviewNote.Text = "";
        var near = new[] { Path.GetDirectoryName(GamePath) ?? "" }
            .Concat(_rows.Select(r => r.TexturePath).OfType<string>().Select(p => Path.GetDirectoryName(p) ?? ""))
            .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Picker.Show(L.T("MaterialWindow.TypePartTexturesPath", row.Name), query =>
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
        }, L.T("MaterialWindow.NothingTypedTexturesBeside"));
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
            ? await main.EnsureProjectAsync(L.T("MaterialWindow.ChangesSavedIntoMods"), Message)
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
            await ShowMessage(L.T("MaterialWindow.ChangesCouldNotSaved"), ex.Message, MessageKind.Warning);
            return false;
        }
        _savedOwn = [.. _own];
        Changed();
        await RefreshProjectAsync(project);
        await ShowMessage(L.T("MaterialWindow.Saved", project.Name), L.T("MaterialWindow.WhichBuildPacksInto", gamePath, project.WorkspacePath), MessageKind.Info);
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
            Title = L.T("MaterialWindow.SaveNewMaterialProject"),
            InitialDirectory = folder,
            FileName = $"{Path.GetFileNameWithoutExtension(_path)}_new.w2mi",
            Filter = L.T("MaterialWindow.MaterialInstanceW2miW2mi"),
        };
        if (dialog.ShowDialog(this) != true) return;
        if (project.GamePathOf(dialog.FileName) is not { } gamePath)
        {
            await ShowMessage(L.T("MaterialWindow.PickPlaceProjectsWorkspace"),
                L.T("MaterialWindow.MaterialGoesProjectsWorkspace", project.WorkspacePath), MessageKind.Warning);
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
            await ShowMessage(L.T("MaterialWindow.MaterialCouldNotSaved"), ex.Message, MessageKind.Warning);
            return;
        }
        await RefreshProjectAsync(project);
        await ShowMessage(L.T("MaterialWindow.Saved", project.Name), L.T("MaterialWindow.NewMaterialWhichMeshes", gamePath), MessageKind.Info);
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
        var choice = await Message.Ask(L.T("Common.SaveChanges"), L.T("MaterialWindow.MaterialHasChangesArent"),
            MessageKind.Question, L.T("Common.SaveProject"), L.T("MaterialWindow.LeaveThem"));
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
