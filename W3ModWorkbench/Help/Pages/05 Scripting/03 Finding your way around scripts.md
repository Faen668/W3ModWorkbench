---
title: Finding your way around scripts
icon: E8FD
order: 3
summary: Go to a definition, see who uses and changes something, and move around a file and the game's scripts.
keywords: go to definition f12 ctrl click peek popup references outline symbol hierarchy usage graph who changes compare
---
Script Bench knows where everything in the game's scripts, the installed mods and your project is declared.

## Going to a definition

- Hold {{Ctrl}} and click a name, or put the cursor on it and press {{F12}}, to jump to where it's declared. Names that lead somewhere get a faint underline.
- Rest the mouse on a name to see its declaration in a popup, with its file, what it belongs to, and the number behind an enum value.
- {{Ctrl+Alt}}+click or {{Ctrl+F12}} opens it in the other group, so the code you came from stays in view.
- {{Alt+Left}} (or the mouse's back button) goes back to where you jumped from, {{Alt+Right}} forward again. The clock button at the top lists where you've been.

It follows `a.b` through variables, return types and casts, up the class's `extends` chain. Clicking the name of a `@wrapMethod` or `@replaceMethod`, or `wrappedMethod`, shows the original.

## Moving around a file

- The bar above a script shows the class and function the cursor is in; click either for a list of the others.
- **Outline** ({{Ctrl+Shift+O}}) shows everything the file declares as a tree beside the text. Click a name to go there.
- {{Ctrl+T}} goes to any class, function, variable or enum value anywhere: type part of a name, or `Class.name`.
- Inside a call's brackets, a strip shows the function's parameters, the one you're on in bold.

## Who uses this, who changes this

These open a list under the files. Click a line to go there; {{F4}} and {{Shift+F4}} step through it.

| Command | Lists |
|---|---|
| **Find all references** ({{Shift+F12}}) | every place that uses the name, the right class's only |
| **Who else changes this** ({{Ctrl+Shift+F12}}) | every mod that wraps, replaces or overrides a function, or adds to a class |
| **Show type hierarchy** ({{Shift+Alt+H}}) | a class's parents, the classes that extend it, a statemachine's states |
| **Show usage graph** | a picture of what calls a function, and what calls those, up to six levels |
| **Find in files** ({{Ctrl+Shift+F}}) | plain text through every script |

## Comparing

- On a mod's copy of one of the game's scripts, **Compare with game version** opens the game's beside it with the differences marked: green added, red removed, amber changed. {{F8}} steps through them.
- **Compare with an open file** compares any two open files.
