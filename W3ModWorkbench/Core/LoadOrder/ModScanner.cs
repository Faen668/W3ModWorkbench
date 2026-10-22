using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Scripts;
using W3ModWorkbench.Core.Scripts.Navigation;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Core.Textures;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.LoadOrder;

/// <summary>One @wrapMethod, @replaceMethod, @addMethod or @addField, from a compiled blob or a loose script.</summary>
/// <param name="Kind">wrap, replace, addMethod or addField.</param>
/// <param name="Target">The class it changes, or empty for a global function.</param>
/// <param name="Source">The blob or script file it came from.</param>
public sealed record Annotation(string Kind, string Target, string Name, string Source)
{
    public bool IsField => Kind == "addField";
    public string Key => Target.Length == 0 ? Name : $"{Target}.{Name}";
}

/// <summary>What one mod's scripts declare and use, whether they're compiled or loose.</summary>
public sealed class ScriptModel
{
    /// <summary>Types it defines, by name: class, state, struct or enum.</summary>
    public Dictionary<string, string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file a type is defined in, for loose scripts.</summary>
    public Dictionary<string, string> TypeFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Bases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Class.member" for what the classes it defines declare.</summary>
    public HashSet<string> Members { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Annotation> Annotations { get; } = [];

    /// <summary>Enum name → member name → value (null when not known, as in loose scripts).</summary>
    public Dictionary<string, Dictionary<string, int?>> EnumMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file a global function is defined in, for loose scripts.</summary>
    public Dictionary<string, string> GlobalFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Type names a compiled script links to. Exact: it's the blob's own type table.</summary>
    public HashSet<string> NeedTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every word in loose scripts' code (comments and strings left out), to match against what other mods declare.</summary>
    public HashSet<string> NeedWords { get; } = new(StringComparer.Ordinal);

    public HashSet<string> NeedGlobals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> NeedMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Provides(string key) =>
        Members.Contains(key) || Annotations.Any(a => a.Kind is "addMethod" or "addField" && a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

public sealed record LooseScript(string Relative, string FullPath, string Hash);

/// <param name="Bundle">The bundle on disk it's in, to read it again for a closer look.</param>
public sealed record BundledFile(string Path, uint Crc, int Size, string Bundle = "");

/// <summary>A texture in a mod's texture.cache: where its packed pixels are, to compare them with another mod's.</summary>
public sealed record CachedTexture(string Path, string Cache, long Offset, int PackedSize, string Description);

/// <summary>A menu setting a mod's scripts read: GetVarValue('Group', 'var').</summary>
public sealed record MenuRead(string Group, string Var, string Source);

/// <summary>An input action a mod's scripts listen for: RegisterListener(this, 'OnEvent', 'Action').</summary>
public sealed record ListenedAction(string Action, string Source);

/// <summary>A mod with everything read out of it that the analysis needs.</summary>
public sealed class ScannedMod
{
    public required ModEntry Entry { get; init; }
    public ScriptModel Scripts { get; } = new();
    public List<LooseScript> Loose { get; } = [];
    public List<BundledFile> Files { get; } = [];
    public Dictionary<uint, string> Strings { get; } = [];
    public string StringsSource { get; set; } = "";
    public List<(string Message, bool Fatal)> Problems { get; } = [];
    public string? Build { get; set; }
    public string? Platform { get; set; }

    /// <summary>Every name its scripts use: a compiled blob's name table, and the quoted names ('Name') in loose scripts.</summary>
    public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<MenuRead> MenuReads { get; } = [];
    public List<ListenedAction> Listens { get; } = [];
    public List<CachedTexture> Textures { get; } = [];

    /// <summary>The DLC IDs its .reddlc files declare, with the file each is in.</summary>
    public List<(string Id, string File)> Dlcs { get; } = [];

    /// <summary>info.json's useLooseScripts. A mod with a compiled blob and this false ships its sources loose, not to be loaded.</summary>
    public bool? UseLooseScripts { get; set; }
}

/// <summary>The game's own scripts, as far as the analysis needs them.</summary>
public sealed class GameIndex
{
    private static readonly Dictionary<string, Task<GameIndex>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly GameIndex Empty = new();

    public HashSet<string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Bases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The quoted names ('Name') the game's scripts use: the input actions it listens for, among others.</summary>
    public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scripts folder's files by path (lower case) → the types each declares.</summary>
    public Dictionary<string, List<string>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Types.Count == 0;

    public static Task<GameIndex> LoadAsync(string scriptsFolder)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(scriptsFolder, out var task))
                Cache[scriptsFolder] = task = Task.Run(() => Read(scriptsFolder));
            return task;
        }
    }

