---
title: Finding files
icon: E721
order: 1
summary: Search, sort and narrow the file list, see files as thumbnails, and everything on the right-click menu.
keywords: search filter wildcard thumbnails preview pane sort columns right-click copy path compare class file type
---
The game has hundreds of thousands of files. This page is about finding the few you want, in the main window's file list. The [[The Asset Browser|Asset Browser]] works the same way.

## The folder tree

Pick a folder in the tree and the list shows everything in it, subfolders included. Pick the top entry of an archive to list all of it, or pick nothing to list everything you have open.

## Searching

Type in **Search files** ({{Ctrl+F}}) to narrow the list. The search covers the folder picked in the tree.

![Searching for the griffin's textures: gryphon *.xbm](main-search.jpg)

- **Every word must be somewhere in the file's path**, in any order. `worldmap redswf` finds `gameplay\gui_new\swf\worldmap\panel_worldmap.redswf`.
- **Words with `*` or `?` match the file name only.** `*.redswf` lists every Flash panel; `*_icon??.xbm` works too.
- Upper and lower case don't matter.
- {{Esc}} clears the search.

**File type** (only `.w2mesh`, only `.xbm`…) and **Class** sit beside the search. Class shows only files that hold one kind of resource, as REDkit's asset browser does: `CMesh` for meshes, `CBitmapTexture` for textures, `CEntityTemplate` for entities, `CStoryScene` for scenes, `C2dArray` for `.csv` tables, and so on, each with how many there are.

## The columns

Click a column header to sort by it; drag headers to move columns or their edges to resize them.

- **Size** is the file's real size, **Packed** what it takes inside the archive.
- **Format** is how it's stored: **Stored** (not compressed), **Zlib**, **LZ4**, **LZ4 HC** or **Snappy** (compressed, which extracting undoes), **Decompiled** (a script rebuilt from compiled scripts) or **Script** (a loose script on disk).
- **Source** is the archive it's in.

Rest the mouse on a file's name to see what kind of file it is: a mesh, an entity, a quest phase, a Flash panel, and so on.

## Thumbnails

**View** switches the list between **Details** and **small**, **medium** or **large thumbnails**. Textures show their pictures, other files an icon for their kind.

![The griffin's folder as medium thumbnails](main-thumbnails.jpg)

Click to highlight, {{Ctrl}}+click to add or take one away, {{Shift}}+click for a run, and the arrow keys to move. The tick box in a thumbnail's corner ticks it for extracting.

## The preview pane

**View > Preview pane** ({{Alt+P}}) shows the highlighted file beside the list without opening it:

- textures and pictures as images (an `.xbm` finds its pixels in a texture cache)
- meshes in 3D, with their textures, to turn round
- cutscenes as a summary with a timeline of their events
- scenes as how they start, with their lines in words
- scripts, XML and other text, coloured

Drag the bar between the list and the pane to make it wider.

## The right-click menu

| Command | What it does |
|---|---|
| **Preview** ({{Enter}}) | opens the file in its viewer |
| **Open in XML editor** | for XML files. See [[XML files]]. |
| **Properties** | every property of a game file. See [[Properties]]. |
| **Open as JSON** | a game file as JSON to read or edit. See [[CR2W files as JSON]]. |
| **Copy to project** | a copy in your project, ready to change. See [[Changing game files]]. |
| **Compare the two** ({{Ctrl+D}}) | two highlighted files side by side, with the differences marked |
| **Other copies of this file** ({{Ctrl+Shift+D}}) | every open archive with a file at the same path. See [[Which copy of a file wins]]. |
| **Extract highlighted** | copies just these. See [[Extracting files]]. |
| **Copy** | the file's **Path** ({{Ctrl+C}}), **Name** ({{Ctrl+Shift+C}}), **Folder** or **Location on disk** |
| **Show in Explorer** ({{Shift+Alt+R}}) | the bundle the file is in, picked out in File Explorer |
| **Export the list as CSV** | the list as a table for Excel or any spreadsheet |

> tip: The path you copy with {{Ctrl+C}} is the game path, exactly as XML files and scripts refer to it, such as `characters\models\monsters\gryphon\model\t_01__gryphon.w2mesh`.
