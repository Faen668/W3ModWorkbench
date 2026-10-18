using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace W3ModWorkbench.Core.Projects;

/// <summary>What a person had open in a project, kept for them and not for the project: it is not in the .w3bench file, so sharing a project doesn't share your tabs.</summary>
public sealed class WorkspaceState
{
    /// <summary>Full paths of the files that were open in previews, in tab order.</summary>
    public List<string> OpenFiles { get; set; } = [];

    /// <summary>The file that was in front.</summary>
    public string? ActiveFile { get; set; }
}

/// <summary>
/// The workspace: the one project that is open, which every window asks whether a file is part of. Opening a project
/// closes the one before it. The project and its files are in the .w3bench file; the recent list and what was open
/// are kept in the tool's own settings folder.
/// </summary>
public static class ProjectWorkspace
{
    private const int MaxRecent = 10;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static readonly string StateFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3ModWorkbench", "workspaces");

    /// <summary>The open project, or null.</summary>
    public static WorkbenchProject? Current { get; private set; }

    /// <summary>The project opened, closed or changed (its files, its name, its scripts folder). Raised on the UI thread.</summary>
    public static event Action? Changed;

    /// <summary>Makes a project the open one (or none), and remembers it as the one to open next time.</summary>
    public static void Open(WorkbenchProject? project)
    {
        Current = project;
        if (project is not null) Remember(project.FilePath);
        App.Settings.LastProject = project?.FilePath ?? "";
        Changed?.Invoke();
    }

    /// <summary>Tells the windows the open project was changed and saved.</summary>
    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>Whether a file belongs to the open project.</summary>
    public static bool IsMember(string path) => Current?.Contains(path) == true;

    // ---------------------------------------------------------------- recent projects

    private static void Remember(string path)
    {
        var recent = App.Settings.RecentProjects;
        recent.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, path);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
    }

    /// <summary>The recent projects whose file is still there.</summary>
    public static List<string> Recent() => [.. App.Settings.RecentProjects.Where(File.Exists)];

    public static void ForgetRecent(string path) => App.Settings.RecentProjects.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- what was open

    private static string StatePath(WorkbenchProject project)
    {
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(project.FilePath.ToLowerInvariant())))[..16];
        return Path.Combine(StateFolder, key + ".json");
    }

    public static WorkspaceState LoadState(WorkbenchProject project)
    {
        try
        {
            string path = StatePath(project);
            if (File.Exists(path)) return JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // a broken or unreadable state file just means nothing to restore
        }
        return new();
    }

    public static void SaveState(WorkbenchProject project, WorkspaceState state)
    {
        try
        {
            Directory.CreateDirectory(StateFolder);
            File.WriteAllText(StatePath(project), JsonSerializer.Serialize(state, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not worth interrupting anyone over
        }
    }
}
