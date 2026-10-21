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

The one thing that writes files is the script editor: a script in a project is saved when you press **Save**, and a compiled blob is written only where you tell it to go (see **Editing and compiling scripts**).
"""),

        new(Basics, "Opening files", "", """
There are three ways to get files in, and you can mix them freely. Everything you open stays listed until you close it.

## Open files

**Open files** (Ctrl+O) lets you pick one or more `.bundle` or `.rsblob` files. Holding Ctrl or Shift in the file picker selects several at once. A `.w3strings` file, or a strings `.csv`, opens in the strings editor instead (see **Editing strings**). A `texture.cache` opens like a bundle, with its textures listed (see **Texture caches**). A `.w3bench` project file opens the project (see **Projects**). A `.ws` script opens together with the rest of its scripts: the `scripts` folder it's in, or its own folder when there's none, as loose scripts that can be read, and edited once they're added to a project.

## Open folder

**Open folder** loads every bundle and compiled script inside a folder, including all of its subfolders. Each folder of `.w3strings` files is listed too, as one entry with all its languages; double-click a file in it to open it in the strings editor. A folder with a `.w3bench` file in it opens as that project. Loose `.ws` scripts open as loose scripts, one entry for each `scripts` folder (see **Projects**). The game's own script folders are left out. Some useful places to point it at:

- `The Witcher 3\content` for the base game's bundles.
- `The Witcher 3\dlc` for the expansions and free DLC.
- `The Witcher 3\mods` to see every mod you have installed at once.
- A single mod folder, if you only care about one mod.
- A REDkit project's `workspace\scripts` folder, to edit and compile its scripts.

The whole game is around 365,000 files and takes about a second to load, so don't worry about opening big folders.

## Drag and drop

You can also drop files or folders anywhere on the window.

## Recent

The clock button beside **Open folder** lists the folders and files you opened last, newest first, and the start screen shows the latest few. Click one to open it again. Ones that were moved or deleted drop off the list, and **Clear the list** at the bottom empties it.

## How things are named

Most mods name their bundle `blob0.bundle`, which isn't much help when twenty of them are open at once. So each entry in the tree is labelled with the folder it came from: the mod, DLC or content folder. The file name is shown greyed out next to it. Hover over an entry to see its full path.

When a mod has more than one thing open (say a bundle, a compiled script, a texture cache, its strings and its loose scripts), they're gathered under one entry with the mod's name and a folder icon, each listed by its file name. Selecting the mod lists every file in it, and **Close mod** closes all of it. A mod with only one thing open, the open project and the game's own scripts stay entries of their own.

Bundles have a box icon. Compiled scripts have a `{ }` icon, a project a `PROJECT` badge in the accent colour, loose scripts a page icon, a texture cache a picture icon, and a folder's strings files a language icon.

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
- **Script**: a loose script, read straight from disk.

Rest the mouse on a file's name to see its path and what kind of file it is: a mesh, a data buffer, an entity, a quest phase, a Flash panel, and so on. The preview pane says the same for files it can't show.

## The right-click menu

- **Compare the two** (Ctrl+D), with two files highlighted: text files open side by side in the preview, with the lines that differ marked (F8 steps through them). Other files are compared byte for byte, and a message says whether they're the same and where they first differ.
- **Other copies of this file** (Ctrl+Shift+D): every open archive that has a file at the same path, in load order: mod.io mods, then DLC, then mods by their `mods.settings` priority, then the game's own files. The copy the game uses is marked, and each one says whether its content is the same as the highlighted copy, with buttons to compare it, open it, or show it in Explorer. Open the whole `mods` folder first to see every mod's copy.
- **Copy**: the file's **Path** in the game (Ctrl+C), as XML and scripts refer to it; its **Name** (Ctrl+Shift+C); its **Folder**; or its **Location on disk**, the bundle or cache it's in.
- **Show in Explorer** (Shift+Alt+R): opens the folder of the bundle, compiled script or cache the file is in with it selected, or the file itself when it's a loose file. Right-click an entry in the tree for the same.
- **Export the list as CSV**: saves the files listed (or the highlighted ones, when there are several) as a table for Excel or any spreadsheet: name, path, kind, sizes, format, mod, archive and location.

## Thumbnails

**View** in the toolbar switches the list between **Details** and **small**, **medium** or **large thumbnails**. Textures and picture files show their picture, and other files an icon for their kind. Click a file to highlight it, Ctrl+click to add or take one away, Shift+click for a run of them, and use the arrow keys to move. Double-click opens a file, and the tick box in a thumbnail's corner checks it for extracting. The tool remembers which view you chose.

## The preview pane

**View > Preview pane** (Alt+P) shows the highlighted file beside the list, without opening it: textures and pictures as images (an `.xbm` finds its texture in a cache), meshes in 3D with their textures, cutscenes as a summary with a timeline of their events, scenes as how they start with their lines in words, scripts with WitcherScript colouring, XML with XML colouring, and other text as it is. With several files highlighted it says how many and their size. Drag the bar between the list and the pane to resize it.

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

## Include .buffer files

A mesh and some other files keep their bulk data in numbered `.buffer` files: `wsteel.w2mesh` goes with `wsteel.w2mesh.1.buffer`, and neither is any use without the other. The game packs the buffers in bundles of their own (`buffers0.bundle` beside `blob0.bundle`). With **Include .buffer files** on, extracting a file takes its buffers along from any bundle open in the same folder, and extracting a buffer takes the file it belongs to. The status bar says how many were added.

## Textures as PNG

Textures are extracted as `.dds` files with every mip level, which texture tools and the game's cooker read. Turn on **Textures as PNG** to get a picture instead: the full-size image with its transparency, as `name.xbm.png`. It applies to textures from a texture cache and to `.dds` files in bundles.

## CR2W files as JSON

Turn on **CR2W files as JSON** to get resource files (`.env`, `.w2mi`, `.w2ent` and the rest) as JSON to edit, as `name.env.json`. **Tools › CR2W and JSON** turns them back (see **CR2W files as JSON**).

## While it runs

Extraction runs in the background, with a progress bar in the status bar. **Cancel** stops it after the current file. Files that fail (very rare) are listed at the end with the reason, and everything else still gets written. If **Open folder when done** is on, Explorer opens the folder with the new files selected.

Right-click a file you've extracted and choose **Show extracted file** to find the copy again later.
"""),

        new(Basics, "Previewing text files", "", """
Text files can be read without extracting them first. That includes decompiled scripts, and the `.xml`, `.csv`, `.ws`, `.json` and `.txt` files found inside bundles, and the cutscene subtitles (`.subs`).

- Double-click a text file, or highlight it and press **Enter**.
- Or right-click it and choose **Preview**.
- XML files open in the preview too. For the XML editor, right-click one and choose **Open in XML editor**.

## In the preview

- Line numbers run down the left. Tabs show four spaces wide.
- WitcherScript (`.ws`) files are colour coded, using the same grammar as WebSpam's WitcherScript extension for VS Code. The colours follow the app theme.
- **Ctrl+F** opens a search bar. **Aa** matches case, **ab** matches whole words only, and **.\*** treats the search as a regular expression. **F3** and **Shift+F3** jump to the next and previous match, and every match is highlighted.
- **Copy** copies all the text. **Save as** (Ctrl+S) writes the file somewhere, exactly as it came out of the archive.
- Files in the open project can be edited, and the button reads **Save** instead. A loose script that isn't in a project asks about that first (see **Projects**). Everything else is read-only, which the line under the file name says for scripts.
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

- It looks in the script's own archive first, then in every other archive open in the main window, then in every mod installed in the game's `mods` folder, then in the game's scripts, which it finds the same way as for enum names (see **Settings**).
- The installed mods are read in the background as soon as a script opens: their compiled scripts, decompiled (well under a second for most installs), and the loose scripts of mods that ship them that way, such as Shared Utils. They're read once and kept until the explorer is closed. Loose files that replace one of the game's scripts are left out, and so is any mod whose archive is open, since many ship the same scripts both ways.
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
- **Show usage graph** (right-click menu): a picture of how a function, event, variable or type is used. The thing itself is on the right, the functions that use it are to its left, and the functions that use those further left, up to six levels out (the **Levels** box). Click a box to light up its chain of calls down to the target, double-click it to open it (**Ctrl** for the other group), and click the label on a line to open the call, or hover it to read the lines of code. Right-click a box for **Show usage graph of this**, which starts again from that function. Drag with either mouse button to pan, **Ctrl+wheel** to zoom, **Fit** to see it all, and **Stretch** to fill the window. Steps with many callers wrap into several columns.
- **Find in files** (**Ctrl+Shift+F**): plain text, whole words or a regular expression, through every script that's searched, comments and strings included.

These search the installed mods' scripts too, loose and compiled, as go to definition does.

## Comparing

- On a mod's loose copy of one of the game's scripts, **Compare with game version** in the right-click menu opens the game's file beside it and marks what's different: green for added lines, red for removed ones, amber for changed ones. **F8** and **Shift+F8** step through the differences.
- **Compare with an open file** does the same with any other file that's open, which is shown as the older version.
- **Stop comparing** takes the marks off both.
"""),

        new(Basics, "Texture caches", "\uEB9F", """
A `texture.cache` holds textures outside the bundles: the game's own is in `content\content0`, and DLC and mods ship small ones beside their bundles. Open one with **Open files**, drop it on the window, or find it under a mod in the asset browser's **Mods**. It is listed like a bundle, with each texture under its game path, and the **Format** column says how it's stored (for example `BC1 2048x1024 ×12`: BC1 colour, with 12 mip levels).

Double-click a texture, or select it and press **Enter**, to open the viewer:

- **RGBA** shows the picture over a chequerboard where it's transparent; **RGB** drops the transparency; **R**, **G**, **B** and **A** show one channel as grey, which is how masks and normal maps are checked.
- **Mip** picks a smaller level of the texture.
- **Face** picks one of a cube map's six faces (`.w2cube`), and **Slice** one of the textures in a texture array (`.texarray`), such as a wall's blend layers.
- **Fit** shows the whole picture in the window; turn it off to see it at its own size, and hold **Ctrl** and turn the wheel to zoom.
- **3D** puts the texture on a lit shape, as REDkit's texture viewer does: pick **Sphere**, **Box**, **Cylinder** or **Plane**, and move **Light** to light it from another side. Drag to turn the shape, use the wheel to come closer, and double-click to put it back. A cube map goes round the shape whole, each part showing the face it looks at; on the plane it shows what's ahead, as through a window. **2D** goes back to the picture.
- **Properties**, on the right, are listed as REDkit lists them. **Texture** at the top says what the cache holds: the format, size, mip levels, faces or slices, the memory it takes and its size in the cache. Below are the properties of the texture's resource file (its `.xbm`, `.w2cube` or `.texarray`) under each class from CResource down, such as the compression, texture group and resident mip, with those the file doesn't keep dimmed. For one of the game's own textures they're read from REDkit's source file when REDkit is installed and has one, as cooking leaves most of them out (see **Properties from REDkit**); a note above them says where they came from. The box above them narrows the list.
- **Save PNG** writes the level and channel shown. **Save DDS** writes the whole texture with every mip level, for tools that read `.dds`: a cube map as a cube map, an array as an array.

The same viewer opens picture files from bundles and projects: PNG, JPEG, BMP, GIF, TIFF and DDS (BC1 to BC7). A DDS keeps its mip levels.

