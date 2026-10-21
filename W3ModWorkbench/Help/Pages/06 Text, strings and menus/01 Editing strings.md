---
title: Editing strings
icon: E8D2
order: 1
summary: The words your mod shows in the game: edit .w3strings files, every language at once, and update translations.
keywords: strings w3strings csv localisation translation language id key text
windows: StringsWindow
---
The text a mod shows in the game lives in `.w3strings` files, one for each language (`en.w3strings`, `de.w3strings`…). Drop one onto the window or open it with **Open files**, and it opens in a table. A strings `.csv`, the kind the modding tools encode from, opens the same way.

![A mod's strings](strings-editor.jpg)

## The table

| Column | What it is |
|---|---|
| **ID** | the number the game looks the string up by. Mods use their own range: `211nnnn000` to `211nnnn999`, where `nnnn` is your mod's Nexus number. |
| **Key** | the name scripts use, as in `GetLocStringByKeyExt("panel_Mods")` |
| **Key hash** | all a `.w3strings` file keeps of a key; a `.csv` beside it gives the names back |
| **Text** | what the game shows |

Double-click a cell, or press {{F2}}, to change it. **Add** ({{Ctrl+N}}) adds a string with the next free ID, **Remove** ({{Del}}) takes the picked ones out, and the search ({{Ctrl+F}}) narrows the list.

A warning sign marks a problem (rest the mouse on it): an ID that isn't a number or is used twice has to be fixed before saving; an ID outside the mod range, or a shared key, saves but may clash in the game.

```ws
// show a string by its key, or by its ID
GetLocStringByKeyExt("panel_Mods");
GetLocStringById(2115940001);
```

## Saving

- **Save** ({{Ctrl+S}}) writes the `.w3strings` file in the format the game reads; **Save as** ({{Ctrl+Shift+S}}) somewhere else.
- **Copy to every language** writes the file as every language the game has (`ar`, `br`, `cz`, `de`, `en`, `es`, `esmx`, `fr`, `hu`, `it`, `jp`, `kr`, `pl`, `ru`, `zh`) beside it, so your text shows whatever language the game is in.
- **Language** is the language the file is saved as. **Export CSV** saves the strings as a `.csv` for a translator.

## Updating a translation

When your mod changes, its translation doesn't have to be done again. Open the new version's strings, then **Load translation** and pick the old translation (a `.csv` or `.w3strings`):

- each string takes its text from the old translation, matched by key (or ID)
- strings the old one hasn't got are **NEW**, shown first, with the **Original** text to translate from
- **Show** picks all strings, those still to translate, or those translated here

## Checking your IDs

**Check IDs** compares every ID with the game's, the DLC's and your other installed mods'. A clash is marked with where the ID is used and its text there. It also suggests a free block of 1,000 IDs. See [[Hashes and IDs]] for a Nexus mod's block.
