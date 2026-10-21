using System.IO;
using System.Windows;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;
using W3ModWorkbench.Localisation;

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
        Tree(L.T("DataViewers.SpawnTree"), SpawnTreeFile.IsSpawnTree, SpawnTreeFile.Read),
        Tree(L.T("DataViewers.SteeringGraph"), SteeringFile.IsSteering, SteeringFile.Read),
        Tree(L.T("DataViewers.JobTree"), JobTreeFile.IsJobTree, JobTreeFile.Read, w => new JobTreePreview(w.Locator)),
        new(L.T("DataViewers.Community"), CommunityWindow.CanShow, CommunityWindow.Open, CommunityWindow.OpenFile),
        new(L.T("DataViewers.Journal"), JournalWindow.CanShow, JournalWindow.Open, JournalWindow.OpenFile),
        new(L.T("DataViewers.AnimationEvents"), AnimEventsWindow.CanShow, AnimEventsWindow.Open, AnimEventsWindow.OpenFile),
        new(L.T("Common.AnimationSet"), AnimSetWindow.CanShow, AnimSetWindow.Open, AnimSetWindow.OpenFile),
        new(L.T("DataViewers.Rig"), RigWindow.CanShow, RigWindow.Open, RigWindow.OpenFile),
        new(L.T("DataViewers.Sound"), SoundWindow.CanShow, SoundWindow.Open, SoundWindow.OpenFile),
        new(L.T("DataViewers.Material"), MaterialWindow.CanShow, MaterialWindow.Open, MaterialWindow.OpenFile),
    ];

    /// <summary>Whether a file's name says a viewer here shows it.</summary>
    public static bool CanShow(string name) => Viewers.Any(v => v.Shows(name));

    /// <summary>Opens a file's data in its viewer, in a window of its own.</summary>
    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        Viewers.First(v => v.Shows(path)).Open(path, source, data, locator, owner, archive);

    /// <summary>Opens a file on disk in its viewer.</summary>
    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        Viewers.First(v => v.Shows(file)).OpenFile(file, locator, owner);

    /// <summary>Whether a viewer in the tool opens a file of this name: one of these, or the mesh, entity, scene, cutscene, behaviour tree, XML or texture viewer.</summary>
    public static bool CanOpen(string path) =>
        CanShow(path) || MeshWindow.CanShow(path) || CutsceneWindow.CanShow(path) || SceneWindow.CanShow(path) || EntityWindow.CanShow(path)
        || BehTreeWindow.CanShow(path) || XmlWindow.CanOpen(path) || TextureWindow.CanShow(path);

    /// <summary>
    /// A file a property refers to, opened in its own viewer (any of the tool's), read from the project, the archives
    /// open or the game; its path is copied when there's no viewer for it. Sound events, sound banks, sounds and voice
    /// lines are read from the game's sound caches and speech files.
    /// </summary>
    public static async Task OpenLinkAsync(string path, ResourceLocator locator, Window owner, IMessageHost host)
    {
        // a sound event, a sound bank by its name, or a sound or voice line, which the sound caches and speech files have
        if (path.StartsWith(SoundLinks.Event, StringComparison.Ordinal))
        {
            SoundWindow.OpenEvent(path[SoundLinks.Event.Length..], locator, owner);
            return;
        }
        if (path.StartsWith(SoundLinks.Bank, StringComparison.Ordinal))
        {
            string bank = path[SoundLinks.Bank.Length..];
            path = Core.Sounds.SoundCacheArchive.Banks + (bank.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) ? bank : bank + ".bnk");
        }
        if (SoundLinks.IsSound(path))
        {
            var sound = await SoundLinks.ReadAsync(path);
            if (sound is null) await host.ShowMessage(L.T("Common.NotFound"), L.T("DataViewers.IsntGamesSoundCaches", path), MessageKind.Warning);
            else SoundWindow.Open(path, sound.Value.Source, sound.Value.Data, locator, owner);
            return;
        }
        // a texture's description: its pixels are in a texture cache under the same path, which the locator finds
        if (Path.GetExtension(path).ToLowerInvariant() is ".xbm" or ".w2cube" or ".texarray"
            && await Task.Run(() => locator.FindTexture(path)) is { } texture)
        {
            TextureWindow.Open(texture, owner, () => locator.ReadFile(path), locator);
            return;
        }
        bool viewable = CanOpen(path);
        if (!viewable)
        {
            try { Clipboard.SetText(path); }
            catch (System.Runtime.InteropServices.COMException) { }
            await host.ShowMessage(null, L.T("DataViewers.NoViewer", FileKinds.Of(path)?.Name.ToLowerInvariant() ?? L.T("Common.Kind2"), path), MessageKind.Info);
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
            await host.ShowMessage(L.T("Common.CouldNotRead"), $"{path}\n\n{ex.Message}", MessageKind.Error);
            return;
        }
        if (data is null)
        {
            await host.ShowMessage(L.T("Common.NotFound"), L.T("DataViewers.IsntOpenProjectArchives", path), MessageKind.Warning);
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
            await host.ShowMessage(L.T("Common.CantShown"), L.T("DataViewers.IsntPictureTextureViewer", path), MessageKind.Warning);
    }
}
