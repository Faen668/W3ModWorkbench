using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Shows a texture from a texture.cache: its mip levels, a cube map's faces or an array's slices, one channel at a time,
/// over a chequerboard for transparency, or on a lit shape in 3D, as REDkit's texture viewer does. Beside it are the
/// texture's properties, from its resource file (.xbm, .w2cube, .texarray) as REDkit lists them, and what the cache says
/// about it. It can be saved as a PNG (the level shown) or as a .dds (every level and slice).
/// </summary>
public partial class TextureWindow : Window, IMessageHost
{
    private static readonly string[] FaceNames = ["+X", "−X", "+Y", "−Y", "+Z", "−Z"];

    private readonly string _path;
    private readonly TextureInfo? _info;
    private readonly Func<byte[]> _readPixels;
    private readonly DecodedImage? _picture;
    private readonly Func<byte[]?>? _resource;
    // finds the files the properties link to, as the window it was opened from finds them
    private readonly ResourceLocator _locator;
    // the cache a texture was opened from, which says whether it's one of the game's own; null for a picture file
    private readonly string? _origin;
    private readonly List<(string Name, string Value)> _facts;
    private byte[]? _pixels;
    private DecodedImage? _image;
    private double _zoom = 1;

    private TextureWindow(string path, string title, string detail, TextureInfo? info, Func<byte[]> readPixels, DecodedImage? picture, int mips,
        List<(string Name, string Value)> facts, Func<byte[]?>? resource, ResourceLocator? locator, int slice = 0, string? origin = null)
    {
        InitializeComponent();
        _locator = locator ?? GameLocatorOnly();
        _path = path;
        _info = info;
        _readPixels = readPixels;
        _picture = picture;
        _facts = facts;
        _resource = resource;
        _origin = origin;
        Title = L.T("TextureWindow.Texture2", title);
        // a texture of the game's (or a mod's), opened from its cache, can be replaced in the project by a picture
        ReplaceButton.Visibility = origin is not null && Path.GetExtension(path).ToLowerInvariant() is ".xbm" or ".w2cube" ? Visibility.Visible : Visibility.Collapsed;
        int width = info?.Width ?? picture!.Width, height = info?.Height ?? picture!.Height;
        for (int level = 0; level < mips; level++)
            MipBox.Items.Add($"{Math.Max(1, width >> level)} x {Math.Max(1, height >> level)}" + (level == 0 ? L.T("TextureWindow.Full") : ""));
        MipBox.SelectedIndex = 0;
        if (info is { Slices: > 1 })
        {
            for (int s = 0; s < info.Slices; s++) SliceBox.Items.Add(info.IsCube ? FaceNames[s] : $"texture_{s}");
            SliceBox.SelectedIndex = Math.Clamp(slice, 0, info.Slices - 1);
            SliceLabel.Text = info.IsCube ? L.T("TextureWindow.Face") : L.T("TextureWindow.Slice");
            SliceLabel.Visibility = SliceBox.Visibility = Visibility.Visible;
        }
        InfoText.Text = $"{path}   ·   {detail}";
        ShowProperties(null);
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>
    /// Opens a texture from a texture.cache in a window of its own; a texture array opens in the array viewer.
    /// <paramref name="resource"/> reads the texture's resource file (its .xbm, .w2cube or .texarray) for its properties,
    /// and may give null when there's none to be found.
    /// </summary>
    public static void Open(TextureEntry entry, Window owner, Func<byte[]?>? resource = null, ResourceLocator? locator = null)
    {
        if (entry.Info is { Slices: > 1, IsCube: false }) TextureArrayWindow.Open(entry, owner, resource, locator);
        else OpenSlice(entry, owner, resource, 0, locator);
    }

    /// <summary>Finds files in the game alone, for a window opened without the archives the tool has open.</summary>
    public static ResourceLocator GameLocatorOnly() => new([], null, GameLocator.Resolve(null));

    /// <summary>
    /// Opens a file a property links to: a texture (an .xbm, a cube map or an array) in the texture viewer, found as the
    /// game would find it. For anything else the path is copied, as there's no viewer for it here.
    /// </summary>
    public static async void OpenLink(Window owner, MessageCard message, ResourceLocator locator, string path)
    {
        string kind = Path.GetExtension(path).ToLowerInvariant();
        if (kind is not (".xbm" or ".w2cube" or ".texarray"))
        {
            try { Clipboard.SetText(path); }
            catch (System.Runtime.InteropServices.COMException) { } // another program has the clipboard open
            _ = message.Show(null, L.T("TextureWindow.NoViewer", FileKinds.Of(path)?.Name.ToLowerInvariant() ?? L.T("Common.Kind2"), path),
                MessageKind.Info, primary: L.T("Common.OK"));
            return;
        }
        var found = await Task.Run(() => locator.FindTexture(path));
        if (found is null)
        {
            _ = message.Show(L.T("Common.NotFound"), L.T("TextureWindow.IsntGamesTextureCaches", path), MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }
        Open(found, owner, () => locator.ReadFile(path), locator);
    }

    private void Properties_LinkClicked(object? sender, string path) => OpenLink(this, Message, _locator, path);

    /// <summary>Opens one texture of a cache entry (one slice of an array) in the texture viewer, as REDkit opens an array's texture.</summary>
    public static void OpenSlice(TextureEntry entry, Window owner, Func<byte[]?>? resource, int slice, ResourceLocator? locator = null)
    {
        var info = entry.Info;
        var archive = entry.Archive;
        string detail = L.T("TextureWindow.Detail", entry.FormatText, Format.Bytes(info.UnpackedSize), Format.Bytes(info.PackedSize))
            + (info.IsCube ? L.T("TextureWindow.CubeFaces") : info.Slices > 1 ? L.T("TextureWindow.ArrayOf", info.Slices) : "");
        string name = Path.GetFileName(entry.Path);
        new TextureWindow(entry.Path, info is { Slices: > 1, IsCube: false } ? $"{name}.texture_{slice}" : name, detail, info, () =>
        {
            using var fs = new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TextureCacheArchive.ReadPixels(fs, info);
        }, null, info.Mips, CacheFacts(entry), resource, locator, slice, archive.FilePath).Standalone(owner).Show();
    }

    /// <summary>
    /// A texture's resource file for its properties, and where they came from: REDkit's own copy before it's cooked, for
    /// one of the game's own textures when REDkit has one, as it keeps the editor's properties that cooking leaves out;
    /// otherwise the file itself, which <paramref name="read"/> reads. <paramref name="origin"/> is the cache the texture
    /// was opened from. Run off the UI thread.
    /// </summary>
    public static (Cr2wFile? File, PropertySource Source, string? RedkitPath) ReadProperties(string gamePath, string? origin, Func<byte[]?>? read)
    {
        var from = RedkitSource.For(gamePath, origin);
        if (from.Redkit is not null) return (from.Redkit, PropertySource.Redkit, from.RedkitPath);
        var file = read is null ? null : RedkitSource.Parse(read);
        return file is null ? (null, PropertySource.None, null) : (file, from.Source, from.RedkitPath);
    }

    /// <summary>What a texture cache says about a texture, for the top of the properties.</summary>
    public static List<(string Name, string Value)> CacheFacts(TextureEntry entry)
    {
        var info = entry.Info;
        var facts = new List<(string, string)>
        {
            (L.T("Common.Format"), info.FormatName),
            (L.T("Common.Size"), $"{info.Width} x {info.Height}"),
            (L.T("TextureWindow.MipLevels"), $"{info.Mips}"),
        };
        if (info.IsCube) facts.Add((L.T("TextureWindow.Faces"), L.T("TextureWindow.CubeMap")));
        else if (info.Slices > 1) facts.Add((L.T("Common.Textures"), L.T("TextureWindow.TextureArray", info.Slices)));
        facts.Add((L.T("TextureWindow.Memory"), Format.Bytes(info.UnpackedSize)));
        facts.Add((L.T("TextureWindow.Cache"), Format.Bytes(info.PackedSize)));
        facts.Add((L.T("TextureWindow.Cache2"), entry.Archive.FilePath));
        return facts;
    }

    /// <summary>Whether a file's name says it's a picture this window can show: PNG, JPEG, BMP, GIF, TIFF or DDS.</summary>
    public static bool CanShow(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".dds";

    /// <summary>Opens a picture file's bytes: an ordinary image, or a .dds, which keeps its mip levels. False when it couldn't be read as one.</summary>
    public static bool Open(string name, string source, byte[] data, Window owner)
    {
        string file = Path.GetFileName(name);
        if (Path.GetExtension(name).Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            if (!Dds.TryRead(data, out var info, out var pixels) || TextureDecoder.Decode(info, pixels, 0) is null) return false;
            var facts = new List<(string, string)> { (L.T("Common.Format"), info.FormatName), (L.T("Common.Size"), $"{info.Width} x {info.Height}"), (L.T("TextureWindow.MipLevels"), $"{info.Mips}") };
            if (info.IsCube) facts.Add((L.T("TextureWindow.Faces"), L.T("TextureWindow.CubeMap")));
            else if (info.Slices > 1) facts.Add((L.T("Common.Textures"), L.T("TextureWindow.TextureArray", info.Slices)));
            facts.Add((L.T("TextureWindow.FileSize"), Format.Bytes(data.Length)));
            facts.Add((L.T("Common.From"), source));
            new TextureWindow(name, file, $"{source}   ·   {info.Description}   ·   {Format.Bytes(data.Length)}",
                info, () => pixels, null, info.Mips, facts, null, null).Standalone(owner).Show();
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
        var pictureFacts = new List<(string, string)>
        {
            (L.T("Common.Size"), $"{picture.Width} x {picture.Height}"), (L.T("TextureWindow.FileSize"), Format.Bytes(data.Length)), (L.T("Common.From"), source),
        };
        new TextureWindow(name, file, $"{source}   ·   {picture.Width}x{picture.Height}   ·   {Format.Bytes(data.Length)}", null, () => [], picture, 1,
            pictureFacts, null, null).Standalone(owner).Show();
        return true;
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        var (resource, origin, path) = (_resource, _origin, _path);
        var readResource = Task.Run(() => origin is null ? (null, PropertySource.None, null) : ReadProperties(path, origin, resource));
        try
        {
            _pixels = _picture is not null ? [] : await Task.Run(_readPixels);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            BusyText.Text = L.T("TextureWindow.TextureCouldNotRead");
            await ShowMessage(L.T("TextureWindow.TextureCouldNotRead2"), ex.Message, MessageKind.Warning);
            return;
        }
        SaveDdsButton.IsEnabled = _info is { Format: not TextureFormat.Unknown };
        Render();
        if (origin is not null)
        {
            var (file, source, redkitPath) = await readResource;
            SourceNoteBar.Show(source, redkitPath, _info is { IsCube: true } ? L.T("TextureWindow.CubeMap2") : L.T("TextureWindow.Texture3"));
            ShowProperties(file);
        }
    }

    /// <summary>
    /// The properties: what the cache or file says about the texture, then its resource's own properties as REDkit lists
    /// them, under each class from CResource down, with those the file doesn't keep dimmed.
    /// </summary>
    private void ShowProperties(Cr2wFile? resource)
    {
        var rows = new List<PropertyRow>();
        var heading = PropertyRow.Child(rows, new PropertyRow(L.T("TextureWindow.Texture"), "", "", 0, null, heading: true), null);
        foreach (var (name, value) in _facts) PropertyRow.Child(rows, new PropertyRow(name, "", value, 1, null), heading);
        if (resource is not null && resource.Exports.Count > 0)
            rows.AddRange(PropertyRow.Of(resource.Object(0), resource));
        Properties.Show(rows);
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    private int Level => Math.Max(0, MipBox.SelectedIndex);

    private int Slice => Math.Max(0, SliceBox.SelectedIndex);

    private bool Is3D => View3D.IsChecked == true;

    private void Render()
    {
        if (_pixels is null) return;
        _image = _picture ?? TextureDecoder.Decode(_info!, _pixels, Level, Slice);
        if (_image is null)
        {
            Picture.Source = null;
            Preview3D.ShowTexture(null);
            BusyText.Visibility = Visibility.Visible;
            BusyText.Text = L.T("TextureWindow.TextureFormatCantShown", _info?.RawFormat);
            SavePngButton.IsEnabled = false;
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        SavePngButton.IsEnabled = true;
        if (Is3D)
        {
            // a cube map goes round the shape whole, each face where it looks
            if (_info is { IsCube: true })
                Preview3D.ShowCube([.. Enumerable.Range(0, 6).Select(face => Bitmap(TextureDecoder.Decode(_info, _pixels, Level, face)!))]);
            else Preview3D.ShowTexture(Bitmap(_image));
            return;
        }
        Picture.Source = Bitmap(_image);
        ApplyZoom();
    }

    private BitmapSource Bitmap(DecodedImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, Channels(image), image.Width * 4);
        bitmap.Freeze();
        return bitmap;
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

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        Preview3D.Visibility = Is3D ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = Is3D ? Visibility.Collapsed : Visibility.Visible;
        ShapeBar.Visibility = Is3D ? Visibility.Visible : Visibility.Collapsed;
        FitToggle.Visibility = Is3D ? Visibility.Collapsed : Visibility.Visible;
        Render();
    }

    private void Shape_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Preview3D is null) return;
        Preview3D.Shape = (PreviewShape)Math.Max(0, ShapeBox.SelectedIndex);
    }

    private void Light_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Preview3D is not null) Preview3D.LightAngle = e.NewValue;
    }

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

    // the buttons on the right keep only their icons (their tooltips say what they do) when both sides won't fit
    private void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TextBlock[] labels = [ReplaceLabel, SavePngLabel, SaveDdsLabel];
        foreach (var label in labels) label.Visibility = Visibility.Visible;
        RightTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool narrow = LeftTools.DesiredSize.Width + RightTools.DesiredSize.Width + 12 > Toolbar.ActualWidth;
        foreach (var label in labels) label.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
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
        var dialog = new SaveFileDialog { Title = L.T("TextureWindow.SavePicture"), FileName = BaseName + (_info is { Slices: > 1 } ? $"_{Slice}" : "") + (Level > 0 ? $"_mip{Level}" : "") + ".png", Filter = L.T("TextureWindow.PNGImagePngPng") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Bitmap(_image)));
            FileSafety.CheckWrite(dialog.FileName);
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessage(L.T("TextureWindow.PictureCouldNotSaved"), ex.Message, MessageKind.Warning);
        }
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (TextureReplacer.Pick(this, _path) is { } picture) await ReplaceWith(picture);
    }

    /// <summary>Replaces the texture in the project with a picture, and shows what Build will make of it.</summary>
    private async Task ReplaceWith(string picture)
    {
        ReplaceButton.IsEnabled = false;
        try { await TextureReplacer.ReplaceAsync(this, Message, _path, _resource, picture); }
        finally { ReplaceButton.IsEnabled = true; }
    }

    private void SaveDds_Click(object sender, RoutedEventArgs e)
    {
        if (_pixels is null || _info is null) return;
        var dialog = new SaveFileDialog { Title = L.T("TextureWindow.SaveTexture"), FileName = BaseName + ".dds", Filter = L.T("TextureWindow.DirectDrawSurfaceDdsDds") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            File.WriteAllBytes(dialog.FileName, Dds.Wrap(_info!, _pixels));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            _ = ShowMessage(L.T("TextureWindow.TextureCouldNotSaved"), ex.Message, MessageKind.Warning);
        }
    }
}
