using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Strings;

/// <summary>A language a .w3strings file can be for: its short name and the two numbers its text is scrambled with.</summary>
/// <param name="Key">Split across the file: the high half after the version, the low half in the last two bytes.</param>
/// <param name="Magic">XORed with each string ID, and the start of the rolling key that scrambles the text.</param>
public sealed record W3Language(string Handle, uint Key, uint Magic)
{
    public override string ToString() => Handle;
}

/// <summary>
/// One string: its ID, and the key scripts look it up by when it has one. A key is only stored as its hash,
/// so a decoded file knows <see cref="KeyName"/> only when it was found in a list of known names.
/// </summary>
public sealed record W3String(uint Id, uint KeyHash, string? KeyName, string Text);

/// <summary>A decoded .w3strings file: its language, format version and strings, in ID order.</summary>
public sealed record W3StringsFile(W3Language Language, uint Version, List<W3String> Strings);

/// <summary>
/// Reads and writes The Witcher 3's string tables (.w3strings), and the CSV files mods keep them in.
/// <para>
/// The file is "RTSW", a version, the high half of the language key, then three blocks: each string's ID
/// (XORed with the language's magic number) with where its text starts and how long it is; each key hash with
/// the ID it belongs to; and the text. The low half of the language key is the last two bytes. Counts are the
/// engine's packed integers.
/// </para>
/// <para>
/// Since the next-gen update the text is UTF-8, each string's bytes XORed with a rolling key and followed by a
/// zero byte, and lengths and offsets count bytes. The original game's files used UTF-16 and counted
/// characters; those are read too. Files are always written the next-gen way.
/// </para>
/// </summary>
public static class W3Strings
{
    /// <summary>What the next-gen tools write. The game's own files say 164.</summary>
    public const uint DefaultVersion = 162;

    /// <summary>
    /// The languages the game knows (its content0 has strings in each), in the order they're usually listed. Seven have no key; their files
    /// can't be told apart by content, so the file name decides.
    /// </summary>
    public static readonly IReadOnlyList<W3Language> Languages =
    [
        new("en", 0x43975139, 0x79321793),
        new("pl", 0x83496237, 0x73946816),
        new("de", 0x75886138, 0x42791159),
        new("fr", 0x23863176, 0x75921975),
        new("it", 0x45931894, 0x12375973),
        new("es", 0x18796651, 0x42387566),
        new("cz", 0x24987354, 0x21793217),
        new("hu", 0x42378932, 0x67823218),
        new("ru", 0x63481486, 0x42386347),
        new("jp", 0x54834893, 0x59825646),
        new("zh", 0x18632176, 0x16875467),
        new("ar", 0, 0),
        new("br", 0, 0),
        new("cn", 0, 0),
        new("esmx", 0, 0),
        new("kr", 0, 0),
        new("tr", 0, 0),
        new("ua", 0, 0),
    ];

