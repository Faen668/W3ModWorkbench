using System.Text;
using System.Text.RegularExpressions;

namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>A change to a script's text: replace <paramref name="Length"/> characters at <paramref name="Offset"/>.</summary>
public sealed record TextEdit(int Offset, int Length, string Text);

/// <summary>What a refactor is called, and so which words describe it, by its kind.</summary>
public enum RefactorKind
{
    ExtractVariable, ExtractFunction, ExtractMethod, InlineVariable, SplitDeclaration, JoinDeclaration,
    CollapseSwitch, ExpandSwitch, CollapseIf, ExpandIf,
}

/// <summary>
/// A refactor that can be made where the cursor or selection is: the edits, made together as one change, and where to
/// rename the new name afterwards (an offset in the text after the edits), when there is one.
/// </summary>
public sealed record Refactor(RefactorKind Kind, IReadOnlyList<TextEdit> Edits, int? RenameAt = null);

/// <summary>
/// The refactors Ctrl+. offers in a script: extract an expression to a variable, statements to a function or method,
/// inline a variable, split or join a declaration, and lay a switch or an if/else chain out on one line a case, or
/// over several. Each one keeps what the code does: one that would change it (an expression that's evaluated only
/// sometimes, a return in the statements to extract) isn't offered.
/// WitcherScript wants every var before the first statement of a function, so a new variable is declared at the end of
/// the function's vars, and given its value where the expression was.
/// </summary>
public static partial class ScriptRefactors
{
    /// <summary>The refactors there are at a selection (or the cursor, with a length of 0), in the order to list them.</summary>
    /// <param name="text">The script's text, as typed: the parsed code has its comments and strings blanked.</param>
    public static List<Refactor> At(string text, ParsedScript doc, DefinitionFinder finder, int start, int length)
    {
        var found = new List<Refactor>();
        void Try(Func<Refactor?> make)
        {
            try
            {
                if (make() is { } r) found.Add(r);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
            {
                // one that can't be worked out here isn't offered
            }
        }
        var ctx = new Context(text, doc, finder);
        if (length > 0)
        {
            Try(() => ctx.ExtractVariable(start, length));
            Try(() => ctx.ExtractFunction(start, length, method: true));
            Try(() => ctx.ExtractFunction(start, length, method: false));
        }
        Try(() => ctx.InlineVariable(start));
        Try(() => ctx.SplitDeclaration(start));
        Try(() => ctx.JoinDeclaration(start));
        Try(() => ctx.Switch(start, collapse: true));
        Try(() => ctx.Switch(start, collapse: false));
        Try(() => ctx.If(start, collapse: true));
        Try(() => ctx.If(start, collapse: false));
        return found;
    }

    /// <summary>Where an offset in the text before the edits is after them: moved by the edits before it.</summary>
    public static int After(IEnumerable<TextEdit> edits, int offset) =>
        offset + edits.Where(e => e.Offset < offset || e.Offset == offset && e.Length == 0).Sum(e => e.Text.Length - Math.Min(e.Length, offset - e.Offset));

    /// <summary>Applies edits to a text, in one go: what the text is after a refactor.</summary>
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var sb = new StringBuilder(text);
        foreach (var e in edits.OrderByDescending(e => e.Offset).ThenByDescending(e => e.Length)) sb.Remove(e.Offset, e.Length).Insert(e.Offset, e.Text);
        return sb.ToString();
    }

    private sealed class Context(string text, ParsedScript doc, DefinitionFinder finder)
    {
        private readonly string _code = doc.Code;

        // ------------------------------------------------------------ pieces of a function

        private FunctionScope? FunctionAt(int offset) => doc.FunctionAt(offset) is { } fn && offset > fn.BodyStart && offset < fn.BodyEnd ? fn : null;

        /// <summary>The whitespace a line starts with.</summary>
        private string IndentAt(int offset)
        {
            int lineStart = LineStart(offset);
            int i = lineStart;
            while (i < text.Length && text[i] is ' ' or '\t') i++;
            return text[lineStart..i];
        }

        private int LineStart(int offset) => _code.LastIndexOf('\n', Math.Max(0, Math.Min(offset, _code.Length) - 1)) + 1;

        private int LineEnd(int offset)
        {
            int end = _code.IndexOf('\n', offset);
            return end < 0 ? _code.Length : end + 1;
        }

