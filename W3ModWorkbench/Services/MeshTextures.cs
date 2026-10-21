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
        if (locator.FindTexture(xbmPath) is not { } texture) return null;
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
            if (TextureDecoder.Decode(info, pixels, level) is not { } image) return null;
            var decoded = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
            decoded.Freeze();
            BitmapSource picture = format == PixelFormats.Bgra32 ? decoded : new FormatConvertedBitmap(decoded, format, null, 0);
            picture.Freeze();
            GpuTextures.Store(picture, GpuTextures.DdsOf(info, pixels, level));
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
        if (locator.FindTexture(xbmPath) is not { } texture) return null;
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

    /// <summary>Where a texture's pixels were found, or that they weren't, for a material's status line.</summary>
    public static string Describe(ResourceLocator locator, string xbmPath) =>
        locator.FindTexture(xbmPath) is { } texture ? L.T("MeshTextures.From", texture.FormatText, texture.Archive.Owner, texture.Archive.Name) : L.T("MeshTextures.NotFoundAnyTexture");
}
