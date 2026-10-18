namespace W3ModWorkbench.Core.Textures;

/// <summary>The block-compressed formats of Direct3D (BC1 to BC5), decoded one 4x4 block at a time into 16 BGRA pixels.</summary>
internal static class Bc
{
    /// <summary>BC1 colour. <paramref name="fourColours"/> ignores the 3-colour mode, as BC3's colour half always does.</summary>
    public static void Bc1(ReadOnlySpan<byte> block, Span<byte> tile, bool fourColours)
    {
        int c0 = block[0] | block[1] << 8, c1 = block[2] | block[3] << 8;
        Span<byte> palette = stackalloc byte[16];
        Expand565(c0, palette[..4]);
        Expand565(c1, palette[4..8]);
        palette[3] = palette[7] = 255;
        if (c0 > c1 || fourColours)
        {
            for (int i = 0; i < 3; i++)
            {
                palette[8 + i] = (byte)((2 * palette[i] + palette[4 + i]) / 3);
                palette[12 + i] = (byte)((palette[i] + 2 * palette[4 + i]) / 3);
            }
            palette[11] = palette[15] = 255;
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                palette[8 + i] = (byte)((palette[i] + palette[4 + i]) / 2);
                palette[12 + i] = 0;
            }
            palette[11] = 255;
            palette[15] = 0; // transparent black
        }
        uint bits = (uint)(block[4] | block[5] << 8 | block[6] << 16 | block[7] << 24);
        for (int i = 0; i < 16; i++)
            palette.Slice((int)((bits >> (i * 2)) & 3) * 4, 4).CopyTo(tile.Slice(i * 4, 4));
    }

    private static void Expand565(int c, Span<byte> bgra)
    {
        int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31;
        bgra[0] = (byte)(b << 3 | b >> 2);
        bgra[1] = (byte)(g << 2 | g >> 4);
        bgra[2] = (byte)(r << 3 | r >> 2);
    }

    /// <summary>The eight (or six) interpolated values of a BC4 channel, and each texel's pick from them.</summary>
    private static void Channel(ReadOnlySpan<byte> block, Span<byte> values)
    {
        int a0 = block[0], a1 = block[1];
        Span<byte> p = stackalloc byte[8];
        p[0] = (byte)a0;
        p[1] = (byte)a1;
        if (a0 > a1)
            for (int i = 1; i <= 6; i++) p[1 + i] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else
        {
            for (int i = 1; i <= 4; i++) p[1 + i] = (byte)(((5 - i) * a0 + i * a1) / 5);
            p[6] = 0;
            p[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) values[i] = p[(int)((bits >> (3 * i)) & 7)];
    }

    /// <summary>BC3's alpha half, in the first 8 bytes of the block: it replaces the alpha of the colours already in the tile.</summary>
    public static void Bc4Alpha(ReadOnlySpan<byte> block, Span<byte> tile)
    {
        Span<byte> a = stackalloc byte[16];
        Channel(block, a);
        for (int i = 0; i < 16; i++) tile[i * 4 + 3] = a[i];
    }

    /// <summary>BC4: one channel, shown as grey.</summary>
    public static void Bc4Gray(ReadOnlySpan<byte> block, Span<byte> tile)
    {
        Span<byte> v = stackalloc byte[16];
        Channel(block, v);
        for (int i = 0; i < 16; i++)
        {
            tile[i * 4] = tile[i * 4 + 1] = tile[i * 4 + 2] = v[i];
            tile[i * 4 + 3] = 255;
        }
    }

    /// <summary>BC5: two channels, red and green. Normal maps keep their x and y here, so the blue is worked out from them.</summary>
    public static void Bc5(ReadOnlySpan<byte> block, Span<byte> tile)
    {
        Span<byte> r = stackalloc byte[16], g = stackalloc byte[16];
        Channel(block, r);
        Channel(block[8..], g);
        for (int i = 0; i < 16; i++)
        {
            double x = r[i] / 127.5 - 1, y = g[i] / 127.5 - 1;
            double z = Math.Sqrt(Math.Max(0, 1 - x * x - y * y));
            tile[i * 4] = (byte)Math.Clamp((int)((z * 0.5 + 0.5) * 255 + 0.5), 0, 255);
            tile[i * 4 + 1] = g[i];
            tile[i * 4 + 2] = r[i];
            tile[i * 4 + 3] = 255;
        }
    }
}
