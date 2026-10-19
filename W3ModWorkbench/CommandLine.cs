using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench;

/// <summary>
/// The tool run from a command line or a script, with no window: converting CR2W files to JSON and back, and bulk
/// edits. Anything else on the command line opens the window as usual.
/// <code>
/// W3ModWorkbench.exe --to-json   &lt;files or folders…&gt; [--out folder] [--recursive] [--overwrite]
/// W3ModWorkbench.exe --from-json &lt;files or folders…&gt; [--out folder] [--recursive] [--overwrite]
/// W3ModWorkbench.exe --edit      &lt;files or folders…&gt; edits… [--class name] [--out folder] [--recursive]
///   edits: --copy &lt;source file&gt; &lt;path&gt;   --set &lt;path&gt; &lt;json&gt;   --remove &lt;path&gt;
///          --scale &lt;path&gt; &lt;factor&gt;   --offset &lt;path&gt; &lt;amount&gt;   (--fields a,b after a scale or offset)
/// </code>
/// </summary>
public static class CommandLine
{
    private static readonly string[] Commands = ["--to-json", "--from-json", "--edit", "--help-json"];

    public static bool Handles(string[] args) => args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>Runs the command; the exit code is 0 when every file was done, 1 when some weren't, 2 for a command that can't run.</summary>
    public static int Run(string[] args)
    {
        // a windowed program has no console of its own: write to the one it was started from, if any
        AttachConsole(-1);
        var output = Console.Out;
        output.WriteLine();
        try
        {
            string command = args[0].ToLowerInvariant();
            if (command == "--help-json") { output.WriteLine(Usage); return 0; }

            var paths = new List<string>();
            var edits = new List<Cr2wBulkEdit>();
            string? outputFolder = null, className = null;
            bool recursive = false, overwrite = false;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value after it.");
                switch (a.ToLowerInvariant())
                {
                    case "--out": outputFolder = Next(); break;
                    case "--recursive" or "-r": recursive = true; break;
                    case "--overwrite": overwrite = true; break;
                    case "--class": className = Next(); break;
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
                        var last = edits[^1];
                        edits[^1] = new Cr2wBulkEdit { Kind = last.Kind, Path = last.Path, Amount = last.Amount, Fields = fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
                        break;
                    }
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{a} isn't an option. Run --help-json for the options.");
                        paths.Add(a);
                        break;
                }
            }
            if (paths.Count == 0) throw new ArgumentException("Give the files or folders to work on. Run --help-json for the options.");
            if (className is not null)
                edits = [.. edits.Select(e => new Cr2wBulkEdit { Kind = e.Kind, Path = e.Path, ClassName = className, Source = e.Source, Value = e.Value, Amount = e.Amount, Fields = e.Fields })];
            if (command == "--edit" && edits.Count == 0) throw new ArgumentException("--edit needs an edit: --copy, --set, --remove, --scale or --offset.");

            var inputs = Cr2wJsonBatch.Inputs(paths, fromJson: command == "--from-json", recursive);
            if (inputs.Count == 0)
            {
                output.WriteLine(command == "--from-json" ? "No converted JSON files were found there." : "No CR2W files were found there.");
                return 1;
            }
            var results = command switch
            {
                "--to-json" => Cr2wJsonBatch.ToJson(inputs, outputFolder, overwrite),
                "--from-json" => Cr2wJsonBatch.FromJson(inputs, outputFolder, overwrite),
                _ => Cr2wJsonBatch.BulkEdit(inputs, edits, outputFolder),
            };
            foreach (var r in results)
            {
                if (!r.Ok) output.WriteLine($"FAILED  {r.Input}: {r.Error}");
                else if (r.Output is null) output.WriteLine($"skipped {r.Input}: {r.Note}");
                else output.WriteLine($"ok      {r.Output}{(r.Note is null ? "" : $"  ({r.Note})")}");
            }
            int failed = results.Count(r => !r.Ok);
            output.WriteLine($"{results.Count(r => r.Ok && r.Output is not null)} written, {results.Count(r => r.Ok && r.Output is null)} skipped, {failed} failed.");
            return failed == 0 ? 0 : 1;
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

    /// <summary>A value for --set: JSON when it reads as JSON (1.5, true, "text", {…}), else the text itself.</summary>
    private static JsonNode? ParseValue(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(text); }
    }

    private const string Usage = """
        W3 Mod Workbench: CR2W files (.env, .w2mi, .w2ent…) to JSON and back

          --to-json   <files or folders…>  Write name.env.json beside each CR2W file (or under --out)
          --from-json <files or folders…>  Write name.env back from each name.env.json (or under --out)
          --edit      <files or folders…>  Make edits to CR2W or JSON files; they're written over themselves, or under --out

        Options
          --out <folder>     Write the results under this folder, keeping the folders they were in
          --recursive, -r    Look in subfolders of the folders given
          --overwrite        Replace files that are there already (to and from JSON)
          --class <name>     Edit only objects of this class (CEnvironmentDefinition)

        Edits, made in order; a path is property names from an object down, with [n] for an item of a list
          --copy <source> <path>     The source file's value at the path, into every file
          --set <path> <json>        A value: 1.5, true, "text", or a JSON object or list
          --remove <path>            Remove the property, so the game uses its default
          --scale <path> <factor>    Multiply the numbers under the path...
          --offset <path> <amount>   ...or add to them
          --fields <a,b>             After --scale or --offset: only numbers in these properties (value, X, Y, Z)

        Examples
          W3ModWorkbench.exe --to-json "D:\envs" -r --out "D:\envs json"
          W3ModWorkbench.exe --edit "D:\mymod\envs" -r --copy "D:\envs\my_look.env" envParams.m_toneMapping
          W3ModWorkbench.exe --edit "D:\mymod\envs" -r --scale envParams.m_globalFog.fogDensity 0.8 --fields value
        """;
}
