---
title: Scripts log
icon: E7C3
order: 10
summary: Watch the game's script log while it runs, and jump to the script that wrote a line.
keywords: log scriptslog logchannel debug debugscripts follow
windows: LogWindow
---
With debug scripts on, the game writes everything scripts log to `Documents\The Witcher 3\scriptslog.txt`. **Tools > Scripts log** follows it while the game runs.

![The scripts log](scripts-log.jpg)

- New lines appear as they're written. **Follow** keeps the newest in view, **Pause** stops for a moment, and **Clear** hides what's there so far (the file isn't changed).
- Narrow it with words (`!word` leaves lines out), a **channel**, or **Problems** (asserts, errors and warnings).
- Double-click a line to find the script that wrote it.
- **Open log…** watches any other log file.

Log from your own scripts with `LogChannel`:

```ws
LogChannel('MyMod', "Geralt has " + IntToString(thePlayer.GetLevel()) + " levels");
```

> tip: Debug scripts are turned on with the game's `-debugscripts` launch option.
