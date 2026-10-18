using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Textures;

namespace W3ModWorkbench;

/// <summary>
/// Shows a texture from a texture.cache: its mip levels, one channel at a time, over a chequerboard for transparency.
/// It can be saved as a PNG (the level shown) or as a .dds (every level).
/// </summary>
public partial class TextureWindow : Window, IMessageHost
{
    private readonly TextureEntry _entry;
    private byte[]? _pixels;
    private DecodedImage? _image;
    private double _zoom = 1;

    private TextureWindow(TextureEntry entry)
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _entry = entry;
        Title = $"{Path.GetFileName(entry.Path)} - Texture";
        for (int level = 0; level < entry.Info.Mips; level++)
            MipBox.Items.Add($"{Math.Max(1, entry.Info.Width >> level)} x {Math.Max(1, entry.Info.Height >> level)}" + (level == 0 ? "  (full)" : ""));
        MipBox.SelectedIndex = 0;
        InfoText.Text = $"{entry.Path}   ·   {entry.FormatText}   ·   {Format.Bytes(entry.Info.UnpackedSize)} unpacked, {Format.Bytes(entry.Info.PackedSize)} in the cache"
            + (entry.Info.Slices > 1 ? $"   ·   {entry.Info.Slices} slices, the first is shown" : "");
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Opens a texture in a window of its own.</summary>
    public static void Open(TextureEntry entry, Window owner) => new TextureWindow(entry).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");

    private async Task LoadAsync()
    {
        try
        {
            var archive = _entry.Archive;
            _pixels = await Task.Run(() =>
            {
                using var fs = new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return TextureCacheArchive.ReadPixels(fs, _entry.Info);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            BusyText.Text = "The texture could not be read.";
            await ShowMessage("The texture could not be read", ex.Message, MessageKind.Warning);
            return;
        }
        SaveDdsButton.IsEnabled = _entry.Info.Format != TextureFormat.Unknown;
        Render();
    }

    private int Level => Math.Max(0, MipBox.SelectedIndex);

    private void Render()
    {
        if (_pixels is null) return;
        _image = TextureDecoder.Decode(_entry.Info, _pixels, Level);
        if (_image is null)
        {
            Picture.Source = null;
            BusyText.Visibility = Visibility.Visible;
            BusyText.Text = $"Texture format {_entry.Info.RawFormat} can't be shown yet. Save DDS isn't available for it either.";
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

    private string BaseName => Path.GetFileNameWithoutExtension(_entry.Path);

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
        if (_pixels is null) return;
        var dialog = new SaveFileDialog { Title = "Save the texture", FileName = BaseName + ".dds", Filter = "DirectDraw Surface (*.dds)|*.dds" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, Dds.Wrap(_entry.Info, _pixels));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            _ = ShowMessage("The texture could not be saved", ex.Message, MessageKind.Warning);
        }
    }
}