        /// <summary>The function's var statements: where each starts (its line) and ends (after its line break).</summary>
        private List<(int Start, int End)> VarLines(FunctionScope fn)
        {
            var lines = new List<(int, int)>();
            int at = fn.BodyStart + 1;
            while (at < fn.BodyEnd)
            {
                int next = NextCode(at);
                if (next >= fn.BodyEnd || !IsWordAt(next, "var")) break;
                int semi = _code.IndexOf(';', next);
                if (semi < 0 || semi > fn.BodyEnd) break;
                lines.Add((LineStart(next), LineEnd(semi)));
                at = semi + 1;
            }
            return lines;
        }

        /// <summary>The first character from an offset that isn't a space (or a blanked comment).</summary>
        private int NextCode(int offset)
        {
            while (offset < _code.Length && char.IsWhiteSpace(_code[offset])) offset++;
            return offset;
        }

        private bool IsWordAt(int offset, string word) =>
            offset + word.Length <= _code.Length && string.CompareOrdinal(_code, offset, word, 0, word.Length) == 0
            && (offset + word.Length == _code.Length || !ScriptParser.IsWordChar(_code[offset + word.Length]))
            && (offset == 0 || !ScriptParser.IsWordChar(_code[offset - 1]));

        /// <summary>Where new vars go in a function: after its last var, or at the top of its body.</summary>
        private (int Offset, string Indent) VarInsertPoint(FunctionScope fn)
        {
            var vars = VarLines(fn);
            if (vars.Count > 0) return (vars[^1].End, IndentAt(vars[^1].Start));
            int first = NextCode(fn.BodyStart + 1);
            int bodyLine = LineEnd(fn.BodyStart);
            return (bodyLine, first < fn.BodyEnd ? IndentAt(first) : IndentAt(fn.BodyStart) + "\t");
        }

        /// <summary>
        /// The statement an offset is in: from after the ; { or } before it to its ; (outside brackets). Null when it's
        /// a control statement's header or isn't a plain statement.
        /// </summary>
        private (int Start, int End)? StatementAt(int offset, FunctionScope fn)
        {
            int depth = 0, start = offset;
            for (int i = offset - 1; i > fn.BodyStart; i--)
            {
                char c = _code[i];
                if (c is ')' or ']') depth++;
                else if (c is '(' or '[') { if (depth-- == 0) { } }
                else if (depth <= 0 && c is ';' or '{' or '}') { start = i + 1; break; }
            }
            start = NextCode(start);
            depth = 0;
            for (int i = start; i < fn.BodyEnd; i++)
            {
                char c = _code[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']') depth--;
                else if (depth == 0 && c == ';') return (start, i + 1);
                else if (depth == 0 && c is '{' or '}') return null;
            }
            return null;
        }

        // ------------------------------------------------------------ extract to variable

        public Refactor? ExtractVariable(int start, int length)
        {
            if (FunctionAt(start) is not { } fn || start + length > fn.BodyEnd) return null;
            (start, length) = Trim(start, length);
            string expr = text.Substring(start, length);
            if (expr.Length == 0 || !Balanced(_code.Substring(start, length)) || expr.Contains(';')) return null;
            if (StatementAt(start, fn) is not { } statement || statement.End < start + length) return null;
            string stmt = _code[statement.Start..statement.End];
            // a statement that runs the expression only sometimes, or more than once, can't have it worked out first
            if (Regex.IsMatch(stmt, @"^\s*(while|for|do|else)\b") || stmt.Contains("&&") || stmt.Contains("||") || stmt.Contains('?')) return null;
            // the left side of an assignment isn't a value to keep
            int assign = Regex.Match(stmt, @"(?<![=!<>])=(?!=)").Index;
            if (assign > 0 && start < statement.Start + assign) return null;
            if (TypeOf(start, length) is not { } type) return null;

            string name = FreeName(fn, NameFor(expr));
            var (varAt, varIndent) = VarInsertPoint(fn);
            string stmtIndent = IndentAt(statement.Start);
            int stmtLine = LineStart(statement.Start);
            var edits = new List<TextEdit>
            {
                new(varAt, 0, $"{varIndent}var {name} : {type};\n"), // loc-ignore
                new(stmtLine, 0, $"{stmtIndent}{name} = {expr};\n"),
                new(start, length, name),
            };
            // the new name is renamed where the expression was, once the two lines above it are in
            return new Refactor(RefactorKind.ExtractVariable, edits, After(edits.Take(2), start));
        }

