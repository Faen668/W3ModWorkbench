---
title: Writing scripts
icon: E70F
order: 2
summary: Typing help, mistakes as you type, snippets, refactoring, formatting, renaming, and saving.
keywords: edit type autocomplete suggest snippets ctrl+j rename f2 save encoding undo errors squiggle underline problems hints parameter names unused faded refactor extract inline ctrl+. format shift+alt+f wrapmethod
---
## Editing and saving

- Type straight into one of your project's scripts. {{Ctrl+Z}} and {{Ctrl+Y}} undo and redo.
- A dot on the tab, `●` in the title and **not saved** under the name mark changes that aren't saved.
- **Save** ({{Ctrl+S}}) writes the file in the encoding it had, so nothing changes but what you typed. **Save all** on a tab's right-click menu ({{Ctrl+Shift+S}}) saves every changed file.
- **Find in this file** on the right-click menu ({{Ctrl+F}}) opens the search bar over the file, with what's selected to look for. {{F3}} and {{Shift+F3}}, or its arrows, go to the next and previous match.
- Closing with changes asks first: **Save**, **Don't save**, or **Cancel**.

## Names as you type

After a dot, a list shows what the thing before it has: its class's functions, events and variables, its parents' too, and an array's methods. Three letters into a name, it lists the locals, parameters, members, globals, types, enum values and keywords that start the same way.

Keep typing to narrow it, then {{Enter}} or {{Tab}} to take the one picked. {{Esc}} closes the list, and {{Ctrl+Space}} opens it any time. You can turn the list off while typing in [[Settings]].

### Wrapping or replacing a function

On the line under `@wrapMethod(CR4Player)` or `@replaceMethod(CR4Player)`, start typing (or press {{Ctrl+Space}}) for a list of that class's functions and events, with their parameters. Pick one and its whole function is written for you: the exact signature, a `wrappedMethod` call that passes the parameters on and returns what the original returns, and the cursor where your own code goes. A replacement gets the original's body to change.

![Picking a function to wrap under @wrapMethod(CR4Player)](bench-wrap-complete.jpg)

The signature starts at `function`, as the compiler wants after an annotation: the original's `public`, `final` or `latent` aren't repeated, and an event is written as a function that returns a `bool`. The [[Stub generator]] writes them the same way.

## Mistakes as you type

A moment after you stop typing, your project's scripts are checked the way **Compile** checks them, with the changes you haven't saved. What's wrong gets a wavy red line, and resting the mouse on it says why. The count at the bottom right of [[Script Bench]] (**2 problems**) lists them all: click a line to go there.

![Two mistakes underlined, and the list from the status bar](bench-problems.jpg)

The game's scripts and your installed mods are read once, when Script Bench opens, so each check after that takes a moment. The refresh button above the tree reads them again. Turn checking off in [[Settings]] (**Scripts and editors**).

> tip: The list says the same as **Compile** would, in the same words. [[Compiling]] says what the common messages mean.

## Parameter names and unused names

- Small labels before each argument say which parameter it fills: `MaxF(a: total, b: 100.0)`. By default they show while {{Ctrl+Alt}} is held, so the code looks as it is until you want to know. The tag button in Script Bench's toolbar shows them all the time, and [[Settings]] can turn them off.
- A local variable, parameter or private field that's never used is faded, so leftovers stand out. Rest the mouse on one to see why. A parameter the function can't do without isn't faded: an event's, a timer's, or one a wrapped or overriding function has to keep.

![Parameter names in calls, and unused names faded](bench-hints.jpg)

## Refactoring

Select some code, or put the cursor on a variable, a `var` line, a `switch` or an `if`, and press {{Ctrl+.}} (or **Refactor…** on the right-click menu) for the changes that fit there:

| Change | Does |
|---|---|
| **Extract to a variable** | puts the selected expression in a new local, declared with the others at the top of the function |
| **Extract to a method** | moves the selected statements into a new private function of the class, after the one they were in |
| **Extract to a function** | moves them into a new global function after the class, with the variables they use as parameters (`out` when they change one that's read later) |
| **Inline the variable** | replaces a local set once with its value everywhere it's read |
| **Split the declaration** / **Join the declaration** | `var x : int = 5;` into a declaration and an assignment, and back |
| **Put each case on one line** / **each statement on its own line** | lays a `switch` out compactly, `case 1: x = 1; break;`, or one statement a line |
| **Put each branch on one line** / **each branch's statement on its own line** | the same for an `if` and its `else` |

![The refactors for a selected line](bench-refactor.jpg)

A new name is picked for you and ready to type over, as with {{F2}}. {{Ctrl+Z}} undoes the whole change in one go. A change that would break the code isn't offered: WitcherScript wants every `var` before a function's first statement, so a new variable goes there.

## Formatting

**Format document** ({{Shift+Alt+F}}, or on the right-click menu) lays a script out the same way all through: indenting, spaces around `:` and operators, and where braces go. It only changes spaces and line breaks, never the code: if it ever would, it changes nothing and says where. How it lays things out is set in [[Settings]] (tabs or spaces, `x : int` or `x: int`, braces on their own line or not, where annotations go), with a sample that shows the result. The defaults are the game's own style. Ticking **Format a script of your project each time it's saved** formats each one as you save it.

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
