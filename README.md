<div align="center">

<img src="docs/images/icon.png" alt="W3 Mod Workbench icon" width="96" />

# W3 Mod Workbench

[![Release](https://badgen.net/github/tag/Faen668/W3BundleExplorer?label=release)](https://github.com/Faen668/W3BundleExplorer/releases/latest)
[![Downloads](https://badgen.net/github/assets-dl/Faen668/W3BundleExplorer?label=downloads&color=green)](https://github.com/Faen668/W3BundleExplorer/releases)
[![Platform](https://badgen.net/badge/platform/Windows%2010%20%7C%2011/0078D6)](#-download)
[![.NET](https://badgen.net/badge/.NET/9.0/512BD4)](https://dotnet.microsoft.com/)
[![Game](https://badgen.net/badge/game/The%20Witcher%203/8B1A1A)](https://www.thewitcher.com/)
[![License](https://badgen.net/badge/license/MIT/green)](LICENSE)
[![Last commit](https://badgen.net/github/last-commit/Faen668/W3BundleExplorer?color=yellow)](https://github.com/Faen668/W3BundleExplorer/commits)
[![Stars](https://badgen.net/github/stars/Faen668/W3BundleExplorer)](https://github.com/Faen668/W3BundleExplorer/stargazers)

**Browse and extract The Witcher 3's `.bundle` archives, and turn REDkit compiled scripts (`precompiled.rsblob`) back into readable WitcherScript.**

<img src="docs/images/main-window.png" alt="W3 Mod Workbench main window" width="860" />

</div>

---

## ✨ Features

### Bundles
- **Opens any `.bundle`**: the base game, expansions, DLC and mods. Supports both the current format (version 5, used since the next-gen update and by REDkit) and the older version 3, including the game's bundles over 4 GB.
- **Opens whole folders**: point it at `The Witcher 3\mods` and every installed mod is listed side by side, labelled by mod name rather than a sea of `blob0.bundle`.
- **Fast**: the entire game, about 365,000 files, loads in around a second.
- **Browse and search**: folder tree, sortable file list, and a search box that matches words anywhere in the path or wildcards on the file name (`*.redswf`, `*_icon??.xbm`).
- **Extract exactly what you need**: tick files across several archives at once, or extract everything shown, a single folder or a quick selection, with or without the original folder structure.
- **Handles every compression the game uses**: stored, zlib and LZ4 HC, plus LZ4 and Snappy.

### Compiled scripts
- **Decompiles `precompiled.rsblob`** back into WitcherScript, split into tidy `.ws` files:
  - one file per class, with its states,
  - the mod's enums and structs,
  - global and `exec` functions,
  - one file per game class the mod changes, holding its `@addField`, `@addMethod`, `@wrapMethod` and `@replaceMethod` code. That's handy for spotting conflicts between mods.
- **Reads like the original**: rebuilds `if` / `else if`, `for`, `while`, `do … while`, `switch`, `break` and `continue`. Keeps original names, types, defaults and hints, and shows every engine operator as its symbol.
- **Real enum names**: values are named from the mod's own enums and from the game's script sources, which the tool finds automatically.
- **Checked against real mods**: every decompiled function's size is verified against what the compiler recorded, and the output was compared line by line with the original source of several mods.

### Preview
- **Read text files without extracting**: decompiled scripts, plus `.xml`, `.csv`, `.ws`, `.json` and `.txt` files inside bundles.
- **WitcherScript syntax highlighting**, using the grammar from WebSpam's VS Code extension.
- **Line numbers and search** (Ctrl+F) with match case, whole word and regex, and F3 to jump between matches.
- **Go to definition**: Ctrl+click (or F12) any function, class, enum value or variable in a script to open where it's declared, in the same mod or in the game's own scripts. A `@wrapMethod` leads to the game's original, and calls the mod wraps offer both. Alt+Left goes back.
- **Definition popup**: rest the mouse on a name to see its declaration, highlighted, before you jump.
- **Tabs and split view**: files open as tabs (or separate windows, if you prefer), and `Ctrl+\` splits the preview to read two files, or two parts of one, side by side. Drag tabs between groups and windows.

### Everything else
- **Light and dark modes**, or follow Windows, with eight colour themes: your Windows accent, one for each witcher school, and a plain Graphite.
- **A full built-in guide** (F1), including an Advanced section on the file formats and how the decompiler works.
- **Read-only**: it never changes a bundle, a script or anything in your game folder.

## 📸 Screenshots

<div align="center">

| Decompiled script with syntax highlighting | Built-in guide |
|:---:|:---:|
| <img src="docs/images/script-preview.png" alt="Script preview" width="420" /> | <img src="docs/images/help.png" alt="Help window" width="420" /> |

<img src="docs/images/settings.png" alt="Settings window" width="380" />

</div>

## 📥 Download

1. Grab the latest `W3ModWorkbench-v<version>-win-x64.zip` from **[Releases](https://github.com/Faen668/W3BundleExplorer/releases/latest)**.
2. Unzip it anywhere.
3. Run `W3ModWorkbench.exe`.

No installer and nothing else to install: the release includes everything it needs, including .NET.

**Requirements:** Windows 10 or 11, 64-bit.

> **"Windows protected your PC"?** The exe isn't code-signed, so SmartScreen may warn the first few times. Click **More info**, then **Run anyway**.

## 🚀 Quick start

1. Click **Open files** or **Open folder**, or drag `.bundle` / `.rsblob` files or folders onto the window.
2. Pick a folder in the tree. The list shows everything inside it.
3. Tick the files you want, or search for them (Ctrl+F).
4. Set **Extract to**, then press **Extract checked** (Ctrl+E).

Double-click any text file or decompiled script to preview it. Press **F1** for the full guide.

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open files |
| `Ctrl+F` | Search |
| `Ctrl+E` | Extract checked files |
| `Space` | Tick or untick the highlighted rows |
| `Enter` | Preview the highlighted text file |
| `Ctrl+,` | Settings |
| `F1` | Help |

## 📝 About decompiled scripts

Compiled scripts keep names, types and logic, so the output is usually very close to what the author wrote. A few things are lost in compilation and can't be recovered:

- **Comments** are gone.
- **Formatting** is the tool's own.
- **Initialisers** like `var x : int = 0;` come back as a declaration plus a separate `x = 0;`.
- **Engine (C++) enums**, such as `EInventoryEventType`, have no script names, so their values show as a cast, e.g. `(EInventoryEventType)1`.

Decompiling is great for understanding how a mod works, checking two mods for compatibility, or recovering your own lost source. The code still belongs to its author, and their licence applies to it, so please don't republish other people's decompiled scripts as your own.

## 🛠️ Building from source

**You need:** Windows, and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or Visual Studio 2022 17.12 or newer.

Open `W3ModWorkbench.sln` in Visual Studio and press F5, or build from a terminal:

```bash
dotnet build W3ModWorkbench.sln -c Release
```

The only NuGet package is [AvalonEdit](https://github.com/icsharpcode/AvalonEdit), and it restores automatically.

**Making a release:** run `create-release.bat`. It publishes a self-contained single-file exe and packs it with the licence, notices, readme and changelog into `release\W3ModWorkbench-v<version>-win-x64.zip`. The version comes from `<Version>` in `W3ModWorkbench.csproj`. Visual Studio's **Publish** button uses the same `Release-win-x64` profile.

<details>
<summary><b>Project layout</b></summary>

| Folder | What's in it |
|---|---|
| `Core/` | Bundle reader, LZ4 and Snappy decoders, extraction, game folder detection |
| `Core/Scripts/` | The `.rsblob` reader, bytecode decoder, operator table, decompiler and WitcherScript writer |
| `Highlighting/` | WebSpam's WitcherScript grammar and the engine that colours the preview with it |
| `Help/` | The in-app guide (`HelpContent.cs` holds the text) |
| `ViewModels/` | The tree and list items behind the main window |

</details>

## 🙏 Credits

- **[WebSpam](https://github.com/webspam)**: the WitcherScript syntax highlighting comes from his VS Code extension, [witcherscript-vscode](https://github.com/webspam/witcherscript-vscode), used with his permission. Well worth installing if you write WitcherScript in VS Code.
- **[Aelto](https://github.com/Aelto)**: his decompiler project [tw3-cahirb](https://github.com/Aelto/tw3-cahirb) filled real gaps in this one. That covers the full list of the engine's built-in operators, several rarely used instructions, and a test blob from an older REDkit build. No code was copied, only facts about the format.
- **[AvalonEdit](https://github.com/icsharpcode/AvalonEdit)** by the SharpDevelop team: the code view in the preview window.

Licence texts for the third-party parts are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## 🤖 How it was made

W3 Mod Workbench was developed with AI assistance (Claude, by Anthropic), most of all in reverse-engineering the undocumented `.rsblob` format and building the decompiler. Nothing was taken on trust: the format work was checked against mods whose original source was available, and every decompiled function's size is verified against what the compiler recorded.

## 📄 Licence

Released under the [MIT Licence](LICENSE). Copyright © 2026 Faen90.

## ⚠️ Disclaimer

W3 Mod Workbench is an unofficial fan-made tool. It is not affiliated with, endorsed by or supported by CD PROJEKT RED. The Witcher, REDkit and REDengine are trademarks of CD PROJEKT S.A., used here only to describe what the tool works with.

<div align="center">
<sub>Developed by Faen90</sub>
</div>
