---
title: The main window
icon: E8A9
order: 2
summary: A tour of the window you start in: the tree, the file list, the preview and the bar along the bottom.
keywords: tour layout overview toolbar tree list interface
windows: MainWindow
---
The main window is where you open things and look around in them. It works a lot like File Explorer: folders on the left, the files in the one you pick on the right.

![The main window with the game open, the griffin's folder picked and its mesh in the preview pane](main-overview.jpg)

## The parts of the window

| Part | What it's for |
|---|---|
| **Toolbar** (top) | Open files and folders, your **project**, **Tools**, **View**, **Settings** and this **Help**. |
| **Tree** (left) | Everything you've opened, with the folders inside each. The number beside a folder is how many files it holds. |
| **File list** (middle) | The files in the folder you picked, its subfolders included, or only its own files (the folder button beside **View**). Click a column header to sort. |
| **Search, File type and Class** (under the list) | Narrow the list down. See [[Finding files]]. |
| **Preview pane** (right, {{Alt+P}}) | The file you picked, without opening it: pictures, 3D models, text, scenes. |
| **Extract bar** (bottom) | Where to put copies of files, and the buttons that make them. See [[Extracting files]]. |
| **Status bar** (very bottom) | How many archives and files are open, how many you've ticked, and what's going on. |

## The toolbar

- **Open files** ({{Ctrl+O}}) and **Open folder**: see [[Opening files and folders]]. The clock button beside them lists what you opened lately.
- **Project** (it shows your project's name when one is open): make, open and build your mod. See [[Projects]].
- **Close all** closes everything that's open.
- **Tools**: the [[The Asset Browser|Asset Browser]], [[Script Bench]] (with [[Find in files]]) and the other tools, each with its own page in this guide.
- **View**: the list as **Details** or **thumbnails**, the preview pane on or off, and **Files in subfolders too**. Off, a folder lists only its own files, as Explorer does; a search still looks in every folder under it. The folder button beside **View** switches it too, and **Settings > General** sets how it starts.
- **Settings** ({{Ctrl+,}}), **Help** ({{F1}}), and the **ⓘ** button for the version, licence and credits.

## Doing things with files

Double-click a file, or pick it and press {{Enter}}, to open it in its viewer: a mesh in the 3D viewer, a scene in the scene viewer, a script in Script Bench, and so on. Every kind of file has its own page under [[Previewing text files|Looking at game files]].

Right-click files for everything else: copy their paths, compare two of them, see the other copies of a file, copy one to your project, open it as JSON, extract it, or show it in Explorer. [[Finding files]] goes through the menu.

## The tree's names

Most mods call their bundle `blob0.bundle`, which isn't much help when twenty are open, so each entry in the tree is named after the folder it came from: the mod, the DLC or `content0`. The file name is greyed out beside it, and resting the mouse on an entry shows its full path.

When a mod has several things open (a bundle, compiled scripts, a texture cache, strings), they're gathered under one entry with the mod's name. Picking that entry lists every file in the mod, and **Close mod** closes all of it.

| Icon | What it is |
|---|---|
| box | a bundle (`.bundle`) |
| `{ }` | compiled scripts (`.rsblob`) |
| **PROJECT** badge | your project |
| page | loose scripts |
| picture | a texture cache |
| language | a folder of strings files |

> tip: Right-click an entry in the tree for **Show in Explorer**, **Extract this folder**, **Check all in folder** and **Close bundle**.
