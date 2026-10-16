using System.IO;
using System.Text;

namespace W3BundleExplorer.Core.Scripts;

public sealed class ScriptProperty
{
    public required string Name { get; init; }
    public required int Flags { get; init; }
    public required string Type { get; init; }
    public required string Owner { get; init; }
    /// <summary>The editor hint text from a "hint name = ..." line, usually empty.</summary>
    public string Hint { get; init; } = "";
}

public enum FunctionKind
{
    Method,
    Global,
    ReplaceGlobal,
    Replace,
    AddMethod,
    Wrap,
}

public sealed class ScriptFunction
{
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public required int Flags { get; init; }
    public required List<ScriptProperty> Return { get; init; }
    public required List<ScriptProperty> Params { get; init; }
    public required List<ScriptProperty> Locals { get; init; }
    public required int RuntimeSize { get; init; }
    public required byte[] Code { get; init; }
    public FunctionKind Kind { get; set; }
    public ScriptClass? Class { get; set; }
}

/// <summary>
/// A default value. Simple values only use <see cref="Value"/>; arrays and structs keep theirs in
/// <see cref="Children"/> (array elements have no key, struct fields are keyed by field name).
/// </summary>
public sealed record ScriptDefault(string Property, string Value, List<ScriptDefaultNode> Children);

public sealed record ScriptDefaultNode(string Key, string Value, List<ScriptDefaultNode> Children);

public sealed class ScriptEnum
{
    public required string Name { get; init; }
    public required List<(string Name, int Value)> Members { get; init; }
}

public sealed class ScriptStruct
{
    public required string Name { get; init; }
    public required int Flags { get; init; }
    public required List<ScriptProperty> Properties { get; init; }
    public required List<ScriptDefault> Defaults { get; init; }
}

public sealed class ScriptClass
{
    public required string Name { get; init; }
    public required string Base { get; init; }
    public required string StateOf { get; init; }
    public required bool IsState { get; init; }
    public required int Flags { get; init; }
    public List<ScriptProperty> Properties { get; set; } = [];
    public List<ScriptFunction> Functions { get; set; } = [];
    public List<ScriptDefault> Defaults { get; set; } = [];
}

/// <summary>
/// A REDkit "precompiled.rsblob": a mod's compiled WitcherScript. Layout, in order: header,
/// enums, structs, classes (with states), global functions, two lists of replaced functions,
/// @addMethod and @wrapMethod functions, @addField fields, then the type, property and function
/// tables the bytecode links against, and a name table at the offset stored in the last four bytes.
/// </summary>
public sealed class ScriptBlob
{
    public string FilePath { get; private init; } = "";
    public int Version { get; private set; }
    public string Platform { get; private set; } = "";
    public string Build { get; private set; } = "";
    public string Configuration { get; private set; } = "";
    public List<ScriptEnum> Enums { get; } = [];
    public List<ScriptStruct> Structs { get; } = [];
    public List<ScriptClass> Classes { get; } = [];
    public List<ScriptFunction> Globals { get; } = [];
    public List<ScriptFunction> ReplacedGlobals { get; } = [];
    public List<ScriptFunction> Replaced { get; } = [];
    public List<ScriptFunction> AddedMethods { get; } = [];
    public List<ScriptFunction> Wrapped { get; } = [];
    public List<ScriptProperty> AddedFields { get; } = [];
    public List<(string Name, int Kind)> Types { get; } = [];
    /// <summary>Properties of other types the code links to: name, type index and owning type index.</summary>
    public List<(string Name, int Type, int OwnerType)> ImportedProperties { get; } = [];
    public List<(string Name, int OwnerType)> FunctionRefs { get; } = [];
    public string[] Names { get; private set; } = [];

    public IEnumerable<ScriptFunction> AllFunctions =>
        Classes.SelectMany(c => c.Functions).Concat(Globals).Concat(ReplacedGlobals).Concat(Replaced)
               .Concat(AddedMethods).Concat(Wrapped);

    public static bool LooksLikeBlob(string path) =>
        Path.GetExtension(path).Equals(".rsblob", StringComparison.OrdinalIgnoreCase);

