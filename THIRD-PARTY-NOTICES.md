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

The animation decoder (`Core/Animation`) was written the same way: WolvenKit's sources gave the names of the animation buffer's properties and the bit widths of its packed orientations and positions. How the numbers map to quaternions, how the parts of a long animation are timed, and the rig's bind pose were worked out from the game's cutscenes and rigs, and checked against the animations' own bounding boxes.

- Project: https://github.com/WolvenKit/WolvenKit-7
- Licence: GPL-3.0 (not included; only facts about the file format were used)

## Asset categories and tags: EasyAssetBrowser by Vincent.02

The Asset Browser's **EAB category** and **EAB tags** filters and columns, and the EasyAssetBrowser window, show the categories and tags of Vincent.02's EasyAssetBrowser, a site for finding assets for world building. They're off until you turn them on; then they're read from its GitHub repository and kept in a cache on your computer (see PRIVACY.md). None of its data is included with the tool. Tags you suggest from the tool go to that repository as an issue, for its author to review. Used with Vincent.02's agreement.

- Site: https://dev-cube.net/eab/
- Project: https://github.com/vincocrafter/EasyAssetBrowser
- Licence: Apache-2.0

## Concentus

The scene viewer decodes the game's voice lines, which are Opus inside Wwise files, with Concentus, a C# port of the Opus reference decoder.

- Project: https://github.com/lostromb/concentus
- Licence: BSD 3-Clause

```
Copyright (c) by various holding parties, including (but not limited to): 
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.


This repository and its redistributable packages contain independently compiled
versions of the Opus C reference library, which is maintained by Xiph.org and the
Opus open-source contributors. The source code for these libraries is freely available
at https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2, and all binaries are being
redistributed to you under the same terms of the general Opus license dictated above.
```

## NAudio

The cutscene viewer plays a cutscene's music and voices together through NAudio's mixer.

- Project: https://github.com/naudio/NAudio
- Licence: MIT

```
Copyright 2020 Mark Heath

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

## Helix Toolkit

The 3D views (meshes, entities, cutscenes, scenes and the preview pane) draw with Helix Toolkit's Direct3D 11 viewport for WPF, `HelixToolkit.Wpf.SharpDX`, with its `HelixToolkit`, `HelixToolkit.SharpDX`, `HelixToolkit.Geometry` and `HelixToolkit.Maths` libraries. It brings SharpDX (MIT, http://sharpdx.org) and the .NET Community Toolkit (`CommunityToolkit.Mvvm`, `.Common`, `.Diagnostics`; MIT, https://github.com/CommunityToolkit/dotnet) with it.

The view's own pixel shader (`Rendering/Shaders/psW3Mesh.hlsl`) is written for this tool on Helix Toolkit's shader library: it includes Helix's `Common.hlsl`, `CommonBuffers.hlsl`, `DataStructs.hlsl` and `psCommon.hlsl` (v3.1.2) unchanged, and its light loop follows Helix's `psMeshBlinnPhong.hlsl`.

- Project: https://github.com/helix-toolkit/helix-toolkit
- Licence: MIT

```
The MIT License (MIT)

Copyright (c) 2023 Helix Toolkit contributors

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be included
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
