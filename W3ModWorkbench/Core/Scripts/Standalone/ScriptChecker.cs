using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// Checks a project's scripts as they're typed, with the built-in compiler: the same check Compile makes, against the
/// game's scripts, the installed mods and the engine's classes, but nothing is written. The game's and the mods'
/// declarations are read once and kept (they don't change while you type), so a check reads only the project, with the
/// unsaved text of its open files, and takes about a second.
/// </summary>
public static class ScriptChecker
{
    /// <summary>What a check reads besides the project: the game's scripts, the installed mods and the engine's classes.</summary>
    private sealed record Base(List<SourceFile> Vanilla, List<SourceFile> Installed, Rtti Rtti);

    private static readonly Dictionary<string, Task<Base>> Bases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>Forgets the game's and the mods' declarations read so far, so the next check reads them again (a mod was installed or changed).</summary>
    public static void Forget()
    {
        lock (Gate) Bases.Clear();
    }

    /// <summary>
    /// Starts reading the game's scripts and the installed mods for checks to come, so the first check doesn't wait as
    /// long. Safe to call again: they're read once.
    /// </summary>
    public static void Prepare(string gameScripts, string? modsFolder, string? ownMod) => _ = BaseFor(gameScripts, modsFolder, ownMod);

    private static Task<Base> BaseFor(string gameScripts, string? modsFolder, string? ownMod)
    {
        string key = $"{gameScripts}|{modsFolder}|{ownMod}";
        lock (Gate)
        {
            if (!Bases.TryGetValue(key, out var task) || task.IsFaulted || task.IsCanceled)
                Bases[key] = task = Task.Run(() =>
                {
                    var vanilla = StandaloneCompiler.ReadGameScripts(gameScripts, _ => { }, CancellationToken.None);
                    var installed = modsFolder is not null && Directory.Exists(modsFolder)
                        ? InstalledMods.Read(modsFolder, _ => { }, CancellationToken.None, ownMod)
                        : [];
                    return new Base(vanilla, installed, Rtti.LoadBuiltIn());
                });
            return task;
        }
    }

    /// <summary>
    /// What's wrong with the project's scripts, by file (its path under the scripts folder) and line, as Compile would
    /// say. A file's text comes from <paramref name="unsaved"/> when it's there (keyed by that path). A syntax error
    /// stops the rest of the check, as it stops a compile: the files that parse can't be checked without the one that
    /// doesn't. Empty when all is well.
    /// </summary>
    public static async Task<List<CompileMessage>> CheckAsync(string scriptsFolder, IReadOnlyDictionary<string, string> unsaved, string gameScripts,
        string? modsFolder, string? ownMod, CancellationToken cancel)
    {
        var known = await BaseFor(gameScripts, modsFolder, ownMod).WaitAsync(cancel);
        return await Task.Run(() =>
        {
            var messages = new List<CompileMessage>();
            var project = StandaloneCompiler.ReadProject(scriptsFolder, unsaved, messages, cancel);
            if (messages.Count > 0) return messages;
            cancel.ThrowIfCancellationRequested();
            try
            {
                var (_, problems) = StandaloneCompiler.Build(project, StandaloneCompiler.Overriding(known.Vanilla, gameScripts, project), known.Installed,
                    known.Rtti, new StringBuilder(), cancel);
                return problems;
            }
            catch (CompileException ex)
            {
                return [new CompileMessage(true, null, Math.Max(ex.Line, 1), ex.Message)];
            }
        }, cancel);
    }

    /// <summary>
    /// The part of a line a problem is about: the first name the message quotes ('name') that the line has as a whole
    /// word, else the line from its first character to its last that isn't a space. Offsets are within the line.
    /// </summary>
    public static (int Start, int Length) Span(string line, string message)
    {
        int quote = 0;
        while ((quote = message.IndexOf('\'', quote)) >= 0)
        {
            int end = message.IndexOf('\'', quote + 1);
            if (end < 0) break;
            string name = message[(quote + 1)..end];
            quote = end + 1;
            if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;
            for (int at = line.IndexOf(name, StringComparison.Ordinal); at >= 0; at = line.IndexOf(name, at + 1, StringComparison.Ordinal))
            {
                bool before = at > 0 && (char.IsLetterOrDigit(line[at - 1]) || line[at - 1] == '_');
                bool after = at + name.Length < line.Length && (char.IsLetterOrDigit(line[at + name.Length]) || line[at + name.Length] == '_');
                if (!before && !after) return (at, name.Length);
            }
        }
        int first = 0, last = line.Length;
        while (first < last && char.IsWhiteSpace(line[first])) first++;
        while (last > first && char.IsWhiteSpace(line[last - 1])) last--;
        return last > first ? (first, last - first) : (0, line.Length);
    }
}
