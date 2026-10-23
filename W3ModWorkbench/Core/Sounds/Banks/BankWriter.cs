// loc-ignore-start
using System.Buffers.Binary;
using System.Text;

namespace W3ModWorkbench.Core.Sounds.Banks;

/// <summary>
/// Writes a Wwise bank from its sections and objects as read (or as changed), each in the layout of the version
/// given: the readers run the other way. Proven by writing every game bank back byte for byte.
/// </summary>
internal static class BankWriter
{
    /// <summary>
    /// A bank: its sections in <paramref name="order"/>, the objects as its HIRC section, and <paramref name="data"/> as
    /// its DATA section (the sounds it keeps).
    /// </summary>
    public static byte[] Write(IReadOnlyList<string> order, IReadOnlyList<(string Tag, BankField Fields)> sections,
        IReadOnlyList<(byte Type, BankField Fields)> objects, byte[]? data, BankFormat f)
    {
        var output = new List<byte>();
        foreach (string tag in order)
        {
            byte[] body = tag switch
            {
                "HIRC" => Objects(objects, f),
                "DATA" => data ?? [],
                _ => BankSections.Write(sections.First(s => s.Tag == tag).Fields, tag, f),
            };
            output.AddRange(Encoding.ASCII.GetBytes(tag));
            Append(output, (uint)body.Length);
            output.AddRange(body);
        }
        return [.. output];
    }

    /// <summary>The HIRC section's body: how many objects, then each one's type, size and body.</summary>
    public static byte[] Objects(IReadOnlyList<(byte Type, BankField Fields)> objects, BankFormat f)
    {
        var output = new List<byte>();
        Append(output, (uint)objects.Count);
        foreach (var (type, fields) in objects)
        {
            byte[] body = BankObjectReader.Write(fields, type, f);
            output.Add(type);
            Append(output, (uint)body.Length);
            output.AddRange(body);
        }
        return [.. output];
    }

    private static void Append(List<byte> output, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        foreach (byte x in b) output.Add(x);
    }
}
// loc-ignore-end
