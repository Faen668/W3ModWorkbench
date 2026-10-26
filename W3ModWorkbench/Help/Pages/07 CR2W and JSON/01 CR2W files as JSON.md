---
title: CR2W files as JSON
icon: E943
order: 1
summary: Turn the game's resource files into JSON to read or edit, back again byte for byte, and change hundreds at once.
keywords: cr2w json convert env w2mi w2ent w2l layer bulk edit command line python script resave 164 recipe automate start overview what is
windows: Cr2wJsonWindow
---
Most of the game's resources are **CR2W** files: environments (`.env`), materials (`.w2mi`), entities (`.w2ent`), world layers (`.w2l`), quests, scenes and the rest. They're binary, so a text editor or a script can't read them. The app turns them into **JSON**, plain text you can read, edit by hand or change with a script, and turns the JSON back into the same file, byte for byte.

![The CR2W and JSON window, ready to convert a mod's environments and layers](cr2w-json.jpg)

## Ways to use it

| To… | Use | Page |
|---|---|---|
| Read or change one file | Right-click it, **Open as JSON** | [[Editing a file as JSON]] |
| Turn a folder of files into JSON, or JSON back | **Tools > CR2W and JSON**, **Convert** tab | [[Converting many files]] |
| Make the same change to many files | **Tools > CR2W and JSON**, **Bulk edit** tab | [[Bulk edits]] |
| Do the same job again later | **Save as recipe**, then the **Recipes** tab | [[Recipes]] |
| Run any of it from a batch file or a terminal | `W3ModWorkbench.exe --to-json …` | [[The command line]] |
| Work on files in your own Python script | `subprocess`, with the JSON on standard output | [[Python scripts]] |
| Know what's in a converted file | | [[The CR2W JSON format]] |

Two more places convert as they go:

- **Extract** has **CR2W files as JSON**: resources come out of the game's bundles as JSON. See [[Extracting files]].
- Right-click a folder of your own in the main window's tree: **Convert to JSON** and **Convert JSON back** do every file in it and its subfolders. See [[Converting many files]].

## Your first conversion

1. Open **Tools > CR2W and JSON**.
2. Drop a folder of `.env` files on the window (or press **Add folder…**).
3. Leave **CR2W to JSON** chosen and press **Convert**. Each `name.env` gets a `name.env.json` beside it.
4. Open one of the `.json` files in any editor and change a value, say `"fogActivated": true` to `false`.
5. Choose **JSON back to CR2W**, tick **Replace files that are there already** and press **Convert** again. Each `name.env.json` is written back over its `name.env`.

The same from a terminal:

```cmd
W3ModWorkbench.exe --to-json "D:\W3 Mods\Night Sky\envs"
W3ModWorkbench.exe --from-json "D:\W3 Mods\Night Sky\envs" --overwrite
```

## What keeps your files safe

- Every file is checked as it's converted: its JSON must turn back into exactly the same bytes, or it isn't written.
- The game's own files are never changed. Files opened from the game are saved into your project, at their game path, so your mod uses them in place of the game's.
- Nothing is replaced without asking. A file that's there already is left alone unless you tick **Replace files that are there already** (`--overwrite` on the command line).
- **Check** (`--check`) tries edits and lists what they'd change, saving nothing.

> tip: Press {{F1}} in the CR2W and JSON window or the JSON editor to come back to these pages.

:::cards
- [[Editing a file as JSON]] | E70F | One file in the JSON editor: outline, search, embedded files.
- [[Converting many files]] | E8B7 | Folders to JSON and back, filters, and resaving as version 164.
- [[Bulk edits]] | E762 | Copy, set, remove, multiply or add to a value in every file.
- [[Recipes]] | E81C | Save a form to run again, or export it as a script.
- [[The command line]] | E756 | Every command and option, with what each one prints.
- [[Python scripts]] | E943 | CR2W files as Python dictionaries, in memory.
- [[The CR2W JSON format]] | E8A5 | What every part of a converted file means.
:::
