using System.IO;
using System.IO.Compression;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.LoadOrder;

/// <summary>
/// A mod as it's downloaded, not installed yet: a folder or .zip that holds one or more mod folders (modName or
/// dlcName, each with a content folder, often under mods\ and dlc\) and the menu XML that goes in the game's
/// bin\config\r4game\user_config_matrix\pc. The load order analyser checks it against the mods installed.
/// </summary>
public sealed class ModDownload : IDisposable
{
    public required string Source { get; init; }

    /// <summary>The folder the mods were found in: the one picked, or where a .zip was unpacked.</summary>
    public required string Root { get; init; }

    public List<string> Mods { get; } = [];
    public List<string> Menus { get; } = [];

    /// <summary>The folder a .zip was unpacked to, deleted by <see cref="Dispose"/>.</summary>
    public string? Unpacked { get; init; }

    public string Name => Path.GetFileNameWithoutExtension(Source.TrimEnd('\\', '/'));

    /// <summary>Reads a download: a folder, or a .zip that's unpacked to a temporary folder first.</summary>
    /// <exception cref="InvalidDataException">There's no mod in it, or the .zip can't be read.</exception>
    public static ModDownload Open(string path)
    {
        string root = path;
        string? unpacked = null;
        if (File.Exists(path))
        {
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("ModDownload.UnpackFirst", Path.GetFileName(path)));
            unpacked = Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "check", $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid().ToString("N")[..8]}");
            try { ZipFile.ExtractToDirectory(path, unpacked, overwriteFiles: true); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                TryDelete(unpacked);
                throw new InvalidDataException(L.T("ModDownload.ZipCouldntRead", Path.GetFileName(path), ex.Message), ex);
            }
            root = unpacked;
        }
        var download = new ModDownload { Source = path, Root = root, Unpacked = unpacked };
        Find(download, root, 0);
        if (download.Mods.Count == 0)
        {
            download.Dispose();
            throw new InvalidDataException(L.T("ModDownload.NoMod", Path.GetFileName(path.TrimEnd('\\', '/'))));
        }
        return download;
    }

    private static void Find(ModDownload download, string folder, int depth)
    {
        if (depth > 6) return;
        string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        string parent = Path.GetFileName(Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? "");
        bool hasContent = Directory.Exists(Path.Combine(folder, "content"));
        // a mod folder: modName or dlcName with a content folder, or any folder with one under mods\ or dlc\
        if (hasContent && (name.StartsWith("mod", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dlc", StringComparison.OrdinalIgnoreCase)
                           || parent.Equals("mods", StringComparison.OrdinalIgnoreCase) || parent.Equals("dlc", StringComparison.OrdinalIgnoreCase)))
        {
            download.Mods.Add(folder);
            return;
        }
        if (name.Equals("pc", StringComparison.OrdinalIgnoreCase) && parent.Equals("user_config_matrix", StringComparison.OrdinalIgnoreCase))
        {
            download.Menus.AddRange(Directory.EnumerateFiles(folder, "*.xml").Order(StringComparer.OrdinalIgnoreCase));
            return;
        }
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (string child in children) Find(download, child, depth + 1);
    }

    public void Dispose()
    {
        if (Unpacked is not null) TryDelete(Unpacked);
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temporary folder left behind is cleared with the rest of the temp folder
        }
    }
}
