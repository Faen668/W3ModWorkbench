namespace W3ModWorkbench.Core.Scripts.Navigation;

/// <summary>Declarations from a set of scripts, looked up by name. Case is ignored, so a call spelled with different capitals still finds its declaration.</summary>
public sealed class ScriptIndex
{
    public static readonly ScriptIndex Empty = new([]);

    private readonly Dictionary<string, List<ScriptSymbol>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScriptSymbol> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ScriptSymbol> _all = [];
    private readonly List<ScriptFile> _files = [];

    public ScriptIndex(IEnumerable<ScriptSymbol> symbols)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in symbols)
        {
            if (!_byName.TryGetValue(s.Name, out var list)) _byName[s.Name] = list = [];
            list.Add(s);
            _all.Add(s);
            if (s.IsType) _types.TryAdd(s.TypeKey, s);
            if (files.Add(s.File.Key)) _files.Add(s.File);
        }
    }

    public int Count => _byName.Count;

    /// <summary>Every declaration, in file order.</summary>
    public IReadOnlyList<ScriptSymbol> All => _all;

    /// <summary>The files the declarations are in, in the order they were read.</summary>
    public IReadOnlyList<ScriptFile> Files => _files;

    public IReadOnlyList<ScriptSymbol> Named(string name) =>
        _byName.TryGetValue(name, out var list) ? list : [];

    public ScriptSymbol? Type(string key) => _types.GetValueOrDefault(key);
}
