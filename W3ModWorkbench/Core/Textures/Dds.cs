using System.Buffers.Binary;
using System.IO;

namespace W3ModWorkbench.Core.Textures;

/// <summary>Writes a texture's pixels as a .dds file, so any image tool can open what was extracted.</summary>
public static class Dds
{
    private const uint Magic = 0x20534444; // "DDS "
    private const uint Caps = 0x1, Height = 0x2, Width = 0x4, Pitch = 0x8, PixelFormat = 0x1000, MipMapCount = 0x20000, LinearSize = 0x80000;
    private const uint FourCc = 0x4, Rgb = 0x40, AlphaPixels = 0x1, Luminance = 0x20000;

    public static byte[] Wrap(TextureInfo info, byte[] pixels)
    {
        if (info.Format == TextureFormat.Unknown)
            throw new NotSupportedException($"Texture format {info.RawFormat} isn't understood yet, so it can't be written as a .dds.");
        int chain = TextureDecoder.ChainSize(info);
        if (pixels.Length < chain) throw new InvalidDataException("The texture's data is shorter than its mip levels need.");
        bool dx10 = info.Format is TextureFormat.Bc7 or TextureFormat.Rgb32F;
        var file = new byte[4 + 124 + (dx10 ? 20 : 0) + chain];
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
        switch (info.Format)
        {
            case TextureFormat.Bc1: Put(80, FourCc); Put(84, 0x31545844); break; // DXT1
            case TextureFormat.Bc3: Put(80, FourCc); Put(84, 0x35545844); break; // DXT5
            case TextureFormat.Bc4: Put(80, FourCc); Put(84, 0x31495441); break; // ATI1
            case TextureFormat.Bc5: Put(80, FourCc); Put(84, 0x32495441); break; // ATI2
            case TextureFormat.Bc7: case TextureFormat.Rgb32F: Put(80, FourCc); Put(84, 0x30315844); break; // DX10
            case TextureFormat.Bgra8:
                Put(80, Rgb | AlphaPixels); Put(88, 32); Put(92, 0x00FF0000); Put(96, 0x0000FF00); Put(100, 0x000000FF); Put(104, 0xFF000000);
                break;
            case TextureFormat.R8:
                Put(80, Luminance); Put(88, 8); Put(92, 0xFF);
                break;
        }
        Put(108, 0x1000 | (info.Mips > 1 ? 0x400008u : 0)); // texture, and complex + mipmap
        int body = 128;
        if (dx10)
        {
            Put(128, info.Format == TextureFormat.Bc7 ? 98u : 6u); // BC7_UNORM, R32G32B32_FLOAT
            Put(132, 3); // 2D
            Put(140, 1);
            body += 20;
        }
        Buffer.BlockCopy(pixels, 0, file, body, chain);
        return file;
    }
}
