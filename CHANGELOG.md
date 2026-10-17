# Changelog

## 1.4.1

- Script editor: a folder of `.ws` files opens as a **script project** (Open folder, or open or drop a `.ws` file), and its files can be edited in the preview, with go to definition, the popup, the outline and highlighting following your edits. Ctrl+S saves (in the file's own encoding), Ctrl+Shift+S saves every file. Tabs show a dot for unsaved changes, and closing a tab, a window or the app asks first.
- Edit as project: a decompiled blob's scripts are saved to a folder of your choice and open as a project in its place, so they can be edited and compiled.
- Compile (Ctrl+Shift+B) builds a project into a `.rsblob` with REDkit's `wcc_lite.exe`, the same compiler REDkit publishes with, and lists its warnings and errors under the files, each opening its line. Set wcc_lite in Settings, or let it find REDkit in your Steam and GOG libraries; Compile stays off, with a note saying why, until it's set.
- wcc_lite often leaves the file name out of its messages for projects with several files. The explorer works out the file from the name in the message and the line, and marks it as guessed.
- Help: a new page, **Editing and compiling scripts**, and the shortcuts, Settings and troubleshooting pages cover the editor.

## 1.4.0

- W3 Bundle Explorer is now **W3 Mod Workbench**, with a new icon: it does a lot more than bundles now. Settings move to `%AppData%\W3ModWorkbench` on the first start, with your old ones carried over.
- Strings editor: drop a `.w3strings` file, or a strings `.csv`, onto the window to read and edit it, then save it as a `.w3strings` file the game reads (byte for byte what the modding tools' encoder writes), optionally copied to every language, or export it as CSV. Opening a folder lists its strings files in the tree. Key names come back from a strings CSV beside the file.
- Script previews: find all references (Shift+F12), who else changes this (Ctrl+Shift+F12) for wraps, replacements, overrides and loose file replacements, a type hierarchy (Shift+Alt+H), go to symbol (Ctrl+T), find in files (Ctrl+Shift+F), all in a results list under the files.
- Script previews: an outline bar with the class and function you're in (Ctrl+Shift+O lists the whole file), parameter hints in calls, and a richer definition popup that says what a declaration belongs to and an enum value's number.
- Compare a mod's loose copy of a game script with the game's version, or any two open files, with the differences marked; F8 steps through them.
- Back and forward (Alt+Left / Alt+Right) with a history list, which reopen closed tabs at the right place. Pinned tabs, and Ctrl+Shift+T to reopen the tab closed last.

- Previews open as tabs in one window instead of a new window each time, and a file that's already open is brought forward instead of opening again. Settings > Preview can put each file in its own window as before.
- Split view: **Split** (`Ctrl+\`) moves the file you're reading into a second group of tabs beside or below the first, or shows a second view of it when it's the only tab. Drag tabs to reorder them, move them between groups and windows, onto the side of the text to split, or out of the window to give them one of their own. Right-click a tab to close others, move it, or copy its path.
- Resting the mouse on a name in a script shows its declaration in a popup, coloured like the preview, with its file and line, so you can see where Ctrl+click will go. Can be turned off in Settings.
- Themes: Settings > Appearance has eight colour themes, Windows plus one for each witcher school and a plain Graphite, each with its own accent and background in light and dark. A sample shows the choice live, and the mode is now a Windows / Light / Dark switch.
- Ctrl+Alt+click (or Ctrl+F12) opens a definition in the other group. Alt+Left goes back across tabs and windows.
- Go to definition finds a struct built in place, such as `SU_MenuDescriptor_OptionDescription(id, text)`, and follows `.field` after it. It used to only look for a function of that name.
- Go to definition in script previews: Ctrl+click a name (or press F12) to open where it's declared, in the same archive, any other open archive or the game's scripts. When nothing is found, **Search Now** looks through the loose scripts of installed mods too, with no second question, for libraries such as Shared Utils that have no archive. Works for functions, events, classes, states, structs, enums and their values, fields, parameters and locals, and follows `a.b` through declared types, return types, casts and `extends` chains.
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
