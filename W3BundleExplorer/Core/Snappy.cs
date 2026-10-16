using System.IO;

namespace W3BundleExplorer.Core;

/// <summary>Decoder for the raw Snappy format (length varint followed by tagged elements).</summary>
public static class Snappy
{
    public static byte[] Decode(ReadOnlySpan<byte> src, int size)
    {
        try
        {
            int s = 0;
            long length = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = src[s++];
                length |= (long)(b & 0x7F) << shift;
                if (b < 0x80) break;
            }
            if (length != size)
                throw new InvalidDataException($"Snappy data is {length} bytes, expected {size}.");

            var dst = new byte[size];
            int d = 0;
            while (s < src.Length)
            {
                int tag = src[s++];
                int len, offset;
                switch (tag & 3)
                {
                    case 0:
                        len = tag >> 2;
                        if (len >= 60)
                        {
                            int bytes = len - 59;
                            len = 0;
                            for (int i = 0; i < bytes; i++)
                                len |= src[s++] << (8 * i);
                        }
                        len++;
                        src.Slice(s, len).CopyTo(dst.AsSpan(d));
                        s += len;
                        d += len;
                        continue;
                    case 1:
                        len = ((tag >> 2) & 7) + 4;
                        offset = ((tag >> 5) << 8) | src[s++];
                        break;
                    case 2:
                        len = (tag >> 2) + 1;
                        offset = src[s] | (src[s + 1] << 8);
                        s += 2;
                        break;
                    default:
                        len = (tag >> 2) + 1;
                        offset = BitConverter.ToInt32(src.Slice(s, 4));
                        s += 4;
                        break;
                }

                int from = d - offset;
                if (offset <= 0 || from < 0)
                    throw new InvalidDataException("Corrupt Snappy data.");
                for (int i = 0; i < len; i++)
                    dst[d + i] = dst[from + i];
                d += len;
            }

            if (d != size)
                throw new InvalidDataException($"Snappy data decoded to {d} bytes, expected {size}.");
            return dst;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            throw new InvalidDataException("Corrupt Snappy data.", ex);
        }
    }
}
