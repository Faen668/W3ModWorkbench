---
title: Script Bench
icon: E943
order: 1
summary: The window every script opens in: your project's scripts, the game's and the installed mods', side by side.
keywords: script bench ide editor tree override copy to project new script rename delete compile status
windows: PreviewWindow:bench
---
**Script Bench** is where you write and read WitcherScript. Open it from **Project > Script Bench** or with {{Ctrl+Shift+E}}. Any script you open, from anywhere in the app, opens here too.

![Script Bench, with a script of the project open](bench-overview.jpg)

## The tree

On the left are all the scripts your mod can reach, in three groups:

| Group | What's in it | Can you change it? |
|---|---|---|
| **Your project** (its name) | your mod's scripts folder: the scripts that get compiled | yes |
| **Game scripts** | the game's own scripts | no: **Copy to project** to change one |
| **Installed mods** | each mod in the game's `mods` folder; compiled ones are decompiled when you open them | no: **Copy to project** to start from one |

- Double-click a script, or press {{Enter}}, to open it.
- **Filter scripts** at the top shows only scripts whose name or folder has what you type ({{Esc}} clears it).
- The buttons beside it read everything again, show the file in front in the tree, and collapse everything.
- Drag the line between the tree and the tabs to make either wider.

## Your own scripts

Right-click your project or one of its folders for **New script…** and **New folder…**. A script or folder can be renamed ({{F2}}) and deleted ({{Del}}, to the Recycle Bin); deleting a folder's last script removes the empty folders too.

Your project's scripts have a **star** on their tabs, so you can always tell them from read-only ones.

## Overriding a game script

Right-click one of the game's scripts and choose **Copy to project**. It's copied into your scripts folder at the same path and opened, ready to change, and the tree marks it **OVERRIDE**: it's compiled in place of the game's file. Right-click it > **Compare with the game's** to see what you've changed.

> tip: Overriding a whole file clashes with any other mod that overrides it. Changing just the functions you need, with [[WitcherScript annotations|annotations]], gets on far better with other mods.

A file an installed mod ships at the same path as a game script is marked **REPLACES**. **Copy to project** works on mods' files too.

## Read-only scripts

The game's scripts, the mods' and any script outside your project are read-only, and say so under their name. Typing in one offers **Copy to project**, or, with no project open, **Create project…** from its folder.

## The bar at the bottom

It says exactly what **Compile** reads: your scripts folder (how many scripts, and how many override the game's), the game's scripts and how many installed mods, and which compiler. **Compile** ({{F7}}) checks your scripts; see [[Compiling]].

## What else is here

The editor has everything code editors have: see [[Writing scripts]] for typing help, snippets and renaming, and [[Finding your way around scripts]] for going to definitions, the outline and searching.
