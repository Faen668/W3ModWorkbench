using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>A warning or error from the compiler, about a line of the project's scripts.</summary>
/// <param name="File">The file, relative to the project's scripts folder, or null when it couldn't be told.</param>
/// <param name="Guessed">The compiler didn't name the file; it was worked out from what's on that line.</param>
public sealed record CompileMessage(bool IsError, string? File, int Line, string Text, bool Guessed = false);

public sealed record CompileResult(bool Success, IReadOnlyList<CompileMessage> Messages, string Log, string? Failure = null);

/// <summary>
/// Compiles a folder of WitcherScript into a .rsblob with REDkit's wcc_lite, the way publishing from REDkit does:
/// <c>wcc_lite compilescripts &lt;REDkit&gt;\r4data\scripts -patch=&lt;folder&gt; -out=&lt;dir&gt;</c>. It compiles the game's
/// scripts first and the project's on top, which takes most of a minute.
/// </summary>
public static partial class ScriptCompiler
{
    public const string ExeName = "wcc_lite.exe";

    /// <summary>The game scripts REDkit compiles a project against: r4data\scripts, two folders up from bin\x64_RedKit.</summary>
    public static string? GameScriptsFor(string wccLite)
    {
        if (Path.GetDirectoryName(wccLite) is not { } bin) return null;
        string scripts = Path.GetFullPath(Path.Combine(bin, "..", "..", "r4data", "scripts"));
        return Directory.Exists(scripts) ? scripts : null;
    }

    public static bool IsWccLite(string path) =>
        path.Length > 0 && File.Exists(path)
        && Path.GetFileName(path).Equals(ExeName, StringComparison.OrdinalIgnoreCase)
        && GameScriptsFor(path) is not null;

    /// <summary>wcc_lite in a REDkit install from Steam or GOG, or in the usual folders on every drive.</summary>
    public static string? Find() => Candidates()
        .Select(redkit => Path.Combine(redkit, "bin", "x64_RedKit", ExeName))
        .FirstOrDefault(IsWccLite);

