using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>
/// A cloth resource (.redcloth) as a mesh to draw: the shape it has before it's simulated, skinned to the character's
/// bones like any other mesh, with the material the resource names.
/// <para>
/// The cloth's geometry is an NVIDIA APEX clothing asset, kept in the .redcloth's <c>apexBinaryAsset</c> as REDkit
/// saves it, or, once cooked, in the game's <c>collision.cache</c> under the .redcloth's path. Either way it's APEX's
/// binary NxParameterized format: a big-endian header (the magic 5A5B5C5D, the file's length, then where its data and
/// its relocations are), then the objects as their 64-bit C++ structs were laid out in memory, then a table of every
/// place in them that holds a pointer (to an object, or to an array's items). Each object's fields start 0x78 bytes in.
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
        var lods = asset.RenderMeshes();
        for (int lod = 0; lod < lods.Count && lod < 8; lod++)
            foreach (var submesh in lods[lod])
                if (asset.Submesh(submesh, chunks.Count, lod) is { } chunk) chunks.Add(chunk);
        if (chunks.Count == 0) throw new InvalidDataException(L.T("ClothFile.ClothsRenderMeshWasnt"));

        var (names, binds) = asset.Bones();
        var materials = cloth.Array("materials").OfType<RedHandle>().ToList();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in chunks.Where(c => c.InLod(0)).SelectMany(c => c.Positions))
            (min, max) = (Vector3.Min(min, p), Vector3.Max(max, p));
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
        };
    }

    /// <summary>An APEX binary asset, read by following the pointers its relocation table lists.</summary>
    private sealed class ApexAsset
    {
        private const int Fields = 0x78;
        private readonly byte[] _data;
        private readonly Dictionary<long, int> _relocations = [];

        public ApexAsset(byte[] data)
        {
            _data = data;
            uint relocationsAt = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(28));
            if (relocationsAt + 4 > data.Length) throw new InvalidDataException(L.T("ClothFile.ApexAssetCutShort"));
            int count = (int)U32(relocationsAt);
            for (int i = 0; i < count && relocationsAt + 12 + i * 8L <= data.Length; i++)
                _relocations[U32(relocationsAt + 8 + i * 8)] = (int)U32(relocationsAt + 4 + i * 8);
        }

        private uint U32(long at) => at >= 0 && at + 4 <= _data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)at)) : 0;

        private float F32(long at) => at >= 0 && at + 4 <= _data.Length ? BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan((int)at)) : 0;

        /// <summary>Where a pointer at a place points, or -1 when nothing is relocated there (an empty pointer).</summary>
        private long Pointer(long at, int kind)
        {
            if (!_relocations.TryGetValue(at, out int k) || k != kind) return -1;
            long to = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan((int)at));
            return to >= 0 && to < _data.Length ? to : -1;
        }

        /// <summary>An array at a place: where its items are, how big each is and how many. Null for none.</summary>
        private (long At, int Size, int Count)? Array(long at)
        {
            long items = Pointer(at, 0);
            if (items < 0) return null;
            int size = (int)U32(at + 12), count = (int)U32(at + 16);
            return size > 0 && count > 0 && items + (long)size * count <= _data.Length ? (items, size, count) : null;
        }

        /// <summary>Whether an object at a place is a submesh: a vertex buffer, then an array of 32-bit indices.</summary>
        private bool IsSubmesh(long o) => Pointer(o + Fields, 1) >= 0 && Array(o + Fields + 8) is { Size: 4 };

        /// <summary>The submeshes of each render mesh asset, one asset a level of detail, the most detailed first.</summary>
        public List<List<long>> RenderMeshes()
        {
            var lods = new List<List<long>>();
            foreach (var (at, kind) in _relocations.OrderBy(r => r.Key))
            {
                if (kind != 0 || Array(at) is not { Size: 8 } list) continue;
                var submeshes = Enumerable.Range(0, list.Count).Select(i => Pointer(list.At + i * 8L, 1)).ToList();
                if (submeshes.All(s => s >= 0 && IsSubmesh(s))) lods.Add(submeshes);
            }
            return lods;
        }

        /// <summary>A submesh as a chunk: its vertex buffer's positions, normals, UVs and bones, and its triangles.</summary>
        public MeshChunk? Submesh(long submesh, int index, int lod)
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
                for (int v = 0; v < vertices; v++) uvs[v] = new Vector2(F32(twos[0].At + v * 8L), F32(twos[0].At + v * 8L + 4));
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
                triangles[i] = vertex < vertices ? vertex : 0;
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

        private Vector3[] Vectors((long At, int Size, int Count) buffer, int vertices)
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
                    int end = System.Array.IndexOf(_data, (byte)0, (int)name);
                    names[internalIndex] = System.Text.Encoding.ASCII.GetString(_data, (int)name, (end < 0 ? _data.Length : end) - (int)name);
                    float M(int k) => F32(bone + 0x14 + k * 4);
                    // the 3×3 is kept a column at a time; the engine's matrices have the axes as rows
                    var pose = new Matrix4x4(M(0), M(3), M(6), 0, M(1), M(4), M(7), 0, M(2), M(5), M(8), 0, M(9), M(10), M(11), 1);
                    binds[internalIndex] = Matrix4x4.Invert(pose, out var inverse) ? inverse : Matrix4x4.Identity;
                }
                if (good) return ([.. names.Select(n => n ?? "")], binds);
            }
            return ([], []);
        }
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
