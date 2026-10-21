using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>
/// A cloth resource (.redcloth) as a mesh to draw: the shape it has before it's simulated, skinned to the character's
/// bones like any other mesh, with the material the resource names, and what it takes to simulate it (<see cref="ClothAsset"/>).
/// <para>
/// The cloth's geometry is an NVIDIA APEX clothing asset, kept in the .redcloth's <c>apexBinaryAsset</c> as REDkit
/// saves it, or, once cooked, in the game's <c>collision.cache</c> under the .redcloth's path. Either way it's APEX's
/// binary NxParameterized format: a big-endian header (the magic 5A5B5C5D, the file's length, then where its data and
/// its relocations are), then the objects as their 64-bit C++ structs were laid out in memory, then a table of every
/// place in them that holds a pointer (to an object, or to an array's items). An object starts with a header (where its
/// fields are, its class name, its name, its version); its fields start 0x78 bytes in.
/// An array is a pointer, a flag, the size of an item and how many there are. The render mesh is found by following
/// those pointers: a render mesh asset for each level of detail lists its submeshes; a submesh points to its vertex
/// buffer and holds its triangles' indices; a vertex buffer holds its vertex count and a list of buffers (positions,
/// normals, tangents, four sets of UVs, bone indices and weights, in that order). The asset's bones each hold their
/// pose as bound and their name.
/// </para>
/// </summary>
public static class ClothFile
{
    /// <summary>Whether a path is a cloth resource.</summary>
    public static bool IsCloth(string path) => path.EndsWith(".redcloth", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a cloth resource as a mesh: its most detailed render mesh, or every level of detail as LODs.
    /// <paramref name="cooked"/> gives its APEX asset from the collision cache when the file hasn't it itself.
    /// </summary>
    public static MeshFile Read(byte[] data, Func<byte[]?> cooked)
    {
        var file = Cr2wFile.Read(data);
        var cloth = file.First("CApexClothResource") ?? throw new InvalidDataException(L.T("ClothFile.IsntClothResource"));
        var apex = cloth["apexBinaryAsset"] is byte[] { Length: > 0 } own ? own : cooked();
        if (apex is null) throw new FileNotFoundException(L.T("ClothFile.ClothsShapeWasntFound"));
        int start = apex.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x5A, 0x5B, 0x5C, 0x5D]);
        if (start < 0) throw new InvalidDataException(L.T("ClothFile.ClothsShapeIsntApex"));
        var asset = new ApexAsset(apex.AsSpan(start).ToArray());

        var chunks = new List<MeshChunk>();
        // each chunk's level of detail and where its vertices start among that level's, which the cloth's maps count in
        var placed = new Dictionary<int, (int Lod, int First)>();
        var lods = asset.RenderMeshes();
        for (int lod = 0; lod < lods.Count && lod < 8; lod++)
        {
            int first = 0;
            foreach (var submesh in lods[lod].Submeshes)
                if (asset.Submesh(submesh, chunks.Count, lod, lods[lod].UvOrigin) is { } chunk)
                {
                    placed[chunk.Index] = (lod, first);
                    first += chunk.Positions.Length;
                    chunks.Add(chunk);
                }
        }
        if (chunks.Count == 0) throw new InvalidDataException(L.T("ClothFile.ClothsRenderMeshWasnt"));

        var (names, binds) = asset.Bones();
        var materials = cloth.Array("materials").OfType<RedHandle>().ToList();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in chunks.Where(c => c.InLod(0)).SelectMany(c => c.Positions))
            (min, max) = (Vector3.Min(min, p), Vector3.Max(max, p));
        ClothAsset? simulated = null;
        try { simulated = ClothAsset.Read(asset, cloth, binds, placed); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { }
        return new MeshFile
        {
            File = file,
            Mesh = cloth,
            Chunks = [.. chunks.Select(c => c.Material < materials.Count ? c : new MeshChunk
            {
                Index = c.Index, Material = 0, LodMask = c.LodMask, RenderMask = c.RenderMask, Skinned = c.Skinned,
                Positions = c.Positions, Normals = c.Normals, Uvs = c.Uvs, Indices = c.Indices,
                BonesPerVertex = c.BonesPerVertex, BoneIndices = c.BoneIndices, BoneWeights = c.BoneWeights,
            })],
            Materials = materials,
            LodDistances = [.. Enumerable.Range(0, Math.Max(1, lods.Count)).Select(i => i * 10f)],
            BoundsMin = min,
            BoundsMax = max,
            BoneNames = names,
            BoneInverseBinds = binds,
            Cloth = simulated,
        };
    }
}

