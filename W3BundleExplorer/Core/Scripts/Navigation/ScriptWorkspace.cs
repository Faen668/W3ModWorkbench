using System.IO;
using System.Runtime.CompilerServices;

namespace W3BundleExplorer.Core.Scripts.Navigation;

/// <summary>
/// The scripts a preview can jump into: every .ws file in the same archive, then those in the other
/// archives open in the main window, then the game's own scripts. Each is indexed in the background once,
/// the first time a preview needs it. The loose scripts of the game's installed mods (Shared Utils ships
/// that way) join in, before the game's, only once the user asks for them.
/// </summary>
public sealed class ScriptWorkspace
{
    private static readonly ConditionalWeakTable<IArchive, ScriptWorkspace> ForArchive = [];
    private static readonly ConditionalWeakTable<IArchive, Task<ScriptIndex>> ArchiveCache = [];
    private static readonly Dictionary<string, Task<ScriptIndex>> GameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<List<(string Mod, ScriptIndex Index)>>> ModsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CacheLock = new();
    private static bool _looseModsWanted;

    private readonly IArchive _own;
    private readonly Func<IEnumerable<IArchive>> _openArchives;
    private readonly Task<ScriptIndex> _archive;
    private readonly Task<ScriptIndex> _game;

    private ScriptWorkspace(IArchive archive, Func<IEnumerable<IArchive>> openArchives)
    {
        _own = archive;
        _openArchives = openArchives;
        _archive = ArchiveIndex(archive);
        GameScriptsFolder = GameScripts.FindFolder(archive.FilePath);
        _game = GameScriptsFolder is null ? Task.FromResult(ScriptIndex.Empty) : GameIndex(GameScriptsFolder);
        ModsFolder = GameScriptsFolder is null ? null : Path.GetFullPath(Path.Combine(GameScriptsFolder, "..", "..", "..", "mods"));
        if (ModsFolder is not null && !Directory.Exists(ModsFolder)) ModsFolder = null;
    }

    /// <param name="openArchives">The archives open right now, read each time the layers are asked for (on the UI thread).</param>
    public static ScriptWorkspace For(IArchive archive, Func<IEnumerable<IArchive>> openArchives) =>
        ForArchive.GetValue(archive, a => new ScriptWorkspace(a, openArchives));

    /// <summary>The game's scripts folder, or null when the game couldn't be found.</summary>
    public string? GameScriptsFolder { get; }

    /// <summary>The game's mods folder, or null when there isn't one.</summary>
    public string? ModsFolder { get; }

    /// <summary>Whether the installed mods' loose scripts are searched. Once asked for, they stay in until the app closes.</summary>
    public bool IncludesLooseMods => _looseModsWanted && ModsFolder is not null;

    /// <summary>Adds the installed mods' loose scripts to every preview's lookups, and waits for them to be read.</summary>
    public Task IncludeLooseModsAsync()
    {
        _looseModsWanted = true;
        return Ready;
    }

    public bool IsReady => _archive.IsCompleted && _game.IsCompleted && (Mods()?.IsCompleted ?? true) && Others().All(t => t.IsCompleted);

    public Task Ready => Task.WhenAll([_archive, _game, .. Mods() is { } mods ? [mods] : Array.Empty<Task>(), .. Others()]);

    private Task<List<(string Mod, ScriptIndex Index)>>? Mods() => IncludesLooseMods ? ModsIndex(GameScriptsFolder!, ModsFolder!) : null;

    /// <summary>
    /// The archive's scripts, then the other open archives' in the order they were opened, then the installed
    /// mods' loose scripts when asked for, then the game's. Empty until <see cref="Ready"/>. Opening or closing an archive changes it.
    /// </summary>
    public IReadOnlyList<ScriptIndex> Layers => IsReady ? [Result(_archive), .. Others().Select(Result), .. LooseMods(), Result(_game)] : [];

    /// <summary>Installed mods' loose scripts, leaving out mods with an archive open: those often ship the same scripts loose as well.</summary>
    private IEnumerable<ScriptIndex> LooseMods()
    {
        var open = _openArchives().Append(_own).Select(a => a.Owner).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mods = Mods() is { IsCompletedSuccessfully: true } task ? task.Result : [];
        return mods.Where(m => !open.Contains(m.Mod)).Select(m => m.Index);
    }

    private List<Task<ScriptIndex>> Others() =>
        _openArchives().Where(a => !ReferenceEquals(a, _own)).Select(ArchiveIndex).ToList();

    private static ScriptIndex Result(Task<ScriptIndex> task) => task.IsCompletedSuccessfully ? task.Result : ScriptIndex.Empty;

