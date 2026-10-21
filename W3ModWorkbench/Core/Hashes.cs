using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core;

/// <summary>One way of hashing a name, and what in the game uses it.</summary>
public sealed record HashKind(string Name, string UsedFor, int Bits, Func<string, ulong> Compute);

/// <summary>
/// The hashes modders run into: names in CR2W files, string keys in .w3strings files, sound events in Wwise
/// banks, and the usual general ones.
/// </summary>
public static class Hashes
{
    // looked up each time, so the names and what they're used for follow the app's language
    public static IReadOnlyList<HashKind> Kinds =>
    [
        new(L.T("Hashes.Cr2wName"), L.T("Hashes.Cr2wName.UsedFor"), 32,
            s => Fnv1a32([.. Encoding.UTF8.GetBytes(s), 0])),
        new("FNV-1a 32", L.T("Hashes.Fnv1a32.UsedFor"), 32, s => Fnv1a32(Encoding.UTF8.GetBytes(s))),
        new(L.T("Hashes.StringKey"), L.T("Hashes.StringKey.UsedFor"), 32, s => Strings.W3Strings.HashKey(s)),
        new(L.T("Hashes.WwiseId"), L.T("Hashes.WwiseId.UsedFor"), 32, s => Fnv1_32(Encoding.ASCII.GetBytes(s.ToLowerInvariant()))),
        new("FNV-1a 64", L.T("Hashes.Fnv1a64.UsedFor"), 64, s => Fnv1a64(Encoding.UTF8.GetBytes(s))),
        new("CRC-32", L.T("Hashes.Crc32.UsedFor"), 32, s => Crc32(Encoding.UTF8.GetBytes(s))),
    ];

    public static uint Fnv1a32(ReadOnlySpan<byte> data)
    {
        uint hash = 2166136261;
        foreach (byte b in data) hash = unchecked((hash ^ b) * 16777619);
        return hash;
    }

    public static uint Fnv1_32(ReadOnlySpan<byte> data)
    {
        uint hash = 2166136261;
        foreach (byte b in data) hash = unchecked((hash * 16777619) ^ b);
        return hash;
    }

    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in data) hash = unchecked((hash ^ b) * 1099511628211);
        return hash;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
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

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>
    /// A number typed as decimal, as hex with 0x in front or an h after, or as plain hex when it has letters a to f.
    /// Null when it's neither.
    /// </summary>
    public static ulong? ParseNumber(string text)
    {
        text = text.Trim().Replace("_", "").Replace(" ", "");
        if (text.Length == 0) return null;
        bool hex = false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { text = text[2..]; hex = true; }
        else if (text.EndsWith('h') || text.EndsWith('H')) { text = text[..^1]; hex = true; }
        else if (text.Any(c => c is >= 'a' and <= 'f' or >= 'A' and <= 'F')) hex = true;
        if (hex)
            return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong h) ? h : null;
        if (ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong d)) return d;
        // a negative number is read as its 32-bit (or 64-bit) two's complement
        if (long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long n))
            return n >= int.MinValue ? (uint)(int)n : (ulong)n;
        return null;
    }

    // ---------------------------------------------------------------- string IDs

    /// <summary>The first ID of the block mods use by convention: 211, the mod's Nexus ID as four digits, then three for the string.</summary>
    public const uint ModIdBase = 2_110_000_000;

    /// <summary>
    /// The 1000 string IDs a mod's Nexus ID gives it by the usual convention: 211 + the ID's last four digits + 000 to 999.
    /// Null for an ID above 9999 that doesn't fit, which the convention leaves to the author.
    /// </summary>
    public static (uint First, uint Last)? ModIdBlock(int nexusId)
    {
        if (nexusId is < 0 or > 9999) return null;
        uint first = ModIdBase + (uint)nexusId * 1000;
        return (first, first + 999);
    }

    /// <summary>For an ID in the 211… range, the Nexus ID and string number it's made of; null for any other.</summary>
    public static (int NexusId, int Index)? SplitModId(ulong id)
    {
        if (id < ModIdBase || id > ModIdBase + 9_999_999) return null;
        uint rest = (uint)(id - ModIdBase);
        return ((int)(rest / 1000), (int)(rest % 1000));
    }
}
