---
title: Materials
icon: E790
order: 4
summary: Look at and change a material's textures, colours and numbers, previewed live on a sphere, a mesh or a whole character.
keywords: material w2mi w2mg shader parameters texture colour preview used by based on
windows: MaterialWindow
---
A **material** decides how a surface looks: which textures it uses, its colours, how shiny it is. A material file (`.w2mi`) sets some of its shader's settings and takes the rest from what it's based on, down to the shader itself (`.w2mg`). REDkit can't open these; here you can look at them and change them.

Double-click a `.w2mi` or `.w2mg`, or press **Open the material** in the [[Meshes|mesh viewer]].

![A material from Geralt's boat, previewed on the boat](material-viewer.jpg)

## Parameters

Every parameter is listed, grouped: textures with their pictures, colours as swatches, numbers and vectors. The material's own are in **bold**; the rest say where they come from. Untick **Inherited too** to see only its own, and type a few letters ({{Ctrl+F}}) to find one.

Change any of them:

- type a value, or drag a number's slider
- click a colour's swatch for its red, green, blue and alpha
- pick a texture with **…**, from the game or a mod you have open

A changed parameter becomes the material's own. Take it away again to go back to its base's value, and **Undo changes** ({{Ctrl+Z}}) puts everything back.

## The preview

The preview draws the material as the game draws it, and changes as you do.

- **Preview on** has the game's sphere and box, the meshes that use the material, and (with the Asset Browser open) the characters and objects drawn with those meshes, whole and dyed.
- **View on model…** picks any mesh or entity in the game.
- **Spin** ({{P}}) turns it slowly; **Fit** ({{F}}) frames it again.

## Based on, and used by

- **Based on** shows what the material is built on, nearest first. Click one to open it.
- **Used by** lists the meshes and materials that use it. Double-click one to open it, or right-click a mesh to preview the material on it.

## Saving

- **Save to project** ({{Ctrl+S}}) saves the changes into your project at the material's own game path, so every mesh that uses it changes in your mod.
- **Save as new material** saves it as a new `.w2mi`, for meshes you point at it with the mesh viewer's **Other material…**.

See [[Changing game files]].
