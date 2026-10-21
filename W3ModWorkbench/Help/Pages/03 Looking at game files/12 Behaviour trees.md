---
title: Behaviour trees
icon: E8F9
order: 12
summary: An NPC's AI as a tree you can fold, search and read node by node.
keywords: behaviour behavior tree w2behtree ai npc nodes composite decorator condition task
windows: BehTreeWindow
---
A **behaviour tree** (`.w2behtree`) is the AI an NPC runs. From its root down:

- **Composites** pick among their children by priority, or run them in order.
- **Decorators** wrap one child with a delay, a loop or a test.
- **Conditions** let their child run only when a test passes.
- **Script tasks** run WitcherScript; **actions** do something themselves (idle, play an animation, move).
- **Subtrees** run another tree, and **comments** are notes.

Double-click a `.w2behtree` to open it. A tree an entity or another tree uses opens from there too.

![An NPC's tactics as a behaviour tree](behtree-viewer.jpg)

## Reading the tree

The tree is drawn from its root down, each node's children in the order they're tried, coloured by kind (the colours are listed under the tree). Each node says what it is or the task it runs, its main settings, and its priority at the top right.

- Drag to move, the wheel to zoom, double-click the background (or {{F}}) to see it all.
- **Double-click a node** to fold its branch away (it says how many nodes are under it); again to open it. The box at the top folds everything below a level.
- **Find** ({{Ctrl+F}}) marks the nodes whose name, class, task or settings have your words. {{Enter}} goes to the next, {{Shift+Enter}} to the one before.

Click a node to see every property it has on the right, and those of the script task it runs.
