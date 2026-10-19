using System.Numerics;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// Bends a skinned mesh into a pose. Each vertex is moved by the bones it's bound to, as much as each one's weight
/// says: taken into the bone's space as the mesh was bound (the mesh's inverse bind matrix), then out again where the
/// bone is in the pose. A mesh names its bones, and they're found in the rig by name.
/// </summary>
public static class Skinning
{
    /// <summary>
    /// For each of a mesh's bones, the rig's bone that moves it: the one with its name, or, for a bone the rig hasn't
    /// got (a face's or a dangling chain's, which have rigs of their own), the nearest joint the mesh is bound to, which carries
    /// it along stiffly. <paramref name="rest"/> is the rig's bones in the model as it stands, from <see cref="Skeleton.ToModel"/>.
    /// </summary>
    public static (int Bone, bool ByName)[] MapBones(MeshFile mesh, Skeleton rig, Matrix4x4[] rest)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rig.Count; i++) byName.TryAdd(rig.Names[i], i);
        var map = new (int, bool)[mesh.BoneNames.Length];
        for (int i = 0; i < map.Length; i++)
            map[i] = byName.TryGetValue(mesh.BoneNames[i], out int found) ? (found, true) : (-1, false);
        // a bone the rig hasn't got goes with a joint the mesh is bound to by name (a face with the head and neck, hair
        // with those and the torso), not one it merely passes near, such as an arm under long hair; and never with a
        // helper the animations turn on its own (the head's roll), which would tear a face in two
        var candidates = map.Where(m => m.Item2).Select(m => m.Item1).Distinct().Where(r => !IsHelper(rig.Names[r])).ToList();
        if (candidates.Count == 0) candidates = [.. Enumerable.Range(0, Math.Min(rig.Count, rest.Length)).Where(r => !IsHelper(rig.Names[r]))];
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i].Item2) continue;
            if (i >= mesh.BoneInverseBinds.Length || !Matrix4x4.Invert(mesh.BoneInverseBinds[i], out var bound)) continue;
            int nearest = -1;
            float best = float.MaxValue;
            foreach (int r in candidates)
            {
                float distance = Vector3.DistanceSquared(rest[r].Translation, bound.Translation);
                if (distance < best) (best, nearest) = (distance, r);
            }
            map[i] = (nearest, false);
        }
        return map;
    }

    /// <summary>A rig's helper joint, which animations turn apart from the bones around it: a roll or twist.</summary>
    private static bool IsHelper(string name) =>
        name.Contains("roll", StringComparison.OrdinalIgnoreCase) || name.Contains("twist", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The matrix that moves each of a mesh's bones from where it was bound to where it is in a pose (the rig's bones
    /// in the model). A bone found by name follows its own; one carried by a nearby joint moves just as that joint does,
    /// as this mesh bound it when it binds it too (a jaw with the head: the mesh may have been bound in a pose a little
    /// off the rig's, and going by the rig's would part the jaw from the face), else as the joint has moved from where
    /// it stands in the rig; one with neither stays where it was bound.
    /// </summary>
    public static Matrix4x4[] BoneMatrices(MeshFile mesh, (int Bone, bool ByName)[] map, Matrix4x4[] rest, Matrix4x4[] pose)
    {
        var matrices = new Matrix4x4[mesh.BoneNames.Length];
        for (int i = 0; i < matrices.Length; i++)
        {
            var (bone, byName) = i < map.Length ? map[i] : (-1, false);
            if (bone < 0 || bone >= pose.Length) matrices[i] = Matrix4x4.Identity;
            else if (byName && i < mesh.BoneInverseBinds.Length) matrices[i] = mesh.BoneInverseBinds[i] * pose[bone];
            else if (Array.FindIndex(map, m => m.Bone == bone && m.ByName) is int carrier and >= 0 && carrier < mesh.BoneInverseBinds.Length)
                matrices[i] = mesh.BoneInverseBinds[carrier] * pose[bone];
            else matrices[i] = Matrix4x4.Invert(rest[bone], out var back) ? back * pose[bone] : Matrix4x4.Identity;
        }
        return matrices;
    }

    /// <summary>A chunk's vertices bent by its bones' matrices, or null for a chunk that isn't skinned.</summary>
    public static Vector3[]? Pose(MeshChunk chunk, Matrix4x4[] bones)
    {
        int per = chunk.BonesPerVertex;
        if (per == 0 || bones.Length == 0) return null;
        var posed = new Vector3[chunk.Positions.Length];
        for (int v = 0; v < posed.Length; v++)
        {
            var at = chunk.Positions[v];
            var sum = Vector3.Zero;
            float total = 0;
            for (int b = 0; b < per; b++)
            {
                float weight = chunk.BoneWeights[v * per + b];
                int bone = chunk.BoneIndices[v * per + b];
                if (weight <= 0 || bone >= bones.Length) continue;
                sum += Vector3.Transform(at, bones[bone]) * weight;
                total += weight;
            }
            // weights saved as bytes add up to about one. A vertex saved with none (some of Imlerith's face) goes wholly
            // with its first bone, as the game draws it: left where it was bound, it would stretch across the scene
            if (total > 0) posed[v] = sum / total;
            else posed[v] = chunk.BoneIndices[v * per] is var first && first < bones.Length ? Vector3.Transform(at, bones[first]) : at;
        }
        return posed;
    }
}
