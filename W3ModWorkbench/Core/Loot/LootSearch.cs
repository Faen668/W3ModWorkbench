using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Loot;

/// <summary>
/// A place that can give what was looked for: how likely at the level asked for, whether the game's roll for that exact
/// spot gives it (null when nothing is fixed there: no seed, a monster's drops, or not asked for), and its respawn time.
/// </summary>
public sealed record LootPlace(LootSpot Spot, double Chance, bool? RolledHere, uint? Respawn)
{
    /// <summary>Whether the place can give it at that level at all: some chance, or its own roll gives it.</summary>
    public bool Possible => Chance > 0 || RolledHere == true;
}

/// <summary>
/// Where items and loot tables can be found, worked out from a <see cref="LootIndex"/> and the game's roll: the places
/// grouped by setup (the same loot whatever the place), which setups can give each item and use each table, and each
/// place's chance, own roll and respawn. The Loot Locator window and the --loot-find command share it.
/// </summary>
public sealed class LootSearch
{
    /// <summary>Places with the same loot, the tables they can use and the items those (and their fixed items) can give.</summary>
    public sealed record Setup(List<LootSpot> Spots, HashSet<string> Tables, HashSet<string> Items);

    public LootIndex Index { get; }
    public LootRoll Roll { get; }
    public Dictionary<string, Setup> Setups { get; }
    public Dictionary<string, List<string>> ItemSetups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> TableSetups { get; } = new(StringComparer.OrdinalIgnoreCase);

