using System.Buffers.Binary;
using System.IO;
using System.Text;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// Turns a Wwise sound made with its Vorbis codec (format 0xFFFF) into a plain Ogg Vorbis stream, as ww2ogg does (a port
/// of its rebuilding, BSD licensed: https://github.com/hcs64/ww2ogg), to decode with an ordinary Vorbis decoder. Wwise
/// keeps Vorbis packets with their headers cut down: the identification and comment headers are left out (the "fmt "
/// and "vorb" say what they would), the setup header names each codebook by its number in a library (the aoTuV 6.03
/// one, <c>packed_codebooks_aoTuV_603.bin</c>) and keeps the rest of it in fewer bits, and each audio packet drops the
/// bits saying what its window's neighbours are. Each is put back as the Vorbis specification has it, with the samples
/// each packet ends on as its page's granule position, so a decoder knows the sound's length.
/// </summary>
public static class WwiseVorbis
{
    private static readonly Lazy<CodebookLibrary> Library = new(() => new CodebookLibrary(ReadLibrary()));

    private static byte[] ReadLibrary()
    {
        using var stream = typeof(WwiseVorbis).Assembly.GetManifestResourceStream("W3ModWorkbench.Assets.Sounds.packed_codebooks_aoTuV_603.bin")
            ?? throw new InvalidDataException(L.T("WwiseVorbis.VorbisCodebookLibraryIsnt"));
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>The sound as an Ogg Vorbis file.</summary>
    public static byte[] ToOgg(byte[] riff) => Rebuild(riff, null);

    /// <summary>
    /// Each audio packet's end (counted from the start of the "data" chunk) and the samples decoded once it's read,
    /// up to the first that reaches <paramref name="upTo"/> samples: how far into a sound a given length of it reaches.
    /// </summary>
    public static List<(int End, long Samples)> AudioPackets(byte[] riff, long upTo = long.MaxValue)
    {
        var packets = new List<(int, long)>();
        Rebuild(riff, (end, samples) =>
        {
            packets.Add((end, samples));
            return samples < upTo;
        });
        return packets;
    }

    /// <summary>The Ogg stream, each audio packet told to <paramref name="onPacket"/>, which can stop the rest.</summary>
    private static byte[] Rebuild(byte[] riff, Func<int, long, bool>? onPacket)
    {
        var info = WemInfo.Read(riff);
        if (info.Codec != 0xFFFF) throw new InvalidDataException(L.T("WwiseVorbis.SoundIsntWwiseVorbis"));
        var fmt = info.Chunks.First(c => c.Id == "fmt ");
        var data = info.Chunks.FirstOrDefault(c => c.Id == "data") ?? throw new InvalidDataException(L.T("WwiseVorbis.SoundHasNoData"));
        // the "vorb" part: its own, or kept at the end of a 0x42-byte "fmt "
        int vorb, vorbSize;
        if (info.Chunks.FirstOrDefault(c => c.Id == "vorb") is { } own) (vorb, vorbSize) = (own.Offset, own.Size);
        else if (fmt.Size == 0x42) (vorb, vorbSize) = (fmt.Offset + 0x18, -1);
        else throw new InvalidDataException(L.T("WwiseVorbis.VorbisSoundHasNo"));

        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(at));
        bool noGranule = false, modPackets = false;
        int at = vorb + 0x18;
        if (vorbSize is -1 or 0x2A)
        {
            noGranule = true;
            uint signal = U32(vorb + 4);
            modPackets = signal is not (0x4A or 0x4B or 0x69 or 0x70);
            at = vorb + 0x10;
        }
        else if (vorbSize is 0x28 or 0x2C)
            throw new NotSupportedException(L.T("WwiseVorbis.SoundEarlyWwiseVorbis"));
        uint setupOffset = U32(at), firstAudioOffset = U32(at + 4);
        int blockPows = vorbSize is -1 or 0x2A ? vorb + 0x24 + 4 : vorb + 0x2C + 4;
        int blocksize0Pow = riff[blockPows], blocksize1Pow = riff[blockPows + 1];
        int channels = info.Channels;
        int dataStart = data.Offset, dataEnd = data.Offset + data.Size;

        var ogg = new OggWriter();

        // identification header
        var id = new BitWriter();
        Header(id, 1);
        id.Write(0, 32);
        id.Write((uint)channels, 8);
        id.Write((uint)info.SampleRate, 32);
        id.Write(0, 32);
        id.Write((uint)(info.AverageBytesPerSecond * 8), 32);
        id.Write(0, 32);
        id.Write((uint)blocksize0Pow, 4);
        id.Write((uint)blocksize1Pow, 4);
        id.Write(1, 1);
        ogg.Packet(id.ToArray(), 0, first: true);

        // comment header
        var comment = new BitWriter();
        Header(comment, 3);
        var vendor = Encoding.ASCII.GetBytes("converted from Audiokinetic Wwise by W3 Mod Workbench (ww2ogg)"); // loc-ignore
        comment.Write((uint)vendor.Length, 32);
        foreach (byte b in vendor) comment.Write(b, 8);
        comment.Write(0, 32);
        comment.Write(1, 1);
        ogg.Packet(comment.ToArray(), 0);

        // setup header, rebuilt
        var (setupPayload, setupSize, setupNext) = Packet(riff, dataStart + (int)setupOffset, noGranule);
        var setup = new BitWriter();
        Header(setup, 5);
        var (modeBlockFlag, modeBits) = RebuildSetup(new BitReader(riff, setupPayload, setupPayload + setupSize), setup, channels, setupSize);
        if (setupNext != dataStart + firstAudioOffset) throw new InvalidDataException(L.T("WwiseVorbis.FirstAudioPacketDoesnt"));
        ogg.Packet(setup.ToArray(), 0);

        // audio packets: the bits Wwise dropped put back, each on a page of its own
        int blocksize0 = 1 << blocksize0Pow, blocksize1 = 1 << blocksize1Pow;
        bool prevBlockFlag = false;
        long granule = 0;
        int previousSize = 0;
        long samples = info.Samples;
        for (int offset = dataStart + (int)firstAudioOffset; offset < dataEnd;)
        {
            var (payload, size, next) = Packet(riff, offset, noGranule);
            if (payload + size > dataEnd) break;
            var packet = new BitWriter();
            bool blockFlag = false;
            if (modPackets && size > 0)
            {
                packet.Write(0, 1);
                var first = new BitReader(riff, payload, payload + size);
                uint mode = first.Read(modeBits);
                packet.Write(mode, modeBits);
                uint remainder = first.Read(8 - modeBits);
                blockFlag = mode < modeBlockFlag.Length && modeBlockFlag[mode];
                if (blockFlag)
                {
                    // a long window says what its neighbours' windows are: the one before, and the next packet's
                    bool nextBlockFlag = false;
                    if (next < dataEnd)
                    {
                        var (nextPayload, nextSize, _) = Packet(riff, next, noGranule);
                        if (nextSize > 0 && nextPayload < dataEnd)
                        {
                            uint nextMode = new BitReader(riff, nextPayload, nextPayload + nextSize).Read(modeBits);
                            nextBlockFlag = nextMode < modeBlockFlag.Length && modeBlockFlag[nextMode];
                        }
                    }
                    packet.Write(prevBlockFlag ? 1u : 0, 1);
                    packet.Write(nextBlockFlag ? 1u : 0, 1);
                }
                prevBlockFlag = blockFlag;
                packet.Write(remainder, 8 - modeBits);
                for (int i = 1; i < size; i++) packet.Write(riff[payload + i], 8);
            }
            else
            {
                if (size > 0)
                {
                    uint mode = new BitReader(riff, payload, payload + size).Read(1 + modeBits) >> 1;
                    blockFlag = mode < modeBlockFlag.Length && modeBlockFlag[mode];
                }
                for (int i = 0; i < size; i++) packet.Write(riff[payload + i], 8);
            }
            // the samples it ends on: each packet after the first gives a quarter of its own block and its last's
            int blockSize = blockFlag ? blocksize1 : blocksize0;
            if (previousSize > 0) granule += previousSize / 4 + blockSize / 4;
            previousSize = blockSize;
            offset = next;
            if (onPacket is not null && !onPacket(offset - dataStart, granule)) break;
            bool last = offset >= dataEnd;
            ogg.Packet(packet.ToArray(), last && samples > 0 ? samples : granule, last: last);
        }
        return ogg.ToArray();
    }

