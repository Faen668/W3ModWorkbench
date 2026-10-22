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
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>What the file list's right-click menu does with the highlighted files: copy, show in Explorer, compare, export.</summary>
public partial class MainWindow
{
    // ---------------------------------------------------------------- reading

    /// <summary>A file's bytes, read off the UI thread.</summary>
    internal static Task<byte[]> ReadAsync(ArchiveEntry entry) => Task.Run(() =>
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
            SetStatus(list.Count == 1 ? L.T("MainWindow.Copied", list[0]) : L.T("MainWindow.Copied2", list.Count, what));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            SetStatus(L.T("MainWindow.ClipboardUseAnotherProgram"));
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
            ShowError(L.T("MainWindow.IsntDiskAnyMore"));
    }

    private void GridRevealExtracted_Click(object sender, RoutedEventArgs e)
    {
        var paths = Highlighted().Select(i => _extracted.GetValueOrDefault(i.Entry)).OfType<string>().ToList();
        if (paths.Count > 0 && !ShellReveal.Show(paths))
            ShowError(L.T("MainWindow.ExtractedCopyIsntThere"));
    }

    private void TreeReveal_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNode node) return;
        while (node.Parent is not null) node = node.Parent;
        var archives = node.IsGroup ? node.Children.Select(c => c.Archive) : [node.Archive];
        if (!ShellReveal.Show(archives.Select(a => a.FilePath)))
            ShowError(L.T("MainWindow.IsntDiskAnyMore2"));
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
            ShowError(L.T("MainWindow.FilesCouldNotRead", ex.Message));
            return;
        }

        // CR2W files, strings, compiled scripts and textures in the compare window; text below, in the preview
        if (await FileCompare.TryShowAsync(this, Compared(older, a), Compared(newer, b))) return;

        if (older.IsPreviewable && newer.IsPreviewable)
        {
            await PreviewWindow.ShowComparedAsync(PreviewItemFor(newer.Entry, b), PreviewItemFor(older.Entry, a), this);
            return;
        }

        int first = a.AsSpan().CommonPrefixLength(b);
        string sizes = a.Length == b.Length ? L.T("MainWindow.Both", Format.Bytes(a.Length)) : L.T("MainWindow.Has2", older.BundleName, Format.Bytes(a.Length), newer.BundleName, Format.Bytes(b.Length));
        if (first == a.Length && a.Length == b.Length)
            await ShowMessage(L.T("MainWindow.Same", older.Name, newer.Name), L.T("MainWindow.EveryByteMatches", sizes), MessageKind.Info);
        else
            await ShowMessage(L.T("MainWindow.Different", older.Name, newer.Name),
                L.T("MainWindow.TheyFirstDifferByte", sizes, first),
                MessageKind.Info);
    }

    /// <summary>A file of the list as one side of a comparison: a texture's pixels from its cache, or the cache beside its .xbm.</summary>
    private ComparedFile Compared(EntryItem item, byte[] data)
    {
        Func<TextureEntry?>? texture = null;
        if (item.Entry is TextureEntry own) texture = () => own;
        else if (FileCompare.IsTexture(item.Path))
            texture = () => new Core.Cr2w.ResourceLocator(OpenArchives(), item.Entry.Archive, GameLocator.Resolve(null)).FindTexture(item.Path);
        return new ComparedFile(item.BundleName, item.Path, data, texture);
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
            ShowError(L.T("MainWindow.IsntDiskAnyMore2"));
    }

    /// <summary>A SHA-256 of a file's contents, to tell copies apart.</summary>
    public static async Task<string> HashAsync(ArchiveEntry entry) => Convert.ToHexString(SHA256.HashData(await ReadAsync(entry)));

    // ---------------------------------------------------------------- export

    private void GridExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var highlighted = Highlighted();
        var items = highlighted.Count > 1 ? highlighted : _shown;
        if (items.Count == 0) return;
        string name = FolderTree.SelectedItem is FolderNode node ? node.DisplayName : L.T("Common.Files");
        var dialog = new SaveFileDialog
        {
            Title = items == highlighted ? L.T("MainWindow.SaveHighlightedFilesList") : L.T("MainWindow.SaveFilesShownList"),
            Filter = L.T("MainWindow.CSVExcelOtherSpreadsheets"),
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
            FileSafety.CheckWrite(dialog.FileName);
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            SetStatus(L.T("MainWindow.SavedList", L.T("Count.File", items.Count), dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(L.T("MainWindow.ListCouldNotSaved", ex.Message));
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
        GridRevealItem.Header = loose ? L.T("Common.ShowExplorer") : items.Select(i => i.Entry.Archive).Distinct().Count() > 1 ? L.T("MainWindow.ShowTheirArchivesExplorer") : L.T("MainWindow.ShowExplorer", ArchiveWord(items.FirstOrDefault()));
    }

    private static string ArchiveWord(EntryItem? item) => item?.Entry.Archive switch
    {
        TextureCacheArchive => L.T("MainWindow.TextureCache"),
        ScriptArchive => L.T("MainWindow.CompiledScript"),
        null => "it",
        _ => L.T("MainWindow.Bundle"),
    };
}
