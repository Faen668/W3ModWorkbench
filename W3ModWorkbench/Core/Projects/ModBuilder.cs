using System.IO;
using System.Text;
using System.Text.Json;
using W3ModWorkbench.Core.Scripts;

namespace W3ModWorkbench.Core.Projects;

/// <summary>What a build made, or why it stopped.</summary>
/// <param name="Compiled">What compiling the scripts gave, when Build compiled them: its errors and warnings.</param>
public sealed record BuildResult(bool Success, string ModFolder, string? Failure, IReadOnlyList<string> Made, string Log, CompileResult? Compiled = null);

/// <summary>
/// Makes a project's mod and installs it. Build puts the mod in the project's build folder, ready to install,
/// <c>build\modThisIsMyMod\content</c>, made new each time:
/// <list type="bullet">
/// <item>the project's scripts are compiled into <c>content\precompiled.rsblob</c>, and go in <c>content\scripts</c> as they are,
/// as the project chooses (with both, an <c>info.json</c> tells the game to run the blob);</item>
/// <item>the workspace's game files go in <c>content\blob0.bundle</c> (and <c>buffers0.bundle</c>) with the
/// <c>metadata.store</c> the game needs to read a mod's bundle, its textures' mip levels in <c>content\texture.cache</c>
/// and its sounds in <c>content\soundspc.cache</c>, all made by <see cref="Packing.ModPacker"/> as REDkit's tools make them;</item>
/// <item>the project's <c>.w3strings</c> files go in <c>content</c>.</item>
/// </list>
/// Install copies that folder into the game's mods folder, in place of the one installed before. Those are the only
/// writes into the game's folder the tool makes, and only into the mod's own folder there.
/// </summary>
public static class ModBuilder
{
    /// <summary>Kept in an installed mod's folder, so the next install knows the folder is this project's to replace.</summary>
    public const string InstallMarker = "w3bench-install.json";

    /// <param name="compiler">What compiles the scripts into the mod's blob, or null when nothing can.</param>
    /// <param name="gameFile">
    /// Reads a game file by its path (the texture a picture in the workspace takes the place of, whose settings it
    /// keeps), or null when the game isn't at hand.
    /// </param>
    public static Task<BuildResult> BuildAsync(WorkbenchProject project, CompilerChoice? compiler, Func<string, byte[]?>? gameFile, IProgress<string>? progress, CancellationToken cancel) =>
        Task.Run(() => Build(project, compiler, gameFile, progress, cancel), cancel);

