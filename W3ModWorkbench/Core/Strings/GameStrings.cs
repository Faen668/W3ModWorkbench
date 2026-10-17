using System.Globalization;
using System.IO;

namespace W3ModWorkbench.Core.Strings;

/// <summary>A string from one of the game's, a DLC's or an installed mod's .w3strings files, and where it came from.</summary>
public sealed record GameString(uint Id, uint KeyHash, string Text, string Source, string File);

/// <summary>A .w3strings file found under the game folder, and the name to show for where it is.</summary>
public sealed record StringsSource(string Name, string Path);

/// <summary>
/// Every string of one language the game would load: the base game's, each DLC's and each mod's. Searched by
/// text, ID or key, and used to find IDs a mod's strings share with the game or another mod.
/// </summary>
public sealed class GameStringIndex
{
    private readonly Dictionary<uint, List<GameString>> _byId = [];

    public required W3Language Language { get; init; }
    public required IReadOnlyList<GameString> All { get; init; }

    /// <summary>The files that were read.</summary>
    public required IReadOnlyList<StringsSource> Sources { get; init; }

    /// <summary>Files that couldn't be read, with the reason.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    private GameStringIndex()
    {
    }

    /// <summary>
    /// The .w3strings files for a language under a game folder: the base game's (content\content*), DLC
    /// (dlc\*\content) and mods (mods\*\content). Some are in a subfolder of the content folder.
    /// </summary>
    public static List<StringsSource> FindFiles(string gameFolder, string handle, bool dlcAndMods = true)
    {
        var found = new List<StringsSource>();
        string name = handle + ".w3strings";
        void Scan(string root, Func<string, string> label)
        {
            if (!Directory.Exists(root)) return;
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                    found.Add(new StringsSource(label(path), path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder that can't be read has nothing to add
            }
        }

        Scan(System.IO.Path.Combine(gameFolder, "content"), _ => "Game");
        if (dlcAndMods)
        {
            Scan(System.IO.Path.Combine(gameFolder, "dlc"), p => "DLC: " + TopFolder(gameFolder, "dlc", p));
            Scan(System.IO.Path.Combine(gameFolder, "mods"), p => "Mod: " + TopFolder(gameFolder, "mods", p));
        }
        return found;
    }

    private static string TopFolder(string gameFolder, string root, string path)
    {
        string relative = System.IO.Path.GetRelativePath(System.IO.Path.Combine(gameFolder, root), path);
        int slash = relative.IndexOfAny(['\\', '/']);
        return slash > 0 ? relative[..slash] : relative;
    }

    /// <summary>Reads every file for the language. Slow for the whole game, so it belongs on a background thread.</summary>
    /// <param name="skipFolder">A folder whose files are left out, such as the mod being edited, so it doesn't clash with itself.</param>
    public static GameStringIndex Load(string gameFolder, W3Language language, bool dlcAndMods = true, string? skipFolder = null)
    {
        var all = new List<GameString>();
        var sources = new List<StringsSource>();
        var problems = new List<string>();
        string? skip = skipFolder is null ? null : System.IO.Path.GetFullPath(skipFolder).TrimEnd('\\', '/');
        foreach (var source in FindFiles(gameFolder, language.Handle, dlcAndMods))
        {
            if (skip is not null && System.IO.Path.GetFullPath(source.Path).StartsWith(skip + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var file = W3Strings.Read(File.ReadAllBytes(source.Path), source.Path);
                string rel = System.IO.Path.GetRelativePath(gameFolder, source.Path);
                foreach (var s in file.Strings) all.Add(new GameString(s.Id, s.KeyHash, s.Text, source.Name, rel));
                sources.Add(source);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                problems.Add($"{source.Path}: {ex.Message}");
            }
        }
        var index = new GameStringIndex { Language = language, All = all, Sources = sources, Problems = problems };
        foreach (var s in all)
        {
            if (!index._byId.TryGetValue(s.Id, out var list)) index._byId[s.Id] = list = [];
            list.Add(s);
        }
        return index;
    }

    private static readonly Dictionary<string, Task<GameStringIndex>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The index for a language, read once in the background and kept until the app closes.</summary>
    public static Task<GameStringIndex> GetAsync(string gameFolder, W3Language language, bool dlcAndMods = true)
    {
        string key = $"{gameFolder}|{language.Handle}|{dlcAndMods}";
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var task))
                Cache[key] = task = Task.Run(() => Load(gameFolder, language, dlcAndMods));
            return task;
        }
    }

    /// <summary>Forgets what was read, so the next look reads the files again (after strings were saved).</summary>
    public static void Forget()
    {
        lock (Cache) Cache.Clear();
    }

    /// <summary>Everything that uses this ID.</summary>
    public IReadOnlyList<GameString> WithId(uint id) => _byId.TryGetValue(id, out var list) ? list : [];

    public bool IsUsed(uint id) => _byId.ContainsKey(id);

    /// <summary>
    /// Strings matching a search: words that all appear in the text (ignoring case), a number that is, or starts, an
    /// ID, or a key, whether typed as its name or as its hash in hex.
    /// </summary>
    public IEnumerable<GameString> Search(string query, bool wholeWord = false)
    {
        query = query.Trim();
        if (query.Length == 0) return All;

        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        uint nameHash = W3Strings.HashKey(query);
        bool hex = query.Length == 8 && uint.TryParse(query, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        uint hexHash = hex ? uint.Parse(query, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
        bool digits = query.All(char.IsAsciiDigit);

        return All.Where(s =>
        {
            if (s.KeyHash != 0 && (s.KeyHash == nameHash || hex && s.KeyHash == hexHash)) return true;
            if (digits && s.Id.ToString(CultureInfo.InvariantCulture).StartsWith(query, StringComparison.Ordinal)) return true;
            foreach (string word in words)
                if (!ContainsWord(s.Text, word, wholeWord)) return false;
            return true;
        });
    }

    private static bool ContainsWord(string text, string word, bool whole)
    {
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (!whole) return true;
            bool before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            int end = at + word.Length;
            bool after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after) return true;
            at = end;
        }
        return false;
    }

    /// <summary>
    /// A 1,000-ID block in the range mods use (211nnnn000 to 211nnnn999) that nothing here uses, picked at
    /// random so two mods asking at the same time are unlikely to get the same one.
    /// </summary>
    public uint FreeBlock(Random? random = null)
    {
        random ??= Random.Shared;
        var used = new HashSet<uint>(_byId.Keys.Select(id => id / 1000));
        for (int tries = 0; tries < 2000; tries++)
        {
            uint block = (uint)random.Next(2110000, 2120000);
            if (!used.Contains(block)) return block * 1000;
        }
        for (uint block = 2110000; block < 2120000; block++)
            if (!used.Contains(block)) return block * 1000;
        return 2110000000;
    }
}
