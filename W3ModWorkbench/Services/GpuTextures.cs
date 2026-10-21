using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit.SharpDX;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Pictures as textures for the 3D view's graphics card: each made once (kept while the picture is), as 32-bit colour
/// with its alpha and a full chain of smaller mip levels, so it stays smooth from afar instead of shimmering. Made off
/// the UI thread where the picture is read (<see cref="Prepare"/>), so showing a stage doesn't stop to make them.
/// </summary>
public static class GpuTextures
{
    private static readonly ConditionalWeakTable<BitmapSource, TextureModel> Made = [];
    private static readonly ConditionalWeakTable<BitmapSource, byte[]> Stored = [];

    /// <summary>
    /// The texture for a picture, made the first time it's asked for: the game's own blocks as they're stored
    /// (block-compressed, with their mip levels) when the picture is a texture read unchanged, else the picture's pixels.
    /// </summary>
    public static TextureModel Of(BitmapSource picture) => Made.GetValue(picture, p =>
        Stored.TryGetValue(p, out var dds) ? TextureModel.Create(new MemoryStream(dds)) ?? Make(p) : Make(p));

    /// <summary>
    /// Notes a texture's stored blocks for a picture decoded from them, so the graphics card gets the blocks themselves:
    /// a quarter to an eighth of the memory, the game's own mip levels, and the alpha kept (a normal map's roughness).
    /// </summary>
    public static void Store(BitmapSource picture, byte[]? dds)
    {
        if (dds is not null) Stored.AddOrUpdate(picture, dds);
    }

    /// <summary>
    /// A texture's stored pixels from <paramref name="level"/> down, as a .dds file the graphics card takes as it is;
    /// null for a format it can't (or a cube map or array, which go whole through <see cref="Cube"/>).
    /// </summary>
    public static byte[]? DdsOf(Core.Textures.TextureInfo info, byte[] pixels, int level)
    {
        if (info.IsCube || info.Slices != 1 || level >= info.Mips) return null;
        if (info.Format is not (TextureFormat.Bc1 or TextureFormat.Bc3 or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Bc7 or TextureFormat.Rgba8)) return null;
        var from = info with { Width = Math.Max(1, info.Width >> level), Height = Math.Max(1, info.Height >> level), Mips = info.Mips - level };
        int start = TextureDecoder.LevelOffset(info, level), length = TextureDecoder.ChainSize(from);
        if (start < 0 || start + length > pixels.Length) return null;
        try
        {
            return Core.Textures.Dds.Wrap(from, pixels.AsSpan(start, length).ToArray());
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static readonly Dictionary<string, TextureModel?> Cubes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A cube map from the texture caches (an environment's, an eye's highlight), whole with its mip levels; null when none has it.</summary>
    public static TextureModel? Cube(ResourceLocator locator, string path)
    {
        lock (Cubes)
            if (Cubes.TryGetValue(path, out var known)) return known;
        TextureModel? made = null;
        if (locator.FindTexture(path) is { Info.IsCube: true } entry)
            try
            {
                byte[] pixels;
                using (var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    pixels = TextureCacheArchive.ReadPixels(fs, entry.Info);
                made = TextureModel.Create(new MemoryStream(Core.Textures.Dds.Wrap(entry.Info, pixels)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException) { }
        lock (Cubes) Cubes[path] = made;
        return made;
    }

    /// <summary>The mip levels a cube map read by <see cref="Cube"/> has, for how blurred a rough surface's reflection is.</summary>
    public static int CubeMips(ResourceLocator locator, string path) => locator.FindTexture(path)?.Info.Mips ?? 1;

    /// <summary>Makes a picture's texture ahead of time, on the thread reading it. The picture must be frozen.</summary>
    public static void Prepare(BitmapSource? picture)
    {
        if (picture is { IsFrozen: true }) Of(picture);
    }

    private static TextureModel Make(BitmapSource picture)
    {
        // straight (not premultiplied) colour and alpha, as the shaders blend it; a picture without alpha gets 255
        BitmapSource bgra = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var level = new byte[width * height * 4];
        bgra.CopyPixels(level, width * 4, 0);
        var levels = new List<(byte[] Pixels, int Width, int Height)> { (level, width, height) };
        while (width > 1 || height > 1)
        {
            (level, width, height) = Half(level, width, height);
            levels.Add((level, width, height));
        }
        return TextureModel.Create(new MemoryStream(Dds(levels))) ?? throw new InvalidOperationException(L.T("GpuTextures.TextureCouldntMade"));
    }

    /// <summary>
    /// A level half the size, each pixel from the four under it: the colour weighted by their alpha, so a hair card's
    /// see-through pixels don't tint the edges of what's kept, and the alpha halfway between their average and the most
    /// of them, so thin strands (a beard's stubble) don't fade away from afar, as they do when it's only averaged.
    /// </summary>
    private static (byte[], int, int) Half(byte[] pixels, int width, int height)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var half = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int b = 0, g = 0, r = 0, a = 0, most = 0, weight = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(width - 1, x * 2 + dx), sy = Math.Min(height - 1, y * 2 + dy);
                        int at = (sy * width + sx) * 4, alpha = pixels[at + 3] + 1;
                        b += pixels[at] * alpha;
                        g += pixels[at + 1] * alpha;
                        r += pixels[at + 2] * alpha;
                        a += pixels[at + 3];
                        most = Math.Max(most, pixels[at + 3]);
                        weight += alpha;
                    }
                int to = (y * w + x) * 4;
                half[to] = (byte)(b / weight);
                half[to + 1] = (byte)(g / weight);
                half[to + 2] = (byte)(r / weight);
                half[to + 3] = (byte)((a / 4 + most) / 2);
            }
        return (half, w, h);
    }

    /// <summary>The levels as an uncompressed 32-bit BGRA .dds file in memory, which Helix reads with its mip levels.</summary>
    private static byte[] Dds(List<(byte[] Pixels, int Width, int Height)> levels)
    {
        var file = new byte[128 + levels.Sum(l => l.Pixels.Length)];
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), value);
        var (_, width, height) = levels[0];
        Put(0, 0x20534444); // "DDS "
        Put(4, 124);
        Put(8, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | 0x20000); // caps, height, width, pitch, pixel format, mip count
        Put(12, (uint)height);
        Put(16, (uint)width);
        Put(20, (uint)(width * 4));
        Put(28, (uint)levels.Count);
        Put(76, 32);
        Put(80, 0x40 | 0x1); // RGB with alpha
        Put(88, 32);
        Put(92, 0x00FF0000);
        Put(96, 0x0000FF00);
        Put(100, 0x000000FF);
        Put(104, 0xFF000000);
        Put(108, 0x1000 | 0x8 | 0x400000); // texture, complex, mipmap
        int offset = 128;
        foreach (var (pixels, _, _) in levels)
        {
            Buffer.BlockCopy(pixels, 0, file, offset, pixels.Length);
            offset += pixels.Length;
        }
        return file;
    }
}
