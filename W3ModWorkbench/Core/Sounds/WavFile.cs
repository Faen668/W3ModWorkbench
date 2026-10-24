using System.Buffers.Binary;
using System.IO;
using Concentus;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Sounds;

/// <summary>
/// A WAV file's sound: its samples as 16-bit, channels interleaved, and its sample rate. Reads PCM of 8, 16, 24 or 32
/// bits and floats of 32 or 64, plainly or in the extensible form most editors save, which covers what Audacity,
/// Reaper, Adobe Audition and recorders write.
/// </summary>
public sealed record WavFile(short[] Samples, int Channels, int SampleRate)
{
    public int Frames => Samples.Length / Math.Max(1, Channels);

    public double Duration => SampleRate > 0 ? (double)Frames / SampleRate : 0;

    public static WavFile Read(byte[] data)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46464952 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != 0x45564157)
            throw new InvalidDataException(L.T("WavFile.NotWav"));
        int fmt = -1, fmtSize = 0, body = -1, bodySize = 0;
        for (int at = 12; at + 8 <= data.Length;)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            // a recorder stopped early can leave the data's size as 0 or past the end: the rest of the file is the data
            if (id == 0x61746164 && (size == 0 || at + 8 + size > data.Length)) size = data.Length - at - 8;
            if (at + 8 + size > data.Length) break;
            if (id == 0x20746D66) { fmt = at + 8; fmtSize = (int)size; } // "fmt "
            else if (id == 0x61746164) { body = at + 8; bodySize = (int)size; } // "data"
            at += 8 + (int)size + (int)(size & 1);
        }
        if (fmt < 0 || fmtSize < 16 || body < 0) throw new InvalidDataException(L.T("WavFile.NoFormatOrData"));
        var f = data.AsSpan(fmt, fmtSize);
        int format = BinaryPrimitives.ReadUInt16LittleEndian(f), channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(f[4..]), bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
        // the extensible form keeps the real format in the first two bytes of its subformat
        if (format == 0xFFFE && fmtSize >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(f[24..]);
        if (channels < 1 || rate < 1000 || rate > 384000) throw new InvalidDataException(L.T("WavFile.BadFormat", channels, rate));
        int bytes = bits / 8;
        if (!(format == 1 && bits is 8 or 16 or 24 or 32) && !(format == 3 && bits is 32 or 64))
            throw new NotSupportedException(L.T("WavFile.FormatNotRead", format, bits));
        int count = bodySize / (bytes * channels) * channels;
        var samples = new short[count];
        var s = data.AsSpan(body, count * bytes);
        for (int i = 0; i < count; i++)
        {
            var b = s.Slice(i * bytes, bytes);
            samples[i] = (format, bits) switch
            {
                (1, 8) => (short)((b[0] - 128) << 8),
                (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(b),
                (1, 24) => (short)(b[1] | b[2] << 8),
                (1, _) => (short)(BinaryPrimitives.ReadInt32LittleEndian(b) >> 16),
                (3, 32) => ToShort(BinaryPrimitives.ReadSingleLittleEndian(b)),
                _ => ToShort((float)BinaryPrimitives.ReadDoubleLittleEndian(b)),
            };
        }
        return new WavFile(samples, channels, rate);
    }

    private static short ToShort(float v) => (short)Math.Clamp(MathF.Round(v * 32767f), -32768f, 32767f);

    /// <summary>The sound at another sample rate (Speex's resampler at its best quality), the same length in time.</summary>
    public WavFile Resampled(int rate)
    {
        if (rate == SampleRate || Frames == 0) return this;
        var resampler = ResamplerFactory.CreateResampler(Channels, SampleRate, rate, 10);
        resampler.SkipZeroes();
        int want = (int)((long)Frames * rate / SampleRate);
        // the input then silence, enough to push the last of the sound through the filter
        int pad = SampleRate / 50;
        var input = new short[(Frames + pad) * Channels];
        Samples.CopyTo(input, 0);
        var output = new short[(want + rate / 50 + 64) * Channels];
        int inFrames = Frames + pad, outFrames = output.Length / Channels;
        resampler.ProcessInterleaved(input, ref inFrames, output, ref outFrames);
        int got = Math.Min(want, outFrames);
        return new WavFile(output.AsSpan(0, got * Channels).ToArray(), Channels, rate);
    }
}
