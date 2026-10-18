# Third-party notices

W3 Mod Workbench includes work by other people. Thank you to everyone below.

## WitcherScript syntax highlighting: witcherscript-vscode by WebSpam

The colours in the preview window for `.ws` files come from the WitcherScript grammar in WebSpam's VS Code extension, used with his kind permission. The grammar file (`witcherscript.tmLanguage.json`) is included unchanged.

- Project: https://github.com/webspam/witcherscript-vscode
- Licence: MIT

```
MIT License

Copyright (c) 2026 webspam

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Decompiler research and the built-in compiler: tw3-cahirb by Aelto and WebSpam

Aelto's own `.rsblob` decompiler project helped fill gaps in this tool's decompiler: the complete, ordered list of the script engine's built-in operators, several rarely used instructions, the imported-property table, and a test blob from an older REDkit build. No code from the decompiler is included; only facts about the file format were used, written up independently.

The built-in script compiler (`Core/Scripts/Standalone`) is based on the work of **WebSpam**. It is a C# port of the WitcherScript-to-`.rsblob` compiler that WebSpam contributed to tw3-cahirb in pull request 1 (lexer, parser, symbol table, code generator and blob writer), extended and checked against `wcc_lite` output on real mods. The engine class list it carries was produced by the game's own `-dumprtti` option.

- Project: https://github.com/Aelto/tw3-cahirb
- Pull request: https://github.com/Aelto/tw3-cahirb/pull/1
- Licence: MIT, as tw3-cahirb

## Texture decoding: DirectXTex by Microsoft

The partition shapes and anchor indices in the BC7 texture decoder (`Core/Textures/Bc7Tables.cs`) are the tables from the Direct3D BC7 format specification, as published in Microsoft's DirectXTex. The rest of the decoder is written from the format specification.

- Project: https://github.com/microsoft/DirectXTex
- Licence: MIT

```
MIT License

Copyright (c) Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Mesh format research: WolvenKit 7

The CR2W reader and the mesh decoder (`Core/Meshes`) were written for this tool, using WolvenKit's Witcher 3 sources as a guide to the format: the class and property names of meshes and materials (`CMesh`, `SMeshCookedData`, `CMaterialInstance` and their properties), and an early description of the cooked chunk table. The rest of the chunk table, the vertex streams, the Remastered's CR2W 164 files and REDkit's uncooked meshes were worked out from the game's own files. No WolvenKit code is included, so its GPL licence doesn't apply to this tool; thank you to the WolvenKit team, and to jlouis, whose witcherconverter WolvenKit credits for its first mesh reader.

- Project: https://github.com/WolvenKit/WolvenKit-7
- Licence: GPL-3.0 (not included; only facts about the file format were used)

## AvalonEdit

The code view in the preview window (line numbers, search, fast scrolling) is AvalonEdit.

- Project: https://github.com/icsharpcode/AvalonEdit
- Licence: MIT

```
MIT License

Copyright (c) 2000-2025 AlphaSierraPapa for the SharpDevelop Team

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.
```
