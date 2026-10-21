---
title: Extracting files
icon: E896
order: 4
summary: Get copies of game files on disk: ticked files, a whole folder or a search's results, as they are or converted.
keywords: extract export save copy unpack tick check dds png json buffer folder structure
---
**Extracting** writes copies of files out of the game's archives into a folder of your choosing. The archives themselves are never changed.

## Quick steps

1. Tick the box beside each file you want. Ticks are kept as you move between folders and archives.
2. Set **Extract to** at the bottom: type a path or click the folder button.
3. Press **Extract checked** ({{Ctrl+E}}).

When it's done, the folder opens with the new files picked out.

## Ticking files quickly

- The box in the column header ticks or unticks everything listed (it shows a dash when only some are).
- Highlight rows (with {{Ctrl}} and {{Shift}} as usual) and press {{Space}} to tick or untick them all.
- Right-click a folder in the tree and choose **Check all in folder**.

The status bar shows how many files are ticked and how big they are.

## Other ways to extract

- **Extract shown**: everything in the list now. Handy after a search: search `*.redswf` and extract every Flash panel.
- Right-click files > **Extract highlighted** for a quick one-off.
- Right-click a folder in the tree > **Extract this folder**.

## The options

| Option | What it does |
|---|---|
| **Keep folder structure** | writes each file at its game path under your folder, which is what a mod needs. Off, everything goes in one folder (a second file of the same name gets ` (2)`). |
| **Include .buffer files** | a mesh keeps its geometry in `.buffer` files beside it (`x.w2mesh.1.buffer`…). With this on, they come along, and extracting a buffer brings its mesh. |
| **Textures as PNG** | textures come out as `name.xbm.png` pictures with their transparency, instead of `.dds` files with every mip level |
| **CR2W files as JSON** | game resources (`.env`, `.w2ent`, `.w2mi`…) come out as JSON to read and edit. See [[CR2W files as JSON]]. |
| **Open folder when done** | opens Explorer with the new files picked out |

> warning: Files already in the folder with the same name are replaced. Extract into an empty folder to be sure nothing of yours is overwritten.

## While it runs

Extracting runs in the background with a progress bar in the status bar; **Cancel** stops after the current file. Any file that fails (very rare) is listed at the end with why, and everything else is still written.

Right-click a file you've extracted and choose **Show extracted file** to find the copy later.

> tip: Want to change a file for your mod? Use **Copy to project** instead. It puts the copy at the right place in your project, ready to build. See [[Changing game files]].
