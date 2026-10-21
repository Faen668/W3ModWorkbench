// Part of the built-in compiler: a C# port of the .ws-to-rsblob compiler written by WebSpam for Aelto's tw3-cahirb (MIT).
// https://github.com/Aelto/tw3-cahirb/pull/1

namespace W3ModWorkbench.Core.Scripts.Standalone;

public enum TyKind { Void, Bool, Int, Float, Byte, String, Name, Null, Primitive, Enum, Struct, Class, Array, Map }

/// <summary>A resolved type. Names are those of the script (a state's class name carries its machine's).</summary>
public sealed record Ty(TyKind Kind, string Name = "", Ty? Element = null, Ty? Key = null)
{
    public static readonly Ty Void = new(TyKind.Void), Bool = new(TyKind.Bool), Int = new(TyKind.Int),
        Float = new(TyKind.Float), Byte = new(TyKind.Byte), String = new(TyKind.String), Name_ = new(TyKind.Name),
        Null = new(TyKind.Null);

    public static Ty Class(string name) => new(TyKind.Class, name);
    public static Ty Struct(string name) => new(TyKind.Struct, name);
    public static Ty Enum(string name) => new(TyKind.Enum, name);
    public static Ty Primitive(string name) => new(TyKind.Primitive, name);
    public static Ty ArrayOf(Ty element) => new(TyKind.Array, "", element);
    public static Ty MapOf(Ty key, Ty value) => new(TyKind.Map, "", value, key);

    public bool IsClass => Kind is TyKind.Class or TyKind.Null;

    /// <summary>The name the engine knows the type by.</summary>
    public string RttiName => Kind switch
    {
        TyKind.Void => "void",
        TyKind.Bool => "Bool",
        TyKind.Int => "Int32",
        TyKind.Float => "Float",
        TyKind.Byte => "Uint8",
        TyKind.String => "String",
        TyKind.Name => "CName",
        TyKind.Null => "NULL",
        TyKind.Array => $"array:2,0,{Element!.RttiName}",
        TyKind.Map => $"map:2,0,{Key!.RttiName},{Element!.RttiName}",
        _ => Name,
    };

    public override string ToString() => Kind switch
    {
        TyKind.Array => $"array<{Element}>",
        TyKind.Map => $"map<{Key}, {Element}>",
        TyKind.Primitive or TyKind.Enum or TyKind.Struct or TyKind.Class => Name,
        _ => RttiName,
    };
}

public sealed record FieldInfo(string Name, Ty Type, string Owner, bool IsBindable = false, bool IsImport = false);

public sealed record ParamInfo(string Name, Ty Type, bool IsOptional, bool IsOut);

public sealed class FunctionInfo
{
    public required string Name { get; init; }
    public string? Owner { get; init; }
    public required List<ParamInfo> Params { get; init; }
    public required Ty ReturnType { get; init; }
    public required FunctionSpecifiers Specifiers { get; init; }

    /// <summary>A native function only the engine's dump names: what it takes and returns isn't known, so calls pass what they're given.</summary>
    public bool IsOpaque { get; init; }

    /// <summary>Global functions and final methods are linked by name; the rest are looked up when called.</summary>
    public bool IsFinalCall => Owner is null || Specifiers.IsFinal;
}

public sealed class ClassInfo
{
    public required string Name { get; init; }
    public required string BlobName { get; init; }
    public string? Parent { get; set; }
    /// <summary>For a state: the state it extends, as written (it may belong to the state machine's base class).</summary>
    public string? StateParentName { get; init; }
    /// <summary>The name the class was declared with: for a state, without its machine.</summary>
    public string ShortName { get; init; } = "";
    public bool IsStruct { get; init; }
    public string? StateMachine { get; init; }
    /// <summary>
    /// When wcc_lite made the class: the engine's come first, in the engine's order, as they're there before any script
    /// is read; then the scripts' in the order they're read. The compiler lists a mod's classes by their parents' ranks.
    /// </summary>
    public int Rank { get; set; }
    public Dictionary<string, FieldInfo> Fields { get; } = [];
    public List<string> FieldOrder { get; } = [];
    public Dictionary<string, FunctionInfo> Methods { get; } = [];
}

