---
title: The CR2W JSON format
icon: E943
order: 5
summary: What's in a converted file: objects, values, types and the file block.
keywords: cr2w json format objects class references import export transform variant raw types
---
A converted file is the CR2W file's objects in order, then a `file` block with what's needed to put it back together. See [[CR2W files as JSON]] for converting.

```json
{
  "$format": "w3mw-cr2w-json",
  "$formatVersion": 1,
  "objects": [
    {
      "$class": "CEnvironmentDefinition",
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

## Values

- Each object has its `$class`, then its properties by name. A property the file doesn't have isn't listed: the game uses its default. Add it to set it.
- Structs are objects, arrays are lists. Numbers, `true`/`false` and strings are what they look like; a name (`CName`) is a string.
- An enum is its value's name (`"SCT_Color"`); a flags enum is a list of names.
- A reference is `null`, `"export:N"` for another object in the file (counting from 0), or `"import:path"` for another file, such as a texture. Point it at a new path and the file gains the import.
- A transform is `{ "position": {X, Y, Z}, "rotation": {Pitch, Yaw, Roll}, "scale": {X, Y, Z} }`. A variant is `{ "type": "Float", "value": 1 }`. Byte arrays are base64.
- A material's parameters are in `$parameters`; the events of a scene section, animation or cutscene in `$events`; a script block's arguments in `$arguments`.
- `{"$raw": "base64"}` is a value kept as its bytes, and `$data` a whole object kept as bytes. Leave them as they are.

## Types

A property's engine type isn't repeated on every value. `file.types` lists the type of each class's properties (`"SSimpleCurve.ScalarEditScale": "Float"`). A property you add that the file never had is looked up in the engine's list of classes, which the app carries.

## The file block

`names` and `imports` are the file's tables, in order; new ones go on the end. The rest (`version`, `flags`, `buffers`, `trailer`, and `$flags`, `$parent` and `$crc` on objects) keeps the layout the file had, so it comes back byte for byte. Leave them as they are.

## Older files

REDkit's oldest files (version 154 and before) are in the Witcher 2's layout: types can have the Witcher 2's names (`@Float` is an array of floats, `*CEntity` a reference), and objects can have `$name`, `$extra` and `$template`. **Resave as version 164** turns them into the current layout.
