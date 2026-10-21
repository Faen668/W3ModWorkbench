---
title: Decompiled mods
icon: E943
order: 6
summary: Read the scripts of mods that ship them compiled, and turn one into a project you can change.
keywords: decompile rsblob precompiled read mod scripts edit as project classes annotations all.ws
---
Many mods ship their scripts compiled, as `content\precompiled.rsblob`, without the `.ws` files. The app turns them back into readable WitcherScript.

## Reading one

- In [[Script Bench]], expand a mod under **Installed mods**: a compiled mod's scripts are decompiled when you open them.
- Or open the `.rsblob` itself with **Open files**.

![A compiled mod's scripts, decompiled, in Script Bench](bench-mod.jpg)

The scripts come out sorted like this:

| File | Holds |
|---|---|
| `all.ws` | everything in one file: usually the easiest place to start |
| `types.ws` | the mod's enums and structs |
| `classes\` | one file for each class, with its states |
| `functions.ws` | global functions, including console commands |
| `annotations\` | one file for each game class the mod changes, with its `@wrapMethod`, `@replaceMethod`, `@addMethod` and `@addField` |

The `annotations` folder is handy for checking two mods get on: two `@replaceMethod`s of the same function can't both win.

## What comes back

Names, types and the code itself come back as the author wrote them. Comments don't (the compiler throws them away), and the layout is the app's own. See [[Reading decompiled code]] for the details.

## Turning one into a project

Press **Edit as project** at the top of the window (typing in a decompiled script offers it too) and choose an empty folder. The mod's scripts are saved there, a project is made around them, and it opens in place of the compiled file, ready to change and compile.

> note: A mod's code belongs to its author. Decompiling is for understanding how a mod works, checking compatibility, or getting back your own lost source. Respect the author's wishes before reusing it.
