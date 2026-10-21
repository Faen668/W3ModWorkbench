---
title: XML overrides
icon: E943
order: 2
summary: Which mods change the same items, abilities and sets in their XML, and which one wins each field.
keywords: xml overrides on_conflict extend replace items abilities sets clash definitions
windows: XmlOverridesWindow
---
Since the Remastered update, a mod's XML can change one of the game's definitions (an item, an ability, a set) without copying the whole file, using `on_conflict`:

```xml
<ability name="Geralt _Stats" on_conflict="extend">
	<vitality type="add" min="500" />
</ability>
```

`extend` adds to the game's definition; `replace` replaces it. When several mods change the same definition, the order decides who wins.

**Tools > View XML overrides…** reads every mod's XML (loose files and XML inside bundles) and lists the definitions they set.

![XML overrides](xml-overrides.jpg)

- Each definition shows which mods set it, in order (the first wins), and whether each **extends**, **replaces** or just defines it.
- **Fields set by more than one mod** shows exactly which values clash, and which mod's value the game uses.
- **Shared or clashing only** hides definitions just one mod touches. Search ({{Ctrl+F}}) by definition or mod.
- **Open file** opens a mod's XML at the definition; **Copy list** copies them all as text.
