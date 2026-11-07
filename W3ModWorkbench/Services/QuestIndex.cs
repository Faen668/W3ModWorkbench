using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Quests;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A phase block in some quest file that runs an external phase.</summary>
public sealed record QuestUse(string File, string BlockName, Guid? Guid, bool Reachable);

/// <summary>
/// One copy of a block with a GUID: the quest file it's in, where that copy is (the game's own bundles, an official DLC,
/// an installed mod, the project), the block's name and class, and whether the game reads that copy and reaches it.
/// </summary>
public sealed record QuestGuidCopy(string File, string Source, SourceKind Kind, string BlockName, string ClassName, bool Effective, bool Reachable,
    Func<byte[]?> Read, string? Origin)
{
    /// <summary>The copy's place in words: "Game", "DLC ep1", the mod's folder, "Project".</summary>
    public override string ToString() => $"{Source} · {File} › {BlockName}";
}

/// <summary>One copy of a quest file: where it is, whether the game reads it, and how to read it.</summary>
public sealed record QuestFileCopy(string File, string Source, SourceKind Kind, bool Effective, Func<byte[]?> Read, string Origin);

/// <summary>Where a copy of a quest file is.</summary>
public enum SourceKind { Game, Dlc, Mod, Project }

/// <summary>
/// Every quest file the game, its DLC and the installed mods have, every copy of each read once in the background: which
/// phase blocks run each external phase, which files the game reaches from its root quests, and where every block's GUID
/// is (the game's copy, a mod's, all of them). The open project's files are looked through each time a GUID is found, as
/// they change as one works. Built the first time a quest viewer asks, and kept for the session.
/// </summary>
public sealed class QuestIndex
{
    private static readonly ConditionalWeakTable<ResourceLocator, Task<QuestIndex>> Built = new();

