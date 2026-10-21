---
title: Quest facts
icon: E9D9
order: 9
summary: Every fact the scripts set or read, and every place that does.
keywords: facts factsadd factsquerysum quest fact database
windows: FactsWindow
---
Quests remember what happened as **facts**: named numbers such as `q103_baron_dead`. **Tools > Quest facts** lists every fact the scripts set or read with `FactsAdd`, `FactsSet`, `FactsRemove`, `FactsQuerySum`, `FactsDoesExist` and the rest, and each place that does. The calls that set a fact are marked.

![Quest facts](quest-facts.jpg)

- Search by name. Double-click a place to open its script there.
- The filter shows only facts your project or a mod uses, facts that are set but never read (or read but never set), or calls whose fact name is built in code.
- **Name** and **FactsQuerySum** copy the fact's name, or the call that reads it.

```ws
if (FactsQuerySum("q103_baron_dead") > 0)
{
	// the Baron is dead
}
```

> note: Quests and scenes set facts too, and they're compiled resources rather than scripts, so a fact "no script sets" may well be set by a quest.
