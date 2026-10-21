using System.Buffers.Binary;
using System.IO;

namespace W3ModWorkbench.Core.Strings;

/// <summary>
/// A language's voice lines (<c>enpc.w3speech</c>): for each spoken string, its sound and its lip sync. The file starts
/// "CPSW", its version and a count, then forty bytes a line: the string's ID (mixed with the language's magic, as in the
/// .w3strings files), four bytes, and where the sound and the lip sync are and how long each is. A sound is its length
/// then a Wwise RIFF file; the Remastered's are Opus (<see cref="WwiseOpus"/>).
/// </summary>
public sealed class W3Speech
{
    private const uint Magic = 0x57535043; // "CPSW"

    public string Path { get; }
    private readonly Dictionary<uint, (long Offset, long Size)> _sounds;
    private readonly Dictionary<uint, (long Offset, long Size)> _lipSyncs;

    private W3Speech(string path, Dictionary<uint, (long, long)> sounds, Dictionary<uint, (long, long)> lipSyncs)
    {
        Path = path;
        _sounds = sounds;
        _lipSyncs = lipSyncs;
    }

    public int Count => _sounds.Count;

    public bool Has(uint stringId) => _sounds.ContainsKey(stringId);

    /// <summary>Every line it has a sound for: the string's ID and the sound's size.</summary>
    public IEnumerable<(uint StringId, long Size)> Lines => _sounds.Select(s => (s.Key, s.Value.Size));

    /// <summary>Every line's sound with where it lies in the file (its length first, then the sound).</summary>
    public IEnumerable<(uint StringId, long Offset, long Size)> Index => _sounds.Select(s => (s.Key, s.Value.Offset, s.Value.Size));

    /// <summary>Whether a line has a lip sync.</summary>
    public bool HasLipSync(uint stringId) => _lipSyncs.ContainsKey(stringId);

    /// <summary>Reads a speech file's list of lines; the sounds are read when they're asked for.</summary>
    public static W3Speech Open(string path, W3Language language)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[16];
        fs.ReadExactly(head, 0, 13);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic) throw new InvalidDataException("This isn't a speech file.");
        // after the version and two bytes of the language's key, the count, packed as the strings files pack lengths
        int at = 10, count = head[at] & 0x3F, shift = 6;
        bool more = (head[at++] & 0x40) != 0;
        while (more)
        {
            int b = at < 13 ? head[at] : fs.ReadByte();
            if (b < 0) throw new InvalidDataException("The speech file is cut short.");
            at++;
            count |= (b & 0x7F) << shift;
            shift += 7;
            more = (b & 0x80) != 0 && shift < 32;
        }
        if (count < 0 || count > 10_000_000) throw new InvalidDataException("The speech file's count makes no sense.");
        fs.Position = at;
        var table = new byte[count * 40L];
        fs.ReadExactly(table);
        var sounds = new Dictionary<uint, (long, long)>(count);
        var lipSyncs = new Dictionary<uint, (long, long)>(count);
        for (int i = 0; i < count; i++)
        {
            var entry = table.AsSpan(i * 40, 40);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(entry) ^ language.Magic;
            long offset = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]), size = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            if (offset > 0 && size > 4 && size < 64 << 20) sounds.TryAdd(id, (offset, size));
            long lipOffset = BinaryPrimitives.ReadInt64LittleEndian(entry[24..]), lipSize = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]);
            if (lipOffset > 0 && lipSize > 16 && lipSize < 16 << 20) lipSyncs.TryAdd(id, (lipOffset, lipSize));
        }
        return new W3Speech(path, sounds, lipSyncs);
    }

    /// <summary>A line's sound as a Wwise RIFF file, or null when this file hasn't got it.</summary>
    public byte[]? Sound(uint stringId)
    {
        if (!_sounds.TryGetValue(stringId, out var at)) return null;
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = at.Offset;
        var data = new byte[at.Size];
        fs.ReadExactly(data);
        // its length comes first
        int length = BinaryPrimitives.ReadInt32LittleEndian(data);
        return length > 0 && length <= data.Length - 4 ? data.AsSpan(4, length).ToArray() : data[4..];
    }

    /// <summary>A line's lip sync as saved (see <see cref="Animation.LipSync"/>), or null when this file hasn't got it.</summary>
    public byte[]? LipSync(uint stringId)
    {
        if (!_lipSyncs.TryGetValue(stringId, out var at)) return null;
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = at.Offset;
        var data = new byte[at.Size];
        fs.ReadExactly(data);
        return data;
    }

    private static readonly Dictionary<string, Task<List<W3Speech>>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The speech files of a language: the installed mods' first, then the DLC's, then content0's, as the game loads
    /// them. Read once, in the background.
    /// </summary>
    public static Task<List<W3Speech>> ForLanguage(string gameFolder, W3Language language)
    {
        string key = $"{gameFolder}|{language.Handle}";
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var task) || task.IsFaulted)
                Cache[key] = task = Task.Run(() =>
                {
                    string name = $"{language.Handle}pc.w3speech";
                    var files = new List<string>();
                    foreach (string folder in new[] { "mods", "dlc" })
                    {
                        string root = System.IO.Path.Combine(gameFolder, folder);
                        if (!Directory.Exists(root)) continue;
                        foreach (string mod in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
                            if (File.Exists(System.IO.Path.Combine(mod, "content", name))) files.Add(System.IO.Path.Combine(mod, "content", name));
                    }
                    files.Add(System.IO.Path.Combine(gameFolder, "content", "content0", name));
                    var speech = new List<W3Speech>();
                    foreach (string file in files.Where(File.Exists))
                        try { speech.Add(Open(file, language)); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) { }
                    return speech;
                });
            return task;
        }
    }
}