/// <summary>The simulation settings a cloth resource keeps as its own properties, which REDkit's Apex Cloth tab sets.</summary>
public static class ClothSettings
{
    /// <summary>The resource's properties for a material and thickness, by name: the ones REDkit's Apex Cloth tab saves.</summary>
    public static Dictionary<string, object> Of(ClothMaterial m, float thickness) => new()
    {
        ["simThickness"] = thickness,
        ["mtlMassScale"] = m.MassScale,
        ["mtlFriction"] = m.Friction,
        ["mtlGravityScale"] = m.GravityScale,
        ["mtlBendingStiffness"] = m.Bending,
        ["mtlShearingStiffness"] = m.Shearing,
        ["mtlTetherStiffness"] = m.TetherStiffness,
        ["mtlTetherLimit"] = m.TetherLimit,
        ["mtlDamping"] = m.Damping,
        ["mtlDrag"] = m.Drag,
        ["mtlInertiaScale"] = m.InertiaScale,
        ["mtlMaxDistanceBias"] = m.MaxDistanceBias,
    };

    /// <summary>
    /// A cloth resource with its settings changed: each property set on its CApexClothResource (added when the file
    /// left it out, as cooking does one at its default), through its JSON, the rest of it kept as it was.
    /// </summary>
    public static byte[] Write(byte[] data, IReadOnlyDictionary<string, object> settings)
    {
        var json = Cr2wJson.ToJson(Cr2wFile.Read(data), out _);
        foreach (var (name, value) in settings)
        {
            var edit = new Cr2wBulkEdit
            {
                Kind = BulkEditKind.Set,
                Path = name,
                ClassName = "CApexClothResource",
                Value = value switch { float f => System.Text.Json.Nodes.JsonValue.Create(f), bool b => System.Text.Json.Nodes.JsonValue.Create(b), _ => null },
            };
            if (edit.Apply(json) == 0) throw new InvalidDataException(L.T("ClothFile.SettingNotSet", name));
        }
        return Cr2wJson.FromJson(json);
    }
}

/// <summary>An APEX binary asset, read by following the pointers its relocation table lists.</summary>
internal sealed class ApexAsset
{
    /// <summary>How far into an object its fields start: its header, then the NvParameters it's built on.</summary>
    public const int Fields = 0x78;
    private readonly byte[] _data;
    private readonly Dictionary<long, int> _relocations = [];
    private readonly List<(long Header, string Class)> _objects = [];

