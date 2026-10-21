using System.IO;
using System.Windows;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;

namespace W3ModWorkbench;

/// <summary>
/// The viewers for the smaller kinds of file (spawn trees, steering graphs and the like): which files they show, and
/// opening one from its data or from disk, so the main window, the Asset Browser and the other viewers open them alike.
/// </summary>
public static class DataViewers
{
    /// <summary>A kind of file a viewer here shows: its name and how to open its data or a file on disk.</summary>
    private sealed record Viewer(string Kind, Func<string, bool> Shows,
        Action<string, string, byte[], ResourceLocator, Window, IArchive?> Open, Action<string, ResourceLocator, Window> OpenFile);

    private static Viewer Tree(string kind, Func<string, bool> shows, Func<byte[], TreeDocument> read, Func<TreeFileWindow, ITreePreview?>? extra = null) => new(kind, shows,
        (path, source, data, locator, owner, archive) => TreeFileWindow.Open(path, source, data, read, kind, locator, owner, archive, extra),
        (file, locator, owner) => TreeFileWindow.OpenFile(file, read, kind, locator, owner, extra));

    private static readonly Viewer[] Viewers =
    [
        Tree("Spawn tree", SpawnTreeFile.IsSpawnTree, SpawnTreeFile.Read),
        Tree("Steering graph", SteeringFile.IsSteering, SteeringFile.Read),
        Tree("Job tree", JobTreeFile.IsJobTree, JobTreeFile.Read, w => new JobTreePreview(w.Locator)),
        new("Community", CommunityWindow.CanShow, CommunityWindow.Open, CommunityWindow.OpenFile),
        new("Journal", JournalWindow.CanShow, JournalWindow.Open, JournalWindow.OpenFile),
        new("Animation events", AnimEventsWindow.CanShow, AnimEventsWindow.Open, AnimEventsWindow.OpenFile),
    ];

    /// <summary>Whether a file's name says a viewer here shows it.</summary>
    public static bool CanShow(string name) => Viewers.Any(v => v.Shows(name));

    /// <summary>Opens a file's data in its viewer, in a window of its own.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        Viewers.First(v => v.Shows(path)).Open(path, source, data, locator, owner, archive);

    /// <summary>Opens a file on disk in its viewer.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        Viewers.First(v => v.Shows(file)).OpenFile(file, locator, owner);

    /// <summary>
    /// A file a property refers to, opened in its own viewer (any of the tool's), read from the project, the archives
    /// open or the game; its path is copied when there's no viewer for it.
    /// </summary>
    public static async Task OpenLinkAsync(string path, ResourceLocator locator, Window owner, IMessageHost host)
    {
        bool viewable = CanShow(path) || MeshWindow.CanShow(path) || CutsceneWindow.CanShow(path) || SceneWindow.CanShow(path) || EntityWindow.CanShow(path)
            || BehTreeWindow.CanShow(path) || XmlWindow.CanOpen(path) || TextureWindow.CanShow(path);
        if (!viewable)
        {
            try { Clipboard.SetText(path); }
            catch (System.Runtime.InteropServices.COMException) { }
            await host.ShowMessage(null, $"There's no viewer for {FileKinds.Of(path)?.Name.ToLowerInvariant() ?? "this kind of"} files yet, so its path is copied:\n{path}", MessageKind.Info);
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
            await host.ShowMessage("It could not be read", $"{path}\n\n{ex.Message}", MessageKind.Error);
            return;
        }
        if (data is null)
        {
            await host.ShowMessage("Not found", $"{path}\n\nIt isn't in the open project, the archives open in the tool, or the game's bundles.", MessageKind.Warning);
            return;
        }
        string source = entry is null ? "project" : entry.Archive.Name;
        string name = Path.GetFileName(path);
        if (CanShow(path)) Open(path, source, data, locator, owner, entry?.Archive);
        else if (MeshWindow.CanShow(path)) MeshWindow.Open(path, source, data, entry?.Archive, locator, owner);
        else if (CutsceneWindow.CanShow(path)) CutsceneWindow.Open(path, source, data, locator, owner, entry?.Archive);
        else if (SceneWindow.CanShow(path)) SceneWindow.Open(path, source, data, locator, owner, entry?.Archive);
        else if (EntityWindow.CanShow(path)) EntityWindow.Open(path, source, data, locator, owner, entry?.Archive);
        else if (BehTreeWindow.CanShow(path)) BehTreeWindow.Open(path, source, data, locator, owner, entry?.Archive);
        else if (XmlWindow.CanOpen(path)) XmlWindow.OpenData(name, $"{source}  ·  {path}", data, owner, path);
        else if (!TextureWindow.Open(name, $"{source}  ·  {path}", data, owner))
            await host.ShowMessage("It can't be shown", $"{path}\n\nIt isn't a picture the texture viewer understands.", MessageKind.Warning);
    }
}
