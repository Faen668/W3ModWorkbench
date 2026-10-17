using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Scripts;

public sealed class ScriptEntry : ArchiveEntry
{
    public required byte[] Data { get; init; }
    public override string FormatText => "Decompiled";
    public override byte[] Read(Stream? archiveStream) => Data;
}

/// <summary>A compiled script blob opened as an archive of decompiled .ws files.</summary>
public sealed class ScriptArchive : IArchive
{
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public string FilePath { get; }
    public string Name => Path.GetFileName(FilePath);
    public string Owner { get; }
    public ScriptBlob Blob { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => false;

    private ScriptArchive(string filePath, ScriptBlob blob, List<ArchiveEntry> entries)
    {
        FilePath = filePath;
        Owner = ArchiveEntry.OwnerOf(filePath);
        Blob = blob;
        Entries = entries;
    }

    public static ScriptArchive Open(string filePath)
    {
        var blob = ScriptBlob.Open(filePath);
        var entries = new List<ArchiveEntry>();
        var archive = new ScriptArchive(filePath, blob, entries);
        foreach (var doc in ScriptWriter.Write(blob, ScriptWriter.Label(filePath)))
        {
            // UTF-8 with a byte order mark, like the game's own script files
            byte[] data = [.. Utf8Bom.GetPreamble(), .. Encoding.UTF8.GetBytes(doc.Text.Replace("\n", "\r\n"))];
            entries.Add(new ScriptEntry { Archive = archive, Path = doc.Path, Size = data.Length, PackedSize = doc.BytecodeSize, Data = data });
        }
        return archive;
    }
}
