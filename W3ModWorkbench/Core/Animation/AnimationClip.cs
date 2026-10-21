using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Animation;

/// <summary>Where a bone is relative to its parent: moved, turned and scaled.</summary>
public readonly record struct BoneTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static BoneTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

/// <summary>
/// The bone data of a skeletal animation (an animation buffer): for each bone, keys of its position, orientation and
/// scale, and a few float tracks (a camera's field of view). Read from a <c>CAnimationBufferBitwiseCompressed</c>, or a
/// <c>CAnimationBufferMultipart</c>, which is several of those one after another for a long animation.
/// </summary>
public sealed class AnimationClip
{
    private readonly List<(int FirstFrame, Part Part)> _parts;
    private readonly float _framesPerSecond;

    public float Duration { get; }
    public int BoneCount { get; }
    public int TrackCount { get; }

    private AnimationClip(List<(int, Part)> parts, float duration)
    {
        _parts = parts;
        BoneCount = parts.Max(p => p.Item2.Bones.Length);
        TrackCount = parts.Max(p => p.Item2.Tracks.Length);
        int lastFrame = parts[^1].Item1 + parts[^1].Item2.FrameCount - 1;
        Duration = duration > 0 ? duration : lastFrame * parts[0].Item2.FrameTime;
        _framesPerSecond = Duration > 0 ? lastFrame / Duration : 30;
    }

    /// <summary>
    /// Reads a skeletal animation's (a CSkeletalAnimation's) bone data. <paramref name="external"/> reads a numbered
    /// <c>.buffer</c> file, for a buffer that streams its data from one. Null when it has a kind of buffer that isn't read.
    /// </summary>
    public static AnimationClip? Read(Cr2wFile file, RedObject animation, Func<int, byte[]?> external)
    {
        if (animation["animBuffer"] is not RedHandle handle || file.Object(handle) is not { } buffer) return null;
        float duration = animation.Float("duration");
        switch (buffer.ClassName)
        {
            case "CAnimationBufferBitwiseCompressed":
                return new AnimationClip([(0, Part.Read(file, buffer, external))], duration);
            case "CAnimationBufferMultipart":
                // a long animation in parts, each starting at a frame of the whole on the last frame of the one before
                var handles = buffer.Array("parts").OfType<RedHandle>().ToList();
                var firsts = buffer.Array("firstFrames").Select(Convert.ToInt32).ToList();
                if (handles.Count == 0 || firsts.Count != handles.Count) return null;
                var parts = new List<(int, Part)>();
                for (int i = 0; i < handles.Count; i++)
                {
                    if (file.Object(handles[i]) is not { ClassName: "CAnimationBufferBitwiseCompressed" } part) return null;
                    parts.Add((firsts[i], Part.Read(file, part, external)));
                }
                return new AnimationClip(parts, duration);
            default:
                return null;
        }
    }

    /// <summary>
    /// The bones' transforms at a time: <paramref name="pose"/> is filled for the bones the animation moves, and the
    /// rest left as they are (the rig's pose).
    /// </summary>
    public void Sample(float time, Span<BoneTransform> pose)
    {
        var (part, t) = PartAt(time);
        for (int i = 0; i < part.Bones.Length && i < pose.Length; i++)
        {
            var bone = part.Bones[i];
            pose[i] = new BoneTransform(bone.Position.Sample(t, Vector3.Lerp), bone.Orientation.Sample(t, Quaternion.Slerp), bone.Scale.Sample(t, Vector3.Lerp));
        }
    }

    /// <summary>A float track's value at a time, or <paramref name="otherwise"/> when there's no such track.</summary>
    public float Track(int index, float time, float otherwise = 0)
    {
        var (part, t) = PartAt(time);
        return index < part.Tracks.Length ? part.Tracks[index].Sample(t, float.Lerp) : otherwise;
    }

    /// <summary>
    /// The part playing at a time, and the time into it. The parts' keys are a nominal frame apart, and the whole is
    /// played at the speed that makes its frames last as long as the animation, so times go through frames.
    /// </summary>
    private (Part Part, float Time) PartAt(float time)
    {
        float frame = Math.Max(0, time) * _framesPerSecond;
        int i = _parts.Count - 1;
        while (i > 0 && frame < _parts[i].FirstFrame) i--;
        var (first, part) = _parts[i];
        return (part, (frame - first) * part.FrameTime);
    }

