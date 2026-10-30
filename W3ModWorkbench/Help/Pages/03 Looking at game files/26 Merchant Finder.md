---
title: Merchant Finder
icon: E719
order: 26
summary: Every merchant in the game's worlds: what they sell and how likely, their money and prices, where they stand through the day, and what decides whether they're there.
keywords: merchant merchants shop shops shopkeeper trader vendor blacksmith armorer armourer herbalist alchemist innkeeper barber enchanter runewright wine dye bookseller shipwright cammerlengo stock sells sell buy funds money crowns prices where to buy who sells quest phase community encounter timetable map
windows: MerchantFinderWindow
---
**Tools > Merchant Finder…** lists every merchant the game's worlds place: what each sells and how likely, how much money they have, their prices, where they stand through the day on the game's own map, and what decides whether they're there.

![Trentin, Beauclair's grandmaster blacksmith: what he sells and where he works](merchant-finder.jpg)

## Finding a merchant

The list is grouped by kind (shopkeepers, blacksmiths, armorers, herbalists, innkeepers and the rest), each merchant with their world and area, and how many places they can be. Search ({{Ctrl+F}}) by a merchant's name or kind, an area, or **an item they sell**, by its in-game name or its name in the files: the list then shows only the merchants who sell it, and picking one marks the item in their stock.

## The merchant

The middle column shows:

- **Crafts**: what a craftsman makes and at what level (journeyman, master, grandmaster).
- **Money**: what the merchant starts with, how much more they get every 5 days when you talk to them, and the most they can hold. A merchant with no money buys nothing from you.
- **Prices**: goods from the merchant's own region are cheaper there.
- **Where and when**: pick a place below, or a spot on the map, to see that place's hours (each spot the merchant goes to and when), and what decides whether they're there: a story phase, a quest done or not done yet, or a group quests can send away. A place with none of these says **Always there**.
- **Files**: open the merchant's template, their shop scene, and the community or encounter that spawns them.

## The map

The game's own map, with each spot the merchant goes to coloured by what they do there: working, on guard, eating, at home, resting or sleeping. Drag to move, use the wheel to zoom, and **Fit** shows all the spots again.

- **World** picks the world shown, each with how many of the merchant's places are in it.
- **Player level** sets the level the stock's chances are for; the [[Loot Locator]] and the [[Bestiary]] use the same level.
- **Installed mods** reads the installed mods' copies of layers, communities, templates and item files too, as the game does with them installed, so a mod's changes to a shop's stock show too.
- **Export** saves every merchant and every place they can be, with their hours, what decides it and their position, as a CSV file.

## Stock, places and quests

- **Stock** lists what the shop holds when it fills: each item's chance at the level chosen, how many, and its base price. Double-click an item to find where else it can be found in the [[Loot Locator]].
- **Places** lists each place the merchant can be: its world and area, its hours, what decides whether they're there, and whether a community or an encounter spawns them. Click one to see it on the card and the map.
- **Quests** lists the quests that bring the merchant, move them to another phase or send them away. Double-click one to open its file.

![A blacksmith who works at the quarry only after a quest is done](merchant-finder-place.jpg)
