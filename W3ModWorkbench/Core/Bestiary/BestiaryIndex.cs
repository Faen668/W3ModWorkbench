using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Loot;
using W3ModWorkbench.Core.World;

namespace W3ModWorkbench.Core.Bestiary;

/// <summary>A vital spot of a bestiary entry: its title and text, as string IDs.</summary>
public sealed record BestiaryVitalSpot(uint Title, uint Text);

/// <summary>
/// A creature of the game's bestiary (a CJournalCreature): its name and texts as string IDs, its picture (a file name the
/// game keeps in its journal and icon folders), its category, what works against it, and the template it names.
/// </summary>
public sealed record BestiaryEntry(Guid Guid, string BaseName, string JournalPath, uint Name, string Image, Guid Category, string? Template,
    IReadOnlyList<string> ItemsAgainst, IReadOnlyList<uint> Descriptions, IReadOnlyList<BestiaryVitalSpot> VitalSpots, IReadOnlyList<uint> Hints);

/// <summary>A category of the bestiary (Beasts, Necrophages…): its name as a string ID, and its order.</summary>
public sealed record BestiaryCategory(Guid Guid, string BaseName, uint Name, long Order);

/// <summary>
/// A monster template and what the game reads from it: its sound tag, abilities and tags (which decide its bestiary
/// entry), its level and its loot.
/// </summary>
public sealed record BestiaryCreature(string Template, string SfxTag, IReadOnlyList<string> Abilities, IReadOnlyList<string> Tags, int Level, LootSpot? Loot)
{
    public BestiaryEntry? Entry { get; set; }

