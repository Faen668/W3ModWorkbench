---
title: Load order analyser
icon: E9D9
order: 1
summary: Look over every installed mod at once, or check one before installing it, and find what will break, what clashes and what's in the wrong order.
keywords: load order conflicts clash mods.settings priority dependency script merger compatibility analyse texture cache menu input.settings keys dlc id xml check before install download zip
windows: LoadOrderWindow
---
**Tools > Load order analyser** ({{Ctrl+Shift+L}}) reads every mod you have installed and reports what will break, what collides and what's in the wrong order. Nothing is changed.

![The load order analyser, with its findings](load-order.jpg)

## What it reads

Point it at the game's `mods` folder (it fills that in for you). For each mod, and each DLC with **Include DLC** on, it reads the compiled and loose scripts (every class, function and annotation, and the menu settings and keys they use), the files in its bundles, its `texture.cache`, its DLC definition and its strings, and checks them against the game's own scripts. It also reads what mods put in the game's own folders when they're installed: the options menus in `bin\config\r4game\user_config_matrix\pc`, and your keys in `Documents\The Witcher 3\input.settings`. Each mod's XML definitions are read as in [[XML overrides]].

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
| **Bundle file** | the same file in two mods' bundles, different; underneath, what each mod changes in it (see below) |
| **Textures** | the same texture in two mods' `texture.cache`, different, or a texture whose `.xbm` comes from one mod and its pixels, at another size, from another |
| **DLC** | two mods declaring a DLC with the same ID, of which the game mounts one |
| **Options menus** | a menu file that isn't valid XML, a menu group in two files, or a mod whose menu isn't installed (its settings all read as empty) or is from another version |
| **Keys** | a mod listening for an action `input.settings` has no line for or no key on, or a key that does a mod's action and another at once |
| **XML definitions** | an item, ability or other definition two mods set to different values |
| **Strings** | the same string ID with different text in two mods |

**Problems** will break something, **warnings** might, **notes** are worth knowing. Pick a mod to see just its findings, or {{Ctrl}}+click or {{Shift}}+click several to see only what they clash over with each other; pick a finding to read what it means and what to do. **Save report** writes it all as Markdown, and {{F5}} analyses again.

## What a clashing file changes

When two mods ship the same file and it's one of the game's own (an entity, a quest phase, a material), each mod's copy is compared with the game's, property by property, and listed under the file:

- **makes 42 changes** names the objects each mod changes and what in them. `+` marks an object a mod adds and `−` one it takes away.
- **both change** lists what both mods change, to different values. Only one mod's copy is used, so the other's changes there are lost.
- **they change different things** means one file could hold both mods' changes: a patch that combines them, made by hand or by a merge tool, would keep both.

Objects are matched by the guids quest blocks carry, or else by their class and place, so a mod that adds an object doesn't make every later one look changed.

## Check a mod before installing it

**Check a mod** reads a mod you've downloaded, from its folder or `.zip` (or drop either on the window), and analyses your mods with it in the place it would load, as if it were installed. A newer version of a mod you have takes the place of the old one, and the menu files in its `bin` folder are checked too. A bar says what it touches, and the list shows only its findings: **Only …: show all** shows everything, and **Stop checking** analyses your installed mods alone again. Nothing is installed or changed.

![Checking a downloaded mod before installing it](load-order-check.jpg)

> note: A `.7z` or `.rar` needs unpacking first (with 7-Zip, say); then pick its folder.

> tip: A dependency such as Shared Utils must load **after** the mods that use it, because scripts compile from the bottom of the order up: give it a higher priority number, or a folder name that sorts last, like `modzzz_sharedutils`.

## Fixing clashes

Many script clashes can be merged with **Script Merger Remastered** (a separate download). Set its path in [[Settings]] and **Open Script Merger** starts it from here. To change the order, set priorities in the game's mod manager (or `mods.settings`) and analyse again.
