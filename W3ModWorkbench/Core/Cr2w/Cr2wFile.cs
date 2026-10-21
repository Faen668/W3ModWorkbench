using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>A file a CR2W file refers to: a material, a texture, a base mesh. Its path is relative to the game's depot.</summary>
public sealed record Cr2wImport(string Path, string ClassName, int Flags);

/// <summary>One of the objects in a CR2W file: its class, the object it belongs to (or -1), and where its data is.</summary>
public sealed record Cr2wExport(int Index, string ClassName, int Parent, int Offset, int Size);

/// <summary>A block of bulk data: kept inside the file (an uncooked one) or in a numbered <c>.buffer</c> file beside it (a cooked one).</summary>
public sealed record Cr2wBuffer(int Index, int Flags, int Offset, int DiskSize, int MemorySize);

/// <summary>
/// A file in the engine's CR2W format, which most of the game's resources use (<c>.w2mesh</c>, <c>.w2mi</c>, <c>.xbm</c>,
/// <c>.w2ent</c>…). It's a header, tables of names, imports, objects and buffers, and each object's properties, saved as
/// a list of (name, type, value). Every version there is a file of is read, 120 to 164: the game's are 161 to 164 (the
/// Remastered re-cooked the base game as 164 but still has older files in some DLC), and REDkit's own source files go
/// back to 120. Those up to 154 are in the older layout <see cref="Cr2wLegacy"/> reads.
/// </summary>
public sealed class Cr2wFile
{
    private const uint Magic = 0x57325243; // "CR2W"

    public byte[] Data { get; }
    public int Version { get; }
    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<Cr2wImport> Imports { get; }
    public IReadOnlyList<Cr2wExport> Exports { get; }
    public IReadOnlyList<Cr2wBuffer> Buffers { get; }

    /// <summary>The file taken apart, when it's in the older layout (version 154 and before); null otherwise.</summary>
    public Cr2wLegacy.Layout? Legacy { get; private init; }

    /// <summary>The oldest and newest versions read. Newer than the newest may well read too: the game's last change was at 164.</summary>
    public const int FirstVersion = Cr2wLegacy.FirstVersion, LastVersion = 164;

    private readonly Dictionary<int, RedObject> _objects = [];

    private Cr2wFile(byte[] data, int version, string[] names, List<Cr2wImport> imports, List<Cr2wExport> exports, List<Cr2wBuffer> buffers)
    {
        Data = data;
        Version = version;
        Names = names;
        Imports = imports;
        Exports = exports;
        Buffers = buffers;
    }

