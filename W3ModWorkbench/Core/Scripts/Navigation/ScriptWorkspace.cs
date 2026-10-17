using System.IO;
using System.Runtime.CompilerServices;

namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>
/// The scripts a preview can jump into: every .ws file in the same archive, then those in the other
/// archives open in the main window, then the game's own scripts. Each is indexed in the background once,
/// the first time a preview needs it. The loose scripts of the game's installed mods (Shared Utils ships
/// that way), and after them the mods' compiled script blobs, join in before the game's only once the user
/// asks for them. Each is read once and kept until the app closes.
/// </summary>
public sealed class ScriptWorkspace
{
    private static readonly ConditionalWeakTable<IArchive, ScriptWorkspace> ForArchive = [];
    private static readonly ConditionalWeakTable<IArchive, Task<ScriptIndex>> ArchiveCache = [];
    private static readonly Dictionary<string, Task<ScriptIndex>> GameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<List<(string Mod, ScriptIndex Index)>>> ModsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<List<(string Mod, ScriptIndex Index)>>> BlobsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CacheLock = new();
    private static bool _looseModsWanted;
    private static bool _compiledModsWanted;

    private readonly IArchive _own;
    private readonly Func<IEnumerable<IArchive>> _openArchives;
    private readonly Task<ScriptIndex> _game;

