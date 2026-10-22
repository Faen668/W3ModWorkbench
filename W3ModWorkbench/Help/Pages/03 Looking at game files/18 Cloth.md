---
title: Cloth
icon: E7B8
order: 4
summary: See capes, skirts and coat tails move as they do in the game, try out their settings, and save them into your mod.
keywords: cloth redcloth apex physx cape cloak skirt dress coat tails flag simulation wind gravity stiffness damping collision
windows: MeshWindow:cloth
---
**Cloth** (`.redcloth`) is what swings in the game: Yennefer's cloak, Eredin's skirt, coat tails, banners and sails. The game doesn't animate it; it simulates it, with NVIDIA's APEX cloth. The body moves and the cloth follows, hangs with gravity and is pushed off the legs.

The workbench simulates cloth the same way, from the game's own data. You'll see it wherever a character is drawn: the [[Entities|entity viewer]], [[Cutscenes|cutscenes]], [[Scenes|scenes]] and the animation previews.

![Yennefer's cloak hanging and swinging in the entity viewer](cloth-entity.jpg)

## Opening a cloth file

Double-click a `.redcloth` to open it in the mesh viewer, as REDkit does. Everything on the [[Meshes]] page works for it too: LODs, materials, textures, **Export**. It also has an **Apex Cloth** tab, where the cloth hangs from its bones and simulates while you look.

![Yennefer's dress in the cloth viewer, swaying in a wind, with its settings](cloth-viewer.jpg)

## The preview

- **Pause** or **Simulate** stops and starts it. **Reset** puts the cloth back where its bones hang it and lets it fall into place.
- **Motion** moves the bones so you can see it swing: **Still**, **Sway** (side to side and back), **Spin** (round and round) or **Walk** (bobbing along).
- **Wind direction**, **Wind pitch** and **Wind strength** blow on it.
- The line under them says how many particles are simulated and how long each frame takes.

> tip: **Show** › **Collision** ({{C}}) draws the spheres and capsules on the bones that the cloth collides with. They move with the bones in the preview.

## The settings

The **Simulation** and **Material** cards have the settings REDkit's Apex Cloth tab has: collision thickness, gravity scale, friction, bending and shearing stiffness, tether stiffness and limit, damping, drag, inertia scale, mass scale and max distance bias. Move a slider, or type a value and press {{Enter}}, and the preview changes at once.

- Double-click a setting's name to put back the value the file has.
- **Restore all defaults** puts them all back.
- Settings in *italics* (vertical and horizontal stretching, the stiffness and solver frequencies) are kept only in the cloth's APEX asset, which the file has no property for. They change the preview, but aren't saved.
- Settings that are shown but not simulated in the preview say so, such as CCD and self collision.

**Save to project** ({{Ctrl+S}}) writes the settings into the `.redcloth` in your project, as the resource's own `mtl…` and `sim…` properties, which the game reads. See [[Changing game files]] for where it goes. Material changes save the same way, together.

> note: A cooked `.redcloth` keeps its shape in the game's `collision.cache`, under its path. Your mod's copy has the same path, so the game finds the shape there; the file you save only changes its settings and materials.

## What it simulates

**What it simulates** lists, for each level of detail:

| Column | What it is |
|---|---|
| **Particles** | the points the solver moves; the cloth's triangles are drawn between them |
| **Held** | particles that stay where their bones put them (where the cloth is sewn on) |
| **Stretch**, **Bend**, **Shear** | the constraints that keep the cloth's shape |
| **Tethers** | how many tethers each particle has, which stop it stretching away from where it's held |
| **Backstops** | particles with a sphere behind them, which keeps them off the body |
| **Furthest** | the furthest any particle may swing from where its bones put it |

Under it are the spheres and capsules it collides with, with their bones and sizes. The **Collision** tab lists them too: cloth has no collision of its own.

## In the other viewers

- In the **entity viewer**, the cloth falls into place when the character is shown, and moves with the animation. It plays even when the entity has no animations of its own.
- In **cutscenes**, it moves as the cutscene plays. The cutscene's own cloth events count: a **Reset cloth and dangles** event puts it back at a cut, and a **Cloth off** event turns it off. Jumping to another time lets it settle there.
- In **scenes**, it moves as each section plays.

Only the level of detail being drawn is simulated, so a far-off actor costs little.

> tip: With a big cast on a slow computer, turn off **Simulate cloth as the game does** in **Settings** › **Viewers**. The cloth then moves stiffly with the bones, like any other mesh. The cloth viewer always simulates.

## How close it is to the game

The simulation is a port of the PhysX 3 cloth solver that APEX runs, on the fabric APEX cooked for each cloth, with the cloth's own settings. Some things aren't simulated yet: self collision, virtual particles (a few cloths have them) and the extra inertia the game gives cloth when the whole character moves across the world. Wind in the preview is a simple drag towards the air's speed.
