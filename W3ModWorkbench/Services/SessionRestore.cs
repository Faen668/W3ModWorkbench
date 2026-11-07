using System.IO;
using System.Text.Json;
using System.Windows;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A window showing one file, which can be opened again the same way after the app restarts.</summary>
public interface IReopenable
{
    /// <summary>The file's game path (a mesh's, an entity's…), or empty when it has none.</summary>
    string ReopenPath { get; }

    /// <summary>Where it was read from: the file on disk (a full path), or a bundle's or the project's name.</summary>
    string ReopenSource { get; }
}

/// <summary>A window that can hold changes not saved yet, or work still running, which an update must not lose.</summary>
public interface IPendingChanges
{
    /// <summary>What isn't saved or done yet, in a few words (the window's file), or null when there's nothing.</summary>
    string? PendingChange { get; }
}

/// <summary>
/// What was open when the app closed to install an update: the project, the bundles and folders in the file list, the
/// files open in tabs, the viewers and the Tools windows. The updated copy opens them all again, once, and deletes it.
/// </summary>
public sealed class SessionSnapshot
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3ModWorkbench", "restore-after-update.json"); // loc-ignore

    /// <summary>The project open (its .w3proj), or null.</summary>
    public string? Project { get; set; }

    /// <summary>Bundles, folders and files open in the main window's file list, by where they are on disk.</summary>
    public List<string> Archives { get; set; } = [];

    /// <summary>Files open in tabs: on disk (<see cref="Tab.Disk"/>) or in an archive (its name and the game path).</summary>
    public List<Tab> Tabs { get; set; } = [];

    /// <summary>Files open in viewers of their own: on disk (a full path) or by game path.</summary>
    public List<Tab> Viewers { get; set; } = [];

    /// <summary>The Tools windows open, by their class names.</summary>
    public List<string> Tools { get; set; } = [];

    public sealed class Tab
    {
        public string? Disk { get; set; }
        public string? Source { get; set; }
        public string? Path { get; set; }
        public bool Active { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>The snapshot an update left, taken away so it's opened only once; null when there's none.</summary>
    public static SessionSnapshot? Take()
    {
        if (!File.Exists(FilePath)) return null;
        try { return JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(FilePath)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
        finally { try { File.Delete(FilePath); } catch { /* opened again next time: harmless */ } }
    }

    public static bool Waiting => File.Exists(FilePath);

    /// <summary>
    /// Everything in the app with changes not saved, or work still running, one line each: files in tabs, and windows
    /// that say so. An update waits until there's none, as restarting would lose them.
    /// </summary>
    public static List<string> PendingChanges()
    {
        var pending = PreviewWindow.UnsavedDocuments().Select(d => d.FileName).ToList();
        foreach (Window window in Application.Current.Windows)
            if (window is IPendingChanges { PendingChange: { Length: > 0 } what }) pending.Add(what);
        return pending.Distinct().ToList();
    }

    /// <summary>The title of a window that has changes to save, for <see cref="PendingChanges"/>.</summary>
    public static string Describe(Window window) => string.IsNullOrWhiteSpace(window.Title) ? L.T("UpdateWindow.AWindow") : window.Title;
}
