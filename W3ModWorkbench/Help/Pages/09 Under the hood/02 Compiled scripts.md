---
title: Compiled scripts (.rsblob)
icon: E943
order: 2
summary: What's inside a precompiled.rsblob, and why mods ship them.
keywords: rsblob precompiled compiled scripts blob format bytecode
---
REDkit compiles a mod's WitcherScript ahead of time into one `precompiled.rsblob`. The game loads it as it is instead of compiling the mod's `.ws` files when it starts, which is quicker and avoids clashes over whole files, but it means the mod doesn't ship readable source. The app reads them back: see [[Decompiled mods]].

## What's inside

A blob isn't an archive of source files. It's the compiler's output, in the order the game needs it:

- a header with the REDkit build that made it, and when it was compiled
- the enums, structs and classes, with every field, parameter and local variable, names and types included
- each function's **bytecode**: compact instructions for the game's script engine
- tables of the types and functions the bytecode refers to
- a table of every name used, which the rest points into

Because names and types survive compiling, decompiled code reads very close to the original: functions, variables and fields keep the names the author gave them.

## Two compile units

REDkit compiles the game's scripts first, then a mod's on top as a second unit. A mod's file at the same path as a game script is compiled again over the game's, and annotations (`@wrapMethod` and the rest) are applied in a fixed order. The app's built-in compiler follows the same steps. See [[Compiling]].
