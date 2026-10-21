---
title: Stub generator
icon: E943
order: 7
summary: Writes the start of a @wrapMethod, @replaceMethod, @addMethod or @addField for any of the game's functions.
keywords: stub generator wrap replace add method field signature template
windows: StubWindow
---
Wrapping or replacing one of the game's functions always starts the same way: the annotation, the exact signature, and a call to the original. The stub generator writes that for you from the game's own scripts. Open it from [[Script Bench]]: the stub button in its toolbar, or {{Ctrl+Shift+W}}, opens it on the name at the cursor, and **Create a wrap or replace stub** on the right-click menu does the same for the function under the mouse.

![The stub generator, wrapping the player's OnSpawned](stub-generator.jpg)

1. Search by class, function or both: `W3PlayerWitcher OnSpawned`. Each result shows its parameters.
2. Choose what to write:
   - **Wrap**: a `@wrapMethod` that calls `wrappedMethod` with the right arguments. A function that returns something keeps it in `result`.
   - **Replace**: a `@replaceMethod`, starting from the game's own code so you can change it (untick **Replace: start from the original code** for an empty one).
   - **Add method** and **Add field**: an `@addMethod` or `@addField` on the class, with the name (and type) filled in.
3. **Copy** puts it on the clipboard, ready to paste into your script.

**Note where it's from** adds the game's file and line as a comment. See [[WitcherScript annotations]] for what each annotation does.
