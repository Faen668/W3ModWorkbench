using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace W3ModWorkbench.Core.Textures;

/// <summary>How a texture's pixels are stored in the cache.</summary>
public enum TextureFormat
{
    Unknown = 0,
    Bc1,
    Bc3,
    Bc4,
    Bc5,
    Bc7,
    Rgba8,
    R8,
    Rgb32F,
}

/// <summary>What a texture.cache entry says about its texture.</summary>
public sealed record TextureInfo(int Width, int Height, int Mips, int Slices, int RawFormat, TextureFormat Format, int Alignment, long DataOffset, int PackedSize, int UnpackedSize)
{
    /// <summary>The format, size and mip count in a few words: BC1 2048x1024 ×12.</summary>
    public string Description => $"{FormatName} {Width}x{Height}" + (Mips > 1 ? $" ×{Mips}" : "");

    /// <summary>The format as the game writes it, for what's not understood yet.</summary>
    public string FormatName => Format == TextureFormat.Unknown ? $"format {RawFormat}" : Format switch
    {
        TextureFormat.Rgba8 => "RGBA8",
        TextureFormat.Rgb32F => "RGB32F",
        _ => Format.ToString().ToUpperInvariant(),
    };
}

/// <summary>One texture in a texture.cache: listed under its game path, read as a DDS file.</summary>
public sealed class TextureEntry : ArchiveEntry
{
    public required TextureInfo Info { get; init; }

    public override long SortKey => Info.DataOffset;

    public override string ExtractPath => Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? Path : Path + ".dds";

    public override string FormatText => Info.Description;

    /// <summary>The texture as a .dds file: all of its mip levels, of the first slice.</summary>
    public override byte[] Read(Stream? archiveStream)
    {
        if (archiveStream is null) throw new InvalidOperationException("A texture is read from its cache file.");
        return Dds.Wrap(Info, TextureCacheArchive.ReadPixels(archiveStream, Info));
    }
}

/// <summary>
/// A texture.cache: the game's textures (and a mod's) outside the bundles. Its file ends in a footer that says where the
/// names and entries are, so a cache of 30 GB opens by reading a few megabytes. Each texture is a run of zlib blocks,
/// largest mip first, that unpack to the texture's mip levels one after another, as a .dds lays them out.
/// </summary>
public sealed class TextureCacheArchive : IArchive
{
    private const uint Magic = 0x54584348; // "HCXT" in the file
    private const int FooterSize = 32;
    private const int EntrySize = 52;
    private const int PageSize = 4096;

