---
title: Changing game files
icon: E70F
order: 3
summary: Copy a game file into your project, change it, and let the build put it in your mod.
keywords: copy to project workspace change texture mesh material xml override replace cook dds png
---
Your mod changes the game by shipping its own copy of a file, at the same game path. The game then uses your copy instead of its own. The app never changes the game's files; you change copies in your project.

## Copy to project

Right-click any game file (in the main window, the [[The Asset Browser|Asset Browser]] or [[Script Bench]]) and choose **Copy to project**. The copy goes in your project at the same game path:

| You copy | It goes to |
|---|---|
| a script, `content0\scripts\game\player\playerInput.ws` | `scripts\game\player\playerInput.ws` |
| anything else, say `gameplay\items\def_item_weapons.xml` | `workspace\gameplay\items\def_item_weapons.xml` |
| a mesh | the mesh and its `.buffer` files |

The copy opens, ready to change. A file your project has already is left alone, so nothing of yours is overwritten. Typing in one of the game's files offers the same, and saving one asks where in your project it goes.

## Changing meshes and materials

The [[Meshes|mesh viewer]] and [[Materials|material viewer]] save straight into your project with **Save to project** ({{Ctrl+S}}):

- A material kept inside the mesh is saved in the mesh, with its buffers.
- A material file (`.w2mi`) may be used by other meshes too, so saving asks: a **new material for this mesh**, or **change it for every mesh** that uses it.
- **Save as new material…** makes a new `.w2mi` wherever you choose.

Once saved, your copies are what the app finds first, so opening the mesh again shows your version.

## Textures

Put a picture in your workspace with the game path of the texture it replaces, and Build makes it into a texture:

- `x.dds`, `x.tga` or `x.bmp` becomes the texture `x.xbm`; so does `x.png` or `x.jpg` when the game has an `x.xbm`.
- It keeps the game texture's group and compression. A `.dds` keeps its own format and mip levels.
- A new texture whose name ends in `_n` is made a normal map.
- A picture beside a cube map's path (`x.w2cube` in the game) becomes that cube map: a `.dds` cube, or six square faces side by side or one above another (+X, −X, +Y, −Y, +Z, −Z).
- A picture beside a texture array's path (`x.texarray`) becomes that array: a `.dds` array, or its slices side by side or one above another.
- REDkit's own `.w2cube` and `.texarray` source files are cooked too: a cube map from the faces it keeps, an array from the textures it names, found in your project first and then in the game.

The texture viewer's **Replace with picture…** puts a picture in the right place for you and shows what Build will make of it. See [[Textures]].

> tip: Start from the game's own texture: extract it with **Textures as PNG** off to get a `.dds`, edit it, and put it back at the same path in your workspace.

## XML, strings and everything else

XML files, `.csv` tables and other text need no cooking: they go into the mod as they are. Strings files (`.w3strings`) go in `content`. See [[Editing strings]].

> warning: REDkit's own source files (from its `r4data`, or a REDkit project's workspace) aren't cooked, and the game can't load them, except textures, cube maps and texture arrays, which Build cooks. Files copied from the game's bundles are cooked already. Build lists any uncooked file before it starts.