A **texture array** (`.texarray`) opens in a window of its own, as REDkit's texture array editor shows one: every texture in it in a row (`texture_0`, `texture_1`…, with **Slots used** above them), the one picked large, and the array's properties beside it. Double-click a texture, or press **Enter**, to open it in the texture viewer with its mip levels, channels and 3D view. **Save PNGs** writes every texture in the array to a folder, and **Save DDS** the whole array as one `.dds`.

An `.xbm`, `.texarray` or `.w2cube` inside a bundle only describes its texture. Double-click one and the tool looks for the pixels in a texture cache under the same path: one that's open, the one beside the bundle, or the game's own, and says so if none has it.

Extracting a texture from the file list writes it as `name.xbm.dds`.

Formats it reads: BC1 and BC3 (colour, with and without alpha), BC4 (single-channel masks), BC5 and BC7 (normal maps and the Remastered's high quality textures), plain 32-bit colour and the environment probes' half-float cube maps. A texture in another format is listed, but the viewer says it can't show it yet. Normal maps are shown as stored, with the blue worked out for BC5.
"""),

        new(Basics, "Properties from REDkit", "\uE946", """
The game's files are cooked: cooking leaves out most of the properties REDkit's editors show, so a cooked cube map keeps none at all, and a texture, an entity or a behaviour tree only some. REDkit keeps its own copies of the game's files from before they were cooked, as loose files in its `r4data` folder.

So the dedicated viewers (textures, texture arrays, meshes, entities and behaviour trees) and the Properties window show the properties of one of **the game's own files** (from `content` or an official DLC) from **REDkit's copy** when REDkit is installed and has one. What they draw still comes from the file the game loads; only the property lists come from REDkit's copy. A note above the properties says where they came from:

- **Properties from REDkit's source file**, with its path in `r4data`: everything REDkit shows.
- **REDkit has no source file for this…**: REDkit hasn't got a copy of this one (it keeps only some meshes, for one), so only what the cooked file keeps is shown.
- **REDkit has a source file… in an older format**: REDkit's copy couldn't be read, so the cooked file's properties are shown. Every CR2W version REDkit has (back to 120, from early in the game's making) is read now, so this should only be a damaged file.
- **REDkit isn't installed**: install it (it's free on Steam and GOG for owners of the game) to see the properties cooking leaves out. It's found from the wcc_lite set in Settings, or in the usual Steam and GOG folders.
- **Properties from this file itself**: the file is from a mod, a project or somewhere on disk, so its own properties are shown, as that's what you're looking at. REDkit's copy would show the game's version instead.

A file opened from REDkit's own `r4data` is REDkit's copy already, and shows all of its properties.
"""),

        new(Basics, "Properties", "\uE946", """
Any of the game's resource files can be opened in a **Properties** window, as REDkit opens every resource in a property window even when it has no editor for it: a layer (`.w2l`), a grass mask, a terrain tile, a font, a particle system, a quest, an environment, and the rest. Double-click one that has no viewer of its own (or select it and press **Enter**); for any of them, right-click and choose **Properties**.

- **Objects**, on the left, lists every object in the file, each under the one it belongs to: a layer's entities with their components, a particle system's emitters with their drawers and curves, a font with its texture. The resource itself is at the top and picked first.
- An object with others under it has an arrow to close or open them (or **Left** and **Right** on the keyboard), and the buttons beside the title open or close them all. **Search objects** shows only the objects whose class or name has what you type, with the ones they're under.
- The object picked has its **properties** on the right, under each class from CResource down, as REDkit lists them, with those the file doesn't keep dimmed. Structs, arrays and the objects they point to fold open beneath them. The box above them narrows the list.
- For one of the game's own files, the properties come from REDkit's source file when REDkit has one, and the note above them says so (see **Properties from REDkit**).
- A file a property names is a link: click it to open the file in its viewer, or in another Properties window.
- **Open as JSON** opens the whole file as JSON, to read or edit; **Copy path** copies its game path.
"""),

        new(Basics, "Meshes", "", """
Double-click a `.w2mesh`, or select it and press **Enter**, to see it in 3D with its textures. The preview pane shows it too, smaller, and it turns there the same way. A mesh file on disk (one you extracted, or one of REDkit's own in `r4data`) opens from **Open files** or by dropping it on the window.

A mesh keeps its shape in its `.buffer` files and its look in its materials and textures, which are all in other files. The tool finds them the way the game would: in the archives you have open first, then in the bundles beside the mesh's own (the game keeps buffers in `buffers.bundle`), then in the game's bundles and texture caches, mods and DLC before the base game. So a mesh in the game's `blob.bundle` shows with its textures without opening anything else, and a mod's mesh shows the mod's textures when the mod is open.

## Looking around

- Drag with the left button to turn around the mesh, with the right button (or **Shift** and the left) to move it, and turn the wheel to come closer. Double-click, or press **F**, to fit it to the view.
- **Front**, **Side** and **Top** (or **1**, **2**, **3**) look at it from those sides.
- **LOD** picks the level of detail: 0 is the one used up close, and each one after it says how many triangles it has and from how far away the game uses it.
- **Textures** (**T**) turns the textures off, to see the shape in plain grey. **Wireframe** (**W**) draws the triangles' edges over it.
- **Floor** (**G**) draws a grid on the ground under it.
- **Show** draws how it's made: its **bounding box** (**B**), its **vertex colours** (**V**, each triangle in its corners' average, which is how hair keeps its colour), its **normals** (**N**), a skinned mesh's **bones** where they were bound (**K**), and its **collision** (**C**).
- The panel at the top left (**I** hides or shows it) says how many triangles, vertices and chunks the LOD shown draws, the mesh's size, the memory its data and its textures take, its bones and its collision shapes.

## Properties, LODs, Report and Collision

Beside the **Materials** tab, as REDkit's mesh editor has them:

