using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// A Wwise sound made for the original game (Wwise 2013) put in the form the Remastered's engine reads, and how much of
/// a sound a bank of 150 keeps prefetched. Both learnt from the game's own sounds: its 4,264 Vorbis sounds kept in
/// banks have the very encoder settings old mods' have, and differ only in how the channels are said.
/// </summary>
public static class WemUpgrade
{
    /// <summary>
    /// The sound with its channels said as the Remastered's sounds say them. Wwise 2013 kept a speaker mask (stereo
    /// 0x3); later Wwise keeps a channel configuration there: the count, then 1 for a standard layout, then the mask
    /// above bit 12 (stereo 0x3102). The surround layouts' back speakers became side speakers (4.0 0x33 is 0x603, 5.1
    /// 0x3F is 0x60F), as every one of the game's surround sounds has them. A sound already so is returned as it is.
    /// </summary>
    public static byte[] Upgrade(byte[] riff)
    {
        var info = WemInfo.Read(riff);
        var fmt = info.Chunks.FirstOrDefault(c => c.Id == "fmt ") ?? throw new InvalidDataException(L.T("WemUpgrade.NoFormat"));
        // Opus came after Wwise 2013: a sound in it (the game's own, re-encoded) is already as the engine reads it
        if (info.Codec == 0x3041) return riff;
        if (info.Codec is not (0xFFFF or 0xFFFE) || fmt.Size < 24) throw new InvalidDataException(L.T("WemUpgrade.CodecNotLearnt", info.CodecName));
        int at = fmt.Offset + 20;
        uint field = BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(at));
        int channels = info.Channels;
        if (IsConfig(field, channels)) return riff;
        uint mask = field;
        if (BitOperations.PopCount(mask) != channels)
            mask = channels switch
            {
                1 => 0x4,
                2 => 0x3,
                4 => 0x603,
                6 => 0x60F,
                _ => throw new InvalidDataException(L.T("WemUpgrade.ChannelsNotLearnt", channels, $"0x{field:X}")),
            };
        if ((mask & 0x30) != 0 && (mask & 0x600) == 0) mask = (mask & ~0x30u) | 0x600;
        var result = (byte[])riff.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), (uint)channels | 1u << 8 | mask << 12);
        return result;
    }

    /// <summary>Whether the field is already a channel configuration: the count, a standard layout, a mask of that many.</summary>
    private static bool IsConfig(uint field, int channels) =>
        (field & 0xFF) == channels && ((field >> 8) & 0xF) == 1 && BitOperations.PopCount(field >> 12) == channels;

    /// <summary>
    /// The start of a sound a bank of 150 keeps when it's prefetched, and whose length it gives when it's streamed: the
    /// file up to the first audio packet that reaches a tenth of a second, the prefetch Wwise makes unless told
    /// otherwise. So every one of the game's 863 streamed and prefetched Vorbis sounds is cut, and its 617 Opus ones but
    /// two set to a second (an Opus packet is 960 samples, and one more is needed for the encoder's delay). Null for a
    /// codec no sound of the game's showed the cut of.
    /// </summary>
    public static byte[]? Prefetch(byte[] riff, double seconds = 0.1)
    {
        var info = WemInfo.Read(riff);
        var data = info.Chunks.FirstOrDefault(c => c.Id == "data");
        if (data is null || info.SampleRate <= 0) return null;
        long samples = (long)Math.Round(info.SampleRate * seconds);
        int end;
        switch (info.Codec)
        {
            case 0xFFFF:
                var packets = WwiseVorbis.AudioPackets(riff, samples);
                int k = packets.FindIndex(p => p.Samples >= samples);
                if (k < 0) return riff;
                end = data.Offset + packets[k].End;
                break;
            case 0x3041:
                var fmt = info.Chunks.First(c => c.Id == "fmt ");
                var seek = info.Chunks.FirstOrDefault(c => c.Id == "seek");
                int frame = BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(fmt.Offset + 18));
                if (seek is null || frame <= 0) return null;
                long count = samples / frame + 1;
                end = data.Offset;
                for (int i = 0; i < count && i * 2 + 2 <= seek.Size; i++) end += BinaryPrimitives.ReadUInt16LittleEndian(riff.AsSpan(seek.Offset + i * 2));
                break;
            default:
                return null;
        }
        return end >= riff.Length ? riff : riff[..end];
    }
}