    // ---------------------------------------------------------------- decoding

    /// <summary>Keys of one thing, a frame apart: one key means it doesn't change.</summary>
    private sealed record Keys<T>(float FrameTime, T[] Values) where T : struct
    {
        public T Sample(float time, Func<T, T, float, T> blend)
        {
            if (Values.Length == 1 || FrameTime <= 0) return Values[0];
            float frame = Math.Clamp(time / FrameTime, 0, Values.Length - 1);
            int a = (int)frame;
            return a + 1 >= Values.Length ? Values[^1] : blend(Values[a], Values[a + 1], frame - a);
        }
    }

    private sealed record BoneKeys(Keys<Vector3> Position, Keys<Quaternion> Orientation, Keys<Vector3> Scale);

    /// <summary>
    /// One bitwise-compressed buffer. Its data is one block of bytes; each bone's position, orientation and scale (and
    /// each float track) says where its keys start in it, how many there are and how far apart. Vectors and floats are
    /// saved as 32-bit floats, or with only their top 24 or 16 bits; orientations as four or three numbers packed in 64,
    /// 48, 40 or 32 bits, as the buffer's <c>orientationCompressionMethod</c> says.
    /// </summary>
    private sealed class Part
    {
        public required BoneKeys[] Bones { get; init; }
        public required Keys<float>[] Tracks { get; init; }
        public required int FrameCount { get; init; }
        public required float FrameTime { get; init; }

        public static Part Read(Cr2wFile file, RedObject buffer, Func<int, byte[]?> external)
        {
            byte[] data = Data(file, buffer, external);
            string method = EnumName(file, buffer["orientationCompressionMethod"]) ?? "ABOCM_PackIn64bitsW";
            float frameTime = buffer.Float("dt", 1 / 30f);

            var bones = new List<BoneKeys>();
            foreach (var bone in buffer.Array("bones").OfType<RedObject>())
                bones.Add(new BoneKeys(
                    Read(bone.Struct("position"), data, frameTime, Vector3.Zero, (d, at, c) => Vector(d, at, c), Bytes3),
                    Read(bone.Struct("orientation"), data, frameTime, Quaternion.Identity, (d, at, _) => Orientation(d, at, method), _ => OrientationBytes(method)),
                    Read(bone.Struct("scale"), data, frameTime, Vector3.One, (d, at, c) => Vector(d, at, c), Bytes3)));
            var tracks = buffer.Array("tracks").OfType<RedObject>()
                .Select(t => Read(t, data, frameTime, 0f, (d, at, c) => Float(d, at, c), c => Bytes3(c) / 3)).ToArray();
            return new Part
            {
                Bones = [.. bones],
                Tracks = tracks,
                FrameCount = (int)Math.Max(1, buffer.Integer("numFrames", 1)),
                FrameTime = frameTime,
            };
        }

        /// <summary>
        /// The buffer's bytes: its data, or the deferred buffer it's streamed from, or both one after the other when
        /// only part of it is streamed.
        /// </summary>
        private static byte[] Data(Cr2wFile file, RedObject buffer, Func<int, byte[]?> external)
        {
            byte[] inline = buffer.Bytes("data");
            if (buffer["deferredData"] is not RedBufferRef { Index: > 0 } deferred) return inline;
            byte[] streamed = file.Buffer(deferred.Index, external) ?? throw new InvalidDataException(L.T("AnimationClip.AnimationsStreamedDataWasnt"));
            string option = EnumName(file, buffer["streamingOption"]) ?? "";
            return option == "ABSO_PartiallyStreamable" ? [.. inline, .. streamed] : streamed;
        }

        private static Keys<T> Read<T>(RedObject? info, byte[] data, float frameTime, T otherwise, Func<byte[], int, int, T> read, Func<int, int> size) where T : struct
        {
            if (info is null) return new Keys<T>(0, [otherwise]);
            int count = (int)info.Integer("numFrames"), at = (int)info.Integer("dataAddr"), compression = (int)info.Integer("compression");
            int step = size(compression);
            if (count <= 0) return new Keys<T>(0, [otherwise]);
            if (at < 0 || at + (long)count * step > data.Length) throw new InvalidDataException(L.T("AnimationClip.AnimationsKeysRunPast"));
            var values = new T[count];
            for (int i = 0; i < count; i++) values[i] = read(data, at + i * step, compression);
            return new Keys<T>(info.Float("dt", frameTime), values);
        }

