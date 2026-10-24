using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench;

/// <summary>
/// The tool run from a command line or a script, with no window: converting CR2W files to JSON and back, bulk
/// edits, and voice lines, lip syncs and strings (CommandLine.Speech.cs). Anything else on the command line opens the
/// window as usual.
/// <code>
/// W3ModWorkbench.exe --to-json   &lt;files or folders…&gt; [--out folder] [--recursive] [--overwrite]
/// W3ModWorkbench.exe --from-json &lt;files or folders…&gt; [--out folder] [--recursive] [--overwrite] [--as-164]
/// W3ModWorkbench.exe --resave-164 &lt;files or folders…&gt; [--out folder] [--recursive] [--overwrite]
/// W3ModWorkbench.exe --edit      &lt;files or folders…&gt; edits… [--class name] [--out folder] [--recursive] [--check]
///   edits: --copy &lt;source file&gt; &lt;path&gt;   --set &lt;path&gt; &lt;json&gt;   --remove &lt;path&gt;
///          --scale &lt;path&gt; &lt;factor&gt;   --offset &lt;path&gt; &lt;amount&gt;   (--fields a,b after a scale or offset)
///          --only-class &lt;name&gt; after an edit: that edit only to objects of the class
/// W3ModWorkbench.exe --recipe    &lt;recipe file&gt; [--check]
///   any of them: --match &lt;regex&gt;, --skip &lt;regex&gt; for the files taken from folders
/// W3ModWorkbench.exe --speech-unpack &lt;files&gt; | --speech-pack &lt;folders&gt; | --lipsync &lt;files or folders&gt; --to re|raw
/// W3ModWorkbench.exe --strings-to-csv &lt;files&gt; [--layout radish|redkit] | --csv-to-strings &lt;files&gt;
/// W3ModWorkbench.exe --help [topic]   (-h, /?; topics: commands, options, filter, edits, recipes, voice, examples)
/// </code>
/// </summary>
public static partial class CommandLine
{
    private static readonly string[] Commands = ["--to-json", "--from-json", "--resave-164", "--edit", "--recipe", "--help", "-h", "/?", "-?", "--help-json"];

