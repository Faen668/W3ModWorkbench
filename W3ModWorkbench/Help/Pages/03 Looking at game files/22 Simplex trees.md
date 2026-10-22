---
title: Simplex trees
icon: E909
order: 22
summary: A world's map cut into areas by straight lines: where the wind is damped, where water can be seen from.
keywords: w3simplex simplex tree map area areas wind damper dampers water visibility occlusion world split splits bsp
windows: SimplexTreeWindow
---
A **simplex tree** (`.w3simplex`) is a map of a world cut by straight lines into areas, for something the world looks up by place. The game uses them for **wind dampers** (inside an area the wind is damped, so trees and cloth sway less), **water visibility** (from inside an area the sea can be seen, so it's drawn) and occlusion settings. Double-click one to open the simplex tree viewer, like REDkit's SimplexTree Editor.

![The wind dampers of White Orchard](simplex-tree-viewer.jpg)

## The map

The world from above, north up, with a grid in metres. Each area is filled in its own colour and its pieces are numbered. When the world the tree belongs to is found (in its folder or above), its ground is drawn as a dashed square.

- Drag to move around, use the wheel to zoom. **Fit** ({{F}}) frames the areas; **World** ({{W}}) frames the whole world.
- Hover to see where you are and which area that is.
- Click a piece to see its area and every line on the way down to it, with the side taken.
- **Every line** also draws the lines that cut pieces belonging to no area. **Numbers** writes each area's number on its pieces.

## Areas and splits

- **Areas**: each area's colour, how many pieces it's in, its size and middle. Pick one to frame it. Right-click to copy its middle, or the outline of every piece.
- **Splits**: the tree itself. Each split is a line across the map: a point is **in front** of it when its normal · point + offset is 0 or more. Each side goes on to another split, or ends in an area or in none. Pick a split to draw it; its arrow points to the front.
- **Properties**: the file's own properties.