    /// <summary>The template's file name as words, for a creature with no entry.</summary>
    public string Title => string.Join(' ', Path.GetFileNameWithoutExtension(Template).Split('_', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>A placed encounter that can spawn a template: where, its area's outline when it has one, and how many it spawns.</summary>
public sealed record BestiaryPlace(string World, string Layer, string Name, Vector3 Position, IReadOnlyList<Vector2>? Area, string Template,
    int TotalSpawnLimit, int MaxSpawnedAtOnce);

/// <summary>
/// The game's bestiary, joined to its monsters: every creature entry and category of the journal, the monster templates
/// the worlds' encounters spawn, which entry each belongs to (by the game's own rules, read from its scripts: a monster's
/// sound tag, abilities and tags, the same tests the game makes when the player meets it), and where each is placed.
/// </summary>
public sealed class BestiaryIndex
{
    public required IReadOnlyList<BestiaryEntry> Entries { get; init; }
    public required IReadOnlyList<BestiaryCategory> Categories { get; init; }
    public required IReadOnlyDictionary<string, BestiaryCreature> Creatures { get; init; }
    public required IReadOnlyList<BestiaryPlace> Places { get; init; }
    public required IReadOnlyList<string> Worlds { get; init; }

    /// <summary>How many rules were read from the game's scripts; 0 when they couldn't be (entries then come only from templates the journal names).</summary>
    public int Rules { get; init; }

    /// <summary>
    /// Where an entry's picture and icon may be, nearest first: under its DLC's own data folder for a DLC's entry
    /// (dlc\bob\data\gameplay\gui_new\textures\journal\bestiary\…), then the base game's.
    /// </summary>
    public static IEnumerable<(string Picture, string Icon)> ImagePaths(BestiaryEntry entry)
    {
        if (entry.Image.Length == 0) yield break;
        int gameplay = entry.JournalPath.IndexOf(@"gameplay\", StringComparison.OrdinalIgnoreCase);
        var roots = new List<string>();
        if (gameplay > 0) roots.Add(entry.JournalPath[..gameplay]);
        roots.AddRange(["", @"dlc\bob\data\", @"dlc\ep1\data\"]);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            yield return ($@"{root}gameplay\gui_new\textures\journal\bestiary\{entry.Image}", $@"{root}gameplay\gui_new\icons\monsters\{entry.Image}");
    }

    /// <summary>The creatures of an entry.</summary>
    public IEnumerable<BestiaryCreature> CreaturesOf(BestiaryEntry entry) => Creatures.Values.Where(c => c.Entry == entry);

    /// <summary>Reads the bestiary, the rules and every world's encounters: a few seconds.</summary>
    public static BestiaryIndex Build(ResourceLocator locator, string? scriptsFolder, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("journal");
        var (entries, categories) = ReadJournals(locator);
        var aliases = ReadAliases(locator);
        var rules = scriptsFolder is null ? [] : ReadRules(Path.Combine(scriptsFolder, "game", "npc", "npc.ws"));

        // the places, world by world
        var places = new ConcurrentBag<BestiaryPlace>();
        var worlds = locator.AllPaths(".w2w").Where(w => !w.StartsWith(@"engine\", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string worldPath in worlds)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(worldPath);
            WorldFile world;
            try { world = WorldFile.Read(locator.ReadFile(worldPath) ?? throw new FileNotFoundException(worldPath), worldPath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
            if (world.EmbeddedLayers is not { } tree) continue;
            Parallel.ForEach(tree.AllLayers(), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = token }, layer =>
            {
                Cr2wFile file;
                try { file = Cr2wFile.Read(locator.ReadFile(layer.Path) ?? []); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
                if (file.First("CLayer") is not { } cl || !cl.Array("entities").OfType<RedHandle>().Any(h => file.Exports[h.Export ?? 0].ClassName == "CEncounter")) return;
                foreach (var e in WorldFile.Entities(file).Where(e => e.ClassName == "CEncounter"))
                {
                    var area = e.Components.FirstOrDefault(c => c.Outline is { Count: > 2 })?.Outline;
                    foreach (var dh in e.Object.Array("creatureDefinition").OfType<RedHandle>())
                        if (file.Object(dh) is { } d && d["entityTemplate"] is RedHandle th && file.PathOf(th) is { } template)
                            places.Add(new BestiaryPlace(worldPath, layer.Path, e.Name, e.Position, area, template,
                                (int)d.Integer("totalSpawnLimit"), (int)d.Integer("maxSpawnedAtOnce")));
                }
            });
        }

        // every template the encounters spawn or the journal names
        progress?.Report("templates");
        var loot = LootIndex.TemplateLoot(locator);
        var templates = places.Select(p => p.Template).Concat(entries.Select(e => e.Template).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var creatures = new ConcurrentDictionary<string, BestiaryCreature>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(templates, template => { if (ReadCreature(locator, template, loot) is { } c) creatures[template] = c; });

        // each creature's entry: the game's rules first, then a template the journal names itself
        var byJournal = entries.GroupBy(e => e.JournalPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byTemplate = entries.Where(e => e.Template is not null).GroupBy(e => e.Template!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var c in creatures.Values)
        {
            if (byTemplate.TryGetValue(c.Template, out var named)) c.Entry = named;
            else if (Match(rules, c) is { } alias && aliases.TryGetValue(alias, out var journal) && byJournal.TryGetValue(journal, out var entry)) c.Entry = entry;
        }

        return new BestiaryIndex
        {
            Entries = entries, Categories = categories, Creatures = new Dictionary<string, BestiaryCreature>(creatures, StringComparer.OrdinalIgnoreCase),
            Places = [.. places.OrderBy(p => p.World).ThenBy(p => p.Layer, StringComparer.OrdinalIgnoreCase)], Worlds = worlds, Rules = rules.Count,
        };
    }

    // ---------------------------------------------------------------- the journal

    private static (List<BestiaryEntry>, List<BestiaryCategory>) ReadJournals(ResourceLocator locator)
    {
        var entries = new ConcurrentBag<BestiaryEntry>();
        var categories = new ConcurrentBag<(BestiaryCategory Category, string Path)>();
        // an expansion's entries sit in a virtual group that stands for one of the game's categories, by its journal file
        var virtualGroups = new ConcurrentDictionary<Guid, string>();
        Parallel.ForEach(locator.AllPaths(".journal"), path =>
        {
            Cr2wFile file;
            try { file = Cr2wFile.Read(locator.ReadFile(path) ?? []); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
            for (int i = 0; i < file.Exports.Count; i++)
            {
                string cls = file.Exports[i].ClassName;
                if (cls == "CJournalCreatureGroup")
                {
                    var g = file.Object(i);
                    categories.Add((new BestiaryCategory(GuidOf(g, "guid"), g["baseName"] as string ?? "", IdOf(g["name"]), g.Integer("order")), path));
                }
                else if (cls == "CJournalCreatureVirtualGroup" && file.Object(i) is var v && v["linkedObjectPath"] is string { Length: > 0 } linked)
                    virtualGroups[GuidOf(v, "guid")] = linked.Replace('/', '\\');
                else if (cls == "CJournalCreature") entries.Add(ReadEntry(file, file.Object(i), path));
            }
        });
        var byPath = categories.GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Category.Guid, StringComparer.OrdinalIgnoreCase);
        // one entry a journal file; the first found wins where a DLC repeats one
        return ([.. entries.DistinctBy(e => e.Guid)
                .Select(e => virtualGroups.TryGetValue(e.Category, out var linked) && byPath.TryGetValue(linked, out var category) ? e with { Category = category } : e)
                .OrderBy(e => e.BaseName, StringComparer.OrdinalIgnoreCase)],
            [.. categories.Select(c => c.Category).DistinctBy(c => c.Guid).OrderBy(c => c.Order).ThenBy(c => c.BaseName, StringComparer.OrdinalIgnoreCase)]);
    }

    private static BestiaryEntry ReadEntry(Cr2wFile file, RedObject c, string path)
    {
        var descriptions = new List<uint>();
        var spots = new List<BestiaryVitalSpot>();
        var hints = new List<uint>();
        foreach (var group in c.Array("children").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
            foreach (var child in group.Array("children").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
                switch (child.ClassName)
                {
                    case "CJournalCreatureDescriptionEntry": if (IdOf(child["description"]) is var d and not 0) descriptions.Add(d); break;
                    case "CJournalCreatureVitalSpotEntry": spots.Add(new BestiaryVitalSpot(IdOf(child["title"]), IdOf(child["description"]))); break;
                    case "CJournalCreatureGameplayHint": if (IdOf(child["description"]) is var h and not 0) hints.Add(h); break;
                }
        return new BestiaryEntry(GuidOf(c, "guid"), c["baseName"] as string ?? "", path, IdOf(c["name"]), c["image"] as string ?? "", GuidOf(c, "parentGuid"),
            c["entityTemplate"] is RedHandle t ? file.PathOf(t) : null, [.. c.Array("itemsUsedAgainstCreature").Select(x => x?.ToString() ?? "").Where(x => x.Length > 0)],
            descriptions, spots, hints);
    }

    private static uint IdOf(object? value) => value switch { uint u => u, int i => (uint)i, _ => 0 };

    private static Guid GuidOf(RedObject o, string name) => o[name] is Guid g ? g : Guid.Empty;

    /// <summary>The game's resource aliases (BestiaryBear → gameplay\journal\bestiary\bear.journal), from its resources XML and the DLCs'.</summary>
    private static Dictionary<string, string> ReadAliases(ResourceLocator locator)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in locator.AllPaths(".xml").Where(p => p.Contains(@"globals\resources\", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var doc = XDocument.Parse(TextDecoding.Decode(locator.ReadFile(path) ?? []));
                foreach (var res in doc.Descendants("res"))
                    if (res.Attribute("alias")?.Value is { } alias && res.Attribute("path")?.Value is { } target) map.TryAdd(alias, target.Replace('/', '\\'));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { }
        }
        return map;
    }

    // ---------------------------------------------------------------- the game's rules

    /// <summary>One test of the game's: when it holds for a monster, the monster's entry is the alias's (null: it has none).</summary>
    private sealed record Rule(Func<BestiaryCreature, bool> Test, string? Alias);

    /// <summary>
    /// The tests the game makes as the player meets a monster, in its order: AddBestiaryKnowledgeEP2's first (the base
    /// function asks it first), then AddBestiaryKnowledge's. Each is an <c>if</c> of the chain, its condition made of
    /// HasAbility, GetSfxTag and HasTag, and the first entry it activates.
    /// </summary>
    private static List<Rule> ReadRules(string npcScript)
    {
        if (!File.Exists(npcScript)) return [];
        string text = TextDecoding.Decode(File.ReadAllBytes(npcScript)).Replace("\r\n", "\n");
        var rules = new List<Rule>();
        foreach (string function in new[] { "AddBestiaryKnowledgeEP2", "AddBestiaryKnowledge" })
        {
            var m = Regex.Match(text, @"function\s+" + function + @"\s*\(\)[^{]*\{");
            if (!m.Success) continue;
            // the function's body, to its closing brace
            int depth = 1, at = m.Index + m.Length;
            while (at < text.Length && depth > 0) depth += text[at++] switch { '{' => 1, '}' => -1, _ => 0 };
            string body = text[(m.Index + m.Length)..(at - 1)];
            // the chain's ifs are the ones at the body's own indent; anything deeper belongs to one of them
            var lines = body.Split('\n');
            string? indent = lines.Select(l => Regex.Match(l, @"^(\s*)if\s*\(")).FirstOrDefault(x => x.Success)?.Groups[1].Value;
            if (indent is null) continue;
            var starts = lines.Select((l, i) => (l, i)).Where(x => x.l.StartsWith(indent + "if", StringComparison.Ordinal) && Regex.IsMatch(x.l, @"^\s*if\s*\(")).Select(x => x.i).ToList();
            for (int k = 0; k < starts.Count; k++)
            {
                string block = string.Join("\n", lines[starts[k]..(k + 1 < starts.Count ? starts[k + 1] : lines.Length)]);
                if (Condition(block) is not { } condition) continue;
                string? alias = Regex.Match(block, @"activate\w*BestiaryEntryWithAlias\s*\(\s*""([^""]+)""") is { Success: true } a ? a.Groups[1].Value : null;
                // "return" with no entry: NoJournalEntry
                if (alias is null && !block.Contains("return", StringComparison.Ordinal)) continue;
                if (Parse(condition) is { } test) rules.Add(new Rule(test, alias));
            }
        }
        return rules;
    }

    /// <summary>An if's condition: the text inside its parentheses.</summary>
    private static string? Condition(string block)
    {
        int open = block.IndexOf('(');
        if (open < 0) return null;
        int depth = 0;
        for (int i = open; i < block.Length; i++)
        {
            if (block[i] == '(') depth++;
            else if (block[i] == ')' && --depth == 0) return block[(open + 1)..i];
        }
        return null;
    }

    private static string? Match(List<Rule> rules, BestiaryCreature creature) => rules.FirstOrDefault(r => r.Test(creature))?.Alias;

    /// <summary>
    /// A condition as a test: HasAbility('x'), GetSfxTag() == 'x' and HasTag('x'), with !, &amp;&amp;, || and brackets.
    /// Anything else can't be told from a template, and fails.
    /// </summary>
    private static Func<BestiaryCreature, bool>? Parse(string condition)
    {
        var tokens = Regex.Matches(condition, @"\|\||&&|!=|==|!|\(|\)|'[^']*'|""[^""]*""|[A-Za-z_][A-Za-z0-9_\.]*|\S").Select(t => t.Value).ToList();
        int at = 0;
        string? Peek() => at < tokens.Count ? tokens[at] : null;
        Func<BestiaryCreature, bool> Or()
        {
            var left = And();
            while (Peek() == "||") { at++; var a = left; var b = And(); left = c => a(c) || b(c); }
            return left;
        }
        Func<BestiaryCreature, bool> And()
        {
            var left = Not();
            while (Peek() == "&&") { at++; var a = left; var b = Not(); left = c => a(c) && b(c); }
            return left;
        }
        Func<BestiaryCreature, bool> Not()
        {
            if (Peek() == "!") { at++; var inner = Not(); return c => !inner(c); }
            if (Peek() == "(") { at++; var inner = Or(); if (Peek() == ")") at++; return inner; }
            string word = tokens[at++];
            // a call: its name, then its brackets with what's in them
            var args = new List<string>();
            if (Peek() == "(")
            {
                int depth = 0;
                do
                {
                    string t = tokens[at++];
                    if (t == "(") depth++;
                    else if (t == ")") depth--;
                    else if (t.Length > 1 && t[0] is '\'' or '"') args.Add(t[1..^1]);
                } while (depth > 0 && at < tokens.Count);
            }
            if (word == "GetSfxTag" && Peek() is "==" or "!=")
            {
                bool equal = tokens[at++] == "==";
                string tag = at < tokens.Count && tokens[at].Length > 1 ? tokens[at++][1..^1] : "";
                return c => c.SfxTag.Equals(tag, StringComparison.OrdinalIgnoreCase) == equal;
            }
            string name = args.FirstOrDefault() ?? "";
            return word switch
            {
                "HasAbility" => c => c.Abilities.Contains(name, StringComparer.OrdinalIgnoreCase),
                "HasTag" => c => c.Tags.Contains(name, StringComparer.OrdinalIgnoreCase),
                _ => _ => false,
            };
        }
        try { return tokens.Count == 0 ? null : Or(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ---------------------------------------------------------------- the templates

    /// <summary>A template's sound tag (CAnimGlobalParam), abilities (CCharacterStatsParam), tags and level (its entity's own), and loot.</summary>
    private static BestiaryCreature? ReadCreature(ResourceLocator locator, string template, Func<string, LootSpot?> loot)
    {
        try
        {
            var file = Cr2wFile.Read(locator.ReadFile(template) ?? []);
            if (file.First("CEntityTemplate") is not { } t) return null;
            string sfx = "";
            var abilities = new List<string>();
            foreach (var p in t.Array("templateParams").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
            {
                if (p.ClassName == "CAnimGlobalParam" && p["sfxTag"]?.ToString() is { Length: > 0 } tag && sfx.Length == 0) sfx = tag;
                if (p.ClassName == "CCharacterStatsParam") abilities.AddRange(p.Array("abilities").Select(a => a?.ToString() ?? "").Where(a => a.Length > 0));
            }
            int level = 1;
            var tags = new List<string>();
            if (t["entityObject"] is RedHandle eh && file.Object(eh) is { } entity)
            {
                if (entity["level"] is not null) level = (int)entity.Integer("level");
                if (entity["tags"] is byte[] raw) tags.AddRange(file.Tags(raw));
            }
            return new BestiaryCreature(template, sfx, abilities, tags, level, loot(template));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
    }
}
