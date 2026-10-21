---
title: Find in files
icon: E721
order: 13
summary: Search the text of every script: your mod, the game's, the installed mods' (compiled ones too), REDkit's or any folder.
keywords: find in files search text grep regex folder scripts look in filter
windows: PreviewWindow:bench
---
Find in files lives in [[Script Bench]]: press {{Ctrl+Shift+F}} there, or use the search button in its toolbar. Nothing needs to be open first. Text you've selected in a script fills the box.

![Every use of GetItemPrice, in your mod, the game and the installed mods](find-in-files.jpg)

1. Type what to look for and press {{Enter}} (or **Find all**).
2. Pick how it matches:
   - **Aa**: match case, so `Foo` doesn't find `foo`.
   - **ab**: whole words only, so `Player` doesn't find `thePlayer`.
   - **.\***: a regular expression.
3. **Look in** says where:
   - **Everywhere**: your mod, the game's scripts and the installed mods.
   - **Your mod**, **Game scripts** or **Installed mods** alone. A compiled mod is searched as decompiled code.
   - **REDkit scripts**, when wcc_lite is set in [[Settings]].
   - **A folder…** of your choosing, which then stays in the list.
4. **Files** narrows it by file name, or by path when the pattern has a `\` in it. Use `;` between patterns and `!` to leave some out. Leave it empty to search every script.

| In **Files** | Finds |
|---|---|
| `player*` | files whose names start with player |
| `game\gui\*` | everything under the game's gui folder |
| `*menu*; !*old*` | names with menu in them, but not old |

| A regular expression (with **.\*** on) | Finds |
|---|---|
| `Get\w+Price\(` | `GetItemPrice(`, `GetItemPriceModified(` … |
| `^\s*@wrapMethod` | every wrapped method |
| `FactsAdd\("q1\d\d` | facts of the q1xx quests being set |

Every match is listed under its file with its line number. Click one, or press {{Enter}}, to open it at the match. The arrows at the top of the list, or {{F4}} and {{Shift+F4}}, step through them, and clicking a file's name folds its lines. A script you've changed but not saved is searched as it is in its tab.

> tip: To find where one particular function or variable is used, rather than every place its name is written, right-click it and choose **Find references** ({{Shift+F12}}). See [[Finding your way around scripts]].
