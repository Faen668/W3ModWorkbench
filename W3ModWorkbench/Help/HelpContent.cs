namespace W3ModWorkbench.Help;

public sealed record HelpTopic(string Group, string Title, string Glyph, string Body);

/// <summary>
/// The help pages. Bodies use a small markup: "## " headings, "- " bullets, "> " notes,
/// ``` code blocks, `inline code` and **bold**. Blank lines separate paragraphs.
/// </summary>
public static class HelpContent
{
    public const string Basics = "Using the explorer";
    public const string Advanced = "Advanced";

    public static IReadOnlyList<HelpTopic> Topics { get; } =
    [
        new(Basics, "Getting started", "", """
W3 Mod Workbench opens the archives The Witcher 3 keeps its files in, lets you look around inside them, and pulls out just the files you need. It also opens the compiled scripts that REDkit mods ship (`precompiled.rsblob`) and turns them back into readable WitcherScript, and it can edit a mod's scripts and compile them again with REDkit's compiler.

## The short version

- Open a `.bundle` or `.rsblob` file, or a whole folder of them.
- Pick a folder in the tree on the left. The list on the right shows everything inside it.
- Tick the files you want.
- Choose where they should go in **Extract to**, then press **Extract checked**.

That covers most of what people use it for. The rest of this guide goes through each part in more detail, and the Advanced section explains what is going on under the hood.

## What it never does

The explorer never changes a bundle or a compiled script blob. Extracted files always go to the folder you choose, so it is safe to point it straight at a live install.

The one thing that writes files is the script editor: a script in a script project is saved when you press **Save**, and a compiled blob is written only where you tell it to go (see **Editing and compiling scripts**).
"""),

        new(Basics, "Opening files", "", """
There are three ways to get files in, and you can mix them freely. Everything you open stays listed until you close it.

## Open files

**Open files** (Ctrl+O) lets you pick one or more `.bundle` or `.rsblob` files. Holding Ctrl or Shift in the file picker selects several at once. A `.w3strings` file, or a strings `.csv`, opens in the strings editor instead (see **Editing strings**). A `.ws` script opens together with the rest of its script project: the `scripts` folder it's in, or its own folder when there's none.

## Open folder

**Open folder** loads every bundle and compiled script inside a folder, including all of its subfolders. Each folder of `.w3strings` files is listed too, as one entry with all its languages; double-click a file in it to open it in the strings editor. Loose `.ws` scripts open as script projects, one for each `scripts` folder, and can be edited (see **Editing and compiling scripts**). The game's own script folders are left out. Some useful places to point it at:

- `The Witcher 3\content` for the base game's bundles.
- `The Witcher 3\dlc` for the expansions and free DLC.
- `The Witcher 3\mods` to see every mod you have installed at once.
- A single mod folder, if you only care about one mod.
- A REDkit project's `workspace\scripts` folder, to edit and compile its scripts.

The whole game is around 365,000 files and takes about a second to load, so don't worry about opening big folders.

## Drag and drop

You can also drop files or folders anywhere on the window.

## How things are named

Most mods name their bundle `blob0.bundle`, which isn't much help when twenty of them are open at once. So each entry in the tree is labelled with the folder it came from: the mod, DLC or content folder. The file name is shown greyed out next to it. Hover over an entry to see its full path.

Bundles have a box icon. Compiled scripts and script projects have a `{ }` icon, and a folder's strings files a language icon.

## Closing

Right-click an entry in the tree and choose **Close bundle** to close just that one, or use **Close all** in the toolbar.
"""),

        new(Basics, "Finding files", "", """
## The folder tree

The tree mirrors the folders stored inside each archive. Selecting a folder lists everything in it, subfolders included. Selecting the top entry of an archive lists the whole thing. The number next to each folder is how many files it holds.

## The file list

Click any column header to sort by it. Columns can be resized and reordered by dragging. **Size** is the real size of the file. **Packed** is how much room it takes up inside the archive.

**Format** shows how each file is stored:

- **Stored**: not compressed at all.
- **Zlib**, **LZ4**, **LZ4 HC** or **Snappy**: compressed. The explorer unpacks these for you when you extract.
- **Decompiled**: a script the explorer rebuilt from a compiled blob (see the Advanced section).
- **Script**: a loose script in a script project, read straight from disk.

## Searching

Type in the search box (Ctrl+F) to filter the list. The search covers whichever folder is selected in the tree. Some tips:

- Every word you type must appear somewhere in the file's path, in any order. `worldmap redswf` finds `gameplay\gui_new\swf\worldmap\panel_worldmap.redswf`.
- Words containing `*` or `?` match the file name only. `*.redswf` lists every Flash panel. `*_icon??.xbm` works too.
- Searches ignore upper and lower case.
- Press Esc in the search box to clear it.

To search everything you have open, select nothing in the tree, or select the top entry of each archive in turn.
"""),

        new(Basics, "Selecting and extracting", "", """
## Choosing files

Tick the box next to each file you want. A few shortcuts make this quicker:

- The box in the column header ticks or unticks everything currently listed. It shows a dash when only some are ticked.
- Highlight rows with the mouse (Ctrl and Shift work as usual) and press **Space** to tick or untick them all.
- Right-click a folder in the tree and choose **Check all in folder**.

Ticks are remembered per file, so you can tick things in several folders, or even several archives, and extract them all at once. The status bar shows how many files are ticked and their total size.

## Extracting

First set **Extract to**. Type a path or click the folder icon to browse. Then pick one of these:

- **Extract checked**: every ticked file in every open archive (Ctrl+E).
- **Extract shown**: everything currently in the list. This works well with a search, e.g. search for `*.redswf` and extract them all.
- Right-click rows and choose **Extract highlighted** for a quick one-off.
- Right-click a folder in the tree and choose **Extract this folder**.

## Keep folder structure

With **Keep folder structure** on, files are written to the same path they had inside the archive. `panel_worldmap.redswf` ends up in `gameplay\gui_new\swf\worldmap\` under your chosen folder. That is usually what you want if the files are going back into a mod, because the game looks for them at exactly that path.

Turn it off to drop everything straight into one folder. If two files share a name, the second one gets ` (2)` added rather than overwriting the first.

> Existing files with the same name in the output folder are overwritten. Extract into an empty folder if you want to be sure nothing gets replaced.

## While it runs

Extraction runs in the background, with a progress bar in the status bar. **Cancel** stops it after the current file. Files that fail (very rare) are listed at the end with the reason, and everything else still gets written. If **Open folder when done** is on, Explorer opens the output folder for you.
"""),

        new(Basics, "Previewing text files", "", """
Text files can be read without extracting them first. That includes decompiled scripts, and the `.xml`, `.csv`, `.ws`, `.json` and `.txt` files found inside bundles.

- Double-click a text file, or highlight it and press **Enter**.
- Or right-click it and choose **Preview**.
- XML files open in the preview too. For the XML editor, right-click one and choose **Open in XML editor**.

## In the preview

- Line numbers run down the left. Tabs show four spaces wide.
- WitcherScript (`.ws`) files are colour coded, using the same grammar as WebSpam's WitcherScript extension for VS Code. The colours follow the app theme.
- **Ctrl+F** opens a search bar. **Aa** matches case, **ab** matches whole words only, and **.\*** treats the search as a regular expression. **F3** and **Shift+F3** jump to the next and previous match, and every match is highlighted.
- **Copy** copies all the text. **Save as** (Ctrl+S) writes the file somewhere, exactly as it came out of the archive.
- Files in a script project can be edited, and the button reads **Save** instead (see **Editing and compiling scripts**). Everything else is read-only, which the line under the file name says for scripts.
- **Esc** closes the search bar first, then the window when it shows just one file.

## Tabs and split view

Files open as tabs in one preview window. Opening a file that's already open brings its tab forward instead of opening it twice. If you'd rather have a window for each file, choose **A separate window for each file** in **Settings**.

- **Ctrl+Tab** and **Ctrl+Shift+Tab** move between tabs. **Ctrl+W**, a middle-click or the tab's **×** closes one. Right-click a tab for more: close the others, open it in the other group, move it to a new window, copy its path.
- **Pin** a tab from its right-click menu to keep it at the front. Closing the others, the ones to the right, or all of them leaves pinned tabs open.
- **Ctrl+Shift+T** opens the tab you closed last again, where you were in it. It remembers the last 25.
- **Split** (Ctrl+\) moves the file you're reading into a second group of tabs, so you can read two files at once. When it's the only tab, the second group shows another view of it instead, so you can keep one part of a file in sight while you read another. **Unsplit** puts every tab back in one group. Right-click **Split** to choose side by side or top and bottom; Settings has the same choice. **Ctrl+1** and **Ctrl+2** move between the groups.
- Drag a tab along the strip to reorder it, onto the other group to move it there, or onto the far side of the text to split the window with it. Drop it outside every preview window and it gets a window of its own. Tabs can be dragged between windows too.

## Going to a definition

In a script, hold **Ctrl** and click a name to jump to where it's declared, the way code editors do. Names that lead somewhere get a faint underline when you point at them, and become a link while Ctrl is held. **F12** does the same for the name at the cursor, and so does **Go to definition** on the right-click menu.

Rest the mouse on an underlined name for a moment to see its declaration in a popup, coloured like the preview, with the file and line it's on, and what it belongs to: the class of a method or variable, or the number behind an enum value, which is what compiled code shows. That way you can tell where a click will take you before you go. When several declarations fit, the popup shows the first few. You can turn the popup off in **Settings**.

- It looks in the script's own archive first, then in every other archive open in the main window, then in the game's scripts, which it finds the same way as for enum names (see **Settings**). Open a library's archive alongside the mod that uses it to jump into the library.
- Some mods, such as Shared Utils, install as loose scripts with no archive to open. When a name isn't found, click **Search Now** in the message to search the loose scripts of every mod in the game's `mods` folder. If that finds nothing either, a second message offers to search inside every installed mod's compiled scripts, which are decompiled first. Either way they're then searched for every definition until the explorer is closed. Loose files that replace one of the game's scripts are left out, and so is any mod whose archive is open, since many ship the same scripts both ways.
- Functions, events, classes, states, structs, enums and their values, member variables, and the function's own parameters and local variables all work.
- For `a.b`, it follows the declared type of `a` through variables, function return types and casts, and up the class's `extends` chain. `this`, `super`, `parent` and `virtual_parent` work too.
- Click the name of a `@wrapMethod` or `@replaceMethod` function, or `wrappedMethod`, to see the original: the game's, or the `@addMethod` of the mod that added it. Click a method's own name to see the one it overrides.
- When several declarations fit, like a game method that the mod wraps, a list lets you pick.
- A declaration in the same file just moves the view. Another file opens in a new tab next to the current one, or selects its tab if it's already open in that group. With **A separate window for each file** set, it opens in a new window instead.
- **Ctrl+Alt+click**, **Ctrl+F12** or **Open definition to the side** opens it in the other group, splitting the window if it isn't split yet, so the code you came from stays in view.
- **Alt+Left** or the mouse's back button goes back to where you jumped from, in whichever tab or window that is. **Alt+Right** or the forward button goes forward again. The arrows at the top of the window do the same, and the clock button lists the places you jumped from, to pick one.

It works from declarations alone, without compiling anything, so a few things can't be followed: engine functions with no script declaration, and names whose type it can't work out, which offer every member with that name instead.

Double-clicking anything that isn't text ticks or unticks it instead.

## Finding your way around a script

- The bar above a script shows the class and function the cursor is in. Click either one for a list of the others in the file.
- **Outline**, or **Ctrl+Shift+O**, shows or hides the outline beside the text: everything the file declares, as a tree with each class's variables, functions and events under it, and each enum's values. Click a name, or pick it and press Enter, to go there. The declaration the cursor is in is picked out as you move, the filter box narrows the tree to matching names, and the outline keeps up as you edit. Drag its edge to make it wider; the width is shared by every file. Each side of a split view shows or hides its own outline, and files you open next start the way you left it last.
- **Go to symbol in file**, on the right-click menu, lists the same declarations to type a name and go to it.
- **Ctrl+T** goes to any class, function, variable or enum value in everything that's searched: type part of the name, or `Class.name`, or just some of its letters in order.
- When the cursor is inside a call's brackets, a strip over the line shows the function's parameters, with the one the cursor is on in bold. It follows the same setting as the definition popup.

## References, changes and the type hierarchy

These open a list under the files. Click a line, or press Enter, to go there; hold Ctrl to open it in the other group. Click a file's heading to fold its lines away, or use the button at the top to fold or unfold them all. **F4** and **Shift+F4** step through the list. **Esc** in the list, or its **×**, closes it.

- **Find all references** (**Shift+F12**): every place that uses the name at the cursor, worked out the same way as go to definition, so a method called `Init` in one class isn't mixed up with another class's. Lines marked **maybe** are calls where the type before the dot couldn't be worked out.
- **Who else changes this** (**Ctrl+Shift+F12**): for a function, every `@wrapMethod` and `@replaceMethod` that changes it and every class that overrides it. For a class, the methods and variables mods add to it, and any mod that declares it again. For one of the game's scripts, the mods whose loose copy of the file replaces it outright.
- **Show type hierarchy** (**Shift+Alt+H**): a class's base classes, the classes that extend it however far down, and a statemachine's states. On a method, the base versions it overrides and the ones that override it.
- **Show usage graph** (right-click menu): a picture of how a function, event, variable or type is used. The thing itself is on the right, the functions that use it are to its left, and the functions that use those further left, up to six levels out (the **Levels** box). Click a box to light up its chain of calls down to the target, double-click it to open it (**Ctrl** for the other group), and click the label on a line to open the call, or hover it to read the lines of code. Right-click a box for **Show usage graph of this**, which starts again from that function. Drag with either mouse button to pan, **Ctrl+wheel** to zoom, **Fit** to see it all, and **Stretch** to fill the window. Steps with many callers wrap into several columns. Like go to definition, it offers to read the installed mods' loose and compiled scripts when the name isn't declared anywhere searched yet, and a bar above the graph does the same when they aren't included yet.
- **Find in files** (**Ctrl+Shift+F**): plain text, whole words or a regular expression, through every script that's searched, comments and strings included.

The installed mods' scripts are only in these searches once they've been read for go to definition. Until then, a strip under the list says so, and its **Loose scripts** and **Compiled scripts** buttons read them and search again.

## Comparing

- On a mod's loose copy of one of the game's scripts, **Compare with game version** in the right-click menu opens the game's file beside it and marks what's different: green for added lines, red for removed ones, amber for changed ones. **F8** and **Shift+F8** step through the differences.
- **Compare with an open file** does the same with any other file that's open, which is shown as the older version.
- **Stop comparing** takes the marks off both.
"""),

        new(Basics, "Editing and compiling scripts", "\uE70F", """
The preview is also a script editor. Scripts in a **script project** can be changed and saved, with highlighting, go to definition, the definition popup and the outline all keeping up as you type, and a whole project can be compiled into a `precompiled.rsblob`, with REDkit's own compiler or, without REDkit, the one built into the tool.

## Script projects

A script project is a folder of loose `.ws` files: a REDkit project's `workspace\scripts`, a mod's `content\scripts`, or a decompiled mod you saved to edit. Open one with **Open folder**, or open or drop any `.ws` file in it, and it's listed in the tree under the project's name. Its files open editable; scripts inside bundles and blobs, and the game's own scripts, stay read-only.

## Editing and saving

- Type straight into the file. **Ctrl+Z** and **Ctrl+Y** undo and redo.
- A dot on the tab, `●` in the window title and **not saved** under the file name mark changes that aren't saved yet.
- **Save** (**Ctrl+S**) writes the file back where it came from, in the same encoding it had (UTF-8 or UTF-16, with or without a byte order mark), so nothing changes but what you typed. **Ctrl+Shift+S** saves every changed file.
- Closing a tab, a window or the app with changes asks first: **Save**, **Don't save**, or **Cancel** to keep it open.
- When the same file is open twice, in both groups of a split, both show the same text, so an edit in one is in the other too.
- After a save, go to definition, references and the other searches see the change everywhere.

## Editing a decompiled mod

Decompiled scripts are read-only, since they live inside the blob. To change one, press **Edit as project** at the top of the window (typing in one offers it too) and choose a folder. The mod's scripts are saved to a `scripts` folder inside it, every file but `all.ws`, which only repeats the others. The folder opens as a script project in place of the blob, and the open tabs switch to its files, at the same place, now editable. The folder must not have a `scripts` folder with anything in it already, so nothing is overwritten.

In testing, a decompiled mod compiled again without changes, though a mod that uses something the decompiler gets wrong may need a fix first. See **Reading decompiled code** for what decompiling can't bring back, such as comments.

## Compiling

**Compile** (**Ctrl+Shift+B**) builds the project into a `.rsblob`. Two compilers can do it:

- **REDkit's `wcc_lite.exe`**, the same compiler REDkit uses to publish. REDkit is free on Steam and GOG for owners of the game: set wcc_lite in **Settings**, or let **Find automatically** look for it. It is used whenever it's set.
- **The built-in compiler**, for when REDkit isn't installed. It reads the game's own scripts (from the game folder in **Settings**, or REDkit's, whichever is found) and the mods you have installed, so a project built on Shared Utils finds it there, and writes the same kind of blob in about a second. Tick **Always use the built-in compiler** in Settings to use it even when wcc_lite is set, which is handy while you're trying changes.

Until one of them can run, the button is greyed out, and resting the mouse on it says why. You can still edit and save scripts without it.

- Changed files are saved first, since both compilers read what's on disk. It asks before saving them.
- The first time, it asks where the compiled blob goes, and remembers that for the project. Right-click **Compile** and choose **Compile to another file** to change it. For a mod, that's usually `content\precompiled.rsblob` in the mod's folder.
- wcc_lite compiles the game's scripts and then yours on top, the way REDkit does, which takes about a minute. The built-in compiler only reads the game's scripts for what they declare, so it takes a second.
- The built-in compiler checks your scripts the way the game's compiler does, but it is newer and less tested. Its output was compared with wcc_lite's on real mods and on about ten thousand of the game's own functions, and it matched on nearly all of them, but for a release, compile with wcc_lite when you can. Maps (`map<K, V>`) are supported, by the compiler and the decompiler.
- Warnings and errors are listed under the files, grouped by file. Click one to go to its line. When it fails, nothing is written, so an older blob that works is left alone.
- wcc_lite often leaves out which file a message is about when a project has more than one. The explorer works it out from the name the message mentions and the line it's on, and marks those **file guessed**. If it can't tell, the message is listed under **File not named**, with its line number.
- **Show log** opens everything the compiler wrote, and **Show in folder** opens the folder the blob was saved to.
"""),

        new(Basics, "Editing strings", "\uE8D2", """
The text a mod shows in game lives in `.w3strings` files, one for each language. Drop one onto the window, or pick it with **Open files**, and it opens in a table you can read and edit. A strings `.csv`, the kind the modding tools encode from, opens the same way.

## The table

- **ID**: the number the game looks the string up by. Mods use their own range, `211nnnn000` to `211nnnn999`, where `nnnn` is the mod's four-digit number.
- **Key**: the name scripts use, as in `GetLocStringByKeyExt("panel_Mods")`. Typing one sets the hash for you.
- **Key hash**: all a `.w3strings` file keeps of a key. When the explorer finds a strings `.csv` in the same folder, it uses it to show the names again; otherwise only the hash is shown, and the string still works.
- **Text**: what the game shows.

Double-click a cell, or press F2, to change it. **Add** (Ctrl+N) adds a string with the next free ID after the one selected, **Remove** (Delete) takes the selected ones out, and the search box (Ctrl+F) narrows the list by ID, key or text.

A warning sign marks a string with a problem; rest the mouse on it to see what. An ID that isn't a number, or one used twice, has to be fixed before saving. An ID outside the mod range, or a key two strings share, still saves, but it may clash in game.

## Saving

- **Save** (Ctrl+S) writes the `.w3strings` file, in the next-gen format the game reads, the same as the modding tools' encoder writes.
- **Save as** (Ctrl+Shift+S) writes it somewhere else.
- **Copy to every language** also writes the file as `ar`, `br`, `cz`, `de`, `en`, `es`, `esmx`, `fr`, `hu`, `it`, `jp`, `kr`, `pl`, `ru` and `zh` beside it, so the text shows whatever language the game is in. It asks before replacing files that are already there.
- **Language** is the language the file is saved as. Each one scrambles its text differently, and the game reads whichever the file says.
- **Export CSV** saves the strings as a `.csv` for the modding tools or a translator, with key names where they're known.

## Updating a translation

When a mod updates, its translation doesn't have to be done again. Open the new version's strings, then **Load translation** and pick the old translation, a `.csv` or a `.w3strings` file.

- Each string takes its text from the old translation, matched by key, or by ID for strings without a key.
- Strings the old translation doesn't have are new in this version. They keep their text, get a **NEW** badge, and are shown first. The **Original** column keeps the text the file had, to translate from.
- Changing a new string's text gives it a tick. **Show** picks which strings are listed: all, the ones still to translate, the ones translated here, or the ones from the old translation.
- The language is set to the translation's, so **Save** writes the file in it. The message after loading says how many strings were found, how many are new, and how many in the old translation aren't in the mod any more.

Files from before the next-gen update, which kept their text as UTF-16, open too. They're saved in the next-gen format.
"""),

        new(Basics, "Searching the game's strings", "", """
Before adding a line of text to your mod, check whether the game already says it. **Tools > Search game strings** (Ctrl+Shift+G), or **Game strings** in the strings editor, searches every string the game would load, in the language you pick.

## What it reads

The base game's `content\content*\<language>.w3strings`, and, with **Include DLC and mods** ticked, the files in `dlc\*\content` and `mods\*\content`. The **From** column says which one each result comes from. It needs the game folder (Settings finds it, or it's picked up from the file you're working on).

## Searching

- Type words and every one has to appear in the text, whatever the case: `swallow potion`. **Whole words** stops `ale` from finding `scale`.
- Type a number to find an ID, or the start of one: `2115940`.
- Type a key's name (`panel_Mods`) or its hash in hex (`4d5f8b0c`) to find the strings that use that key.

Results show the ID, the key hash and the text, from the first 2,000 matches. Double-click a row to copy its ID; right-click to copy the text, `GetLocStringById(id)` for a script, or a line ready to paste into a strings CSV.

## Checking your own IDs

In the strings editor, **Check IDs** compares every ID with the game's, the DLC's and the other installed mods'. Clashes are marked with a warning sign that says where the ID is used and what its text is there. Two strings with one ID show each other's text, which is how a mod ends up showing the wrong words in game. The check also suggests a block of 1,000 IDs that nothing uses.
"""),

        new(Basics, "XML files and the mod menu builder", "", """
XML files open in the preview when you double-click them, with an outline of their elements and folding. For an editor of their own, like strings, right-click one in the file list and choose **Open in XML editor**, drop it on the window, or use **Tools > Open an XML file**. XML inside a bundle opens as a copy that **Save as** writes out.

## The editor

- Tags, names, attribute values, comments and entities are coloured, in both light and dark.
- The **Outline** (the button at the top, or Ctrl+Shift+O) lists every element in the file as a tree, each with the attribute that names it: `ability  DLC5 Nilfgaardian Armor _Stats`. Click one to go there; the element the cursor is in is picked out as you move, and the filter box narrows the tree to matching names. The preview has the same outline.
- Elements fold from the margin, a folded element keeps its attributes so you can tell which one it is, and the bar under the text says whether the XML is well-formed, or where it isn't. Click the message to jump to the line.
- **Format** indents the XML with tabs, one element a line.
- Files keep the encoding they came in with (UTF-8, UTF-8 with BOM or UTF-16). A file that says `encoding="utf-16"` is saved as UTF-16.

## The mod menu builder

A mod's in-game options live in an XML file in `bin\config\r4game\user_config_matrix\pc`. **Menu builder** (Ctrl+B) edits one with forms, and writes the XML beside them as you go. **Tools > New mod menu** starts a new one from a small example.

- **Pages** are the groups of the menu, **Settings** are what's on a page: a toggle, a slider (min, max and steps), a list of options, or a separator.
- **Details** edits what you pick. A setting's text key follows its id while the two are the same, as most mods name them.
- **Presets** are named sets of values. The first one gives the settings their starting values, which you type as each setting's default.
- **Strings** lists every key the menu needs text for, named as the game expects (`panel_`, `option_`, `preset_`, `preset_value_`). Type the texts, then **Add to CSV** writes the missing ones with new IDs into a strings CSV, ready for the strings editor. If a strings CSV is next to the menu, its texts and IDs are picked up. **Find a free block** picks IDs nothing else uses.
- **Script** gives WitcherScript that reads every setting into a variable of the right type.
- **Problems** lists mistakes: ids used twice, a default outside a slider's range, a preset setting something that isn't on the page, and so on. Double-click one to go to it.

> The builder writes the XML itself, so it drops what it can't keep, such as comments. It says so before it does, and the code view undoes it. Parts of the file it doesn't know are listed in that message.
"""),

        new(Basics, "Stub generator", "", """
Wrapping or replacing one of the game's functions starts the same way every time: the annotation, the exact signature, and a call to the original. **Tools > Stub generator** (Ctrl+Shift+W) writes that for you from the game's own scripts. In a script preview, **Create a wrap or replace stub** on the right-click menu opens it on the function under the caret.

## Picking

Search by class, function or both: `W3PlayerWitcher OnSpawned`. Each result shows the function's parameters. Choose what to write:

- **Wrap**: `@wrapMethod`, with a call to `wrappedMethod` with the right arguments, and room before and after. A function that returns a value keeps it in `result`.
- **Replace**: `@replaceMethod`, starting from the original's code so you can change it. Untick **Replace: start from the original code** for an empty body.
- **Add method** and **Add field**: `@addMethod` or `@addField` on a class you search for, with the name (and the type, for a field) filled in.

Events keep `event`, and states are named the way the compiler does, as in `CR4PlayerStateIdle`. **Note where it's from** adds the game's file and line as a comment. **Copy** puts it on the clipboard.
"""),

        new(Basics, "Settings", "", """
Open **Settings** from the toolbar, or press Ctrl+, (comma).

## Appearance

- **Mode**: **Light**, **Dark**, or **Windows setting** to follow whatever Windows is set to.
- **Theme**: the accent and background colours. **Windows** uses your Windows accent colour and the see-through window background. The others, named after the witcher schools (Wolf, Cat, Griffin, Bear, Viper, Manticore, plus a plain Graphite), each have their own accent and a matching background, in both light and dark.

The sample at the top, and every open window, change as you click, so you can try each one. **Cancel** puts it back.

## Extraction

The default output folder and the two extraction options. These are the same values as the bar along the bottom of the main window, so changing one changes the other.

## Game folder (optional)

Where The Witcher 3 is installed. It only affects decompiled scripts, and only how enum values are shown. Without it, a line might read `if (type == 1)`. With it, the same line reads `if (type == EACIT_Potion)`.

**Find automatically** checks your Steam and GOG libraries. Or use the folder button to pick it yourself: the folder that contains `bin` and `content`. A tick under the box means it found the game's scripts.

You can leave it empty if you only open scripts from inside the game's `mods` folder, because those find the game on their own. It matters for scripts opened from anywhere else, like a mod's release folder or a download. A change applies to files you open after saving.

## Script compiler (optional)

REDkit's `wcc_lite.exe`, which **Compile** uses to build a script project into a `.rsblob`. It's in REDkit's `bin\x64_RedKit` folder. **Find automatically** looks for REDkit in your Steam and GOG libraries, or use the folder button to pick it. A tick under the box means it found wcc_lite and the game scripts REDkit compiles against (`r4data\scripts`). Leave it empty and **Compile** uses the built-in compiler, as long as the game's scripts are found; editing and saving scripts work either way.

## Script merger (optional)

The path to **Script Merger Remastered** by relapse12, a separate download on Nexus Mods that merges mods changing the same scripts or files. **Find automatically** looks in the game folder, Documents and Downloads. With it set, the load order analyser has an **Open Script Merger** button and suggests the merger where a clash can be merged. This tool only starts it; nothing of the merger is shipped or changed.

## Preview

- **Open files in**: tabs in one preview window, or a separate window for each file. Go to definition follows the same choice.
- **Split view**: whether a split puts the second group of tabs beside the first or below it.
- **Show a name's declaration when the mouse rests on it**: the definition popup in scripts.

## Where settings live

Everything is saved in `%AppData%\W3ModWorkbench\settings.json`. Delete that file to reset everything to the defaults.
"""),

        new(Basics, "Source inspector", "", """
Mods get copied, renamed and re-released. **Tools > Source inspector** (Ctrl+Shift+I) compares two mods and reports how much of one is in the other, so you can see what was reused instead of guessing. Everything is read on your PC; nothing is changed or uploaded.

## Choosing the two mods

Pick the **Original** (the mod you believe came first) and the **Suspect** (the one you want to check) with Browse, by pasting a path, or by dropping a folder on each box. A source can be a folder of loose files, a folder with `.bundle` and `.rsblob` files, a single bundle or compiled script, or a mix. The arrows swap the two. Then **Analyse** (Ctrl+Enter).

**Ignore the game's own scripts** sets aside code the game already has. A mod that wraps or replaces a game function starts from that code, so without this two honest mods look alike. It needs the game folder from Settings.

## What is compared

- **Scripts**: loose `.ws` files, and `.rsblob` files after decompiling. Comments, spacing and letter case are ignored. The code is compared as written, and as a shape with every name and value replaced, so renaming variables and functions doesn't hide a copy.
- **Menus and XML**: every element with its attributes and text, whatever the layout or attribute order.
- **Text and strings**: CSV, ini, text and similar files, line by line.
- **Bundle contents and other files**: every file inside every bundle is unpacked and fingerprinted with SHA-256. Identical files are found under any name or path.
- **Coding style**: indentation, braces, spacing, naming habits, comment density and function size. Habits survive renaming, but many coders share them, so it only supports the rest.

## Reading the result

- **Summary**: the headline percentage (the stronger of the two directions), what it means, key findings, and a breakdown by kind of content. Original found in the suspect is how much of the original's content turns up in the suspect; Suspect made of the original is the other way round. A small mod built almost wholly from a big one scores high on the second and low on the first.
- **Matching files**: each file pair with how strong the match is: **Identical**, **Renamed copy** (same structure, new names), **Near copy**, **Large overlap** or **Partial overlap**. Click one to see both files side by side, with the shared lines in green or the differences marked.
- **Shared details**: comments, messages, class and function names and menu keys found in both, functions copied whole, and functions copied with new names. A distinctive comment that appears twice is hard to put down to chance.
- **Coding style**: the habits side by side.
- **Sources**: what was read from each, file dates, and files that couldn't be read.

**Save report** writes everything as a web page or Markdown file you can keep or send.

## Be fair

Similarity is evidence, not proof. Mods for one game share its code and common frameworks (a mod menu library, say), and file dates can be changed. Read the matches and shared details, and talk to the authors before drawing a conclusion.
"""),

        new(Basics, "Load order analyser", "", """
**Tools > Load order analyser** (Ctrl+Shift+L) looks over every mod you have installed at once and reports what will break, what collides and what is in the wrong order. Everything is read on your PC; nothing is changed.

## What it reads

Point it at the game's `mods` folder (it fills that in when it finds the game). Each mod and, if **Include DLC** is on, each DLC is read:

- **Compiled scripts** (`precompiled.rsblob`) are decompiled to what they declare: classes, structs, enums, global functions, and every `@wrapMethod`, `@replaceMethod`, `@addMethod` and `@addField`, plus every class, function and field their code links to.
- **Loose scripts** (`content\scripts\...`) are parsed the same way. A mod whose `info.json` says `useLooseScripts: false` and that has a compiled blob keeps its sources loose; those are ignored.
- **Bundles**: the list of files inside, with size and checksum.
- **Strings**: the IDs in one language file.
- The game's own scripts, to check against.

## The load order

The list on the left is the order the analyser assumes: DLC first, then mods by the **Priority** in `Documents\The Witcher 3\mods.settings` (lowest number first), then mods that file doesn't list, by folder name. The mod at the top has the highest priority, and wins a file clash; for scripts the game compiles from the bottom up. Mods switched off in `mods.settings` are left out of every check except dependencies.

> The game's exact rules for clashes between compiled scripts aren't published. The analyser treats the lowest priority number as the winner, as the Witcher 3 mod manager does, and says plainly when the outcome depends on order.

## What it finds

- **Script file**: two mods ship a different `.ws` file at the same path (only one is used); a mod replaces a game script or class whole.
- **Same function**: two mods `@replaceMethod` or `@addMethod`/`@addField` the same thing (one is lost, or one fails), a replace together with wraps, and several mods wrapping one function (they stack, so this is a note).
- **Type defined twice**, **Function defined twice** and **Enum value**: two mods declare the same class, struct or global function, or use an enum value name or number differently.
- **Missing target**: an annotation names a class or function that neither the game nor any enabled mod has, which means the mod was made for another game version. Also a mod whose copy of a game class lacks a function another mod wraps.
- **Dependency** and **Load order**: a mod uses classes, functions or members only another mod provides, and that mod is missing, disabled, or loads *before* it. Scripts compile in reverse load order, so a dependency such as Shared Utils has to load after the mods that use it (a higher priority number, or a folder name that sorts last like `modzzz_...`). The other way round ends in errors such as "Unknown base class". If only one of the two has a priority it is flagged too: give both one, or neither. Dependencies in `info.json` are checked the same way.
- **Bundle file**: the same path is in two mods' bundles with different content. A merge such as `mod0000_MergedFiles` that loads first is treated as the answer.
- **Strings**: the same string ID with different text in two mods.
- **Build**: compiled scripts for a different platform than the rest.

## Using the result

Problems will break something, warnings might, notes are only worth knowing. Pick a mod in the list to see just its findings. Pick a finding to read what it means, the files or functions involved, and what to do. **Save report** writes it all as Markdown. **F5** analyses again.

**Script Merger**: many of the clashes above can be settled by merging the mods. Set the path to Script Merger Remastered (by relapse12, a separate download on Nexus Mods) under Settings > Script merger, and **Open Script Merger** starts it from the analyser. This app only launches it.

To change the order, set the priority numbers in the game's mod manager (or in `mods.settings`), then analyse again.
"""),

        new(Basics, "Find in files", "", """
**Tools > Find in files** (Ctrl+Shift+F) searches the text of files in folders, like Find in Files in Notepad++. Nothing needs to be open first: no project, no archive, no script.

## Searching

Type what to look for and press Enter, or click **Find all**. The three buttons beside the box are **Aa** (match case), **ab** (whole words only) and **.\*** (a regular expression). Every match is listed under its file with the line number, and the match is picked out in the line.

**Filters** says which files to read: wildcards separated by `;`, such as `*.ws; *.xml`. Put `!` in front of one to leave files out, such as `!*.bak`. Leave it empty to search every text file; files that are too big or aren't text are skipped.

## Where it looks

The **Look in** list has the places you're most likely to want:

- **Game scripts and installed mods**: the game's own scripts (`content\content0\scripts`) together with the loose files in its `mods` folder.
- **Game scripts**, **Installed mods** or **REDkit scripts** alone (REDkit's is found from the wcc_lite.exe set in Settings).
- **Open script projects**: the projects open in the main window.
- **A folder...**: any folder you pick. The last one picked is kept in the list.

**Subfolders** controls whether folders inside are searched too.

## Opening a match

Click a line, or press Enter, to open that file in a preview with the match selected. **F4** and **Shift+F4** step through the matches. Clicking a file's name folds its lines away. The game's scripts open read-only, and a script project's files can be edited and saved as usual. Go to definition works from any script opened this way.

Inside a script preview, Ctrl+Shift+F still searches from there, through the scripts that preview knows about.

## Browsing instead

**Tools > Browse game scripts** lists the game's own scripts in the main window, to read, and **Browse installed mods** opens everything in the `mods` folder the way Open folder does. Both need the game folder, which is found for you or set in Settings.
"""),

        new(Basics, "Keyboard shortcuts", "", """
## Anywhere

- **Ctrl+O**: open files
- **Ctrl+F**: jump to the search box
- **Ctrl+E**: extract checked files
- **Ctrl+Shift+F**: find in files, in the game's scripts, the mods or any folder
- **Ctrl+Shift+G**: search the game's strings
- **Ctrl+Shift+W**: stub generator
- **Ctrl+Shift+I**: source inspector
- **Ctrl+Shift+L**: load order analyser
- **Ctrl+,**: settings
- **F1**: this help

## In the file list

- **Space**: tick or untick the highlighted rows
- **Enter**: preview the highlighted text file
- **Ctrl+C**: copy the highlighted files' paths
- **Ctrl+A**: highlight every row

## In the XML editor

- **Ctrl+S** / **Ctrl+Shift+S**: save or save as
- **Ctrl+B**: show or hide the menu builder
- **Ctrl+F**: search

## In the search box

- **Esc**: clear the search

## In a preview

- **Ctrl+F**: search
- **F3** / **Shift+F3**: next or previous match
- **Ctrl+S**: save, or save as for a read-only file
- **Ctrl+Shift+S**: save every changed file
- **Ctrl+Shift+B**: compile the script project
- **Ctrl+Z** / **Ctrl+Y**: undo or redo
- **Ctrl+click** or **F12**: go to definition (scripts)
- **Ctrl+Alt+click** or **Ctrl+F12**: open the definition in the other group
- **Shift+F12**: find all references
- **Ctrl+Shift+F12**: who else changes this
- **Shift+Alt+H**: type hierarchy
- **Ctrl+T**: go to any symbol
- **Ctrl+Shift+O**: show or hide the outline
- **Ctrl+Shift+F**: find in files
- **F4** / **Shift+F4**: next or previous result in the list
- **F8** / **Shift+F8**: next or previous difference when comparing
- **Alt+Left** / **Alt+Right**: go back or forward
- **Ctrl+Tab** / **Ctrl+Shift+Tab**: next or previous tab
- **Ctrl+W**: close the tab
- **Ctrl+Shift+T**: reopen the tab closed last
- **Ctrl+\**: split or unsplit
- **Ctrl+1** / **Ctrl+2**: first or second group
- **Esc**: close the search bar, then the window when it shows one file
"""),

        new(Basics, "Troubleshooting", "", """
## "Not a Witcher 3 bundle"

The file doesn't start the way every Witcher 3 bundle does. It is most likely a different kind of file with a `.bundle` extension, or it was cut short during a download.

## "Unknown bundle version"

The explorer understands version 3 bundles (older community tools) and version 5 (the game itself since the next-gen update, and REDkit). Anything else is new to it.

## "Doboz compression is not supported"

Doboz is a compression method the engine supports but nothing in the current game actually uses. If a file really is packed with it, the explorer says so rather than guessing.

## A script shows numbers where names should be

See **Enum names** in the Advanced section. The usual fix is to set the game folder in Settings.

## Compile is greyed out

Rest the mouse on it to see why. Neither compiler can run: wcc_lite isn't set in **Settings**, and the game's scripts weren't found for the built-in compiler. Set the game folder (or wcc_lite) there. If wcc_lite is set but the box under it shows a warning, the file isn't wcc_lite, or REDkit's `r4data\scripts` folder isn't where it should be, two folders up from it. Reinstalling or verifying REDkit fixes that.

Compile only shows for a script project's files. For a decompiled script, use **Edit as project** first.

## A compile fails with errors I can't find

Check the line it names. When the file is marked **file guessed**, the explorer worked it out from wcc_lite's message; **Show log** has the compiler's own words. Errors in the game's scripts are left out, since they come from REDkit itself, so every error listed is in your project. If the built-in compiler stops on something wcc_lite accepts, compile with wcc_lite and tell me about the script.

## A script can't be typed in

It's read-only: inside a bundle or a blob, or one of the game's own scripts. Open its folder as a script project to edit it, or use **Edit as project** for a decompiled mod.

## Files won't extract to a folder

Check you can write to it. Program Files and the game folder itself often need admin rights. Pick a folder in your Documents or on another drive instead.

## Visual Studio says the exe is in use

If you are building the tool yourself, close any running copy first. Windows locks the exe while it is running.
"""),

        new(Basics, "About and licence", "", """
W3 Mod Workbench is made by **Faen90** and is free to use.

## Licence

The tool is open source under the MIT Licence. In plain terms: you can use it, share it, change it and build on it, including in your own projects, as long as the copyright notice and licence text stay with it. It comes with no warranty. The full text is in the `LICENSE` file next to the exe, and under About (the i button at the top right of the main window).

Copyright © 2026 Faen90.

## Other people's work

The licence covers the tool itself, not the files you open with it. Game files belong to CD PROJEKT RED, and a mod's files and scripts belong to whoever made the mod. Decompiled scripts are a way to understand a mod, check compatibility or recover your own lost source. Please respect the author's wishes and licence before reusing or republishing their code.

## Not an official tool

W3 Mod Workbench is an unofficial fan-made tool. It is not affiliated with, endorsed by or supported by CD PROJEKT RED. The Witcher, REDkit and REDengine are trademarks of CD PROJEKT S.A., mentioned only to describe what the tool works with.

## Credits

- **WebSpam**: the WitcherScript syntax highlighting in the preview comes from his VS Code extension, witcherscript-vscode (github.com/webspam/witcherscript-vscode), used with his kind permission. If you write WitcherScript in VS Code, his extension is well worth a look.
- **AvalonEdit** by the SharpDevelop team: the code view in the preview window.
- **Aelto**: his own decompiler project, tw3-cahirb (github.com/Aelto/tw3-cahirb), filled real gaps in this one. The full list of the script engine's built-in operators, several rarely used instructions, and a sample from an older REDkit build that showed up property hints, array defaults and `do`/`while` loops all came from comparing notes with his work.

WebSpam's grammar and AvalonEdit are MIT licensed, and their full licence texts are in `THIRD-PARTY-NOTICES.md` next to the exe, and under About (the i button at the top right of the main window). The built-in compiler is a port of the WitcherScript-to-`.rsblob` compiler that **WebSpam** wrote for tw3-cahirb (github.com/Aelto/tw3-cahirb, pull request 1), extended and checked against wcc_lite's output on real mods. Apart from that, no code was copied from tw3-cahirb, only facts about the file format.

The bundle reader, the LZ4 and Snappy decoders and the script decompiler are all written for this tool. Compiling uses REDkit's own `wcc_lite.exe` from your REDkit install when you have one; nothing of REDkit's is shipped with the tool. Without it, the built-in compiler reads the game's own scripts from your game install. The tool carries a list of the engine's native classes and enums (made with the game's own `-dumprtti` option) so it knows what the scripts can name.
"""),

        new(Advanced, "How bundles work", "", """
Every bundle starts with the text `POTATO70`, followed by a small header and a table of contents. Each entry in the table records the file's path, where its data sits in the bundle, its real size, its packed size, a checksum and the compression method.

## Two layouts

The table comes in two layouts, and the header says which:

- **Version 5**: used by the game since the next-gen update and by REDkit. Entries are 304 bytes. File offsets are 64-bit, because a few of the game's world bundles are larger than 4 GB.
- **Version 3**: older community tools. Entries are 320 bytes, with a 32-bit offset.

## Compression

Each file is compressed on its own, so pulling one file out never means unpacking the rest. Across the current game files are stored uncompressed, with zlib, or with LZ4 HC. Snappy is also supported in case a mod uses it.

## Why "Packed" can equal "Size"

Already-compressed formats such as textures and audio don't shrink, so they're simply stored. A few LZ4-flagged entries are actually stored raw too, and the explorer handles those quietly.
"""),

        new(Advanced, "Compiled scripts (.rsblob)", "", """
REDkit can compile a mod's WitcherScript ahead of time into a single `precompiled.rsblob`. The game loads it directly instead of compiling the mod's `.ws` files at startup. That is quicker and avoids script merging conflicts, but it means the mod no longer ships readable source.

Open one in the explorer and it appears as a set of `.ws` files you can read, preview and extract like anything else:

- `scripts\all.ws`: everything in one file. Usually the easiest place to start.
- `scripts\types.ws`: the mod's enums and structs.
- `scripts\classes\`: one file per class, with its states underneath.
- `scripts\functions.ws`: global functions, including `exec` console commands.
- `scripts\annotations\`: one file for each game class the mod changes, holding its `@addField`, `@addMethod`, `@wrapMethod` and `@replaceMethod` code.

The annotations folder is especially handy for checking compatibility. If two mods both have `@replaceMethod` for the same function, only one of them can win.

## What's inside a blob

A blob isn't an archive of source files. It's the compiler's output, written out in the order the game needs it:

- A header with the REDkit build that produced it.
- The enums, structs and classes, with every field, parameter and local variable, including names and types.
- Each function's bytecode: compact instructions for the game's script engine to run.
- Lookup tables for the types and functions the bytecode refers to.
- A table of every name used, which the rest of the file points into.

Because names and types survive compilation, the rebuilt code reads very close to the original. Functions, variables and fields all keep the names the author gave them.
"""),

        new(Advanced, "How decompiling works", "", """
Turning bytecode back into readable code happens in three passes.

## 1. Decoding

Each function's bytecode is read instruction by instruction: "load this variable", "call that function", "jump if false", and so on. About ninety instruction types exist, covering constants, variables, calls, operators, array methods, casts, switches and jumps.

Jump targets are stored as offsets in the game's in-memory layout, not in the file. So while decoding, the explorer also works out how big each instruction will be once loaded. As a check, the total is compared with the size the compiler recorded for each function. Every function in every blob tested matches exactly.

## 2. Expressions

Instructions are stored in prefix order, so `a + b * c` arrives as "add, a, multiply, b, c". Reading them back produces an expression tree, which is then printed with the fewest brackets that keep the meaning. A few details:

- Operators are calls to built-in functions, with a separate one per type (int `+`, float `+`, string `+` and so on). They're mapped back to their symbols.
- `&&` and `||` carry short-circuit markers, which are skipped.
- Casts the compiler adds on its own are hidden, because they're implied in the original source. Examples are int to float, object to bool in an `if`, and anything to string when joining text. Explicit casts such as `(int)`, `(float)` and `(SomeClass)` are kept.
- Calls that leave out optional arguments keep their empty slots, so `onHover('Name', , "hint")` comes back exactly as it was written.

## 3. Control flow

Bytecode has no `if`, `for` or `while`, only jumps. The explorer rebuilds them from the jump patterns:

- A conditional jump forward is an `if`. If the block it skips ends with a jump further forward, that's an `else`. An `else` that holds nothing but another `if` becomes `else if`.
- A conditional jump whose block ends by jumping back to it is a `while` loop.
- A `for` loop is a `while` whose setup, condition and step all sit on the same source line. The compiler keeps line numbers, which makes this reliable.
- Jumps to the end of a loop or switch become `break`. Jumps to the step of a `for` loop become `continue`.
- Each `switch` keeps a chain of case labels. Grouped cases (`case A: case B:`) are stored back to front, and the explorer puts them back in order.

Source line numbers also decide where blank lines go, so the layout of the original roughly survives.
"""),

        new(Advanced, "Reading decompiled code", "", """
The output is real WitcherScript and usually very close to what the author wrote. There are a few things the compiler throws away, though, and the explorer can't bring them back.

## What changes

- **Comments** are gone completely.
- **Formatting** is the explorer's own: tabs, braces on their own line, one statement per line.
- **Initialisers** such as `var total : int = 0;` come back as a declaration at the top of the function plus a separate `total = 0;` line where the original line was.
- **`public`** isn't shown, because it's the default and the compiler records it whether or not it was written. `private` and `protected` are always shown.
- **Annotated functions** always use the word `function`, even when they wrap an `event`. That matches REDkit's own syntax.
- **Defaults** (`default x = ...;`) appear after the class fields, in the order the compiler stored them. Struct defaults come out one field at a time (`default pos.X = 1;`). Array defaults are listed as a comment, because there is no single source form for them.
- **Hints** (`hint x = "...";`) are kept, after the defaults.

## Signs that something needs a look

The explorer would rather tell you than guess. If you see one of these, the code around it is worth reading carefully:

- A call such as `Multiply_Matrix_Matrix(a, b)`: an operator that has no WitcherScript symbol. Every known operator has a symbol, so this is only a safety net.
- `// (jump to offset ... could not be structured)`: a jump that didn't fit any known pattern, e.g. code built in an unusual way.
- `// This function could not be decompiled: ...`: the function hit something the decoder doesn't know. The rest of the file is unaffected.

None of these turned up across the mods used to build and test the decompiler, so seeing one most likely means a mod uses a feature they didn't.

## Can I recompile it?

Usually yes, after a quick look over the result. Bear in mind the mod author's licence still applies to their code. Decompiling is best used to understand how a mod works, check compatibility, or recover your own source after losing it.
"""),

        new(Advanced, "Enum names", "", """
In compiled code an enum value is just its number. `processMode = kSell` is stored as `processMode = 0`. The explorer turns numbers back into names wherever it can tell what the number means:

- Assigning to a variable or field whose type is an enum.
- Comparing two values of the same enum.
- `switch` cases on an enum.
- `return` from a function that returns an enum.

The names come from two places: the enums the mod itself declares, and the game's own script sources in `content\content0\scripts`. Those ship with every copy of the game, and the explorer finds them on its own when a blob sits inside the game's `mods` folder. For a blob anywhere else, set the game folder in Settings.

## Values that stay as numbers

Some enums belong to the engine itself, written in C++ rather than script, e.g. `EInventoryEventType`. Their names aren't in any script file, so values show as a cast such as `(EInventoryEventType)1`. That is valid WitcherScript and means exactly the same thing.

## Mods that add enum values

A mod can declare extra values for one of the game's enums. When a blob lists only new names, the explorer numbers them on from the game's last value. When it lists the whole enum again, it counts up from zero. The file notes which case applies.
"""),
    ];
}
