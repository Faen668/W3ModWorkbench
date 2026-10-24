---
title: Projects
icon: E821
order: 2
summary: Where your mod lives: its scripts, your copies of game files, and how it's built.
keywords: project w3bench new open properties scripts folder workspace mod name author version recent
windows: ProjectWindow
---
A **project** is where you make your mod: a folder of your own, outside the game, with a small `.w3bench` file in it.

```text
My First Mod\
  My First Mod.w3bench     the project file
  scripts\                 your scripts
  workspace\               your copies of game files, each at its game path
  build\
    modMyFirstMod\         what Build makes, ready to install
```

Everything in `scripts` is compiled with the mod. Everything in `workspace` is packed into the mod's bundle. The `.w3bench` file is plain JSON, and its paths are relative to it, so the folder can be moved, zipped or kept in git.

## Making and opening one

- **Project > New project…** ({{Ctrl+Shift+N}}) asks for a name, the mod name and a folder, and makes the project with its `scripts` and `workspace` folders. A folder inside the game's or REDkit's folders isn't allowed.
- **Project > Open project…** ({{Ctrl+Shift+O}}), or drop a `.w3bench` file on the window.
- **Recent projects** lists the last ten. One project is open at a time.
- When the app starts it opens the project you had last, with the scripts that were open (see [[Settings]]).
- Right-click a folder of loose scripts in the tree > **Create a project from these scripts** makes one around it. To only compile them, choose **Compile these scripts** there instead (see [[Compiling]]).

## Project properties

**Project > Project properties…** changes the project's details:

![Project properties](project-properties.jpg)

| Field | What it is |
|---|---|
| **Name** | the project's name |
| **Mod name** | the folder Build makes and Install puts in the game's `mods` folder. It must start with `mod`. |
| **Scripts folder** | where the mod's scripts are. Every `.ws` file in it is compiled. |
| **When the mod is built** | **Also ship the loose scripts** puts your `.ws` files beside the compiled ones. See [[Building and installing]]. |
| **Author**, **version**, **description** | go in the built mod's `info.json` |
| **Other files** | menus, strings, notes, anything else you add to the project |

## The project in the tree

The project is the first entry in the main window's tree, with a **PROJECT** badge. Right-click it for **Project properties**, **Add files to the project**, **New mesh from glTF** (see [[Meshes]]), **Refresh the project** ({{F5}}, after files change outside the app; the folders you had open stay open, and the one you picked stays picked), **Open the project folder** and **Close project**. Right-click files for **Add to project** or **Remove from project** (which only takes the name off the list; the file stays).

## Handling the project's files

The project's own files can be handled in the file list as in Explorer:

| | |
|---|---|
| **Cut** ({{Ctrl+X}}), **Copy files** ({{Ctrl+C}}) and **Paste** ({{Ctrl+V}}) | Paste goes into the folder picked in the tree. Files copied in Explorer paste here too, and the project's files paste into Explorer. A file pasted where it is already gets a copy's name (`player - Copy.ws`); one with the name of a file already there asks whether to replace it or keep both. Something pasted into the project's own folder is added to the project. |
| **Duplicate** | a copy beside it, named the same way |
| **Rename** ({{F2}}) | in the bar over the list; {{Enter}} renames, {{Esc}} leaves it |
| **Delete** ({{Del}}) | sends them to the Recycle Bin, where they can be restored from. It asks first, unless **Settings > General** says not to. |

A file open in a tab is closed first, asking about unsaved changes. The game's files and installed mods' are never changed: they can be copied, and cut ones are copied rather than moved.

> note: Only your project's files can be changed. The game's files, other people's mods (and your own mod once it's installed) are read-only everywhere in the app. To change one, copy it into your project first: see [[Changing game files]].
