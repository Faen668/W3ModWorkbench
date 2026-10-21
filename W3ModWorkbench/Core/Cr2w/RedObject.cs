using System.Buffers.Binary;
using System.IO;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>
/// An object or struct read from a CR2W file: its class and its properties by name. A property's value is a bool, a
/// number, a string (names and strings alike), a <see cref="RedHandle"/>, a <see cref="RedObject"/> for a struct, a list
/// for an array, a <see cref="RedEnum"/>, a <see cref="RedBufferRef"/>, or the raw bytes when the type isn't understood.
/// </summary>
public sealed class RedObject
{
    public string ClassName { get; }
    public List<(string Name, string Type, object? Value)> Properties { get; } = [];

    /// <summary>Where each property's value is in the file: from its first byte to the byte after it.</summary>
    public Dictionary<string, (int Start, int End)> Spans { get; } = [];

    /// <summary>The object in the file's export table, for objects (not structs).</summary>
    public Cr2wExport? Export { get; internal set; }

    /// <summary>What the object's class keeps after its properties: a material instance's parameters, a mesh's bones.</summary>
    public byte[] Tail { get; internal set; } = [];

    public RedObject(string className) => ClassName = className;

    public object? this[string name] => Properties.FirstOrDefault(p => p.Name == name).Value;

    public RedObject? Struct(string name) => this[name] as RedObject;
    public List<object?> Array(string name) => this[name] as List<object?> ?? [];
    public byte[] Bytes(string name) => this[name] as byte[] ?? [];

    public float Float(string name, float otherwise = 0) => this[name] switch
    {
        float f => f,
        double d => (float)d,
        IConvertible c and not string and not bool => Convert.ToSingle(c),
        _ => otherwise,
    };

    public long Integer(string name, long otherwise = 0) => this[name] switch
    {
        null or string or float or double or bool => otherwise,
        IConvertible c => Convert.ToInt64(c),
        _ => otherwise,
    };

    /// <summary>A Vector struct's X, Y and Z, or the fallback when it isn't there.</summary>
    public System.Numerics.Vector3 Vector(string name, System.Numerics.Vector3 otherwise = default) =>
        Struct(name) is { } v ? new(v.Float("X", otherwise.X), v.Float("Y", otherwise.Y), v.Float("Z", otherwise.Z)) : otherwise;

    public override string ToString() => ClassName;
}

/// <summary>A reference to another object: one in this file (export), one in another file (import), or nothing.</summary>
public readonly record struct RedHandle(int Raw, bool Soft)
{
    /// <summary>The export it points at: a handle's positive value, 1-based.</summary>
    public int? Export => !Soft && Raw > 0 ? Raw - 1 : null;

    /// <summary>The import it points at: a handle's negative value, or a soft reference's index, both 1-based.</summary>
    public int? Import => Soft ? (Raw > 0 ? Raw - 1 : null) : Raw < 0 ? -Raw - 1 : null;
}

/// <summary>A parameter of a material instance: its name, type and value, and the value's bytes as the file has them.</summary>
public sealed record RedParameter(string Name, string Type, object? Value, byte[] Raw);

/// <summary>An enum value, or the values of a flags enum, as their names.</summary>
public sealed record RedEnum(IReadOnlyList<string> Values)
{
    public override string ToString() => string.Join(" | ", Values);
}

/// <summary>A deferred data buffer: the number of a buffer in the file's buffer table, so of its <c>.N.buffer</c> file.</summary>
public readonly record struct RedBufferRef(int Index);

/// <summary>
/// Reads properties as the engine saves them. An object or struct is a zero byte, then (name, type, size, value) until a
/// name of 0; the size counts its own four bytes. Arrays are a count and that many values, untagged. Types are known by
/// their names (<c>Float</c>, <c>array:2,0,handle:IMaterial</c>…), so a property of a type this doesn't understand
/// is kept as its bytes and skipped by its size. Files in the older layout (154 and before) leave out the zero byte,
/// have two more bytes after a property's type before 139, and may name types the Witcher 2's way (<c>@Float</c>
/// for an array, <c>*CEntity</c> for a handle).
/// </summary>
internal sealed class RedReader
{
    private readonly Cr2wFile _file;
    private readonly byte[] _data;
    private readonly int _end;
    private readonly bool _marker, _pad;
    public int Position;

    /// <param name="otherMarker">Read objects the other way about the zero byte from what their version usually has.</param>
    public RedReader(Cr2wFile file, byte[] data, int start, int end, bool otherMarker = false)
    {
        _file = file;
        _data = data;
        Position = start;
        _end = end;
        _marker = Cr2wLegacy.HasObjectMarker(file.Version) != otherMarker;
        _pad = Cr2wLegacy.HasPropertyPad(file.Version);
    }

