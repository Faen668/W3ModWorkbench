using System.IO;

namespace W3ModWorkbench.Core;

/// <summary>
/// Which folders in the game's dlc folder are CD PROJEKT's own DLC rather than mods installed as DLC. The Remastered
/// game ships its DLC inside content0 and keeps an empty folder for each in <c>dlc-tombstones</c>, so those names are
/// the official ones when it's there; older installs have them in dlc itself: the free DLC (dlc1 to dlc16), Hearts of
/// Stone (ep1) and Blood and Wine (bob). Anything else in dlc was put there by a mod.
/// </summary>
public static class OfficialDlc
{
    private static readonly string[] Known = ["bob", "ep1", .. Enumerable.Range(1, 16).Select(i => $"dlc{i}")];

    private static readonly Dictionary<string, HashSet<string>> ByGame = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The official DLC's folder names for a game folder.</summary>
    public static HashSet<string> Names(string? game)
    {
        lock (ByGame)
        {
            string key = game ?? "";
            if (ByGame.TryGetValue(key, out var known)) return known;
            var names = new HashSet<string>(Known, StringComparer.OrdinalIgnoreCase);
            string tombstones = game is null ? "" : Path.Combine(game, "dlc-tombstones");
            try
            {
                if (Directory.Exists(tombstones)) names.UnionWith(Directory.GetDirectories(tombstones).Select(Path.GetFileName).OfType<string>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return ByGame[key] = names;
        }
    }

    /// <summary>Whether a folder in dlc (by its name) is official DLC.</summary>
    public static bool Is(string? game, string folderName) => Names(game).Contains(folderName);
}
