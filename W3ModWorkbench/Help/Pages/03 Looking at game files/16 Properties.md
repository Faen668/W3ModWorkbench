---
title: Properties
icon: E946
order: 16
summary: Every object and property in any game resource, as REDkit lists them, and where the properties come from.
keywords: properties objects resource layer w2l env particles redkit r4data cooked source file
windows: PropertiesWindow
---
Any of the game's resource files can be opened in a **Properties** window, as REDkit opens every resource in a property window: a layer (`.w2l`), an environment (`.env`), a particle system, a quest, a font, and the rest. Double-click a file that has no viewer of its own, or right-click any game file and choose **Properties**.

![An environment's properties](properties-window.jpg)

- **Objects**, on the left, lists every object in the file, each under the one it belongs to: a layer's entities with their components, a particle system's emitters. The file itself is at the top and picked first. Arrows open and close branches, and **Search objects** finds objects by class or name.
- **Properties**, on the right, are those of the object you pick, under each class they come from, as REDkit lists them. Properties the file doesn't keep (left at their defaults) are dimmed. Structs, arrays and the objects they point to fold open beneath them. The box above them narrows the list.
- A file a property names is a link: click it to open that file.
- **Open as JSON** opens the whole file as JSON to read or edit; **Copy path** copies its game path.

## Properties from REDkit

The game's files are **cooked**: cooking leaves out most of the properties REDkit's editors show. REDkit keeps its own copies of the game's files from before they were cooked, in its `r4data` folder.

So when REDkit is installed and has a copy, the viewers (textures, meshes, entities, behaviour trees, animations, scenes and this Properties window) show the properties of one of **the game's own files** from **REDkit's copy**. What they draw still comes from the file the game loads. A note above the properties says where they came from:

| The note says | Meaning |
|---|---|
| **Properties from REDkit's source file** | everything REDkit shows, with its path in `r4data` |
| **REDkit has no source file for this** | only what the cooked file keeps (REDkit keeps only some files) |
| **REDkit isn't installed** | install it to see what cooking leaves out. It's free on Steam and GOG for owners of the game. |
| **Properties from this file itself** | a mod's, a project's or a file on disk: its own properties are what you're looking at |
