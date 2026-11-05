namespace W3ModWorkbench.Core.Scripts.Standalone;

/// <summary>
/// An LALR(1) parser built from a written-out grammar, resolving conflicts and choosing default reductions the way the
/// parser generator behind wcc_lite does, because those choices decide when a mistake is noticed and what the message
/// says it expected. Built once per grammar, the first time it's needed.
/// </summary>
internal sealed class LalrGrammar
{
    private const string Accept = "$accept", End = "$end", ErrorToken = "error", Empty = "%empty";

    private readonly record struct Rule(int Left, int[] Right);

    /// <summary>Symbols: terminals first, then nonterminals.</summary>
    private readonly List<string> _symbols = [];
    private readonly Dictionary<string, int> _ids = [];
    private readonly List<Rule> _rules = [];
    private int _terminalCount;

    /// <summary>What a state does on each terminal: above 0 shift to state n-1, below 0 reduce by rule -n, 0 fall to the default.</summary>
    internal int[][] Actions = [];
    /// <summary>Each state's default rule, or 0 if a terminal with no action is an error.</summary>
    internal int[] Defaults = [];
    /// <summary>Whether a state has any action of its own: without one it reduces without reading a token.</summary>
    internal bool[] HasActions = [];
    internal Dictionary<int, int>[] Gotos = [];
    internal int ErrorId, EndId, UndefinedId;
    internal int TerminalCount => _terminalCount;

    internal string SymbolName(int id) => _symbols[id];
    internal int RuleLength(int rule) => _rules[rule].Right.Length;
    internal int RuleLeft(int rule) => _rules[rule].Left;
    internal int StateCount => Actions.Length;

    /// <summary>A terminal's number, or $undefined for what the grammar doesn't know.</summary>
    internal int Terminal(string name) => _ids.TryGetValue(name, out int id) && id < _terminalCount ? id : UndefinedId;

    internal int RuleNamed(string left, string right)
    {
        int l = _ids[left];
        string want = right.Length == 0 ? "" : right;
        for (int r = 1; r < _rules.Count; r++)
            if (_rules[r].Left == l && string.Join(' ', _rules[r].Right.Select(SymbolName)) == want) return r;
        throw new InvalidOperationException(left);
    }

