using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace W3ModWorkbench.Core.Items;

/// <summary>The kinds of definition the item editor shows: items, the abilities that hold their stats, how they're made and where they're found.</summary>
public enum DefKind { Item, Ability, Schematic, Recipe, Loot }

/// <summary>
/// One definition in the game's XML: an item, an ability, a crafting schematic, an alchemy recipe or a loot table, as
/// written in its file. New Game+ keeps its own copies, in the folders ending _plus (items_plus, abilities_plus).
/// </summary>
public sealed class XmlDef
{
    public required DefKind Kind { get; init; }
    public required string Name { get; init; }

    /// <summary>The file it's in, by its game path.</summary>
    public required string GamePath { get; init; }
    public required bool NewGamePlus { get; init; }
    public required XElement Element { get; init; }

    public string? Attribute(string name) => Element.Attribute(name)?.Value;

    /// <summary>For a schematic or recipe, the item it makes.</summary>
    public string? Makes => Kind switch
    {
        DefKind.Schematic => Attribute("craftedItem_name"),
        DefKind.Recipe => Attribute("cookedItem_name"),
        _ => null,
    };

    /// <summary>For an item, the abilities that hold its stats, in order.</summary>
    public IEnumerable<string> BaseAbilities =>
        Element.Element("base_abilities")?.Elements("a").Select(a => a.Value.Trim()).Where(a => a.Length > 0) ?? [];

    /// <summary>An item's or ability's tags, from its comma-separated &lt;tags&gt;.</summary>
    public IEnumerable<string> Tags =>
        (Element.Element("tags")?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override string ToString() => $"{Kind} {Name} ({GamePath})";
}

/// <summary>
/// The game's own item definitions, read from its XML: content0's bundles and the official DLC's, under
/// gameplay\items and gameplay\abilities (and their New Game+ copies). Read once; the lists are the game's, which a
/// project's changes are set against.
/// </summary>
public sealed partial class ItemDatabase
{
    private readonly Dictionary<(DefKind, bool, string), List<XmlDef>> _byName = [];
    private readonly Dictionary<string, List<XmlDef>> _makers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<XmlDef>> _loot = new(StringComparer.OrdinalIgnoreCase);

    public List<XmlDef> All { get; } = [];

    /// <summary>The XML files read, by game path, as their text, for whole-file edits that change one definition and keep the rest as it is.</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files that couldn't be read, with why.</summary>
    public List<string> Problems { get; } = [];

    [GeneratedRegex(@"<([A-Za-z_][\w.\-]*)(\s[^<>]*?)?(/?)>", RegexOptions.Singleline)]
    private static partial Regex StartTag();

    [GeneratedRegex(@"([\w:.\-]+)(\s*=\s*)(""[^""]*""|'[^']*')")]
    private static partial Regex AttributeRx();

