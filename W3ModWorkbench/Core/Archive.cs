using System.IO;

namespace W3ModWorkbench.Core;

/// <summary>A file the explorer can open and list: a .bundle or a compiled script blob.</summary>
public interface IArchive
{
    string FilePath { get; }
    string Name { get; }

    /// <summary>The mod, DLC or content folder the file belongs to.</summary>
    string Owner { get; }

    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Whether entries are read from the file on disk (bundles) or are already in memory (scripts).</summary>
    bool ReadsFromDisk { get; }
}

public abstract class ArchiveEntry
{
    public required IArchive Archive { get; init; }
    public required string Path { get; init; }
    public required int Size { get; init; }
    public required int PackedSize { get; init; }

    /// <summary>Order to read entries in, so a bundle is read front to back.</summary>
    public virtual long SortKey => 0;

    public abstract string FormatText { get; }

    /// <param name="archiveStream">The archive's file when <see cref="IArchive.ReadsFromDisk"/>, otherwise null.</param>
    public abstract byte[] Read(Stream? archiveStream);

    public static string OwnerOf(string filePath)
    {
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(filePath) ?? "");
        bool nested = dir.Name.Equals("content", StringComparison.OrdinalIgnoreCase)
                   || dir.Name.Equals("bundles", StringComparison.OrdinalIgnoreCase);
        return (nested ? dir.Parent?.Name : null) ?? dir.Name;
    }
}
