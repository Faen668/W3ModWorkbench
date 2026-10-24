using System.Buffers.Binary;
using System.IO;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Hdf5;

/// <summary>
/// Reads HDF5 files, as far as REDkit's <c>.re</c> files use the format (they're written by h5py with its defaults):
/// superblock version 0 or 1, version 1 object headers, groups as symbol tables (a B-tree of symbol nodes naming into a
/// local heap), datasets kept contiguous or compact, attributes, and numbers, strings (fixed or variable length, the
/// latter in global heaps) and compounds of them. Written from the HDF5 file format specification.
/// </summary>
public static class Hdf5Reader
{
    private static readonly byte[] Signature = [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsHdf5(byte[] data) => data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(Signature);

    /// <summary>Reads a whole file into its root group.</summary>
    public static Hdf5Group Read(byte[] data)
    {
        if (!IsHdf5(data)) throw new InvalidDataException(L.T("Hdf5.NotHdf5"));
        var r = new Cursor(data);
        int version = data[8];
        if (version > 1) throw new InvalidDataException(L.T("Hdf5.SuperblockNotRead", version));
        r.OffsetSize = data[13];
        r.LengthSize = data[14];
        int at = 24 + (version == 1 ? 4 : 0);
        r.Base = (long)r.Offset(at);
        // free space, end of file and driver addresses, then the root group's symbol table entry
        at += 4 * r.OffsetSize;
        var (_, header, cache, btree, heap) = r.Entry(at);
        return r.Group("", header, cache == 1 ? (btree, heap) : null);
    }

    /// <summary>Where reading is, and how big the file's addresses and lengths are.</summary>
    private sealed class Cursor(byte[] data)
    {
        public readonly byte[] Data = data;
        public int OffsetSize = 8, LengthSize = 8;
        public long Base;

        public ulong Offset(long at) => Number(at, OffsetSize);
        public ulong Length(long at) => Number(at, LengthSize);

        public ulong Number(long at, int size) => size switch
        {
            2 => BinaryPrimitives.ReadUInt16LittleEndian(Span(at, 2)),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(Span(at, 4)),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(Span(at, 8)),
            _ => throw new InvalidDataException(L.T("Hdf5.OddNumberSize", size)),
        };

        public ReadOnlySpan<byte> Span(long at, long size)
        {
            if (at < 0 || size < 0 || at + size > Data.Length) throw new InvalidDataException(L.T("Hdf5.PastEnd", at));
            return Data.AsSpan((int)at, (int)size);
        }

        public bool Undefined(ulong address) => OffsetSize == 8 ? address == ulong.MaxValue : address == (1UL << (OffsetSize * 8)) - 1;

        /// <summary>A symbol table entry: its name's place in the heap, its object header, and what the entry caches.</summary>
        public (ulong Name, long Header, int Cache, long BTree, long Heap) Entry(long at)
        {
            ulong name = Offset(at);
            long header = (long)Offset(at + OffsetSize) + Base;
            int cache = (int)Number(at + 2 * OffsetSize, 4);
            long scratch = at + 2 * OffsetSize + 8;
            return (name, header, cache, (long)Offset(scratch) + Base, (long)Offset(scratch + OffsetSize) + Base);
        }

        /// <summary>A null-ended string at a place.</summary>
        public string Text(long at)
        {
            int end = Array.IndexOf(Data, (byte)0, (int)at);
            if (end < 0) throw new InvalidDataException(L.T("Hdf5.PastEnd", at));
            return Encoding.UTF8.GetString(Data, (int)at, end - (int)at);
        }

        // ---------------------------------------------------------------- object headers

        /// <summary>An object header's messages: each one's type and where its body is and how long.</summary>
        public List<(int Type, long At, int Size)> Messages(long header)
        {
            if (Data[header] != 1) throw new InvalidDataException(L.T("Hdf5.HeaderNotRead", Data[header]));
            int count = (int)Number(header + 2, 2);
            long size = (long)Number(header + 8, 4);
            var messages = new List<(int, long, int)>();
            var blocks = new Queue<(long At, long Size)>();
            // the first block starts after the 12-byte prefix, padded to 16
            blocks.Enqueue((header + 16, size));
            while (blocks.Count > 0 && messages.Count < count)
            {
                var (at, length) = blocks.Dequeue();
                long end = at + length;
                while (at + 8 <= end && messages.Count < count)
                {
                    int type = (int)Number(at, 2), bodySize = (int)Number(at + 2, 2);
                    long body = at + 8;
                    if (type == 0x10) blocks.Enqueue(((long)Offset(body) + Base, (long)Length(body + OffsetSize)));
                    messages.Add((type, body, bodySize));
                    at = body + bodySize;
                }
            }
            return messages;
        }

        // ---------------------------------------------------------------- groups

        public Hdf5Group Group(string name, long header, (long BTree, long Heap)? cached)
        {
            var messages = Messages(header);
            var group = new Hdf5Group(name);
            ReadAttributes(messages, group.Attributes);
            var table = messages.FirstOrDefault(m => m.Type == 0x11);
            (long BTree, long Heap)? symbols = table.Type == 0x11
                ? ((long)Offset(table.At) + Base, (long)Offset(table.At + OffsetSize) + Base)
                : cached;
            if (symbols is not { } s) return group;
            long heapData = HeapData(s.Heap);
            foreach (var (nameAt, child, cache, btree, heap) in Symbols(s.BTree))
            {
                string childName = Text(heapData + (long)nameAt);
                var childMessages = Messages(child);
                if (childMessages.Any(m => m.Type == 0x11) || cache == 1) group.Groups.Add(Group(childName, child, cache == 1 ? (btree, heap) : null));
                else if (childMessages.Any(m => m.Type == 0x08)) group.Datasets.Add(Dataset(childName, childMessages));
            }
            return group;
        }

        /// <summary>A local heap's data, which group entries name into.</summary>
        private long HeapData(long heap)
        {
            if (Encoding.ASCII.GetString(Span(heap, 4)) != "HEAP") throw new InvalidDataException(L.T("Hdf5.NoHeap", heap));
            return (long)Offset(heap + 8 + 2L * LengthSize) + Base;
        }

        /// <summary>The entries of a group's B-tree, from its leaves (symbol nodes) in order.</summary>
        private IEnumerable<(ulong Name, long Header, int Cache, long BTree, long Heap)> Symbols(long node)
        {
            if (Encoding.ASCII.GetString(Span(node, 4)) != "TREE") throw new InvalidDataException(L.T("Hdf5.NoTree", node));
            int level = Data[node + 5], used = (int)Number(node + 6, 2);
            long at = node + 8 + 2L * OffsetSize;
            for (int i = 0; i < used; i++)
            {
                // a key (the heap offset of the name before), then the child
                long child = (long)Offset(at + LengthSize) + Base;
                at += LengthSize + OffsetSize;
                if (level > 0)
                {
                    foreach (var e in Symbols(child)) yield return e;
                    continue;
                }
                if (Encoding.ASCII.GetString(Span(child, 4)) != "SNOD") throw new InvalidDataException(L.T("Hdf5.NoSymbolNode", child));
                int symbols = (int)Number(child + 6, 2);
                for (int k = 0; k < symbols; k++) yield return Entry(child + 8 + k * (2L * OffsetSize + 24));
            }
        }

        // ---------------------------------------------------------------- datasets and attributes

        private Hdf5Dataset Dataset(string name, List<(int Type, long At, int Size)> messages)
        {
            var space = messages.First(m => m.Type == 0x01);
            var type = messages.First(m => m.Type == 0x03);
            var layout = messages.First(m => m.Type == 0x08);
            var dataType = Type(type.At);
            long[] shape = Space(space.At);
            byte[] raw = Layout(layout.At, shape.Aggregate(1L, (a, b) => a * b) * dataType.Size);
            var set = new Hdf5Dataset(name, new Hdf5Data(dataType, shape, raw, Strings(dataType, raw)));
            ReadAttributes(messages, set.Attributes);
            return set;
        }

        private void ReadAttributes(List<(int Type, long At, int Size)> messages, Dictionary<string, Hdf5Data> into)
        {
            foreach (var m in messages.Where(m => m.Type == 0x0C))
            {
                int version = Data[m.At];
                int nameSize = (int)Number(m.At + 2, 2), typeSize = (int)Number(m.At + 4, 2), spaceSize = (int)Number(m.At + 6, 2);
                long at = m.At + (version >= 3 ? 9 : 8);
                int Pad(int n) => version == 1 ? (n + 7) & ~7 : n;
                string name = Text(at);
                at += Pad(nameSize);
                var dataType = Type(at);
                at += Pad(typeSize);
                long[] shape = Space(at);
                at += Pad(spaceSize);
                long size = shape.Aggregate(1L, (a, b) => a * b) * dataType.Size;
                byte[] raw = Span(at, size).ToArray();
                into[name] = new Hdf5Data(dataType, shape, raw, Strings(dataType, raw));
            }
        }

        /// <summary>A dataspace's dimensions (none for a single value).</summary>
        private long[] Space(long at)
        {
            int version = Data[at], rank = Data[at + 1];
            if (version == 2 && Data[at + 3] == 2) return [0]; // null
            long dims = at + (version == 1 ? 8 : 4);
            var shape = new long[rank];
            for (int i = 0; i < rank; i++) shape[i] = (long)Length(dims + i * LengthSize);
            return shape;
        }

        /// <summary>A dataset's bytes, from where its layout keeps them.</summary>
        private byte[] Layout(long at, long size)
        {
            int version = Data[at];
            if (version == 3 || version == 4)
            {
                int kind = Data[at + 1];
                if (kind == 0) return Span(at + 4, (long)Number(at + 2, 2)).ToArray();
                if (kind == 1)
                {
                    ulong address = Offset(at + 2);
                    return Undefined(address) ? new byte[size] : Span((long)address + Base, size).ToArray();
                }
                throw new InvalidDataException(L.T("Hdf5.ChunkedNotRead"));
            }
            // versions 1 and 2: rank, class, five reserved, the address unless compact, the dimensions
            int rank = Data[at + 1], cls = Data[at + 2];
            if (cls == 0)
            {
                long sizeAt = at + 8 + rank * 4L;
                return Span(sizeAt + 4, (long)Number(sizeAt, 4)).ToArray();
            }
            if (cls != 1) throw new InvalidDataException(L.T("Hdf5.ChunkedNotRead"));
            ulong where = Offset(at + 8);
            return Undefined(where) ? new byte[size] : Span((long)where + Base, size).ToArray();
        }

        /// <summary>A datatype message, read into a type.</summary>
        public Hdf5Type Type(long at)
        {
            int classAndVersion = Data[at], cls = classAndVersion & 0x0F, version = classAndVersion >> 4;
            uint flags = (uint)(Data[at + 1] | Data[at + 2] << 8 | Data[at + 3] << 16);
            int size = (int)Number(at + 4, 4);
            long props = at + 8;
            switch (cls)
            {
                case 0: return new Hdf5Type(Hdf5Class.Integer, size, Signed: (flags & 8) != 0);
                case 1: return new Hdf5Type(Hdf5Class.Float, size);
                case 3: return new Hdf5Type(Hdf5Class.String, size);
                case 9:
                    // variable length: a sequence of a base type, or (bits 0-3 = 1) a string
                    return new Hdf5Type((flags & 0xF) == 1 ? Hdf5Class.VariableString : Hdf5Class.Sequence, size, Base: Type(props));
                case 6:
                    var members = new List<Hdf5Member>();
                    int count = (int)(flags & 0xFFFF);
                    for (int i = 0; i < count; i++)
                    {
                        string name = Text(props);
                        int nameLength = Encoding.UTF8.GetByteCount(name) + 1;
                        props += version < 3 ? (nameLength + 7) & ~7 : nameLength;
                        int offset;
                        if (version < 3) { offset = (int)Number(props, 4); props += 4; }
                        else
                        {
                            int bytes = size < 256 ? 1 : size < 65536 ? 2 : size < 16777216 ? 3 : 4;
                            offset = 0;
                            for (int b = 0; b < bytes; b++) offset |= Data[props + b] << (8 * b);
                            props += bytes;
                        }
                        if (version == 1) props += 28; // rank, reserved, permutation, reserved, four dimensions
                        var memberType = Type(props);
                        props += TypeSize(props);
                        members.Add(new Hdf5Member(name, offset, memberType));
                    }
                    return new Hdf5Type(Hdf5Class.Compound, size, Members: members);
                default:
                    throw new InvalidDataException(L.T("Hdf5.TypeNotRead", cls));
            }
        }

        /// <summary>How many bytes a datatype message takes, to step past one inside a compound.</summary>
        private long TypeSize(long at)
        {
            int cls = Data[at] & 0x0F;
            return cls switch
            {
                0 => 8 + 4,
                1 => 8 + 12,
                3 => 8,
                9 => 8 + TypeSize(at + 8),
                6 => CompoundSize(at),
                _ => throw new InvalidDataException(L.T("Hdf5.TypeNotRead", cls)),
            };
        }

        private long CompoundSize(long at)
        {
            int version = Data[at] >> 4, count = Data[at + 1] | Data[at + 2] << 8;
            int size = (int)Number(at + 4, 4);
            long props = at + 8;
            for (int i = 0; i < count; i++)
            {
                int nameLength = Encoding.UTF8.GetByteCount(Text(props)) + 1;
                props += version < 3 ? (nameLength + 7) & ~7 : nameLength;
                props += version < 3 ? 4 : size < 256 ? 1 : size < 65536 ? 2 : size < 16777216 ? 3 : 4;
                if (version == 1) props += 28;
                props += TypeSize(props);
            }
            return props - at;
        }

        /// <summary>A variable-length string's value for each element, from the global heaps; null for other types.</summary>
        private string[]? Strings(Hdf5Type type, byte[] raw)
        {
            if (type.Class != Hdf5Class.VariableString) return null;
            int count = type.Size == 0 ? 0 : raw.Length / type.Size;
            var strings = new string[count];
            for (int i = 0; i < count; i++)
            {
                int at = i * type.Size;
                long collection = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(at + 4)) + Base;
                int index = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 4 + OffsetSize));
                strings[i] = HeapObject(collection, index);
            }
            return strings;
        }

        /// <summary>An object in a global heap collection, as text.</summary>
        private string HeapObject(long collection, int index)
        {
            if (Encoding.ASCII.GetString(Span(collection, 4)) != "GCOL") throw new InvalidDataException(L.T("Hdf5.NoGlobalHeap", collection));
            long size = (long)Length(collection + 8);
            long at = collection + 8 + LengthSize, end = collection + size;
            while (at + 8 + LengthSize <= end)
            {
                int id = (int)Number(at, 2);
                long length = (long)Length(at + 8);
                if (id == 0) break;
                if (id == index) return Encoding.UTF8.GetString(Span(at + 8 + LengthSize, length)).TrimEnd('\0');
                at += 8 + LengthSize + ((length + 7) & ~7L);
            }
            throw new InvalidDataException(L.T("Hdf5.NoHeapObject", index));
        }
    }
}
