---
title: The CR2W JSON format
icon: E8A5
order: 8
summary: What's in a converted file: objects, values, references, types and the file block, and how to make a new file.
keywords: cr2w json format objects class references import export transform variant raw types file block names imports version new file from scratch add object comments nan infinity base64 $class $parent $flags $raw $data $tail $types $source
---
A converted file is the CR2W file's objects in order, then a `file` block with what's needed to put it back together. See [[CR2W files as JSON]] for converting.

## A whole file

The smallest layer in the game, `levels\prolog_village\asd.w2l`, is an empty `CLayer`. Its whole JSON:

```json title="asd.w2l.json"
{
  "$format": "w3mw-cr2w-json",
  "$formatVersion": 1,
  "objects": [
    { "$class": "CLayer", "$flags": 8192 }
  ],
  "file": {
    "version": 164,
    "flags": 0,
    "timestamp": 0,
    "build": 0,
    "chunks": 6,
    "names": ["", "CLayer"],
    "imports": [],
    "types": {}
  }
}
```

- `$format` and `$formatVersion` say it's a file this app made, and which layout of it.
- `objects` is the file's objects, in the order the file has them. The first is usually the resource itself: a `CLayer`, a `CEnvironmentDefinition`, a `CEntityTemplate`.
- `file` holds the CR2W file's own tables and layout (see below).
- `$source`, when it's there, is the game path the file was converted from. It's only a note.

A bigger example, an environment, shortened:

```json
{
  "$format": "w3mw-cr2w-json",
  "$formatVersion": 1,
  "objects": [
    {
      "$class": "CEnvironmentDefinition",
      "$flags": 8192,
      "envParams": {
        "m_toneMapping": {
          "activated": true,
          "luminanceLimitMax": {
            "dataCurveValues": [
              { "time": 0.5, "controlPoint": { "X": -0.1, "Y": 0, "Z": 0.1, "W": 0 }, "value": 1.2 }
            ]
          }
        }
      }
    }
  ],
  "file": { "version": 164, "names": [], "imports": [], "types": {} }
}
```

## Objects

Each object has its `$class`, then its properties by name. A property the file doesn't have isn't listed: the game uses its default. Add it to set it.

The keys starting with `$` keep how the object sits in the file. Leave them as they are:

| Key | Is |
|---|---|
| `$class` | the object's class |
| `$parent` | the object it belongs to, by its place in `objects` (counting from 0) |
| `$flags`, `$crc`, `$template` | the object's flags and checksum in the file |
| `$types` | the types of properties that differ from the file's usual ones |
| `$tail`, `$eventsBefore`, `$marker`, `$end`, `$pad` | bytes and markers around the properties, as base64 or true and false |
| `$data` | the whole object kept as its bytes |

## Values

| In the file | In the JSON |
|---|---|
| a number | a number: `1.5`, `-3`, `0` |
| a number JSON can't write | a string: `"NaN"`, `"Infinity"`, `"-Infinity"`. A NaN with its own bits keeps them: `"NaN:ffffffff"` |
| true or false | `true`, `false` |
| text, or a name (`CName`) | a string |
| an enum | its value's name: `"SCT_Color"` |
| a flags enum | a list of names |
| a struct | an object |
| an array | a list |
| a reference to another object in the file | `"export:N"`, by its place in `objects`, counting from 0 |
| a reference to another file, such as a texture | `"import:path"`. Point it at a new path and the file gains the import |
| no reference | `null` |
| a transform | `{ "position": {X, Y, Z}, "rotation": {Pitch, Yaw, Roll}, "scale": {X, Y, Z} }`, each part only when it isn't the default |
| a variant | `{ "type": "Float", "value": 1 }` |
| bytes | base64 text |
| a whole CR2W file inside another | base64 text starting `"Q1IyV…"`: open it with **Open embedded file** in the [[Editing a file as JSON\|JSON editor]], or `loads` in [[Python scripts]] |
| something the app can't read yet | `{"$raw": "base64"}`, kept as its bytes. Leave it as it is |

- A material's parameters are in `$parameters`; the events of a scene section, animation or cutscene in `$events`; a script block's arguments in `$arguments`; an appearance's attachment in `$attachment`.
- A negative zero is written `-0.0`, so it stays negative zero when another program reads and writes the file.
- The reader takes `//` and `/* */` comments, and a comma after the last item of an object or list.

## Types

A property's engine type isn't repeated on every value. `file.types` lists the type of each class's properties:

```json
"types": {
  "CEnvironmentDefinition.envParams": "CAreaEnvironmentParams",
  "CAreaEnvironmentParams.m_globalFog": "CEnvGlobalFogParameters",
  "CEnvGlobalFogParameters.fogActivated": "Bool",
  "SSimpleCurve.dataCurveValues": "array:147,0,SCurveDataEntry"
}
```

A property you add that the file never had is looked up in the engine's list of classes, which the app carries, so you needn't add its type.

## The file block

`names` and `imports` are the file's tables, in order. New ones are added on the end as the file is built, for the properties and references you add. The rest keeps the layout the file had, so it comes back byte for byte: `version`, `flags`, `timestamp`, `build`, `chunks`, `buffers`, `properties`, `trailer` and the like. Leave them as they are.

## Making a new file

A new file needs only its objects and the version. The names, imports and types are filled in as it's built:

```json title="new_look.env.json"
{
  "$format": "w3mw-cr2w-json",
  "$formatVersion": 1,
  "objects": [
    {
      "$class": "CEnvironmentDefinition",
      "envParams": {
        "m_globalFog": {
          "fogActivated": true,
          "fogDensity": {
            "dataCurveValues": [
              { "time": 0.5, "controlPoint": { "X": -0.1, "Y": 0, "Z": 0.1, "W": 0 }, "value": 1.2 }
            ]
          }
        }
      }
    }
  ],
  "file": { "version": 164 }
}
```

```cmd
W3ModWorkbench.exe --from-json new_look.env.json
```

For anything bigger than a few properties, start from a real file of the same kind instead: it has every property the game expects, and you change only what you need. [[Python scripts]] shows how to make many files from one template.

To add an object to a file, put it on the end of `objects` and point to it with `"export:N"` from the object that holds it. Copy a similar object from the same file, or from another file of the same kind, so it has what the game expects.

## Older files

REDkit's oldest files (version 154 and before) are in the Witcher 2's layout: types can have the Witcher 2's names (`@Float` is an array of floats, `*CEntity` a reference), objects can have `$name`, `$extra` and `$template`, and the file block has `dependencies`. **Resave as version 164** turns them into the current layout: see [[Converting many files]].
