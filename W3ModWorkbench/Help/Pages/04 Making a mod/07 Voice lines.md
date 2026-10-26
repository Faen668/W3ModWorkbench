---
title: Voice lines
icon: E720
order: 7
summary: Unpack the game's speech files into sounds and lip syncs, and pack your own voice lines into one.
keywords: voice speech w3speech enpc wem wav record recording opus bitrate lipsync generate make automatic learn lip sync raw cr2w re hdf5 blender redkit face dialogue vo pack unpack redspeech convert command line batch
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

Choose a folder of `ID.wem` files (or `ID.wav`, see below), each with its lip sync beside it (`.raw`, `.cr2w` or `.re`). Anything after the ID is ignored, so `2115490012_hello there.wem` is line 2115490012. The language comes from the folder's name (`speech\de` is German) or the list, and the speech file goes beside the `speech` folder.

- **Keep the lines already in the speech file** adds your lines to a speech file that's already there, and replaces the ones with the same ID.
- A `.wav` without a `.wem` is your recording, and is made a `.wem` as it's packed (below). A `.wav` beside its `.wem` is a copy to listen to, and the `.wem` is used.

## Your own recordings

Save each line as `ID.wav` in the folder and pack it, or Build: each one is made a `.wem` the way the game's voice lines are made (Wwise Opus at 48 kHz, 20 ms frames), so the game plays it as one of its own.

- Mono, stereo and 5.1 recordings, at any sample rate, as 8, 16, 24 or 32-bit PCM or as 32 or 64-bit float: what Audacity, Reaper and most recorders save. The game's lines are mono.
- **Settings > Viewers** sets the bitrate, 64 kbit/s a channel unless you change it, which is close to the game's own lines.
- To change a line you unpacked, edit its `.wav` and delete its `.wem`.
- A line with no lip sync gets one made from its recording (below).


## Lip sync made from a recording

With **Make a lip sync for each line that has none, from its sound** ticked, packing gives every line that has a `.wav` or `.wem` but no lip sync one made from its sound, and so does Build (**Settings > Viewers** sets whether it's on).

It's made the way your game's own lines teach it. The first time it's needed, the app listens to your game's voice lines, 10,000 of them, each with the lip sync CDPR made for it, and learns how the jaw, lips, tongue and cheeks move with the sound. That takes about ten minutes, once; **Learn again** does it again. What it learns is kept on this computer, in your settings folder, and nothing of the game's is copied anywhere.

- It hears the sound, not the words, so it works for any language, though it has learnt from the languages your game has voice lines in.
- It moves the mouth, as the game's lip syncs do; the brows and eyes are left to the scene.
- The line under the box says how close it came to the game's own lip syncs on lines it hadn't heard. Edit a made lip sync in Blender as any other: unpack it as `.re` (above).
- A lip sync already beside a line is always used as it is.



A `.re` file is the format REDkit's Blender plugin opens and saves: every frame of the face's controls (the jaw, the lips, the brows, the eyes and the rest), each as a curve you can edit. Unpack with **Save lip syncs as .re** ticked, edit the line's `ID.re` in Blender, save it over itself and pack the folder, or Build. A `.re` is turned into the game's own lip sync as it's packed; nothing else needs doing.

- The game keeps 148 controls for a face; REDkit's own `.re` files keep the first 145. Ours keep all 148, the last three moving the head a little, and Blender passes over any it hasn't got.
- A control in a `.re` that the face hasn't got is left out, with a note saying which.
- A control that stays where it is is kept as one value, as the game keeps it, so a lip sync packed from a `.re` can be smaller than the one it came from. Moving controls are kept frame by frame.
- REDkit's Blender plugin was made for Blender 4 (Python 3.11). On Blender 5 its **Import .re mimic animation** stops with an error about `fcurves`, for REDkit's own files too, so use Blender 4.x for lip syncs.
- Blender keeps a shape key between its **Range Min** and **Max**, 0 and 1 unless you change them. The game's lines dip a little below 0 now and then (the jaw most of all), and those dips come back as 0. Set **Range Min** to -1 on the face's shape keys before importing to keep them.

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
W3ModWorkbench.exe --wav-to-wem "D:\My Mod\recordings" --out "D:\My Mod\workspace\speech\en"
W3ModWorkbench.exe --lipsync-from-audio "D:\My Mod\workspace\speech\en" --to re
```

`--speech-unpack` takes `--wav`, `--re` and `--out`; `--speech-pack` takes `--keep`, `--kbps` and `--out`; `--wav-to-wem` makes each `.wav` a `.wem` beside it or under `--out`, with `-r`, `--overwrite` and `--kbps`; `--lipsync-from-audio` makes a lip sync for each `.wav` or `.wem` (`--to re` for Blender), `--speech-pack --make-lipsync` fills in the lines that have none, and `--learn-lipsync` learns again (`--game <folder>` when Settings hasn't the game's folder); `--lipsync` takes `--to re` or `--to raw`, `-r` for subfolders, `--out` and `--overwrite`. `--help voice` lists them all. The exit code is 0 when every file was done and 1 when some weren't.

## How the file is laid out

A speech file has a table of every line, then each line's sound and lip sync one after another. The table is sorted by each line's ID mixed with the language's number, and the game finds a line by halving it, so a line out of order can't be found at all. Any change means writing the whole file again: the game's own English file is 4.2 GB.

> note: A `.re` is an HDF5 file, the kind REDkit's Blender plugin opens and saves. Every face control keeps its values when a lip sync goes to `.re` and back.

> note: A `.wem` made from a `.wav` is made as the game's own voice lines are, with the game's settings, so the game plays it as one of its own. The encoding is Opus's own, from the Concentus library the app already uses to play the game's voice lines.
