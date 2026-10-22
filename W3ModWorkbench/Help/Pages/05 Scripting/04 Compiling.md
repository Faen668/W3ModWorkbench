---
title: Compiling
icon: E9F5
order: 4
summary: Check your scripts for mistakes, with REDkit's compiler or the app's own, and read the errors.
keywords: compile compiler wcc_lite built-in errors warnings rsblob f7 check redkit loose scripts folder without project
windows: CompileWindow
---
**Compiling** turns your scripts into the form the game runs, and finds your mistakes on the way.

## Checking your scripts

Press **Compile** in [[Script Bench]]'s bottom bar ({{F7}}, or **Project > Compile scripts**). Your changed scripts are saved first (it asks), then compiled. Any warnings and errors are listed under the files, grouped by file: click one to go to its line.

![Compiling a project: no warnings](bench-compile.jpg)

You don't have to wait for **Compile** to find most mistakes: Script Bench checks your scripts the same way as you type, and underlines what's wrong (see [[Writing scripts]]).

When they compile, the scripts go in your built mod as `content\precompiled.rsblob` (in the project's `mod…` folder), the same place **Build** puts them (see [[Building and installing]]). When they don't, the one that's there is left as it was.

## Scripts without a project

A folder of loose scripts doesn't need a project to be compiled. Open it in the main window (**Open folder**, or drop it on the window), right-click it in the tree and choose **Compile these scripts**. A small **Compile scripts** window lists the warnings and errors the same way Script Bench does: click one to open its line. **Compile again** ({{F7}}) runs it again once you've fixed them, and **Show log** opens everything the compiler wrote.

![Compiling a folder of loose scripts: three errors and a warning](compile-folder.jpg)

The compiled scripts are saved beside the folder: `scripts` on your Desktop makes `precompiled.rsblob` on your Desktop, and a mod's `content\scripts` makes `content\precompiled.rsblob`, where the game reads it. A `precompiled.rsblob` that's there already is replaced, but only when the scripts compile. Scripts in the game's own folders, or in an installed mod, can't be compiled in place: [[Projects|make a project from them]] instead.

## Two compilers

| Compiler | When it's used | Speed |
|---|---|---|
| **REDkit's `wcc_lite.exe`** | when it's set in [[Settings]] | about a minute: it compiles the whole game first |
| **The built-in compiler** | without REDkit, or when **Always use the built-in compiler** is ticked | about a second |

The built-in compiler does what REDkit's does: the same steps in the same order, the same checks and the same error messages, and it writes the same compiled scripts. It compiles yours against the game's scripts and every mod you have installed (so a mod built on Shared Utils finds it), leaving out your own mod's installed copy.

## Reading an error

```text
local\my_first_mod.ws (6)   No implicit cast exists for converting 'String' to 'Int32'
```

The file and line, then what's wrong, in the same words REDkit uses. Some common ones:

| Message | Usually means |
|---|---|
| **I dont know any 'x'** | a misspelt name, or a variable you didn't declare |
| **Could not find function 'x'** | a misspelt function, or one the class hasn't got |
| **'x' is not a member of '…'** | the class has no variable or function of that name |
| **No implicit cast exists for converting 'A' to 'B'** | you're putting one type into another: write a cast, such as `(int)value` |
| **Function 'x' can't return a value** / **should return a value** | a `return` with or without a value where the function says otherwise |
| **@wrapMethod cannot wrap 'Class::x' because it is not defined** | the class has no such function in this game version |
| **@wrapMethod Wrapper 'Class::x' must contain a 'wrappedMethod' token** | a wrapper must call `wrappedMethod` |
| **Unknown type 'X' for property 'y'** | a misspelt type, or one from a mod you haven't installed: on a variable, a parameter, a local or what a function returns (`__return`) |
| **Unable to parse value** | a `default` that can't be read as its variable's type: a name the enum hasn't got, a word for a number, or text for a bool that isn't true or false |
| **Property 'x' does not exist in 'Class'** | a `default` for a variable the class hasn't got |

REDkit's warnings don't stop the compile, and the built-in compiler gives the same ones:

| Warning | Means |
|---|---|
| **Class X is not marked as abstract, but has missing functions!** | a class it extends declares functions without a body (`function F();`) that yours doesn't write. Each is listed under it. |
| **State machine 'X' has no autostate** | a `statemachine class` that doesn't say which state it starts in, with `default autoState = 'Idle';` |
| **Entry function 'x' should be called only from events or other entry functions** | an `entry function` called from an ordinary function |

The built-in compiler adds one REDkit doesn't give: a bool `default` such as `falsesss`. REDkit takes it, as it reads only how the value starts, but it's likely a typo.

> note: WitcherScript has no `++` or `--`: write `i += 1`. And a `@replaceMethod` or `@wrapMethod` must be followed straight by `function`, without `public` or `private`.

**Show log** opens everything the compiler wrote.