    private static BuildResult Build(WorkbenchProject project, CompilerChoice? compiler, Func<string, byte[]?>? gameFile, IProgress<string>? progress, CancellationToken cancel)
    {
        var log = new StringBuilder();
        var made = new List<string>();
        string mod = project.BuiltModPath;
        string content = Path.Combine(mod, "content");
        CompileResult? compiled = null;
        BuildResult Failed(string why) => new(false, mod, why, made, log.ToString(), compiled);

        if (FileSafety.Protection(project.FilePath) is { } protection)
            return Failed($"The project is inside the game's folders, where nothing is written. {protection}");
        // the build folder is the project's own and only ever this project's build: it's emptied and made again
        if (!Path.GetFullPath(mod).StartsWith(Path.GetFullPath(project.BuildPath) + "\\", StringComparison.OrdinalIgnoreCase))
            return Failed("The mod's name doesn't make a folder inside the build folder.");

        try
        {
            progress?.Report("Clearing the last build…");
            if (Directory.Exists(mod)) Directory.Delete(mod, recursive: true);
            Directory.CreateDirectory(content);

            var scripts = Directory.Exists(project.ScriptsPath)
                ? Directory.EnumerateFiles(project.ScriptsPath, "*.ws", SearchOption.AllDirectories).ToList() : [];
            // the scripts always go in compiled, as REDkit publishes a mod; the loose ones beside them when the project says so
            bool compile = scripts.Count > 0;
            bool loose = compile && project.BuildLooseScripts == true;

            // compiled, as REDkit publishes a mod: content\precompiled.rsblob
            if (compile)
            {
                if (compiler is null)
                    return Failed(CompilerChoice.NothingToCompileWith + ".");
                progress?.Report(compiler.Kind == CompilerKind.WccLite ? "Compiling the scripts with wcc_lite (about a minute)…" : "Compiling the scripts…");
                // compiled to a file of its own first: the compilers won't write into a build, which only Build itself fills
                string temp = Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "build-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    string compiledBlob = Path.Combine(temp, "precompiled.rsblob");
                    compiled = compiler.CompileAsync(project.ScriptsPath, compiledBlob, progress, cancel, Path.GetFileName(mod)).GetAwaiter().GetResult();
                    if (compiled.Success) File.Copy(compiledBlob, Path.Combine(content, "precompiled.rsblob"));
                }
                finally
                {
                    try { Directory.Delete(temp, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                log.Append(compiled.Log);
                if (!compiled.Success)
                {
                    int errors = compiled.Messages.Count(m => m.IsError);
                    return Failed(errors > 0
                        ? $"The scripts didn't compile: {errors} error{(errors == 1 ? "" : "s")}. Nothing was built."
                        : $"The scripts didn't compile. {compiled.Failure}");
                }
                made.Add($"{scripts.Count:N0} script{(scripts.Count == 1 ? "" : "s")} compiled into content\\precompiled.rsblob, by {compiler.Name}");
            }

            // loose, as they are: beside the blob they're the mod's source, for others to read or merge
            if (loose)
            {
                foreach (string script in scripts)
                {
                    cancel.ThrowIfCancellationRequested();
                    string target = Path.Combine(content, "scripts", Path.GetRelativePath(project.ScriptsPath, script));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(script, target);
                }
                made.Add($"{scripts.Count:N0} script{(scripts.Count == 1 ? "" : "s")} in content\\scripts, as source");
            }

            // with both, info.json tells the game to run the blob and leave the loose copies alone, as REDkit's own does
            if (compile && loose && !File.Exists(Path.Combine(content, "info.json")))
            {
                File.WriteAllText(Path.Combine(content, "info.json"), JsonSerializer.Serialize(new
                {
                    name = project.ModFolderName,
                    modName = project.Name,
                    version = project.Version,
                    description = project.Description,
                    author = project.Author,
                    useLooseScripts = false,
                }, new JsonSerializerOptions { WriteIndented = true }));
                made.Add("content\\info.json, so the game runs the compiled scripts and not the loose ones");
            }

            // strings, from anywhere in the project
            foreach (string strings in project.OtherFiles().Where(f => f.EndsWith(".w3strings", StringComparison.OrdinalIgnoreCase)))
            {
                File.Copy(strings, Path.Combine(content, Path.GetFileName(strings)), overwrite: true);
                made.Add($"content\\{Path.GetFileName(strings)}");
            }

            // the workspace's game files, packed into the mod's bundle, its textures cooked and its sounds cached
            if (Directory.Exists(project.WorkspacePath))
            {
                progress?.Report("Packing the workspace…");
                if (Packing.ModPacker.Pack(project.WorkspacePath, content, gameFile, progress, cancel) is { } packed)
                {
                    made.AddRange(packed.Made);
                    foreach (string note in packed.Notes) log.AppendLine(note);
                    made.AddRange(packed.Notes.Take(10).Select(note => "Note: " + note));
                    if (packed.Notes.Count > 10) made.Add($"…and {packed.Notes.Count - 10:N0} more notes");
                }
            }
            if (made.Count == 0) return Failed("There's nothing to build yet: the project has no scripts, no workspace files and no strings.");
            return new BuildResult(true, mod, null, made, log.ToString(), compiled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(ex.Message);
        }
    }

    // ---------------------------------------------------------------- checking the workspace

    /// <summary>
    /// Files in the workspace that a build would pack but the game wouldn't use, and why: REDkit's source files Build
    /// can't cook (anything but a texture: a mesh, an entity, a cube map…), which the game can't load until they're cooked.
    /// Textures are cooked when the mod is built (REDkit's source <c>.xbm</c> files, and pictures made into textures), and
    /// everything else is fine to pack.
    /// <para>
    /// A cooked file is told by its objects: the game's cook marks each one with flag 0x2000, and REDkit's source files
    /// have none marked. Of the game's, its DLC's and installed mods' bundles, every file has at least one.
    /// </para>
    /// </summary>
    public static List<WorkspaceIssue> CheckWorkspace(WorkbenchProject project)
    {
        var issues = new List<WorkspaceIssue>();
        if (!Directory.Exists(project.WorkspacePath)) return issues;
        foreach (string file in Directory.EnumerateFiles(project.WorkspacePath, "*", SearchOption.AllDirectories))
            if (IsUncooked(file) && !BuildCooks(file))
                issues.Add(new WorkspaceIssue(Path.GetRelativePath(project.WorkspacePath, file),
                    "isn't cooked (it's a REDkit source file): the game won't load it until it's cooked, and Build only cooks textures (.xbm) so far."));
        return issues;
    }

    /// <summary>Whether Build cooks a file of this kind when it isn't cooked: textures (<c>.xbm</c>).</summary>
    public static bool BuildCooks(string file) => file.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file is a CR2W resource that hasn't been cooked: none of its objects carry the cook's flag.</summary>
    public static bool IsUncooked(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[160];
            if (stream.Read(header) < header.Length || !Cr2w.Cr2wFile.IsCr2w(header)) return false;
            int exportsAt = BitConverter.ToInt32(header, 40 + 4 * 12), count = BitConverter.ToInt32(header, 44 + 4 * 12);
            if (count <= 0 || exportsAt < 160 || exportsAt + (long)count * 24 > stream.Length) return false;
            var exports = new byte[count * 24];
            stream.Position = exportsAt;
            stream.ReadExactly(exports);
            for (int i = 0; i < count; i++)
                if ((BitConverter.ToUInt16(exports, i * 24 + 2) & 0x2000) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- installing

    /// <summary>Where Install puts the mod: its folder in the game's mods folder.</summary>
    public static string InstallPath(WorkbenchProject project, string gameFolder) => Path.Combine(gameFolder, "mods", project.ModFolderName);

    /// <summary>
    /// What's in the mods folder at that name now: nothing, this project's last install, or someone else's mod (a mod
    /// of the same name, or one installed by hand), which Install would replace.
    /// </summary>
    public static InstalledState Installed(WorkbenchProject project, string gameFolder)
    {
        string target = InstallPath(project, gameFolder);
        if (!Directory.Exists(target)) return InstalledState.None;
        try
        {
            string marker = Path.Combine(target, InstallMarker);
            if (File.Exists(marker) && JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(marker)) is { } record
                && record.Project.Equals(project.FilePath, StringComparison.OrdinalIgnoreCase))
                return InstalledState.Ours;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return InstalledState.Someone;
    }

    /// <summary>
    /// Copies the build into the game's mods folder, replacing what's there under the mod's name. Only that one folder
    /// is touched. The build has to be there already.
    /// </summary>
    public static Task<string> InstallAsync(WorkbenchProject project, string gameFolder) => Task.Run(() =>
    {
        string source = project.BuiltModPath;
        if (!Directory.Exists(Path.Combine(source, "content"))) throw new IOException("There's no build to install yet. Build the mod first.");
        string mods = Path.Combine(gameFolder, "mods");
        string target = InstallPath(project, gameFolder);
        // only ever a mod's own folder, straight under mods, with the name the game wants
        if (!project.ModFolderName.StartsWith("mod", StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(Path.GetFullPath(target))!.Equals(Path.GetFullPath(mods), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{project.ModFolderName} isn't a mod folder name the tool installs to.");
        FileSafety.CheckInstallWrite(Path.Combine(target, "content", "_"));
        Directory.CreateDirectory(mods);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        File.WriteAllText(Path.Combine(target, InstallMarker),
            JsonSerializer.Serialize(new InstallRecord(project.FilePath, project.Name, DateTime.Now), new JsonSerializerOptions { WriteIndented = true }));
        return target;
    });

    private sealed record InstallRecord(string Project, string Name, DateTime Installed);
}

/// <summary>A file in the workspace that a build would pack but the game wouldn't use: its game path, and why, to follow it.</summary>
public sealed record WorkspaceIssue(string GamePath, string Problem);

/// <summary>What the mods folder has under a project's mod name.</summary>
public enum InstalledState { None, Ours, Someone }
