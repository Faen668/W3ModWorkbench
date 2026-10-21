---
title: Compiling
icon: E9F5
order: 4
summary: Check your scripts for mistakes, with REDkit's compiler or the app's own, and read the errors.
keywords: compile compiler wcc_lite built-in errors warnings rsblob f7 check redkit
---
**Compiling** turns your scripts into the form the game runs, and finds your mistakes on the way.

## Checking your scripts

Press **Compile** in [[Script Bench]]'s bottom bar ({{F7}}, or **Project > Compile scripts**). Your changed scripts are saved first (it asks), then compiled. Any warnings and errors are listed under the files, grouped by file: click one to go to its line.

![Compiling a project: no warnings](bench-compile.jpg)

Compile only checks: nothing is kept. **Build** is what puts the compiled scripts in your mod (see [[Building and installing]]). **Project > Compile scripts to…** writes a `.rsblob` wherever you choose, to try it.

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

> note: WitcherScript has no `++` or `--`: write `i += 1`. And a `@replaceMethod` or `@wrapMethod` must be followed straight by `function`, without `public` or `private`.

**Show log** opens everything the compiler wrote.
