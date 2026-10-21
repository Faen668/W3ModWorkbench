---
title: Your first mod
icon: E8FB
order: 1
summary: A step by step walk through: make a project, write a script, check it, build the mod and try it in the game.
keywords: tutorial walkthrough beginner first mod hello start step by step
---
This walk through makes a small mod that greets you when Geralt appears in the world. It takes about ten minutes and uses the parts of the app you'll use most. You don't need REDkit.

## 1. Make a project

A **project** is a folder of your own where your mod lives.

1. Open the **Project** menu in the toolbar and choose **New project…** ({{Ctrl+Shift+N}}).
2. Type a name, say `My First Mod`. The **mod name** (`modMyFirstMod`) fills itself in: it's the folder name the game loads the mod from, and it must start with `mod`.
3. Leave the folder as it is (`Documents\W3 Mod Workbench\My First Mod`) or pick another, then press **Create project**.

The project opens at the top of the tree, with a **PROJECT** badge.

![A new project in the main window](main-project.jpg)

## 2. Write a script

1. Open **Project > Script Bench** ({{Ctrl+Shift+E}}).
2. In the tree on the left, right-click your project and choose **New script…**. Call it `my_first_mod`.
3. Type this, or copy it with the button:

```ws title="my_first_mod.ws"
// My first mod: says hello when Geralt appears in the world.
@wrapMethod(W3PlayerWitcher)
function OnSpawned(spawnData : SEntitySpawnData)
{
	wrappedMethod(spawnData);
	theGame.GetGuiManager().ShowNotification("Hello from my first mod!");
}

// Type hello in the debug console to see it again.
exec function hello()
{
	theGame.GetGuiManager().ShowNotification("Hello again!");
}
```

4. Press {{Ctrl+S}} to save.

What it does: `@wrapMethod(W3PlayerWitcher)` wraps the game's own `OnSpawned`, which runs when Geralt appears in the world. `wrappedMethod(spawnData)` runs the game's version first, then the next line shows a notification. [[WitcherScript annotations]] explains wrapping and the other ways a mod changes the game.

![The script in Script Bench](bench-overview.jpg)

## 3. Check it

Press **Compile** in the bar at the bottom ({{F7}}). After a second it says **Compiled with no warnings**. If you made a typo, the mistake is listed under the script with its line: click it to go there, fix it, and compile again.

![Compiled with no warnings](bench-compile.jpg)

## 4. Build and install

1. Back in the main window, choose **Project > Build modMyFirstMod** ({{Ctrl+Shift+B}}). The app compiles the scripts into `content\precompiled.rsblob` and puts the finished mod in your project's `build` folder.
2. Choose **Project > Install modMyFirstMod…**. It copies the mod into the game's `mods` folder, after saying where.

## 5. Try it in the game

Start the game and load a save. As Geralt appears, the notification shows: **Hello from my first mod!**

> tip: To try the `hello` command, turn on the game's debug console: add `DBGConsoleOn=true` under `[General]` in `Documents\The Witcher 3\dx12user.settings` (or `user.settings`), press {{~}} in the game, and type `hello`. See [[Console commands]].

## Where next

- Change a game file, such as a texture or an item's stats: [[Changing game files]].
- Learn the four ways a mod changes the game's scripts: [[WitcherScript annotations]].
- See how your mods get on together: [[Load order analyser]].
