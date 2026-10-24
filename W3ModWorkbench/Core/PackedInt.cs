using System.IO;

namespace W3ModWorkbench.Core;

/// <summary>
/// The engine's packed integer (a VLQ, sometimes called "dynamic" or "packed" Int32), which CR2W files, speech and
/// strings files, compiled scripts, meshes and world files all use for counts, lengths and numbers: the first byte has
/// a flag in its top bit (0x80), 0x40 when more bytes follow and the low six bits; each byte after has seven more bits
/// and 0x80 when another follows, five bytes at most. The top flag is a sign for a number and "8-bit text" for a
/// string's length; some writers set it on an empty count (written 0x80), which the game reads as 0.
/// </summary>
public static class PackedInt
{
    /// <summary>The most bytes one takes.</summary>
    public const int MaxBytes = 5;

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Reads one at <paramref name="at"/>, moving past it: its value (without the top flag) and the top flag. False
    /// when the data ends first, with <paramref name="at"/> at the end and <paramref name="value"/> what was read so far.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> data, ref int at, out int value, out bool top)
    {
        value = 0;
        top = false;
        if (at < 0 || at >= data.Length) return false;
        int first = data[at++];
        top = (first & 0x80) != 0;
        value = first & 0x3F;
        if ((first & 0x40) == 0) return true;
        for (int shift = 6; shift < 32; shift += 7)
        {
            if (at >= data.Length) return false;
            int next = data[at++];
            value |= (next & 0x7F) << shift;
            if ((next & 0x80) == 0) break;
        }
        return true;
    }

    /// <summary>Reads one at <paramref name="at"/>, moving past it: its value and the top flag. Past the end of the data it throws <see cref="IndexOutOfRangeException"/>.</summary>
    public static int Read(ReadOnlySpan<byte> data, ref int at, out bool top)
    {
        int first = data[at++];
        top = (first & 0x80) != 0;
        int value = first & 0x3F;
        if ((first & 0x40) == 0) return value;
        for (int shift = 6; shift < 32; shift += 7)
        {
            int next = data[at++];
            value |= (next & 0x7F) << shift;
            if ((next & 0x80) == 0) break;
        }
        return value;
    }

    /// <summary>Reads a count or length, its top flag ignored.</summary>
    public static int Read(ReadOnlySpan<byte> data, ref int at) => Read(data, ref at, out _);

    /// <summary>Reads a number, the top flag its sign.</summary>
    public static int ReadSigned(ReadOnlySpan<byte> data, ref int at)
    {
        int value = Read(data, ref at, out bool negative);
        return negative ? -value : value;
    }

    // ---------------------------------------------------------------- writing

    /// <summary>How many bytes <paramref name="value"/> takes.</summary>
    public static int Size(uint value)
    {
        int size = 1;
        for (value >>= 6; value > 0; value >>= 7) size++;
        return size;
    }

    /// <summary>Writes <paramref name="value"/> (with the top flag set when <paramref name="top"/>) into <paramref name="into"/>, returning how many bytes it took.</summary>
    public static int Write(Span<byte> into, uint value, bool top = false)
    {
        int n = 0;
        uint rest = value >> 6;
        into[n++] = (byte)((top ? 0x80 : 0) | (int)(value & 0x3F) | (rest > 0 ? 0x40 : 0));
        for (; rest > 0; rest >>= 7)
            into[n++] = (byte)((int)(rest & 0x7F) | (rest > 0x7F ? 0x80 : 0));
        return n;
    }

    public static void Write(Stream output, uint value, bool top = false)
    {
        Span<byte> bytes = stackalloc byte[MaxBytes];
        output.Write(bytes[..Write(bytes, value, top)]);
    }

    public static void Write(Stream output, int value, bool top = false) => Write(output, (uint)value, top);

    public static void Write(List<byte> output, int value, bool top = false) => output.AddRange(Bytes(value, top));

    public static byte[] Bytes(int value, bool top = false)
    {
        Span<byte> bytes = stackalloc byte[MaxBytes];
        return bytes[..Write(bytes, (uint)value, top)].ToArray();
    }

    /// <summary>A count as the engine writes one where an empty count is 0x80 (CR2W arrays and buffers).</summary>
    public static byte[] CountBytes(int count) => Bytes(count, top: count == 0);
}
