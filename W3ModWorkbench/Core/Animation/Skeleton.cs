using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// A rig (.w2rig, a CSkeleton): its bones' names, each bone's parent, and the pose they're in when nothing moves them.
/// The pose is kept after the properties, three vectors of four floats a bone: position, orientation and scale.
/// </summary>
public sealed class Skeleton
{
    public required string[] Names { get; init; }

    /// <summary>Each bone's parent, or -1 for a root. A parent comes before its children.</summary>
    public required short[] Parents { get; init; }
    public required BoneTransform[] Pose { get; init; }

    public int Count => Names.Length;

    public static Skeleton Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var skeleton = file.First("CSkeleton") ?? throw new InvalidDataException("There's no skeleton in it: it has no CSkeleton.");
        var names = skeleton.Array("bones").OfType<RedObject>().Select(b => b["nameAsCName"] as string ?? b["name"] as string ?? "").ToArray();
        var parents = skeleton.Array("parentIndices").Select(p => p is short s ? s : (short)-1).ToArray();
        if (parents.Length != names.Length) throw new InvalidDataException("The skeleton's bones and parents don't match.");

        var pose = new BoneTransform[names.Length];
        var tail = skeleton.Tail;
        for (int i = 0; i < pose.Length; i++)
        {
            if (tail.Length < (i + 1) * 48)
            {
                pose[i] = BoneTransform.Identity;
                continue;
            }
            float F(int k) => BinaryPrimitives.ReadSingleLittleEndian(tail.AsSpan(i * 48 + k * 4));
            var rotation = new Quaternion(F(4), F(5), F(6), F(7));
            pose[i] = new BoneTransform(new Vector3(F(0), F(1), F(2)), rotation.LengthSquared() > 1e-8f ? Quaternion.Normalize(rotation) : Quaternion.Identity, new Vector3(F(8), F(9), F(10)));
        }
        return new Skeleton { Names = names, Parents = parents, Pose = pose };
    }

    /// <summary>
    /// Where each bone is in the model, from where each is relative to its parent. A parent is placed before its
    /// children, as the rig lists them.
    /// </summary>
    public void ToModel(ReadOnlySpan<BoneTransform> local, Span<Matrix4x4> model)
    {
        for (int i = 0; i < Count && i < local.Length; i++)
        {
            var t = local[i];
            var m = Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Position);
            int parent = Parents[i];
            model[i] = parent >= 0 && parent < i ? m * model[parent] : m;
        }
    }
}
