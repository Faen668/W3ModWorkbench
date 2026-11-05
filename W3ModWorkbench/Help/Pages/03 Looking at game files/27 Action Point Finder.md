---
title: Action Point Finder
icon: E707
order: 27
summary: Every action point the game's worlds place, the spots NPCs work, rest, eat, sleep and shelter from the rain at, on the game's own map, and whether the game can send anyone there.
keywords: action point actionpoint ap aps job tree w2job category categories rain shelter rain_shelter townsfolk npc npcs community timetable work resting sleeping where map tags quest quests quest code open world main side minor contract character characters main_npcs secondary_npcs job jobs usage eating sitting immersion
windows: ActionPointFinderWindow
---
**Tools > Action Point Finder…** shows the action points the game's worlds place on the game's own map. An action point is a spot an NPC goes to and does something at: hammering at an anvil, sitting on a bench, sleeping, or waiting out the rain under an awning. Each one has tags, and a job tree that says which categories of work it offers and which animations play there. The game sends an NPC to a point only when both match what it's looking for.

## Views

The list on the left picks what's shown:

- **Views** say what a game system does with the points. **Rain shelters** shows every point tagged for the rain, coloured by who the rain reaction can send there (men, women, children, dwarves), and in red the points it never picks: tagged for the rain, but with a job tree that doesn't accept the same category.
  **Quest action points** shows every point placed for a quest, coloured by the sort of quest (main, side, minor, contract, treasure hunt, fistfight, gwent, horse race). **Story characters' action points** shows the usual spots of the main and secondary characters, and **Open-world action points** every other one: the communities' everyday spots. With **Installed mods** ticked, **Added by mods** shows the points the installed mods add.
- **Quests** has one view for each quest that places points, by its internal code (`q309`, `sq301`, `mq7010`…), with the name its folders give it (`q309_casablanca`). The views for a quests folder with no code, such as `part_3`, are at the end.
- **Story characters** has one view for each character with spots of their own (`triss`, `zoltan_chivay`, `crach_an_craite`…), main characters first, with the worlds they're in.
- **Jobs, most used first** puts together the job trees that are one job: the same but for who they're for (`stand_m_sweeping_floor` and `stand_w_sweeping_floor`), the DLC folder they're in, or a variant that plays the same animations (`lie_mwdc_sleep_bed_l` and `_r`, `kneel_c_playing_stick_01` and `_02`, a `_dynamic` one). Each is named for what's done (**Sitting: eating fish**, **Standing: sweeping floor**, **(rain)** for a rain shelter's), with who does it and what its animations show: the names of the animations it plays, without whose they are and their start, stop and loop parts. Its points are coloured by what they're there for: the open world, a quest or a story character. The list's order shows which jobs the game leans on and which it uses once or twice.
- **Job trees, most used first** has one view for each job tree on its own, such as `sit_m_eating_fish`, with its folder and categories, coloured the same way.
- **Job tree categories** has one view for each category the game's job trees accept (`work_man`, `resting_woman`, `sleeping_man`…): every point an NPC can be sent to for it, coloured like a job tree's.

The box under the search picks which group the list shows (**Jobs** to begin with, or every view); the built-in views are always at the top, whatever's picked or searched for. Jumping to a quest, a job or a job tree from a point picks its group.

> note: Two trees are put together by their names, or by their animations when the names differ by a number, a side or `_dynamic`, or when a pose's trees play just the same animations under different names (`stand_m_work_with_vine` and `stand_w_work_with_wine`). Two trees with like names but different animations stay two jobs, and their names say what each shows: `sit_m_ground_01` is **Sitting: ground, broken leg**, `sit_m_ground_02` **Sitting: ground, sew sails**. An animal's or a monster's trees are never put together by their animations, which are mostly just "eating".

Search ({{Ctrl+F}}) finds a view by its name, its detail, and for a job tree its categories and animations. Every word typed must match, so `sit eat` finds the sitting and eating jobs, and a quest's code (`mq7010`) or a word of its name (`casablanca`) goes straight to it. A quest with no view places no action points of its own.

**Job trees** under the list saves every job tree's use as a CSV file, the most used first, with the job it's part of: how many points use it in the open world, in quests and for story characters, in how many areas and which worlds, with its categories and animations. That's the place to start when looking for jobs the open world uses too little, or too much.

> note: The game keeps no list of which action points are a quest's. Each quest's layers sit in a folder named for its code, under a world's quests folder or in a community's own folder (`community\barrens\quarry\mq7010\`), so a quest code in the layer's path names the quest. When the path has none, a code in one of the point's tags does (`beauclair_e_streets_q705_restoration_worker`). A layer under a quests folder with no code counts as quest content too. A layer in a `main_npcs` or `secondary_npcs` folder is a story character's, named by the folder or layer after it, whatever quest code it has as well. So is one in Vizima's `npcs_actionpoints` folder (`emhyr_default`, `voorhis_default`), which doesn't say main or secondary, and a community's layer named just for a character the same world has a folder for (`elihal.w2l`, `elihal_scene_02.w2l`, `baron_quartermaster_01.w2l`). Another world's character doesn't count: Novigrad's houses' `butler.w2l` isn't Toussaint's Butler.

## The card

The middle column says what the view is, how many points it has in each world and of each kind, and its busiest areas. A job's view shows what its animations show, who it's for, and each of its job trees with how often it's used here and a button to its own view; a job tree's view shows its categories and animations, and opens it. Pick a point on the map or in the list to see:

- where it is: its world, its area and position;
- for a rain shelter, whom the rain reaction can send there, or why it never picks it;
- the quest or story character it's placed for and where that was read from, or that it's the open world's, with buttons that show all of that quest's or character's points, and every point with the same job or job tree;
- its tags, its job tree's categories, its component's name, and the animations its job tree plays;
- **Open the layer**, **Open the job tree**, and the template when the layer keeps one.

## The map and the list

- **World** picks the world shown, each with how many of the view's points are in it.
- **Installed mods** reads the installed mods' copies of layers, templates and job trees too, as the game does with them installed. A point a mod adds is a diamond on the map, and its card names the mod; one of the game's that a mod moves or gives another job tree stays a dot, and its card says which mod changed it.
- **The map's key** has a tick for each kind of pin, and for the mods' diamonds: untick one to hide those pins, on the map and in the list (**Main quest** and **Men**, say). What's hidden stays hidden from view to view until ticked again, and the count under the list says how many are hidden.
- **Fit** shows all the points again; double-click a point in the list to zoom in on it.
- **Filter the points** keeps only the points whose area, quest, job, layer, tags, job tree, categories or mod have the words typed: `beauclair`, `q705`, `rain_stand_m_hopeless`.
- **Export** saves every point of the view, in every world, as a CSV file: its kind, quest, tags, categories, job tree and position.

> note: A mod's copy of one of the game's layers holds the game's points as well as its own, so the tool reads the game's own copy too: a point with none of the game's within a metre is the mod's, and every point in a layer only a mod has. Mods often nudge the game's points a little, so a point near one of the game's counts as changed, not added.

> note: The points come from the worlds' layers, DLC's included, as the game cooks them. A point a layer keeps by its template (about 2,800 of the game's) is read from the template, where its action point is.

See also [[Spawn trees, steering and job trees]] for opening a job tree, and [[Communities]] for the timetables that send NPCs to their action points.