        private (int, int) Trim(int start, int length)
        {
            while (length > 0 && char.IsWhiteSpace(text[start])) { start++; length--; }
            while (length > 0 && char.IsWhiteSpace(text[start + length - 1])) length--;
            return (start, length);
        }

        private static bool Balanced(string s)
        {
            int round = 0, square = 0;
            foreach (char c in s)
            {
                if (c == '(') round++;
                else if (c == ')' && --round < 0) return false;
                else if (c == '[') square++;
                else if (c == ']' && --square < 0) return false;
            }
            return round == 0 && square == 0;
        }

        /// <summary>
        /// The type of an expression where it can be told without guessing: a literal, a cast, a variable or field, or
        /// a call (its return type). Null otherwise.
        /// </summary>
        private string? TypeOf(int start, int length)
        {
            string e = _code.Substring(start, length).Trim();
            string raw = text.Substring(start, length).Trim();
            if (Regex.IsMatch(e, @"^-?\d+$")) return "int";
            if (Regex.IsMatch(e, @"^-?\d*\.\d+f?$")) return "float";
            if (raw.StartsWith('"') && raw.EndsWith('"')) return "string";
            if (raw.StartsWith('\'') && raw.EndsWith('\'')) return "name";
            if (e is "true" or "false") return "bool";
            if (Regex.Match(e, @"^\((\w+)\)\s*\w") is { Success: true } cast && !char.IsDigit(cast.Groups[1].Value[0])) return cast.Groups[1].Value;
            if (Regex.IsMatch(e, @"^[\w.]+$"))
            {
                int last = start + _code.Substring(start, length).LastIndexOfAny(['.', ' ']) + 1;
                if (_code.Substring(start, length).IndexOfAny(['.', ' ']) < 0) last = start;
                int end = start + length;
                while (end > last && !ScriptParser.IsWordChar(_code[end - 1])) end--;
                return finder.Find(last, end - last).FirstOrDefault(s => s.Kind is SymbolKind.Field or SymbolKind.Local or SymbolKind.Parameter)?.Type is { Length: > 0 } t ? t : null;
            }
            // a call: Name(...) or a.b.Name(...), its last part
            if (e.EndsWith(')') && Regex.Match(e, @"^[\w.]*?(\w+)\s*\(") is { Success: true })
            {
                int close = start + _code.Substring(start, length).LastIndexOf(')');
                int open = MatchingOpen(close);
                if (open < 0) return null;
                int nameEnd = open;
                while (nameEnd > start && char.IsWhiteSpace(_code[nameEnd - 1])) nameEnd--;
                int nameStart = nameEnd;
                while (nameStart > start && ScriptParser.IsWordChar(_code[nameStart - 1])) nameStart--;
                // the call must be the whole expression, not its first part: Foo().Bar is Bar's
                if (nameStart == nameEnd || close != start + _code.Substring(start, length).TrimEnd().Length - 1) return null;
                string? returns = finder.Find(nameStart, nameEnd - nameStart).FirstOrDefault(s => s.Kind is SymbolKind.Function)?.Type;
                return returns is { Length: > 0 } && returns != "void" ? returns : null;
            }
            return null;
        }

