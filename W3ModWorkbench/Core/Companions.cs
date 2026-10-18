using System.IO;

namespace W3ModWorkbench.Core;

/// <summary>
/// Files that only work as a set. A resource such as a mesh keeps its bulk data in numbered buffers
/// (<c>wsteel.w2mesh</c> with <c>wsteel.w2mesh.1.buffer</c>, <c>.2.buffer</c>…), and neither is any use without the other,
/// so extracting one takes the rest along. The game packs buffers in bundles of their own (<c>buffers0.bundle</c> beside
/// <c>blob0.bundle</c>), so they're looked for in every open archive in the same folder.
/// </summary>
public static class Companions
{
    /// <summary>The resource a buffer belongs to: <c>x.w2mesh.1.buffer</c> gives <c>x.w2mesh</c>. Null when it isn't a numbered buffer.</summary>
    public static string? BufferOwner(string path)
    {
        if (!path.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase)) return null;
        string stem = path[..^".buffer".Length];
        int dot = stem.LastIndexOf('.');
        if (dot <= 0 || dot == stem.Length - 1) return null;
        for (int i = dot + 1; i < stem.Length; i++)
            if (!char.IsAsciiDigit(stem[i])) return null;
        return stem[..dot];
    }

    /// <summary>
    /// <paramref name="entries"/> with the companions of each one that aren't already there, after the files they go with.
    /// Companions are looked for in <paramref name="open"/> archives in the same folder as the file's own.
    /// <paramref name="added"/> is how many were added.
    /// </summary>
    public static List<ArchiveEntry> With(IReadOnlyList<ArchiveEntry> entries, IEnumerable<IArchive> open, out int added)
    {
        var result = new List<ArchiveEntry>(entries);
        var have = new HashSet<ArchiveEntry>(entries, ReferenceEqualityComparer.Instance);
        var openList = open.ToList();
        added = 0;
        foreach (var group in entries.GroupBy(e => FolderOf(e.Archive), StringComparer.OrdinalIgnoreCase))
        {
            var archives = openList.Where(a => FolderOf(a).Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                .Concat(group.Select(e => e.Archive)).Distinct().ToList();

            // every resource's buffers in this folder's archives; only worth going on when there are some
            var buffers = new Dictionary<string, List<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archives.SelectMany(a => a.Entries))
            {
                if (BufferOwner(entry.Path) is not { } owner) continue;
                if (!buffers.TryGetValue(owner, out var list)) buffers[owner] = list = [];
                list.Add(entry);
            }
            if (buffers.Count == 0) continue;

            Dictionary<string, ArchiveEntry>? byPath = null;
            foreach (var entry in group)
            {
                string owner = BufferOwner(entry.Path) ?? entry.Path;
                if (!buffers.TryGetValue(owner, out var set)) continue;
                if (!owner.Equals(entry.Path, StringComparison.OrdinalIgnoreCase))
                {
                    byPath ??= archives.SelectMany(a => a.Entries).Where(e => BufferOwner(e.Path) is null)
                        .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    if (byPath.TryGetValue(owner, out var resource) && have.Add(resource)) { result.Add(resource); added++; }
                }
                foreach (var buffer in set.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase))
                    if (have.Add(buffer)) { result.Add(buffer); added++; }
            }
        }
        return result;
    }

    /// <summary>The folder an archive is in. Loose files' folders (scripts, strings) have no buffers, which is fine.</summary>
    private static string FolderOf(IArchive archive) =>
        Path.GetDirectoryName(archive.FilePath.TrimEnd('\\', '/')) ?? archive.FilePath;
}
