using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

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

    /// <summary>One file Script Merger merged: the mods that went into it and the mod folder it wrote the result to.</summary>
    public sealed record MergeRecord(string RelativePath, string? Bundle, string MergedMod, IReadOnlyList<string> Included);

    /// <summary>
    /// Script Merger's MergeInventory.xml, kept beside its exe: the one record of which mods went into the merged mod.
    /// Uses the configured merger, else the newest one found in the usual places.
    /// </summary>
    public static List<MergeRecord> ReadInventory(string? configuredPath, string? gameFolder)
    {
        var dirs = new List<string>();
        if (IsSet(configuredPath)) dirs.Add(Path.GetDirectoryName(configuredPath)!);
        else
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(gameFolder)) roots.Add(Path.Combine(gameFolder, "bin"));
            foreach (var f in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
                roots.Add(Environment.GetFolderPath(f));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            foreach (string root in roots.Where(Directory.Exists))
            {
                try { dirs.AddRange(Directory.EnumerateFiles(root, "MergeInventory.xml", options).Select(f => Path.GetDirectoryName(f)!)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        var file = dirs.Select(d => Path.Combine(d, "MergeInventory.xml")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null) return [];
        try
        {
            var doc = XDocument.Load(file);
            return [.. doc.Descendants("Merge").Select(m => new MergeRecord(
                (string?)m.Element("RelativePath") ?? "", (string?)m.Element("BundleName"), (string?)m.Element("MergedModName") ?? "",
                [.. m.Elements("IncludedMod").Select(i => i.Value)]))
                .Where(m => m.RelativePath.Length > 0 && m.MergedMod.Length > 0)];
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException) { return []; }
    }

    public static void Start(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
}
