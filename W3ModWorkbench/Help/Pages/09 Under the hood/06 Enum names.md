---
title: Enum names
icon: E943
order: 6
summary: Why decompiled code sometimes shows a number where a name should be, and how names come back.
keywords: enum names numbers decompiled game folder cast
---
In compiled code an enum value is just a number: `processMode = kSell` is stored as `processMode = 0`. The decompiler turns numbers back into names wherever it can tell what the number means:

- assigning to a variable or field whose type is an enum
- comparing two values of the same enum
- `switch` cases on an enum
- `return` from a function that returns an enum

The names come from the mod's own enums and the game's script sources in `content\content0\scripts`. A blob inside the game's `mods` folder finds the game by itself; for one anywhere else, set the game folder in [[Settings]].

## Values that stay as numbers

Some enums belong to the engine itself, written in C++ rather than script, such as `EInventoryEventType`. Their names aren't in any script, so values show as a cast, `(EInventoryEventType)1`, which is valid WitcherScript and means the same.

## Mods that add enum values

A mod can add values to one of the game's enums. They're numbered on from the game's last value, as the compiler numbers them. See [[WitcherScript annotations]].