    public ApexAsset(byte[] data)
    {
        _data = data;
        uint relocationsAt = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(28));
        if (relocationsAt + 4 > data.Length) throw new InvalidDataException(L.T("ClothFile.ApexAssetCutShort"));
        int count = (int)U32(relocationsAt);
        for (int i = 0; i < count && relocationsAt + 12 + i * 8L <= data.Length; i++)
            _relocations[U32(relocationsAt + 8 + i * 8)] = (int)U32(relocationsAt + 4 + i * 8);
        // every object something points to: a header whose class name is a pointer to its name
        var seen = new HashSet<long>();
        foreach (var (at, kind) in _relocations.OrderBy(r => r.Key))
            if (kind == 1 && Pointer(at, 1) is >= 0 and var header && seen.Add(header) && Pointer(header + 8, 0) is >= 0 and var name)
                _objects.Add((header, Text(name)));
        _objects.Sort((a, b) => a.Header.CompareTo(b.Header));
    }

    public uint U32(long at) => at >= 0 && at + 4 <= _data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)at)) : 0;

    public float F32(long at) => at >= 0 && at + 4 <= _data.Length ? BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan((int)at)) : 0;

    public byte U8(long at) => at >= 0 && at < _data.Length ? _data[at] : (byte)0;

    public Vector3 Vector(long at) => new(F32(at), F32(at + 4), F32(at + 8));

    /// <summary>A zero-ended string at a place.</summary>
    public string Text(long at)
    {
        int end = System.Array.IndexOf(_data, (byte)0, (int)at);
        return System.Text.Encoding.ASCII.GetString(_data, (int)at, (end < 0 ? _data.Length : end) - (int)at);
    }

    /// <summary>The objects in the asset, by where their header is, with their class.</summary>
    public IReadOnlyList<(long Header, string Class)> Objects => _objects;

    /// <summary>The first object of a class's fields, or -1.</summary>
    public long First(string className) => _objects.FirstOrDefault(o => o.Class == className) is { Class: not null } o ? o.Header + Fields : -1;

    /// <summary>Where a pointer at a place points, or -1 when nothing is relocated there (an empty pointer).</summary>
    public long Pointer(long at, int kind)
    {
        if (!_relocations.TryGetValue(at, out int k) || k != kind) return -1;
        long to = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan((int)at));
        return to >= 0 && to < _data.Length ? to : -1;
    }

    /// <summary>The fields of the object a reference at a place points to, or -1.</summary>
    public long Reference(long at) => Pointer(at, 1) is >= 0 and var header ? header + Fields : -1;

    /// <summary>An array at a place: where its items are, how big each is and how many. Null for none.</summary>
    public (long At, int Size, int Count)? Array(long at)
    {
        long items = Pointer(at, 0);
        if (items < 0) return null;
        int size = (int)U32(at + 12), count = (int)U32(at + 16);
        return size > 0 && count > 0 && items + (long)size * count <= _data.Length ? (items, size, count) : null;
    }

    /// <summary>An array of items of a size, or none when it's missing or its items aren't that size.</summary>
    public (long At, int Count) Items(long at, int size) => Array(at) is { } a && a.Size == size ? (a.At, a.Count) : (0, 0);

    /// <summary>Whether an object at a place is a submesh: a vertex buffer, then an array of 32-bit indices.</summary>
    private bool IsSubmesh(long o) => Pointer(o + Fields, 1) >= 0 && Array(o + Fields + 8) is { Size: 4 };

    /// <summary>The header of the object a place is in.</summary>
    private long Owner(long at)
    {
        long owner = -1;
        foreach (var (header, _) in _objects)
        {
            if (header > at) break;
            owner = header;
        }
        return owner;
    }

    /// <summary>
    /// The submeshes of each render mesh asset, one asset a level of detail, the most detailed first: in the order the
    /// clothing asset's graphical LODs list them when it has them, which its maps to the simulated mesh go by. With each,
    /// the corner its UVs count from (<c>textureUVOrigin</c>, after its submeshes, material names and part bounds): 0 the
    /// top left, as the engine's do, 1 the top right, 2 the bottom left (every cloth the game has), 3 the bottom right.
    /// </summary>
    public List<(List<long> Submeshes, int UvOrigin)> RenderMeshes()
    {
        var lists = new List<(long Owner, List<long> Submeshes)>();
        foreach (var (at, kind) in _relocations.OrderBy(r => r.Key))
        {
            if (kind != 0 || Array(at) is not { Size: 8 } list) continue;
            var submeshes = Enumerable.Range(0, list.Count).Select(i => Pointer(list.At + i * 8L, 1)).ToList();
            if (submeshes.All(s => s >= 0 && IsSubmesh(s))) lists.Add((Owner(at), submeshes));
        }
        int Origin(long owner) => owner >= 0 && _objects.Any(o => o.Header == owner && o.Class == "RenderMeshAssetParameters") ? (int)U32(owner + Fields + 72) : 0;
        long clothing = First("ClothingAssetParameters");
        if (clothing >= 0 && Items(clothing + 24, 8) is { Count: > 0 } graphical)
        {
            var ordered = new List<(List<long>, int)>();
            for (int i = 0; i < graphical.Count; i++)
            {
                long lod = Reference(graphical.At + i * 8L);
                long renderMesh = lod >= 0 ? Pointer(lod + 32, 1) : -1;
                if (lists.FirstOrDefault(l => l.Owner == renderMesh) is { Submeshes: not null } found) ordered.Add((found.Submeshes, Origin(found.Owner)));
            }
            if (ordered.Count == graphical.Count) return ordered;
        }
        return [.. lists.Select(l => (l.Submeshes, Origin(l.Owner)))];
    }

    /// <summary>
    /// A submesh as a chunk: its vertex buffer's positions, normals, UVs and bones, and its triangles. Its UVs are turned
    /// to count from the top left as the engine's do (<paramref name="uvOrigin"/> says where they count from), and its
    /// triangles wound the engine's way, which is the other way round to APEX's.
    /// </summary>
    public MeshChunk? Submesh(long submesh, int index, int lod, int uvOrigin)
    {
        long vertexBuffer = Pointer(submesh + Fields, 1);
        if (Array(submesh + Fields + 8) is not { } indexArray || vertexBuffer < 0) return null;
        int vertices = (int)U32(vertexBuffer + Fields);
        if (Array(vertexBuffer + Fields + 16) is not { Size: 8 } bufferList) return null;
        var buffers = new List<(long At, int Size, int Count)>();
        for (int i = 0; i < bufferList.Count; i++)
            if (Pointer(bufferList.At + i * 8L, 1) is >= 0 and var buffer && Array(buffer + Fields) is { } items && items.Count == vertices)
                buffers.Add(items);

        // in the order the vertex format lists them: position, normal, tangent, UVs, bone indices, bone weights
        var threes = buffers.Where(b => b.Size == 12).ToList();
        var twos = buffers.Where(b => b.Size == 8).ToList();
        var fours = buffers.Where(b => b.Size == 16).ToList();
        if (threes.Count == 0) return null;
        var positions = Vectors(threes[0], vertices);
        var normals = threes.Count > 1 ? Vectors(threes[1], vertices) : new Vector3[vertices];
        var uvs = new Vector2[vertices];
        if (twos.Count > 0)
            for (int v = 0; v < vertices; v++)
            {
                float u = F32(twos[0].At + v * 8L), w = F32(twos[0].At + v * 8L + 4);
                uvs[v] = new Vector2(uvOrigin is 1 or 3 ? 1 - u : u, uvOrigin is 2 or 3 ? 1 - w : w);
            }
        // the bone indices are four 16-bit numbers, the last eight-byte buffer; the weights four floats, the last sixteen-byte one
        byte[] boneIndices = [];
        float[] boneWeights = [];
        if (twos.Count > 1 && fours.Count > 1)
        {
            var (ia, wa) = (twos[^1].At, fours[^1].At);
            boneIndices = new byte[vertices * 4];
            boneWeights = new float[vertices * 4];
            for (int i = 0; i < vertices * 4; i++)
            {
                boneIndices[i] = (byte)Math.Min(255, (int)BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)(ia + i * 2L))));
                boneWeights[i] = F32(wa + i * 4L);
            }
        }
        var triangles = new int[indexArray.Count - indexArray.Count % 3];
        for (int i = 0; i < triangles.Length; i++)
        {
            int vertex = (int)U32(indexArray.At + i * 4L);
            // each triangle's last two corners swapped, to wind it as the engine does
            triangles[i % 3 == 0 ? i : i % 3 == 1 ? i + 1 : i - 1] = vertex < vertices ? vertex : 0;
        }
        return new MeshChunk
        {
            Index = index,
            Material = 0,
            LodMask = 1 << lod,
            RenderMask = 1, // drawn in the scene, as a cooked chunk with the scene bit is
            Skinned = boneIndices.Length > 0,
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Indices = triangles,
            BonesPerVertex = boneIndices.Length > 0 ? 4 : 0,
            BoneIndices = boneIndices,
            BoneWeights = boneWeights,
        };
    }

    public Vector3[] Vectors((long At, int Size, int Count) buffer, int vertices)
    {
        var vectors = new Vector3[vertices];
        for (int v = 0; v < vertices; v++)
            vectors[v] = new Vector3(F32(buffer.At + v * 12L), F32(buffer.At + v * 12L + 4), F32(buffer.At + v * 12L + 8));
        return vectors;
    }

    /// <summary>
    /// The asset's bones, by the index the vertices use (each bone's internal index): their names, and the inverse
    /// of the pose each was bound in. A bone is 88 bytes: its internal and external index, two counts, its parent,
    /// its pose as a 3×4 matrix (the 3×3 a column at a time, then where it is) and a pointer to its name.
    /// </summary>
    public (string[] Names, Matrix4x4[] InverseBinds) Bones()
    {
        foreach (var (at, kind) in _relocations.OrderBy(r => r.Key))
        {
            if (kind != 0 || Array(at) is not { Size: 88 } bones) continue;
            var names = new string[bones.Count];
            var binds = new Matrix4x4[bones.Count];
            bool good = true;
            for (int i = 0; i < bones.Count && good; i++)
            {
                long bone = bones.At + i * 88L;
                int internalIndex = (int)U32(bone);
                long name = Pointer(bone + 0x48, 0);
                good = name >= 0 && internalIndex < bones.Count;
                if (!good) break;
                names[internalIndex] = Text(name);
                binds[internalIndex] = Matrix4x4.Invert(Pose(bone + 0x14), out var inverse) ? inverse : Matrix4x4.Identity;
            }
            if (good) return ([.. names.Select(n => n ?? "")], binds);
        }
        return ([], []);
    }

    /// <summary>A 3×4 pose as APEX keeps it (the 3×3 a column at a time, then where it is), as the engine's matrix with the axes as rows.</summary>
    public Matrix4x4 Pose(long at)
    {
        float M(int k) => F32(at + k * 4);
        return new Matrix4x4(M(0), M(3), M(6), 0, M(1), M(4), M(7), 0, M(2), M(5), M(8), 0, M(9), M(10), M(11), 1);
    }
}