public sealed class EnumInfo
{
    public required string Name { get; init; }
    public required List<(string Name, int Value)> Values { get; init; }
    public bool IsScript { get; init; }

    public int? ValueOf(string member)
    {
        foreach (var (name, value) in Values)
            if (name == member) return value;
        return null;
    }
}

/// <summary>Every type, member and function the scripts being compiled can name: theirs, the game's, and the engine's.</summary>
public sealed class Symbols
{
    public Dictionary<string, ClassInfo> Classes { get; } = [];
    public Dictionary<string, EnumInfo> Enums { get; } = [];
    public Dictionary<string, string> EnumMembers { get; } = [];
    public Dictionary<string, FunctionInfo> Functions { get; } = [];
    public HashSet<string> SimpleTypes { get; } = [];

    public static string StateClassKey(string machine, string state) => $"{machine}State{state}";

    public static string ClassKey(ClassDecl decl) =>
        decl.StateMachineOf is { } machine ? StateClassKey(machine, decl.Name) : decl.Name;

    public void AddEnums(IEnumerable<SourceFile> files, bool isScript)
    {
        foreach (var file in files)
            foreach (var item in file.Items.OfType<EnumItem>())
            {
                var decl = item.Decl;
                if (!isScript && Enums.ContainsKey(decl.Name)) continue;
                long next = 0;
                var values = new List<(string, int)>();
                foreach (var (name, value) in decl.Members)
                {
                    long v = value ?? next;
                    next = v + 1;
                    values.Add((name, (int)v));
                }
                foreach (var (member, _) in values) EnumMembers[member] = decl.Name;
                Enums[decl.Name] = new EnumInfo { Name = decl.Name, Values = values, IsScript = isScript };
            }
    }

    /// <summary>
    /// Mods' enums, as wcc_lite takes a mod's: an enum the game hasn't got is defined, and one it has is extended. A
    /// member it already has keeps its value (a value given must be the same), and a new one takes the next value free,
    /// so mods adding to the same enum don't clash; a mod can't give a new one a value. The game's enums and the
    /// engine's go in first. <paramref name="problem"/> hears what's wrong (null: values given are taken, as an
    /// installed mod's compiled ones are).
    /// </summary>
    public void ExtendEnums(IEnumerable<SourceFile> files, Action<SourceFile, int, string>? problem)
    {
        foreach (var file in files)
            foreach (var item in file.Items.OfType<EnumItem>())
            {
                var decl = item.Decl;
                if (!Enums.TryGetValue(decl.Name, out var existing))
                {
                    AddEnums([new SourceFile(file.Path, [item])], true);
                    continue;
                }
                var values = existing.Values.ToList();
                var used = values.Select(v => v.Value).ToHashSet();
                int next = used.Count == 0 ? 0 : used.Max() + 1;
                for (int i = 0; i < decl.Members.Count; i++)
                {
                    var (name, value) = decl.Members[i];
                    int line = i < decl.Lines.Count ? decl.Lines[i] : 1;
                    int at = values.FindIndex(v => v.Name == name);
                    if (at >= 0)
                    {
                        if (problem is not null && value is { } given && given != values[at].Value)
                            problem(file, line, $"Enumerator '{name}' in '{decl.Name}' has an explicit value, but it does not match previous definition.");
                        continue;
                    }
                    int v;
                    if (value is { } explicitValue)
                    {
                        if (problem is not null)
                        {
                            problem(file, line, $"Enumerator '{name}' in '{decl.Name}' has an explicit value, that is forbidden for new enumerators defined in enum extensions (mods) to prevent conflicts.");
                            continue;
                        }
                        v = (int)explicitValue;
                    }
                    else
                    {
                        while (used.Contains(next)) next++;
                        v = next;
                    }
                    values.Add((name, v));
                    used.Add(v);
                    EnumMembers[name] = decl.Name;
                }
                Enums[decl.Name] = new EnumInfo { Name = decl.Name, Values = values, IsScript = existing.IsScript };
            }
    }

