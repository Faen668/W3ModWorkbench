---
title: Recipes
icon: E81C
order: 5
summary: Save a Convert or Bulk edit form as a recipe, run it again in one click, or export it as a Python script or batch file.
keywords: recipe recipes saved save as recipe run again repeat automate python script batch file bat export copy command line import share synced folder w3recipe.json task scheduler notes steps
---
A recipe is a filled-in **Convert** or **Bulk edit** form, saved so you can run it again in one click: after you've tuned your look again, or on the next version of your mod.

![The Recipes tab, a recipe of edits after Check](cr2w-recipes.jpg)

## Saving a recipe

1. Fill in the **Convert** or **Bulk edit** tab as usual: files, edits, where to save.
2. Press **Save as recipe**. The **Recipes** tab opens with the new recipe chosen, named after what it does.
3. Type a better **Name**, and **Notes** to remind you what it's for. They're saved as you leave the box (or press {{Enter}} in **Name**).

## Running one

Pick a recipe in **Saved recipes** and press **Run** (or double-click it, or press {{Enter}}). For a recipe of edits, **Check** first tries them without saving anything. The results come up under it, as on the other tabs.

**Steps** shows what the recipe does:

- **What**: convert to JSON, back to CR2W, resave as 164, or edit.
- **Files**: the files and folders it works on (**+ subfolders** when it looks in them), and **Match** and **Skip** for its filter.
- **Save to**: a folder, **beside each file** or **over the files**.
- **Options**: such as **replace files that are there already** or **save older files as version 164**.
- **Edits**: each one in order, with its path, value and class.
- **Last run**: when, or **not run yet**.

A copy's file is read each time the recipe runs, so it always takes the value as you have it now. A file or folder that has gone since is listed in amber under the steps: **Not there any more: …**.

## Changing, sharing and deleting

- **Open in the form** puts the recipe back in its tab, to change it and **Save as recipe** again as a new one.
- **Delete** (or {{Delete}}) deletes it. {{F2}} renames it.
- **Import…**, or dropping `.w3recipe.json` files on the tab, adds recipes from somewhere else.
- **Open folder** shows where they're kept.

Recipes are kept in `%AppData%\W3ModWorkbench\recipes`, a `.w3recipe.json` file each. **Settings > General > CR2W and JSON recipes** can put them somewhere else, such as a synced folder to share them between computers.

A recipe file is plain JSON. This one, a little shortened, is the recipe in the picture above:

```json title="Night look for every env.w3recipe.json"
{
  "$format": "w3mw-cr2w-recipe",
  "name": "Night look for every env",
  "notes": "My tone mapping and thinner fog, for all the mod's environments.",
  "kind": "edit",
  "inputs": ["D:\\W3 Mods\\Night Sky\\envs"],
  "recursive": true,
  "output": "D:\\W3 Mods\\Night Sky\\envs edited",
  "edits": [
    { "kind": "copy", "path": "envParams.m_toneMapping", "className": "CEnvironmentDefinition", "source": "D:\\W3 Mods\\Night Sky\\looks\\my_look.env" },
    { "kind": "scale", "path": "envParams.m_globalFog.fogDensity", "amount": 0.8, "fields": ["value"] },
    { "kind": "set", "path": "envParams.m_globalFog.fogActivated", "value": true }
  ]
}
```

## Running a recipe without the app open

Three buttons make a recipe something that runs on its own:

- **Python script…** saves it as a `.py` file.
- **Batch file…** saves it as a `.bat` file, for running without Python or from Task Scheduler.
- **Copy command line** copies the command it stands for, to paste into a terminal or a script of your own.

Each runs the copy of the app it was made from, so keep the app where it is, or change the path at the top.

### The Python script

The files, folders and edits are at the top, to change. Run it from a terminal with `python night_look.py`, adding `--check` to try it first: anything after the script's name is passed on to the app.