    private static IEnumerable<string> Candidates()
    {
        foreach (string library in GameLocator.SteamLibraries())
            yield return Path.Combine(library, "steamapps", "common", "The Witcher 3 REDkit");
        foreach (string? path in GameLocator.GogPaths(name => name.Contains("REDkit", StringComparison.OrdinalIgnoreCase)))
            if (!string.IsNullOrEmpty(path)) yield return path;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            string root = drive.RootDirectory.FullName;
            yield return Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "The Witcher 3 REDkit");
            yield return Path.Combine(root, "SteamLibrary", "steamapps", "common", "The Witcher 3 REDkit");
            yield return Path.Combine(root, "Steam", "steamapps", "common", "The Witcher 3 REDkit");
            yield return Path.Combine(root, "GOG Games", "The Witcher 3 REDkit");
        }
    }

    /// <summary>
    /// Compiles the .ws files under <paramref name="scriptsFolder"/> and, when they compile, saves the blob to
    /// <paramref name="output"/>. <paramref name="progress"/> hears what stage it's at. Cancelling stops wcc_lite.
    /// </summary>
    public static async Task<CompileResult> CompileAsync(string wccLite, string scriptsFolder, string output,
        IProgress<string>? progress, CancellationToken cancel)
    {
        if (GameScriptsFor(wccLite) is not { } gameScripts)
            return new CompileResult(false, [], "", "REDkit's game scripts (r4data\\scripts) weren't found next to wcc_lite.");

        // the game's files are never written: a compiled blob goes in a mod's own folder, or anywhere of yours
        try { Projects.FileSafety.CheckInstallWrite(output); }
        catch (Projects.ProtectedFileException ex) { return new CompileResult(false, [], "", ex.Message); }

        string work = Path.Combine(Path.GetTempPath(), "W3ModWorkbench", "compile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var log = new StringBuilder();
        var lines = new List<string>();
        try
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
            start.ArgumentList.Add("compilescripts");
            start.ArgumentList.Add(gameScripts);
            start.ArgumentList.Add("-patch=" + Path.GetFullPath(scriptsFolder));
            start.ArgumentList.Add("-out=" + work);

            using var process = new Process { StartInfo = start };
            int parsing = 0;
            void OnLine(string? line)
            {
                if (line is null) return;
                lock (log)
                {
                    log.AppendLine(line);
                    lines.Add(line);
                }
                if (Parsing().IsMatch(line))
                    progress?.Report(++parsing == 1 ? "Compiling the game's scripts…" : "Compiling your scripts…");
            }
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            progress?.Report("Starting wcc_lite…");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            List<string> all;
            lock (log) all = [.. lines];
            var messages = Messages(all, scriptsFolder);
            string blob = Path.Combine(work, "blob.rsblob");
            if (process.ExitCode != 0 || !File.Exists(blob))
            {
                string? failure = messages.Any(m => m.IsError) ? null
                    : all.LastOrDefault(l => l.Contains("[Error][WCC]")) is { } last ? Message().Match(last).Groups["text"].Value.Trim()
                    : $"wcc_lite stopped with exit code {process.ExitCode}.";
                return new CompileResult(false, messages, log.ToString(), failure);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.Copy(blob, output, overwrite: true);
            return new CompileResult(true, messages, log.ToString());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The warnings and errors about the project: those after wcc_lite starts parsing it, the second time it says
    /// "Parsing N script files". A line starting with " - " carries on the message before it.
    /// </summary>
    public static List<CompileMessage> Messages(IReadOnlyList<string> log, string scriptsFolder)
    {
        int from = 0, seen = 0;
        for (int i = 0; i < log.Count; i++)
            if (Parsing().IsMatch(log[i]) && ++seen == 2) from = i;
        if (seen < 2) from = log.Count; // it never got to the project

        var raw = new List<(bool Error, string? File, int Line, string Text)>();
        for (int i = from; i < log.Count; i++)
        {
            var m = Message().Match(log[i]);
            if (!m.Success || !m.Groups["line"].Success) continue;
            string text = m.Groups["text"].Value.TrimEnd();
            bool error = m.Groups["level"].Value == "Error";
            string? file = m.Groups["file"].Success ? m.Groups["file"].Value.Trim() : null;
            int line = int.Parse(m.Groups["line"].Value);
            if (text.StartsWith(" - ") && raw.Count > 0 && raw[^1].Line == line && raw[^1].Error == error)
                raw[^1] = raw[^1] with { Text = raw[^1].Text + "\n" + text.Trim() };
            else
                raw.Add((error, file, line, text.Trim()));
        }

        var files = ProjectFiles(scriptsFolder);
        return [.. raw.Select(r =>
        {
            if (r.File is { } named)
            {
                string relative = Path.IsPathRooted(named) ? Path.GetRelativePath(scriptsFolder, named) : named;
                return new CompileMessage(r.Error, relative, r.Line, r.Text);
            }
            string? guess = GuessFile(files, r.Line, r.Text);
            return new CompileMessage(r.Error, guess, r.Line, r.Text, Guessed: guess is not null);
        })];
    }

    /// <summary>The project's files, relative path and lines, for working out which one a message is about.</summary>
    private static List<(string Path, string[] Lines)> ProjectFiles(string scriptsFolder)
    {
        var result = new List<(string, string[])>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(scriptsFolder, "*.ws", SearchOption.AllDirectories))
            {
                try
                {
                    string text = TextDecoding.Decode(File.ReadAllBytes(path));
                    result.Add((Path.GetRelativePath(scriptsFolder, path), text.Split('\n')));
                }
                catch (IOException)
                {
                    // a file that can't be read can't be the one
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing to guess from
        }
        return result;
    }

    /// <summary>
    /// wcc_lite leaves the file out of most messages about a project with more than one file. The one file whose
    /// line has a name from the message on it is taken to be the one; with only one file, it's that one.
    /// </summary>
    private static string? GuessFile(List<(string Path, string[] Lines)> files, int line, string text)
    {
        var longEnough = files.Where(f => line >= 1 && line <= f.Lines.Length).ToList();
        if (longEnough.Count == 1) return longEnough[0].Path;
        var names = Quoted().Matches(text).Select(m => m.Groups[1].Value)
            .Concat(Identifier().Matches(text).Select(m => m.Value))
            .Where(n => n.Length > 2)
            .Distinct()
            .ToList();
        foreach (string name in names)
        {
            var having = longEnough.Where(f => Regex.IsMatch(f.Lines[line - 1], $@"\b{Regex.Escape(name)}\b")).ToList();
            if (having.Count == 1) return having[0].Path;
        }
        return null;
    }

    [GeneratedRegex(@"\[Info\]\[WCC\] Parsing \d+ script files")]
    private static partial Regex Parsing();

    [GeneratedRegex(@"^\[[^\]]*\]\[(?<level>Error|Warning)\]\[WCC\] (?:(?<file>[^\[\]]*?\.ws))?(?:\[(?<line>\d+)\]:)?(?<text>.*)$")]
    private static partial Regex Message();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex Quoted();

    /// <summary>Names that look like code: with a capital after the start, a digit or an underscore.</summary>
    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*(?:[A-Z0-9_])[A-Za-z0-9_]*\b")]
    private static partial Regex Identifier();
}
