using System.IO;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Projects;

/// <summary>A file the tool won't change, because it's the game's, REDkit's, an installed mod's or a build's.</summary>
public sealed class ProtectedFileException(string message) : IOException(message);

/// <summary>
/// The one rule every editor and every save follows: the game's files are never changed. A file inside a bundle, a
/// texture cache or compiled scripts can't be written at all; a file under the game's folder (its content, its DLC and
/// the installed mods), or one of REDkit's own (its r4data and its programs), is read-only too, and so is what a
/// project's Build makes. To change one, copy it into a project first. Everything else (a project's files, and loose
/// files of your own anywhere else) can be edited.
/// <para>
/// The game's folders are found the way the rest of the tool finds them: the one a file is in, the one in Settings,
/// and the usual Steam and GOG places; REDkit's from wcc_lite.
/// </para>
/// </summary>
public static class FileSafety
{
    /// <summary>Why a file on disk mustn't be changed, in a sentence or two; null when it may be.</summary>
    public static string? Protection(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        if (GameFolderOf(full) is { } game)
        {
            string relative = Path.GetRelativePath(game, full);
            string first = relative.Split('\\', '/')[0];
            if (first.Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                string mod = relative.Split('\\', '/').ElementAtOrDefault(1) ?? "";
                return L.T("FileSafety.InstalledModWhichLeft", mod);
            }
            return first.Equals("dlc", StringComparison.OrdinalIgnoreCase)
                ? L.T("FileSafety.OneGamesDLCFiles")
                : L.T("FileSafety.OneGamesOwnFiles");
        }
        foreach (string redkit in RedkitFolders())
            foreach (string own in new[] { "r4data", "bin" })
                if (Under(Path.Combine(redkit, own), full))
                    return L.T("FileSafety.OneREDkitsOwnFiles");
        if (BuildOutputOf(full) is not null)
            return L.T("FileSafety.MadeBuildWhichReplaces");
        return null;
    }

    /// <summary>Whether a file on disk may be changed.</summary>
    public static bool CanEdit(string path) => Protection(path) is null;

    /// <summary>Stops a write to a file that mustn't be changed. The last check before every save, whatever the window.</summary>
    /// <exception cref="ProtectedFileException">The file is the game's, REDkit's, an installed mod's or a build's.</exception>
    public static void CheckWrite(string path)
    {
        if (Protection(path) is { } why) throw new ProtectedFileException(L.T("FileSafety.WasntSaved", Path.GetFileName(path), why));
    }

    /// <summary>
    /// Stops writing a folder of files into a protected place, as extracting does: the game's folders, REDkit's own,
    /// or a build. A folder of your own anywhere else is fine.
    /// </summary>
    public static void CheckFolder(string folder)
    {
        if (Protection(Path.Combine(folder, "_")) is { } why)
            throw new ProtectedFileException(L.T("FileSafety.NothingCanWritten", folder, why));
    }

    /// <summary>
    /// For the two things that do write into the game's mods folder, because you asked them to: Install, and Compile
    /// set to write there. Allowed in a mod's own folder under mods; anywhere else, the usual rule.
    /// </summary>
    public static void CheckInstallWrite(string path)
    {
        string full = Path.GetFullPath(path);
        if (GameFolderOf(full) is { } game && Under(Path.Combine(game, "mods"), full)
            && Path.GetRelativePath(Path.Combine(game, "mods"), full).Contains(Path.DirectorySeparatorChar))
            return;
        // a mod's own DLC folder under dlc, never one of the game's official DLC
        if (GameFolderOf(full) is { } root && Under(Path.Combine(root, "dlc"), full)
            && Path.GetRelativePath(Path.Combine(root, "dlc"), full) is var inDlc && inDlc.Contains(Path.DirectorySeparatorChar)
            && !OfficialDlc.Is(root, inDlc[..inDlc.IndexOf(Path.DirectorySeparatorChar)]))
            return;
        CheckWrite(full);
    }

