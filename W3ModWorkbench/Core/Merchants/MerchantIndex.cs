using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Loot;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.Core.World;

namespace W3ModWorkbench.Core.Merchants;

/// <summary>What spawns a merchant: an encounter placed in a world's layer, or a community (.w2comm).</summary>
public enum MerchantSource { Encounter, Community }

/// <summary>
/// A spot a merchant goes to: an action point (where it works, eats or sleeps, by its category) or a spawn point, and
/// the time of day it's there, in seconds after midnight; null for any time.
/// </summary>
public sealed record MerchantStand(Vector3 Position, string Category, int? From, int? To);

/// <summary>A craft a merchant offers (ECT_Smith, ECT_Armorer…) and at what level (ECL_Journeyman to ECL_Grand_Master).</summary>
public sealed record MerchantCraft(string Type, string Level);

/// <summary>
/// A merchant's template and what the game reads from it: its name (a string ID), its map pin kind, its crafts, its
/// funds, its price multiplier and the inventory tags that name its price abilities, its tags, its stock (the
/// template's loot params, rolled when the shop is opened) and its shop scene.
/// </summary>
public sealed record Merchant(string Template, string EntityClass, uint DisplayName, string PinType, IReadOnlyList<MerchantCraft> Crafts, string FundsType,
    float PriceMult, IReadOnlyList<string> InventoryTags, IReadOnlyList<string> Tags, LootSpot? Stock, string? Scene)
{
    /// <summary>
    /// What kind of merchant it is: its map pin's kind, else (a plain shopkeeper's, or one with no pin) the kind its
    /// inventory's type tag names, a boat builder by its stock, else a shopkeeper.
    /// </summary>
    public string Kind => PinType is { Length: > 0 } and not "Shopkeeper" ? PinType
        : InventoryTags.FirstOrDefault(t => t.StartsWith("type_", StringComparison.OrdinalIgnoreCase)) switch
        {
            "type_armorer" => "Armorer",
            "type_blacksmith" => "Blacksmith",
            "type_inn" => "Innkeeper",
            "type_book" => "BookSeller",
            "type_barber" => "Hairdresser",
            "type_collector" => "Collector",
            "type_alchemist" => "Alchemist",
            "type_herbalist" => "Herbalist",
            _ when Stock?.Params.Any(p => p.Tables.Any(t => t.Table.Contains("Boat_Builder", StringComparison.OrdinalIgnoreCase))) == true => "BoatBuilder",
            _ => "Shopkeeper",
        };
}

/// <summary>
/// A spawn condition: a fact compared with a number (Op is Equal, NotEqual, Less, LessEqual, Greater or GreaterEqual),
/// or, with no fact, another condition by its class and values.
/// </summary>
public sealed record MerchantCondition(string Fact, string Op, long Value, string Other);

/// <summary>
/// A place a merchant can be: the encounter or community that spawns it (File: the encounter's layer or the community),
/// the world, the layer its spots are in (for the area's name; "" when not known), where (the first spot it stands at,
/// else the encounter's own place; Located is false when neither is known), the spots it goes to, and what decides
/// whether it's there: the story or quest phases it's spawned in ("" for the default phase), the creature groups
/// quests can turn off, other spawn conditions, and the encounter's tags (which quests name it by).
/// </summary>
public sealed record MerchantPlace(string Template, MerchantSource Source, string World, string File, string Layer, string Name, Vector3 Position, bool Located,
    IReadOnlyList<Vector2>? Area, IReadOnlyList<MerchantStand> Stands, IReadOnlyList<string> Phases, IReadOnlyList<string> Groups,
    IReadOnlyList<MerchantCondition> Conditions, IReadOnlyList<string> Tags);

/// <summary>What a quest does to a merchant's place.</summary>
public enum MerchantQuestAction
{
    /// <summary>Starts a story phase of a community (CActivateStoryPhase).</summary>
    StoryPhase,
    /// <summary>Turns a community off (CDeactivateSpawnset).</summary>
    CommunityOff,
    /// <summary>Starts a spawn phase of an encounter (CQuestEncounterManagerBlock, CQuestEncounterPhaseBlock).</summary>
    EncounterPhase,
    /// <summary>Turns an encounter on (CQuestEncounterActivator).</summary>
    EncounterOn,
    /// <summary>Turns an encounter off (CQuestEncounterActivator with deactivateEncounter).</summary>
    EncounterOff,
}

