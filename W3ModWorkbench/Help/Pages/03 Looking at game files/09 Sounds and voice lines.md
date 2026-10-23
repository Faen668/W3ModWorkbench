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
- **A bank** lists every sound it plays: click one to play it, or right-click to open it in a window of its own. The line at the top gives the bank's version, and says so when the game won't load it: the game loads only version 150 banks, made with Wwise 2023.1, so a bank from the next-gen game or the original game plays here but not in the game. **Upgrade to v150…** at the top makes such a bank, or the whole cache it's in, one the game loads (see [[Upgrading old sounds]]).

## What's in a bank

A bank keeps more than its sounds: the events that play them, what each event does, and every setting Wwise gave each sound. **Objects** shows all of it, for any bank from 2013 on, the original game's (version 88), the next-gen game's (140) and the Remastered's (150) alike.

![A bank's objects: its events, and a random container's settings](sound-bank-objects.jpg)

- **The tree** starts with the bank, then its **events**, each opening to its actions and what they act on, down through containers to the sounds. Below that, every object by kind: sounds and containers, music, actions, buses, states, effects, attenuations and modulators. Type in the box to find one by name, kind or ID.
- **Its settings in words**: where the sound is kept and in which codec, its output bus, its volume and other properties, what's randomised and by how much, its effects in order, 2D or 3D, the volume each game state sets, the game parameters that change it, a container's mode, loop and playlist with weights, a switch container's switches. **Play** plays the sound it plays.
- **Effects** show their settings by name: a reverb's decay time and room size, a harmonizer's voices, a delay's time and feedback, and so on for every effect the game uses.
- **Project settings** appear in a project's `Init.bnk`: its state groups with their transition times, its switch groups and the game parameters that drive them, every game parameter's starting value, the plugins it uses, and its obstruction and occlusion curves.
- **All its values** below are every value the bank keeps for it, read to its last byte.
- **Copy all** and **Save as text…** give the whole bank as text: a record of every setting, handy before making a bank again in Wwise.

Wwise keeps names only as numbers (hashes), so names are found by hashing the strings that could be them: the game's own sound names come with the app, and the scripts of the game, its mods and any scripts folder near the bank add theirs. A mod's events show by name when its scripts are installed beside it. When you make a bank again, give each event exactly its old name, and the mod's scripts and scenes go on playing it.

## Sound events

The game plays sounds through **events**: a footstep plays `g_footstep_walk`, a scene or an emitter plays others. Wherever an event is named (in a property list, on an animation's timeline, in a cutscene's events) you can open it, and the sound viewer lists every sound it can play, each to click and hear, and what the event does.

## Making voice lines

For voice lines of your own, use [RedSpeech Tool](https://www.nexusmods.com/witcher3/mods/14750) by nikich340. It packs and unpacks the game's speech files (`enpc.w3speech` and the others), turns WAV into the game's WEM, and converts lip sync to and from REDkit's. The link under a voice line's words opens its page. Put the speech file it makes at the top of your workspace, and **Build** puts it in your mod as it is (see [[Building and installing]]).

> note: Every one of the game's 90,995 sounds plays, read straight from the game's files. REDkit isn't needed.