        private int MatchingOpen(int close)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (_code[i] == ')') depth++;
                else if (_code[i] == '(' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>A name for a value from its expression: GetHealth() is health, player.target is target.</summary>
        private static string NameFor(string expr)
        {
            var words = Regex.Matches(expr, @"[A-Za-z_]\w*").Select(m => m.Value).Where(w => w is not ("this" or "theGame" or "thePlayer")).ToList();
            string pick = words.Count > 0 ? words[^1] : "value";
            if (Regex.Match(expr, @"(\w+)\s*\(") is { Success: true } call) pick = call.Groups[1].Value;
            pick = Regex.Replace(pick, @"^(Get|Is|Has|Find|Calc|Calculate)(?=[A-Z])", "");
            if (pick.Length == 0) pick = "value";
            return char.ToLowerInvariant(pick[0]) + pick[1..];
        }

        /// <summary>A name not used in the function yet: the one asked for, else with a number after it.</summary>
        private string FreeName(FunctionScope fn, string name)
        {
            string body = _code[fn.ParamsStart..fn.BodyEnd];
            bool Taken(string n) => Regex.IsMatch(body, $@"\b{Regex.Escape(n)}\b", RegexOptions.IgnoreCase);
            if (!Taken(name)) return name;
            // a name the expression's own call has (MaxF) reads better as maxFValue than maxF2
            if (!Taken(name + "Value")) return name + "Value";
            string candidate = name;
            for (int n = 2; Taken(candidate); n++) candidate = name + n;
            return candidate;
        }

        // ------------------------------------------------------------ extract to function or method

        public Refactor? ExtractFunction(int start, int length, bool method)
        {
            if (FunctionAt(start) is not { } fn || start + length > fn.BodyEnd) return null;
            // whole statements: from the start of a line to after a ; or } at the end of one
            int from = LineStart(NextCode(start));
            int lastChar = start + length - 1;
            while (lastChar > from && char.IsWhiteSpace(_code[lastChar])) lastChar--;
            if (_code[lastChar] is not (';' or '}')) return null;
            int to = LineEnd(lastChar);
            string block = _code[from..to];
            if (!Balanced(block) || block.Count(c => c == '{') != block.Count(c => c == '}')) return null;
            if (Regex.IsMatch(block, @"\b(return|var)\b")) return null;
            if (Regex.IsMatch(block, @"\b(break|continue)\b") && !Regex.IsMatch(block, @"\b(for|while|do|switch)\b")) return null;
            var vars = VarLines(fn);
            if (vars.Count > 0 && from < vars[^1].End) return null;
            if (method && fn.Symbol.Owner is null) return null;

            // the function's locals and parameters the statements use: passed in, or out when they're changed and read after
            var locals = LocalsOf(fn);
            var used = locals.Where(l => Mentions(block, l.Name)).ToList();
            var after = _code[to..fn.BodyEnd];
            var before = _code[fn.BodyStart..from];
            var parameters = new List<string>();
            var arguments = new List<string>();
            var moved = new List<string>();
            foreach (var local in used)
            {
                bool assigned = Regex.IsMatch(block, $@"\b{Regex.Escape(local.Name)}\b\s*(?:[+\-*/]?=(?!=)|\+\+|--)") || Regex.IsMatch(block, $@"\b{Regex.Escape(local.Name)}\s*\.\s*(PushBack|Clear|Erase|Insert|Remove|Resize|Grow|PopBack)\b");
                bool readAfter = Mentions(after, local.Name);
                bool setBefore = local.Kind == SymbolKind.Parameter || Mentions(before.Replace(_code[local.Offset..(local.Offset + local.Name.Length)], ""), local.Name);
                if (!readAfter && !setBefore && local.Kind == SymbolKind.Local)
                {
                    // used only here: declared in the new function instead
                    moved.Add($"\tvar {local.Name} : {local.Type};\n"); // loc-ignore
                    continue;
                }
                parameters.Add($"{(assigned && readAfter ? "out " : "")}{local.Name} : {local.Type}");
                arguments.Add(local.Name);
            }
            if (used.Any(l => string.IsNullOrEmpty(l.Type))) return null;
            // a plain function can't use the class's members
            if (!method && UsesMembers(fn, from, to)) return null;

            string name = FreeTopName(method ? "NewMethod" : "NewFunction", fn.Symbol.Owner);
            string indent = IndentAt(from);
            string body = Reindent(text[from..to], indent, "\t");
            // a method goes after the function it came from; a function after the class it was in, at the top level
            var type = method ? null : doc.TypeAt(fn.Symbol.Offset);
            string declIndent = type is null ? IndentAt(fn.Symbol.Offset) : "";
            string signature = $"{(method ? "private " : "")}function {name}({string.Join(", ", parameters)})"; // loc-ignore
            string newFunction = $"\n{declIndent}{signature}\n{declIndent}{{\n{Reindent(string.Concat(moved), "\t", declIndent + "\t")}{(moved.Count > 0 ? "\n" : "")}{Reindent(body, "\t", declIndent + "\t")}{declIndent}}}\n";
            int fnEnd = type is null ? LineEnd(fn.BodyEnd) : LineEnd(type.End);
            var edits = new List<TextEdit>
            {
                new(from, to - from, $"{indent}{name}({string.Join(", ", arguments)});\n"),
                new(fnEnd, 0, newFunction),
            };
            // the moved locals' declarations go from the original function
            foreach (var local in used.Where(l => moved.Any(m => m.Contains($"var {l.Name} :"))))
                if (VarLine(fn, local) is { } line) edits.Add(line);
            return new Refactor(method ? RefactorKind.ExtractMethod : RefactorKind.ExtractFunction, edits,
                After(edits.Where(e => e.Offset != from), from + indent.Length));
        }

        /// <summary>A function's parameters and locals, with their types.</summary>
        private List<ScriptSymbol> LocalsOf(FunctionScope fn)
        {
            var result = new List<ScriptSymbol>();
            foreach (var m in Regex.Matches(_code[fn.ParamsStart..fn.ParamsEnd], @"(?:\b(?:out|optional)\s+)*(?<name>\w+)(?:\s*,\s*(?<name>\w+))*\s*:\s*(?<type>\w+(?:\s*<[^>;]*>)?)").Cast<Match>())
                foreach (Capture c in m.Groups["name"].Captures)
                    result.Add(new ScriptSymbol(c.Value, SymbolKind.Parameter, null, ScriptParser.Squash(m.Groups["type"].Value), doc.File, fn.ParamsStart + c.Index, 0));
            foreach (var m in Regex.Matches(_code[fn.BodyStart..fn.BodyEnd], @"\bvar\s+(?<name>\w+)(?:\s*,\s*(?<name>\w+))*\s*:\s*(?<type>\w+(?:\s*<[^>;]*>)?)").Cast<Match>())
                foreach (Capture c in m.Groups["name"].Captures)
                    result.Add(new ScriptSymbol(c.Value, SymbolKind.Local, null, ScriptParser.Squash(m.Groups["type"].Value), doc.File, fn.BodyStart + c.Index, 0));
            return result;
        }

        /// <summary>Whether code names a variable (not as a member after a dot).</summary>
        private static bool Mentions(string code, string name) => Regex.IsMatch(code, $@"(?<![\w.])\s*\b{Regex.Escape(name)}\b(?!\s*\()", RegexOptions.IgnoreCase)
            && Regex.Matches(code, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase).Any(m => m.Index == 0 || code[..m.Index].TrimEnd() is var b && (b.Length == 0 || b[^1] != '.'));

        /// <summary>Whether statements use the class's members without this: a field or method of the class.</summary>
        private bool UsesMembers(FunctionScope fn, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                if (!ScriptParser.IsWordStart(_code[i]) || i > 0 && ScriptParser.IsWordChar(_code[i - 1])) continue;
                int end = i;
                while (end < to && ScriptParser.IsWordChar(_code[end])) end++;
                string word = _code[i..end];
                if (word is "this" or "super" or "parent" or "virtual_parent") return true;
                int before = i - 1;
                while (before >= 0 && char.IsWhiteSpace(_code[before])) before--;
                if (before >= 0 && _code[before] == '.') { i = end; continue; }
                if (fn.Symbol.Owner is not null && finder.Find(i, end - i).Any(s => s.IsMember && s.Owner is not null)) return true;
                i = end;
            }
            return false;
        }

