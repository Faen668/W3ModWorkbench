using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Textures;

namespace W3ModWorkbench;

/// <summary>
/// Textures for the mesh viewer: an .xbm path's pixels, found in a texture cache, as a picture to put on a mesh.
/// The colour is kept and the alpha dropped, as the game often keeps something else there (gloss, a mask).
/// </summary>
public static class MeshTextures
{
    /// <summary>
    /// The texture no larger than <paramref name="max"/> across (a smaller mip level when it has one). Null when no
    /// cache has it or its format can't be read. Reads from disk, so it belongs off the UI thread.
    /// </summary>
    public static BitmapSource? Load(ResourceLocator locator, string xbmPath, int max)
    {
        if (locator.FindTexture(xbmPath) is not { } texture) return null;
        try
        {
            if (EntryImages.FromTexture(texture, max)?.Image is not { } image) return null;
            var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The texture with its alpha, for a material that's cut out by it (hair cards): null when it can't be read. Reads
    /// from disk, so it belongs off the UI thread.
    /// </summary>
    public static BitmapSource? LoadWithAlpha(ResourceLocator locator, string xbmPath, int max)
    {
        if (locator.FindTexture(xbmPath) is not { } texture) return null;
        try
        {
            if (EntryImages.FromTexture(texture, max)?.Image is not { } image) return null;
            var withAlpha = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            withAlpha.Freeze();
            return withAlpha;
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
        locator.FindTexture(xbmPath) is { } texture ? $"{texture.FormatText}, from {texture.Archive.Owner}\\{texture.Archive.Name}" : "Not found in any texture cache.";
}
