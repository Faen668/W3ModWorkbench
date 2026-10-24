using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// A character's face as the game moves it (a <c>.w3fac</c>'s CMimicFace): the face's own skeleton (from the upper
/// back through the neck and head to every bone of the brows, eyelids, lips, jaw and tongue), the face's controls by
/// name (jaw_open_a, corner_left_up, eyelids_upper_right_down…), and for each control a pose, how far it moves each of
/// the face bones it works on when it's fully on. A face animation or a line's lip sync says how far each control is
/// on at a time; the face is its skeleton's pose with each control's pose added in that far.
/// </summary>
public sealed class MimicFace
{
    /// <summary>The face's skeleton in its own pose.</summary>
    public required Skeleton Skeleton { get; init; }

    /// <summary>The controls, in the order face animations and lip sync keep their tracks.</summary>
    public required string[] Controls { get; init; }

    /// <summary>For each control with a pose, how far it moves each bone in <see cref="Bones"/> (translation added, rotation turned by).</summary>
    public required BoneTransform[][] Poses { get; init; }

    /// <summary>The face skeleton's bones the poses work on, in the order each pose lists them.</summary>
    public required int[] Bones { get; init; }

    public static MimicFace Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var face = file.First("CMimicFace") ?? throw new InvalidDataException(L.T("MimicFace.TheresNoFaceHas"));
        var skeleton = face["mimicSkeleton"] is RedHandle s && file.Object(s) is { } bones ? Skeleton.FromObject(bones, file)
            : throw new InvalidDataException(L.T("MimicFace.FaceHasNoSkeleton"));
        var controls = face["floatTrackSkeleton"] is RedHandle t && file.Object(t) is { } tracks
            ? tracks.Array("tracks").OfType<RedObject>().Select(c => c["nameAsCName"] as string ?? c["name"] as string ?? "").ToArray()
            : [];
        var mapping = face.Array("mapping").Select(Convert.ToInt32).ToArray();
        var poses = face["mimicPoses"] is byte[] raw ? ReadPoses(raw) : [];
        return new MimicFace { Skeleton = skeleton, Controls = controls, Poses = poses, Bones = mapping };
    }

    /// <summary>
    /// The poses as saved: a count, then each pose as a count and a transform for each of its bones, each transform a
    /// byte saying what it has (1 a translation, 2 a rotation, 4 a scale) and three, four and three floats for them.
    /// </summary>
    private static BoneTransform[][] ReadPoses(byte[] raw)
    {
        int at = 0;
        int Int() { int v = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at)); at += 4; return v; }
        float Float() { float v = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(at)); at += 4; return v; }
        int count = Int();
        if (count < 0 || count > 4096) throw new InvalidDataException(L.T("MimicFace.FacesPosesMakeNo"));
        var poses = new BoneTransform[count][];
        for (int p = 0; p < count; p++)
        {
            int bones = Int();
            if (bones < 0 || bones > 4096) throw new InvalidDataException(L.T("MimicFace.FacePoseMakesNo"));
            var pose = new BoneTransform[bones];
            for (int b = 0; b < bones; b++)
            {
                int has = raw[at++];
                var position = (has & 1) != 0 ? new Vector3(Float(), Float(), Float()) : Vector3.Zero;
                var rotation = (has & 2) != 0 ? new Quaternion(Float(), Float(), Float(), Float()) : Quaternion.Identity;
                var scale = (has & 4) != 0 ? new Vector3(Float(), Float(), Float()) : Vector3.One;
                pose[b] = new BoneTransform(position, rotation.LengthSquared() > 1e-8f ? Quaternion.Normalize(rotation) : Quaternion.Identity, scale);
            }
            poses[p] = pose;
        }
        return poses;
    }

    /// <summary>
    /// Adds each control's pose into a pose of the face's bones, as far as <paramref name="weights"/> says that control
    /// is on (by control, as <see cref="Controls"/> lists them). <paramref name="index"/> says where each face bone is in
    /// <paramref name="pose"/>, or -1 for one that isn't there.
    /// </summary>
    public void Apply(ReadOnlySpan<float> weights, Span<BoneTransform> pose, int[] index)
    {
        for (int p = 0; p < Poses.Length && p < weights.Length; p++)
        {
            float w = weights[p];
            if (MathF.Abs(w) < 1e-3f) continue;
            var deltas = Poses[p];
            for (int k = 0; k < deltas.Length && k < Bones.Length; k++)
            {
                int bone = Bones[k];
                if (bone < 0 || bone >= index.Length || index[bone] is not (int at and >= 0) || at >= pose.Length) continue;
                var d = deltas[k];
                var current = pose[at];
                pose[at] = current with
                {
                    Position = current.Position + d.Position * w,
                    Rotation = Quaternion.Normalize(current.Rotation * (w >= 0 ? Quaternion.Slerp(Quaternion.Identity, d.Rotation, w) : Quaternion.Inverse(Quaternion.Slerp(Quaternion.Identity, d.Rotation, -w)))),
                };
            }
        }
    }
}

