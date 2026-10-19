using System.Diagnostics;
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
/// <item>the workspace's game files go in <c>content\blob0.bundle</c>, packed by REDkit's wcc_lite, with the
/// <c>metadata.store</c> the game needs to read a mod's bundle;</item>
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
    public static Task<BuildResult> BuildAsync(WorkbenchProject project, string? wccLite, CompilerChoice? compiler, IProgress<string>? progress, CancellationToken cancel) =>
        Task.Run(() => Build(project, wccLite, compiler, progress, cancel), cancel);

    private static BuildResult Build(WorkbenchProject project, string? wccLite, CompilerChoice? compiler, IProgress<string>? progress, CancellationToken cancel)
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
            bool compile = scripts.Count > 0 && project.BuildCompiledScripts != false;
            bool loose = scripts.Count > 0 && (project.BuildLooseScripts ?? !compile);
            if (scripts.Count > 0 && !compile && !loose)
                return Failed("The project has scripts, but Project properties has neither Compile the scripts nor Ship the loose scripts ticked, so the mod would have none. Tick one and build again.");

            // compiled, as REDkit publishes a mod: content\precompiled.rsblob
            if (compile)
            {
                if (compiler is null)
                    return Failed(CompilerChoice.NothingToCompileWith + ". Or untick Compile scripts in Project properties to ship the scripts loose instead.");
                progress?.Report(compiler.Kind == CompilerKind.WccLite ? "Compiling the scripts with wcc_lite (about a minute)…" : "Compiling the scripts…");
                // compiled to a file of its own first: the compilers won't write into a build, which only Build itself fills
                string temp = Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "build-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    string compiledBlob = Path.Combine(temp, "precompiled.rsblob");
                    compiled = compiler.CompileAsync(project.ScriptsPath, compiledBlob, progress, cancel).GetAwaiter().GetResult();
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

            // loose, as they are: the game compiles them when it starts, or beside a blob they're the mod's source
            if (loose)
            {
                foreach (string script in scripts)
                {
                    cancel.ThrowIfCancellationRequested();
                    string target = Path.Combine(content, "scripts", Path.GetRelativePath(project.ScriptsPath, script));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(script, target);
                }
                made.Add($"{scripts.Count:N0} script{(scripts.Count == 1 ? "" : "s")} in content\\scripts" + (compile ? ", as source" : ""));
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

            // the workspace's game files, packed into the mod's bundle
            var files = Directory.Exists(project.WorkspacePath)
                ? Directory.EnumerateFiles(project.WorkspacePath, "*", SearchOption.AllDirectories).ToList() : [];
            if (files.Count > 0)
            {
                if (string.IsNullOrEmpty(wccLite) || !File.Exists(wccLite))
                    return Failed($"The workspace has {files.Count:N0} game file{(files.Count == 1 ? "" : "s")} to pack into the mod's bundle, which needs REDkit's wcc_lite. Set where it is in Settings (REDkit is free on Steam and GOG), then build again.");
                progress?.Report($"Packing {files.Count:N0} file{(files.Count == 1 ? "" : "s")} into the bundle…");
                if (Run(wccLite, ["pack", "-dir=" + project.WorkspacePath, "-outdir=" + content], log, cancel) is { } packFailure)
                    return Failed("wcc_lite couldn't pack the workspace: " + packFailure);
                progress?.Report("Writing the bundle's metadata…");
                if (Run(wccLite, ["metadatastore", "-path=" + content], log, cancel) is { } metaFailure)
                    return Failed("wcc_lite couldn't write metadata.store: " + metaFailure);
                var bundles = Directory.GetFiles(content, "*.bundle");
                if (bundles.Length == 0) return Failed("wcc_lite finished without making a bundle. Its log says more.");
                made.Add($"{files.Count:N0} game file{(files.Count == 1 ? "" : "s")} in content\\{string.Join(", ", bundles.Select(Path.GetFileName))}");
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

    /// <summary>Pictures a modder may put in the workspace as a texture, which go in a texture cache, not a bundle.</summary>
    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase) { ".dds", ".png", ".tga", ".jpg", ".jpeg", ".bmp" };

    /// <summary>
    /// Files in the workspace that a build would pack but the game wouldn't use, and why: a file that isn't cooked
    /// (one of REDkit's source files, which the game can't load until it's cooked), or a picture meant as a texture
    /// (a mod's textures go in its own texture cache, which Build doesn't make yet). Everything else is fine to pack.
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
        {
            string gamePath = Path.GetRelativePath(project.WorkspacePath, file);
            if (PictureExtensions.Contains(Path.GetExtension(file)))
            {
                issues.Add(new WorkspaceIssue(gamePath, "is a picture: a mod's textures go in its own texture cache, which Build doesn't make yet, so the game won't see it."));
                continue;
            }
            if (IsUncooked(file))
                issues.Add(new WorkspaceIssue(gamePath, "isn't cooked (it's a REDkit source file): the game won't load it until it's cooked, which Build doesn't do yet."));
        }
        return issues;
    }

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

    /// <summary>Runs wcc_lite. Null when it worked, otherwise what went wrong.</summary>
    private static string? Run(string wccLite, string[] arguments, StringBuilder log, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(wccLite)
        {
            WorkingDirectory = Path.GetDirectoryName(wccLite)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        lock (log) log.AppendLine("> wcc_lite " + string.Join(' ', arguments));
        using var process = new Process { StartInfo = start };
        string? lastError = null;
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (log) log.AppendLine(line);
            if (line.Contains("[Error]", StringComparison.OrdinalIgnoreCase)) lastError = line.Trim();
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (cancel.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
            process.WaitForExit();
        cancel.ThrowIfCancellationRequested();
        return process.ExitCode == 0 ? null : lastError ?? $"it stopped with exit code {process.ExitCode}.";
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
