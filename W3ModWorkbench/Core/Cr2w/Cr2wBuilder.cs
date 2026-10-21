using System.Buffers.Binary;
using System.IO;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>
/// Writes a CR2W file: a copy of one that was read, with some of its objects' data replaced, or a new one. Names,
/// imports and strings are only ever added to, after the ones already there, so every index and offset in the
/// objects that aren't touched stays right. Built from a file with nothing changed, the bytes come out the same as
/// they went in, checksums and all.
/// <para>
/// The layout is the engine's: the header, ten tables (strings, names, imports, properties, objects, buffers, and
/// four that aren't used here), each object's data one after another, then the buffers kept inside the file, then
/// anything after them (REDkit keeps a thumbnail there). Each table has a CRC-32 of its bytes, each object a CRC-32
/// of its data, and the header a CRC-32 of itself (with 0xDEADBEEF in place of the checksum) and the table headers.
/// </para>
/// </summary>
public sealed class Cr2wBuilder
{
    private const uint Magic = 0x57325243;
    private const int HeaderSize = 160;

    // the checksum the file had, kept while the data is unchanged: REDkit sometimes saves one that isn't the data's
    private sealed record ExportEntry(int ClassName, int Flags, int Parent, int Template, byte[] Data, uint? Crc = null);
    private sealed record BufferEntry(int Flags, int Index, byte[]? Data, int DiskSize, int MemorySize, uint Crc);

    private readonly int _version, _flags, _build, _chunks;
    private readonly long _timestamp;
    private readonly MemoryStream _strings = new();
    private readonly Dictionary<string, int> _stringOffsets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _latin1 = new(StringComparer.Ordinal);
    private readonly List<(int Offset, uint Hash, string Text)> _names = [];
    private readonly List<(int Path, int ClassName, int Flags, string PathText, string ClassText)> _imports = [];
    private readonly byte[] _properties;
    private readonly int _propertyCount;
    private readonly List<ExportEntry> _exports = [];
    private readonly List<BufferEntry> _buffers = [];
    private readonly byte[] _trailer;
    private IReadOnlyDictionary<int, (int Offset, uint Crc, uint Actual)> _tableHeaders = new Dictionary<int, (int, uint, uint)>();

    private Cr2wBuilder(int version, int flags, long timestamp, int build, int chunks, byte[] properties, int propertyCount, byte[] trailer)
    {
        _version = version;
        _flags = flags;
        _timestamp = timestamp;
        _build = build;
        _chunks = chunks;
        _properties = properties;
        _propertyCount = propertyCount;
        _trailer = trailer;
    }

    /// <summary>A new, empty file of a version, with the one empty name every file starts with.</summary>
    public static Cr2wBuilder New(int version)
    {
        var builder = new Cr2wBuilder(version, 0, 0, 0, 6, new byte[16], 1, []);
        builder.Name("");
        return builder;
    }

