namespace W3BundleExplorer.Core.Scripts.Navigation;

/// <summary>Declarations from a set of scripts, looked up by name. Case is ignored, so a call spelled with different capitals still finds its declaration.</summary>
public sealed class ScriptIndex
{
    public static readonly ScriptIndex Empty = new([]);

    private readonly Dictionary<string, List<ScriptSymbol>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScriptSymbol> _types = new(StringComparer.OrdinalIgnoreCase);

    public ScriptIndex(IEnumerable<ScriptSymbol> symbols)
    {
        foreach (var s in symbols)
        {
            if (!_byName.TryGetValue(s.Name, out var list)) _byName[s.Name] = list = [];
            list.Add(s);
            if (s.IsType) _types.TryAdd(s.TypeKey, s);
        }
    }

    public int Count => _byName.Count;

    public IReadOnlyList<ScriptSymbol> Named(string name) =>
        _byName.TryGetValue(name, out var list) ? list : [];

    public ScriptSymbol? Type(string key) => _types.GetValueOrDefault(key);
}