    /// <summary>The phase blocks that run each phase, by the phase's path (the copies the game reads).</summary>
    public required IReadOnlyDictionary<string, List<QuestUse>> UsedBy { get; init; }
    /// <summary>The files the game runs: its root quests and every phase they reach.</summary>
    public required IReadOnlySet<string> Reachable { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
    /// <summary>Every copy of every block with a GUID, by the GUID.</summary>
    public required IReadOnlyDictionary<Guid, List<QuestGuidCopy>> Guids { get; init; }
    public int Files { get; init; }
    private ResourceLocator Locator { get; init; } = null!;
    private IReadOnlyDictionary<string, List<QuestFileCopy>> FileCopies { get; init; } = new Dictionary<string, List<QuestFileCopy>>();

    /// <summary>Every copy of a quest file in the game, its DLC and the installed mods, the game's own first.</summary>
    public IReadOnlyList<QuestFileCopy> CopiesOf(string path) => FileCopies.TryGetValue(path, out var copies) ? copies : [];

    /// <summary>The index for a locator: built now in the background, or the one built before.</summary>
    public static Task<QuestIndex> GetAsync(ResourceLocator locator)
    {
        lock (Built) return Built.GetValue(locator, l => Task.Run(() => Build(l)));
    }

    /// <summary>Every copy of a GUID: the game's, the DLC's and the installed mods' from the index, and the project's read now.</summary>
    public List<QuestGuidCopy> Find(Guid guid)
    {
        var copies = Guids.TryGetValue(guid, out var known) ? new List<QuestGuidCopy>(known) : [];
        if (Locator.ProjectFolder is { } project && Directory.Exists(project))
            foreach (string file in ProjectQuestFiles(project))
            {
                byte[] data;
                QuestFile quest;
                try { data = File.ReadAllBytes(file); quest = QuestFile.Read(data, file); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException) { continue; }
                string path = Path.GetRelativePath(project, file);
                foreach (var block in quest.AllBlocks())
                    if (block.Guid == guid)
                        copies.Add(new QuestGuidCopy(path, L.T("QuestGraph.Source.Project"), SourceKind.Project, block.Name, block.ClassName, true,
                            Reachable.Contains(path), () => File.ReadAllBytes(file), file));
            }
        return [.. copies.OrderBy(c => c.Kind).ThenBy(c => c.Source, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.File, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The other files the game reaches that have a GUID: what makes it a duplicate there. The copies of the same file
    /// (a mod's and the game's) aren't counted, as the game reads only one of them.
    /// </summary>
    public List<QuestGuidCopy> ReachedElsewhere(Guid guid, string file) =>
        Guids.TryGetValue(guid, out var copies)
            ? [.. copies.Where(c => c.Effective && c.Reachable && !string.Equals(c.File, file, StringComparison.OrdinalIgnoreCase))]
            : [];

    private static IEnumerable<string> ProjectQuestFiles(string project)
    {
        try
        {
            return Directory.EnumerateFiles(project, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".w2phase", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".w2quest", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static QuestIndex Build(ResourceLocator locator)
    {
        // every copy: each bundle of the game, its DLC and the installed mods, read for itself
        var copies = new List<(string Path, string Source, SourceKind Kind, ArchiveEntry Entry)>();
        string? game = locator.GameFolder ?? GameLocator.Resolve(null);
        if (game is not null)
            foreach (string bundle in Bundles(game))
            {
                Bundle opened;
                try { opened = Bundle.Open(bundle); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
                var (source, kind) = SourceOf(game, bundle);
                foreach (var entry in opened.Entries)
                    if (QuestGraphWindow.CanShow(entry.Path)) copies.Add((entry.Path, source, kind, entry));
            }

        var guids = new ConcurrentDictionary<Guid, ConcurrentBag<QuestGuidCopy>>();
        var uses = new ConcurrentBag<(string Phase, string File, string Name, Guid? Guid)>();
        var phasesOf = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var fileCopies = new ConcurrentBag<QuestFileCopy>();
        var reachedLater = new ConcurrentBag<(Guid Guid, string File, string Source, SourceKind Kind, string Name, string Class, bool Effective, Func<byte[]?> Read, string Origin)>();
        Parallel.ForEach(copies, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, copy =>
        {
            Func<byte[]?> read = () => ReadEntry(copy.Entry);
            QuestFile file;
            try { file = QuestFile.Read(read() ?? [], copy.Path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { return; }
            // the copy the game reads is the one the locator finds
            bool effective = string.Equals(locator.Find(copy.Path)?.Archive.FilePath, copy.Entry.Archive.FilePath, StringComparison.OrdinalIgnoreCase);
            fileCopies.Add(new QuestFileCopy(copy.Path, copy.Source, copy.Kind, effective, read, copy.Entry.Archive.FilePath));
            var phases = new List<string>();
            foreach (var block in file.AllBlocks())
            {
                if (block.Guid is { } g) reachedLater.Add((g, copy.Path, copy.Source, copy.Kind, block.Name, block.ClassName, effective, read, copy.Entry.Archive.FilePath));
                if (effective && block.ExternalPhase is { } phase)
                {
                    uses.Add((phase, copy.Path, block.Name, block.Guid));
                    phases.Add(phase);
                }
            }
            if (effective) phasesOf[copy.Path] = phases;
        });

        var roots = QuestRoots.Find(locator);
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out string? path))
            if (reachable.Add(path) && phasesOf.TryGetValue(path, out var next))
                foreach (string phase in next) pending.Push(phase);

        foreach (var c in reachedLater)
            guids.GetOrAdd(c.Guid, _ => []).Add(new QuestGuidCopy(c.File, c.Source, c.Kind, c.Name, c.Class, c.Effective, c.Effective && reachable.Contains(c.File), c.Read, c.Origin));

        var usedBy = uses.GroupBy(u => u.Phase, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
            g => g.Select(u => new QuestUse(u.File, u.Name, u.Guid, reachable.Contains(u.File)))
                .OrderByDescending(u => u.Reachable).ThenBy(u => u.File, StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        return new QuestIndex
        {
            UsedBy = usedBy,
            Reachable = reachable,
            Roots = roots,
            Guids = guids.ToDictionary(g => g.Key, g => g.Value.ToList()),
            Files = copies.Count,
            Locator = locator,
            FileCopies = fileCopies.GroupBy(c => c.File, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Kind).ThenBy(c => c.Source, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase),
        };
    }

    private static byte[]? ReadEntry(ArchiveEntry entry)
    {
        try
        {
            using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return entry.Read(fs);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every bundle under the game's content, dlc and mods folders.</summary>
    private static IEnumerable<string> Bundles(string game)
    {
        foreach (string root in new[] { "content", "dlc", "mods" })
        {
            string folder = Path.Combine(game, root);
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(folder, "*.bundle", SearchOption.AllDirectories).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string bundle in found) yield return bundle;
        }
    }

    /// <summary>Where a bundle is, in words: the game's own, an official DLC (its folder), or an installed mod (its folder).</summary>
    private static (string Source, SourceKind Kind) SourceOf(string game, string bundle)
    {
        var parts = Path.GetRelativePath(game, bundle).Split('\\', '/');
        if (parts.Length > 1 && parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase)) return (parts[1], SourceKind.Mod);
        if (parts.Length > 1 && parts[0].Equals("dlc", StringComparison.OrdinalIgnoreCase))
            return OfficialDlc.Is(game, parts[1]) ? (L.T("QuestGraph.Source.Dlc", parts[1]), SourceKind.Dlc) : (parts[1], SourceKind.Mod);
        return (L.T("QuestGraph.Source.Game"), SourceKind.Game);
    }
}