    internal LalrGrammar(string tokens, string rules)
    {
        foreach (string t in new[] { End, ErrorToken, "$undefined" }) Add(t);
        foreach (string t in tokens.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) Add(t);
        _terminalCount = _symbols.Count;
        EndId = 0;
        ErrorId = 1;
        UndefinedId = 2;
        Add(Accept);

        var lines = rules.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string line in lines) Add(line[..line.IndexOf(':')]);
        _rules.Add(default);
        string first = lines[0][..lines[0].IndexOf(':')];
        _rules.Add(new Rule(_ids[Accept], [_ids[first], EndId]));
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            int left = _ids[line[..colon]];
            foreach (string alternative in line[(colon + 1)..].Split(" | "))
            {
                var right = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(s => s != Empty).Select(s => _ids[s]).ToArray();
                _rules.Add(new Rule(left, right));
            }
        }
        Build();
    }

    private void Add(string symbol)
    {
        if (_ids.ContainsKey(symbol)) return;
        _ids[symbol] = _symbols.Count;
        _symbols.Add(symbol);
    }

    private readonly record struct Item(int Rule, int Dot);

    private void Build()
    {
        int count = _symbols.Count;
        var byLeft = new List<int>[count];
        for (int i = 0; i < count; i++) byLeft[i] = [];
        for (int r = 1; r < _rules.Count; r++) byLeft[_rules[r].Left].Add(r);

        // which symbols can vanish, and what can begin each
        var nullable = new bool[count];
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int r = 1; r < _rules.Count; r++)
                if (!nullable[_rules[r].Left] && _rules[r].Right.All(s => nullable[s]))
                    changed = nullable[_rules[r].Left] = true;
        }
        var firsts = new HashSet<int>[count];
        for (int i = 0; i < count; i++) firsts[i] = i < _terminalCount ? [i] : [];
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int r = 1; r < _rules.Count; r++)
                foreach (int s in _rules[r].Right)
                {
                    int before = firsts[_rules[r].Left].Count;
                    firsts[_rules[r].Left].UnionWith(firsts[s]);
                    changed |= firsts[_rules[r].Left].Count != before;
                    if (!nullable[s]) break;
                }
        }

        // the LR(0) states, by their kernels
        var kernels = new List<Item[]>();
        var transitions = new List<Dictionary<int, int>>();
        var known = new Dictionary<string, int>();
        int State(IEnumerable<Item> kernel)
        {
            var sorted = kernel.OrderBy(i => i.Rule).ThenBy(i => i.Dot).ToArray();
            string key = string.Join(',', sorted.Select(i => i.Rule * 100 + i.Dot));
            if (known.TryGetValue(key, out int s)) return s;
            known[key] = kernels.Count;
            kernels.Add(sorted);
            transitions.Add([]);
            return kernels.Count - 1;
        }
        List<Item> Closure(IEnumerable<Item> kernel)
        {
            var result = new List<Item>(kernel);
            var added = new HashSet<int>();
            for (int i = 0; i < result.Count; i++)
            {
                var item = result[i];
                var right = _rules[item.Rule].Right;
                if (item.Dot < right.Length && right[item.Dot] >= _terminalCount && added.Add(right[item.Dot]))
                    foreach (int r in byLeft[right[item.Dot]]) result.Add(new Item(r, 0));
            }
            return result;
        }
        State([new Item(1, 0)]);
        for (int s = 0; s < kernels.Count; s++)
        {
            var groups = new SortedDictionary<int, List<Item>>();
            foreach (var item in Closure(kernels[s]))
            {
                var right = _rules[item.Rule].Right;
                if (item.Dot >= right.Length) continue;
                if (!groups.TryGetValue(right[item.Dot], out var group)) groups[right[item.Dot]] = group = [];
                group.Add(item with { Dot = item.Dot + 1 });
            }
            foreach (var (symbol, group) in groups)
                transitions[s][symbol] = State(group);
        }

        // lookaheads: worked out for kernel items by closing each over a marker, then passed along until nothing changes
        const int Marker = -1;
        var look = new Dictionary<(int State, Item Item), HashSet<int>>();
        HashSet<int> Look(int s, Item i)
        {
            if (!look.TryGetValue((s, i), out var set)) look[(s, i)] = set = [];
            return set;
        }
        var passes = new Dictionary<(int, Item), List<(int, Item)>>();
        List<(Item Item, HashSet<int> Ahead)> ClosureWith(Item start, HashSet<int> ahead)
        {
            var map = new Dictionary<Item, HashSet<int>> { [start] = [.. ahead] };
            var queue = new Queue<Item>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                var right = _rules[item.Rule].Right;
                if (item.Dot >= right.Length || right[item.Dot] < _terminalCount) continue;
                var next = new HashSet<int>();
                bool all = true;
                for (int k = item.Dot + 1; k < right.Length; k++)
                {
                    next.UnionWith(firsts[right[k]]);
                    if (!nullable[right[k]]) { all = false; break; }
                }
                if (all) next.UnionWith(map[item]);
                foreach (int r in byLeft[right[item.Dot]])
                {
                    var child = new Item(r, 0);
                    bool fresh = !map.TryGetValue(child, out var set);
                    if (fresh) map[child] = set = [];
                    int before = set!.Count;
                    set.UnionWith(next);
                    if (fresh || set.Count != before) queue.Enqueue(child);
                }
            }
            return [.. map.Select(p => (p.Key, p.Value))];
        }
        Look(0, new Item(1, 0));
        for (int s = 0; s < kernels.Count; s++)
        {
            foreach (var kernel in kernels[s])
                foreach (var (item, ahead) in ClosureWith(kernel, [Marker]))
                {
                    var right = _rules[item.Rule].Right;
                    if (item.Dot >= right.Length) continue;
                    int target = transitions[s][right[item.Dot]];
                    var moved = item with { Dot = item.Dot + 1 };
                    foreach (int a in ahead)
                    {
                        if (a == Marker)
                        {
                            if (!passes.TryGetValue((s, kernel), out var list)) passes[(s, kernel)] = list = [];
                            list.Add((target, moved));
                        }
                        else Look(target, moved).Add(a);
                    }
                }
        }
        var work = new Queue<(int, Item)>(look.Keys);
        var queued = new HashSet<(int, Item)>(look.Keys);
        while (work.Count > 0)
        {
            var at = work.Dequeue();
            queued.Remove(at);
            if (!passes.TryGetValue(at, out var list)) continue;
            var from = Look(at.Item1, at.Item2);
            foreach (var to in list)
            {
                var set = Look(to.Item1, to.Item2);
                int before = set.Count;
                set.UnionWith(from);
                if (set.Count != before && queued.Add(to)) work.Enqueue(to);
            }
        }

        // the tables: reductions (lowest rule first on a tie), then shifts over them, then the commonest reduction as the default
        int states = kernels.Count;
        Actions = new int[states][];
        Defaults = new int[states];
        HasActions = new bool[states];
        Gotos = new Dictionary<int, int>[states];
        for (int s = 0; s < states; s++)
        {
            var row = new int[_terminalCount];
            var reductions = new SortedDictionary<int, HashSet<int>>();
            foreach (var kernel in kernels[s])
                foreach (var (item, ahead) in ClosureWith(kernel, Look(s, kernel)))
                    if (item.Dot >= _rules[item.Rule].Right.Length)
                    {
                        if (!reductions.TryGetValue(item.Rule, out var set)) reductions[item.Rule] = set = [];
                        set.UnionWith(ahead);
                    }
            foreach (var (rule, ahead) in reductions.Reverse())
                foreach (int a in ahead)
                    if (a >= 0) row[a] = -rule;
            Gotos[s] = [];
            bool shiftsError = false;
            foreach (var (symbol, target) in transitions[s])
            {
                if (symbol < _terminalCount)
                {
                    row[symbol] = target + 1;
                    shiftsError |= symbol == ErrorId;
                }
                else Gotos[s][symbol] = target;
            }
            int best = reductions.ContainsKey(1) ? 1 : 0, most = 0;
            if (!shiftsError)
                foreach (int rule in reductions.Keys)
                {
                    int uses = row.Count(a => a == -rule);
                    if (uses > most)
                    {
                        most = uses;
                        best = rule;
                    }
                }
            if (best != 0)
                for (int t = 0; t < row.Length; t++)
                    if (row[t] == -best) row[t] = 0;
            Actions[s] = row;
            Defaults[s] = best;
            HasActions[s] = row.Any(a => a != 0);
        }
    }
}

