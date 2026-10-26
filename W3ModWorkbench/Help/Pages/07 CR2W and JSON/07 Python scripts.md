---
title: Python scripts
icon: E943
order: 7
summary: Read and write CR2W files as Python dictionaries, in memory, and make many files from a table or a database.
keywords: python script api subprocess json dict dictionary load save loads dumps stdin stdout standard input output memory database sql sqlite csv table generate template layer w2l entity streamingDataBuffer base64 automate w3cr2w
---
A converted CR2W file is JSON, and JSON is a Python dictionary. A Python script can run the app to turn a CR2W file into a dictionary, change it, and turn it back into a CR2W file, with nothing written to disk in between: the JSON goes through standard input and output (see [[The command line]]).

You need Python 3.7 or later, and the app. Nothing else to install: the scripts here use only what comes with Python.

## The helper

Save this as `w3cr2w.py` beside your scripts, and set `WORKBENCH` to where your copy of the app is:

```py title="w3cr2w.py"
"""Read and write the Witcher 3's CR2W files as Python dictionaries, through W3 Mod Workbench."""
import json
import pathlib
import subprocess

# where the app is: change this to your copy
WORKBENCH = r"C:\Tools\W3 Mod Workbench\W3ModWorkbench.exe"


class Cr2wError(Exception):
    """The app couldn't convert the file; the message says why."""


def _run(args, data=None):
    done = subprocess.run([WORKBENCH, *args], input=data, capture_output=True)
    if done.returncode != 0:
        raise Cr2wError(done.stderr.decode("utf-8", "replace").strip() or done.stdout.decode("utf-8", "replace").strip())
    return done.stdout


def load(path):
    """A CR2W file on disk, as a dictionary."""
    return json.loads(_run(["--to-json", str(path), "--out", "-"]))


def loads(data):
    """A CR2W file's bytes (from a bundle, a database, another file), as a dictionary."""
    return json.loads(_run(["--to-json", "-"], data))


def dumps(doc):
    """A dictionary back as a CR2W file's bytes."""
    return _run(["--from-json", "-"], json.dumps(doc, ensure_ascii=False).encode("utf-8"))


def save(doc, path):
    """Writes a dictionary as a CR2W file."""
    path = pathlib.Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(dumps(doc))
```

| Function | Gives |
|---|---|
| `load(path)` | a CR2W file on disk, as a dictionary |
| `loads(data)` | a CR2W file's bytes, as a dictionary |
| `dumps(doc)` | a dictionary as a CR2W file's bytes |
| `save(doc, path)` | writes a dictionary as a CR2W file, making its folder if need be |

Each raises `Cr2wError` with the app's reason when a file can't be converted. The dictionary is the file's JSON: `doc["objects"]` is the list of objects, each with its `$class`. [[The CR2W JSON format]] explains the rest.

## Reading a file

A world layer (`.w2l`) is a `CLayer` first, whose `entities` are references to the objects after it:

```py title="list_entities.py"
import w3cr2w

layer = w3cr2w.load(r"D:\W3 Mods\Night Sky\layers\fisherman_hut.w2l")
objects = layer["objects"]

# the layer is the first object; its entities are references to the others
for ref in objects[0]["entities"]:
    entity = objects[int(ref.split(":")[1])]
    pos = entity.get("transform", {}).get("position", {})
    print(f'{entity["$class"]:<14} {entity.get("guid", "")}  '
          f'X {pos.get("X", 0):9.2f}  Y {pos.get("Y", 0):9.2f}  Z {pos.get("Z", 0):7.2f}')
```

```cmd
D:\W3 Mods\Night Sky>python list_entities.py
CEntityGroup   7b2cff02-ea6e-4d84-a9c1-862b160a35f8  X    109.14  Y   -214.46  Z   -0.38
CEntity        da64ff60-773f-4b5a-84fe-5e1a26f66fc6  X    113.41  Y   -233.99  Z    0.21
```

> tip: A property the file leaves out isn't in the dictionary: the game uses its default. Read with `.get()` and a default, as above, rather than `[...]`.

## Changing a file and saving it

