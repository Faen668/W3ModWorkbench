using System.IO;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>A loose .ws file in a <see cref="ScriptFolder"/>, read straight from disk and saved back to it.</summary>
public sealed class LooseScriptEntry : ArchiveEntry
{
    public required string FullPath { get; init; }
    public override string FormatText => "Script";
    public override byte[] Read(Stream? archiveStream) => File.ReadAllBytes(FullPath);
}

/// <summary>
/// A folder of loose WitcherScript files: a script project, such as a REDkit project's workspace\scripts, a mod's
/// content\scripts or a decompiled blob saved to edit. Listed like an archive; its files can be edited and saved,
/// and the whole folder compiled into a .rsblob.
/// </summary>
public sealed class ScriptFolder : IArchive
{
    /// <summary>Folder names skipped when naming a project after the folders it's in.</summary>
    private static readonly HashSet<string> Plain = new(StringComparer.OrdinalIgnoreCase) { "scripts", "content", "workspace", "local" };

    public string FilePath { get; }
    public string Name => Path.GetFileName(FilePath);
    public string Owner { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => false;

    private ScriptFolder(string folder, List<ArchiveEntry> entries)
    {
        FilePath = folder;
        Owner = ProjectName(folder);
        Entries = entries;
    }

    public static ScriptFolder Open(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\');
        var entries = new List<ArchiveEntry>();
        var archive = new ScriptFolder(folder, entries);
        string name = Path.GetFileName(folder);
        foreach (string path in Directory.EnumerateFiles(folder, "*.ws", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            int size = (int)Math.Min(int.MaxValue, new FileInfo(path).Length);
            entries.Add(new LooseScriptEntry
            {
                Archive = archive,
                // under the folder's own name, so a scripts folder lists as scripts\..., like a decompiled blob
                Path = Path.Combine(name, Path.GetRelativePath(folder, path)),
                Size = size,
                PackedSize = size,
                FullPath = path,
            });
        }
        return archive;
    }

    /// <summary>A folder with no files listed, to stand for it as the archive that scripts opened from it belong to.</summary>
    public static ScriptFolder Empty(string folder) => new(Path.GetFullPath(folder).TrimEnd('\\'), []);

    /// <summary>The first folder up from <paramref name="folder"/> that isn't just scripts, content, workspace or local.</summary>
    private static string ProjectName(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            if (!Plain.Contains(dir.Name)) return dir.Name;
        return Path.GetFileName(folder);
    }

    /// <summary>
    /// The project a .ws file belongs to: its nearest folder called scripts, no higher than <paramref name="within"/>,
    /// or the folder it was opened from when there's none.
    /// </summary>
    public static string RootOf(string file, string within)
    {
        within = Path.GetFullPath(within).TrimEnd('\\');
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(file))!); dir is not null; dir = dir.Parent)
        {
            if (dir.Name.Equals("scripts", StringComparison.OrdinalIgnoreCase)) return dir.FullName;
            if (dir.FullName.Equals(within, StringComparison.OrdinalIgnoreCase)) break;
        }
        return within;
    }

    /// <summary>
    /// The game's and REDkit's own script folders (content\content0\scripts and r4data\scripts): thousands of files that
    /// go to definition reads already, and that shouldn't be edited.
    /// </summary>
    public static bool IsGameScripts(string folder)
    {
        string parent = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\')) ?? "");
        return parent.Equals("content0", StringComparison.OrdinalIgnoreCase) || parent.Equals("r4data", StringComparison.OrdinalIgnoreCase);
    }
}
