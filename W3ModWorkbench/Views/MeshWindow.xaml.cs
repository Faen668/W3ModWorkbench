using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// Shows a mesh (.w2mesh) in 3D with its textures: each LOD, each chunk on its own, and each material's textures, any of
/// which can be swapped for another texture or another material's while you look. Its materials' parameters can be
/// changed, and the changes saved into the open project at the files' game paths, never over the files they came from.
/// It can be exported as an .obj.
/// </summary>
public partial class MeshWindow : Window, IMessageHost
{
    private const int TextureSize = 2048;

    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly Func<int, byte[]?> _readBuffer;
    private readonly ResourceLocator _locator;
    private MeshFile? _mesh;

    /// <summary>The mesh as it is saved now: as opened, until changes are saved into the project. Changes are made to it.</summary>
    private MeshFile? _meshFile;
    private readonly List<MeshChunkItem> _chunks = [];
    private readonly List<MeshMaterialItem> _materials = [];
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _allTextures, _allMaterials;

    private MeshWindow(string path, string source, Func<byte[]> readData, Func<int, byte[]?> readBuffer, ResourceLocator locator)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _path = path;
        _source = source;
        _readData = readData;
        _readBuffer = readBuffer;
        _locator = locator;
        Title = $"{Path.GetFileName(path)} - Mesh";
        InfoText.Text = $"{path}   ·   {source}";
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Whether a file's name says it's a mesh this window can show.</summary>
    public static bool CanShow(string name) => name.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a mesh in a window of its own. <paramref name="data"/> is the .w2mesh; its buffers, materials and textures are
    /// looked for with <paramref name="locator"/>, its buffers first beside <paramref name="archive"/>.
    /// </summary>
    public static void Open(string path, string source, byte[] data, IArchive? archive, ResourceLocator locator, Window owner)
    {
        new MeshWindow(path, source, () => data, n => locator.ReadBuffer(path, n, archive), locator).Standalone(owner).Show();
    }

    /// <summary>
    /// Opens a mesh file on disk: one in the project's game files, one extracted from a bundle, or REDkit's own. Its
    /// buffers are taken from beside it (<c>name.w2mesh.1.buffer</c>) or, failing that, looked for by its game path:
    /// where it is in the project's workspace, or else the part after <c>r4data</c>, <c>content</c> or <c>files</c>.
    /// </summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner)
    {
        string depot = GamePathOf(file);
        new MeshWindow(depot, file, () => File.ReadAllBytes(file),
            n => File.Exists($"{file}.{n}.buffer") ? File.ReadAllBytes($"{file}.{n}.buffer") : locator.ReadBuffer(depot, n, null), locator).Standalone(owner).Show();
    }

