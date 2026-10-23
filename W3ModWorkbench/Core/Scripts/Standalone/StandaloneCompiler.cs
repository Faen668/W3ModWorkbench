using System.IO;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// Compiles a folder of WitcherScript into a .rsblob without REDkit. It reads the game's own scripts for the
/// declarations the project's code names and the built-in RTTI dump for the engine's native classes, then writes the
/// project's blob: the same kind REDkit's wcc_lite writes, a mod's classes and functions with their code, linked to
/// the game's by name.
/// The compiler is a port of the .ws-to-rsblob compiler webspam wrote for Aelto's tw3-cahirb (MIT).
/// </summary>
public static class StandaloneCompiler
{
    /// <summary>
    /// The game's .ws sources the compile reads declarations from: the game's own content\content0\scripts, otherwise
    /// REDkit's r4data\scripts. The game's come first because r4data\scripts is where modders copy the mods theirs
    /// depend on (REDkit can't look anywhere else), and those are installed mods, read from the mods folder.
    /// </summary>
    public static string? FindGameScripts(string? wccLite, string? gameFolder)
    {
        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            string scripts = Path.Combine(gameFolder, "content", "content0", "scripts");
            if (Directory.Exists(scripts)) return scripts;
        }
        return !string.IsNullOrWhiteSpace(wccLite) ? ScriptCompiler.GameScriptsFor(wccLite) : null;
    }

    /// <param name="modsFolder">The game's mods folder, when there is one: the other mods' classes and functions can be used by the project.</param>
    /// <param name="ownMod">The project's own mod's folder name, left out of the installed mods: it's an older build of the same scripts.</param>
    public static Task<CompileResult> CompileAsync(string scriptsFolder, string gameScripts, string output,
        IProgress<string>? progress, CancellationToken cancel, string? modsFolder = null, string? ownMod = null) =>
        Task.Run(() => Compile(scriptsFolder, gameScripts, output, progress, cancel, modsFolder, ownMod), cancel);

    public static CompileResult Compile(string scriptsFolder, string gameScripts, string output,
        IProgress<string>? progress, CancellationToken cancel, string? modsFolder = null, string? ownMod = null)
    {
        var log = new System.Text.StringBuilder();
        var messages = new List<CompileMessage>();
        // the game's files are never written: a compiled blob goes in a mod's own folder, or anywhere of yours
        try { Projects.FileSafety.CheckInstallWrite(output); }
        catch (Projects.ProtectedFileException ex) { return new CompileResult(false, [], "", ex.Message); }
        try
        {
            if (!Directory.Exists(gameScripts))
                return new CompileResult(false, [], "", L.T("StandaloneCompiler.GamesScriptsWerentFound"));

            progress?.Report(L.T("StandaloneCompiler.ReadingGamesScripts"));
            var vanilla = ReadGameScripts(gameScripts, note => { lock (log) log.AppendLine(note); }, cancel);

            var installed = new List<SourceFile>();
            if (modsFolder is not null && Directory.Exists(modsFolder))
            {
                progress?.Report(L.T("StandaloneCompiler.ReadingInstalledMods"));
                installed = InstalledMods.Read(modsFolder, note => { lock (log) log.AppendLine(note); }, cancel, ownMod);
            }

            progress?.Report(L.T("StandaloneCompiler.ReadingYourScripts"));
            var project = ReadProject(scriptsFolder, null, messages, cancel);
            if (messages.Count > 0) return Failed(messages, log);

            progress?.Report("Compiling…");
            var (blob, problems) = Build(project, Overriding(vanilla, gameScripts, project), installed, Rtti.LoadBuiltIn(), log, cancel);
            if (blob is null) return Failed(problems, log);
            messages.AddRange(problems);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllBytes(output, blob);
            return new CompileResult(true, messages, log.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CompileException)
        {
            return new CompileResult(false, messages, log.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// The game's scripts, read for their declarations only (their code isn't compiled), every file under the
    /// folder. One that can't be read is left out, with why in <paramref name="note"/>.
    /// </summary>
    internal static List<SourceFile> ReadGameScripts(string gameScripts, Action<string> note, CancellationToken cancel)
    {
        var paths = Directory.EnumerateFiles(gameScripts, "*.ws", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var files = new SourceFile?[paths.Count];
        Parallel.For(0, paths.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            try
            {
                files[i] = new SourceFile(paths[i], Parser.ParseSource(TextDecoding.Decode(File.ReadAllBytes(paths[i])), skipBodies: true));
            }
            catch (ScriptSyntaxException ex)
            {
                note($"[Skipped] {Path.GetRelativePath(gameScripts, paths[i])}[{ex.Line}]: {ex.Message}");
            }
        });
        return [.. files.OfType<SourceFile>()];
    }

    /// <summary>The game's scripts less those the project has its own copy of, at the same path, which replace them.</summary>
    internal static List<SourceFile> Overriding(List<SourceFile> vanilla, string gameScripts, List<SourceFile> project)
    {
        var overridden = project.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return overridden.Count == 0 ? vanilla : [.. vanilla.Where(f => !overridden.Contains(Path.GetRelativePath(gameScripts, f.Path)))];
    }

    /// <summary>
    /// The project's scripts parsed, by their path under the scripts folder, with a file's text from
    /// <paramref name="unsaved"/> when it's there (by that path), so a change not saved yet is what's read. A file that
    /// doesn't parse is a message instead.
    /// </summary>
    internal static List<SourceFile> ReadProject(string scriptsFolder, IReadOnlyDictionary<string, string>? unsaved, List<CompileMessage> messages, CancellationToken cancel)
    {
        var project = new List<SourceFile>();
        var paths = Directory.Exists(scriptsFolder)
            ? Directory.EnumerateFiles(scriptsFolder, "*.ws", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        foreach (string path in paths)
        {
            cancel.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(scriptsFolder, path);
            try
            {
                string text = unsaved is not null && unsaved.TryGetValue(relative, out var edited) ? edited : TextDecoding.Decode(File.ReadAllBytes(path));
                project.Add(new SourceFile(relative, Parser.ParseSource(text, skipBodies: false)));
            }
            catch (ScriptSyntaxException ex)
            {
                messages.Add(new CompileMessage(true, relative, Math.Max(ex.Line, 1), ex.Message));
            }
        }
        return project;
    }

    /// <summary>
    /// Compiles the parsed project against the game's and the installed mods' declarations and the engine's: the
    /// blob, or null with the problems. A blob comes with its warnings, if any, as problems that aren't errors. Nothing is written. The game's and the mods' files are only read, so the same
    /// ones can be used for compile after compile.
    /// </summary>
    internal static (byte[]? Blob, List<CompileMessage> Problems) Build(List<SourceFile> project, List<SourceFile> vanillaFiles, List<SourceFile> installed,
        Rtti rtti, System.Text.StringBuilder log, CancellationToken cancel)
    {
        var symbols = new Symbols();
        // the game's and the engine's enums, then the mods' added to them as wcc_lite adds a mod's: the installed ones, then the project's
        symbols.AddEnums(vanillaFiles, true);
        symbols.AddRttiEnums(rtti);
        symbols.ExtendEnums(installed, null);
        var enumProblems = new List<CompileMessage>();
        symbols.ExtendEnums(project, (file, line, text) => enumProblems.Add(new CompileMessage(true, file.Path, line, text)));
        if (enumProblems.Count > 0) return (null, enumProblems);
        var duplicates = Duplicates.Check(project);
        if (duplicates.Count > 0)
            return (null, [.. duplicates.Select(p => new CompileMessage(true, p.File, Math.Max(p.Line, 1), p.Text))]);
        symbols.AddTypeNames(project);
        symbols.AddTypeNames(vanillaFiles);
        symbols.AddTypeNames(installed);
        symbols.AddRttiClasses(rtti);
        symbols.ResolveStateParents();
        var membersProblem = new List<CompileMessage>();
        symbols.AddMembers(project, (file, text) => membersProblem.Add(new CompileMessage(true, file.Path, 1, text)));
        symbols.AddMembers(vanillaFiles, (file, text) => { lock (log) log.AppendLine($"[Skipped] {file.Path}: {text}"); });
        symbols.AddMembers(installed, (file, text) => { lock (log) log.AppendLine($"[Skipped] {file.Path}: {text}"); });
        symbols.AddRttiProperties(rtti);
        symbols.AddRttiFunctions(rtti);
        if (membersProblem.Count > 0) return (null, membersProblem);
        var typeProblems = UnknownTypes.Check(symbols, project);
        if (typeProblems.Count > 0)
            return (null, [.. typeProblems.Select(p => new CompileMessage(true, p.File, Math.Max(p.Line, 1), p.Text))]);
        var extensionProblems = Extensions.Check(symbols, project);
        if (extensionProblems.Count > 0)
            return (null, [.. extensionProblems.Select(p => new CompileMessage(true, p.File, Math.Max(p.Line, 1), p.Text))]);
        var (defaultErrors, defaultWarnings) = DefaultValues.Check(symbols, project);
        if (defaultErrors.Count > 0)
            return (null, [.. defaultErrors.Select(p => new CompileMessage(true, p.File, Math.Max(p.Line, 1), p.Text))]);
        // wcc_lite's warnings, pass by pass: incomplete classes, state machines without an autostate, then what compiling the functions finds
        var found = IncompleteClasses.Check(symbols, project, vanillaFiles.Concat(installed))
            .Concat(DefaultValues.AutoStates(symbols, project, vanillaFiles.Concat(installed))).Concat(defaultWarnings).ToList();
        var warnings = new List<CompileMessage>();
        void AddWarnings() => warnings.AddRange(found.Select(p => new CompileMessage(false, p.File, Math.Max(p.Line, 1), p.Text)));
        cancel.ThrowIfCancellationRequested();
        try
        {
            var blob = Assembler.Assemble(symbols, project, found);
            AddWarnings();
            return (blob, warnings);
        }
        catch (CompileFailedException ex)
        {
            AddWarnings();
            return (null, [.. ex.Problems.Select(p => new CompileMessage(true, p.File, Math.Max(p.Line, 1), p.Text)), .. warnings]);
        }
    }

    private static CompileResult Failed(List<CompileMessage> messages, System.Text.StringBuilder log) =>
        new(false, messages, log.ToString());
}
