---
title: Settings
icon: E713
order: 4
summary: Where your work goes, the look of the app, where the game and its tools are, the app's and the game's language, the code editors and the 3D views.
keywords: options preferences theme dark light accent game folder documents proton wine wcc_lite compiler merger language translation localisation font size tab width texture detail redkit privacy online easyassetbrowser projects folder
windows: SettingsWindow
---
Open **Settings** from the toolbar, or press {{Ctrl+,}}. Pick a section on the left. Changes to the look show straight away, so you can try them; **Cancel** puts everything back, and **Save** keeps it.

![Settings](settings.jpg)

Settings has the choices the app would otherwise make for you. What you switch for the moment, such as the thumbnail size or a viewer's floor grid, stays in that window.

## General

- **Where new projects go**: the folder **New project** starts in. Empty means `Documents\W3 Mod Workbench`. You can still put any project anywhere.
- **Open the last project again when the app starts**.
- **Clear the recent lists**: forgets the recent files, projects and Asset Browser files at once.
- **Extracting**: the folder files are extracted to unless you pick another, whether to keep the game's folders under it, and whether to open it when done. The extract bar under the file list has these too.
- **Ask which copy to open**: when a mod or a DLC has its own copy of a game file, the [[The Asset Browser|Asset Browser]] asks which one to open. Off, it opens the copy the game loads. See [[Which copy of a file wins]].

## Appearance

- **Mode**: **Light**, **Dark**, or **Windows setting** to follow Windows.
- **Theme**: the accent and background colours. **Windows** uses your Windows accent colour and see-through window background. The others are named after the witcher schools (Wolf, Cat, Griffin, Bear, Viper, Manticore) plus a plain Graphite.

The sample at the top shows a bit of the app in the theme you're trying.

## Game and tools

Each of these is found by itself when it can be. A tick under a box says what was found.

- **Game folder**: where The Witcher 3 is installed, the folder with `bin` and `content` in it. Empty, the app uses the one it finds in your Steam or GOG libraries. Decompiled scripts use it to show names instead of numbers:

```ws
// without the game folder
if (type == 1)
// with it
if (type == EACIT_Potion)
```

- **The game's own settings folder**: where the game keeps `input.settings` (key bindings), `mods.settings` (mod priorities) and `scriptslog.txt`. On Windows it's `Documents\The Witcher 3`. Set it when your game writes them somewhere else, such as under Proton or Wine. The [[Key binding helper]], the [[Load order analyser]] and the [[Scripts log]] read from here.
- **REDkit's wcc_lite**: REDkit's own compiler, in its `bin\x64_RedKit` folder. REDkit's source files, which fill in what cooking leaves out of the game's files, are found from it too.
- **Script Merger**: **Script Merger Remastered** by relapse12, a separate download. With it set, the [[Load order analyser]] has an **Open Script Merger** button. This app only starts it.

> note: REDkit is free on Steam and GOG for owners of the game. Nothing in this app needs it, but the viewers show more with it. See [[Properties]].

## Language

Two languages, set apart: the app's own, and the game's.

**The app's language** is the language of the app's windows, menus, messages and this guide. **Windows setting** uses the language Windows shows when the app has it, else English; under the list it says which that is. Most text changes as soon as you **Save**. Text a window has already filled in changes when the window is opened again, or the app restarts. A guide page not translated yet shows in English.

> tip: Want the app in your language? LOCALISATION.md, with the app's source on GitHub, says how to translate it.

**The game's language** is the language of the game's text and voices the viewers start in. Each viewer's own language list can still change it while you look.

| Where | What follows the language |
|---|---|
| [[Scenes]] | the lines and subtitles, and the voices (English where a language has none) |
| [[Cutscenes]] | the lines; the voices stay English, as the animation and lip sync are timed to them |
| [[Searching the game's strings]] | the language it starts in |
| [[Editing strings]] | a file that doesn't say its language |

Under the list, it says whether your game has that language's text and voices installed.

## Scripts and editors

- **Compiling**: wcc_lite when it's set, else the built-in compiler; or always the built-in one, which takes a second instead of a minute. Both write the same `precompiled.rsblob`. Use wcc_lite for a release. See [[Compiling]].
- **Opening files**: tabs in one window, or a window for each file, for files that open in the preview window. Scripts always open in [[Script Bench]].
- **Split view**: whether a split ({{Ctrl+\}}) puts the second group of tabs beside the first or below it.
- **Font size** and **tab width** of the code editors (scripts, XML and stubs), with a sample. The game's scripts are indented with tabs, so the tab width is how far in their code sits. JSON keeps its two spaces.
- **Show a name's declaration when the mouse rests on it**, and **Suggest names while typing**. See [[Finding your way around scripts]].
- **As you type**: whether your project's scripts are checked as you type, with what's wrong underlined; when the names of parameters show in calls (**Off**, **While Ctrl+Alt is held**, **Always**); and whether names that are never used are faded. See [[Writing scripts]].
- **Formatting**: how **Format document** ({{Shift+Alt+F}}) lays a script out: tabs or spaces, `x : int` or `x: int`, braces on their own line or at the end of the line, where annotations go, and spaces inside brackets. **Keep the spaces that line declarations up with each other, as the game's scripts do** leaves lined-up code as it is; **Line up the colons of a class's fields declared one after another** does the lining up itself. A sample under them shows the result as you choose. Tick **Format a script of your project each time it's saved** to format on every save.

![The As you type and Formatting cards](settings-scripts-typing.jpg)

## Viewers

- **Read properties from REDkit's source files**: cooking leaves out what the game doesn't need to run, which REDkit's copies keep. Turn it off to see exactly what the game reads. A mod's or your project's files always show their own. See [[Properties]].
- **Texture detail in the 3D views**: the largest a texture is read for entities, scenes, cutscenes, rigs and animations. **Low** loads quickest and uses least memory, **Full** is as sharp as the game. **Medium** suits most; a scene with many actors at **Full** uses a lot of memory.
- **Smoothing edges in the 3D views**: how smooth meshes' edges are drawn. **4×** is smoothest but slowest; a mesh filling a big window can draw at half the frame rate with it. **2×** suits most.
- **Simulate cloth as the game does**: capes, skirts and coat tails swing and hang in the entity, scene, cutscene and animation views. Turn it off for a quicker view with a big cast; cloth then moves stiffly with the bones. See [[Cloth]].
- **Exporting meshes as glTF**: whether the mesh viewer's **Export** puts only **the LOD shown** in a `.glb` or **every LOD**, and whether the textures go inside it. See [[Meshes]].

## Privacy and online

The app works offline and collects nothing about you. The one thing that can go online is **EasyAssetBrowser tags**: community categories and tags for the game's files in the Asset Browser, downloaded from GitHub. They're off until you turn them on, which asks first and says what it means. Turn them off here, and choose whether to delete what was downloaded. See [[EasyAssetBrowser]].

## Where settings are kept

Everything is saved in `%AppData%\W3ModWorkbench\settings.json`, on your computer only. Delete that file to put every setting back to how it started.
