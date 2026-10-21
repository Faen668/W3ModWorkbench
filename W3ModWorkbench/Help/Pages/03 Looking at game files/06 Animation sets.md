---
title: Animation sets
icon: E714
order: 7
summary: Play any of the game's animations on a character, with its events, bones and the path a bone takes.
keywords: animation w2anims anim set play bones skeleton trace events blend loop motion
windows: AnimSetWindow
---
An **animation set** (`.w2anims`) keeps the animations one kind of character plays for something: a weapon's attacks, a job's work, a creature's moves. Each animation has events (sounds, footsteps, effects) timed along it.

Double-click a `.w2anims` to open it. The window is laid out like REDkit's animation browser: the character at the top left, the animations at the top right, and the events along a timeline underneath.

![The griffin's animation set, one of its animations playing](animset-viewer.jpg)

## Playing animations

- **The animations** are in a table: each one's length, frames, events, how far it moves and turns the character, and the bones it moves. Click a heading to sort, type letters ({{Ctrl+F}}) to narrow it, and click one to play it.
- **The character** is picked to suit the set (Geralt, a man, a woman, a child, a dwarf, or the creature the set is for); choose another in **Preview on**. An animation made for another body plays in proportion.
- {{Space}} plays and pauses, the arrow keys step a frame, and the speed box slows it down or speeds it up.
- **Loop** plays it round, or stops on its last frame. **Blend from** blends it in from another animation (its idle to start with), as the game blends one into the next.

## Bones

- **Bones** draws the skeleton over the character as it moves, and **Names** labels each bone.
- **Trace** draws the path one bone takes over the whole animation. Pick `r_weapon` to see a sword's swing.

## Events and properties

- **The timeline** has a row for each track, with the set's own events and those of the events files it names. Click an event to see its properties under **Event**; double-click a sound to hear it.
- **Animation** has the animation's properties and how far it has moved the character by now.
- **Character's sets** lists every animation set the character's templates use. Double-click one to open it.
- Right-click an animation to copy its name, or open its events file.
