# Writing help pages

The guide (F1) is built from the Markdown files in this folder. Each folder is a category, each `.md` file in it a
page. They're built into the app, so a new page shows after the next build. This file isn't shown.

## Folders are categories

A folder is named `NN Title`: the number places it, the rest is its name (`05 Scripting`). Its `_category.md`
gives its icon and a one-line summary:

```
---
icon: E943
summary: Write, read and compile WitcherScript.
---
```

## A page

A page starts with a header block, then the text:

```
---
title: Script Bench
icon: E943
order: 10
summary: The window every script opens in: your project's scripts, the game's and the installed mods'.
keywords: ide editor tree override compile
windows: PreviewWindow:bench
---
Text starts here.
```

- `title`: the page's name. Links to it use this, so keep it once it's set.
- `icon`: a Segoe Fluent Icons code point, in hex, as the app's icons are.
- `order`: where it comes in its category (lower first).
- `summary`: one sentence under the title, in search results and on tooltips.
- `keywords`: more words people might search for, which the text doesn't use.
- `windows`: the class names of windows this page is about (comma separated). F1 in one of them opens it.

## How a page is written

The benchmark is a person who has never modded before:

1. Say what it is for, in a sentence or two, before how it works.
2. Say how to open it.
3. Show it: a screenshot near the top.
4. Then what you can do, a section each, as short steps or a list.
5. Tips and warnings in callouts, examples in code blocks.

Use the app's own words for buttons and menus, in **bold**. Keep sentences short and plain: "Tick the files you
want", not "Select the desired entries". Every feature the app has should be on some page.

## Markup

| Write | Shows |
|---|---|
| `## Heading` | a section, listed under "On this page" (three or more make the list show) |
| `### Smaller heading` | a heading inside a section |
| a blank line | a new paragraph |
| `- item` (two spaces in front for a level down) | a bullet list |
| `1. step` | a numbered step |
| `**bold**` | bold, for the names of buttons and menus |
| `` `code` `` | a file name, a path or a bit of code |
| `{{Ctrl+S}}` | keys, each in a little box |
| `[[Page title]]` or `[[Page title\|text]]` | a link to another page |
| `[text](https://…)` | a link to a website |
| `> note: …`, `> tip: …`, `> warning: …` | a box with an icon; lines starting `>` straight after join it |
| `![Caption](file.jpg)` | a screenshot from Help\Images, with its caption; a click shows it full size |
| ```` ```ws ```` … ```` ``` ```` | code with a Copy button: `ws` (WitcherScript), `xml`, `json`, `cmd`, `ps`, `py`, `ini`, `csv` or none; `title="name.ws"` after the language names it |
| `\| a \| b \|` rows | a table, the first row its header |
| `:::cards` with `- [[Page]] \| E8E5 \| text` lines, then `:::` | cards that go to pages |

## Screenshots

Screenshots go in `Help\Images` as `.jpg` (about 1400 pixels wide), named after what they show
(`mesh-viewer.jpg`). Take them in the dark theme, of the real app on real game files, with nothing personal in
them (no user names in paths: use a project in a plain folder). Crop to what the text is about.

## Translated pages

English pages are the source. A translation is the same file at the same path under a folder named for its
language's tag, the tag its string table has (`de`, `pl`, `pt-BR`):

```
Help/Pages/05 Scripting/10 Script Bench.md        the English page
Help/Pages/de/05 Scripting/10 Script Bench.md     its German translation
Help/Pages/de/05 Scripting/_category.md           the category's name and summary in German
```

- The guide shows the pages of the app's language (Settings > Language), page by page: a page not translated yet
  shows in English, so a translation can be done a page at a time.
- Keep the file and folder names English: they're how a translation finds its page. Translate the header's `title`,
  `summary` and `keywords`; leave out `icon`, `order` and `windows`, which come from the English page.
- `[[Links]]` may use the English title or the translated one. Screenshots are shared; a language's own can go in
  `Help/Images` with its tag before the extension (`settings.de.jpg`), and shows instead of the English one.
- Search looks through the pages shown, in the language they're shown in.
- When an English page changes, its translations need the same change: `git log` on the English file shows what.

LOCALISATION.md, at the top of the repository, says how the app's own text is translated.
