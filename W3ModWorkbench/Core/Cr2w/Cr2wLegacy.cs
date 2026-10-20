using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Cr2w;

/// <summary>
/// The older CR2W layout, which REDkit's own source files (its <c>r4data</c>) still have when nobody has saved them
/// since: versions 120 to 154, from before the Witcher 3 shipped. It's the Witcher 2's container. A header of offsets
/// and counts, then the names, the imports ("links") and the dependencies written out as strings, the objects table,
/// and each object's data. There's no strings table, no checksums and no buffers table. REDkit keeps a thumbnail after
/// the objects, as it does in the newer files.
/// <para>
/// The header is eleven numbers: magic, version, flags, then an offset and a count each for the names, the objects, the
/// links and the dependencies. 153 and 154 add three: where the objects' data ends (so where the thumbnail starts)
/// and two zeros. A name or a dependency is a length-prefixed string, the way a String property is. A link is its path
/// with the length in bytes in front (the 0 at its end counted), its class's name and its flags. The dependency count
/// counts an empty first one that isn't written. An object's entry is its class, its parent (1-based, 0 for none),
/// its data's size and offset, its flags, its template (a u16, a u32 before 125), a string and eight bytes, which
/// all the files there are leave empty.
/// </para>
/// <para>
/// The properties inside an object are as the newer files have them, less the zero byte each object and struct starts
/// with there; before 139 a property also has two bytes (always 0xFFFF) between its type and its size.
/// </para>
/// </summary>
public static class Cr2wLegacy
{
    /// <summary>The newest version with the older layout; 155 has the newer one.</summary>
    public const int LastVersion = 154;

    /// <summary>The oldest version there's a file of to check against (REDkit's r4data has none older).</summary>
    public const int FirstVersion = 120;

    /// <summary>Whether a version's objects and structs start with a zero byte before their properties (155 on).</summary>
    public static bool HasObjectMarker(int version) => version > LastVersion;

    /// <summary>Whether a version's properties have two bytes between their type and their size (before 139).</summary>
    public static bool HasPropertyPad(int version) => version < 139;

    public sealed record Link(string Path, int ClassName, int Flags);

    /// <summary>An object's entry. <see cref="Parent"/> is as the file has it: 1-based, 0 for none.</summary>
    public sealed record Export(int ClassName, int Parent, int Offset, int Size, int Flags, int Template, string Name, byte[] Extra);

    /// <summary>A file in the older layout, taken apart. <see cref="Names"/> has an empty name first, which isn't written:
    /// the file counts its names from 1, so the indices objects use are the same as in a newer file.</summary>
    public sealed class Layout
    {
        public required int Version { get; init; }
        public required int Flags { get; init; }
        public required List<string> Names { get; init; }
        public required List<Link> Links { get; init; }
        public required List<string> Dependencies { get; init; }
        /// <summary>The dependency count the header has: one more than there are, or 0 when there are none and it says so.</summary>
        public required int DependencyCount { get; init; }
        public required List<Export> Exports { get; init; }
        /// <summary>Where the objects' data ends; anything after it (REDkit's thumbnail) is the trailer.</summary>
        public required int DataEnd { get; init; }
    }

    public static int HeaderSize(int version) => version >= 153 ? 56 : 44;

    public static Layout Read(byte[] d)
    {
        int version = I32(d, 4);
        int headerSize = HeaderSize(version);
        if (d.Length < headerSize) throw new InvalidDataException("The CR2W file is cut short.");
        int flags = I32(d, 8);
        int namesAt = I32(d, 12), nameCount = I32(d, 16);
        int exportsAt = I32(d, 20), exportCount = I32(d, 24);
        int linksAt = I32(d, 28), linkCount = I32(d, 32);
        int dependenciesAt = I32(d, 36), dependencyCount = I32(d, 40);
        if (nameCount < 0 || exportCount < 0 || linkCount < 0 || dependencyCount < 0) throw Damaged();

        var names = new List<string>(nameCount + 1) { "" };
        int at = namesAt;
        for (int i = 0; i < nameCount; i++) names.Add(Text(d, ref at));

        var links = new List<Link>(linkCount);
        at = linksAt;
        for (int i = 0; i < linkCount; i++)
        {
            int length = Length(d, ref at, out _);
            Need(d, at, length + 4);
            string path = Encoding.Latin1.GetString(d, at, Math.Max(0, length - 1));
            if (length == 0 || d[at + length - 1] != 0) throw Damaged();
            at += length;
            links.Add(new Link(path, U16(d, at), U16(d, at + 2)));
            at += 4;
        }

        var dependencies = new List<string>();
        at = dependenciesAt;
        for (int i = 1; i < dependencyCount; i++) dependencies.Add(Text(d, ref at));

        var exports = new List<Export>(exportCount);
        at = exportsAt;
        int dataEnd = 0;
        for (int i = 0; i < exportCount; i++)
        {
            Need(d, at, 18);
            int className = U16(d, at), parent = I32(d, at + 2), size = I32(d, at + 6), offset = I32(d, at + 10), objectFlags = I32(d, at + 14);
            at += 18;
            int template;
            if (version < 125) { Need(d, at, 4); template = I32(d, at); at += 4; }
            else { Need(d, at, 2); template = U16(d, at); at += 2; }
            string name = Text(d, ref at);
            Need(d, at, 8);
            byte[] extra = d.AsSpan(at, 8).ToArray();
            at += 8;
            if (offset < 0 || size < 0 || offset + (long)size > d.Length) throw Damaged();
            exports.Add(new Export(className, parent, offset, size, objectFlags, template, name, extra));
            dataEnd = Math.Max(dataEnd, offset + size);
        }
        if (exportCount == 0) dataEnd = Math.Max(at, version >= 153 ? I32(d, 44) : d.Length);
        return new Layout
        {
            Version = version, Flags = flags, Names = names, Links = links, Dependencies = dependencies,
            DependencyCount = dependencyCount, Exports = exports, DataEnd = dataEnd,
        };
    }

