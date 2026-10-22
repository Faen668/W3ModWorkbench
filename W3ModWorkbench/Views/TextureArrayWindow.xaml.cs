using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>One texture in a texture array: its name as REDkit gives it (texture_0, texture_1…) and a small picture.</summary>
public sealed record ArraySliceItem(int Index, BitmapSource? Thumbnail)
{
    public string Name => $"texture_{Index}";
}

/// <summary>
/// A texture array (.texarray), as REDkit's texture array editor shows it: every texture in it in a row, the one picked
/// large, and the array's properties beside it. Double-click a texture to open it in the texture viewer, as REDkit does.
/// </summary>
public partial class TextureArrayWindow : Window, IMessageHost
{
    private const int ThumbnailSize = 104;

    private readonly TextureEntry _entry;
    private readonly Func<byte[]?>? _resource;
    private readonly ResourceLocator _locator;
    private byte[]? _pixels;

    private TextureArrayWindow(TextureEntry entry, Func<byte[]?>? resource, ResourceLocator? locator)
    {
        InitializeComponent();
        _locator = locator ?? TextureWindow.GameLocatorOnly();
        _entry = entry;
        _resource = resource;
        var info = entry.Info;
        Title = L.T("TextureArrayWindow.TextureArray2", Path.GetFileName(entry.Path));
        SlotsText.Text = L.T("TextureArrayWindow.SlotsUsed", info.Slices);
        InfoText.Text = L.T("TextureArrayWindow.UnpackedCache", entry.Path, entry.FormatText, Format.Bytes(info.UnpackedSize), Format.Bytes(info.PackedSize));
        ShowProperties(null);
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Opens a texture array from a texture.cache. <paramref name="resource"/> reads its .texarray, for its properties.</summary>
    public static void Open(TextureEntry entry, Window owner, Func<byte[]?>? resource = null, ResourceLocator? locator = null) =>
        new TextureArrayWindow(entry, resource, locator).Standalone(owner).Show();

    private void Properties_LinkClicked(object? sender, string path) => TextureWindow.OpenLink(this, Message, _locator, path);

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        var info = _entry.Info;
        var resource = _resource;
        string path = _entry.Path;
        string origin = _entry.Archive.FilePath;
        var readResource = Task.Run(() => TextureWindow.ReadProperties(path, origin, resource));
        List<ArraySliceItem> items;
        try
        {
            string cache = _entry.Archive.FilePath;
            (_pixels, items) = await Task.Run(() =>
            {
                byte[] pixels;
                using (var fs = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    pixels = TextureCacheArchive.ReadPixels(fs, info);
                // each thumbnail from the first mip level small enough, which is quick to decode
                int level = 0;
                while (level < info.Mips - 1 && Math.Max(info.Width, info.Height) >> level > ThumbnailSize * 2) level++;
                var list = Enumerable.Range(0, info.Slices)
                    .Select(slice => new ArraySliceItem(slice, TextureDecoder.Decode(info, pixels, level, slice) is { } image ? Bitmap(image) : null))
                    .ToList();
                return (pixels, list);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            BusyText.Text = L.T("TextureArrayWindow.TexturesCouldNotRead");
            await ShowMessage(L.T("TextureArrayWindow.TextureArrayCouldNot"), ex.Message, MessageKind.Warning);
            return;
        }
        Slices.ItemsSource = items;
        bool known = info.Format != TextureFormat.Unknown;
        SavePngsButton.IsEnabled = SaveDdsButton.IsEnabled = known;
        ReplaceButton.Visibility = Visibility.Visible;
        if (!known) BusyText.Text = L.T("TextureArrayWindow.TextureFormatCantShown", info.RawFormat);
        Slices.SelectedIndex = 0;
        Slices.Focus();
        var (file, source, redkitPath) = await readResource;
        SourceNoteBar.Show(source, redkitPath, L.T("TextureArrayWindow.TextureArray3"));
        ShowProperties(file);
    }

    /// <summary>What the cache says about the array, then its .texarray's properties as REDkit lists them.</summary>
    private void ShowProperties(Cr2wFile? resource)
    {
        var rows = new List<PropertyRow>();
        var heading = PropertyRow.Child(rows, new PropertyRow(L.T("TextureArrayWindow.TextureArray"), "", "", 0, null, heading: true), null);
        foreach (var (name, value) in TextureWindow.CacheFacts(_entry)) PropertyRow.Child(rows, new PropertyRow(name, "", value, 1, null), heading);
        if (resource is not null && resource.Exports.Count > 0) rows.AddRange(PropertyRow.Of(resource.Object(0), resource));
        Properties.Show(rows);
    }

    private void PropertySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PropertySearchPlaceholder.Visibility = PropertySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Properties.Search = PropertySearch.Text;
    }

    private static BitmapSource Bitmap(DecodedImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void Slices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenButton.IsEnabled = Slices.SelectedItem is not null;
        if (_pixels is null || Slices.SelectedItem is not ArraySliceItem item) return;
        if (TextureDecoder.Decode(_entry.Info, _pixels, 0, item.Index) is not { } image)
        {
            Picture.Source = null;
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        Picture.Source = Bitmap(image);
    }

    private void OpenSelected()
    {
        if (Slices.SelectedItem is ArraySliceItem item) TextureWindow.OpenSlice(_entry, this, _resource, item.Index, _locator);
    }

    private void Slices_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Slices, e.OriginalSource as DependencyObject) is ListBoxItem) OpenSelected();
    }