    private static string GamePathOf(string file)
    {
        if (ProjectWorkspace.Current?.GamePathOf(file) is { } inProject) return inProject;
        var parts = Path.GetFullPath(file).Split('\\', '/');
        for (int i = parts.Length - 2; i >= 0; i--)
            if (parts[i].Equals("r4data", StringComparison.OrdinalIgnoreCase) || parts[i].Equals("content", StringComparison.OrdinalIgnoreCase)
                || parts[i].Equals("files", StringComparison.OrdinalIgnoreCase) || parts[i].Equals("depot", StringComparison.OrdinalIgnoreCase))
                return string.Join('\\', parts[(i + 1)..]);
        return Path.GetFileName(file);
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        List<MeshMaterial> materials;
        try
        {
            (_mesh, materials) = await Task.Run(() =>
            {
                var mesh = MeshFile.Read(_readData(), _readBuffer);
                return (mesh, mesh.Materials.Select(h => MeshMaterial.Resolve(mesh.File, h, _locator.ReadFile)).ToList());
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = "The mesh could not be shown.\n\n" + ex.Message;
            return;
        }
        if (_mesh.Chunks.Count == 0)
        {
            BusyText.Text = "This mesh has nothing to draw.";
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        _meshFile = _mesh;
        View.Show(_mesh);

        for (int lod = 0; lod < _mesh.LodCount; lod++)
        {
            var drawn = MeshView.Drawn(_mesh, lod).ToList();
            string distance = lod < _mesh.LodDistances.Count && lod > 0 ? $", from {_mesh.LodDistances[lod]:0.#} m" : "";
            LodBox.Items.Add($"{lod}  ·  {drawn.Sum(c => c.Triangles):N0} triangles{distance}");
        }
        LodBox.SelectedIndex = 0;
        LodBox.IsEnabled = _mesh.LodCount > 1;

        // a mesh can name more materials than it has, and chunks can share one
        for (int i = 0; i < Math.Max(materials.Count, _mesh.Chunks.Max(c => c.Material) + 1); i++)
        {
            var material = i < materials.Count ? materials[i] : new MeshMaterial { Source = "missing from the mesh", Bases = [] };
            _materials.Add(new MeshMaterialItem(i, material, item => { _ = ApplyAsync(item); UpdateSave(); }));
        }
        MaterialList.ItemsSource = _materials;
        ShowChunks();
        InfoText.Text = $"{_path}   ·   {_source}   ·   CR2W {_mesh.File.Version}, {_mesh.Chunks.Count} chunks, {_mesh.LodCount} LOD{(_mesh.LodCount == 1 ? "" : "s")}, {materials.Count} material{(materials.Count == 1 ? "" : "s")}";
        ExportButton.IsEnabled = true;
        DisabledLook.Apply(this);
        foreach (var item in _materials) await ApplyAsync(item);
    }

    /// <summary>The chunks the LOD shown draws, each with a tick to hide it.</summary>
    private void ShowChunks()
    {
        if (_mesh is null) return;
        _chunks.Clear();
        foreach (var chunk in MeshView.Drawn(_mesh, View.Lod))
            _chunks.Add(new MeshChunkItem(chunk, item => View.SetChunkVisible(item.Chunk.Index, item.IsVisible)));
        ChunkList.ItemsSource = null;
        ChunkList.ItemsSource = _chunks;
        View.ShowAllChunks();
    }

    /// <summary>Puts a material's chosen texture on its chunks, reading it the first time it's wanted.</summary>
    private async Task ApplyAsync(MeshMaterialItem item)
    {
        var choice = item.Selected;
        if (choice?.Path is not { } path)
        {
            View.SetTexture(item.Index, null);
            item.Status = item.Current.Diffuse is null ? "No colour texture found for this material." : "Shown in plain grey.";
            return;
        }
        item.Status = "Reading the texture…";
        if (!_textures.TryGetValue(path, out var task))
            _textures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, TextureSize));
        var picture = await task;
        if (!ReferenceEquals(item.Selected, choice)) return; // another was picked meanwhile
        View.SetTexture(item.Index, picture);
        item.Status = picture is not null ? await Task.Run(() => MeshTextures.Describe(_locator, path))
            : "Its pixels weren't found in a texture cache (open the mod's texture.cache, or the game's), or its format can't be read.";
    }

    // ---------------------------------------------------------------- toolbar

    private void Lod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mesh is null || LodBox.SelectedIndex < 0 || LodBox.SelectedIndex == View.Lod) return;
        View.Lod = LodBox.SelectedIndex;
        ShowChunks();
    }

    private void Textured_Click(object sender, RoutedEventArgs e) => View.Textured = TexturedToggle.IsChecked == true;

    private void Wire_Click(object sender, RoutedEventArgs e) => View.Wireframe = WireToggle.IsChecked == true;

    private void Fit_Click(object sender, RoutedEventArgs e) => View.FitToView();

    private void Front_Click(object sender, RoutedEventArgs e) => View.LookFrom(0);

    private void Side_Click(object sender, RoutedEventArgs e) => View.LookFrom(90);

    private void Top_Click(object sender, RoutedEventArgs e) => View.LookFrom(0, top: true);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen || Picker.IsOpen) return;
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // a value being typed counts: it's taken before saving
            if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (SaveButton.IsEnabled) _ = SaveAsync();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBox or ComboBox) return;
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.F: View.FitToView(); break;
            case Key.W: WireToggle.IsChecked = WireToggle.IsChecked != true; View.Wireframe = WireToggle.IsChecked == true; break;
            case Key.T: TexturedToggle.IsChecked = TexturedToggle.IsChecked != true; View.Textured = TexturedToggle.IsChecked == true; break;
            case Key.D1 or Key.NumPad1: View.LookFrom(0); break;
            case Key.D2 or Key.NumPad2: View.LookFrom(90); break;
            case Key.D3 or Key.NumPad3: View.LookFrom(0, top: true); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- swapping materials

    /// <summary>The folder a game path is in, to put a mesh's own textures and materials first in the lists.</summary>
    private static string FolderOf(string path) => Path.GetDirectoryName(path) ?? "";

    private IReadOnlyList<QuickPickItem> Filter(List<string> all, string query, string glyph, IEnumerable<string> nearFolders)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var near = nearFolders.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IEnumerable<string> found = words.Length == 0
            // nothing typed: what's beside the mesh and its materials
            ? all.Where(p => near.Any(f => FolderOf(p).Equals(f, StringComparison.OrdinalIgnoreCase)))
            : all.Where(p => words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => near.Any(f => FolderOf(p).Equals(f, StringComparison.OrdinalIgnoreCase)));
        return [.. found.Take(400).Select(p => new QuickPickItem(glyph, Path.GetFileName(p), FolderOf(p), p))];
    }

    private IEnumerable<string> NearFolders(MeshMaterialItem item) =>
        new[] { FolderOf(_path) }.Concat(item.Current.Textures.Select(t => FolderOf(t.Path))).Concat(item.Own.Textures.Select(t => FolderOf(t.Path)));

    private async void OtherTexture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeshMaterialItem item) return;
        item.Status = "Listing the textures…";
        _allTextures ??= await Task.Run(() => _locator.AllPaths(".xbm"));
        item.Status = "";
        var near = NearFolders(item).ToList();
        Picker.Show($"Texture for material {item.Index}: type part of its path", q => Filter(_allTextures, q, "", near), chosen =>
        {
            if (chosen.Tag is string path) item.UseTexture(path);
        }, $"{_allTextures.Count:N0} textures in the open files, the folder beside this one and the game. With nothing typed, those beside the mesh and its textures.");
    }

    private async void OtherMaterial_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeshMaterialItem item) return;
        item.Status = "Listing the materials…";
        _allMaterials ??= await Task.Run(() => _locator.AllPaths(".w2mi"));
        item.Status = "";
        // a mesh's materials are usually in a materials folder beside its model folder
        var near = NearFolders(item).Concat(NearFolders(item).Select(f => Path.Combine(FolderOf(f), "materials"))).ToList();
        Picker.Show($"Material for material {item.Index}: type part of a .w2mi's path", q => Filter(_allMaterials, q, "", near), async chosen =>
        {
            if (chosen.Tag is not string path) return;
            item.Status = "Reading the material…";
            var material = await Task.Run(() => MeshMaterial.FromFile(path, _locator.ReadFile));
            item.Use(material);
        }, $"{_allMaterials.Count:N0} material instances in the open files, the folder beside this one and the game.");
    }

    private void ResetMaterial_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MeshMaterialItem item) item.Reset();
    }

    // ---------------------------------------------------------------- changing materials

    private void UpdateSave()
    {
        SaveButton.IsEnabled = _materials.Any(m => m.NeedsSave);
        Title = (SaveButton.IsEnabled ? "• " : "") + $"{Path.GetFileName(_path)} - Mesh";
    }

    /// <summary>The material a parameter shown in the list belongs to.</summary>
    private MeshMaterialItem? OwnerOf(MaterialParameterItem parameter) => _materials.FirstOrDefault(m => m.Parameters.Contains(parameter));

    private void ParameterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private void ResetParameter_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MaterialParameterItem parameter && OwnerOf(parameter) is { } material)
            material.ResetParameter(parameter.Parameter);
    }

    private async void PickParameterTexture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MaterialParameterItem parameter || OwnerOf(parameter) is not { } material) return;
        material.Status = "Listing the textures…";
        _allTextures ??= await Task.Run(() => _locator.AllPaths(".xbm"));
        material.Status = "";
        var near = NearFolders(material).ToList();
        Picker.Show($"{parameter.Name} for material {material.Index}: type part of a texture's path", q => Filter(_allTextures, q, "", near), chosen =>
        {
            if (chosen.Tag is string path) material.SetParameter(parameter.Parameter, new TextureRef(path));
        }, "With nothing typed, the textures beside the mesh and the material's own.");
    }

    // ---------------------------------------------------------------- saving into the project

    /// <summary>
    /// The open project, which changes are saved into. With none open, asks to make one or open one: changes are never
    /// saved over the files they came from, only into a mod's project.
    /// </summary>
    private async Task<WorkbenchProject?> ProjectAsync() =>
        Application.Current.MainWindow is MainWindow main
            ? await main.EnsureProjectAsync("Changes are saved into a mod's project: each file at its game path in the project's workspace, which Build packs into the mod, so the mod uses it in place of the game's. The files they came from are never changed.", Message)
            : ProjectWorkspace.Current;

    /// <summary>Asks where in the project a new material goes, beside the mesh's own place by default. Its game path, or null.</summary>
    private string? AskNewMaterialPath(WorkbenchProject project, MeshMaterialItem item)
    {
        string folder = project.GameFilePath(Path.GetDirectoryName(_path) ?? "");
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folder = project.Root; }
        var dialog = new SaveFileDialog
        {
            Title = "Save as a new material in the project",
            InitialDirectory = folder,
            FileName = $"{Path.GetFileNameWithoutExtension(_path)}_{item.Index}.w2mi",
            Filter = "Material instance (*.w2mi)|*.w2mi",
        };
        if (dialog.ShowDialog(this) != true) return null;
        if (project.GamePathOf(dialog.FileName) is { } gamePath) return gamePath;
        _ = ShowMessage("Pick a place in the project's workspace",
            $"A material goes in the project's workspace, {project.WorkspacePath}, where its place under the folder is its game path.", MessageKind.Warning);
        return null;
    }

    /// <summary>A material as a new .w2mi of its own: the same base and the same parameters, with the changes.</summary>
    private byte[] NewMaterialFile(MeshMaterial material) => material.IsGraph
        ? MaterialWriter.NewFile(_meshFile!.File.Version, material.ExportFlags, material.Source, "CMaterialGraph", material.Own)
        : MaterialWriter.NewFile(_meshFile!.File.Version, material.ExportFlags, material.BasePath, material.BaseClass, material.Own);

    private async void SaveNewMaterial_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeshMaterialItem item || _meshFile is null) return;
        if (await ProjectAsync() is not { } project || AskNewMaterialPath(project, item) is not { } gamePath) return;
        try
        {
            string file = project.GameFilePath(gamePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            FileSafety.CheckWrite(file);
            File.WriteAllBytes(file, NewMaterialFile(item.Current));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowMessage("The material could not be saved", ex.Message, MessageKind.Warning);
            return;
        }
        item.Use(await Task.Run(() => MeshMaterial.FromFile(gamePath, _locator.ReadFile)));
        item.Status = $"Saved as {gamePath}. Save to project to make the mesh use it.";
        await RefreshProjectAsync(project);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>
    /// Saves the changes into the open project: the mesh, when its own materials or its material list changed (with its
    /// buffers beside it), and the .w2mi files that were changed or made. False when nothing was saved.
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (_meshFile is null || !_materials.Any(m => m.NeedsSave)) return false;
        if (await ProjectAsync() is not { } project) return false;
        var writes = new List<(string GamePath, byte[] Data)>();
        var inline = new Dictionary<int, List<MaterialParameter>>();
        var slots = new Dictionary<int, string>();
        var saved = new Dictionary<MeshMaterialItem, string?>(); // and what each is afterwards: a .w2mi's game path, or null for the same
        try
        {
            foreach (var item in _materials.Where(m => m.NeedsSave))
            {
                var material = item.Current;
                if (!item.IsEdited)
                {
                    slots[item.Index] = material.Source;
                    saved[item] = null;
                }
                else if (material.InlineExport is int export && !item.IsSwapped)
                {
                    inline[export] = material.Own;
                    saved[item] = null;
                }
                else if (material.IsFile)
                {
                    var choice = await Message.Ask($"{Path.GetFileName(material.Source)} is a material of its own",
                        "Other meshes can use it too. Save the changes as a new material for this mesh only, or change it in the project, so every mesh that uses it changes in the mod?",
                        MessageKind.Question, "New material for this mesh", "Change it for every mesh");
                    if (choice == MessageChoice.Cancel) return false;
                    if (choice == MessageChoice.Primary)
                    {
                        if (AskNewMaterialPath(project, item) is not { } gamePath) return false;
                        writes.Add((gamePath, NewMaterialFile(material)));
                        slots[item.Index] = gamePath;
                        saved[item] = gamePath;
                    }
                    else
                    {
                        var original = await Task.Run(() => _locator.ReadFile(material.Source))
                            ?? throw new FileNotFoundException($"{material.Source} wasn't found to change.");
                        writes.Add((material.Source, MaterialWriter.EditFile(Cr2wFile.Read(original), material.Own)));
                        if (item.IsSwapped) slots[item.Index] = material.Source;
                        saved[item] = material.Source;
                    }
                }
                else
                {
                    // a shader graph has nothing to change: the changes become a new instance of it
                    if (AskNewMaterialPath(project, item) is not { } gamePath) return false;
                    writes.Add((gamePath, NewMaterialFile(material)));
                    slots[item.Index] = gamePath;
                    saved[item] = gamePath;
                }
            }
            byte[]? mesh = null;
            if (inline.Count > 0 || slots.Count > 0)
            {
                mesh = MaterialWriter.EditMesh(_meshFile, inline, slots);
                writes.Add((_path, mesh));
            }

            foreach (var (gamePath, data) in writes)
            {
                string file = project.GameFilePath(gamePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                FileSafety.CheckWrite(file);
                await File.WriteAllBytesAsync(file, data);
            }
            if (mesh is not null)
            {
                // a cooked mesh's shape is in its buffers, which go beside it, as they are
                string file = project.GameFilePath(_path);
                foreach (var buffer in _meshFile.File.Buffers.Where(b => b.Offset == 0))
                {
                    string target = $"{file}.{buffer.Index}.buffer";
                    if (File.Exists(target)) continue;
                    FileSafety.CheckWrite(target);
                    if (await Task.Run(() => _readBuffer(buffer.Index)) is { } bytes) await File.WriteAllBytesAsync(target, bytes);
                }
                _meshFile = MeshFile.Read(mesh, _readBuffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            await ShowMessage("The changes could not be saved", ex.Message, MessageKind.Warning);
            return false;
        }

        foreach (var (item, gamePath) in saved)
            item.Saved(gamePath is null ? item.Current : await Task.Run(() => MeshMaterial.FromFile(gamePath, _locator.ReadFile)));
        UpdateSave();
        await RefreshProjectAsync(project);
        await ShowMessage($"Saved to {project.Name}",
            string.Join("\n", writes.Select(w => w.GamePath)) + $"\n\nin {project.WorkspacePath}, which Build packs into the mod's bundle.", MessageKind.Info);
        return true;
    }

    /// <summary>Lists the project's game files in the tree, adding the workspace to the project the first time.</summary>
    private static async Task RefreshProjectAsync(WorkbenchProject project)
    {
        if (Application.Current.MainWindow is not MainWindow main) return;
        if (!project.Contains(project.WorkspacePath)) await main.AddToProjectAsync([project.WorkspacePath]);
        else await main.ReloadProjectAsync();
    }

    private bool _closeAsked;

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeAsked || !_materials.Any(m => m.NeedsSave)) return;
        e.Cancel = true;
        var choice = await Message.Ask("Save the changes to the materials?", "They're lost if the window closes without saving them into the project.",
            MessageKind.Question, "Save to project", "Don't save");
        if (choice == MessageChoice.Cancel || choice == MessageChoice.Primary && !await SaveAsync()) return;
        _closeAsked = true;
        Close();
    }

    // ---------------------------------------------------------------- exporting

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_mesh is null) return;
        string name = Path.GetFileNameWithoutExtension(_path);
        var dialog = new SaveFileDialog
        {
            Title = "Export the mesh",
            FileName = name + (View.Lod > 0 ? $"_lod{View.Lod}" : "") + ".obj",
            Filter = "Wavefront OBJ (*.obj)|*.obj",
        };
        if (dialog.ShowDialog(this) != true) return;
        var chunks = _chunks.Where(c => c.IsVisible).Select(c => c.Chunk).ToList();
        var wanted = _materials.Where(m => m.Selected?.Path is not null && chunks.Any(c => c.Material == m.Index))
            .ToDictionary(m => m.Index, m => m.Selected!.Path!);
        ExportButton.IsEnabled = false;
        try
        {
            FileSafety.CheckFolder(Path.GetDirectoryName(Path.GetFullPath(dialog.FileName))!);
            var written = await Task.Run(() =>
            {
                var textures = new Dictionary<int, (string, byte[])>();
                foreach (var (material, path) in wanted)
                    if (MeshTextures.Png(_locator, path) is { } png)
                        textures[material] = (Path.GetFileNameWithoutExtension(path) + ".png", png);
                return ObjExport.Write(dialog.FileName, _path, chunks, textures);
            });
            int pictures = written.Count(w => w.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
            await ShowMessage("Mesh exported", $"{Path.GetFileName(dialog.FileName)} and its .mtl were saved" + (pictures > 0 ? $", with {pictures} texture{(pictures == 1 ? "" : "s")} as PNG." : "."), MessageKind.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("The mesh could not be exported", ex.Message, MessageKind.Warning);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }
}
