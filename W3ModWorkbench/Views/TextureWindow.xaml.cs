using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Core.Projects;

namespace W3ModWorkbench;

/// <summary>
/// Shows a texture from a texture.cache: its mip levels, one channel at a time, over a chequerboard for transparency.
/// It can be saved as a PNG (the level shown) or as a .dds (every level).
/// </summary>
public partial class TextureWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly TextureInfo? _info;
    private readonly Func<byte[]> _readPixels;
    private readonly DecodedImage? _picture;
    private byte[]? _pixels;
    private DecodedImage? _image;
    private double _zoom = 1;

    private TextureWindow(string path, string detail, TextureInfo? info, Func<byte[]> readPixels, DecodedImage? picture, int mips)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _path = path;
        _info = info;
        _readPixels = readPixels;
        _picture = picture;
        Title = $"{Path.GetFileName(path)} - Texture";
        int width = info?.Width ?? picture!.Width, height = info?.Height ?? picture!.Height;
        for (int level = 0; level < mips; level++)
            MipBox.Items.Add($"{Math.Max(1, width >> level)} x {Math.Max(1, height >> level)}" + (level == 0 ? "  (full)" : ""));
        MipBox.SelectedIndex = 0;
        InfoText.Text = $"{path}   ·   {detail}";
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Opens a texture from a texture.cache in a window of its own.</summary>
    public static void Open(TextureEntry entry, Window owner)
    {
        var info = entry.Info;
        var archive = entry.Archive;
        string detail = $"{entry.FormatText}   ·   {Format.Bytes(info.UnpackedSize)} unpacked, {Format.Bytes(info.PackedSize)} in the cache"
            + (info.Slices > 1 ? $"   ·   {info.Slices} slices, the first is shown" : "");
        new TextureWindow(entry.Path, detail, info, () =>
        {
            using var fs = new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TextureCacheArchive.ReadPixels(fs, info);
        }, null, info.Mips).Standalone(owner).Show();
    }

    /// <summary>Whether a file's name says it's a picture this window can show: PNG, JPEG, BMP, GIF, TIFF or DDS.</summary>
    public static bool CanShow(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".dds";

    /// <summary>Opens a picture file's bytes: an ordinary image, or a .dds, which keeps its mip levels. False when it couldn't be read as one.</summary>
    public static bool Open(string name, string source, byte[] data, Window owner)
    {
        if (Path.GetExtension(name).Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            if (!Dds.TryRead(data, out var info, out var pixels) || TextureDecoder.Decode(info, pixels, 0) is null) return false;
            new TextureWindow(name, $"{source}   ·   {info.Description}   ·   {Format.Bytes(data.Length)}",
                info, () => pixels, null, info.Mips).Standalone(owner).Show();
            return true;
        }
        DecodedImage picture;
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var bgra = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(bgra, converted.PixelWidth * 4, 0);
            picture = new DecodedImage(converted.PixelWidth, converted.PixelHeight, bgra);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or ArgumentException or FileFormatException)
        {
            return false;
        }
        new TextureWindow(name, $"{source}   ·   {picture.Width}x{picture.Height}   ·   {Format.Bytes(data.Length)}", null, () => [], picture, 1).Standalone(owner).Show();
        return true;
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        try
        {
            _pixels = _picture is not null ? [] : await Task.Run(_readPixels);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            BusyText.Text = "The texture could not be read.";
            await ShowMessage("The texture could not be read", ex.Message, MessageKind.Warning);
            return;
        }
        SaveDdsButton.IsEnabled = _info is { Format: not TextureFormat.Unknown };
        Render();
    }

    private int Level => Math.Max(0, MipBox.SelectedIndex);

    private void Render()
    {
        if (_pixels is null) return;
        _image = _picture ?? TextureDecoder.Decode(_info!, _pixels, Level);
        if (_image is null)
        {
            Picture.Source = null;
            BusyText.Visibility = Visibility.Visible;
            BusyText.Text = $"Texture format {_info?.RawFormat} can't be shown yet. Save DDS isn't available for it either.";
            SavePngButton.IsEnabled = false;
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        SavePngButton.IsEnabled = true;
        var shown = Channels(_image);
        var bitmap = BitmapSource.Create(_image.Width, _image.Height, 96, 96, PixelFormats.Bgra32, null, shown, _image.Width * 4);
        bitmap.Freeze();
        Picture.Source = bitmap;
        ApplyZoom();
    }

    /// <summary>The pixels as the channel buttons say: everything, the colour alone, or one channel as grey.</summary>
    private byte[] Channels(DecodedImage image)
    {
        if (ChannelRgba.IsChecked == true) return image.Bgra;
        var result = new byte[image.Bgra.Length];
        for (int i = 0; i < image.Bgra.Length; i += 4)
        {
            byte b = image.Bgra[i], g = image.Bgra[i + 1], r = image.Bgra[i + 2], a = image.Bgra[i + 3];
            if (ChannelRgb.IsChecked == true) { result[i] = b; result[i + 1] = g; result[i + 2] = r; }
            else
            {
                byte v = ChannelR.IsChecked == true ? r : ChannelG.IsChecked == true ? g : ChannelB.IsChecked == true ? b : a;
                result[i] = result[i + 1] = result[i + 2] = v;
            }
            result[i + 3] = 255;
        }
        return result;
    }

    private void Channel_Checked(object sender, RoutedEventArgs e) => Render();

    private void Mip_SelectionChanged(object sender, SelectionChangedEventArgs e) => Render();

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (FitToggle.IsChecked != true) _zoom = 1;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_image is null) return;
        if (FitToggle.IsChecked == true)
        {
            Picture.Width = double.NaN;
            Picture.Height = double.NaN;
            Picture.Stretch = Stretch.Uniform;
            Backdrop.Width = Math.Max(16, Scroller.ViewportWidth - 38);
            Backdrop.Height = Math.Max(16, Scroller.ViewportHeight - 38);
            // keep the shape: the backdrop is as large as fits, the picture sits in it
            double scale = Math.Min(Backdrop.Width / _image.Width, Backdrop.Height / _image.Height);
            Backdrop.Width = _image.Width * scale;
            Backdrop.Height = _image.Height * scale;
            Picture.Stretch = Stretch.Fill;
        }
        else
        {
            Picture.Stretch = Stretch.Fill;
            Backdrop.Width = Picture.Width = _image.Width * _zoom;
            Backdrop.Height = Picture.Height = _image.Height * _zoom;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (FitToggle.IsChecked == true) ApplyZoom();
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _image is null) return;
        e.Handled = true;
        FitToggle.IsChecked = false;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.25 : 0.8), 0.05, 32);
        ApplyZoom();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.Escape) Close();
    }

    // ---------------------------------------------------------------- saving

    private string BaseName => Path.GetFileNameWithoutExtension(_path);

    private void SavePng_Click(object sender, RoutedEventArgs e)
    {
        if (_image is null) return;
        var dialog = new SaveFileDialog { Title = "Save the picture", FileName = BaseName + (Level > 0 ? $"_mip{Level}" : "") + ".png", Filter = "PNG image (*.png)|*.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bitmap = BitmapSource.Create(_image.Width, _image.Height, 96, 96, PixelFormats.Bgra32, null, Channels(_image), _image.Width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            FileSafety.CheckWrite(dialog.FileName);
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessage("The picture could not be saved", ex.Message, MessageKind.Warning);
        }
    }

    private void SaveDds_Click(object sender, RoutedEventArgs e)
    {
        if (_pixels is null || _info is null) return;
        var dialog = new SaveFileDialog { Title = "Save the texture", FileName = BaseName + ".dds", Filter = "DirectDraw Surface (*.dds)|*.dds" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            File.WriteAllBytes(dialog.FileName, Dds.Wrap(_info!, _pixels));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            _ = ShowMessage("The texture could not be saved", ex.Message, MessageKind.Warning);
        }
    }
}
