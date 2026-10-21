using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Cutscenes;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Highlighting;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>
/// The preview pane beside a file list, in the main window and the asset browser: the one file highlighted, as a
/// picture, a mesh or an entity's meshes, a cutscene's or a scene's summary, or text.
/// </summary>
public partial class FilePreview : UserControl
{
    private CancellationTokenSource? _cts;

    public FilePreview() => InitializeComponent();

    /// <summary>What finds a file's buffers, materials and textures.</summary>
    public Func<ArchiveEntry, ResourceLocator>? LocatorFor { get; set; }

    /// <summary>The texture an .xbm (or other texture header) describes, which is in a cache; null when none has it.</summary>
    public Func<EntryItem, Task<TextureEntry?>>? TextureFor { get; set; }

    /// <summary>Stops reading the file it was showing.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private Task<TextureEntry?> FindTextureForHeaderAsync(EntryItem item) => TextureFor?.Invoke(item) ?? Task.FromResult<TextureEntry?>(null);

    private ResourceLocator Locator(ArchiveEntry entry) => LocatorFor?.Invoke(entry) ?? new([], entry.Archive, GameLocator.Resolve(null), null, otherMods: false);

    private static Task<byte[]> ReadAsync(ArchiveEntry entry) => MainWindow.ReadAsync(entry);

    private const int MaxPreviewText = 200_000;

    /// <summary>Puts text in the preview editor, coloured as WitcherScript or XML when the file is one.</summary>
    private void SetPreviewText(string text, string? name)
    {
        var view = PreviewText.TextArea.TextView;
        var document = SetPreviewDocument(new TextDocument(text));
        bool dark = TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b && 0.299 * b.Color.R + 0.587 * b.Color.G + 0.114 * b.Color.B > 128;
        string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
        if (ext == ".ws") view.LineTransformers.Insert(0, new WitcherScriptColorizer(document, dark) { View = view });
        else if (ext is ".xml" or ".xsd" or ".config") view.LineTransformers.Insert(0, new XmlColorizer(document, dark) { View = view });
        PreviewText.ScrollToHome();
    }

    /// <summary>
    /// Swaps the preview editor's document, taking the colourers off with the old one: a colourer reads its own
    /// document, and left on it colours the new one's lines with the old one's offsets.
    /// </summary>
    private TextDocument SetPreviewDocument(TextDocument document)
    {
        var view = PreviewText.TextArea.TextView;
        foreach (var old in view.LineTransformers.Where(t => t is WitcherScriptColorizer or XmlColorizer).ToList()) view.LineTransformers.Remove(old);
        PreviewText.Document = document;
        return document;
    }

