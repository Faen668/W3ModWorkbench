using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace W3ModWorkbench.Core;

/// <summary>Finds The Witcher 3 in the usual Steam and GOG install locations.</summary>
public static partial class GameLocator
{
    public static bool IsGameFolder(string folder) =>
        folder.Length > 0 && Directory.Exists(Path.Combine(folder, "content", "content0", "scripts"));

    /// <summary>
    /// The game folder a file belongs to when it's inside one (mods\X\content\...), otherwise the one set in
    /// Settings, otherwise whatever <see cref="Find"/> turns up.
    /// </summary>
    public static string? Resolve(string? nearPath)
    {
        if (!string.IsNullOrEmpty(nearPath))
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(nearPath)) ?? "");
                for (int up = 0; up < 8 && dir is not null; up++, dir = dir.Parent)
                    if (IsGameFolder(dir.FullName)) return dir.FullName;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                // not a path worth walking
            }
        }
        if (!string.IsNullOrWhiteSpace(Scripts.GameScripts.GameFolder) && IsGameFolder(Scripts.GameScripts.GameFolder))
            return Scripts.GameScripts.GameFolder;
        return Find();
    }

    public static string? Find() => Candidates().FirstOrDefault(IsGameFolder);

    private static IEnumerable<string> Candidates()
    {
        foreach (string library in SteamLibraries())
            yield return Path.Combine(library, "steamapps", "common", "The Witcher 3");

        // GOG Galaxy and the GOG installer record each game's folder in the registry
        foreach (string? path in GogPaths(name => name.Contains("Witcher 3", StringComparison.OrdinalIgnoreCase)
                                                  && !name.Contains("REDkit", StringComparison.OrdinalIgnoreCase)))
            if (!string.IsNullOrEmpty(path)) yield return path;

        // last resort: the default folders on every drive
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            string root = drive.RootDirectory.FullName;
            yield return Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "The Witcher 3");
            yield return Path.Combine(root, "SteamLibrary", "steamapps", "common", "The Witcher 3");
            yield return Path.Combine(root, "Steam", "steamapps", "common", "The Witcher 3");
            yield return Path.Combine(root, "GOG Games", "The Witcher 3 Wild Hunt GOTY");
            yield return Path.Combine(root, "Program Files (x86)", "GOG Galaxy", "Games", "The Witcher 3 Wild Hunt GOTY");
        }
    }

    /// <summary>
    /// Every Steam library folder listed in libraryfolders.vdf, then Steam's own folder
    /// (library paths first: the registry keeps Steam's own path in lower case).
    /// </summary>
    public static IEnumerable<string> SteamLibraries()
    {
        foreach (string steam in SteamRoots())
        {
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            string text;
            try { text = File.Exists(vdf) ? File.ReadAllText(vdf) : ""; }
            catch (IOException) { text = ""; }
            foreach (Match m in LibraryPath().Matches(text))
                yield return m.Groups[1].Value.Replace(@"\\", @"\");
            yield return steam;
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string a) roots.Add(a.Replace('/', '\\'));
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string b) roots.Add(b);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // no access to the registry: fall through to the default folders
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The folders GOG recorded for the games whose name passes <paramref name="wanted"/>.</summary>
    public static List<string?> GogPaths(Func<string, bool> wanted)
    {
        var paths = new List<string?>();
        try
        {
            using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (games is null) return paths;
            foreach (string id in games.GetSubKeyNames())
            {
                using var game = games.OpenSubKey(id);
                if (game?.GetValue("gameName") is string name && wanted(name))
                    paths.Add(game.GetValue("path") as string);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // no access to the registry
        }
        return paths;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();
}
