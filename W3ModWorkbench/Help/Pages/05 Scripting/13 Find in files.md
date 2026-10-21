---
title: Find in files
icon: E721
order: 13
summary: Search the text of the game's scripts, your mods or any folder, like Find in Files in Notepad++.
keywords: find in files search text grep regex folder scripts
windows: FindInFilesWindow
---
**Tools > Find in files** ({{Ctrl+Shift+F}}) searches the text of files in folders. Nothing needs to be open first.

![Every place the game's scripts show a notification](find-in-files.jpg)

1. Type what to look for and press {{Enter}} (or **Find all**). **Aa** matches case, **ab** whole words only, **.\*** a regular expression.
2. **Look in**: the game's scripts and installed mods, either alone, REDkit's scripts, your project, or **a folder…** of your choosing. **Subfolders** searches inside them too.
3. **Filters** says which files: `*.ws; *.xml`, with `!` to leave some out (`!*.bak`).

Every match is listed under its file with its line number. Click one, or press {{Enter}}, to open it at the match; {{F4}} and {{Shift+F4}} step through them, and clicking a file's name folds its lines.

> tip: In Script Bench, {{Ctrl+Shift+F}} searches the scripts that window knows about, in the list under the files.
