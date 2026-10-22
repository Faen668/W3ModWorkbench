using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// Tools > Compare files: any two files, each a file on disk (picked, typed or dropped) or a game path read from the
/// game itself, compared as <see cref="FileCompare"/> compares them. For two files with different paths, or files
/// outside the game and the project.
/// </summary>
public partial class ComparePickWindow : Window, IMessageHost
{
    private static ComparePickWindow? _open;

    private ComparePickWindow() => InitializeComponent();

    public static void Open(Window owner)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }
        _open = new ComparePickWindow().Standalone(owner);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        CompareButton.IsEnabled = LeftBox.Text.Trim().Length > 0 && RightBox.Text.Trim().Length > 0;
        StatusText.Text = "";
    }

    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftBox);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightBox);

    private void Browse(TextBox box)
    {
        var dialog = new OpenFileDialog { Title = L.T("ComparePick.PickFile") };
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
    }

    private void Box_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: >= 1 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A file dropped on a box goes in it; two dropped at once go in both.</summary>
    private void Box_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: >= 1 } files) return;
        if (files.Length >= 2)
        {
            LeftBox.Text = files[0];
            RightBox.Text = files[1];
        }
        else ((TextBox)sender).Text = files[0];
        e.Handled = true;
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        CompareButton.IsEnabled = false;
        try
        {
            string left = LeftBox.Text.Trim().Trim('"'), right = RightBox.Text.Trim().Trim('"');
            var sides = await Task.Run(() => (Side(left), Side(right)));
            if (sides.Item1 is null || sides.Item2 is null)
            {
                StatusText.Text = L.T("ComparePick.NotFound", sides.Item1 is null ? left : right);
                return;
            }
            await FileCompare.ShowAnyAsync(this, sides.Item1, sides.Item2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await ShowMessage(L.T("CompareWindow.CouldntRead"), ex.Message, MessageKind.Warning);
        }
        finally
        {
            CompareButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// One side: a file on disk (known by its game path when it's in the project's workspace or a game folder, so a
    /// texture's pixels are found in the cache beside it), or a game path read from the game itself. Null when it's neither.
    /// </summary>
    private static ComparedFile? Side(string text)
    {
        if (File.Exists(text))
        {
            string path = ProjectWorkspace.Current?.GamePathOf(text) ?? FileSafety.GamePathOf(text) ?? Path.GetFileName(text);
            string cache = Path.Combine(Path.GetDirectoryName(text) ?? "", "texture.cache");
            return new ComparedFile(ArchiveEntry.OwnerOf(text), path, File.ReadAllBytes(text),
                FileCompare.IsTexture(path) ? () => ResourceLocator.Cache(cache) is { } c ? ResourceLocator.FindInCache(c, path) : null : null);
        }
        if (Path.IsPathRooted(text)) return null;
        string gamePath = text.Replace('/', '\\');
        var game = new ResourceLocator([], null, GameLocator.Resolve(null), otherMods: false);
        var data = game.ReadFile(gamePath);
        var texture = FileCompare.IsTexture(gamePath) ? game.FindTexture(gamePath) : null;
        if (data is null && texture is null) return null;
        return new ComparedFile(L.T("CompareWindow.Game"), gamePath, data ?? [], texture is null ? null : () => texture);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen || e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
