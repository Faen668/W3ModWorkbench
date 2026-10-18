using System.IO;
using W3ModWorkbench.Core.Scripts.Standalone;

namespace W3ModWorkbench.Core.Scripts;

public enum CompilerKind { WccLite, BuiltIn }

/// <summary>
/// Which compiler builds a script project. REDkit's wcc_lite is the game's own and is used whenever it's set;
/// without it, or when asked for, the built-in compiler does the job from the game's scripts alone.
/// </summary>
public sealed record CompilerChoice(CompilerKind Kind, string? WccLite, string GameScripts, string? ModsFolder = null)
{
    public string Name => Kind == CompilerKind.WccLite ? "wcc_lite" : "the built-in compiler";

    /// <summary>The compiler to use, or null when neither can run: no REDkit, and no game scripts for the built-in one.</summary>
    public static CompilerChoice? Pick(string? wccLitePath, string? gameFolder, bool preferBuiltIn)
    {
        bool hasWcc = ScriptCompiler.IsWccLite(wccLitePath ?? "");
        if (hasWcc && !preferBuiltIn)
            return new CompilerChoice(CompilerKind.WccLite, wccLitePath, ScriptCompiler.GameScriptsFor(wccLitePath!)!);

        string? folder = string.IsNullOrWhiteSpace(gameFolder) ? GameLocator.Find() : gameFolder;
        if (StandaloneCompiler.FindGameScripts(hasWcc ? wccLitePath : null, folder) is not { } scripts) return null;
        string? mods = folder is null ? null : Path.Combine(folder, "mods");
        return new CompilerChoice(CompilerKind.BuiltIn, hasWcc ? wccLitePath : null, scripts, mods is not null && Directory.Exists(mods) ? mods : null);
    }

    /// <summary>Why nothing can compile, for a tooltip or a message.</summary>
    public const string NothingToCompileWith =
        "Compiling needs either REDkit's wcc_lite.exe or the game's scripts. Set wcc_lite in Settings (Ctrl+, in the main window), "
        + "or set the game folder there so the built-in compiler can find the game's scripts";

    /// <summary>Compiles the project's .ws files and, when they compile, saves the blob to <paramref name="output"/>.</summary>
    public Task<CompileResult> CompileAsync(string scriptsFolder, string output, IProgress<string>? progress, CancellationToken cancel) =>
        Kind == CompilerKind.WccLite
            ? ScriptCompiler.CompileAsync(WccLite!, scriptsFolder, output, progress, cancel)
            : StandaloneCompiler.CompileAsync(scriptsFolder, GameScripts, output, progress, cancel, ModsFolder);
}
