using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace W3ModWorkbench.Core;

/// <summary>One of EasyAssetBrowser's categories (Meshes, Entities, Decals…), the file that lists its assets, and the tags its assets can have.</summary>
public sealed record EabCategory(string Name, string File, IReadOnlyList<string> Tags);

/// <summary>A game file as EasyAssetBrowser lists it: its category, which part of the game it's from, and the tags people gave it.</summary>
public sealed record EabAsset(string Name, string Category, string Origin, IReadOnlyList<string> Tags);

/// <summary>
/// EasyAssetBrowser's categories and tags for the game's files, by Vincent.02 (<see cref="Site"/>): a list of the
/// assets useful for building worlds, each in a category and tagged by where or what it's for (Kaer Morhen, Ruin,
/// Corpses…). The lists live in its GitHub repository, where anyone can suggest tags, and are read from there (Apache
/// License 2.0), kept in a cache so they work offline, and read again once a day.
/// </summary>
public sealed class EasyAssetBrowser
{
    public const string Site = "https://dev-cube.net/eab/";
    public const string Repository = "https://github.com/vincocrafter/EasyAssetBrowser";
    private const string DataUrl = "https://raw.githubusercontent.com/vincocrafter/EasyAssetBrowser/main/assets/data/";
    private const string ConfigFile = "categoryConfig.json";

    /// <summary>Where the lists are kept between runs.</summary>
    public static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "W3ModWorkbench", "EasyAssetBrowser");

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly Dictionary<string, EabAsset> _byPath;

    private EasyAssetBrowser(List<EabCategory> categories, Dictionary<string, EabAsset> byPath, DateTime read)
    {
        Categories = categories;
        _byPath = byPath;
        Read = read;
        Tags = [.. categories.SelectMany(c => c.Tags).Concat(byPath.Values.SelectMany(a => a.Tags))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The lists last read, once they have been; null until then, or when they can't be had.</summary>
    public static EasyAssetBrowser? Current { get; private set; }

    public IReadOnlyList<EabCategory> Categories { get; }

    /// <summary>Every tag, of every category, by name.</summary>
    public IReadOnlyList<string> Tags { get; }

    public int Count => _byPath.Count;

    /// <summary>When the lists were read from GitHub.</summary>
    public DateTime Read { get; }

    /// <summary>How the lists were had, for the status line, such as "read from GitHub" or "from the cache: GitHub couldn't be reached".</summary>
    public string Note { get; private set; } = "";

    /// <summary>What EasyAssetBrowser lists for a game path (backslashes, as the game's), or null when it doesn't list it.</summary>
    public EabAsset? Find(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>
    /// Reads the lists: from the cache when they were read from GitHub in the last day (unless <paramref name="refresh"/>),
    /// otherwise from GitHub into the cache, and from the cache again if GitHub can't be reached. Null when there are
    /// none to be had.
    /// </summary>
    public static async Task<EasyAssetBrowser?> LoadAsync(bool refresh, CancellationToken token = default)
    {
        string config = Path.Combine(CacheFolder, ConfigFile);
        bool fresh = File.Exists(config) && DateTime.UtcNow - File.GetLastWriteTimeUtc(config) < MaxAge;
        string note;
        if (fresh && !refresh) note = "from the cache";
        else
            try
            {
                await DownloadAsync(token);
                note = "read from GitHub";
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException && !token.IsCancellationRequested)
            {
                note = $"from the cache: GitHub couldn't be reached ({ex.Message})";
            }
        try
        {
            var read = await Task.Run(ReadCache, token);
            if (read is not null) read.Note = note;
            return Current = read ?? Current;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Current;
        }
    }

    /// <summary>Lets go of the lists read, as EasyAssetBrowser is turned off, and deletes the ones downloaded when <paramref name="deleteCache"/>.</summary>
    public static void Forget(bool deleteCache)
    {
        Current = null;
        if (!deleteCache) return;
        try
        {
            if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Downloads the category list and every list it names into a new folder, then puts it in place of the cache.</summary>
    private static async Task DownloadAsync(CancellationToken token)
    {
        string incoming = CacheFolder + ".incoming";
        if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
        Directory.CreateDirectory(incoming);
        byte[] config = await Http.GetByteArrayAsync(DataUrl + ConfigFile, token);
        foreach (var category in ParseConfig(config))
            await File.WriteAllBytesAsync(Path.Combine(incoming, category.File), await Http.GetByteArrayAsync(DataUrl + category.File, token), token);
        // the config last, so a cache with a config has all its lists
        await File.WriteAllBytesAsync(Path.Combine(incoming, ConfigFile), config, token);
        if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, true);
        Directory.Move(incoming, CacheFolder);
    }

    /// <summary>The categories the config names, each with a file of its own that's only a name (nothing that leads out of the cache folder).</summary>
    private static List<EabCategory> ParseConfig(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var categories = new List<EabCategory>();
        foreach (var c in doc.RootElement.GetProperty("categories").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString() ?? "";
            string file = c.GetProperty("file").GetString() ?? "";
            if (name.Length == 0 || !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) != file
                || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Equals(ConfigFile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"EasyAssetBrowser's category list names a file that isn't a list: \"{file}\".");
            var tags = c.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString()).OfType<string>().Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : [];
            categories.Add(new EabCategory(name, file, tags));
        }
        return categories;
    }

    private static EasyAssetBrowser? ReadCache()
    {
        string config = Path.Combine(CacheFolder, ConfigFile);
        if (!File.Exists(config)) return null;
        var categories = ParseConfig(File.ReadAllBytes(config));
        var byPath = new Dictionary<string, EabAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            string file = Path.Combine(CacheFolder, category.File);
            if (!File.Exists(file)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("path", out var p) || p.GetString() is not { Length: > 0 } path) continue;
                var tags = a.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];
                byPath[GamePath(path)] = new EabAsset(Text(a, "name"), Text(a, "category") is { Length: > 0 } c ? c : category.Name, Text(a, "origin"), tags);
            }
        }
        return new EasyAssetBrowser(categories, byPath, File.GetLastWriteTime(config));

        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>EasyAssetBrowser's path (forward slashes) as the game's.</summary>
    public static string GamePath(string path) => path.Replace('/', '\\').TrimStart('\\');

    /// <summary>A game path as EasyAssetBrowser writes it, with forward slashes.</summary>
    public static string EabPath(string path) => path.Replace('\\', '/');

    /// <summary>
    /// The category EasyAssetBrowser would put a file in, by its type, or null when it has none for it. Of textures it
    /// lists only decals, all of which are in a decals folder or named as one.
    /// </summary>
    public static string? CategoryFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".w2mesh" => "Meshes",
        ".w2ent" => "Entities",
        ".xbm" when path.Contains("decal", StringComparison.OrdinalIgnoreCase) => "Decals",
        ".w2p" => "Particles",
        ".w2comm" => "Communities",
        ".srt" => "SRT Trees",
        _ => null,
    };
}
