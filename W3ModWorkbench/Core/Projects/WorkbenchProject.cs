using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Where Compile writes the .rsblob: relative to the project file, or a full path. Asked for the first time when empty.</summary>
    public string? CompileOutput { get; set; }

    /// <summary>The project's other files and folders (menus, strings, notes…), relative to the project file or full paths.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Where the project file is. Not saved in it.</summary>
    [JsonIgnore] public string FilePath { get; private set; } = "";

    /// <summary>The folder the project file is in, which every relative path in it starts from.</summary>
    [JsonIgnore] public string Root => Path.GetDirectoryName(FilePath) ?? "";

    /// <summary>The scripts folder, as a full path.</summary>
    [JsonIgnore] public string ScriptsPath => Resolve(ScriptsFolder);

    /// <summary>The compiled scripts' file, as a full path, or null while none is chosen.</summary>
    [JsonIgnore] public string? CompileOutputPath => string.IsNullOrWhiteSpace(CompileOutput) ? null : Resolve(CompileOutput);

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
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't a readable project file: {ex.Message}");
        }
        if (project is null) throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
        if (project.Format > CurrentFormat)
            throw new InvalidDataException($"{Path.GetFileName(path)} was made by a newer version of W3 Mod Workbench (project format {project.Format}). Update the tool to open it.");
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
        if (File.Exists(file)) throw new IOException($"{Path.GetFileName(file)} already exists in that folder.");
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
