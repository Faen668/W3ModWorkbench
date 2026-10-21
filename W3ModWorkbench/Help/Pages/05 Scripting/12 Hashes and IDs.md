---
title: Hashes and IDs
icon: E8EF
order: 12
summary: The hashes the game stores names as, the names behind hashes, numbers read every way, and a mod's string IDs.
keywords: hash fnv crc wwise id string id nexus number hex
windows: HashToolsWindow
---
**Tools > Hashes and IDs** works out the numbers the game uses in place of names.

![Hashes and IDs](hash-tools.jpg)

- **Hash a name**: the hash a name is stored as: in game files (FNV-1a with the closing zero), as a strings key, as a Wwise sound ID, and plain FNV-1a 32 and 64 and CRC-32.
- **Find the name behind a hash**: looks through every name in the game's scripts and every string key. Type it in decimal, or with `0x` in front.
- **Read a number**: as hex, signed and as a float, its bytes as a file stores them, and for a string ID in the `211…` range, the Nexus mod ID and string number it's made of.
- **String IDs for a mod**: the block of 1,000 string IDs a Nexus mod ID gives by the usual convention: 211, then the mod's four-digit ID, then 000 to 999. See [[Editing strings]].
