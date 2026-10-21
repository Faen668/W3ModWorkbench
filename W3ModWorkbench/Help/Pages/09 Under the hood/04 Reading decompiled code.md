---
title: Reading decompiled code
icon: E943
order: 4
summary: What decompiled scripts lose, and the signs that something needs a closer look.
keywords: decompiled comments formatting initialisers defaults hints warnings recompile
---
Decompiled code is real WitcherScript, usually very close to what the author wrote. A few things the compiler throws away can't come back.

## What changes

- **Comments** are gone.
- **Formatting** is the app's own: tabs, braces on their own line, one statement a line.
- **Initialisers** such as `var total : int = 0;` come back as a declaration at the top of the function and `total = 0;` where the line was.
- **`public`** isn't shown, as it's the default; `private` and `protected` are.
- **Annotated functions** always use `function`, even when they wrap an `event`, as REDkit writes them.
- **Defaults** (`default x = …;`) come after the class's variables, and **hints** after them.

## Signs that something needs a look

- A call such as `Multiply_Matrix_Matrix(a, b)`: an operator with no WitcherScript symbol.
- `// (jump to offset … could not be structured)`: a jump that fits no known pattern.
- `// This function could not be decompiled: …`: something the decoder doesn't know; the rest of the file is fine.

None of these turned up across the mods used to test the decompiler, so seeing one most likely means a mod uses something unusual.

## Can I compile it again?

Usually yes, after a quick look. The author's licence still applies to their code: decompiling is best used to understand a mod, check compatibility, or get back your own lost source.
