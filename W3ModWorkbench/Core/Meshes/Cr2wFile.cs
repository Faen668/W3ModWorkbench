using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>A file a CR2W file refers to: a material, a texture, a base mesh. Its path is relative to the game's depot.</summary>
public sealed record Cr2wImport(string Path, string ClassName, int Flags);

/// <summary>One of the objects in a CR2W file: its class, the object it belongs to (or -1), and where its data is.</summary>
public sealed record Cr2wExport(int Index, string ClassName, int Parent, int Offset, int Size);

/// <summary>A block of bulk data: kept inside the file (an uncooked one) or in a numbered <c>.buffer</c> file beside it (a cooked one).</summary>
public sealed record Cr2wBuffer(int Index, int Flags, int Offset, int DiskSize, int MemorySize);

/// <summary>
/// A file in the engine's CR2W format, which most of the game's resources use (<c>.w2mesh</c>, <c>.w2mi</c>, <c>.xbm</c>,
/// <c>.w2ent</c>…). It's a header, tables of names, imports, objects and buffers, and each object's properties, saved as
/// a list of (name, type, value). Versions 162 to 164 are read: the original game, and the Remastered, which re-cooked the
/// base game as 164 but still has older files in some DLC.
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
    public static bool IsCr2w(ReadOnlySpan<byte> data) => data.Length >= 160 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    public static Cr2wFile Read(byte[] data)
    {
        if (!IsCr2w(data)) throw new InvalidDataException("This isn't a CR2W resource file.");
        int version = I32(data, 4);
        if (version is < 160 or > 170) throw new InvalidDataException($"CR2W version {version} isn't supported (162 to 164 are).");

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

    /// <summary>The first object of a class, read; null when there's none.</summary>
    public RedObject? First(string className) =>
        Exports.FirstOrDefault(e => e.ClassName == className) is { } export ? Object(export.Index) : null;

    /// <summary>An object's properties and anything its class keeps after them. Read the first time it's asked for.</summary>
    public RedObject Object(int index)
    {
        if (_objects.TryGetValue(index, out var known)) return known;
        var export = Exports[index];
        var reader = new RedReader(this, Data, export.Offset, export.Offset + export.Size);
        var result = reader.ReadObject(export.ClassName);
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

    /// <summary>The import a handle or soft reference points at, when it points at one.</summary>
    public Cr2wImport? Import(RedHandle handle) => handle.Import is int i && i < Imports.Count ? Imports[i] : null;

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
