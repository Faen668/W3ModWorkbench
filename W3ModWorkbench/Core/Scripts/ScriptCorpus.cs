using System.Collections.Concurrent;
using System.IO;
using W3ModWorkbench.Core.Scripts.Navigation;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>Where a script in a <see cref="ScriptCorpus"/> comes from.</summary>
public enum CorpusOrigin { Project, Mod, Game }

/// <summary>
/// One script to read. <see cref="ScriptFile.Path"/> is relative to its scripts folder (game\player\player.ws), so a
/// mod's loose copy of a game script has the same path as the game's. <paramref name="Entry"/> is set for a file
/// decompiled from a compiled mod, so it can be opened with the rest of its blob.
/// </summary>
/// <param name="Label">The project's or mod's name, or "Game scripts".</param>
public sealed record CorpusFile(ScriptFile File, CorpusOrigin Origin, string Label, ArchiveEntry? Entry = null);

/// <summary>
/// Every script some tools read at once, straight from disk with nothing open: the open project's, the installed mods'
/// loose scripts and their compiled scripts decompiled, and the game's. Used by the console command list, the
/// fact browser and the log viewer, which look for patterns in the text rather than following names.
/// </summary>
public static class ScriptCorpus
{
    /// <param name="GameFolder">The game's folder, or null when it isn't known.</param>
    /// <param name="Mods">Read mods\X\content\scripts, and decompile mods\X\content\*.rsblob.</param>
    public sealed record Options(string? GameFolder, bool Mods = true, bool Project = true);

    /// <summary>A decompiled blob is kept until the file changes, so asking again is quick.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime Written, ScriptArchive Archive)> Blobs = new(StringComparer.OrdinalIgnoreCase);

    public static string? GameScriptsFolder(string? gameFolder)
    {
        if (gameFolder is null) return null;
        string scripts = Path.Combine(gameFolder, "content", "content0", "scripts");
        return Directory.Exists(scripts) ? scripts : null;
    }

    /// <summary>The project's files first, then the mods' in folder order, then the game's.</summary>
    public static List<CorpusFile> Gather(Options options, CancellationToken cancel)
    {
        var files = new List<CorpusFile>();
        if (options.Project && Projects.ProjectWorkspace.Current is { } project && Directory.Exists(project.ScriptsPath))
            files.AddRange(Loose(project.ScriptsPath, project.Name, CorpusOrigin.Project, editable: true));

        string? mods = options.GameFolder is null ? null : Path.Combine(options.GameFolder, "mods");
        if (options.Mods && mods is not null && Directory.Exists(mods))
        {
            string[] folders;
            try { folders = Directory.GetDirectories(mods); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folders = []; }
            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);

            var compiled = new List<CorpusFile>[folders.Length];
            Parallel.For(0, folders.Length, new ParallelOptions { CancellationToken = cancel }, i => compiled[i] = Compiled(folders[i]));
            for (int i = 0; i < folders.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                string name = Path.GetFileName(folders[i]);
                string scripts = Path.Combine(folders[i], "content", "scripts");
                // the game runs a mod's compiled scripts when it has them, so its loose copy is only read for a mod without
                files.AddRange(compiled[i]);
                if (compiled[i].Count == 0 && Directory.Exists(scripts)) files.AddRange(Loose(scripts, name, CorpusOrigin.Mod, editable: false));
            }
        }

        if (GameScriptsFolder(options.GameFolder) is { } game)
            files.AddRange(Loose(game, "Game scripts", CorpusOrigin.Game, editable: false));
        return files;
    }

    private static IEnumerable<CorpusFile> Loose(string folder, string label, CorpusOrigin origin, bool editable)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\');
        string[] paths;
        try { paths = Directory.GetFiles(folder, "*.ws", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        string source = origin == CorpusOrigin.Mod ? label + " (loose scripts)" : label;
        foreach (string path in paths)
        {
            string full = path;
            yield return new CorpusFile(new ScriptFile
            {
                Key = full,
                Path = Path.GetRelativePath(folder, full),
                Source = source,
                SourceKey = folder,
                DiskPath = editable ? full : null,
                Read = () => File.ReadAllBytes(full),
            }, origin, label);
        }
    }

    /// <summary>A mod's compiled scripts, decompiled, with paths starting under scripts\ as the decompiler writes them.</summary>
    private static List<CorpusFile> Compiled(string modFolder)
    {
        var result = new List<CorpusFile>();
        string content = Path.Combine(modFolder, "content");
        if (!Directory.Exists(content)) return result;
        string name = Path.GetFileName(modFolder);
        try
        {
            foreach (string blob in Directory.EnumerateFiles(content, "*.rsblob"))
            {
                try
                {
                    var written = File.GetLastWriteTimeUtc(blob);
                    var archive = Blobs.TryGetValue(blob, out var kept) && kept.Written == written ? kept.Archive : ScriptArchive.Open(blob);
                    Blobs[blob] = (written, archive);
                    foreach (var entry in archive.Entries)
                    {
                        // all.ws repeats every other file
                        if (!entry.Path.EndsWith(".ws", StringComparison.OrdinalIgnoreCase)
                            || entry.Path.Equals(@"scripts\all.ws", StringComparison.OrdinalIgnoreCase)) continue;
                        var file = ScriptWorkspace.FileFor(entry);
                        var shown = new ScriptFile
                        {
                            Key = file.Key,
                            Path = entry.Path.StartsWith(@"scripts\", StringComparison.OrdinalIgnoreCase) ? entry.Path[8..] : entry.Path,
                            Source = name + " (compiled scripts)",
                            SourceKey = file.SourceKey,
                            Read = file.Read,
                        };
                        result.Add(new CorpusFile(shown, CorpusOrigin.Mod, name, entry));
                    }
                }
                catch (Exception)
                {
                    // a blob that won't decompile shouldn't stop the other mods from being read
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    /// <summary>The line an offset is on, 1-based, and its text with tabs as spaces, for listing a match.</summary>
    public static ScriptHit Hit(ScriptFile file, List<int> lineStarts, string text, int offset, int length, string? label = null) =>
        ScriptSearch.Hit(file, lineStarts, text, offset, length, label);

    public static List<int> LineStarts(string text) => ScriptSearch.LineStarts(text);
}