    private void Slices_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenSelected();
        e.Handled = true;
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        if (e.Key == Key.Escape) Close();
    }

    // ---------------------------------------------------------------- saving

    private string BaseName => Path.GetFileNameWithoutExtension(_entry.Path);

    private void SavePngs_Click(object sender, RoutedEventArgs e)
    {
        if (_pixels is null) return;
        var dialog = new OpenFolderDialog { Title = L.T("TextureArrayWindow.SaveEveryTextureArray2") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            for (int slice = 0; slice < _entry.Info.Slices; slice++)
            {
                if (TextureDecoder.Decode(_entry.Info, _pixels, 0, slice) is not { } image) continue;
                string file = Path.Combine(dialog.FolderName, $"{BaseName}_texture_{slice}.png");
                FileSafety.CheckWrite(file);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(Bitmap(image)));
                using var stream = File.Create(file);
                encoder.Save(stream);
            }
            _ = Message.Show(null, L.T("TextureArrayWindow.SavedTextures", _entry.Info.Slices, dialog.FolderName), MessageKind.Info, autoClose: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessage(L.T("TextureArrayWindow.TexturesCouldNotSaved"), ex.Message, MessageKind.Warning);
        }
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (TextureReplacer.Pick(this, _entry.Path) is { } picture) await ReplaceWith(picture);
    }

    /// <summary>Replaces the texture in the project with a picture, and shows what Build will make of it.</summary>
    private async Task ReplaceWith(string picture)
    {
        ReplaceButton.IsEnabled = false;
        try { await TextureReplacer.ReplaceAsync(this, Message, _entry.Path, _resource, picture); }
        finally { ReplaceButton.IsEnabled = true; }
    }

    private void SaveDds_Click(object sender, RoutedEventArgs e)
    {
        if (_pixels is null) return;
        var dialog = new SaveFileDialog { Title = L.T("TextureArrayWindow.SaveTextureArray"), FileName = BaseName + ".dds", Filter = L.T("TextureArrayWindow.DirectDrawSurfaceDdsDds") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            File.WriteAllBytes(dialog.FileName, Dds.Wrap(_entry.Info, _pixels));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            _ = ShowMessage(L.T("TextureArrayWindow.TextureArrayCouldNot2"), ex.Message, MessageKind.Warning);
        }
    }
}
