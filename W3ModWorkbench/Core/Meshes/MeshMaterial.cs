using System.IO;
using System.Numerics;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>A texture a material uses: the parameter it's in (Diffuse, Normal, TintMask…) and its .xbm path.</summary>
public sealed record MaterialTexture(string Parameter, string Path, string From);

/// <summary>A texture parameter's value: a texture's game path, or none.</summary>
public sealed record TextureRef(string? Path);

/// <summary>A colour parameter's value, 0 to 255 a channel.</summary>
public sealed record ColorValue(byte R, byte G, byte B, byte A)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}" + (A == 255 ? "" : $"{A:X2}");
}

/// <summary>A parameter's value of a type that isn't edited here, kept as the bytes the file has.</summary>
public sealed record RawValue(byte[] Bytes);

public enum ParameterKind { Texture, Scalar, Color, Vector, Other }

/// <summary>
/// One of a material's parameters: its name, the engine's type for it (<c>Float</c>, <c>Color</c>, <c>Vector</c>,
/// <c>handle:ITexture</c>…), its value, and where it's set (the material itself, one of its bases, or the shader).
/// A value is a <see cref="TextureRef"/>, a float, a <see cref="ColorValue"/>, a <see cref="Vector4"/> or a <see cref="RawValue"/>.
/// </summary>
public sealed record MaterialParameter(string Name, string Type, object? Value, string From)
{
    public ParameterKind Kind => Value switch
    {
        TextureRef => ParameterKind.Texture,
        float => ParameterKind.Scalar,
        ColorValue => ParameterKind.Color,
        Vector4 => ParameterKind.Vector,
        _ => ParameterKind.Other,
    };

    /// <summary>The import class a texture of this path is: a bitmap, a texture array or a cube map.</summary>
    public static string TextureClass(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".texarray" => "CTextureArray",
        ".w2cube" => "CCubeTexture",
        _ => "CBitmapTexture",
    };
}

/// <summary>
/// A mesh's material, with its parameters worked out. A mesh's material is usually an instance kept in the mesh, which
/// sets some parameters and takes the rest from its base: another instance (<c>.w2mi</c>) or the shader graph
/// (<c>.w2mg</c>), which declares every parameter there is and its default. <see cref="Own"/> is what the material
/// itself sets; <see cref="Inherited"/> is what its bases and the shader give the rest.
/// </summary>
public sealed class MeshMaterial
{
    /// <summary>Where the material is: "in the mesh", or the .w2mi or .w2mg path.</summary>
    public required string Source { get; init; }

    /// <summary>The bases in turn, nearest first, ending with the shader graph when it was found.</summary>
    public required List<string> Bases { get; init; }

    /// <summary>The parameters the material sets itself, in the order it keeps them.</summary>
    public List<MaterialParameter> Own { get; init; } = [];

    /// <summary>The parameters it takes from its bases and the shader, nearest first, the shader's in the order it declares them.</summary>
    public List<MaterialParameter> Inherited { get; init; } = [];

    /// <summary>The material object in the mesh, for one kept there.</summary>
    public int? InlineExport { get; init; }

    /// <summary>What the material is directly based on: its path and class (CMaterialInstance or CMaterialGraph).</summary>
    public string? BasePath { get; init; }
    public string? BaseClass { get; init; }

    /// <summary>The flags the material's object has in its file, which a copy of it keeps (cooked files mark theirs).</summary>
    public int ExportFlags { get; init; }

