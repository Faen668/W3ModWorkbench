using System.Buffers.Binary;
using System.Text;

namespace W3ModWorkbench.Core.Hdf5;

/// <summary>The kinds of data an HDF5 file keeps that REDkit's files use.</summary>
public enum Hdf5Class { Integer, Float, String, VariableString, Sequence, Compound }

/// <summary>A member of a compound type: its name, where it is in each element, and its type.</summary>
public sealed record Hdf5Member(string Name, int Offset, Hdf5Type Type);

/// <summary>An HDF5 datatype: its kind and size in bytes, and for some kinds more (a compound's members, a sequence's base).</summary>
public sealed record Hdf5Type(Hdf5Class Class, int Size, bool Signed = false, Hdf5Type? Base = null, IReadOnlyList<Hdf5Member>? Members = null)
{
    public static readonly Hdf5Type Float32 = new(Hdf5Class.Float, 4);
    public static readonly Hdf5Type Float64 = new(Hdf5Class.Float, 8);
    public static readonly Hdf5Type Int32 = new(Hdf5Class.Integer, 4, Signed: true);
    public static readonly Hdf5Type Int64 = new(Hdf5Class.Integer, 8, Signed: true);
    public static readonly Hdf5Type Text = new(Hdf5Class.VariableString, 16, Base: new Hdf5Type(Hdf5Class.Integer, 1));

    /// <summary>A compound of floats, one for each name, in that order (as REDkit's tables of keys are).</summary>
    public static Hdf5Type FloatRow(IReadOnlyList<string> names, int size = 4) =>
        new(Hdf5Class.Compound, names.Count * size, Members: names.Select((n, i) => new Hdf5Member(n, i * size, new Hdf5Type(Hdf5Class.Float, size))).ToList());
}

/// <summary>
/// Values of one type in a shape (none for a single value): their bytes as the file keeps them, and the text of
/// variable-length strings, which the file keeps elsewhere.
/// </summary>
public sealed class Hdf5Data(Hdf5Type type, long[] shape, byte[] raw, string[]? strings = null)
{
    public Hdf5Type Type { get; } = type;
    public long[] Shape { get; } = shape;
    public byte[] Raw { get; } = raw;
    public string[]? StringValues { get; } = strings;

    public int Count => Type.Size == 0 ? 0 : Raw.Length / Type.Size;

    /// <summary>Each element as a number (a float or an integer).</summary>
    public double[] Numbers() => Enumerable.Range(0, Count).Select(i => Number(Raw, i * Type.Size, Type)).ToArray();

    /// <summary>Each element as text (a fixed or variable-length string).</summary>
    public string[] Strings() => StringValues ?? Enumerable.Range(0, Count)
        .Select(i => Encoding.UTF8.GetString(Raw, i * Type.Size, Type.Size).TrimEnd('\0', ' ')).ToArray();

    /// <summary>One member of each element of a compound, as numbers.</summary>
    public double[] Field(string name)
    {
        var member = Type.Members?.FirstOrDefault(m => m.Name == name) ?? throw new KeyNotFoundException(name);
        return Enumerable.Range(0, Count).Select(i => Number(Raw, i * Type.Size + member.Offset, member.Type)).ToArray();
    }

    public double Single => Numbers().FirstOrDefault();

