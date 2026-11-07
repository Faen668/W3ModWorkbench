---
title: Quest graphs
icon: E8A9
order: 28
summary: Quests and quest phases (.w2quest, .w2phase) as REDkit's quest editor draws them, with every block's GUID, where else it is, and a ready-made Native Quest Hook listener for the blocks you pick.
keywords: quest phase w2phase w2quest graph block blocks guid guids native quest hook nqh listener watched redkit quest editor phase block embedded external used by deletion marker duplicate compare mod changes live log fired output script factsdb pause condition scene interaction dialog
windows: QuestGraphWindow, QuestStubWindow, QuestLogWindow
---
A quest file is a graph of blocks: pauses that wait for something, scenes and dialogues, scripts, facts being set, and phases that hold graphs of their own. The quest graph viewer shows one as REDkit's quest editor draws it, with every block's properties and its **GUID**, the number the game knows the block by. It opens when you double-click a `.w2phase` or `.w2quest` file anywhere in the app.

The GUIDs are what [Native Quest Hook](https://github.com/Faen668/NativeQuestHook/releases) listens for: a mod that wants to know when a quest moves on watches the blocks that move it. This viewer finds those blocks and writes the listener for you.

![At the Mercy of Strangers: the dialogue with the deserter picked, with its GUID, its links and where else its GUID is](quest-graph.jpg)

## The graph

- Each block is a box, its top edge coloured by its group (the key under the graph names them). It shows its name, a line saying what it does (the script it runs, the fact it sets, what a pause waits for, the scene it plays), its inputs down the left and its outputs down the right. A block's comment sits over it.
- A phase's inputs and outputs are red arrows. A note is a box with a green header.
- Lines run from each output to the input it leads to. A link that goes back to an earlier block loops underneath. A **Cut control**'s red dashed arrow points at the block it stops.
- An output with no name, such as a pause's, is **None**: that's what the game and Native Quest Hook call it too.
- Drag to move about, use the wheel to zoom, and press **Fit** ({{F}}) or double-click the background to see it all.

The game's files keep no places for their blocks, so the viewer lays them out from the phase's inputs to its outputs. REDkit's own files (its `r4data`) keep where each block was put, and show that way.

### Phases inside phases

Double-click a phase block, or pick it and press **Open this phase**, to open the graph it runs: its own, or another `.w2phase` file (those have a link icon). The way back is over the graph: click any part of it, or press the up arrow beside it ({{Backspace}}).

## The block list

Under the graph is every block in the file, from every phase in it. Type in the search box ({{Ctrl+F}}) to find blocks by any word in them: a fact (`mq1036_cursing_played`), a script function (`EnableDynamicMappin`), a scene, a tag, a condition, or a GUID in either form. Every word typed must match. Press {{Enter}} to go from one block found to the next.

- **Every kind** picks one kind of block: Script, Pause, Interaction dialog…
- **This graph only** lists just the blocks in the graph shown.
- Picking a block in the list opens the graph it's in and puts it in the middle.

## A block's GUID

Pick a block and its GUID is at the top of the side panel, twice. The first is the usual form, the one Native Quest Hook logs and the game files' JSON uses; the second is the same GUID as REDkit's property grid shows it, which looks different but is the same number. **Copy** copies either.

> tip: Native Quest Hook 1.1.0 and later takes either form in `watched`. Older versions match only the first.

To copy several, pick them in the list (or {{Ctrl}}+click them in the graph) and press **Copy GUIDs** or {{Ctrl+C}}: one a line. Right-click the list for **Copy GUIDs as REDkit shows them**.

Under **Where this GUID is** the panel lists every copy of the block: the game's, an official DLC's, an installed mod's, your project's. It says which one the game reads, as a mod's copy of a quest file replaces the game's.

Notes and comments have no GUID: the game doesn't run them.

## Used by

With no block picked, the side panel is about the file. **Used by** says how many phase blocks, in every quest file, run it, lists them, and says whether the game ever reaches it. A file nothing runs is usually a test or a leftover, and a block in it never fires.

![A small phase many quests use, with the phase blocks that run it](quest-graph-used-by.jpg)

## Find GUID

Paste a GUID in the box over the graph and press {{Enter}}. If the block is in the file shown, it's picked; if not, the side panel lists every file in the game, its DLC, the installed mods and your project that has it. Click one to open it there.

## Duplicate GUIDs

Two blocks with one GUID look the same to Native Quest Hook. When a block shares its GUID with another one, in the same file or in another file the game runs, a warning over the graph says how many, the list marks them, and **Show them** lists just those. The side panel names the other block.

## Making a listener

Pick the blocks to listen to and press **Make listener**. The window writes a WitcherScript listener for Native Quest Hook:

- **Your mod's name** names its registration, its class and its file.
- **Events** are the ones to fill in. **OnBlockFinished** is the one that moves a quest on: each block's case says which outputs it can fire.
- **Copy** copies it, **Save to project** puts it in your project's `scripts\local` folder, and **Save as…** anywhere.

![A listener for the deserter's dialogue and the pause that ends the encounter](quest-listener.jpg)

The listener watches the blocks, and registers itself when Native Quest Hook starts:

```ws title="MyModQuestListener.ws"
class CMyMod_QuestListener extends CNQH_Listener
{
	public function Init() : CMyMod_QuestListener
	{
		watched.PushBack("4b435b43-30c2-40d8-9eae-08df8d1b7df9");   // CQuestInteractionDialogBlock "Interaction dialog"
		return this;
	}

	public function OnBlockFinished(guid : string, output : name)
	{
		switch (guid)
		{
		case "4b435b43-30c2-40d8-9eae-08df8d1b7df9":   // CQuestInteractionDialogBlock "Interaction dialog": fires 'Help' or 'Nope'
			break;
		}
	}
}

@wrapMethod
function NQH_RegisterListeners()
{
	wrappedMethod();
	NQH_Register('MyMod', (new CMyMod_QuestListener in theGame).Init());
}
```

Fill in what your mod does in each case, for example checking `output == 'Help'`. It needs Native Quest Hook installed; without it the mod still compiles and runs, and simply hears nothing.

## Comparing with another copy

**Compare** lists the file's other copies (the game's, and each installed mod's) and compares the one shown with the one picked, block by block by GUID. Mods made in REDkit keep the game's GUIDs, so this shows what a mod did to a quest:

- blocks **new here**, marked green on the graph;
- blocks **relinked**, **moved** to another phase or with **changed** properties, marked amber; the side panel says what changed, and how its links went in the other copy;
- blocks **only in the other copy**, listed under the graph: double-click one to open it there.

The bar over the graph counts them, and **Show them** lists just what changed. **Stop comparing** ends it.

## The live log

**Live log** (also **Tools > Quest block log**) shows Native Quest Hook's log as the game writes it: every block that starts, finishes (with the output it fired) or stops while you play. Play a quest, watch which blocks fire, and double-click one to open it in its quest file with its GUID.

![The deserter's dialogue starting, stopping on a load and coming back, and the pause that fires when the player leaves](quest-log.jpg)

Native Quest Hook logs only the blocks mods watch, unless its ini says to log them all. Put these lines in `bin\x64_dx12\NativeQuestHook.ini` in the game's folder, and start the game again:

```ini title="NativeQuestHook.ini"
[Debug]
LogEveryBlock=1
```

- **Started** and **Finished** are what you'll usually want. **Stopped** and **Replayed** are mostly loads and world changes putting back what was running; **Input again** is a block reached once more.
- **Only watched** shows just the blocks a mod watches.
- **Pause** stops the table moving while you read it; **Open log…** reads an older log, such as the last session's `NativeQuestHook.prev.log`.

## Settings

**Settings > Viewers > Quest graphs** has:

- **Left to right** or **Top to bottom**: which way the game's quest graphs run.
- **Keep REDkit's places for uncooked files**: off, REDkit's own files are laid out like the game's.
- **Open phases in a new window**: on, each phase opens in a window of its own instead of in place.

See also [[Properties]] for the property list, and [[The journal]] for the journal entries quest blocks name.