    public void AddTypeNames(IEnumerable<SourceFile> files)
    {
        foreach (var file in files)
            foreach (var item in file.Items.OfType<ClassItem>())
            {
                var decl = item.Decl;
                string key = ClassKey(decl);
                if (Classes.ContainsKey(key)) continue;

                string? parent;
                if (decl.StateMachineOf is { } machine)
                    parent = decl.Parent is { } p ? StateClassKey(machine, p) : "CScriptableState";
                else if (decl.Parent is null && !decl.IsStruct && decl.Name is not ("IScriptable" or "ISerializable"))
                    parent = "IScriptable";
                else
                    parent = decl.Parent;

                Classes[key] = new ClassInfo
                {
                    Name = key,
                    BlobName = key,
                    Parent = parent,
                    IsStruct = decl.IsStruct,
                    StateMachine = decl.StateMachineOf,
                    StateParentName = decl.StateMachineOf is null ? null : decl.Parent,
                    ShortName = decl.Name,
                    Rank = Classes.Count,
                };
            }
    }

    /// <summary>
    /// A state can extend a state of its machine's base class (<c>state CombatSword in W3ReplacerCiri extends Combat</c>
    /// with Combat belonging to CR4Player), so its parent is looked for up the machine's own chain of base classes.
    /// </summary>
    public void ResolveStateParents()
    {
        foreach (var info in Classes.Values)
        {
            if (info.StateMachine is not { } machine) continue;
            // a state with no extends still stands on the base machine's state of its own name, when it has one
            string wanted = info.StateParentName ?? info.ShortName;
            string? current = machine;
            for (int depth = 0; current is not null && depth <= 64; depth++)
            {
                string key = StateClassKey(current, wanted);
                if (key != info.Name && Classes.ContainsKey(key)) // a state may extend the base machine's state of its own name
                {
                    info.Parent = key;
                    break;
                }
                current = Classes.TryGetValue(current, out var up) ? up.Parent : null;
            }
        }
    }

    /// <summary>
    /// Adds the members and functions the files declare. A problem with one declaration (a type nobody knows, say) is
    /// given to <paramref name="problem"/> and skipped, so a game script that can't be read doesn't stop the rest.
    /// </summary>
    public void AddMembers(IEnumerable<SourceFile> files, Action<SourceFile, string>? problem = null)
    {
        foreach (var file in files)
        {
            foreach (var item in file.Items)
            {
                try
                {
                    AddMembersOf(item);
                }
                catch (CompileException ex)
                {
                    problem?.Invoke(file, ex.Message);
                }
            }
        }
    }

    private void AddMembersOf(Item item)
    {
        switch (item)
        {
            case ClassItem { Decl: var decl }:
            {
                string key = ClassKey(decl);
                foreach (var field in decl.Fields)
                {
                    var ty = Resolve(field.Type);
                    foreach (string name in field.Names) AddField(key, name, ty, field.IsBinding, field.Specifiers.IsImport);
                }
                foreach (var method in decl.Methods)
                {
                    var info = FunctionInfoOf(method, key);
                    Classes[key].Methods.TryAdd(method.Name, info);
                }
                break;
            }
            case FunctionItem { Decl: var decl }:
            {
                var annotation = decl.Annotation;
                if (annotation is null)
                    Functions.TryAdd(decl.Name, FunctionInfoOf(decl, null));
                else if (annotation is { Kind: AnnotationKind.AddMethod, Class: { } cls })
                {
                    // a class that isn't there is for the extensions' pass to say
                    if (Classes.TryGetValue(cls, out var target)) target.Methods[decl.Name] = FunctionInfoOf(decl, cls);
                }
                break;
            }
            case FieldItem { Decl: var field }:
            {
                if (field.Annotation?.Class is not { } cls) break;
                var ty = Resolve(field.Type);
                foreach (string name in field.Names) AddField(cls, name, ty);
                break;
            }
        }
    }

    public void AddRttiEnums(Rtti rtti)
    {
        foreach (var native in rtti.Enums)
        {
            if (Enums.ContainsKey(native.Name)) continue;
            foreach (var (member, _) in native.Options) EnumMembers.TryAdd(member, native.Name);
            Enums[native.Name] = new EnumInfo { Name = native.Name, Values = [.. native.Options], IsScript = false };
        }
    }

