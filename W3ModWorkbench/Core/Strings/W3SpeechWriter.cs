using System.Buffers.Binary;
using System.IO;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Strings;

/// <summary>A voice line's sound as a speech file keeps it: the Wwise RIFF file, its length in seconds, and the number after it.</summary>
public sealed record SpeechVoice(byte[] Wem, float Duration, uint Compression);

/// <summary>One line to write: its string ID, and its sound and lip sync, each read only when it's written.</summary>
public sealed record SpeechLine(uint StringId, Func<SpeechVoice?> Voice, Func<byte[]?> LipSync);

/// <summary>
/// Writes a language's voice lines as a <c>.w3speech</c> file, laid out as the game's own: "CPSW", the version, the
/// language key's upper half, the count (packed as the strings files pack lengths), forty bytes a line, the key's lower
/// half, then each line's sound and lip sync one after another. Lines go in the order of their stored ID (the string ID
/// mixed with the language's number), which the game searches by halves, so any other order loses lines. A sound is
/// its length, the RIFF file, its length in seconds and a 1; a line without a lip sync has 0 for where and how long it
/// is. Proven by writing the game's own <c>enpc.w3speech</c> (64,718 lines, 4.2 GB) back byte for byte.
/// </summary>
public static class W3SpeechWriter
{
    private const uint Magic = 0x57535043; // "CPSW"

    /// <summary>The version the Remastered's speech files are.</summary>
    public const uint Version = 164;

    /// <summary>What the game saves after a sound: 1 in all of its 64,718 lines (older mods' files have 4).</summary>
    public const uint Compression = 1;

    /// <summary>
    /// Writes <paramref name="lines"/> to <paramref name="output"/>, which must be able to seek: the table of where each
    /// line is goes in once its sound and lip sync are written. Any number of gigabytes; one line is in memory at a time.
    /// </summary>
    public static void Write(Stream output, W3Language language, IReadOnlyList<SpeechLine> lines, uint version = Version,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        if (lines.Select(l => l.StringId).Distinct().Count() != lines.Count) throw new InvalidDataException(L.T("W3SpeechWriter.SameIdTwice"));
        var sorted = lines.OrderBy(l => l.StringId ^ language.Magic).ToList();
        byte[] count = CountBytes(sorted.Count);
        long start = output.Position;
        var head = new byte[10];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(8), (ushort)(language.Key >> 16));
        output.Write(head);
        output.Write(count);
        long tableAt = output.Position;
        var table = new byte[sorted.Count * 40L];
        output.Write(table);
        var tail = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tail, (ushort)language.Key);
        output.Write(tail);

        for (int i = 0; i < sorted.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((i, sorted.Count));
            var line = sorted[i];
            var entry = table.AsSpan(i * 40, 40);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, line.StringId ^ language.Magic);
            if (line.Voice() is { } voice)
            {
                long at = output.Position - start;
                var length = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, voice.Wem.Length);
                output.Write(length);
                output.Write(voice.Wem);
                var after = new byte[8];
                BinaryPrimitives.WriteSingleLittleEndian(after, voice.Duration);
                BinaryPrimitives.WriteUInt32LittleEndian(after.AsSpan(4), voice.Compression);
                output.Write(after);
                BinaryPrimitives.WriteInt64LittleEndian(entry[8..], at);
                BinaryPrimitives.WriteInt64LittleEndian(entry[16..], voice.Wem.Length + 12L);
            }
            if (line.LipSync() is { Length: > 0 } lipSync)
            {
                BinaryPrimitives.WriteInt64LittleEndian(entry[24..], output.Position - start);
                BinaryPrimitives.WriteInt64LittleEndian(entry[32..], lipSync.Length);
                output.Write(lipSync);
            }
        }
        long end = output.Position;
        output.Position = tableAt;
        output.Write(table);
        output.Position = end;
        progress?.Report((sorted.Count, sorted.Count));
    }

    /// <summary>
    /// A count packed as the speech and strings files pack it: six bits and a flag for more in the first byte (its top
    /// bit is a sign, never set here), then seven bits a byte, each with a flag for more.
    /// </summary>
    private static byte[] CountBytes(int count)
    {
        var bytes = new List<byte> { (byte)(count & 0x3F) };
        count >>= 6;
        if (count > 0) bytes[0] |= 0x40;
        while (count > 0)
        {
            byte b = (byte)(count & 0x7F);
            count >>= 7;
            if (count > 0) b |= 0x80;
            bytes.Add(b);
        }
        return [.. bytes];
    }

    /// <summary>
    /// The length in seconds the game saves with a sound: its "data" chunk's size over the bytes a second "fmt " gives,
    /// as every one of the game's lines has it.
    /// </summary>
    public static float Duration(byte[] wem)
    {
        var info = Sounds.WemInfo.Read(wem);
        var data = info.Chunks.FirstOrDefault(c => c.Id == "data");
        return data is null || info.AverageBytesPerSecond <= 0 ? 0 : (float)((double)data.Size / info.AverageBytesPerSecond);
    }
}
