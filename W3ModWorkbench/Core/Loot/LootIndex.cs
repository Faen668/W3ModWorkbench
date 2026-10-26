using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.World;

namespace W3ModWorkbench.Core.Loot;

/// <summary>A loot table a param can use: its name, its weight (null for the engine's default, 100) and its respawn time (null or 0: the table's own).</summary>
public sealed record LootTableRef(string Table, float? Chance, uint? Respawn)
{
    /// <summary>The weight the engine rolls with: the param's own, else 100.</summary>
    public float Weight => Chance ?? 100f;
}

/// <summary>
/// A CR4LootParam of an entity template: the tables it can use and how many of them (usedContainersMin to Max, both 1
/// when not set), and whether it's always used when the entity limits its params.
/// </summary>
public sealed record LootParamInfo(string Name, IReadOnlyList<LootTableRef> Tables, uint UsedMin, uint UsedMax, bool AlwaysPresent);

/// <summary>An item a template always holds, from its CInventoryDefinition: a named item, or a random one of a category.</summary>
public sealed record LootFixedItem(string Item, string Category, uint Min, uint Max, float? Probability, bool Random, bool Lootable);

/// <summary>What kind of place loot is in, for filtering: a monster's is an encounter that spawns it, its drops rolled at each kill.</summary>
public enum LootKind { Container, Herb, Beehive, Other, Monster }

/// <summary>
/// One placed entity that holds loot: where it is, the template it's made from, its loot params (the tables it rolls)
/// and the items its template always holds, plus the entity's own limit on how many params it uses (-1 for none).
/// </summary>
public sealed record LootSpot(string World, string Layer, string ClassName, string Name, Vector3 Position, string Template,
    IReadOnlyList<LootParamInfo> Params, IReadOnlyList<LootFixedItem> Fixed, int MinParams, int MaxParams)
{
    public LootKind Kind => ClassName switch
    {
        "W3Herb" => LootKind.Herb,
        "CEncounter" => LootKind.Monster,
        "CBeehiveEntity" or "W3BeehiveStandingEntity" => LootKind.Beehive,
        _ when ClassName.Contains("Container", StringComparison.OrdinalIgnoreCase) || ClassName.Contains("Stash", StringComparison.OrdinalIgnoreCase) => LootKind.Container,
        _ => LootKind.Other,
    };

    /// <summary>The template's file name, which names the kind of container (sack, old chest, wall shelf…).</summary>
    public string Title => Name.Length > 0 ? Name : Path.GetFileNameWithoutExtension(Template);

    /// <summary>The same loot whatever the place: the params, the fixed items and the limits.</summary>
    public string SetupKey { get; } = string.Join("|", Params.Select(p => $"{p.Name}:{p.UsedMin}-{p.UsedMax}:{p.AlwaysPresent}:" +
        string.Join(",", p.Tables.Select(t => $"{t.Table}*{t.Weight}")))) + $"#{MinParams},{MaxParams}";
}

/// <summary>
/// Every placed entity in the game's worlds that holds loot, read from the worlds' layers. A layer entity's loot is
/// its template's CR4LootParams (cooked templates keep their includes' params already merged), else those of the
/// template its inventory component names (an NPC's or a corpse's loot lives there); a template may also hold fixed
/// items. Verified on 64 places against REDkit's uncooked templates and layers: the same tables and positions.
/// </summary>
public sealed class LootIndex
{
    public required IReadOnlyList<LootSpot> Spots { get; init; }

    /// <summary>The worlds read, by path, in the order they were found.</summary>
    public required IReadOnlyList<string> Worlds { get; init; }

    /// <summary>Layers and templates that couldn't be read, by path, with why.</summary>
    public required IReadOnlyList<string> Problems { get; init; }

    private sealed record Setup(List<LootParamInfo> Params, List<LootFixedItem> Fixed);

