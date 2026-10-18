using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.ViewModels;

namespace W3ModWorkbench;

/// <summary>What the file list's right-click menu does with the highlighted files: copy, show in Explorer, compare, export.</summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- reading

    /// <summary>A file's bytes, read off the UI thread.</summary>
    private static Task<byte[]> ReadAsync(ArchiveEntry entry) => Task.Run(() =>
    {
        if (!entry.Archive.ReadsFromDisk) return entry.Read(null);
        using var fs = new FileStream(entry.Archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return entry.Read(fs);
    });

    /// <summary>Where a file is on disk: a loose file itself, otherwise the bundle, compiled script or cache it's in.</summary>
    private static string DiskLocation(ArchiveEntry entry) => entry switch
    {
        LooseScriptEntry loose => loose.FullPath,
        StringsEntry strings => strings.FullPath,
        _ => entry.Archive.FilePath,
    };

    // ---------------------------------------------------------------- copying

    private void CopyLines(IEnumerable<string> lines, string what)
    {
        var list = lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, list));
            SetStatus(list.Count == 1 ? $"Copied {list[0]}" : $"Copied {list.Count:N0} {what}");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus("The clipboard is in use by another program. Try again.");
        }
    }

    private void GridCopyName_Click(object sender, RoutedEventArgs e) => CopyLines(Highlighted().Select(i => i.Name), "names");

    private void GridCopyFolder_Click(object sender, RoutedEventArgs e) => CopyLines(Highlighted().Select(i => i.Folder), "folders");

    private void GridCopyLocation_Click(object sender, RoutedEventArgs e) => CopyLines(Highlighted().Select(i => DiskLocation(i.Entry)), "locations");

    // ---------------------------------------------------------------- Explorer

    private void GridReveal_Click(object sender, RoutedEventArgs e) => RevealHighlighted();

    private void RevealHighlighted()
    {
        var items = Highlighted();
        if (items.Count == 0) return;
        if (!ShellReveal.Show(items.Select(i => DiskLocation(i.Entry))))
            ShowError("It isn't on disk any more.\n\nThe file or the archive it's in was moved or deleted after it was opened.");
    }

    private void GridRevealExtracted_Click(object sender, RoutedEventArgs e)
    {
        var paths = Highlighted().Select(i => _extracted.GetValueOrDefault(i.Entry)).OfType<string>().ToList();
        if (paths.Count > 0 && !ShellReveal.Show(paths))
            ShowError("The extracted copy isn't there any more.\n\nIt was moved or deleted after it was extracted. Extract it again to get a new one.");
    }

    private void TreeReveal_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode node) return;
        while (node.Parent is not null) node = node.Parent;
        var archives = node.IsGroup ? node.Children.Select(c => c.Archive) : [node.Archive];
        if (!ShellReveal.Show(archives.Select(a => a.FilePath)))
            ShowError("It isn't on disk any more.\n\nIt was moved or deleted after it was opened.");
    }

    // ---------------------------------------------------------------- comparing

    private void GridCompare_Click(object sender, RoutedEventArgs e) => _ = CompareHighlightedAsync();

    private async Task CompareHighlightedAsync()
    {
        var items = Highlighted();
        if (items.Count != 2) return;
        // the one higher in the list is the older version, on the left
        items.Sort((a, b) => _shown.IndexOf(a).CompareTo(_shown.IndexOf(b)));
        await CompareAsync(items[0], items[1]);
    }

    /// <summary>
    /// Compares two files. Text opens side by side in the preview with the differences marked; anything else is compared
    /// byte for byte, and a message says whether they're the same and where they first differ.
    /// </summary>
    public async Task CompareAsync(EntryItem older, EntryItem newer)
    {
        byte[] a, b;
        try
        {
            a = await ReadAsync(older.Entry);
            b = await ReadAsync(newer.Entry);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowError($"The files could not be read.\n\n{ex.Message}");
            return;
        }

        if (older.IsPreviewable && newer.IsPreviewable)
        {
            await PreviewWindow.ShowComparedAsync(PreviewItemFor(newer.Entry, b), PreviewItemFor(older.Entry, a), this);
            return;
        }

        int first = a.AsSpan().CommonPrefixLength(b);
        string sizes = a.Length == b.Length ? $"Both are {Format.Bytes(a.Length)}." : $"{older.BundleName} has {Format.Bytes(a.Length)} and {newer.BundleName} {Format.Bytes(b.Length)}.";
        if (first == a.Length && a.Length == b.Length)
            await ShowMessage($"{older.Name} and {newer.Name} are the same", $"{sizes} Every byte matches.", MessageKind.Info);
        else
            await ShowMessage($"{older.Name} and {newer.Name} are different",
                $"{sizes} They first differ at byte {first:N0} (0x{first:X}).\n\nThey aren't text, so the differences can't be shown line by line. Extract both to look at them in a tool for this kind of file.",
                MessageKind.Info);
    }

    // ---------------------------------------------------------------- other copies

    private void GridCopies_Click(object sender, RoutedEventArgs e) => ShowOtherCopies();

    private void ShowOtherCopies()
    {
        if (Highlighted().FirstOrDefault() is not { } item) return;
        // a texture in a cache and an .xbm in a bundle share a path but aren't copies of each other
        bool texture = item.Entry is TextureEntry;
        var copies = _roots.SelectMany(r => r.AllFiles())
            .Where(i => (i.Entry is TextureEntry) == texture && i.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        OtherCopiesWindow.Open(this, item, copies);
    }

    /// <summary>Opens a file the way double-clicking it in the list does.</summary>
    public void Open(EntryItem item) => OpenItem(item);

    /// <summary>Shows a file's bundle (or the file itself) in File Explorer.</summary>
    public void Reveal(EntryItem item)
    {
        if (!ShellReveal.Show([DiskLocation(item.Entry)]))
            ShowError("It isn't on disk any more.\n\nIt was moved or deleted after it was opened.");
    }

    /// <summary>A SHA-256 of a file's contents, to tell copies apart.</summary>
    public static async Task<string> HashAsync(ArchiveEntry entry) => Convert.ToHexString(SHA256.HashData(await ReadAsync(entry)));

    // ---------------------------------------------------------------- export

    private void GridExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var highlighted = Highlighted();
        var items = highlighted.Count > 1 ? highlighted : _shown;
        if (items.Count == 0) return;
        string name = FolderTree.SelectedItem is FolderNode node ? node.DisplayName : "Files";
        var dialog = new SaveFileDialog
        {
            Title = items == highlighted ? "Save the highlighted files as a list" : "Save the files shown as a list",
            Filter = "CSV, for Excel and other spreadsheets (*.csv)|*.csv",
            FileName = string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".csv",
            InitialDirectory = Directory.Exists(OutputBox.Text.Trim()) ? OutputBox.Text.Trim() : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var csv = new StringBuilder("Name,Path,Folder,Kind,Size,Packed,Format,Mod,Archive,Location\r\n");
            foreach (var item in items)
            {
                string[] fields = [item.Name, item.Path, item.Folder, item.Kind?.Name ?? "", item.Size.ToString(), item.PackedSize.ToString(),
                    item.CompressionText, item.Entry.Archive.Owner, item.Entry.Archive.Name, DiskLocation(item.Entry)];
                csv.Append(string.Join(",", fields.Select(Quote))).Append("\r\n");
            }
            // with a BOM, so Excel reads accented names correctly
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            SetStatus($"Saved a list of {Format.Count(items.Count, "file")} to {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The list could not be saved.\n\n{ex.Message}");
        }

        static string Quote(string field) =>
            field.IndexOfAny([',', '"', '\n', '\r']) >= 0 || field.StartsWith(' ') ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }

    // ---------------------------------------------------------------- menu

    /// <summary>Shows what applies to the highlighted files.</summary>
    private void UpdateFileMenu()
    {
        var items = Highlighted();
        GridCompareItem.Visibility = items.Count == 2 ? Visibility.Visible : Visibility.Collapsed;
        GridCopiesItem.Visibility = items.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        GridRevealExtractedItem.Visibility = items.Any(i => _extracted.ContainsKey(i.Entry)) ? Visibility.Visible : Visibility.Collapsed;
        bool loose = items.Count > 0 && items.All(i => i.Entry is LooseScriptEntry or StringsEntry);
        GridRevealItem.Header = loose ? "Show in Explorer" : items.Select(i => i.Entry.Archive).Distinct().Count() > 1 ? "Show their archives in Explorer" : $"Show {ArchiveWord(items.FirstOrDefault())} in Explorer";
    }

    private static string ArchiveWord(EntryItem? item) => item?.Entry.Archive switch
    {
        TextureCacheArchive => "the texture cache",
        ScriptArchive => "the compiled script",
        null => "it",
        _ => "the bundle",
    };
}