/// <summary>
/// A line's lip sync, as a speech file keeps it beside the line's sound: how far each of the face's controls is on over
/// the line, a frame apart. Saved without a CR2W file round it: its frame rate and length, the face's two bones (neck
/// and head, which it leaves where they are) and its float tracks, each a record of sixteen bytes (frame time,
/// compression, frame count and where its keys are), then its keys' bytes after their count, then its frame count and
/// frame time again.
/// </summary>
public sealed class LipSync
{
    private readonly float[][] _tracks;
    private readonly float _frameTime;

    private LipSync(float[][] tracks, float frameTime, float duration)
    {
        _tracks = tracks;
        _frameTime = frameTime;
        Duration = duration;
    }

    public float Duration { get; }

    public int TrackCount => _tracks.Length;

    /// <summary>How far a control is on at a time into the line.</summary>
    public float Value(int track, float time)
    {
        if (track < 0 || track >= _tracks.Length || _tracks[track].Length == 0) return 0;
        var keys = _tracks[track];
        if (keys.Length == 1 || _frameTime <= 0) return keys[0];
        float frame = Math.Clamp(time / _frameTime, 0, keys.Length - 1);
        int a = (int)frame;
        return a + 1 >= keys.Length ? keys[^1] : keys[a] + (keys[a + 1] - keys[a]) * (frame - a);
    }

    public static LipSync Read(byte[] blob)
    {
        int at = 4;
        float duration = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(at));
        at += 4;
        int Packed() => PackedInt.Read(blob, ref at);
        int bones = Packed();
        if (bones < 0 || bones > 64) throw new InvalidDataException(L.T("MimicFace.LipSyncsBonesMake"));
        at += bones * 2 * 16; // the neck and head stay where they are
        int count = Packed();
        if (count < 0 || count > 1024) throw new InvalidDataException(L.T("MimicFace.LipSyncsTracksMake"));
        var records = new (float FrameTime, int Compression, int Frames, int Address)[count];
        for (int t = 0; t < count; t++)
        {
            var r = blob.AsSpan(at, 16);
            records[t] = (BinaryPrimitives.ReadSingleLittleEndian(r), r[4], BinaryPrimitives.ReadUInt16LittleEndian(r[6..]), BinaryPrimitives.ReadInt32LittleEndian(r[8..]));
            at += 16;
        }
        int length = Packed(), data = at;
        if (data + length > blob.Length) throw new InvalidDataException(L.T("MimicFace.LipSyncsKeysRun"));
        float frameTime = records.FirstOrDefault(r => r.Frames > 1).FrameTime;
        var tracks = new float[count][];
        for (int t = 0; t < count; t++)
        {
            var (_, compression, frames, address) = records[t];
            int size = compression switch { 0 => 4, 1 => 3, 2 => 2, _ => throw new InvalidDataException(L.T("MimicFace.LipSyncUsesCompression", compression)) };
            if (address < 0 || address + (long)frames * size > length) throw new InvalidDataException(L.T("MimicFace.LipSyncsTrackRuns"));
            var keys = new float[frames];
            for (int k = 0; k < frames; k++)
            {
                int p = data + address + k * size;
                keys[k] = compression switch
                {
                    0 => BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(p)),
                    1 => BitConverter.Int32BitsToSingle(blob[p] << 8 | blob[p + 1] << 16 | blob[p + 2] << 24),
                    _ => BitConverter.Int32BitsToSingle(blob[p] << 16 | blob[p + 1] << 24),
                };
            }
            tracks[t] = keys;
        }
        return new LipSync(tracks, frameTime > 0 ? frameTime : 1 / 30f, duration);
    }
}
