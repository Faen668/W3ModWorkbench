// loc-ignore-start
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>What an ID in a bank is the hash of, so a name can be looked for: Wwise keeps names only as FNV-1 hashes.</summary>
public enum BankHash { None, Event, Bus, Bank, Variable, Value, GameParameter, Trigger, Effect, Language }

/// <summary>
/// One value read from a Wwise bank, under the name Wwise's own code gives it (<c>bIsLooping</c>, <c>ulStateGroupID</c>…),
/// or a group of them (a node or a list). A bank's objects are read into trees of these, so every byte is accounted for
/// and shown.
/// </summary>
public sealed class BankField
{
    public string Name { get; internal set; }

    /// <summary>u8, u16, u32, s8, s16, s32, f32, d64, tid (an ID), var (a variable-length number), uni (a number or a
    /// float), gap (bytes kept as they are), str, node, list or bit (a part of the value above it).</summary>
    public string Type { get; }

    public object? Value { get; internal set; }
    public int Offset { get; }
    public int Size { get; internal set; }

    /// <summary>The name Wwise gives the value (an enum's), when it has one.</summary>
    public string? Meaning { get; internal set; }

    /// <summary>The unit the value is in (dB, ms, Hz…), when it has one.</summary>
    public string? Unit { get; internal set; }

    /// <summary>What the ID is the hash of, when it's an ID.</summary>
    public BankHash Hash { get; internal set; }

    /// <summary>Why reading stopped here, when it did.</summary>
    public string? Error { get; internal set; }

    public BankField? Parent { get; internal set; }
    public List<BankField> Children { get; } = [];

    internal BankField(string name, string type, object? value, int offset, int size)
    {
        Name = name;
        Type = type;
        Value = value;
        Offset = offset;
        Size = size;
    }

    public bool IsGroup => Type is "node" or "list";

    /// <summary>The value as a whole number (0 when it isn't one).</summary>
    public long Long => Value switch { long l => l, int i => i, uint u => u, ulong ul => (long)ul, _ => 0 };

    /// <summary>The value as a number, whole or not.</summary>
    public double Number => Value switch { float f => f, double d => d, _ => Long };

    /// <summary>The value as text: a number (hex for IDs and flags), with Wwise's name for it when it has one.</summary>
    public string Text
    {
        get
        {
            string value = Value switch
            {
                null => "",
                float f => f.ToString("0.######", CultureInfo.InvariantCulture),
                double d => d.ToString("0.######", CultureInfo.InvariantCulture),
                string s => s,
                byte[] b => b.Length <= 32 ? Convert.ToHexString(b) : Convert.ToHexString(b, 0, 32) + "…",
                _ when Type == "tid" && Long == -1 => "-1",
                _ => Long.ToString(CultureInfo.InvariantCulture),
            };
            if (Unit is not null) value += " " + Unit;
            return Meaning is null ? value : $"{value} [{Meaning}]";
        }
    }

    public BankField? Find(string name) => Children.FirstOrDefault(c => c.Name == name);

    /// <summary>The first field of that name anywhere under this one.</summary>
    public BankField? FindDeep(string name)
    {
        foreach (var c in Children)
        {
            if (c.Name == name) return c;
            if (c.FindDeep(name) is { } found) return found;
        }
        return null;
    }

    /// <summary>Every field of that name under this one.</summary>
    public IEnumerable<BankField> FindAll(string name)
    {
        foreach (var c in Children)
        {
            if (c.Name == name) yield return c;
            foreach (var deeper in c.FindAll(name)) yield return deeper;
        }
    }

    /// <summary>A part of this value (some of its bits) as a field of its own under it.</summary>
    internal BankField Bit(string name, int shift, long mask = 1, IReadOnlyDictionary<long, string>? names = null)
    {
        long value = (Long >> shift) & mask;
        var bit = new BankField(name, "bit", value, Offset, 0) { Parent = this };
        if (names is not null) bit.Meaning = names.GetValueOrDefault(value, "?");
        Children.Add(bit);
        return this;
    }

