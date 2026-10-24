using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Concentus;
using Concentus.Enums;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// Sounds made into Wwise Opus sounds (format 0x3041) as the Remastered's voice lines are, worked out from the game's
/// own 64,718: a RIFF of "fmt ", "hash", "seek" and "data". The "fmt " is 36 bytes: the format, channels, 48000, the
/// bytes a second (the data's size times 48000 over the samples, in 32-bit floats, rounded down), two zeros, 18 more
/// bytes to come, 960 samples a frame, Wwise's channel configuration (the count, 1 for a standard layout, the speaker
/// mask from bit 12), the samples, the packets, the encoder's look-ahead (312), 1, and Opus's channel mapping family
/// (1 for more than two channels). "hash" is 16 bytes Wwise keeps for the source recording; "seek" each packet's size
/// in two bytes; "data" the packets, each one 20 ms CELT frame at full bandwidth, encoded with variable bitrate at
/// about 64 kbit/s a channel, enough of them to cover the samples and the look-ahead. Checked by writing every one of
/// the game's sounds without markers (64,509) again from its own packets, byte for byte.
/// <para>
/// The encoding is Opus's own (libopus's encoder, as Concentus has it, BSD licensed, which the app already uses to
/// decode); the file around it is ours.
/// </para>
/// </summary>
public static class WemOpus
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960;

    /// <summary>The bitrate a channel the game's voice lines are near (mono 65 kbit/s, stereo 120, 5.1 350 at the median).</summary>
    public const int DefaultKbpsPerChannel = 64;

    /// <summary>Wwise's channel configurations of the layouts the game's voice lines have: mono (centre), stereo and 5.1 (with side speakers).</summary>
    private static uint ChannelConfig(int channels) => channels switch
    {
        1 => 0x4101,
        2 => 0x3102,
        6 => 0x60F106,
        _ => throw new NotSupportedException(L.T("WemOpus.ChannelsNotMade", channels)),
    };

    /// <summary>The Wwise Opus sound for <paramref name="packets"/>, as the game's own are laid out.</summary>
    public static byte[] Write(IReadOnlyList<byte[]> packets, int channels, int samples, int preSkip, ReadOnlySpan<byte> hash)
    {
        if (hash.Length != 16) throw new ArgumentException(L.T("WemOpus.HashSize"), nameof(hash));
        int dataSize = packets.Sum(p => p.Length);
        int seekSize = packets.Count * 2;
        int size = 12 + (8 + 36) + (8 + 16) + (8 + seekSize) + (8 + dataSize);
        var riff = new byte[size];
        var w = riff.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(w, 0x46464952); // RIFF
        BinaryPrimitives.WriteInt32LittleEndian(w[4..], size - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(w[8..], 0x45564157); // WAVE
        int at = 12;
        BinaryPrimitives.WriteUInt32LittleEndian(w[at..], 0x20746D66); // fmt
        BinaryPrimitives.WriteInt32LittleEndian(w[(at + 4)..], 36);
        var f = w.Slice(at + 8, 36);
        BinaryPrimitives.WriteUInt16LittleEndian(f, 0x3041);
        BinaryPrimitives.WriteUInt16LittleEndian(f[2..], (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(f[4..], SampleRate);
        // bytes a second worked out in 32-bit floats, as all 64,509 of the game's say
        BinaryPrimitives.WriteInt32LittleEndian(f[8..], samples > 0 ? (int)((float)dataSize * SampleRate / samples) : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(f[0x10..], 0x12);
        BinaryPrimitives.WriteUInt16LittleEndian(f[0x12..], FrameSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(f[0x14..], ChannelConfig(channels));
        BinaryPrimitives.WriteInt32LittleEndian(f[0x18..], samples);
        BinaryPrimitives.WriteInt32LittleEndian(f[0x1C..], packets.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(f[0x20..], (ushort)preSkip);
        f[0x22] = 1;
        f[0x23] = (byte)(channels > 2 ? 1 : 0);
        at += 8 + 36;
        BinaryPrimitives.WriteUInt32LittleEndian(w[at..], 0x68736168); // hash
        BinaryPrimitives.WriteInt32LittleEndian(w[(at + 4)..], 16);
        hash.CopyTo(w[(at + 8)..]);
        at += 8 + 16;
        BinaryPrimitives.WriteUInt32LittleEndian(w[at..], 0x6B656573); // seek
        BinaryPrimitives.WriteInt32LittleEndian(w[(at + 4)..], seekSize);
        for (int i = 0; i < packets.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(w[(at + 8 + i * 2)..], checked((ushort)packets[i].Length));
        at += 8 + seekSize;
        BinaryPrimitives.WriteUInt32LittleEndian(w[at..], 0x61746164); // data
        BinaryPrimitives.WriteInt32LittleEndian(w[(at + 4)..], dataSize);
        at += 8;
        foreach (var p in packets)
        {
            p.CopyTo(w[at..]);
            at += p.Length;
        }
        return riff;
    }

    /// <summary>
    /// Samples at 48 kHz (channels interleaved, in WAV order: FL FR C LFE BL BR for 5.1) encoded as the game's voice
    /// lines are. <paramref name="hash"/> stands for the source in the "hash" part; null takes the samples' MD5.
    /// </summary>
    public static byte[] Encode(short[] samples, int channels, int kbpsPerChannel = DefaultKbpsPerChannel, byte[]? hash = null, CancellationToken cancel = default)
    {
        ChannelConfig(channels);
        int total = samples.Length / channels;
        int bitrate = Math.Clamp(kbpsPerChannel, 6, 256) * 1000 * channels;
        Func<short[], int, byte[], int> encode;
        int lookahead;
        if (channels <= 2)
        {
            var encoder = OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = bitrate;
            encoder.UseVBR = true;
            encoder.UseConstrainedVBR = false;
            encoder.ForceMode = OpusMode.MODE_CELT_ONLY;
            encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
            encoder.Complexity = 10;
            lookahead = encoder.Lookahead;
            encode = (pcm, at, packet) => encoder.Encode(pcm.AsSpan(at, FrameSamples * channels), FrameSamples, packet, packet.Length);
        }
        else
        {
            // 5.1 as Opus's channel mapping family 1 has it: the Vorbis order (FL C FR BL BR LFE), two stereo streams and two mono
            byte[] mapping = [0, 4, 1, 2, 3, 5];
            var encoder = OpusCodecFactory.CreateMultiStreamEncoder(SampleRate, channels, 1, out int streams, out int coupled, mapping, OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = bitrate;
            encoder.UseVBR = true;
            encoder.UseConstrainedVBR = false;
            encoder.ForceMode = OpusMode.MODE_CELT_ONLY;
            encoder.Bandwidth = OpusBandwidth.OPUS_BANDWIDTH_FULLBAND;
            encoder.Complexity = 10;
            lookahead = encoder.Lookahead;
            samples = ToVorbisOrder(samples, channels);
            encode = (pcm, at, packet) => encoder.EncodeMultistream(pcm.AsSpan(at, FrameSamples * channels), FrameSamples, packet, packet.Length);
        }
        // the samples then silence, enough frames to cover them past the encoder's look-ahead
        int frames = (total + lookahead + FrameSamples - 1) / FrameSamples;
        var input = new short[frames * FrameSamples * channels];
        samples.AsSpan(0, total * channels).CopyTo(input);
        var packets = new List<byte[]>(frames);
        var buffer = new byte[1275 * 3 * channels];
        for (int i = 0; i < frames; i++)
        {
            cancel.ThrowIfCancellationRequested();
            int length = encode(input, i * FrameSamples * channels, buffer);
            packets.Add(buffer.AsSpan(0, length).ToArray());
        }
        hash ??= MD5.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
        return Write(packets, channels, total, lookahead, hash);
    }

    /// <summary>
    /// A WAV file as a voice line's sound: read, made 48 kHz, and encoded as the game's are, with the file's MD5 as the
    /// source's hash. Mono, stereo and 5.1, the layouts the game's voice lines have.
    /// </summary>
    public static byte[] FromWav(byte[] wav, int kbpsPerChannel = DefaultKbpsPerChannel, CancellationToken cancel = default)
    {
        var sound = WavFile.Read(wav);
        ChannelConfig(sound.Channels);
        sound = sound.Resampled(SampleRate);
        return Encode(sound.Samples, sound.Channels, kbpsPerChannel, MD5.HashData(wav), cancel);
    }

    /// <summary>5.1 from WAV order (FL FR C LFE BL BR) to the Vorbis order Opus's surround mapping keeps (FL C FR BL BR LFE).</summary>
    private static short[] ToVorbisOrder(short[] samples, int channels)
    {
        int[] from = [0, 2, 1, 4, 5, 3];
        var result = new short[samples.Length];
        for (int at = 0; at + channels <= samples.Length; at += channels)
            for (int c = 0; c < channels; c++) result[at + c] = samples[at + from[c]];
        return result;
    }
}
