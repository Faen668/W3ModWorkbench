using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Packing;

/// <summary>
/// Writes a sound cache (<c>soundspc.cache</c>): Wwise sound banks (<c>.bnk</c>) and streamed sounds (<c>.wem</c>, named
/// by their ID), under their file names, which is how the game finds them. It's laid out as the game's own: a 64-byte
/// header ("CS3W", version 2, where the list and the names are, how many files, the largest file rounded up to 4 KB, and a
/// 64-bit FNV-1a of the names then the list), the files one after another, the names, then the list (each file's name,
/// where it is and its size, eight bytes each). REDkit has no tool that makes one; a mod's sounds go in it.
/// </summary>
public static class SoundCacheWriter
{
    public const string FileName = "soundspc.cache";
    private const uint Magic = 0x57335343; // "CS3W"
    private const int HeaderSize = 64;

    /// <summary>The files a sound cache holds.</summary>
    public static bool IsSound(string path) =>
        path.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the files, in this order, under their names, to a new cache at <paramref name="path"/>.</summary>
    public static void Write(string path, IReadOnlyList<(string Name, string FilePath)> files, CancellationToken cancel = default)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        output.Write(new byte[HeaderSize]);
        var list = new byte[files.Count * 24];
        var names = new MemoryStream();
        long largest = 0;
        for (int i = 0; i < files.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            long at = output.Position;
            using (var input = File.OpenRead(files[i].FilePath)) input.CopyTo(output);
            long size = output.Position - at;
            largest = Math.Max(largest, size);
            BinaryPrimitives.WriteInt64LittleEndian(list.AsSpan(i * 24), names.Length);
            BinaryPrimitives.WriteInt64LittleEndian(list.AsSpan(i * 24 + 8), at);
            BinaryPrimitives.WriteInt64LittleEndian(list.AsSpan(i * 24 + 16), size);
            names.Write(Encoding.ASCII.GetBytes(files[i].Name));
            names.WriteByte(0);
        }
        long namesAt = output.Position;
        var nameBytes = names.ToArray();
        output.Write(nameBytes);
        long listAt = output.Position;
        output.Write(list);

        ulong check = 0xCBF29CE484222325;
        foreach (byte b in nameBytes) { check ^= b; check *= 0x100000001B3; }
        foreach (byte b in list) { check ^= b; check *= 0x100000001B3; }

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), listAt);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), files.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), namesAt);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), nameBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(44), 1);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), Math.Max(4096, (largest + 4095) / 4096 * 4096));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), check);
        output.Position = 0;
        output.Write(header);
    }
}
