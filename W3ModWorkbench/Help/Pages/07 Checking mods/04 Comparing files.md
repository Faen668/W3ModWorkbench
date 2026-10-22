---
title: Comparing files
icon: E8F1
order: 4
summary: See what differs between two versions of a file: a mod's copy and the game's, two mods' copies, or any two files you pick.
keywords: compare diff difference two versions files side by side mod game copy property texture picture strings w3strings compiled scripts rsblob changes what changed
windows: CompareWindow, ComparePickWindow
---
Two versions of the same file, side by side, showing just what's different. Each kind of file is compared in the way that suits it:

| File | Compared |
|---|---|
| Entities, meshes, quest phases and every other CR2W file | property by property, in a tree |
| Strings (`.w3strings`) | string by string, by ID |
| Textures (`.xbm`, `.dds`, and pictures such as `.png`) | picture by picture, side by side or as one picture of the difference |
| Compiled scripts (`.rsblob`) | decompiled, then line by line as script text |
| Scripts, XML and other text | line by line, side by side in the preview |

Any other file is compared byte for byte: a message says whether the two are the same, and if not, their sizes and where they first differ.

## Where to compare from

- **Compare with game's copy**, at the end of the info line along the bottom of every viewer, when the file shown is a copy of a game file: an installed mod's, your project's, or one in an archive you opened. It sets that copy beside the game's own. (For the game's own files the same button is **Make a mod…**: see [[Changing game files]].)
- **Compare copies** in the [[Load order analyser]], on a finding about files two mods both ship: pick a file and see the two mods' copies against each other.
- **Compare the two** when you right-click two highlighted files in the main window or the Asset Browser.
- **Tools > Compare files…**, for any two files: see below.

![Compare with game's copy, at the end of a viewer's info line](compare-with-game.jpg)

## Property by property

![A mod's copy of Roach's entity, compared with the game's](compare-tree.jpg)

Each object in the file is a branch, with its properties under it, and the two versions' values in the columns beside them. A row tinted amber differs; red is only on the left, green only on the right. A list shows how many entries it has, `[12]` against `[1]`, and opens to its entries.

- **Only differences** (on when the window opens) hides everything that's the same, except the objects and lists a difference is in. Untick it to see the whole file.
- {{F8}} and {{Shift+F8}}, or the arrows, go to the next and previous difference, opening the branches it's in.
- **Search names and values** keeps only the rows that have what you type in their name or either value.
- **Swap** puts the left side on the right.
- Right-click a row to copy its left value, right value, or its path.

Objects are matched by the guids quest blocks carry, or else by their class and place among their parent's, so an object added part way through doesn't make every later one look changed.

## Strings

![The game's English and German strings, string by string](compare-strings.jpg)

Strings are matched by ID, so a string only on one side shows red or green, and a changed one amber with both texts.

## Textures

![A texture beside an edited picture of it](compare-texture.jpg)

**Side by side** shows the two pictures with their format and size. **Difference** shows one picture: the left one in grey, with every pixel the right one changes in magenta, and how many pixels that is. Two pictures of different sizes can only be compared side by side.

![The difference: what the edited picture changes, in magenta](compare-texture-diff.jpg)

A texture is compared by its pixels wherever they are: in the `texture.cache` beside the bundle it came from, in a `.dds`, or in a picture in your project (Build makes it into the texture again).

## Compiled scripts

A mod's `.rsblob` is decompiled (as in [[Decompiled mods]]) and the two scripts are compared line by line in the preview, side by side, with {{F8}} stepping through the changes.

## Compare files

![Compare files: a game path on the left, a file on disk on the right](compare-pick.jpg)

**Tools > Compare files…** compares any two files. Each side is either:

- a file on disk: **Browse…**, type or paste its path, or drop it on the box (drop two files to fill both sides), or
- a game path, such as `characters\npc_entities\animals\horse\horse.w2ent`, read from the game itself.

A file in your project's workspace or a game folder is known by its game path, so a texture's pixels are found in the cache beside it.

> tip: Comparing a mod's file with the game's is quickest from the file's viewer: open it from the Asset Browser and click **Compare with game's copy**.
