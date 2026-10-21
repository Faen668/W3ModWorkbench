using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Projects;

/// <summary>
/// A mod project: a <c>.w3bench</c> file in the mod's folder that says which scripts folder is the mod's, which other
/// files belong to it (XML, strings, anything), and where its compiled scripts go. Paths are kept relative to the
/// project file, with forward slashes, so a project can be moved, shared or kept in git.
/// </summary>
public sealed class WorkbenchProject
{
    public const string Extension = ".w3bench";
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Folders that are never listed as part of a project when a whole folder is added.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", ".svn", "obj", "bin" };

    public int Format { get; set; } = CurrentFormat;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0";

    /// <summary>The mod's scripts folder, relative to the project file. Everything in it is part of the project and is what Compile builds.</summary>
    public string ScriptsFolder { get; set; } = "scripts";

    /// <summary>
    /// Whether Build puts the scripts in the mod's <c>content\scripts</c> as they are, as the mod's source beside the
    /// compiled <c>content\precompiled.rsblob</c> that Build always makes. Off when it isn't set.
    /// </summary>
    public bool? BuildLooseScripts { get; set; }

    /// <summary>
    /// The mod's workspace: its game files (meshes, materials, XML and the like), each at its game path under it, as
    /// REDkit keeps a project's. Build packs them into the mod's bundle. Relative to the project file; <c>workspace</c>
    /// when it isn't set.
    /// </summary>
    public string? WorkspaceFolder { get; set; }

    /// <summary>
    /// The name of the mod Build makes, which is its folder under the game's mods folder: <c>modThisIsMyMod</c>. Made
    /// from the project's name when it isn't set. The game only loads folders that start with "mod".
    /// </summary>
    public string? ModName { get; set; }

    /// <summary>The project's other files and folders (menus, strings, notes…), relative to the project file or full paths.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Where the project file is. Not saved in it.</summary>
    [JsonIgnore] public string FilePath { get; private set; } = "";

    /// <summary>The folder the project file is in, which every relative path in it starts from.</summary>
    [JsonIgnore] public string Root => Path.GetDirectoryName(FilePath) ?? "";

    /// <summary>The scripts folder, as a full path.</summary>
    [JsonIgnore] public string ScriptsPath => Resolve(ScriptsFolder);

    /// <summary>The workspace, as a full path.</summary>
    [JsonIgnore] public string WorkspacePath => Resolve(string.IsNullOrWhiteSpace(WorkspaceFolder) ? "workspace" : WorkspaceFolder);

    /// <summary>Where a game file goes in the project: its game path under the workspace.</summary>
    public string GameFilePath(string gamePath) => Path.Combine(WorkspacePath, gamePath.Replace('/', '\\'));

    /// <summary>The game path of a file in the workspace, or null for a file that isn't in it.</summary>
    public string? GamePathOf(string path)
    {
        string full = Path.GetFullPath(path);
        return Under(WorkspacePath, full) ? Path.GetRelativePath(WorkspacePath, full) : null;
    }

    /// <summary>Where a copy of a game file goes in the project: a script in the scripts folder, anything else in the workspace.</summary>
    public string CopyPathFor(string gamePath)
    {
        string path = gamePath.Replace('/', '\\').TrimStart('\\');
        if (!path.EndsWith(".ws", StringComparison.OrdinalIgnoreCase)) return GameFilePath(path);
        // a script's game path starts at the scripts folder: content\scripts\game\x.ws and scripts\game\x.ws are game\x.ws
        foreach (string start in new[] { @"content\scripts\", @"scripts\" })
            if (path.StartsWith(start, StringComparison.OrdinalIgnoreCase)) { path = path[start.Length..]; break; }
        return Path.Combine(ScriptsPath, path);
    }

    /// <summary>The mod's folder name: <see cref="ModName"/>, or one made from the project's name, starting with "mod".</summary>
    [JsonIgnore] public string ModFolderName => ModFolderFor(string.IsNullOrWhiteSpace(ModName) ? Name : ModName);

    /// <summary>Where Build puts what it makes. Everything in it is made again by each build.</summary>
    [JsonIgnore] public string BuildPath => Path.Combine(Root, "build");

    /// <summary>The mod Build makes, ready to install, in the build folder under its mod name.</summary>
    [JsonIgnore] public string BuiltModPath => Path.Combine(BuildPath, ModFolderName);

    /// <summary>
    /// A mod folder name from a name as people write it: "This is my mod" gives modThisIsMyMod. Only letters, digits
    /// and underscores, and "mod" in front unless it's there already.
    /// </summary>
    public static string ModFolderFor(string name)
    {
        var words = System.Text.RegularExpressions.Regex.Split(name.Trim(), "[^A-Za-z0-9_]+").Where(w => w.Length > 0).ToList();
        string joined = string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        if (joined.Length == 0) joined = "Mod";
        return joined.StartsWith("mod", StringComparison.OrdinalIgnoreCase) ? "mod" + joined[3..] : "mod" + joined;
    }

    // ---------------------------------------------------------------- files

    /// <summary>Reads a project file. Throws <see cref="InvalidDataException"/> when it isn't one this version understands.</summary>
    public static WorkbenchProject Load(string path)
    {
        path = Path.GetFullPath(path);
        WorkbenchProject? project;
        try
        {
            project = JsonSerializer.Deserialize<WorkbenchProject>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(L.T("WorkbenchProject.IsntReadableProjectFile", Path.GetFileName(path), ex.Message));
        }
        if (project is null) throw new InvalidDataException(L.T("WorkbenchProject.Empty", Path.GetFileName(path)));
        if (project.Format > CurrentFormat)
            throw new InvalidDataException(L.T("WorkbenchProject.MadeNewerVersionW3", Path.GetFileName(path), project.Format));
        project.FilePath = path;
        if (string.IsNullOrWhiteSpace(project.Name)) project.Name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(project.ScriptsFolder)) project.ScriptsFolder = "scripts";
        project.Include ??= [];
        return project;
    }

    /// <summary>A new project in <paramref name="folder"/>, saved as <c>name.w3bench</c>. The scripts folder is made when it isn't there.</summary>
    public static WorkbenchProject Create(string folder, string name, string scriptsFolder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, SafeFileName(name) + Extension);
        if (File.Exists(file)) throw new IOException(L.T("WorkbenchProject.AlreadyExistsFolder", Path.GetFileName(file)));
        var project = new WorkbenchProject { Name = name.Trim(), FilePath = file, ScriptsFolder = Normalize(scriptsFolder) };
        Directory.CreateDirectory(project.ScriptsPath);
        project.Save();
        return project;
    }