    public static ScriptBlob Open(string path)
    {
        var data = File.ReadAllBytes(path);
        var blob = new ScriptBlob { FilePath = path };
        try
        {
            blob.Read(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            throw new InvalidDataException("The script blob is damaged or in an unknown format.", ex);
        }
        return blob;
    }

    private void Read(byte[] data)
    {
        if (data.Length < 16)
            throw new InvalidDataException("The file is too small to be a script blob.");

        var r = new BlobReader(data, 0);
        int tableAt = BitConverter.ToInt32(data, data.Length - 4);
        if (tableAt <= 0 || tableAt >= data.Length - 4)
            throw new InvalidDataException("Not a compiled script blob (no name table).");
        var names = new BlobReader(data, tableAt);
        int count = names.U32();
        Names = new string[count];
        for (int i = 0; i < count; i++)
            Names[i] = names.Str();
        r.Names = Names;

        Version = r.U32();
        if (Version != 1)
            throw new InvalidDataException($"Unsupported script blob version {Version}.");
        Platform = r.Str();
        Build = r.Str();
        r.Skip(8);
        Configuration = r.Str();

        for (int n = r.Vlq(); n > 0; n--)
        {
            string name = r.Name();
            var members = new List<(string, int)>();
            for (int m = r.Vlq(); m > 0; m--)
                members.Add((r.Name(), r.Vlq()));
            Enums.Add(new ScriptEnum { Name = name, Members = members });
        }

        for (int n = r.Vlq(); n > 0; n--)
        {
            string name = r.Name();
            int flags = r.Vlq();
            Structs.Add(new ScriptStruct { Name = name, Flags = flags, Properties = Props(r), Defaults = Defaults(r) });
        }

        for (int n = r.Vlq(); n > 0; n--)
        {
            var c = new ScriptClass { Name = r.Name(), Base = r.Name(), StateOf = r.Name(), IsState = r.U8() != 0, Flags = r.Vlq() };
            c.Properties = Props(r);
            for (int f = r.Vlq(); f > 0; f--)
                c.Functions.Add(Function(r, FunctionKind.Method, c));
            c.Defaults = Defaults(r);
            Classes.Add(c);
        }

        ReadFunctions(r, Globals, FunctionKind.Global);
        ReadFunctions(r, ReplacedGlobals, FunctionKind.ReplaceGlobal);
        ReadFunctions(r, Replaced, FunctionKind.Replace);
        ReadFunctions(r, AddedMethods, FunctionKind.AddMethod);
        ReadFunctions(r, Wrapped, FunctionKind.Wrap);
        AddedFields.AddRange(Props(r));

        for (int n = r.Vlq(); n > 0; n--)
            Types.Add((r.Name(), r.Vlq()));
        for (int n = r.Vlq(); n > 0; n--)
            ImportedProperties.Add((r.Name(), r.Vlq(), r.Vlq()));
        for (int n = r.Vlq(); n > 0; n--)
            FunctionRefs.Add((r.Name(), r.Vlq()));

        if (r.Position != tableAt)
            throw new InvalidDataException("The script blob did not read through to its name table.");
    }

    private static void ReadFunctions(BlobReader r, List<ScriptFunction> into, FunctionKind kind)
    {
        for (int n = r.Vlq(); n > 0; n--)
            into.Add(Function(r, kind, null));
    }

    private static ScriptFunction Function(BlobReader r, FunctionKind kind, ScriptClass? owner)
    {
        string name = r.Name(), ownerName = r.Name();
        int flags = r.Vlq();
        var ret = Props(r);
        var parameters = Props(r);
        var locals = Props(r);
        int codeLength = r.Vlq() - 4;
        int runtime = r.U32();
        var code = r.Bytes(codeLength);
        return new ScriptFunction
        {
            Name = name, Owner = ownerName, Flags = flags, Return = ret, Params = parameters, Locals = locals,
            RuntimeSize = runtime, Code = code, Kind = kind, Class = owner,
        };
    }

    private static List<ScriptProperty> Props(BlobReader r)
    {
        var list = new List<ScriptProperty>();
        for (int n = r.Vlq(); n > 0; n--)
        {
            string name = r.Name();
            string hint = r.Str();
            int flags = r.Vlq();
            string type = r.Name(), owner = r.Name();
            r.Name(); // unknown, empty in every blob seen so far
            list.Add(new ScriptProperty { Name = name, Hint = hint, Flags = flags, Type = type, Owner = owner });
        }
        return list;
    }

    private static List<ScriptDefault> Defaults(BlobReader r)
    {
        var list = new List<ScriptDefault>();
        for (int n = r.Vlq(); n > 0; n--)
        {
            string prop = r.Name();
            var node = DefaultNode(r);
            list.Add(new ScriptDefault(prop, node.Value, node.Children));
        }
        return list;
    }

    /// <summary>One value: a key (field name, or empty), the value as text, then any child values.</summary>
    private static ScriptDefaultNode DefaultNode(BlobReader r)
    {
        string key = r.Name();
        string value = r.Str();
        var children = new List<ScriptDefaultNode>();
        for (int n = r.Vlq(); n > 0; n--)
            children.Add(DefaultNode(r));
        return new ScriptDefaultNode(key, value, children);
    }
}

/// <summary>Reads the blob's variable-length integers (CR2W style: sign bit, continuation bit, 6 bits, then 7-bit groups).</summary>
internal sealed class BlobReader(byte[] data, int position)
{
    public int Position { get; private set; } = position;
    public string[] Names { get; set; } = [];

    public byte U8() => data[Position++];

    public int U32()
    {
        int v = BitConverter.ToInt32(data, Position);
        Position += 4;
        return v;
    }

    public void Skip(int n) => Position += n;

    public byte[] Bytes(int n)
    {
        var b = data.AsSpan(Position, n).ToArray();
        Position += n;
        return b;
    }

    public int Vlq()
    {
        int b = data[Position++];
        bool negative = (b & 0x80) != 0;
        int value = b & 0x3F;
        if ((b & 0x40) != 0)
        {
            int shift = 6;
            do
            {
                b = data[Position++];
                value |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
        }
        return negative ? -value : value;
    }

    public string Str()
    {
        int length = Math.Abs(Vlq());
        string s = Encoding.UTF8.GetString(data, Position, length);
        Position += length;
        return s;
    }

    public string Name()
    {
        int i = Vlq();
        return i >= 0 && i < Names.Length ? Names[i] : $"<name{i}>";
    }
}