    /// <summary>The mesh points straight at a shader graph, which has nothing of its own to change: changes make a new instance of it.</summary>
    public bool IsGraph => Source.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase);

    /// <summary>A material in a file of its own, a .w2mi, which other meshes can use too.</summary>
    public bool IsFile => Source.EndsWith(".w2mi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every parameter with the value that counts: the material's own where it sets one, else the inherited one.</summary>
    public List<MaterialParameter> Parameters
    {
        get
        {
            var result = Inherited.Select(p => Own.FirstOrDefault(o => Same(o.Name, p.Name)) ?? p).ToList();
            result.AddRange(Own.Where(o => !Inherited.Any(p => Same(o.Name, p.Name))));
            return result;
        }
    }

    /// <summary>The textures it uses, its own first, as the viewer lists them.</summary>
    public List<MaterialTexture> Textures => [.. Own.Concat(Inherited.Where(p => !Own.Any(o => Same(o.Name, p.Name))))
        .Where(p => p.Value is TextureRef { Path.Length: > 0 })
        .Select(p => new MaterialTexture(p.Name, ((TextureRef)p.Value!).Path!, p.From))];

    /// <summary>The shader graph's file name without its folder, as a short description: pbr_std, pbr_skin…</summary>
    public string Shader => Bases.LastOrDefault(b => b.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase)) is { } graph
        ? Path.GetFileNameWithoutExtension(graph)
        : IsGraph ? Path.GetFileNameWithoutExtension(Source) : "";

    /// <summary>The texture that gives the colour: the Diffuse parameter, or its like under another name.</summary>
    public MaterialTexture? Diffuse => DiffuseOf(Textures);

    public static MaterialTexture? DiffuseOf(IReadOnlyList<MaterialTexture> all)
    {
        // texture arrays (.texarray) and cube maps aren't single pictures to put on a mesh
        var textures = all.Where(t => t.Path.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase)).ToList();
        return textures.FirstOrDefault(t => t.Parameter.Equals("Diffuse", StringComparison.OrdinalIgnoreCase))
        ?? textures.FirstOrDefault(t => t.Parameter.Equals("Diffusemap", StringComparison.OrdinalIgnoreCase))
        ?? textures.FirstOrDefault(t => t.Parameter.Contains("diffuse", StringComparison.OrdinalIgnoreCase) && !t.Parameter.Contains("snow", StringComparison.OrdinalIgnoreCase))
        ?? textures.FirstOrDefault(t => t.Parameter.Contains("diffuse", StringComparison.OrdinalIgnoreCase));
    }

    public MaterialTexture? Normal => Textures.FirstOrDefault(t => t.Parameter.Equals("Normal", StringComparison.OrdinalIgnoreCase))
        ?? Textures.FirstOrDefault(t => t.Parameter.StartsWith("Normal", StringComparison.OrdinalIgnoreCase));

    /// <summary>The same material with other parameters of its own.</summary>
    public MeshMaterial WithOwn(List<MaterialParameter> own) => new()
    {
        Source = Source,
        Bases = Bases,
        Own = own,
        Inherited = Inherited,
        InlineExport = InlineExport,
        BasePath = BasePath,
        BaseClass = BaseClass,
        ExportFlags = ExportFlags,
    };

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Works out a material from one of a mesh's material handles. <paramref name="readFile"/> reads a .w2mi or .w2mg by
    /// its game path, or gives null when it isn't found; the chain stops there.
    /// </summary>
    public static MeshMaterial Resolve(Cr2wFile file, RedHandle handle, Func<string, byte[]?> readFile)
    {
        if (file.Object(handle) is { ClassName: "CMaterialInstance" } inline)
        {
            var (basePath, baseClass) = BaseOf(file, inline);
            var bases = new List<string>();
            var inherited = new List<MaterialParameter>();
            var own = ParametersOf(file, inline, "the mesh");
            FollowBase(file, inline, own, inherited, bases, readFile, 0);
            return new MeshMaterial
            {
                Source = "in the mesh", Bases = bases, Own = own, Inherited = inherited,
                InlineExport = inline.Export?.Index, BasePath = basePath, BaseClass = baseClass, ExportFlags = FlagsOf(file, inline),
            };
        }
        return FromFile(file.Import(handle)?.Path ?? "", readFile);
    }

    /// <summary>Works out a material from its own file, a .w2mi or a .w2mg, as a mesh pointing at it would have it.</summary>
    public static MeshMaterial FromFile(string path, Func<string, byte[]?> readFile)
    {
        var bases = new List<string>();
        var inherited = new List<MaterialParameter>();
        var file = ReadMaterial(path, readFile);
        if (file?.First("CMaterialInstance") is { } instance)
        {
            var (basePath, baseClass) = BaseOf(file, instance);
            var own = ParametersOf(file, instance, Path.GetFileName(path));
            FollowBase(file, instance, own, inherited, bases, readFile, 0);
            return new MeshMaterial
            {
                Source = path, Bases = bases, Own = own, Inherited = inherited,
                BasePath = basePath, BaseClass = baseClass, ExportFlags = FlagsOf(file, instance),
            };
        }
        if (file?.First("CMaterialGraph") is not null)
            return new MeshMaterial
            {
                Source = path, Bases = bases, Inherited = GraphParameters(file, Path.GetFileName(path)),
                BasePath = path, BaseClass = "CMaterialGraph", ExportFlags = file.Exports.Count > 0 ? ExportFlagsOf(file, 0) : 0,
            };
        return new MeshMaterial { Source = path, Bases = bases };
    }

    private static Cr2wFile? ReadMaterial(string path, Func<string, byte[]?> readFile)
    {
        if (path.Length == 0) return null;
        try
        {
            return readFile(path) is { } data ? Cr2wFile.Read(data) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static (string? Path, string? Class) BaseOf(Cr2wFile file, RedObject instance) =>
        instance["baseMaterial"] is RedHandle handle && file.Import(handle) is { } import ? (import.Path, import.ClassName) : (null, null);

    private static int FlagsOf(Cr2wFile file, RedObject instance) => instance.Export is { } e ? ExportFlagsOf(file, e.Index) : 0;

    /// <summary>An object's flags, from the file's table of objects.</summary>
    private static int ExportFlagsOf(Cr2wFile file, int export)
    {
        int at = BitConverter.ToInt32(file.Data, 40 + 4 * 12) + export * 24 + 2;
        return BitConverter.ToUInt16(file.Data, at);
    }

    /// <summary>Follows an instance's base, adding what each base sets that nothing nearer sets.</summary>
    private static void FollowBase(Cr2wFile file, RedObject instance, List<MaterialParameter> own, List<MaterialParameter> inherited,
        List<string> bases, Func<string, byte[]?> readFile, int depth)
    {
        if (depth > 8 || instance["baseMaterial"] is not RedHandle handle) return;
        if (file.Object(handle) is { ClassName: "CMaterialInstance" } inlineBase && !ReferenceEquals(inlineBase, instance))
        {
            Merge(inherited, own, ParametersOf(file, inlineBase, "the mesh"));
            FollowBase(file, inlineBase, own, inherited, bases, readFile, depth + 1);
            return;
        }
        if (file.Import(handle)?.Path is not { Length: > 0 } path || bases.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        bases.Add(path);
        if (ReadMaterial(path, readFile) is not { } baseFile) return;
        string from = Path.GetFileName(path);
        if (baseFile.First("CMaterialInstance") is { } baseInstance)
        {
            Merge(inherited, own, ParametersOf(baseFile, baseInstance, from));
            FollowBase(baseFile, baseInstance, own, inherited, bases, readFile, depth + 1);
        }
        else if (baseFile.First("CMaterialGraph") is not null)
            Merge(inherited, own, GraphParameters(baseFile, from));
    }

    private static void Merge(List<MaterialParameter> inherited, List<MaterialParameter> own, List<MaterialParameter> more)
    {
        foreach (var parameter in more)
            if (!own.Any(o => Same(o.Name, parameter.Name)) && !inherited.Any(i => Same(i.Name, parameter.Name)))
                inherited.Add(parameter);
    }

    /// <summary>An instance's parameters as values to show and change.</summary>
    private static List<MaterialParameter> ParametersOf(Cr2wFile file, RedObject instance, string from) =>
        [.. file.Parameters(instance).Select(p => new MaterialParameter(p.Name, p.Type, ValueOf(file, p.Type, p.Value, p.Raw), from))];

    private static object? ValueOf(Cr2wFile file, string type, object? value, byte[] raw)
    {
        if (value is RedHandle handle && type.StartsWith("handle:", StringComparison.Ordinal))
        {
            if (handle.Raw == 0) return new TextureRef(null);
            if (file.Import(handle) is { } import) return new TextureRef(import.Path);
        }
        if (value is float f && type == "Float") return f;
        if (value is RedObject color && type == "Color")
            return new ColorValue((byte)color.Integer("Red"), (byte)color.Integer("Green"), (byte)color.Integer("Blue"), (byte)color.Integer("Alpha", 255));
        if (value is RedObject vector && type == "Vector")
            return new Vector4(vector.Float("X"), vector.Float("Y"), vector.Float("Z"), vector.Float("W"));
        return new RawValue(raw);
    }

    /// <summary>A shader graph's parameters and their defaults, each an object of its own in the graph's file.</summary>
    private static List<MaterialParameter> GraphParameters(Cr2wFile file, string from)
    {
        var result = new List<MaterialParameter>();
        foreach (var export in file.Exports.Where(e => e.ClassName.StartsWith("CMaterialParameter", StringComparison.Ordinal)))
        {
            var parameter = file.Object(export.Index);
            if (parameter["parameterName"] is not string name || name.Length == 0 || result.Any(r => Same(r.Name, name))) continue;
            MaterialParameter? found = export.ClassName switch
            {
                "CMaterialParameterScalar" => new(name, "Float", parameter.Float("scalar"), from),
                "CMaterialParameterColor" => new(name, "Color", parameter.Struct("color") is { } c
                    ? new ColorValue((byte)c.Integer("Red"), (byte)c.Integer("Green"), (byte)c.Integer("Blue"), (byte)c.Integer("Alpha", 255))
                    : new ColorValue(0, 0, 0, 255), from),
                "CMaterialParameterVector" => new(name, "Vector", parameter.Struct("vector") is { } v
                    ? new Vector4(v.Float("X"), v.Float("Y"), v.Float("Z"), v.Float("W")) : Vector4.Zero, from),
                "CMaterialParameterTexture" => new(name, "handle:ITexture", Texture(file, parameter), from),
                "CMaterialParameterTextureArray" => new(name, "handle:CTextureArray", Texture(file, parameter), from),
                "CMaterialParameterCube" => new(name, "handle:CCubeTexture", Texture(file, parameter), from),
                _ => null,
            };
            if (found is not null) result.Add(found);
        }
        return result;
    }

    private static TextureRef Texture(Cr2wFile file, RedObject parameter) =>
        new(parameter.Properties.Select(p => p.Value).OfType<RedHandle>().Select(h => file.Import(h)?.Path).FirstOrDefault(p => p is not null));
}