/// <summary>A quest file's block that changes a merchant's place: what it does, and the phase it names ("" for none).</summary>
public sealed record MerchantQuestLink(string QuestFile, MerchantQuestAction Action, string Phase);

/// <summary>
/// The game's merchants and where they are: every encounter and community in the worlds that spawns a merchant
/// template (one with a merchant or craftsman component, the merchant class or a shop's loot table), the spots each
/// goes to through the day (its action points and spawn points, found by their tags in the same world), the phases and
/// groups that decide whether it's there, and the quests that change those.
/// </summary>
public sealed class MerchantIndex
{
    public required IReadOnlyDictionary<string, Merchant> Merchants { get; init; }
    public required IReadOnlyList<MerchantPlace> Places { get; init; }
    public required IReadOnlyList<string> Worlds { get; init; }

    /// <summary>Quest blocks by the community file they name.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<MerchantQuestLink>> ByCommunity { get; init; }

    /// <summary>Quest blocks by the encounter tag they name.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<MerchantQuestLink>> ByEncounterTag { get; init; }

    /// <summary>The quests' titles (string IDs) by their code (mq7010, q704…), from the journal.</summary>
    public required IReadOnlyDictionary<string, uint> QuestTitles { get; init; }

    /// <summary>A quest's code from a fact's, a quest file's or a journal quest's name: its first word when that's letters then digits (mq7010_finished → mq7010).</summary>
    public static string? QuestCode(string name)
    {
        var m = Regex.Match(Path.GetFileNameWithoutExtension(name), @"^([a-z]{1,5}\d{2,4}[a-z]?)([_ ]|$)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>The title (a string ID) of the quest a fact or quest file belongs to, or 0.</summary>
    public uint QuestTitle(string name) => QuestCode(name) is { } code && QuestTitles.TryGetValue(code, out uint id) ? id : 0;

    /// <summary>The quest blocks that change a place.</summary>
    public IEnumerable<MerchantQuestLink> QuestsOf(MerchantPlace place) => place.Source == MerchantSource.Community
        ? ByCommunity.TryGetValue(place.File, out var c) ? c : []
        : place.Tags.SelectMany(t => ByEncounterTag.TryGetValue(t, out var e) ? e : []).Distinct();

    private sealed record EncounterDef(string World, string Layer, string Name, Vector3 Position, IReadOnlyList<Vector2>? Area, IReadOnlyList<string> Tags,
        string Definition, string Template, Cr2wFile File, RedObject Encounter);

    /// <summary>Reads every world's layers, the communities, the merchant templates and the quests: well under a minute.</summary>
    public static MerchantIndex Build(ResourceLocator locator, IProgress<string>? progress, CancellationToken token)
    {
        var worlds = locator.AllPaths(".w2w").Where(w => !w.StartsWith(@"engine\", StringComparison.OrdinalIgnoreCase)).ToList();
        var loot = LootIndex.TemplateLoot(locator);
        var merchants = new ConcurrentDictionary<string, Merchant?>(StringComparer.OrdinalIgnoreCase);
        Merchant? MerchantOf(string template) => merchants.GetOrAdd(template, t => ReadMerchant(locator, t, loot));

        // the worlds' layers: encounters that spawn merchants, and every tagged entity's position (action and spawn points)
        var encounters = new ConcurrentBag<EncounterDef>();
        var tagged = new Dictionary<string, ConcurrentDictionary<string, ConcurrentBag<Vector3>>>(StringComparer.OrdinalIgnoreCase);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = token };
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
                    if (e["tags"] is not byte[] { Length: > 1 } raw) continue;
                    var at = SceneStaging.Transform(e["transform"]).Position;
                    foreach (string tag in file.Tags(raw)) tags.GetOrAdd(tag, _ => []).Add(at);
                }
                if (!hasEncounter) return;
                foreach (var e in WorldFile.Entities(file).Where(e => e.ClassName == "CEncounter"))
                {
                    var area = e.Components.FirstOrDefault(c => c.Outline is { Count: > 2 })?.Outline;
                    var encounterTags = e.Object["tags"] is byte[] t ? file.Tags(t) : [];
                    foreach (var dh in e.Object.Array("creatureDefinition").OfType<RedHandle>())
                        if (file.Object(dh) is { } d && d["entityTemplate"] is RedHandle th && file.PathOf(th) is { } template && MerchantOf(template) is not null)
                            encounters.Add(new EncounterDef(worldPath, layer.Path, e.Name, e.Position, area, encounterTags, d["definitionName"] as string ?? "", template, file, e.Object));
                }
            });
        }

