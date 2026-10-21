---
title: The mod menu builder
icon: E700
order: 4
summary: Build your mod's in-game options menu with forms, with its strings and the script that reads it.
keywords: mod menu options settings user_config_matrix xml builder presets toggle slider
---
A mod's options in the game's **Mods** menu come from an XML file in `bin\config\r4game\user_config_matrix\pc`. The **menu builder** edits one with forms and writes the XML for you.

**Project > New mod menu** starts one from a small example; in the XML editor, **Menu builder** ({{Ctrl+B}}) opens it on any menu file.

![The menu builder](menu-builder.jpg)

## Building the menu

- **Pages** are the groups of the menu; **Settings** are what's on a page: a **toggle**, a **slider** (with its min, max and steps), a **list** of options, or a **separator**.
- **Details** edits what you pick. A setting's text key follows its id, as most mods name them.
- **Presets** are named sets of values. The first gives the settings their starting values.

## Strings

**Strings** lists every key the menu needs text for, named as the game expects (`panel_`, `option_`, `preset_`…). Type the texts, then **Add to CSV** writes them with new IDs into a strings `.csv`, ready for the [[Editing strings|strings editor]]. **Find a free block** picks IDs nothing else uses.

## Reading the settings in your script

**Script** writes the WitcherScript that reads every setting into a variable of the right type:

```ws
var config : CInGameConfigWrapper;
var enabled : bool;
config = theGame.GetInGameConfigWrapper();
enabled = config.GetVarValue('MyModGroup', 'MyModEnabled');
```

## Problems

**Problems** lists mistakes: ids used twice, a default outside a slider's range, a preset setting something that isn't on the page. Double-click one to go to it.

> note: The builder writes the XML itself, so it drops what it can't keep, such as comments. It says so before it does, and the code view can undo it.
