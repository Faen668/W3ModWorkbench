---
title: Settings
icon: E713
order: 4
summary: The look of the app, where the game and REDkit are, and how files open.
keywords: options preferences theme dark light accent game folder wcc_lite compiler merger
windows: SettingsWindow
---
Open **Settings** from the toolbar, or press {{Ctrl+,}}. Changes to the look show straight away, so you can try them; **Cancel** puts everything back, and **Save** keeps it.

![Settings](settings.jpg)

## Appearance

- **Mode**: **Light**, **Dark**, or **Windows setting** to follow Windows.
- **Theme**: the accent and background colours. **Windows** uses your Windows accent colour and see-through window background. The others are named after the witcher schools (Wolf, Cat, Griffin, Bear, Viper, Manticore) plus a plain Graphite, each in light and dark.

The sample at the top shows a bit of the app in the theme you're trying.

## Extraction

The folder files are extracted to unless you pick another, and whether to keep their folders and open the folder when it's done. They're the same as the bar along the bottom of the main window, so changing one changes the other. **Open the last project again when the tool starts** is here too.

## Preview

- **Open files in**: tabs in one window, or a separate window for each file. Go to definition follows the same choice.
- **Split view**: whether a split puts the second group of tabs beside the first or below it.
- **Show a name's declaration when the mouse rests on it**: the popup in scripts. See [[Finding your way around scripts]].
- **Suggest names while typing**: the list of names in scripts. {{Ctrl+Space}} shows it either way.

## Game folder

Where The Witcher 3 is installed: the folder with `bin` and `content` in it. **Find automatically** looks through your Steam and GOG libraries. A tick under the box means it found the game's scripts.

The app finds the game by itself most of the time. Setting it here helps when you open files from somewhere else, such as a mod you downloaded: decompiled scripts then show names instead of numbers.

```ws
// without the game folder
if (type == 1)
// with it
if (type == EACIT_Potion)
```

The built-in script compiler also reads the game's scripts from here. See [[Compiling]].

## Script compiler

REDkit's `wcc_lite.exe`, the compiler REDkit uses. It's in REDkit's `bin\x64_RedKit` folder; **Find automatically** looks for it. When it's set, compiling uses it. Leave it empty and the app's built-in compiler is used instead, which needs only the game folder above.

**Always use the built-in compiler** uses the built-in one even when wcc_lite is set. It's much quicker, which is handy while you try changes.

> note: REDkit is free on Steam and GOG for owners of the game. Nothing in this app needs it, but some things are better with it: REDkit keeps the game's files from before they were cooked, so the viewers can show more of their properties. See [[Properties]].

## Script merger

The path to **Script Merger Remastered** by relapse12, a separate download from Nexus Mods that merges mods which change the same scripts. With it set, the [[Load order analyser]] has an **Open Script Merger** button. This app only starts it.

## Where settings are kept

Everything is saved in `%AppData%\W3ModWorkbench\settings.json`. Delete that file to put every setting back to how it started.