    private ScriptWorkspace(IArchive archive, Func<IEnumerable<IArchive>> openArchives)
    {
        _own = archive;
        _openArchives = openArchives;
        _ = ArchiveIndex(archive);
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

    /// <summary>Whether the installed mods' compiled script blobs are searched. Once asked for, they stay in until the app closes.</summary>
    public bool IncludesCompiledMods => _compiledModsWanted && ModsFolder is not null;

    /// <summary>Adds the installed mods' compiled scripts to every preview's lookups, and waits for them to be decompiled and read.</summary>
    public Task IncludeCompiledModsAsync()
    {
        _compiledModsWanted = true;
        return Ready;
    }

    /// <summary>The archive's own scripts. Read again after a file in it is saved, so it's looked up each time.</summary>
    private Task<ScriptIndex> OwnIndex => ArchiveIndex(_own);

    /// <summary>The archive a file is in: this one or another one open, or null for the game's or a mod's scripts.</summary>
    public IArchive? ArchiveOf(ScriptFile file) =>
        _openArchives().Prepend(_own).FirstOrDefault(a => string.Equals(a.FilePath, file.SourceKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads an archive's scripts again, after one of them was saved, so every preview's lookups see the change.</summary>
    public static void Reindex(IArchive archive) => ArchiveCache.AddOrUpdate(archive, Task.Run(() => IndexArchive(archive)));

    public bool IsReady => OwnIndex.IsCompleted && _game.IsCompleted && (Mods()?.IsCompleted ?? true) && (Blobs()?.IsCompleted ?? true)
        && Others().All(t => t.IsCompleted);

    public Task Ready => Task.WhenAll([OwnIndex, _game, .. Mods() is { } mods ? [mods] : Array.Empty<Task>(),
        .. Blobs() is { } blobs ? [blobs] : Array.Empty<Task>(), .. Others()]);

    private Task<List<(string Mod, ScriptIndex Index)>>? Mods() => IncludesLooseMods ? ModsIndex(GameScriptsFolder!, ModsFolder!) : null;

    private Task<List<(string Mod, ScriptIndex Index)>>? Blobs() => IncludesCompiledMods ? BlobsIndex(ModsFolder!) : null;

    /// <summary>
    /// The archive's scripts, then the other open archives' in the order they were opened, then the installed
    /// mods' loose scripts and then their compiled scripts when asked for, then the game's. Empty until <see cref="Ready"/>.
    /// Opening or closing an archive changes it.
    /// </summary>
    public IReadOnlyList<ScriptIndex> Layers => IsReady
        ? [Result(OwnIndex), .. Others().Select(Result), .. NotOpen(Mods()), .. NotOpen(Blobs()), Result(_game)]
        : [];

    /// <summary>
    /// Installed mods' scripts, leaving out mods with an archive open: those are searched already, and often
    /// ship the same scripts loose as well.
    /// </summary>
    private IEnumerable<ScriptIndex> NotOpen(Task<List<(string Mod, ScriptIndex Index)>>? scripts)
    {
        var open = _openArchives().Append(_own).Select(a => a.Owner).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mods = scripts is { IsCompletedSuccessfully: true } task ? task.Result : [];
        return mods.Where(m => !open.Contains(m.Mod)).Select(m => m.Index);
    }

    private List<Task<ScriptIndex>> Others() =>
        [.. _openArchives().Where(a => !ReferenceEquals(a, _own)).Select(ArchiveIndex)];

    private static ScriptIndex Result(Task<ScriptIndex> task) => task.IsCompletedSuccessfully ? task.Result : ScriptIndex.Empty;

    // ---------------------------------------------------------------- game files and their replacements

    /// <summary>
    /// The game's own version of a script a mod ships loose, matched by its path under scripts\, or null when
    /// the game has no file there. A game script, or a file from a decompiled blob, has none.
    /// </summary>
    public ScriptFile? GameVersionOf(ScriptFile file)
    {
        if (GameScriptsFolder is null || IsGameFile(file)) return null;
        string path = file.Path.Replace('/', '\\');
        foreach (string prefix in new[] { @"content\scripts\", @"scripts\" })
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string relative = path[prefix.Length..];
            // a decompiled blob's files are named after classes, not after the game's files
            if (relative.StartsWith(@"classes\", StringComparison.OrdinalIgnoreCase) || !relative.Contains('\\')) return null;
            string full = Path.GetFullPath(Path.Combine(GameScriptsFolder, relative));
            return File.Exists(full) ? GameFile(full) : null;
        }
        return null;
    }

    /// <summary>The loose copies of a game script that installed mods ship, each replacing it outright when the game runs.</summary>
    public List<ScriptFile> LooseReplacementsOf(ScriptFile file)
    {
        var result = new List<ScriptFile>();
        if (ModsFolder is null || !IsGameFile(file)) return result;
        string relative = Path.GetRelativePath(GameScriptsFolder!, file.Key);
        try
        {
            foreach (string mod in Directory.GetDirectories(ModsFolder).Order(StringComparer.OrdinalIgnoreCase))
            {
                string content = Path.Combine(mod, "content");
                string path = Path.Combine(content, "scripts", relative);
                if (File.Exists(path))
                    result.Add(LooseFile(path, content, Path.GetFileName(mod) + " (loose scripts)", Path.Combine(content, "scripts")));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable mods folder has nothing to list
        }
        return result;
    }

    private bool IsGameFile(ScriptFile file) =>
        GameScriptsFolder is not null && string.Equals(file.SourceKey, GameScriptsFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>The game's script at a full path: the indexed one when there is one, so its names lead somewhere.</summary>
    private ScriptFile GameFile(string full) =>
        Result(_game).Files.FirstOrDefault(f => string.Equals(f.Key, full, StringComparison.OrdinalIgnoreCase))
        ?? LooseFile(full, GameScriptsFolder!, "Game scripts", GameScriptsFolder!);

    private static ScriptFile LooseFile(string path, string root, string source, string sourceKey) => new()
    {
        Key = Path.GetFullPath(path),
        Path = Path.GetRelativePath(root, path),
        Source = source,
        SourceKey = sourceKey,
        Read = () => File.ReadAllBytes(path),
    };

    // ---------------------------------------------------------------- files

    /// <summary>The navigation identity of an archive entry.</summary>
    public static ScriptFile FileFor(ArchiveEntry entry) => new()
    {
        Key = entry.Archive.FilePath + "|" + entry.Path,
        Path = entry.Path,
        Source = entry.Archive.Owner + "\\" + entry.Archive.Name,
        SourceKey = entry.Archive.FilePath,
        DiskPath = (entry as LooseScriptEntry)?.FullPath,
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

    private static Task<List<(string Mod, ScriptIndex Index)>> BlobsIndex(string modsFolder)
    {
        lock (CacheLock)
        {
            if (!BlobsCache.TryGetValue(modsFolder, out var task))
                BlobsCache[modsFolder] = task = Task.Run(() => IndexBlobs(modsFolder));
            return task;
        }
    }

    /// <summary>mods\X\content\*.rsblob, decompiled and indexed like an opened blob, one index per mod in the folder's order.</summary>
    private static List<(string Mod, ScriptIndex Index)> IndexBlobs(string modsFolder)
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

        var found = new List<(string Mod, ScriptIndex Index)>[mods.Length];
        Parallel.For(0, mods.Length, i =>
        {
            found[i] = [];
            string content = Path.Combine(mods[i], "content");
            if (!Directory.Exists(content)) return;
            foreach (string blob in Directory.EnumerateFiles(content, "*.rsblob"))
            {
                try
                {
                    var index = IndexArchive(ScriptArchive.Open(blob));
                    if (index != ScriptIndex.Empty) found[i].Add((Path.GetFileName(mods[i]), index));
                }
                catch (Exception)
                {
                    // a blob that won't decompile shouldn't stop the other mods from being found
                }
            }
        });
        return [.. found.SelectMany(f => f)];
    }

    /// <summary>Every .ws file under <paramref name="folder"/>, with paths shown relative to <paramref name="root"/>.</summary>
    private static ScriptIndex IndexFolder(string folder, string root, string source, Func<string, bool> skip)
    {
        string[] paths;
        try
        {
            paths = [.. Directory.GetFiles(folder, "*.ws", SearchOption.AllDirectories).Where(p => !skip(p))];
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
            var file = LooseFile(path, root, source, folder);
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
