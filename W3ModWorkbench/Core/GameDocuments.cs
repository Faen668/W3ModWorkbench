using System.IO;

namespace W3ModWorkbench.Core;

/// <summary>
/// The folder the game keeps its own settings and logs in (input.settings, mods.settings, scriptslog.txt): Documents\The
/// Witcher 3, unless Settings names another, for a game run under Proton or Wine, or a Documents folder the game doesn't follow.
/// </summary>
public static class GameDocuments
{
    /// <summary>The folder Settings names, or empty for the usual one (set from Settings).</summary>
    public static string? Override { get; set; }

    /// <summary>Where the game puts it on Windows: Documents\The Witcher 3.</summary>
    public static string Default => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "The Witcher 3");

    public static string Folder => string.IsNullOrWhiteSpace(Override) ? Default : Override.Trim();

    /// <summary>A file in it, such as input.settings.</summary>
    public static string FileIn(string name) => Path.Combine(Folder, name);
}