```py title="night_look.py"
# Night look for every env: a CR2W and JSON recipe from W3 Mod Workbench
# My tone mapping and thinner fog, for all the mod's environments.
# Run it from a terminal. Add --check after it to try the edits without saving anything.

import subprocess
import sys

# The app that does the work: this copy of W3 Mod Workbench
WORKBENCH = r"C:\Tools\W3 Mod Workbench\W3ModWorkbench.exe"

# The files and folders to work on
INPUTS = [
    r"D:\W3 Mods\Night Sky\envs",
]

# The edits, made in order (W3ModWorkbench.exe --help edits lists them all)
EDITS = [
    ["--copy", r"D:\W3 Mods\Night Sky\looks\my_look.env", "envParams.m_toneMapping", "--only-class", "CEnvironmentDefinition"],
    ["--scale", "envParams.m_globalFog.fogDensity", "0.8", "--fields", "value"],
    ["--set", "envParams.m_globalFog.fogActivated", "true"],
]

# Where the results go, and how: --out <folder>, --recursive, --overwrite, --as-164, --match <regex>, --skip <regex>
OPTIONS = ["--out", r"D:\W3 Mods\Night Sky\envs edited", "--recursive"]


def main():
    args = [WORKBENCH, "--edit", *INPUTS]
    for edit in EDITS:
        args += edit
    args += OPTIONS
    args += sys.argv[1:]
    return subprocess.run(args).returncode


if __name__ == "__main__":
    sys.exit(main())
```

### The batch file

```cmd title="night_look.bat"
@echo off
rem Night look for every env: a CR2W and JSON recipe from W3 Mod Workbench
rem My tone mapping and thinner fog, for all the mod's environments.
rem Run it from a terminal. Add --check after it to try the edits without saving anything.
setlocal
set "WORKBENCH=C:\Tools\W3 Mod Workbench\W3ModWorkbench.exe"

for /f "tokens=2 delims=:." %%a in ('chcp') do set "CODEPAGE=%%a"
chcp 65001 >nul
start "" /wait "%WORKBENCH%" ^
  --edit ^
  "D:\W3 Mods\Night Sky\envs" ^
  --copy "D:\W3 Mods\Night Sky\looks\my_look.env" envParams.m_toneMapping --only-class CEnvironmentDefinition ^
  --scale envParams.m_globalFog.fogDensity 0.8 --fields value ^
  --set envParams.m_globalFog.fogActivated true ^
  --out "D:\W3 Mods\Night Sky\envs edited" --recursive %*
set "RESULT=%ERRORLEVEL%"
chcp %CODEPAGE% >nul
exit /b %RESULT%
```

It ends with the app's exit code, so another batch file can check it: 0 when every file was done.

### From the command line

`--recipe` runs a saved recipe file. `--check` tries a recipe of edits first, and `--match` and `--skip` take the place of its own filter:

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --recipe "looks\Night look for every env.w3recipe.json" --check

Night look for every env
would   D:\W3 Mods\Night Sky\envs edited\cutscene_definition_level.env  (copied envParams.m_toneMapping; set envParams.m_globalFog.fogActivated)
would   D:\W3 Mods\Night Sky\envs edited\cutscen_definition_global.env  (copied envParams.m_toneMapping; set envParams.m_globalFog.fogActivated)
would   D:\W3 Mods\Night Sky\envs edited\dashdot_env_nvg_global_23_07_2013_copy.env  (copied envParams.m_toneMapping; 6 numbers under envParams.m_globalFog.fogDensity multiplied; set envParams.m_globalFog.fogActivated)
would   D:\W3 Mods\Night Sky\envs edited\env_level_v09_tm.env  (copied envParams.m_toneMapping; 8 numbers under envParams.m_globalFog.fogDensity multiplied; set envParams.m_globalFog.fogActivated)
4 would be written, 0 skipped, 0 failed.

D:\W3 Mods\Night Sky>W3ModWorkbench.exe --recipe "looks\Night look for every env.w3recipe.json" --match "v09"

Night look for every env
ok      D:\W3 Mods\Night Sky\envs edited\env_level_v09_tm.env  (copied envParams.m_toneMapping; 8 numbers under envParams.m_globalFog.fogDensity multiplied; set envParams.m_globalFog.fogActivated)
1 written, 0 skipped, 0 failed.
```

See [[The command line]] for the rest.
