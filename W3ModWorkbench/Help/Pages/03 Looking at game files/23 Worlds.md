---
title: Worlds
icon: E909
order: 23
summary: A whole world from above: its terrain, every layer's entities, its navigation and areas, and its layers as a tree.
keywords: w2w world srt speedtree minimap game map foliage trees grass outlines areas triggers pins waypoints components level map layers layer groups w2l entities terrain w2ter tiles heights relief navigation navmesh navtile pathlib occlusion umbra dlc entities browser
windows: WorldWindow
---
A **world** (`.w2w`) is a level of the game: White Orchard, Velen, Skellige. Its ground is **terrain**, square tiles of heights and painted textures. What stands on it is in **layers** (`.w2l`), grouped by folder, each holding entities: buildings, props, lights, spawn points, triggers. Double-click a world to open it as a map, with its layers as a tree, as REDkit's world editor lists them.

![White Orchard from above, every layer's entities on its terrain](world-map.jpg)

## The map

The terrain is drawn from its tiles, and every layer's entities are dots in their group's colour. Drag to move, use the wheel to zoom, double-click or **Fit** ({{F}}) to see the whole world.

- **Relief** shades the ground by its heights (sea blue, high ground white). **Colour map** shows the terrain's own colours; **Textures** gives each texture painted on it a colour of its own. **Game map**, what a world opens on, lays the game's own minimap under everything, as the player sees it; a world the game has no minimap for opens on its relief.
- **Foliage** draws the world's trees and grass. From afar each tree is a disc in its own colour and the grass a wash; zoom in and every plant is drawn as it looks from above, made from its SpeedTree file (`.srt`) with its own pictures, turned and sized as it's placed. Grass is seen looking down at a slant, so its blades show. The tooltip says how many plants and kinds there are.
- **Outlines** draws every area the shown layers have: triggers yellow, sounds cyan, navmesh areas blue, environments purple, scenes pink, denied areas red, the rest grey. Some reach past the terrain, such as the global environment and the sea's sounds.
- **Pins** draws the hub map's pins: signposts, places of power and the like in white, quests' places in yellow with their areas. **Waypoints** draws the cooked spots NPCs walk to and stand at.
- Pick a **simplex tree** in the list beside it to draw its areas (the wind dampers, the water visibility).
- **Navigation** draws where nothing can walk (red), the ground navmeshes take over (blue), and each navmesh's border.
- **Tiles** draws the terrain tiles' edges; **Occlusion** the occlusion grid's.
- Hover to see where you are, how high the ground is, the textures painted there and which tile it is.
- Click a dot to pick its entity: its name, class, layer, where it stands and which way it faces, and its components, with a link to each one's mesh, template or other file. With **Outlines** on, the entity's own areas are filled in. **Open template** opens its entity; **Open layer** lists everything in its layer.

![A fisherman's hut picked: its interior and sound areas filled in, its components listed](world-map-picked.jpg)

![Close up on the game map: every tree and tuft of grass drawn from its own SpeedTree file](world-map-foliage.jpg)

![The village's navmesh and blocked buildings, a rabbit found in Entities](world-map-navigation.jpg)

## Layers, entities and the world

- **Layers**: the groups and their layers, each with how many entities it has. Untick one to hide it; pick one to light its entities up and frame them. Right-click to show only that, show everything, open the layer or copy its path. Layers a DLC adds to the world from its own folders are in a **DLC** group, as REDkit shows them.
- **Entities**: every entity, found by its name, type, layer or template, as REDkit's Entities Browser does. Pick one to frame it on the map.
- **World**: the terrain's size, tiles and heights, the occlusion grid, how many layers and entities there are, and every file the world names (its environment, sounds, terrain material, hidden layer groups), each opened by a click.
- **Properties**: the world's own properties.

> note: REDkit's worlds find their layers by their folders. The game's cooked worlds keep the whole tree of layer groups inside the world instead, and their layers keep only what the game needs, so a cooked world can have fewer entities than REDkit's copy.
