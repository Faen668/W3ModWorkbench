using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>
/// A <see cref="Cr2wRecipe"/> as a script to keep and run outside the app: the command line it stands for, written
/// out as a Python script or a batch file with its files, folders and edits at the top to change. Both run the app's
/// own command line (<see cref="CommandLine"/>), so they do exactly what the window does.
/// </summary>
public static class Cr2wRecipeExport
{
    /// <summary>The command for the recipe's kind: --to-json, --from-json, --resave-164 or --edit.</summary>
    public static string Command(Cr2wRecipeKind kind) => kind switch // loc-ignore-start
    {
        Cr2wRecipeKind.FromJson => "--from-json",
        Cr2wRecipeKind.Resave => "--resave-164",
        Cr2wRecipeKind.Edit => "--edit",
        _ => "--to-json",
    }; // loc-ignore-end

    /// <summary>One edit's arguments, as the command line takes them.</summary>
    public static List<string> EditArguments(Cr2wRecipeEdit edit) // loc-ignore-start
    {
        var args = edit.Kind switch
        {
            BulkEditKind.Copy => new List<string> { "--copy", edit.Source ?? "", edit.Path },
            BulkEditKind.Set => ["--set", edit.Path, edit.Value?.ToJsonString(Relaxed) ?? "null"],
            BulkEditKind.Remove => ["--remove", edit.Path],
            BulkEditKind.Scale => ["--scale", edit.Path, Cr2wRecipe.Number(edit.Amount)],
            _ => ["--offset", edit.Path, Cr2wRecipe.Number(edit.Amount)],
        };
        if (edit.Kind is BulkEditKind.Scale or BulkEditKind.Offset && edit.Fields.Count > 0) args.AddRange(["--fields", string.Join(",", edit.Fields)]);
        if (edit.ClassName.Length > 0) args.AddRange(["--only-class", edit.ClassName]);
        return args;
    } // loc-ignore-end

