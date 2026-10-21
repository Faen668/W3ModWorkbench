---
title: XML files
icon: E943
order: 3
summary: Read and edit the game's XML (items, abilities, menus) with colouring, an outline and folding.
keywords: xml editor items abilities def_item outline format encoding
windows: XmlWindow
---
Much of the game's data is XML: items, abilities, loot, menus. Double-click an XML file to read it; right-click one and choose **Open in XML editor** (or use **Tools > Open an XML file**, or drop one on the window) to edit it.

![An item definition in the XML editor](xml-editor.jpg)

- Tags, names, values and comments are coloured, in light and dark.
- **Outline** ({{Ctrl+Shift+O}}) lists every element as a tree, each with the attribute that names it, such as `ability  DLC5 Nilfgaardian Armor _Stats`. Click one to go there; the filter box finds names.
- Elements fold from the margin, and the bar under the text says whether the XML is well-formed, or where it isn't (click to go there).
- **Format** indents it neatly, one element a line.
- Files keep the encoding they came in.

A game file opens as a copy that **Save as** writes out; for your mod, **Copy to project** first (see [[Changing game files]]).

```xml
<ability name="MyMod_Ability">
	<attack_power type="mult" min="0.1" />
</ability>
```

For mod menus, see [[The mod menu builder]].
