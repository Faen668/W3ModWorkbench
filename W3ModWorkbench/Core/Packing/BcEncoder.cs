using W3ModWorkbench.Localisation;
namespace W3ModWorkbench.Core.Packing;

/// <summary>
/// Block-compresses 32-bit pictures (red, green, blue, alpha a byte each) into the formats the game's textures use: BC1
/// (colour), BC3 (colour and alpha), BC4 (one channel) and BC5 (two). Each 4×4 block's colours are fitted along their
/// main axis, then the two end colours are refined by least squares against the palette picks; one channel is fitted to
/// its range the same way. Not REDkit's encoder (so not its exact bytes), but close to it in quality.
/// </summary>
public static class BcEncoder
{
    /// <summary>The bytes one 4×4 block takes in a format: 8 for BC1 and BC4, 16 for BC3 and BC5.</summary>
    public static int BlockBytes(byte format) => format is 7 or 14 ? 8 : 16;

    /// <summary>
    /// Encodes a picture into <paramref name="format"/> (the cache's numbers: 7 BC1, 8 BC3, 14 BC4, 15 BC5). Edges that
    /// don't fill a block repeat the last row and column.
    /// </summary>
    public static byte[] Encode(byte[] rgba, int width, int height, byte format)
    {
        int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
        int size = BlockBytes(format);
        var output = new byte[bw * bh * size];
        Parallel.For(0, bh, by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, width - 1), sy = Math.Min(by * 4 + y, height - 1);
                        rgba.AsSpan((sy * width + sx) * 4, 4).CopyTo(block.Slice((y * 4 + x) * 4));
                    }
                var dst = output.AsSpan((by * bw + bx) * size, size);
                switch (format)
                {
                    case 7: Colour(block, dst); break;
                    case 8: Channel(block, 3, dst); Colour(block, dst[8..]); break;
                    case 14: Channel(block, 0, dst); break;
                    case 15: Channel(block, 0, dst); Channel(block, 1, dst[8..]); break;
                    default: throw new NotSupportedException(L.T("BcEncoder.CantEncodeTextureFormat", format));
                }
            }
        });
        return output;
    }

    // ---------------------------------------------------------------- colour (BC1, and BC3's colour half)

    private static void Colour(ReadOnlySpan<byte> block, Span<byte> dst)
    {
        Span<float> px = stackalloc float[48];
        for (int i = 0; i < 16; i++) { px[i * 3] = block[i * 4]; px[i * 3 + 1] = block[i * 4 + 1]; px[i * 3 + 2] = block[i * 4 + 2]; }

        // the mean and the main axis (power iteration on the covariance)
        float mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < 16; i++) { mr += px[i * 3]; mg += px[i * 3 + 1]; mb += px[i * 3 + 2]; }
        mr /= 16; mg /= 16; mb /= 16;
        float crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (int i = 0; i < 16; i++)
        {
            float r = px[i * 3] - mr, g = px[i * 3 + 1] - mg, b = px[i * 3 + 2] - mb;
            crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
        }
        // started from the covariance's column for the channel that varies most, which always leans along the main axis.
        // Started from grey (1, 1, 1), a block whose red and blue go opposite ways (brown leather beside blue cloth) has
        // its axis square to it: the iteration found noise, and the block came out a flat wrong colour
        float ar, ag, ab;
        if (crr >= cgg && crr >= cbb) (ar, ag, ab) = (crr, crg, crb);
        else if (cgg >= cbb) (ar, ag, ab) = (crg, cgg, cgb);
        else (ar, ag, ab) = (crb, cgb, cbb);
        for (int k = 0; k < 8; k++)
        {
            float nr = crr * ar + crg * ag + crb * ab, ng = crg * ar + cgg * ag + cgb * ab, nb = crb * ar + cgb * ag + cbb * ab;
            float len = MathF.Max(MathF.Max(MathF.Abs(nr), MathF.Abs(ng)), MathF.Abs(nb));
            if (len < 1e-6f) break;
            ar = nr / len; ag = ng / len; ab = nb / len;
        }
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float t = (px[i * 3] - mr) * ar + (px[i * 3 + 1] - mg) * ag + (px[i * 3 + 2] - mb) * ab;
            lo = MathF.Min(lo, t); hi = MathF.Max(hi, t);
        }
        float n2 = ar * ar + ag * ag + ab * ab;
        if (n2 < 1e-12f) { n2 = 1; ar = ag = ab = 0; }
        Span<float> e0 = stackalloc float[3], e1 = stackalloc float[3];
        e0[0] = mr + ar * hi / n2; e0[1] = mg + ag * hi / n2; e0[2] = mb + ab * hi / n2;
        e1[0] = mr + ar * lo / n2; e1[1] = mg + ag * lo / n2; e1[2] = mb + ab * lo / n2;

        // the end colours refined while that brings the block closer; a refinement can also throw them far off (a
        // nearly flat block, where the picks barely say anything), each channel clamped on its own, and the block came
        // out a flat wrong colour, green or orange in brown leather. So the closest pair tried is the one kept.
        Span<int> picks = stackalloc int[16];
        ushort c0 = Pack565(e0), c1 = Pack565(e1);
        float best = Pick(px, c0, c1, picks);
        for (int pass = 0; pass < 2 && Refine(px, picks, e0, e1); pass++)
        {
            ushort r0 = Pack565(e0), r1 = Pack565(e1);
            float error = Pick(px, r0, r1, picks);
            if (error >= best) break;
            (c0, c1, best) = (r0, r1, error);
        }
        if (c0 < c1) (c0, c1) = (c1, c0);
        uint bits = 0;
        if (c0 != c1)
        {
            Pick(px, c0, c1, picks);
            for (int i = 0; i < 16; i++) bits |= (uint)picks[i] << (i * 2);
        }
        dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8); dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
        dst[4] = (byte)bits; dst[5] = (byte)(bits >> 8); dst[6] = (byte)(bits >> 16); dst[7] = (byte)(bits >> 24);
    }

    private static ushort Pack565(ReadOnlySpan<float> c)
    {
        int r = Math.Clamp((int)MathF.Round(c[0] * 31 / 255), 0, 31);
        int g = Math.Clamp((int)MathF.Round(c[1] * 63 / 255), 0, 63);
        int b = Math.Clamp((int)MathF.Round(c[2] * 31 / 255), 0, 31);
        return (ushort)(r << 11 | g << 5 | b);
    }

    private static void Unpack565(ushort c, Span<float> rgb)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        rgb[0] = (r << 3) | (r >> 2); rgb[1] = (g << 2) | (g >> 4); rgb[2] = (b << 3) | (b >> 2);
    }

    /// <summary>
    /// Each pixel's nearest of the four colours c0, c1, 2/3 c0 + 1/3 c1, 1/3 c0 + 2/3 c1 (BC1's index order), and how far
    /// the block is from them in all.
    /// </summary>
    private static float Pick(ReadOnlySpan<float> px, ushort c0, ushort c1, Span<int> picks)
    {
        float total = 0;
        Span<float> pal = stackalloc float[12];
        Unpack565(c0, pal);
        Unpack565(c1, pal[3..]);
        for (int k = 0; k < 3; k++)
        {
            pal[6 + k] = (2 * pal[k] + pal[3 + k]) / 3;
            pal[9 + k] = (pal[k] + 2 * pal[3 + k]) / 3;
        }
        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int p = 0; p < 4; p++)
            {
                float dr = px[i * 3] - pal[p * 3], dg = px[i * 3 + 1] - pal[p * 3 + 1], db = px[i * 3 + 2] - pal[p * 3 + 2];
                float d = dr * dr * 0.3f + dg * dg * 0.59f + db * db * 0.11f;
                if (d < bestD) { bestD = d; best = p; }
            }
            picks[i] = best;
            total += bestD;
        }
        return total;
    }

    /// <summary>The end colours that best give the picks' weights, by least squares. False when they can't be solved.</summary>
    private static bool Refine(ReadOnlySpan<float> px, ReadOnlySpan<int> picks, Span<float> e0, Span<float> e1)
    {
        ReadOnlySpan<float> w0 = [1f, 0f, 2f / 3, 1f / 3];
        float a2 = 0, b2 = 0, ab = 0;
        Span<float> ax = stackalloc float[3], bx = stackalloc float[3];
        for (int i = 0; i < 16; i++)
        {
            float a = w0[picks[i]], b = 1 - a;
            a2 += a * a; b2 += b * b; ab += a * b;
            for (int k = 0; k < 3; k++) { ax[k] += a * px[i * 3 + k]; bx[k] += b * px[i * 3 + k]; }
        }
        float det = a2 * b2 - ab * ab;
        if (MathF.Abs(det) < 1e-6f) return false;
        for (int k = 0; k < 3; k++)
        {
            e0[k] = Math.Clamp((ax[k] * b2 - bx[k] * ab) / det, 0, 255);
            e1[k] = Math.Clamp((bx[k] * a2 - ax[k] * ab) / det, 0, 255);
        }
        return true;
    }

    // ---------------------------------------------------------------- one channel (BC4, BC5's halves, BC3's alpha)

    private static void Channel(ReadOnlySpan<byte> block, int channel, Span<byte> dst)
    {
        Span<int> v = stackalloc int[16];
        int lo = 255, hi = 0;
        for (int i = 0; i < 16; i++) { v[i] = block[i * 4 + channel]; lo = Math.Min(lo, v[i]); hi = Math.Max(hi, v[i]); }
        // eight steps between the two ends (a0 > a1); a flat block keeps its value
        int a0 = hi, a1 = lo;
        Span<int> picks = stackalloc int[16];
        for (int pass = 0; pass < 2; pass++)
        {
            PickChannel(v, a0, a1, picks);
            if (a0 == a1 || pass == 1) break;
            // least squares on the ends for these picks
            float s00 = 0, s11 = 0, s01 = 0, sv0 = 0, sv1 = 0;
            for (int i = 0; i < 16; i++)
            {
                float w = Weight(picks[i]), u = 1 - w;
                s00 += w * w; s11 += u * u; s01 += w * u; sv0 += w * v[i]; sv1 += u * v[i];
            }
            float det = s00 * s11 - s01 * s01;
            if (MathF.Abs(det) < 1e-6f) break;
            int n0 = Math.Clamp((int)MathF.Round((sv0 * s11 - sv1 * s01) / det), 0, 255);
            int n1 = Math.Clamp((int)MathF.Round((sv1 * s00 - sv0 * s01) / det), 0, 255);
            if (n0 <= n1) break;
            if (Error(v, n0, n1) < Error(v, a0, a1)) { a0 = n0; a1 = n1; } else break;
        }
        PickChannel(v, a0, a1, picks);
        dst[0] = (byte)a0;
        dst[1] = (byte)a1;
        ulong bits = 0;
        for (int i = 0; i < 16; i++) bits |= (ulong)picks[i] << (i * 3);
        for (int k = 0; k < 6; k++) dst[2 + k] = (byte)(bits >> (k * 8));
    }

    /// <summary>How much of the first end a pick takes: index 0 is a0, 1 is a1, 2 to 7 the steps between.</summary>
    private static float Weight(int pick) => pick switch { 0 => 1f, 1 => 0f, _ => (8 - pick) / 7f };

    private static int Value(int pick, int a0, int a1) => pick switch
    {
        0 => a0,
        1 => a1,
        _ => ((8 - pick) * a0 + (pick - 1) * a1) / 7,
    };

    private static void PickChannel(ReadOnlySpan<int> v, int a0, int a1, Span<int> picks)
    {
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestD = int.MaxValue;
            for (int p = 0; p < 8; p++)
            {
                int d = Math.Abs(v[i] - Value(p, a0, a1));
                if (d < bestD) { bestD = d; best = p; }
            }
            picks[i] = a0 == a1 ? 0 : best;
        }
    }

    private static int Error(ReadOnlySpan<int> v, int a0, int a1)
    {
        Span<int> picks = stackalloc int[16];
        PickChannel(v, a0, a1, picks);
        int e = 0;
        for (int i = 0; i < 16; i++) { int d = v[i] - Value(picks[i], a0, a1); e += d * d; }
        return e;
    }
}
