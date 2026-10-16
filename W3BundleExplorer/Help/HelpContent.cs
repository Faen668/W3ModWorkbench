namespace W3BundleExplorer.Help;

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
W3 Bundle Explorer opens the archives The Witcher 3 keeps its files in, lets you look around inside them, and pulls out just the files you need. It also opens the compiled scripts that REDkit mods ship (`precompiled.rsblob`) and turns them back into readable WitcherScript.

## The short version

- Open a `.bundle` or `.rsblob` file, or a whole folder of them.
- Pick a folder in the tree on the left. The list on the right shows everything inside it.
- Tick the files you want.
- Choose where they should go in **Extract to**, then press **Extract checked**.

That covers most of what people use it for. The rest of this guide goes through each part in more detail, and the Advanced section explains what is going on under the hood.

## What it never does

The explorer only reads. It never changes a bundle, a script blob or anything in your game folder. Extracted files always go to the folder you choose, so it is safe to point it straight at a live install.
"""),

        new(Basics, "Opening files", "", """
There are three ways to get files in, and you can mix them freely. Everything you open stays listed until you close it.

## Open files

**Open files** (Ctrl+O) lets you pick one or more `.bundle` or `.rsblob` files. Holding Ctrl or Shift in the file picker selects several at once.

## Open folder

**Open folder** loads every bundle and compiled script inside a folder, including all of its subfolders. Some useful places to point it at:

- `The Witcher 3\content` for the base game's bundles.
- `The Witcher 3\dlc` for the expansions and free DLC.
- `The Witcher 3\mods` to see every mod you have installed at once.
- A single mod folder, if you only care about one mod.

The whole game is around 365,000 files and takes about a second to load, so don't worry about opening big folders.

## Drag and drop

You can also drop files or folders anywhere on the window.

## How things are named

Most mods name their bundle `blob0.bundle`, which isn't much help when twenty of them are open at once. So each entry in the tree is labelled with the folder it came from: the mod, DLC or content folder. The file name is shown greyed out next to it. Hover over an entry to see its full path.

Bundles have a box icon. Compiled scripts have a `{ }` icon.

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

## In the preview

- Line numbers run down the left. Tabs show four spaces wide.
- WitcherScript (`.ws`) files are colour coded, using the same grammar as WebSpam's WitcherScript extension for VS Code. The colours follow the app theme.
- **Ctrl+F** opens a search bar. **Aa** matches case, **ab** matches whole words only, and **.\*** treats the search as a regular expression. **F3** and **Shift+F3** jump to the next and previous match, and every match is highlighted.
- **Copy** copies all the text. **Save as** (Ctrl+S) writes the file somewhere, exactly as it came out of the archive.
- **Esc** closes the search bar first, then the window.

You can open several previews side by side.

Double-clicking anything that isn't text ticks or unticks it instead.
"""),

        new(Basics, "Settings", "", """
Open **Settings** from the toolbar, or press Ctrl+, (comma).

## Appearance

Choose **Light**, **Dark**, or **Use Windows setting** to follow whatever Windows is set to. The theme changes as you click, so you can try each one. **Cancel** puts it back.

## Extraction

The default output folder and the two extraction options. These are the same values as the bar along the bottom of the main window, so changing one changes the other.

## Game folder (optional)

Where The Witcher 3 is installed. It only affects decompiled scripts, and only how enum values are shown. Without it, a line might read `if (type == 1)`. With it, the same line reads `if (type == EACIT_Potion)`.

**Find automatically** checks your Steam and GOG libraries. Or use the folder button to pick it yourself: the folder that contains `bin` and `content`. A tick under the box means it found the game's scripts.

You can leave it empty if you only open scripts from inside the game's `mods` folder, because those find the game on their own. It matters for scripts opened from anywhere else, like a mod's release folder or a download. A change applies to files you open after saving.

## Where settings live

Everything is saved in `%AppData%\W3BundleExplorer\settings.json`. Delete that file to reset everything to the defaults.
"""),

        new(Basics, "Keyboard shortcuts", "", """
## Anywhere

- **Ctrl+O**: open files
- **Ctrl+F**: jump to the search box
- **Ctrl+E**: extract checked files
- **Ctrl+,**: settings
- **F1**: this help

## In the file list

- **Space**: tick or untick the highlighted rows
- **Enter**: preview the highlighted text file
- **Ctrl+C**: copy the highlighted files' paths
- **Ctrl+A**: highlight every row

## In the search box

- **Esc**: clear the search

## In a preview

- **Ctrl+F**: search
- **F3** / **Shift+F3**: next or previous match
- **Ctrl+S**: save as
- **Esc**: close the search bar, then the window
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

## Files won't extract to a folder

Check you can write to it. Program Files and the game folder itself often need admin rights. Pick a folder in your Documents or on another drive instead.

## Visual Studio says the exe is in use

If you are building the tool yourself, close any running copy first. Windows locks the exe while it is running.
"""),

        new(Basics, "About and licence", "", """
W3 Bundle Explorer is made by **Faen90** and is free to use.

## Licence

The tool is open source under the MIT Licence. In plain terms: you can use it, share it, change it and build on it, including in your own projects, as long as the copyright notice and licence text stay with it. It comes with no warranty. The full text is in the `LICENSE` file next to the exe, and in Settings under About.

Copyright © 2026 Faen90.

## Other people's work

The licence covers the tool itself, not the files you open with it. Game files belong to CD PROJEKT RED, and a mod's files and scripts belong to whoever made the mod. Decompiled scripts are a way to understand a mod, check compatibility or recover your own lost source. Please respect the author's wishes and licence before reusing or republishing their code.

## Not an official tool

W3 Bundle Explorer is an unofficial fan-made tool. It is not affiliated with, endorsed by or supported by CD PROJEKT RED. The Witcher, REDkit and REDengine are trademarks of CD PROJEKT S.A., mentioned only to describe what the tool works with.

## How it was made

W3 Bundle Explorer was developed by myself with the help of an AI assistant (Claude, by Anthropic). The AI did a lot of the heavy lifting on the compiled script side: working out the undocumented `.rsblob` format, mapping the script engine's instructions, and writing the decompiler that rebuilds readable code from them.

Nothing was taken on trust. The format work was checked against mods whose original source was available. Every decompiled function's size is verified against what the compiler recorded, and the output was compared line by line with the real scripts. If you do spot something that looks wrong, it's worth reporting, along with the mod it came from.

## Credits

- **WebSpam**: the WitcherScript syntax highlighting in the preview comes from his VS Code extension, witcherscript-vscode (github.com/webspam/witcherscript-vscode), used with his kind permission. If you write WitcherScript in VS Code, his extension is well worth a look.
- **AvalonEdit** by the SharpDevelop team: the code view in the preview window.
- **Aelto**: his own decompiler project, tw3-cahirb (github.com/Aelto/tw3-cahirb), filled real gaps in this one. The full list of the script engine's built-in operators, several rarely used instructions, and a sample from an older REDkit build that showed up property hints, array defaults and `do`/`while` loops all came from comparing notes with his work.

WebSpam's grammar and AvalonEdit are MIT licensed, and their full licence texts are in `THIRD-PARTY-NOTICES.md` next to the exe, and in Settings under About. No code was copied from tw3-cahirb, only facts about the file format.

The bundle reader, the LZ4 and Snappy decoders and the script decompiler are all written for this tool.
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
