---
title: Action Point Finder
icon: E707
order: 27
summary: Every action point the game's worlds place, the spots NPCs work, rest, eat, sleep and shelter from the rain at, on the game's own map, and whether the game can send anyone there.
keywords: action point actionpoint ap aps job tree w2job category categories rain shelter rain_shelter townsfolk npc npcs community timetable work resting sleeping where map tags
windows: ActionPointFinderWindow
---
**Tools > Action Point Finder…** shows the action points the game's worlds place on the game's own map. An action point is a spot an NPC goes to and does something at: hammering at an anvil, sitting on a bench, sleeping, or waiting out the rain under an awning. Each one has tags, and a job tree that says which categories of work it offers and which animations play there. The game sends an NPC to a point only when both match what it's looking for.

## Views

The list on the left picks what's shown:

- **Views** say what a game system does with the points. **Rain shelters** shows every point tagged for the rain, coloured by who the rain reaction can send there (men, women, children, dwarves), and in red the points it never picks: tagged for the rain, but with a job tree that doesn't accept the same category.
- **Job tree categories** has one view for each category the game's job trees accept (`work_man`, `resting_woman`, `sleeping_man`…): every point an NPC can be sent to for it.

Search ({{Ctrl+F}}) finds a view by its name.

## The card

The middle column says what the view is, how many points it has in each world and of each kind. Pick a point on the map or in the list to see:

- where it is: its world, its area and position;
- for a rain shelter, whom the rain reaction can send there, or why it never picks it;
- its tags, its job tree's categories, its component's name, and the animations its job tree plays;
- **Open the layer**, **Open the job tree**, and the template when the layer keeps one.

## The map and the list

- **World** picks the world shown, each with how many of the view's points are in it.
- **Installed mods** reads the installed mods' copies of layers, templates and job trees too, as the game does with them installed.
- **Fit** shows all the points again; double-click a point in the list to zoom in on it.
- **Filter the points** keeps only the points whose area, layer, tags, job tree or categories have the words typed: `beauclair`, `rain_stand_m_hopeless`.
- **Export** saves every point of the view, in every world, as a CSV file: its kind, tags, categories, job tree and position.

> note: The points come from the worlds' layers, DLC's included, as the game cooks them. A point a layer keeps by its template (about 2,800 of the game's) is read from the template, where its action point is.

See also [[Spawn trees, steering and job trees]] for opening a job tree, and [[Communities]] for the timetables that send NPCs to their action points.
