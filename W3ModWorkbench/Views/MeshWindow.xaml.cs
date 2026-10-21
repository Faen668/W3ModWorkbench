using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

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
    // where the mesh was opened from (its archive, or the file on disk), which says whether it's one of the game's own
    private readonly string? _origin;
    private MeshFile? _mesh;

    /// <summary>The mesh as it is saved now: as opened, until changes are saved into the project. Changes are made to it.</summary>
    private MeshFile? _meshFile;
    private readonly List<MeshChunkItem> _chunks = [];
    private readonly List<MeshMaterialItem> _materials = [];
    private readonly Dictionary<string, Task<BitmapSource?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _allTextures, _allMaterials;

    private MeshWindow(string path, string source, Func<byte[]> readData, Func<int, byte[]?> readBuffer, ResourceLocator locator, string? origin)
    {
        InitializeComponent();
        _origin = origin;
        _path = path;
        _source = source;
        _readData = readData;
        _readBuffer = readBuffer;
        _locator = locator;
        Title = ClothFile.IsCloth(path) ? L.T("MeshWindow.ClothTitle", Path.GetFileName(path)) : L.T("MeshWindow.Mesh2", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        FloorToggle.IsChecked = View.ShowFloor = App.Settings.ShowMeshFloor;
        InfoItem.IsChecked = App.Settings.ShowMeshInfo;
        BoundsItem.Click += (_, _) => View.ShowBounds = BoundsItem.IsChecked;
        ColorsItem.Click += (_, _) => View.VertexColors = ColorsItem.IsChecked;
        NormalsItem.Click += (_, _) => View.ShowNormals = NormalsItem.IsChecked;
        BonesItem.Click += (_, _) => View.ShowBones = BonesItem.IsChecked;
        CollisionItem.Click += (_, _) => _ = ShowCollisionAsync(CollisionItem.IsChecked);
        InfoItem.Click += (_, _) => SetInfo(InfoItem.IsChecked);
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Whether a file's name says it's a mesh this window can show: a mesh, or a cloth resource, which REDkit opens as one.</summary>
    public static bool CanShow(string name) => name.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase) || ClothFile.IsCloth(name);

    /// <summary>
    /// Opens a mesh in a window of its own. <paramref name="data"/> is the .w2mesh; its buffers, materials and textures are
    /// looked for with <paramref name="locator"/>, its buffers first beside <paramref name="archive"/>.
    /// </summary>
    public static void Open(string path, string source, byte[] data, IArchive? archive, ResourceLocator locator, Window owner)
    {
        new MeshWindow(path, source, () => data, n => locator.ReadBuffer(path, n, archive), locator, archive?.FilePath).Standalone(owner).Show();
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
            n => File.Exists($"{file}.{n}.buffer") ? File.ReadAllBytes($"{file}.{n}.buffer") : locator.ReadBuffer(depot, n, null), locator, file).Standalone(owner).Show();
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

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        List<MeshMaterial> materials;
        try
        {
            (_mesh, materials) = await Task.Run(() =>
            {
                // a cloth resource's shape is its APEX asset: its own, or the one the game's collision cache keeps for it
                var mesh = ClothFile.IsCloth(_path) ? ClothFile.Read(_readData(), () => _locator.ReadCollision(_path)) : MeshFile.Read(_readData(), _readBuffer);
                return (mesh, mesh.Materials.Select(h => MeshMaterial.Resolve(mesh.File, h, _locator.ReadFile)).ToList());
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            BusyText.Text = L.T("MeshWindow.MeshCouldNotShown", ex.Message);
            return;
        }
        if (_mesh.Chunks.Count == 0)
        {
            BusyText.Text = L.T("MeshWindow.MeshHasNothingDraw");
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        _meshFile = _mesh;
        View.Show(_mesh);

        for (int lod = 0; lod < _mesh.LodCount; lod++)
        {
            var drawn = MeshView.Drawn(_mesh, lod).ToList();
            string distance = lod < _mesh.LodDistances.Count && lod > 0 ? L.T("MeshWindow.M", _mesh.LodDistances[lod]) : "";
            LodBox.Items.Add(L.T("MeshWindow.Triangles", lod, drawn.Sum(c => c.Triangles), distance));
        }
        LodBox.SelectedIndex = 0;
        LodBox.IsEnabled = _mesh.LodCount > 1;

        // a mesh can name more materials than it has, and chunks can share one
        for (int i = 0; i < Math.Max(materials.Count, _mesh.Chunks.Max(c => c.Material) + 1); i++)
        {
            var material = i < materials.Count ? materials[i] : new MeshMaterial { Source = L.T("MeshWindow.MissingMesh"), Bases = [] };
            _materials.Add(new MeshMaterialItem(i, material, item => { _ = ApplyAsync(item); UpdateSave(); }));
        }
        MaterialList.ItemsSource = _materials;
        ShowChunks();
        InfoText.Text = L.T("MeshWindow.Info", _path, _source, _mesh.File.Version, _mesh.Chunks.Count, L.T("Count.Lod", _mesh.LodCount), L.T("MeshWindow.MaterialCount", materials.Count));
        ExportButton.IsEnabled = true;
        Properties.Show(PropertyRow.Of(_mesh.Mesh, _mesh.File, leaveOut: ["cookedData"]));
        _ = ShowRedkitPropertiesAsync();
        ShowLods();
        ShowInfo();
        _ = ReadReportAsync(materials);
        _ = ReadCollisionAsync();
        ShowCloth();
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
            item.Status = item.Current.Diffuse is null ? L.T("MeshWindow.NoColourTextureFound") : L.T("MeshWindow.ShownPlainGrey");
            return;
        }
        item.Status = L.T("MeshWindow.ReadingTexture");
        if (!_textures.TryGetValue(path, out var task))
            _textures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, TextureSize));
        var picture = await task;
        // the material as the game draws it under its colour texture (normal map, masks, detail maps, its numbers); not
        // under another texture picked to look at (a mask, or the normal map itself)
        MaterialLook? look = null;
        if (string.Equals(path, item.Current.Diffuse?.Path, StringComparison.OrdinalIgnoreCase))
        {
            Task<BitmapSource?> Texture(string p)
            {
                if (!_textures.TryGetValue(p, out var t)) _textures[p] = t = Task.Run(() => MeshTextures.Load(_locator, p, TextureSize));
                return t;
            }
            look = await LookTextures.LookOf(item.Current, _locator, Texture);
        }
        if (!ReferenceEquals(item.Selected, choice)) return; // another was picked meanwhile
        View.SetTexture(item.Index, picture, look);
        item.Status = picture is not null ? await Task.Run(() => MeshTextures.Describe(_locator, path))
            : L.T("MeshWindow.PixelsWerentFoundTexture");
    }

    // ---------------------------------------------------------------- toolbar

    private void Lod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mesh is null || LodBox.SelectedIndex < 0 || LodBox.SelectedIndex == View.Lod) return;
        View.Lod = LodBox.SelectedIndex;
        ShowChunks();
        ShowInfo();
    }

    private void Floor_Click(object sender, RoutedEventArgs e) => SetFloor(FloorToggle.IsChecked == true);

    private void SetFloor(bool on)
    {
        FloorToggle.IsChecked = View.ShowFloor = on;
        App.Settings.ShowMeshFloor = on;
        App.Settings.Save();
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        ShowMenu.PlacementTarget = ShowButton;
        ShowMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ShowMenu.IsOpen = true;
    }

    /// <summary>Ticks one of the Show menu's items and does what it says, as a key does.</summary>
    private void Toggle(MenuItem item)
    {
        item.IsChecked = !item.IsChecked;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    // ---------------------------------------------------------------- what's drawn, and the tabs

    private (List<CollisionShape> Shapes, int Count)? _collision;
    private long _textureBytes;
    private int _textureCount;

    /// <summary>Shows or hides the overlay, and remembers which.</summary>
    private void SetInfo(bool on)
    {
        InfoItem.IsChecked = on;
        App.Settings.ShowMeshInfo = on;
        App.Settings.Save();
        ShowInfo();
    }

    /// <summary>The overlay at the top left of the view: what the LOD shown draws, how big the mesh is and the memory its data and textures take.</summary>
    private void ShowInfo()
    {
        InfoPanel.Visibility = App.Settings.ShowMeshInfo && _mesh is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_mesh is null || !App.Settings.ShowMeshInfo) return;
        var (triangles, vertices, chunks) = View.Stats();
        var size = View.Size();
        var lines = new List<string>
        {
            L.T("MeshWindow.Triangles2", triangles),
            L.T("MeshWindow.Vertices", vertices),
            L.T("MeshWindow.Chunks2", chunks),
            L.T("MeshWindow.LodLine", View.Lod) + (_mesh.LodCount > 1 ? L.T("MeshWindow.LodOf", _mesh.LodCount) : ""),
            L.T("MeshWindow.SizeXXM", size.X, size.Y, size.Z),
            L.T("MeshWindow.MeshDataMB", Megabytes(MeshBytes(_mesh))),
        };
        if (_textureCount > 0) lines.Add(L.T("MeshWindow.TexturesMB", Megabytes(_textureBytes), _textureCount));
        if (_mesh.BoneNames.Length > 0) lines.Add(L.T("MeshWindow.Bones", _mesh.BoneNames.Length));
        if (_collision is { } c) lines.Add(L.T("MeshWindow.CollisionShapes", c.Count));
        InfoOverlay.Text = string.Join("\n", lines);
    }

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.000");

    /// <summary>How much the mesh's own data takes: its cooked vertex and index buffers, or else all the buffers it has.</summary>
    private static long MeshBytes(MeshFile mesh) =>
        mesh.Mesh["cookedData"] is RedObject cooked && cooked.Integer("vertexBufferSize") > 0
            ? cooked.Integer("vertexBufferSize") + cooked.Integer("indexBufferSize")
            : mesh.File.Buffers.Sum(b => (long)b.MemorySize);

    /// <summary>
    /// The mesh's properties from REDkit's source file, for one of the game's own meshes when REDkit has it, as it keeps
    /// what cooking leaves out; said above them either way.
    /// </summary>
    private async Task ShowRedkitPropertiesAsync()
    {
        if (_mesh is null) return;
        var (path, origin, mesh) = (_path, _origin, _mesh);
        var from = await Task.Run(() => RedkitSource.For(path, origin));
        if (from.Redkit is { } redkit && RedkitSource.Match(redkit, mesh.Mesh, mesh.File) is { } own)
            Properties.Show(PropertyRow.Of(own, redkit, leaveOut: ["cookedData"]));
        SourceNoteBar.Show(from.Source, from.RedkitPath ?? origin, L.T("MeshWindow.Mesh3"));
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    /// <summary>The LODs tab: each LOD, the distance it's drawn from, and its chunks with their material, kind of vertex and counts.</summary>
    private void ShowLods()
    {
        if (_mesh is null) return;
        LodPanel.Children.Clear();
        for (int lod = 0; lod < _mesh.LodCount; lod++)
        {
            var chunks = MeshView.Drawn(_mesh, lod).ToList();
            string from = lod < _mesh.LodDistances.Count ? L.T("MeshWindow.DrawnM", _mesh.LodDistances[lod]) : "";
            LodPanel.Children.Add(Heading(L.T("MeshWindow.LOD2", lod), L.T("MeshWindow.VerticesTriangles", from, L.T("Count.Chunk", chunks.Count), chunks.Sum(c => c.Positions.Length), chunks.Sum(c => c.Triangles))));
            LodPanel.Children.Add(Table([L.T("MeshWindow.Chunk"), L.T("MeshWindow.Material"), L.T("MeshWindow.Vertices2"), L.T("MeshWindow.Indices"), L.T("MeshWindow.VerticesTriangle")],
                [.. chunks.Select(c => new[]
                {
                    $"{c.Index}" + (c.Skinned ? "  skinned" : ""),
                    MaterialName(c.Material),
                    $"{c.Positions.Length:N0}",
                    $"{c.Indices.Length:N0}",
                    c.Triangles > 0 ? $"{(double)c.Positions.Length / c.Triangles:0.00}" : "",
                })]));
        }
    }

    private string MaterialName(int index) =>
        index < _materials.Count ? $"{index}  {_materials[index].Current.Shader}" : $"{index}";

    /// <summary>
    /// The Report tab, as REDkit's mesh editor has it: what the mesh takes in memory, each LOD's chunks and the share of its
    /// triangles each material draws, and every texture the materials use with its size and the memory it takes.
    /// </summary>
    private async Task ReadReportAsync(List<MeshMaterial> materials)
    {
        if (_mesh is null) return;
        var mesh = _mesh;
        // every texture the materials use, each once, with which materials use it
        var used = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < materials.Count; i++)
            foreach (var t in materials[i].Textures.Where(t => t.Path.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase)))
            {
                if (!used.TryGetValue(t.Path, out var list)) used[t.Path] = list = [];
                if (!list.Contains(i)) list.Add(i);
            }
        var textures = await Task.Run(() => used.Select(u => (Path: u.Key, Users: u.Value, Entry: _locator.FindTexture(u.Key))).ToList());
        _textureCount = textures.Count(t => t.Entry is not null);
        _textureBytes = textures.Sum(t => (long)(t.Entry?.Size ?? 0));
        ShowInfo();

        ReportPanel.Children.Clear();
        ReportPanel.Children.Add(Heading(L.T("MeshWindow.Totals"), ""));
        ReportPanel.Children.Add(Lines(
            L.T("MeshWindow.MeshDataMB2", Megabytes(MeshBytes(mesh))),
            L.T("MeshWindow.TextureData", Megabytes(_textureBytes), L.T("Count.Texture", _textureCount)) + (textures.Count > _textureCount ? L.T("MeshWindow.NotFoundCount", textures.Count - _textureCount) : ""),
            L.T("MeshWindow.DrawnUpTo", mesh.Mesh["autoHideDistance"] is float hide ? L.T("MeshWindow.Metres", hide) : L.T("MeshWindow.DefaultDistance")),
            L.T("MeshWindow.BoundingBoxXX", View.Size().X, View.Size().Y, View.Size().Z)));
        for (int lod = 0; lod < mesh.LodCount; lod++)
        {
            var chunks = MeshView.Drawn(mesh, lod).ToList();
            int triangles = chunks.Sum(c => c.Triangles);
            ReportPanel.Children.Add(Heading(L.T("MeshWindow.LOD2", lod), L.T("MeshWindow.VerticesTriangles2", L.T("Count.Chunk", chunks.Count), chunks.Sum(c => c.Positions.Length), triangles)));
            ReportPanel.Children.Add(Table([L.T("MeshWindow.Material"), L.T("MeshWindow.Triangles3"), L.T("MeshWindow.Share")],
                [.. chunks.GroupBy(c => c.Material).Select(g => new[]
                {
                    MaterialName(g.Key),
                    $"{g.Sum(c => c.Triangles):N0}",
                    triangles > 0 ? $"{100.0 * g.Sum(c => c.Triangles) / triangles:0.0}%" : "",
                })]));
        }
        ReportPanel.Children.Add(Heading(L.T("Common.Textures"), L.T("Count.Texture", textures.Count)));
        foreach (var t in textures.OrderByDescending(t => t.Entry?.Size ?? 0))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var picture = new Image { Width = 72, Height = 72, Margin = new Thickness(0, 0, 10, 0), Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(picture, Dock.Left);
            row.Children.Add(picture);
            var text = (StackPanel)Lines(
                t.Entry is { } entry ? $"{entry.FormatText}  ·  {Megabytes(entry.Size)} MB" : L.T("MeshWindow.NotFoundAnyTexture"),
                L.T("MeshWindow.UsedMaterial", string.Join(", ", t.Users)));
            // the path, and the picture, open the texture in its viewer
            var name = new TextBlock { Text = t.Path, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) };
            if (t.Entry is { } found)
            {
                name.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                name.Cursor = picture.Cursor = Cursors.Hand;
                name.ToolTip = picture.ToolTip = L.T("MeshWindow.OpenTextureViewer");
                string path = t.Path;
                name.MouseLeftButtonUp += (_, _) => TextureWindow.Open(found, this, () => _locator.ReadFile(path), _locator);
                picture.MouseLeftButtonUp += (_, _) => TextureWindow.Open(found, this, () => _locator.ReadFile(path), _locator);
            }
            text.Children.Insert(0, name);
            row.Children.Add(text);
            ReportPanel.Children.Add(row);
            if (t.Entry is not null) _ = Thumbnail(t.Path, picture);
        }
    }

    private async Task Thumbnail(string path, Image into)
    {
        if (!_textures.TryGetValue(path, out var task))
            _textures[path] = task = Task.Run(() => MeshTextures.Load(_locator, path, TextureSize));
        into.Source = await task;
    }

    /// <summary>Reads the mesh's collision from the game's collision caches, for the Collision tab and the overlay.</summary>
    private async Task ReadCollisionAsync()
    {
        // a cloth resource's entry in the collision cache is its APEX asset; what it collides with is its bones' spheres
        if (_mesh?.Cloth is { } cloth)
        {
            ShowClothCollision(cloth);
            return;
        }
        var data = await Task.Run(() => _locator.ReadCollision(_path));
        _collision = data is null ? null : await Task.Run(() => MeshCollision.Read(data));
        CollisionPanel.Children.Clear();
        if (_collision is not { } found)
        {
            CollisionPanel.Children.Add(Lines(L.T("MeshWindow.MeshHasNoCollision"),
                L.T("MeshWindow.MeshOnlyCollidesWhen")));
            CollisionItem.IsEnabled = false;
            ShowInfo();
            return;
        }
        CollisionPanel.Children.Add(Heading(L.T("MeshWindow.Shapes"), L.T("Count.Shape", found.Count)));
        CollisionPanel.Children.Add(Table([L.T("MeshWindow.Shape"), L.T("Common.Kind"), L.T("MeshWindow.Vertices2"), L.T("MeshWindow.Triangles3"), L.T("MeshWindow.Material")],
            [.. found.Shapes.Select((s, i) => new[] { $"{i}", s.Kind, $"{s.Vertices.Length:N0}", $"{s.Triangles.Length / 3:N0}", s.Material })]));
        if (found.Count > found.Shapes.Count)
            CollisionPanel.Children.Add(Lines(L.T("MeshWindow.MoreShapeBoxesSpheres", found.Count - found.Shapes.Count)));
        CollisionPanel.Children.Add(Lines(L.T("MeshWindow.ShowCollisionCDraws")));
        ShowInfo();
    }

    private async Task ShowCollisionAsync(bool on)
    {
        if (_mesh?.Cloth is not null)
        {
            ShowClothShapes(on);
            return;
        }
        if (on && _collision is null) await ReadCollisionAsync();
        View.ShowCollision(on && _collision is { } c ? [.. c.Shapes.Select(s => (s.Vertices, s.Triangles))] : []);
    }

    // ---------------------------------------------------------------- building the tabs' contents

    private static FrameworkElement Heading(string title, string detail)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
        if (detail.Length > 0)
        {
            var small = new TextBlock { Text = detail, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            small.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            panel.Children.Add(small);
        }
        return panel;
    }

    private static FrameworkElement Lines(params string[] lines)
    {
        var panel = new StackPanel();
        foreach (string line in lines)
            panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
        return panel;
    }

    /// <summary>A small table: a header row, then a row for each, the columns as wide as they need.</summary>
    private static FrameworkElement Table(string[] headers, List<string[]> rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int c = 0; c < headers.Length; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        for (int r = 0; r <= rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = r == 0 ? headers : rows[r - 1];
            for (int c = 0; c < headers.Length && c < cells.Length; c++)
            {
                var cell = new TextBlock { Text = cells[c], Margin = new Thickness(0, 1, 14, 1), TextTrimming = TextTrimming.CharacterEllipsis };
                if (r == 0)
                {
                    cell.FontSize = 11.5;
                    cell.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                }
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
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
            case Key.G: SetFloor(!View.ShowFloor); break;
            case Key.B: Toggle(BoundsItem); break;
            case Key.V: Toggle(ColorsItem); break;
            case Key.N: Toggle(NormalsItem); break;
            case Key.K: Toggle(BonesItem); break;
            case Key.C when CollisionItem.IsEnabled: Toggle(CollisionItem); break;
            case Key.I: SetInfo(!App.Settings.ShowMeshInfo); break;
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
        item.Status = L.T("MeshWindow.ListingTextures");
        _allTextures ??= await Task.Run(() => _locator.AllPaths(".xbm"));
        item.Status = "";
        var near = NearFolders(item).ToList();
        Picker.Show(L.T("MeshWindow.TextureMaterialTypePart", item.Index), q => Filter(_allTextures, q, "", near), chosen =>
        {
            if (chosen.Tag is string path) item.UseTexture(path);
        }, L.T("MeshWindow.TexturesOpenFilesFolder", _allTextures.Count));
    }

    private async void OtherMaterial_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeshMaterialItem item) return;
        item.Status = L.T("MeshWindow.ListingMaterials");
        _allMaterials ??= await Task.Run(() => _locator.AllPaths(".w2mi"));
        item.Status = "";
        // a mesh's materials are usually in a materials folder beside its model folder
        var near = NearFolders(item).Concat(NearFolders(item).Select(f => Path.Combine(FolderOf(f), "materials"))).ToList();
        Picker.Show(L.T("MeshWindow.MaterialMaterialTypePart", item.Index), q => Filter(_allMaterials, q, "", near), async chosen =>
        {
            if (chosen.Tag is not string path) return;
            item.Status = L.T("MeshWindow.ReadingMaterial");
            var material = await Task.Run(() => MeshMaterial.FromFile(path, _locator.ReadFile));
            item.Use(material);
        }, L.T("MeshWindow.MaterialInstancesOpenFiles", _allMaterials.Count));
    }

    private void ResetMaterial_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MeshMaterialItem item) item.Reset();
    }

    // ---------------------------------------------------------------- changing materials

    private void UpdateSave()
    {
        SaveButton.IsEnabled = _materials.Any(m => m.NeedsSave) || ClothNeedsSave;
        Title = ClothFile.IsCloth(_path) ? L.T("MeshWindow.ClothTitle", (SaveButton.IsEnabled ? "• " : "") + Path.GetFileName(_path))
            : L.T("MeshWindow.Mesh4", (SaveButton.IsEnabled ? "• " : ""), Path.GetFileName(_path));
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

    private void OpenMaterial_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string path) _ = DataViewers.OpenLinkAsync(path, _locator, this, this);
    }

    private async void PickParameterTexture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MaterialParameterItem parameter || OwnerOf(parameter) is not { } material) return;
        material.Status = L.T("MeshWindow.ListingTextures");
        _allTextures ??= await Task.Run(() => _locator.AllPaths(".xbm"));
        material.Status = "";
        var near = NearFolders(material).ToList();
        Picker.Show(L.T("MeshWindow.MaterialTypePartTextures", parameter.Name, material.Index), q => Filter(_allTextures, q, "", near), chosen =>
        {
            if (chosen.Tag is string path) material.SetParameter(parameter.Parameter, new TextureRef(path));
        }, L.T("MeshWindow.NothingTypedTexturesBeside"));
    }

    // ---------------------------------------------------------------- saving into the project

    /// <summary>
    /// The open project, which changes are saved into. With none open, asks to make one or open one: changes are never
    /// saved over the files they came from, only into a mod's project.
    /// </summary>
    private async Task<WorkbenchProject?> ProjectAsync() =>
        Application.Current.MainWindow is MainWindow main
            ? await main.EnsureProjectAsync(L.T("MeshWindow.ChangesSavedIntoMods"), Message)
            : ProjectWorkspace.Current;

    /// <summary>Asks where in the project a new material goes, beside the mesh's own place by default. Its game path, or null.</summary>
    private string? AskNewMaterialPath(WorkbenchProject project, MeshMaterialItem item)
    {
        string folder = project.GameFilePath(Path.GetDirectoryName(_path) ?? "");
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folder = project.Root; }
        var dialog = new SaveFileDialog
        {
            Title = L.T("MeshWindow.SaveNewMaterialProject"),
            InitialDirectory = folder,
            FileName = $"{Path.GetFileNameWithoutExtension(_path)}_{item.Index}.w2mi",
            Filter = L.T("MeshWindow.MaterialInstanceW2miW2mi"),
        };
        if (dialog.ShowDialog(this) != true) return null;
        if (project.GamePathOf(dialog.FileName) is { } gamePath) return gamePath;
        _ = ShowMessage(L.T("MeshWindow.PickPlaceProjectsWorkspace"),
            L.T("MeshWindow.MaterialGoesProjectsWorkspace", project.WorkspacePath), MessageKind.Warning);
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
            await ShowMessage(L.T("MeshWindow.MaterialCouldNotSaved"), ex.Message, MessageKind.Warning);
            return;
        }
        item.Use(await Task.Run(() => MeshMaterial.FromFile(gamePath, _locator.ReadFile)));
        item.Status = L.T("MeshWindow.SavedSaveProjectMake", gamePath);
        await RefreshProjectAsync(project);
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    /// <summary>
    /// Saves the changes into the open project: the mesh, when its own materials or its material list changed (with its
    /// buffers beside it), and the .w2mi files that were changed or made. False when nothing was saved.
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (_meshFile is null || !_materials.Any(m => m.NeedsSave) && !ClothNeedsSave) return false;
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
                    var choice = await Message.Ask(L.T("MeshWindow.MaterialOwn", Path.GetFileName(material.Source)),
                        L.T("MeshWindow.OtherMeshesCanUse"),
                        MessageKind.Question, L.T("MeshWindow.NewMaterialMesh"), L.T("MeshWindow.ChangeEveryMesh"));
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
                            ?? throw new FileNotFoundException(L.T("MeshWindow.WasntFoundChange", material.Source));
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
            if (inline.Count > 0 || slots.Count > 0) mesh = MaterialWriter.EditMesh(_meshFile, inline, slots);
            // a cloth's simulation settings are the resource's own properties
            if (ClothNeedsSave)
            {
                var (material, thickness) = (_clothMaterial!, _clothThickness);
                var from = mesh ?? _readData();
                mesh = await Task.Run(() => ClothSettings.Write(from, ClothSettings.Of(material, thickness)));
            }
            if (mesh is not null) writes.Add((_path, mesh));

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
                _meshFile = ClothFile.IsCloth(_path) ? ClothFile.Read(mesh, () => _locator.ReadCollision(_path)) : MeshFile.Read(mesh, _readBuffer);
                ClothSaved();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            await ShowMessage(L.T("MeshWindow.ChangesCouldNotSaved"), ex.Message, MessageKind.Warning);
            return false;
        }

        foreach (var (item, gamePath) in saved)
            item.Saved(gamePath is null ? item.Current : await Task.Run(() => MeshMaterial.FromFile(gamePath, _locator.ReadFile)));
        UpdateSave();
        await RefreshProjectAsync(project);
        await ShowMessage(L.T("MeshWindow.Saved", project.Name),
            L.T("MeshWindow.WhichBuildPacksInto", string.Join("\n", writes.Select(w => w.GamePath)), project.WorkspacePath), MessageKind.Info);
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
        if (_closeAsked || !_materials.Any(m => m.NeedsSave) && !ClothNeedsSave) return;
        e.Cancel = true;
        var choice = await Message.Ask(ClothNeedsSave ? L.T("MeshWindow.ClothSaveChanges") : L.T("MeshWindow.SaveChangesMaterials"), L.T("MeshWindow.TheyreLostIfWindow"),
            MessageKind.Question, L.T("Common.SaveProject"), L.T("Common.DontSave"));
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
            Title = L.T("MeshWindow.ExportMesh"),
            FileName = name + (View.Lod > 0 ? $"_lod{View.Lod}" : "") + ".obj",
            Filter = L.T("MeshWindow.WavefrontOBJObjObj"),
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
            await ShowMessage(L.T("MeshWindow.MeshExported"), L.T("MeshWindow.ExportSaved", Path.GetFileName(dialog.FileName)) + (pictures > 0 ? L.T("MeshWindow.WithTextures", pictures) : "."), MessageKind.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage(L.T("MeshWindow.MeshCouldNotExported"), ex.Message, MessageKind.Warning);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }
}
