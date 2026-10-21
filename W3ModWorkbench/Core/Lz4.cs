using System.IO;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core;

/// <summary>Decoder for the raw LZ4 block format (no frame header).</summary>
public static class Lz4
{
    public static byte[] Decode(ReadOnlySpan<byte> src, int size)
    {
        var dst = new byte[size];
        int s = 0, d = 0;
        try
        {
            while (s < src.Length)
            {
                int token = src[s++];

                int literals = token >> 4;
                if (literals == 15)
                {
                    byte b;
                    do { b = src[s++]; literals += b; } while (b == 255);
                }
                src.Slice(s, literals).CopyTo(dst.AsSpan(d));
                s += literals;
                d += literals;
                if (s >= src.Length)
                    break;

                int offset = src[s] | (src[s + 1] << 8);
                s += 2;
                int match = token & 15;
                if (match == 15)
                {
                    byte b;
                    do { b = src[s++]; match += b; } while (b == 255);
                }
                match += 4;

                int from = d - offset;
                if (offset == 0 || from < 0)
                    throw new InvalidDataException(L.T("Lz4.CorruptLZ4Data"));
                if (offset >= match)
                {
                    dst.AsSpan(from, match).CopyTo(dst.AsSpan(d));
                }
                else
                {
                    for (int i = 0; i < match; i++)
                        dst[d + i] = dst[from + i];
                }
                d += match;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            throw new InvalidDataException(L.T("Lz4.CorruptLZ4Data"), ex);
        }

        if (d != size)
            throw new InvalidDataException(L.T("Lz4.LZ4DataDecodedBytes", d, size));
        return dst;
    }
}
