---
title: Searching the game's strings
icon: E721
order: 2
summary: Find any line of the game's text, its ID and key, before writing your own.
keywords: game strings search text id key voiceover redkit database language
windows: GameStringsWindow
---
Before adding a line to your mod, check whether the game already says it. **Tools > Search game strings** ({{Ctrl+Shift+G}}) searches every string the game loads, in the language you pick.

![Searching the game's strings for swallow potion](game-strings.jpg)

## Searching

- Type words, and every one must be in the text: `swallow potion`. **Whole words** stops `ale` from finding `scale`.
- Type a number to find an ID: `2115940`.
- Type a key's name (`panel_Mods`) or its hash in hex to find the strings that use it.

Results show the ID, key and text. Double-click a row to copy its ID; right-click to copy the text, `GetLocStringById(id)`, or a line for a strings `.csv`.

## Where it reads from

**Read from** picks the source:

- **Game files**: the game's `.w3strings`, and with **Include DLC and mods**, your DLC's and mods' too.
- **REDkit database**: REDkit's strings database, which knows more: each key's **name**, the **kind** of text (a line, a choice, an item, a book…), the **scene** it's used in and the **voiceover** name. Part of a scene path (`q202_05b`) or a voiceover name finds lines too.
- **Database file…**: any REDkit strings database you pick.

The **From** column says where each result came from.