        /// <summary>
        /// An enum's value. The buffer's enums are named S… like structs, so they're read as their two bytes: a name.
        /// </summary>
        private static string? EnumName(Cr2wFile file, object? value) => value switch
        {
            RedEnum e => e.ToString(),
            byte[] { Length: 2 } b when (b[0] | b[1] << 8) < file.Names.Count => file.Names[b[0] | b[1] << 8],
            _ => null,
        };

        private static int Bytes3(int compression) => compression switch { 0 => 12, 1 => 9, 2 => 6, _ => throw Unknown(compression) };

        private static Exception Unknown(int compression) => new InvalidDataException(L.T("AnimationClip.AnimationUsesCompressionDoesnt", compression));

        /// <summary>A float as saved: whole, or its top 24 or 16 bits with the rest zero.</summary>
        private static float Float(byte[] d, int at, int compression) => compression switch
        {
            0 => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at)),
            1 => BitConverter.Int32BitsToSingle(d[at] << 8 | d[at + 1] << 16 | d[at + 2] << 24),
            2 => BitConverter.Int32BitsToSingle(d[at] << 16 | d[at + 1] << 24),
            _ => throw Unknown(compression),
        };

        private static Vector3 Vector(byte[] d, int at, int compression)
        {
            int size = Bytes3(compression) / 3;
            return new Vector3(Float(d, at, compression), Float(d, at + size, compression), Float(d, at + size * 2, compression));
        }

        private static int OrientationBytes(string method) => method switch
        {
            "ABOCM_PackIn64bitsW" => 8,
            "ABOCM_PackIn48bitsW" or "ABOCM_PackIn48bits" => 6,
            "ABOCM_PackIn40bitsW" or "ABOCM_PackIn40bits" => 5,
            "ABOCM_PackIn32bits" => 4,
            "ABOCM_AsFloat_XYZW" => 16,
            "ABOCM_AsFloat_XYZSignedWInLastBit" => 12,
            _ => throw new InvalidDataException(L.T("AnimationClip.AnimationPacksOrientationsWay", method)),
        };

        /// <summary>
        /// An orientation as saved. The packed kinds store each number n in [-1, 1] as (1 - n) scaled to the bits there
        /// are, so all zeros is 1 and all ones -1.
        /// </summary>
        private static Quaternion Orientation(byte[] d, int at, string method)
        {
            switch (method)
            {
                case "ABOCM_PackIn64bitsW":
                    float Short(int i) => (32767f - BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + i * 2))) / 32768f;
                    return Unit(new Quaternion(Short(0), Short(1), Short(2), Short(3)));
                case "ABOCM_PackIn48bitsW":
                    // four 12-bit numbers, the first in the top bits of the six bytes read as big-endian
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++) bits = bits << 8 | d[at + i];
                    float Twelve(int shift) => (2047f - (int)(bits >> shift & 0xFFF)) / 2048f;
                    return Unit(new Quaternion(Twelve(36), Twelve(24), Twelve(12), Twelve(0)));
                case "ABOCM_AsFloat_XYZW":
                    var span = d.AsSpan(at);
                    return Unit(new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(span), BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
                        BinaryPrimitives.ReadSingleLittleEndian(span[8..]), BinaryPrimitives.ReadSingleLittleEndian(span[12..])));
                case "ABOCM_AsFloat_XYZSignedWInLastBit":
                {
                    // X, Y and Z as floats, W made from them, the last bit of Z's saying it's negative
                    var xyz = d.AsSpan(at);
                    uint zBits = BinaryPrimitives.ReadUInt32LittleEndian(xyz[8..]);
                    float x = BinaryPrimitives.ReadSingleLittleEndian(xyz), y = BinaryPrimitives.ReadSingleLittleEndian(xyz[4..]);
                    float z = BitConverter.UInt32BitsToSingle(zBits & ~1u);
                    float w = MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y - z * z));
                    return Unit(new Quaternion(x, y, z, (zBits & 1) != 0 ? -w : w));
                }
                default:
                    throw new InvalidDataException(L.T("AnimationClip.AnimationPacksOrientationsWay", method));
            }
        }

        /// <summary>Packed orientations are a little off unit length; blending and turning bones want them exact.</summary>
        private static Quaternion Unit(Quaternion q) => q.LengthSquared() > 1e-8f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }
}
