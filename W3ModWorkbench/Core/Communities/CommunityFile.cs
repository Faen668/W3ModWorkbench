using System.IO;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;

namespace W3ModWorkbench.Core.Communities;

/// <summary>
/// One row of a community's spawnset, as REDkit's community editor lays it out: an entry (who), one of its entities
/// (the template spawned, its appearances and tags) and one of its story phases with one of the phase's spawn times
/// (when, how many, where they work). An entry with more entities, phases or times than one takes a row for each, its
/// own columns only on its first.
/// </summary>
public sealed record SpawnsetRow(
    int Group, bool First, RedObject Entry, RedObject? Entity, RedObject? Phase, RedObject? Spawn,
    string Comment, string EntryId, string Template, string Appearances, string SpawnTags, string MappinTag, string MappinType,
    string EntityInitializers, string Despawners, string PhaseComment, string StoryPhase, bool HiddenSpawn, string PhaseInitializers,
    string Time, string Quantity, string RespawnDelay, bool Respawn, string Timetable, string SpawnDelay, string SpawnPointTags,
    string DespawnPointTags, bool StartInAP, bool UseLastAP, bool AlwaysSpawned, string SpawnStrategy, string MapPin)
{
    /// <summary>Whether its entry is an odd one, shaded in the grid so each entry's rows stand together.</summary>
    public bool Shade => Group % 2 == 1;

    /// <summary>Everything the row says, for finding it.</summary>
    public string Text => string.Join(" ", Comment, EntryId, Template, Appearances, SpawnTags, MappinTag, StoryPhase, Timetable, SpawnPointTags, DespawnPointTags, PhaseComment);
}

/// <summary>
/// One row of a community's timetables: a timetable (by name, which a story phase uses), a time of day, the layer whose
/// action points it uses, and one of the action categories there with its weight and the action point tags it takes.
/// </summary>
public sealed record TimetableRow(int Group, bool First, string Name, string Time, string Layer, string Category, string Weight, string ApTags)
{
    public bool Shade => Group % 2 == 1;

    public string Text => string.Join(" ", Name, Layer, Category, ApTags);
}