    public void Save()
    {
        Format = CurrentFormat;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Moves the project to another file, as when it's renamed. The old file is left to the caller.</summary>
    public void Rebase(string newFilePath) => FilePath = Path.GetFullPath(newFilePath);

    // ---------------------------------------------------------------- paths

    /// <summary>A path as the project file stores it: relative to the project when it's inside it, otherwise the full path; forward slashes.</summary>
    public string ToStored(string path)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(Root, full);
        return Normalize(relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative);
    }

    /// <summary>A stored path as a full one.</summary>
    public string Resolve(string stored) => Path.GetFullPath(Path.Combine(Root, stored.Replace('/', Path.DirectorySeparatorChar)));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static string SafeFileName(string name)
    {
        string safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return safe.Length == 0 ? "project" : safe;
    }

    private static bool Under(string folder, string path) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- membership

    /// <summary>Whether a file is part of the project: in its scripts folder, or one of its other files, or in one of its folders.</summary>
    public bool Contains(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Equals(FilePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (Under(ScriptsPath, full)) return true;
        foreach (string stored in Include)
        {
            string member = Resolve(stored);
            if (member.Equals(full, StringComparison.OrdinalIgnoreCase) || Under(member, full)) return true;
        }
        return false;
    }

    /// <summary>Adds a file or a folder to the project. False when it is part of it already.</summary>
    public bool Add(string path)
    {
        if (Contains(path)) return false;
        Include.Add(ToStored(path));
        Include.Sort(StringComparer.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>Takes a file or folder out of the project's list. The file itself is never touched. False when it was only part of the scripts folder.</summary>
    public bool Remove(string path)
    {
        string full = Path.GetFullPath(path);
        return Include.RemoveAll(s => Resolve(s).Equals(full, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>Every file in the project except the scripts: the listed files, and what's inside the listed folders.</summary>
    public IEnumerable<string> OtherFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string stored in Include)
        {
            string member = Resolve(stored);
            if (File.Exists(member))
            {
                if (seen.Add(member)) yield return member;
            }
            else if (Directory.Exists(member))
            {
                foreach (string file in Walk(member))
                    if (seen.Add(file)) yield return file;
            }
        }
    }

    private static IEnumerable<string> Walk(string folder)
    {
        IEnumerable<string> files = [], folders = [];
        try
        {
            files = Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase).ToList();
            folders = Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        foreach (string file in files) yield return file;
        foreach (string sub in folders)
        {
            if (SkippedFolders.Contains(Path.GetFileName(sub))) continue;
            foreach (string file in Walk(sub)) yield return file;
        }
    }

    // ---------------------------------------------------------------- finding things

    /// <summary>The scripts folder of a mod's folder, as REDkit and mods lay them out: <c>scripts</c>, <c>content\scripts</c> or <c>workspace\scripts</c>. Null when there is none.</summary>
    public static string? DetectScriptsFolder(string root)
    {
        foreach (string candidate in new[] { "scripts", @"content\scripts", @"workspace\scripts" })
            if (Directory.Exists(Path.Combine(root, candidate))) return candidate.Replace('\\', '/');
        return null;
    }

    /// <summary>The project file in a folder, when there is exactly one.</summary>
    public static string? FileIn(string folder)
    {
        try
        {
            var files = Directory.GetFiles(folder, "*" + Extension);
            return files.Length == 1 ? files[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The nearest project file at or above a file, so a script found anywhere can say which project it would belong to. Null when there is none.</summary>
    public static string? FindAbove(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); dir is not null; dir = dir.Parent)
            if (FileIn(dir.FullName) is { } file) return file;
        return null;
    }
}
