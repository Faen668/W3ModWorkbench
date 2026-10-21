using System.IO;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>A loose file in a <see cref="ScriptFolder"/> (a .ws script, or one of a project's other files), read straight from disk and saved back to it.</summary>
public sealed class LooseScriptEntry : ArchiveEntry
{
    public required string FullPath { get; init; }
    public bool IsScript => System.IO.Path.GetExtension(FullPath).Equals(".ws", StringComparison.OrdinalIgnoreCase);
    public override string FormatText => IsScript ? L.T("ScriptFolder.Script") : System.IO.Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : L.T("Common.File");
    public override byte[] Read(Stream? archiveStream) => File.ReadAllBytes(FullPath);
}

/// <summary>
/// A folder of loose WitcherScript files, listed like an archive. Opened from a <see cref="WorkbenchProject"/> it is the
/// project's files: its scripts folder and its other files, which can be edited, saved and compiled into a .rsblob.
/// Opened on its own it is just loose scripts: readable, and editable once they're added to a project.
/// </summary>
public sealed class ScriptFolder : IArchive
{
    /// <summary>Folder names skipped when naming a project after the folders it's in.</summary>
    private static readonly HashSet<string> Plain = new(StringComparer.OrdinalIgnoreCase) { "scripts", "content", "workspace", "local" };

    /// <summary>The scripts folder.</summary>
    public string FilePath { get; }
    public string Name => Project is null ? Path.GetFileName(FilePath) : Path.GetFileName(Project.FilePath);
    public string Owner { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public bool ReadsFromDisk => false;

    /// <summary>The project this is the files of, or null for loose scripts opened without one.</summary>
    public WorkbenchProject? Project { get; }

    private ScriptFolder(string folder, List<ArchiveEntry> entries, WorkbenchProject? project = null)
    {
        FilePath = folder;
        Project = project;
        Owner = project?.Name ?? ProjectName(folder);
        Entries = entries;
    }

    /// <summary>
    /// The project's files: every script in its scripts folder, then its other files. Paths start at the project's
    /// folder, so they read the same as the mod's own layout (<c>scripts\game\player.ws</c>, <c>menu\settings.xml</c>).
    /// </summary>
    public static ScriptFolder OpenProject(WorkbenchProject project)
    {
        string scripts = project.ScriptsPath.TrimEnd('\\');
        var entries = new List<ArchiveEntry>();
        var archive = new ScriptFolder(scripts, entries, project);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (!seen.Add(path)) return;
            int size;
            try { size = (int)Math.Min(int.MaxValue, new FileInfo(path).Length); }
            catch (IOException) { return; }
            string relative = Path.GetRelativePath(project.Root, path);
            entries.Add(new LooseScriptEntry
            {
                Archive = archive,
                Path = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.Combine("external", Path.GetFileName(path)) : relative,
                Size = size,
                PackedSize = size,
                FullPath = path,
            });
        }
        if (Directory.Exists(scripts))
            foreach (string path in Directory.EnumerateFiles(scripts, "*.ws", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                Add(path);
        foreach (string path in project.OtherFiles()) Add(path);
        return archive;
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

    /// <summary>The name a mod's folder gives to loose scripts found in it, such as when a project is made from them.</summary>
    public static string SuggestedName(string scriptsFolder) => ProjectName(Path.GetFullPath(scriptsFolder).TrimEnd('\\'));

    /// <summary>
    /// The folder a project for loose scripts would live in: the folder above <paramref name="scriptsFolder"/> that isn't
    /// just scripts, content, workspace or local, which is the mod's own folder.
    /// </summary>
    public static string SuggestedRoot(string scriptsFolder)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(scriptsFolder).TrimEnd('\\')); dir is not null; dir = dir.Parent)
            if (!Plain.Contains(dir.Name)) return dir.FullName;
        return Path.GetFullPath(scriptsFolder);
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