```py title="raise_entities.py"
import w3cr2w

layer = w3cr2w.load(r"D:\W3 Mods\Night Sky\layers\fisherman_hut.w2l")
objects = layer["objects"]

# raise every entity of the layer by 2 metres
for ref in objects[0]["entities"]:
    entity = objects[int(ref.split(":")[1])]
    position = entity.setdefault("transform", {}).setdefault("position", {"X": 0, "Y": 0, "Z": 0})
    position["Z"] = position.get("Z", 0) + 2

w3cr2w.save(layer, r"D:\W3 Mods\Night Sky\layers raised\fisherman_hut.w2l")
print("saved")
```

`save` checks the whole file as it builds it. A value of the wrong kind stops it with the object and property it's in, and nothing is written.

## Files inside files

Some values are a whole CR2W file inside another, as base64 text starting `"Q1IyV…"`. In a cooked layer, an entity keeps its components in `streamingDataBuffer` this way. `loads` reads one straight from its bytes:

```py title="components.py"
import base64
import w3cr2w

layer = w3cr2w.load(r"D:\W3 Mods\Night Sky\layers\fisherman_hut.w2l")

# an entity's components are a whole CR2W file inside it, in streamingDataBuffer
for entity in layer["objects"]:
    buffer = entity.get("streamingDataBuffer")
    if not buffer:
        continue
    inner = w3cr2w.loads(base64.b64decode(buffer))
    print(entity["$class"], entity["guid"])
    for component in inner["objects"]:
        print("   ", component["$class"], component.get("name", ""))
```

```cmd
D:\W3 Mods\Night Sky>python components.py
CEntity da64ff60-773f-4b5a-84fe-5e1a26f66fc6
    CSoundEmitterComponent CSoundEmitterComponent0
```

To change one, change the inner dictionary, then put it back with `dumps` and base64:

```py title="inner_edit.py"
import base64
import w3cr2w

layer = w3cr2w.load(r"D:\W3 Mods\Night Sky\layers\fisherman_hut.w2l")

# make every sound emitter in the layer reach twice as far
for entity in layer["objects"]:
    buffer = entity.get("streamingDataBuffer")
    if not buffer:
        continue
    inner = w3cr2w.loads(base64.b64decode(buffer))
    for component in inner["objects"]:
        if component["$class"] == "CSoundEmitterComponent" and "maxDistance" in component:
            component["maxDistance"] *= 2
    entity["streamingDataBuffer"] = base64.b64encode(w3cr2w.dumps(inner)).decode("ascii")

w3cr2w.save(layer, r"D:\W3 Mods\Night Sky\layers louder\fisherman_hut.w2l")
print("saved")
```

## Making many files from a table

Start from a real file as a template, copy it for each row of your data, change what the row says, and save each copy under its own name. The template keeps everything you don't change as the game has it.

```csv title="looks.csv"
name,fog_density,fog_on
misty_morning,1.6,true
clear_noon,0.4,true
no_fog,0,false
```

```py title="from_table.py"
import copy
import csv
import w3cr2w

template = w3cr2w.load(r"D:\W3 Mods\Night Sky\envs\env_level_v09_tm.env")

# one environment for each row of the table, made from the template
with open("looks.csv", newline="", encoding="utf-8") as table:
    for row in csv.DictReader(table):
        env = copy.deepcopy(template)
        fog = env["objects"][0]["envParams"]["m_globalFog"]
        fog["fogActivated"] = row["fog_on"] == "true"
        for point in fog["fogDensity"]["dataCurveValues"]:
            point["value"] = float(row["fog_density"])
        w3cr2w.save(env, rf'D:\W3 Mods\Night Sky\envs made\{row["name"]}.env')
        print("made", row["name"])
```

```cmd
D:\W3 Mods\Night Sky>python from_table.py
made misty_morning
made clear_noon
made no_fog
```

### From a database

The same from an SQL database: here SQLite, which comes with Python. Any database Python can read works the same way.

```py title="from_database.py"
import copy
import sqlite3
import w3cr2w

template = w3cr2w.load(r"D:\W3 Mods\Night Sky\envs\env_level_v09_tm.env")
db = sqlite3.connect("looks.db")

for name, density, fog_on in db.execute("SELECT name, fog_density, fog_on FROM looks"):
    env = copy.deepcopy(template)
    fog = env["objects"][0]["envParams"]["m_globalFog"]
    fog["fogActivated"] = bool(fog_on)
    for point in fog["fogDensity"]["dataCurveValues"]:
        point["value"] = density
    w3cr2w.save(env, rf"D:\W3 Mods\Night Sky\envs from db\{name}.env")
    print("made", name)
```

