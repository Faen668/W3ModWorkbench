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
using W3ModWorkbench.Localisation;

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

    /// <summary>
    /// Reads every texture of the meshes shown several at once, as the viewers do, saying how many are done, and puts
    /// them on together once they're all read, rather than one material after another.
    /// </summary>
    private async Task ShowTexturesAsync(List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts,
        IReadOnlyList<(ColorShift? First, ColorShift? Second)?> colorings, ResourceLocator locator, string detail, CancellationToken token)
    {
        var first = parts[0].Mesh;
        var progress = new Progress<(int Done, int Total)>(n =>
        {
            if (!token.IsCancellationRequested && n.Total > 0 && n.Done < n.Total) PreviewDetail.Text = L.T("FilePreview.Textures", detail, n.Done, n.Total);
        });
        List<(int Part, int Material, System.Windows.Media.Imaging.BitmapSource? Picture, MaterialLook? Look, bool LeaveOut)> textures;
        try
        {
            textures = await StageTextures.LoadAsync(parts.Select((p, i) => (p.Mesh, colorings[i])).ToList(), locator, PreviewTexture(locator), progress, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested || !ReferenceEquals(PreviewMesh.Mesh, first)) return;
        PreviewMesh.ShowParts(parts, textures);
        PreviewDetail.Text = detail;
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
            PreviewName.Text = items.Count > 1 ? L.T("FilePreview.FilesSelected", items.Count) : "";
            PreviewDetail.Text = items.Count > 1 ? L.T("FilePreview.All", Format.Bytes(items.Sum(i => (long)i.Size))) : "";
            ShowPreview("\uE8A5", items.Count == 0 ? L.T("FilePreview.HighlightFileSeeHere") : L.T("FilePreview.HighlightSingleFileSee"));
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
                if (picture is null) ShowPreview(item.Glyph, L.T("FilePreview.PictureCantShownYet"));
                else
                {
                    ShowPreview(item.Glyph, null, picture);
                    PreviewDetail.Text = detail + "  ·  " + picture.Info;
                }
            }
            else if (item.IsTextureHeader)
            {
                ShowPreview(item.Glyph, L.T("FilePreview.LookingTexturesPixels"));
                var texture = await FindTextureForHeaderAsync(item);
                if (token.IsCancellationRequested) return;
                var picture = texture is null ? null : await Task.Run(() => EntryImages.FromTexture(texture, 1024));
                if (token.IsCancellationRequested) return;
                if (picture is null) ShowPreview(item.Glyph, L.T("FilePreview.PixelsTextureCacheUnder"));
                else
                {
                    ShowPreview(item.Glyph, null, picture);
                    PreviewDetail.Text = detail + "  ·  " + picture.Info;
                }
            }
            else if (MeshWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, L.T("FilePreview.ReadingMesh"));
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
                PreviewDetail.Text = detail + L.T("FilePreview.MeshDetail", triangles, L.T("Count.Lod", mesh.LodCount));
                // the colour textures, smaller than the viewer's, all read at once, as the viewers read them, then put on together
                await ShowTexturesAsync([(mesh, System.Numerics.Matrix4x4.Identity)], [null], locator, detail, token);
            }
            else if (CutsceneWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, L.T("FilePreview.ReadingCutscene"));
                var entry = item.Entry;
                CutsceneFile cutscene;
                try
                {
                    byte[] data = await ReadAsync(entry);
                    cutscene = await Task.Run(() => CutsceneFile.Read(data), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, L.T("FilePreview.CutsceneCouldNotRead", ex.Message));
                    return;
                }
                if (token.IsCancellationRequested) return;
                ShowPreview(item.Glyph, null, cutscene: cutscene);
                PreviewDetail.Text = L.T("FilePreview.DoubleClickOpen", detail);
            }
            else if (EntityWindow.CanShow(item.Name))
            {
                // its meshes put together, in its first appearance
                ShowPreview(item.Glyph, L.T("FilePreview.ReadingEntity"));
                var locator = Locator(item.Entry);
                Core.Entities.EntityFile entity;
                List<(MeshFile Mesh, System.Numerics.Matrix4x4 Place)> parts;
                List<Core.Entities.EntityComponent> drawnBy;
                try
                {
                    byte[] data = await ReadAsync(item.Entry);
                    entity = await Task.Run(() =>
                    {
                        // with the templates it includes found, whose colour entries dye its parts
                        var e = Core.Entities.EntityFile.Read(data);
                        e.FindOrigins(locator.ReadFile);
                        return e;
                    }, token);
                    var look = await Task.Run(() => entity.Look(entity.DefaultAppearance, locator.ReadFile), token);
                    (parts, drawnBy, _, _) = await Task.Run(() => EntityWindow.ReadMeshes(look, locator, token), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, L.T("FilePreview.EntityCouldNotRead", ex.Message));
                    return;
                }
                if (token.IsCancellationRequested) return;
                string what = $"{entity.EntityClass ?? L.T("FilePreview.Entity")}  ·  {L.T("Count.Component", entity.Components.Count)}"
                            + (entity.Appearances.Count > 0 ? $", {L.T("Count.Appearance", entity.Appearances.Count)}" : "");
                if (parts.Count == 0)
                {
                    ShowPreview(item.Glyph, L.T("FilePreview.HasNoMeshesShow", what));
                    return;
                }
                ShowPreview(item.Glyph, null, mesh: parts[0].Mesh);
                PreviewMesh.ShowParts(parts);
                PreviewDetail.Text = L.T("FilePreview.DoubleClickOpen2", detail, what, L.T("Count.Mesh", parts.Count));
                var colorings = drawnBy.Select(c => LookTextures.Coloring(entity, entity.DefaultAppearance?.Name, c.Name)).ToList();
                await ShowTexturesAsync(parts, colorings, locator, PreviewDetail.Text, token);
            }
            else if (SceneWindow.CanShow(item.Name))
            {
                ShowPreview(item.Glyph, L.T("FilePreview.ReadingScene"));
                Core.Scenes.SceneFile scene;
                try
                {
                    byte[] data = await ReadAsync(item.Entry);
                    scene = await Task.Run(() => Core.Scenes.SceneFile.Read(data), token);
                }
                catch (InvalidDataException ex)
                {
                    if (!token.IsCancellationRequested) ShowPreview(item.Glyph, L.T("FilePreview.SceneCouldNotRead", ex.Message));
                    return;
                }
                if (token.IsCancellationRequested) return;
                // shown at once, then again with its lines in words once the game's strings are read
                ShowPreview(item.Glyph, null, scene: scene);
                PreviewDetail.Text = L.T("FilePreview.DoubleClickOpen", detail);
                var (text, note) = await SceneWindow.LinesAsync();
                if (!token.IsCancellationRequested && ReferenceEquals(PreviewScene.Scene, scene)) PreviewScene.Show(scene, text ?? (_ => null), note);
            }
            else if (item.Entry is StringsEntry)
                ShowPreview(item.Glyph, L.T("FilePreview.DoubleClickOpenStrings"));
            else if (item.IsPreviewable)
            {
                if (item.Size > 4_000_000)
                {
                    ShowPreview(item.Glyph, L.T("FilePreview.TooLargeReadHere"));
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
                ShowPreview(item.Glyph, L.T("FilePreview.TheresNoPreviewKind", kind.Name, kind.Description));
            else
                ShowPreview(item.Glyph, L.T("FilePreview.NoPreviewKindFile"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!token.IsCancellationRequested) ShowPreview(item.Glyph, L.T("FilePreview.CouldNotRead", ex.Message));
        }
    }
}
