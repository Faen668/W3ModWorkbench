using System.IO;
using W3ModWorkbench.Core.Scripts;

namespace W3ModWorkbench.Core;

/// <summary>
/// REDkit's depot, its <c>r4data</c> folder: its own copies of the game's files from before they were cooked (meshes,
/// entities, materials, scenes, communities…) and the editor's own, about 230,000 loose files. Listed like an archive,
/// each file by its game path; each is a loose file on disk, so the viewers open it from there, as REDkit's, read-only.
/// It's found from REDkit's wcc_lite set in Settings, two folders up from <c>bin\x64_RedKit</c>.
/// </summary>
public sealed class RedkitDepot : IArchive
{
    private RedkitDepot(string folder, List<ArchiveEntry> entries)
    {
        FilePath = folder;
        Entries = entries;
    }

    /// <summary>The r4data folder.</summary>
    public string FilePath { get; }
    public string Name => "r4data";
    public string Owner => "REDkit";
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => false;

    /// <summary>REDkit's r4data from the wcc_lite in Settings, or null when it isn't set or isn't beside it.</summary>
    public static string? FolderFromSettings()
    {
        string wcc = App.Settings.WccLitePath;
        if (string.IsNullOrWhiteSpace(wcc) || Path.GetDirectoryName(wcc) is not { } bin) return null;
        string folder = Path.GetFullPath(Path.Combine(bin, "..", "..", "r4data"));
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>Lists every file in r4data, telling <paramref name="progress"/> each top folder as it's read.</summary>
    public static RedkitDepot Open(string folder, Action<string>? progress = null, CancellationToken token = default)
    {
        var entries = new List<ArchiveEntry>(240_000);
        var depot = new RedkitDepot(folder, entries);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        void Add(FileInfo file, string path)
        {
            int size = (int)Math.Min(int.MaxValue, file.Length);
            entries.Add(new LooseScriptEntry { Archive = depot, Path = path, Size = size, PackedSize = size, FullPath = file.FullName });
        }
        var root = new DirectoryInfo(folder);
        foreach (var file in root.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true })) Add(file, file.Name);
        foreach (var top in root.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }))
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(top.Name);
            foreach (var file in top.EnumerateFiles("*", options))
                Add(file, Path.GetRelativePath(folder, file.FullName));
        }
        return depot;
    }
}
