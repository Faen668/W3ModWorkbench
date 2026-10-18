using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>
/// Writes materials: a material instance's parameters in a mesh or a .w2mi, a new .w2mi, and a mesh whose material
/// list points at other .w2mi files. Everything else in a file is kept as it was, byte for byte.
/// <para>
/// A material instance is its properties (<c>baseMaterial</c>, the material it's based on) and then its parameters:
/// a count, and for each one its size (with the eight bytes before its value), its name, its type and its value.
/// </para>
/// </summary>
public static class MaterialWriter
{
    /// <summary>Types whose saved bytes mean the same in any file, so a parameter of one can be copied into a new file as it is.</summary>
    private static readonly HashSet<string> PlainTypes = ["Bool", "Int8", "Uint8", "Int16", "Uint16", "Int32", "Uint32", "Int64", "Uint64", "Float", "Double"];

    /// <summary>A material instance's parameters as its file keeps them, after its properties.</summary>
    /// <param name="sameFile">Whether the parameters were read from this file, so values kept as bytes can be written back as they are.</param>
    private static byte[] ParametersData(Cr2wBuilder builder, IEnumerable<MaterialParameter> parameters, bool sameFile)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var written = new List<(int Name, int Type, byte[] Value)>();
        foreach (var parameter in parameters)
        {
            if (parameter.Value is RawValue && !sameFile && !PlainTypes.Contains(parameter.Type)) continue;
            written.Add((builder.Name(parameter.Name), builder.Name(parameter.Type), Value(builder, parameter)));
        }
        w.Write(written.Count);
        foreach (var (name, type, value) in written)
        {
            w.Write(value.Length + 8);
            w.Write((ushort)name);
            w.Write((ushort)type);
            w.Write(value);
        }
        return ms.ToArray();
    }

    private static byte[] Value(Cr2wBuilder builder, MaterialParameter parameter)
    {
        switch (parameter.Value)
        {
            case TextureRef texture:
                int handle = texture.Path is { Length: > 0 } path ? -(builder.Import(path, MaterialParameter.TextureClass(path)) + 1) : 0;
                return BitConverter.GetBytes(handle);
            case float f:
                return BitConverter.GetBytes(f);
            case ColorValue c:
                return Struct(builder, [("Red", "Uint8", [c.R]), ("Green", "Uint8", [c.G]), ("Blue", "Uint8", [c.B]), ("Alpha", "Uint8", [c.A])]);
            case Vector4 v:
                return Struct(builder, [("X", "Float", BitConverter.GetBytes(v.X)), ("Y", "Float", BitConverter.GetBytes(v.Y)),
                    ("Z", "Float", BitConverter.GetBytes(v.Z)), ("W", "Float", BitConverter.GetBytes(v.W))]);
            case RawValue raw:
                return raw.Bytes;
            default:
                throw new InvalidDataException($"The parameter {parameter.Name} has a value that can't be written.");
        }
    }

    /// <summary>A struct as the engine saves one: a zero byte, then (name, type, size, value) for each field, then a name of 0.</summary>
    private static byte[] Struct(Cr2wBuilder builder, (string Name, string Type, byte[] Value)[] fields)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)0);
        foreach (var (name, type, value) in fields)
        {
            w.Write((ushort)builder.Name(name));
            w.Write((ushort)builder.Name(type));
            w.Write(value.Length + 4);
            w.Write(value);
        }
        w.Write((ushort)0);
        return ms.ToArray();
    }

    /// <summary>A material instance's data from nothing: based on <paramref name="basePath"/>, with these parameters.</summary>
    private static byte[] NewInstanceData(Cr2wBuilder builder, string? basePath, string? baseClass, IEnumerable<MaterialParameter> parameters)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)0);
        if (basePath is { Length: > 0 })
        {
            int import = builder.Import(basePath, baseClass ?? (basePath.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase) ? "CMaterialGraph" : "CMaterialInstance"));
            w.Write((ushort)builder.Name("baseMaterial"));
            w.Write((ushort)builder.Name("handle:IMaterial"));
            w.Write(8);
            w.Write(-(import + 1));
        }
        w.Write((ushort)0);
        w.Write(ParametersData(builder, parameters, sameFile: false));
        return ms.ToArray();
    }

    /// <summary>A new material instance file (.w2mi), of the CR2W version of the files it'll sit with.</summary>
    public static byte[] NewFile(int version, int flags, string? basePath, string? baseClass, IEnumerable<MaterialParameter> parameters)
    {
        var builder = Cr2wBuilder.New(version);
        builder.AddExport("CMaterialInstance", -1, NewInstanceData(builder, basePath, baseClass, parameters), flags);
        return builder.Build();
    }

    /// <summary>A .w2mi with its instance's parameters changed; its base and everything else stay as they were.</summary>
    public static byte[] EditFile(Cr2wFile file, IEnumerable<MaterialParameter> parameters)
    {
        var instance = file.First("CMaterialInstance") ?? throw new InvalidDataException("There's no material instance in this file.");
        var builder = Cr2wBuilder.From(file);
        Replace(builder, instance, parameters);
        return builder.Build();
    }

    /// <summary>
    /// A mesh with changes to its materials: new parameters for materials kept in it (by their object), and slots of its
    /// material list pointed at .w2mi files (by slot). The mesh's shape and the rest are kept as they were.
    /// </summary>
    public static byte[] EditMesh(MeshFile mesh, IReadOnlyDictionary<int, List<MaterialParameter>> inline, IReadOnlyDictionary<int, string> files)
    {
        var builder = Cr2wBuilder.From(mesh.File);
        foreach (var (export, parameters) in inline)
            Replace(builder, mesh.File.Object(export), parameters);
        if (files.Count > 0)
        {
            var meshExport = mesh.Mesh.Export ?? throw new InvalidDataException("The mesh object wasn't found.");
            if (!mesh.Mesh.Spans.TryGetValue("materials", out var span)) throw new InvalidDataException("The mesh has no material list to change.");
            var data = builder.ExportData(meshExport.Index).ToArray();
            int at = span.Start - meshExport.Offset;
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
            foreach (var (slot, path) in files)
            {
                if (slot < 0 || slot >= count) continue;
                int import = builder.Import(path, path.EndsWith(".w2mg", StringComparison.OrdinalIgnoreCase) ? "CMaterialGraph" : "CMaterialInstance");
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at + 4 + slot * 4), -(import + 1));
            }
            builder.ReplaceExport(meshExport.Index, data);
        }
        return builder.Build();
    }

    /// <summary>Puts new parameters after an instance's properties, which are kept as they were.</summary>
    private static void Replace(Cr2wBuilder builder, RedObject instance, IEnumerable<MaterialParameter> parameters)
    {
        var export = instance.Export ?? throw new InvalidDataException("The material's object wasn't found.");
        var old = builder.ExportData(export.Index);
        int properties = old.Length - instance.Tail.Length;
        var tail = ParametersData(builder, parameters, sameFile: true);
        var data = new byte[properties + tail.Length];
        old.AsSpan(0, properties).CopyTo(data);
        tail.CopyTo(data, properties);
        builder.ReplaceExport(export.Index, data);
    }
}
