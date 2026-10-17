using System.IO;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>Finds the game's own script sources (content\content0\scripts) for a file being opened.</summary>
public static class GameScripts
{
    /// <summary>A game install to fall back on when the file is not inside one (set from Settings).</summary>
    public static string? GameFolder { get; set; }

    /// <summary>mods\X\content\precompiled.rsblob → content\content0\scripts of the same game install.</summary>
    public static string? FindFolder(string nearPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(nearPath)) ?? "");
        for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "content0", "scripts");
            if (Directory.Exists(candidate)) return candidate;
        }
        if (!string.IsNullOrWhiteSpace(GameFolder))
        {
            string fallback = Path.Combine(GameFolder, "content", "content0", "scripts");
            if (Directory.Exists(fallback)) return fallback;
        }
        return null;
    }
}
