---
title: Source inspector
icon: E8B9
order: 3
summary: Compare two mods and see how much of one is in the other: scripts, menus, text and files.
keywords: source inspector compare similarity copy plagiarism reuse match report
windows: SourceInspectorWindow
---
Mods get copied, renamed and re-released. **Tools > Source inspector** ({{Ctrl+Shift+I}}) compares two mods and reports how much of one is in the other. Everything is read on your PC; nothing is uploaded.

![Comparing two mods](source-inspector.jpg)

1. Pick the **Original** (the mod you think came first) and the **Suspect** (the one to check): browse, paste a path, or drop a folder on each. A folder of files, of bundles and compiled scripts, or a single file all work.
2. **Ignore the game's own scripts** sets aside code both start from (a mod that wraps a game function starts from its code), so honest mods don't look alike.
3. **Analyse** ({{Ctrl+Enter}}).

## What's compared

- **Scripts**, including compiled ones (decompiled first), ignoring comments, spacing and case, both as written and with every name replaced, so renaming doesn't hide a copy.
- **Menus and XML**, element by element.
- **Text and strings**, line by line.
- **Every file in every bundle**, fingerprinted, found under any name.
- **Coding style**: indentation, braces, naming habits, comment density.

## The result

- **Summary**: the headline figure, what it means, and a breakdown.
- **Matching files**: each pair, from **Identical** and **Renamed copy** to **Partial overlap**. Click one to see the two side by side.
- **Shared details**: comments, messages and names found in both, and functions copied whole or renamed.
- **Coding style** and **Sources**.

**Save report** writes it as a web page or Markdown.

> warning: Similarity is evidence, not proof. Mods for one game share its code and common libraries. Read the matches and talk to the authors before drawing a conclusion.