        IReadOnlyList<Vector3> Find(string world, IEnumerable<string> tags) =>
            tagged.TryGetValue(world, out var byTag) ? [.. tags.SelectMany(t => byTag.TryGetValue(t, out var at) ? at : []).Distinct()] : [];

        var places = new ConcurrentBag<MerchantPlace>();

        // encounters: each merchant definition's entries in the spawn tree (and the trees it includes, read once)
        progress?.Report("encounters");
        var trees = new Dictionary<string, Cr2wFile?>(StringComparer.OrdinalIgnoreCase);
        Cr2wFile? ReadTree(string path)
        {
            if (trees.TryGetValue(path, out var known)) return known;
            try { return trees[path] = Cr2wFile.Read(locator.ReadFile(path) ?? []); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return trees[path] = null; }
        }
        foreach (var def in encounters)
        {
            var leaves = new List<Leaf>();
            if (def.Encounter["spawnTree"] is RedHandle root && def.File.Object(root) is { } tree)
                Walk(def.File, tree, new Context([], [], [], null, null, []), def.Definition, leaves, [], ReadTree);
            // a definition the tree never spawns (an encounter keeps some it doesn't use) puts nobody there
            if (leaves.Count == 0) continue;
            // one place for each set of phases, groups and conditions; its entries' times kept on the spots
            foreach (var same in leaves.GroupBy(l => string.Join("|", l.Context.Phases.Distinct()) + "#" + string.Join("|", l.Context.Groups.Distinct()) + "#" + string.Join("|", l.Context.Conditions.Distinct())))
            {
                var stands = new List<MerchantStand>();
                foreach (var leaf in same)
                {
                    var found = leaf.ActionPoints.SelectMany(a => Find(def.World, a.ApTags).Select(p => new MerchantStand(p, a.Category, leaf.Context.From, leaf.Context.To))).ToList();
                    if (found.Count == 0)
                        found.AddRange(Find(def.World, leaf.SpawnPoints.Concat(leaf.Context.SpawnPoints)).Select(p => new MerchantStand(p, "spawn", leaf.Context.From, leaf.Context.To)));
                    stands.AddRange(found);
                }
                var context = same.First().Context;
                places.Add(new MerchantPlace(def.Template, MerchantSource.Encounter, def.World, def.Layer, def.Layer, def.Name, stands.FirstOrDefault()?.Position ?? def.Position, true,
                    def.Area, [.. stands.Distinct()], [.. context.Phases.Distinct()], [.. context.Groups.Distinct()], [.. context.Conditions.Distinct()], def.Tags));
            }
        }