    private static GameIndex Read(string folder)
    {
        var index = new GameIndex();
        string[] paths;
        try { paths = Directory.GetFiles(folder, "*.ws", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return index; }

        var parsed = new ParsedScript?[paths.Length];
        var names = new HashSet<string>?[paths.Length];
        Parallel.For(0, paths.Length, i =>
        {
            string path = paths[i];
            try
            {
                var file = new ScriptFile
                {
                    Key = path, Path = Path.GetRelativePath(folder, path), Source = L.T("Common.GameScripts"), SourceKey = folder,
                    Read = () => File.ReadAllBytes(path),
                };
                string text = TextDecoding.Decode(File.ReadAllBytes(path));
                parsed[i] = ScriptParser.Parse(text, file);
                names[i] = [.. ModScanner.QuotedNames(text)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a script that can't be read adds nothing
            }
        });

        foreach (var set in names)
            if (set is not null) index.Names.UnionWith(set);
        foreach (var p in parsed)
        {
            if (p is null) continue;
            var declared = new List<string>();
            foreach (var s in p.Symbols)
            {
                switch (s.Kind)
                {
                    case SymbolKind.Class or SymbolKind.State or SymbolKind.Struct or SymbolKind.Enum when s.Annotation is null:
                        index.Types.Add(s.TypeKey);
                        declared.Add(s.TypeKey);
                        if (s.Base is not null) index.Bases[s.TypeKey] = s.Base;
                        break;
                    case SymbolKind.Function or SymbolKind.Event when s.Annotation is null:
                        if (s.Owner is null) index.Globals.Add(s.Name);
                        else index.Members.Add($"{s.Owner}.{s.Name}");
                        break;
                    case SymbolKind.Field when s.Annotation is null && s.Owner is not null:
                        index.Members.Add($"{s.Owner}.{s.Name}");
                        break;
                }
            }
            index.Files[p.File.Path] = declared;
        }
        return index;
    }
}

/// <summary>Reads each installed mod: what its scripts declare and use, its bundles' files and its strings.</summary>
public static partial class ModScanner
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Word();

    [GeneratedRegex("'([A-Za-z_][A-Za-z0-9_ ]*)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"GetVarValue\s*\(\s*'([^']+)'\s*,\s*'([^']+)'")]
    private static partial Regex VarRead();

    [GeneratedRegex(@"RegisterListener\s*\(\s*[^,()]+,\s*'[^']*'\s*,\s*'([^']+)'")]
    private static partial Regex Listener();

    /// <summary>The names quoted in script code: 'Name'.</summary>
    public static IEnumerable<string> QuotedNames(string code) => Quoted().Matches(code).Select(m => m.Groups[1].Value);

    /// <summary>The menu settings and input actions a script's code reads and listens for.</summary>
    private static void ReadCalls(ScannedMod mod, string code, string source)
    {
        foreach (Match m in VarRead().Matches(code)) mod.MenuReads.Add(new MenuRead(m.Groups[1].Value, m.Groups[2].Value, source));
        foreach (Match m in Listener().Matches(code)) mod.Listens.Add(new ListenedAction(m.Groups[1].Value, source));
    }

    public static ScannedMod Scan(ModEntry entry, CancellationToken token)
    {
        var mod = new ScannedMod { Entry = entry };
        string content = Directory.Exists(Path.Combine(entry.Folder, "content")) ? Path.Combine(entry.Folder, "content") : entry.Folder;

        ReadInfo(mod, content);
        token.ThrowIfCancellationRequested();

        string[] files;
        try { files = Directory.GetFiles(content, "*", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mod.Problems.Add((L.T("ModScanner.FolderCouldntRead", ex.Message), true));
            return mod;
        }

        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string ext = Path.GetExtension(file);
            string rel = Path.GetRelativePath(content, file);
            try
            {
                if (ext.Equals(".rsblob", StringComparison.OrdinalIgnoreCase)) AddBlob(mod, file, rel);
                else if (ext.Equals(".bundle", StringComparison.OrdinalIgnoreCase)) AddBundle(mod, file, rel);
                else if (ext.Equals(".cache", StringComparison.OrdinalIgnoreCase) && TextureCacheArchive.LooksLikeCache(file)) AddTextures(mod, file);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                mod.Problems.Add((L.T("ModScanner.CouldntRead", rel, ex.Message), true));
            }
        }

        // loose scripts: content\scripts\...
        string scripts = Path.Combine(content, "scripts");
        if (Directory.Exists(scripts) && !(mod.UseLooseScripts == false && mod.Entry.CompiledScripts > 0))
            foreach (string file in Directory.EnumerateFiles(scripts, "*.ws", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                try { AddLoose(mod, scripts, file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    mod.Problems.Add((L.T("ModScanner.CouldntRead2", Path.GetRelativePath(content, file), ex.Message), false));
                }
            }

        AddStrings(mod, files);
        entry.Annotations = mod.Scripts.Annotations.DistinctBy(a => (a.Kind, a.Key.ToLowerInvariant())).Count();
        return mod;
    }

    // ---------------------------------------------------------------- info.json

    private static void ReadInfo(ScannedMod mod, string content)
    {
        string path = Path.Combine(content, "info.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), Lenient);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("modName", out var title) && title.ValueKind == JsonValueKind.String) mod.Entry.Title = title.GetString() ?? "";
            if (mod.Entry.Title.Length == 0 && root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) mod.Entry.Title = name.GetString() ?? "";
            if (root.TryGetProperty("useLooseScripts", out var loose) && loose.ValueKind is JsonValueKind.True or JsonValueKind.False)
                mod.UseLooseScripts = loose.GetBoolean();
            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String) mod.Entry.Version = version.GetString() ?? "";
            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
                foreach (var d in deps.EnumerateArray())
                {
                    string? text = d.ValueKind switch
                    {
                        JsonValueKind.String => d.GetString(),
                        JsonValueKind.Object when d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(text)) mod.Entry.DeclaredDependencies.Add(text.Trim());
                }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            mod.Problems.Add((L.T("ModScanner.InfoJsonCouldntRead", ex.Message), false));
        }
    }

    // ---------------------------------------------------------------- compiled scripts

    private static void AddBlob(ScannedMod mod, string file, string rel)
    {
        var blob = ScriptBlob.Open(file);
        mod.Entry.CompiledScripts++;
        mod.Build ??= blob.Build;
        mod.Platform ??= blob.Platform;
        var m = mod.Scripts;

        foreach (var e in blob.Enums)
        {
            m.Types[e.Name] = "enum";
            if (!m.EnumMembers.TryGetValue(e.Name, out var members)) m.EnumMembers[e.Name] = members = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in e.Members) members[name] = value == int.MaxValue ? null : value;
        }
        foreach (var s in blob.Structs) m.Types[s.Name] = "struct";
        foreach (var c in blob.Classes)
        {
            // a state is named after the class it belongs to, as the script parser names it
            string key = c.IsState ? SymbolStateKey(c) : c.Name;
            m.Types[key] = c.IsState ? "state" : "class";
            if (c.Base.Length > 0) m.Bases[key] = c.Base;
            foreach (var f in c.Functions) m.Members.Add($"{key}.{f.Name}");
            foreach (var p in c.Properties) m.Members.Add($"{key}.{p.Name}");
        }
        foreach (var f in blob.Globals) m.Globals.Add(f.Name);

        foreach (var f in blob.ReplacedGlobals) m.Annotations.Add(new Annotation("replace", "", f.Name, rel));
        foreach (var f in blob.Replaced) m.Annotations.Add(new Annotation("replace", f.Owner, f.Name, rel));
        foreach (var f in blob.Wrapped) m.Annotations.Add(new Annotation("wrap", f.Owner, f.Name, rel));
        foreach (var f in blob.AddedMethods) m.Annotations.Add(new Annotation("addMethod", f.Owner, f.Name, rel));
        foreach (var p in blob.AddedFields) m.Annotations.Add(new Annotation("addField", p.Owner, p.Name, rel));

        // what the code links to: types, global functions, and members of other classes
        string TypeName(int i) => i >= 0 && i < blob.Types.Count ? blob.Types[i].Name : "";
        foreach (var (name, _) in blob.Types)
            foreach (Match w in Word().Matches(name)) m.NeedTypes.Add(w.Value);
        foreach (var (name, owner) in blob.FunctionRefs)
        {
            if (owner < 0) m.NeedGlobals.Add(name);
            else if (TypeName(owner) is { Length: > 0 } t) m.NeedMembers.Add($"{t}.{name}");
        }
        foreach (var (name, _, owner) in blob.ImportedProperties)
            if (TypeName(owner) is { Length: > 0 } t) m.NeedMembers.Add($"{t}.{name}");

        mod.Names.UnionWith(blob.Names);
        // the menu settings and input actions it uses are in its code, which is decompiled only when it calls for them
        if (blob.FunctionRefs.Any(f => f.Name is "GetVarValue" or "RegisterListener"))
            foreach (var doc in ScriptWriter.Write(blob, rel))
                ReadCalls(mod, doc.Text, rel);
    }

    private static string SymbolStateKey(ScriptClass c) => ScriptSymbol.StateKey(c.StateOf, c.Name);

    // ---------------------------------------------------------------- loose scripts

    private static void AddLoose(ScannedMod mod, string scriptsRoot, string file)
    {
        byte[] data = File.ReadAllBytes(file);
        string rel = Path.GetRelativePath(scriptsRoot, file);
        mod.Loose.Add(new LooseScript(rel, file, Convert.ToHexString(SHA256.HashData(data))));
        mod.Entry.LooseScripts++;

        var parsed = ScriptParser.Parse(TextDecoding.Decode(data), new ScriptFile
        {
            Key = file, Path = rel, Source = mod.Entry.Name, SourceKey = mod.Entry.Name, Read = () => data,
        });
        var m = mod.Scripts;
        foreach (var s in parsed.Symbols)
        {
            switch (s.Kind)
            {
                case SymbolKind.Class or SymbolKind.State or SymbolKind.Struct or SymbolKind.Enum:
                    m.Types[s.TypeKey] = s.Kind.ToString().ToLowerInvariant();
                    m.TypeFiles[s.TypeKey] = rel;
                    if (s.Base is not null) m.Bases[s.TypeKey] = s.Base;
                    break;
                case SymbolKind.EnumMember when s.Owner is not null:
                    if (!m.EnumMembers.TryGetValue(s.Owner, out var members)) m.EnumMembers[s.Owner] = members = new(StringComparer.OrdinalIgnoreCase);
                    members[s.Name] = null;
                    break;
                case SymbolKind.Function or SymbolKind.Event or SymbolKind.Field:
                    if (s.Annotation is { } a)
                    {
                        string kind = a switch { "wrapMethod" => "wrap", "replaceMethod" => "replace", _ => a };
                        m.Annotations.Add(new Annotation(kind, s.Owner ?? "", s.Name, rel));
                    }
                    else if (s.Owner is null)
                    {
                        m.Globals.Add(s.Name);
                        m.GlobalFiles[s.Name] = rel;
                    }
                    else m.Members.Add($"{s.Owner}.{s.Name}");
                    break;
            }
        }
        foreach (Match w in Word().Matches(parsed.Code)) m.NeedWords.Add(w.Value);
        string text = TextDecoding.Decode(data);
        mod.Names.UnionWith(QuotedNames(text));
        ReadCalls(mod, text, rel);
    }

    // ---------------------------------------------------------------- bundles

    private static void AddBundle(ScannedMod mod, string file, string rel)
    {
        var bundle = Bundle.Open(file);
        mod.Entry.Bundles++;
        foreach (var e in bundle.Entries)
            mod.Files.Add(new BundledFile(e.Path.Replace('/', '\\'), ((BundleEntry)e).Crc, e.Size, file));
        mod.Entry.BundleFiles += bundle.Entries.Count;

        // a DLC's definition names its ID, which no two DLC may share
        foreach (var e in bundle.Entries.Where(e => e.Path.EndsWith(".reddlc", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = File.OpenRead(file);
            if (DlcId(e.Read(stream)) is { Length: > 0 } id) mod.Dlcs.Add((id, e.Path.Replace('/', '\\')));
        }
    }

    /// <summary>The id a .reddlc (CDLCDefinition) gives its DLC. Null when it can't be read.</summary>
    public static string? DlcId(byte[] data)
    {
        try
        {
            var json = Cr2wJson.ToJson(Cr2wFile.Read(data), out _);
            return json["objects"] is JsonArray { Count: > 0 } objects && objects[0]?["id"] is { } id ? id.ToString() : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- texture caches

    private static void AddTextures(ScannedMod mod, string file)
    {
        var cache = TextureCacheArchive.Open(file);
        foreach (var e in cache.Entries.OfType<TextureEntry>())
            mod.Textures.Add(new CachedTexture(e.Path.Replace('/', '\\'), file, e.Info.DataOffset, e.Info.PackedSize, e.Info.Description));
    }

    // ---------------------------------------------------------------- strings

    private static void AddStrings(ScannedMod mod, string[] files)
    {
        var strings = files.Where(f => f.EndsWith(".w3strings", StringComparison.OrdinalIgnoreCase)).ToList();
        mod.Entry.StringsFiles = strings.Count;
        if (strings.Count == 0) return;
        // one language is enough to find IDs two mods both use
        string pick = strings.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals("en", StringComparison.OrdinalIgnoreCase)) ?? strings[0];
        try
        {
            var read = W3Strings.Read(File.ReadAllBytes(pick), pick);
            foreach (var s in read.Strings) mod.Strings[s.Id] = s.Text;
            mod.StringsSource = Path.GetFileName(pick);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            mod.Problems.Add((L.T("ModScanner.CouldntRead3", Path.GetFileName(pick), ex.Message), false));
        }
    }

    // ---------------------------------------------------------------- mods.settings

    /// <summary>The per-mod Enabled and Priority values in the game's mods.settings.</summary>
    public static Dictionary<string, (bool Enabled, int? Priority)> ReadSettings(string? path)
    {
        var result = new Dictionary<string, (bool, int?)>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path)) return result;
        try
        {
            string? section = null;
            bool enabled = true;
            int? priority = null;
            void Flush()
            {
                if (section is not null) result[section] = (enabled, priority);
            }
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    Flush();
                    section = line[1..^1];
                    enabled = true;
                    priority = null;
                }
                else if (line.Split('=', 2) is [var key, var value])
                {
                    if (key.Trim().Equals("Enabled", StringComparison.OrdinalIgnoreCase)) enabled = value.Trim() != "0";
                    else if (key.Trim().Equals("Priority", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Trim(), out int p)) priority = p;
                }
            }
            Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable settings file means default priorities
        }
        return result;
    }

    /// <summary>Documents\The Witcher 3\mods.settings, where the game keeps each mod's priority.</summary>
    public static string? FindSettingsFile()
    {
        string path = GameDocuments.FileIn("mods.settings");
        return File.Exists(path) ? path : null;
    }
}
