---
title: Entities
icon: E77B
order: 6
summary: See a character, creature or object whole, in each of its looks, playing its animations, with every component it's made of.
keywords: entity w2ent npc character appearance components animation dye colours inventory voicetags template
windows: EntityWindow
---
An **entity** (`.w2ent`) is something the game spawns: an NPC, a horse, a door, a chest. It's made of **components** (meshes, a skeleton, sounds, triggers…), has **appearances** (the different looks an NPC can have), and often builds on other entity templates it **includes**.

Double-click a `.w2ent` to open the entity viewer. The preview pane shows its first appearance too.

![Triss in the entity viewer, standing in an idle, with her components on the right](entity-viewer.jpg)

## The character

On the left the entity is put together from all its meshes, in the **Appearance** you pick, with its textures and colours, as the game draws it. Hair takes its colour from its mesh, and dyed clothes are coloured as the appearance says.

- Drag to turn, right-drag to move, the wheel to zoom and double-click (or **Fit**, {{F}}) to see it all.
- **Textures** ({{T}}), **Wireframe** ({{W}}) and **Floor** ({{G}}) as in the mesh viewer; **LOD** picks the level of detail.
- The panel at the top left ({{I}}) counts what's drawn, and while it moves, how many frames a second.
- Click a part of the model to pick its component.

## Animations

When the entity has a skeleton, the bar under the view plays its animations. Pick an **animation set** and an **animation** (type the start of a name to jump to it). It opens standing in an idle when it has one.

- {{Space}} plays and pauses; drag the bar to any moment; the stop button stands it as it was made.
- The frame rate box sets how smoothly it's drawn (lower is lighter on your computer), and the speed box plays it from 10% to 200%.

## Components

On the right, the components are drawn as REDkit's entity editor lays them out, with a line from each to what it's attached to. Each is marked **I** (included from another template), **A** (from the appearance) or **O** (overridden here). Click one to see every property it has under **Component**. Click a mesh component and its mesh glows in the 3D view.

## The tabs

| Tab | Shows |
|---|---|
| **Components** | every component in a list |
| **Entity** | the properties of the object the game spawns |
| **Template** | its class, the templates it includes, and every file it depends on |
| **Appearances** | each look, with the templates it uses |
| **Colors** | how an appearance recolours a part, and its dyes |
| **Effects**, **Slots**, **AI**, **Inventory**, **Voicetags**, **Animation**, **Gameplay**, **Animation Slots**, **Dismemberment** | REDkit's tabs, each a list with the properties of the one you pick |
| **Files** | everything it refers to; click one to open it |

> tip: A dyeable piece of armour opens undyed. Pick a dye under **Appearance** to see it coloured the way the game colours it.
