using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace W3ModWorkbench;

/// <summary>
/// Shows files in File Explorer the way Windows itself does ("Open file location"): their folder opens, or the window
/// already showing it comes forward, with the files selected. Files in several folders get a window each.
/// </summary>
public static class ShellReveal
{
    /// <summary>Opens a folder in File Explorer.</summary>
    public static void Open(string folder)
    {
        if (!Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Selects the files (or folders) in File Explorer. Ones that don't exist are left out; false when none do.</summary>
    public static bool Show(IEnumerable<string> paths)
    {
        var existing = paths.Select(Path.GetFullPath).Where(p => File.Exists(p) || Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return false;
        foreach (var group in existing.GroupBy(p => Path.GetDirectoryName(p) ?? p, StringComparer.OrdinalIgnoreCase).Take(8))
            if (!Select(group.Key, [.. group]))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{group.First()}\"") { UseShellExecute = true })?.Dispose();
        return true;
    }

    private static bool Select(string folder, IReadOnlyList<string> files)
    {
        IntPtr folderId = IntPtr.Zero;
        var fileIds = new List<IntPtr>(files.Count);
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderId, 0, out _) != 0 || folderId == IntPtr.Zero) return false;
            foreach (string file in files)
                if (SHParseDisplayName(file, IntPtr.Zero, out var id, 0, out _) == 0 && id != IntPtr.Zero) fileIds.Add(id);
            return fileIds.Count > 0 && SHOpenFolderAndSelectItems(folderId, (uint)fileIds.Count, [.. fileIds], 0) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return false;
        }
        finally
        {
            foreach (var id in fileIds) CoTaskMemFree(id);
            if (folderId != IntPtr.Zero) CoTaskMemFree(folderId);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);
}