    [GeneratedRegex(@"<!--(.*?)-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>Whether a game path is one of the folders item definitions are kept in.</summary>
    public static bool IsDefinitionFile(string path)
    {
        string p = path.Replace('/', '\\').ToLowerInvariant();
        return p.EndsWith(".xml") && (p.Contains(@"gameplay\items\") || p.Contains(@"gameplay\items_plus\")
            || p.Contains(@"gameplay\abilities\") || p.Contains(@"gameplay\abilities_plus\"));
    }

    public static bool IsNewGamePlus(string path)
    {
        string p = path.Replace('/', '\\').ToLowerInvariant();
        return p.Contains(@"\items_plus\") || p.Contains(@"\abilities_plus\");
    }

    /// <summary>
    /// Reads the game's definitions from its folder. With <paramref name="withMods"/>, the enabled installed mods' are
    /// read first, in the game's load order: a mod's copy of a file is the one kept, as in the game, and the files a mod
    /// installed as DLC adds are read beside the game's.
    /// </summary>
    public static ItemDatabase Load(string gameFolder, IProgress<string>? progress = null, CancellationToken token = default, bool withMods = false)
    {
        var db = new ItemDatabase();
        var bundles = new List<string>();
        if (withMods)
        {
            var official = OfficialDlc.Names(gameFolder);
            var options = new LoadOrder.LoadOrderOptions(Path.Combine(gameFolder, "mods"), true, LoadOrder.ModScanner.FindSettingsFile(), gameFolder);
            foreach (var mod in LoadOrder.LoadOrderAnalyzer.DiscoverMods(options).Where(m => m.Enabled && !(m.IsDlc && official.Contains(m.Name))))
            {
                string modContent = Path.Combine(mod.Folder, "content");
                try
                {
                    if (Directory.Exists(modContent)) bundles.AddRange(Directory.GetFiles(modContent, "*.bundle").Order(StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    db.Problems.Add($"{mod.Name}: {ex.Message}");
                }
            }
        }
        string content = Path.Combine(gameFolder, "content");
        if (Directory.Exists(content)) bundles.AddRange(Directory.GetFiles(content, "*.bundle", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase));
        foreach (string path in bundles)
        {
            token.ThrowIfCancellationRequested();
            Bundle bundle;
            try { bundle = Bundle.Open(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                db.Problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                continue;
            }
            var entries = bundle.Entries.Where(e => IsDefinitionFile(e.Path)).ToList();
            if (entries.Count == 0) continue;
            progress?.Report(Path.GetFileName(path));
            using var stream = File.OpenRead(path);
            foreach (var entry in entries)
            {
                string gamePath = entry.Path.Replace('/', '\\');
                if (db.Files.ContainsKey(gamePath)) continue;
                try { db.Add(gamePath, Decode(entry.Read(stream))); }
                catch (Exception ex) when (ex is XmlException or IOException or InvalidDataException)
                {
                    db.Problems.Add($"{gamePath}: {ex.Message}");
                }
            }
        }
        return db;
    }

    /// <summary>A definitions file's text, read as the game writes it (UTF-8, with or without its mark).</summary>
    public static string Decode(byte[] data) => Encoding.UTF8.GetString(data).TrimStart('﻿');

    /// <summary>Adds the definitions of one file, read from its text.</summary>
    public void Add(string gamePath, string text)
    {
        Files[gamePath] = text;
        bool plus = IsNewGamePlus(gamePath);
        foreach (var (kind, element) in Definitions(Parse(text)))
        {
            string? name = element.Attribute(kind is DefKind.Schematic or DefKind.Recipe ? "name_name" : "name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var def = new XmlDef { Kind = kind, Name = name, GamePath = gamePath, NewGamePlus = plus, Element = element };
            All.Add(def);
            var key = (kind, plus, name.ToLowerInvariant());
            if (!_byName.TryGetValue(key, out var list)) _byName[key] = list = [];
            list.Add(def);
            if (plus) continue;
            if (def.Makes is { Length: > 0 } made)
            {
                if (!_makers.TryGetValue(made, out var makers)) _makers[made] = makers = [];
                makers.Add(def);
            }
            if (kind == DefKind.Loot)
                foreach (string item in element.Elements("loot_entry").Select(e => e.Attribute("name")?.Value).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!_loot.TryGetValue(item, out var tables)) _loot[item] = tables = [];
                    tables.Add(def);
                }
        }
    }

    /// <summary>
    /// The XML of a definitions file, read as the game reads it: some of the game's own files give an attribute twice
    /// in one tag, or write "--" in a comment, which a strict reader refuses; all but the first of an attribute are left
    /// out, and a run of dashes in a comment is made one.
    /// </summary>
    public static XDocument Parse(string text)
    {
        text = Comment().Replace(text, m => "<!--" + Regex.Replace(m.Groups[1].Value, "-{2,}", "-").TrimEnd('-') + "-->");
        string clean = StartTag().Replace(text, m =>
        {
            if (!m.Groups[2].Success) return m.Value;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string attributes = AttributeRx().Replace(m.Groups[2].Value, a => seen.Add(a.Groups[1].Value) ? a.Value : "");
            return $"<{m.Groups[1].Value}{attributes}{m.Groups[3].Value}>";
        });
        return XDocument.Parse(clean, LoadOptions.SetLineInfo);
    }

    private static IEnumerable<(DefKind, XElement)> Definitions(XDocument doc)
    {
        // definitions, and the crafting schematics and alchemy recipes kept beside them under <custom>
        var sections = (doc.Root?.Elements().Where(e => e.Name.LocalName is "definitions" or "custom") ?? []).SelectMany(e => e.Elements());
        foreach (var section in sections)
        {
            (string Tag, DefKind Kind)? what = section.Name.LocalName switch
            {
                "items" => ("item", DefKind.Item),
                "abilities" => ("ability", DefKind.Ability),
                "crafting_schematics" => ("schematic", DefKind.Schematic),
                "alchemy_recipes" or "cooking_recipes" => ("recipe", DefKind.Recipe),
                "loot_definitions" => ("loot", DefKind.Loot),
                _ => null,
            };
            if (what is not { } w) continue;
            foreach (var element in section.Elements(w.Tag)) yield return (w.Kind, element);
        }
    }

    /// <summary>Every definition of a name: the first is the one shown, the rest are copies other files have.</summary>
    public IReadOnlyList<XmlDef> Named(DefKind kind, string name, bool newGamePlus = false) =>
        _byName.TryGetValue((kind, newGamePlus, name.ToLowerInvariant()), out var list) ? list : [];

    public XmlDef? Find(DefKind kind, string name, bool newGamePlus = false) => Named(kind, name, newGamePlus).FirstOrDefault();

    /// <summary>The items of the normal game, one for each name.</summary>
    public IEnumerable<XmlDef> Items => All.Where(d => d.Kind == DefKind.Item && !d.NewGamePlus).DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The schematics and recipes that make an item.</summary>
    public IReadOnlyList<XmlDef> MadeBy(string item) => _makers.TryGetValue(item, out var list) ? list : [];

    /// <summary>The items whose base abilities include an ability: what changing it changes.</summary>
    public IReadOnlyList<XmlDef> ItemsUsing(string ability)
    {
        if (_users is null)
        {
            _users = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items)
                foreach (string a in item.BaseAbilities.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!_users.TryGetValue(a, out var list)) _users[a] = list = [];
                    list.Add(item);
                }
        }
        return _users.TryGetValue(ability, out var users) ? users : [];
    }

    private Dictionary<string, List<XmlDef>>? _users;

    /// <summary>The loot tables an item is in.</summary>
    public IReadOnlyList<XmlDef> FoundIn(string item) => _loot.TryGetValue(item, out var list) ? list : [];
}