    public static W3Language? Language(string handle) =>
        Languages.FirstOrDefault(l => l.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));

    /// <summary>The language a file's name says, e.g. "de" for de.w3strings or de.csv.</summary>
    public static W3Language? LanguageFromName(string path)
    {
        string name = Path.GetFileName(path);
        int dot = name.IndexOf('.');
        return Language(dot > 0 ? name[..dot] : name);
    }

    /// <summary>The hash a key is stored as: its letters lower-cased, as UTF-16, each folded in with ×31.</summary>
    public static uint HashKey(string key)
    {
        uint hash = 0;
        foreach (char c in key.ToLowerInvariant()) hash = unchecked(hash * 31 + c);
        return hash;
    }

    /// <summary>
    /// The ID range a mod is given: 211nnnn000 to 211nnnn999, nnnn being the mod's four-digit number. IDs
    /// outside it can clash with the game's or another mod's.
    /// </summary>
    public static bool InModIdSpace(uint id) => id >= 2110000000 && id <= 2119999999;

    // ---------------------------------------------------------------- reading

    /// <param name="path">The file's name, for a language with no key of its own.</param>
    /// <param name="keyNames">Known key names, by hash, to name the keys the file has.</param>
    public static W3StringsFile Read(byte[] data, string? path = null, IReadOnlyDictionary<uint, string>? keyNames = null)
    {
        if (data.Length < 14 || data[0] != 'R' || data[1] != 'T' || data[2] != 'S' || data[3] != 'W')
            throw new InvalidDataException(L.T("W3Strings.IsntW3stringsFileDoesnt"));
        var span = data.AsSpan();
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        uint key = (uint)BinaryPrimitives.ReadUInt16LittleEndian(span[8..]) << 16
                   | BinaryPrimitives.ReadUInt16LittleEndian(span[^2..]);
        var language = key == 0 && path is not null && LanguageFromName(path) is { Key: 0 } named
            ? named
            : Languages.FirstOrDefault(l => l.Key == key)
              ?? throw new InvalidDataException(L.T("W3Strings.FilesLanguageKeyIsnt", key));
        uint magic = language.Magic;

        int pos = 10;
        int count = ReadPacked(span, ref pos);
        var entries = new (uint Id, int Offset, int Length)[count];
        for (int i = 0; i < count; i++)
        {
            entries[i] = (ReadU32(span, ref pos) ^ magic, (int)ReadU32(span, ref pos), (int)ReadU32(span, ref pos));
        }
        int keyCount = ReadPacked(span, ref pos);
        var keys = new Dictionary<uint, uint>(keyCount);
        for (int i = 0; i < keyCount; i++)
        {
            uint hash = ReadU32(span, ref pos);
            keys.TryAdd(ReadU32(span, ref pos) ^ magic, hash);
        }
        int size = ReadPacked(span, ref pos);
        int start = pos;
        // next-gen files count the text in bytes; the original game's counted UTF-16 characters
        bool utf16 = start + size * 2 + 2 == data.Length;
        if (!utf16 && start + size + 2 != data.Length)
            throw new InvalidDataException(L.T("W3Strings.FileCutShortHas"));

        var strings = new List<W3String>(count);
        ushort init = (ushort)(magic >> 8);
        foreach (var (id, offset, length) in entries)
        {
            string text = utf16
                ? ReadUtf16(span.Slice(start + offset * 2, length * 2), length, init)
                : ReadUtf8(span.Slice(start + offset, length), init);
            uint hash = keys.GetValueOrDefault(id);
            string? name = hash != 0 && keyNames is not null && keyNames.TryGetValue(hash, out var n) ? n : null;
            strings.Add(new W3String(id, hash, name, text));
        }
        strings.Sort((a, b) => a.Id.CompareTo(b.Id));
        return new W3StringsFile(language, version, strings);
    }

    private static string ReadUtf8(ReadOnlySpan<byte> scrambled, ushort init)
    {
        var bytes = new byte[scrambled.Length];
        ushort rolling = init;
        int multiplier = scrambled.Length + 1;
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(scrambled[i] ^ (byte)(multiplier * rolling));
            rolling = Rotate(rolling);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string ReadUtf16(ReadOnlySpan<byte> scrambled, int length, ushort init)
    {
        var chars = new char[length];
        ushort rolling = init;
        for (int i = 0; i < length; i++)
        {
            ushort key = (ushort)((length + 1) * rolling);
            chars[i] = (char)(BinaryPrimitives.ReadUInt16LittleEndian(scrambled[(i * 2)..]) ^ key);
            rolling = Rotate(rolling);
        }
        return new string(chars);
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Writes strings the way the next-gen tools do, so the game reads them: entries in order of their scrambled
    /// IDs, keys in order of their hashes, and the text in ID order.
    /// </summary>
    public static byte[] Write(IEnumerable<W3String> strings, W3Language language, uint version = DefaultVersion)
    {
        var sorted = strings.OrderBy(s => s.Id).ToList();
        uint magic = language.Magic;
        ushort init = (ushort)(magic >> 8);

        var text = new MemoryStream();
        var placed = new List<(uint Id, int Offset, int Length)>(sorted.Count);
        foreach (var s in sorted)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s.Text);
            ushort rolling = init;
            int multiplier = bytes.Length + 1;
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] ^= (byte)(multiplier * rolling);
                rolling = Rotate(rolling);
            }
            placed.Add((s.Id, (int)text.Length, bytes.Length));
            text.Write(bytes);
            text.WriteByte(0);
        }

        var output = new MemoryStream();
        var four = new byte[4];
        void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(four, v);
            output.Write(four);
        }
        void U16(ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(four, v);
            output.Write(four, 0, 2);
        }

        output.Write("RTSW"u8);
        U32(version);
        U16((ushort)(language.Key >> 16));
        WritePacked(output, placed.Count);
        foreach (var (id, offset, length) in placed.OrderBy(p => p.Id ^ magic))
        {
            U32(id ^ magic);
            U32((uint)offset);
            U32((uint)length);
        }
        var keyed = sorted.Where(s => s.KeyHash != 0).OrderBy(s => s.KeyHash).ToList();
        WritePacked(output, keyed.Count);
        foreach (var s in keyed)
        {
            U32(s.KeyHash);
            U32(s.Id ^ magic);
        }
        WritePacked(output, (int)text.Length);
        text.Position = 0;
        text.CopyTo(output);
        U16((ushort)language.Key);
        return output.ToArray();
    }

    private static ushort Rotate(ushort value) => (ushort)(value << 1 | value >> 15);

    private static uint ReadU32(ReadOnlySpan<byte> span, ref int pos)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
        pos += 4;
        return value;
    }

    /// <summary>The engine's packed integer: 6 bits in the first byte, with a sign bit and a "more" bit, then 7 bits a byte.</summary>
    private static int ReadPacked(ReadOnlySpan<byte> span, ref int pos) => PackedInt.ReadSigned(span, ref pos);

    private static void WritePacked(Stream output, int value) => PackedInt.Write(output, value);

    // ---------------------------------------------------------------- CSV

    /// <summary>
    /// The CSV the modding tools use: a ;meta[language=xx] line, then id|key(hex)|key(str)|text, one string a line.
    /// A key whose name is known is written by name, otherwise by its hash.
    /// </summary>
    public static string ToCsv(W3StringsFile file)
    {
        var sb = new StringBuilder();
        sb.Append(";meta[language=").Append(file.Language.Handle).Append("]\n");
        sb.Append("; id      |key(hex)|key(str)| text\n");
        foreach (var s in file.Strings)
        {
            string hex = s.KeyHash == 0 || s.KeyName is not null ? "" : s.KeyHash.ToString("x8");
            sb.Append(s.Id).Append('|').Append(hex).Append('|').Append(s.KeyName).Append('|').Append(s.Text).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A line of a CSV that couldn't be used, and why.</summary>
    public sealed record CsvProblem(int Line, string Message);

    /// <summary>
    /// Reads a strings CSV. Lines starting with ; are comments, apart from the ;meta line naming the language.
    /// Lines that can't be read are skipped and listed in the problems. A CSV in REDkit's layout
    /// (<see cref="RedkitStringsCsv"/>) is read too: its <paramref name="prefer"/> column when it has one, else the
    /// first language with text.
    /// </summary>
    public static (W3Language? Language, List<W3String> Strings, List<CsvProblem> Problems) FromCsv(string csv, W3Language? prefer = null)
    {
        if (RedkitStringsCsv.Is(csv))
        {
            var (languages, redkitProblems) = RedkitStringsCsv.Read(csv);
            var chosen = languages.FirstOrDefault(p => prefer is not null && p.Key.Handle == prefer.Handle && p.Value.Count > 0);
            if (chosen.Key is null) chosen = languages.FirstOrDefault(p => p.Value.Count > 0);
            return (chosen.Key, chosen.Value ?? [], redkitProblems);
        }
        W3Language? language = null;
        var strings = new List<W3String>();
        var problems = new List<CsvProblem>();
        int number = 0;
        foreach (string raw in csv.Split('\n'))
        {
            number++;
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith(';'))
            {
                const string meta = ";meta[language=";
                if (line.StartsWith(meta, StringComparison.OrdinalIgnoreCase) && line.IndexOf(']') is int end and > 0)
                {
                    string handle = line[meta.Length..end];
                    language = Language(handle);
                    if (language is null) problems.Add(new(number, L.T("W3Strings.LanguageIsntOneGame", handle)));
                }
                continue;
            }
            var parts = line.Split('|', 4);
            if (parts.Length < 4)
            {
                problems.Add(new(number, L.T("W3Strings.ExpectedIdKeyHex")));
                continue;
            }
            if (!uint.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
            {
                problems.Add(new(number, L.T("W3Strings.IsntStringID", parts[0].Trim())));
                continue;
            }
            string hex = parts[1].Trim(), name = parts[2].Trim();
            uint hash = 0;
            if (name.Length > 0) hash = HashKey(name);
            else if (hex.Length > 0 && !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
            {
                problems.Add(new(number, L.T("W3Strings.IsntKeyHashHex", hex)));
                continue;
            }
            strings.Add(new W3String(id, hash, name.Length > 0 ? name : null, parts[3]));
        }
        return (language, strings, problems);
    }
}
