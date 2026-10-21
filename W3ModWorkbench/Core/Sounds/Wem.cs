using System.Buffers.Binary;
using System.IO;
using System.Text;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>A part of a RIFF file: its four-letter ID, where its data starts and how long it is.</summary>
public sealed record RiffChunk(string Id, int Offset, int Size);

/// <summary>A point marked in a sound (a cue), at a sample, with its label when it has one.</summary>
public sealed record SoundMarker(uint Id, long Sample, string? Label);

/// <summary>
/// What a Wwise sound (.wem, a RIFF file) says about itself: its codec, channels, rate and length, its loop and marked
/// points, and the parts it's made of. Every codec's are read, whether or not it can be played.
/// </summary>
public sealed class WemInfo
{
    public required ushort Codec { get; init; }
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public int AverageBytesPerSecond { get; init; }
    public int BlockAlign { get; init; }
    public int BitsPerSample { get; init; }

    /// <summary>The channels' speakers as a mask (front left 1, front right 2, centre 4, LFE 8, back left 16…), or 0.</summary>
    public uint ChannelMask { get; init; }

    /// <summary>How many samples a channel has, or 0 when the sound doesn't say.</summary>
    public long Samples { get; init; }

    /// <summary>Where it loops, in samples, when it does.</summary>
    public (long Start, long End)? Loop { get; init; }

    public IReadOnlyList<SoundMarker> Markers { get; init; } = [];
    public required IReadOnlyList<RiffChunk> Chunks { get; init; }
    public required int Size { get; init; }

    public double Duration => SampleRate > 0 && Samples > 0 ? (double)Samples / SampleRate : 0;

    public string CodecName => Codec switch
    {
        0x0001 => "PCM",
        0xFFFE => "PCM (extensible)",
        0x0002 => "Wwise IMA ADPCM",
        0x0011 => "IMA ADPCM",
        0xFFFF => "Wwise Vorbis",
        0x3040 => "Wwise Opus (Ogg)",
        0x3041 => "Wwise Opus",
        0x0166 => "XMA2",
        0x8311 => "PTADPCM",
        _ => $"codec 0x{Codec:X4}",
    };

    /// <summary>The speakers its channels are for, by its mask, or by how many there are.</summary>
    public string Speakers
    {
        get
        {
            string[] names = ["FL", "FR", "C", "LFE", "BL", "BR", "FLC", "FRC", "BC", "SL", "SR"];
            if (ChannelMask != 0)
                return string.Join(" ", names.Where((_, i) => (ChannelMask & (1u << i)) != 0));
            return Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{Channels} channels" };
        }
    }

    public static bool IsRiff(byte[] data) => data.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x46464952;

