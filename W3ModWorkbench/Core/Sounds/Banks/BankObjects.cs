using System.Buffers.Binary;
using System.Text;

namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>One of a bank's objects: its type, ID and everything it keeps, read into a tree of named values.</summary>
public sealed record BankObject(byte Type, string TypeName, uint Id, BankField Fields)
{
    /// <summary>Wwise's class for it (CAkSound, CAkActionPlay…), the name its tree has.</summary>
    public string ClassName => Fields.Name;

    /// <summary>Whether it was read to its last byte without trouble.</summary>
    public bool ReadWhole => Fields.Error is null && Fields.Find("unread") is null;
}

/// <summary>
/// A Wwise bank's objects (its HIRC section) read in full, for bank versions <see cref="BankFormat.Oldest"/>
/// on: every sound, container, event, action, bus, state, modulator and attenuation with all its settings. Wwise
/// keeps names only as hashes; <see cref="BankNames"/> names what it can.
/// </summary>
public sealed class BankObjects
{
    public uint Version { get; private init; }
    public uint BankId { get; private init; }

    /// <summary>Banks this one names in its STID section (the banks its events load), by ID.</summary>
    public IReadOnlyDictionary<uint, string> BankFileNames { get; private init; } = new Dictionary<uint, string>();

    public IReadOnlyList<BankObject> Objects { get; private init; } = [];

    /// <summary>The bank's other sections (header, sounds index, bank names, and an Init.bnk's global settings), read in full, by tag.</summary>
    public IReadOnlyList<(string Tag, BankField Fields)> Sections { get; private init; } = [];

    /// <summary>How the bank's version lays things out.</summary>
    internal BankFormat Format { get; private init; } = new(0, false);

    /// <summary>The bank's sections' tags in the order the file has them (HIRC and DATA included).</summary>
    public IReadOnlyList<string> Order { get; private init; } = [];

    /// <summary>Why the objects couldn't be read, when they couldn't (a version too old, no HIRC section).</summary>
    public string? Problem { get; private init; }

    public BankObject? this[uint id] => _byId.GetValueOrDefault(id);

    private Dictionary<uint, BankObject> _byId = [];

    public static BankObjects Read(byte[] bank)
    {
        uint version = 0, bankId = 0;
        bool feedback = false;
        var objects = new List<BankObject>();
        var names = new Dictionary<uint, string>();
        bool hasHirc = false;
        var sections = new List<(string, BankField)>();
        var order = new List<string>();
        for (int at = 0; at + 8 <= bank.Length;)
        {
            string tag = Encoding.ASCII.GetString(bank, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(at + 4));
            int body = at + 8;
            if (size < 0 || body + size > bank.Length) break;
            order.Add(tag);
            switch (tag)
            {
                case "BKHD" when size >= 8:
                    version = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(body));
                    bankId = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(body + 4));
                    // up to 126, whether the bank has motion feedback data, which adds a field to every node
                    if (version <= 126 && size >= 16) feedback = (BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(body + 12)) & 1) != 0;
                    break;
                case "STID" when size >= 8:
                    int count = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(body + 4));
                    for (int i = 0, p = body + 8; i < count && p + 5 <= body + size; i++)
                    {
                        uint id = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(p));
                        int length = bank[p + 4];
                        if (p + 5 + length > body + size) break;
                        names[id] = Encoding.UTF8.GetString(bank, p + 5, length);
                        p += 5 + length;
                    }
                    break;
                case "HIRC" when size >= 4:
                    hasHirc = true;
                    if (version < BankFormat.Oldest) break;
                    var format = new BankFormat((int)version, feedback);
                    int items = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(body));
                    for (int i = 0, p = body + 4; i < items && p + 5 <= body + size; i++)
                    {
                        byte type = bank[p];
                        int length = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(p + 1));
                        if (length < 4 || p + 5 + length > body + size) break;
                        uint id = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(p + 5));
                        string typeName = SoundBank.KindOf(type, version);
                        var root = new BankField(BankObjectReader.ClassOf(type, format), "node", null, p, 5 + length);
                        BankObjectReader.Read(root, bank, p + 5, length, type, format);
                        objects.Add(new BankObject(type, typeName, id, root));
                        p += 5 + length;
                    }
                    break;
            }
            // every section but the objects, read in full once the header has given the version
            if (tag != "HIRC" && version >= BankFormat.Oldest)
            {
                var field = new BankField(tag, "node", null, at, 8 + size);
                if (tag == "DATA") field.Children.Add(new BankField("sounds", "gap", null, body, size) { Parent = field });
                else BankSections.Read(field, tag, bank, body, size, new BankFormat((int)version, feedback));
                sections.Add((tag, field));
            }
            at = body + size;
        }
        string? problem = !hasHirc ? null : version < BankFormat.Oldest ? $"version {version}" : null; // loc-ignore
        var result = new BankObjects { Version = version, BankId = bankId, Objects = objects, BankFileNames = names, Problem = problem, Sections = sections, Order = order, Format = new BankFormat((int)version, feedback) };
        result._byId = [];
        foreach (var o in objects) result._byId.TryAdd(o.Id, o);
        return result;
    }
}
