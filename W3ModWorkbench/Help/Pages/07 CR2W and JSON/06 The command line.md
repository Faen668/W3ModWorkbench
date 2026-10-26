---
title: The command line
icon: E756
order: 6
summary: Every CR2W and JSON command and option, run from a terminal, a batch file or a script, with what each one prints.
keywords: command line cli terminal cmd command prompt powershell batch bat script --to-json --from-json --resave-164 --edit --recipe --help --out --recursive --overwrite --as-164 --class --check --match --skip --copy --set --remove --scale --offset --fields --only-class stdin stdout standard input output pipe redirect exit code errorlevel start wait headless automation
---
Everything the CR2W and JSON window does runs without a window too, from a terminal, a batch file or a script. The app is the same `W3ModWorkbench.exe`: given one of these commands, it does the work, prints what happened and closes, without opening a window.

```cmd
W3ModWorkbench.exe --to-json "D:\W3 Mods\Night Sky\envs" --out "D:\W3 Mods\Night Sky\envs json"
```

## Running it

The examples on this page are run from the mod's folder, `D:\W3 Mods\Night Sky`, with the app's folder on the `PATH`, so they can say just `W3ModWorkbench.exe`. Otherwise give its whole path in quotes: `"C:\Tools\W3 Mod Workbench\W3ModWorkbench.exe" --to-json …`.

The app is a windowed program, so how a terminal waits for it differs:

| From | It waits | What to do |
|---|---|---|
| A batch file (`.bat`), or `cmd /c` | Yes | Nothing |
| Command Prompt, typed by hand | No: the prompt comes back first, and what it prints follows | Put `start "" /wait` in front, for commands that don't use `-` |
| PowerShell | Only when its output goes somewhere | End the line with `\| Out-Host` |
| Python's `subprocess` | Yes | Nothing: see [[Python scripts]] |

```cmd
start "" /wait W3ModWorkbench.exe --to-json envs --out "envs json"
```

```ps
& "C:\Tools\W3 Mod Workbench\W3ModWorkbench.exe" --to-json envs --out "envs json" | Out-Host
```

> note: `start` doesn't pass `<` and `>` on to the app, so for standard input and output (below), use a batch file or Python.

## Commands

| Command | Does |
|---|---|
| `--to-json <files or folders…>` | Writes `name.env.json` beside each CR2W file, or under `--out` |
| `--from-json <files or folders…>` | Writes `name.env` back from each `name.env.json`, beside it or under `--out` |
| `--resave-164 <files or folders…>` | Saves older CR2W files as version 164, as the engine would after loading them: under `--out`, or over themselves with `--overwrite` |
| `--edit <files or folders…> <edits…>` | Makes edits to CR2W files or converted JSON files: over themselves, or under `--out` |
| `--recipe <recipe file>` | Runs a recipe saved in the CR2W and JSON window (a `.w3recipe.json` file) |
| `--help [topic]`, `-h`, `/?` | Prints the help, or one part of it |

Files given one by one are always taken. From a folder, `--to-json` and `--resave-164` take every CR2W file, `--from-json` takes the `.json` files the app made, and `--edit` takes both.

The app does voice lines, lip syncs and strings from the command line too: `--speech-unpack`, `--speech-pack`, `--wav-to-wem`, `--lipsync`, `--lipsync-from-audio`, `--learn-lipsync`, `--strings-to-csv` and `--csv-to-strings`. See [[Voice lines]] and [[Editing strings]], or `--help voice`.

## Options

| Option | Does |
|---|---|
| `--out <folder>` | Writes the results under this folder, keeping the folders they were in below what was given |
| `--recursive`, `-r` | Looks in the subfolders of the folders given |
| `--overwrite` | Replaces files that are there already. Without it they're left as they are |
| `--as-164` | With `--from-json`: writes JSON made from older files as version 164 |
| `--match <regex>` | Only the files in folders whose path matches |
| `--skip <regex>` | Not the files in folders whose path matches |
| `--check` | With `--edit` or `--recipe`: makes the edits and says what would change, saving nothing |
| `--class <name>` | With `--edit`: edits only objects of this class |
| `-` | With `--to-json` or `--from-json`: the file comes from standard input, or with `--out -` goes to standard output |

Options can come in any order after the command. A value with spaces goes in double quotes.

## Converting to JSON and back

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json envs --out "envs json"