    public LootSearch(LootIndex index, LootRoll roll)
    {
        Index = index;
        Roll = roll;
        Setups = index.Spots.GroupBy(s => s.SetupKey).ToDictionary(g => g.Key, g =>
        {
            var tables = g.First().Params.SelectMany(p => p.Tables.Select(t => t.Table)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = tables.SelectMany(t => roll.Tables.TryGetValue(t, out var def) ? def.Items.Select(i => i.Name) : [])
                .Concat(g.First().Fixed.Where(f => f.Lootable && f.Item.Length > 0).Select(f => f.Item)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new Setup([.. g], tables, items);
        });
        foreach (var (key, setup) in Setups)
        {
            foreach (string item in setup.Items) (ItemSetups.TryGetValue(item, out var list) ? list : ItemSetups[item] = []).Add(key);
            foreach (string table in setup.Tables) (TableSetups.TryGetValue(table, out var list) ? list : TableSetups[table] = []).Add(key);
        }
    }

    /// <summary>
    /// The places with the monsters added: every encounter that spawns a creature with drops is a place of its own, its
    /// loot the creature's, rolled again at each kill.
    /// </summary>
    public static LootIndex WithMonsters(LootIndex index, ResourceLocator locator, string game, IProgress<string>? progress)
    {
        var bestiary = Bestiary.BestiaryIndex.Build(locator, Scripts.ScriptCorpus.GameScriptsFolder(game), progress, CancellationToken.None);
        var monsters = bestiary.Places.Where(p => bestiary.Creatures.TryGetValue(p.Template, out var c) && c.Loot is { Params.Count: > 0 })
            .Select(p => bestiary.Creatures[p.Template].Loot! with { World = p.World, Layer = p.Layer, ClassName = "CEncounter", Name = "", Position = p.Position });
        return new LootIndex { Spots = [.. index.Spots, .. monsters], Worlds = index.Worlds, Problems = index.Problems };
    }

    /// <summary>Every place whose setup can give an item or use a table, whatever the level.</summary>
    public IEnumerable<LootSpot> SpotsFor(string name, bool table) =>
        ((table ? TableSetups : ItemSetups).TryGetValue(name, out var keys) ? keys : []).SelectMany(key => Setups[key].Spots);

    /// <summary>How many places can give an item or use a table, whatever the level.</summary>
    public int CountFor(string name, bool table) =>
        (table ? TableSetups : ItemSetups).TryGetValue(name, out var keys) ? keys.Sum(k => Setups[k].Spots.Count) : 0;

    /// <summary>
    /// A place's chance of an item or a table at a level, what its own roll gives (with <paramref name="rolled"/>), and its
    /// respawn: the game's own for the spot when its roll uses a table, else the param's or table's.
    /// </summary>
    public LootPlace Place(LootSpot s, string name, bool table, uint level, bool rolled)
    {
        var odds = Roll.Odds(s, level);
        double chance = table ? odds.Tables.GetValueOrDefault(name) : odds.Items.GetValueOrDefault(name);
        // what the game's roll gives this spot: its respawn time is the game's own for it (see LootRoll.Roll)
        var given = Roll.Roll(s, level, LootRoll.Seed(s));
        var hit = given.FirstOrDefault(i => table ? i.Table.Equals(name, StringComparison.OrdinalIgnoreCase) : i.Item.Equals(name, StringComparison.OrdinalIgnoreCase));
        // a seed of 0 is no seed: the game rolls that spot at random each time, so nothing is fixed there
        // a monster's drops are rolled at each kill, where it falls: nothing is fixed at its encounter
        bool? here = rolled && s.Kind != LootKind.Monster && LootRoll.Seed(s) != 0 ? hit is not null : null;
        return new LootPlace(s, chance, here, s.Kind == LootKind.Monster ? null : hit is { Table.Length: > 0 } ? hit.Respawn : RespawnOf(s, name, table));
    }

    /// <summary>Each place's <see cref="Place"/>, in the order given, worked out in parallel (each setup's odds once).</summary>
    public List<LootPlace> Places(IReadOnlyList<LootSpot> spots, string name, bool table, uint level, bool rolled)
    {
        Parallel.ForEach(spots.DistinctBy(s => s.SetupKey), s => Roll.Odds(s, level));
        return spots.AsParallel().AsOrdered().Select(s => Place(s, name, table, level, rolled)).ToList();
    }

    /// <summary>The respawn time of what was looked for at a place: the param's own when set, else its table's (the shortest if several give it).</summary>
    public uint? RespawnOf(LootSpot spot, string name, bool table)
    {
        uint? best = null;
        foreach (var t in TablesAt(spot, name, table))
        {
            var def = Roll.Tables[t.Table];
            uint respawn = t.Respawn is { } own && own != 0 ? own : def.Respawn;
            if (best is null || respawn < best) best = respawn;
        }
        return best;
    }

    /// <summary>The tables a place can use that give an item, or the table itself; only tables the game defines.</summary>
    public IEnumerable<LootTableRef> TablesAt(LootSpot spot, string name, bool table)
    {
        foreach (var p in spot.Params)
            foreach (var t in p.Tables)
            {
                if (!Roll.Tables.TryGetValue(t.Table, out var def)) continue;
                if (table ? t.Table.Equals(name, StringComparison.OrdinalIgnoreCase) : def.Items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    yield return t;
            }
    }

    /// <summary>
    /// The tables that give an item (or the table itself) that no place uses: a shop's, a reward's, one a script or quest
    /// hands out. These are what the window's "Show tables no place uses" adds to its list.
    /// </summary>
    public IEnumerable<LootTableDef> UnusedTables(string name, bool table) =>
        Roll.Tables.Values.Where(t => (table ? t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) : t.Items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            && CountFor(t.Name, true) == 0).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The lowest player level an item can be found at: the lowest of its tables' and its own level limits where a place can give it.</summary>
    public uint? LowestLevel(string item)
    {
        if (!ItemSetups.TryGetValue(item, out var keys)) return null;
        uint? lowest = null;
        foreach (string table in keys.SelectMany(k => Setups[k].Tables).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Roll.Tables.TryGetValue(table, out var def)) continue;
            foreach (var i in def.Items.Where(i => i.Name.Equals(item, StringComparison.OrdinalIgnoreCase) && (i.Chance == -1f || i.Chance > 0)))
            {
                uint at = Math.Max(1, Math.Max(def.LevelMin, i.LevelMin));
                if (lowest is null || at < lowest) lowest = at;
            }
        }
        return lowest;
    }
}
