---
title: Environments
icon: E706
order: 20
summary: How a world looks through the day: every lighting, fog, sky and colour setting, each a curve over the 24 hours.
keywords: env environment definition environments lighting light sun sky fog bloom tone mapping color balance day night time of day curve weather area environment
windows: EnvironmentWindow
---
An **environment** (`.env`) is how a world looks through the day: the sun's colour, the sky, the fog, bloom, tone mapping, colour balance, water, wind and more. Most settings are curves over the 24 hours, so dawn, noon and night each look their own. A world names one, and area environments in its layers blend others in where the player is. Double-click one to open the environment viewer, like REDkit's Environment System panel but easier to read.

![The sun's colour through a sunset environment's day](environment-viewer.jpg)

## Time of day

The bar at the top picks the time everything is read at, as REDkit's fake day cycle does. Drag it, or click or drag on a chart. **Play** ({{Space}}) runs through the day. Beside it, the sun, sky and fog colours at that time.

## Groups and settings

On the left are the groups of settings (**Global light**, **Sky**, **Global fog**, **Tone mapping**…). A green dot means the group is turned on, a red one that it's off. The numbers say how many of its settings the file sets.

- Type in **Find** ({{Ctrl+F}}) to show only the groups and settings with those letters, such as `sunColor` or `fog`.
- **Only what the file sets** is ticked to start with. Untick it to list every setting the engine has, those the file leaves at their defaults dimmed.

Pick a group to list its settings with their values at the time picked: a number, or a colour with its swatch and scale.

## Curves

Pick a setting to draw it over the day. A colour curve shows a band of its colours through the day, with its red, green and blue under it. Hover to read the values at any time.

Right-click a setting to copy its value now, its keys (time and value, one a line) or where it is (`m_globalLight › sunColor`).

> note: REDkit's weather and environment presets, and its editor switches, are settings of the editor while it runs, not part of the file, so they aren't here.