    public static bool Handles(string[] args) => args.Length > 0 && (Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase) || SpeechCommands.Contains(args[0], StringComparer.OrdinalIgnoreCase));

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr file);

    /// <summary>Whether the output is going to a file or a pipe already: a script reading it (Python's subprocess, a redirect).</summary>
    private static bool OutputRedirected() => GetFileType(GetStdHandle(-11)) is 1 or 3;

    /// <summary>Runs the command; the exit code is 0 when every file was done, 1 when some weren't, 2 for a command that can't run.</summary>
    public static int Run(string[] args)
    {
        // a windowed program has no console of its own: write to the one it was started from, if any, unless the
        // output is being read already (attaching would send it to the console instead)
        bool redirected = OutputRedirected();
        bool console = !redirected && AttachConsole(-1);
        if (redirected) Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var output = Console.Out;
        // in a terminal, a line that keeps saying how far it is; a script reading the output gets only the results
        var progress = console ? new ConsoleProgress(output) : null;
        output.WriteLine();
        try
        {
            string command = args[0].ToLowerInvariant();
            if (command is "--help" or "-h" or "/?" or "-?" or "--help-json") return Help(output, args.Length > 1 ? args[1] : null);
            if (SpeechCommands.Contains(command)) return RunSpeech(command, args, output);

            var paths = new List<string>();
            var edits = new List<Cr2wBulkEdit>();
            string? outputFolder = null, className = null, match = null, skip = null;
            bool recursive = false, overwrite = false, asLatest = false, check = false;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value after it.");
                switch (a.ToLowerInvariant())
                {
                    case "--out": outputFolder = Next(); break;
                    case "--recursive" or "-r": recursive = true; break;
                    case "--overwrite": overwrite = true; break;
                    case "--as-164": asLatest = true; break;
                    case "--class": className = Next(); break;
                    case "--check": check = true; break;
                    case "--match": match = Next(); break;
                    case "--skip": skip = Next(); break;
                    case "--only-class":
                    {
                        string only = Next();
                        if (edits.Count == 0) throw new ArgumentException("--only-class goes after an edit.");
                        edits[^1] = With(edits[^1], className: only);
                        break;
                    }
                    case "--copy":
                    {
                        string source = Next();
                        edits.Add(new Cr2wBulkEdit { Kind = BulkEditKind.Copy, Path = Next(), Source = Cr2wJsonBatch.Load(Path.GetFullPath(source)) });
                        break;
                    }
                    case "--set":
                    {
                        string path = Next(), value = Next();
                        edits.Add(new Cr2wBulkEdit { Kind = BulkEditKind.Set, Path = path, Value = ParseValue(value) });
                        break;
                    }
                    case "--remove": edits.Add(new Cr2wBulkEdit { Kind = BulkEditKind.Remove, Path = Next() }); break;
                    case "--scale" or "--offset":
                    {
                        string path = Next(), amount = Next();
                        edits.Add(new Cr2wBulkEdit
                        {
                            Kind = a.Equals("--scale", StringComparison.OrdinalIgnoreCase) ? BulkEditKind.Scale : BulkEditKind.Offset,
                            Path = path,
                            Amount = double.Parse(amount, CultureInfo.InvariantCulture),
                        });
                        break;
                    }
                    case "--fields":
                    {
                        string fields = Next();
                        if (edits.Count == 0 || edits[^1].Kind is not (BulkEditKind.Scale or BulkEditKind.Offset))
                            throw new ArgumentException("--fields goes after a --scale or --offset.");
                        edits[^1] = With(edits[^1], fields: fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    }
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{a} isn't an option. Run --help for the options.");
                        paths.Add(a);
                        break;
                }
            }
            var filter = Cr2wFileFilter.Make(match, skip);
            if (check && command is not ("--edit" or "--recipe")) throw new ArgumentException("--check goes with --edit or --recipe: converting has nothing to try first.");
            if (command == "--recipe")
            {
                if (paths.Count != 1) throw new ArgumentException("--recipe takes one recipe file.");
                var recipe = Cr2wRecipe.Load(Path.GetFullPath(paths[0]));
                if (check && recipe.Kind != Cr2wRecipeKind.Edit) throw new ArgumentException("--check goes with a recipe of edits: converting has nothing to try first.");
                // a filter given here takes the place of the recipe's own
                if (match is not null) recipe.Only = match;
                if (skip is not null) recipe.Skip = skip;
                output.WriteLine(recipe.Name);
                var ran = recipe.Run(progress, dryRun: check);
                progress?.Clear();
                return Report(output, ran, check,
                    recipe.Kind == Cr2wRecipeKind.FromJson ? NoJson : recipe.Kind == Cr2wRecipeKind.Edit ? NoEditable : NoCr2w);
            }
            if (paths.Count == 0) throw new ArgumentException("Give the files or folders to work on. Run --help for the options.");
            if (className is not null)
                edits = [.. edits.Select(e => e.ClassName.Length > 0 ? e : With(e, className: className))];
            if (command == "--edit" && edits.Count == 0) throw new ArgumentException("--edit needs an edit: --copy, --set, --remove, --scale or --offset.");

            // an edit takes converted JSON files from folders too, as the window does
            var inputs = command == "--edit" ? Cr2wRecipe.EditInputs(paths, recursive, progress, filter: filter)
                : Cr2wJsonBatch.Inputs(paths, fromJson: command == "--from-json", recursive, progress, filter: filter);
            var results = inputs.Count == 0 ? null : command switch
            {
                "--to-json" => Cr2wJsonBatch.ToJson(inputs, outputFolder, overwrite, progress),
                "--from-json" => Cr2wJsonBatch.FromJson(inputs, outputFolder, overwrite, progress, asLatest: asLatest),
                "--resave-164" => Cr2wJsonBatch.ResaveAsLatest(inputs, outputFolder, overwrite, progress),
                _ => Cr2wJsonBatch.BulkEdit(inputs, edits, outputFolder, progress, dryRun: check),
            };
            progress?.Clear();
            return Report(output, results, check, command == "--from-json" ? NoJson : command == "--edit" ? NoEditable : NoCr2w);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            output.WriteLine(ex.Message);
            return 2;
        }
        finally
        {
            output.Flush();
        }
    }

    private const string NoJson = "No converted JSON files were found there.";
    private const string NoCr2w = "No CR2W files were found there.";
    private const string NoEditable = "No CR2W or converted JSON files were found there.";

    /// <summary>How far a command is, as one line in the terminal written over each time (it comes ten times a second at most).</summary>
    private sealed class ConsoleProgress(TextWriter output) : IProgress<Cr2wJsonProgress>
    {
        private readonly object _lock = new();
        private int _width;

        public void Report(Cr2wJsonProgress p)
        {
            string line = p.Stage switch // loc-ignore-start
            {
                Cr2wJsonStage.Finding => $"Finding files: {p.Done:N0} so far",
                Cr2wJsonStage.Checking => $"Looking at each file: {p.Done:N0} of {p.Total:N0}",
                _ => $"{p.Done:N0} of {p.Total:N0}  {Path.GetFileName(p.Current)}",
            }; // loc-ignore-end
            lock (_lock)
            {
                output.Write("\r" + line.PadRight(_width));
                _width = line.Length;
            }
        }

        /// <summary>Clears the line, for the results to be written from the start of it.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                if (_width > 0) output.Write("\r" + new string(' ', _width) + "\r");
                _width = 0;
            }
        }
    }

    /// <summary>Writes what happened to each file and the totals; the exit code is 0 when every file was done.</summary>
    private static int Report(TextWriter output, List<Cr2wJsonResult>? results, bool check, string noneFound)
    {
        if (results is null)
        {
            output.WriteLine(noneFound);
            return 1;
        }
        foreach (var r in results)
        {
            if (!r.Ok) output.WriteLine($"FAILED  {r.Input}: {r.Error}");
            else if (r.Output is null) output.WriteLine($"skipped {r.Input}: {r.Note}");
            else output.WriteLine($"{(check ? "would  " : "ok     ")} {r.Output}{(r.Note is null ? "" : $"  ({r.Note})")}");
        }
        int failed = results.Count(r => !r.Ok);
        output.WriteLine($"{results.Count(r => r.Ok && r.Output is not null)} {(check ? "would be written" : "written")}, {results.Count(r => r.Ok && r.Output is null)} skipped, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>An edit with its class or its fields changed.</summary>
    private static Cr2wBulkEdit With(Cr2wBulkEdit e, string? className = null, IReadOnlyList<string>? fields = null) => new()
    {
        Kind = e.Kind,
        Path = e.Path,
        ClassName = className ?? e.ClassName,
        Source = e.Source,
        Value = e.Value,
        Amount = e.Amount,
        Fields = fields ?? e.Fields,
        AddMissing = e.AddMissing,
    };

    /// <summary>A value for --set: JSON when it reads as JSON (1.5, true, "text", {…}), else the text itself.</summary>
    private static JsonNode? ParseValue(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(text); }
    }

    /// <summary>Writes the help: all of it, or one topic of it. An unknown topic lists the topics.</summary>
    private static int Help(TextWriter output, string? topic)
    {
        if (topic is null)
        {
            output.WriteLine(Title);
            foreach (var (_, text) in Topics) output.WriteLine(text);
            return 0;
        }
        var found = Topics.Where(t => t.Name.StartsWith(topic.TrimStart('-', '/').ToLowerInvariant(), StringComparison.Ordinal)).ToList();
        if (found.Count == 0)
        {
            output.WriteLine($"There's no help topic \"{topic}\". The topics are: {string.Join(", ", Topics.Select(t => t.Name))}.");
            return 2;
        }
        foreach (var (_, text) in found) output.WriteLine(text);
        return 0;
    }

    // loc-ignore-start
    private const string Title = """
        W3 Mod Workbench: CR2W files (.env, .w2mi, .w2ent…) to JSON and back, the same edit to many at once, and voice
        lines, lip syncs and strings.
        --help <topic> shows one part: commands, options, filter, edits, recipes, voice, examples.

        """;

    private static readonly (string Name, string Text)[] Topics =
    [
        ("commands", """
            Commands
              --to-json   <files or folders…>  Write name.env.json beside each CR2W file (or under --out)
              --from-json <files or folders…>  Write name.env back from each name.env.json (or under --out)
              --resave-164 <files or folders…> Save older CR2W files (REDkit's go back to version 120) as version 164,
                                               as the engine would after loading them (under --out, or over themselves with --overwrite)
              --edit      <files or folders…>  Make edits to CR2W or JSON files; they're written over themselves, or under --out
              --recipe    <recipe file>        Run a recipe saved in the CR2W and JSON window (a .w3recipe.json file)
              --speech-unpack, --speech-pack, --wav-to-wem, --lipsync, --lipsync-from-audio, --learn-lipsync,
              --strings-to-csv, --csv-to-strings
                                               Voice lines, lip syncs and strings (see: --help voice)
              --help [topic], -h, /?           This help, or one topic of it

            """),
        ("options", """
            Options
              --out <folder>     Write the results under this folder, keeping the folders they were in
              --recursive, -r    Look in subfolders of the folders given
              --overwrite        Replace files that are there already (to and from JSON, and resaving)
              --as-164           With --from-json: write files of older versions as version 164
              --class <name>     Edit only objects of this class (CEnvironmentDefinition)
              --check            With --edit or --recipe: try the edits and say what would change, saving nothing
              --match <regex>    Only the files in folders whose path matches (see: --help filter)
              --skip <regex>     Not the files in folders whose path matches

            """),
        ("filter", """
            Choosing files in folders with --match and --skip
              Each is a regular expression, tested against a file's path below the folder given, with / between
              folders (quests/main_quests/q101.w2scene). It matches anywhere in the path unless anchored with ^ or $,
              and ignores case. Files given one by one are always taken. With --recipe, they replace the recipe's own.
                --match "\.w2scene$"            only scenes
                --match ".*w2scene"             the same, written the other way
                --match "^quests/"              only what's in the quests folder
                --skip "(^|/)thumbnails/"       not REDkit's thumbnails
                --match "\.(env|w2mi)$"         environments and materials

            """),
        ("edits", """
            Edits, made in order; a path is property names from an object down, with [n] for an item of a list
              --copy <source> <path>     The source file's value at the path, into every file
              --set <path> <json>        A value: 1.5, true, "text", or a JSON object or list
              --remove <path>            Remove the property, so the game uses its default
              --scale <path> <factor>    Multiply the numbers under the path...
              --offset <path> <amount>   ...or add to them
              --fields <a,b>             After --scale or --offset: only numbers in these properties (value, X, Y, Z)
              --only-class <name>        After an edit: make that edit only to objects of this class

            """),
        ("recipes", """
            Recipes
              A recipe is a form from the CR2W and JSON window saved to run again (Save as recipe; the Recipes tab).
              They're kept in %AppData%\W3ModWorkbench\recipes, or the folder set in Settings > General.
              --recipe <file> runs one; --check tries a recipe of edits first; --match and --skip replace its filter.

            """),
        ("voice", VoiceHelp),
        ("examples", """
            Examples
              W3ModWorkbench.exe --to-json "D:\envs" -r --out "D:\envs json"
              W3ModWorkbench.exe --to-json "F:\REDkit\r4data" -r --match "\.w2scene$" --out "D:\scenes json"
              W3ModWorkbench.exe --edit "D:\mymod\envs" -r --copy "D:\envs\my_look.env" envParams.m_toneMapping
              W3ModWorkbench.exe --edit "D:\mymod\envs" -r --scale envParams.m_globalFog.fogDensity 0.8 --fields value
              W3ModWorkbench.exe --recipe "%AppData%\W3ModWorkbench\recipes\Night fog.w3recipe.json" --check
              W3ModWorkbench.exe --speech-unpack "D:\mymod\content\enpc.w3speech" --re
              W3ModWorkbench.exe --lipsync "D:\mymod\workspace\speech" -r --to raw
              W3ModWorkbench.exe --strings-to-csv en.w3strings de.w3strings --layout redkit --out "D:\redkit\strings.csv"
            """),
    ];
    // loc-ignore-end
}
