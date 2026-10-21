using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>What a recipe does: one of the converter's directions, or bulk edits.</summary>
public enum Cr2wRecipeKind
{
    ToJson,
    FromJson,
    Resave,
    Edit,
}

/// <summary>
/// A bulk edit as a recipe keeps it: the copy's source as a path, read each time the recipe runs, so a recipe follows
/// the file it copies from rather than the file as it was when the recipe was saved.
/// </summary>
public sealed class Cr2wRecipeEdit
{
    public BulkEditKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string? Source { get; set; }
    public JsonNode? Value { get; set; }
    public double Amount { get; set; }
    public List<string> Fields { get; set; } = [];

    public static Cr2wRecipeEdit From(Cr2wBulkEdit edit, string? source) => new()
    {
        Kind = edit.Kind,
        Path = edit.Path,
        ClassName = edit.ClassName,
        Source = edit.Kind == BulkEditKind.Copy ? source : null,
        Value = edit.Kind == BulkEditKind.Set ? edit.Value?.DeepClone() : null,
        Amount = edit.Amount,
        Fields = [.. edit.Fields],
    };

    /// <summary>The edit to make, with a copy's source file read now.</summary>
    public Cr2wBulkEdit ToEdit() => new()
    {
        Kind = Kind,
        Path = Path,
        ClassName = ClassName,
        Source = Kind == BulkEditKind.Copy
            ? Cr2wJsonBatch.Load(System.IO.Path.GetFullPath(Source is { Length: > 0 } s ? s : throw new InvalidDataException(L.T("Cr2wRecipe.CopyHasNoSource", Path))))
            : null,
        Value = Value?.DeepClone(),
        Amount = Amount,
        Fields = Fields,
    };
}

/// <summary>
/// The CR2W and JSON window's form saved to run again: the files and folders, what to do to them, and where the results
/// go. Kept as a JSON file each in the recipes folder; it runs in the window, from the command line with
/// <c>--recipe</c>, or as the Python or batch script <see cref="Cr2wRecipeExport"/> writes from it.
/// </summary>
public sealed class Cr2wRecipe
{
    public const string Extension = ".w3recipe.json"; // loc-ignore
    private const string FormatName = "w3mw-cr2w-recipe"; // loc-ignore

    [JsonPropertyName("$format")] // loc-ignore
    public string Format { get; set; } = FormatName;

    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public Cr2wRecipeKind Kind { get; set; }
    public List<string> Inputs { get; set; } = [];
    public bool Recursive { get; set; } = true;
    public bool Overwrite { get; set; }

    /// <summary>For <see cref="Cr2wRecipeKind.FromJson"/>: write files of older versions as version 164.</summary>
    public bool AsLatest { get; set; }

    /// <summary>The folder the results go under, keeping the folders they were in; null for beside (or, for edits, over) the files.</summary>
    public string? Output { get; set; }

    public List<Cr2wRecipeEdit> Edits { get; set; } = [];
    public DateTime Saved { get; set; }
    public DateTime? LastRun { get; set; }