ok      D:\W3 Mods\Night Sky\envs json\cutscene_definition_level.env.json
ok      D:\W3 Mods\Night Sky\envs json\cutscen_definition_global.env.json
ok      D:\W3 Mods\Night Sky\envs json\dashdot_env_nvg_global_23_07_2013_copy.env.json
ok      D:\W3 Mods\Night Sky\envs json\env_level_v09_tm.env.json
4 written, 0 skipped, 0 failed.
```

Run it again and the JSON files are there already, so nothing is replaced until you add `--overwrite`:

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json envs --out "envs json"

FAILED  D:\W3 Mods\Night Sky\envs\cutscene_definition_level.env: The JSON file is there already (turn on Overwrite to replace it).
FAILED  D:\W3 Mods\Night Sky\envs\cutscen_definition_global.env: The JSON file is there already (turn on Overwrite to replace it).
FAILED  D:\W3 Mods\Night Sky\envs\dashdot_env_nvg_global_23_07_2013_copy.env: The JSON file is there already (turn on Overwrite to replace it).
FAILED  D:\W3 Mods\Night Sky\envs\env_level_v09_tm.env: The JSON file is there already (turn on Overwrite to replace it).
0 written, 0 skipped, 4 failed.

D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json envs --out "envs json" --overwrite

ok      D:\W3 Mods\Night Sky\envs json\cutscene_definition_level.env.json
ok      D:\W3 Mods\Night Sky\envs json\cutscen_definition_global.env.json
ok      D:\W3 Mods\Night Sky\envs json\dashdot_env_nvg_global_23_07_2013_copy.env.json
ok      D:\W3 Mods\Night Sky\envs json\env_level_v09_tm.env.json
4 written, 0 skipped, 0 failed.
```

One file, beside itself:

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json layers\fisherman_hut.w2l

ok      D:\W3 Mods\Night Sky\layers\fisherman_hut.w2l.json
1 written, 0 skipped, 0 failed.
```

And back again. `fc /b` compares two files byte by byte: the round trip gives the same file.

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --from-json "envs json" --out "envs back"

ok      D:\W3 Mods\Night Sky\envs back\cutscene_definition_level.env
ok      D:\W3 Mods\Night Sky\envs back\cutscen_definition_global.env
ok      D:\W3 Mods\Night Sky\envs back\dashdot_env_nvg_global_23_07_2013_copy.env
ok      D:\W3 Mods\Night Sky\envs back\env_level_v09_tm.env
4 written, 0 skipped, 0 failed.

D:\W3 Mods\Night Sky>fc /b envs\env_level_v09_tm.env "envs back\env_level_v09_tm.env"
Comparing files ENVS\env_level_v09_tm.env and ENVS BACK\ENV_LEVEL_V09_TM.ENV
FC: no differences encountered
```

## Choosing files with --match and --skip

Each is a regular expression, tested on a file's path below the folder given, with `/` between folders (`quests/main_quests/q101.w2scene`). It matches anywhere in the path unless anchored with `^` or `$`, and ignores case. Files given one by one are always taken.

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json . -r --match "\.w2l$" --out "layers json"

ok      D:\W3 Mods\Night Sky\layers json\layers\assets.w2l.json
ok      D:\W3 Mods\Night Sky\layers json\layers\fisherman_hut.w2l.json
2 written, 0 skipped, 0 failed.
```

| Filter | Takes |
|---|---|
| `--match "\.w2scene$"` | only scenes |
| `--match ".*w2scene"` | the same, written the other way |
| `--match "^quests/"` | only what's in the quests folder |
| `--match "\.(env\|w2mi)$"` | environments and materials |
| `--skip "(^\|/)thumbnails/"` | everything but REDkit's thumbnails |

A pattern that isn't a regular expression stops before anything runs:

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json envs --out "envs json" --match "\.env("

"\.env(" isn't a regular expression: Invalid pattern '\.env(' at offset 6. Not enough )'s.
```

## Resaving older files as version 164

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --resave-164 redkit --out "redkit 164"

