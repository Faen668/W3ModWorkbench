---
title: CR2W files as JSON
icon: E943
order: 17
summary: Turn game resources into JSON to read or edit, back again byte for byte, and change hundreds at once.
keywords: cr2w json convert env w2mi w2ent bulk edit command line python resave 164 base64 embedded
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

![The converter](cr2w-json.jpg)

- **CR2W to JSON** writes `name.env.json` beside each `name.env`.
- **JSON back to CR2W** writes `name.env` from each `name.env.json`.
- **Resave as version 164** saves REDkit's older source files in the format the engine saves today, which other tools read more easily.

Save **beside each file** or **into a folder**. Every file is checked as it's converted: its JSON must turn back into exactly the same bytes, or it isn't written.

## Changing many files at once

**Bulk edit** makes a list of edits to every file you add:

- **Copy the value from another file**: give every `.env` the tone mapping of the one you've tuned.
- **Set a value**, **Remove** it (the game then uses its default), **Multiply numbers** or **Add to numbers**.

A path is property names separated by dots, with `[n]` for an item of a list: `envParams.m_globalFog.fogDensity.dataCurveValues[0].value`. **Choose…** picks from a file's properties. **Check** tries the edits without saving; **Make the edits** saves.

## From a script

The same work runs without a window, so a batch file or a Python script can use it:

```cmd
W3ModWorkbench.exe --to-json "D:\mymod\envs" -r --out "D:\mymod\envs json"
W3ModWorkbench.exe --from-json "D:\mymod\envs json" -r --out "D:\mymod\envs"
W3ModWorkbench.exe --edit "D:\mymod\envs" -r --copy "D:\my_look.env" envParams.m_toneMapping
W3ModWorkbench.exe --edit "D:\mymod\envs" -r --scale envParams.m_globalFog 0.8 --fields value
```

`--help-json` lists every option. In a batch file use `start /wait "" W3ModWorkbench.exe …` so it waits for the app to finish.

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
