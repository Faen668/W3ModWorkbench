---
title: About and licence
icon: E946
order: 2
summary: Who made the app, its licence, and the people whose work it builds on.
keywords: about licence license mit credits copyright webspam aelto avalonedit
---
W3 Mod Workbench is made by **Faen90** and is free to use.

## Licence

The app is open source under the **MIT Licence**. In plain terms: you can use it, share it, change it and build on it, in your own projects too, as long as the copyright notice and licence stay with it. It comes with no warranty. The full text is in the `LICENSE` file beside the app, and under the **ⓘ** button in the main window.

Copyright © 2026 Faen90.

## Other people's work

The licence covers the app, not the files you open with it. The game's files belong to CD PROJEKT RED, and a mod's files belong to whoever made it. Please respect a mod author's wishes and licence before reusing their work.

## Not an official tool

W3 Mod Workbench is an unofficial fan-made tool, not affiliated with, endorsed by or supported by CD PROJEKT RED. The Witcher, REDkit and REDengine are trademarks of CD PROJEKT S.A., named only to say what the app works with.

## Credits

- **WebSpam**: the WitcherScript colouring comes from his VS Code extension, [witcherscript-vscode](https://github.com/webspam/witcherscript-vscode), used with his kind permission. The built-in compiler is a port of the WitcherScript compiler he wrote for tw3-cahirb, extended and checked against REDkit's.
- **Aelto**: his decompiler project, [tw3-cahirb](https://github.com/Aelto/tw3-cahirb), filled real gaps in this one.
- **AvalonEdit** by the SharpDevelop team: the code editor.
- **ww2ogg**, **NVorbis** and **vgmstream**: playing the game's sounds.

Their licences are in `THIRD-PARTY-NOTICES.md` beside the app, and under the **ⓘ** button. Everything else (the bundle, cache and script readers and writers, the decompiler, the viewers) is written for this app, checked against the game's and REDkit's own files. Nothing of REDkit's is shipped with it.

## Works alongside

Some jobs already have a good tool of their own, made by people in the community, so the app points to it rather than doing that job itself:

- **[Script Merger Remastered](https://www.nexusmods.com/witcher3/mods/13076)** by relapse12: merging mods that change the same scripts or files. The load order analyser opens it (set its path in **Settings > Game**).
- **[RedSpeech Tool](https://www.nexusmods.com/witcher3/mods/14750)** by nikich340: making voice lines (packing speech files, WAV to WEM, lip sync). Build puts the speech files it makes in your mod.
