using System.IO;

namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// The declarations of the mods installed in the game's mods folder. A project often builds on another mod (Shared Utils,
/// say) that REDkit carries in its own scripts but the game doesn't; without REDkit, the installed copy is where its classes
/// and functions are. A mod that ships a compiled blob is read from the blob, since that's what the game runs, and
/// one without from its loose scripts.
/// </summary>
public static class InstalledMods
{
    /// <param name="except">A mod's folder name to leave out: the project's own, whose installed copy is an older build of what's being compiled.</param>
    public static List<SourceFile> Read(string modsFolder, Action<string> note, CancellationToken cancel, string? except = null)
    {
        string[] mods;
        try
        {
            mods = [.. Directory.GetDirectories(modsFolder).Where(m => except is null || !Path.GetFileName(m).Equals(except, StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        Array.Sort(mods, StringComparer.OrdinalIgnoreCase);

        var found = new List<SourceFile>?[mods.Length];
        Parallel.For(0, mods.Length, new ParallelOptions { CancellationToken = cancel }, i => found[i] = ReadMod(mods[i], note));
        return [.. found.Where(f => f is not null).SelectMany(f => f!)];
    }

    private static List<SourceFile> ReadMod(string mod, Action<string> note)
    {
        var files = new List<SourceFile>();
        string name = Path.GetFileName(mod);
        string content = Path.Combine(mod, "content");
        try
        {
            var blobs = Directory.Exists(content)
                ? Directory.EnumerateFiles(content, "*.rsblob", SearchOption.TopDirectoryOnly).ToList()
                : [];
            foreach (string blob in blobs)
            {
                try
                {
                    var docs = ScriptWriter.Write(ScriptBlob.Open(blob), name);
                    // one document holds everything the blob declares; the others split it up by class
                    var all = docs.FirstOrDefault(d => d.Path.EndsWith("all.ws", StringComparison.OrdinalIgnoreCase));
                    foreach (var doc in all is null ? docs : [all])
                        files.Add(new SourceFile($"{name}\\{doc.Path}", Parser.ParseSource(doc.Text, skipBodies: true)));
                }
                catch (Exception ex) when (ex is ScriptSyntaxException or InvalidDataException or NotSupportedException or IOException)
                {
                    note($"[Skipped] {name}\\{Path.GetFileName(blob)}" + (ex is ScriptSyntaxException syntax ? $" (line {syntax.Line} of the decompiled text)" : "") + $": {ex.Message}");
                }
            }
            if (blobs.Count > 0) return files;

            string scripts = Path.Combine(content, "scripts");
            if (!Directory.Exists(scripts)) return files;
            foreach (string path in Directory.EnumerateFiles(scripts, "*.ws", SearchOption.AllDirectories))
            {
                try
                {
                    files.Add(new SourceFile($"{name}\\{Path.GetRelativePath(content, path)}",
                        Parser.ParseSource(TextDecoding.Decode(File.ReadAllBytes(path)), skipBodies: true)));
                }
                catch (ScriptSyntaxException ex)
                {
                    note($"[Skipped] {name}\\{Path.GetRelativePath(content, path)}[{ex.Line}]: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note($"[Skipped] {name}: {ex.Message}");
        }
        return files;
    }
}