    /// <summary>Reads a sound's RIFF parts and its "fmt " (and its loop and markers, when it has them).</summary>
    public static WemInfo Read(byte[] riff)
    {
        if (!IsRiff(riff)) throw new InvalidDataException("The sound isn't a RIFF file.");
        var chunks = new List<RiffChunk>();
        for (int at = 12; at + 8 <= riff.Length;)
        {
            string id = Encoding.ASCII.GetString(riff, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(riff.AsSpan(at + 4));
            if (size < 0 || at + 8L + size > riff.Length) size = riff.Length - at - 8;
            chunks.Add(new RiffChunk(id, at + 8, size));
            // a LIST holds parts of its own, after its type
            at += 8 + size + (size & 1);
        }
        var fmtChunk = chunks.FirstOrDefault(c => c.Id == "fmt ") ?? throw new InvalidDataException("The sound has no \"fmt \".");
        var f = riff.AsSpan(fmtChunk.Offset, fmtChunk.Size);
        if (f.Length < 16) throw new InvalidDataException("The sound's \"fmt \" is cut short.");
        ushort codec = BinaryPrimitives.ReadUInt16LittleEndian(f);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(f[4..]);
        int average = BinaryPrimitives.ReadInt32LittleEndian(f[8..]);
        int block = BinaryPrimitives.ReadUInt16LittleEndian(f[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
        uint mask = f.Length >= 0x18 ? BinaryPrimitives.ReadUInt32LittleEndian(f[0x14..]) : 0;
        // Wwise keeps its own channel layout there: the count in the low byte, the kind in the next four bits, the speakers above
        if (codec != 0xFFFE && (mask & 0xFF) == channels && ((mask >> 8) & 0xF) is 1 or 2) mask >>= 12;
        var data = chunks.FirstOrDefault(c => c.Id == "data");

        long samples = codec switch
        {
            0x3041 when f.Length >= 0x1C => BinaryPrimitives.ReadInt32LittleEndian(f[0x18..]),
            0xFFFF when f.Length >= 0x1C => BinaryPrimitives.ReadUInt32LittleEndian(f[0x18..]),
            0x0001 or 0xFFFE when block > 0 && data is not null => data.Size / block,
            // 64 samples a channel in each 36 bytes a channel
            0x0002 when block > 0 && channels > 0 && data is not null => (long)data.Size / block * ((block / channels - 4) * 2 + 1),
            _ => 0,
        };
        // a Vorbis sound may keep its sample count in a "vorb" part of its own
        if (codec == 0xFFFF && samples == 0 && chunks.FirstOrDefault(c => c.Id == "vorb") is { Size: >= 4 } vorb)
            samples = BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(vorb.Offset));

        (long, long)? loop = null;
        if (chunks.FirstOrDefault(c => c.Id == "smpl") is { Size: >= 0x24 + 24 } smpl && BinaryPrimitives.ReadInt32LittleEndian(riff.AsSpan(smpl.Offset + 0x1C)) > 0)
            loop = (BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(smpl.Offset + 0x24 + 8)), BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(smpl.Offset + 0x24 + 12)));

        var labels = new Dictionary<uint, string>();
        foreach (var list in chunks.Where(c => c.Id == "LIST" && c.Size >= 4 && Encoding.ASCII.GetString(riff, c.Offset, 4) == "adtl"))
            for (int at = list.Offset + 4; at + 12 <= list.Offset + list.Size;)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(riff.AsSpan(at + 4));
                if (size < 4 || at + 8 + size > riff.Length) break;
                if (Encoding.ASCII.GetString(riff, at, 4) == "labl")
                    labels[BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(at + 8))] = Encoding.UTF8.GetString(riff, at + 12, size - 4).TrimEnd('\0');
                at += 8 + size + (size & 1);
            }
        var markers = new List<SoundMarker>();
        if (chunks.FirstOrDefault(c => c.Id == "cue ") is { Size: >= 4 } cue)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(riff.AsSpan(cue.Offset));
            for (int i = 0; i < count && cue.Offset + 4 + (i + 1) * 24 <= cue.Offset + cue.Size; i++)
            {
                int at = cue.Offset + 4 + i * 24;
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(at));
                markers.Add(new SoundMarker(id, BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(at + 4)), labels.GetValueOrDefault(id)));
            }
        }
        return new WemInfo
        {
            Codec = codec, Channels = channels, SampleRate = rate, AverageBytesPerSecond = average, BlockAlign = block, BitsPerSample = bits,
            ChannelMask = mask, Samples = samples, Loop = loop, Markers = markers, Chunks = chunks, Size = riff.Length,
        };
    }
}

/// <summary>A sound a bank plays: the object that plays it and the sound's ID, kept in the bank or streamed from the cache.</summary>
public sealed record BankSound(uint ObjectId, uint SourceId, string Kind, bool Streamed, int MediaSize);

/// <summary>
/// A Wwise sound bank (.bnk): its header (the bank's version and ID), the sounds it keeps itself (DIDX lists them, DATA
/// holds them), and its objects (HIRC: sounds, music tracks, events, actions, containers, buses…), the sounds and music
/// tracks with the sounds they play.
/// </summary>
public sealed class SoundBank
{
    public uint Version { get; private init; }
    public uint Id { get; private init; }

    /// <summary>The sounds the bank keeps, by ID: where each is in the bank and how long.</summary>
    public required IReadOnlyDictionary<uint, (int Offset, int Size)> Embedded { get; init; }

    /// <summary>The sounds its sound objects and music tracks play.</summary>
    public required IReadOnlyList<BankSound> Sounds { get; init; }

    /// <summary>How many objects of each kind it has.</summary>
    public required IReadOnlyDictionary<string, int> Objects { get; init; }

    public required IReadOnlyList<RiffChunk> Sections { get; init; }

    private byte[] _data = [];

    /// <summary>A sound the bank keeps, as a Wwise RIFF file, or null.</summary>
    public byte[]? EmbeddedSound(uint id) => Embedded.TryGetValue(id, out var at) ? _data.AsSpan(at.Offset, at.Size).ToArray() : null;

    private static string KindOf(byte type) => type switch
    {
        1 => "States", 2 => "Sounds", 3 => "Actions", 4 => "Events", 5 => "Random or sequence containers", 6 => "Switch containers",
        7 => "Actor-mixers", 8 => "Buses", 9 => "Blend containers", 10 => "Music segments", 11 => "Music tracks", 12 => "Music switches",
        13 => "Music playlists", 14 => "Attenuations", 15 => "Dialogue events", 16 => "Effect share sets", 17 => "Effects", 18 => "Auxiliary buses",
        19 => "LFOs", 20 => "Envelopes", 21 => "Audio devices", 22 => "Time modulators",
        _ => $"Objects of type {type}",
    };