    /// <summary>A packet's payload, size and where the next starts: a two-byte size, and four of granule unless they're left out.</summary>
    private static (int Payload, int Size, int Next) Packet(byte[] riff, int offset, bool noGranule)
    {
        if (offset + 2 > riff.Length) throw new InvalidDataException(L.T("WwiseVorbis.PacketRunsPastEnd"));
        int size = BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(offset));
        int header = noGranule ? 2 : 6;
        return (offset + header, size, offset + header + size);
    }

    private static void Header(BitWriter w, byte type)
    {
        w.Write(type, 8);
        foreach (byte b in "vorbis"u8) w.Write(b, 8);
    }

    private static int ILog(uint v)
    {
        int r = 0;
        while (v != 0) { r++; v >>= 1; }
        return r;
    }

    /// <summary>Rebuilds the setup header's codebooks, floors, residues, mappings and modes, returning each mode's block flag.</summary>
    private static (bool[] ModeBlockFlag, int ModeBits) RebuildSetup(BitReader ss, BitWriter os, int channels, int size)
    {
        uint codebookCount = ss.Read(8) + 1;
        os.Write(codebookCount - 1, 8);
        var library = Library.Value;
        for (int i = 0; i < codebookCount; i++) library.Rebuild((int)ss.Read(10), os);

        // time domain transforms: a placeholder
        os.Write(0, 6);
        os.Write(0, 16);

        uint floorCount = ss.Read(6) + 1;
        os.Write(floorCount - 1, 6);
        for (int i = 0; i < floorCount; i++)
        {
            os.Write(1, 16); // always floor type 1
            uint partitions = ss.Copy(os, 5);
            var classes = new uint[partitions];
            uint maximumClass = 0;
            for (int j = 0; j < partitions; j++)
            {
                classes[j] = ss.Copy(os, 4);
                maximumClass = Math.Max(maximumClass, classes[j]);
            }
            var dimensions = new uint[maximumClass + 1];
            for (int j = 0; j <= maximumClass; j++)
            {
                dimensions[j] = ss.Copy(os, 3) + 1;
                uint subclasses = ss.Copy(os, 2);
                if (subclasses != 0 && ss.Copy(os, 8) >= codebookCount) throw new InvalidDataException(L.T("WwiseVorbis.FloorsMasterBookOut"));
                for (int k = 0; k < 1 << (int)subclasses; k++)
                    if ((int)ss.Copy(os, 8) - 1 is var book && book >= 0 && book >= codebookCount) throw new InvalidDataException(L.T("WwiseVorbis.FloorsSubclassBookOut"));
            }
            ss.Copy(os, 2);
            int rangeBits = (int)ss.Copy(os, 4);
            for (int j = 0; j < partitions; j++)
                for (int k = 0; k < dimensions[classes[j]]; k++) ss.Copy(os, rangeBits);
        }

        uint residueCount = ss.Read(6) + 1;
        os.Write(residueCount - 1, 6);
        for (int i = 0; i < residueCount; i++)
        {
            uint type = ss.Read(2);
            if (type > 2) throw new InvalidDataException(L.T("WwiseVorbis.ResiduesTypeOutRange"));
            os.Write(type, 16);
            ss.Copy(os, 24);
            ss.Copy(os, 24);
            ss.Copy(os, 24);
            uint classifications = ss.Copy(os, 6) + 1;
            if (ss.Copy(os, 8) >= codebookCount) throw new InvalidDataException(L.T("WwiseVorbis.ResiduesClassBookOut"));
            var cascade = new uint[classifications];
            for (int j = 0; j < classifications; j++)
            {
                uint low = ss.Copy(os, 3), high = 0;
                if (ss.Copy(os, 1) != 0) high = ss.Copy(os, 5);
                cascade[j] = high * 8 + low;
            }
            for (int j = 0; j < classifications; j++)
                for (int k = 0; k < 8; k++)
                    if ((cascade[j] & (1 << k)) != 0 && ss.Copy(os, 8) >= codebookCount) throw new InvalidDataException(L.T("WwiseVorbis.ResiduesBookOutRange"));
        }

        uint mappingCount = ss.Read(6) + 1;
        os.Write(mappingCount - 1, 6);
        int channelBits = ILog((uint)channels - 1);
        for (int i = 0; i < mappingCount; i++)
        {
            os.Write(0, 16); // always mapping type 0
            uint submaps = 1;
            if (ss.Copy(os, 1) != 0) submaps = ss.Copy(os, 4) + 1;
            if (ss.Copy(os, 1) != 0)
            {
                uint steps = ss.Copy(os, 8) + 1;
                for (int j = 0; j < steps; j++)
                {
                    uint magnitude = ss.Copy(os, channelBits), angle = ss.Copy(os, channelBits);
                    if (angle == magnitude || magnitude >= channels || angle >= channels) throw new InvalidDataException(L.T("WwiseVorbis.MappingsCouplingWrong"));
                }
            }
            if (ss.Copy(os, 2) != 0) throw new InvalidDataException(L.T("WwiseVorbis.MappingsReservedBitsArent"));
            if (submaps > 1)
                for (int j = 0; j < channels; j++)
                    if (ss.Copy(os, 4) >= submaps) throw new InvalidDataException(L.T("WwiseVorbis.MappingsChannelNoSubmap"));
            for (int j = 0; j < submaps; j++)
            {
                ss.Copy(os, 8);
                if (ss.Copy(os, 8) >= floorCount) throw new InvalidDataException(L.T("WwiseVorbis.MappingsFloorOutRange"));
                if (ss.Copy(os, 8) >= residueCount) throw new InvalidDataException(L.T("WwiseVorbis.MappingsResidueOutRange"));
            }
        }

        uint modeCount = ss.Read(6) + 1;
        os.Write(modeCount - 1, 6);
        var blockFlags = new bool[modeCount];
        for (int i = 0; i < modeCount; i++)
        {
            blockFlags[i] = ss.Copy(os, 1) != 0;
            os.Write(0, 16);
            os.Write(0, 16);
            if (ss.Copy(os, 8) >= mappingCount) throw new InvalidDataException(L.T("WwiseVorbis.ModesMappingOutRange"));
        }
        os.Write(1, 1);
        if ((ss.BitsRead + 7) / 8 != size) throw new InvalidDataException(L.T("WwiseVorbis.SetupPacketWasntRead"));
        return (blockFlags, ILog(modeCount - 1));
    }

    /// <summary>
    /// The codebooks Wwise numbers: the library's data, then a table of where each starts, the table's own place in the
    /// file's last four bytes. Each is kept packed and rebuilt as the Vorbis specification has a codebook.
    /// </summary>
    private sealed class CodebookLibrary
    {
        private readonly byte[] _data;
        private readonly int[] _offsets;

        public CodebookLibrary(byte[] file)
        {
            int table = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 4));
            int count = (file.Length - table) / 4;
            _data = file;
            _offsets = new int[count];
            for (int i = 0; i < count; i++) _offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(table + i * 4));
        }

        public void Rebuild(int id, BitWriter os)
        {
            if (id < 0 || id >= _offsets.Length - 1) throw new InvalidDataException(L.T("WwiseVorbis.CodebookIsntLibrary", id));
            int start = _offsets[id], size = _offsets[id + 1] - start;
            var bis = new BitReader(_data, start, start + size);
            uint dimensions = bis.Read(4), entries = bis.Read(14);
            os.Write(0x564342, 24);
            os.Write(dimensions, 16);
            os.Write(entries, 24);
            if (bis.Copy(os, 1) != 0)
            {
                // ordered: an initial length, then how many of each length
                bis.Copy(os, 5);
                for (uint current = 0; current < entries;) current += bis.Copy(os, ILog(entries - current));
            }
            else
            {
                uint lengthBits = bis.Read(3);
                uint sparse = bis.Read(1);
                if (lengthBits is 0 or > 5) throw new InvalidDataException(L.T("WwiseVorbis.CodebooksCodewordLengthsMake"));
                os.Write(sparse, 1);
                for (int i = 0; i < entries; i++)
                    if (sparse == 0 || bis.Copy(os, 1) != 0) os.Write(bis.Read((int)lengthBits), 5);
            }
            uint lookup = bis.Read(1);
            os.Write(lookup, 4);
            if (lookup == 1)
            {
                bis.Copy(os, 32);
                bis.Copy(os, 32);
                uint valueBits = bis.Copy(os, 4) + 1;
                bis.Copy(os, 1);
                uint values = QuantValues(entries, dimensions);
                for (int i = 0; i < values; i++) bis.Copy(os, (int)valueBits);
            }
            if (bis.BitsRead / 8 + 1 != size) throw new InvalidDataException(L.T("WwiseVorbis.CodebookWasntReadExactly", id));
        }

        private static uint QuantValues(uint entries, uint dimensions)
        {
            int bits = ILog(entries);
            long vals = entries >> (int)((bits - 1) * (dimensions - 1) / dimensions);
            while (true)
            {
                long acc = 1, acc1 = 1;
                for (int i = 0; i < dimensions; i++)
                {
                    acc *= vals;
                    acc1 *= vals + 1;
                }
                if (acc <= entries && acc1 > entries) return (uint)vals;
                vals += acc > entries ? -1 : 1;
            }
        }
    }

    /// <summary>Reads bits as Vorbis packs them: from each byte's lowest bit up.</summary>
    private sealed class BitReader(byte[] data, int start, int end)
    {
        private long _bit;

        public long BitsRead => _bit;

        public uint Read(int bits)
        {
            uint value = 0;
            for (int i = 0; i < bits; i++, _bit++)
            {
                long at = start + (_bit >> 3);
                if (at >= end) throw new InvalidDataException(L.T("WwiseVorbis.PacketReadPastEnd"));
                if ((data[at] >> (int)(_bit & 7) & 1) != 0) value |= 1u << i;
            }
            return value;
        }

        /// <summary>Reads bits and writes them as they are.</summary>
        public uint Copy(BitWriter to, int bits)
        {
            uint value = Read(bits);
            to.Write(value, bits);
            return value;
        }
    }
}

