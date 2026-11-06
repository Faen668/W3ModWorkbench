using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>A piece of Geralt's gear to draw: its entity, and the slot it hangs on (a sword's on his back), or null for one he wears.</summary>
public sealed record GearPiece(string Item, string Template, string? Slot)
{
    /// <summary>The appearance of its entity to draw it in (a dye), or null for its default one.</summary>
    public string? Appearance { get; init; }
}

/// <summary>
/// What Geralt wears when nothing else is known, as the game gives it to him: the player's entities have only a neck seam
/// of their own, and the game puts him together from what he has equipped. His default inventory
/// (<c>geralt_inventory_release.w2ent</c>: the starting armour, trousers, gloves and boots, the Long Steel Sword, the
/// Witcher Silver Sword and his hairstyle) is looked up in the game's item definitions (<c>gameplay\items\def_item_*.xml</c>)
/// for each item's entity and the slot it's equipped on, with the items bound to it (each sword's scabbard) and his head
/// added, as it isn't an item.
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

    private sealed record ItemDefinition(string Category, string Template, string? Slot, List<ItemVariant> Variants, List<string> Bound);

    /// <summary>
    /// Another entity an item is drawn with while other things are worn: items by name (a hairstyle cut shorter under a
    /// hood), categories, or both; with <see cref="All"/>, only while all of them are, else while any is.
    /// </summary>
    private sealed record ItemVariant(string Template, List<string> Items, List<string> Categories, bool All, bool CategoryList)
    {
        /// <summary>How the game ranks a variant that fits: one naming items first, then a list of categories, then one category.</summary>
        public int Rank => Items.Count > 0 ? 0 : CategoryList ? 1 : 2;

        public bool Fits(ISet<string> items, ISet<string> categories)
        {
            var held = Items.Select(items.Contains).Concat(Categories.Select(categories.Contains)).ToList();
            return held.Count > 0 && (All ? held.All(h => h) : held.Any(h => h));
        }
    }

    private static Dictionary<string, ItemDefinition>? _definitions;
    private static Dictionary<string, string>? _entities;

    /// <summary>
    /// An item's entity by its name in the game's item definitions (a broom, Regis's bag), or, for a kind of item Geralt
    /// carries (silversword, steelsword), the one his default gear gives him. Null when there's no such item. Reads the
    /// game's files the first time, so it belongs off the UI thread.
    /// </summary>
    public static string? Find(ResourceLocator locator, string name)
    {
        lock (Gate)
        {
            Known(locator);
            if (_definitions!.TryGetValue(name, out var definition) && _entities!.TryGetValue(definition.Template, out var path)) return path;
            // a kind of item: the one of that kind in Geralt's default gear
            // a kind of item (steelsword): Geralt's own if he has one, else the first the game defines
            var piece = (_known ??= Read(locator)).FirstOrDefault(g => _definitions!.TryGetValue(g.Item, out var d) && d.Category.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (piece is not null) return piece.Template;
            return _definitions.Values.Where(d => d.Category.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(d => _entities!.GetValueOrDefault(d.Template)).FirstOrDefault(t => t is not null);
        }
    }

    /// <summary>
    /// The item whose entity a template is (its <c>equip_template</c>), by name, for an item Geralt can wear or carry; null
    /// for any other entity. Of several items drawn with the same entity (the Long Steel Sword and its crafted copies), the
    /// one Geralt starts with, else the game's own first. Geralt's own head isn't offered: he always wears it. Reads the
    /// game's files the first time, so it belongs off the UI thread.
    /// </summary>
    public static string? ItemOf(ResourceLocator locator, string entityPath)
    {
        string name = Path.GetFileNameWithoutExtension(entityPath);
        if (name.Equals(Path.GetFileNameWithoutExtension(Head), StringComparison.OrdinalIgnoreCase)) return null;
        lock (Gate)
        {
            var items = Known(locator).Definitions.Where(d => d.Value.Template.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !d.Value.Category.Equals("fist", StringComparison.OrdinalIgnoreCase)).Select(d => d.Key).ToList();
            var starting = InventoryItems(locator);
            return items.FirstOrDefault(i => starting.Contains(i, StringComparer.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
        }
    }

    /// <summary>
    /// Geralt's default gear with one item put on, as the game would equip it: it takes the place of what he wears of the
    /// same category or on the same slot (another armour, his hairstyle, the steel sword), comes with what's bound to it,
    /// and what he wears takes the variants it calls for (a shorter cut under a hood). The item is drawn in
    /// <paramref name="appearance"/> when it has it. Reads the game's files the first time, so it belongs off the UI thread.
    /// </summary>
    public static List<GearPiece> Wearing(ResourceLocator locator, string item, string? appearance)
    {
        lock (Gate)
        {
            var (definitions, _) = Known(locator);
            var items = InventoryItems(locator);
            if (definitions.TryGetValue(item, out var put))
                items.RemoveAll(i => definitions.TryGetValue(i, out var d)
                    && (d.Category.Equals(put.Category, StringComparison.OrdinalIgnoreCase) || put.Slot is { } slot && slot.Equals(d.Slot, StringComparison.OrdinalIgnoreCase)));
            items.Add(item);
            return Read(locator, items).Select(g => g.Item.Equals(item, StringComparison.OrdinalIgnoreCase) ? g with { Appearance = appearance } : g).ToList();
        }
    }

    /// <summary>The item definitions and the entities by name, read once.</summary>
    private static (Dictionary<string, ItemDefinition> Definitions, Dictionary<string, string> Entities) Known(ResourceLocator locator)
    {
        _definitions ??= Definitions(locator);
        _entities ??= locator.AllPaths(".w2ent").GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return (_definitions, _entities);
    }

    private static List<GearPiece> Read(ResourceLocator locator) => Read(locator, InventoryItems(locator));

    /// <summary>Geralt's head and the gear the items give him, each with what's bound to it and in the variant the others call for.</summary>
    private static List<GearPiece> Read(ResourceLocator locator, List<string> items)
    {
        var gear = new List<GearPiece> { new(L.T("PlayerGear.GeraltsHead"), Head, null) };
        if (items.Count == 0) return gear;
        var (definitions, entities) = Known(locator);
        var wornItems = items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var worn = items.Where(definitions.ContainsKey).Select(i => definitions[i].Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // each item with those that come with it (a sword's scabbard, on his back beside it)
        foreach (string item in items.SelectMany(i => definitions.TryGetValue(i, out var d) ? d.Bound.Prepend(i) : [i]).Distinct().ToList())
        {
            // fists are an item too, drawn only in a fist fight
            if (!definitions.TryGetValue(item, out var definition) || definition.Category.Equals("fist", StringComparison.OrdinalIgnoreCase)) continue;
            // an item drawn differently with another worn (the armour with gloves on) takes that variant
            string template = Variant(definition, wornItems, worn);
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

    /// <summary>
    /// The entity an item is drawn with, given the items and categories worn: of its variants that fit, the best ranked
    /// (naming items, then listing categories, then one category), the first of those (mods' extensions come ahead of
    /// the item's own); else its own entity.
    /// </summary>
    private static string Variant(ItemDefinition definition, ISet<string> items, ISet<string> categories) =>
        definition.Variants.Where(v => v.Fits(items, categories)).OrderBy(v => v.Rank).FirstOrDefault()?.Template ?? definition.Template;

    // an item's own tag has attributes; a bare <item> names one bound to it or one a variant needs
    private static readonly Regex ItemTag = new(@"<item\s[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ExtensionTag = new(@"<item_extension\s[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);
    // where an item's or extension's own part ends: the next item or extension, or the end of the list
    private static readonly Regex PartEnd = new(@"<item\s|<item_extension\s|</items>|<items_extensions\b|</items_extensions>", RegexOptions.Compiled);
    private static readonly Regex VariantBlock = new(@"<variant\b([^>]*?)(?:/>|>(.*?)</variant>)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex NamedItem = new(@"<item>([^<]*)</item>", RegexOptions.Compiled);
    private static readonly Regex NamedCategory = new(@"<item_category>([^<]*)</item_category>", RegexOptions.Compiled);
    private static readonly Regex BoundList = new(@"<bound_items>.*?</bound_items>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Attribute = new(@"(\w+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// Every item the game and the installed mods define with an entity, by name: its category, entity, the slot it's
    /// equipped on and its variants, with those mods add to it in <c>item_extension</c> blocks ahead of its own. Every
    /// item file is read (masks are in <c>w3r_items.xml</c> too), the base game's first; the first definition of a name wins.
    /// </summary>
    private static Dictionary<string, ItemDefinition> Definitions(ResourceLocator locator)
    {
        var definitions = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
        var extensions = new Dictionary<string, List<ItemVariant>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in locator.AllPaths(".xml").Where(p => p.Contains(@"gameplay\items", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.StartsWith("dlc", StringComparison.OrdinalIgnoreCase)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (locator.ReadFile(path) is not { } data) continue;
            string text = Encoding.UTF8.GetString(data);
            foreach (Match tag in ItemTag.Matches(text))
            {
                var attributes = Attributes(tag.Value);
                if (!attributes.TryGetValue("name", out var name) || !attributes.TryGetValue("equip_template", out var template) || template.Length == 0) continue;
                string part = Part(text, tag);
                // the items that come with it, equipped beside it (a sword's scabbard)
                var bound = BoundList.Match(part) is { Success: true } list
                    ? NamedItem.Matches(list.Value).Select(m => m.Groups[1].Value.Trim()).Where(n => n.Length > 0).ToList() : [];
                string? slot = attributes.GetValueOrDefault("equip_slot") is { Length: > 0 } s && !s.Equals("None", StringComparison.OrdinalIgnoreCase) ? s : null;
                definitions.TryAdd(name, new ItemDefinition(attributes.GetValueOrDefault("category", ""), template, slot, Variants(BoundList.Replace(part, "")), bound));
            }
            foreach (Match tag in ExtensionTag.Matches(text))
                if (Attributes(tag.Value).GetValueOrDefault("name") is { Length: > 0 } name)
                {
                    if (!extensions.TryGetValue(name, out var added)) extensions[name] = added = [];
                    added.AddRange(Variants(Part(text, tag)));
                }
        }
        foreach (var (name, added) in extensions)
            if (definitions.TryGetValue(name, out var definition)) definitions[name] = definition with { Variants = [.. added, .. definition.Variants] };
        return definitions;
    }

    /// <summary>An item's or extension's own part of its file: from its tag to the next item or extension, or the end of the list.</summary>
    private static string Part(string text, Match tag)
    {
        int from = tag.Index + tag.Length;
        var end = PartEnd.Match(text, from);
        return text[from..(end.Success ? end.Index : text.Length)];
    }

    /// <summary>The variants in an item's or extension's part of its file.</summary>
    private static List<ItemVariant> Variants(string part) =>
        VariantBlock.Matches(part).Select(m =>
        {
            var attributes = Attributes(m.Groups[1].Value);
            string inner = m.Groups[2].Value;
            var categories = NamedCategory.Matches(inner).Select(c => c.Groups[1].Value.Trim()).Where(c => c.Length > 0).ToList();
            bool list = categories.Count > 0;
            if (attributes.GetValueOrDefault("category") is { Length: > 0 } category) categories.Insert(0, category);
            return new ItemVariant(attributes.GetValueOrDefault("equip_template") ?? "",
                NamedItem.Matches(inner).Select(i => i.Groups[1].Value.Trim()).Where(i => i.Length > 0).ToList(),
                categories, attributes.GetValueOrDefault("all") is { } all && all.Equals("true", StringComparison.OrdinalIgnoreCase), list);
        }).Where(v => v.Template.Length > 0).ToList();

    private static Dictionary<string, string> Attributes(string tag) =>
        Attribute.Matches(tag).GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.OrdinalIgnoreCase);
}
