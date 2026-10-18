# Changelog

## Unreleased

- **View menu and preview pane in the main window**: **View** switches the file list between **Details** and small, medium or large **thumbnails** (textures and picture files show their picture, other files an icon for their kind; click, Ctrl+click, Shift+click and the arrow keys select, double-click opens). **Preview pane** (Alt+P) shows the highlighted file beside the list: textures and pictures as images (an `.xbm` in a bundle finds its texture in the cache), scripts with WitcherScript colouring, XML with XML colouring, and other text as plain text. The choice and the pane's width are remembered.
- **Picture files open in the texture viewer**: PNG, JPEG, BMP, GIF, TIFF and DDS files in bundles (and in projects) show in the same viewer as cached textures, with channels, zoom and Save PNG. A DDS keeps its mip levels. Double-click, or press Enter.
- **Texture caches** (`texture.cache`): open the game's, a DLC's or a mod's texture cache like a bundle (Open files, Open folder, Browse installed mods) and every texture in it is listed under its game path, with its format and size. A 30 GB cache opens in a moment, since only its index is read. Double-click a texture, or press Enter, to see it in a viewer with its mip levels, the colour and each channel (R, G, B, A) on their own, a chequerboard for transparency, fit or actual size (Ctrl+wheel zooms), and **Save PNG** and **Save DDS**. Extracting a texture writes a `.dds` with all its mip levels. Reads BC1, BC3, BC4, BC5 and BC7 (the formats the cache uses for colour, normals, masks and the Remastered's high quality textures), plain 32-bit colour and float data. Arrays and cube maps show their first slice.
- **`.xbm` files in bundles open their texture**: an `.xbm` in a bundle only describes the texture, so double-clicking one finds its pixels in a texture cache under the same path (an open one, the one beside the bundle, or the game's) and shows them in the viewer.
- **The tree groups a mod's files under the mod's name**: a mod's bundles, compiled scripts, texture cache, strings and loose scripts are now listed under one entry for the mod (Browse installed mods, Open folder), instead of as separate top-level items. Selecting the mod shows every file in it, and right-click closes or extracts all of it. A mod with one archive stays as it was.
- **Real projects**: a project is now a `.w3bench` file (plain JSON) in the mod's folder, not just any folder of scripts you pointed the tool at. It names the mod's **scripts folder**, lists the **other files and folders** that belong to the mod (XML, strings, notes), remembers **where Compile writes** the `.rsblob`, and carries a name, author, version and description. Paths are relative, so a project can be moved or kept in git. **Project** in the toolbar has **New project**, **Open project** (Ctrl+Shift+O), **Recent projects**, **Project properties**, **Add files / a folder to the project**, **Refresh** and **Close project**. A `.w3bench` file opens by dropping it on the window or through Open files, and so does a folder with one in it. Project properties edit everything in it, and Compile's output now lives in the project (old per-folder choices in settings are picked up when a project is made for that folder).
- **One workspace project**: one project is open at a time and every window knows it. It is the first entry in the tree (PROJECT badge, the project's name), showing its scripts and other files laid out as in the mod's folder; XML files in it open in the XML editor. Opening another project, closing it or closing the tool asks about unsaved files. The tool **reopens the last project** with the scripts that were open (kept in the tool's settings folder, not in the project file; turn it off in Settings).
- **Loose files ask to join**: scripts opened outside the open project (Open folder, a dropped `.ws`, Find in files) are *loose scripts*, listed without a badge. They can be read and followed, but typing in one stops and asks: **Add to project** (with a project open) or **Create project…** from the script's folder (with none), or edit without. A file edited outside a project says **not in a project** and has no Compile button. The XML and strings editors ask once per session, after a save, whether to add the file. Right-click files for **Add to project** / **Remove from project** (the file is never deleted), and right-click loose scripts for **Create a project from these scripts**.
- **Edit as project** on a decompiled mod now makes a `.w3bench` project in the folder you pick. Find in files' scope is **The open project and loose scripts**.
- **Built-in script compiler**: Compile no longer needs REDkit. When wcc_lite isn't set, it compiles the project with a compiler built into the tool, which reads the game's own scripts (from the game folder, or REDkit's if found) and the mods you have installed (so a project built on Shared Utils finds it), and writes the same kind of `.rsblob` in about a second. On the NG mods it was tried on, the blobs are byte-for-byte what wcc_lite writes or the same code, declaration by declaration, and on about ten thousand of the game's own functions 99.6% came out the same. **Always use the built-in compiler** in Settings picks it even when wcc_lite is set. Maps (`map<K, V>`) work in both the compiler and the decompiler, which no longer stops on compiled scripts that use them. It is a port of the compiler webspam wrote for Aelto's tw3-cahirb (pull request 1).
- **View XML overrides** (Tools): lists the definitions (items, abilities, sets…) that mod.io mods, DLC and mods set in their XML, both loose files and XML packed in bundles, normal and New Game+ kept apart. Definitions with `on_conflict` (the Remastered's XML overrides) and plain ones that redefine the same name are both counted. Script Merger's merged mod is understood: its `MergeInventory.xml` (found beside the configured merger, or in the usual places) says which mods went into each merged file, so the merged copy is labelled "merge of …" and those mods' own copies are marked as already inside it instead of being reported as clashes. The rules follow Script Merger Remastered: a field only clashes when two mods give it different values, and the winner is decided by the game's XML order (mod.io mods first, then DLC by name with the later name winning). For each one it shows the mods that touch it, winner first, whether each extends or replaces it, the fields they set, and which mod wins each field when several set the same one. Open any of the files from the list.
- The **load order analyser** now includes mods installed through the in-game mod.io hub. They live outside the game folder (the root is read from `%LOCALAPPDATA%\mod.io\globalsettings.json`, by default `C:\Users\Public\mod.io`) and load before every other mod, so they come first in the order and are checked for conflicts like the rest.
- Tool windows (preview, XML editor, load order analyser, find in files, strings, stub generator, usage graph, help…) are now **independent windows**: each has its own taskbar button and no longer minimises, hides or closes with the main window.
- **Show usage graph** on the script right-click menu: right-click a function, event, variable or type and get a graph of how it's used. The thing itself is on the right, the functions that use it are to its left, and the functions that use those further left, up to six levels out. Click a box to light up its chain of calls down to the target, double-click it to open it (Ctrl for the other side), and hover or click the label on a line to see the lines of code that make the call. Right-click a box to start a new graph from it. Drag with either mouse button to pan and Ctrl+wheel to zoom; **Fit** shows the whole graph and **Stretch** fills the window. Steps with many callers wrap into several columns. It offers to search the installed mods' loose and compiled scripts when they aren't included yet, so callers in other mods show up too.
- Usage graph boxes have a third line in the accent colour naming the mod the script is from, or **Game Script** for the game's own scripts.
- Go to definition, find usages, find all references and the usage graph now **prefer a mod's compiled scripts over its loose ones**. A mod that ships both is listed once, from the compiled blob; mods without a blob fall back to their loose scripts.
- Fixed: an error while the window was being laid out could hang the tool, using a whole CPU core, instead of showing the error. Seen when clicking a thumbnail after previewing a script.
- Fixed: in the stub generator, switching between Wrap, Replace and the other kinds did nothing until you left it and came back. The output now updates straight away and keeps the selected function.
- The menu builder's **Script** tab now has WitcherScript syntax highlighting and line numbers.

## 1.5.2

- **Find in files** (Tools, Ctrl+Shift+F): searches the text of files in folders with nothing open, like Notepad++. Pick where to look (the game's scripts, the installed mods, REDkit's scripts, the open projects or any folder), filter file names (`*.ws; *.xml`, `!*.bak`), and match case, whole words or a regular expression. Matches are listed by file, a click opens the file at the match, and F4 / Shift+F4 step through them.
- **Browse game scripts** and **Browse installed mods** (Tools, and on the start screen): list the game's own scripts, or everything in the mods folder, in the main window without opening a project first. The game's scripts always open read-only and no longer carry a PROJECT badge.
- **XML outline**: XML previews and the XML editor have an outline side bar of the file's elements, each with the attribute that names it (`ability  DLC5 Nilfgaardian Armor _Stats`). It follows the cursor, filters by name and keeps up as you edit.
- **XML folding** in the preview, and a softer look for the fold markers everywhere. A folded element keeps its attributes, so it reads `<ability name="DLC5 Nilfgaardian Armor _Stats">` instead of `<ability>`.
- **Double-clicking an XML file** now opens the preview, like right-click > Preview. **Open in XML editor** on the right-click menu opens the editor.
- The outline can be shown or hidden **separately in each side of a split view**. What you chose last is what newly opened files start with.
- Fixed: splitting a preview with only one XML tab hung the app.
- Fixed: go to definition on a type name written after a colon (`out vector : Vector`) went to the variable of the same name instead of the type.

## 1.5.1

- **Load order analyser** (Tools, Ctrl+Shift+L): reads every installed mod and DLC and checks the load order. Compiled scripts are decompiled to what they declare and use, loose scripts parsed, bundle tables and strings listed, and priorities come from `mods.settings` (DLC first, then mods by priority, then unlisted mods by folder name).
  - **Collisions**: loose scripts at the same path, `@replaceMethod`, `@addMethod` and `@addField` used twice, replaces mixed with wraps, wraps stacking, classes, global functions and enum values declared twice, bundle files with different content, and string IDs with different text.
  - **Breakages**: annotations whose class or function the game and the other mods don't have (usually a mod made for another game version), and mods that use something no installed mod provides.
  - **Dependencies and order**: a mod that needs another mod that is missing or disabled; and, because scripts compile in reverse load order, a dependency such as Shared Utils that loads *before* the mods using it, or has a priority when they don't (or the other way round). Dependencies declared in `info.json` are checked too. Leftover `mods.settings` entries for removed mods are flagged.
  - **Who wins**: clashes with a clear outcome show a ★ winner on the row and in a WINNER box, and the winner is starred in the file lists. A merged-files mod that loads first is treated as the answer to the clashes it covers.
  - Pick a mod to see only its findings; filter by Problems, Warnings and Notes; **Save report** writes Markdown.
- **Script merger** setting (Settings): the path to Script Merger Remastered by relapse12. The analyser suggests it for script and bundle clashes and has an **Open Script Merger** button. The merger is a separate download and is only started, never bundled.
- Help: a new **Load order analyser** page, and the Settings and shortcuts pages cover the new setting and Ctrl+Shift+L.

## 1.5.0

- **Source inspector** (Tools, Ctrl+Shift+I): compares two mods, as folders of loose files, bundles or compiled scripts, and reports how much of one is in the other. Scripts are compared by their code and by their shape, so renamed variables and functions still match; menus by element; text by line; every file inside every bundle by SHA-256. It also lists the comments, messages, names and menu keys both share, functions copied whole or renamed, and compares coding style (indentation, braces, spacing, naming habits, comment density). The game's own scripts can be set aside so shared game code isn't counted. Matches open side by side with the shared lines marked, and the report saves as a web page or Markdown.
- The **About** card (the **i** button) now stays open until you click elsewhere, press Esc or click the button again, instead of closing when the mouse moved.
- **Search game strings** (Tools, Ctrl+Shift+G): finds the ID, key and text of any string the game, its DLC or your installed mods would load, in any language. Search by words in the text, an ID, or a key's name or hash; copy an ID, the text, a `GetLocStringById(...)` call or a strings CSV line. All 100,000 or so strings are searched as you type.
- Strings editor: **Check IDs** looks for IDs your strings share with the game, its DLC or other installed mods, marks them with where they're used, and suggests a free block of IDs. **Game strings** opens the search from the editor.
- **XML editor**: XML files open in a window of their own, like strings (double-click one, drop it, or Tools > Open an XML file). Coloured in light and dark, with folding, a live well-formed check that jumps to the error, **Format**, and saving that keeps the file's encoding. XML inside a bundle opens as a copy to Save as. XML previews are coloured too.
- **Mod menu builder**: Menu builder (Ctrl+B) in the XML editor, or Tools > New mod menu, edits a `user_config_matrix` menu with forms. Pages, toggles, sliders, option lists and separators, presets, the text each key needs (added to a strings CSV with new IDs), the WitcherScript that reads every setting, and a list of problems. It was checked by reading and rewriting the menus of the game and a set of installed mods without changing their meaning.
- **Stub generator** (Tools, Ctrl+Shift+W, or *Create a wrap or replace stub* on a script's right-click menu): writes `@wrapMethod`, `@replaceMethod` (starting from the original code), `@addMethod` and `@addField` code from the game's own scripts, with the exact signature, the right `wrappedMethod(...)` call, and states and events handled.
- Help: new pages for all of the above.
- New app icon: the wolf over a gear, with a wrench and screwdriver.
- Script previews: the **outline** is now a side bar beside the text, a tree of the file's classes, structs and enums with their members under them. Click a name to go there; the one the cursor is in is picked out as you move, a filter narrows it, and it keeps up as you edit. Outline or Ctrl+Shift+O shows and hides it, and its width is remembered. The type-to-search list moves to *Go to symbol in file* on the right-click menu.
- About moves out of Settings to an **i** button at the top right of the main window. It opens a card with the version, licence and credits, which stays open until you click elsewhere, press Esc or click the button again.
- **Open in external editor**: right-click a text file in the file list, or a preview tab, to open it in the app Windows uses for its type (or Open with, when there's none). A project's file opens itself; a file inside a bundle or compiled scripts opens as a read-only copy in the temp folder.
- Strings editor: **Load translation** puts an old translation (`.csv` or `.w3strings`) over a mod's new strings, matched by key, or by ID for strings without one. Strings it doesn't have get a **NEW** badge, the text they had stays in an **Original** column to translate from, and **Show** lists only what's still to translate, what's been translated, or what came from the old file.
- Script projects stand out: a **PROJECT** badge, the accent colour and a bold name in the tree, and a badge with the project's name beside the file name in the preview.

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
