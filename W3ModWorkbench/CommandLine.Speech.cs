using System.IO;
using System.Text;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Core.Sounds;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench;

// loc-ignore-start
/// <summary>
/// The voice line and strings commands: speech files unpacked and packed, lip syncs converted, and strings files to
/// CSV (the radish layout or REDkit's) and back. Each writes a line for each file and the totals, as the CR2W
/// commands do.
/// </summary>
public static partial class CommandLine
{
    private static readonly string[] SpeechCommands = ["--speech-unpack", "--speech-pack", "--lipsync", "--strings-to-csv", "--csv-to-strings", "--wav-to-wem", "--lipsync-from-audio", "--learn-lipsync"];

    /// <summary>The options the voice line and strings commands take, read the same way for each.</summary>
    private sealed class SpeechOptions
    {
        public List<string> Paths { get; } = [];
        public string? Out, Language, To, Layout;
        public bool Wav, Re, Keep, Recursive, Overwrite, MakeLipSync;
        public string? Game = Core.GameLocator.Resolve(null);
        public int Kbps = App.Settings.SpeechKbps;
    }

    private static SpeechOptions ReadSpeechOptions(string[] args)
    {
        var o = new SpeechOptions();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value after it.");
            switch (a.ToLowerInvariant())
            {
                case "--out": o.Out = Next(); break;
                case "--language": o.Language = Next(); break;
                case "--to": o.To = Next().TrimStart('.').ToLowerInvariant(); break;
                case "--layout": o.Layout = Next().ToLowerInvariant(); break;
                case "--wav": o.Wav = true; break;
                case "--re": o.Re = true; break;
                case "--keep": o.Keep = true; break;
                case "--make-lipsync": o.MakeLipSync = true; break;
                case "--game": o.Game = Next(); break;
                case "--recursive" or "-r": o.Recursive = true; break;
                case "--overwrite": o.Overwrite = true; break;
                case "--kbps": o.Kbps = int.TryParse(Next(), out int k) && k is >= 6 and <= 256 ? k : throw new ArgumentException("--kbps is the bitrate a channel in kbit/s, 6 to 256 (the game's voice lines are near 64)."); break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{a} isn't an option of {args[0]}. Run --help voice for its options.");
                    o.Paths.Add(a);
                    break;
            }
        }
        if (o.Paths.Count == 0 && args[0] != "--learn-lipsync") throw new ArgumentException($"{args[0]} needs the files or folders to work on. Run --help voice for its options.");
        return o;
    }

    /// <summary>The lip sync model learnt here, learning it from the game first (printing how far it's got) when there's none yet.</summary>
    private static LipSyncModel LipSyncModelFor(SpeechOptions o, TextWriter output)
    {
        if (LipSyncModel.Load(LipSyncModel.DefaultPath) is { } saved) return saved;
        if (string.IsNullOrEmpty(o.Game) || !Directory.Exists(o.Game))
            throw new ArgumentException("Lip sync is learnt from the game's voice lines, and the game wasn't found: give its folder with --game, or set it in Settings.");
        output.WriteLine("Learning lip sync from the game's voice lines (once; about ten minutes)...");
        output.Flush();
        // each stage as it comes, flushed, so a script reading the output sees how far it's got
        string last = "";
        var gate = new object();
        var progress = new LineProgress<LipSyncTrainingProgress>(p =>
        {
            lock (gate)
            {
                if (p.Stage == last) return;
                last = p.Stage;
                output.WriteLine($"  {p.Stage}");
                output.Flush();
            }
        });
        return LipSyncTrainer.Learn(o.Game, progress);
    }

    /// <summary>Progress reported as it happens, on the thread that reports it (Progress&lt;T&gt; posts it on later, after the result is written).</summary>
    private sealed class LineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static W3Language LanguageOption(string handle) =>
        W3Strings.Language(handle) ?? throw new ArgumentException($"\"{handle}\" isn't one of the game's languages ({string.Join(", ", W3Strings.Languages.Select(l => l.Handle))}).");

    /// <summary>Runs a voice line or strings command; the exit code is 0 when every file was done, 1 when some weren't.</summary>
    private static int RunSpeech(string command, string[] args, TextWriter output)
    {
        var o = ReadSpeechOptions(args);
        int done = 0, failed = 0;
        void Ok(string line) { output.WriteLine($"ok      {line}"); done++; }
        void Fail(string path, string why) { output.WriteLine($"FAILED  {path}: {why}"); failed++; }

        switch (command)
        {
            case "--speech-unpack":
                foreach (string path in o.Paths)
                {
                    try
                    {
                        if (!File.Exists(path)) throw new ArgumentException("there's no such file.");
                        var language = (o.Language is null ? null : LanguageOption(o.Language)) ?? SpeechFolder.LanguageOf(path)
                            ?? throw new ArgumentException("its name doesn't say its language (enpc.w3speech is English); give one with --language.");
                        string folder = o.Out is null ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", "speech", language.Handle)
                            : o.Paths.Count > 1 ? Path.Combine(o.Out, language.Handle) : o.Out;
                        var (lines, noWav, noRe) = SpeechFolder.Unpack(W3Speech.Open(path, language), folder, o.Wav, o.Re);
                        Ok($"{folder}  ({lines} voice lines{(noWav.Count > 0 ? $"; {noWav.Count} couldn't be made WAV" : "")}{(noRe.Count > 0 ? $"; {noRe.Count} lip syncs kept as they were, not .re" : "")})");
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
                    {
                        Fail(path, ex.Message);
                    }
                }
                break;

            case "--speech-pack":
                if (o.Out is not null && o.Paths.Count > 1) throw new ArgumentException("--out names one speech file: pack one folder at a time with it.");
                foreach (string path in o.Paths)
                {
                    try
                    {
                        string folder = Path.GetFullPath(path).TrimEnd('\\', '/');
                        if (!Directory.Exists(folder)) throw new ArgumentException("there's no such folder.");
                        var language = (o.Language is null ? null : LanguageOption(o.Language)) ?? W3Strings.Language(Path.GetFileName(folder))
                            ?? throw new ArgumentException("the folder's name isn't a language (speech\\en is English); give one with --language.");
                        string parent = Path.GetDirectoryName(folder) ?? folder;
                        if (Path.GetFileName(parent).Equals("speech", StringComparison.OrdinalIgnoreCase)) parent = Path.GetDirectoryName(parent) ?? parent;
                        string file = o.Out ?? Path.Combine(parent, $"{language.Handle}pc.w3speech");
                        var (lines, notes) = SpeechFolder.Read(folder);
                        foreach (string note in notes) output.WriteLine($"note    {note}");
                        if (lines.Count == 0) throw new ArgumentException("there are no ID.wem voice lines in it.");
                        var startFrom = o.Keep && File.Exists(file) ? W3Speech.Open(file, language) : null;
                        var model = o.MakeLipSync && lines.Any(l => l.LipSync is null) ? LipSyncModelFor(o, output) : null;
                        var (total, madeLips) = SpeechFolder.Pack(lines, language, file, startFrom, model, kbps: o.Kbps);
                        Ok($"{file}  ({total} voice lines{(startFrom is null ? "" : $", {lines.Count} from the folder")}{(madeLips > 0 ? $", {madeLips} lip syncs made from their sound" : "")})");
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
                    {
                        Fail(path, ex.Message);
                    }
                }
                break;

            case "--learn-lipsync":
            {
                if (File.Exists(LipSyncModel.DefaultPath)) File.Delete(LipSyncModel.DefaultPath);
                var model = LipSyncModelFor(o, output);
                Ok($"{LipSyncModel.DefaultPath}  ({model.Info.Describe()})");
                break;
            }

            case "--lipsync-from-audio":
            {
                var inputs = new List<(string File, string? Root)>();
                foreach (string path in o.Paths)
                    if (File.Exists(path)) inputs.Add((Path.GetFullPath(path), null));
                    else if (Directory.Exists(path))
                        inputs.AddRange(Directory.EnumerateFiles(path, "*", o.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".wav" or ".wem").Order(StringComparer.OrdinalIgnoreCase)
                            .Select(f => (Path.GetFullPath(f), (string?)Path.GetFullPath(path))));
                if (inputs.Count == 0)
                {
                    output.WriteLine("No .wav or .wem sounds were found there.");
                    return 1;
                }
                var model = LipSyncModelFor(o, output);
                foreach (var (file, root) in inputs)
                {
                    string below = root is null ? "." : Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                    string folder = o.Out is null ? Path.GetDirectoryName(file)! : below == "." ? o.Out : Path.Combine(o.Out, below);
                    string target = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + (o.To == "re" ? ".re" : ".raw"));
                    if (File.Exists(target) && !o.Overwrite) { output.WriteLine($"skipped {file}: {target} is there already (--overwrite replaces it)"); continue; }
                    try
                    {
                        byte[] data = File.ReadAllBytes(file);
                        (short[] samples, int channels) sound;
                        if (Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                        {
                            var wav = WavFile.Read(data).Resampled(WemOpus.SampleRate);
                            sound = (wav.Samples, wav.Channels);
                        }
                        else sound = WwiseOpus.Decode(data);
                        var lip = model.Make(sound.samples, sound.channels);
                        Directory.CreateDirectory(folder);
                        File.WriteAllBytes(target, LipSyncConverter.Write(lip, o.To == "re" ? LipSyncFormat.Re : LipSyncFormat.Raw));
                        Ok($"{target}  ({lip.Duration:0.0} s)");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                    {
                        Fail(file, ex.Message);
                    }
                }
                break;
            }

            case "--wav-to-wem":
            {
                var inputs = new List<(string File, string? Root)>();
                foreach (string path in o.Paths)
                    if (File.Exists(path)) inputs.Add((Path.GetFullPath(path), null));
                    else if (Directory.Exists(path))
                        inputs.AddRange(Directory.EnumerateFiles(path, "*.wav", o.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                            .Order(StringComparer.OrdinalIgnoreCase).Select(f => (Path.GetFullPath(f), (string?)Path.GetFullPath(path))));
                if (inputs.Count == 0)
                {
                    output.WriteLine("No .wav files were found there.");
                    return 1;
                }
                foreach (var (file, root) in inputs)
                {
                    string below = root is null ? "." : Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                    string folder = o.Out is null ? Path.GetDirectoryName(file)! : below == "." ? o.Out : Path.Combine(o.Out, below);
                    string wem = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + ".wem");
                    if (File.Exists(wem) && !o.Overwrite) { output.WriteLine($"skipped {file}: {wem} is there already (--overwrite replaces it)"); continue; }
                    try
                    {
                        byte[] made = WemOpus.FromWav(File.ReadAllBytes(file), o.Kbps);
                        Directory.CreateDirectory(folder);
                        File.WriteAllBytes(wem, made);
                        Ok($"{wem}  ({made.Length / 1024.0:0.#} KB)");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                    {
                        Fail(file, ex.Message);
                    }
                }
                break;
            }

            case "--lipsync":
            {
                var format = o.To switch
                {
                    "re" => LipSyncFormat.Re,
                    "raw" => LipSyncFormat.Raw,
                    _ => throw new ArgumentException("--lipsync needs --to re (REDkit's, for Blender) or --to raw (the game's)."),
                };
                var inputs = LipSyncConverter.Inputs(o.Paths, o.Recursive);
                if (inputs.Count == 0)
                {
                    output.WriteLine("No .raw, .cr2w or .re lip syncs were found there.");
                    return 1;
                }
                foreach (var r in LipSyncConverter.Convert(inputs, format, o.Out, o.Overwrite))
                {
                    if (!r.Ok) Fail(r.Input, r.Error!);
                    else if (r.Output is null) output.WriteLine($"skipped {r.Input}: {r.Note}");
                    else Ok($"{r.Output}{(r.Note is null ? "" : $"  ({r.Note})")}");
                }
                break;
            }

            case "--strings-to-csv":
            {
                bool redkit = o.Layout switch
                {
                    null or "radish" => false,
                    "redkit" => true,
                    _ => throw new ArgumentException("--layout is radish (one language a file, as most mod tools read) or redkit (REDkit's, every language a column)."),
                };
                var files = new List<(string Path, W3StringsFile File)>();
                foreach (string path in o.Paths)
                    try { files.Add((path, W3Strings.Read(File.ReadAllBytes(path), path))); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) { Fail(path, ex.Message); }
                if (redkit && files.Count > 0)
                {
                    // one CSV with a column for each language given
                    string file = o.Out is not null && o.Out.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? o.Out
                        : Path.Combine(o.Out ?? Path.GetDirectoryName(Path.GetFullPath(files[0].Path)) ?? "", "strings_redkit.csv");
                    var dupes = files.GroupBy(f => f.File.Language.Handle).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    if (dupes.Count > 0) throw new ArgumentException($"Two files are in the same language ({string.Join(", ", dupes)}): REDkit's CSV has one column a language.");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                    File.WriteAllText(file, RedkitStringsCsv.Write(files.Select(f => f.File).ToList(), out int keysLeftOut), new UTF8Encoding(false));
                    Ok($"{file}  ({string.Join(", ", files.Select(f => f.File.Language.Handle))}{(keysLeftOut > 0 ? $"; {keysLeftOut} keys known only by their hash left out, which REDkit can't take" : "")})");
                }
                else
                    foreach (var (path, strings) in files)
                    {
                        string file = Path.Combine(o.Out ?? Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", Path.GetFileNameWithoutExtension(path) + ".csv");
                        if (File.Exists(file) && !o.Overwrite) { output.WriteLine($"skipped {path}: {file} is there already (--overwrite replaces it)"); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, W3Strings.ToCsv(strings), new UTF8Encoding(false));
                        Ok($"{file}  ({strings.Strings.Count} strings)");
                    }
                break;
            }

            case "--csv-to-strings":
                foreach (string path in o.Paths)
                {
                    try
                    {
                        string csv = File.ReadAllText(path, Encoding.UTF8);
                        var only = o.Language is null ? null : LanguageOption(o.Language);
                        // REDkit's CSV gives a strings file for each language with text in it; the radish layout gives one
                        var tables = new List<(W3Language Language, List<W3String> Strings)>();
                        List<W3Strings.CsvProblem> problems;
                        if (RedkitStringsCsv.Is(csv))
                        {
                            var (languages, redkitProblems) = RedkitStringsCsv.Read(csv);
                            problems = redkitProblems;
                            tables.AddRange(languages.Where(l => l.Value.Count > 0 && (only is null || l.Key.Handle == only.Handle)).Select(l => (l.Key, l.Value)));
                        }
                        else
                        {
                            var read = W3Strings.FromCsv(csv);
                            problems = read.Problems;
                            var language = only ?? read.Language ?? W3Strings.LanguageFromName(path)
                                ?? throw new ArgumentException("it has no ;meta[language=xx] line and its name isn't a language; give one with --language.");
                            tables.Add((language, read.Strings));
                        }
                        foreach (var p in problems) output.WriteLine($"note    {Path.GetFileName(path)} line {p.Line}: {p.Message}");
                        if (tables.Count == 0) throw new ArgumentException(only is null ? "it has no strings." : $"it has no strings in {only.Handle}.");
                        foreach (var (language, strings) in tables)
                        {
                            var twice = strings.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                            if (twice.Count > 0) throw new ArgumentException($"string IDs are there twice in {language.Handle}: {string.Join(", ", twice.Take(10))}.");
                            string file = Path.Combine(o.Out ?? Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", $"{language.Handle}.w3strings");
                            if (File.Exists(file) && !o.Overwrite) { output.WriteLine($"skipped {language.Handle}: {file} is there already (--overwrite replaces it)"); continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                            File.WriteAllBytes(file, W3Strings.Write(strings, language));
                            Ok($"{file}  ({strings.Count} strings)");
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        Fail(path, ex.Message);
                    }
                }
                break;
        }
        output.WriteLine($"{done} written, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private const string VoiceHelp = """
        Voice lines, lip syncs and strings
          --speech-unpack <file.w3speech…>   Unpack each speech file into ID.wem and its lip sync (ID.raw or ID.cr2w);
                                             into speech\<language> beside it, or --out <folder>
              --wav                          Save a .wav of each voice line too
              --re                           Save lip syncs as ID.re (REDkit's, for editing in Blender)
              --language <xx>                When the file's name doesn't say it (enpc.w3speech is English)
          --speech-pack <folder…>            Pack each folder of ID.wem (with ID.raw, ID.cr2w or ID.re) into a speech file;
                                             <language>pc.w3speech beside its speech folder, or --out <file>. An ID.wav
                                             with no ID.wem is made one, as the game's voice lines are
              --keep                         Keep the lines already in that speech file the folder hasn't got
              --kbps <n>                     The bitrate a channel a .wav is made at (64 unless Settings says otherwise)
              --language <xx>                When the folder's name isn't a language (speech\en is English)
          --lipsync-from-audio <files or folders…> [--to re]
                                             A lip sync made from each .wav or .wem, as the game's own lines taught it;
                                             ID.raw beside each (or ID.re with --to re), or under --out <folder>
          --learn-lipsync                    Learn lip sync again from the game's voice lines (a few minutes); it's
                                             learnt the first time it's needed anyway. --game <folder> when Settings
                                             hasn't the game's folder
              --make-lipsync                 With --speech-pack: make a lip sync for each line that has none
          --wav-to-wem <files or folders…>   Make each .wav a .wem as the game's voice lines are (Wwise Opus, 48 kHz; mono,
                                             stereo or 5.1); beside each one, or under --out <folder>. -r, --overwrite
                                             and --kbps as above
          --lipsync <files or folders…> --to re|raw
                                             Convert lip syncs (.raw, .cr2w, .re) to REDkit's .re or the game's raw form;
                                             beside each one, or under --out <folder>. -r looks in subfolders,
                                             --overwrite replaces files there already
          --strings-to-csv <files.w3strings…> [--layout radish|redkit]
                                             radish (the default): a lang.csv beside each file, or under --out <folder>.
                                             redkit: one CSV with a column for each file's language (strings_redkit.csv,
                                             or --out <file.csv>), as REDkit's strings database imports
          --csv-to-strings <files.csv…>      A lang.w3strings from each CSV, either layout (REDkit's gives one for each
                                             language with text; --language <xx> takes only that one); beside it, or
                                             under --out <folder>. --overwrite replaces files there already

        """;
}
// loc-ignore-end
