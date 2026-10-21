using System.IO;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>Finds the game's own script sources (content\content0\scripts) for a file being opened.</summary>
public static class GameScripts
{
    /// <summary>
    /// A game install to fall back on when the file is not inside one (set from Settings). Empty means the one found in
    /// the Steam and GOG libraries, as everywhere else in the app.
    /// </summary>
    public static string? GameFolder { get; set; }

    // looked for once, the first time a file outside the game needs it
    private static readonly Lazy<string?> Detected = new(GameLocator.Find);

    /// <summary>mods\X\content\precompiled.rsblob → content\content0\scripts of the same game install.</summary>
    public static string? FindFolder(string nearPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(nearPath)) ?? "");
        for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "content", "content0", "scripts");
            if (Directory.Exists(candidate)) return candidate;
        }
        string? game = string.IsNullOrWhiteSpace(GameFolder) ? Detected.Value : GameFolder;
        if (!string.IsNullOrWhiteSpace(game))
        {
            string fallback = Path.Combine(game, "content", "content0", "scripts");
            if (Directory.Exists(fallback)) return fallback;
        }
        return null;
    }
}