/// <summary>
/// A community (.w2comm): who lives somewhere and when. Its table lists entries, each the entities that may be spawned
/// and, for each story phase (a set of quest tags), when and how many, where they spawn and which timetable they keep;
/// its timetables say, for each time of day, which layer's action points they go to and what they do there.
/// </summary>
public sealed class CommunityFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Community { get; init; }
    public required string SpawnsetType { get; init; }
    public required List<SpawnsetRow> Spawnset { get; init; }
    public required List<TimetableRow> Timetables { get; init; }
    public required int Entries { get; init; }
    public required int TimetableCount { get; init; }

    public static bool IsCommunity(string path) => path.EndsWith(".w2comm", StringComparison.OrdinalIgnoreCase);

    public static CommunityFile Read(byte[] data) => Read(Cr2wFile.Read(data));

    public static CommunityFile Read(Cr2wFile file)
    {
        var community = file.First("CCommunity") ?? throw new InvalidDataException("This isn't a community.");
        string Path(object? value) => value is RedHandle h ? file.PathOf(h) ?? "" : "";
        string Object(object? value) => value is RedHandle { Export: int i } && i < file.Exports.Count ? ObjectTree.Words(file.Exports[i].ClassName, "CCommunity", "CSpawnTree", "C") : "";
        // a tag list is kept as its bytes, or (a story phase's names) as a struct holding one
        string Tags(object? value) => value switch
        {
            byte[] raw => string.Join(", ", file.Tags(raw)),
            RedObject tags => Tags(tags["tags"]),
            _ => ObjectTree.Short(value) ?? "",
        };
        static string Time(object? value) => ObjectTree.GameTime(value) ?? "0:00";

        var spawnset = new List<SpawnsetRow>();
        var entries = community.Array("communityTable").OfType<RedObject>().ToList();
        for (int g = 0; g < entries.Count; g++)
        {
            var entry = entries[g];
            var entities = entry.Array("entities").OfType<RedObject>().ToList();
            // a phase with several spawn times takes a row for each
            var phases = entry.Array("storyPhases").OfType<RedObject>()
                .SelectMany(p => p.Array("spawnTimetable").OfType<RedObject>().DefaultIfEmpty().Select(s => (Phase: p, Spawn: s))).ToList();
            int rows = Math.Max(1, Math.Max(entities.Count, phases.Count));
            for (int r = 0; r < rows; r++)
            {
                var entity = r < entities.Count ? entities[r] : null;
                var (phase, spawn) = r < phases.Count ? phases[r] : (null, null);
                bool firstOfPhase = phase is not null && (r == 0 || !ReferenceEquals(phases[r - 1].Phase, phase));
                spawnset.Add(new SpawnsetRow(g, r == 0, entry, entity, phase, spawn,
                    r == 0 ? entry["comment"] as string ?? "" : "",
                    r == 0 ? entry["entryID"] as string ?? "" : "",
                    entity is null ? "" : Path(entity["entityTemplate"]),
                    entity is null ? "" : ObjectTree.Short(entity["appearances"]) ?? "",
                    entity is null ? "" : Tags(entity["entitySpawnTags"]),
                    entity is null ? "" : ObjectTree.Short(entity["mappinTag"]) ?? "",
                    entity is null ? "" : (entity["mappinType"] as RedEnum)?.ToString() ?? "",
                    entity is null ? "" : Object(entity["initializers"]),
                    entity is null ? "" : Object(entity["despawners"]),
                    firstOfPhase ? phase!["comment"] as string ?? "" : "",
                    firstOfPhase ? Tags(phase!["storyPhaseName"]) : "",
                    firstOfPhase && phase!["isHiddenSpawn"] is true,
                    firstOfPhase ? Object(phase!["initializers"]) : "",
                    spawn is null ? "" : Time(spawn["time"]),
                    spawn is null ? "" : spawn.Integer("quantity").ToString(),
                    spawn is null ? "" : Time(spawn["respawnDelay"]),
                    spawn?["respawn"] is true,
                    firstOfPhase ? ObjectTree.Short(phase!["timetableName"]) ?? "" : "",
                    firstOfPhase ? Time(phase!["spawnDelay"]) : "",
                    firstOfPhase ? Tags(phase!["spawnPointTags"]) : "",
                    firstOfPhase ? Tags(phase!["despawnPointTags"]) : "",
                    firstOfPhase && phase!["startInAP"] is true,
                    firstOfPhase && phase!["useLastAP"] is true,
                    firstOfPhase && phase!["alwaysSpawned"] is true,
                    firstOfPhase ? Object(phase!["spawnStrategy"]) : "",
                    firstOfPhase && phase!.Struct("cachedMapPinPosition") is { } pin ? $"{pin.Float("X"):0.#} {pin.Float("Y"):0.#} {pin.Float("Z"):0.#}" : ""));
            }
        }

        var timetables = new List<TimetableRow>();
        var named = community.Array("storyPhaseTimetable").OfType<RedObject>().ToList();
        for (int g = 0; g < named.Count; g++)
        {
            var timetable = named[g];
            bool first = true;
            string name = ObjectTree.Short(timetable["name"]) ?? "";
            foreach (var at in timetable.Array("actionCategies").OfType<RedObject>())
                foreach (var action in at.Array("actions").OfType<RedObject>())
                {
                    var categories = action.Array("actionCategories").OfType<RedObject>().ToList();
                    string layer = ObjectTree.Short(action.Struct("layerName")?["layerName"]) ?? "";
                    foreach (var category in categories.DefaultIfEmpty())
                    {
                        timetables.Add(new TimetableRow(g, first, first ? name : "", Time(at["time"]), layer,
                            ObjectTree.Short(category?["name"]) ?? "", category is null ? "" : category.Float("weight", 1).ToString("0.####"),
                            category is null ? "" : Tags(category["apTags"])));
                        first = false;
                    }
                }
            if (first) timetables.Add(new TimetableRow(g, true, name, "", "", "", "", ""));
        }

        return new CommunityFile
        {
            File = file,
            Community = community,
            SpawnsetType = (community["spawnsetType"] as RedEnum)?.ToString() ?? "CST_Global",
            Spawnset = spawnset,
            Timetables = timetables,
            Entries = entries.Count,
            TimetableCount = named.Count,
        };
    }
}
