---
title: Writing scripts
icon: E70F
order: 2
summary: Typing help, snippets, your own snippets, renaming, and saving.
keywords: edit type autocomplete suggest snippets ctrl+j rename f2 save encoding undo
---
## Editing and saving

- Type straight into one of your project's scripts. {{Ctrl+Z}} and {{Ctrl+Y}} undo and redo.
- A dot on the tab, `●` in the title and **not saved** under the name mark changes that aren't saved.
- **Save** ({{Ctrl+S}}) writes the file in the encoding it had, so nothing changes but what you typed. {{Ctrl+Shift+S}} saves every changed file.
- Closing with changes asks first: **Save**, **Don't save**, or **Cancel**.

## Names as you type

After a dot, a list shows what the thing before it has: its class's functions, events and variables, its parents' too, and an array's methods. Three letters into a name, it lists the locals, parameters, members, globals, types, enum values and keywords that start the same way.

Keep typing to narrow it, then {{Enter}} or {{Tab}} to take the one picked. {{Esc}} closes the list, and {{Ctrl+Space}} opens it any time. You can turn the list off while typing in [[Settings]].

## Snippets

{{Ctrl+J}} (or **Insert snippet** on the right-click menu) inserts common WitcherScript: `@wrapMethod`, `@replaceMethod`, `@addMethod`, `@addField`, code that runs when the player spawns, a class, a statemachine with a state, a struct, an enum, a console command, a timer, a loop, listening for a key, reading a menu setting, a log line, a notification, a fact check and giving the player an item.

Typing a snippet's name on a line of its own and pressing {{Tab}} inserts it too: `wrap`, `onspawned`, `timer`, `listen`… The parts to fill in are picked out: type over the first, {{Tab}} to the next.

### Your own snippets

Save a `.ws` file in `%AppData%\W3ModWorkbench\snippets` and it's a snippet named after the file. A first line starting with `//` describes it. Write `${name}` where something should be typed and `$0` where the cursor goes at the end:

```ws title="notify.ws"
// Shows a notification
theGame.GetGuiManager().ShowNotification("${message}");
$0
```

## Renaming

{{F2}} (or **Rename** on the right-click menu) gives a function, event, variable, type or enum value a new name everywhere your project uses it. Another class's member with the same name is left alone. Only names your project declares can be renamed: renaming the game's would break it.

Open files keep the change unsaved for you to look over; files that aren't open are saved. The list under the files shows every place that changed.
