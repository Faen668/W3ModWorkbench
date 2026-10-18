namespace W3ModWorkbench.Core.Textures;

/// <summary>A decoded mip level: 32-bit pixels, blue first, as WPF wants them.</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Bgra);

/// <summary>Turns a texture's stored pixels (block-compressed or plain) into 32-bit images to show.</summary>
public static class TextureDecoder
{
    public static int BytesPerPixel(TextureFormat format) => format switch
    {
        TextureFormat.Rgba8 => 4,
        TextureFormat.R8 => 1,
        TextureFormat.Rgb32F => 12,
        _ => 0,
    };

    private static int BlockBytes(TextureFormat format) => format switch
    {
        TextureFormat.Bc1 or TextureFormat.Bc4 => 8,
        TextureFormat.Bc3 or TextureFormat.Bc5 or TextureFormat.Bc7 => 16,
        _ => 0,
    };

    /// <summary>The bytes of one mip level.</summary>
    public static int LevelSize(TextureFormat format, int width, int height)
    {
        int block = BlockBytes(format);
        return block > 0 ? Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * block : width * height * BytesPerPixel(format);
    }

    private static int LevelWidth(TextureInfo info, int level) => Math.Max(1, info.Width >> level);

    private static int LevelHeight(TextureInfo info, int level) => Math.Max(1, info.Height >> level);

    /// <summary>The bytes of all the mip levels of one slice, the size of a .dds's data.</summary>
    public static int ChainSize(TextureInfo info)
    {
        int total = 0;
        for (int level = 0; level < info.Mips; level++) total += LevelSize(info.Format, LevelWidth(info, level), LevelHeight(info, level));
        return total;
    }

    /// <summary>Decodes one mip level (0 is the largest) of the first slice. Null when the format isn't understood.</summary>
    public static DecodedImage? Decode(TextureInfo info, byte[] pixels, int level = 0)
    {
        level = Math.Clamp(level, 0, info.Mips - 1);
        int offset = 0;
        for (int i = 0; i < level; i++) offset += LevelSize(info.Format, LevelWidth(info, i), LevelHeight(info, i));
        int w = LevelWidth(info, level), h = LevelHeight(info, level);
        int size = LevelSize(info.Format, w, h);
        if (info.Format == TextureFormat.Unknown || offset + size > pixels.Length) return null;
        var src = pixels.AsSpan(offset, size);
        var bgra = new byte[w * h * 4];
        switch (info.Format)
        {
            case TextureFormat.Rgba8:
                // stored red first (the game's TRF_TrueColor, R8G8B8A8); WPF wants blue first
                for (int i = 0; i < w * h * 4; i += 4) { bgra[i] = src[i + 2]; bgra[i + 1] = src[i + 1]; bgra[i + 2] = src[i]; bgra[i + 3] = src[i + 3]; }
                break;
            case TextureFormat.R8:
                for (int i = 0; i < w * h; i++) { bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = src[i]; bgra[i * 4 + 3] = 255; }
                break;
            case TextureFormat.Rgb32F:
            {
                // light and probe data is not limited to 0..1: scale it so the brightest value shows as white
                float max = 0;
                for (int i = 0; i + 4 <= src.Length; i += 4) max = Math.Max(max, BitConverter.ToSingle(src.Slice(i, 4)));
                float scale = max > 0 && !float.IsInfinity(max) ? 255f / max : 255f;
                for (int i = 0; i < w * h; i++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        float v = BitConverter.ToSingle(src.Slice(i * 12 + c * 4, 4));
                        bgra[i * 4 + 2 - c] = (byte)Math.Clamp((int)(float.IsNaN(v) ? 0 : v * scale + 0.5f), 0, 255);
                    }
                    bgra[i * 4 + 3] = 255;
                }
                break;
            }
            default: DecodeBlocks(info.Format, src, w, h, bgra); break;
        }
        return new DecodedImage(w, h, bgra);
    }

    private static void DecodeBlocks(TextureFormat format, ReadOnlySpan<byte> src, int w, int h, byte[] bgra)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4), block = BlockBytes(format);
        Span<byte> tile = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var data = src.Slice((by * bw + bx) * block, block);
                switch (format)
                {
                    case TextureFormat.Bc1: Bc.Bc1(data, tile, false); break;
                    case TextureFormat.Bc3: Bc.Bc1(data[8..], tile, true); Bc.Bc4Alpha(data, tile); break;
                    case TextureFormat.Bc4: Bc.Bc4Gray(data, tile); break;
                    case TextureFormat.Bc5: Bc.Bc5(data, tile); break;
                    case TextureFormat.Bc7: Bc7.Decode(data, tile); break;
                }
                for (int y = 0; y < 4 && by * 4 + y < h; y++)
                    for (int x = 0; x < 4 && bx * 4 + x < w; x++)
                        tile.Slice((y * 4 + x) * 4, 4).CopyTo(bgra.AsSpan(((by * 4 + y) * w + bx * 4 + x) * 4, 4));
            }
    }
}
