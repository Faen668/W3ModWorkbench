---
title: Rigs
icon: E77B
order: 7
summary: A skeleton's bones in 3D and as a tree, with where each one sits.
keywords: rig w2rig skeleton bones bone names axes hierarchy
windows: RigWindow
---
A **rig** (`.w2rig`) is a skeleton: bones, each hanging from a parent, in the pose that meshes are bound to and animations move. Double-click one to open the rig viewer, laid out like REDkit's skeleton editor.

![A man's skeleton in the rig viewer](rig-viewer.jpg)

## The view

- A line runs from each bone to its parent. Helper bones (IK targets, the trajectory) are drawn faintly.
- **Bone names** labels each bone; **Bone axes** draws each bone's axes (X red, Y green, Z blue).
- **On a character** shows the skeleton inside a character made on it, to see where the bones sit in the body.
- **Fit** ({{F}}) frames the whole skeleton.

## The bones

**Bones** is a tree from the root, each bone numbered as the rig numbers it. Type letters ({{Ctrl+F}}) to find one. Click a bone to pick it out in the view and see where it is: its position, rotation and scale from its parent, and in the model.

**Tracks** lists the extra values animations can drive beside the bones. **Properties** has the rig's own properties.

Right-click a bone to copy its name, its chain from the root (`Root > pelvis > torso > …`) or every bone with its parent, or to open or close every branch.
