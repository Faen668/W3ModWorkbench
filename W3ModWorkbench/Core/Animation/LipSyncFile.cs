using System.Buffers.Binary;
using System.IO;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// A voice line's lip sync as speech files keep it since the next-gen update (version 163; the Remastered's 164 is
/// the same), the "raw" form: the face's 148 controls over time, a value a frame for each, as the game decodes and writes them. Read and written whole: the frames a second
/// and length, the neck and head bones (always still), a record for each control, the keys, the frame count and the
/// time between frames. Written as the game's own are: a still control as one frame pointing at zeros (or its own two
/// bytes), a moving one as a 32-bit float a frame, each block stored once however many controls share it. Checked by
/// writing back every one of the game's 64,709 lip syncs byte for byte.
/// </summary>
public sealed class LipSyncFile
{
    /// <summary>How many controls a lip sync drives: a face's, the last three (head1-3) the head's.</summary>
    public const int ControlCount = 148;

    /// <summary>How far a control may wander and still count as still (the game's trackSkipFrameTolerance).</summary>
    public const float StillTolerance = 0.0005f;

    /// <summary>The line's length in seconds.</summary>
    public required float Duration { get; init; }

    /// <summary>How many frames there are (a still control keeps one).</summary>
    public required int FrameCount { get; init; }

    /// <summary>Each control's value at each of its frames, or one value when it doesn't move.</summary>
    public required float[][] Controls { get; init; }

    /// <summary>
    /// The time between each control's frames, when it isn't the line's: the game keeps only every second to fifth
    /// frame of a control that changes slowly. Null for every control a frame apart.
    /// </summary>
    public float[]? ControlFrameTimes { get; init; }

    /// <summary>Frames a second, as the game works it out: the frames less one over the length, in 32 bits.</summary>
    public float FramesPerSecond => FrameCount > 1 && Duration > 0 ? (float)((FrameCount - 1) / (double)Duration) : 30;

    /// <summary>The time between frames: one over the frames a second, in 32 bits, as the game's records have it.</summary>
    public float FrameTime => (float)(1.0 / FramesPerSecond);

    /// <summary>A control's value at a frame of the line, between its own keys when it keeps fewer.</summary>
    public float Value(int control, int frame)
    {
        var keys = Controls[control];
        if (keys.Length == 1) return keys[0];
        float step = (ControlFrameTimes?[control] ?? FrameTime) / FrameTime;
        float at = Math.Clamp(frame / step, 0, keys.Length - 1);
        int a = (int)at;
        return a + 1 >= keys.Length ? keys[^1] : keys[a] + (keys[a + 1] - keys[a]) * (at - a);
    }

    // the data's first 20 bytes in every one of the game's lip syncs: the bones' position (three zeros, 16 bits
    // each), orientation (0, 0, 0, 1 packed in 64 bits) and a scale of 1 (16 bits each), which nothing points at
    private static readonly byte[] BonePrefix = Convert.FromHexString("000000000000008000800080FFFF803F803F803F");