/// <summary>
/// Runs a grammar over tokens as wcc_lite's parser does: the same moment of noticing a mistake, the same message, the
/// same recovery (a grammar with a rule for it picks up again after the next semicolon), and the same fixed-size stack,
/// which is what "memory exhausted" means.
/// </summary>
internal sealed class WccParserRun(LalrGrammar grammar, int stackLimit, List<WccMessage> messages)
{
    private const int Empty = -2;

    public Func<WccToken?> Next = () => null;
    /// <summary>Called as a rule is reduced, before it comes off the stack.</summary>
    public Action<int>? OnReduce;

    private WccToken? _last;
    private int _lookahead = Empty;

    public WccToken? Last => _last;
    public void DropLookahead() => _lookahead = Empty;
    public void Report(string message) => messages.Add(new WccMessage(_last?.Line ?? 1, $"{message}, near '{_last?.Text ?? ""}'"));

    /// <summary>False when the parse stopped short (it can't recover, or ran out of stack); messages have been added either way.</summary>
    public bool Parse()
    {
        var stack = new List<int>(stackLimit) { 0 };
        int errorStatus = 0;
        while (true)
        {
            int state = stack[^1];
            int action = 0;
            bool reduce = false;
            if (grammar.HasActions[state])
            {
                if (_lookahead == Empty) Read();
                action = grammar.Actions[state][_lookahead];
            }
            if (action > 0)
            {
                if (errorStatus > 0) errorStatus--;
                _lookahead = Empty;
                if (!Push(stack, action - 1)) return false;
                continue;
            }
            if (action < 0)
            {
                action = -action;
                reduce = true;
            }
            else if (grammar.Defaults[state] != 0)
            {
                action = grammar.Defaults[state];
                reduce = true;
            }
            if (reduce)
            {
                if (action == 1) return true;
                OnReduce?.Invoke(action);
                int length = grammar.RuleLength(action);
                stack.RemoveRange(stack.Count - length, length);
                if (!Push(stack, grammar.Gotos[stack[^1]][grammar.RuleLeft(action)])) return false;
                continue;
            }

            // a mistake
            if (errorStatus == 0) Report(SyntaxMessage(state));
            if (errorStatus == 3)
            {
                if (_lookahead == grammar.EndId) return false;
                _lookahead = Empty;
            }
            errorStatus = 3;
            while (true)
            {
                int top = stack[^1];
                int recover = grammar.HasActions[top] ? grammar.Actions[top][grammar.ErrorId] : 0;
                if (recover > 0)
                {
                    if (!Push(stack, recover - 1)) return false;
                    break;
                }
                if (stack.Count == 1) return false;
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    private void Read()
    {
        var token = Next();
        if (token is null)
        {
            _lookahead = grammar.EndId;
            return;
        }
        _last = token;
        _lookahead = grammar.Terminal(token.Terminal);
    }

    private bool Push(List<int> stack, int state)
    {
        stack.Add(state);
        if (stack.Count < stackLimit) return true;
        Report("memory exhausted"); // loc-ignore
        return false;
    }

    private string SyntaxMessage(int state)
    {
        const int Listed = 4;
        var expected = new List<string>();
        if (grammar.HasActions[state])
            for (int t = 0; t < grammar.TerminalCount; t++)
                if (t != grammar.ErrorId && grammar.Actions[state][t] != 0)
                    expected.Add(grammar.SymbolName(t));
        string message = $"syntax error, unexpected {grammar.SymbolName(_lookahead)}"; // loc-ignore
        if (expected.Count is > 0 and <= Listed)
            message += ", expecting " + string.Join(" or ", expected); // loc-ignore
        return message;
    }
}