        // communities: each merchant entry's story phases and the timetables they keep
        progress?.Report("communities");
        Parallel.ForEach(locator.AllPaths(".w2comm").ToList(), options, path =>
        {
            Cr2wFile file;
            try { file = Cr2wFile.Read(locator.ReadFile(path) ?? []); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
            if (file.First("CCommunity") is not { } community) return;
            var timetables = ReadTimetables(file, community);
            foreach (var entry in community.Array("communityTable").OfType<RedObject>())
            {
                var templates = entry.Array("entities").OfType<RedObject>().Select(e => e["entityTemplate"] is RedHandle h ? file.PathOf(h) : null)
                    .OfType<string>().Where(t => MerchantOf(t) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (templates.Count == 0) continue;
                foreach (var phase in entry.Array("storyPhases").OfType<RedObject>())
                {
                    string phaseName = TagsOf(file, phase["storyPhaseName"]).FirstOrDefault() ?? "";
                    var stands = new List<MerchantStand>();
                    string world = "", layer = "";
                    if (phase["timetableName"] is string tt && timetables.TryGetValue(tt, out var actions))
                        foreach (var a in actions)
                        {
                            if (world.Length == 0) (world, layer) = (a.World, a.Layer);
                            stands.AddRange(Find(a.World, a.ApTags).Select(p => new MerchantStand(p, a.Category, a.From, a.To)));
                        }
                    var spawnTags = TagsOf(file, phase["spawnPointTags"]).ToList();
                    if (stands.Count == 0 && world.Length > 0)
                        stands.AddRange(Find(world, spawnTags).Select(p => new MerchantStand(p, "spawn", null, null)));
                    if (world.Length == 0)
                    {
                        // no timetable: the world that has its spawn points
                        world = worlds.FirstOrDefault(w => Find(w, spawnTags).Count > 0) ?? "";
                        stands.AddRange(Find(world, spawnTags).Select(p => new MerchantStand(p, "spawn", null, null)));
                    }
                    if (world.Length == 0) continue;
                    foreach (string template in templates)
                        places.Add(new MerchantPlace(template, MerchantSource.Community, world, path, layer, entry["comment"] as string ?? "", stands.FirstOrDefault()?.Position ?? default,
                            stands.Count > 0, null, stands, [phaseName], [], [], []));
                }
            }
        });

        // quests: the blocks that start a community's phase or an encounter's, or turn either on or off
        progress?.Report("quests");
        var byCommunity = new ConcurrentDictionary<string, ConcurrentBag<MerchantQuestLink>>(StringComparer.OrdinalIgnoreCase);
        var byTag = new ConcurrentDictionary<string, ConcurrentBag<MerchantQuestLink>>(StringComparer.OrdinalIgnoreCase);
        var quests = locator.AllPaths(".w2phase").Concat(locator.AllPaths(".w2quest")).ToList();
        Parallel.ForEach(quests, options, quest =>
        {
            Cr2wFile file;
            try { file = Cr2wFile.Read(locator.ReadFile(quest) ?? []); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
            for (int i = 0; i < file.Exports.Count; i++)
            {
                string cls = file.Exports[i].ClassName;
                if (cls is not ("CActivateStoryPhase" or "CDeactivateSpawnset" or "CQuestEncounterManagerBlock" or "CQuestEncounterPhaseBlock" or "CQuestEncounterActivator")) continue;
                var o = file.Object(i);
                if (cls is "CActivateStoryPhase" or "CDeactivateSpawnset")
                {
                    if (o["spawnset"] is RedHandle h && file.PathOf(h) is { } spawnset)
                        byCommunity.GetOrAdd(spawnset, _ => []).Add(new MerchantQuestLink(quest,
                            cls == "CActivateStoryPhase" ? MerchantQuestAction.StoryPhase : MerchantQuestAction.CommunityOff, o["phase"] as string ?? ""));
                }
                else if (o["encounterTag"] is string tag && tag.Length > 0)
                {
                    var action = cls == "CQuestEncounterActivator" ? (o["deactivateEncounter"] is true ? MerchantQuestAction.EncounterOff : MerchantQuestAction.EncounterOn)
                        : MerchantQuestAction.EncounterPhase;
                    byTag.GetOrAdd(tag, _ => []).Add(new MerchantQuestLink(quest, action, o["encounterSpawnPhase"] as string ?? ""));
                }
            }
        });

        // the quests' titles, by their codes, from the journal
        progress?.Report("journal");
        var titles = new ConcurrentDictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(locator.AllPaths(".journal").ToList(), options, path =>
        {
            Cr2wFile file;
            try { file = Cr2wFile.Read(locator.ReadFile(path) ?? []); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
            // a quest's code is the first word of its name in the journal ("mq7010 Airborne Cattle")
            for (int i = 0; i < file.Exports.Count; i++)
                if (file.Exports[i].ClassName == "CJournalQuest" && file.Object(i) is var quest && QuestCode(quest["baseName"] as string ?? "") is { } code
                    && quest["title"] switch { uint u => u, int n => (uint)n, _ => 0u } is var id and not 0)
                    titles.TryAdd(code, id);
        });

        static IReadOnlyDictionary<string, IReadOnlyList<MerchantQuestLink>> Frozen(ConcurrentDictionary<string, ConcurrentBag<MerchantQuestLink>> d) =>
            d.ToDictionary(p => p.Key, p => (IReadOnlyList<MerchantQuestLink>)[.. p.Value.Distinct().OrderBy(l => l.QuestFile, StringComparer.OrdinalIgnoreCase)], StringComparer.OrdinalIgnoreCase);

        return new MerchantIndex
        {
            Merchants = merchants.Where(m => m.Value is not null).ToDictionary(m => m.Key, m => m.Value!, StringComparer.OrdinalIgnoreCase),
            Places = [.. places.OrderBy(p => p.World).ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase)],
            Worlds = worlds,
            ByCommunity = Frozen(byCommunity),
            ByEncounterTag = Frozen(byTag),
            QuestTitles = new Dictionary<string, uint>(titles, StringComparer.OrdinalIgnoreCase),
        };
    }

    // ---------------------------------------------------------------- templates

    /// <summary>
    /// A template's merchant data, or null when it isn't a merchant: one with a merchant or craftsman component, of the
    /// merchant class, or with a shop's loot table. A cooked template keeps its components (its includes' too) in its
    /// flat data.
    /// </summary>
    private static Merchant? ReadMerchant(ResourceLocator locator, string template, Func<string, LootSpot?> loot)
    {
        Cr2wFile file;
        try { file = Cr2wFile.Read(locator.ReadFile(template) ?? []); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
        if (file.First("CEntityTemplate") is not { } t) return null;
        string entityClass = t["entityClass"] as string ?? "";
        Cr2wFile? flat = null;
        if (t.Bytes("flatCompiledData") is { Length: > 0 } raw)
            try { flat = Cr2wFile.Read(raw); } catch (InvalidDataException) { }
        var exports = flat?.Exports ?? [];
        bool merchantPart = exports.Any(e => e.ClassName is "W3MerchantComponent" or "W3CraftsmanComponent");
        var stock = loot(template);
        bool store = stock?.Params.Any(p => p.Tables.Any(x => x.Table.Contains("_store", StringComparison.OrdinalIgnoreCase))) == true;
        if (!merchantPart && entityClass != "W3MerchantNPC" && !store) return null;
        bool sells = stock?.Params.Any(p => p.Tables.Count > 0) == true;

        string pin = "", funds = "EInventoryFunds_Avg", scene = "";
        float priceMult = 1;
        uint name = 0;
        var crafts = new List<MerchantCraft>();
        var inventoryTags = new List<string>();
        var tags = new List<string>();
        if (flat is not null)
            for (int i = 0; i < flat.Exports.Count; i++)
            {
                string cls = flat.Exports[i].ClassName;
                if (i == 0 || cls is "W3MerchantComponent" or "W3CraftsmanComponent" or "CInventoryComponent")
                {
                    var o = flat.Object(i);
                    if (i == 0)
                    {
                        name = (uint)o.Integer("displayName");
                        if (o["tags"] is byte[] rt) tags.AddRange(flat.Tags(rt));
                        scene = o.Array("embeddedScenes").OfType<RedObject>().Select(s => s["storyScene"] is RedHandle sh ? flat.PathOf(sh) : null).FirstOrDefault(s => s is not null) ?? "";
                    }
                    if (cls is "W3MerchantComponent" or "W3CraftsmanComponent" && pin.Length == 0) pin = (o["mapPinType"] as RedEnum)?.ToString() ?? "";
                    if (cls == "W3CraftsmanComponent")
                        crafts.AddRange(o.Array("craftsmanData").OfType<RedObject>().Select(c => new MerchantCraft((c["type"] as RedEnum)?.ToString() ?? "", (c["level"] as RedEnum)?.ToString() ?? "")));
                    if (cls == "CInventoryComponent")
                    {
                        if (o["fundsType"] is RedEnum f) funds = f.ToString();
                        priceMult = o.Float("priceMult", 1);
                        if (o["tags"] is byte[] it) inventoryTags.AddRange(flat.Tags(it));
                    }
                }
            }
        // one of the merchant class that sells nothing and crafts nothing (a courtesan, a moneylender) isn't a shop
        if (!sells && pin.Length == 0 && crafts.Count == 0) return null;
        return new Merchant(template, entityClass, name, pin.Replace("EMMPT_", ""), crafts, funds.Replace("EInventoryFunds_", ""), priceMult, inventoryTags, tags,
            stock, scene.Length > 0 ? scene : null);
    }

    // ---------------------------------------------------------------- communities

    private sealed record TimetableAction(string World, string Layer, string Category, IReadOnlyList<string> ApTags, int? From, int? To);

    /// <summary>A community's timetables by name: for each time of day, the layer's action points (by tag) its people go to and what they do there, until the next time.</summary>
    private static Dictionary<string, List<TimetableAction>> ReadTimetables(Cr2wFile file, RedObject community)
    {
        var result = new Dictionary<string, List<TimetableAction>>(StringComparer.OrdinalIgnoreCase);
        foreach (var timetable in community.Array("storyPhaseTimetable").OfType<RedObject>())
        {
            if (timetable["name"] is not string name) continue;
            var times = timetable.Array("actionCategies").OfType<RedObject>().ToList();
            var actions = new List<TimetableAction>();
            for (int i = 0; i < times.Count; i++)
            {
                int from = Seconds(times[i]["time"]);
                int to = times.Count > 1 ? Seconds(times[(i + 1) % times.Count]["time"]) : from;
                int? start = from == to ? null : from, end = from == to ? null : to;
                foreach (var action in times[i].Array("actions").OfType<RedObject>())
                {
                    // a layer is named as "its path;its world's path" (without extensions)
                    string layer = action.Struct("layerName")?["layerName"] as string ?? "";
                    int split = layer.IndexOf(';');
                    string world = split >= 0 ? layer[(split + 1)..] + ".w2w" : "";
                    string layerPath = split > 0 ? Path.Combine(Path.GetDirectoryName(world) ?? "", layer[..split] + ".w2l") : "";
                    foreach (var category in action.Array("actionCategories").OfType<RedObject>())
                        actions.Add(new TimetableAction(world, layerPath, category["name"] as string ?? "", TagsOf(file, category["apTags"]).ToList(), start, end));
                }
            }
            result[name] = actions;
        }
        return result;
    }

    private static int Seconds(object? time) => time is RedObject t ? (int)(t.Integer("m_seconds") % 86400) : 0;

    /// <summary>A tag list's tags, kept as its bytes or (a story phase's name) in a struct holding them.</summary>
    private static IEnumerable<string> TagsOf(Cr2wFile file, object? value) => value switch
    {
        byte[] raw => file.Tags(raw),
        RedObject holder => TagsOf(file, holder["tags"]),
        _ => [],
    };

    // ---------------------------------------------------------------- encounters' spawn trees

    /// <summary>What the branches above an entry say: the quest phases it's in, the creature groups and other conditions it needs, when (seconds of the day) and its spawner's spawn points.</summary>
    private sealed record Context(List<string> Phases, List<string> Groups, List<MerchantCondition> Conditions, int? From, int? To, List<string> SpawnPoints);

    /// <summary>An entry that spawns the merchant: the context above it, its action points (category, tags) and its own spawn points.</summary>
    private sealed record Leaf(Context Context, List<(string Category, List<string> ApTags)> ActionPoints, List<string> SpawnPoints);

    /// <summary>Walks an encounter's spawn tree from a node, gathering the entries that spawn a creature definition and what the branches above them say.</summary>
    private static void Walk(Cr2wFile file, RedObject node, Context context, string definition, List<Leaf> leaves, HashSet<RedObject> seen, Func<string, Cr2wFile?> read)
    {
        if (!seen.Add(node)) return;
        var here = context;
        switch (node.ClassName)
        {
            case "CSpawnTreeIncludeTreeNode":
                // a tree kept in a .spawntree file of its own, walked as if it stood here
                if (node["spawnTree"] is RedHandle include && file.PathOf(include) is { } path && read(path) is { Exports.Count: > 0 } included)
                    Walk(included, included.Object(0), here, definition, leaves, seen, read);
                return;
            case "CSpawnTreeQuestPhase":
                here = here with { Phases = [.. here.Phases, node["tag"] as string ?? ""] };
                break;
            case "CSpawnTreeTimetableEntry":
                int begin = Seconds(node["begin"]), end = Seconds(node["end"]);
                here = begin == end ? here : here with { From = begin, To = end };
                break;
            case "CSpawnTreeConditionNode":
                foreach (var c in node.Array("conditions").OfType<RedHandle>().Select(file.Object).OfType<RedObject>())
                    if (c.ClassName == "W3CreaturesGroupEnabledCondition") here = here with { Groups = [.. here.Groups, c["groupName"] as string ?? ""] };
                    else here = here with { Conditions = [.. here.Conditions, ReadCondition(file, c)] };
                break;
        }
        if (node.Struct("baseSpawner") is { } spawner)
            here = here with { SpawnPoints = [.. here.SpawnPoints, .. TagsOf(file, spawner["tags"])] };

        if (node["creatureDefinition"] is string named)
        {
            if (named.Equals(definition, StringComparison.OrdinalIgnoreCase))
            {
                var points = new List<(string, List<string>)>();
                var spawns = new List<string>();
                Gather(file, node, points, spawns, []);
                leaves.Add(new Leaf(here, points, spawns));
            }
            return;
        }
        // the nodes below: a generator's entries are only its own bookkeeping, its spawning is done by its child nodes
        foreach (var (name, _, value) in node.Properties)
        {
            if (name is "conditions" or "initializers") continue;
            foreach (var child in Children(value))
                if (child is RedHandle h && file.Object(h) is { } o) Walk(file, o, here, definition, leaves, seen, read);
        }
    }

    private static readonly string[] Priority = ["work", "guarding", "dining", "household", "resting", "sleeping"];

    /// <summary>The action point selectors (their categories and tags) and spawn point tags under an entry.</summary>
    private static void Gather(Cr2wFile file, RedObject o, List<(string, List<string>)> points, List<string> spawns, HashSet<RedObject> seen)
    {
        if (!seen.Add(o)) return;
        if (o["apTags"] is { } ap && TagsOf(file, ap).ToList() is { Count: > 0 } apTags)
        {
            var categories = o.Array("categories").OfType<string>().ToList();
            // a selector lists every category it may use; the most telling one stands for the spot (working before eating before resting)
            points.Add((Priority.Select(p => categories.FirstOrDefault(c => c.StartsWith(p, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c is not null)
                ?? categories.FirstOrDefault() ?? "", apTags));
        }
        if (o.Struct("baseSpawner") is { } spawner) spawns.AddRange(TagsOf(file, spawner["tags"]));
        foreach (var (_, _, value) in o.Properties)
            foreach (var child in Children(value))
                if (child is RedHandle h && file.Object(h) is { } c) Gather(file, c, points, spawns, seen);
                else if (child is RedObject s) Gather(file, s, points, spawns, seen);
    }

    private static string TagsText(Cr2wFile file, byte[] raw)
    {
        try { return string.Join(" ", file.Tags(raw)); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return ""; }
    }

    private static IEnumerable<object?> Children(object? value) => value switch
    {
        List<object?> list => list,
        RedHandle or RedObject => [value],
        _ => [],
    };

    /// <summary>A spawn condition: a fact test (its fact, comparison and number), else its class and short values.</summary>
    private static MerchantCondition ReadCondition(Cr2wFile file, RedObject c)
    {
        // a fact test leaves out a comparison of Equal and a number of 0, the defaults
        string? fact = c.Properties.Select(p => p.Value).OfType<string>().FirstOrDefault(v => v.Length > 0 && v != "None");
        string op = c.Properties.Select(p => p.Value).OfType<RedEnum>().FirstOrDefault()?.ToString() ?? "EO_Equal";
        if (fact is not null && c.ClassName.Contains("Fact", StringComparison.Ordinal))
        {
            long value = c.Properties.Where(p => p.Name is not ("id" or "guid")).Select(p => p.Value).FirstOrDefault(v => v is int or uint or long or short or byte) is { } n ? Convert.ToInt64(n) : 0;
            return new MerchantCondition(fact, op[(op.IndexOf('_') + 1)..], value, "");
        }
        return new MerchantCondition("", "", 0, Describe(file, c));
    }

    /// <summary>A spawn condition as its class and its short values.</summary>
    private static string Describe(Cr2wFile file, RedObject c)
    {
        var values = c.Properties.Where(p => p.Name is not ("guid" or "id")).Select(p => p.Value is byte[] b ? TagsText(file, b) : Trees.ObjectTree.Short(p.Value))
            .Where(v => !string.IsNullOrEmpty(v));
        return $"{c.ClassName} {string.Join(" ", values)}".Trim();
    }
}
