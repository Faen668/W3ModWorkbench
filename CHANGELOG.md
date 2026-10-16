# Changelog

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