    private string Name(int index) => index >= 0 && index < _file.Names.Count ? _file.Names[index] : $"#{index}";

    public RedObject ReadObject(string className, int end = -1)
    {
        if (end < 0) end = _end;
        var result = new RedObject(className);
        if (Position >= end) return result;
        if (_marker)
        {
            Need(1);
            if (_data[Position] != 0) throw new InvalidDataException(L.T("RedObject.DoesntStartWayProperties", className));
            Position++;
        }
        while (Position + 2 <= end)
        {
            int name = U16();
            if (name == 0) break;
            int type = U16();
            if (_pad) U16();
            int size = I32();
            int valueEnd = Position - 4 + size;
            if (size < 4 || valueEnd > end) throw new InvalidDataException(L.T("RedObject.PropertyLongerThanObject", className));
            string typeName = Name(type);
            object? value;
            int start = Position;
            try
            {
                value = ReadValue(typeName, valueEnd);
                if (Position != valueEnd) throw new InvalidDataException();
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                value = _data.AsSpan(start, valueEnd - start).ToArray();
            }
            Position = valueEnd;
            result.Properties.Add((Name(name), typeName, value));
            result.Spans.TryAdd(Name(name), (start, valueEnd));
        }
        return result;
    }

    /// <summary>
    /// A counted list of (size, name, type, value), the way a material instance keeps its parameters after its properties.
    /// The size counts its own four bytes and the name and type.
    /// </summary>
    public List<RedParameter> ReadParameters()
    {
        var result = new List<RedParameter>();
        if (Position + 4 > _end) return result;
        int count = I32();
        for (int i = 0; i < count && Position + 8 <= _end; i++)
        {
            int size = I32();
            int name = U16(), type = U16();
            int valueEnd = Position - 8 + size;
            if (size < 8 || valueEnd > _end) break;
            string typeName = Name(type);
            int start = Position;
            object? value;
            try
            {
                value = ReadValue(typeName, valueEnd);
                if (Position != valueEnd) throw new InvalidDataException();
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                value = _data.AsSpan(start, valueEnd - start).ToArray();
            }
            Position = valueEnd;
            result.Add(new RedParameter(Name(name), typeName, value, _data.AsSpan(start, valueEnd - start).ToArray()));
        }
        return result;
    }

    /// <summary>
    /// A counted list of (size, class, properties). The size counts its own four bytes and the class's two. An object
    /// that can't be read is skipped by its size.
    /// </summary>
    public List<RedObject> ReadSizedObjects()
    {
        var result = new List<RedObject>();
        int count = I32();
        if (count < 0 || count > 100_000) throw new InvalidDataException(L.T("RedObject.ListsLengthMakesNo"));
        for (int i = 0; i < count && Position + 6 <= _end; i++)
        {
            int start = Position;
            int size = I32();
            int objectEnd = start + size;
            if (size < 6 || objectEnd > _end) throw new InvalidDataException(L.T("RedObject.ObjectLongerThanList"));
            string className = Name(U16());
            try { result.Add(ReadObject(className, objectEnd)); }
            catch (InvalidDataException) { result.Add(new RedObject(className)); }
            Position = objectEnd;
        }
        return result;
    }

    /// <summary>
    /// An array of the older layout (154 and before, typed @T) that the reading of properties keeps as its bytes: its
    /// count, then the name of its elements' type and 0xFFFF, then the elements. A rig's bones are kept so.
    /// </summary>
    public List<object?> ReadOldArray(string elementType)
    {
        int count = I32();
        if (count < 0 || count > 50_000_000) throw new InvalidDataException(L.T("RedObject.ArraysLengthMakesNo"));
        U16();
        U16();
        var list = new List<object?>(Math.Min(count, 4096));
        for (int i = 0; i < count; i++) list.Add(ReadValue(elementType, -1));
        if (Position != _end) throw new InvalidDataException(L.T("RedObject.ArrayDoesntFillProperty"));
        return list;
    }

