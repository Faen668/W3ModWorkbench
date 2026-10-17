using System.IO;

namespace W3ModWorkbench.Core.Strings;

/// <summary>A .w3strings file in a <see cref="StringsFolder"/>, read straight from disk.</summary>
public sealed class StringsEntry : ArchiveEntry
{
    public required string FullPath { get; init; }
    public override string FormatText => "Strings";
    public override byte[] Read(Stream? archiveStream) => File.ReadAllBytes(FullPath);
}

/// <summary>
/// The .w3strings files in one folder, listed like an archive so they show in the tree beside a mod's bundles
/// and can be extracted the same way. Opening one shows it in the strings editor.
/// </summary>
public sealed class StringsFolder : IArchive
{
    public string FilePath { get; }
    public string Name => "strings";
    public string Owner { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => false;

    private StringsFolder(string folder, List<ArchiveEntry> entries)
    {
        FilePath = folder;
        // named after the mod or content folder it's in, like a bundle beside it
        Owner = ArchiveEntry.OwnerOf(Path.Combine(folder, "x"));
        Entries = entries;
    }

    public static StringsFolder Open(string folder)
    {
        var entries = new List<ArchiveEntry>();
        var archive = new StringsFolder(folder, entries);
        foreach (string path in Directory.EnumerateFiles(folder, "*.w3strings").Order(StringComparer.OrdinalIgnoreCase))
        {
            int size = (int)Math.Min(int.MaxValue, new FileInfo(path).Length);
            entries.Add(new StringsEntry { Archive = archive, Path = Path.GetFileName(path), Size = size, PackedSize = size, FullPath = path });
        }
        return archive;
    }
}