    // ---------------------------------------------------------------- files

    /// <summary>The navigation identity of an archive entry.</summary>
    public static ScriptFile FileFor(ArchiveEntry entry) => new()
    {
        Key = entry.Archive.FilePath + "|" + entry.Path,
        Path = entry.Path,
        Source = entry.Archive.Owner + "\\" + entry.Archive.Name,
        SourceKey = entry.Archive.FilePath,
        Read = () =>
        {
            if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
            using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return entry.Read(fs);
        },
    };

    private static Task<ScriptIndex> ArchiveIndex(IArchive archive) =>
        ArchiveCache.GetValue(archive, a => Task.Run(() => IndexArchive(a)));

    private static ScriptIndex IndexArchive(IArchive archive)
    {
        var symbols = new List<ScriptSymbol>();
        var entries = archive.Entries
            .Where(e => e.Path.EndsWith(".ws", StringComparison.OrdinalIgnoreCase))
            // a decompiled blob's all.ws repeats every other file
            .Where(e => archive is not ScriptArchive || !e.Path.Equals(@"scripts\all.ws", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.SortKey)
            .ToList();
        if (entries.Count == 0) return ScriptIndex.Empty;

        using var fs = archive.ReadsFromDisk
            ? new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            : null;
        foreach (var entry in entries)
        {
            try
            {
                var file = FileFor(entry);
                symbols.AddRange(ScriptParser.Parse(TextDecoding.Decode(entry.Read(fs)), file).Symbols);
            }
            catch (Exception)
            {
                // one unreadable file shouldn't stop the rest from being found
            }
        }
        return new ScriptIndex(symbols);
    }

    private static Task<ScriptIndex> GameIndex(string folder)
    {
        lock (CacheLock)
        {
            if (!GameCache.TryGetValue(folder, out var task))
                GameCache[folder] = task = Task.Run(() => IndexGame(folder));
            return task;
        }
    }

    private static ScriptIndex IndexGame(string folder) => IndexFolder(folder, folder, "Game scripts", _ => false);

    private static Task<List<(string Mod, ScriptIndex Index)>> ModsIndex(string gameScripts, string modsFolder)
    {
        lock (CacheLock)
        {
            if (!ModsCache.TryGetValue(modsFolder, out var task))
                ModsCache[modsFolder] = task = Task.Run(() => IndexMods(gameScripts, modsFolder));
            return task;
        }
    }

    /// <summary>
    /// mods\X\content\scripts of the same game install, one index per mod, in the folder's order. A loose file at
    /// the same path as one of the game's scripts replaces it outright, so it's left out: it's mostly the game's own code.
    /// </summary>
    private static List<(string Mod, ScriptIndex Index)> IndexMods(string gameScripts, string modsFolder)
    {
        string[] mods;
        try
        {
            mods = Directory.GetDirectories(modsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        Array.Sort(mods, StringComparer.OrdinalIgnoreCase);

        var result = new List<(string, ScriptIndex)>();
        foreach (string mod in mods)
        {
            string content = Path.Combine(mod, "content");
            string scripts = Path.Combine(content, "scripts");
            if (!Directory.Exists(scripts)) continue;
            string name = Path.GetFileName(mod);
            var index = IndexFolder(scripts, content, name + " (loose scripts)",
                path => File.Exists(Path.Combine(gameScripts, Path.GetRelativePath(scripts, path))));
            if (index != ScriptIndex.Empty) result.Add((name, index));
        }
        return result;
    }

    /// <summary>Every .ws file under <paramref name="folder"/>, with paths shown relative to <paramref name="root"/>.</summary>
    private static ScriptIndex IndexFolder(string folder, string root, string source, Func<string, bool> skip)
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles(folder, "*.ws", SearchOption.AllDirectories).Where(p => !skip(p)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ScriptIndex.Empty;
        }
        if (paths.Length == 0) return ScriptIndex.Empty;

        var parsed = new List<ScriptSymbol>[paths.Length];
        Parallel.For(0, paths.Length, i =>
        {
            string path = paths[i];
            var file = new ScriptFile
            {
                Key = Path.GetFullPath(path),
                Path = Path.GetRelativePath(root, path),
                Source = source,
                SourceKey = folder,
                Read = () => File.ReadAllBytes(path),
            };
            try
            {
                parsed[i] = ScriptParser.Parse(TextDecoding.Decode(File.ReadAllBytes(path)), file).Symbols;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                parsed[i] = [];
            }
        });
        return new ScriptIndex(parsed.SelectMany(p => p));
    }
}