    public static double Number(byte[] raw, int at, Hdf5Type type) => (type.Class, type.Size, type.Signed) switch
    {
        (Hdf5Class.Float, 4, _) => BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at)),
        (Hdf5Class.Float, 8, _) => BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(at)),
        (Hdf5Class.Integer, 1, true) => (sbyte)raw[at],
        (Hdf5Class.Integer, 1, false) => raw[at],
        (Hdf5Class.Integer, 2, true) => BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(at)),
        (Hdf5Class.Integer, 2, false) => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at)),
        (Hdf5Class.Integer, 4, true) => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at)),
        (Hdf5Class.Integer, 4, false) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at)),
        (Hdf5Class.Integer, 8, true) => BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(at)),
        (Hdf5Class.Integer, 8, false) => BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(at)),
        _ => double.NaN,
    };

    // ---------------------------------------------------------------- making values to write

    public static Hdf5Data Scalar(double value, Hdf5Type type) => new(type, [], Bytes([value], type));

    public static Hdf5Data Array(IReadOnlyList<double> values, Hdf5Type type, params long[] shape) =>
        new(type, shape.Length == 0 ? [values.Count] : shape, Bytes(values, type));

    public static Hdf5Data Texts(IReadOnlyList<string> values, params long[] shape) =>
        new(Hdf5Type.Text, shape.Length == 0 ? [values.Count] : shape, new byte[values.Count * Hdf5Type.Text.Size], [.. values]);

    /// <summary>Rows of a compound of floats: each row's values in its members' order.</summary>
    public static Hdf5Data Rows(Hdf5Type compound, IReadOnlyList<IReadOnlyList<double>> rows)
    {
        var raw = new byte[rows.Count * compound.Size];
        for (int r = 0; r < rows.Count; r++)
            for (int m = 0; m < compound.Members!.Count; m++)
                Write(raw, r * compound.Size + compound.Members[m].Offset, rows[r][m], compound.Members[m].Type);
        return new(compound, [rows.Count], raw);
    }

    private static byte[] Bytes(IReadOnlyList<double> values, Hdf5Type type)
    {
        var raw = new byte[values.Count * type.Size];
        for (int i = 0; i < values.Count; i++) Write(raw, i * type.Size, values[i], type);
        return raw;
    }

    private static void Write(byte[] raw, int at, double value, Hdf5Type type)
    {
        var s = raw.AsSpan(at);
        switch (type.Class, type.Size)
        {
            case (Hdf5Class.Float, 4): BinaryPrimitives.WriteSingleLittleEndian(s, (float)value); break;
            case (Hdf5Class.Float, 8): BinaryPrimitives.WriteDoubleLittleEndian(s, value); break;
            case (Hdf5Class.Integer, 1): s[0] = (byte)(long)value; break;
            case (Hdf5Class.Integer, 2): BinaryPrimitives.WriteInt16LittleEndian(s, (short)(long)value); break;
            case (Hdf5Class.Integer, 4): BinaryPrimitives.WriteInt32LittleEndian(s, (int)(long)value); break;
            case (Hdf5Class.Integer, 8): BinaryPrimitives.WriteInt64LittleEndian(s, (long)value); break;
        }
    }
}

/// <summary>A dataset: a named array of values, with attributes.</summary>
public sealed class Hdf5Dataset(string name, Hdf5Data data)
{
    public string Name { get; } = name;
    public Hdf5Data Data { get; } = data;
    public Dictionary<string, Hdf5Data> Attributes { get; } = [];
}

/// <summary>A group: groups and datasets by name, with attributes.</summary>
public sealed class Hdf5Group(string name)
{
    public string Name { get; } = name;
    public List<Hdf5Group> Groups { get; } = [];
    public List<Hdf5Dataset> Datasets { get; } = [];
    public Dictionary<string, Hdf5Data> Attributes { get; } = [];

    public Hdf5Group? Group(string name) => Groups.FirstOrDefault(g => g.Name == name);
    public Hdf5Dataset? Dataset(string name) => Datasets.FirstOrDefault(d => d.Name == name);

    /// <summary>A group by its path from here (a/b/c), or null.</summary>
    public Hdf5Group? Path(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate((Hdf5Group?)this, (g, part) => g?.Group(part));

    /// <summary>The group at a path from here, made with any groups missing.</summary>
    public Hdf5Group Make(string path)
    {
        var g = this;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = g.Group(part);
            if (next is null) g.Groups.Add(next = new Hdf5Group(part));
            g = next;
        }
        return g;
    }
}