        /// <summary>A name for a new function that the class (or the script, for a global one) hasn't got.</summary>
        private string FreeTopName(string name, string? owner)
        {
            string candidate = name;
            for (int n = 2; doc.Symbols.Any(s => s.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) && (owner is null || s.Owner == owner)); n++) candidate = name + n;
            return candidate;
        }

        /// <summary>Lines with one indent at their start given another: the extracted code's, moved into a function of its own.</summary>
        private static string Reindent(string lines, string from, string to)
        {
            var sb = new StringBuilder();
            foreach (string line in lines.Split('\n'))
            {
                if (line.Length == 0) { sb.Append('\n'); continue; }
                sb.Append(line.StartsWith(from, StringComparison.Ordinal) ? to + line[from.Length..] : line).Append('\n');
            }
            string result = sb.ToString();
            return lines.EndsWith('\n') ? result[..^1] : result.TrimEnd('\n');
        }

        /// <summary>The edit that takes a local's declaration out: its line, or its name from a var list.</summary>
        private TextEdit? VarLine(FunctionScope fn, ScriptSymbol local)
        {
            foreach (var (s, e) in VarLines(fn))
            {
                if (local.Offset < s || local.Offset >= e) continue;
                string line = _code[s..e];
                var names = Regex.Match(line, @"\bvar\s+(?<names>[\w\s,]+?)\s*:").Groups["names"].Value.Split(',').Select(n => n.Trim()).ToList();
                if (names.Count <= 1) return new TextEdit(s, e - s, "");
                int at = local.Offset;
                // the name and the comma beside it
                int commaAfter = _code.IndexOf(',', at);
                int colon = _code.IndexOf(':', at);
                if (commaAfter >= 0 && commaAfter < colon)
                {
                    int next = NextCode(commaAfter + 1);
                    return new TextEdit(at, next - at, "");
                }
                int commaBefore = _code.LastIndexOf(',', at);
                return new TextEdit(commaBefore, at + local.Name.Length - commaBefore, "");
            }
            return null;
        }

