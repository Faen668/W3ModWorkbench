using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Packing;

/// <summary>Reads the pictures a modder may make a texture from, into 32-bit pixels (red, green, blue, alpha).</summary>
public static class Pictures
{
    /// <summary>The picture file types a texture can be made from, besides .dds.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".tga", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff",
    };

    /// <summary>A picture's pixels, its width and height. Throws <see cref="InvalidDataException"/> for one that can't be read.</summary>
    public static (byte[] Rgba, int Width, int Height) Read(string path, byte[] file)
    {
        if (Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase)) return Tga(file);
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(file), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int width = frame.PixelWidth, height = frame.PixelHeight;
            var pixels = new byte[width * height * 4];
            frame.CopyPixels(pixels, width * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            return (pixels, width, height);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException(L.T("Pictures.PictureCouldntRead", ex.Message), ex);
        }
    }

    /// <summary>
    /// A Targa picture: true colour (24 or 32 bits) or greyscale, plain or run-length packed, rows from the bottom up
    /// unless its header says top down.
    /// </summary>
    private static (byte[], int, int) Tga(byte[] f)
    {
        if (f.Length < 18) throw new InvalidDataException(L.T("Pictures.TgaTooShort"));
        int idLength = f[0], colourMapType = f[1], type = f[2];
        int width = f[12] | f[13] << 8, height = f[14] | f[15] << 8, bits = f[16], descriptor = f[17];
        if (colourMapType != 0 || type is not (2 or 3 or 10 or 11) || width == 0 || height == 0)
            throw new InvalidDataException(L.T("Pictures.OnlyTrueColourGreyscale"));
        int bytes = bits / 8;
        bool grey = type is 3 or 11;
        if ((grey && bytes != 1) || (!grey && bytes is not (3 or 4))) throw new InvalidDataException(L.T("Pictures.BitTgaCantUsed", bits));
        int at = 18 + idLength;
        var pixels = new byte[width * height * 4];
        int count = width * height, done = 0;
        void Put(int i, int src)
        {
            if (src + bytes > f.Length) throw new InvalidDataException(L.T("Pictures.TgaCutShort"));
            if (grey) { pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = f[src]; pixels[i * 4 + 3] = 255; }
            else
            {
                pixels[i * 4] = f[src + 2]; pixels[i * 4 + 1] = f[src + 1]; pixels[i * 4 + 2] = f[src];
                pixels[i * 4 + 3] = bytes == 4 ? f[src + 3] : (byte)255;
            }
        }
        if (type is 2 or 3)
            for (; done < count; done++, at += bytes) Put(done, at);
        else
            while (done < count)
            {
                if (at >= f.Length) throw new InvalidDataException(L.T("Pictures.TgaCutShort"));
                int header = f[at++], run = (header & 0x7F) + 1;
                if ((header & 0x80) != 0) { for (int k = 0; k < run && done < count; k++) Put(done++, at); at += bytes; }
                else for (int k = 0; k < run && done < count; k++, at += bytes) Put(done++, at);
            }
        // bottom-up rows (the usual) turned the right way up
        if ((descriptor & 0x20) == 0)
        {
            int row = width * 4;
            var line = new byte[row];
            for (int y = 0; y < height / 2; y++)
            {
                Buffer.BlockCopy(pixels, y * row, line, 0, row);
                Buffer.BlockCopy(pixels, (height - 1 - y) * row, pixels, y * row, row);
                Buffer.BlockCopy(line, 0, pixels, (height - 1 - y) * row, row);
            }
        }
        return (pixels, width, height);
    }
}
