using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Loot;
using W3ModWorkbench.Core.Scenes;
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

/// <summary>What spawns a creature at a place: an encounter placed in a world's layer, or a community (.w2comm).</summary>
public enum BestiarySource { Encounter, Community }

/// <summary>
/// A place that can spawn a template: an encounter (Layer: its layer) or a community's story phase (Layer: the
/// community file), where, its area's outline when it has one, and how many it spawns (0 when it doesn't say). Quest
/// is set for one kept with a quest's files, which spawns only while the quest needs it.
/// </summary>
public sealed record BestiaryPlace(string World, string Layer, string Name, Vector3 Position, IReadOnlyList<Vector2>? Area, string Template,
    int TotalSpawnLimit, int MaxSpawnedAtOnce)
{
    public BestiarySource Source { get; init; } = BestiarySource.Encounter;

    public bool Quest => BestiaryIndex.IsQuestPath(Layer);
}

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

    /// <summary>Whether a file is kept with a quest's files (quests\…, dlc\bob\data\quests\…).</summary>
    public static bool IsQuestPath(string path) =>
        path.StartsWith(@"quests\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\quests\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The creatures of an entry.</summary>
    public IEnumerable<BestiaryCreature> CreaturesOf(BestiaryEntry entry) => Creatures.Values.Where(c => c.Entry == entry);

    /// <summary>Reads the bestiary, the rules, the communities and every world's encounters: a few seconds.</summary>
    public static BestiaryIndex Build(ResourceLocator locator, string? scriptsFolder, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("journal");
        var (entries, categories) = ReadJournals(locator);
        var aliases = ReadAliases(locator);
        var rules = scriptsFolder is null ? [] : ReadRules(Path.Combine(scriptsFolder, "game", "npc", "npc.ws"));
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = token };

        // each creature's entry: the game's rules first, then a template the journal names itself
        var loot = LootIndex.TemplateLoot(locator);
        var creatures = new ConcurrentDictionary<string, BestiaryCreature>(StringComparer.OrdinalIgnoreCase);
        var byJournal = entries.GroupBy(e => e.JournalPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byTemplate = entries.Where(e => e.Template is not null).GroupBy(e => e.Template!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        BestiaryEntry? EntryOf(BestiaryCreature c) => byTemplate.TryGetValue(c.Template, out var named) ? named
            : Match(rules, c) is { } alias && aliases.TryGetValue(alias, out var journal) && byJournal.TryGetValue(journal, out var entry) ? entry : null;
        // one that turns into a monster when it dies (a cloaked woman into an alp) is that monster's, fought at its level
        var deathSpawns = ReadDeathSpawns(locator, options);
        var spawnedCreatures = new ConcurrentDictionary<string, BestiaryCreature?>(StringComparer.OrdinalIgnoreCase);
        BestiaryCreature? Spawned(string resource) => spawnedCreatures.GetOrAdd(resource, r =>
        {
            if (!aliases.TryGetValue(r, out var path) || ReadCreature(locator, path, _ => null) is not { } spawned) return null;
            spawned.Entry = EntryOf(spawned);
            return spawned;
        });
        void ReadCreatures(IEnumerable<string> templates) => Parallel.ForEach(templates.Where(t => !creatures.ContainsKey(t)).ToList(), options, template =>
        {
            if (ReadCreature(locator, template, loot) is not { } c) return;
            c.Entry = EntryOf(c);
            if (c.Entry is null && c.Abilities.Where(deathSpawns.ContainsKey).Select(a => Spawned(deathSpawns[a])).FirstOrDefault(s => s?.Entry is not null) is { } into)
                c = c with { Level = into.Level, Entry = into.Entry };
            creatures[template] = c;
        });

        // the communities that spawn a bestiary creature (a village's people are spawned the same way, and left out),
        // and the tags of the spots their story phases spawn at
        progress?.Report("communities");
        var communities = ReadCommunities(locator, options);
        ReadCreatures(communities.SelectMany(c => c.Templates).Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var c in creatures.Values.Where(c => c.Entry is null).ToList()) creatures.TryRemove(c.Template, out _);
        communities = [.. communities.Select(c => c with { Templates = [.. c.Templates.Where(creatures.ContainsKey)] }).Where(c => c.Templates.Count > 0)];
        var wanted = communities.SelectMany(c => c.Phases).SelectMany(p => p.SpawnTags.Concat(p.Actions.SelectMany(a => a.ApTags))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // the places, world by world, and the spots tagged as those communities' spawn and action points
        var places = new ConcurrentBag<BestiaryPlace>();
        var tagged = new Dictionary<string, ConcurrentDictionary<string, ConcurrentBag<Vector3>>>(StringComparer.OrdinalIgnoreCase);
        var worlds = locator.AllPaths(".w2w").Where(w => !w.StartsWith(@"engine\", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string worldPath in worlds)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(worldPath);
            var tags = tagged[worldPath] = new(StringComparer.OrdinalIgnoreCase);
            WorldFile world;
            try { world = WorldFile.Read(locator.ReadFile(worldPath) ?? throw new FileNotFoundException(worldPath), worldPath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
            if (world.EmbeddedLayers is not { } tree) continue;
            Parallel.ForEach(tree.AllLayers(), options, layer =>
            {
                Cr2wFile file;
                try { file = Cr2wFile.Read(locator.ReadFile(layer.Path) ?? []); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
                if (file.First("CLayer") is not { } cl) return;
                bool hasEncounter = false;
                foreach (var h in cl.Array("entities").OfType<RedHandle>())
                {
                    if (file.Object(h) is not { } e) continue;
                    if (e.ClassName == "CEncounter") hasEncounter = true;
                    if (wanted.Count == 0 || e["tags"] is not byte[] { Length: > 1 } raw) continue;
                    var found = file.Tags(raw).Where(wanted.Contains).ToList();
                    if (found.Count == 0) continue;
                    var at = SceneStaging.Transform(e["transform"]).Position;
                    foreach (string tag in found) tags.GetOrAdd(tag, _ => []).Add(at);
                }
                if (!hasEncounter) return;
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

        // a community's story phase stands where its spawn points are, else where its timetable sends it
        IReadOnlyList<Vector3> Find(string world, IEnumerable<string> tags) =>
            tagged.TryGetValue(world, out var byTag) ? [.. tags.SelectMany(t => byTag.TryGetValue(t, out var at) ? at : []).Distinct()] : [];
        foreach (var community in communities)
            foreach (var phase in community.Phases)
            {
                // a timetable's layer named without its world is in the world that has its action points
                var apTags = phase.Actions.SelectMany(a => a.ApTags).ToList();
                string? world = phase.Actions.Select(a => a.World).FirstOrDefault(w => w.Length > 0 && tagged.ContainsKey(w))
                    ?? worlds.Select(w => (World: w, Hits: Find(w, phase.SpawnTags).Count * 1000 + Find(w, apTags).Count)).Where(x => x.Hits > 0)
                        .OrderByDescending(x => x.Hits).Select(x => x.World).FirstOrDefault();
                if (world is null) continue;
                var spots = Find(world, phase.SpawnTags);
                if (spots.Count == 0) spots = Find(world, phase.Actions.Where(a => a.World.Length == 0 || a.World.Equals(world, StringComparison.OrdinalIgnoreCase)).SelectMany(a => a.ApTags));
                if (spots.Count == 0) continue;
                foreach (string template in community.Templates)
                    places.Add(new BestiaryPlace(world, community.Path, community.Comment.Length > 0 ? community.Comment : Path.GetFileNameWithoutExtension(community.Path),
                        spots[0], null, template, 0, phase.Quantity) { Source = BestiarySource.Community });
            }

        // every template the encounters spawn or the journal names
        progress?.Report("templates");
        ReadCreatures(places.Select(p => p.Template).Concat(entries.Select(e => e.Template).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase));

        return new BestiaryIndex
        {
            Entries = entries, Categories = categories, Creatures = new Dictionary<string, BestiaryCreature>(creatures, StringComparer.OrdinalIgnoreCase),
            Places = [.. places.OrderBy(p => p.World).ThenBy(p => p.Layer, StringComparer.OrdinalIgnoreCase)], Worlds = worlds, Rules = rules.Count,
        };
    }

    // ---------------------------------------------------------------- communities

    /// <summary>A story phase of a community entry: the tags of its spawn points, its timetable's action points (by world) and how many it spawns.</summary>
    private sealed record CommunityPhase(IReadOnlyList<string> SpawnTags, IReadOnlyList<(string World, IReadOnlyList<string> ApTags)> Actions, int Quantity);

    /// <summary>A community entry: its file, its comment, the templates it spawns and its story phases.</summary>
    private sealed record CommunitySpawn(string Path, string Comment, IReadOnlyList<string> Templates, IReadOnlyList<CommunityPhase> Phases);

    private static List<CommunitySpawn> ReadCommunities(ResourceLocator locator, ParallelOptions options)
    {
        var found = new ConcurrentBag<CommunitySpawn>();
        Parallel.ForEach(locator.AllPaths(".w2comm"), options, path =>
        {
            Cr2wFile file;
            try { file = Cr2wFile.Read(locator.ReadFile(path) ?? []); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
            if (file.First("CCommunity") is not { } community) return;
            // a timetable's action points; a layer is named as "its path;its world's path" (without extensions)
            var timetables = new Dictionary<string, List<(string, IReadOnlyList<string>)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var timetable in community.Array("storyPhaseTimetable").OfType<RedObject>())
                if (timetable["name"] is string name)
                    timetables[name] = [.. timetable.Array("actionCategies").OfType<RedObject>().SelectMany(t => t.Array("actions").OfType<RedObject>()).SelectMany(a =>
                    {
                        string layer = a.Struct("layerName")?["layerName"] as string ?? "";
                        int split = layer.IndexOf(';');
                        string world = split >= 0 ? layer[(split + 1)..] + ".w2w" : "";
                        return a.Array("actionCategories").OfType<RedObject>().Select(c => (world, (IReadOnlyList<string>)[.. TagsOf(file, c["apTags"])]));
                    })];
            foreach (var entry in community.Array("communityTable").OfType<RedObject>())
            {
                var templates = entry.Array("entities").OfType<RedObject>().Select(e => e["entityTemplate"] is RedHandle h ? file.PathOf(h) : null)
                    .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (templates.Count == 0) continue;
                var phases = entry.Array("storyPhases").OfType<RedObject>().Select(p => new CommunityPhase([.. TagsOf(file, p["spawnPointTags"])],
                    p["timetableName"] is string tt && timetables.TryGetValue(tt, out var actions) ? actions : [],
                    p.Array("spawnTimetable").OfType<RedObject>().Select(t => (int)t.Integer("quantity", 1)).DefaultIfEmpty(0).Max())).ToList();
                found.Add(new CommunitySpawn(path, entry["comment"] as string ?? "", templates, phases));
            }
        });
        return [.. found];
    }

    /// <summary>
    /// The monsters some creatures turn into when they die, from the game's AI trees: an ability test (has SpawnAlpor)
    /// above a spawn task (CBTTaskBruxaSpawnDef) names the resource alias spawned in their place. By ability.
    /// </summary>
    private static Dictionary<string, string> ReadDeathSpawns(ResourceLocator locator, ParallelOptions options)
    {
        var found = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var marker = "CBTTaskBruxaSpawnDef"u8.ToArray();
        Parallel.ForEach(locator.AllPaths(".w2behtree"), options, path =>
        {
            if (locator.ReadFile(path) is not { } data || data.AsSpan().IndexOf(marker) < 0) return;
            Cr2wFile file;
            try { file = Cr2wFile.Read(data); }
            catch (InvalidDataException) { return; }
            RedObject? TaskOf(RedObject decorator) => decorator["taskOrigin"] is RedHandle h ? file.Object(h) : null;
            for (int i = 0; i < file.Exports.Count; i++)
            {
                if (TaskOf(file.Object(i)) is not { ClassName: "CBTTaskHasAbilityDef" } test || test["abilityName"]?.ToString() is not { Length: > 0 } ability) continue;
                // the decorators below the test, down to the spawn task
                var node = file.Object(i);
                for (int depth = 0; depth < 8 && node["child"] is RedHandle child && file.Object(child) is { } next; depth++)
                {
                    node = next;
                    if (TaskOf(node) is { ClassName: "CBTTaskBruxaSpawnDef" } spawn && spawn["resourceName"]?.ToString() is { Length: > 0 } resource)
                    {
                        found.TryAdd(ability, resource);
                        break;
                    }
                }
            }
        });
        return new Dictionary<string, string>(found, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A tag list's tags, kept as its bytes or in a struct holding them.</summary>
    private static IEnumerable<string> TagsOf(Cr2wFile file, object? value) => value switch
    {
        byte[] raw => file.Tags(raw),
        RedObject holder => TagsOf(file, holder["tags"]),
        _ => [],
    };

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
            var (sfx, abilities) = SoundAndAbilities(locator, file, t, 0);
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

    /// <summary>
    /// A template's sound tag and abilities, from its own params, else from the templates it includes, as the game puts
    /// them together (a cloaked woman who turns into an alp includes the alp's template, and with it the alp's).
    /// </summary>
    private static (string Sfx, List<string> Abilities) SoundAndAbilities(ResourceLocator locator, Cr2wFile file, RedObject template, int depth)
    {
        string sfx = "";
        var abilities = new List<string>();
        foreach (var p in template.Array("templateParams").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
        {
            if (p.ClassName == "CAnimGlobalParam" && p["sfxTag"]?.ToString() is { Length: > 0 } tag && sfx.Length == 0) sfx = tag;
            if (p.ClassName == "CCharacterStatsParam") abilities.AddRange(p.Array("abilities").Select(a => a?.ToString() ?? "").Where(a => a.Length > 0));
        }
        if (depth > 4 || (sfx.Length > 0 && abilities.Count > 0)) return (sfx, abilities);
        foreach (var inc in template.Array("includes").OfType<RedHandle>())
        {
            if (file.PathOf(inc) is not { } path) continue;
            try
            {
                var included = Cr2wFile.Read(locator.ReadFile(path) ?? []);
                if (included.First("CEntityTemplate") is not { } it) continue;
                var (s, a) = SoundAndAbilities(locator, included, it, depth + 1);
                if (sfx.Length == 0) sfx = s;
                if (abilities.Count == 0) abilities = a;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        }
        return (sfx, abilities);
    }
}