    /// <summary>A copy of a file that was read, to change.</summary>
    public static Cr2wBuilder From(Cr2wFile file)
    {
        if (file.Legacy is not null)
            throw new NotSupportedException(L.T("Cr2wBuilder.FileOlderCR2WLayout", file.Version));
        var d = file.Data;
        (int Offset, int Count) Table(int i) => (I32(d, 40 + i * 12), I32(d, 44 + i * 12));
        for (int i = 6; i < 10; i++)
            if (Table(i).Count > 0) throw new NotSupportedException(L.T("Cr2wBuilder.FileHasEmbeddedFiles"));
        var (stringsAt, stringsSize) = Table(0);
        var (namesAt, nameCount) = Table(1);
        var (importsAt, importCount) = Table(2);
        var (propertiesAt, propertyCount) = Table(3);
        var (exportsAt, exportCount) = Table(4);
        var (buffersAt, bufferCount) = Table(5);
        int bufferSize = I32(d, 28);

        var builder = new Cr2wBuilder(I32(d, 4), I32(d, 8), BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(12)), I32(d, 20), I32(d, 36),
            d.AsSpan(propertiesAt, propertyCount * 16).ToArray(), propertyCount,
            bufferSize > 0 && bufferSize < d.Length ? d.AsSpan(bufferSize).ToArray() : []);
        builder._strings.Write(d, stringsAt, stringsSize);
        string Text(int offset)
        {
            var from = d.AsSpan(stringsAt + offset, stringsSize - offset);
            int end = from.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? from : from[..end]);
        }
        for (int i = 0; i < nameCount; i++)
        {
            int offset = I32(d, namesAt + i * 8);
            string text = Text(offset);
            builder._stringOffsets.TryAdd(text, offset);
            builder._names.Add((offset, (uint)I32(d, namesAt + i * 8 + 4), text));
        }
        for (int i = 0; i < importCount; i++)
        {
            int at = importsAt + i * 8;
            int path = I32(d, at), className = U16(d, at + 4);
            string text = Text(path);
            builder._stringOffsets.TryAdd(text, path);
            builder._imports.Add((path, className, U16(d, at + 6), text, builder._names[className].Text));
        }
        for (int i = 0; i < exportCount; i++)
        {
            int at = exportsAt + i * 24;
            builder._exports.Add(new ExportEntry(U16(d, at), U16(d, at + 2), I32(d, at + 4), I32(d, at + 16), d.AsSpan(I32(d, at + 12), I32(d, at + 8)).ToArray(), (uint)I32(d, at + 20)));
        }
        for (int i = 0; i < bufferCount; i++)
        {
            int at = buffersAt + i * 24;
            int offset = I32(d, at + 8), disk = I32(d, at + 12);
            byte[]? data = offset > 0 ? d.AsSpan(offset, disk).ToArray() : null;
            builder._buffers.Add(new BufferEntry(I32(d, at), I32(d, at + 4), data, disk, I32(d, at + 16), (uint)I32(d, at + 20)));
        }
        return builder;
    }

    /// <summary>
    /// A file put back together from its parts, as <see cref="Cr2wJson"/> keeps them: the header's fields, the names
    /// and imports in their order (so the indices objects use stay right), the properties table and the trailer.
    /// Strings are laid out names first, then import paths, each once, the way the engine writes them; those in
    /// <paramref name="latin1"/> as 8-bit text rather than UTF-8 (a few of the game's own are in a Windows code page).
    /// A name's hash is worked out, unless <paramref name="hashes"/> has the one the file was saved with.
    /// <paramref name="order"/>, when given, is the order the strings go in instead: numbers into the names, then on
    /// into the import paths (the engine often writes an import's path just before its class's name).
    /// <paramref name="tableHeaders"/> has the table headers the file had that writing it wouldn't give: by the table's
    /// number, its offset and checksum, and the checksum of its bytes, kept only while they're still those bytes.
    /// </summary>
    public static Cr2wBuilder Create(int version, int flags, long timestamp, int build, int chunks, IEnumerable<string> names,
        IEnumerable<(string Path, string ClassName, int Flags)> imports, byte[] properties, int propertyCount, byte[] trailer,
        IEnumerable<string>? latin1 = null, IReadOnlyDictionary<int, uint>? hashes = null, IEnumerable<int>? order = null,
        IReadOnlyDictionary<int, (int Offset, uint Crc, uint Actual)>? tableHeaders = null)
    {
        var builder = new Cr2wBuilder(version, flags, timestamp, build, chunks, properties, propertyCount, trailer);
        if (tableHeaders is not null) builder._tableHeaders = tableHeaders;
        if (latin1 is not null) builder._latin1.UnionWith(latin1);
        names = names as IReadOnlyList<string> ?? names.ToList();
        imports = imports as IReadOnlyList<(string, string, int)> ?? imports.ToList();
        if (order is not null)
        {
            var nameList = (IReadOnlyList<string>)names;
            var importList = (IReadOnlyList<(string Path, string ClassName, int Flags)>)imports;
            foreach (int i in order)
            {
                if (i >= 0 && i < nameList.Count) builder.StringOffset(nameList[i]);
                else if (i - nameList.Count is int import && import >= 0 && import < importList.Count) builder.StringOffset(importList[import].Path);
            }
        }
        // the empty name every file starts with has a hash of 0, not its FNV-1a
        foreach (string name in names)
        {
            uint hash = hashes is not null && hashes.TryGetValue(builder._names.Count, out uint kept) ? kept : name.Length == 0 ? 0 : Checksums.Fnv1a32(name);
            builder._names.Add((builder.StringOffset(name), hash, name));
        }
        foreach (var (path, className, importFlags) in imports)
            builder._imports.Add((builder.StringOffset(path), builder.Name(className), importFlags, path, className));
        return builder;
    }

    /// <summary>
    /// Adds an object with all of its export entry: <paramref name="parent"/> as the file has it (1-based, 0 for none),
    /// and the checksum to keep when the data is still what it was saved with (null to work it out).
    /// </summary>
    public int AddExport(string className, int flags, int parent, int template, byte[] data, uint? crc)
    {
        _exports.Add(new ExportEntry(Name(className), flags, parent, template, data, crc));
        return _exports.Count - 1;
    }

    /// <summary>Adds a buffer: its bytes when they're kept inside the file, else null and its sizes and checksum.</summary>
    public void AddBuffer(int flags, int index, byte[]? data, int diskSize, int memorySize, uint crc) =>
        _buffers.Add(new BufferEntry(flags, index, data, diskSize, memorySize, crc));

    public int Version => _version;

    /// <summary>A name's index, adding it when the file doesn't have it yet.</summary>
    public int Name(string text)
    {
        int index = _names.FindIndex(n => n.Text == text);
        if (index >= 0) return index;
        _names.Add((StringOffset(text), Checksums.Fnv1a32(text), text));
        return _names.Count - 1;
    }

    /// <summary>An import's index (0-based), adding it when the file doesn't have it yet.</summary>
    public int Import(string path, string className, int flags = 0)
    {
        int index = _imports.FindIndex(i => i.PathText.Equals(path, StringComparison.OrdinalIgnoreCase) && i.ClassText == className);
        if (index >= 0) return index;
        int name = Name(className);
        _imports.Add((StringOffset(path), name, flags, path, className));
        return _imports.Count - 1;
    }

    private int StringOffset(string text)
    {
        if (_stringOffsets.TryGetValue(text, out int known)) return known;
        int offset = (int)_strings.Length;
        _strings.Write(_latin1.Contains(text) ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text));
        _strings.WriteByte(0);
        _stringOffsets[text] = offset;
        return offset;
    }

    /// <summary>Puts new data in place of an object's.</summary>
    public void ReplaceExport(int index, byte[] data) => _exports[index] = _exports[index] with { Data = data, Crc = null };

    /// <summary>The data an object has now.</summary>
    public byte[] ExportData(int index) => _exports[index].Data;

    /// <summary>Adds an object; <paramref name="parent"/> is the 0-based index of the object it belongs to, or -1.</summary>
    public int AddExport(string className, int parent, byte[] data, int flags)
    {
        _exports.Add(new ExportEntry(Name(className), flags, parent + 1, 0, data));
        return _exports.Count - 1;
    }

    public byte[] Build()
    {
        byte[] strings = _strings.ToArray();
        var names = new byte[_names.Count * 8];
        for (int i = 0; i < _names.Count; i++)
        {
            Put(names, i * 8, _names[i].Offset);
            Put(names, i * 8 + 4, (int)_names[i].Hash);
        }
        var imports = new byte[_imports.Count * 8];
        for (int i = 0; i < _imports.Count; i++)
        {
            Put(imports, i * 8, _imports[i].Path);
            BinaryPrimitives.WriteUInt16LittleEndian(imports.AsSpan(i * 8 + 4), (ushort)_imports[i].ClassName);
            BinaryPrimitives.WriteUInt16LittleEndian(imports.AsSpan(i * 8 + 6), (ushort)_imports[i].Flags);
        }

        // where everything goes: the tables after the header, then the objects' data, then the buffers inside the file
        int at = HeaderSize;
        int stringsAt = at; at += strings.Length;
        int namesAt = at; at += names.Length;
        int importsAt = at; at += imports.Length;
        int propertiesAt = at; at += _properties.Length;
        int exportsAt = at; at += _exports.Count * 24;
        int buffersAt = at; at += _buffers.Count * 24;
        var exportOffsets = new int[_exports.Count];
        for (int i = 0; i < _exports.Count; i++) { exportOffsets[i] = at; at += _exports[i].Data.Length; }
        int fileSize = at;
        var bufferOffsets = new int[_buffers.Count];
        for (int i = 0; i < _buffers.Count; i++)
            if (_buffers[i].Data is { } data) { bufferOffsets[i] = at; at += data.Length; }
        int bufferSize = at;

        var exports = new byte[_exports.Count * 24];
        for (int i = 0; i < _exports.Count; i++)
        {
            var e = _exports[i];
            var s = exports.AsSpan(i * 24);
            BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)e.ClassName);
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)e.Flags);
            Put(exports, i * 24 + 4, e.Parent);
            Put(exports, i * 24 + 8, e.Data.Length);
            Put(exports, i * 24 + 12, exportOffsets[i]);
            Put(exports, i * 24 + 16, e.Template);
            Put(exports, i * 24 + 20, (int)(e.Crc ?? Checksums.Crc32(e.Data)));
        }
        var buffers = new byte[_buffers.Count * 24];
        for (int i = 0; i < _buffers.Count; i++)
        {
            var b = _buffers[i];
            Put(buffers, i * 24, b.Flags);
            Put(buffers, i * 24 + 4, b.Index);
            Put(buffers, i * 24 + 8, bufferOffsets[i]);
            Put(buffers, i * 24 + 12, b.Data?.Length ?? b.DiskSize);
            Put(buffers, i * 24 + 16, b.MemorySize);
            Put(buffers, i * 24 + 20, (int)(b.Data is { } data ? Checksums.Crc32(data) : b.Crc));
        }

        var output = new byte[bufferSize + _trailer.Length];
        var tables = new (int Offset, int Count, uint Crc)[10];
        tables[0] = (stringsAt, strings.Length, Checksums.Crc32(strings));
        tables[1] = (namesAt, _names.Count, Checksums.Crc32(names));
        tables[2] = (importsAt, _imports.Count, Checksums.Crc32(imports));
        tables[3] = (propertiesAt, _propertyCount, Checksums.Crc32(_properties));
        tables[4] = (exportsAt, _exports.Count, Checksums.Crc32(exports));
        tables[5] = (buffersAt, _buffers.Count, Checksums.Crc32(buffers));
        for (int i = 0; i < 10; i++)
        {
            if (tables[i].Count == 0) tables[i] = (0, 0, 0);
            // what the file had instead, while the table is still what it was: an empty one's offset, a checksum not of the bytes
            if (_tableHeaders.TryGetValue(i, out var kept) && tables[i].Crc == kept.Actual)
                tables[i] = (tables[i].Count == 0 ? kept.Offset : tables[i].Offset, tables[i].Count, kept.Crc);
        }

        Put(output, 0, (int)Magic);
        Put(output, 4, _version);
        Put(output, 8, _flags);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(12), _timestamp);
        Put(output, 20, _build);
        Put(output, 24, fileSize);
        Put(output, 28, bufferSize);
        Put(output, 32, unchecked((int)0xDEADBEEF));
        Put(output, 36, _chunks);
        for (int i = 0; i < 10; i++)
        {
            Put(output, 40 + i * 12, tables[i].Offset);
            Put(output, 44 + i * 12, tables[i].Count);
            Put(output, 48 + i * 12, (int)tables[i].Crc);
        }
        // the header's checksum is of the header as it is now, with 0xDEADBEEF where the checksum goes
        Put(output, 32, (int)Checksums.Crc32(output.AsSpan(0, HeaderSize)));

        strings.CopyTo(output, stringsAt);
        names.CopyTo(output, namesAt);
        imports.CopyTo(output, importsAt);
        _properties.CopyTo(output, propertiesAt);
        exports.CopyTo(output, exportsAt);
        buffers.CopyTo(output, buffersAt);
        for (int i = 0; i < _exports.Count; i++) _exports[i].Data.CopyTo(output, exportOffsets[i]);
        for (int i = 0; i < _buffers.Count; i++) _buffers[i].Data?.CopyTo(output, bufferOffsets[i]);
        _trailer.CopyTo(output, bufferSize);
        return output;
    }

    private static void Put(byte[] data, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at), value);
    private static int I32(byte[] d, int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
    private static int U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
}