ok      D:\W3 Mods\Night Sky\redkit 164\camera_fly.env  (version 127 saved as 164; 41 changes the engine makes: CEnvFinalColorBalanceParameters.sharpenStrength isn't in the engine's CEnvFinalColorBalanceParameters any more, so it's left out, as the engine leaves it out. …)
ok      D:\W3 Mods\Night Sky\redkit 164\env_fairy_forest_qa.env  (version 127 saved as 164; 41 changes the engine makes: …)
2 written, 0 skipped, 0 failed.

D:\W3 Mods\Night Sky>W3ModWorkbench.exe --resave-164 "redkit 164"

skipped D:\W3 Mods\Night Sky\redkit 164\camera_fly.env: It's version 164 already.
skipped D:\W3 Mods\Night Sky\redkit 164\env_fairy_forest_qa.env: It's version 164 already.
0 written, 2 skipped, 0 failed.
```

`--from-json … --as-164` does the same while converting JSON back.

## Edits

`--edit` takes the files, then one or more edits, made in order. A path is property names from an object down, with `[n]` for an item of a list: see [[Bulk edits]].

| Edit | Does |
|---|---|
| `--copy <source file> <path>` | The source file's value at the path, into every file |
| `--set <path> <json>` | A value: `1.5`, `true`, `"text"`, or a JSON object or list. Text that isn't JSON is taken as a string |
| `--remove <path>` | Removes the property, so the game uses its default |
| `--scale <path> <factor>` | Multiplies the numbers under the path |
| `--offset <path> <amount>` | Adds to the numbers under the path |
| `--fields <a,b>` | After `--scale` or `--offset`: only the numbers in these properties (`value`, `X,Y,Z`) |
| `--only-class <name>` | After an edit: that edit only to objects of this class |

`--check` first, to see what would change:

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --edit envs --scale envParams.m_globalFog.fogDensity 0.8 --fields value --check

skipped D:\W3 Mods\Night Sky\envs\cutscene_definition_level.env: Nothing to change: no object has that path.
skipped D:\W3 Mods\Night Sky\envs\cutscen_definition_global.env: Nothing to change: no object has that path.
would   D:\W3 Mods\Night Sky\envs\dashdot_env_nvg_global_23_07_2013_copy.env  (6 numbers under envParams.m_globalFog.fogDensity multiplied)
would   D:\W3 Mods\Night Sky\envs\env_level_v09_tm.env  (8 numbers under envParams.m_globalFog.fogDensity multiplied)
2 would be written, 2 skipped, 0 failed.
```

Two edits at once, saved into a folder:

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --edit envs --copy looks\my_look.env envParams.m_toneMapping --set envParams.m_globalFog.fogActivated true --out "envs edited"

ok      D:\W3 Mods\Night Sky\envs edited\cutscene_definition_level.env  (copied envParams.m_toneMapping; set envParams.m_globalFog.fogActivated)
ok      D:\W3 Mods\Night Sky\envs edited\cutscen_definition_global.env  (copied envParams.m_toneMapping; set envParams.m_globalFog.fogActivated)
ok      D:\W3 Mods\Night Sky\envs edited\dashdot_env_nvg_global_23_07_2013_copy.env  (copied envParams.m_toneMapping; set envParams.m_globalFog.fogActivated)
ok      D:\W3 Mods\Night Sky\envs edited\env_level_v09_tm.env  (copied envParams.m_toneMapping; set envParams.m_globalFog.fogActivated)
4 written, 0 skipped, 0 failed.
```

Edits work on converted JSON files too, which stay JSON:

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --edit "envs json" --offset envParams.m_globalFog.fogDensity 0.05 --fields value --check

skipped D:\W3 Mods\Night Sky\envs json\cutscene_definition_level.env.json: Nothing to change: no object has that path.
skipped D:\W3 Mods\Night Sky\envs json\cutscen_definition_global.env.json: Nothing to change: no object has that path.
would   D:\W3 Mods\Night Sky\envs json\dashdot_env_nvg_global_23_07_2013_copy.env.json  (6 numbers under envParams.m_globalFog.fogDensity added to)
would   D:\W3 Mods\Night Sky\envs json\env_level_v09_tm.env.json  (8 numbers under envParams.m_globalFog.fogDensity added to)
2 would be written, 2 skipped, 0 failed.
```

> warning: Without `--out`, edits are saved over the files themselves, and there's no undo. Try `--check` first, or keep a copy.

## Standard input and output

`--to-json` and `--from-json` can take one file through standard input and output instead of files on disk, so a script can keep it in memory: read a file out of a database, convert it, change the JSON and write it back, with nothing saved in between.

| Write | To |
|---|---|
| `-` as the file | read it from standard input |
| `--out -` | write the result to standard output. With `-` as the file, that's where it goes anyway |
| `--out <file>` | with `-` as the file: the file to write (`--overwrite` to replace it) |

What happened goes to the error output, so standard output holds only the file: JSON from `--to-json`, the CR2W file's bytes from `--from-json`. In a batch file:

```cmd title="round_trip.bat"
W3ModWorkbench.exe --to-json layers\fisherman_hut.w2l --out - > hut.w2l.json
W3ModWorkbench.exe --from-json - --out hut_copy.w2l < hut.w2l.json
fc /b layers\fisherman_hut.w2l hut_copy.w2l
```

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json layers\fisherman_hut.w2l --out - > hut.w2l.json
ok      standard output

D:\W3 Mods\Night Sky>W3ModWorkbench.exe --from-json - --out hut_copy.w2l < hut.w2l.json

ok      D:\W3 Mods\Night Sky\hut_copy.w2l

D:\W3 Mods\Night Sky>fc /b layers\fisherman_hut.w2l hut_copy.w2l
Comparing files LAYERS\fisherman_hut.w2l and HUT_COPY.W2L
FC: no differences encountered
```

Both ends at once, and the JSON straight into another program, here `find` counting the lines that name an entity:

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json - < layers\fisherman_hut.w2l > hut2.w2l.json
ok      standard output

D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json - < layers\fisherman_hut.w2l | find /c "CEntity"
13
```

- Standard input and output take one file at a time; `--match`, `--skip` and `--recursive` are for folders.
- Windows PowerShell 5 turns what goes through its pipes into text, which breaks a CR2W file's bytes. Use `<` and `>` in a batch file, or Python.
- With nothing sent in, `-` stops with **Nothing is coming in on standard input**, rather than waiting.

[[Python scripts]] uses this to read and write CR2W files as Python dictionaries.

## What it prints

Each file gets a line, then the totals:

| Line | Means |
|---|---|
| `ok      <file written>` | done, with a note in brackets if there's something to know |
| `would   <file>` | with `--check`: what would be written, and the changes |
| `skipped <file>: <why>` | left as it is: nothing to change, or version 164 already |
| `FAILED  <file>: <why>` | not done: it was there already, isn't a CR2W file, or the edit couldn't be made |
| `4 written, 0 skipped, 0 failed.` | the totals |

In a terminal, a line that keeps saying how far it is shows while it works, and is cleared for the results. A script reading the output gets only the results.

## Exit codes

| Code | Means |
|---|---|
| `0` | every file was done (or skipped with nothing to do) |
| `1` | some files failed, or none were found |
| `2` | the command can't run: an option is wrong or missing, or a pattern isn't a regular expression |

In a batch file, `if errorlevel 1` is true for 1 and 2:

```cmd
W3ModWorkbench.exe --from-json "envs json" --out envs --overwrite
if errorlevel 1 (
  echo Some files didn't convert back.
  exit /b 1
)
```

## When something's wrong

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json looks\missing.env

No CR2W files were found there.

D:\W3 Mods\Night Sky>W3ModWorkbench.exe --edit envs --scale envParams.m_globalFog.fogDensity

--scale needs a value after it.

D:\W3 Mods\Night Sky>W3ModWorkbench.exe --to-json - < hut.w2l.json
FAILED  standard input: This isn't a CR2W resource file.
```

## Help

`--help` prints everything. `--help` and a topic prints one part: `commands`, `options`, `filter`, `edits`, `recipes`, `pipes`, `voice` or `examples`.

```cmd
D:\W3 Mods\Night Sky>W3ModWorkbench.exe --help pipes

Standard input and output, for a script that keeps the file in memory (Python's subprocess)
  --to-json and --from-json convert one file this way:
    - as the file        Read it from standard input
    --out -              Write the result to standard output; with - as the file, that's the default
    --out <file>         With - as the file: the file to write (--overwrite to replace it)
  What happened goes to the error output, so standard output holds only the file. The exit code is 0
  when it was converted and 1 when it couldn't be; the error output says why.
    W3ModWorkbench.exe --to-json "D:\mymod\envs\night.env" --out -
    W3ModWorkbench.exe --to-json - < night.env > night.env.json
    W3ModWorkbench.exe --from-json - --out "D:\mymod\envs\night.env" < night.env.json
```