    /// <summary>
    /// The game path of a loose file in the game's folders, as a mod would override it: after <c>content\content0</c>,
    /// a DLC's or a mod's <c>content</c>, or REDkit's <c>r4data</c>. Null for a file that has none (the game's
    /// programs and config, or a file outside the game).
    /// </summary>
    public static string? GamePathOf(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = GameFolderOf(full);
        if (root is not null)
        {
            var parts = Path.GetRelativePath(root, full).Split('\\', '/');
            // content\content0\x, dlc\name\content\x, mods\name\content\x
            if (parts.Length > 2 && parts[0].Equals("content", StringComparison.OrdinalIgnoreCase)) return string.Join('\\', parts[2..]);
            if (parts.Length > 3 && parts[0] is var top && (top.Equals("dlc", StringComparison.OrdinalIgnoreCase) || top.Equals("mods", StringComparison.OrdinalIgnoreCase))
                && parts[2].Equals("content", StringComparison.OrdinalIgnoreCase))
                return string.Join('\\', parts[3..]);
            return null;
        }
        foreach (string redkit in RedkitFolders())
        {
            string depot = Path.Combine(redkit, "r4data");
            if (Under(depot, full)) return Path.GetRelativePath(depot, full);
        }
        return null;
    }

    /// <summary>The game folder a path is in, or null when it isn't in one.</summary>
    public static string? GameFolderOf(string full)
    {
        foreach (string game in GameFolders(full))
            if (Under(game, full)) return game;
        return null;
    }

    /// <summary>The build folder of the project a path is in, when it's in one: the project's own Build makes everything there.</summary>
    private static string? BuildOutputOf(string full)
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(full) ?? ""); dir?.Parent is not null; dir = dir.Parent)
                if (dir.Name.Equals("build", StringComparison.OrdinalIgnoreCase) && WorkbenchProject.FileIn(dir.Parent.FullName) is not null)
                    return dir.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private static IEnumerable<string> GameFolders(string near)
    {
        // the one the file is in first, however deep: it may be a second copy of the game, not the one in Settings
        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(Path.GetDirectoryName(near) ?? ""); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { dir = null; }
        for (; dir is not null; dir = dir.Parent)
            if (GameLocator.IsGameFolder(dir.FullName))
            {
                yield return dir.FullName;
                break;
            }
        if (GameLocator.Resolve(null) is { } set) yield return set;
    }

    /// <summary>
    /// Whether an archive or file is one of the game's own: under its <c>content</c> or an official DLC's folder in
    /// <c>dlc</c>, not a mod, a project or a file somewhere else.
    /// </summary>
    public static bool IsGameContent(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (GameFolderOf(full) is not { } root) return false;
        string first = Path.GetRelativePath(root, full).Split('\\', '/')[0];
        return first.Equals("content", StringComparison.OrdinalIgnoreCase) || first.Equals("dlc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a file is one of REDkit's own, in its <c>r4data</c>.</summary>
    public static bool IsRedkitFile(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return RedkitFolders().Any(redkit => Under(Path.Combine(redkit, "r4data"), full));
    }

    /// <summary>Whether REDkit is installed: found from the wcc_lite in Settings, or in the usual Steam and GOG folders.</summary>
    public static bool RedkitInstalled => RedkitFolders().Any();

    /// <summary>
    /// REDkit's own copy of a game file, before it's cooked (a loose file in its <c>r4data</c>), when REDkit is installed
    /// and has one. Its resources keep the editor's properties that cooking leaves out.
    /// </summary>
    public static string? RedkitFile(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || Path.IsPathRooted(gamePath)) return null;
        foreach (string redkit in RedkitFolders())
        {
            string file = Path.Combine(redkit, "r4data", gamePath);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    private static IEnumerable<string> RedkitFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? wcc in new[] { App.Settings.WccLitePath, FoundWcc.Value })
        {
            if (string.IsNullOrEmpty(wcc) || Path.GetDirectoryName(wcc) is not { } bin) continue;
            string root = Path.GetFullPath(Path.Combine(bin, "..", ".."));
            if (seen.Add(root)) yield return root;
        }
    }

    // looking for REDkit walks every drive's usual folders: done once
    private static readonly Lazy<string?> FoundWcc = new(ScriptCompiler.Find);

    private static bool Under(string folder, string path) =>
        path.StartsWith(folder.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
}
