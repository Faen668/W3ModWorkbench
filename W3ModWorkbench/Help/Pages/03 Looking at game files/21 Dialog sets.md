---
title: Dialog sets
icon: E722
order: 21
summary: Where a dialogue's actors stand and the cameras that film them, in 3D with stand-in actors, and any camera looked through.
keywords: w2dset dialogset dialog set scene slots slot cameras camera shot closeup semicloseup medium wide stance placement
windows: DialogsetWindow
---
A **dialog set** (`.w2dset`) is the stage of a conversation: the **slots** its actors stand in, each with the stance and face its actor starts in, and the **cameras** that film them. Scenes name one for each section, and the game stands it where the scene's placement tag says. Double-click one to open the dialog set viewer.

![A ring of ten slots with their cameras](dialogset-viewer.jpg)

## The view

A stand-in stands in each slot, in the idle of the stance the slot gives (standing, sitting, lying…). A dialog set names places, not people, so pick who stands in under **Stand-ins**: a man, a woman, a child or a dwarf.

- Each slot has a ring on the ground and an arrow the way it faces. Each camera is a small yellow frustum where it stands.
- **Names** labels the slots and cameras; with many cameras, only the one picked is named.
- **Idles** plays each stand-in's idle, as the actors breathe and shift in the game.
- Drag to turn the view, use the wheel to zoom. **Fit** ({{F}}) frames every slot.

## Looking through a camera

Pick a camera and press **Through camera** ({{C}}), or double-click it, to see its shot as the game frames it.

![Looking through a semicloseup camera](dialogset-through-camera.jpg)

## Slots and cameras

- **Slots**: each slot's number and name, where it is, which way it faces, the stance (status, mood, pose) and the face its actor starts in. Pick one to light it up in the view.
- **Cameras**: each camera's name, its shot (closeup, medium, wide…), the slots it films from and to, its field of view and tags. Type to find one, or pick a slot to see only the cameras filming it. The box below has its position, angles, lens and depth of field. Right-click to copy its name, or where it stands and its angles.
- **Properties**: the dialog set's own properties.
