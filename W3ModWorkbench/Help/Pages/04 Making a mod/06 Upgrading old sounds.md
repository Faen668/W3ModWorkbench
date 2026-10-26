---
title: Upgrading old sounds
icon: E767
order: 6
summary: Make a sound mod from the original game or the next-gen update play in the Remastered.
keywords: sound bnk bank wem soundspc cache wwise version 88 140 150 upgrade convert old mod silent next-gen
windows: SoundUpgradeWindow
---
The Remastered plays only sound banks of **version 150**, made with Wwise 2023. Banks made for the original game are version 88, and banks from the next-gen update are 140. The game loads neither: such a mod installs without errors, but its sounds stay silent. **Upgrade sounds to v150** rewrites a mod's sounds so the Remastered plays them.

Open it from **Tools > Upgrade sounds to v150…**, or with **Upgrade to v150…** at the top of the sound viewer when a bank isn't version 150.

![A bank the game won't load, with the upgrade button](sound-upgrade-button.jpg)

## Upgrading a mod

1. Choose the mod's `soundspc.cache` (in its `content` folder), or drop it on the window. One `.bnk` file works too. The window says how many banks there are, of which versions, and how many won't load.
2. Choose where the upgraded file goes: in place of the old one, which stays beside it as `soundspc.cache.bak`, or as a new file. **Settings > Viewers > Upgrading old sounds** sets which one comes first.
3. Click **Upgrade to v150**. Each file is listed with what happened to it.

![A mod's cache upgraded, each bank with its notes](sound-upgrade.jpg)

To undo, delete the upgraded file and remove `.bak` from the end of the original's name.

## What changes

- **Every bank is rewritten as version 150.** Each object (sounds, containers, events, actions, music, attenuations, effects) is laid out as the Remastered's own banks lay theirs out, with its settings renumbered and repacked as version 150 keeps them. A state's values move into the objects it changes, and a 2D or 3D placement becomes 150's positioning.
- **The bank header** names the game's project and keeps its sounds on 16-byte boundaries, as every one of the game's banks does.
- **Sounds keep their audio.** Only the header of an old Vorbis sound changes: Wwise 2013 named a sound's speakers in a way that Wwise 2023 reads differently. Nothing is re-encoded, so nothing loses quality.
- **A streamed sound's size** is worked out from its `.wem`, the mod's own or the game's.
- **A mod's own `Init.bnk` is left out.** It would replace the game's, which every other sound needs.

> note: Some of the original game's sounds got new IDs in the Remastered. A mod bank that plays one of those by its old ID still won't find it, and its notes list each one. Those sounds have to be added to the mod again.

> tip: Old mods that replace whole game banks (music packs, for example) carry the original game's version of every other sound in that bank too. The upgrade keeps them, but the Remastered's own versions of those sounds are newer.