    public string FilePath { get; }
    public string Name => Path.GetFileName(FilePath);
    public string Owner { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => true;

    /// <summary>The cache's format version (6 and 7 are known).</summary>
    public int Version { get; }

    private TextureCacheArchive(string path, int version, List<ArchiveEntry> entries)
    {
        FilePath = path;
        Version = version;
        Entries = entries;
        Owner = ArchiveEntry.OwnerOf(path);
    }

    /// <summary>Whether a file is a texture cache: a .cache file whose footer carries the cache's mark.</summary>
    public static bool LooksLikeCache(string path)
    {
        if (!path.EndsWith(".cache", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < FooterSize) return false;
            fs.Seek(-8, SeekOrigin.End);
            Span<byte> tail = stackalloc byte[8];
            fs.ReadExactly(tail);
            return BinaryPrimitives.ReadUInt32LittleEndian(tail) == Magic;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static TextureCacheArchive Open(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long length = fs.Length;
        if (length < FooterSize) throw new InvalidDataException("The file is too small to be a texture cache.");
        var footer = new byte[FooterSize];
        fs.Seek(-FooterSize, SeekOrigin.End);
        fs.ReadExactly(footer);
        // 8 bytes of something (a hash), then the pages used, the entry count, the size of the names, the mip table's length, the mark and the version
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12));
        int namesSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16));
        int mipTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(20));
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(24));
        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(28));
        if (magic != Magic) throw new InvalidDataException("This isn't a texture cache: its footer has no texture cache mark.");
        if (version is < 4 or > 9) throw new InvalidDataException($"Texture cache version {version} isn't supported (6 and 7 are).");
        long entriesAt = length - FooterSize - (long)count * EntrySize;
        long namesAt = entriesAt - namesSize;
        if (count < 0 || namesSize < 0 || namesAt - (long)mipTable * 4 < 0) throw new InvalidDataException("The texture cache's footer is damaged.");

        var names = new byte[namesSize];
        fs.Seek(namesAt, SeekOrigin.Begin);
        fs.ReadExactly(names);
        var table = new byte[count * EntrySize];
        fs.ReadExactly(table);

        var archive = new TextureCacheArchive(path, version, new List<ArchiveEntry>(count));
        var list = (List<ArchiveEntry>)archive.Entries;
        for (int i = 0; i < count; i++)
        {
            var e = table.AsSpan(i * EntrySize, EntrySize);
            int nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            long page = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            int packed = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            int unpacked = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
            int alignment = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
            int width = BinaryPrimitives.ReadUInt16LittleEndian(e[24..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]);
            int mips = BinaryPrimitives.ReadUInt16LittleEndian(e[28..]);
            int slices = BinaryPrimitives.ReadUInt16LittleEndian(e[30..]);
            int raw = e[48];
            if (nameOffset < 0 || nameOffset >= names.Length) continue;
            int end = Array.IndexOf(names, (byte)0, nameOffset);
            string name = System.Text.Encoding.Latin1.GetString(names, nameOffset, (end < 0 ? names.Length : end) - nameOffset);
            var info = new TextureInfo(width, height, Math.Max(1, mips), Math.Max(1, slices), raw, FormatOf(raw), alignment, page * PageSize, packed, unpacked);
            list.Add(new TextureEntry
            {
                Archive = archive,
                Path = name.Replace('/', '\\'),
                Size = unpacked,
                PackedSize = packed,
                Info = info,
            });
        }
        return archive;
    }

    /// <summary>The pixel format for the number the cache stores.</summary>
    public static TextureFormat FormatOf(int raw) => raw switch
    {
        7 => TextureFormat.Bc1,
        8 => TextureFormat.Bc3,
        14 => TextureFormat.Bc4,
        15 => TextureFormat.Bc5,
        10 => TextureFormat.Bc7,
        13 => TextureFormat.Bc3,
        253 => TextureFormat.Rgba8,
        16 => TextureFormat.Rgb32F,
        _ => TextureFormat.Unknown,
    };

    /// <summary>
    /// The texture's pixels as the cache holds them, every mip level one after another, largest first. A texture is a run of
    /// blocks, each with a header (packed size, unpacked size, which mip it starts at) and a zlib stream.
    /// </summary>
    public static byte[] ReadPixels(Stream cache, TextureInfo info)
    {
        var result = new byte[info.UnpackedSize];
        int filled = 0;
        long at = info.DataOffset;
        long end = at + info.PackedSize;
        var header = new byte[9];
        while (at + 9 <= end && filled < result.Length)
        {
            cache.Seek(at, SeekOrigin.Begin);
            cache.ReadExactly(header);
            int packed = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            int unpacked = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (packed < 0 || unpacked < 0 || at + 9 + packed > end + PageSize || filled + unpacked > result.Length)
                throw new InvalidDataException("A block of the texture is damaged.");
            var block = new byte[packed];
            cache.ReadExactly(block);
            if (packed == unpacked)
            {
                // stored as it is, when packing didn't help
                if (!LooksLikeZlib(block)) Buffer.BlockCopy(block, 0, result, filled, unpacked);
                else Inflate(block, result, filled, unpacked);
            }
            else Inflate(block, result, filled, unpacked);
            filled += unpacked;
            at += 9 + packed;
        }
        if (filled < result.Length) Array.Resize(ref result, filled);
        return result;
    }

    private static bool LooksLikeZlib(byte[] block) => block.Length > 2 && block[0] == 0x78 && (block[1] is 0x01 or 0x5E or 0x9C or 0xDA);

    private static void Inflate(byte[] block, byte[] target, int offset, int length)
    {
        using var zlib = new ZLibStream(new MemoryStream(block), CompressionMode.Decompress);
        int read = 0;
        while (read < length)
        {
            int n = zlib.Read(target, offset + read, length - read);
            if (n == 0) throw new InvalidDataException("A block of the texture ended early.");
            read += n;
        }
    }
}
