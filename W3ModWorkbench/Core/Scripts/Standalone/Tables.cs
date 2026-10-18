// Part of the built-in compiler: a C# port of the .ws-to-rsblob compiler written by WebSpam for Aelto's tw3-cahirb (MIT).
// https://github.com/Aelto/tw3-cahirb/pull/1

namespace W3ModWorkbench.Core.Scripts.Standalone;

public enum TypeKind { Simple = 0, Enum = 1, Class = 2, Array = 3, Handle = 7, Fundamental = 11, Map = 12 }

/// <summary>
/// The name table and the type and function tables a blob's code links against. Everything is added the first
/// time it's asked for and has the same index from then on.
/// </summary>
public sealed class Tables
{
    public const int NoType = -1;

    public List<string> Strings { get; } = [];
    public List<(int Name, int Kind)> ImportTypes { get; } = [];
    public List<(int Name, int ScopeType)> ImportFunctions { get; } = [];

    private readonly Dictionary<string, int> _stringIndex = [];
    private readonly Dictionary<string, int> _typeIndex = [];
    private readonly Dictionary<(string, string?), int> _functionIndex = [];

    public int Name(string value)
    {
        if (_stringIndex.TryGetValue(value, out int found)) return found;
        int index = Strings.Count;
        Strings.Add(value);
        _stringIndex[value] = index;
        return index;
    }

    public int ImportTypeNamed(string name, TypeKind kind)
    {
        if (_typeIndex.TryGetValue(name, out int found)) return found;
        int index = ImportTypes.Count;
        ImportTypes.Add((Name(name), (int)kind));
        _typeIndex[name] = index;
        return index;
    }

    public int ImportType(Ty ty, Symbols symbols)
    {
        var kind = ty.Kind switch
        {
            TyKind.Bool or TyKind.Int or TyKind.Float or TyKind.Byte or TyKind.Primitive => TypeKind.Fundamental,
            TyKind.String or TyKind.Name or TyKind.Void or TyKind.Null => TypeKind.Simple,
            TyKind.Enum => TypeKind.Enum,
            TyKind.Map => TypeKind.Map,
            TyKind.Struct => TypeKind.Class,
            TyKind.Class => TypeKind.Handle,
            _ => TypeKind.Array,
        };
        string name = ty.Kind switch
        {
            TyKind.Class => $"handle:{symbols.BlobName(ty.Name)}",
            TyKind.Array => $"array:2,0,{ImportElementName(ty.Element!, symbols)}",
            TyKind.Map => $"map:2,0,{ImportElementName(ty.Key!, symbols)},{ImportElementName(ty.Element!, symbols)}",
            _ => ty.RttiName,
        };
        return ImportTypeNamed(name, kind);
    }

    public int ClassType(string cls, Symbols symbols) => ImportTypeNamed(symbols.BlobName(cls), TypeKind.Class);

    public int ImportFunction(string name, string? scopeClass, Symbols symbols)
    {
        var key = (name, scopeClass is null ? null : symbols.BlobName(scopeClass));
        if (_functionIndex.TryGetValue(key, out int found)) return found;
        int index = ImportFunctions.Count;
        int cname = Name(name);
        int scope = scopeClass is null ? NoType : ClassType(scopeClass, symbols);
        ImportFunctions.Add((cname, scope));
        _functionIndex[key] = index;
        return index;
    }

    private static string ImportElementName(Ty ty, Symbols symbols) => ty.Kind switch
    {
        TyKind.Class => $"handle:{symbols.BlobName(ty.Name)}",
        TyKind.Array => $"array:2,0,{ImportElementName(ty.Element!, symbols)}",
        TyKind.Map => $"map:2,0,{ImportElementName(ty.Key!, symbols)},{ImportElementName(ty.Element!, symbols)}",
        _ => ty.RttiName,
    };

    /// <summary>What a property or default is declared as: a class by its name, an array by its element's.</summary>
    public static string DefinitionTypeName(Ty ty, Symbols symbols) => ty.Kind switch
    {
        TyKind.Class or TyKind.Struct => symbols.BlobName(ty.Name),
        TyKind.Array => $"array:2,0,{DefinitionTypeName(ty.Element!, symbols)}",
        TyKind.Map => $"map:2,0,{DefinitionTypeName(ty.Key!, symbols)},{DefinitionTypeName(ty.Element!, symbols)}",
        _ => ty.RttiName,
    };
}