    /// <summary>
    /// Writes a file in the older layout: the header, the names, links and dependencies, the objects table, each
    /// object's data in turn, then the trailer. <paramref name="names"/> has the empty first name, which isn't written.
    /// </summary>
    public static byte[] Write(int version, int flags, IReadOnlyList<string> names, IReadOnlyList<Link> links,
        IReadOnlyList<string> dependencies, int dependencyCount, IReadOnlyList<(Export Entry, byte[] Data)> exports, byte[] trailer)
    {
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        int headerSize = HeaderSize(version);
        o.Write(new byte[headerSize]);

        int namesAt = (int)o.Position;
        for (int i = 1; i < names.Count; i++) WriteText(o, names[i]);
        int linksAt = (int)o.Position;
        foreach (var link in links)
        {
            byte[] path = Encoding.Latin1.GetBytes(link.Path + "\0");
            WriteLength(o, path.Length, narrow: false);
            o.Write(path);
            w.Write((ushort)link.ClassName);
            w.Write((ushort)link.Flags);
        }
        int dependenciesAt = (int)o.Position;
        foreach (string dependency in dependencies) WriteText(o, dependency);

        int exportsAt = (int)o.Position;
        int entriesSize = 0;
        foreach (var (entry, _) in exports)
            entriesSize += 18 + (version < 125 ? 4 : 2) + TextSize(entry.Name) + 8;
        int dataAt = exportsAt + entriesSize;
        foreach (var (entry, data) in exports)
        {
            w.Write((ushort)entry.ClassName);
            w.Write(entry.Parent);
            w.Write(data.Length);
            w.Write(dataAt);
            w.Write(entry.Flags);
            if (version < 125) w.Write(entry.Template); else w.Write((ushort)entry.Template);
            WriteText(o, entry.Name);
            o.Write(entry.Extra.Length == 8 ? entry.Extra : new byte[8]);
            dataAt += data.Length;
        }
        foreach (var (_, data) in exports) o.Write(data);
        int dataEnd = (int)o.Position;
        o.Write(trailer);

        o.Position = 0;
        w.Write(0x57325243);
        w.Write(version);
        w.Write(flags);
        w.Write(namesAt); w.Write(names.Count - 1);
        w.Write(exportsAt); w.Write(exports.Count);
        w.Write(linksAt); w.Write(links.Count);
        w.Write(dependenciesAt); w.Write(dependencyCount);
        if (headerSize > 44) { w.Write(dataEnd); w.Write(0); w.Write(0); }
        w.Flush();
        return o.ToArray();
    }

    /// <summary>A length-prefixed string as a String property and the names are: 8-bit text when every character fits.</summary>
    internal static void WriteText(Stream o, string text)
    {
        bool narrow = text.All(c => c <= 0xFF);
        WriteLength(o, text.Length, narrow);
        o.Write(narrow ? Encoding.Latin1.GetBytes(text) : Encoding.Unicode.GetBytes(text));
    }

    private static int TextSize(string text)
    {
        var o = new MemoryStream();
        WriteText(o, text);
        return (int)o.Length;
    }

    /// <summary>A length: its low six bits in the first byte with 0x80 for 8-bit text and 0x40 when more follow, then
    /// seven bits a byte.</summary>
    private static void WriteLength(Stream o, int length, bool narrow)
    {
        int first = (narrow ? 0x80 : 0) | (length & 0x3F);
        if (length > 0x3F) first |= 0x40;
        o.WriteByte((byte)first);
        for (int rest = length >> 6; rest > 0; rest >>= 7)
            o.WriteByte((byte)((rest & 0x7F) | (rest > 0x7F ? 0x80 : 0)));
    }

    private static int Length(byte[] d, ref int at, out bool narrow)
    {
        Need(d, at, 1);
        int first = d[at++];
        narrow = (first & 0x80) != 0;
        int length = first & 0x3F;
        if ((first & 0x40) != 0)
        {
            int shift = 6, b;
            do
            {
                Need(d, at, 1);
                b = d[at++];
                length |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0 && shift < 32);
        }
        return length;
    }

    private static string Text(byte[] d, ref int at)
    {
        int length = Length(d, ref at, out bool narrow);
        int bytes = narrow ? length : length * 2;
        Need(d, at, bytes);
        at += bytes;
        return narrow ? Encoding.Latin1.GetString(d, at - bytes, bytes) : Encoding.Unicode.GetString(d, at - bytes, bytes);
    }

    private static void Need(byte[] d, int at, int count)
    {
        if (at < 0 || count < 0 || at + (long)count > d.Length) throw Damaged();
    }

    private static InvalidDataException Damaged() => new("The CR2W file's tables point outside it; it's damaged or cut short.");

    private static int I32(byte[] d, int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
    private static int U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
}