        // ------------------------------------------------------------ inline variable

        public Refactor? InlineVariable(int at)
        {
            if (FunctionAt(at) is not { } fn) return null;
            if (finder.WordAt(at) is not { } word) return null;
            var local = LocalsOf(fn).FirstOrDefault(l => l.Kind == SymbolKind.Local && l.Name.Equals(_code.Substring(word.Start, word.Length), StringComparison.OrdinalIgnoreCase));
            if (local is null) return null;
            string name = local.Name;
            var body = _code[fn.BodyStart..fn.BodyEnd];
            // given its value once, in a statement of its own, and never changed after
            var assigns = Regex.Matches(body, $@"(?<![\w.])\b{Regex.Escape(name)}\s*=(?!=)\s*(?<value>[^;]+);", RegexOptions.IgnoreCase);
            if (assigns.Count != 1) return null;
            if (Regex.IsMatch(body, $@"\b{Regex.Escape(name)}\s*(?:[+\-*/]=|\+\+|--)|\b{Regex.Escape(name)}\s*\.\s*(PushBack|Clear|Erase|Insert|Remove|Resize|Grow|PopBack)\b", RegexOptions.IgnoreCase)) return null;
            if (Regex.IsMatch(_code[fn.ParamsStart..fn.BodyEnd], $@"\bout\s+{Regex.Escape(name)}\b")) return null;
            var assign = assigns[0];
            int assignAt = fn.BodyStart + assign.Index;
            if (StatementAt(assignAt + 1, fn) is not { } statement || statement.Start != assignAt) return null;
            string value = text.Substring(fn.BodyStart + assign.Groups["value"].Index, assign.Groups["value"].Length).Trim();
            // an assignment inside a loop or a branch runs more than once, or not at all
            int depth = 0;
            for (int i = fn.BodyStart + 1; i < assignAt; i++) depth += _code[i] == '{' ? 1 : _code[i] == '}' ? -1 : 0;
            if (depth != 0) return null;
            var uses = Regex.Matches(_code[statement.End..fn.BodyEnd], $@"(?<![\w.])\b{Regex.Escape(name)}\b(?!\s*\()", RegexOptions.IgnoreCase)
                .Select(m => statement.End + m.Index).ToList();
            if (uses.Count == 0) return null;
            // a value with a call in it would be run once per use instead of once
            bool simple = !value.Contains('(') || uses.Count == 1;
            if (!simple) return null;
            string inserted = Regex.IsMatch(value, @"^[\w.'""]+$") ? value : $"({value})";
            var edits = uses.Select(u => new TextEdit(u, name.Length, inserted)).ToList();
            edits.Add(new TextEdit(LineStart(statement.Start), LineEnd(statement.End - 1) - LineStart(statement.Start), ""));
            if (VarLine(fn, local) is { } decl) edits.Add(decl);
            return new Refactor(RefactorKind.InlineVariable, edits);
        }

        // ------------------------------------------------------------ split / join a declaration

        /// <summary>var x : int = 5; into var x : int; and x = 5; after the function's vars.</summary>
        public Refactor? SplitDeclaration(int at)
        {
            if (FunctionAt(at) is not { } fn) return null;
            var vars = VarLines(fn);
            var line = vars.FirstOrDefault(v => at >= v.Start && at < v.End);
            if (line == default) return null;
            var m = Regex.Match(_code[line.Start..line.End], @"\bvar\s+(?<name>\w+)\s*:\s*(?<type>[^=;]+?)\s*=\s*(?<value>[^;]+);");
            if (!m.Success) return null;
            string value = text.Substring(line.Start + m.Groups["value"].Index, m.Groups["value"].Length).Trim();
            int eq = line.Start + m.Groups["type"].Index + m.Groups["type"].Length;
            int semi = line.Start + m.Index + m.Length - 1;
            string indent = IndentAt(line.Start);
            var (after, _) = VarInsertPoint(fn);
            return new Refactor(RefactorKind.SplitDeclaration,
            [
                new TextEdit(eq, semi - eq, ""),
                new TextEdit(after, 0, $"{indent}{m.Groups["name"].Value} = {value};\n"),
            ]);
        }

