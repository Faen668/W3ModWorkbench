---
title: Particle systems
icon: E945
order: 19
summary: Watch an effect play (fire, smoke, sparks, magic), see each emitter's modules and their curves, and switch any of them off.
keywords: w2p particle particles effect fx emitter emitters module modules initializer modificator curve fire smoke sparks billboard atlas subuv lod birth rate burst
windows: ParticleWindow
---
A **particle system** (`.w2p`) is a visual effect made of many small pictures: a campfire, smoke from a chimney, sparks from a sword, a sign's magic. It's made of **emitters**. Each one pours out particles and draws them with its material, and its **modules** shape them: how long they live, where they start, how fast they move, and how they grow, fade and change colour. Double-click one to open the particle viewer, laid out like REDkit's particle editor.

![A campfire playing, with an emitter's alpha curve](particle-viewer.jpg)

## The preview

The effect plays at the top left, over a grid one metre a square.

- **Play / pause** ({{Space}}) and **Restart** ({{R}}). It starts already run on by the effect's prewarming time, as the game does, so a fire is burning when you open it.
- **×0.1** to **×2** slows it down or speeds it up.
- **LOD** plays a level of detail: further ones may birth fewer particles or leave emitters out.
- Drag to turn round it, right-drag to move, use the wheel to zoom. **Fit** ({{F}}) frames the particles there are now.

Shaders that add light (fire, sparks, glow) are added to what's behind; the rest cover it. Each particle shows its texture's atlas frame.

> note: The preview plays the editor's data closely, but it's not the game's renderer: particles aren't lit by the sun, and collisions, targets in the world and the scene's wind are left out.

## Emitters and modules

The bottom right has a box for each emitter, in its own colour, as REDkit lays them out: its texture, how it's drawn (billboard, motion blur, mesh…), how many particles it has at most, whether it adds light, and its modules.

- Untick an emitter's box to hide it, or a module to switch it off. The preview starts again without it, so you can see what each part does.
- Click a module to see it. Click an emitter's name to see the emitter.

## Curves

The top right draws the module you picked. Most values are read over a particle's life, from 0 when it's born to 1 when it's gone: **Alpha over life** fades it in and out, **Size over life** grows it. Values set when a particle is born are read over the emitter's run instead.

- A curve shows its keys. A random value shows its low and high ends; a constant is a flat line.
- A vector has a line for each axis (X red, Y green, Z blue); a colour, one for each channel.
- Hover over the chart to read the values off. When a module has several values, pick one at the top right.

## Properties

- **Module**: the picked module's values, each with its kind of evaluator (constant, random, start to end, curve) and value.
- **Emitter**: all of the emitter's properties.
- **Material**: its shader, whether it adds light, its atlas, and each parameter, the emitter's own over its shader's defaults. Double-click a texture to open it.
- **System**: the effect's own properties (its LODs, prewarming time, how far away it hides).

> tip: The game's cooked effects have their modules compiled away. When REDkit is installed, the viewer plays REDkit's own copy, which has them, and the bar at the bottom says so. Without REDkit, a cooked emitter shows its properties but can't play.
