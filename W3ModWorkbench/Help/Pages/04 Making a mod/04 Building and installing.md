---
title: Building and installing
icon: E90F
order: 4
summary: Turn your project into a finished mod, and put it in the game.
keywords: build install mod bundle precompiled rsblob loose scripts info.json texture cache sound cache mods folder dlc reddlc
---
## Build

**Project > Build** ({{Ctrl+Shift+B}}) makes your mod in the project's `build` folder, ready to install. The app does everything REDkit does to make a mod, byte for byte, so building doesn't need REDkit.

```text
build\modMyFirstMod\content\
  precompiled.rsblob     your scripts, compiled
  blob0.bundle           your workspace's game files
  buffers0.bundle        meshes' buffers
  metadata.store         the index the game needs for the bundles
  texture.cache          your textures
  soundspc.cache         your sounds
  en.w3strings …         your strings
  info.json              the mod's name, author and version
```

- **Scripts** are always compiled into `precompiled.rsblob`, the way REDkit publishes a mod. If any don't compile, nothing is built, and **Show errors** lists the mistakes, each opening its line.
- With **Also ship the loose scripts** ticked in [[Projects|Project properties]], the `.ws` files go in `content\scripts` beside it, as your mod's source for others to read or merge. The `info.json` tells the game to run the compiled ones.
- **Workspace files** are packed into the bundle, textures into the texture cache (cooked from your pictures, see [[Changing game files]]) and sounds into the sound cache.
- **Sound banks** (`.bnk`) must be made with Wwise 2023.1, as the game's own are (bank version 150). The game won't load a bank of any other version, such as one made for the next-gen game, and never plays it. Build still packs it, but says which banks those are.
- **Voice lines** go in a speech file (`enpc.w3speech`, `depc.w3speech`…), made with nikich340's [RedSpeech Tool](https://www.nexusmods.com/witcher3/mods/14750). Put it at the top of your workspace, as you would a `collision.cache`, and Build puts it in `content` as it is. Loose voice-line `.wem` files under `speech\` are left out, with a note, since the game doesn't read them.

## The project's own DLC

Some things only a DLC can do, such as mounting the XML that overrides the game's items (see [[Items and stats]]). Your project's own DLC is the workspace's `dlc\dlcNAME` folder, NAME being your mod's name in lower case (`modMyRebalance` gives `dlcmyrebalance`). Build makes it into `build\dlcNAME\content` beside the mod, with a `dlcNAME.reddlc` that mounts each XML file in its `data\gameplay\items` folder (and New Game+'s in `items_plus`), unless the folder has a `.reddlc` of its own.

The `build` folder is made anew each time, so don't keep anything of your own in it.

## Install

**Project > Install modMyFirstMod…** copies the build into the game's `mods` folder, after saying where, and the project's DLC, when it has one, into the game's `dlc` folder. It only replaces those folders, and never one of the game's own DLC. If a folder of that name is there and wasn't installed from this project (someone else's mod of the same name, say), it asks first.

> tip: Build, install, then load a save to try your changes. Turn on the game's debug console to try `exec` functions. See [[Console commands]].
