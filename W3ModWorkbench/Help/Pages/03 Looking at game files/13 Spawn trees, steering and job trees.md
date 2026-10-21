---
title: Spawn trees, steering and job trees
icon: E81E
order: 14
summary: What spawns creatures, how an NPC steers, and what it does at its work, each as a tree.
keywords: spawntree w2steer steering w2job job tree action point work animation encounter creature
windows: TreeFileWindow
---
Three kinds of file are trees like a behaviour tree, and open in the same kind of viewer: drawn from the root down, coloured by kind, with a label over each node saying why its parent goes to it. Drag to move, the wheel to zoom, double-click a node to fold its branch, and **Find** ({{Ctrl+F}}) to search. Click a node to see its properties; the button by **Fit** shows the file's own.

## Spawn trees

A **spawn tree** (`.spawntree`) spawns a community's or an encounter's creatures: branches that run all their children, one at random, or only when facts are as they say, down to **spawn entries** (how many of which creature, and where). **Initializers** set up what's spawned under them (where it guards, its AI, its attitude). Under the tree are the **creature definitions**: each one's entity and how many may be spawned.

![The cows of an encounter, as a spawn tree](spawntree-viewer.jpg)

## Steering graphs

A **steering graph** (`.w2steer`) is how an NPC moves towards its goal: **groups** run all their children, a **condition** runs its first child when its test passes and its second when it doesn't, and **tasks** are steering rules (keep away from walls, strafe round the target, keep a distance).

![A large NPC's steering](steering-viewer.jpg)

## Job trees

A **job tree** (`.w2job`) is what an NPC does at a work spot: nodes that run their children in order or at random, with the **actions** they play on arriving, leaving, and leaving in a hurry. Each action is an animation, maybe with an item in hand.

![Making bricks: a job tree with its animation playing](jobtree-viewer.jpg)

Above the properties, a character plays the action you pick, on a man, a woman, a child or a dwarf as the job is for (or the one in **Preview on**). Beside it are all the animations of the job's category: pick another category, narrow the list, and click any animation to play it.
