using System.IO;
using System.Windows;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Opens a file a viewer names (in a property, a list of files, a link) in the viewer for its kind, found the way the
/// game would find it: a mesh, an entity, a behaviour tree, a scene, a cutscene, a texture, XML, or any other CR2W file
/// in a properties window. A kind of file the tool can't show has its path copied instead.
/// </summary>
public static class ViewerLinks
{
    /// <param name="lookAs">For an entity opened from a scene or cutscene: the appearance that shows it in.</param>
    public static async Task OpenAsync(Window owner, MessageCard message, ResourceLocator locator, string path, EntityWindow.LookAs? lookAs = null)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is ".xbm" or ".w2cube" or ".texarray")
        {
            TextureWindow.OpenLink(owner, message, locator, path);
            return;
        }
        bool viewable = MeshWindow.CanShow(path) || CutsceneWindow.CanShow(path) || SceneWindow.CanShow(path) || EntityWindow.CanShow(path)
            || BehTreeWindow.CanShow(path) || XmlWindow.CanOpen(path) || TextureWindow.CanShow(path) || PropertiesWindow.CanShow(path);
        if (!viewable)
        {
            Copy(message, path, $"There's no viewer for {FileKinds.Of(path)?.Name.ToLowerInvariant() ?? "this kind of"} files yet, so its path is copied:\n{path}");
            return;
        }
        byte[]? data;
        ArchiveEntry? entry;
        try
        {
            (data, entry) = await Task.Run(() => (locator.ReadFile(path), locator.Find(path)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            await message.Show(L.T("Common.CouldNotRead"), $"{path}\n\n{ex.Message}", MessageKind.Error, primary: L.T("Common.OK"));
            return;
        }
        if (data is null)
        {
            await message.Show(L.T("Common.NotFound"), L.T("ViewerLinks.IsntOpenProjectArchives", path), MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        string source = entry is null ? "project" : entry.Archive.Name;
        string name = Path.GetFileName(path);
        if (MeshWindow.CanShow(path)) MeshWindow.Open(path, source, data, entry?.Archive, locator, owner);
        else if (CutsceneWindow.CanShow(path)) CutsceneWindow.Open(path, source, data, locator, owner, entry?.Archive);
        else if (SceneWindow.CanShow(path)) SceneWindow.Open(path, source, data, locator, owner, entry?.Archive);
        else if (EntityWindow.CanShow(path)) EntityWindow.Open(path, source, data, locator, owner, entry?.Archive, lookAs);
        else if (BehTreeWindow.CanShow(path)) BehTreeWindow.Open(path, source, data, locator, owner, entry?.Archive);
        else if (XmlWindow.CanOpen(path)) XmlWindow.OpenData(name, $"{source}  ·  {path}", data, owner, path);
        else if (PropertiesWindow.CanShow(path)) PropertiesWindow.Open(path, source, data, locator, owner, entry?.Archive);
        else if (!TextureWindow.Open(name, $"{source}  ·  {path}", data, owner))
            await message.Show(L.T("Common.CantShown"), L.T("ViewerLinks.IsntPictureTextureViewer", path), MessageKind.Warning, primary: L.T("Common.OK"));
    }

    private static void Copy(MessageCard message, string text, string note)
    {
        try
        {
            Clipboard.SetText(text);
            _ = message.Show(null, note, MessageKind.Info, primary: L.T("Common.OK"));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // another program has the clipboard open
        }
    }
}
