---
title: Opening files and folders
icon: E8E5
order: 3
summary: Open the game, a mod, a single bundle or a folder of scripts, and come back to them later.
keywords: open load drop drag recent bundle rsblob folder mods content dlc w3strings texture cache
---
There are three ways to get files in, and you can mix them. Everything you open stays listed in the tree until you close it.

## Open files

**Open files** ({{Ctrl+O}}) picks one or more files. Hold {{Ctrl}} or {{Shift}} in the file picker to choose several. What happens depends on the kind of file:

| File | Opens as |
|---|---|
| `.bundle` | an archive in the tree, its files listed |
| `.rsblob` (compiled scripts) | readable WitcherScript, one file per class. See [[Decompiled mods]]. |
| `texture.cache` | its textures, listed like a bundle's. See [[Textures]]. |
| `.w3strings` or a strings `.csv` | the strings editor. See [[Editing strings]]. |
| `.w3bench` | your project. See [[Projects]]. |
| `.ws` (a script) | the `scripts` folder it's in, to read in [[Script Bench]] |
| a mesh, entity, scene or other game file on disk | its viewer |

## Open folder

**Open folder** loads every bundle and compiled script inside a folder and all of its subfolders. Folders of `.w3strings` files are listed too, and a folder with a `.w3bench` file in it opens as that project. Good places to point it at:

- `The Witcher 3\content`: the whole base game (about 390,000 files, which take a second or two).
- `The Witcher 3\dlc`: the expansions and free DLC.
- `The Witcher 3\mods`: every mod you have installed, side by side.
- A single mod's folder, when you only care about that one.

> tip: For browsing the whole game, the [[The Asset Browser|Asset Browser]] ({{Ctrl+Shift+A}}) is quicker still: it opens the game, every DLC and every mod at once, with nothing to pick.

## Drag and drop

Drop files or folders anywhere on the main window and they open as if you'd picked them.

## Opening again later

- The **clock button** beside Open folder lists the files and folders you opened last, newest first. Click one to open it again. **Clear the list** empties it.
- The start screen (with nothing open) shows the latest few.
- Your project opens by itself when the app starts, with the scripts you had open. You can turn that off in [[Settings]].

## Closing

Right-click an entry in the tree and choose **Close bundle** (or **Close mod**), or press **Close all** in the toolbar.

> note: Opening never changes anything. Bundles, compiled scripts and texture caches are only read, so it's safe to open the game's own folders.
