using System.IO;

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
    /// The game's .ws sources the compile reads declarations from: REDkit's r4data\scripts when it's there, otherwise the
    /// game's own content\content0\scripts.
    /// </summary>
    public static string? FindGameScripts(string? wccLite, string? gameFolder)
    {
        if (!string.IsNullOrWhiteSpace(wccLite) && ScriptCompiler.GameScriptsFor(wccLite) is { } redkit) return redkit;
        if (!string.IsNullOrWhiteSpace(gameFolder))
        {
            string scripts = Path.Combine(gameFolder, "content", "content0", "scripts");
            if (Directory.Exists(scripts)) return scripts;
        }
        return null;
    }

    /// <param name="modsFolder">The game's mods folder, when there is one: the other mods' classes and functions can be used by the project.</param>
    public static Task<CompileResult> CompileAsync(string scriptsFolder, string gameScripts, string output,
        IProgress<string>? progress, CancellationToken cancel, string? modsFolder = null) =>
        Task.Run(() => Compile(scriptsFolder, gameScripts, output, progress, cancel, modsFolder), cancel);

    public static CompileResult Compile(string scriptsFolder, string gameScripts, string output,
        IProgress<string>? progress, CancellationToken cancel, string? modsFolder = null)
    {
        var log = new System.Text.StringBuilder();
        var messages = new List<CompileMessage>();
        try
        {
            if (!Directory.Exists(gameScripts))
                return new CompileResult(false, [], "", "The game's scripts weren't found. Set the game folder in Settings.");

            progress?.Report("Reading the game's scripts…");
            var projectPaths = Directory.EnumerateFiles(scriptsFolder, "*.ws", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var overridden = projectPaths.Select(p => Path.GetRelativePath(scriptsFolder, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var vanillaPaths = Directory.EnumerateFiles(gameScripts, "*.ws", SearchOption.AllDirectories)
                .Where(p => !overridden.Contains(Path.GetRelativePath(gameScripts, p)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

            var vanilla = new SourceFile?[vanillaPaths.Count];
            Parallel.For(0, vanillaPaths.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                try
                {
                    vanilla[i] = new SourceFile(vanillaPaths[i], Parser.ParseSource(TextDecoding.Decode(File.ReadAllBytes(vanillaPaths[i])), skipBodies: true));
                }
                catch (ScriptSyntaxException ex)
                {
                    lock (log) log.AppendLine($"[Skipped] {Path.GetRelativePath(gameScripts, vanillaPaths[i])}[{ex.Line}]: {ex.Message}");
                }
            });

            var installed = new List<SourceFile>();
            if (modsFolder is not null && Directory.Exists(modsFolder))
            {
                progress?.Report("Reading the installed mods…");
                installed = InstalledMods.Read(modsFolder, note => { lock (log) log.AppendLine(note); }, cancel);
            }

            progress?.Report("Reading your scripts…");
            var project = new List<SourceFile>();
            foreach (string path in projectPaths)
            {
                cancel.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(scriptsFolder, path);
                try
                {
                    project.Add(new SourceFile(relative, Parser.ParseSource(TextDecoding.Decode(File.ReadAllBytes(path)), skipBodies: false)));
                }
                catch (ScriptSyntaxException ex)
                {
                    messages.Add(new CompileMessage(true, relative, Math.Max(ex.Line, 1), ex.Message));
                }
            }
            if (messages.Count > 0) return Failed(messages, log);

            progress?.Report("Compiling…");
            var rtti = Rtti.LoadBuiltIn();
            var vanillaFiles = vanilla.OfType<SourceFile>().ToList();
            var symbols = new Symbols();
            symbols.AddEnums(installed, true); // the project's own come last, so they win
            symbols.AddEnums(project, true);
            symbols.AddEnums(vanillaFiles, true);
            symbols.AddRttiEnums(rtti);
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
            if (membersProblem.Count > 0) return Failed(membersProblem, log);
            cancel.ThrowIfCancellationRequested();

            byte[] blob;
            try
            {
                blob = Assembler.Assemble(symbols, project);
            }
            catch (CompileFailedException ex)
            {
                return Failed([.. ex.Problems.Select(p => new CompileMessage(true, p.File, Math.Max(p.Line, 1), p.Text))], log);
            }

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

    private static CompileResult Failed(List<CompileMessage> messages, System.Text.StringBuilder log) =>
        new(false, messages, log.ToString());
}
