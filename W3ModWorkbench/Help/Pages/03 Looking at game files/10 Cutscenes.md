---
title: Cutscenes
icon: E714
order: 11
summary: Play a cutscene in 3D with its actors, faces, camera, music and voices, and look at every event along its timeline.
keywords: cutscene w2cutscene timeline camera actors play music voices dialog events
windows: CutsceneWindow
---
A **cutscene** (`.w2cutscene`) is recorded: each actor and the camera have an animation made for the whole of it, and events (lines, music, effects, fades) are timed along them. Double-click one to open the cutscene viewer, laid out like REDkit's cutscene editor: the actors playing at the top, the timeline below, and REDkit's tabs beside them.

![The prologue's griffin cutscene, playing](cutscene-viewer.jpg)

## Playing it

Press **Load preview** over the view to load the actors (until then the events, properties and sound can already be looked at). Everything loads first, so it plays smoothly.

- **Play** ({{Space}}), {{Home}} to go back to the start, the loop button ({{L}}), or drag the bar. Clicking the timeline's ruler moves there too.
- Drag the view to turn, right-drag to move, the wheel to come closer; double-click to see everyone.
- **Through the camera** ({{C}}) shows it as the cutscene's camera sees it, in a 16:9 picture. **Aspect ratio** offers 21:9, 2.39:1 and 4:3 too, and **Camera** picks one of several.
- Each actor is put together from their entity, in their appearance, with their face moving and their lips synced to their lines. Geralt wears the gear he starts the game in.

It plays with its sound: its music and effects, and each line's voice (in English). The music button and the microphone button ({{V}}) turn each on and off; {{M}} both. **Save sound…** in **Dialogs** saves them as WAV files.

## The timeline

The top lane, **Scene**, has the events of the whole cutscene: lines, music, effects, fades, depth of field and lights. Each actor has a lane under it with their own: items in hand, effects, looks, cloth. Click an event to see its properties.

- **Fit** ({{F}}) fits the cutscene to the width; **Zoom**, or {{Ctrl}} and the wheel, widens it.
- The buttons above it show or hide each kind of event, with how many there are.

## The tabs

| Tab | Shows |
|---|---|
| **Properties** | the event you picked |
| **Cutscene** | the actors, props and cameras it spawns; click one to list only its events |
| **Template** | the cutscene's own properties |
| **Effects** | the effects its events use |
| **Animation** | each actor's body and face animation |
| **Dialogs** | each line: when, who and what they say. Click one to go to it. |
| **Scenes** | the scenes that play it; click to open in the [[Scenes|scene viewer]] |
| **Camera** | what the camera does at this moment |
| **Events** | every event in a table, with a search ({{Ctrl+F}}) |
| **Files** | everything it refers to; click to open |
