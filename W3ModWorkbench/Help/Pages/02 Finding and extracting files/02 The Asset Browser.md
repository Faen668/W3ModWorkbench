---
title: The Asset Browser
icon: E8B7
order: 2
summary: The whole game, every DLC and every installed mod in one window, with bookmarks, filters and "what uses this file".
keywords: asset browser game files mods dlc redkit r4data bookmarks recent uses references filter class whole game
windows: AssetBrowserWindow, UsesWindow
---
The **Asset Browser** is the quickest way to find anything in the game. It opens the game, its DLC and every mod you have installed together, with nothing to pick first. It's a window of its own, like REDkit's.

Open it from **Tools > Asset Browser** or with {{Ctrl+Shift+A}}. The first time takes a second or two; after that it opens where you left it.

![The Asset Browser](asset-browser.jpg)

## What's in it

The tree on the left has four headings:

- **Game files**: the game itself, the base game and the official DLC, each file once, with nothing any mod adds. **From** says which bundle a file comes from, and textures kept only in a texture cache are listed too.
- **Mod DLC**: each mod installed into the game's `dlc` folder.
- **Mods**: each mod in `mods`, and mod.io's, with everything it holds: bundles, texture cache, compiled scripts (decompiled), loose scripts and strings.
- **REDkit**: REDkit's own files, about 230,000 copies of the game's files from before they were cooked, and files only REDkit has. It's greyed out until REDkit's `wcc_lite` is set in [[Settings]].

## Finding a file

The search, **File type** and **Class** work as in the main window (see [[Finding files]]). The second row adds:

- **Folder**: words in a file's folder, not its name.
- **Regex**: the search and folder boxes as regular expressions.
- **Whole game**: which parts of the game to show: the base game, Hearts of Stone, Blood and Wine, and each free DLC.
- **Clear filters** takes every filter off at once.

## Opening a file

Double-click a file, or press {{Enter}}, to open it in its viewer. When a mod has its own copy of a game file, the browser asks which copy to open, in load order, with the one the game uses marked. See [[Which copy of a file wins]].

Whatever you open finds the files it uses (textures, materials, animations) in **its own mod first, then the game**. Other installed mods are never mixed in, so the game's copy of a scene shows the game's actors and a mod's copy shows the mod's.

## Bookmarks and recent files

- Pick a folder and press the **star** beside Class to bookmark it, or right-click files and choose **Bookmark**. The **bookmarks** button lists them; pick one to go straight there. Bookmarks are kept between sessions.
- **Recent** (the clock) lists the last 30 files you opened from the browser.

## What uses this file?

Right-click a file and choose **What uses this file?** to see every file that refers to it, from the game, the installed mods and REDkit: the entities that use a mesh, the meshes that use a material, the materials that use a texture. Beside them are the files it refers to itself. Double-click either to open it, or **Show in the Asset Browser** to go to it.

> note: The first time, every file's links are read, which takes about half a minute. They're kept, and only archives that change (a mod installed or updated) are read again.

## Right-click menu

Preview, open as JSON, **Copy to project** (a mesh's buffers come too), extract, copy paths, **Open another copy…**, **Other copies of this file…**, show in Explorer, and **Copy > Link to open it in W3 Mod Workbench**, a `w3modworkbench://` link that opens the browser at that file. See [[EasyAssetBrowser]] for links from its website.

**Tools > Browse game scripts** lists the game's own scripts in the main window, to read.
