using System.Globalization;
using System.IO;
using System.Text;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Items;
using W3ModWorkbench.Core.Loot;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench;

// loc-ignore-start
/// <summary>
/// The Loot Locator from the command line: where items and loot tables can be found, as CSV, worked out as the window
/// does (<see cref="LootSearch"/>). The data goes to standard output (or --out), what happened to the error output.
/// </summary>
public static partial class CommandLine
{
    private static readonly string[] LootCommands = ["--loot-find"];

    private sealed class LootFindOptions
    {
        public List<string> Names { get; } = [];
        public string? Game = GameLocator.Resolve(null);
        public string? Out, World;
        public bool Mods, VanillaOnly, RolledOnly;
        public uint Level = (uint)Math.Clamp(App.Settings.LootPlayerLevel, 1, LootLocatorWindow.MaxLevel);
    }

    private static LootFindOptions ReadLootFindOptions(string[] args)
    {
        var o = new LootFindOptions();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value after it.");
            switch (a.ToLowerInvariant())
            {
                case "--game": o.Game = Next(); break;
                case "--mods": o.Mods = true; break;
                case "--vanilla-only": o.VanillaOnly = true; break;
                case "--rolled-only": o.RolledOnly = true; break;
                case "--world": o.World = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--level":
                    o.Level = uint.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out uint level) && level is >= 1 and <= LootLocatorWindow.MaxLevel
                        ? level : throw new ArgumentException($"--level is the player level, 1 to {LootLocatorWindow.MaxLevel}.");
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{a} isn't an option of --loot-find. Run --help loot for its options.");
                    if (a.StartsWith('@'))
                    {
                        string list = a[1..];
                        if (!File.Exists(list)) throw new ArgumentException($"There's no file {Path.GetFullPath(list)} to read names from.");
                        o.Names.AddRange(File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
                    }
                    else o.Names.Add(a.Trim());
                    break;
            }
        }
        if (o.Names.Count == 0) throw new ArgumentException("--loot-find needs the names of items or loot tables, or @file with one a line. Run --help loot for its options.");
        if (string.IsNullOrEmpty(o.Game) || !Directory.Exists(o.Game))
            throw new ArgumentException("The game wasn't found: give its folder with --game, or set it in Settings.");
        return o;
    }

    /// <summary>
    /// --loot-find: reads the places once, then writes a row for each place that can give each name at the level, and
    /// a row for each table holding it that no place uses. 0 when it ran (names not found are said and get a row), 1 when it couldn't.
    /// </summary>
    private static int RunLootFind(string[] args, TextWriter report)
    {
        LootFindOptions o;
        try { o = ReadLootFindOptions(args); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            report.WriteLine(ex.Message);
            return 1;
        }
        string game = o.Game!;
        var started = System.Diagnostics.Stopwatch.StartNew();
        LootSearch search;
        ItemDatabase db;
        try
        {
            report.WriteLine($"Game: {game}{(o.Mods ? o.VanillaOnly ? "  (with installed mods, but no mods installed as DLC)" : "  (with installed mods)" : "")}");
            report.WriteLine("Reading item definitions and loot tables...");
            report.Flush();
            db = ItemDatabase.Load(game, null, withMods: o.Mods, modDlc: !o.VanillaOnly);
            var locator = new ResourceLocator([], null, game, otherMods: o.Mods, modDlc: !o.VanillaOnly);
            string last = "";
            bool monsters = false;
            var progress = new LineProgress<string>(what =>
            {
                // each world's layers, then where monsters spawn (the worlds again): one line each
                string line = monsters ? $"Reading where monsters spawn: {what}" : $"Reading the places in {what}";
                lock (report)
                {
                    if (line == last) return;
                    last = line;
                    report.WriteLine(line);
                    report.Flush();
                }
            });
            var placed = LootIndex.Build(locator, progress, CancellationToken.None);
            monsters = true;
            var index = LootSearch.WithMonsters(placed, locator, game, progress);
            search = new LootSearch(index, LootRoll.FromDatabase(db));
            report.WriteLine($"{index.Spots.Count:N0} places in {index.Worlds.Count} worlds, {search.Roll.Tables.Count:N0} loot tables, read in {started.Elapsed.TotalSeconds:0.0} s."
                + (index.Problems.Count > 0 ? $" {index.Problems.Count} files couldn't be read." : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            report.WriteLine($"The game's files couldn't be read: {ex.Message}");
            return 1;
        }

        var worlds = search.Index.Worlds;
        string? onlyWorld = null;
        if (o.World is { } wanted)
        {
            onlyWorld = worlds.FirstOrDefault(w => w.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(Path.GetDirectoryName(w) ?? "").Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(w).Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || LootLocatorWindow.WorldName(w).Equals(wanted, StringComparison.CurrentCultureIgnoreCase));
            if (onlyWorld is null)
            {
                report.WriteLine($"There's no world \"{wanted}\". The worlds are: {string.Join(", ", worlds.Select(w => Path.GetFileName(Path.GetDirectoryName(w))).Distinct())}.");
                return 1;
            }
        }

        // in-game names, read only when a name isn't a file's name
        Dictionary<string, List<string>>? byGameName = null;
        Dictionary<string, List<string>> GameNames()
        {
            if (byGameName is not null) return byGameName;
            report.WriteLine("Reading the items' names in the game...");
            report.Flush();
            byGameName = new(StringComparer.CurrentCultureIgnoreCase);
            foreach (var language in new[] { App.GameLanguage, W3Strings.Languages[0] }.DistinctBy(l => l.Handle))
            {
                var strings = GameStringIndex.Load(game, language, dlcAndMods: o.Mods);
                var names = strings.All.GroupBy(s => s.KeyHash).ToDictionary(g => g.Key, g => g.First().Text);
                foreach (var item in db.Items)
                    if (item.Attribute("localisation_key_name") is { Length: > 0 } key && names.TryGetValue(W3Strings.HashKey(key), out var text) && text.Trim().Length > 0)
                    {
                        var list = byGameName.TryGetValue(text.Trim(), out var known) ? known : byGameName[text.Trim()] = [];
                        if (!list.Contains(item.Name, StringComparer.OrdinalIgnoreCase)) list.Add(item.Name);
                    }
            }
            return byGameName;
        }

        var csv = new StringBuilder();
        csv.AppendLine("row,query,match,match_type,world,world_name,area,layer,kind,entity,template,x,y,z,chance,rolled_here,respawn,tables,table_file");
        static string Cell(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        static string Number(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
        // as a float, as the window's Export writes it: widened to a double first, the last digit can round the other way
        static string Coordinate(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        int placeRows = 0, unusedRows = 0, notFound = 0;
        foreach (string query in o.Names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // a file's name first: an item (one a place can give, the item files define or a table lists) and a table
            var matches = new List<(string Name, bool Table)>();
            if (search.ItemSetups.ContainsKey(query) || db.Find(DefKind.Item, query) is not null
                || search.Roll.Tables.Values.Any(t => t.Items.Any(i => i.Name.Equals(query, StringComparison.OrdinalIgnoreCase))))
                matches.Add((CanonicalItem(search, db, query), false));
            if (search.Roll.Tables.TryGetValue(query, out var def)) matches.Add((def.Name, true));
            else if (search.TableSetups.Keys.FirstOrDefault(t => t.Equals(query, StringComparison.OrdinalIgnoreCase)) is { } undefined) matches.Add((undefined, true));
            if (matches.Count == 0 && GameNames().TryGetValue(query, out var named))
                matches.AddRange(named.Select(n => (n, false)));
            if (matches.Count == 0)
            {
                report.WriteLine($"not found  \"{query}\": no item or loot table has that name.");
                csv.AppendLine(string.Join(",", "not found", Cell(query), "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", ""));
                notFound++;
                continue;
            }

            foreach (var (name, table) in matches)
            {
                var spots = search.SpotsFor(name, table).Where(s => onlyWorld is null || s.World.Equals(onlyWorld, StringComparison.OrdinalIgnoreCase)).ToList();
                var places = search.Places(spots, name, table, o.Level, rolled: true).Where(p => p.Possible && (!o.RolledOnly || p.RolledHere == true))
                    .OrderBy(p => p.Spot.World, StringComparer.OrdinalIgnoreCase).ThenByDescending(p => p.Chance)
                    .ThenBy(p => p.Spot.Layer, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Spot.Position.X).ThenBy(p => p.Spot.Position.Y).ToList();
                foreach (var p in places)
                {
                    var s = p.Spot;
                    string tables = string.Join(";", search.TablesAt(s, name, table).Select(t => t.Table).Distinct(StringComparer.OrdinalIgnoreCase));
                    if (!table && tables.Length == 0 && s.Fixed.Any(f => f.Lootable && f.Item.Equals(name, StringComparison.OrdinalIgnoreCase))) tables = "(always holds)";
                    csv.AppendLine(string.Join(",", "place", Cell(query), Cell(name), table ? "table" : "item",
                        Cell(Path.GetFileName(Path.GetDirectoryName(s.World)) ?? s.World), Cell(LootLocatorWindow.WorldName(s.World)), Cell(LootLocatorWindow.AreaOf(s)),
                        Cell(s.Layer), s.Kind.ToString().ToLowerInvariant(), Cell(s.EngineName), Cell(s.Template),
                        Coordinate(s.Position.X), Coordinate(s.Position.Y), Coordinate(s.Position.Z), Number(p.Chance, "0.####"),
                        p.RolledHere switch { true => "yes", false => "no", _ => "random" },
                        p.Respawn?.ToString(CultureInfo.InvariantCulture) ?? "", Cell(tables), ""));
                }
                placeRows += places.Count;
                int worldCount = places.Select(p => p.Spot.World).Distinct().Count();
                report.WriteLine($"{(table ? "table" : "item ")}  {name}: {places.Count:N0} {(places.Count == 1 ? "place" : "places")} at level {o.Level}"
                    + (places.Count > 0 ? $" in {worldCount} {(worldCount == 1 ? "world" : "worlds")}" : "")
                    + (o.RolledOnly ? " whose own roll gives it" : ""));

                // the tables holding it that no place uses: a shop's, a reward's, one a quest or script hands out
                foreach (var unused in search.UnusedTables(name, table))
                {
                    double chance = table ? 1 : search.Roll.TableOdds(unused, o.Level).Items.GetValueOrDefault(name);
                    csv.AppendLine(string.Join(",", "unused table", Cell(query), Cell(name), table ? "table" : "item", "", "", "", "", "", "", "", "", "", "",
                        Number(chance, "0.####"), "", unused.Respawn.ToString(CultureInfo.InvariantCulture), Cell(unused.Name), Cell(unused.GamePath)));
                    unusedRows++;
                    report.WriteLine($"         also in {unused.Name}, which no place uses ({unused.GamePath})");
                }
            }
        }

        try
        {
            string text = csv.ToString();
            if (o.Out is null or StandardStream)
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(new UTF8Encoding(false).GetBytes(text));
                stdout.Flush();
            }
            else
            {
                string file = Path.GetFullPath(o.Out);
                if (Directory.Exists(file)) throw new ArgumentException($"--out is the CSV file to write, and {file} is a folder.");
                Core.Projects.FileSafety.CheckWrite(file);
                File.WriteAllText(file, text, new UTF8Encoding(true));
                report.WriteLine($"ok      {file}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            report.WriteLine($"The CSV couldn't be written: {ex.Message}");
            return 1;
        }
        report.WriteLine($"{placeRows:N0} {(placeRows == 1 ? "place" : "places")}, {unusedRows:N0} {(unusedRows == 1 ? "table" : "tables")} no place uses, {notFound} {(notFound == 1 ? "name" : "names")} not found ({started.Elapsed.TotalSeconds:0.0} s).");
        return 0;
    }

    /// <summary>An item's name as the files write it, for a name given in any case.</summary>
    private static string CanonicalItem(LootSearch search, ItemDatabase db, string query) =>
        search.ItemSetups.Keys.FirstOrDefault(k => k.Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? db.Find(DefKind.Item, query)?.Name
        ?? search.Roll.Tables.Values.SelectMany(t => t.Items).FirstOrDefault(i => i.Name.Equals(query, StringComparison.OrdinalIgnoreCase))?.Name
        ?? query;

    private const string LootHelp = """
        Where items and loot tables can be found (the Loot Locator, without its window)
          --loot-find <names…>    Each place that can give each item or loot table, as CSV. A name is an item's or a
                                  table's name in the files (_unique_silversword_gynvael), or an item's name in the
                                  game (in the game language set in Settings, or English); @file reads one name a
                                  line (# starts a comment line)
              --game <folder>     The game's folder (Settings' when not given)
              --mods              Read installed mods too, as the window's "Installed mods" box does
              --vanilla-only      With --mods: leave out every folder in the game's dlc folder that isn't CD PROJEKT
                                  RED's own, such as dlc\dlcbrothersinarms; mods in the mods folder are still read.
                                  The game's own are ep1 (Hearts of Stone), bob (Blood and Wine), dlc1 to dlc16 (the
                                  free DLC), and every name in the game's dlc-tombstones folder. Without --mods, no
                                  mod is read at all, in either folder
              --level <n>         The player level the chances are for, 1 to 100 (the window's when not given)
              --world <name>      Only this world: its folder (novigrad, skellige, bob) or its name in the window
              --rolled-only       Only places whose own roll gives it (the window's "Rolled here")
              --out <file>        The CSV file to write; - or none writes it to standard output
            Columns: row, query, match, match_type (item or table), world (its folder), world_name, area, layer,
            kind (container, herb, beehive, monster, other), entity, template, x, y, z, chance (0 to 1, at the
            level), rolled_here (yes, no, or random where nothing is fixed: no seed, or a monster's drops), respawn,
            tables (those at the place that give it), table_file.
            A row is "place" for a place; "unused table" for a table holding it that no place uses (a shop's, a
            reward's: chance is the table's own when it's used, table_file its XML file); "not found" for a name
            nothing has. The places are read once however many names are given; how far it's got goes to the
            error output. The exit code is 0 when it ran and 1 when it couldn't, with the reason.
              W3ModWorkbench.exe --loot-find "Gynvael" _uniqe_weapons_epic_dungeon_skelige --level 30
              W3ModWorkbench.exe --loot-find @relics.txt --mods --vanilla-only --out relics.csv

        """;
}
// loc-ignore-end
