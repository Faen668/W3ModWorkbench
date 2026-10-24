---
title: Voice lines
icon: E720
order: 7
summary: Unpack the game's speech files into sounds and lip syncs, and pack your own voice lines into one.
keywords: voice speech w3speech enpc wem lipsync lip sync raw cr2w re hdf5 blender redkit face dialogue vo pack unpack redspeech convert command line batch
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
| `ID.raw` | its lip sync, in the format speech files have used since the next-gen update (version 163, unchanged in the Remastered's 164) |
| `ID.cr2w` | its lip sync, in the format of files from before it (version 162) |
| `ID.re` | with **Save lip syncs as .re, for editing in Blender**: its lip sync as REDkit keeps it, in place of the `.raw` or `.cr2w` (**Settings > Viewers** sets whether it's ticked) |
| `ID.wav` | with **Also save each sound as .wav**: a copy to listen to or edit (**Settings > Viewers** sets whether it's ticked) |

The files are named as nikich340's [RedSpeech Tool](https://www.nexusmods.com/witcher3/mods/14750) names them, so folders made by either open in the other.

## Packing

Choose a folder of `ID.wem` files, each with its lip sync beside it (`.raw`, `.cr2w` or `.re`). Anything after the ID is ignored, so `2115490012_hello there.wem` is line 2115490012. The language comes from the folder's name (`speech\de` is German) or the list, and the speech file goes beside the `speech` folder.

- **Keep the lines already in the speech file** adds your lines to a speech file that's already there, and replaces the ones with the same ID.
- A `.wav` without a `.wem` is left out, with a note: the game only plays `.wem`. RedSpeech Tool turns WAV into WEM, and makes lip sync from a recording.

## Editing a lip sync in Blender

A `.re` file is the format REDkit's Blender plugin opens and saves: every frame of the face's controls (the jaw, the lips, the brows, the eyes and the rest), each as a curve you can edit. Unpack with **Save lip syncs as .re** ticked, edit the line's `ID.re` in Blender, save it over itself and pack the folder, or Build. A `.re` is turned into the game's own lip sync as it's packed; nothing else needs doing.

- The game keeps 148 controls for a face; REDkit's own `.re` files keep the first 145. Ours keep all 148, the last three moving the head a little, and Blender passes over any it hasn't got.
- A control in a `.re` that the face hasn't got is left out, with a note saying which.
- A control that stays where it is is kept as one value, as the game keeps it, so a lip sync packed from a `.re` can be smaller than the one it came from. Moving controls are kept frame by frame.

## Converting lip syncs

**Convert lip syncs** turns lip sync files from one form to another without unpacking a speech file: `.raw`, `.cr2w` or `.re` files, or a folder of them (its subfolders too), to **.re (Blender)** or **.raw (the game's)**. Each is written beside the one it's made from; one already in that form is passed over, and so is one whose result is there already unless **Replace files there already** is ticked. Drop lip sync files on the window to fill it in.

## In a project

You don't need to pack by hand. Put your lines in your workspace as `speech\en\ID.wem` (and `ID.raw`), and **Build** packs each language's folder into its speech file in the mod. A speech file at the top of the workspace gives the lines the folder hasn't got.

```text
My Mod\workspace\
  speech\en\2115490012.wem
  speech\en\2115490012.raw     (or .re)
  speech\de\2115490012.wem
```

## On the command line

Each of these works without the window, for a script or a whole folder at once:

```text
W3ModWorkbench.exe --speech-unpack "D:\My Mod\content\enpc.w3speech" --re
W3ModWorkbench.exe --speech-pack "D:\My Mod\workspace\speech\en" --keep
W3ModWorkbench.exe --lipsync "D:\My Mod\workspace\speech" -r --to raw
```

`--speech-unpack` takes `--wav`, `--re` and `--out`; `--speech-pack` takes `--keep` and `--out`; `--lipsync` takes `--to re` or `--to raw`, `-r` for subfolders, `--out` and `--overwrite`. `--help voice` lists them all. The exit code is 0 when every file was done and 1 when some weren't.

## How the file is laid out

A speech file has a table of every line, then each line's sound and lip sync one after another. The table is sorted by each line's ID mixed with the language's number, and the game finds a line by halving it, so a line out of order can't be found at all. Any change means writing the whole file again: the game's own English file is 4.2 GB.

> note: A `.re` is an HDF5 file, read and written by our own code. A `.re` made from each of 3,236 of the game's lines and 1,145 older mod lines reads back to the same lip sync, every control within 0.0005 at every frame, and REDkit's own `.re` files read and write back with every value the same.

> note: The writer was checked by writing the game's own `enpc.w3speech` (64,718 lines) back byte for byte, and mods' speech files the same way.
