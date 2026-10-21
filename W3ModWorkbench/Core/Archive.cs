using System.IO;

namespace W3ModWorkbench.Core;

/// <summary>
/// The paths in the game's archives (bundles, texture caches, metadata.store) as bytes: UTF-8, as the game writes them.
/// Nearly all are plain ASCII, the same either way; the one that isn't (Blood and Wine's <c>reginald’s.w2mesh</c>) has
/// a curly apostrophe, which read byte by byte became <c>reginaldâ</c> and two control characters.
/// </summary>
public static class ArchivePaths
{
    /// <summary>A path's text: UTF-8, or byte by byte (Latin-1) for one that isn't valid UTF-8, so no name is lost.</summary>
    public static string Read(ReadOnlySpan<byte> bytes) =>
        System.Text.Unicode.Utf8.IsValid(bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : System.Text.Encoding.Latin1.GetString(bytes);

    /// <summary>A path as the game writes it, in UTF-8.</summary>
    public static byte[] Bytes(string path) => System.Text.Encoding.UTF8.GetBytes(path);
}

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

    /// <summary>The path a file is extracted to. A texture is written as a .dds, so its path says so.</summary>
    public virtual string ExtractPath => Path;

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
