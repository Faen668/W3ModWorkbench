using System.Buffers.Binary;
using System.IO;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Hdf5;

/// <summary>
/// Writes HDF5 files in the form the HDF5 library (through h5py) writes REDkit's <c>.re</c> files: superblock version 0
/// with eight-byte addresses, version 1 object headers, each group a symbol table (a B-tree of symbol nodes, its
/// names in a local heap), datasets contiguous, attributes in version 1 messages, and variable-length strings in a
/// global heap. Each message is encoded as the library encodes it, checked against REDkit's own files.
/// </summary>
public static class Hdf5Writer
{
    private const int GroupLeafK = 4, GroupInternalK = 16;
    private const ulong Undefined = ulong.MaxValue;

    public static byte[] Write(Hdf5Group root)
    {
        var w = new Output();
        w.Bytes(new byte[96]); // the superblock, filled in last
        var (header, btree, heap) = w.Group(root);
        var s = w.Buffer;
        byte[] signature = [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(s, 0);
        // versions (superblock, free space, root group, reserved, shared header), sizes of addresses and lengths, K values
        s[13] = 8;
        s[14] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(16), GroupLeafK);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(18), GroupInternalK);
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(24), 0); // base address
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(32), Undefined); // free space
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(40), (ulong)s.Length); // end of file
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(48), Undefined); // driver information
        // the root group's symbol table entry: no name, its header, and its B-tree and heap cached
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(64), (ulong)header);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(72), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(80), (ulong)btree);
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(88), (ulong)heap);
        return s;
    }

    /// <summary>The file as it's written: each object goes at the end, before anything that points at it.</summary>
    private sealed class Output
    {
        private readonly MemoryStream _stream = new();
        public byte[] Buffer => _stream.ToArray();
        public long Position => _stream.Length;

        public long Bytes(ReadOnlySpan<byte> bytes)
        {
            long at = _stream.Length;
            _stream.Position = at;
            _stream.Write(bytes);
            return at;
        }

        /// <summary>Pads to an eight-byte boundary, where the library starts its objects.</summary>
        public void Align()
        {
            while (_stream.Length % 8 != 0) _stream.WriteByte(0);
        }

        // ---------------------------------------------------------------- groups

        /// <summary>A group and all under it; returns its header, B-tree and local heap.</summary>
        public (long Header, long BTree, long Heap) Group(Hdf5Group group)
        {
            // what it holds first, so their addresses are known, in the order the library looks them up: by name, bytewise
            var children = new List<(string Name, long Header, bool IsGroup, long BTree, long Heap)>();
            foreach (var g in group.Groups)
            {
                var (h, b, l) = Group(g);
                children.Add((g.Name, h, true, b, l));
            }
            foreach (var d in group.Datasets) children.Add((d.Name, Dataset(d), false, 0, 0));
            children.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            if (children.Count > 2 * GroupLeafK * 2 * GroupInternalK) throw new InvalidDataException(L.T("Hdf5.GroupTooBig", group.Name, children.Count));

            // the names, in a local heap: offset 0 is the empty name, each name ends at a zero and is padded to eight
            var names = new MemoryStream();
            names.Write(new byte[8]);
            var nameAt = new Dictionary<string, long>();
            foreach (var c in children)
            {
                nameAt[c.Name] = names.Length;
                byte[] text = Encoding.UTF8.GetBytes(c.Name);
                names.Write(text);
                names.Write(new byte[8 - text.Length % 8]);
            }
            Align();
            long heapData = Bytes(names.ToArray());
            Align();
            var heapHead = new byte[32];
            "HEAP"u8.CopyTo(heapHead);
            BinaryPrimitives.WriteUInt64LittleEndian(heapHead.AsSpan(8), (ulong)names.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(heapHead.AsSpan(16), 1); // no free space
            BinaryPrimitives.WriteUInt64LittleEndian(heapHead.AsSpan(24), (ulong)heapData);
            long heap = Bytes(heapHead);

            // symbol nodes of up to 2K entries each, then a B-tree pointing at them
            var nodes = new List<(long At, string Last)>();
            for (int start = 0; start < Math.Max(1, children.Count); start += 2 * GroupLeafK)
            {
                var part = children.Skip(start).Take(2 * GroupLeafK).ToList();
                var node = new byte[8 + 2 * GroupLeafK * 40];
                "SNOD"u8.CopyTo(node);
                node[4] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(6), (ushort)part.Count);
                for (int i = 0; i < part.Count; i++)
                {
                    var e = node.AsSpan(8 + i * 40);
                    BinaryPrimitives.WriteUInt64LittleEndian(e, (ulong)nameAt[part[i].Name]);
                    BinaryPrimitives.WriteUInt64LittleEndian(e[8..], (ulong)part[i].Header);
                    if (part[i].IsGroup)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(e[16..], 1);
                        BinaryPrimitives.WriteUInt64LittleEndian(e[24..], (ulong)part[i].BTree);
                        BinaryPrimitives.WriteUInt64LittleEndian(e[32..], (ulong)part[i].Heap);
                    }
                }
                Align();
                nodes.Add((Bytes(node), part.Count == 0 ? "" : part[^1].Name));
                if (children.Count == 0) break;
            }
            var tree = new byte[8 + 16 + (2 * GroupInternalK + 1) * 8 + 2 * GroupInternalK * 8];
            "TREE"u8.CopyTo(tree);
            int used = children.Count == 0 ? 0 : nodes.Count;
            BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(6), (ushort)used);
            BinaryPrimitives.WriteUInt64LittleEndian(tree.AsSpan(8), Undefined);
            BinaryPrimitives.WriteUInt64LittleEndian(tree.AsSpan(16), Undefined);
            int at = 24;
            BinaryPrimitives.WriteUInt64LittleEndian(tree.AsSpan(at), 0); // the first key: the empty name
            at += 8;
            for (int i = 0; i < used; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(tree.AsSpan(at), (ulong)nodes[i].At);
                BinaryPrimitives.WriteUInt64LittleEndian(tree.AsSpan(at + 8), (ulong)nameAt[nodes[i].Last]);
                at += 16;
            }
            Align();
            long btree = Bytes(tree);

            var messages = new List<(int Type, byte[] Body, byte Flags)>();
            var table = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(table, (ulong)btree);
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(8), (ulong)heap);
            messages.Add((0x11, table, 0));
            foreach (var (name, value) in group.Attributes) messages.Add((0x0C, Attribute(name, value), 0));
            return (Header(messages), btree, heap);
        }

        // ---------------------------------------------------------------- datasets and attributes

        private long Dataset(Hdf5Dataset set)
        {
            var data = set.Data;
            byte[] raw = VariableStrings(data);
            Align();
            long dataAt = raw.Length > 0 ? Bytes(raw) : -1;
            var messages = new List<(int, byte[], byte)>
            {
                (0x01, Space(data.Shape, withMax: true), 0),
                (0x03, TypeMessage(data.Type), 1),
                // fill value, version 2: allocated late, written never for numbers (at allocation for strings), defined
                (0x05, [2, 2, (byte)(data.Type.Class == Hdf5Class.VariableString ? 0 : 2), 1, 0, 0, 0, 0], 1),
                (0x08, Layout(dataAt, raw.Length), 1),
            };
            foreach (var (name, value) in set.Attributes) messages.Add((0x0C, Attribute(name, value), 0));
            return Header(messages);
        }

        private byte[] Attribute(string name, Hdf5Data value)
        {
            byte[] nameBytes = [.. Encoding.UTF8.GetBytes(name), 0];
            byte[] type = TypeMessage(value.Type), space = Space(value.Shape, withMax: false), raw = VariableStrings(value);
            var a = new MemoryStream();
            var head = new byte[8];
            head[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), (ushort)type.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(6), (ushort)space.Length);
            a.Write(head);
            foreach (var part in new[] { nameBytes, type, space })
            {
                a.Write(part);
                a.Write(new byte[(8 - part.Length % 8) % 8]);
            }
            a.Write(raw);
            return a.ToArray();
        }

        /// <summary>A value's bytes, with each variable-length string put in a global heap and pointed at.</summary>
        private byte[] VariableStrings(Hdf5Data data)
        {
            if (data.Type.Class != Hdf5Class.VariableString) return data.Raw;
            var strings = data.Strings();
            var collection = new MemoryStream();
            for (int i = 0; i < strings.Length; i++)
            {
                byte[] text = Encoding.UTF8.GetBytes(strings[i]);
                var head = new byte[16];
                BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)(i + 1));
                BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(8), (ulong)text.Length);
                collection.Write(head);
                collection.Write(text);
                collection.Write(new byte[(8 - text.Length % 8) % 8]);
            }
            // the collection is at least 4 KB, as the library makes them; the rest is free space, object 0
            long size = Math.Max(4096, 16 + collection.Length + 16);
            size = (size + 7) & ~7L;
            var free = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(free.AsSpan(8), (ulong)(size - 16 - collection.Length));
            collection.Write(free);
            var gcol = new byte[16];
            "GCOL"u8.CopyTo(gcol);
            gcol[4] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(gcol.AsSpan(8), (ulong)size);
            Align();
            long at = Bytes(gcol);
            Bytes(collection.ToArray());
            Bytes(new byte[size - 16 - collection.Length]);
            var raw = new byte[strings.Length * 16];
            for (int i = 0; i < strings.Length; i++)
            {
                var e = raw.AsSpan(i * 16);
                BinaryPrimitives.WriteInt32LittleEndian(e, Encoding.UTF8.GetByteCount(strings[i]));
                BinaryPrimitives.WriteUInt64LittleEndian(e[4..], (ulong)at);
                BinaryPrimitives.WriteInt32LittleEndian(e[12..], i + 1);
            }
            return raw;
        }

        /// <summary>A version 1 object header of these messages, each padded to eight bytes.</summary>
        private long Header(List<(int Type, byte[] Body, byte Flags)> messages)
        {
            var body = new MemoryStream();
            foreach (var (type, data, flags) in messages)
            {
                int size = (data.Length + 7) & ~7;
                var head = new byte[8];
                BinaryPrimitives.WriteUInt16LittleEndian(head, (ushort)type);
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(2), (ushort)size);
                head[4] = flags;
                body.Write(head);
                body.Write(data);
                body.Write(new byte[size - data.Length]);
            }
            var prefix = new byte[16];
            prefix[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(2), (ushort)messages.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(8), (uint)body.Length);
            Align();
            long at = Bytes(prefix);
            Bytes(body.ToArray());
            return at;
        }
    }

    // ---------------------------------------------------------------- messages

    /// <summary>A version 1 dataspace: the rank, whether maximum sizes follow (as for datasets), the sizes.</summary>
    public static byte[] Space(long[] shape, bool withMax)
    {
        bool max = withMax && shape.Length > 0;
        var s = new byte[8 + shape.Length * 8 * (max ? 2 : 1)];
        s[0] = 1;
        s[1] = (byte)shape.Length;
        s[2] = (byte)(max ? 1 : 0);
        for (int i = 0; i < shape.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(8 + i * 8), (ulong)shape[i]);
            if (max) BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(8 + (shape.Length + i) * 8), (ulong)shape[i]);
        }
        return s;
    }

    /// <summary>A version 3 contiguous layout: where the data is (undefined when there's none) and how long.</summary>
    public static byte[] Layout(long at, long size)
    {
        var l = new byte[18];
        l[0] = 3;
        l[1] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(l.AsSpan(2), at < 0 ? Undefined : (ulong)at);
        BinaryPrimitives.WriteUInt64LittleEndian(l.AsSpan(10), (ulong)size);
        return l;
    }

    /// <summary>A datatype message, as the library encodes each kind.</summary>
    public static byte[] TypeMessage(Hdf5Type type)
    {
        var m = new MemoryStream();
        void Head(int cls, int version, uint flags, int size)
        {
            var h = new byte[8];
            h[0] = (byte)(cls | version << 4);
            h[1] = (byte)flags;
            h[2] = (byte)(flags >> 8);
            h[3] = (byte)(flags >> 16);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), size);
            m.Write(h);
        }
        void U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); m.Write(b); }
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); m.Write(b); }
        switch (type.Class)
        {
            case Hdf5Class.Integer:
                Head(0, 1, type.Signed ? 8u : 0u, type.Size);
                U16(0);
                U16(type.Size * 8);
                break;
            case Hdf5Class.Float:
                if (type.Size == 4)
                {
                    // little-endian IEEE single: mantissa normalised (implied), sign at bit 31; exponent at 23 (8 bits), mantissa 23 bits, bias 127
                    Head(1, 1, 0x20 | 31 << 8, 4);
                    U16(0); U16(32); m.WriteByte(23); m.WriteByte(8); m.WriteByte(0); m.WriteByte(23); U32(127);
                }
                else
                {
                    Head(1, 1, 0x20 | 63 << 8, 8);
                    U16(0); U16(64); m.WriteByte(52); m.WriteByte(11); m.WriteByte(0); m.WriteByte(52); U32(1023);
                }
                break;
            case Hdf5Class.String:
                Head(3, 1, 0, type.Size);
                break;
            case Hdf5Class.VariableString:
                // a string, null-terminated, ASCII, of unsigned bytes
                Head(9, 1, 1, 16);
                m.Write(TypeMessage(new Hdf5Type(Hdf5Class.Integer, 1)));
                break;
            case Hdf5Class.Compound:
                Head(6, 1, (uint)type.Members!.Count, type.Size);
                foreach (var member in type.Members)
                {
                    byte[] name = [.. Encoding.UTF8.GetBytes(member.Name), 0];
                    m.Write(name);
                    m.Write(new byte[(8 - name.Length % 8) % 8]);
                    U32((uint)member.Offset);
                    m.Write(new byte[28]); // no dimensions: rank, reserved, permutation, reserved, four sizes
                    m.Write(TypeMessage(member.Type));
                }
                break;
            default:
                throw new InvalidDataException(L.T("Hdf5.TypeNotWritten", type.Class));
        }
        return m.ToArray();
    }
}
