# Changelog

## Unreleased

- Go to definition in script previews: Ctrl+click a name (or press F12) to open where it's declared, in the same archive, any other open archive or the game's scripts. When nothing is found, **Search Now…** offers to look through the loose scripts of installed mods too, for libraries such as Shared Utils that have no archive. Works for functions, events, classes, states, structs, enums and their values, fields, parameters and locals, and follows `a.b` through declared types, return types, casts and `extends` chains.
- Names that lead somewhere get a faint underline on hover, and become a link while Ctrl is held. When several declarations fit, such as a game method that a mod wraps, a list lets you choose.
- Clicking a `@wrapMethod` or `@replaceMethod` function's name, or `wrappedMethod`, opens the game's original. Alt+Left or the mouse's back button returns to where you jumped from.
- Messages in the preview, such as a file that couldn't be saved, now show inside the window instead of as a Windows message box.
- Closing a preview no longer sends the explorer behind other apps.

## 1.3.1

- Decompiler: every one of the script engine's 153 built-in operators now shows as its symbol, including byte, `Uint64`, `EngineTime`, `GameTime`, matrix and attribute maths, and `&=` / `|=`.
- Decompiler: reads several rarely used instructions it used to stop on (parent calls, default-object fields, save points, fixed-size arrays), and byte conversions are now decoded correctly.
- Decompiler: rebuilds `do { ... } while (...)` loops, and handles `continue` as older REDkit builds compile it.
- Decompiler: keeps property hints (`hint x = "...";`) and struct and array defaults, which used to stop blobs that had them from opening.
- Scripts that link to properties of other classes now open instead of reporting an unknown section.
- Thanks to Aelto, whose tw3-cahirb project filled several of these gaps.

## 1.3.0

- WitcherScript syntax highlighting in the preview, using the grammar from WebSpam's witcherscript-vscode extension (with his permission). Colours follow the app theme.
- The preview is now a proper code view: line numbers, and a search bar (Ctrl+F) with match case, whole word and regex options, F3 / Shift+F3 to move between matches.
- Credits and third-party notices in Settings > About, the Help and the README.
- The game folder setting is clearer, and can find your Steam or GOG install automatically.
- The version and copyright show in the bottom bar.
- Scroll bars show the normal arrow cursor everywhere.

## 1.2.0

- Settings window: theme (Windows, Light or Dark), extraction defaults and the game folder used to name enum values in decompiled scripts.
- Help window with a full guide and an Advanced section on the file formats and the script decompiler (F1).
- About section with version, copyright and licence.
- Fixed the disabled "Extract checked" button losing its theme styling.
- The Visual Studio designer now shows the Fluent styles.

## 1.1.0

- Opens REDkit compiled scripts (`precompiled.rsblob`) as decompiled WitcherScript: classes and states, enums, structs, global functions, and `@addField`, `@addMethod`, `@wrapMethod` and `@replaceMethod` changes.
- Enum values are named using the game's own script sources when they can be found.
- Preview window for text files (double-click or Enter).
- New app icon.

## 1.0.0

- First release: open, browse, search and extract The Witcher 3 `.bundle` files (versions 3 and 5, zlib, LZ4 and Snappy).
