---
title: Editing a file as JSON
icon: E70F
order: 2
summary: Open one CR2W file as JSON, change it in the editor, and save it back as CR2W.
keywords: open as json editor outline search format save cr2w embedded buffer base64 Q1IyV streamingDataBuffer valid converts back line column comments
windows: JsonWindow
---
The JSON editor shows one CR2W file as JSON. Change what you like and press **Save**: it's written back as CR2W, ready for the game.

![The JSON editor with an environment open and its outline on the right](json-editor.jpg)

## Opening a file

- Right-click a CR2W file in the main window's list (one of the game's, an installed mod's or your own) and choose **Open as JSON**. The **Properties** window and the Asset Browser's right-click menu have **Open as JSON** too.
- Right-click a `.json` file you converted earlier and choose **Open in JSON editor**.

The header shows the file's name, where it came from, and what **Save** will do with it. A `•` before the name means there are changes that haven't been saved.

## Moving around

| Press | To |
|---|---|
| {{Ctrl+Shift+O}} or **Outline** | Show the outline: every object with its `$class`, and its lists. Click one to go there |
| {{Ctrl+F}} | Search. **Aa** matches case, **ab** whole words only, **.\*** a regular expression |
| {{F3}} and {{Shift+F3}} | Next and previous match |
| {{Ctrl+Shift+F}} or **Format** | Indent the JSON again, keeping small objects and lists on one line |
| {{Esc}} | Close the search bar |

The editor colours the JSON and folds objects and lists: click the small box beside a line to fold or unfold it.

> note: JSON has no comments of its own. The editor reads `//` and `/* */` comments, and the converter skips them, but **Format** leaves them out. Press {{Ctrl+Z}} to get them back.

## The status bar

The bar at the bottom checks the JSON as you type:

- **Valid JSON · 13,287 lines · CEnvironmentDefinition · converts back to CR2W (215.9 KB)**: all's well, and Save will work. It names the file's classes and the size it'll be.
- **Line 12, column 9: …**: the JSON itself has a mistake, such as a missing comma or quote.
- **Won't convert back to CR2W: …**: the JSON is valid, but a value isn't right for its property, such as text where a number goes. It says which object and property.

Click the bar to put the cursor on the mistake. The line and column of the cursor are on its right.

![A value that won't convert back, shown in the status bar](json-editor-error.jpg)

## Saving

**Save** ({{Ctrl+S}}) does what suits the file:

| You opened | Save writes |
|---|---|
| One of your own CR2W files, with **Open as JSON** | the CR2W file, over itself |
| One of the game's files, or an installed mod's | a copy into your project at its game path, so your mod uses it in place of the original. You're asked first, and can choose **Save as…** instead |
| A `.json` file on disk | the `.json` file |

- **Save as CR2W** shows for a converted `.json` file: it writes `name.env` from `name.env.json` beside it, asking before it replaces one.
- **Save as** ({{Ctrl+Shift+S}}) saves the JSON somewhere else.
- When the JSON won't convert back, the CR2W file isn't saved, and a message says why. Your text stays in the editor to fix.

> tip: The game's own files and bundles are never changed. Saving one always makes a copy in your project.

## Files inside files

Some values are a whole CR2W file stored inside another, as base64 text starting `"Q1IyV…"`. An entity in a world layer (`.w2l`) keeps its components in `streamingDataBuffer` like this, and some resources keep thumbnails or cooked data the same way.

1. Put the cursor anywhere in the value.
2. Press **Open embedded file** ({{Ctrl+E}}). It opens as JSON in a window of its own, headed **Embedded in…**.
3. Change it and press **Save**. That writes it back into the first window, which then has unsaved changes.
4. Save the first window to keep the change.

![A layer entity's components, opened from its streamingDataBuffer](json-editor-embedded.jpg)

If you close the first window first, the embedded one can't go back: **Save** then saves it as a file of its own.

## Values kept as bytes

A few values the app can't read yet are kept as their bytes: `{"$raw": "…"}`, or `$data` for a whole object. When a file has some, a note says so as it opens. Leave them as they are: they're written back unchanged, so the file still works. [[The CR2W JSON format]] explains the rest of what's in the file.

## Example: turning off an environment's fog

1. Find the environment in the main window, for example `environment\definitions\env_novigrad\env_novigrad_global\env_novigrad_city.env`, right-click it and choose **Open as JSON**.
2. Press {{Ctrl+F}} and search for `m_globalFog`.
3. This file has no `fogActivated` line: a property a file leaves out uses the game's default, which is on. Add it as the first line inside `m_globalFog`, as below.
4. The status bar should still say **converts back to CR2W**. Press {{Ctrl+S}} and choose **Save to project**.

```json
"m_globalFog": {
  "fogActivated": false,
  "fogAppearDistance": {
    "ScalarEditScale": 100,
    "ScalarEditOrigin": 11.877158,
    …
```

To make the same change to every environment in your mod, see [[Bulk edits]].