    /// <summary>The options after the files and edits: where results go, subfolders, replacing, version 164.</summary>
    public static List<string> OptionArguments(Cr2wRecipe recipe) // loc-ignore-start
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(recipe.Output)) args.AddRange(["--out", recipe.Output]);
        if (recipe.Recursive) args.Add("--recursive");
        if (!string.IsNullOrWhiteSpace(recipe.Only)) args.AddRange(["--match", recipe.Only]);
        if (!string.IsNullOrWhiteSpace(recipe.Skip)) args.AddRange(["--skip", recipe.Skip]);
        if (recipe.Overwrite && recipe.Kind != Cr2wRecipeKind.Edit) args.Add("--overwrite");
        if (recipe.AsLatest && recipe.Kind == Cr2wRecipeKind.FromJson) args.Add("--as-164");
        return args;
    } // loc-ignore-end

    /// <summary>The whole command line, without the program.</summary>
    public static List<string> Arguments(Cr2wRecipe recipe) =>
        [Command(recipe.Kind), .. recipe.Inputs, .. recipe.Edits.SelectMany(EditArguments), .. OptionArguments(recipe)];

    // ---------------------------------------------------------------- Python

    /// <summary>A Python 3 script that runs the recipe through the app at <paramref name="program"/>.</summary>
    public static string ToPython(Cr2wRecipe recipe, string program)
    {
        var s = new StringBuilder();
        Comment(s, "# ", recipe);
        s.AppendLine();
        s.AppendLine("import subprocess"); // loc-ignore
        s.AppendLine("import sys"); // loc-ignore
        s.AppendLine();
        s.AppendLine($"# {L.T("Cr2wRecipeExport.ProgramComment")}");
        s.AppendLine($"WORKBENCH = {Py(program)}");
        s.AppendLine();
        s.AppendLine($"# {L.T("Cr2wRecipeExport.InputsComment")}");
        s.AppendLine("INPUTS = [");
        foreach (string input in recipe.Inputs) s.AppendLine($"    {Py(input)},");
        s.AppendLine("]");
        if (recipe.Kind == Cr2wRecipeKind.Edit)
        {
            s.AppendLine();
            s.AppendLine($"# {L.T("Cr2wRecipeExport.EditsComment")}");
            s.AppendLine("EDITS = [");
            foreach (var edit in recipe.Edits) s.AppendLine($"    [{string.Join(", ", EditArguments(edit).Select(Py))}],");
            s.AppendLine("]");
        }
        s.AppendLine();
        s.AppendLine($"# {L.T("Cr2wRecipeExport.OptionsComment")}");
        s.AppendLine($"OPTIONS = [{string.Join(", ", OptionArguments(recipe).Select(Py))}]");
        s.AppendLine();
        s.AppendLine();
        // loc-ignore-start
        s.AppendLine("def main():");
        s.AppendLine($"    args = [WORKBENCH, {Py(Command(recipe.Kind))}, *INPUTS]");
        if (recipe.Kind == Cr2wRecipeKind.Edit)
        {
            s.AppendLine("    for edit in EDITS:");
            s.AppendLine("        args += edit");
        }
        s.AppendLine("    args += OPTIONS");
        s.AppendLine("    args += sys.argv[1:]");
        s.AppendLine("    return subprocess.run(args).returncode");
        s.AppendLine();
        s.AppendLine();
        s.AppendLine("if __name__ == \"__main__\":");
        s.AppendLine("    sys.exit(main())");
        // loc-ignore-end
        return s.ToString();
    }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// A Python string literal: raw (r"D:\mods") for a path it can be written that way, else with escapes. JSON's
    /// escapes are Python's too.
    /// </summary>
    public static string Py(string text)
    {
        if (text.Length > 0 && !text.EndsWith('\\') && !text.Contains('"') && !text.Any(char.IsControl) && text.Contains('\\')) return $"r\"{text}\"";
        return JsonSerializer.Serialize(text, Relaxed);
    }

    // ---------------------------------------------------------------- batch file

    /// <summary>A batch file that runs the recipe through the app at <paramref name="program"/>; it passes on what it's given (--check).</summary>
    public static string ToBatch(Cr2wRecipe recipe, string program)
    {
        var s = new StringBuilder();
        s.AppendLine("@echo off"); // loc-ignore
        Comment(s, "rem ", recipe);
        // loc-ignore-start
        s.AppendLine("setlocal");
        s.AppendLine($"set \"WORKBENCH={Percent(program)}\"");
        s.AppendLine();
        // UTF-8 for paths outside the console's code page, and the console's own put back after
        s.AppendLine("for /f \"tokens=2 delims=:.\" %%a in ('chcp') do set \"CODEPAGE=%%a\"");
        s.AppendLine("chcp 65001 >nul");
        s.Append("start \"\" /wait \"%WORKBENCH%\"");
        foreach (var line in Lines(recipe))
        {
            s.AppendLine(" ^");
            s.Append("  ").Append(string.Join(" ", line.Select(Cmd)));
        }
        s.AppendLine(" %*");
        s.AppendLine("set \"RESULT=%ERRORLEVEL%\"");
        s.AppendLine("chcp %CODEPAGE% >nul");
        s.AppendLine("exit /b %RESULT%");
        // loc-ignore-end
        return s.ToString();
    }

    /// <summary>The command line in lines to read: the command and each file, each edit, then the options.</summary>
    private static IEnumerable<List<string>> Lines(Cr2wRecipe recipe)
    {
        yield return [Command(recipe.Kind)];
        foreach (string input in recipe.Inputs) yield return [input];
        foreach (var edit in recipe.Edits) yield return EditArguments(edit);
        if (OptionArguments(recipe) is { Count: > 0 } options) yield return options;
    }

    /// <summary>
    /// An argument for a batch file: quoted the way the program reads its command line (backslashes before a quote
    /// doubled, quotes escaped), then with cmd's own characters escaped where cmd would see them outside quotes, and
    /// percent signs doubled.
    /// </summary>
    public static string Cmd(string arg)
    {
        bool plain = arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '\\' or '/' or '[' or ']');
        string quoted = plain ? arg : Quote(arg);
        var s = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in quoted)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && c is '^' or '&' or '|' or '<' or '>' or '(' or ')') s.Append('^');
            s.Append(c == '%' ? "%%" : c.ToString());
        }
        return s.ToString();
    }

    /// <summary>An argument quoted for the Windows command line, as CommandLineToArgvW reads it back.</summary>
    private static string Quote(string arg)
    {
        var s = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { s.Append('\\', slashes * 2 + 1); slashes = 0; s.Append('"'); continue; }
            s.Append('\\', slashes);
            slashes = 0;
            s.Append(c);
        }
        s.Append('\\', slashes * 2);
        return s.Append('"').ToString();
    }

    private static string Percent(string text) => text.Replace("%", "%%");

    /// <summary>The script's opening comment: the recipe's name and notes, where it came from, and how to run it.</summary>
    private static void Comment(StringBuilder s, string prefix, Cr2wRecipe recipe)
    {
        var lines = new List<string> { L.T("Cr2wRecipeExport.Title", recipe.Name.ReplaceLineEndings(" ")) };
        lines.AddRange(recipe.Notes.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        lines.Add(L.T("Cr2wRecipeExport.Made", DateTime.Now));
        lines.Add(L.T(recipe.Kind == Cr2wRecipeKind.Edit ? "Cr2wRecipeExport.HowToRunEdit" : "Cr2wRecipeExport.HowToRun"));
        // a batch file expands %names% even in a comment
        foreach (string line in lines) s.AppendLine(prefix + (prefix == "rem " ? Percent(line) : line));
    }
}
