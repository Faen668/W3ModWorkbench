---
title: Voice lines
icon: E720
order: 7
summary: Unpack the game's speech files into sounds and lip syncs, and pack your own voice lines into one.
keywords: voice speech w3speech enpc wem lipsync lip sync raw cr2w dialogue vo pack unpack redspeech
windows: SpeechWindow
---
The game keeps each language's voice lines in one **speech file**: `enpc.w3speech` for English, `depc.w3speech` for German, and so on. Each line is a sound (a Wwise `.wem`) and its lip sync, found by the line's string ID, the same number its words have in the strings files.

Open **Tools > Voice lines (.w3speech)…**, or drop a speech file or a folder on the window.

![Unpacking a speech file and packing a folder](speech-window.jpg)

## Unpacking

Choose a speech file. The window says its language, how many lines it has and its size, and suggests a `speech\<language>` folder beside it, the way a project keeps voice lines.

Each line becomes:

| File | What it is |
|---|---|
| `ID.wem` | the sound, as the game keeps it |
| `ID.raw` | its lip sync, in the format the Remastered's files use |
| `ID.cr2w` | its lip sync, in older files' format |
| `ID.wav` | with **Also save each sound as .wav**: a copy to listen to or edit (**Settings > Viewers** sets whether it's ticked) |

The files are named as nikich340's [RedSpeech Tool](https://www.nexusmods.com/witcher3/mods/14750) names them, so folders made by either open in the other.

## Packing

Choose a folder of `ID.wem` files, each with its lip sync beside it. Anything after the ID is ignored, so `2115490012_hello there.wem` is line 2115490012. The language comes from the folder's name (`speech\de` is German) or the list, and the speech file goes beside the `speech` folder.

- **Keep the lines already in the speech file** adds your lines to a speech file that's already there, and replaces the ones with the same ID.
- A `.wav` without a `.wem` is left out, with a note: the game only plays `.wem`. RedSpeech Tool turns WAV into WEM, and makes lip sync from a recording.

## In a project

You don't need to pack by hand. Put your lines in your workspace as `speech\en\ID.wem` (and `ID.raw`), and **Build** packs each language's folder into its speech file in the mod. A speech file at the top of the workspace gives the lines the folder hasn't got.

```text
My Mod\workspace\
  speech\en\2115490012.wem
  speech\en\2115490012.raw
  speech\de\2115490012.wem
```

## How the file is laid out

A speech file has a table of every line, then each line's sound and lip sync one after another. The table is sorted by each line's ID mixed with the language's number, and the game finds a line by halving it, so a line out of order can't be found at all. Any change means writing the whole file again: the game's own English file is 4.2 GB.

> note: The writer was checked by writing the game's own `enpc.w3speech` (64,718 lines) back byte for byte, and mods' speech files the same way.