    internal BankField Named(IReadOnlyDictionary<long, string>? names)
    {
        if (names is not null) Meaning = names.GetValueOrDefault(Long, "?");
        return this;
    }

    internal BankField Fnv(BankHash hash)
    {
        Hash = hash;
        return this;
    }

    public override string ToString() => $"{Name} = {Text}";

    /// <summary>The tree under this field as indented text, one value a line.</summary>
    public string Dump(Func<BankField, string?>? nameOf = null)
    {
        var sb = new StringBuilder();
        void Walk(BankField f, int depth)
        {
            sb.Append(' ', depth * 2).Append(f.Name);
            if (!f.IsGroup)
            {
                sb.Append(" = ").Append(f.Text);
                if (nameOf?.Invoke(f) is { } known) sb.Append(" (").Append(known).Append(')');
            }
            if (f.Error is not null) sb.Append("  !! ").Append(f.Error);
            sb.Append('\n');
            foreach (var c in f.Children) Walk(c, depth + 1);
        }
        Walk(this, 0);
        return sb.ToString();
    }
}

/// <summary>A bank object couldn't be read: something in it isn't laid out as its version's are.</summary>
public sealed class BankReadException(string message) : Exception(message);

/// <summary>
/// Reads values from a bank into a <see cref="BankField"/> tree, each under its name, never past the end of the object
/// being read. The same calls also write: given a source tree (<see cref="Source"/>) and a cursor that writes, each
/// value is taken from the source's next field of that name and written in the cursor's version's layout, so one
/// description of each layout serves both reading and writing.
/// </summary>
internal sealed class BankNode
{
    private readonly BankCursor _r;
    public BankField Field { get; }
    public int Version => _r.Version;
    public bool Feedback => _r.Feedback;
    public bool Writing => _r.Writing;

    /// <summary>When writing: the tree the values come from (the node matching this one).</summary>
    public BankField? Source { get; }

    private readonly Dictionary<string, int> _taken = [];

    /// <summary>The value read (or written) last, which decides what comes next.</summary>
    public long Last { get; private set; }

    public BankNode(BankCursor reader, BankField field, BankField? source = null)
    {
        _r = reader;
        Field = field;
        Source = source;
    }

    private BankField Add(string name, string type, object? value, int offset, int size)
    {
        var f = new BankField(name, type, value, offset, size) { Parent = Field };
        Field.Children.Add(f);
        return f;
    }

    private int Take(int size)
    {
        int at = _r.Position;
        if (at + size > _r.Limit) throw new BankReadException($"reading past the object at 0x{at:X} (+{size})");
        _r.Position += size;
        return at;
    }

    /// <summary>When writing: the source's next field of this name (the first, then the second… of that name).</summary>
    private BankField From(string name)
    {
        if (Source is null) throw new BankReadException($"nothing to write {name} from");
        int n = _taken.GetValueOrDefault(name);
        _taken[name] = n + 1;
        int seen = 0;
        foreach (var c in Source.Children)
            if (c.Name == name && seen++ == n) return c;
        throw new BankReadException($"{Source.Name} has no {(n == 0 ? "" : $"{n + 1}th ")}{name} to write");
    }

    /// <summary>When writing: puts a value's bytes out, and records the field as reading it back would.</summary>
    private BankField Put(string name, string type, BankField from, ReadOnlySpan<byte> bytes, object? value)
    {
        int at = _r.Out!.Count;
        foreach (byte b in bytes) _r.Out.Add(b);
        Last = value switch { long l => l, float x => (long)x, double d => (long)d, _ => 0 };
        var field = Add(name, type, value, at, bytes.Length);
        field.Meaning = from.Meaning;
        field.Unit = from.Unit;
        field.Hash = from.Hash;
        return field;
    }

    private static long WholeOf(BankField f) => f.Value switch
    {
        long l => l, int i => i, uint u => u, float x => BitConverter.SingleToInt32Bits(x), _ => 0,
    };

