using System.Buffers.Binary;
using System.IO;

namespace W3ModWorkbench.Core.Textures;

/// <summary>Writes a texture's pixels as a .dds file, so any image tool can open what was extracted.</summary>
public static class Dds
{
    private const uint Magic = 0x20534444; // "DDS "
    private const uint Caps = 0x1, Height = 0x2, Width = 0x4, Pitch = 0x8, PixelFormat = 0x1000, MipMapCount = 0x20000, LinearSize = 0x80000;
    private const uint FourCc = 0x4, Rgb = 0x40, AlphaPixels = 0x1, Luminance = 0x20000;

    /// <summary>
    /// The texture as a .dds file with every slice: a cube map's six faces, or an array's textures (which need the DX10
    /// header). <paramref name="pixels"/> are in the cache's order, level after level.
    /// </summary>
    public static byte[] Wrap(TextureInfo info, byte[] pixels)
    {
        if (info.Format == TextureFormat.Unknown)
            throw new NotSupportedException($"Texture format {info.RawFormat} isn't understood yet, so it can't be written as a .dds.");
        int chain = TextureDecoder.ChainSize(info);
        if (pixels.Length < chain * info.Slices) throw new InvalidDataException("The texture's data is shorter than its mip levels need.");
        bool array = info.Slices > 1 && !info.IsCube;
        bool dx10 = info.Format is TextureFormat.Bc7 or TextureFormat.R16F || array;
        int body = 128 + (dx10 ? 20 : 0);
        var file = new byte[body + chain * info.Slices];
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);
        Put(0, Magic);
        Put(4, 124);
        bool compressed = info.Format is TextureFormat.Bc1 or TextureFormat.Bc3 or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Bc7;
        Put(8, Caps | Height | Width | PixelFormat | (info.Mips > 1 ? MipMapCount : 0) | (compressed ? LinearSize : Pitch));
        Put(12, (uint)info.Height);
        Put(16, (uint)info.Width);
        Put(20, compressed ? (uint)TextureDecoder.LevelSize(info.Format, info.Width, info.Height) : (uint)(info.Width * TextureDecoder.BytesPerPixel(info.Format)));
        Put(28, (uint)info.Mips);
        Put(76, 32); // pixel format size
        if (dx10) { Put(80, FourCc); Put(84, 0x30315844); } // DX10
        else switch (info.Format)
        {
            case TextureFormat.Bc1: Put(80, FourCc); Put(84, 0x31545844); break; // DXT1
            case TextureFormat.Bc3: Put(80, FourCc); Put(84, 0x35545844); break; // DXT5
            case TextureFormat.Bc4: Put(80, FourCc); Put(84, 0x31495441); break; // ATI1
            case TextureFormat.Bc5: Put(80, FourCc); Put(84, 0x32495441); break; // ATI2
            case TextureFormat.Rgba8:
                Put(80, Rgb | AlphaPixels); Put(88, 32); Put(92, 0x000000FF); Put(96, 0x0000FF00); Put(100, 0x00FF0000); Put(104, 0xFF000000);
                break;
            case TextureFormat.R8:
                Put(80, Luminance); Put(88, 8); Put(92, 0xFF);
                break;
        }
        Put(108, 0x1000 | (info.Mips > 1 || info.Slices > 1 ? 0x8u : 0) | (info.Mips > 1 ? 0x400000u : 0)); // texture, complex, mipmap
        if (info.IsCube) Put(112, 0x200 | 0xFC00); // a cube map with all six faces
        if (dx10)
        {
            Put(128, DxgiFormat(info.Format));
            Put(132, 3); // 2D
            Put(136, info.IsCube ? 0x4u : 0); // a cube map
            Put(140, (uint)(info.IsCube ? 1 : info.Slices));
        }
        Buffer.BlockCopy(TextureDecoder.SliceMajor(info, pixels), 0, file, body, chain * info.Slices);
        return file;
    }

    private static uint DxgiFormat(TextureFormat format) => format switch
    {
        TextureFormat.Bc1 => 71,
        TextureFormat.Bc3 => 77,
        TextureFormat.Bc4 => 80,
        TextureFormat.Bc5 => 83,
        TextureFormat.Bc7 => 98,
        TextureFormat.Rgba8 => 28,
        TextureFormat.R8 => 61,
        TextureFormat.R16F => 54,
        _ => 0,
    };

    /// <summary>
    /// Reads a .dds file's header and gives its pixels: every mip level of every slice (a cube map's faces, an array's
    /// textures), in the cache's order, level after level. False for a format that isn't understood: volumes, BC2 and the like.
    /// </summary>
    public static bool TryRead(byte[] file, out TextureInfo info, out byte[] pixels)
    {
        info = null!;
        pixels = [];
        if (file.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic) return false;
        uint U(int at) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
        int height = (int)U(12), width = (int)U(16), mips = Math.Max(1, (int)U(28));
        uint flags = U(80), fourCc = U(84);
        int body = 128;
        bool cube = (U(112) & 0x200) != 0;
        int slices = cube ? 6 : 1;
        TextureFormat format = TextureFormat.Unknown;
        if ((flags & FourCc) != 0)
        {
            switch (fourCc)
            {
                case 0x31545844: format = TextureFormat.Bc1; break;
                case 0x35545844: format = TextureFormat.Bc3; break;
                case 0x31495441: case 0x55344342: format = TextureFormat.Bc4; break;
                case 0x32495441: case 0x55354342: format = TextureFormat.Bc5; break;
                case 0x30315844:
                    if (file.Length < 148) return false;
                    format = U(128) switch
                    {
                        71 => TextureFormat.Bc1, 77 => TextureFormat.Bc3, 80 => TextureFormat.Bc4, 83 => TextureFormat.Bc5, 98 => TextureFormat.Bc7,
                        28 => TextureFormat.Rgba8, 61 => TextureFormat.R8, 54 => TextureFormat.R16F, _ => TextureFormat.Unknown,
                    };
                    if (U(132) != 3) return false; // only 2D textures
                    cube |= (U(136) & 0x4) != 0;
                    slices = Math.Max(1, (int)U(140)) * (cube ? 6 : 1);
                    body = 148;
                    break;
            }
        }
        // 32-bit pixels, red first or blue first; blue-first ones are turned round below
        bool blueFirst = false;
        if (format == TextureFormat.Unknown && (flags & Rgb) != 0 && U(88) == 32 && U(96) == 0x0000FF00)
        {
            if (U(92) == 0x000000FF && U(100) == 0x00FF0000) format = TextureFormat.Rgba8;
            else if (U(92) == 0x00FF0000 && U(100) == 0x000000FF) (format, blueFirst) = (TextureFormat.Rgba8, true);
        }
        if (format == TextureFormat.Unknown || width <= 0 || height <= 0) return false;
        info = new TextureInfo(width, height, mips, slices, 0, format, 16, 0, file.Length, file.Length - body) { IsCube = cube && slices == 6 };
        int chain = TextureDecoder.ChainSize(info) * slices;
        if (slices > 1 && file.Length - body < chain) return false;
        if (file.Length - body < chain)
        {
            // a file cut short still shows what it has: the levels that fit
            while (info.Mips > 1 && file.Length - body < TextureDecoder.ChainSize(info)) info = info with { Mips = info.Mips - 1 };
            chain = TextureDecoder.ChainSize(info);
            if (file.Length - body < chain) return false;
        }
        pixels = TextureDecoder.LevelMajor(info, file.AsSpan(body, chain).ToArray());
        if (blueFirst)
            for (int i = 0; i + 4 <= pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return true;
    }
}
