---
title: Sounds and voice lines
icon: E767
order: 10
summary: Play any of the game's sounds, banks and voice lines, see what plays them, and save them as WAV.
keywords: sound audio wem bnk bank voice speech wwise play wav event music
windows: SoundWindow
---
The game's sounds are in the Asset Browser's game files:

- `sounds\banks`: **sound banks** (`.bnk`), each a group of sounds and the events that play them
- `sounds\streamed`: **streamed sounds** (`.wem`), the longer ones, named by number
- `speech\<language>`: **voice lines**, named by the number of the line they say

Double-click one to open the sound viewer.

![A sound bank, its sounds listed to play](sound-viewer.jpg)

## Playing

- **The waveform** shows the left channel over the right, with a ruler of seconds and the part that loops shaded. Click or drag on it to go to a moment.
- {{Space}} plays and pauses, {{Home}} goes back to the start, {{L}} loops.
- **Save as WAV** saves a file anything can play; **Save .wem** saves it as the game keeps it.

## What it shows

- **A voice line**: its words, in its own language or any other, who says it, its string ID and its lip sync. **Copy ID** ({{Ctrl+C}}) copies the ID.
- **Details**: everything the sound says about itself: its codec, channels, rate, length, loop and size.
- **Played by**: the banks that play a streamed sound. Double-click one to open it.
- **A bank** lists every sound it plays: click one to play it, or right-click to open it in a window of its own. **Objects** counts what's in the bank.

## Sound events

The game plays sounds through **events**: a footstep plays `g_footstep_walk`, a scene or an emitter plays others. Wherever an event is named (in a property list, on an animation's timeline, in a cutscene's events) you can open it, and the sound viewer lists every sound it can play, each to click and hear, and what the event does.

> note: Every one of the game's 90,995 sounds plays, read straight from the game's files. REDkit isn't needed.
