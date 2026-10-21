---
title: Load order analyser
icon: E9D9
order: 1
summary: Look over every installed mod at once and find what will break, what clashes and what's in the wrong order.
keywords: load order conflicts clash mods.settings priority dependency script merger compatibility analyse
windows: LoadOrderWindow
---
**Tools > Load order analyser** ({{Ctrl+Shift+L}}) reads every mod you have installed and reports what will break, what collides and what's in the wrong order. Nothing is changed.

![The load order analyser, with its findings](load-order.jpg)

## What it reads

Point it at the game's `mods` folder (it fills that in for you). For each mod, and each DLC with **Include DLC** on, it reads the compiled and loose scripts (every class, function and annotation), the files in its bundles, and its strings, and checks them against the game's own scripts.

## The load order

The list on the left is the order the game loads mods in: DLC first, then mods by the **Priority** in `Documents\The Witcher 3\mods.settings` (lowest number first), then mods without one by folder name. The mod at the top wins a file clash. Mods switched off in `mods.settings` are left out.

## What it finds

| Finding | Means |
|---|---|
| **Script file** | two mods ship different copies of the same script file; only one is used |
| **Same function** | two mods `@replaceMethod` (or add) the same thing; one is lost |
| **Type** or **function defined twice** | two mods declare the same class or global function |
| **Missing target** | a mod changes a class or function the game hasn't got: it was made for another game version |
| **Dependency** and **Load order** | a mod needs another (such as Shared Utils) that's missing, switched off, or loads in the wrong order |
| **Bundle file** | the same file in two mods' bundles, different |
| **Strings** | the same string ID with different text in two mods |

**Problems** will break something, **warnings** might, **notes** are worth knowing. Pick a mod to see just its findings; pick a finding to read what it means and what to do. **Save report** writes it all as Markdown, and {{F5}} analyses again.

> tip: A dependency such as Shared Utils must load **after** the mods that use it, because scripts compile from the bottom of the order up: give it a higher priority number, or a folder name that sorts last, like `modzzz_sharedutils`.

## Fixing clashes

Many script clashes can be merged with **Script Merger Remastered** (a separate download). Set its path in [[Settings]] and **Open Script Merger** starts it from here. To change the order, set priorities in the game's mod manager (or `mods.settings`) and analyse again.
