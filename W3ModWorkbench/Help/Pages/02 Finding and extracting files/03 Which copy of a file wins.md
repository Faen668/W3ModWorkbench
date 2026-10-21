---
title: Which copy of a file wins
icon: E8C8
order: 3
summary: When several mods have the same file, see every copy, which one the game uses, and whether they really differ.
keywords: other copies load order priority conflict same path override mods.settings pick copy
windows: OtherCopiesWindow, PickCopyWindow
---
Two mods can ship a file at the same game path, say a changed `def_item_weapons.xml`. The game only uses one of them. The app shows you every copy and which one wins.

## Other copies of a file

Highlight a file and press {{Ctrl+Shift+D}} (or right-click > **Other copies of this file…**). The window lists every open archive with a file at that path, in the order the game loads them:

1. mod.io mods
2. DLC
3. mods, by their priority in `Documents\The Witcher 3\mods.settings` (lowest number first)
4. the game's own files

The copy the game uses is marked, and each copy says whether its content is the **same** as the one you highlighted. Buttons beside each compare it, open it, or show it in Explorer.

> tip: Open the whole `mods` folder first (or use the [[The Asset Browser|Asset Browser]]) so every mod's copy is in the list.

## Choosing a copy to open

In the Asset Browser's **Game files**, double-clicking a file that a mod also has asks which copy to open: every copy in load order, the one the game loads marked, and the game's own picked to start with. **Don't ask again** turns the question off (Game files then opens the game's own copy); **Ask which copy** in the browser's toolbar turns it back on. **Open another copy…** on the right-click menu asks even when asking is off.

## Comparing two files

Highlight two files and press {{Ctrl+D}} (**Compare the two**). Text files open side by side with the differences marked ({{F8}} steps through them). Other files are compared byte for byte, and a message says whether they're the same and where they first differ.

For whole mods and their scripts, the [[Load order analyser]] does this for every file at once.
