---
title: Key binding helper
icon: E765
order: 11
summary: Give your mod's action a key: the input.settings lines, the input.xml entry and the script that listens.
keywords: key binding input settings xml listener hotkey action context
windows: InputBindingsWindow
---
**Tools > Key binding helper** writes everything a mod needs to give an action a key.

![The key binding helper](key-bindings.jpg)

1. Type the action's name (`MyModAction`), pick the **key** (every key the engine knows), whether it fires when **held**, and the **contexts** it works in (exploring, combat, on a horse, swimming…; **Show every context** lists the rest).
2. The helper writes three things, each with a **copy** button:
   - the lines for `input.settings`
   - the `input.xml` entry that puts the action in the game's key bindings menu
   - the script that listens for it
3. It reads your own `input.settings` to say whether the key is free in those contexts.

```ws
theInput.RegisterListener(this, 'OnMyModAction', 'MyModAction');

event OnMyModAction(action : SInputAction)
{
	if (IsPressed(action))
	{
		// the key was pressed
	}
}
```

**Add to my game** puts the lines in your `input.settings` to try the key, keeping a `.bak` copy (close the game first). It then asks separately before adding the entry to the game's `input.xml`, with an `input.xml.bak` copy. **Leave it** keeps the game's folder as it is: the key still works, it just isn't in the menu.
