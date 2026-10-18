using System.Text;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>The checksums a CR2W file carries: CRC-32 over its tables and objects, and FNV-1a over each name.</summary>
public static class Checksums
{
    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>The usual CRC-32 (the one zip uses), of <paramref name="data"/> after the bytes <paramref name="crc"/> came from.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>The 32-bit FNV-1a hash of a name, with the zero that ends it, as the names table keeps beside each one.</summary>
    public static uint Fnv1a32(string text)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.Latin1.GetBytes(text + "\0"))
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }
}