    /// <summary>One value of a type. <paramref name="end"/> is where a tagged value ends, or -1 inside an array.</summary>
    private object? ReadValue(string type, int end)
    {
        switch (type)
        {
            case "Bool": return Byte() != 0;
            case "Int8": return (sbyte)Byte();
            case "Uint8": return Byte();
            case "Int16": return (short)U16();
            case "Uint16": return (ushort)U16();
            case "Int32" or "Int": return I32();
            case "Uint32" or "Uint": return (uint)I32();
            case "Int64": Need(8); Position += 8; return BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(Position - 8));
            case "Uint64": Need(8); Position += 8; return BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(Position - 8));
            case "Float": Need(4); Position += 4; return BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(Position - 4));
            case "Double": Need(8); Position += 8; return BinaryPrimitives.ReadDoubleLittleEndian(_data.AsSpan(Position - 8));
            case "CName": return Name(U16());
            case "String": return LengthPrefixedString();
            case "LocalizedString": return (uint)I32();
            case "DeferredDataBuffer": return new RedBufferRef(U16());
            case "CGUID": Need(16); Position += 16; return new Guid(_data.AsSpan(Position - 16, 16));
        }
        if (type.StartsWith("handle:", StringComparison.Ordinal) || type.StartsWith("ptr:", StringComparison.Ordinal) || type.StartsWith('*') || type.StartsWith('#'))
            return new RedHandle(I32(), false);
        if (type.StartsWith("soft:", StringComparison.Ordinal) || type.StartsWith('~'))
            return new RedHandle(U16(), true);
        if (type.StartsWith("array:", StringComparison.Ordinal) || type.StartsWith("static:", StringComparison.Ordinal) || type.StartsWith('@'))
        {
            // array:2,0,T and static:N,T; the element type is after the last comma that isn't inside it
            string element = ElementType(type);
            int count = I32();
            if (count < 0 || count > 50_000_000) throw new InvalidDataException(L.T("RedObject.ArraysLengthMakesNo"));
            // bytes, signed or not, are kept as they are: an animation's data is half a megabyte of Int8
            if (element is "Uint8" or "Int8")
            {
                Need(count);
                Position += count;
                return _data.AsSpan(Position - count, count).ToArray();
            }
            var list = new List<object?>(Math.Min(count, 4096));
            for (int i = 0; i < count; i++) list.Add(ReadValue(element, -1));
            return list;
        }
        if (IsEnum(type))
        {
            // a plain enum is one name; a flags enum is names until a 0
            if (end >= 0 && end - Position == 2) return new RedEnum([Name(U16())]);
            if (end < 0) return new RedEnum([Name(U16())]);
            var values = new List<string>();
            while (Position + 2 <= end)
            {
                int v = U16();
                if (v == 0) break;
                values.Add(Name(v));
            }
            return new RedEnum(values);
        }
        if (_marker && end >= 0 && end - Position >= 1 && _data[Position] != 0)
            throw new InvalidDataException(); // not a struct: kept as its bytes
        return ReadObject(type, end >= 0 ? end : _end);
    }

    /// <summary>Engine enums are named E and a capital: EMeshVertexType, EMaterialVertexFactory.</summary>
    private static bool IsEnum(string type) => type.Length > 1 && type[0] == 'E' && char.IsUpper(type[1]) && !type.Contains(':');

    private static string ElementType(string type)
    {
        // "array:2,0,handle:IMaterial" → "handle:IMaterial"; "array:2,0,array:2,0,Float" → "array:2,0,Float"; "@*CEntity" → "*CEntity"
        if (type.StartsWith('@')) return type[1..];
        int colon = type.IndexOf(':');
        string rest = type[(colon + 1)..];
        for (int i = 0, commas = type.StartsWith("static:") ? 1 : 2; i < rest.Length; i++)
            if (rest[i] == ',' && --commas == 0) return rest[(i + 1)..];
        return rest;
    }

    /// <summary>
    /// A string with its length in front: the first byte's top bit says it's 8-bit text (else UTF-16), the next bit that
    /// the length goes on in the following bytes, seven bits at a time.
    /// </summary>
    private string LengthPrefixedString()
    {
        int first = Byte();
        bool narrow = (first & 0x80) != 0;
        int length = first & 0x3F;
        if ((first & 0x40) != 0)
        {
            int shift = 6, b;
            do
            {
                b = Byte();
                length |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0 && shift < 32);
        }
        int bytes = narrow ? length : length * 2;
        Need(bytes);
        Position += bytes;
        return narrow ? Encoding.Latin1.GetString(_data, Position - bytes, bytes) : Encoding.Unicode.GetString(_data, Position - bytes, bytes);
    }

    private void Need(int count)
    {
        if (count < 0 || Position + count > _data.Length) throw new InvalidDataException(L.T("RedObject.ValueRunsPastEnd"));
    }

    private int Byte() { Need(1); return _data[Position++]; }

    private int U16() { Need(2); Position += 2; return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position - 2)); }

    private int I32() { Need(4); Position += 4; return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position - 4)); }
}
