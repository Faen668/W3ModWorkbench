---
title: Previewing text files
icon: E8A5
order: 1
summary: Read XML, CSV, JSON, subtitles and other text straight from the game, with search, tabs and a split view.
keywords: preview text csv txt subs json read tabs split compare search
windows: PreviewWindow
---
Text files can be read without extracting them first: the `.csv` tables, `.xml`, `.json`, `.txt` and the cutscene subtitles (`.subs`). Double-click one, or highlight it and press {{Enter}}. Scripts (`.ws`) open in [[Script Bench]] instead, and XML has an editor of its own (see [[XML files]]).

![A game table open in the preview](text-preview.jpg)

## Reading

- {{Ctrl+F}} opens a search bar. **Aa** matches case, **ab** whole words only, and **.\*** treats the search as a regular expression. {{F3}} and {{Shift+F3}} go to the next and previous match.
- **Copy** copies all the text. **Save as** ({{Ctrl+S}}) writes the file somewhere, exactly as it came out of the archive.
- **Outline** shows an XML or JSON file's elements as a tree beside the text. Click one to go there.
- Your project's files can be edited and saved here; everything else is read-only. See [[Projects]].

## Tabs and split view

Files open as tabs in one window. Opening one that's open already brings its tab forward. (In [[Settings]] you can have a window for each file instead.)

- {{Ctrl+Tab}} and {{Ctrl+Shift+Tab}} move between tabs. {{Ctrl+W}}, a middle-click or the tab's **×** closes one, and {{Ctrl+Shift+T}} opens the last one you closed again, where you were in it.
- Right-click a tab to close the others, **Pin** it (pinned tabs stay at the front and stay open when others close), open it in the other group, move it to a new window, or copy its path.
- **Split** ({{Ctrl+\}}) shows a second group of tabs, to read two files, or two parts of one, at once. Right-click **Split** to choose side by side or top and bottom. {{Ctrl+1}} and {{Ctrl+2}} move between the groups.
- Drag a tab along the strip to reorder it, onto the other group to move it, onto the far side of the text to split there, or out of the window to give it a window of its own.

## Comparing

- Highlight two files in the main window and press {{Ctrl+D}}: they open side by side with what's different marked: green for added lines, red for removed, amber for changed. {{F8}} and {{Shift+F8}} step through the differences.
- Right-click a tab > **Compare with an open file** does the same with any other open file.
- **Stop comparing** takes the marks off.
