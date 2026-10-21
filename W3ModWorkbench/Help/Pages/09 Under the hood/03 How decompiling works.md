---
title: How decompiling works
icon: E943
order: 3
summary: How bytecode is turned back into readable WitcherScript, in three passes.
keywords: decompile bytecode expressions control flow if while for switch
---
Turning bytecode back into readable code happens in three passes.

## 1. Decoding

Each function's bytecode is read instruction by instruction: "load this variable", "call that function", "jump if false". About ninety kinds of instruction cover constants, variables, calls, operators, array methods, casts, switches and jumps.

Jumps are stored as offsets in the game's in-memory layout, not the file's, so while decoding the app also works out how big each instruction is once loaded. As a check, the total is compared with the size the compiler recorded for each function: every function in every blob tested matches exactly.

## 2. Expressions

Instructions come in prefix order, so `a + b * c` arrives as "add, a, multiply, b, c". Reading them back gives an expression tree, printed with the fewest brackets that keep the meaning.

- Operators are calls to built-in functions, one for each type (int `+`, float `+`, string `+`…), mapped back to their symbols.
- Casts the compiler adds by itself are hidden (int to float, object to bool in an `if`); casts written in the code, such as `(int)`, are kept.
- Calls that leave out optional arguments keep their empty slots: `onHover('Name', , "hint")` comes back as written.

## 3. Control flow

Bytecode has no `if`, `for` or `while`, only jumps. They're rebuilt from the jump patterns:

- A conditional jump forward is an `if`; if the block it skips ends with a jump further on, that's an `else`.
- A conditional jump whose block jumps back to it is a `while`.
- A `for` is a `while` whose setup, test and step are on the same source line (the compiler keeps line numbers).
- Jumps to the end of a loop are `break`; to its step, `continue`.
- Each `switch` keeps a chain of case labels, put back in order.

Line numbers also decide where blank lines go, so the original layout roughly survives.
