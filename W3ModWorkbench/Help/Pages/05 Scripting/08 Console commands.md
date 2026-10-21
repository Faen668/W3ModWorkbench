---
title: Console commands
icon: E756
order: 8
summary: Every command the game's debug console runs, from the game and your mods, ready to copy.
keywords: console commands exec function debug additem cheat DBGConsoleOn
windows: ConsoleCommandsWindow
---
The game's debug console runs any `exec function` by name, such as `additem('Swallow 1')`. **Tools > Console commands** lists every one in the game's scripts, your installed mods and your project.

![Console commands, searching for additem](console-commands.jpg)

- Search by name, parameter or mod.
- Each shows exactly what to type, names and strings in quotes, its parameters (and which can be left out), and where it's declared. Double-click to open its script.
- **Copy** puts the command on the clipboard; **Include optional parameters** adds the ones that can be left out.

## Turning the console on

The console needs debug scripts on. Add this under `[General]` in `Documents\The Witcher 3\dx12user.settings` (or `user.settings` for the DirectX 11 version):

```ini
[General]
DBGConsoleOn=true
```

Then press {{~}} in the game to open it.

Your own mod's commands are `exec function`s:

```ws
exec function hello()
{
	theGame.GetGuiManager().ShowNotification("Hello again!");
}
```