/// <summary>
/// Turns a Wwise sound made with its Opus codec (format 0x3041, the Remastered's voices) into plain PCM. The RIFF's
/// "fmt " says the channels, the sample count, how many packets there are and how many samples to skip at the start;
/// "seek" has each packet's size as two bytes; "data" has the packets one after another, each a raw Opus packet.
/// </summary>
public static class WwiseOpus
{
    public const int SampleRate = 48000;

    /// <summary>The sound's samples at 48 kHz, its channels interleaved.</summary>
    public static (short[] Samples, int Channels) Decode(byte[] riff)
    {
        if (riff.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(riff) != 0x46464952) throw new InvalidDataException("The sound isn't a RIFF file.");
        int fmt = -1, fmtSize = 0, seek = -1, seekSize = 0, data = -1, dataSize = 0;
        for (int at = 12; at + 8 <= riff.Length;)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(at));
            int size = BinaryPrimitives.ReadInt32LittleEndian(riff.AsSpan(at + 4));
            if (size < 0 || at + 8L + size > riff.Length) size = riff.Length - at - 8;
            switch (id)
            {
                case 0x20746D66: fmt = at + 8; fmtSize = size; break; // "fmt "
                case 0x6B656573: seek = at + 8; seekSize = size; break; // "seek"
                case 0x61746164: data = at + 8; dataSize = size; break; // "data"
            }
            at += 8 + size + (size & 1);
        }
        if (fmt < 0 || data < 0 || seek < 0 || fmtSize < 0x22) throw new InvalidDataException("The sound is missing a part it needs.");
        var f = riff.AsSpan(fmt, fmtSize);
        if (BinaryPrimitives.ReadUInt16LittleEndian(f) != 0x3041) throw new InvalidDataException($"The sound's codec ({BinaryPrimitives.ReadUInt16LittleEndian(f):X4}) isn't Wwise Opus.");
        int channels = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(f[2..]), 1, 8);
        int total = BinaryPrimitives.ReadInt32LittleEndian(f[0x18..]);
        int packets = BinaryPrimitives.ReadInt32LittleEndian(f[0x1C..]);
        int skip = BinaryPrimitives.ReadUInt16LittleEndian(f[0x20..]);
        if (packets <= 0 || packets * 2 > seekSize) throw new InvalidDataException("The sound's packet list is cut short.");

        // more than two channels are several Opus streams in each packet, laid out as Opus's channel mapping family 1
        // lays them out (the Vorbis order); they're put back in the usual order after
        Func<byte[], int, int, short[], int> decode;
        if (channels <= 2)
        {
            var decoder = Concentus.OpusCodecFactory.CreateDecoder(SampleRate, channels);
            decode = (d, at, size, frame) => decoder.Decode(d.AsSpan(at, size), frame, 5760, false);
        }
        else
        {
            var (streams, coupled, mapping) = VorbisLayouts[channels - 1];
            var decoder = Concentus.OpusCodecFactory.CreateMultiStreamDecoder(SampleRate, channels, streams, coupled, mapping);
            decode = (d, at, size, frame) => decoder.DecodeMultistream(d.AsSpan(at, size), frame, 5760, false);
        }
        var output = new List<short>(Math.Max(0, total) * channels + 5760 * channels);
        var frame = new short[5760 * channels];
        int read = data;
        for (int i = 0; i < packets; i++)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(seek + i * 2));
            if (read + size > data + dataSize) break;
            int samples = decode(riff, read, size, frame);
            output.AddRange(frame.AsSpan(0, samples * channels).ToArray());
            read += size;
        }
        if (channels > 2 && WaveOrder[channels - 1] is { } order)
        {
            // from the Vorbis order (FL C FR …) to the usual one (FL FR C LFE …)
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output);
            var copy = new short[channels];
            for (int at = 0; at + channels <= span.Length; at += channels)
            {
                span.Slice(at, channels).CopyTo(copy);
                for (int c = 0; c < channels; c++) span[at + c] = copy[order[c]];
            }
        }
        // the encoder's delay is skipped, and the end trimmed to the sound's length
        int start = Math.Min(skip * channels, output.Count);
        int length = total > 0 ? Math.Min(total * channels, output.Count - start) : output.Count - start;
        return (output.GetRange(start, length).ToArray(), channels);
    }

    /// <summary>Opus's layouts of one to eight channels (mapping family 1): its streams, how many are stereo, and each channel's place.</summary>
    private static readonly (int Streams, int Coupled, byte[] Mapping)[] VorbisLayouts =
    [
        (1, 0, [0]), (1, 1, [0, 1]), (2, 1, [0, 2, 1]), (2, 2, [0, 1, 2, 3]), (3, 2, [0, 4, 1, 2, 3]),
        (4, 2, [0, 4, 1, 2, 3, 5]), (4, 3, [0, 4, 1, 2, 3, 5, 6]), (5, 3, [0, 6, 1, 2, 3, 4, 5, 7]),
    ];

    /// <summary>
    /// For each count of channels, which Vorbis-ordered channel goes in each place of the usual order: 3 is FL C FR to
    /// FL FR C, 5.1 is FL C FR BL BR LFE to FL FR C LFE BL BR, 7.1 FL C FR SL SR BL BR LFE to FL FR C LFE BL BR SL SR.
    /// </summary>
    private static readonly int[]?[] WaveOrder =
    [
        null, null, [0, 2, 1], null, [0, 2, 1, 3, 4], [0, 2, 1, 5, 3, 4], [0, 2, 1, 6, 5, 3, 4], [0, 2, 1, 7, 5, 6, 3, 4],
    ];

    /// <summary>Samples as a 16-bit PCM WAV file, which Windows can play as it is.</summary>
    public static byte[] ToWav(short[] samples, int channels, int rate = SampleRate)
    {
        int bytes = samples.Length * 2;
        var wav = new byte[44 + bytes];
        var w = wav.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(w, 0x46464952); // RIFF
        BinaryPrimitives.WriteInt32LittleEndian(w[4..], 36 + bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(w[8..], 0x45564157); // WAVE
        BinaryPrimitives.WriteUInt32LittleEndian(w[12..], 0x20746D66); // fmt
        BinaryPrimitives.WriteInt32LittleEndian(w[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(w[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(w[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(w[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(w[28..], rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(w[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(w[34..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(w[36..], 0x61746164); // data
        BinaryPrimitives.WriteInt32LittleEndian(w[40..], bytes);
        Buffer.BlockCopy(samples, 0, wav, 44, bytes);
        return wav;
    }
}