        /// <summary>
        /// var x : int; and the x = 5; straight after the vars, into var x : int = 5;. Only for the last var, so the value
        /// is still worked out after every other var has its own, as it was.
        /// </summary>
        public Refactor? JoinDeclaration(int at)
        {
            if (FunctionAt(at) is not { } fn) return null;
            var vars = VarLines(fn);
            if (vars.Count == 0) return null;
            var last = vars[^1];
            var m = Regex.Match(_code[last.Start..last.End], @"\bvar\s+(?<name>\w+)\s*:\s*(?<type>[^=;]+?)\s*;");
            if (!m.Success) return null;
            string name = m.Groups["name"].Value;
            int first = NextCode(last.End);
            var a = Regex.Match(_code[first..fn.BodyEnd], $@"^{Regex.Escape(name)}\s*=(?!=)\s*(?<value>[^;]+);");
            if (!a.Success) return null;
            bool onVar = at >= last.Start && at < last.End;
            bool onAssign = at >= first && at <= first + a.Length;
            if (!onVar && !onAssign) return null;
            string value = text.Substring(first + a.Groups["value"].Index, a.Groups["value"].Length).Trim();
            int typeEnd = last.Start + m.Groups["type"].Index + m.Groups["type"].Length;
            return new Refactor(RefactorKind.JoinDeclaration,
            [
                new TextEdit(typeEnd, 0, $" = {value}"),
                new TextEdit(LineStart(first), LineEnd(first + a.Length - 1) - LineStart(first), ""),
            ]);
        }

        // ------------------------------------------------------------ switch and if/else layout

        /// <summary>The switch whose header or cases the offset is on: where its { and } are.</summary>
        private (int Open, int Close)? SwitchAt(int at, FunctionScope fn)
        {
            for (var m = SwitchHeader().Match(_code, fn.BodyStart); m.Success && m.Index < fn.BodyEnd; m = m.NextMatch())
            {
                int open = _code.IndexOf('{', m.Index + m.Length);
                if (open < 0) break;
                int close = MatchingClose(open);
                if (close < 0) continue;
                if (at >= m.Index && at <= close) return (open, close);
            }
            return null;
        }

