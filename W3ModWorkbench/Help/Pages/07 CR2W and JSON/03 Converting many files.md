---
title: Converting many files
icon: E8B7
order: 3
summary: The Convert tab: whole folders of CR2W files to JSON and back, with filters, and older files resaved as version 164.
keywords: convert tab folder batch to json back from json resave 164 version older redkit r4data filter regex match skip overwrite beside into folder subfolders progress stop results explorer right-click folder tree
---
The **Convert** tab of **Tools > CR2W and JSON** converts as many files as you give it, in one go: a folder of your mod's environments, or all 220,000 of REDkit's `r4data`.

![The Convert tab, set to convert a mod's environments and layers into a folder of their own](cr2w-json.jpg)

## Converting a folder

1. Open **Tools > CR2W and JSON**. The **Convert** tab is first.
2. Add what to convert: drop files or folders on the window, or press **Add files…** or **Add folder…**. Highlight one and press **Remove** (or {{Delete}}) to take it off; **Clear** empties the list.
3. Choose what to do:
   - **CR2W to JSON** writes `name.env.json` from each `name.env`.
   - **JSON back to CR2W** writes `name.env` from each `name.env.json`.
   - **Resave as version 164** (see below).
4. Choose where the results go, under **Save**:
   - **Beside each file**, in the same folder.
   - **Into a folder, keeping the folders below what was added**. Type the folder or press **Browse…**.
5. Tick what you need:
   - **Look in subfolders** takes the files in every folder below the ones added.
   - **Replace files that are there already**. Without it, a file that's there is left as it is and listed as not done.
6. Press **Convert**.

> tip: Drop `.json` files on the tab and **JSON back to CR2W** is chosen for you; drop CR2W files and it's **CR2W to JSON**.

What a folder gives depends on the direction. Converting to JSON takes every CR2W file in it, whatever its extension. Converting back takes only the `.json` files this tool made, so your other JSON files are left alone.

## While it works

A big folder shows how far it is in three steps: the files found so far, then each one looked at, then the work itself, with the file it's on.

![Converting a big folder: how far it is, and Stop](cr2w-json-progress.jpg)

**Stop** works at every step. The files done before you stopped are kept, and the totals end **(stopped)**.

## The results

When it's done, the line above the results gives the totals and the time, such as **4 saved · 0.6 s**, or **2 saved, 2 failed · 0.4 s**. Each file has a line of its own, the ones that failed first:

![Results: written, and files left because they were there already](cr2w-json-results.jpg)

- A file that was written shows where it went, with a note if there's something to know, such as values kept as bytes.
- A file that wasn't says why: it was there already, it isn't a CR2W file, or it wouldn't turn back into the same bytes.
- Double-click a result to show it in File Explorer. **Show in Explorer** shows the highlighted ones, or all of them.

> note: Every file is checked as it's converted: its JSON must turn back into exactly the same bytes, or it isn't written. If a game file ever fails that check, please report it, with the file.

## Only some of a folder's files

**Only files matching** and **Skip files matching** choose a folder's files by their path below it. Each is a regular expression, with `/` between folders and capital letters ignored. Files you add one by one are always taken, whatever the filter says.

- `\.w2scene$`: only scenes.
- `.*w2scene`: the same, written the other way.
- `\.(env|w2mi)$`: environments and materials.
- `^quests/`: only what's in the `quests` folder.
- `^levels/novigrad/.*\.w2l$`: Novigrad's world layers.
- `(^|/)thumbnails/` in **Skip**: everything but REDkit's thumbnails.
- `^backup/` in **Skip**: everything but your `backup` folder.

A pattern that isn't a regular expression says why under the boxes as you type, and nothing runs until it's fixed.

![The filter: only environments, with the backup and redkit folders left out](cr2w-json-filter.jpg)

> tip: A narrow pattern on a big folder is quick too: files it leaves out are never opened.

## Resave as version 164

The game's files are all CR2W version 162 to 164. Many of REDkit's source files in `r4data` are older, going back to version 120, and some other tools can't read those. **Resave as version 164** saves them in the layout the engine saves today, making the same changes the engine makes when it loads an old file, such as leaving out properties the engine no longer has.

- Files that are 164 already are skipped, with **It's version 164 already**.
- Each file's result says what changed: **version 127 saved as 164; 41 changes the engine makes: …**.
- Without **Into a folder**, the files are saved over themselves, so tick **Replace files that are there already** for that, or choose a folder to keep the originals.

![Resaving REDkit's environments as version 164](cr2w-json-resave.jpg)

**Save older files as version 164** does the same while converting JSON back: JSON made from an older file is written as version 164, not the version it was.

## From the main window

Right-click a folder of your own in the main window's tree:

- **Convert to JSON** writes `name.w2ent.json` beside every CR2W file in the folder and its subfolders, keeping the files themselves.
- **Convert JSON back** writes each one back beside its JSON.

Files already there are asked about once: replace them all, or leave them as they are. It's offered on your project's folders and folders you opened from disk, not on the game's or installed mods'.

## Settings it remembers

When it closes, the window remembers the folder you saved into and whether **Look in subfolders** was ticked, for the next time it opens.

## The same from a script

Every form here is a command line too:

```cmd
W3ModWorkbench.exe --to-json "D:\W3 Mods\Night Sky\envs" -r --out "D:\W3 Mods\Night Sky\envs json"
W3ModWorkbench.exe --to-json "F:\REDkit\r4data" -r --match "\.w2scene$" --skip "(^|/)thumbnails/" --out "D:\scenes json"
W3ModWorkbench.exe --from-json "D:\W3 Mods\Night Sky\envs json" -r --out "D:\W3 Mods\Night Sky\envs" --overwrite
W3ModWorkbench.exe --resave-164 "F:\REDkit\r4data\environment" -r --out "D:\envs 164"
```

Press **Save as recipe** to keep a form and run it again later, or to export it as a Python script or batch file: see [[Recipes]]. [[The command line]] lists every option.
