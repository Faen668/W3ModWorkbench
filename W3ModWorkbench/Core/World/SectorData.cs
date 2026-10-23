using System.Numerics;

namespace W3ModWorkbench.Core.World;

/// <summary>
/// What a cooked layer's sector data places: meshes (static and physical), collision, decals, dimmers, lights and
/// particle systems, each by the number the data gives its kind.
/// </summary>
public enum SectorObjectKind : byte
{
    Mesh = 1, Collision = 2, Decal = 3, Dimmer = 4, PointLight = 5, SpotLight = 6, RigidBody = 7, Particles = 10,
}

/// <summary>
/// One thing a layer's sector data places: its kind, where it is (its matrix: its axes, then where it stands), the
/// resource it uses (by the hash of its path; 0 for none), its flags, and its data block as it's kept.
/// </summary>
public sealed record SectorObject(SectorObjectKind Kind, Matrix4x4 Place, ulong Resource, ushort Flags, byte[] Data)
{
    /// <summary>For a mesh: whether it's seen, and whether it casts the sun's shadow (its flags' bits 4 and 1).</summary>
    public bool Visible => (Flags & 4) != 0;

    public bool CastsShadow => (Flags & 1) != 0;
}

/// <summary>
/// A cooked layer's CSectorData (its tail), where the game's cooking moves what the layer's entities draw that never
/// changes: their meshes, decals and lights, out of the entities themselves. It's a 4-byte header; a packed count of
/// 64-bit numbers, then them; a packed count of resources, each its bounding box (six floats) and the FNV-1a 64 hash
/// of its lower-case path (the first an empty one); a packed count of objects, each 24 bytes (its kind, flags, a
/// radius, where its data starts in the data block, where it stands); then the data block's packed length and the
/// block. A mesh's or a rigid body's data starts with its matrix (three axes, then where it is, twelve floats), then
/// two 16-bit numbers (the second its flags), a 32-bit one, and the number of its resource; a decal's and a particle
/// system's resource number is there too.
/// </summary>
public static class SectorData
{
    public static List<SectorObject> Read(byte[] tail)
    {
        var objects = new List<SectorObject>();
        if (tail.Length < 8) return objects;
        int at = 4;
        if (!Packed.Count(tail, ref at, out int numbers)) return objects;
        at += numbers * 8;
        if (!Packed.Count(tail, ref at, out int resourceCount) || at + resourceCount * 32L > tail.Length) return objects;
        var resources = new ulong[resourceCount];
        for (int i = 0; i < resourceCount; i++) resources[i] = BitConverter.ToUInt64(tail, at + i * 32 + 24);
        at += resourceCount * 32;
        if (!Packed.Count(tail, ref at, out int objectCount) || at + objectCount * 24L > tail.Length) return objects;
        var heads = new (byte Kind, long Offset)[objectCount];
        for (int i = 0; i < objectCount; i++) heads[i] = (tail[at + i * 24], BitConverter.ToInt64(tail, at + i * 24 + 4));
        at += objectCount * 24;
        if (!Packed.Count(tail, ref at, out int blockLength) || at + (long)blockLength > tail.Length) return objects;
        var ends = heads.Select(h => h.Offset).Append(blockLength).Distinct().Order().ToArray();
        foreach (var (kind, offset) in heads)
        {
            if (offset < 0 || offset >= blockLength) continue;
            long end = ends.First(e => e > offset);
            var data = tail.AsSpan(at + (int)offset, (int)(end - offset)).ToArray();
            if (data.Length < 48) continue;
            float F(int i) => BitConverter.ToSingle(data, i * 4);
            var place = new Matrix4x4(F(0), F(1), F(2), 0, F(3), F(4), F(5), 0, F(6), F(7), F(8), 0, F(9), F(10), F(11), 1);
            ushort flags = data.Length >= 52 ? BitConverter.ToUInt16(data, 50) : (ushort)0;
            // the resource's number at 56 for the kinds that use one (a mesh, a rigid body, a decal's texture, a particle system)
            int index = (SectorObjectKind)kind is SectorObjectKind.Mesh or SectorObjectKind.RigidBody or SectorObjectKind.Decal or SectorObjectKind.Particles
                && data.Length >= 60 ? BitConverter.ToInt32(data, 56) : 0;
            ulong resource = index > 0 && index < resources.Length ? resources[index] : 0;
            objects.Add(new SectorObject((SectorObjectKind)kind, place, resource, flags, data));
        }
        return objects;
    }

    /// <summary>The hash a sector's resources are named by: FNV-1a 64 of the lower-case path.</summary>
    public static ulong PathHash(string path)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (char c in path.ToLowerInvariant().Replace('/', '\\'))
        {
            // the paths are ASCII; a wider character as its UTF-8 bytes
            if (c < 0x80) { h = (h ^ c) * 0x100000001b3; continue; }
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(c.ToString())) h = (h ^ b) * 0x100000001b3;
        }
        return h;
    }
}