- **Properties**: every property of the mesh, under the class that brings each in, with a search.
- **LODs**: each level of detail, the distance it's drawn from, and its chunks with their material, vertices, indices and vertices a triangle.
- **Report**: the memory the mesh's data and textures take, how far away it's drawn, each LOD's share of triangles for each material, and every texture the materials use with a picture of it, its format and size, and the materials that use it.
- **Collision**: the shapes the game collides with, read from its collision.cache (where a cooked mesh's collision is kept): each one's kind (convex or a triangle mesh), vertices, triangles and physical material. Boxes, spheres and capsules are counted but not drawn.

## Chunks and materials

A mesh is split into **chunks**, each drawn with one of its **materials**. Untick a chunk to hide it, for instance to see what's under a cloak.

Each material says where it's from: kept in the mesh, or a material instance (`.w2mi`), and what it's based on, down to the shader (`pbr_std`, `pbr_skin`…). **Textures** lists every texture it uses with the parameter it's in (Diffuse, Normal, TintMask…), its own and those it takes from its bases. **Colour texture** picks which of them is drawn, which is how you look at a normal map or a mask on the mesh itself.

- **Other texture…** puts any texture on the material's chunks: type part of its path. With nothing typed, it lists the textures beside the mesh and its own.
- **Other material…** draws the chunks with another material instance (`.w2mi`), with its textures.
- **Back to its own** puts the mesh's material back, undoing the changes that aren't saved.

## Changing materials

Open **Parameters** on a material to see every parameter its shader has: textures, numbers, colours (`#RRGGBB`, or `#RRGGBBAA` with alpha) and vectors (`x, y, z, w`). Those in bold are the material's own; the rest it takes from its base or the shader, which the tip says. Type a new value and press **Enter**, or pick a texture with **…**, and the parameter becomes the material's own; the undo button beside it takes its own value away again. A new colour texture shows on the mesh straight away. Other changes (colours, numbers) don't show, since the viewer doesn't run the game's shaders, but they're saved.

**Save to project** (Ctrl+S) saves the changes into the open project, never over the files they came from. Each file goes to its game path in the project's **workspace**, which Build packs into the mod's bundle, so the mod uses it in place of the game's:

- A material kept in the mesh is saved in the mesh, which goes to the project with its `.buffer` files.
- A material of its own (`.w2mi`) can be used by other meshes too, so saving asks: a **new material for this mesh**, or **change it for every mesh**, which puts a changed copy of the `.w2mi` in the project.
- A mesh that points straight at a shader (`.w2mg`) gets a new material instance of it, as the shader has nothing of its own to change.
- **Other material…** points the mesh at that `.w2mi` when it's saved.

**Save as new material…** saves a material, with its changes, as a new `.w2mi` wherever you choose in the project's game files, and uses it for those chunks; save the mesh afterwards to keep that.

With no project open, saving asks you to make one or open one first. Once saved, the project's copies are what the tool finds first, so opening the mesh again, from the project or from the game's bundles, shows your version. Closing the window with changes that aren't saved asks about them.

## Export OBJ

**Export OBJ** saves the LOD shown, with the chunks that are ticked, as a Wavefront `.obj` and `.mtl`, with each material's colour texture as a PNG beside them. Blender, 3ds Max and most other tools read it. It's turned to stand up as those tools expect.

## What it shows and what it doesn't

Each material is drawn as the game's own shader reads it: the normal map's creases and roughness, the tint mask's baked shadows and detail maps up close, skin's shine and pores, hair cut out by its alpha with a highlight along the strands, eyes' highlight, glass and what glows. It's lit from behind you, with light all round and reflections from one of the game's daylight probes, shadows and ambient occlusion. Dyes are only shown in the entity, scene and cutscene viewers, so some armour shows in the raw colours of its tint mask here. Foliage cards show as solid sheets. Skinned meshes are shown in their bind pose, without a skeleton. Textures that are missing from every cache, or in texture arrays, show as plain grey, and the material says why.

The game's cooked meshes (CR2W versions 162 to 164: the original game, and the Remastered with its DLC) and REDkit's uncooked ones are read.
"""),

        new(Basics, "Cutscenes", "\uE714", """
Double-click a `.w2cutscene`, or select it and press **Enter**, to open it in the cutscene viewer, laid out as REDkit's cutscene editor is: the actors played back at the top, the timeline below, and beside it the tabs REDkit has. The preview pane shows a cutscene too: how long it is, its actors and the entities they're made from, a timeline of its events and the scene that plays it. A cutscene file on disk opens from **Open files** or by dropping it on the window.

A cutscene is recorded: each actor (and the camera) has an animation for its body and one for its face, made for the whole length of it, and events are timed along them.

## Playing it

The actors are drawn as they are in the game: each put together from the entity it's made from, in the appearance the cutscene gives it, with its textures and colours, and its body animation played on its rig, so it moves where the cutscene puts it. Faces move too: each actor's face animation (eyes, brows, expressions) and the lip sync of each line it says, from the speech files beside the voices. Press **Play** (**Space**), **Home** to go back to the start, the loop button (**L**) to play it round again, or drag the bar; the line on the timeline shows where it is, and clicking or dragging on the timeline's ruler moves it. Drag the view to turn around, right-drag to move, use the wheel to come closer, and double-click (or the fit button) to see everyone again. **Through the camera** (**C**) shows it as the cutscene's camera sees it, with its field of view, in a 16:9 picture. The buttons beside it turn textures, wireframe and the floor grid on and off. The floor is laid under everywhere the actors go in the whole cutscene, at their feet. The actors load when **Load preview** over the view is clicked, so the events, properties and sound can be looked at straight away; the playback buttons and the view's settings wait until then. The actors and their textures are all loaded before it plays, with how far it has got shown in the view, so playback doesn't stop to load.

It plays with its sound, as REDkit does: its recorded music and effects (the audio track it names, from the game's sound caches) and each dialog line's voice (from the speech files, in English, when the line was found in the scene that plays it), mixed as they play. The music button turns the music and effects on and off and the microphone button the voices (**V**); **M** turns them both on or off. **Save sound…** in **Dialogs** saves the music and effects, each voiced line and the two mixed as WAV files.

Under the view, as in REDkit:

- **FOV**: the free camera's field of view (60° to start, as REDkit's). Through the cutscene's camera, its own is used, and shown on the right with the depth of field in force.
- **Aspect ratio**: the shape of the picture through the camera: 16:9, 21:9, 2.39:1 or 4:3.
- **Light position**: where the light comes from, round the actors.
- **Camera**: which of the cutscene's cameras to look through, when it has more than one.

REDkit also has shadows and an environment picker; those need the game's own renderer, which this viewer doesn't have.

When an actor is the player, the game dresses Geralt in whatever he has equipped, so the viewer shows him in the gear he starts the game in, as his default inventory gives it (the starting jacket, trousers, gloves and boots, his hairstyle, and the Long Steel Sword and Witcher Silver Sword on his back), and says so under the view. A piece made to be dyed is coloured as the game colours it: its texture's red and blue parts are placeholders that the appearance's colour shifts turn into its colours, worked out as the engine does it (the same shifts recolour people's clothes in the entity viewer, where a dyeable item opens undyed and each dye can be picked under **Appearance**). An actor that can't be drawn (no rig, entity or meshes found) is named there too, with why. Faces, items put in hands and effects aren't drawn yet.

## The timeline

The top lane, **Scene**, has the events of the whole cutscene: dialog lines, music, effects placed in the world, fades, clipping planes, depth of field and lights. Under it each actor has a lane with its own: items put in or taken from a hand, effects on the actor, appearance changes, morphs and cloth. An event that lasts is a bar, and bars that overlap go one under another; one that doesn't last is a tick. Click an event to see its properties.

- **Fit** (**F**) fits the whole cutscene to the width. **Zoom**, or **Ctrl** and the wheel over the timeline, makes it wider to see events up close.
- The buttons above it show or hide each kind of event, with how many there are. **Cloth** starts hidden: it's mostly resets of cloth and dangles, often hundreds of them.

## The tabs

- **Properties**: everything the event picked keeps, as REDkit lists it.
- **Cutscene**: who and what the cutscene spawns (actors, props and cameras), each with the entity it's made from (click it to open it), its appearance, voice tag and tags, and its animations as REDkit names them (`actor:Root:animation`). Click one to list only its events in **Events**.
- **Template**: the cutscene's own properties, from REDkit's source file for one of the game's own when REDkit has it (see **Properties from REDkit**).
- **Effects**: the effects the cutscene defines for its effect events, with the particle systems, materials and lights they're made of; click one for its properties.
- **Animation**: each actor's body and face animation, its properties and its set entry's, and its event track.
- **Dialogs**: each line with when it's said, who says it, its words and the sound event that plays it. Dialog lines are only marks in a cutscene: the words and speakers are in the scene that plays it, which the viewer reads (in English) when its cutscene section has as many lines as the cutscene has marks. Click a line to go to it.
- **Scenes**: the scenes (`.w2scene`) that play it; click one to open it in the scene viewer.
- **Camera**: what the cutscene's camera does at the time shown: its field of view, where it is and where it looks, and the depth of field, clipping planes and fade in force.
- **Events**: every event with its time, whose it is, its kind, what it is and the rest of what it says. Search (**Ctrl+F**) finds part of any of those, or of the event's class. Right-click a row to copy it.
- **Files**: the entities, particle systems and materials it refers to, the scenes that play it and the world it was made in. Click one to open it in its viewer, found the way the game would find it (any resource without one opens in a Properties window); right-click for **Copy path**.

The music and effects are the cutscene's recorded sound, the audio event named under **Cutscene**; the voices are the dialog lines', played where the cutscene marks them.
"""),

        new(Basics, "Entities", "", """
Double-click a `.w2ent`, or select it and press **Enter**, to open it in the entity viewer. The preview pane shows its meshes put together, in its first appearance. An entity file on disk opens from **Open files** or by dropping it on the window.

An entity template is what the game spawns: an NPC, a horse, a door, a prop. It's an object of a class (`CNewNPC`, `CGameplayEntity`…) with **components** (meshes, a skeleton, sound emitters, triggers, effects…) hung from one another, **appearances** (the looks an NPC can have, each a list of templates for its head, body and armour), templates it **includes** and builds on, **slots** to put things in, and **effects**.

## The view

On the left are the entity's meshes put together where its components place them, with their colour textures, in the **Appearance** picked: its own meshes and those of every template the appearance includes. Drag to turn, right-drag to move, use the wheel to zoom and double-click to fit. **Textures** (**T**) and **Wireframe** (**W**) as in the mesh viewer; **Floor** (**G**) draws a grid on the ground under the entity; the panel at the top left of the view (**I** hides or shows it) says how many triangles, vertices, meshes and chunks are drawn, the LOD, the size, the skeleton, and while an animation plays how many frames a second are drawn and how long each pose takes; **Fit** (**F**). **LOD** picks the level of detail, and the line under the view says how many triangles and vertices it draws. Click a part to pick its component. Colours are worked out as the game's shaders work them out: hair takes its colour from the colours kept on its mesh's vertices, and a component the appearance recolours (its **Colors** entry) has the red and blue parts of its texture shifted as the entry says, where its tint mask allows. Materials are drawn as the game's own shaders read them (normal maps, roughness, detail maps, skin, hair, eyes, glass and what glows), lit from behind you with light all round and reflections from one of the game's daylight probes, shadows and ambient occlusion. The lighting is the viewer's own, not the scene's. Cloth (a long coat, a skirt, a cape) is drawn in the shape it has before the game moves it, and follows the bones it's bound to in an animation, without swaying. Characters stand in the pose their meshes were made in; shadow-only meshes aren't drawn.

## Animation

When the entity has a skeleton and skinned meshes, the bar under the view plays its animations on them: pick an **animation set** (those the entity's animation settings name) and an **animation** (with its list open, type the start of a name to jump to it), and it plays, looping (an entity opens in a standing idle from its own sets when it has one, and the animation picked carries on when the appearance changes), with the actor brought back to the middle of the view and turned the way the entity faces (many animations are made for a place in a scene, off to one side or turned). Play and pause with the button or **Space**, drag the bar to any moment, and the stop button stands it as bound again. The frame rate box beside the bar sets how many times a second it's drawn (15 to 60, or **Max** for as often as the screen draws); fewer is lighter on the computer, and the choice is remembered. The speed box beside it plays it slower or faster, from 10% to 200% of the game's speed. The face's and dangling bones (hair, medallions) have rigs of their own and move with the head or the body, stiffly; the face's own sets aren't listed. Items held in slots don't move with it, and cloth keeps its shape rather than swaying.

## Components

On the right are the components as the entity editor lays them out, with a line from each to the ones attached to it (dashed for meshes skinned to a skeleton): the entity's own, those from the templates it includes, and those of the templates the appearance picked uses. Each says which template it comes from, and is marked as the editor marks them: **I** included, **A** from the appearance, **O** overridden by this template. Above them is how many are streamed, the streaming distance and how many resources they use. Click a mesh component and its mesh glows in the 3D view, with how many triangles it has. Drag to move around, use the wheel to zoom, and double-click to see them all. Click one to see every property it has under **Component**, with what it's attached to, as REDkit shows them: under the class that brings each one in (`CNode`, `CComponent`, `CDrawableComponent`…), with transforms, tags and enums in words. Properties the file leaves at their default are listed dimmed. A property that refers to a file opens it. **Components** lists them all, and picking one finds it in the graph.

**Entity** has the properties of the object the game spawns. **Template** has the entity's class, the templates it includes, and every file it depends on, following its includes and appearances as far as they go (**Show only entity templates** narrows the list), then the template's own properties. Structs, arrays and the objects a property points to fold open and shut with the arrow beside them, and a bool shows a tick or a cross.

The tabs after them are REDkit's, each a list with the properties of the one picked beside it, and "(included)" on what comes from a template the entity includes: **Appearances** (with the templates each uses), **Colors** (the colour shifts of a component in an appearance), **Slots**, **AI** (its profile, base tree, presets and attack ranges), **Inventory** (what it spawns with), **Voicetags**, **Animation** (animation sets, behaviour graphs, constraints, mimics), **Gameplay** (stats, abilities, vital spots and the rest of its params), **Animation Slots** and **Dismemberment** (its wounds). **Effects** lists the effects it can play, and **Files** everything it refers to. Click a file to open it where the tool has a viewer for it.
"""),

        new(Basics, "Behaviour trees", "\uE8F9", """
Double-click a `.w2behtree`, or select it and press **Enter**, to open it in the behaviour tree viewer. A tree an entity or another tree uses opens from it too, and a file on disk opens from **Open files** or by dropping it on the window.

A behaviour tree is the AI an NPC runs: from its root, nodes that pick among their children by priority or run them in order (**composites**), nodes that wrap one child with a delay, a loop, a priority or a variable (**decorators**), nodes that let their child run only when a test passes (**conditions**), tasks written in WitcherScript (**script tasks**), and nodes that do something themselves, such as idle, play an animation or move (**actions**). **Subtrees** run another tree, and **comments** are notes in the tree.

## The tree

The tree is drawn from its root down, each node's children in a row under it in the order they're tried, each node coloured by what it is (the colours are listed under the tree). A node says what it is or the task it runs, its main settings (the variable it tests or sets, the animation event, the distance) and, at its top right, its priority. Drag to move around, use the wheel to zoom, and double-click the background (or press **F**) to see it all.

- **Double-click a node** to fold its branch away; it says how many nodes are folded under it. Double-click it again to open it. The box at the top folds every branch below a level, to see the tree's shape.
- **Find** (**Ctrl+F**) marks the nodes whose name, class, task, settings or comment has the words, opens the branches they're in and goes to the first. **Enter** goes to the next, **Shift+Enter** to the one before.

## The node picked

Click a node to see every property it has on the right, under the class that brings each one in, as the entity viewer shows them, and then those of the script task it runs. A value a behaviour variable can set says which. The search above the list narrows it, and right-click a property to copy it.

Every behaviour tree in the game reads (390, base game and DLC).
"""),

        new(Basics, "Spawn trees and steering", "\uE81E", """
Double-click a `.spawntree` or a `.w2steer`, or select it and press **Enter**, to open it in its viewer. One a property refers to opens from the other viewers too, and a file on disk opens from **Open files** or by dropping it on the window.

A **spawn tree** is what spawns a community's or an encounter's creatures: from its root, branches that run all their children (**parallel**), one at random, or only when facts are as they say (**condition**, which says each fact it tests), and entries that spawn at times of day, down to the **spawn entries** themselves (how many of which creature, and where), **parties** and the **work smart AI entries** a generator makes. **Initializers** set up what's spawned under them: where it guards, its AI, its attitude, the action points it works at. Under the tree are the **creature definitions** the entries spawn by name: each one's entity template, how many may be spawned in all and at once, and its forced appearance and tags.

A **steering graph** is how an NPC moves towards its goal: **groups** run all their children, a **condition** runs its first child when its test passes and its second when it doesn't (marked **(0) true** and **(1) false**), and **tasks** are each one steering rule (keep away from walls, strafe round the target, keep a distance). Each node says its task or test and its main settings.

Both are drawn as the behaviour tree viewer draws a tree, from the root down, coloured by kind (the colours are listed under it), with a label over a node saying why its parent goes to it: a node's comment (a time of day, a quest fact), or its place among its parent's children. Drag to move, use the wheel to zoom, double-click a node to fold its branch, and **Find** (**Ctrl+F**) marks the nodes whose name, class, comment or settings have the words. Click a node to see its properties on the right (a steering task's or test's under it), and the button by **Fit** to see the file's own. Every one of REDkit's spawn trees and steering graphs reads (558).
"""),

        new(Basics, "Job trees", "\uE7C1", """
Double-click a `.w2job`, or select it and press **Enter**, to open it in the job tree viewer. A job tree is what an NPC does at an action point: nodes that run their children in order (**sequence**) or pick one at random, some times over or looping, with the **actions** they play on entering, leaving and being made to leave in a hurry. Each action is an animation of its job category's set (`quest_man`, `work_woman`…), at a place, with an item in hand maybe.

The tree is drawn as the behaviour tree viewer draws one: nodes say how they run and for which categories, actions their animation and where, labelled **enter**, **leave** or **fast leave**. Click one for its properties under the tree; with none picked (or with the button by **Fit**), the job tree's own settings show: how the NPC moves to it, and what happens to what it holds.

Above the properties a character plays the action picked, in 3D, as REDkit's job tree editor previews it: from its category's animation set (the game's table of them, `gameplay\globals\animations.csv`), on a man, a woman, a child or a dwarf as the category is for, or the one picked in **Preview on**. When the game's copy of the set hasn't got the animation, the other categories' sets are looked in, and it says where it was found. Beside it is the category's set: pick another category, narrow the list by some letters, and click any animation to play it. Play, pause, drag to any moment and change the speed under the view.

Every one of REDkit's job trees reads (1,057).
"""),

        new(Basics, "Animation events", "\uE8D6", """
Double-click a `.w2animev`, or select it and press **Enter**, to open it in the animation events viewer. An animation events file keeps events timed to a set of animations (sounds, footsteps, effects, things scripts hear) apart from the animations, so they can be worked on alone. Each event names its animation and its track, and when it starts (and for how long, for those that last).

The window is laid out as REDkit's animation events editor: the animation picked playing on a character in 3D at the top left, the file's animations at the top right, and the picked animation's events along a timeline underneath.

- **The animation set** the events are for is found by the file's name (`man_axe_1handed_sounds.w2animev` is for `man_axe_1handed.w2anims`), or else by which set in folders like the file's has the most of its animations; or pick one in **Animation set**. The note over the list says how many of the file's animations are in it, and those that aren't are dimmed.
- **The character** is Geralt, a man, a woman, a child or a dwarf, as the file's folder is for; pick another in **Preview on**. Play, pause, drag to any moment and change the speed under the view; **Space** plays and pauses.
- **The animations** say how many events each has; narrow them by some letters (**Ctrl+F**). Click one to play it and see its events.
- **The timeline** has a row for each track, each event marked where it starts (blue for a sound, brown for a footstep, orange for an effect, a bar for one that lasts) with the sound or effect it plays, a ruler of seconds, and a line that follows the animation. Click an event to go to it and see its properties under **Event**; click the ruler or a row to go to that moment; the wheel zooms the time.

**File** has the file's own properties, from REDkit's copy when there is one. Every one of REDkit's animation events files reads (346).
"""),

        new(Basics, "Animation sets", "\uE714", """
Double-click a `.w2anims`, or select it and press **Enter**, to open it in the animation set viewer. An animation set keeps the animations a kind of character plays for something (a weapon's attacks, a job's work, a creature's moves), each with the events it fires (sounds, footsteps, effects, things scripts hear), and names the events files kept apart from it.

The window is laid out as REDkit's animation browser: the animation picked playing on a character in 3D at the top left, the set's animations at the top right, and the picked animation's events along a timeline underneath.

- **The animations** are in a table: each one's length, frames, events, how far it moves and turns the character, how many bones it moves and the data it keeps in the file. Click a heading to sort by it, narrow them by some letters (**Ctrl+F**), and click one to play it.
- **The character** is Geralt, a man, a woman, a child or a dwarf as the set's folder is for, or a creature whose template is named as the set's folder is (the bear's set plays on a grizzly); pick another in **Preview on**. An animation made for another build plays in proportion. Play, pause, drag to any moment and change the speed under the view; **Space** plays and pauses.
- **Bones** draws the skeleton over the character as it moves, **Names** writes each bone's name beside it, and **Trace** draws the path a bone takes over the whole animation (pick `r_weapon` to see a sword's swing), as REDkit's trajectory tool does.
- **Animation** has the animation's properties, from REDkit's copy when there is one, and its motion extraction: where it has moved and turned the character by its end and at the moment shown. The character stays in place in the view, as the game moves it along that.
- **The timeline** has a row for each track, the set's own events and those of the events files it names (from REDkit's copies, which keep their tracks' names). Click an event to go to it and see its properties under **Event**.
- **Loop** plays the animation on, or stops it on its last frame. **Blend from** blends it in from another of the set's animations (its idle is picked for you, as the character would stand before acting), from the pose the rig rests in, or not at all, and back out at its end, over the seconds in **in** and **out** (REDkit's 0.2 to start), as the game blends one animation into the next. The arrow keys step a frame back or on.
- **Character's sets** lists every animation set the character's templates name (and those they include), this one in bold, and says when this set isn't one of them: in the game the character plays its animations only when a behaviour or a scene brings it. Double-click one, or press **Enter**, to open it.
- **Right-click an animation** to copy its name (**Ctrl+C**) or every name shown, blend the others from it, or open the events file the set names.

**Properties** has the set's own properties. Every one of REDkit's animation sets reads (450), and 25,293 of their animations play.
"""),

        new(Basics, "Rigs", "\uE77B", """
Double-click a `.w2rig`, or select it and press **Enter**, to open it in the rig viewer. A rig is a skeleton: its bones, each under a parent, in the pose they rest in, which meshes are bound to and animations move.

The window is laid out as REDkit's skeleton editor: the skeleton in 3D on the left, its bones on the right.

- **The view** draws a line from each bone to its parent, faint for the helper bones hung from the root (IK targets, the trajectory). **Bone names** writes each bone's name beside it, **Bone axes** draws each bone's axes (X red, Y green, Z blue), and **On a character** shows the skeleton inside a character made on it (a base character's rig, or one beside it), to see where the bones sit in the body. **Fit** (**F**) frames the whole skeleton.
- **Bones** is a tree from the root, each bone numbered as the rig numbers it; narrow it by some letters (**Ctrl+F**). Click a bone to pick it out in the view and see where it is from its parent (position, rotation in degrees and as a quaternion, scale) and in the model.
- **Tracks** lists the float tracks animations can drive beside the bones.
- **Right-click a bone** to copy its name (**Ctrl+C**), its chain from the root (`Root > pelvis > torso > …`) or every bone numbered with its parent, or to open or close every branch. A bone picked lists the bones under it and its chain.

**Properties** has the rig's properties (its speeds, LODs and control rig), from REDkit's copy when there is one. Every one of REDkit's rigs reads (420), the oldest (137 and before) too.
"""),

        new(Basics, "Communities", "\uE716", """
Double-click a `.w2comm`, or select it and press **Enter**, to open it in the community viewer. One a property refers to opens from the other viewers too, and a file on disk opens from **Open files** or by dropping it on the window.

A community is who lives somewhere and when. Its type is at the top (**CST_Global**, or **CST_Template** for one other communities use), with how many entries, story phases and timetables it has.

**Spawnset** lists its entries as REDkit's community editor does: each entry's ID and comment, then a row for each of its entities (the entity template spawned, its appearances, spawn tags and map pin) and each of its story phases (the quest tags it spawns in, whether it's hidden, then each spawn time: when, how many, and the respawn delay), with the timetable the phase keeps, where they spawn and despawn, and whether they start in an action point. An entry's rows are kept together, every other entry shaded. Click a row to see its entry's, entity's, story phase's and spawn time's properties on the right; double-click it (or **Open the template**) to open the entity template, and **Its timetable** goes to the story phase's timetable.

**Timetables** lists each timetable by name: at each time of day, the layer whose action points it uses, and each action category there with its weight and the action point tags it takes. Click a row to see the timetable's properties and which story phases keep it.

The search at the top (**Ctrl+F**) narrows both lists to the entries and timetables with the words (an entry, a template, a story phase, a timetable, a tag). Every one of REDkit's communities reads (2,515).
"""),

        new(Basics, "The journal", "\uE82D", """
Double-click a `.journal`, or select it and press **Enter**, to open the journal viewer with its entry picked. One window shows the whole journal, as REDkit's journal editor does: every journal file the game, the archives open and the project have, read together (1,521 in the game, with its DLC). A journal file from a mod or a project is shown as it is in that copy.

Each journal file keeps one entry, which names the entry it goes under by its GUID; the tree puts them together. Under **Quests** are the quest groups (Prologue, the chapters, the side quests, each DLC's, marked "dlc: bob" and so on), each quest with its **Descriptions**, its phases, **objectives** and **map pins**; then **Characters**, the **Bestiary**, the **Glossary**, **Tutorial** entries, **Places** and the **Storybook**. Each kind of entry has its own icon and colour, a group says how many it holds, and the tooltip says an entry's title in words.

Click an entry to see its texts in words (its title, name or description, in the language last picked in the scene viewer, with a quest's objectives listed), its kind, class, unique script tag and file, then every property it has, from REDkit's copy of the file when there is one. **Copy the script tag** copies the tag scripts find it by, and a quest's **quest phase** opens from it.

**Find** (**Ctrl+F**) lists the entries whose name, script tag or texts have the words, with where each is; pick one to go to it. **Tools** has REDkit's checks: duplicated unique script tags, duplicated GUIDs, and entries without a script tag.
"""),

        new(Basics, "Scenes", "", """
Double-click a `.w2scene`, or select it and press **Enter**, to open it in the scene viewer. The preview pane shows it too: how many parts, lines and choices it has, how it starts with its lines in words, who's in it, the cutscenes it plays, and the facts and scripts it uses. A scene file on disk opens from **Open files** or by dropping it on the window.

A scene is the flow of a conversation, or of the cutscenes a quest plays: from a **Start**, through **sections** that play lines, choices, cutscenes and videos, and through branches (a **Condition** on facts, a **Switch**, a **Random** pick, a **Script** run), to an **End**. A quest starts a scene at one of its starts and goes on from the end it reaches.

The window is laid out as REDkit's scene editor is: the scene's own tabs above, the flow below, and what's picked at the side.

## The screenplay

**Screenplay** shows every part as a card, numbered in the order the flow reaches it from the starts; parts nothing leads to come last. A section shows its lines with who says them and to whom, and its choice with each option numbered, what it needs or does (pay, leave, only if a fact is set, picked when time runs out) and where it leads; the options the game picks out (the story's way on, REDkit's emphasised lines) are outlined. A branch shows its condition and where each way goes. Click any **→** link to go to that part, and **Back** (**Alt+Left**, or the mouse's back button) to return. A cutscene section links to its cutscene, which opens in the cutscene viewer.

## The graph

**Graph** (below) draws the flow: a box for each part, coloured by what it is, with its first lines, its options or its condition, and a curve from each way out to where it goes, named when the way has a name. For one of the game's own scenes with REDkit installed, it's laid out as the writers laid it out in REDkit (REDkit's copy keeps the graph; the game's doesn't); otherwise left to right by the flow. Click a box to pick the part; **Ctrl** and the wheel zoom, the right or middle button drags, and the fit button shows the whole flow.

## The rest of the scene

- **Starting Conditions**: who each start needs, by voice tag, and what's asked of them (must be the context actor, invulnerable, optional), with the start's settings below.
- **Definitions**: the actors and props, with their tags, the entity each is made from (click to open it), how many lines each has, appearance filters and settings.
- **Files**: the entities, cutscenes and dialog sets it uses.
- **Dialog sets** (below): where the actors stand in each of the scene's dialog sets: each place, who stands there, where and which way they face, and the set's settings.
- **Properties** (side): the part picked, from REDkit's copy for one of the game's own scenes (a note says where they came from, as in the other viewers).
- **Cameras**: the cameras the scene defines by name, with their lens and position; click one for its settings.
- **Animations**: every animation the sections' timelines play, by actor and kind (body, face, additive, camera), and how often.
- **Section**: the picked section's versions: the game can time a section to each language's voices, and plays the default for any without its own.

## Hearing it

The ▷ beside a line plays its voice, from the game's speech files (`enpc.w3speech` and the DLC's, in the language picked, and in English for any line it has no voice for: the game installs the voices of one language, set in Steam or GOG apart from the text). **Read through** (**R**) plays the scene from the part picked, or from the start, as the game would: it opens the **Preview** and plays each section on its timeline, its lines said at their times as its actors play it (a cutscene section its cutscene, with its music), marking the line being spoken and following the flow from part to part. At a choice it shows "Choosing…" for a moment and picks an option at random, leaving out a shop, a barber, Gwent and the other services a trader offers unless there's nothing else (click one meanwhile to go that way instead); at a condition or a random pick it goes a random way; and it stops at an end. Click **Stop** to stop. While it reads, the timeline's **Play** waits, and the other way round.

## Finding your way

**Search** (**Ctrl+F**) keeps the cards whose lines, speakers or names match. **Pauses** shows the pauses between lines. **Language** picks the language the lines are shown in, read from the game's strings; without the game folder set they show as string IDs. The writers' comments are shown where the game's strings have them.

**Timeline**, under the screenplay, shows the section picked as REDkit's timeline does (a cutscene section its cutscene, with its lines where the cutscene says them): its lines and pauses along the top, coloured by speaker (each line at least as long as its voice in the language picked, so none is cut off, with the events on it keeping their places along it), then its camera cuts (by the camera they cut to), lights and fades, and for each actor their body animations, faces and looks, timed along the lines. Animations show where they blend in and out, and events that happen at a moment are diamonds. Click an event for its properties at the side, or click the ruler to move the line that shows the time. **Play** runs the section from there, saying each line when it comes; paused part way through a line, it goes on from there. The fit button fits the section to the width; the slider zooms in to see events up close.

**Preview**, beside the screenplay, plays the section picked in 3D, in time with the timeline: its actors put together from their entities (textured and coloured, the player in Geralt's default gear) and stood where the section's dialog set puts them, each in the idle the game picks for how they stand (from its `scene_body_animations` tables), with what the section does to them played at its times:

- their body animations, changes of stance and additive gestures, and walks: an animation that walks or turns them carries them over the ground as the game does (by its motion extraction), so they arrive where the next placement has them;
- their faces, as the game moves them: the face's pose, eyes and idle (which blinks), its expressions and nods, and each line's **lip sync**, read from the speech files beside its voice;
- who they look at, turning their head (or their upper body too) and their eyes;
- where the section moves them, and when it hides or shows them;
- the scene's **props** and the items they take up: a prop where the section places it or in the hand (or on the slot) it's attached to, and an equipped item (a broom, Geralt's sword) in the hand it's taken up in, until it's put away.

**Through the camera** sees it through the section's cameras in a 16:9 picture, cut as its camera events cut and moved along their blends' curves, with the section's camera shake on top (the hand-held `medium_shake` most dialogues have, a drunk or a hit's shake), the line being said as a subtitle and the section's fades; turn it off (or press the fit button) to look round freely. The lights the scene sets up (a campfire, a candle, a key or rim light) light the actors with their colours, brightness and reach, spots shining the way they're turned. A cutscene section plays its cutscene, as the cutscene viewer does. An actor the scene gives no appearance is shown in the one its entity gives its voice tag, as a crowd character is in the game.

Nothing is loaded until it's asked for, so looking through the tabs stays quick: the preview shows **Load preview** (and its buttons wait) until it's clicked, and **Read through** loads it too. Then everything is loaded first, as REDkit does, so it plays smoothly: the actors, their textures, the props and every cutscene the scene plays, with how far it has got shown in the view (**Cancel** stops it, and Load preview starts it again). Poses are worked out in the background, so a heavy scene shows fewer frames, still in time, rather than holding the window up. Cloth, the camera lights that follow the camera, and lights placed in world coordinates aren't played, and a few props kept in an older file format aren't drawn yet.
"""),

        new(Basics, "Projects", "\uE821", """
A **project** is where you make your mod. It's a folder of your own, outside the game, with a small `.w3bench` file (plain JSON) in it:

```
My Project\
  My Project.w3bench
  scripts\        the mod's scripts
  workspace\      copies of the game's files you change, each at its game path
  build\
    modThisIsMyMod\   what Build makes, ready to install
```

The `.w3bench` file keeps:

- the mod's **scripts folder**: every `.ws` file in it is part of the project, and it's what **Compile scripts** and **Build** compile;
- the mod's **workspace** (`workspace` unless the `.w3bench` says otherwise): meshes, materials, XML and other game files at their game paths, which Build packs into the mod's bundle;
- the **mod name**, the folder Build makes and Install puts in the game's mods folder (`modThisIsMyMod`; the game only loads folders that start with `mod`);
- the project's **other files and folders**: menus, strings, notes, anything you add;
- **where Compile writes** the `.rsblob`;
- a name, author, version and description.

## The game's files are never changed

Everything that comes from the game is read-only, everywhere in the tool: files inside bundles, texture caches and compiled scripts, and every file in the game's folder, its DLC and the installed mods (other people's work, and your own mod's installed copy, which the next Install replaces). REDkit's own files (its `r4data` and its programs) are read-only too, and so is the project's `build` folder, which Build makes new each time. Typing in one of these says why and offers **Copy to project**; saving one asks where in your project it goes; extracting into the game's folders, or making a project there, is refused.

**Copy to project** (right-click files in the list, or the offer in an editor) puts a copy in your project at the same game path: a script in the project's scripts folder (`content0\scripts\game\x.ws` becomes `scripts\game\x.ws`), anything else in the workspace, with a mesh's `.buffer` files along with it. The copy opens, ready to change. A file the project has already is left as it is, so nothing of yours is overwritten. Textures in a texture cache aren't copied yet, as their pixels aren't a game file of their own.

Your own files anywhere else (a folder of scripts in Documents, say) can be edited as before.

Two things do write into the game's folder, and only because you asked: **Install** (below), into the mod's own folder under `mods`, and Compile, when the project is set to write its `.rsblob` into an installed mod's folder. The scripting tools' key binding helper can also change the game's `input.xml`, after asking, keeping a `.bak` copy.

## Build and Install

**Project > Build** (Ctrl+Shift+B) makes the mod in the project's `build` folder, ready to install:

- the scripts are compiled into `content\precompiled.rsblob`, the way REDkit publishes a mod, and if any don't compile, nothing is built and **Show errors** lists them, each opening its line. They can also go in `content\scripts` as they are: on their own, for the game to compile when it starts, or beside the compiled ones as the mod's source, with an `info.json` that tells the game to run the compiled ones. The first build asks which; **Project properties** changes it later (**When the mod is built**);
- the workspace's files are packed into `content\blob0.bundle` (and `buffers0.bundle` for meshes' buffers) with the `metadata.store` the game needs, its textures' mip levels go in `content\texture.cache`, and its sounds (`.wem`, `.bnk`) in `content\soundspc.cache`, where the game looks for a mod's sounds. The tool makes all of these itself, the way REDkit's `wcc_lite` makes them (byte for byte, checked against it on real mods), so building doesn't need REDkit. A `.cache` file at the top of the workspace (a `collision.cache` made with REDkit, say) goes in `content` as it is;
- the project's `.w3strings` files go in `content`.

Textures are cooked as the mod is built. A REDkit source `.xbm` (from REDkit's import, or its `r4data`) is cooked exactly as REDkit cooks it. A picture is made into the texture of its name: `x.dds`, `x.tga` or `x.bmp` becomes `x.xbm`, and so does `x.png` or `x.jpg` when the workspace or the game has an `x.xbm` (otherwise a `.png` goes in as it is, as GUI icons do). A picture that takes the place of one of the game's textures keeps that texture's group and compression. A `.dds` keeps its own format and mip levels (one saved from the game's texture comes out exactly as the game's); other pictures get a full mip chain, block-compressed to the texture's format (BC1, BC3, BC4 or BC5; a BC7 texture becomes BC3). A new texture is a normal map when its name ends in `_n`, and keeps its alpha when the picture has any. Anything worth knowing about a file (a picture that couldn't be read, a `.dds` in another format than the texture it replaces) is listed when the build is done.

Files copied from the game's bundles (with Copy to project, or changed in the mesh viewer) are cooked already, and XML and other text files need no cooking, so they go in as they are. REDkit's other source files (meshes, entities, cube maps, texture arrays, from its `r4data` or a REDkit project's workspace) aren't cooked yet, and the game can't load them. Before it builds, Build lists any of these in the workspace and asks whether to go on, and Copy to project says so when a copy isn't cooked. A file is cooked when the game's cook has marked its objects, which is how WolvenKit tells them apart too.

**Project > Install** copies the build to the game's `mods` folder under the mod's name, after saying where. It replaces only that one folder. If a folder of that name is there and wasn't installed from this project (someone else's mod of the same name, or one copied in by hand), it asks before replacing it. A small `w3bench-install.json` in the installed folder records which project installed it.

Paths in it are relative to the file, so the folder can be moved, zipped or kept in git and the project still opens. Which tabs you had open is kept separately, in the tool's own settings folder, not in the `.w3bench` file, so sharing a project doesn't share your tabs.

## Making and opening a project

**Project > New project** asks for a name, the mod name (made from the name until you change it), the project's folder (`Documents\W3 Mod Workbench\Name` unless you choose another) and the scripts folder, and makes `Name.w3bench` with `scripts` and `workspace` folders. A folder inside the game's or REDkit's folders isn't accepted. **Project > Open project** (**Ctrl+Shift+O**), a `.w3bench` file dropped on the window or opened from Open files, or a folder with one in it, opens an existing one. **Recent projects** lists the last ten. One project is open at a time: opening another closes the first, asking about unsaved changes. When the tool starts it opens the project you had last, with the scripts that were open (turn this off in Settings).

To turn a folder of loose scripts into a project, right-click its entry in the tree and choose **Create a project from these scripts**. The project goes in the mod's folder above the scripts, and the name and scripts folder are filled in for you. **Edit as project** on a decompiled mod makes one as well.

## What the tree shows

The project is the first entry in the tree, with a `PROJECT` badge and its name. Under it are its scripts and its other files, laid out as they are in the mod's folder. Loose scripts opened with Open folder or by dropping a `.ws` file are listed separately and aren't part of it. The game's scripts and mods are never part of a project.

Right-click the project for **Project properties**, **Add files to the project**, **Refresh the project** (after files change outside the tool), **Open the project folder** and **Close project**. Right-click files in the list for **Add to project** (on loose files) or **Remove from project** (on files added by hand). Removing only takes the name off the list: the file is never deleted. XML files in a project open in the XML editor on double-click.

**Add files to the project** links files where they are, and ones outside the project's folder are listed under **external**. A script (`.ws`) only compiles with the project from its scripts folder, so for a script from anywhere else it asks first: **Copy into scripts** puts a copy in the scripts folder, where it's compiled with the mod (the original is left alone, and a script with the same name there already isn't overwritten), while **Add where it is** links it as before, listed with the project but not compiled.

## Editing files that aren't in the project

Any loose script can still be opened, read and followed with go to definition. The first time you type in one that isn't in the open project, the editor stops and asks:

- with a project open: **Add to project**, or **Edit without adding**;
- with no project open: **Create project\u2026** from that script's folder, or **Edit without a project**.

A script outside the project's scripts folder can be added, but it isn't compiled with the project. A file you're editing without a project says **not in a project** under its name, and has no Compile button.

The XML editor and the strings editor don't interrupt typing. When you save a file that isn't in the open project they ask once per session whether to add it.

## Project properties

**Project > Project properties** changes the name, scripts folder, compile output, author, version and description, and lists the other files, with **Add files**, **Add folder** and **Remove**. Adding a folder adds everything inside it (not `.git`, `bin` or `obj`). Changing the scripts folder or the file list reloads the tree.
"""),

        new(Basics, "Editing and compiling scripts", "\uE70F", """
The preview is also a script editor. Scripts in a **project** can be changed and saved, with highlighting, go to definition, the definition popup and the outline all keeping up as you type, and a whole project can be compiled into a `precompiled.rsblob`, with REDkit's own compiler or, without REDkit, the one built into the tool.

## Projects

Editing is done in a project: see the **Projects** page for how they work. In short, a project is a `.w3bench` file in the mod's folder that names the mod's scripts folder, lists its other files and remembers where it compiles to. Scripts that aren't in the open project are loose: the editor asks whether to add them (or to make a project for them) the first time you type in one.

## Editing and saving

- Type straight into the file. **Ctrl+Z** and **Ctrl+Y** undo and redo.
- A dot on the tab, `●` in the window title and **not saved** under the file name mark changes that aren't saved yet.
- **Save** (**Ctrl+S**) writes the file back where it came from, in the same encoding it had (UTF-8 or UTF-16, with or without a byte order mark), so nothing changes but what you typed. **Ctrl+Shift+S** saves every changed file.
- Closing a tab, a window or the app with changes asks first: **Save**, **Don't save**, or **Cancel** to keep it open.
- When the same file is open twice, in both groups of a split, both show the same text, so an edit in one is in the other too.
- After a save, go to definition, references and the other searches see the change everywhere.

## Help while typing

- **Names as you type**: after a dot, a list of what the thing before it has: its type's functions, events and variables, its base classes' too, and an array's built-in methods. Three letters into a name, it lists the locals, parameters, the class's own members, globals, types, enum values and keywords that begin the same way; not for a keyword typed in full (`if`, `for`, `return`…), a name being declared (after `var`, `function`, `class` and the like), or a name nothing else starts with. Keep typing to narrow it, then **Enter** or **Tab** to take the one picked; a bracket, dot, comma or semicolon takes it too and goes in after it. **Esc** or a space closes the list. **Ctrl+Space** opens it any time. Turn the list off while typing in **Settings**; Ctrl+Space still works.
- **Snippets** (**Ctrl+J**, or **Insert snippet** on the right-click menu): common WitcherScript to start from: `@wrapMethod`, `@replaceMethod`, `@addMethod` and `@addField`, running code when the player spawns, a class, a statemachine with a state, a struct, an enum, a console command, a timer, a loop over an array, listening for a key, reading a menu setting, a log line, a notification, a fact check and giving the player an item. Typing a snippet's name on a line of its own and pressing **Tab** inserts it as well (`wrap`, `onspawned`, `timer`, `listen`…). The parts to fill in are picked out: type over the first, **Tab** to the next (**Shift+Tab** back), and after the last, Tab goes to where the snippet ends.
- **Your own snippets**: save a `.ws` file in `%AppData%\W3ModWorkbench\snippets` and it's a snippet named after the file. A first line starting with `//` is its description. Write `${name}` where something should be typed, filled with `name` to start with, and `$0` where the caret goes at the end. One with the same name as a built-in snippet replaces it.
- **Rename** (**F2**, or **Rename** on the right-click menu): gives a function, event, variable, type or enum value a new name everywhere the project uses it, worked out the way **Find all references** does, so another class's member with the same name is left alone. Only names your project declares can be renamed (and a function's locals and parameters, in its own file): renaming one of the game's or another mod's would break what uses it there. Open files keep the change unsaved so you can look it over; files that aren't open are saved. Uses where the type before the dot couldn't be worked out are counted, and you choose whether to rename them too. The list under the files shows every place that changed.

## Editing a decompiled mod

Decompiled scripts are read-only, since they live inside the blob. To change one, press **Edit as project** at the top of the window (typing in one offers it too) and choose a folder. The mod's scripts are saved to a `scripts` folder inside it, every file but `all.ws`, which only repeats the others. A `.w3bench` project is made in that folder, named after the mod, and opens in place of the blob; the open tabs switch to its files, at the same place, now editable. The folder must not have a `scripts` folder with anything in it already, or a project file, so nothing is overwritten.

In testing, a decompiled mod compiled again without changes, though a mod that uses something the decompiler gets wrong may need a fix first. See **Reading decompiled code** for what decompiling can't bring back, such as comments.

## Compiling

**Project > Compile scripts** compiles the project's scripts into a `.rsblob` to check them or try them, with any errors listed under a preview, each opening its line. **Build** compiles them into the mod as well, so you don't need both. Two compilers can do it:

- **REDkit's `wcc_lite.exe`**, the same compiler REDkit uses to publish. REDkit is free on Steam and GOG for owners of the game: set wcc_lite in **Settings**, or let **Find automatically** look for it. It is used whenever it's set.
- **The built-in compiler**, for when REDkit isn't installed. It reads the game's own scripts (from the game folder in **Settings**, or REDkit's, whichever is found) and the mods you have installed, so a project built on Shared Utils finds it there, and writes the same kind of blob in about a second. Tick **Always use the built-in compiler** in Settings to use it even when wcc_lite is set, which is handy while you're trying changes.

Until one of them can run, compiling says why. You can still edit and save scripts without it.

- Changed files are saved first, since both compilers read what's on disk. It asks before saving them.
- The first time, it asks where the compiled blob goes, and keeps that in the project's `.w3bench` file (also in Project > Project properties). **Project > Compile scripts to…** asks again.
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

**Read from** picks where the strings come from:

- **Game files (.w3strings)**: the base game's `content\content*\<language>.w3strings`, and, with **Include DLC and mods** ticked, the files in `dlc\*\content` and `mods\*\content`. It needs the game folder (Settings finds it, or it's picked up from the file you're working on).
- **REDkit database**: REDkit's own strings database (`r4data\LocalEditorStringDataBaseW3_UTF8.db`), with the strings of the game and both expansions. It knows more than the game's files do: the **name** of each key (the game's files only keep a hash), the **kind** of text (dialogue line, choice, journal, item, book…), the **scene or resource** it's used in, and the **voiceover** name of spoken lines. It's found from the wcc_lite set in Settings, or in the usual Steam and GOG folders. REDkit is free on Steam and GOG for owners of the game; without it this choice is greyed out. **Include installed mods** adds your mods' `.w3strings` on top.
- **Database file…**: any REDkit strings database you pick, such as a copy of REDkit's.

The database comes with REDkit and may miss a few hundred lines added to the game in later patches; the game's files have every string the game loads. The **From** column says where each result comes from, and the choice is remembered.

## Searching

- Type words and every one has to appear in the text, whatever the case: `swallow potion`. **Whole words** stops `ale` from finding `scale`.
- Type a number to find an ID, or the start of one: `2115940`.
- Type a key's name (`panel_Mods`) or its hash in hex (`4d5f8b0c`) to find the strings that use that key.
- Reading from the REDkit database, part of a key's name finds it too (`item_name_cat`), and so does part of a scene's path (`q202_05b`) or a voiceover name (`EGHT_Q202`).

Results show the ID, the key and the text, from the first 2,000 matches, and from the database also the kind, where it's used and the voiceover. Double-click a row to copy its ID; right-click to copy the text, `GetLocStringById(id)` for a script, or a line ready to paste into a strings CSV. Strings with a known key name can also copy the key, or `GetLocStringByKeyExt("key")`.

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

        new(Basics, "Scripting tools", "\uE90F", """
Five tools in the **Tools** menu for working on a mod's scripts. The first three read the game's scripts, the installed mods and the open project, straight from disk, with nothing open first. Each mod's compiled scripts are decompiled (well under a second for most installs), and a mod without any is read from its loose scripts; untick **Installed mods** to leave the mods out. A mod's loose copy of one of the game's files isn't listed twice. Double-click a line, or press Enter, to open the script at that place.

## Console commands

Every `exec function`: the functions the game's debug console runs by name. Search by name, parameter or mod. Each shows what to type, with names and strings in quotes (`additem('itemName')`), its parameters and which can be left out, and where it's declared. **Copy** puts the command on the clipboard; **Include optional parameters** adds the ones that can be left out. The console needs debug scripts on: `DBGConsoleOn=true` under `[General]` in `dx12user.settings` or `user.settings` in `Documents\The Witcher 3`.

## Quest facts

Every fact the scripts set or read with `FactsAdd`, `FactsSet`, `FactsSubstract`, `FactsRemove`, `FactsQuerySum`, `FactsQuerySumSince`, `FactsQueryLatestValue` and `FactsDoesExist`, and each place that does, with the calls that set it marked. The filter shows only facts the project or a mod uses, facts that are set but no script reads (or read but no script sets), or the calls whose fact name the code builds rather than writes out. Quests and scenes use facts too, but they're compiled resources, so a fact "no script sets" may well be set by a quest. **Name** and **FactsQuerySum** copy the fact's name, or the call that reads it.

## Scripts log

Follows `Documents\The Witcher 3\scriptslog.txt` while the game runs, which it writes when debug scripts are on (`-debugscripts`). New lines appear as they're written, **Follow** keeps the newest in view, **Pause** stops for a moment, and **Clear** hides what's there so far without changing the file. When the game starts again with a new log, the list starts again. Narrow it with words (`!word` leaves lines out), a channel (the name `LogChannel` was given) or **Problems**, which keeps asserts and lines that mention an error or a warning. **Open log…** watches any other log file.

Double-click a line, or press Enter, to find the script that wrote it: the `LogChannel` call on that channel whose text matches the line best, followed through the small functions the game logs some channels with, such as `LogBoat`.

## Key binding helper

Gives a mod's action a key. Fill in the action's name, the key (every key the engine knows is listed, keyboard and mouse first), whether it fires when **held**, and the contexts it works in (exploring, combat, on a horse, swimming…; **Show every context** lists the rest). It writes the lines for `input.settings`, the `input.xml` entry that puts the action in the game's key bindings menu, and the script that listens for it. Your `input.settings` is read to say whether the key is free in those contexts, and what it's bound to if not. **input.settings**, **input.xml** and **Script** copy each part, for your code and your mod's readme. **Add to my game** puts the lines in your `input.settings` to try the key, keeping a `.bak` copy; close the game first, as it writes `input.settings` when it quits. Then, since the game's `input.xml` is one of the game's own files, it asks separately before adding the entry there for the key bindings menu (with an `input.xml.bak` copy). **Leave it** keeps the game folder as it is; the key still works, it just isn't listed in the menu.

## Hashes and IDs

- **Hash a name**: the hash the game stores a name as: in CR2W files (FNV-1a 32 with the name's closing zero), as a `.w3strings` key, as a Wwise ID for sounds, and plain FNV-1a 32 and 64 and CRC-32.
- **Find the name behind a hash**: looks through every name in the game's scripts and every string key the game has, with each kind of hash. Type it in decimal or with `0x` in front.
- **Read a number**: as hex, as signed and as float bits, its bytes as a file stores them, and for a string ID in the `211…` range, the Nexus mod ID and string number it's made of.
- **String IDs for a mod**: the block of 1000 IDs a Nexus mod ID gives by the usual convention: 211, then the four-digit ID, then 000 to 999.
"""),

        new(Basics, "CR2W files as JSON", "\uE943", """
Most of the game's resources are CR2W files: environments (`.env`), material instances (`.w2mi`), entities (`.w2ent`), quests, scenes, meshes and the rest. **Tools › CR2W and JSON** turns them into JSON you can read, edit by hand or change with a script, and turns the JSON back into the same file, byte for byte. It also makes the same edit to hundreds of files at once, such as putting one set of tone mapping parameters into every `.env`.

The game's own files are never changed. Extract the files you want to work on first (tick **CR2W files as JSON** beside **Extract to** to get them as JSON straight away), or work on your project's copies.

## The JSON editor

Right-click a CR2W file in the list (an `.env`, `.w2ent`, `.w2mi`…) and choose **Open as JSON** to read and edit it as JSON straight from the bundle. **Save** writes it back as CR2W: into your project at its game path for one of the game's files, or over the file for one of your own. `.json` files open in the same editor: drop them on the window, open them with **Open files**, double-click one in a project, or use **Tools › Open a JSON file…**.

It's coloured and folded as you type, with an **Outline** of the objects and lists (each by its key, with its `$class` beside it; Ctrl+Shift+O), find (Ctrl+F, with F3 for the next match), and **Format** (Ctrl+Shift+F), which indents it the way the converter does, with small objects such as a curve point on one line. The bar at the bottom says whether the JSON is valid, and a click goes to a mistake. For a file the converter made, it also says whether it still converts back to CR2W, and if not, which value is wrong and what it should be. **Save as name.env** writes the game file from a converted `name.env.json`.

Some values are whole CR2W files kept inside another, as base64 starting `"Q1IyV…"` (that's "CR2W"): an entity's cooked effects and streamed components, for example. Put the cursor on one and press **Open embedded file** (Ctrl+E) to open it in a window of its own, with the same outline and checks. **Save** there writes it back into the file it came from, which is then marked as changed; save that file to keep it. **Save as** keeps it as a file of its own instead.

## Convert

Add files or folders, or drop them on the window, and choose a direction:

- **CR2W to JSON** writes `name.env.json` for each `name.env`. In a folder it takes every CR2W file, whatever its extension.
- **JSON back to CR2W** writes `name.env` from each `name.env.json` it made. Other `.json` files are left alone. Tick **Save older files as version 164** to write JSON made from an older file as version 164 (see below).
- **Resave as version 164** saves older CR2W files as version 164, CR2W to CR2W. Files that are 164 already are left alone. Saving beside each file replaces it, so it needs **Replace files that are there already**; or save into a folder.

### Resaving as version 164

REDkit's source files go back to version 120, from early in the game's making. The engine still reads them, but other tools often don't, and the oldest are in the Witcher 2's layout. Resaving makes each one what the engine itself saves after loading it: in the 164 layout, with the changes the engine makes as it loads. Curves become the list of points they are now (`dataCurveValues`). Properties the engine no longer has are left out, and renamed ones moved (`m_ssao` to `m_ssaoNV`). Types take the engine's names, and a value of a type the engine converts on loading (8-bit text to text or a name, a data buffer to a shared one) is saved as the engine's type. Entities and components lose the world transform 162 stopped saving, and area shapes get their newer version. Behaviour graphs before 145 get their variables named and numbered as the engine does, destruction meshes' chunks the fields 164 added, and the oldest cube maps the newer layout. Each of these was worked out from REDkit's own files of every version and checked against the game's cooked copies of the same files: the game's environments, materials and the rest come out the same, but for textures the game doesn't have.

Some classes keep data of their own after their properties, in a form that's changed for some of them. All but a handful of REDkit's files resave: a file with a class whose newer form isn't known (a class the engine no longer has, or one no 164 file has to check against) isn't resaved, and the result says which class. Nothing is written that might not load. Each result also says what changed, including what the engine no longer reads and so is left out: an old entity template's own copies of its components (its compiled data has them), the low-resolution copy a few textures kept of their smallest mips, and an old texture array's copy of its textures.

Save **beside each file**, or **into a folder**, where each file keeps the folders it was in below the folder you added. Turn on **Replace files that are there already** to write over earlier results. Double-click a result to show it in File Explorer.

Every CR2W version there is a file of is converted: the game's own (161 to 164) and all of REDkit's source files in `r4data`, back to version 120 from early in the game's making, including those saved in the older layout (154 and before). Each converts back to the version it was.

Every file is checked as it's converted: its JSON must turn back into exactly the same bytes, or it isn't written. A value the converter doesn't understand is kept as its bytes (`{"$raw": "…"}`) so it still survives, it just can't be edited; the result says which. Of the game's and REDkit's 438,000 CR2W files, 12 have one: some cooked terrain data in 10 worlds, and a buffer in 2 of REDkit's oldest animation files.

## Bulk edit

Add the files to edit: CR2W files, JSON files this tool made, or folders of either. Then build a list of edits, made in order to every file:

- **Copy the value from another file**: the value at the path in the file you choose, put in place of each file's (or added where a file doesn't have it). Copy `envParams.m_toneMapping` from the environment you've tuned to give every `.env` the same tone mapping.
- **Set a value**: JSON, such as `1.5`, `true`, `"text"`, or a whole object or list.
- **Remove**: drops the property, so the game uses its default.
- **Multiply numbers** and **Add to numbers**: change every number below the path, or only those in the properties you list. A curve's values are in `value`; a colour curve's colours are in `controlPoint`'s `X`, `Y` and `Z`. Multiplying `envParams.m_globalFog` by 0.8 in `value` thins all the fog curves by a fifth.

A path is property names from an object down, separated by dots, with `[n]` for an item of a list: `envParams.m_globalFog.fogDensity.dataCurveValues[0].value`. **Choose…** shows the properties of the source file (or of the first file to edit) to pick from, and fills in the class too. **Only objects of** limits the edit to one class, such as `CEnvironmentDefinition`; leave it empty for every object that has the path.

**Check** tries the edits without saving anything: which files they'd change, and whether each could be saved. **Make the edits** saves each changed file, in the form it was in, into the folder you chose or over the files themselves. A file the edits don't reach isn't written.

## From a script or the command line

The same work runs with no window, so a batch file or a Python script can use it:

```
W3ModWorkbench.exe --to-json "D:\mymod\envs" -r --out "D:\mymod\envs json"
W3ModWorkbench.exe --from-json "D:\mymod\envs json" -r --out "D:\mymod\envs"
W3ModWorkbench.exe --resave-164 "D:\redkit envs" -r --out "D:\mymod\envs"
W3ModWorkbench.exe --edit "D:\mymod\envs" -r --copy "D:\my_look.env" envParams.m_toneMapping
W3ModWorkbench.exe --edit "D:\mymod\envs" -r --scale envParams.m_globalFog 0.8 --fields value
```

`-r` looks in subfolders, `--out` saves under another folder, `--overwrite` replaces files that are there, `--as-164` makes `--from-json` write older files as version 164, and `--class` limits edits to one class. The edits are `--copy <file> <path>`, `--set <path> <json>`, `--remove <path>`, `--scale <path> <factor>` and `--offset <path> <amount>`. `--help-json` lists them all. Each file's result is printed, and the exit code is 0 when every file worked. It's a windowed program, so a Command Prompt doesn't wait for it to finish: in a batch file use `start /wait "" W3ModWorkbench.exe …`, or in PowerShell `Start-Process -Wait -NoNewWindow`. Python's `subprocess.run` waits by itself.

In Python, convert to JSON, change the files with the `json` module, and convert back:

```
import json, pathlib
look = json.loads(pathlib.Path("my_look.env.json").read_text("utf-8"))
tone = look["objects"][0]["envParams"]["m_toneMapping"]
for f in pathlib.Path("envs json").rglob("*.env.json"):
    env = json.loads(f.read_text("utf-8"))
    env["objects"][0]["envParams"]["m_toneMapping"] = tone
    f.write_text(json.dumps(env, indent=2, ensure_ascii=False), "utf-8")
```

See **The CR2W JSON format** in the Advanced section for what's in the files.
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

REDkit's `wcc_lite.exe`, which **Compile** uses to build a project into a `.rsblob`. It's in REDkit's `bin\x64_RedKit` folder. **Find automatically** looks for REDkit in your Steam and GOG libraries, or use the folder button to pick it. A tick under the box means it found wcc_lite and the game scripts REDkit compiles against (`r4data\scripts`). Leave it empty and **Compile** uses the built-in compiler, as long as the game's scripts are found; editing and saving scripts work either way.

## Script merger (optional)

The path to **Script Merger Remastered** by relapse12, a separate download on Nexus Mods that merges mods changing the same scripts or files. **Find automatically** looks in the game folder, Documents and Downloads. With it set, the load order analyser has an **Open Script Merger** button and suggests the merger where a clash can be merged. This tool only starts it; nothing of the merger is shipped or changed.

## Preview

- **Open files in**: tabs in one preview window, or a separate window for each file. Go to definition follows the same choice.
- **Split view**: whether a split puts the second group of tabs beside the first or below it.
- **Show a name's declaration when the mouse rests on it**: the definition popup in scripts.
- **Suggest names while typing**: the list of names that fit, after a dot and at the start of a name, in a script you can edit. Ctrl+Space shows it either way.

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
- **The open project and loose scripts**: the project's scripts folder and folders, and any loose scripts open in the main window.
- **A folder...**: any folder you pick. The last one picked is kept in the list.

**Subfolders** controls whether folders inside are searched too.

## Opening a match

Click a line, or press Enter, to open that file in a preview with the match selected. **F4** and **Shift+F4** step through the matches. Clicking a file's name folds its lines away. The game's scripts open read-only, and a project's files can be edited and saved as usual. Go to definition works from any script opened this way.

Inside a script preview, Ctrl+Shift+F still searches from there, through the scripts that preview knows about.

## Browsing instead

**Tools > Asset Browser** (**Ctrl+Shift+A**) opens the asset browser, a tool window of its own as REDkit's is. **Game files** is the game itself: content0 and the official DLC (on the Remastered game these are inside content0, and `dlc-tombstones` names them), each game path once, with nothing any mod adds. **Mod DLC** lists each mod installed into the `dlc` folder, and **Mods** each mod in `mods` (and mod.io's), each with everything it holds. **From** says which bundle each comes from, and textures only in a texture cache are listed too. Search it (words in the path, or wildcards on the name), pick a folder, and right-click to preview, open as JSON, **Copy to project** (with a mesh's buffers), extract, copy paths or show a file in Explorer. It's read in a second or two the first time it opens, and opening it again brings it back.

**Which copy opens**: double-click a file (or press Enter) to open it in its viewer, after narrowing the list with the search, **File type** (only `.w2mesh`, only `.w2phase`…) and **Class** if you like. When a mod has its own copy of a file in Game files, it asks which copy to open: every copy, in the load order the load order analyser uses (mod.io mods, then DLC, then mods by their mods.settings priority), the one the game loads marked and the game's own picked to start with; **Open another copy…** on the right-click menu asks even when asking is off, and **Other copies of this file…** compares them. Turn asking off with **Don't ask again** in the question (Game files then opens the game's own copy), or **Ask which copy** in the asset browser's toolbar, which turns it back on too.

**What it loads comes from its mod, then the game**: a file opened anywhere in the tool (a mesh, an entity, a scene, a cutscene) finds what it uses (buffers, materials, textures, animations, other entities) in its own mod first, then the game's DLC and content0. Other installed mods aren't looked in, so the game's copy of a scene shows the game's actors and the mod's copy shows the mod's, never a mix with whatever else is installed.

**Class**, beside the search box, shows only the files that hold one class of resource, the way REDkit's asset browser filters: `CMesh` for meshes, `CBitmapTexture` for textures, `CEntityTemplate` for entities, `CMaterialInstance`, `CStoryScene`, `C2dArray` for `.csv` tables, and the rest, each with how many of them the folder picked has. It works with the search, in the asset browser and in the main window, where **Class**, **File type** (only `.w2mesh`, only `.xbm`…) and the search sit in a bar under the file list while something's open to browse.

**Bookmarks**: pick a folder in the asset browser and press the star beside Class to bookmark it (press it again to take it off), or right-click files and choose **Bookmark**. The bookmarks button beside the star lists them; pick one to go straight there, a bookmarked file highlighted in its folder. **Remove a bookmark** at the bottom of the list takes one off. Bookmarks are kept between sessions.

**Mod DLC** and **Mods**, under Game files in the asset browser, list each mod on its own, with everything it holds: its bundles, texture cache, compiled scripts (`precompiled.rsblob`, decompiled), loose scripts and strings. A file opened from a mod there is that mod's copy, without asking which, and what it loads comes from that mod first, then the game. A mod installed as DLC counts as a mod for this, so it never leaks into what another file loads.

**Tools > Browse game scripts** lists the game's own scripts in the main window, to read. It needs the game folder, which is found for you or set in Settings.
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
- **Ctrl+Shift+B**: build the open project's mod
- **Ctrl+,**: settings
- **F1**: this help

## In the file list

- **Space**: tick or untick the highlighted rows
- **Enter**: preview the highlighted text file
- **Alt+P**: show or hide the preview pane
- **Ctrl+C**: copy the highlighted files' paths
- **Ctrl+Shift+C**: copy their names
- **Ctrl+D**: compare the two highlighted files
- **Ctrl+Shift+D**: other copies of the highlighted file
- **Shift+Alt+R**: show the highlighted files in Explorer
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
- **Ctrl+Shift+B**: build the open project's mod
- **Ctrl+Shift+N** and **Ctrl+Shift+O** (main window): new project, open project
- **Ctrl+Z** / **Ctrl+Y**: undo or redo
- **Ctrl+click** or **F12**: go to definition (scripts)
- **Ctrl+Alt+click** or **Ctrl+F12**: open the definition in the other group
- **Shift+F12**: find all references
- **Ctrl+Shift+F12**: who else changes this
- **F2**: rename (names the project declares)
- **Ctrl+Space**: suggest names
- **Ctrl+J**: insert a snippet; a snippet's name and **Tab** inserts it too
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

Compile only shows for the open project's files. For a decompiled script, use **Edit as project** first.

## A compile fails with errors I can't find

Check the line it names. When the file is marked **file guessed**, the explorer worked it out from wcc_lite's message; **Show log** has the compiler's own words. Errors in the game's scripts are left out, since they come from REDkit itself, so every error listed is in your project. If the built-in compiler stops on something wcc_lite accepts, compile with wcc_lite and tell me about the script.

## A script can't be typed in

It's read-only: inside a bundle or a blob, or one of the game's own scripts. Open its folder, make a project for it and edit it there, or use **Edit as project** for a decompiled mod.

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

- **WebSpam**: the WitcherScript syntax highlighting in the preview comes from his VS Code extension, witcherscript-vscode (github.com/webspam/witcherscript-vscode), used with his kind permission. He also wrote the WitcherScript-to-`.rsblob` compiler for tw3-cahirb that the built-in compiler is a port of, which is what lets Compile work without REDkit. If you write WitcherScript in VS Code, his extension is well worth a look.
- **AvalonEdit** by the SharpDevelop team: the code view in the preview window.
- **Aelto**: his own decompiler project, tw3-cahirb (github.com/Aelto/tw3-cahirb), filled real gaps in this one. The full list of the script engine's built-in operators, several rarely used instructions, and a sample from an older REDkit build that showed up property hints, array defaults and `do`/`while` loops all came from comparing notes with his work.

WebSpam's grammar and AvalonEdit are MIT licensed, and their full licence texts are in `THIRD-PARTY-NOTICES.md` next to the exe, and under About (the i button at the top right of the main window). The built-in compiler is a port of the WitcherScript-to-`.rsblob` compiler that **WebSpam** wrote for tw3-cahirb (github.com/Aelto/tw3-cahirb, pull request 1), extended and checked against wcc_lite's output on real mods. Apart from that, no code was copied from tw3-cahirb, only facts about the file format.

The bundle reader, the LZ4 and Snappy decoders and the script decompiler are all written for this tool. So are the bundle, `metadata.store`, texture cache and sound cache writers that Build uses, with the LZ4 HC and zlib encoders and the texture cooking they need, made to give the same bytes as REDkit's `wcc_lite` and checked against its output; webspam's W3Bundle notes on the bundle and `metadata.store` layout helped, and no code was copied. Compiling uses REDkit's own `wcc_lite.exe` from your REDkit install when you have one; nothing of REDkit's is shipped with the tool. Without it, the built-in compiler reads the game's own scripts from your game install. The tool carries a list of the engine's native classes and enums (made with the game's own `-dumprtti` option) so it knows what the scripts can name.
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

        new(Advanced, "The CR2W JSON format", "", """
A converted file is the CR2W file's objects in order, then a `file` block with what's needed to put it back together.

```
{
  "$format": "w3mw-cr2w-json",
  "$formatVersion": 1,
  "objects": [
    {
      "$class": "CEnvironmentDefinition",
      "envParams": {
        "m_toneMapping": {
          "activated": true,
          "luminanceLimitMax": {
            "dataCurveValues": [
              { "time": 0.5, "controlPoint": { "X": -0.1, "Y": 0, "Z": 0.1, "W": 0 }, "value": 1.2 }
            ]
          }
        }
      }
    }
  ],
  "file": { "version": 164, "names": […], "imports": […], "types": {…} }
}
```

## Values

- Each object has its `$class`, then its properties by name, in the order the file has them. A property the file doesn't have isn't listed: the game uses its default. Add it to set it.
- Structs are objects and arrays are lists. Numbers, `true`/`false` and strings are what they look like; a name (`CName`) is a string too.
- An enum is its value's name (`"SCT_Color"`); a flags enum is a list of names.
- A reference is `null`, `"export:N"` for another object in the file (counting from 0), or `"import:path"` for another file, such as a texture. Point it at a new path and the file gains the import.
- A transform is `{ "position": {X, Y, Z}, "rotation": {Pitch, Yaw, Roll}, "scale": {X, Y, Z} }`, each part optional; a bone's (`EngineQsTransform`) has its rotation as a quaternion, `{X, Y, Z, W}`. A variant (`CVariant`) is `{ "type": "Float", "value": 1 }`. Byte arrays and data buffers are base64 strings.
- An entity reference (`EntityHandle`) is `{ "type": 1, "guids": [two GUIDs] }`, or `{ "type": 2, "idTag": { "type", "guid" } }`. A physical collision is `{ "version": 2, "types": [collision type names], "masks": [two numbers] }`; the masks are what the engine works out from the names.
- A tag list the engine saved empty in its other way is `null` rather than `[]`; both are no tags.
- A NaN that isn't the usual one keeps its bits as `"NaN:ffffffff"` (some files have them where a value was never set).
- `"$marker"` on an object or struct says it was saved the other way about the zero byte objects start with than its version has them (158 on have it, 155 to 157 mostly don't): `true` or `false`. Leave it as it is.
- A curve point's properties are saved by the engine under shortened names (`me`, `ntrolPoint`, `lue`, `rveTypeL`, `rveTypeR`). The JSON calls them `time`, `controlPoint`, `value`, `curveTypeL` and `curveTypeR`, and they're saved back under the engine's names.
- A material instance's parameters (its textures, colours and numbers) are in `$parameters`, each `{ "name", "type", "value" }`, after its properties.
- The events a scene section, an animation (in its set), an animation events file or a cutscene keeps are in `$events`, each an object with its `$class` and properties, as the objects are. A cutscene's four bytes before them are in `$eventsBefore` when they aren't zeros.
- A scene script's or a quest script block's arguments are in `$arguments`, by name, as properties are; their types are under `CStorySceneScript.arguments` or `CQuestScriptBlock.arguments` in `file.types`.
- `"$end": false` on an object of a file before version 139 says its properties don't end with the zero those files have. Leave it as it is.
- `{"$raw": "base64"}` is a value kept as its bytes, and an object with `$data` is a whole object kept as its bytes. Leave them as they are.

## Types

A property's engine type isn't repeated on every value. `file.types` lists the type of each class's properties (`"SSimpleCurve.ScalarEditScale": "Float"`); an object lists in `$types` any of its own that differ. A property you add that the file never had is looked up in the engine's list of classes, which the tool carries. Add its type to `file.types` if that doesn't know it.

## The file block

`names` and `imports` are the file's tables, in order, so the numbers its objects use stay right; new names and imports go on the end. The rest (`version`, `flags`, `buffers`, `trailer`, `stringOrder`, `nameHashes`, `latin1`, `tableHeaders`, and `$flags`, `$parent` and `$crc` on objects) keeps the header and layout the file had, so it comes back byte for byte. Leave them as they are.

## Older files

REDkit's oldest source files (CR2W version 154 and before) are in the Witcher 2's layout, and their JSON says so in a few places:

- Types can have the Witcher 2's names: `@Float` is an array of floats, `*CEntity` and `#CEntity` references, `~CEntityTemplate` a soft reference, `Uint` and `Int` are `Uint32` and `Int32`.
- A soft reference is to one of the file's `dependencies` (in the file block) rather than its imports, but is written `"import:path"` all the same.
- Objects can have `$name`, `$extra` and `$template`, fields of that layout's object table, and the file block `dependencyCount`. Leave them as they are.
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
