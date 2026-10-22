---
title: Items and stats
icon: E7BF
order: 5
summary: Change the game's items, their stats, how they're crafted and where they're found, saved as XML overrides that work beside other mods.
keywords: item editor items stats abilities price weight stack durability crafting schematic recipe loot on_conflict extend replace override dlc new game plus rebalance
windows: ItemEditorWindow
---
**Tools > Item editor…** lists every item of the game, by its in-game name, and shows the one you pick as a card: its attributes, its stats, the schematics and recipes that make it and the loot tables it's found in. Change any value and **Save to project** ({{Ctrl+S}}).

![The item editor, with the Poker's price and weight changed](item-editor.jpg)

## Finding an item

Search ({{Ctrl+F}}) by in-game name, item name or tag, or pick a category. **Changed** shows only the items your project changes, or whose stats, schematics or loot it changes; a dot marks them in the list.

## The card

- **General**: the item's own attributes, the ones most changed first (price, weight, stack size, durability, upgrade slots…), the rest under **More attributes**, then its tags and base abilities.
- **Stats**: each ability the item is given, with its stats. A stat the game gives one number shows one box; one with a range shows its min and max. An ability shared by other items says how many: changing it changes them all.
- **Crafted by** / **Made by**: the schematic or alchemy recipe that makes it, with its ingredients.
- **Found in**: each loot table it's in, with its quantities and chance.

A value you've changed is marked, with the game's beside it. **Put back** on a section returns that definition to the game's.

## How it's saved

Since the Remastered update, a mod can change one of the game's definitions without copying the whole file, using `on_conflict` in XML that a DLC mounts. The editor saves that way where the game allows it:

- **Items** go in your project's own DLC (`workspace\dlc\dlcNAME\data\gameplay\items\dlcNAME.xml`). An item whose attributes alone changed is written as `on_conflict="extend"` with just those attributes, which plays well with other mods changing the same item; one whose tags or abilities changed is written whole, as `on_conflict="replace"`.
- **Abilities** (stats) are written whole, as `on_conflict="replace"`.
- **Schematics, recipes and loot tables** can't be overridden, so they're saved in a copy of the game's file in your workspace, with only the changed definition written anew and the rest of the file kept as it is. Another mod that changes the same file would clash with yours: the [[Load order analyser]] shows such clashes.
- **Also New Game+** makes the same changes to New Game+'s copies (`items_plus`), each to its own values, so its stronger stats stay.

**Build** makes the project's DLC beside the mod, with a `.reddlc` that mounts the override files, and **Install** puts it in the game's `dlc` folder. See [[Building and installing]].

```xml
<item name="Iron Poker" on_conflict="extend" price="55" />
<ability name="Iron Poker _Stats" on_conflict="replace">
	<SilverDamage min="1" max="1" />
	<weight type="base" min="9" max="9" />
	<quality type="add" min="1" />
</ability>
```

> note: The editor changes the game's own definitions. To see what other mods change in the same definitions, see [[XML overrides]].
