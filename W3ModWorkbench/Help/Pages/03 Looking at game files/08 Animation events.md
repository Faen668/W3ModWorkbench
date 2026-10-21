---
title: Animation events
icon: E8D6
order: 8
summary: The sounds, footsteps and effects timed to a set of animations, along a timeline, with the animation playing.
keywords: w2animev animation events sounds footsteps timeline tracks
windows: AnimEventsWindow
---
An **animation events** file (`.w2animev`) keeps events timed to a set of animations (sounds, footsteps, effects, things scripts hear) apart from the animations themselves. Double-click one to open it, laid out like REDkit's animation events editor.

![An axe's sound events, its animation playing on a man](animev-viewer.jpg)

- **The animation set** the events are for is found from the file's name (`man_axe_1handed_sounds.w2animev` goes with `man_axe_1handed.w2anims`), or pick one in **Animation set**. Animations the set hasn't got are dimmed.
- **The character** is picked to suit the file; choose another in **Preview on**. {{Space}} plays and pauses.
- **The animations** say how many events each has. Click one to play it and see its events.
- **The timeline** has a row for each track, each event where it starts: blue for a sound, brown for a footstep, orange for an effect, a bar for one that lasts. Click an event to see its properties under **Event**, click the ruler to go to a moment, and turn the wheel to zoom.
- **File** has the file's own properties.

> tip: Double-click a sound event on the timeline to hear it. See [[Sounds and voice lines]].