        private int MatchingClose(int open)
        {
            int depth = 0;
            for (int i = open; i < _code.Length; i++)
            {
                if (_code[i] == '{') depth++;
                else if (_code[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        public Refactor? Switch(int at, bool collapse)
        {
            if (FunctionAt(at) is not { } fn || SwitchAt(at, fn) is not { } sw) return null;
            string inner = text[(sw.Open + 1)..sw.Close];
            string innerCode = _code[(sw.Open + 1)..sw.Close];
            // a case with a block of its own, or a nested switch, is left as it is: only flat cases change
            if (innerCode.Contains('{')) return null;
            var labels = CaseLabel().Matches(innerCode).Cast<Match>().ToList();
            if (labels.Count == 0) return null;
            string caseIndent = IndentAt(sw.Open + 1 + labels[0].Index);
            var sb = new StringBuilder("\n");
            bool changed = false;
            for (int i = 0; i < labels.Count; i++)
            {
                int bodyStart = labels[i].Index + labels[i].Length;
                int bodyEnd = i + 1 < labels.Count ? labels[i + 1].Index : inner.Length;
                string label = inner.Substring(labels[i].Index, labels[i].Length).Trim();
                var statements = Statements(inner[bodyStart..bodyEnd], innerCode[bodyStart..bodyEnd]);
                if (collapse)
                {
                    if (statements.Count == 0) sb.Append(caseIndent).Append(label).Append('\n');
                    else sb.Append(caseIndent).Append(label).Append(' ').Append(string.Join(" ", statements)).Append('\n');
                    if (inner[bodyStart..bodyEnd].Contains('\n') && inner[bodyStart..bodyEnd].Trim().Contains('\n') || statements.Count > 0 && inner[labels[i].Index..bodyEnd].Trim().Contains('\n')) changed = true;
                }
                else
                {
                    sb.Append(caseIndent).Append(label).Append('\n');
                    foreach (string s in statements) sb.Append(caseIndent).Append('\t').Append(s).Append('\n');
                    if (statements.Count > 0 && !inner[bodyStart..bodyEnd].TrimStart(' ', '\t').StartsWith('\n') && !inner[bodyStart..bodyEnd].TrimStart(' ', '\t').StartsWith("\r\n")) changed = true;
                }
            }
            if (!changed) return null;
            sb.Append(IndentAt(sw.Close));
            return new Refactor(collapse ? RefactorKind.CollapseSwitch : RefactorKind.ExpandSwitch, [new TextEdit(sw.Open + 1, sw.Close - sw.Open - 1, sb.ToString())]);
        }

        /// <summary>The statements in a case's body, each with its ; and its comments as typed.</summary>
        private static List<string> Statements(string raw, string code)
        {
            var list = new List<string>();
            int start = 0, depth = 0;
            for (int i = 0; i < code.Length; i++)
            {
                if (code[i] is '(' or '[') depth++;
                else if (code[i] is ')' or ']') depth--;
                else if (code[i] == ';' && depth == 0)
                {
                    list.Add(Regex.Replace(raw[start..(i + 1)].Trim(), @"\s*\r?\n\s*", " "));
                    start = i + 1;
                }
            }
            return list;
        }

        /// <summary>
        /// An if/else if/else chain of single statements, laid out one branch a line (if (a) x = 1;) or each statement on
        /// a line of its own under its branch. A branch with braces is left alone.
        /// </summary>
        public Refactor? If(int at, bool collapse)
        {
            if (FunctionAt(at) is not { } fn) return null;
            int lineStart = LineStart(at);
            int first = NextCode(lineStart);
            if (!IsWordAt(first, "if")) return null;
            // the chain: if (c) s; [else if (c) s;]* [else s;]
            var parts = new List<(string Head, string Statement)>();
            int i = first;
            while (true)
            {
                string head;
                if (IsWordAt(i, "if") || IsWordAt(i, "else") && IsWordAt(NextCode(i + 4), "if"))
                {
                    int ifAt = IsWordAt(i, "if") ? i : NextCode(i + 4);
                    int open = NextCode(ifAt + 2);
                    if (open >= _code.Length || _code[open] != '(') return null;
                    int close = MatchingCloseParen(open);
                    if (close < 0) return null;
                    head = (ifAt == i ? "" : "else ") + "if " + Regex.Replace(text[open..(close + 1)], @"\s*\r?\n\s*", " ");
                    i = close + 1;
                }
                else if (IsWordAt(i, "else"))
                {
                    head = "else";
                    i += 4;
                }
                else break;
                int s = NextCode(i);
                if (s >= fn.BodyEnd || _code[s] == '{') return null;
                int semi = s, depth = 0;
                for (; semi < fn.BodyEnd; semi++)
                {
                    if (_code[semi] is '(' or '[') depth++;
                    else if (_code[semi] is ')' or ']') depth--;
                    else if (depth == 0 && _code[semi] == ';') break;
                    else if (_code[semi] is '{' or '}') return null;
                }
                if (semi >= fn.BodyEnd) return null;
                parts.Add((head, Regex.Replace(text[s..(semi + 1)], @"\s*\r?\n\s*", " ")));
                i = NextCode(semi + 1);
                if (head == "else") break;
            }
            if (parts.Count == 0) return null;
            int end = LineEnd(i > 0 ? _code.LastIndexOf(';', i - 1) : i);
            string indent = IndentAt(first);
            var sb = new StringBuilder();
            foreach (var (head, statement) in parts)
                sb.Append(indent).Append(head).Append(collapse ? " " : "\n" + indent + "\t").Append(statement).Append('\n');
            string replacement = sb.ToString();
            string original = text[lineStart..end];
            if (Regex.Replace(original, @"\s+", " ").Trim() == Regex.Replace(replacement, @"\s+", " ").Trim() && original.Replace("\r", "") == replacement) return null;
            bool alreadyThatWay = collapse ? parts.All(p => original.Contains(p.Head + " " + p.Statement)) : !parts.Any(p => original.Contains(p.Head + " " + p.Statement));
            if (alreadyThatWay) return null;
            return new Refactor(collapse ? RefactorKind.CollapseIf : RefactorKind.ExpandIf, [new TextEdit(lineStart, end - lineStart, replacement)]);
        }

        private int MatchingCloseParen(int open)
        {
            int depth = 0;
            for (int i = open; i < _code.Length; i++)
            {
                if (_code[i] == '(') depth++;
                else if (_code[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }
    }

    [GeneratedRegex(@"\bswitch\s*\(")]
    private static partial Regex SwitchHeader();

    [GeneratedRegex(@"\b(?:case\s+[^:;{}]+|default)\s*:")]
    private static partial Regex CaseLabel();
}
