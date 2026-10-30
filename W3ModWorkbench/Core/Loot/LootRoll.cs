using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using W3ModWorkbench.Core.Items;

namespace W3ModWorkbench.Core.Loot;

/// <summary>An item a loot table can give: how many (a range), the player levels it's for (0 for no limit) and its chance (-1: picked first, evenly).</summary>
public sealed record LootItemDef(string Name, uint QuantityMin, uint QuantityMax, uint LevelMin, uint LevelMax, float Chance);

/// <summary>A loot table from the game's XML: how many of its items are picked (a range), its player levels, its respawn time and its items, in the file's order.</summary>
public sealed record LootTableDef(string Name, uint PicksMin, uint PicksMax, uint LevelMin, uint LevelMax, uint Respawn, string GamePath, List<LootItemDef> Items);

/// <summary>An item a roll gave: how many (the amount is rolled again each time the place fills), from which table, and the respawn time.</summary>
public sealed record RolledItem(string Item, uint QuantityMin, uint QuantityMax, string Table, uint Respawn);

/// <summary>How likely each item and table is at a place: the share of rolls that gave it.</summary>
public sealed record LootOdds(IReadOnlyDictionary<string, double> Items, IReadOnlyDictionary<string, double> Tables, int Rolls);

/// <summary>
/// The game's loot roll, re-created from witcher3.exe: CR4LootManager's generator (template params, then each param's
/// tables, then each table's items), with the engine's own defaults (a table weight of 100, one table a param), level
/// limits, its order of picking (chance -1 first and evenly, then by weight with no repeats), and its integer noise
/// seeded by where the place stands. Two things it can't know: the recipes and diagrams the player already has (left
/// out of a roll by the game) and a per-item cap the game keeps.
/// </summary>
public sealed class LootRoll
{
    /// <summary>How many times a setup is rolled to tell its odds.</summary>
    public const int OddsRolls = 3000;

    private readonly ConcurrentDictionary<(string, uint), LootOdds> _odds = new();
    private readonly ConcurrentDictionary<(string, uint), LootOdds> _tableOdds = new();

    public LootRoll(IReadOnlyDictionary<string, LootTableDef> tables) => Tables = tables;

    /// <summary>The loot tables by name, any case.</summary>
    public IReadOnlyDictionary<string, LootTableDef> Tables { get; }