    private BankField Small(string name, string type, int size)
    {
        if (_r.Writing)
        {
            var from = From(name);
            long v = WholeOf(from);
            Span<byte> b = stackalloc byte[size];
            if (size == 1) b[0] = (byte)v;
            else BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
            return Put(name, type, from, b, v);
        }
        int at = Take(size);
        Last = type switch
        {
            "u8" => _r.Data[at],
            "s8" => (sbyte)_r.Data[at],
            "u16" => BinaryPrimitives.ReadUInt16LittleEndian(_r.Data.AsSpan(at)),
            _ => BinaryPrimitives.ReadInt16LittleEndian(_r.Data.AsSpan(at)),
        };
        return Add(name, type, Last, at, size);
    }

    public BankField U8(string name) => Small(name, "u8", 1);
    public BankField S8(string name) => Small(name, "s8", 1);
    public BankField U16(string name) => Small(name, "u16", 2);
    public BankField S16(string name) => Small(name, "s16", 2);

    public BankField U32(string name) => Word(name, "u32");

    /// <summary>An object's or a name's ID (a u32).</summary>
    public BankField Tid(string name) => Word(name, "tid");

    public BankField S32(string name) => Word(name, "s32");

    private BankField Word(string name, string type)
    {
        if (_r.Writing)
        {
            var from = From(name);
            Span<byte> b = stackalloc byte[4];
            // a value read as a float (a property's) goes back as its bits
            if (from.Value is float x) BinaryPrimitives.WriteSingleLittleEndian(b, x);
            else BinaryPrimitives.WriteUInt32LittleEndian(b, unchecked((uint)WholeOf(from)));
            return Put(name, type, from, b, from.Value is float ? from.Value : WholeOf(from));
        }
        int at = Take(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_r.Data.AsSpan(at));
        Last = type == "s32" ? (int)v : v == 0xFFFFFFFF ? -1 : v;
        return Add(name, type, Last, at, 4);
    }

    public BankField F32(string name)
    {
        if (_r.Writing)
        {
            var from = From(name);
            Span<byte> b = stackalloc byte[4];
            float v = from.Value is float x ? x : from.Value is double d ? (float)d : WholeOf(from);
            BinaryPrimitives.WriteSingleLittleEndian(b, v);
            return Put(name, "f32", from, b, v);
        }
        int at = Take(4);
        float value = BinaryPrimitives.ReadSingleLittleEndian(_r.Data.AsSpan(at));
        Last = (long)value;
        return Add(name, "f32", value, at, 4);
    }

    public BankField D64(string name)
    {
        if (_r.Writing)
        {
            var from = From(name);
            Span<byte> b = stackalloc byte[8];
            double v = from.Value is double d ? d : from.Value is float x ? x : WholeOf(from);
            BinaryPrimitives.WriteDoubleLittleEndian(b, v);
            return Put(name, "d64", from, b, v);
        }
        int at = Take(8);
        double value = BinaryPrimitives.ReadDoubleLittleEndian(_r.Data.AsSpan(at));
        Last = (long)value;
        return Add(name, "d64", value, at, 8);
    }