A database that holds whole CR2W files as blobs needs no files at all: `w3cr2w.loads(blob)` reads one, and `w3cr2w.dumps(doc)` gives the bytes to store back.

> tip: To find out which properties to fill in, convert a file of the kind you're making and look at its JSON in the [[Editing a file as JSON|JSON editor]]: its **Outline** lists the objects, and search finds a property. A file made in REDkit with one of each thing you need makes a good template.

## Many files quickly

Each call starts the app, which takes about a third of a second. That's fine for a few hundred files. For thousands, convert the whole folder in one go, work on the `.json` files, and convert the folder back: the app does each folder's files side by side, many at a time.

```py title="whole_folder.py"
import json
import pathlib
import subprocess
import w3cr2w

mod = pathlib.Path(r"D:\W3 Mods\Night Sky")
work = mod / "layers work"

def run(*args):
    done = subprocess.run([w3cr2w.WORKBENCH, *args], capture_output=True, text=True, encoding="utf-8")
    print(done.stdout.strip())
    if done.returncode != 0:
        raise SystemExit(done.returncode)

# 1. the whole folder to JSON, in one go
run("--to-json", str(mod / "layers"), "-r", "--out", str(work), "--overwrite")

# 2. change each file's JSON
for path in work.rglob("*.w2l.json"):
    layer = json.loads(path.read_text("utf-8"))
    for obj in layer["objects"]:
        position = obj.get("transform", {}).get("position")
        if position is not None and obj["$class"] in ("CEntity", "CEntityGroup"):
            position["X"] = position.get("X", 0) + 5
    path.write_text(json.dumps(layer, indent=2, ensure_ascii=False), "utf-8")

# 3. the whole folder back to CR2W, beside its JSON
run("--from-json", str(work), "-r", "--overwrite")
```

```cmd
D:\W3 Mods\Night Sky>python whole_folder.py
ok      D:\W3 Mods\Night Sky\layers work\assets.w2l.json
ok      D:\W3 Mods\Night Sky\layers work\fisherman_hut.w2l.json
2 written, 0 skipped, 0 failed.
ok      D:\W3 Mods\Night Sky\layers work\assets.w2l
ok      D:\W3 Mods\Night Sky\layers work\fisherman_hut.w2l
2 written, 0 skipped, 0 failed.
```

## When a file can't be converted

`Cr2wError` says why, as the app does:

```py title="errors.py"
import w3cr2w

try:
    w3cr2w.load(r"D:\W3 Mods\Night Sky\looks\notes.txt")
except w3cr2w.Cr2wError as e:
    print("couldn't read it:", e)

doc = w3cr2w.load(r"D:\W3 Mods\Night Sky\envs\env_level_v09_tm.env")
doc["objects"][0]["envParams"]["m_globalFog"]["fogActivated"] = "yes please"
try:
    w3cr2w.dumps(doc)
except w3cr2w.Cr2wError as e:
    print("couldn't write it:", e)
```

```cmd
D:\W3 Mods\Night Sky>python errors.py
couldn't read it: FAILED  D:\W3 Mods\Night Sky\looks\notes.txt: This isn't a CR2W resource file.
couldn't write it: FAILED  standard input: objects[0] (CEnvironmentDefinition): CEnvGlobalFogParameters.fogActivated should be a true or false, not "yes please".
```

## Good to know

- Keep the keys that start with `$` and the `file` block as they are: they're what puts the file back together byte for byte. Change the properties.
- Numbers come back as Python `int` or `float`, whichever the JSON has. A decimal property takes either; a whole-number property takes `2` or `2.0`, but not `2.5`.
- `json.dumps` with `ensure_ascii=False` keeps names and text in other languages readable. The app reads either.
- No window opens: the app does the work and closes.
- A recipe from the CR2W and JSON window can be exported as a ready-made Python script too: see [[Recipes]].