/// <summary>Writes bits as Vorbis packs them: into each byte from its lowest bit up.</summary>
public sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bit;

    public void Write(uint value, int bits)
    {
        for (int i = 0; i < bits; i++)
        {
            if (_bit == 0) _bytes.Add(0);
            if ((value >> i & 1) != 0) _bytes[^1] |= (byte)(1 << _bit);
            _bit = (_bit + 1) & 7;
        }
    }

    public byte[] ToArray() => [.. _bytes];
}

/// <summary>An Ogg stream of one logical stream: each packet on pages of its own, with its granule position.</summary>
public sealed class OggWriter
{
    private readonly MemoryStream _stream = new();
    private uint _sequence;
    private const uint Serial = 1;

    public void Packet(byte[] packet, long granule, bool first = false, bool last = false)
    {
        // a page holds 255 segments of up to 255 bytes; a longer packet goes on, continued, over more pages
        int offset = 0;
        bool continued = false;
        do
        {
            var lacing = new List<byte>();
            int take = 0;
            while (lacing.Count < 255 && offset + take < packet.Length)
            {
                int segment = Math.Min(255, packet.Length - offset - take);
                lacing.Add((byte)segment);
                take += segment;
            }
            bool finished = offset + take >= packet.Length;
            // a packet a whole number of 255s long ends with an empty segment
            if (finished && lacing.Count < 255 && (lacing.Count == 0 || lacing[^1] == 255)) lacing.Add(0);
            else if (finished && lacing.Count == 255 && lacing[^1] == 255) finished = false;
            byte flags = (byte)((continued ? 1 : 0) | (first && offset == 0 ? 2 : 0) | (last && finished ? 4 : 0));
            WritePage(flags, finished ? granule : -1, lacing, packet.AsSpan(offset, take));
            offset += take;
            continued = true;
            if (finished) break;
        } while (true);
    }

    private void WritePage(byte flags, long granule, List<byte> lacing, ReadOnlySpan<byte> body)
    {
        var page = new byte[27 + lacing.Count + body.Length];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), Serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _sequence++);
        page[26] = (byte)lacing.Count;
        for (int i = 0; i < lacing.Count; i++) page[27 + i] = lacing[i];
        body.CopyTo(page.AsSpan(27 + lacing.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
        _stream.Write(page);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint r = (uint)i << 24;
        for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
        return r;
    }).ToArray();

    private static uint Crc(byte[] page)
    {
        uint crc = 0;
        foreach (byte b in page) crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ b];
        return crc;
    }

    public byte[] ToArray() => _stream.ToArray();
}