    private void ShowPreview(string glyph, string? message, EntryPicture? picture = null, string? text = null, string? name = null, MeshFile? mesh = null,
        CutsceneFile? cutscene = null, Core.Scenes.SceneFile? scene = null)
    {
        PreviewScene.Visibility = scene is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(PreviewScene.Scene, scene)) PreviewScene.Show(scene);
        PreviewCutscene.Visibility = cutscene is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(PreviewCutscene.Cutscene, cutscene)) PreviewCutscene.Show(cutscene);
        PreviewMeshHost.Visibility = mesh is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(PreviewMesh.Mesh, mesh)) PreviewMesh.Show(mesh);
        PreviewImageHost.Visibility = picture is not null ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Source = picture?.Image;
        PreviewText.Visibility = text is not null ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) SetPreviewText(text, name);
        else SetPreviewDocument(new TextDocument());
        PreviewEmpty.Visibility = picture is null && text is null && mesh is null && cutscene is null && scene is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewEmptyGlyph.Text = glyph;
        PreviewEmptyText.Text = message ?? "";
    }

    /// <summary>The preview's textures, smaller than the viewers', each read once for the file shown.</summary>
    private static Func<string, Task<System.Windows.Media.Imaging.BitmapSource?>> PreviewTexture(ResourceLocator locator)
    {
        var read = new Dictionary<string, Task<System.Windows.Media.Imaging.BitmapSource?>>(StringComparer.OrdinalIgnoreCase);
        return path =>
        {
            lock (read)
            {
                if (!read.TryGetValue(path, out var task)) read[path] = task = Task.Run(() => MeshTextures.Load(locator, path, 512));
                return task;
            }
        };
    }

    /// <summary>Shows the file highlighted, when there's just one: a picture for textures and picture files, text for text files, and what it is for the rest.</summary>
    public async Task ShowAsync(IReadOnlyList<EntryItem> items)
    {
        Stop();
        if (items.Count != 1)
        {
            PreviewName.Text = items.Count > 1 ? $"{items.Count:N0} files selected" : "";
            PreviewDetail.Text = items.Count > 1 ? Format.Bytes(items.Sum(i => (long)i.Size)) + " in all" : "";
            ShowPreview("\uE8A5", items.Count == 0 ? "Highlight a file to see it here." : "Highlight a single file to see it here.");
            return;
        }
        var item = items[0];
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        PreviewName.Text = item.Name;
        string detail = (item.Kind is { } fileKind ? fileKind.Name + "  ·  " : "") + $"{item.Entry.FormatText}  ·  {item.SizeText}  ·  {item.Folder}";
        PreviewDetail.Text = detail;
        try
        {
            if (item.Entry is TextureEntry || TextureWindow.CanShow(item.Name))
            {
                var picture = await EntryImages.LoadAsync(item.Entry, 1024, token);
                if (token.IsCancellationRequested) return;
                if (picture is null) ShowPreview(item.Glyph, "This picture can't be shown yet.");
                else
                {
                    ShowPreview(item.Glyph, null, picture);
                    PreviewDetail.Text = detail + "  ·  " + picture.Info;
                }
            }
            else if (item.IsTextureHeader)
            {
                ShowPreview(item.Glyph, "Looking for the texture's pixels…");
                var texture = await FindTextureForHeaderAsync(item);
                if (token.IsCancellationRequested) return;
                var picture = texture is null ? null : await Task.Run(() => EntryImages.FromTexture(texture, 1024));
                if (token.IsCancellationRequested) return;
                if (picture is null) ShowPreview(item.Glyph, "Its pixels are in a texture.cache under the same path. Open the mod's texture.cache, or the game's, and they show here.");
                else
                {
                    ShowPreview(item.Glyph, null, picture);
                    PreviewDetail.Text = detail + "  ·  " + picture.Info;
                }
            }
            else if (MeshWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, "Reading the mesh…");
                var entry = item.Entry;
                var locator = Locator(entry);
                var mesh = await Task.Run(() =>
                {
                    byte[] data;
                    if (!entry.Archive.ReadsFromDisk) data = entry.Read(null);
                    else
                    {
                        using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        data = entry.Read(fs);
                    }
                    return MeshFile.Read(data, n => locator.ReadBuffer(entry.Path, n, entry.Archive));
                }, token);
                if (token.IsCancellationRequested) return;
                ShowPreview(item.Glyph, null, mesh: mesh);
                int triangles = MeshView.Drawn(mesh, 0).Sum(c => c.Triangles);
                PreviewDetail.Text = detail + $"  ·  {triangles:N0} triangles, {mesh.LodCount} LOD{(mesh.LodCount == 1 ? "" : "s")}  ·  double-click to open";
                // the colour textures, smaller than the viewer's, each put on as soon as it's read
                for (int i = 0; i < mesh.Materials.Count; i++)
                {
                    int material = i;
                    var (picture, look, leaveOut) = await Task.Run(() => LookTextures.ForMaterial(mesh, material, locator, PreviewTexture(locator)), token);
                    if (token.IsCancellationRequested || !ReferenceEquals(PreviewMesh.Mesh, mesh)) return;
                    if (leaveOut) PreviewMesh.LeaveOut(0, material);
                    else PreviewMesh.SetTexture(material, picture, look);
                }
            }
            else if (CutsceneWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, "Reading the cutscene…");
                var entry = item.Entry;
                CutsceneFile cutscene;
                try
                {
                    byte[] data = await ReadAsync(entry);
                    cutscene = await Task.Run(() => CutsceneFile.Read(data), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, $"This cutscene could not be read.\n\n{ex.Message}\n\nIt can still be checked and extracted.");
                    return;
                }
                if (token.IsCancellationRequested) return;
                ShowPreview(item.Glyph, null, cutscene: cutscene);
                PreviewDetail.Text = detail + "  ·  double-click to open";
            }
            else if (EntityWindow.CanShow(item.Name))
            {
                // its meshes put together, in its first appearance
                ShowPreview(item.Glyph, "Reading the entity…");
                var locator = Locator(item.Entry);
                Core.Entities.EntityFile entity;
                List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts;
                List<Core.Entities.EntityComponent> drawnBy;
                try
                {
                    byte[] data = await ReadAsync(item.Entry);
                    entity = await Task.Run(() => Core.Entities.EntityFile.Read(data), token);
                    var look = await Task.Run(() => entity.Look(entity.DefaultAppearance, locator.ReadFile), token);
                    (parts, drawnBy, _, _) = await Task.Run(() => EntityWindow.ReadMeshes(look, locator, token), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, $"This entity could not be read.\n\n{ex.Message}\n\nIt can still be checked and extracted.");
                    return;
                }
                if (token.IsCancellationRequested) return;
                string what = $"{entity.EntityClass ?? "Entity"}  ·  {Format.Count(entity.Components.Count, "component")}"
                            + (entity.Appearances.Count > 0 ? $", {Format.Count(entity.Appearances.Count, "appearance")}" : "");
                if (parts.Count == 0)
                {
                    ShowPreview(item.Glyph, $"{what}\n\nIt has no meshes to show. Double-click to see its components.");
                    return;
                }
                ShowPreview(item.Glyph, null, mesh: parts[0].Mesh);
                PreviewMesh.ShowParts(parts);
                PreviewDetail.Text = detail + $"  ·  {what}, {Format.Count(parts.Count, "mesh", "meshes")}  ·  double-click to open";
                var texture = PreviewTexture(locator);
                for (int p = 0; p < parts.Count; p++)
                {
                    var coloring = LookTextures.Coloring(entity, entity.DefaultAppearance?.Name, drawnBy[p].Name);
                    for (int m = 0; m < parts[p].Mesh.Materials.Count; m++)
                    {
                        int part = p, material = m;
                        var mesh = parts[p].Mesh;
                        var (picture, look, leaveOut) = await Task.Run(() => LookTextures.ForMaterial(mesh, material, locator, texture, coloring), token);
                        if (token.IsCancellationRequested || !ReferenceEquals(PreviewMesh.Mesh, parts[0].Mesh)) return;
                        if (leaveOut) PreviewMesh.LeaveOut(part, material);
                        else PreviewMesh.SetTexture(part, material, picture, look);
                    }
                }
            }
            else if (SceneWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, "Reading the scene…");
                Core.Scenes.SceneFile scene;
                try
                {
                    byte[] data = await ReadAsync(item.Entry);
                    scene = await Task.Run(() => Core.Scenes.SceneFile.Read(data), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, $"This scene could not be read.\n\n{ex.Message}\n\nIt can still be checked and extracted.");
                    return;
                }
                if (token.IsCancellationRequested) return;
                // shown at once, then again with its lines in words once the game's strings are read
                ShowPreview(item.Glyph, null, scene: scene);
                PreviewDetail.Text = detail + "  ·  double-click to open";
                var (text, note) = await SceneWindow.LinesAsync();
                if (!token.IsCancellationRequested && ReferenceEquals(PreviewScene.Scene, scene)) PreviewScene.Show(scene, text ?? (_ => null), note);
            }
            else if (item.Entry is StringsEntry)
                ShowPreview(item.Glyph, "Double-click to open it in the strings editor.");
            else if (item.IsPreviewable)
            {
                if (item.Size > 4_000_000)
                {
                    ShowPreview(item.Glyph, "Too large to read here. Double-click to open it.");
                    return;
                }
                var entry = item.Entry;
                string text = await Task.Run(() =>
                {
                    byte[] data;
                    if (!entry.Archive.ReadsFromDisk) data = entry.Read(null);
                    else
                    {
                        using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        data = entry.Read(fs);
                    }
                    string decoded = TextDecoding.Decode(data);
                    return decoded.Length > MaxPreviewText ? decoded[..MaxPreviewText] + "\n…" : decoded;
                }, token);
                if (token.IsCancellationRequested) return;
                ShowPreview(item.Glyph, null, text: text, name: item.Name);
            }
            else if (item.Kind is { } kind)
                ShowPreview(item.Glyph, $"{kind.Name}\n\n{kind.Description}\n\nThere's no preview for this kind of file; check it and extract it.");
            else
                ShowPreview(item.Glyph, "No preview for this kind of file. Check it and extract it, or double-click for what the tool can do with it.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!token.IsCancellationRequested) ShowPreview(item.Glyph, "It could not be read: " + ex.Message);
        }
    }
}
