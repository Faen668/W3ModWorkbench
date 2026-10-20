using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Packing;

/// <summary>
/// One texture as a texture cache holds it: under its game path and key, its size, format and mip levels. Each level's
/// bytes are every slice's (a cube map's six faces) one after another. <see cref="ResidentMip"/> is where the levels the
/// cooked resource keeps itself begin: the cache has each level before it in a block of its own, and those from it on in
/// one more block.
/// </summary>
public sealed record CacheTexture(string Path, uint Key, int Width, int Height, int Slices, byte Format, byte Kind, bool IsCube,
    IReadOnlyList<byte[]> Mips, int ResidentMip, long Timestamp = 0);

/// <summary>
/// Writes a <c>texture.cache</c> as REDkit's <c>wcc_lite buildcache textures</c> does, byte for byte. Each texture
/// starts on a 4 KB page: a run of blocks, each nine bytes (its size packed, unpacked, and a count down to 0 for the last)
/// then zlib (level 9). After the textures, padded to a page, come the mip table (where each texture's blocks after the
/// first start, from its start), the names, the entries (52 bytes each) and a 32-byte footer: a 64-bit FNV-1a of the
/// three tables, the pages used, the entry count, the names' size, the mip table's length, "HCXT" and version 7.
/// </summary>
public static class TextureCacheWriter
{
    public const string FileName = "texture.cache";
    private const int PageSize = 4096;
    private const uint Magic = 0x54584348;
    private const int Version = 7;
    private const int EntrySize = 52;

    /// <summary>The key the engine finds a texture by in a cache: its path's characters, each taken as one byte, as h * 31 + c.</summary>
    public static uint Key(string path)
    {
        uint hash = 0;
        foreach (char c in path.ToLowerInvariant().Replace('/', '\\')) hash = hash * 31 + (byte)c;
        return hash;
    }

    /// <summary>Writes the textures, in this order, to a new cache at <paramref name="path"/>.</summary>
    public static void Write(string path, IReadOnlyList<CacheTexture> textures, CancellationToken cancel = default)
    {
        // the textures' blocks are packed side by side, then written in order
        var packed = new List<(byte[] Data, int Size, int Mip)>[textures.Count];
        Parallel.For(0, textures.Count, new ParallelOptions { CancellationToken = cancel }, i => packed[i] = Blocks(textures[i]));

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var mipTable = new List<uint>();
        var names = new MemoryStream();
        var entries = new byte[textures.Count * EntrySize];
        long at = 0;
        for (int i = 0; i < textures.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var texture = textures[i];
            long start = at;
            int mipIndex = mipTable.Count;
            var head = new byte[9];
            for (int b = 0; b < packed[i].Count; b++)
            {
                var (data, size, mip) = packed[i][b];
                if (b > 0) mipTable.Add((uint)(at - start));
                BinaryPrimitives.WriteInt32LittleEndian(head, data.Length);
                BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), size);
                head[8] = (byte)mip;
                output.Write(head);
                output.Write(data);
                at += head.Length + data.Length;
            }
            long packedSize = at - start;
            long end = (at + PageSize - 1) / PageSize * PageSize;
            output.Write(new byte[end - at]);
            at = end;

            int nameAt = (int)names.Length;
            names.Write(Encoding.Latin1.GetBytes(texture.Path));
            names.WriteByte(0);

            var e = entries.AsSpan(i * EntrySize, EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(e, texture.Key);
            BinaryPrimitives.WriteInt32LittleEndian(e[4..], nameAt);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)(start / PageSize));
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)packedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(e[16..], (uint)texture.Mips.Sum(m => (long)m.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(e[20..], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(e[24..], (ushort)texture.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(e[26..], (ushort)texture.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(e[28..], (ushort)texture.Mips.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(e[30..], (ushort)texture.Slices);
            BinaryPrimitives.WriteInt32LittleEndian(e[32..], mipIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(e[36..], (ushort)(packed[i].Count - 1));
            BinaryPrimitives.WriteUInt16LittleEndian(e[38..], 1);
            BinaryPrimitives.WriteInt64LittleEndian(e[40..], texture.Timestamp);
            e[48] = texture.Format;
            e[49] = texture.Kind;
            e[50] = (byte)(texture.IsCube ? 1 : 0);
        }

        var mips = new byte[mipTable.Count * 4];
        for (int i = 0; i < mipTable.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(mips.AsSpan(i * 4), mipTable[i]);
        var nameBytes = names.ToArray();
        ulong check = Fnv1a64(Fnv1a64(Fnv1a64(0xCBF29CE484222325, mips), nameBytes), entries);
        output.Write(mips);
        output.Write(nameBytes);
        output.Write(entries);
        var footer = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(footer, check);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(8), (uint)(at / PageSize));
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(12), textures.Count);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(16), nameBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(20), mipTable.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(24), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(28), Version);
        output.Write(footer);
    }

    /// <summary>A texture's blocks: each level before the resident ones on its own, then the resident ones together.</summary>
    private static List<(byte[] Data, int Size, int Mip)> Blocks(CacheTexture texture)
    {
        int resident = Math.Clamp(texture.ResidentMip, 0, texture.Mips.Count);
        var raw = new List<byte[]>();
        for (int m = 0; m < resident; m++) raw.Add(texture.Mips[m]);
        if (resident < texture.Mips.Count)
        {
            var tail = new byte[texture.Mips.Skip(resident).Sum(m => m.Length)];
            int at = 0;
            foreach (var mip in texture.Mips.Skip(resident)) { mip.CopyTo(tail, at); at += mip.Length; }
            raw.Add(tail);
        }
        var blocks = new List<(byte[], int, int)>(raw.Count);
        for (int b = 0; b < raw.Count; b++) blocks.Add((Zlib.Compress(raw[b]), raw[b].Length, raw.Count - 1 - b));
        return blocks;
    }

    private static ulong Fnv1a64(ulong hash, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 0x100000001B3;
        }
        return hash;
    }
}
