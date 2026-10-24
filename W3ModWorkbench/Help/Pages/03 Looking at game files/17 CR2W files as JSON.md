---
title: CR2W files as JSON
icon: E943
order: 18
summary: Turn game resources into JSON to read or edit, back again byte for byte, and change hundreds at once.
keywords: cr2w json convert env w2mi w2ent bulk edit command line python resave 164 base64 embedded recipe recipes saved script batch bat automate again regex regular expression filter pattern match skip only extension help progress
windows: Cr2wJsonWindow, JsonWindow
---
Most of the game's resources are **CR2W** files: environments (`.env`), materials (`.w2mi`), entities (`.w2ent`), quests, scenes and the rest. The app turns them into **JSON** you can read, edit by hand or change with a script, and turns the JSON back into the same file, byte for byte.

## Editing one file

Right-click a CR2W file in the list and choose **Open as JSON** to edit it straight from the game. **Save** writes it back as CR2W: into your project at its game path for one of the game's files, or over the file for one of your own.

The JSON editor colours and folds as you type, has an **Outline** ({{Ctrl+Shift+O}}) of the objects (each with its `$class`), search ({{Ctrl+F}}) and **Format** ({{Ctrl+Shift+F}}). The bar at the bottom says whether the JSON is valid, and whether it still converts back to CR2W; click it to go to a mistake.

```json
{
  "$format": "w3mw-cr2w-json",
  "objects": [
    {
      "$class": "CEnvironmentDefinition",
      "envParams": {
        "m_toneMapping": { "activated": true }
      }
    }
  ]
}
```

> tip: Some values are whole CR2W files inside another, as base64 starting `"Q1IyV…"`. Put the cursor on one and press **Open embedded file** ({{Ctrl+E}}) to edit it in a window of its own; saving there puts it back.

## Converting many files

**Tools > CR2W and JSON** opens the converter. Add files or folders (or drop them on it) and choose a direction:

![The converter, taking a folder's .env files and leaving out its backup folder](cr2w-json.jpg)

- **CR2W to JSON** writes `name.env.json` beside each `name.env`.
- **JSON back to CR2W** writes `name.env` from each `name.env.json`.
- **Resave as version 164** saves REDkit's older source files in the format the engine saves today, which other tools read more easily.

For a whole folder of your own, right-click it in the main window's tree: **Convert to JSON** and **Convert JSON back** do the same for every file in it and its subfolders, beside each one, keeping the originals. Files already there are asked about once: replace them, or leave them as they are. It's offered on your project's folders and on folders you opened from disk, not on the game's or installed mods'.

Save **beside each file** or **into a folder**. Every file is checked as it's converted: its JSON must turn back into exactly the same bytes, or it isn't written.

A big folder (REDkit's `r4data` has over 220,000 files) shows its progress as it goes: the files found, then each one checked, then the work. **Stop** works at every step.

### Only some of a folder's files

**Only files matching** and **Skip files matching** take a folder's files by their path below it, on both the **Convert** and **Bulk edit** tabs. Each is a regular expression, with `/` between folders and capital letters ignored. Files you add one by one are always taken.

- `\.w2scene$` (or `.*w2scene`): only scenes.
- `\.(env|w2mi)$`: environments and materials.
- `^quests/`: only what's in the `quests` folder.
- `(^|/)thumbnails/` in **Skip**: everything but REDkit's thumbnails.

> tip: A narrow pattern on a big folder is quick too: files it leaves out are never opened.

## Changing many files at once

**Bulk edit** makes a list of edits to every file you add:

- **Copy the value from another file**: give every `.env` the tone mapping of the one you've tuned.
- **Set a value**, **Remove** it (the game then uses its default), **Multiply numbers** or **Add to numbers**.

A path is property names separated by dots, with `[n]` for an item of a list: `envParams.m_globalFog.fogDensity.dataCurveValues[0].value`. **Choose…** picks from a file's properties. **Check** tries the edits without saving; **Make the edits** saves.

Some of REDkit's older `.env` files keep a group as a class the game no longer has: their `envProbeBaseLightingAmbient` has `colorSkyMul` and `colorSkyAdd` where the game's has `colorSkyTop` and `colorSkyHorizon`. The game skips a group like that when it loads the file. When an edit puts the game's properties into one, the group is made the game's class, holding only what the edit puts in, so the game reads it.

## Recipes: the same job again later

A recipe is a filled-in **Convert** or **Bulk edit** form, saved so you can run it again in one click: after you've tuned your look again, or on the next version of your mod.

1. Fill in the form as usual, then press **Save as recipe**.
2. The **Recipes** tab opens with it chosen. Type a better **Name**, and **Notes** to remind you what it's for.
3. Press **Run** (or double-click it in the list). For a recipe of edits, **Check** first tries them without saving anything.

![The Recipes tab, after Check](cr2w-recipes.jpg)

**Steps** shows what the recipe does: the files and folders it works on, where it saves, and each edit with its path. A copy's file is read each time the recipe runs, so it always takes the value as you have it now. A file or folder that has gone since is listed in amber under the steps.

- **Open in the form** puts the recipe back in its tab, to change it and save a new one.
- **Python script…** and **Batch file…** save the recipe as a script that runs it without opening the app (see below).
- **Copy command line** copies the command the recipe stands for.
- **Import…** (or dropping `.w3recipe.json` files on the tab) adds recipes from somewhere else. **Open folder** shows where they're kept.

Recipes are kept in `%AppData%\W3ModWorkbench\recipes`, a `.w3recipe.json` file each. **Settings > General > CR2W and JSON recipes** can put them somewhere else, such as a synced folder to share them between computers.

> tip: Press {{F2}} in the list to rename a recipe, {{Delete}} to delete it, and {{Enter}} to run it.

## From a script

The same work runs without a window, so a batch file or a Python script can use it:

```cmd
W3ModWorkbench.exe --to-json "D:\mymod\envs" -r --out "D:\mymod\envs json"
W3ModWorkbench.exe --from-json "D:\mymod\envs json" -r --out "D:\mymod\envs"
W3ModWorkbench.exe --edit "D:\mymod\envs" -r --copy "D:\my_look.env" envParams.m_toneMapping
W3ModWorkbench.exe --edit "D:\mymod\envs" -r --scale envParams.m_globalFog 0.8 --fields value
```

`--help` (or `-h`, `/?`) lists every command and option; `--help filter`, `--help edits`, `--help recipes` and the like show one part. In a terminal it shows its progress as it goes. In a batch file use `start /wait "" W3ModWorkbench.exe …` so it waits for the app to finish.

- `--match <regex>` and `--skip <regex>` are the file filter: `--match "\.w2scene$"` converts only scenes.
- `--check` with `--edit` tries the edits and says what would change, saving nothing.
- `--only-class <name>` after an edit makes that edit only to objects of that class (`--class` before the edits sets it for all of them).
- `--recipe <file>` runs a saved recipe, `--check` too.

```cmd
W3ModWorkbench.exe --recipe "%AppData%\W3ModWorkbench\recipes\Night look for every env.w3recipe.json" --check
```

### A recipe as a script

**Python script…** on the Recipes tab writes a script like this one. The files, folders and edits are at the top, to change; it runs the copy of the app it was saved from. Run it from a terminal with `python night_look.py`, adding `--check` to try it first.

```py title="night_look.py"
WORKBENCH = r"C:\Tools\W3 Mod Workbench\W3ModWorkbench.exe"

INPUTS = [
    r"D:\W3 Mods\Night Sky\envs",
]

EDITS = [
    ["--copy", r"D:\W3 Mods\Night Sky\looks\my_look.env", "envParams.m_toneMapping", "--only-class", "CEnvironmentDefinition"],
    ["--scale", "envParams.m_globalFog.fogDensity", "0.8", "--fields", "value"],
    ["--set", "envParams.m_globalFog.fogActivated", "true"],
]

OPTIONS = ["--out", r"D:\W3 Mods\Night Sky\envs edited", "--recursive"]
```

**Batch file…** writes the same as a `.bat`, for running without Python or from Task Scheduler.

```py
import json, pathlib
look = json.loads(pathlib.Path("my_look.env.json").read_text("utf-8"))
tone = look["objects"][0]["envParams"]["m_toneMapping"]
for f in pathlib.Path("envs json").rglob("*.env.json"):
    env = json.loads(f.read_text("utf-8"))
    env["objects"][0]["envParams"]["m_toneMapping"] = tone
    f.write_text(json.dumps(env, indent=2, ensure_ascii=False), "utf-8")
```

[[The CR2W JSON format]] explains everything in the files.