    /// <summary>Whether the bytes start like a CR2W file.</summary>
    public static bool IsCr2w(ReadOnlySpan<byte> data) => data.Length >= 44 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    public static Cr2wFile Read(byte[] data)
    {
        if (!IsCr2w(data)) throw new InvalidDataException("This isn't a CR2W resource file.");
        int version = I32(data, 4);
        if (version is < FirstVersion or > 170) throw new InvalidDataException($"CR2W version {version} isn't supported ({FirstVersion} to {LastVersion} are).");
        if (version <= Cr2wLegacy.LastVersion) return ReadLegacy(data);
        if (data.Length < 160) throw new InvalidDataException("The CR2W file is cut short.");

        // ten tables after the header, each an offset, a count and a checksum
        (int Offset, int Count) Table(int i) => (I32(data, 40 + i * 12), I32(data, 44 + i * 12));
        var (stringsAt, stringsSize) = Table(0);
        var (namesAt, nameCount) = Table(1);
        var (importsAt, importCount) = Table(2);
        var (exportsAt, exportCount) = Table(4);
        var (buffersAt, bufferCount) = Table(5);
        Check(data, stringsAt, stringsSize);
        Check(data, namesAt, nameCount * 8L);
        Check(data, importsAt, importCount * 8L);
        Check(data, exportsAt, exportCount * 24L);
        Check(data, buffersAt, bufferCount * 24L);

        string Text(int offset)
        {
            if (offset < 0 || offset >= stringsSize) return "";
            var from = data.AsSpan(stringsAt + offset, stringsSize - offset);
            int end = from.IndexOf((byte)0);
            return Encoding.UTF8.GetString(end < 0 ? from : from[..end]);
        }

        var names = new string[nameCount];
        for (int i = 0; i < nameCount; i++) names[i] = Text(I32(data, namesAt + i * 8));
        string Name(int i) => i >= 0 && i < names.Length ? names[i] : "";

        var imports = new List<Cr2wImport>(importCount);
        for (int i = 0; i < importCount; i++)
        {
            int at = importsAt + i * 8;
            imports.Add(new Cr2wImport(Text(I32(data, at)), Name(U16(data, at + 4)), U16(data, at + 6)));
        }

        var exports = new List<Cr2wExport>(exportCount);
        for (int i = 0; i < exportCount; i++)
        {
            int at = exportsAt + i * 24;
            // class name, flags, parent (1-based, 0 for none), size, offset, template, checksum
            int size = I32(data, at + 8), offset = I32(data, at + 12);
            Check(data, offset, size);
            exports.Add(new Cr2wExport(i, Name(U16(data, at)), I32(data, at + 4) - 1, offset, size));
        }

        var buffers = new List<Cr2wBuffer>(bufferCount);
        for (int i = 0; i < bufferCount; i++)
        {
            int at = buffersAt + i * 24;
            // flags, index (1-based, as the .N.buffer file is numbered), offset, size on disk, size in memory, checksum
            buffers.Add(new Cr2wBuffer(I32(data, at + 4), I32(data, at), I32(data, at + 8), I32(data, at + 12), I32(data, at + 16)));
        }
        return new Cr2wFile(data, version, names, imports, exports, buffers);
    }

    private static Cr2wFile ReadLegacy(byte[] data)
    {
        var layout = Cr2wLegacy.Read(data);
        string Name(int i) => i >= 0 && i < layout.Names.Count ? layout.Names[i] : "";
        var imports = layout.Links.Select(l => new Cr2wImport(l.Path, Name(l.ClassName), l.Flags)).ToList();
        var exports = layout.Exports.Select((e, i) => new Cr2wExport(i, Name(e.ClassName), e.Parent - 1, e.Offset, e.Size)).ToList();
        return new Cr2wFile(data, layout.Version, [.. layout.Names], imports, exports, []) { Legacy = layout };
    }

    /// <summary>The first object of a class, read; null when there's none.</summary>
    public RedObject? First(string className) =>
        Exports.FirstOrDefault(e => e.ClassName == className) is { } export ? Object(export.Index) : null;

    /// <summary>An object's properties and anything its class keeps after them. Read the first time it's asked for.</summary>
    public RedObject Object(int index)
    {
        if (_objects.TryGetValue(index, out var known)) return known;
        var export = Exports[index];
        var reader = new RedReader(this, Data, export.Offset, export.Offset + export.Size);
        RedObject result;
        try { result = reader.ReadObject(export.ClassName); }
        catch (InvalidDataException) when (Cr2wLegacy.MarkerVaries(Version))
        {
            // files of 155 on can have objects either way about the zero byte before their properties
            reader = new RedReader(this, Data, export.Offset, export.Offset + export.Size, otherMarker: true);
            result = reader.ReadObject(export.ClassName);
        }
        // before 139 an object's properties end with a 0
        if (Version < 139 && reader.Position < export.Offset + export.Size && Data[reader.Position] == 0) reader.Position++;
        result.Export = export;
        result.Tail = Data.AsSpan(reader.Position, export.Offset + export.Size - reader.Position).ToArray();
        return _objects[index] = result;
    }

    /// <summary>A material instance's parameters (Diffuse, Normal, SpecularColor…), which it keeps after its properties.</summary>
    public List<RedParameter> Parameters(RedObject instance)
    {
        if (instance.Export is not { } export || instance.Tail.Length == 0) return [];
        var reader = new RedReader(this, Data, export.Offset + export.Size - instance.Tail.Length, export.Offset + export.Size);
        try { return reader.ReadParameters(); }
        catch (InvalidDataException) { return []; }
    }

