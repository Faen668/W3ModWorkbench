using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Textures for the mesh viewer: an .xbm path's pixels, found in a texture cache, as a picture to put on a mesh.
/// The colour is kept and the alpha dropped, as the game often keeps something else there (gloss, a mask).
/// </summary>
public static class MeshTextures
{
    /// <summary>
    /// The texture no larger than <paramref name="max"/> across (a smaller mip level when it has one), without its alpha.
    /// Null when no cache has it or its format can't be read. Reads from disk, so it belongs off the UI thread. The 3D
    /// view is given the stored blocks for it (with the alpha), see <see cref="GpuTextures.Store"/>.
    /// </summary>
    public static BitmapSource? Load(ResourceLocator locator, string xbmPath, int max) => Read(locator, xbmPath, max, PixelFormats.Bgr32);

    /// <summary>
    /// The texture with its alpha, for a material that's cut out by it (hair cards): null when it can't be read. Reads
    /// from disk, so it belongs off the UI thread.
    /// </summary>
    public static BitmapSource? LoadWithAlpha(ResourceLocator locator, string xbmPath, int max) => Read(locator, xbmPath, max, PixelFormats.Bgra32);

    private static BitmapSource? Read(ResourceLocator locator, string xbmPath, int max, PixelFormat format)
    {
        if (locator.FindTexture(xbmPath) is not { } texture) return ProjectPicture(locator, xbmPath, format) ?? OwnPixels(locator, xbmPath, max, format);
        try
        {
            var info = texture.Info;
            if (info.Format == TextureFormat.Unknown) return null;
            byte[] pixels;
            using (var fs = new FileStream(texture.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                pixels = TextureCacheArchive.ReadPixels(fs, info);
            // the largest mip level no larger than asked for, as the texture viewer picks one
            int level = 0;
            while (level + 1 < info.Mips && Math.Max(info.Width >> (level + 1), info.Height >> (level + 1)) >= max) level++;
            // the graphics card takes the stored blocks from that level; what's kept decoded in memory is a smaller level
            // when there are blocks to give it (GpuTextures.Full decodes the whole one to paint over)
            var dds = GpuTextures.DdsOf(info, pixels, level);
            int kept = level;
            if (dds is not null)
                while (kept + 1 < info.Mips && Math.Max(info.Width >> kept, info.Height >> kept) > GpuTextures.KeptSize) kept++;
            if (TextureDecoder.Decode(info, pixels, kept) is not { } image) return null;
            // made straight in the format asked for: 32-bit colour without alpha is the same bytes, its alpha left out, so
            // there's no converter over a second copy (a scene at full texture detail ran out of memory on them)
            var picture = format == PixelFormats.Bgra32 || format == PixelFormats.Bgr32
                ? BitmapSource.Create(image.Width, image.Height, 96, 96, format, null, image.Bgra, image.Width * 4)
                : new FormatConvertedBitmap(BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4), format, null, 0);
            picture.Freeze();
            GpuTextures.Store(picture, dds);
            return picture;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A texture's full-size picture as a PNG, with its transparency, for exporting. Null when it can't be read.</summary>
    public static byte[]? Png(ResourceLocator locator, string xbmPath)
    {
        if (locator.FindTexture(xbmPath) is not { } texture)
            return ProjectPicture(locator, xbmPath, PixelFormats.Bgra32) is { } picture ? EncodePng(picture) : null;
        try
        {
            if (EntryImages.FromTexture(texture, int.MaxValue)?.Image is not { } image) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A material's pictures for a glTF, at full size: its colour, and its normal map as glTF reads one, with blue made
    /// from red and green as the game's shaders make it (it's often left empty) and the roughness the game keeps in its
    /// alpha given as a picture of its own, in green. Pictures that aren't found are left out, and all of them without
    /// <paramref name="pictures"/>. Reads from disk.
    /// </summary>
    public static GltfMaterial ForGltf(ResourceLocator locator, MeshMaterial material, string name, bool pictures)
    {
        GltfPicture? colour = null, normal = null, roughness = null;
        var plain = new GltfMaterial(name, material, VertexTint: LookTextures.TintsWithVertexColour(material), Overlay: LookTextures.IsOverlay(material));
        if (!pictures || plain.Overlay) return plain;
        if (material.Diffuse?.Path is { } diffuse && Png(locator, diffuse) is { } png)
            colour = new GltfPicture(Path.GetFileNameWithoutExtension(diffuse), png);
        if (material.Normal?.Path is { } normalPath && normalPath.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase)
            && Decoded(locator, normalPath) is { } image)
        {
            string baseName = Path.GetFileNameWithoutExtension(normalPath);
            var pixels = image.Bgra;
            var normals = new byte[pixels.Length];
            var rough = new byte[pixels.Length];
            bool hasAlpha = false;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte r = pixels[i + 2], g = pixels[i + 1], a = pixels[i + 3];
                float x = r / 127.5f - 1, y = g / 127.5f - 1;
                float z = MathF.Sqrt(Math.Clamp(1 - x * x - y * y, 0, 1));
                normals[i] = (byte)Math.Round((z + 1) * 127.5f);
                normals[i + 1] = g;
                normals[i + 2] = r;
                normals[i + 3] = 255;
                rough[i + 1] = a;
                rough[i + 3] = 255;
                hasAlpha |= a != 255;
            }
            normal = new GltfPicture(baseName + "_normal", EncodePng(normals, image.Width, image.Height)); // loc-ignore
            // a normal map without alpha keeps no roughness
            if (hasAlpha) roughness = new GltfPicture(baseName + "_roughness", EncodePng(rough, image.Width, image.Height)); // loc-ignore
        }
        return plain with { Colour = colour, Normal = normal, Roughness = roughness };
    }

    private static DecodedImage? Decoded(ResourceLocator locator, string xbmPath)
    {
        if (locator.FindTexture(xbmPath) is not { } texture || texture.Info.Format == TextureFormat.Unknown) return null;
        try
        {
            byte[] pixels;
            using (var fs = new FileStream(texture.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                pixels = TextureCacheArchive.ReadPixels(fs, texture.Info);
            return TextureDecoder.Decode(texture.Info, pixels, 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[] EncodePng(byte[] bgra, int width, int height)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Where a texture's pixels were found, or that they weren't, for a material's status line.</summary>
    public static string Describe(ResourceLocator locator, string xbmPath) =>
        locator.FindTexture(xbmPath) is { } texture ? L.T("MeshTextures.From", texture.FormatText, texture.Archive.Owner, texture.Archive.Name)
        : PictureFile(locator, xbmPath) is { } file ? L.T("MeshTextures.FromProjectPicture", Path.GetFileName(file))
        : L.T("MeshTextures.NotFoundAnyTexture");

    /// <summary>
    /// The picture in the project that Build makes a texture from (<c>name.tga</c> beside <c>name.xbm</c>, or .dds, .png…),
    /// before the mod is built: its disk path, or null.
    /// </summary>
    private static string? PictureFile(ResourceLocator locator, string xbmPath)
    {
        foreach (string ext in new[] { ".dds", ".tga", ".png", ".bmp", ".jpg", ".jpeg" })
            if (locator.ProjectFile(Path.ChangeExtension(xbmPath, ext)) is { } file && File.Exists(file)) return file;
        return null;
    }

    /// <summary>A project picture standing for a texture not built yet, as the format asked for; null when there's none or it can't be read.</summary>
    /// <summary>
    /// A texture that keeps its pixels in its own file rather than the texture cache (the entity proxies' far pictures,
    /// for one): its largest level no larger than asked for. Null when it isn't such a file.
    /// </summary>
    private static BitmapSource? OwnPixels(ResourceLocator locator, string xbmPath, int max, PixelFormat format)
    {
        try
        {
            if (Core.Packing.TextureCooker.GameLevels(locator, xbmPath) is not { Levels.Count: > 0 } levels) return null;
            int level = 0;
            while (level + 1 < levels.Levels.Count && Math.Max(levels.Levels[level + 1].Width, levels.Levels[level + 1].Height) >= max) level++;
            var chosen = levels.Levels[level];
            var info = new TextureInfo(chosen.Width, chosen.Height, 1, 1, levels.Format, TextureCacheArchive.FormatOf(levels.Format), 0, 0, chosen.Data.Length, chosen.Data.Length);
            if (TextureDecoder.Decode(info, chosen.Data) is not { } image) return null;
            var picture = format == PixelFormats.Bgra32 || format == PixelFormats.Bgr32
                ? BitmapSource.Create(image.Width, image.Height, 96, 96, format, null, image.Bgra, image.Width * 4)
                : new FormatConvertedBitmap(BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4), format, null, 0);
            picture.Freeze();
            return picture;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static BitmapSource? ProjectPicture(ResourceLocator locator, string xbmPath, PixelFormat format)
    {
        if (PictureFile(locator, xbmPath) is not { } file) return null;
        try
        {
            var data = File.ReadAllBytes(file);
            byte[] rgba;
            int width, height;
            if (Path.GetExtension(file).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            {
                if (!Dds.TryRead(data, out var info, out var pixels) || TextureDecoder.Decode(info, pixels, 0) is not { } image) return null;
                (rgba, width, height) = (image.Bgra, image.Width, image.Height);
            }
            else
            {
                (rgba, width, height) = Core.Packing.Pictures.Read(file, data);
                // red and blue the other way round, as a picture on screen keeps them
                for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            }
            var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4);
            BitmapSource result = format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, format, null, 0);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[] EncodePng(BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
