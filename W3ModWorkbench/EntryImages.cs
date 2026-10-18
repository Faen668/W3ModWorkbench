using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Textures;

namespace W3ModWorkbench;

/// <summary>A picture made from a file in a list: ready to show, and a line about what it is.</summary>
public sealed record EntryPicture(BitmapSource Image, string Info);

/// <summary>
/// Pictures for the file list: thumbnails and the preview pane. Textures from a texture.cache, picture files (PNG, JPEG,
/// BMP, GIF, TIFF) and DDS files all become small images, decoded off the UI thread a few at a time.
/// </summary>
public static class EntryImages
{
    private static readonly SemaphoreSlim Slots = new(3);

    /// <summary>Whether a file in a list can be shown as a picture without looking anything up.</summary>
    public static bool IsPicture(ArchiveEntry entry) => entry is TextureEntry || TextureWindow.CanShow(entry.Path);

    /// <summary>
    /// The entry as a picture no larger than <paramref name="max"/> pixels on its longest side (smaller mip levels are
    /// used when there are some). Null when it isn't a picture or can't be read.
    /// </summary>
    /// <param name="wanted">Asked once its turn comes: false when the picture isn't needed any more (it scrolled out of view), so it's skipped.</param>
    public static async Task<EntryPicture?> LoadAsync(ArchiveEntry entry, int max, CancellationToken cancel = default, Func<bool>? wanted = null)
    {
        await Slots.WaitAsync(cancel);
        try
        {
            if (wanted is not null && !wanted()) return null;
            return await Task.Run(() => Load(entry, max), cancel);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            Slots.Release();
        }
    }

    private static EntryPicture? Load(ArchiveEntry entry, int max)
    {
        try
        {
            if (entry is TextureEntry texture) return FromTexture(texture, max);
            if (!TextureWindow.CanShow(entry.Path)) return null;
            byte[] data;
            if (entry.Archive.ReadsFromDisk)
            {
                using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                data = entry.Read(fs);
            }
            else data = entry.Read(null);
            return FromFile(entry.Path, data, max);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A texture from a cache, at the smallest mip level that is still at least <paramref name="max"/> across, or the largest there is.</summary>
    public static EntryPicture? FromTexture(TextureEntry texture, int max)
    {
        var info = texture.Info;
        if (info.Format == TextureFormat.Unknown) return null;
        byte[] pixels;
        using (var fs = new FileStream(texture.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            pixels = TextureCacheArchive.ReadPixels(fs, info);
        return FromPixels(info, pixels, max, texture.FormatText);
    }

    private static EntryPicture? FromPixels(TextureInfo info, byte[] pixels, int max, string description)
    {
        int level = 0;
        while (level + 1 < info.Mips && Math.Max(info.Width >> (level + 1), info.Height >> (level + 1)) >= max) level++;
        if (TextureDecoder.Decode(info, pixels, level) is not { } image) return null;
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();
        return new EntryPicture(bitmap, description + (level > 0 ? $"  ·  showing {image.Width}x{image.Height}" : ""));
    }

    /// <summary>A picture file's bytes, shrunk to <paramref name="max"/> while it's decoded.</summary>
    public static EntryPicture? FromFile(string name, byte[] data, int max)
    {
        if (Path.GetExtension(name).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            return Dds.TryRead(data, out var info, out var pixels) ? FromPixels(info, pixels, max, info.Description) : null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(data);
        image.EndInit();
        int w = image.PixelWidth, h = image.PixelHeight;
        if (Math.Max(w, h) > max)
        {
            // decoding at the smaller size is quicker and lighter than shrinking afterwards
            var small = new BitmapImage();
            small.BeginInit();
            small.CacheOption = BitmapCacheOption.OnLoad;
            small.StreamSource = new MemoryStream(data);
            if (w >= h) small.DecodePixelWidth = max; else small.DecodePixelHeight = max;
            small.EndInit();
            small.Freeze();
            return new EntryPicture(small, $"{w}x{h}  ·  showing {small.PixelWidth}x{small.PixelHeight}");
        }
        image.Freeze();
        return new EntryPicture(image, $"{w}x{h}");
    }
}