    public void AddRttiClasses(Rtti rtti)
    {
        foreach (string simple in rtti.SimpleTypes) SimpleTypes.Add(simple);
        foreach (var native in rtti.Classes)
        {
            if (Classes.ContainsKey(native.Name)) continue;
            Classes[native.Name] = new ClassInfo
            {
                Name = native.Name,
                BlobName = native.Name,
                Parent = native.Base,
                IsStruct = native.IsStruct,
                Rank = Classes.Count,
            };
        }
        // the engine's classes, the script-declared imports among them, before every script's
        int engine = rtti.Classes.Count;
        foreach (var info in Classes.Values) info.Rank += engine;
        for (int i = 0; i < rtti.Classes.Count; i++)
            if (Classes.TryGetValue(rtti.Classes[i].Name, out var native) && native.Rank >= engine) native.Rank = i;
    }

    public void AddRttiProperties(Rtti rtti)
    {
        var declared = rtti.Classes
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => (Base: g.First().Base, Properties: g.First().Properties.Select(p => p.Name).ToHashSet()));

        bool Inherited(string cls, string property)
        {
            string? current = declared.GetValueOrDefault(cls).Base;
            int guard = 0;
            while (current is not null && guard++ < 64)
            {
                if (!declared.TryGetValue(current, out var info)) return false;
                if (info.Properties.Contains(property)) return true;
                current = info.Base;
            }
            return false;
        }