    /// <summary>
    /// The game's loot tables (not New Game+'s), the items in a table in the order its file lists them. As the game reads
    /// them: an attribute left out keeps its default (quantities 1, levels 0, an item's chance 100), and a table or item
    /// whose quantities or player or crafter levels run backwards (a max of 0 being no max) is left out.
    /// </summary>
    public static LootRoll FromDatabase(ItemDatabase db)
    {
        static uint U(XElement e, string a, uint otherwise = 0) => e.Attribute(a)?.Value is { } v && long.TryParse(v, CultureInfo.InvariantCulture, out long l) ? unchecked((uint)l) : otherwise;
        static float F(XElement e, string a) => e.Attribute(a)?.Value is { } v && float.TryParse(v, CultureInfo.InvariantCulture, out float f) ? f : 100;
        static bool Readable(XElement e) => U(e, "quantity_min", 1) <= U(e, "quantity_max", 1)
            && (U(e, "player_level_max") == 0 || U(e, "player_level_min") <= U(e, "player_level_max"))
            && (U(e, "crafter_level_max") == 0 || U(e, "crafter_level_min") <= U(e, "crafter_level_max"));
        var map = new Dictionary<string, LootTableDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in db.All.Where(d => d.Kind == DefKind.Loot && !d.NewGamePlus && Readable(d.Element)))
        {
            var l = def.Element;
            var items = l.Elements("loot_entry").Where(Readable).Select(e => new LootItemDef(e.Attribute("name")?.Value ?? "", U(e, "quantity_min", 1),
                U(e, "quantity_max", 1), U(e, "player_level_min"), U(e, "player_level_max"), F(e, "chance"))).ToList();
            // a DLC adding to a shop's table under the same name
            if (map.TryGetValue(def.Name, out var had)) had.Items.AddRange(items);
            else map[def.Name] = new LootTableDef(def.Name, U(l, "quantity_min", 1), U(l, "quantity_max", 1), U(l, "player_level_min"), U(l, "player_level_max"),
                U(l, "respawn_time"), def.GamePath, items);
        }
        return new LootRoll(map);
    }

    // ---------------------------------------------------------------- the engine's random numbers

    /// <summary>The integer noise the engine rolls with when it has a seed: i counts the rolls, s is the seed.</summary>
    public static uint Hash(uint i, uint s)
    {
        uint x = unchecked(((i << 13) + (s << 13)) ^ (i + s));
        return unchecked((x * (x * x * 15731u + 789221u) - 771171059u) & 0x7fffffff);
    }

    /// <summary>
    /// A placed entity's seed: the sum of its name's characters (<see cref="LootSpot.EngineName"/>) and the floor of its x, its y and a tenth of its z. The
    /// same place always rolls the same items, except where this comes out 0: the engine takes 0 as no seed and rolls
    /// that place with its own random numbers each time.
    /// </summary>
    public static uint Seed(string name, System.Numerics.Vector3 at)
    {
        uint sum = 0;
        // the engine adds the name's bytes as signed chars
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name)) sum += (ushort)(sbyte)b;
        return unchecked(sum + (uint)((int)MathF.Floor(at.X) + (int)MathF.Floor(at.Y) + (int)MathF.Floor(at.Z * 0.1f)));
    }

    /// <summary>A placed loot spot's seed, from its name and where it stands.</summary>
    public static uint Seed(LootSpot spot) => Seed(spot.EngineName, spot.Position);

    private static uint Between(uint lo, uint hiExclusive, uint seed) => lo + Hash(0, seed) % (hiExclusive - lo);

    private static bool LevelOk(uint level, uint min, uint max) => level == 0 || (level >= min && (max == 0 || level <= max));

    // ---------------------------------------------------------------- one roll

    /// <summary>What a place gives at a player level, rolled with a seed (its own <see cref="Seed"/> for what the game gives it).</summary>
    public List<RolledItem> Roll(LootSpot spot, uint level, uint seed)
    {
        var chosen = spot.Params.ToList();
        if (spot.MinParams > -1 && spot.MaxParams > -1 && spot.MinParams <= spot.MaxParams)
        {
            // the engine's range here leaves out the max
            int n = spot.MinParams == spot.MaxParams ? spot.MinParams : (int)(spot.MinParams + Hash(0, seed) % (uint)(spot.MaxParams - spot.MinParams));
            var rest = chosen;
            chosen = [];
            for (int i = 0; i < rest.Count;)
                if (rest[i].AlwaysPresent)
                {
                    chosen.Add(rest[i]);
                    rest[i] = rest[^1];
                    rest.RemoveAt(rest.Count - 1);
                }
                else i++;
            n -= chosen.Count;
            uint r = seed;
            for (int picked = 0; picked < n && rest.Count > 0; picked++, r++)
            {
                int idx = (int)(Hash(0, r) % (uint)rest.Count);
                chosen.Add(rest[idx]);
                rest[idx] = rest[^1];
                rest.RemoveAt(rest.Count - 1);
            }
        }
        var output = new List<RolledItem>();
        foreach (var p in chosen) RollParam(p, seed, level, output);
        // the engine adds up the same item from different tables: the shorter respawn time when both respawn, else
        // the longer (a respawning one wins over one that never does)
        var merged = output.GroupBy(o => o.Item, StringComparer.OrdinalIgnoreCase).Select(g => g.Skip(1).Aggregate(g.First(), (a, b) => a with
        {
            QuantityMin = a.QuantityMin + b.QuantityMin,
            QuantityMax = a.QuantityMax + b.QuantityMax,
            Respawn = a.Respawn > 0 && b.Respawn > 0 ? Math.Min(a.Respawn, b.Respawn) : Math.Max(a.Respawn, b.Respawn),
        })).ToList();
        // what the template always holds is its inventory's own, not a loot roll's
        foreach (var f in spot.Fixed)
            if (f.Lootable && f.Item.Length > 0) merged.Add(new RolledItem(f.Item, f.Min, Math.Max(f.Min, f.Max), "", 0));
        return merged;
    }

    private void RollParam(LootParamInfo p, uint seed, uint level, List<RolledItem> output)
    {
        uint k = p.UsedMax > p.UsedMin ? Between(p.UsedMin, p.UsedMax + 1, seed) : p.UsedMin;
        var candidates = new List<(LootTableRef Ref, LootTableDef Def)>();
        foreach (var t in p.Tables)
            if (Tables.TryGetValue(t.Table, out var d)) candidates.Add((t, d));
        var tables = Pick(candidates, k, c => c.Ref.Weight, c => LevelOk(level, c.Def.LevelMin, c.Def.LevelMax), seed);
        for (int index = 0; index < tables.Count; index++)
        {
            var def = tables[index].Def;
            uint seed2 = seed == 0 ? 0 : unchecked((uint)index + seed);
            uint m = def.PicksMax > def.PicksMin && seed2 != 0 ? Between(def.PicksMin, def.PicksMax + 1, seed2) : def.PicksMin;
            // as the engine does it, the respawn time is the param's table at the same place in its own list, not the
            // table picked: its own time when it sets one, else that table's
            var (sameRef, sameDef) = candidates[index];
            uint respawn = sameRef.Respawn is { } own && own != 0 ? own : sameDef.Respawn;
            foreach (var i in Pick(def.Items, m, i => i.Chance, i => LevelOk(level, i.LevelMin, i.LevelMax), seed2))
                output.Add(new RolledItem(i.Name, i.QuantityMin, Math.Max(i.QuantityMin, i.QuantityMax), def.Name, respawn));
        }
    }

    /// <summary>
    /// The engine's picker for tables and items alike: a chance of 0 or less is left out, except exactly -1, and so is
    /// a level out of range. When everything fits it's all taken, the -1s first; otherwise the -1s first, picked evenly,
    /// then the rest by weight, none twice.
    /// </summary>
    private static List<T> Pick<T>(List<T> list, uint n, Func<T, float> chance, Func<T, bool> levelOk, uint seed)
    {
        var even = new List<T>();
        var weighted = new List<T>();
        float total = 0;
        foreach (var x in list)
        {
            float c = chance(x);
            if (c != -1f && c <= 0) continue;
            if (!levelOk(x)) continue;
            if (c >= 0)
            {
                weighted.Add(x);
                total += c;
            }
            else even.Add(x);
        }
        var output = new List<T>();
        if (n == 0) return output;
        if (even.Count + weighted.Count <= n)
        {
            output.AddRange(even);
            output.AddRange(weighted);
            return output;
        }
        if (even.Count > 0)
        {
            if (even.Count <= n) output.AddRange(even);
            else
                for (uint i = 0; output.Count < n; i++)
                {
                    int idx = (int)(Hash(i, seed) % (uint)even.Count);
                    output.Add(even[idx]);
                    even.RemoveAt(idx);
                }
        }
        for (uint i = 0; output.Count < n && weighted.Count > 0; i++)
        {
            float target = Hash(i, seed) * 4.65661287e-10f * total;
            int j = 0;
            float cum = chance(weighted[0]);
            while (j < weighted.Count - 1 && !(cum >= target)) cum += chance(weighted[++j]);
            output.Add(weighted[j]);
            total -= chance(weighted[j]);
            weighted.RemoveAt(j);
        }
        return output;
    }

    // ---------------------------------------------------------------- odds

    /// <summary>The share of rolls of a place's setup that give each item and use each table, at a player level.</summary>
    public LootOdds Odds(LootSpot spot, uint level) => _odds.GetOrAdd((spot.SetupKey, level), _ =>
    {
        var items = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // the engine's own noise from many seeds: a seed is a place, so this is the spread over places like it
        uint seed = 0x9E3779B9;
        for (int r = 0; r < OddsRolls; r++)
        {
            seed = unchecked(seed * 1664525u + 1013904223u) | 1;
            var rolled = Roll(spot, level, seed);
            foreach (var i in rolled) items[i.Item] = items.GetValueOrDefault(i.Item) + 1;
            foreach (string t in rolled.Select(i => i.Table).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) tables[t] = tables.GetValueOrDefault(t) + 1;
        }
        return new LootOdds(items.ToDictionary(p => p.Key, p => p.Value / (double)OddsRolls, StringComparer.OrdinalIgnoreCase),
            tables.ToDictionary(p => p.Key, p => p.Value / (double)OddsRolls, StringComparer.OrdinalIgnoreCase), OddsRolls);
    });

    /// <summary>The share of a table's rolls that give each of its items, at a player level: what it gives when it's used.</summary>
    public LootOdds TableOdds(LootTableDef table, uint level) => _tableOdds.GetOrAdd((table.Name, level), _ =>
    {
        var items = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        uint seed = 0x9E3779B9;
        for (int r = 0; r < OddsRolls; r++)
        {
            seed = unchecked(seed * 1664525u + 1013904223u) | 1;
            uint m = table.PicksMax > table.PicksMin ? Between(table.PicksMin, table.PicksMax + 1, seed) : table.PicksMin;
            foreach (string name in Pick(table.Items, m, i => i.Chance, i => LevelOk(level, i.LevelMin, i.LevelMax), seed).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                items[name] = items.GetValueOrDefault(name) + 1;
        }
        return new LootOdds(items.ToDictionary(p => p.Key, p => p.Value / (double)OddsRolls, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(), OddsRolls);
    });
}
