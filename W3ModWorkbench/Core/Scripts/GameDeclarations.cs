using System.IO;
using W3ModWorkbench.Core.Scripts.Standalone;

namespace W3ModWorkbench.Core.Scripts;

/// <summary>A function's parameter types and return type, in the blob's type names (<c>String</c>, <c>CName</c>, <c>EEquipmentSlots</c>).</summary>
public sealed record FunctionSignature(List<string> Params, string? Return);

/// <summary>
/// The classes, fields and functions the game's scripts and the engine declare, for decompiling a mod: compiled code only
/// keeps a call's argument values, so whether a number was an enum's constant comes from the parameter's type here.
/// Read once per game scripts folder.
/// </summary>
public sealed class GameDeclarations
{
    public sealed class TypeDecl
    {
        public string? Parent { get; set; }
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FunctionSignature> Methods { get; } = new(StringComparer.Ordinal);
    }

    public static readonly GameDeclarations Empty = new();

    private static readonly Dictionary<string, GameDeclarations> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CacheLock = new();

    public Dictionary<string, TypeDecl> Types { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, FunctionSignature> Globals { get; } = new(StringComparer.Ordinal);
    /// <summary>Every class's method of each name, for a call whose receiver's type isn't known.</summary>
    public Dictionary<string, List<FunctionSignature>> MethodsByName { get; } = new(StringComparer.Ordinal);

    /// <summary>The declarations for the game install <paramref name="blob"/> sits in (or the one in Settings); empty when there's none.</summary>
    public static GameDeclarations For(ScriptBlob blob) =>
        GameScripts.FindFolder(blob.FilePath) is { } dir ? Load(dir) : Empty;

    public static GameDeclarations Load(string scriptsFolder)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(scriptsFolder, out var cached)) return cached;
            var decls = new GameDeclarations();
            try
            {
                decls.AddNative(Rtti.LoadBuiltIn());
                decls.AddScripts(StandaloneCompiler.ReadGameScripts(scriptsFolder, _ => { }, CancellationToken.None));
            }
            catch (IOException)
            {
                // unreadable script folder: what was read is kept
            }
            return Cache[scriptsFolder] = decls;
        }
    }

    private void AddNative(Rtti rtti)
    {
        foreach (var c in rtti.Classes)
        {
            var t = TypeOf(c.Name);
            t.Parent ??= c.Base;
            foreach (var (name, type) in c.Properties)
                t.Fields.TryAdd(name, type);
        }
    }

    private void AddScripts(List<SourceFile> files)
    {
        foreach (var item in files.SelectMany(f => f.Items))
        {
            switch (item)
            {
                case FunctionItem { Decl: var f } when f.Annotation is null:
                    Globals.TryAdd(f.Name, Signature(f));
                    break;
                case ClassItem { Decl: var c }:
                {
                    var t = TypeOf(c.StateMachineOf is { } machine ? Symbols.StateClassKey(machine, c.Name) : c.Name);
                    if (c.StateMachineOf is null && c.Parent is not null) t.Parent = c.Parent;
                    foreach (var field in c.Fields)
                        foreach (string name in field.Names)
                            t.Fields[name] = BlobName(field.Type);
                    foreach (var f in c.Methods)
                    {
                        var sig = Signature(f);
                        t.Methods.TryAdd(f.Name, sig);
                        (MethodsByName.TryGetValue(f.Name, out var list) ? list : MethodsByName[f.Name] = []).Add(sig);
                    }
                    break;
                }
            }
        }
    }

    private TypeDecl TypeOf(string name) => Types.TryGetValue(name, out var t) ? t : Types[name] = new TypeDecl();

    private static FunctionSignature Signature(FunctionDecl f) =>
        new([.. f.Params.Select(p => BlobName(p.Type))], f.ReturnType is { } r ? BlobName(r) : null);

    /// <summary>A source type as the blob names it.</summary>
    public static string BlobName(TypeRef type) => type.Name switch
    {
        "array" when type.Args.Count == 1 => "array:2,0," + BlobName(type.Args[0]),
        "string" => "String",
        "name" => "CName",
        "int" => "Int32",
        "float" => "Float",
        "bool" => "Bool",
        "byte" => "Uint8",
        _ => type.Name,
    };

    /// <summary>A class or struct name from a blob type name: <c>handle:CEntity</c> is <c>CEntity</c>.</summary>
    public static string ClassName(string type)
    {
        foreach (string prefix in (string[])["handle:", "ptr:", "soft:"])
            if (type.StartsWith(prefix, StringComparison.Ordinal)) return type[prefix.Length..];
        return type;
    }
}
