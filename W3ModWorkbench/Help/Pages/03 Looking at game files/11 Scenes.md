---
title: Scenes
icon: E8BD
order: 11
summary: Read a conversation as a screenplay, follow its choices, hear its voices and watch it play in 3D.
keywords: scene w2scene dialogue conversation choices screenplay graph voice read through preview timeline lip sync camera
windows: SceneWindow
---
A **scene** (`.w2scene`) is the flow of a conversation, or of the cutscenes a quest plays: from a **Start**, through **sections** that play lines, choices, cutscenes and videos, and through branches (a **Condition** on facts, a **Switch**, a **Random** pick, a **Script**), to an **End**.

Double-click a scene to open the scene viewer, laid out like REDkit's scene editor: the scene's tabs above, the flow below, and what you pick at the side.

![The Bloody Baron's final talk, as a screenplay](scene-viewer.jpg)

## The screenplay

**Screenplay** shows every part as a card, in the order the flow reaches it:

- a section's lines, with who says them and to whom
- a choice's options, numbered, with what each needs or does (pay, leave, only if a fact is set) and where it leads. The options the game picks out are outlined.
- a branch's condition and where each way goes

Click any **→** link to go to that part, and **Back** ({{Alt+Left}}) to return. **Search** ({{Ctrl+F}}) keeps the cards whose lines or speakers match, and **Language** shows the lines in another language.

## Hearing it

The **▷** beside a line plays its voice. **Read through** ({{R}}) plays the scene from the part you pick, as the game would: each section's lines at their times, following the flow from part to part. At a choice it waits a moment and picks one at random (click one meanwhile to go that way instead). **Stop** stops it.

## Watching it

**Preview** plays the section you pick in 3D, in time with its **Timeline**. Press **Load preview** first.

![The same scene playing in the preview](scene-preview.jpg)

The actors are put together from their entities and stand where the scene puts them, each in their idle, with what the section does played at its times: their animations and walks, their faces and lip sync, who they look at, the props they hold, and the lights the scene sets up. **Through the camera** sees it through the scene's cameras, cut as the scene cuts, with the line being said as a subtitle.

**Timeline**, under the screenplay, shows the section as REDkit's timeline does: its lines along the top, coloured by speaker, then its camera cuts, lights, fades, and each actor's animations and expressions. Click an event for its properties; **Play** runs the section from where the line is.

## The rest of the scene

- **Graph** draws the flow as boxes and curves; for the game's own scenes with REDkit installed, laid out as the writers laid it out.
- **Starting Conditions**: who each start needs.
- **Definitions**: the actors and props, with the entity each is made from.
- **Dialog sets**: where the actors stand.
- **Cameras**, **Animations**, **Files** and **Properties**.