    /// <summary>
    /// The objects a class keeps after its properties as a counted list of (size, class, properties), the way an
    /// animation set entry keeps its events. <paramref name="skip"/> is how many bytes come before the list (a cutscene
    /// keeps four). Empty when there are none or they can't be read.
    /// </summary>
    public List<RedObject> TailObjects(RedObject owner, int skip = 0)
    {
        if (owner.Export is not { } export || owner.Tail.Length < skip + 4) return [];
        var reader = new RedReader(this, Data, export.Offset + export.Size - owner.Tail.Length + skip, export.Offset + export.Size);
        try { return reader.ReadSizedObjects(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { return []; }
    }

    /// <summary>
    /// An array property of the older layout (154 and before) that was kept as its bytes, read as its elements; the
    /// property as it was read otherwise, or empty when it can't be.
    /// </summary>
    public List<object?> Array(RedObject owner, string name)
    {
        if (owner[name] is List<object?> list) return list;
        if (owner[name] is not byte[] || !owner.Spans.TryGetValue(name, out var span)) return [];
        string type = owner.Properties.First(p => p.Name == name).Type;
        if (!type.StartsWith('@')) return [];
        try { return new RedReader(this, Data, span.Start, span.End).ReadOldArray(type[1..]); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { return []; }
    }

    /// <summary>A TagList's tags. It's kept as its bytes: a count, then that many names.</summary>
    public List<string> Tags(byte[] raw)
    {
        var tags = new List<string>();
        if (raw.Length == 0) return tags;
        for (int i = 0, at = 1; i < raw[0] && at + 2 <= raw.Length; i++, at += 2)
            if (U16(raw, at) is var name && name < Names.Count) tags.Add(Names[name]);
        return tags;
    }

    /// <summary>The import a handle or soft reference points at, when it points at one.</summary>
    public Cr2wImport? Import(RedHandle handle) => handle.Import is int i && i < Imports.Count ? Imports[i] : null;

    /// <summary>
    /// The path of the file a reference points at: an import's, or, for a soft reference in the older layout (154 and
    /// before), the dependency's it's numbered in. Null when it points at none.
    /// </summary>
    public string? PathOf(RedHandle handle) =>
        handle.Soft && Legacy is { } legacy
            ? handle.Import is int d && d < legacy.Dependencies.Count ? legacy.Dependencies[d] : null
            : Import(handle)?.Path;

    /// <summary>The object a handle points at inside this file, when it points at one.</summary>
    public RedObject? Object(RedHandle handle) => handle.Export is int i && i < Exports.Count ? Object(i) : null;

    /// <summary>
    /// A buffer's bytes: from inside the file when it's kept there, else from <paramref name="external"/>, which reads the
    /// numbered <c>.buffer</c> file (1 for <c>name.w2mesh.1.buffer</c>). Null when neither has it.
    /// </summary>
    public byte[]? Buffer(int index, Func<int, byte[]?> external)
    {
        var buffer = Buffers.FirstOrDefault(b => b.Index == index);
        if (buffer is not null && buffer.Offset > 0 && buffer.DiskSize > 0 && buffer.Offset + (long)buffer.DiskSize <= Data.Length)
        {
            var packed = Data.AsSpan(buffer.Offset, buffer.DiskSize);
            if (buffer.DiskSize == buffer.MemorySize) return packed.ToArray();
            try
            {
                using var z = new System.IO.Compression.ZLibStream(new MemoryStream(packed.ToArray()), System.IO.Compression.CompressionMode.Decompress);
                var output = new byte[buffer.MemorySize];
                z.ReadExactly(output);
                return output;
            }
            catch (InvalidDataException) { return packed.ToArray(); }
        }
        return external(index);
    }

    private static void Check(byte[] data, long offset, long size)
    {
        if (offset < 0 || size < 0 || offset + size > data.Length) throw new InvalidDataException("The CR2W file's tables point outside it; it's damaged or cut short.");
    }

    private static int I32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);
    private static int U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
}
