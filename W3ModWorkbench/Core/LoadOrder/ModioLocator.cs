using System.IO;
using System.Text.Json;

namespace W3ModWorkbench.Core.LoadOrder;

/// <summary>One folder of a mod installed from the in-game mod.io hub: a mod (mods\X) or a DLC (dlc\X) of the package.</summary>
public sealed record ModioFolder(long ModId, string Name, string Folder, bool IsDlc, bool? Enabled, int? Priority);

/// <summary>
/// Finds the mods the in-game mod.io hub has installed, the way Script Merger Remastered does. They live outside the
/// game folder, under a root recorded in %LOCALAPPDATA%\mod.io\globalsettings.json (by default C:\Users\Public\mod.io),
/// in a folder per game (8254 for The Witcher 3). metadata\state.json lists each installed mod with the folder it
/// sits in (PathOnDisk, holding mods\ and dlc\ like a game folder does), and Documents\The Witcher 3\modioModsConfig.json
/// holds the player's enabled switch and priority for each. mod.io mods load before every other mod.
/// </summary>
public static class ModioLocator
{
    private const string GameId = "8254";

    public static string? Root()
    {
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mod.io", "globalsettings.json");
        try
        {
            if (File.Exists(settings))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("RootLocalStoragePath", out var p) && p.GetString() is { Length: > 0 } path && Directory.Exists(path))
                    return Path.GetFullPath(path);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        string fallback = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public", "mod.io");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public static List<ModioFolder> InstalledMods()
    {
        var found = new List<ModioFolder>();
        if (Root() is not { } root) return found;
        string state = Path.Combine(root, GameId, "metadata", "state.json");
        if (!File.Exists(state)) return found;
        var config = ReadConfig();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(state));
            if (!doc.RootElement.TryGetProperty("Mods", out var mods) || mods.ValueKind != JsonValueKind.Array) return found;
            foreach (var mod in mods.EnumerateArray())
            {
                if (!mod.TryGetProperty("ID", out var idElement) || !idElement.TryGetInt64(out long id)) continue;
                if (!mod.TryGetProperty("PathOnDisk", out var pathElement) || pathElement.GetString() is not { Length: > 0 } package || !Directory.Exists(package)) continue;
                string title = mod.TryGetProperty("Profile", out var profile) && profile.ValueKind == JsonValueKind.Object
                    && profile.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name ? name : $"mod {id}";
                config.TryGetValue(id, out var settings);
                foreach (var (sub, isDlc) in new[] { ("mods", false), ("dlc", true) })
                {
                    string dir = Path.Combine(package, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string folder in Directory.GetDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
                        found.Add(new ModioFolder(id, $"{title} [Mod.io #{id}]" + (isDlc ? $" dlc {Path.GetFileName(folder)}" : ""), folder, isDlc, settings.Enabled, settings.Priority));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>modioModsConfig.json is an object with one "mod" property per installed mod: modID, isEnabled, priority.</summary>
    private static Dictionary<long, (bool? Enabled, int? Priority)> ReadConfig()
    {
        var result = new Dictionary<long, (bool?, int?)>();
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "The Witcher 3", "modioModsConfig.json");
        try
        {
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject().Where(p => p.Name.Equals("mod", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object))
            {
                if (!prop.Value.TryGetProperty("modID", out var id) || !id.TryGetInt64(out long modId)) continue;
                bool? enabled = prop.Value.TryGetProperty("isEnabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
                int? priority = prop.Value.TryGetProperty("priority", out var pr) && pr.TryGetInt32(out int pv) ? pv : null;
                result[modId] = (enabled, priority);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return result;
    }
}
