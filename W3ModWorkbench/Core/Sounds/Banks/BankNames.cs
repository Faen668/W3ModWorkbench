using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>
/// Names for the IDs in Wwise banks. A bank keeps names only as hashes (the lowercase name's FNV-1), so names are found
/// by hashing the strings that could be them: the game's own sound names (found once in its scripts and files and kept
/// with the app), and the string literals in the scripts of the game, its DLC, its mods and any folder near the bank
/// being looked at (a mod's own scripts name its events, states and game parameters).
/// </summary>
public static partial class BankNames
{
    private const string ResourceName = "W3ModWorkbench.Core.Sounds.Banks.gamesoundnames.txt.gz";
    private static readonly Lazy<Dictionary<uint, string>> Builtin = new(LoadBuiltin);
    private static readonly Dictionary<string, Task<Dictionary<uint, string>>> Folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's own sound names (events, buses, states, switches, game parameters, banks) kept with the app.</summary>
    public static IReadOnlyDictionary<uint, string> Game => Builtin.Value;

    private static Dictionary<uint, string> LoadBuiltin()
    {
        var names = new Dictionary<uint, string>();
        using var stream = typeof(BankNames).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return names;
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress), Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            int space = line.IndexOf(' ');
            if (space > 0 && uint.TryParse(line.AsSpan(0, space), out uint id)) names.TryAdd(id, line[(space + 1)..]);
        }
        return names;
    }

    /// <summary>
    /// Every name found for a bank: the game's own, and those in the scripts of the game, its DLC and mods, and of any
    /// <c>scripts</c> folder a few folders above <paramref name="near"/> (the bank's file). Read in the background,
    /// once per folder.
    /// </summary>
    public static async Task<IReadOnlyDictionary<uint, string>> ForAsync(string? gameFolder, string? near)
    {
        var all = new Dictionary<uint, string>(Game);
        var folders = new List<string>();
        if (gameFolder is not null)
        {
            folders.Add(Path.Combine(gameFolder, "content", "content0", "scripts"));
            foreach (string root in new[] { "dlc", "mods" })
            {
                string dir = Path.Combine(gameFolder, root);
                if (Directory.Exists(dir))
                    foreach (string mod in Directory.EnumerateDirectories(dir)) folders.Add(Path.Combine(mod, "content", "scripts"));
            }
        }
        // a mod's or a project's own scripts, wherever the bank is
        if (near is not null && Path.IsPathRooted(near))
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(near) ?? near);
            for (int up = 0; dir is not null && up < 5; up++, dir = dir.Parent)
                foreach (string name in new[] { "scripts", Path.Combine("content", "scripts") })
                    if (Directory.Exists(Path.Combine(dir.FullName, name))) folders.Add(Path.Combine(dir.FullName, name));
        }
        foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            foreach (var (id, name) in await ScriptNamesAsync(folder)) all.TryAdd(id, name);
        return all;
    }

    private static Task<Dictionary<uint, string>> ScriptNamesAsync(string folder)
    {
        lock (Folders)
        {
            if (!Folders.TryGetValue(folder, out var task) || task.IsFaulted)
                Folders[folder] = task = Task.Run(() => ScriptNames(folder));
            return task;
        }
    }

    [GeneratedRegex("[\"']([A-Za-z0-9_ .-]{1,100})[\"']")]
    private static partial Regex Literal();

    /// <summary>The string and name literals in a folder's scripts, by their Wwise hashes.</summary>
    private static Dictionary<uint, string> ScriptNames(string folder)
    {
        var names = new Dictionary<uint, string>();
        foreach (string file in Directory.EnumerateFiles(folder, "*.ws", SearchOption.AllDirectories))
        {
            byte[] raw;
            try { raw = File.ReadAllBytes(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            // scripts are UTF-16 as often as UTF-8
            string text = raw.Length > 1 && (raw[0] == 0xFF && raw[1] == 0xFE || raw[1] == 0) ? Encoding.Unicode.GetString(raw) : Encoding.UTF8.GetString(raw);
            foreach (Match m in Literal().Matches(text)) names.TryAdd(SoundBankIndex.Hash(m.Groups[1].Value), m.Groups[1].Value);
        }
        return names;
    }
}
