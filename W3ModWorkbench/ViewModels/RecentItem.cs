using System.IO;

namespace W3ModWorkbench.ViewModels;

/// <summary>A folder or file opened before, in the Recent menu and on the start screen.</summary>
public sealed class RecentItem(string path)
{
    public string Path => path;

    public bool IsFolder => Directory.Exists(path);

    /// <summary>The folder's or file's name. A bundle is named by its mod, since most are blob0.bundle.</summary>
    public string Name
    {
        get
        {
            string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (IsFolder || name.Length == 0) return name.Length > 0 ? name : path;
            string owner = Core.ArchiveEntry.OwnerOf(path);
            return owner.Length > 0 && !owner.Equals(name, StringComparison.OrdinalIgnoreCase) ? $"{owner}  ·  {name}" : name;
        }
    }

    /// <summary>Where it is: the folder it's in.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? "";

    public string Glyph => IsFolder ? "" : System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".bundle" => "",
        ".rsblob" => "",
        ".cache" => "",
        ".ws" => "",
        ".w3strings" or ".csv" => "",
        ".xml" => "",
        _ => "",
    };

    public bool Exists => File.Exists(path) || Directory.Exists(path);

    private const int Max = 12;

    /// <summary>Puts paths at the top of the list, newest first, keeping at most a dozen.</summary>
    public static void Remember(List<string> list, IEnumerable<string> paths)
    {
        foreach (string raw in paths.Reverse())
        {
            string full;
            try { full = System.IO.Path.GetFullPath(raw); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            list.RemoveAll(p => p.Equals(full, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, full);
        }
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
    }
}