    public static LipSyncFile Read(byte[] raw)
    {
        int at = 0;
        float Float() { float v = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at)); at += 4; return v; }
        int Packed() => PackedInt.Read(raw, ref at);
        Float(); // frames a second, worked out again on writing
        float duration = Float();
        int bones = Packed();
        at += bones * 2 * 16;
        int count = Packed();
        if (count < 0 || count > 1024) throw new InvalidDataException(L.T("MimicFace.LipSyncsTracksMake"));
        var records = new (int Compression, int Frames, int Address)[count];
        var frameTimes = new float[count];
        for (int t = 0; t < count; t++)
        {
            frameTimes[t] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at));
            records[t] = (raw[at + 4], BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 6)), BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 8)));
            at += 16;
        }
        int length = Packed(), data = at;
        if (data + length > raw.Length) throw new InvalidDataException(L.T("MimicFace.LipSyncsKeysRun"));
        at += length;
        int frameCount = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at));
        var controls = new float[count][];
        for (int t = 0; t < count; t++)
        {
            var (compression, frames, address) = records[t];
            int size = compression switch { 0 => 4, 1 => 3, 2 => 2, _ => throw new InvalidDataException(L.T("MimicFace.LipSyncUsesCompression", compression)) };
            if (address < 0 || address + (long)frames * size > length) throw new InvalidDataException(L.T("MimicFace.LipSyncsTrackRuns"));
            var keys = new float[Math.Max(1, frames)];
            for (int k = 0; k < frames; k++)
            {
                int p = data + address + k * size;
                keys[k] = compression switch
                {
                    0 => BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(p)),
                    1 => BitConverter.Int32BitsToSingle(raw[p] << 8 | raw[p + 1] << 16 | raw[p + 2] << 24),
                    _ => BitConverter.Int32BitsToSingle(raw[p] << 16 | raw[p + 1] << 24),
                };
            }
            controls[t] = keys;
        }
        float lineTime = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at + 4));
        return new LipSyncFile
        {
            Duration = duration, FrameCount = frameCount, Controls = controls,
            ControlFrameTimes = frameTimes.Any(f => f != lineTime) ? frameTimes : null,
        };
    }

    /// <summary>Whether a speech file's lip sync is a CR2W file (as older speech files keep them) rather than the raw form.</summary>
    public static bool IsCr2w(byte[] data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x57325243; // "CR2W"

    /// <summary>A lip sync as either form a speech file keeps: raw, or a CR2W file of older ones.</summary>
    public static LipSyncFile ReadAny(byte[] data) => IsCr2w(data) ? ReadCr2w(data) : Read(data);

    /// <summary>
    /// A lip sync kept as a CR2W file (a <c>CSkeletalAnimation</c> whose buffer is bitwise compressed, as older speech
    /// files have them): its controls are the buffer's float tracks, each with its own keys and time between them.
    /// </summary>
    public static LipSyncFile ReadCr2w(byte[] data)
    {
        var file = Cr2w.Cr2wFile.Read(data);
        var buffer = file.First("CAnimationBufferBitwiseCompressed") ?? throw new InvalidDataException(L.T("LipSyncFile.NoBuffer"));
        byte[] keys = buffer.Bytes("data");
        if (buffer["deferredData"] is Cr2w.RedBufferRef { Index: > 0 } deferred)
            keys = file.Buffer(deferred.Index, _ => null) ?? throw new InvalidDataException(L.T("LipSyncFile.NoBuffer"));
        var tracks = buffer.Array("tracks").OfType<Cr2w.RedObject>().ToList();
        float lineTime = buffer.Float("dt", 1 / 30f);
        var controls = new float[tracks.Count][];
        var times = new float[tracks.Count];
        for (int t = 0; t < tracks.Count; t++)
        {
            var track = tracks[t];
            int frames = (int)Math.Max(1, track.Integer("numFrames", 1)), at = (int)track.Integer("dataAddr"), compression = (int)track.Integer("compression");
            int size = compression switch { 0 => 4, 1 => 3, 2 => 2, _ => throw new InvalidDataException(L.T("MimicFace.LipSyncUsesCompression", compression)) };
            if (at < 0 || at + (long)frames * size > keys.Length) throw new InvalidDataException(L.T("MimicFace.LipSyncsTrackRuns"));
            var values = new float[frames];
            for (int k = 0; k < frames; k++)
            {
                int p = at + k * size;
                values[k] = compression switch
                {
                    0 => BinaryPrimitives.ReadSingleLittleEndian(keys.AsSpan(p)),
                    1 => BitConverter.Int32BitsToSingle(keys[p] << 8 | keys[p + 1] << 16 | keys[p + 2] << 24),
                    _ => BitConverter.Int32BitsToSingle(keys[p] << 16 | keys[p + 1] << 24),
                };
            }
            controls[t] = values;
            times[t] = track.Float("dt", lineTime);
        }
        return new LipSyncFile
        {
            Duration = buffer.Float("duration"),
            FrameCount = (int)Math.Max(1, buffer.Integer("numFrames", 1)),
            Controls = controls,
            ControlFrameTimes = times.Any(f => f != lineTime) ? times : null,
        };
    }

    /// <summary>
    /// The same lip sync with each control that stays within <see cref="StillTolerance"/> of its first value kept as
    /// that one value, as the game's own lip syncs keep a control that doesn't move.
    /// </summary>
    public LipSyncFile WithStillControls() => new()
    {
        Duration = Duration,
        FrameCount = FrameCount,
        ControlFrameTimes = ControlFrameTimes,
        Controls = Controls.Select(k => k.Length > 1 && k.All(v => Math.Abs(v - k[0]) <= StillTolerance) ? [k[0]] : k).ToArray(),
    };

    /// <summary>The lip sync in the raw form, laid out as the game's own: a control with one value is kept still.</summary>
    public byte[] Write()
    {
        float dt = FrameTime;
        var data = new List<byte>(BonePrefix);
        var blocks = new Dictionary<string, int>();
        var records = new (int Compression, int Frames, int Address)[Controls.Length];
        for (int t = 0; t < Controls.Length; t++)
        {
            var keys = Controls[t];
            if (keys.Length <= 1)
            {
                // still: one value, in the fewest bytes that keep it exactly (its top 16, 24 or all 32 bits), pointing at
                // those bytes wherever they're already in the data; zero points at the bones' zeros
                float value = keys.Length == 0 ? 0 : keys[0];
                uint bits = (uint)BitConverter.SingleToInt32Bits(value);
                if ((bits & 0x7FFFFFFF) == 0) { records[t] = (2, 1, 0); continue; }
                byte[] kept = (bits & 0xFFFF) == 0 ? [(byte)(bits >> 16), (byte)(bits >> 24)]
                    : (bits & 0xFF) == 0 ? [(byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24)]
                    : BitConverter.GetBytes(bits);
                int compression = kept.Length switch { 2 => 2, 3 => 1, _ => 0 };
                int found = data.ToArray().AsSpan().IndexOf(kept);
                records[t] = (compression, 1, found >= 0 ? found : Block(kept));
                continue;
            }
            // moving: a key a frame, in 24 bits when that keeps every key exactly (and 16 wouldn't), else 32, as the
            // game's 64,709 lip syncs have it (none keeps a moving control in 16)
            var allBits = keys.Select(k => (uint)BitConverter.SingleToInt32Bits(k)).ToArray();
            int size = allBits.All(b => (b & 0xFF) == 0) && !allBits.All(b => (b & 0xFFFF) == 0) ? 3 : 4;
            var bytes = new byte[keys.Length * size];
            for (int k = 0; k < keys.Length; k++)
                for (int b = 0; b < size; b++) bytes[k * size + b] = (byte)(allBits[k] >> (8 * (4 - size + b)));
            records[t] = (size switch { 2 => 2, 3 => 1, _ => 0 }, keys.Length, Block(bytes));
        }

        // a block the same as one already stored is pointed at, not stored again
        int Block(byte[] bytes)
        {
            string key = Convert.ToHexString(bytes);
            if (blocks.TryGetValue(key, out int at)) return at;
            at = data.Count;
            blocks[key] = at;
            data.AddRange(bytes);
            return at;
        }

        var o = new List<byte>();
        void F(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); o.AddRange(b); }
        void I(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); o.AddRange(b); }
        void Packed(int v) => PackedInt.Write(o, v);
        void Record(int compression, int frames, int address, float time)
        {
            F(time);
            o.Add((byte)compression);
            o.Add(0);
            o.Add((byte)frames);
            o.Add((byte)(frames >> 8));
            I(address);
            I(address);
        }
        F(FramesPerSecond);
        F(Duration);
        // the neck and head: still, at the bones' zero position and plain orientation
        Packed(2);
        for (int b = 0; b < 2; b++)
        {
            Record(2, 1, 0, dt);
            Record(0, 1, 6, dt);
        }
        Packed(records.Length);
        for (int t = 0; t < records.Length; t++) Record(records[t].Compression, records[t].Frames, records[t].Address, ControlFrameTimes?[t] ?? dt);
        Packed(data.Count);
        o.AddRange(data);
        I(FrameCount);
        F(dt);
        return [.. o];
    }
}
