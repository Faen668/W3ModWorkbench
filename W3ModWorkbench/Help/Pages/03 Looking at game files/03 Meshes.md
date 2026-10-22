---
title: Meshes
icon: E7B8
order: 3
summary: See a model in 3D with its textures, look at its parts and materials, change them, export it as glTF or OBJ, import a glTF made in Blender back onto it, or make a new mesh from one.
keywords: mesh w2mesh 3d model lod chunk material texture export import new mesh create gltf glb blender obj collision wireframe bones skeleton rig weights buffer replace edit model
windows: MeshWindow
---
A **mesh** (`.w2mesh`) is a 3D model: a sword, a wall, a piece of armour, a monster's body. Cloth (`.redcloth`) opens in the same viewer, with its simulation on an extra tab: see [[Cloth]]. Double-click one, or highlight it and press {{Enter}}, to see it in 3D with its textures. The preview pane shows it too, smaller.

![The griffin's mesh in the mesh viewer, with its parts and materials on the right](mesh-viewer.jpg)

A mesh keeps its shape in `.buffer` files and its look in materials and textures, all in other files. The viewer finds them as the game would, so a mesh from the game shows with its textures without opening anything else, and a mod's mesh shows the mod's textures.

## Looking around

| Do | To |
|---|---|
| drag | turn round the mesh |
| right-drag (or {{Shift}}+drag) | move it |
| wheel | come closer |
| double-click, {{F}} | fit it to the view |
| **Front**, **Side**, **Top** ({{1}}, {{2}}, {{3}}) | look from that side |

- **LOD** picks the level of detail: 0 is the one used up close, and each one after it says how many triangles it has and from how far away the game uses it.
- **Textures** ({{T}}) off shows the shape in plain grey; **Wireframe** ({{W}}) draws the triangles' edges.
- **Floor** ({{G}}) draws a grid under it.
- **Show** draws how it's made: the **bounding box** ({{B}}), **vertex colours** ({{V}}), **normals** ({{N}}), a skinned mesh's **bones** ({{K}}) and its **collision** ({{C}}).
- The panel at the top left ({{I}}) counts the triangles, vertices and chunks, and gives the mesh's size and the memory its data and textures take.

## Chunks and materials

A mesh is made of **chunks**, each drawn with one **material**. Untick a chunk to hide it, say to see what's under a cloak.

Each material says where it's from (kept in the mesh, or a material file, `.w2mi`) and what shader it's based on. **Colour texture** picks which of its textures is drawn, which is how you look at a normal map or a mask on the model itself.

- **Other texture…** puts any texture on the material's chunks.
- **Other material…** draws them with another material file.
- **Open its base** opens the material in the [[Materials|material viewer]].

## Changing materials

Open **Parameters** on a material to see every setting its shader has: textures, numbers, colours and vectors. Type a new value and press {{Enter}}, or pick a texture with **…**. A new colour texture shows on the model at once.

**Save to project** ({{Ctrl+S}}) saves the changes into your project, never over the game's files. See [[Changing game files]] for where they go and how a shared material is handled. **Save as new material…** saves a material as a new `.w2mi` for these chunks.

## The other tabs

- **Properties**: every property of the mesh, with a search.
- **LODs**: each level of detail, the distance it's drawn from, and its chunks.
- **Report**: the memory the mesh and its textures take, how far away it's drawn, and every texture its materials use, with a picture of each.
- **Collision**: the shapes the game collides with, from its `collision.cache`.

## Exporting

**Export** saves the mesh, with the ticked chunks, for Blender, 3ds Max, Maya and most other 3D tools. Pick the kind of file in the save box:

| Kind | What's in it |
|---|---|
| **glTF binary** (`.glb`) | One file with everything: the mesh, its materials with their textures, and a skinned mesh's bones and weights. Blender opens it with **File > Import > glTF 2.0**. |
| **Wavefront OBJ** (`.obj`) | The shape only, with a `.mtl` and each material's colour texture as a PNG beside it. No bones. |

The viewer remembers which you picked last.

### What a .glb has

- **The LOD shown**, or **every LOD**, each a node of its own (`name_lod0`, `name_lod1`…). Choose which in [[Settings]] > **Viewers**.
- **Materials** named after their `.w2mi` or the material they're based on, with the colour texture, the normal map and the roughness the game keeps in the normal map's alpha. Hair is cut out by its colour's alpha and seen from both sides, as in the game. Turning the textures off in Settings makes the file much smaller.
- **Vertex colours**: hair the game tints with them (beards, eyebrows, most NPCs' hair) has them as its colour, so Blender shows it the colour it is. Every mesh with vertex colours also has them as a plain attribute named `_W3_COLOR`, with the byte the game keeps beside them, which nothing paints with; Geralt's hair keeps masks there. An eye's shadow, which the game draws over the eye to darken it, is a faint see-through black.
- **A second UV set** (`UVMap.001` in Blender), for meshes the game gives one.
- **Bones**, for a skinned mesh: each one where the mesh was bound to it, under an **Armature**, with every vertex's weights (up to 8 bones a vertex). Which bone is whose child comes from the rigs beside the mesh and in the folders above it: the body's `.w2rig`, a head's face (`.w3fac`), a dangle's `dyng_` rig, or the base character's. The message after saving says which were used. A bone none of them has hangs straight from the armature.
- **The mesh is turned to stand up** as glTF expects (the game is Z-up, glTF Y-up), and it sits exactly where the game has it, to a fraction of a millimetre.

What the game knows that glTF has no place for is kept in each part's **extras** (Blender shows them as custom properties), so the file can be matched back to the mesh later:

```json title="a part's extras"
{ "w3Chunk": 0, "w3Material": 0, "w3LodMask": 1, "w3RenderMask": 1 }
```

The file itself keeps the mesh's path, its LOD distances and the rigs used; each material keeps its source, shader, bases and every texture's game path.

> tip: Untick chunks before exporting to leave them out, say a monster's eyes or an armour's shadow-only parts.

## Importing a glTF

**Import** puts a `.glb` or `.gltf` onto the mesh open: its parts take the place of the mesh's chunks, and everything else the mesh has stays as it is (its materials, how far away each LOD is drawn, its collision, how far away it hides). The new mesh shows in the viewer straight away; nothing is saved until **Save to project**, which puts the `.w2mesh` and its `.buffer` in your mod, in place of the game's.

![A barrel made taller in Blender, imported back onto the game's barrel](mesh-import.jpg)

The usual way round:

1. Open the mesh and **Export** it as glTF.
2. In Blender, **File > Import > glTF 2.0**, change it, then **File > Export > glTF 2.0** (Blender's own settings do).
3. Back in the viewer, **Import** the file Blender saved, look it over, and **Save to project**.

### How the parts are matched

- **LOD**: from the part's name, `name_lod0`, `name_lod1`…, as the export names them. Keep those names in Blender. A LOD the file has no parts for gets the file's LOD below it (or keeps the mesh's own; choose in [[Settings]] > **Viewers**).
- **Material**: from the material's name, as the export names them (Blender's `.001` is left off), so keep those too. Parts with the same LOD and material become one chunk.
- **Bones**: by name. A skinned mesh keeps its bones and how they're bound; a bone the file adds is added to the mesh. Each vertex keeps its four strongest bones (eight in meshes made for eight).

What the file hasn't got is made or taken from the mesh as it was: normals from the triangles, tangents from the UVs, and vertex colours, or a skinned mesh's weights when the file has no armature, from the nearest vertex of the same material. The message after importing says what was done.

> tip: To keep vertex colours exactly as they are, tick **Data > Mesh > Attributes** in Blender's glTF export, which keeps `_W3_COLOR`; **Include > Custom Properties** keeps the extras too. Without them, the nearest vertex's are used, which is right unless the shape moved a long way.

### What can't change

- A chunk has 65,535 vertices at most; a bigger part is split into several chunks, and a mesh has 255 bones at most.
- Only a mesh of the game as it's cooked can be imported onto, not a cloth (`.redcloth`).
- **Collision isn't remade.** The game keeps a static mesh's collision in its `collision.cache`, made from the old shape, so walls and floors still collide where they were.
- The chunks only for shadows are made again from the new ones, as the game cooks them.

![The Viewers settings for exporting and importing meshes as glTF](mesh-export-settings.jpg)

## A new mesh from a glTF

To add a model of your own rather than change one of the game's, right-click your project in the main window's tree (or a folder in its workspace) > **New mesh from glTF…**, pick the `.glb` or `.gltf`, then where the mesh goes. Its place in the workspace is its path in the game. The mesh, its `.buffer` and the pictures its materials use go in your project, and it opens in the mesh viewer.

![A monkey made in Blender, as a mesh of the game's own, its texture shown from the picture beside it](mesh-new.jpg)

- **Parts and LODs** are made as an import's are: each part's LOD from its name (`name_lod1`), else LOD 0, and a chunk for each material in each LOD. The LODs are drawn from 0, 10, 20, 40… metres away, and the mesh hides from 100 metres (or twice its last LOD's distance).
- **A skinned mesh**: when the file's parts have a skin, the mesh is skinned, with its bones as the file binds them, four bones a vertex (eight when the file gives more). To be seen moving in the game it needs an entity with a rig that has bones of the same names.
- **Materials**: one exported from the game (the export's `w3Source`, which Blender keeps with **Custom Properties** ticked) uses that material again. Any other becomes a material of the mesh's own on the game's standard shader (`pbr_std`): the file's colour picture its **Diffuse**, and its normal map, with the roughness put in its alpha as the game keeps it, its **Normal**. Those pictures go beside the mesh as `.tga` (`name_material_d01.tga`, `_n01.tga`), shown in the viewer at once, and **Build** makes them into textures. Change any of it afterwards in the viewer: **Other material…**, **Other texture…** or the parameters.

> note: A new mesh has no collision, so nothing bumps into it, and see-through parts are drawn solid on the standard shader: give their chunks a cut-out material (one on `pbr_hair`, say) in the viewer. The mesh's `generalizedMeshRadius` is its bounding sphere's; the game's own meshes were given theirs when they were made.

> note: Materials are drawn with the game's own shader settings: normal maps, roughness, skin, hair, eyes, glass and what glows, lit with one of the game's daylight probes. Dyes are only shown in the entity, scene and cutscene viewers, so dyeable armour shows its raw tint-mask colours here.