    /// <summary>
    /// A reader of templates' loot, its own cache shared by every call: a template's loot params and fixed items as a
    /// <see cref="LootSpot"/> standing nowhere (for a monster's drops), or null for one with no loot.
    /// </summary>
    public static Func<string, LootSpot?> TemplateLoot(ResourceLocator locator)
    {
        var cache = new ConcurrentDictionary<string, Setup?>(StringComparer.OrdinalIgnoreCase);
        var problems = new ConcurrentBag<string>();
        return template => Resolve(locator, template, cache, problems, 0) is { } setup
            ? new LootSpot("", "", "", "", default, template, setup.Params, setup.Fixed, -1, -1) : null;
    }

    /// <summary>Reads every world's layers (REDkit's test world left out), a few seconds for the whole game.</summary>
    public static LootIndex Build(ResourceLocator locator, IProgress<string>? progress, CancellationToken token)
    {
        var templates = new ConcurrentDictionary<string, Setup?>(StringComparer.OrdinalIgnoreCase);
        var ints = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var problems = new ConcurrentBag<string>();
        var spots = new List<LootSpot>();
        var worlds = locator.AllPaths(".w2w").Where(w => !w.StartsWith(@"engine\", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string worldPath in worlds)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(worldPath);
            WorldFile world;
            try { world = WorldFile.Read(locator.ReadFile(worldPath) ?? throw new FileNotFoundException(worldPath), worldPath); }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                problems.Add($"{worldPath}: {ex.Message}");
                continue;
            }
            if (world.EmbeddedLayers is not { } tree) continue;
            var found = new ConcurrentBag<LootSpot>();
            Parallel.ForEach(tree.AllLayers(), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = token }, layer =>
            {
                Cr2wFile file;
                try { file = Cr2wFile.Read(locator.ReadFile(layer.Path) ?? throw new FileNotFoundException(layer.Path)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    problems.Add($"{layer.Path}: {ex.Message}");
                    return;
                }
                if (file.First("CLayer") is not { } cl) return;
                foreach (var h in cl.Array("entities").OfType<RedHandle>())
                {
                    if (file.Object(h) is not { } e || e["template"] is not RedHandle th || file.PathOf(th) is not { } template) continue;
                    if (Resolve(locator, template, templates, problems, 0) is not { } setup) continue;
                    // fixed items alone count only in a container: on an NPC's work spot they're the props it holds
                    if (setup.Params.Count == 0 && !e.ClassName.Contains("Container", StringComparison.OrdinalIgnoreCase)
                        && !e.ClassName.Contains("Stash", StringComparison.OrdinalIgnoreCase)) continue;
                    var (position, _) = Scenes.SceneStaging.Transform(e["transform"]);
                    found.Add(new LootSpot(worldPath, layer.Path, e.ClassName, e["name"] as string ?? "", position, template, setup.Params, setup.Fixed,
                        IntOf(e, "minLootParamNumber") ?? TemplateInt(locator, template, "minLootParamNumber", ints),
                        IntOf(e, "maxLootParamNumber") ?? TemplateInt(locator, template, "maxLootParamNumber", ints)));
                }
            });
            spots.AddRange(found.OrderBy(s => s.Layer, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Position.X).ThenBy(s => s.Position.Y));
        }
        return new LootIndex { Spots = spots, Worlds = worlds, Problems = [.. problems.Distinct().Order(StringComparer.OrdinalIgnoreCase)] };
    }

    private static int? IntOf(RedObject o, string name) => o[name] is null ? null : (int)o.Integer(name);

    /// <summary>An int the template's entity object sets (minLootParamNumber…), else -1, the script's default.</summary>
    private static int TemplateInt(ResourceLocator locator, string template, string name, ConcurrentDictionary<string, int> cache) =>
        cache.GetOrAdd(template + "|" + name, _ =>
        {
            try
            {
                var file = Cr2wFile.Read(locator.ReadFile(template) ?? []);
                return file.First("CEntityTemplate")?["entityObject"] is RedHandle h && file.Object(h) is { } entity ? IntOf(entity, name) ?? -1 : -1;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return -1; }
        });