    public static SoundBank Read(byte[] bank)
    {
        var sections = new List<RiffChunk>();
        var embedded = new Dictionary<uint, (int, int)>();
        var index = new List<(uint Id, int Offset, int Size)>();
        var sounds = new List<BankSound>();
        var objects = new Dictionary<string, int>();
        uint version = 0, id = 0;
        int data = -1;
        for (int at = 0; at + 8 <= bank.Length;)
        {
            string tag = Encoding.ASCII.GetString(bank, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(at + 4));
            int body = at + 8;
            if (size < 0 || body + size > bank.Length) break;
            sections.Add(new RiffChunk(tag, body, size));
            switch (tag)
            {
                case "BKHD" when size >= 8:
                    version = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(body));
                    id = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(body + 4));
                    break;
                case "DIDX":
                    for (int i = body; i + 12 <= body + size; i += 12)
                        index.Add((BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(i)), BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(i + 4)), BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(i + 8))));
                    break;
                case "DATA":
                    data = body;
                    break;
                case "HIRC" when size >= 4:
                    int count = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(body));
                    for (int i = body + 4, n = 0; n < count && i + 9 <= body + size; n++)
                    {
                        byte type = bank[i];
                        int length = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(i + 1));
                        if (length < 4 || i + 5 + length > body + size) break;
                        string kind = KindOf(type);
                        objects[kind] = objects.GetValueOrDefault(kind) + 1;
                        uint objectId = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(i + 5));
                        // a sound: its source after its ID (the codec's plugin, how it's kept, the source's ID, its size)
                        if (type == 2 && length >= 4 + 14)
                            sounds.Add(Source(bank, i + 9, objectId, "Sound"));
                        // a music track: a flags byte, then its sources, each as a sound's
                        else if (type == 11 && length >= 4 + 5)
                            sounds.AddRange(TrackSources(bank, i + 9, i + 5 + length, objectId));
                        i += 5 + length;
                    }
                    break;
            }
            at = body + size;
        }
        if (data >= 0)
            foreach (var (soundId, offset, size) in index)
                if (offset >= 0 && size > 0 && data + offset + size <= bank.Length) embedded.TryAdd(soundId, (data + offset, size));
        return new SoundBank { Version = version, Id = id, Embedded = embedded, Sounds = sounds, Objects = objects, Sections = sections, _data = bank };
    }

    /// <summary>A source as a sound object or a music track keeps it: the codec's plugin, how it's kept (0 in the bank, 1 and 2 streamed), its ID and size.</summary>
    private static BankSound Source(byte[] bank, int at, uint objectId, string kind)
    {
        byte stream = bank[at + 4];
        return new BankSound(objectId, BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(at + 5)), kind, stream != 0, BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(at + 9)));
    }

    /// <summary>A music track's sources, when they read sensibly: a count, then each source's fourteen bytes.</summary>
    private static IEnumerable<BankSound> TrackSources(byte[] bank, int at, int end, uint objectId)
    {
        // later banks keep a byte of flags before the count
        foreach (int skip in new[] { 1, 0 })
        {
            int count = at + skip + 4 <= end ? BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(at + skip)) : -1;
            if (count < 0 || count > 64 || at + skip + 4 + count * 14 > end) continue;
            var found = new List<BankSound>();
            bool sensible = true;
            for (int s = 0; s < count && sensible; s++)
            {
                int source = at + skip + 4 + s * 14;
                // a source's plugin is a codec: its low bits say so
                sensible = (BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(source)) & 0xF) == 1 && bank[source + 4] <= 2;
                if (sensible) found.Add(Source(bank, source, objectId, "Music track"));
            }
            if (sensible) return found;
        }
        return [];
    }
}

/// <summary>Turns a Wwise sound into samples to play, for the codecs the tool reads: Wwise Opus and PCM.</summary>
public static class SoundDecoder
{
    /// <summary>Whether a codec can be played.</summary>
    public static bool CanPlay(ushort codec) => codec is 0x3041 or 0x0001 or 0xFFFE;

    /// <summary>The sound's samples, its channels interleaved, and its rate. Throws for a codec it doesn't read.</summary>
    public static (short[] Samples, int Channels, int Rate) Decode(byte[] riff, WemInfo info)
    {
        switch (info.Codec)
        {
            case 0x3041:
                var (samples, channels) = WwiseOpus.Decode(riff);
                return (samples, channels, WwiseOpus.SampleRate);
            case 0x0001 or 0xFFFE when info.BitsPerSample == 16:
                var data = info.Chunks.First(c => c.Id == "data");
                var pcm = new short[data.Size / 2];
                Buffer.BlockCopy(riff, data.Offset, pcm, 0, pcm.Length * 2);
                return (pcm, Math.Max(1, info.Channels), info.SampleRate);
            default:
                throw new NotSupportedException($"{info.CodecName} sounds can't be played yet.");
        }
    }
}
