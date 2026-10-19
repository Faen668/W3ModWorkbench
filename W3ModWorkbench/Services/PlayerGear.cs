using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench;

/// <summary>A piece of Geralt's gear to draw: its entity, and the slot it hangs on (a sword's on his back), or null for one he wears.</summary>
public sealed record GearPiece(string Item, string Template, string? Slot);

/// <summary>
/// What Geralt wears when nothing else is known, as the game gives it to him: the player's entities have only a neck seam
/// of their own, and the game puts him together from what he has equipped. His default inventory
/// (<c>geralt_inventory_release.w2ent</c>: the starting armour, trousers, gloves and boots, the Long Steel Sword, the
/// Witcher Silver Sword and his hairstyle) is looked up in the game's item definitions (<c>gameplay\items\def_item_*.xml</c>)
/// for each item's entity and the slot it's equipped on, with his head added, as it isn't an item.
/// </summary>
public static class PlayerGear
{
    private const string Inventory = @"characters\player_entities\geralt\geralt_inventory_release.w2ent";
    private const string Head = @"items\bodyparts\geralt_items\heads\h_01_mg__geralt.w2ent";

    private static readonly object Gate = new();
    private static List<GearPiece>? _known;

    /// <summary>
    /// Geralt's default gear, worked out once. Empty when the inventory or the item definitions can't be read. Reads the
    /// game's files, so it belongs off the UI thread.
    /// </summary>
    public static List<GearPiece> Default(ResourceLocator locator)
    {
        lock (Gate)
            return _known ??= Read(locator);
    }

    private sealed record ItemDefinition(string Category, string Template, string? Slot, List<(string Template, string Category)> Variants);

    private static List<GearPiece> Read(ResourceLocator locator)
    {
        var gear = new List<GearPiece> { new("Geralt's head", Head, null) };
        var items = InventoryItems(locator);
        if (items.Count == 0) return gear;
        var definitions = Definitions(locator);
        var entities = locator.AllPaths(".w2ent").GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var worn = items.Where(definitions.ContainsKey).Select(i => definitions[i].Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string item in items)
        {
            if (!definitions.TryGetValue(item, out var definition)) continue;
            // an item drawn differently with another worn (the armour with gloves on) takes that variant
            string template = definition.Variants.FirstOrDefault(v => worn.Contains(v.Category)).Template ?? definition.Template;
            if (entities.TryGetValue(template, out var path)) gear.Add(new GearPiece(item, path, definition.Slot));
        }
        return gear;
    }

    /// <summary>The items Geralt's default inventory gives him, by name.</summary>
    private static List<string> InventoryItems(ResourceLocator locator)
    {
        try
        {
            if (locator.ReadFile(Inventory) is not { } data) return [];
            var file = Cr2wFile.Read(data);
            return file.Exports.Where(x => x.ClassName == "CInventoryInitializerUniform")
                .Select(x => file.Object(x.Index)["itemName"] as string).OfType<string>().Where(n => n.Length > 0).ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    private static readonly Regex ItemTag = new(@"<item\b[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex VariantTag = new(@"<variant\b[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Attribute = new(@"(\w+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>Every item the game defines with an entity, by name: its category, entity, the slot it's equipped on and its variants.</summary>
    private static Dictionary<string, ItemDefinition> Definitions(ResourceLocator locator)
    {
        var definitions = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in locator.AllPaths(".xml").Where(p => p.StartsWith(@"gameplay\items\def_item", StringComparison.OrdinalIgnoreCase)))
        {
            if (locator.ReadFile(path) is not { } data) continue;
            string text = Encoding.UTF8.GetString(data);
            var tags = ItemTag.Matches(text);
            for (int i = 0; i < tags.Count; i++)
            {
                var attributes = Attributes(tags[i].Value);
                if (!attributes.TryGetValue("name", out var name) || !attributes.TryGetValue("equip_template", out var template) || template.Length == 0) continue;
                // the item's variants are between its tag and the next item's
                int end = i + 1 < tags.Count ? tags[i + 1].Index : text.Length;
                var variants = VariantTag.Matches(text[tags[i].Index..end]).Select(v => Attributes(v.Value))
                    .Where(v => v.ContainsKey("equip_template") && v.ContainsKey("category"))
                    .Select(v => (v["equip_template"], v["category"])).ToList();
                definitions.TryAdd(name, new ItemDefinition(attributes.GetValueOrDefault("category", ""), template, attributes.GetValueOrDefault("equip_slot"), variants));
            }
        }
        return definitions;
    }

    private static Dictionary<string, string> Attributes(string tag) =>
        Attribute.Matches(tag).GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
}
