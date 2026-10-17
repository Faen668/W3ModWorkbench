using System.IO;
using System.IO.Compression;
using System.Text;

namespace W3ModWorkbench.Core;

public enum Compression : uint
{
    None = 0,
    Zlib = 1,
    Snappy = 2,
    Doboz = 3,
    Lz4 = 4,
    Lz4Hc = 5,
}

public sealed class BundleEntry : ArchiveEntry
{
    public required long Offset { get; init; }
    public required uint Crc { get; init; }
    public required Compression Compression { get; init; }

    public override long SortKey => Offset;

    public override string FormatText => Compression switch
    {
        Compression.None => "Stored",
        Compression.Lz4Hc => "LZ4 HC",
        Compression.Lz4 => "LZ4",
        var c => c.ToString(),
    };

    public override byte[] Read(Stream? archiveStream) =>
        Bundle.ReadEntry(archiveStream ?? throw new ArgumentNullException(nameof(archiveStream)), this);
}

/// <summary>
/// A Witcher 3 "POTATO70" bundle. Two table layouts exist: version 5 (the game since the
/// next-gen update, and REDkit) uses 0x130-byte entries, version 3 (older tools) 0x140.
/// </summary>
public sealed class Bundle : IArchive
{
    private const string Magic = "POTATO70";
    private const int HeaderSize = 0x20;
    private const int NameSize = 0x100;

    public string FilePath { get; }
    public string Name => System.IO.Path.GetFileName(FilePath);

    /// <summary>
    /// The mod, DLC or content folder the bundle belongs to, e.g. "modmapredraw" for
    /// mods\modmapredraw\content\blob0.bundle and "content0" for content\content0\bundles\blob.bundle.
    /// </summary>
    public string Owner { get; }

    public int Version { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => true;

    private Bundle(string filePath, int version, List<ArchiveEntry> entries)
    {
        FilePath = filePath;
        Version = version;
        Entries = entries;
        Owner = ArchiveEntry.OwnerOf(filePath);
    }

    public static Bundle Open(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[HeaderSize];
        fs.ReadExactly(header);

        if (Encoding.ASCII.GetString(header, 0, 8) != Magic)
            throw new InvalidDataException("Not a Witcher 3 bundle (no POTATO70 header).");

        int tocSize = BitConverter.ToInt32(header, 0x10);
        int version = BitConverter.ToUInt16(header, 0x14);
        int entrySize = version switch
        {
            5 => 0x130,
            3 => 0x140,
            _ => throw new InvalidDataException($"Unknown bundle version {version}."),
        };
        if (tocSize < 0 || tocSize % entrySize != 0 || HeaderSize + tocSize > fs.Length)
            throw new InvalidDataException("The bundle's file table is damaged.");

        var toc = new byte[tocSize];
        fs.ReadExactly(toc);

        var entries = new List<ArchiveEntry>(tocSize / entrySize);
        var bundle = new Bundle(filePath, version, entries);
        for (int p = 0; p < tocSize; p += entrySize)
        {
            var e = toc.AsSpan(p, entrySize);
            int nameLength = e[..NameSize].IndexOf((byte)0);
            string name = Encoding.Latin1.GetString(e[..(nameLength < 0 ? NameSize : nameLength)]);

            ulong offset;
            uint size, packed, crc, comp;
            if (version == 5)
            {
                offset = BitConverter.ToUInt64(e[0x110..]); // 64-bit: the game has bundles over 4 GB
                size = U32(e, 0x118);
                packed = U32(e, 0x11C);
                crc = U32(e, 0x120);
                comp = U32(e, 0x124);
            }
            else
            {
                size = U32(e, 0x114);
                packed = U32(e, 0x118);
                offset = U32(e, 0x11C);
                crc = U32(e, 0x138);
                comp = U32(e, 0x13C);
            }

            entries.Add(new BundleEntry
            {
                Archive = bundle,
                Path = name,
                Offset = (long)offset,
                Size = (int)size,
                PackedSize = (int)packed,
                Crc = crc,
                Compression = (Compression)comp,
            });
        }
        return bundle;
    }

    private static uint U32(ReadOnlySpan<byte> s, int at) => BitConverter.ToUInt32(s[at..]);

    /// <summary>Reads and decompresses one entry. The stream must be this bundle's file.</summary>
    public static byte[] ReadEntry(Stream bundleStream, BundleEntry entry)
    {
        var packed = new byte[entry.PackedSize];
        bundleStream.Position = entry.Offset;
        bundleStream.ReadExactly(packed);

        switch (entry.Compression)
        {
            case Compression.None:
                return packed;

            case Compression.Zlib:
            {
                var output = new byte[entry.Size];
                using var z = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
                z.ReadExactly(output);
                return output;
            }

            case Compression.Lz4:
            case Compression.Lz4Hc:
                try
                {
                    return Lz4.Decode(packed, entry.Size);
                }
                catch (InvalidDataException) when (entry.PackedSize == entry.Size)
                {
                    return packed; // stored uncompressed despite the flag
                }

            case Compression.Snappy:
                return Snappy.Decode(packed, entry.Size);

            default:
                throw new NotSupportedException($"{entry.Compression} compression is not supported.");
        }
    }
}
