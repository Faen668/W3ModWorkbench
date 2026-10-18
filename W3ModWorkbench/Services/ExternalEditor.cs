using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace W3ModWorkbench;

/// <summary>
/// Opens a file in whatever Windows opens its type with. A file on disk (a project's script, a strings file) opens
/// as it is; a file inside a bundle or a compiled blob is written to a read-only copy in the temp folder first.
/// </summary>
public static class ExternalEditor
{
    private const int NoAssociation = 1155; // ERROR_NO_ASSOCIATION

    private static string CopiesFolder => Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "External");

    /// <summary>Opens the file with its default app, or Windows' Open with list when its type has none.</summary>
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NoAssociation)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "openas" })?.Dispose();
        }
    }

    /// <summary>Writes a file read from an archive to a read-only copy under the temp folder and opens that.</summary>
    /// <param name="source">The archive it's from, so files of the same name in different mods keep apart.</param>
    public static string OpenCopy(string source, string path, byte[] data)
    {
        string file = Path.Combine(CopiesFolder, Safe(source), Safe(path));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
        File.WriteAllBytes(file, data);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        Open(file);
        return file;
    }

    private static string Safe(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = path.Split('\\', '/').Where(p => p.Length > 0 && p != "." && p != "..")
            .Select(p => new string([.. p.Select(c => invalid.Contains(c) ? '_' : c)]));
        return Path.Combine([.. parts]);
    }
}
