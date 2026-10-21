---
title: WitcherScript annotations
icon: E943
order: 5
summary: The four ways a mod changes the game's scripts without replacing whole files, with examples.
keywords: annotations wrapmethod replacemethod addmethod addfield wrappedmethod enum extension compatibility
---
A mod can change the game's scripts without copying whole files, which makes it far less likely to clash with other mods. You write an **annotation** above a function or variable, saying which of the game's classes it's for.

## @wrapMethod: add to a function

Your code runs around the game's own. Call `wrappedMethod` to run the original, with the same arguments.

```ws
@wrapMethod(W3PlayerWitcher)
function GetLevel() : int
{
	var level : int;
	level = wrappedMethod();   // the game's own GetLevel
	return level + 1;          // one level higher than the game thinks
}
```

Several mods can wrap the same function: each wraps the one before, so they all run. That makes `@wrapMethod` the friendliest choice.

## @replaceMethod: replace a function

Your function runs instead of the game's. Only one mod can replace a function, so use it when wrapping can't do what you need.

```ws
@replaceMethod(W3PlayerWitcher)
function GetLevel() : int
{
	return 50;
}

// a global function (one that isn't in a class) leaves the class out
@replaceMethod
function AreaIsCold() : bool
{
	return true;
}
```

## @addMethod: add a new function to a class

```ws
@addMethod(W3PlayerWitcher)
public function MyModGreet()
{
	theGame.GetGuiManager().ShowNotification("Hello!");
}
```

Anywhere else, `thePlayer.MyModGreet()` (or `GetWitcherPlayer().MyModGreet()`) calls it.

## @addField: add a variable to a class

```ws
@addField(W3PlayerWitcher)
var myModCounter : int;
```

## Adding to an enum

Declare one of the game's enums again with your new values. They're numbered on from the game's own, so mods adding to the same enum don't clash. Don't give them numbers yourself.

```ws
enum ECompareOp
{
	CO_MyModAlways
}
```

## Rules

- Write the annotation, then `function` straight after: `@wrapMethod(Class) function …`. `public` or `private` can't go in between for a wrap or a replace (they can for `@addMethod` and `@addField`).
- Use the function's exact name and parameters. The [[Stub generator]] writes them out for you.
- A wrapper of an `event` is still written with `function`.

> tip: Not sure whether another mod already wraps or replaces the same function? **Who else changes this** ({{Ctrl+Shift+F12}}) in Script Bench lists every mod that does, and the [[Load order analyser]] checks all your mods at once.