        foreach (var native in rtti.Classes)
            foreach (var (name, typeName) in native.Properties)
            {
                if (Inherited(native.Name, name)) continue;
                // a script class up the chain may declare it already, and then it belongs to that class
                if (Classes.GetValueOrDefault(native.Name)?.Parent is { } parent && FindField(parent, name) is not null) continue;
                AddField(native.Name, name, ResolveRtti(typeName));
            }
    }

    /// <summary>
    /// The engine's own methods and global functions the scripts don't declare. The dump has their names only, so a call to
    /// one passes its arguments as they are and is taken to return nothing.
    /// </summary>
    public void AddRttiFunctions(Rtti rtti)
    {
        foreach (string name in rtti.GlobalFunctions)
            Functions.TryAdd(name, Opaque(name, null));

        foreach (var native in rtti.Classes)
        {
            if (!Classes.TryGetValue(native.Name, out var info)) continue;
            foreach (string name in native.Methods)
                if (FindMethod(native.Name, name) is null)
                    info.Methods[name] = Opaque(name, native.Name);
        }
    }

    private static FunctionInfo Opaque(string name, string? owner) => new()
    {
        Name = name,
        Owner = owner,
        Params = [],
        ReturnType = Ty.Void,
        Specifiers = new FunctionSpecifiers(),
        IsOpaque = true,
    };

    private Ty ResolveRtti(string name)
    {
        if (name.StartsWith("array:"))
        {
            string[] parts = name["array:".Length..].Split(',', 3);
            return Ty.ArrayOf(ResolveRtti(parts.Length > 2 ? parts[2] : name["array:".Length..]));
        }
        if (name.StartsWith("map:"))
        {
            string[] parts = name["map:".Length..].Split(',', 4);
            return parts.Length == 4 ? Ty.MapOf(ResolveRtti(parts[2]), ResolveRtti(parts[3])) : Ty.Primitive(name);
        }
        if (name.StartsWith("handle:")) return Ty.Class(name["handle:".Length..]);
        if (name.StartsWith("ptr:")) return Ty.Class(name["ptr:".Length..]);

        try
        {
            var ty = Resolve(new TypeRef(name));
            return ty.Kind == TyKind.Class && !Classes.ContainsKey(ty.Name) ? Ty.Primitive(ty.Name) : ty;
        }
        catch (CompileException)
        {
            return Ty.Primitive(name);
        }
    }

    private void AddField(string cls, string name, Ty ty, bool bindable = false, bool isImport = false)
    {
        if (!Classes.TryGetValue(cls, out var info) || info.Fields.ContainsKey(name)) return;
        info.FieldOrder.Add(name);
        info.Fields[name] = new FieldInfo(name, ty, cls, bindable, isImport);
    }

    public FunctionInfo FunctionInfoOf(FunctionDecl decl, string? owner)
    {
        try
        {
            var ps = decl.Params.Select(p => new ParamInfo(p.Name, Resolve(p.Type), p.IsOptional, p.IsOut)).ToList();
            var ret = decl.ReturnType is { } rt ? Resolve(rt) : decl.Specifiers.IsEvent ? Ty.Bool : Ty.Void;
            return new FunctionInfo { Name = decl.Name, Owner = owner, Params = ps, ReturnType = ret, Specifiers = decl.Specifiers };
        }
        catch (CompileException ex)
        {
            throw new CompileException($"{decl.Name}: {ex.Message}");
        }
    }

    public Ty Resolve(TypeRef type)
    {
        if (type.Name == "array")
        {
            if (type.Args.Count == 0) throw new CompileException("array without an element type");
            return Ty.ArrayOf(Resolve(type.Args[0]));
        }
        if (type.Name == "map" && type.Args.Count == 2) return Ty.MapOf(Resolve(type.Args[0]), Resolve(type.Args[1]));
        switch (type.Name)
        {
            case "void": return Ty.Void;
            case "bool" or "Bool": return Ty.Bool;
            case "int" or "Int32": return Ty.Int;
            case "float" or "Float": return Ty.Float;
            case "byte" or "Uint8": return Ty.Byte;
            case "string" or "String": return Ty.String;
            case "name" or "CName": return Ty.Name_;
            case "Uint64" or "Int8" or "Int16" or "Uint16" or "Uint32" or "Int64" or "Double":
                return Ty.Primitive(type.Name);
        }
        if (SimpleTypes.Contains(type.Name) && !Classes.ContainsKey(type.Name)) return Ty.Primitive(type.Name);
        if (Enums.ContainsKey(type.Name)) return Ty.Enum(type.Name);
        if (Classes.TryGetValue(type.Name, out var cls)) return cls.IsStruct ? Ty.Struct(type.Name) : Ty.Class(type.Name);
        return type.Args.Count == 0 ? Ty.Class(type.Name) : Ty.Primitive(type.Name);
    }

    /// <summary>
    /// The field called <paramref name="name"/> on <paramref name="cls"/> or up its chain. A script's <c>import var</c> only
    /// says the engine has that field, and the engine's own class that has it is the owner.
    /// </summary>
    public FieldInfo? FindField(string cls, string name)
    {
        FieldInfo? found = null;
        string? current = cls;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (!Classes.TryGetValue(current, out var info)) break;
            if (info.Fields.TryGetValue(name, out var field))
            {
                if (!field.IsImport) return field;
                found ??= field;
                if (found != field) found = field; // an ancestor with it too is the real owner
            }
            current = info.Parent;
        }
        return found;
    }

    public FunctionInfo? FindMethod(string cls, string name)
    {
        string? current = cls;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (!Classes.TryGetValue(current, out var info)) return null;
            if (info.Methods.TryGetValue(name, out var method)) return method;
            current = info.Parent;
        }
        return null;
    }

    public bool IsSubclass(string cls, string ancestor)
    {
        string? current = cls;
        for (int depth = 0; current is not null && depth <= 64; depth++)
        {
            if (current == ancestor) return true;
            current = Classes.TryGetValue(current, out var info) ? info.Parent : null;
        }
        return false;
    }

    public string BlobName(string cls) => Classes.TryGetValue(cls, out var info) ? info.BlobName : cls;
}

/// <summary>A script the compiler can't make sense of; carries what's wrong and, when known, the line.</summary>
public sealed class CompileException(string message, int line = 0) : Exception(message)
{
    public int Line { get; } = line;
}

/// <summary>Everything wrong with one function, in the order it's written.</summary>
public sealed class CompileErrorsException(List<CompileException> errors) : Exception(errors[0].Message)
{
    public List<CompileException> Errors { get; } = errors;
}

public sealed record SourceFile(string Path, List<Item> Items);
