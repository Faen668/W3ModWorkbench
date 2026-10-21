---
title: Troubleshooting
icon: E90F
order: 1
summary: What to do when something doesn't work.
keywords: problem error not working fix help bundle version compile greyed read-only extract
---
## I can't type in a script

It's read-only: one of the game's scripts, a mod's, or a script outside your project. Typing in it offers **Copy to project**, which puts a copy in your project to change. With no project open, make one first ([[Projects]]). For a compiled mod, use **Edit as project** ([[Decompiled mods]]).

## Compile is greyed out

Rest the mouse on it to see why. Usually no project is open, or neither compiler can run: set the game folder (for the built-in compiler) or REDkit's `wcc_lite` in [[Settings]]. If wcc_lite is set but shows a warning, verifying REDkit in Steam or GOG fixes it.

## A compile fails and I can't see why

Click the error to go to its line, and check [[Compiling]] for what the common messages mean. **Show log** has the compiler's own words. Every error listed is in your project: the game's own scripts are left out.

## My mod does nothing in the game

- Is it installed? **Project > Install** puts it in the game's `mods` folder.
- Is the mod name a folder starting with `mod`? The game only loads those.
- Does another mod replace the same function? The [[Load order analyser]] finds that.
- For scripts: does it compile? Build won't make a mod whose scripts don't.

## A script shows numbers where names should be

Set the game folder in [[Settings]]. See [[Enum names]].

## "Not a Witcher 3 bundle" or "Unknown bundle version"

The file isn't a Witcher 3 bundle, or it was cut short while downloading. The app reads version 3 bundles (older community tools) and version 5 (the game and REDkit).

## Files won't extract to a folder

Check you can write to it. Program Files and the game's own folder need admin rights; pick a folder in Documents or on another drive.

## The textures are grey

The texture isn't in any texture cache the app can find: open the mod's folder (with its `texture.cache`), or the material says why.

## Start again

Delete `%AppData%\W3ModWorkbench\settings.json` to put every setting back to how it started. Your projects aren't touched.
