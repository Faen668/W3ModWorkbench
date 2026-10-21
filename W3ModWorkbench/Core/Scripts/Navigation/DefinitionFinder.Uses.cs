namespace W3ModWorkbench.Core.Scripts.Navigation;

public sealed partial class DefinitionFinder
{
    /// <summary>
    /// Every place in this script that names the same thing as the word at <paramref name="start"/>: the uses that go
    /// to the same declaration, and the declaration itself when it's here. Another class's member, a local of the same
    /// name in another function, or the word in a comment, isn't one. Null when the word leads nowhere (it isn't
    /// declared anywhere searched), for the caller to fall back on the words spelt the same.
    /// </summary>
    public List<int>? Uses(int start, int length)
    {
        var wanted = Identity(start, length);
        if (wanted.Count == 0) return null;
        string word = _code.Substring(start, length);
        var found = new List<int>();
        for (int at = _code.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = _code.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = at + word.Length;
            if (at > 0 && ScriptParser.IsWordChar(_code[at - 1]) || end < _code.Length && ScriptParser.IsWordChar(_code[end])) continue;
            if (at == start || Identity(at, word.Length).Overlaps(wanted)) found.Add(at);
        }
        return found;
    }

    /// <summary>
    /// For a member after a dot (<c>a.b</c>), the type of what's before the dot, when it can be worked out; for a name
    /// written on its own inside a class, that class. Null otherwise.
    /// </summary>
    public string? OwnerOf(int start)
    {
        var fn = _doc.FunctionAt(start);
        string? self = fn?.Symbol.Owner ?? _doc.TypeAt(start)?.Symbol.TypeKey;
        int before = PrevNonSpace(start - 1);
        if (before >= 0 && _code[before] == '.') return TypeOf(before, fn, self, 0);
        return self;
    }

    /// <summary>
    /// What a word names, by declaration (file and place): the declaration itself when the word is one, else where it
    /// goes. Comments and strings are blanked in the code read, so a word in one is never found.
    /// </summary>
    private HashSet<(string, int)> Identity(int start, int length)
    {
        if (_doc.Symbols.FirstOrDefault(s => s.Offset == start) is { } declared) return [(declared.File.Key, declared.Offset)];
        return [.. Find(start, length).Select(s => (s.File.Key, s.Offset))];
    }
}
