using System.Diagnostics;
using System.IO;

namespace W3ModWorkbench.Core.LoadOrder;

/// <summary>Script Merger Remastered (by relapse12), a separate program that merges mods changing the same scripts and files. Only found and started here.</summary>
public static class ScriptMerger
{
    public static bool IsSet(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>Looks for the merger's exe in the game folder and the usual places a download ends up.</summary>
    public static string? Find(string? gameFolder)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameFolder)) roots.Add(Path.Combine(gameFolder, "bin"));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        foreach (string root in roots.Where(Directory.Exists))
        {
            try
            {
                var hit = Directory.EnumerateFiles(root, "*.exe", options)
                    .FirstOrDefault(f => Path.GetFileName(f).Contains("merger", StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next place
            }
        }
        return null;
    }

    public static void Start(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
}
