---
title: Textures
icon: EB9F
order: 2
summary: See any texture, its channels and mip levels, on a 3D shape, save it as PNG or DDS, or replace it with a picture of your own.
keywords: texture xbm dds png texture.cache cube w2cube texarray channels mip alpha normal map bc1 bc3 bc5 bc7 replace picture import
windows: TextureWindow, TextureArrayWindow
---
The game's pictures (textures) are `.xbm` files, and their pixels are kept in `texture.cache` files. Double-click an `.xbm`, `.w2cube` (cube map) or `.texarray` (texture array) and the texture viewer opens with the picture, found in a texture cache under the same path: one you have open, the one beside the bundle, or the game's own.

![The texture viewer showing one of the griffin's textures](texture-viewer.jpg)

## Looking at a texture

- **RGBA** shows the picture over a chequerboard where it's see-through; **RGB** without the see-through.
- **R**, **G**, **B** and **A** show one channel as grey. That's how masks and normal maps are checked.
- **Mip** picks a smaller level of the texture.
- **Face** picks one of a cube map's six faces, and **Slice** one texture of a texture array.
- **Fit** shows the whole picture; turn it off to see it at its own size, and hold {{Ctrl}} and turn the wheel to zoom.

## On a 3D shape

**3D** puts the texture on a lit **Sphere**, **Box**, **Cylinder** or **Plane**, as REDkit's texture viewer does. Drag to turn the shape, use the wheel to come closer, double-click to put it back, and move **Light** to light it from another side. A cube map wraps round the shape whole. **2D** goes back to the picture.

## Properties

On the right, **Texture** says what the cache holds: the format, size, mip levels, faces or slices and the memory it takes. Below are the texture file's properties, such as its compression and texture group. For the game's own textures they come from REDkit's copy when REDkit is installed, as cooking leaves most of them out; a note says where they came from. See [[Properties]].

## Saving

- **Save PNG** writes the level and channel you're looking at.
- **Save DDS** writes the whole texture with every mip level, for tools that read `.dds`: a cube map as a cube map, an array as an array.

## Replace with picture

**Replace with picture…** makes the texture from a picture of your own (PNG, TGA, DDS, JPEG, BMP or TIFF). The picture goes in your project beside the texture's game path, with the texture's name, and **Build** makes it into the texture, keeping the game's group, compression and size of mip chain for it (see [[Changing game files]]). It's made the same way straight away and opens in a window of its own, so you see what the mod will have. A message says what it became, and anything to know: a picture of another size than the game's, say.

![A picture replacing a texture, with what Build will make of it](texture-replace.jpg)

- A **cube map** takes a `.dds` cube, or one picture of six square faces side by side (six times as wide as tall) or one above another, in the order +X, −X, +Y, −Y, +Z, −Z, as **Face** lists them.
- A **texture array** takes a `.dds` array, or one picture of its slices side by side or one above another. Square slices are split as they come; otherwise the picture is split into as many slices as the game's array has. A different number of slices is allowed, but a material that picks a slice past the last shows nothing.
- If your project already has another picture for the same texture (a `.png` where you now pick a `.tga`), you're asked first, and the old one is deleted, so Build has only one to choose from.
- The button shows for the game's textures, cube maps and arrays, not for loose pictures or for files already in your project.

> tip: When the window is narrow, the buttons on the right keep only their icons; hover one to see what it does.

## Texture arrays

A `.texarray` (such as a wall's blend layers) opens in a window of its own, like REDkit's texture array editor: every texture in a row, the one you pick shown large, and the array's properties. Double-click a texture to open it in the texture viewer. **Replace with picture…** replaces the whole array, as above. **Save PNGs** writes every texture to a folder; **Save DDS** the whole array as one file.

## Texture caches and pictures

A `texture.cache` opened with **Open files** is listed like a bundle, each texture under its game path, and **Format** says how it's stored (`BC1 2048x1024 ×12` is BC1 colour with 12 mip levels). The same viewer opens PNG, JPEG, BMP, GIF, TIFF and DDS pictures.

> note: Formats it reads: BC1 and BC3 (colour, with and without alpha), BC4 (masks), BC5 and BC7 (normal maps and the Remastered's high quality textures), plain 32-bit colour and the half-float cube maps of environment probes.