    /// <summary>The file it was read from or saved to.</summary>
    [JsonIgnore]
    public string? FilePath { get; private set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>%AppData%\W3ModWorkbench\recipes, where recipes are kept unless Settings names another folder.</summary>
    public static string DefaultFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3ModWorkbench", "recipes"); // loc-ignore

    /// <summary>The recipes folder: the one chosen in Settings, or <see cref="DefaultFolder"/>.</summary>
    public static string Folder(string? chosen) => string.IsNullOrWhiteSpace(chosen) ? DefaultFolder : chosen.Trim();

    public static Cr2wRecipe Load(string file)
    {
        Cr2wRecipe? recipe;
        try { recipe = JsonSerializer.Deserialize<Cr2wRecipe>(File.ReadAllText(file, Encoding.UTF8), Options); }
        catch (JsonException ex) { throw new InvalidDataException(L.T("Cr2wRecipe.CantRead", ex.Message)); }
        if (recipe is null || recipe.Format != FormatName) throw new InvalidDataException(L.T("Cr2wRecipe.IsntRecipe"));
        recipe.FilePath = System.IO.Path.GetFullPath(file);
        if (recipe.Name.Length == 0) recipe.Name = NameOf(file);
        return recipe;
    }

    /// <summary>Every recipe in the folder, by name; files that aren't recipes are passed over.</summary>
    public static List<Cr2wRecipe> LoadAll(string folder)
    {
        var result = new List<Cr2wRecipe>();
        if (!Directory.Exists(folder)) return result;
        foreach (string file in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            try { result.Add(Load(file)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        return [.. result.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Writes the recipe into the folder, as a file named after it; a recipe renamed since it was last saved moves to
    /// its new name. A name another recipe has already gains a number.
    /// </summary>
    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        string wanted = System.IO.Path.Combine(folder, SafeFileName(Name) + Extension);
        string target = wanted;
        for (int n = 2; File.Exists(target) && !target.Equals(FilePath, StringComparison.OrdinalIgnoreCase); n++)
            target = System.IO.Path.Combine(folder, $"{SafeFileName(Name)} ({n}){Extension}");
        Saved = DateTime.Now;
        File.WriteAllText(target, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        if (FilePath is not null && !FilePath.Equals(target, StringComparison.OrdinalIgnoreCase) && File.Exists(FilePath)) File.Delete(FilePath);
        FilePath = target;
    }

    public void Delete()
    {
        if (FilePath is not null && File.Exists(FilePath)) File.Delete(FilePath);
        FilePath = null;
    }

    /// <summary>The recipe's name from its file name, for a recipe file that doesn't say.</summary>
    private static string NameOf(string file)
    {
        string name = System.IO.Path.GetFileName(file);
        return name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name[..^Extension.Length] : System.IO.Path.GetFileNameWithoutExtension(file);
    }

    private static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        string safe = new([.. name.Trim().Select(c => invalid.Contains(c) ? '_' : c)]);
        safe = safe.TrimEnd('.', ' ');
        return safe.Length == 0 ? "recipe" : safe.Length > 80 ? safe[..80] : safe; // loc-ignore
    }

    /// <summary>A name no recipe in the folder has yet: the one given, or it with a number after it.</summary>
    public static string UniqueName(string name, IEnumerable<Cr2wRecipe> others)
    {
        var taken = others.Select(r => r.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        string result = name;
        for (int n = 2; taken.Contains(result); n++) result = $"{name} ({n})";
        return result;
    }

    // ---------------------------------------------------------------- running

    /// <summary>
    /// The files a bulk edit works on: CR2W files and converted JSON files alike, the JSON ones from folders only when
    /// there isn't the same file found as CR2W.
    /// </summary>
    public static List<(string File, string Root)> EditInputs(IReadOnlyCollection<string> paths, bool recursive)
    {
        var inputs = Cr2wJsonBatch.Inputs(paths, fromJson: false, recursive);
        var seen = inputs.Select(i => i.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        inputs.AddRange(Cr2wJsonBatch.Inputs(paths.Where(Directory.Exists), fromJson: true, recursive).Where(j => seen.Add(j.File)));
        return inputs;
    }

    /// <summary>The edits to make, with the copies' source files read now.</summary>
    public List<Cr2wBulkEdit> MakeEdits() => [.. Edits.Select(e => e.ToEdit())];

    /// <summary>
    /// Runs the recipe: converts, resaves or edits its files. Null when none of its files or folders had a file to
    /// work on. <paramref name="dryRun"/> (for edits) only says what would change.
    /// </summary>
    public List<Cr2wJsonResult>? Run(IProgress<Cr2wJsonProgress>? progress = null, CancellationToken token = default, bool dryRun = false)
    {
        string? output = string.IsNullOrWhiteSpace(Output) ? null : System.IO.Path.GetFullPath(Output);
        if (Kind == Cr2wRecipeKind.Edit)
        {
            if (Edits.Count == 0) throw new InvalidDataException(L.T("Cr2wRecipe.NoEdits"));
            var edits = MakeEdits();
            var inputs = EditInputs(Inputs, Recursive);
            return inputs.Count == 0 ? null : Cr2wJsonBatch.BulkEdit(inputs, edits, output, progress, token, dryRun);
        }
        var files = Cr2wJsonBatch.Inputs(Inputs, fromJson: Kind == Cr2wRecipeKind.FromJson, Recursive);
        if (files.Count == 0) return null;
        return Kind switch
        {
            Cr2wRecipeKind.FromJson => Cr2wJsonBatch.FromJson(files, output, Overwrite, progress, token, AsLatest),
            Cr2wRecipeKind.Resave => Cr2wJsonBatch.ResaveAsLatest(files, output, Overwrite, progress, token),
            _ => Cr2wJsonBatch.ToJson(files, output, Overwrite, progress, token),
        };
    }

    /// <summary>What would stop the recipe running as saved: files and folders that aren't there any more.</summary>
    public List<string> Problems()
    {
        var result = new List<string>();
        foreach (string input in Inputs)
            if (!File.Exists(input) && !Directory.Exists(input)) result.Add(L.T("Cr2wRecipe.InputMissing", input));
        foreach (var edit in Edits.Where(e => e.Kind == BulkEditKind.Copy))
            if (edit.Source is null || !File.Exists(edit.Source)) result.Add(L.T("Cr2wRecipe.SourceMissing", edit.Source ?? ""));
        return result;
    }

    /// <summary>The amount for a scale or offset as the command line takes it.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
