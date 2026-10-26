---
title: Bulk edits
icon: E762
order: 4
summary: The Bulk edit tab: copy, set, remove, multiply or add to a value in every file you give it, checked first.
keywords: bulk edit tab copy value another file set remove default multiply scale add offset numbers path choose picker property class only objects fields curve check make the edits over the files into folder tone mapping fog
---
The **Bulk edit** tab makes a list of edits to every file you give it: give every environment in your mod the tone mapping of the one you've tuned, thin all their fog by a fifth, or switch a setting off in a hundred entities.

![The Bulk edit tab with three edits for a mod's environments](cr2w-bulk.jpg)

## Making edits

1. Open **Tools > CR2W and JSON** and the **Bulk edit** tab.
2. Under **Files to edit**, add CR2W files or `.json` files this tool made, or folders of them (drop them, or **Add files…** and **Add folder…**). Each is saved in the form it's in: a CR2W file stays CR2W, a `.json` file stays JSON.
3. Under **Edits**, choose the **Edit**, fill in its boxes and press **Add to the edits**. Add as many as you like; they're made in order. **Remove edit** takes off the highlighted one (or the last).
4. Choose where the edited files go: **Into a folder**, keeping the folders below what was added, or **Over the files**.
5. Press **Check** to see what would change, then **Make the edits**.

> warning: **Over the files** changes the files themselves, which can't be undone, so the app asks first. Keep a copy, or save into a folder. The game's own files are never changed: extract them first.

With only one edit, you needn't add it to the list: the one in the boxes is made.

## The kinds of edit

| Edit | What it does | Boxes |
|---|---|---|
| **Copy the value from another file** | Puts the value at the path in another file into every file | **From file**, **Path** |
| **Set a value** | Puts a value of your own at the path | **Path**, **Value** |
| **Remove (the game uses its default)** | Takes the property out, so the game uses its default | **Path** |
| **Multiply numbers** | Multiplies every number under the path | **Path**, **By**, **In properties** |
| **Add to numbers** | Adds to every number under the path (a minus number takes away) | **Path**, **By**, **In properties** |

- **Value** is JSON: `1.5`, `true`, `"text"`, or a whole object or list. Text that isn't JSON is taken as a string.
- **In properties** keeps a multiply or add to the numbers in those properties, at any depth below the path, comma separated. For a curve that's `value`, so its times and control points are left alone; for colours and vectors `X, Y, Z`. Empty changes every number.
- **Only objects of** makes the edit only to objects of that class, such as `CEnvironmentDefinition`. Empty edits every object that has the path.
- A copy or a set puts in a property the file doesn't have yet (and the groups on the way to it), so you can set something the file was leaving at its default. A multiply or add changes only numbers that are there.

## Paths

A path is property names from an object down, separated by dots, with `[n]` for an item of a list, counting from 0:

```
envParams.m_toneMapping
envParams.m_globalFog.fogActivated
envParams.m_globalFog.fogDensity.dataCurveValues[0].value
envParams.m_finalColorBalance.vignetteColor.dataCurveValues[1].controlPoint.X
```

A list item has to be there already: a path can add properties on its way, but not items to a list.

**Choose…** saves typing: it opens the properties of the file you're copying from (or the first file to edit), each object with its class, then its properties down, with a summary of each (a curve's points, a list's items, a value). Click one and its path goes in **Path**, with its class in **Only objects of**. **Close** or {{Esc}} puts it away.

![Choose…: an environment's properties, to pick a path from](cr2w-bulk-choose.jpg)

## Check first

**Check** makes the edits without saving anything, and lists each file with what would change:

![Check: what each of the four files would get](cr2w-bulk-check.jpg)

- **Would be saved: copied envParams.m_toneMapping; 6 numbers under envParams.m_globalFog.fogDensity multiplied**: the file gets those changes.
- **Nothing to change: no object has that path**: the file hasn't got it, so it's left out. With **Only objects of** set, objects of other classes don't count.
- A file whose edit can't be made says why, such as a value of the wrong type.

When it looks right, press **Make the edits**. Each result shows where the file was saved. Double-click one to show it in File Explorer.

## Examples

**Every environment gets the tone mapping of the one you've tuned:** Copy the value from another file, **From file** your tuned `.env`, **Path** `envParams.m_toneMapping`, **Only objects of** `CEnvironmentDefinition`.

**Thinner fog everywhere:** Multiply numbers, **Path** `envParams.m_globalFog.fogDensity`, **By** `0.8`, **In properties** `value`.

**Fog switched on, whatever the file had:** Set a value, **Path** `envParams.m_globalFog.fogActivated`, **Value** `true`.

**Back to the game's default motion blur:** Remove, **Path** `envParams.m_motionBlur`.

## Older environments from REDkit

Some of REDkit's older `.env` files keep a group as a class the game no longer has: their `envProbeBaseLightingAmbient` has `colorSkyMul` and `colorSkyAdd` where the game's has `colorSkyTop` and `colorSkyHorizon`. The game skips a group like that when it loads the file. When an edit puts the game's properties into one, the group is made the game's class, holding only what the edit puts in, so the game reads it.

## The same from a script

```cmd
W3ModWorkbench.exe --edit "D:\W3 Mods\Night Sky\envs" -r --copy "D:\W3 Mods\Night Sky\looks\my_look.env" envParams.m_toneMapping --only-class CEnvironmentDefinition --scale envParams.m_globalFog.fogDensity 0.8 --fields value --out "D:\W3 Mods\Night Sky\envs edited" --check
```

Drop `--check` to make the edits. Press **Save as recipe** to keep the form and run it again: see [[Recipes]]. [[The command line]] has every edit option.