/// <summary>
/// The game's collision.cache, where cooked meshes' collision and cloth's APEX assets are kept by their resource's
/// path. It's "CC3W" and a version, then where its index is (64 bits at 0x10), how many entries (at 0x18), where the
/// names are (64 bits at 0x1C) and how long they are (at 0x24). An entry is 64 bytes: where its name starts among the
/// names, where its data is (64 bits at 0x0C), its packed and unpacked size (at 0x14 and 0x18); the data is zlib.
/// </summary>
public sealed class CollisionCache
{
    private readonly string _path;
    private readonly Dictionary<string, (long At, int Packed, int Size)> _entries = new(StringComparer.OrdinalIgnoreCase);

    private CollisionCache(string path) => _path = path;

    public static CollisionCache Open(string path)
    {
        var cache = new CollisionCache(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[0x40];
        fs.ReadExactly(header);
        if (header[0] != 'C' || header[1] != 'C' || header[2] != '3' || header[3] != 'W') throw new InvalidDataException(L.T("ClothFile.NotCollisionCache"));
        long indexAt = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x10));
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x18));
        long namesAt = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x1C));
        int namesLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x24));
        if (count < 0 || namesLength < 0 || indexAt + count * 64L > fs.Length || namesAt + namesLength > fs.Length) throw new InvalidDataException(L.T("ClothFile.CollisionCachesIndexOut"));
        var names = new byte[namesLength];
        fs.Position = namesAt;
        fs.ReadExactly(names);
        var index = new byte[count * 64];
        fs.Position = indexAt;
        fs.ReadExactly(index);
        for (int i = 0; i < count; i++)
        {
            var e = index.AsSpan(i * 64, 64);
            int name = BinaryPrimitives.ReadInt32LittleEndian(e);
            if (name < 0 || name >= names.Length) continue;
            int end = System.Array.IndexOf(names, (byte)0, name);
            string resource = System.Text.Encoding.UTF8.GetString(names, name, (end < 0 ? names.Length : end) - name);
            cache._entries.TryAdd(resource, (BinaryPrimitives.ReadInt64LittleEndian(e[0x0C..]), BinaryPrimitives.ReadInt32LittleEndian(e[0x14..]), BinaryPrimitives.ReadInt32LittleEndian(e[0x18..])));
        }
        return cache;
    }

    public bool Has(string path) => _entries.ContainsKey(path);

    /// <summary>A resource's entry, unpacked. Null when the cache hasn't it.</summary>
    public byte[]? Read(string path)
    {
        if (!_entries.TryGetValue(path, out var entry)) return null;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Position = entry.At;
        var packed = new byte[entry.Packed];
        fs.ReadExactly(packed);
        if (entry.Packed == entry.Size) return packed;
        using var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        var data = new byte[entry.Size];
        zlib.ReadExactly(data);
        return data;
    }
}
