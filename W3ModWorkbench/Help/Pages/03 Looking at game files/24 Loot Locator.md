---
title: Loot Locator
icon: E721
order: 24
summary: Where an item or a loot table can be found in the game's worlds, and how likely, on the game's own map.
keywords: loot locator loot table loot tables where to find item find items containers chests herbs beehives chance odds drop rate respawn player level map spawn points CR4LootParam def_loot_containers xml
windows: LootLocatorWindow
---
**Tools > Loot Locator…** shows where in the game's worlds an item or a loot table can be found, and how likely, on the game's own map: every container, herb and beehive that can give it, and every place that spawns a creature that drops it.

![Where bottled water can be found in Velen, coloured by its chance](loot-locator.jpg)

## Finding an item or a table

- **Items** lists every item a placed container, herb or hive can give, by its in-game name with its name in the files under it, and how many places can give it. Search ({{Ctrl+F}}) by either name. The box under the search shows one category only (steel swords, crafting diagrams, alchemy ingredients…), named as the game's inventory names it, with how many items each has.
- **Loot tables** lists the tables places use, with their item count and file. **Show tables no place uses** adds the rest: shops, quest rewards and monsters' drops. A table a template names but the game never defines has a warning mark: the game skips it, so it gives nothing.

Pick one and the map shows every place that can give it.

## The map

The game's own minimap, with each place as a dot coloured by its chance: yellow for 90% or more, down to violet for under 5%. A **diamond** marks the places where the game's roll for that exact spot gives it. Drag to move, use the wheel to zoom, and **Fit** shows all the places again. Zoomed in close, the map draws the game's own full-size minimap tiles, as sharp as the game has them. A world the game has no minimap for (Vizima's palace) shows its terrain's relief instead.

- **World** picks the world shown. Each says how many of its places can give what's picked.
- **Player level** sets the level the chances are for. Tables and items can be limited to player levels, so some only turn up later; the status line says how many places give it only at other levels.
- **Containers**, **Herbs**, **Beehives**, **Monsters** and **Other** (clues, stashes, nests) choose which places are shown. A monster's place is where it spawns, with the chance of the item at each kill; its drops are rolled again every time, so it has no **Rolled here**.
- **Rolled here** shows or hides what the game's roll gives each exact spot (the diamonds, and the column of that name).
- **Installed mods** reads the installed mods' copies of layers, templates and item files too, as the game does with them installed: a mod's own loot tables, and its changes to the game's, are rolled like the game's.
- **Export** saves every place, in every world, as a CSV file with its position, chance and respawn time.

## Places and what they give

**Places** lists the world's places, likeliest first, with their area (the layer they're in), chance, respawn time and position. **Chance** is how often a container like that one gets the item, over many random rolls; the game's roll for each placed spot is fixed by where it stands, so **Rolled here** is the one to trust for that exact spot. A chest can show 10% and still say **Yes**. Click one, or a dot on the map, to see what it can give:

- **Rolled here**: what that exact spot gives at the level chosen. The game picks a place's items from where it stands, so the same spot always gets the same items; a spot rolled at random each time says so.
- **Uses N of these tables**: the place's tables, each with the chance it's used. Click a table to look at it.
- **Can give**: every item and its chance. Click an item to look for it.
- **Always holds**: items the template always holds (some quest chests).

**Copy position** copies its world position, and **Open the template** and **Open the layer** open the files it comes from.

![A loot table: where it's used, and what it gives when it is](loot-locator-table.jpg)

With a loot table picked, **Items in the table** lists what it gives each time it's used: each item's chance, its weight (**first** for an item picked before the weighted ones), how many and the levels it's for.

> tip: In a table where every item's weight is **first**, some items can never come at all. **Items in the table** shows them at 0%.

To change where an item is found, edit its loot tables in the [[Items and stats]] editor.