    /// <summary>A whole number of 7-bit parts, the top bit of each byte saying another follows.</summary>
    public BankField Var(string name)
    {
        if (_r.Writing)
        {
            var from = From(name);
            long v = WholeOf(from);
            var groups = new List<byte>();
            do { groups.Insert(0, (byte)(v & 0x7F)); v >>= 7; } while (v > 0);
            for (int i = 0; i < groups.Count - 1; i++) groups[i] |= 0x80;
            return Put(name, "var", from, groups.ToArray(), WholeOf(from));
        }
        int at0 = _r.Position;
        long value = 0;
        for (int i = 0; ; i++)
        {
            if (i >= 10) throw new BankReadException($"variable-length number too long at 0x{at0:X}");
            int b = _r.Data[Take(1)];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        Last = value;
        return Add(name, "var", value, at0, _r.Position - at0);
    }

    /// <summary>Bytes kept as they are.</summary>
    public BankField Gap(string name, long size)
    {
        if (_r.Writing)
        {
            var from = From(name);
            var bytes = from.Value as byte[] ?? new byte[size];
            if (bytes.Length != size) throw new BankReadException($"{name} has {bytes.Length} bytes to write, not {size}");
            return Put(name, "gap", from, bytes, bytes);
        }
        if (size < 0 || size > int.MaxValue) throw new BankReadException($"unlikely size {size}");
        int at = Take((int)size);
        Last = size;
        return Add(name, "gap", _r.Data[at..(at + (int)size)], at, (int)size);
    }

    public BankField Str(string name, long size)
    {
        if (_r.Writing)
        {
            var from = From(name);
            var bytes = Encoding.UTF8.GetBytes(from.Value as string ?? "");
            if (bytes.Length != size) throw new BankReadException($"{name} is {bytes.Length} bytes, not {size}");
            return Put(name, "str", from, bytes, from.Value);
        }
        if (size > 255) throw new BankReadException($"unlikely string size {size}");
        int at = Take((int)size);
        return Add(name, "str", Encoding.UTF8.GetString(_r.Data, at, (int)size), at, (int)size);
    }

    /// <summary>A string ending at a zero byte.</summary>
    public BankField Stz(string name)
    {
        if (_r.Writing)
        {
            var from = From(name);
            return Put(name, "str", from, [.. Encoding.UTF8.GetBytes(from.Value as string ?? ""), 0], from.Value);
        }
        int at = _r.Position;
        int end = Array.IndexOf(_r.Data, (byte)0, at, Math.Max(0, _r.Limit - at));
        if (end < 0 || end - at > 255) throw new BankReadException($"string without an end at 0x{at:X}");
        Take(end - at + 1);
        return Add(name, "str", Encoding.UTF8.GetString(_r.Data, at, end - at), at, end - at + 1);
    }

    /// <summary>A group of values under a name of their own.</summary>
    public BankNode Node(string name)
    {
        int at = _r.Writing ? _r.Out!.Count : _r.Position;
        var source = _r.Writing ? From(name) : null;
        return new(_r, Add(name, "node", null, at, 0), source);
    }

    /// <summary>A list of <paramref name="count"/> items, each a node named <paramref name="item"/>.</summary>
    public List<BankNode> List(string name, string item, long count)
    {
        if (count < 0 || count > 0x30000) throw new BankReadException($"unlikely count {count}");
        var list = Node(name);
        list.Field.Value = count;
        if (_r.Writing && list.Source!.Children.Count(c => c.Name == item) != count)
            throw new BankReadException($"{name} says {count} but has {list.Source.Children.Count(c => c.Name == item)}");
        var items = new List<BankNode>((int)count);
        for (int i = 0; i < count; i++) items.Add(list.Node(item));
        return items;
    }

    /// <summary>
    /// Reads the next <paramref name="size"/> bytes with <paramref name="read"/>, which must take exactly that many:
    /// a block whose layout is known, so a wrong layout shows rather than spills into what follows.
    /// </summary>
    public void Within(long size, Action read)
    {
        if (_r.Writing)
        {
            int start = _r.Out!.Count;
            read();
            if (_r.Out.Count - start != size) throw new BankReadException($"block wrote {_r.Out.Count - start} of its {size} bytes");
            return;
        }
        int outer = _r.Limit, end = _r.Position + (int)size;
        if (size < 0 || end > outer) throw new BankReadException($"block of {size} bytes runs past its object");
        _r.Limit = end;
        try { read(); }
        finally { _r.Limit = outer; }
        if (_r.Position != end) throw new BankReadException($"block read {_r.Position - (end - (int)size)} of its {size} bytes");
    }
}

/// <summary>
/// Where reading a bank is (its bytes, the position, how far the object being read goes, its version), or, when
/// writing, the bytes written so far.
/// </summary>
internal sealed class BankCursor(byte[] data, int version, bool feedback, List<byte>? output = null)
{
    public byte[] Data { get; } = data;
    public int Position { get; set; }
    public int Limit { get; set; } = data.Length;
    public int Version { get; } = version;
    public bool Feedback { get; } = feedback;

    /// <summary>When writing: the bytes written so far.</summary>
    public List<byte>? Out { get; } = output;
    public bool Writing => Out is not null;
}
// loc-ignore-end
