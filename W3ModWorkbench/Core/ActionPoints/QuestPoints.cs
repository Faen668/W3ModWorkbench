using System.IO;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.ActionPoints;

/// <summary>
/// What sort of quest a code is, by its prefix; Other is a quests folder with no code (part_3, radovid_ship), and the
/// characters are a story character's own spots (main_npcs, secondary_npcs), not a quest's.
/// </summary>
public enum QuestKind { Main, Side, Minor, Contract, TreasureHunt, Fistfight, Gwent, HorseRace, Other, MainCharacter, SecondaryCharacter, Character }

/// <summary>
/// Where an action point's quest was read from: its layer's path, one of its tags, the quests folder its layer is in,
/// a story character's folder, or a layer named for a story character the world has (elihal.w2l).
/// </summary>
public enum QuestSource { Path, Tag, Folder, Character, NamedLayer }

/// <summary>
/// The quest or story character an action point is placed for: its internal code (q309, sq301, mq7010…, the folder's
/// name when its quests folder has no code, or the character's name), what sort it is, and where it was read from (the
/// tag, for a tag).
/// </summary>
public sealed record QuestLink(string Code, QuestKind Kind, QuestSource Source, string? From)
{
    /// <summary>Whether it's a story character's usual spots rather than a quest's.</summary>
    public bool IsCharacter => Kind is QuestKind.MainCharacter or QuestKind.SecondaryCharacter or QuestKind.Character;
}

/// <summary>
/// Which action points are a quest's rather than the open world's. The game keeps no list: each quest's layers go in a
/// folder named for its code (levels\novigrad\quests\main_quests\q309_casablanca\…, or a community's own folder, such
/// as …\community\barrens\quarry\mq7010\), so a code in the layer's path is the quest (the last, for a path with two).
/// A layer with none can still be a quest's community, which its tags name (beauclair_e_streets_q705_restoration_worker);
/// and a layer under a quests folder with no code (part_3, radovid_ship) is quest content all the same. A story
/// character's usual spots go in a main_npcs or secondary_npcs folder, named for the character (…\main_npcs\baron\,
/// …\secondary_npcs\eskel.w2l): those are the character's, whatever quest code the layer's name has as well. Vizima
/// keeps its own in npcs_actionpoints (emhyr_default.w2l), which doesn't say main or secondary; and a community's layer
/// named just for a character the same world has a folder for (elihal.w2l, elihal_scene_02.w2l) is theirs too
/// (<see cref="NamedFor"/>).
/// </summary>
public static partial class QuestPoints
{
    // q main, sq side, mq minor, mh contracts (monster hunts), th treasure hunts, ff fistfights, cg card games, hr horse races
    [GeneratedRegex(@"(?<![a-z0-9])(?:q|sq|mq|mh|th|ff|cg|hr)\d{3,4}(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\\quests\\([^\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestsFolder();

    [GeneratedRegex(@"\\(main|secondary)_npcs\\([^\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharacterFolder();

    [GeneratedRegex(@"\\npcs_actionpoints\\([^\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpcsFolder();

    // a layer named for a character: theirs alone, their usual spot, one of a few, or a scene of theirs
    [GeneratedRegex(@"^(?:default_)?(.+?)(?:_default|_\d+|_scene(?:_\d+)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NamedLayer();

    /// <summary>The quest or story character an action point is placed for, or null for one of the open world's.</summary>
    public static QuestLink? Of(ActionPoint p)
    {
        if (CharacterFolder().Match(p.Layer) is { Success: true } character)
            return new QuestLink(Path.GetFileNameWithoutExtension(character.Groups[2].Value).ToLowerInvariant(),
                character.Groups[1].Value.Equals("main", StringComparison.OrdinalIgnoreCase) ? QuestKind.MainCharacter : QuestKind.SecondaryCharacter, QuestSource.Character, null);
        if (NpcsFolder().Match(p.Layer) is { Success: true } npc)
            return new QuestLink(NamedLayer().Match(Path.GetFileNameWithoutExtension(npc.Groups[1].Value).ToLowerInvariant()).Groups[1].Value,
                QuestKind.Character, QuestSource.Character, null);
        if (CodePattern().Matches(p.Layer) is { Count: > 0 } inPath)
            return Link(inPath[^1].Value, QuestSource.Path, null);
        foreach (string tag in p.Tags)
            if (CodePattern().Match(tag) is { Success: true } inTag)
                return Link(inTag.Value, QuestSource.Tag, tag);
        if (QuestsFolder().Match(p.Layer) is { Success: true } folder)
        {
            string name = Path.GetFileNameWithoutExtension(folder.Groups[1].Value).ToLowerInvariant();
            return new QuestLink(name, QuestKind.Other, QuestSource.Folder, null);
        }
        return null;
    }

    /// <summary>
    /// The story character an open-world point's layer is named for, from the characters its world has folders for
    /// (by world and name): elihal.w2l, elihal_scene_02.w2l or baron_quartermaster_01.w2l, not baron_keep_village_trader.
    /// Another world's character doesn't count, so Novigrad's houses' butler.w2l isn't Toussaint's Butler.
    /// </summary>
    public static QuestLink? NamedFor(ActionPoint p, IReadOnlyDictionary<(string World, string Name), QuestKind> characters)
    {
        string name = NamedLayer().Match(Path.GetFileNameWithoutExtension(p.Layer).ToLowerInvariant()).Groups[1].Value;
        return characters.TryGetValue((p.World.ToLowerInvariant(), name), out var kind) ? new QuestLink(name, kind, QuestSource.NamedLayer, null) : null;
    }

    private static QuestLink Link(string code, QuestSource source, string? from)
    {
        code = code.ToLowerInvariant();
        return new QuestLink(code, KindOf(code), source, from);
    }

    /// <summary>A quest code's sort, by its prefix.</summary>
    public static QuestKind KindOf(string code) => code.TrimEnd("0123456789".ToCharArray()) switch
    {
        "q" => QuestKind.Main,
        "sq" => QuestKind.Side,
        "mq" => QuestKind.Minor,
        "mh" => QuestKind.Contract,
        "th" => QuestKind.TreasureHunt,
        "ff" => QuestKind.Fistfight,
        "cg" => QuestKind.Gwent,
        "hr" => QuestKind.HorseRace,
        _ => QuestKind.Other,
    };

    /// <summary>
    /// The names a quest's folders give it beside its code (q309_casablanca, mq1060_devil_pit), most used first: the
    /// path's folders and layers that start with the code and go on with a name.
    /// </summary>
    public static IEnumerable<string> NamesIn(string code, IEnumerable<string> layers) =>
        layers.SelectMany(l => l.Split('\\'))
            .Select(s => Path.GetFileNameWithoutExtension(s).ToLowerInvariant())
            .Where(s => s.Length > code.Length + 1 && s.StartsWith(code + "_", StringComparison.Ordinal))
            .GroupBy(s => s).OrderByDescending(g => g.Count()).Select(g => g.Key);
}