    /// <summary>A template's loot: its own params and fixed items, else its includes', else its inventory's container template's.</summary>
    private static Setup? Resolve(ResourceLocator locator, string path, ConcurrentDictionary<string, Setup?> cache, ConcurrentBag<string> problems, int depth)
    {
        if (depth > 6) return null;
        if (cache.TryGetValue(path, out var known)) return known;
        Setup? result = null;
        try
        {
            if (locator.ReadFile(path) is { } data && Cr2wFile.Read(data) is var file && file.First("CEntityTemplate") is { } template)
            {
                var own = ParamsOf(file, template);
                var fixedItems = FixedOf(file, template);
                if (own.Count == 0)
                    foreach (var inc in template.Array("includes").OfType<RedHandle>())
                        if (file.PathOf(inc) is { } included && Resolve(locator, included, cache, problems, depth + 1) is { } got) own.AddRange(got.Params);
                if (own.Count > 0 || fixedItems.Count > 0) result = new Setup(own, fixedItems);
                else
                    foreach (string inner in ContainerTemplates(file, template))
                        if (Resolve(locator, inner, cache, problems, depth + 1) is { } via)
                        {
                            result = via;
                            break;
                        }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            problems.Add($"{path}: {ex.Message}");
        }
        cache[path] = result;
        return result;
    }

    private static List<LootParamInfo> ParamsOf(Cr2wFile file, RedObject template)
    {
        var list = new List<LootParamInfo>();
        foreach (var h in template.Array("templateParams").OfType<RedHandle>())
            if (file.Object(h) is { ClassName: "CR4LootParam" } p)
                list.Add(new LootParamInfo(p["name"] as string ?? "",
                    [.. p.Array("containers").OfType<RedObject>().Select(c => new LootTableRef(c["name"]?.ToString() ?? "",
                        c["chance"] is null ? null : c.Float("chance"), c["respawnTime"] is null ? null : (uint)c.Integer("respawnTime")))],
                    (uint)p.Integer("usedContainersMin", 1), (uint)p.Integer("usedContainersMax", 1), p["alwaysPresent"] is true));
        return list;
    }

    private static List<LootFixedItem> FixedOf(Cr2wFile file, RedObject template)
    {
        var list = new List<LootFixedItem>();
        foreach (var h in template.Array("templateParams").OfType<RedHandle>())
            if (file.Object(h) is { ClassName: "CInventoryDefinition" } definition)
                foreach (var eh in definition.Array("entries").OfType<RedHandle>())
                    if (file.Object(eh) is { } e && e["isMount"] is not true)
                    {
                        var init = e["initializer"] is RedHandle ih ? file.Object(ih) : null;
                        list.Add(new LootFixedItem(init?["itemName"]?.ToString() ?? "", e["category"]?.ToString() ?? "", (uint)e.Integer("quantityMin", 1),
                            (uint)e.Integer("quantityMax", 1), e["probability"] is null ? null : e.Float("probability"),
                            init?.ClassName == "CInventoryInitializerRandom", e["isLootable"] is not false));
                    }
        return list;
    }

    private static IEnumerable<string> ContainerTemplates(Cr2wFile file, RedObject template)
    {
        for (int i = 0; i < file.Exports.Count; i++)
            if (file.Exports[i].ClassName == "CInventoryComponent" && file.Object(i)["containerTemplate"] is RedHandle h && file.PathOf(h) is { } path) yield return path;
        if (template.Bytes("flatCompiledData") is not { Length: > 0 } flat) yield break;
        Cr2wFile? inner = null;
        try { inner = Cr2wFile.Read(flat); }
        catch (InvalidDataException) { }
        if (inner is null) yield break;
        for (int i = 0; i < inner.Exports.Count; i++)
            if (inner.Exports[i].ClassName == "CInventoryComponent" && inner.Object(i)["containerTemplate"] is RedHandle h && inner.PathOf(h) is { } path) yield return path;
    }
}
