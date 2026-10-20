using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>One drawable part of a mesh: its own vertices and triangles, drawn with one of the mesh's materials, in some of its LODs.</summary>
public sealed class MeshChunk
{
    public required int Index { get; init; }
    public required int Material { get; init; }

    /// <summary>Which LODs it's drawn in, one bit for each (bit 0 is the most detailed).</summary>
    public required int LodMask { get; init; }

    /// <summary>The passes it's drawn in. 0 for chunks that are only for shadows or proxies, which aren't shown.</summary>
    public required int RenderMask { get; init; }

    public required bool Skinned { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }

    /// <summary>
    /// Each vertex's tangent, the way the normal map's red runs across the surface, with its handedness in W: the way
    /// its green runs is the normal crossed with the tangent, times W. Empty when the mesh doesn't keep them.
    /// </summary>
    public Vector4[] Tangents { get; init; } = [];

    /// <summary>Three per triangle, into this chunk's own vertices.</summary>
    public required int[] Indices { get; init; }

    /// <summary>For a skinned chunk, how many bones move each vertex (4, or 8 for some), else 0.</summary>
    public int BonesPerVertex { get; init; }

    /// <summary>Each vertex's bones, <see cref="BonesPerVertex"/> of them, as numbers into the mesh's <see cref="MeshFile.BoneNames"/>.</summary>
    public byte[] BoneIndices { get; init; } = [];

    /// <summary>How much each of a vertex's bones moves it, from 0 to 1, in the same order.</summary>
    public float[] BoneWeights { get; init; } = [];

    /// <summary>
    /// Each vertex's colour as red, green and blue bytes (three a vertex), or empty when the chunk has none. Hair
    /// shaders multiply their texture by it, which is where a character's hair colour is kept.
    /// </summary>
    public byte[] Colors { get; init; } = [];

    public int Triangles => Indices.Length / 3;

    public bool InLod(int lod) => (LodMask & (1 << lod)) != 0;
}

/// <summary>
/// A mesh (<c>.w2mesh</c>) as the game has cooked it. The mesh object says how many LODs there are and where each chunk's
/// data is; the vertices and triangles are in the mesh's first buffer (<c>name.w2mesh.1.buffer</c>, or inside the file
/// for one that isn't cooked). That buffer has every chunk's vertex streams one after another, then the triangles.
/// <para>
/// A cooked chunk's description (the mesh's <c>renderChunks</c> bytes) is: its vertex type; where its positions, UVs,
/// normals and two more streams start in the buffer; where its triangles start after the vertices; its vertex and
/// index counts; its material; its render mask; and its LOD bits. Positions are three 16-bit numbers scaled into the
/// mesh's box (with bone indices and weights after them when the mesh is skinned), UVs two half floats, and normals
/// three 10-bit numbers in a 32-bit word followed by the tangent the same way, its top two bits its handedness (both
/// set when the normal map's green runs along normal × tangent, clear when against it). Each stream starts on 16 bytes.
/// </para>
/// </summary>
public sealed class MeshFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Mesh { get; init; }
    public required List<MeshChunk> Chunks { get; init; }

    /// <summary>The mesh's materials, as the handles in its <c>materials</c> list: one in the file, or a .w2mi or .w2mg it imports.</summary>
    public required List<RedHandle> Materials { get; init; }

    /// <summary>How far away each LOD starts being drawn, in metres. One entry for a mesh without LODs.</summary>
    public required List<float> LodDistances { get; init; }

    public int LodCount => Math.Max(1, Math.Max(LodDistances.Count, Chunks.Count == 0 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)Chunks.Max(c => c.LodMask))));

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    /// <summary>The bones a skinned mesh is bound to, by name, so they can be found in a rig.</summary>
    public string[] BoneNames { get; init; } = [];

    /// <summary>For each of <see cref="BoneNames"/>, the matrix that takes the mesh into the bone's space as it was bound.</summary>
    public Matrix4x4[] BoneInverseBinds { get; init; } = [];

    /// <summary>
    /// Reads a mesh. <paramref name="buffer"/> reads one of its numbered <c>.buffer</c> files (1 for <c>.1.buffer</c>),
    /// or gives null when it isn't to be found.
    /// </summary>
    public static MeshFile Read(byte[] data, Func<int, byte[]?> buffer)
    {
        var file = Cr2wFile.Read(data);
        var mesh = file.First("CMesh") ?? throw new InvalidDataException(
            file.Exports.Count > 0 ? $"This is a {file.Exports[0].ClassName}, not a mesh." : "There's no mesh in this file.");
        var cooked = mesh.Struct("cookedData");
        var materials = mesh.Array("materials").OfType<RedHandle>().ToList();
        var box = mesh.Struct("boundingBox");
        var chunks = new List<MeshChunk>();
        var lods = new List<float>();

        var renderChunks = cooked?.Bytes("renderChunks") ?? [];
        if (renderChunks.Length > 0)
        {
            int bufferIndex = cooked!["renderBuffer"] is RedBufferRef r && r.Index > 0 ? r.Index : 1;
            var bytes = file.Buffer(bufferIndex, buffer)
                ?? throw new FileNotFoundException("The mesh's data isn't with it: its .1.buffer file wasn't found. Open the bundle with the game's buffers (buffers.bundle) in the same folder.");
            lods.AddRange(cooked.Array("renderLODs").OfType<float>());
            chunks = ReadCooked(renderChunks, bytes, cooked, mesh);
        }
        else if (mesh.Array("chunks").Count > 0)
        {
            var vertices = mesh["rawVertices"] is RedBufferRef v && v.Index > 0 ? file.Buffer(v.Index, buffer) : null;
            var indices = mesh["rawIndices"] is RedBufferRef i && i.Index > 0 ? file.Buffer(i.Index, buffer) : null;
            if (vertices is null || indices is null)
                throw new NotSupportedException("This mesh keeps its shape in a way that can't be read yet: it has no cooked data and no raw vertices.");
            chunks = ReadUncooked(mesh, vertices, indices, lods);
        }
        if (lods.Count == 0) lods.Add(0);
        var (boneNames, inverseBinds) = Bones(file, mesh);

        return new MeshFile
        {
            BoneNames = boneNames,
            BoneInverseBinds = inverseBinds,
            File = file,
            Mesh = mesh,
            Chunks = chunks,
            Materials = materials,
            LodDistances = lods,
            BoundsMin = box?.Vector("Min") ?? default,
            BoundsMax = box?.Vector("Max") ?? default,
        };
    }

    /// <summary>
    /// A skinned mesh's bones, which it keeps after its properties: after a few LOD details, a count (packed as the
    /// engine packs lengths), that many names, the count again and that many 4×4 matrices, each taking the mesh into a
    /// bone's space. They're found by that pattern, a count of names that are all names followed by as many matrices.
    /// </summary>
    private static (string[] Names, Matrix4x4[] InverseBinds) Bones(Cr2wFile file, RedObject mesh)
    {
        var tail = mesh.Tail;
        for (int at = 0; at < Math.Min(tail.Length, 128); at++)
        {
            var (count, next) = PackedCount(tail, at);
            if (count <= 0 || count > 1024 || next + count * 2L > tail.Length) continue;
            var names = new string[count];
            bool good = true;
            for (int i = 0; i < count && good; i++)
            {
                int name = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(next + i * 2));
                good = name > 0 && name < file.Names.Count;
                if (good) names[i] = file.Names[name];
            }
            if (!good) continue;
            var (again, matricesAt) = PackedCount(tail, next + count * 2);
            if (again != count || matricesAt + count * 64L > tail.Length) continue;
            var binds = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                int m = matricesAt + i * 64;
                float F(int k) => BinaryPrimitives.ReadSingleLittleEndian(tail.AsSpan(m + k * 4));
                binds[i] = new Matrix4x4(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15));
            }
            return (names, binds);
        }
        return ([], []);
    }

    /// <summary>A length as the engine packs it: six bits, and more in the next bytes while a bit says so. (-1, at) when it runs out.</summary>
    private static (int Count, int Next) PackedCount(byte[] data, int at)
    {
        if (at >= data.Length) return (-1, at);
        int count = data[at] & 0x3F;
        bool more = (data[at++] & 0x40) != 0;
        for (int shift = 6; more; shift += 7)
        {
            if (at >= data.Length || shift > 27) return (-1, at);
            count |= (data[at] & 0x7F) << shift;
            more = (data[at++] & 0x80) != 0;
        }
        return (count, at);
    }

    private const int ChunkSize = 37;

    private static List<MeshChunk> ReadCooked(byte[] info, byte[] buffer, RedObject cooked, RedObject mesh)
    {
        var scale = cooked.Vector("quantizationScale", Vector3.One);
        var offset = cooked.Vector("quantizationOffset");
        long indexBase = cooked.Integer("indexBufferOffset");
        var packed = mesh.Array("chunks").OfType<RedObject>().ToList();

        // the count is written as the engine writes lengths: six bits, and more in the next byte when bit 6 is set
        int count = info[0] & 0x3F, start = 1;
        if ((info[0] & 0x40) != 0)
            for (int shift = 6; start < info.Length; shift += 7)
            {
                count |= (info[start] & 0x7F) << shift;
                if ((info[start++] & 0x80) == 0) break;
            }
        if (start + (long)count * ChunkSize > info.Length) throw new InvalidDataException("The mesh's chunk table is shorter than it says.");
        var chunks = new List<MeshChunk>(count);
        for (int c = 0; c < count; c++)
        {
            var s = info.AsSpan(start + c * ChunkSize, ChunkSize);
            int vertexType = s[0];
            uint positionsAt = U32(s, 1), uvsAt = U32(s, 5), normalsAt = U32(s, 9);
            uint indicesAt = U32(s, 22), colorsAt = U32(s, 13);
            int vertices = BinaryPrimitives.ReadUInt16LittleEndian(s[27..]);
            int indices = (int)U32(s, 29);
            int material = s[33], renderMask = s[34], lodMask = s[36];

            // vertex type 2 is a static mesh, 8 bytes a position; 3 is skinned, with four bone indices and four weights after each
            int stride = vertexType == 3 ? 16 : 8;
            if (vertexType == 3 && vertices > 0 && uvsAt > positionsAt && (uvsAt - positionsAt) / vertices == 24) stride = 24;

            var positions = new Vector3[vertices];
            var uvs = new Vector2[vertices];
            var normals = new Vector3[vertices];
            var tangents = Array.Empty<Vector4>();
            // a skinned vertex has its bones' numbers, then their weights as bytes, after its 8 bytes of position
            int bones = vertexType == 3 ? (stride - 8) / 2 : 0;
            var boneIndices = new byte[vertices * bones];
            var boneWeights = new float[vertices * bones];
            Need(buffer, positionsAt, (long)vertices * stride);
            for (int v = 0; v < vertices; v++)
            {
                var p = buffer.AsSpan((int)(positionsAt + v * stride));
                positions[v] = new Vector3(
                    BinaryPrimitives.ReadUInt16LittleEndian(p) / 65535f,
                    BinaryPrimitives.ReadUInt16LittleEndian(p[2..]) / 65535f,
                    BinaryPrimitives.ReadUInt16LittleEndian(p[4..]) / 65535f) * scale + offset;
                for (int b = 0; b < bones; b++)
                {
                    boneIndices[v * bones + b] = p[8 + b];
                    boneWeights[v * bones + b] = p[8 + bones + b] / 255f;
                }
            }
            if (uvsAt != positionsAt)
            {
                Need(buffer, uvsAt, vertices * 4L);
                for (int v = 0; v < vertices; v++)
                {
                    var u = buffer.AsSpan((int)(uvsAt + v * 4));
                    uvs[v] = new Vector2((float)BinaryPrimitives.ReadHalfLittleEndian(u), (float)BinaryPrimitives.ReadHalfLittleEndian(u[2..]));
                }
            }
            if (normalsAt > 0 && normalsAt + vertices * 8L <= buffer.Length)
            {
                for (int v = 0; v < vertices; v++)
                {
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan((int)(normalsAt + v * 8)));
                    normals[v] = Vector3.Normalize(new Vector3(Ten(n), Ten(n >> 10), Ten(n >> 20)));
                }
                tangents = new Vector4[vertices];
                for (int v = 0; v < vertices; v++)
                {
                    uint t = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan((int)(normalsAt + v * 8 + 4)));
                    var tangent = new Vector3(Ten(t), Ten(t >> 10), Ten(t >> 20));
                    tangent = tangent.LengthSquared() > 1e-8f ? Vector3.Normalize(tangent) : Vector3.UnitX;
                    tangents[v] = new Vector4(tangent, t >> 30 == 3 ? 1 : -1);
                }
            }

            // after the normals, a vertex's colour (red, green, blue and a byte the engine uses otherwise) and 4 more bytes
            var colors = colorsAt > 0 && colorsAt + vertices * 8L <= buffer.Length ? new byte[vertices * 3] : [];
            for (int v = 0; v < colors.Length / 3; v++)
                buffer.AsSpan((int)(colorsAt + v * 8), 3).CopyTo(colors.AsSpan(v * 3));

            long trianglesAt = indexBase + indicesAt;
            Need(buffer, trianglesAt, indices * 2L);
            var triangles = new int[indices - indices % 3];
            for (int i = 0; i < triangles.Length; i++)
            {
                int index = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan((int)(trianglesAt + i * 2)));
                triangles[i] = index < vertices ? index : 0;
            }

            // the uncooked description says what the cooked one doesn't spell out: whether the chunk is skinned
            bool skinned = vertexType == 3 || (c < packed.Count && packed[c]["vertexType"] is RedEnum e && e.Values.Contains("MVT_SkinnedMesh"));
            chunks.Add(new MeshChunk
            {
                Index = c,
                Material = material,
                LodMask = lodMask == 0 ? 1 : lodMask,
                RenderMask = renderMask,
                Skinned = skinned,
                Positions = positions,
                Normals = normals,
                Uvs = uvs,
                Tangents = tangents,
                Indices = triangles,
                BonesPerVertex = bones,
                BoneIndices = boneIndices,
                BoneWeights = boneWeights,
                Colors = colors,
            });
        }
        return chunks;
    }

    /// <summary>
    /// A mesh as REDkit keeps it before it's cooked (the loose files in r4data): every vertex in full, 152 bytes of floats
    /// (position, bone indices and weights, normal, colour, two UVs, tangent and more), and 16-bit triangles, both in
    /// buffers inside the file, with the <c>chunks</c> list saying which belong to which chunk. The tangent is at byte
    /// 64 and the binormal at 76 (checked against tangents worked out from the UVs: the same on 99% of vertices). Its LODs, when it has
    /// some, list their chunks in <c>lodLevelInfo</c>.
    /// </summary>
    private static List<MeshChunk> ReadUncooked(RedObject mesh, byte[] vertexData, byte[] indexData, List<float> lods)
    {
        var packed = mesh.Array("chunks").OfType<RedObject>().ToList();
        long total = packed.Sum(c => c.Integer("numVertices"));
        if (total == 0) return [];
        int stride = (int)(vertexData.Length / total);
        if (stride < 56 || vertexData.Length % total != 0) stride = 152;

        // which LODs each chunk is drawn in; without a list of LODs, every chunk is in the only one
        var lodMasks = new int[packed.Count];
        var levels = mesh.Array("lodLevelInfo").OfType<RedObject>().ToList();
        for (int lod = 0; lod < levels.Count && lod < 31; lod++)
        {
            lods.Add(levels[lod].Float("distance", levels[lod].Struct("meshLODProperties")?.Float("distance") ?? 0));
            foreach (var index in levels[lod].Array("chunks"))
                if (index is IConvertible c && Convert.ToInt32(c) is int n && n >= 0 && n < packed.Count) lodMasks[n] |= 1 << lod;
        }

        var chunks = new List<MeshChunk>(packed.Count);
        for (int c = 0; c < packed.Count; c++)
        {
            var info = packed[c];
            int vertices = (int)info.Integer("numVertices"), first = (int)info.Integer("firstVertex");
            int indices = (int)info.Integer("numIndices"), firstIndex = (int)info.Integer("firstIndex");
            Need(vertexData, (long)first * stride, (long)vertices * stride);
            Need(indexData, firstIndex * 2L, indices * 2L);
            var positions = new Vector3[vertices];
            var normals = new Vector3[vertices];
            var uvs = new Vector2[vertices];
            var tangents = stride >= 88 ? new Vector4[vertices] : [];
            bool skinned = info["vertexType"] is RedEnum t && t.Values.Contains("MVT_SkinnedMesh");
            // the bones' numbers as four bytes after the position, their weights as four floats after those
            var boneIndices = new byte[skinned ? vertices * 4 : 0];
            var boneWeights = new float[skinned ? vertices * 4 : 0];
            for (int v = 0; v < vertices; v++)
            {
                var s = vertexData.AsSpan((first + v) * stride, stride);
                positions[v] = new Vector3(F(s, 0), F(s, 4), F(s, 8));
                if (skinned)
                    for (int b = 0; b < 4; b++)
                    {
                        boneIndices[v * 4 + b] = s[12 + b];
                        boneWeights[v * 4 + b] = F(s, 16 + b * 4);
                    }
                var normal = new Vector3(F(s, 32), F(s, 36), F(s, 40));
                normals[v] = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                uvs[v] = new Vector2(F(s, 48), F(s, 52));
                if (tangents.Length > 0)
                {
                    var tangent = new Vector3(F(s, 64), F(s, 68), F(s, 72));
                    var binormal = new Vector3(F(s, 76), F(s, 80), F(s, 84));
                    tangent = tangent.LengthSquared() > 1e-8f ? Vector3.Normalize(tangent) : Vector3.UnitX;
                    tangents[v] = new Vector4(tangent, Vector3.Dot(Vector3.Cross(normals[v], tangent), binormal) >= 0 ? 1 : -1);
                }
            }
            var triangles = new int[indices - indices % 3];
            for (int i = 0; i < triangles.Length; i++)
            {
                int index = BinaryPrimitives.ReadUInt16LittleEndian(indexData.AsSpan((firstIndex + i) * 2));
                triangles[i] = index < vertices ? index : 0;
            }
            chunks.Add(new MeshChunk
            {
                Index = c,
                Material = (int)info.Integer("materialID"),
                LodMask = levels.Count > 0 && lodMasks[c] != 0 ? lodMasks[c] : 1,
                RenderMask = 1,
                Skinned = skinned,
                Positions = positions,
                Normals = normals,
                Uvs = uvs,
                Tangents = tangents,
                Indices = triangles,
                BonesPerVertex = skinned ? 4 : 0,
                BoneIndices = boneIndices,
                BoneWeights = boneWeights,
            });
        }
        return chunks;
    }

    private static float F(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);

    /// <summary>A 10-bit number from 0 to 1023 as -1 to 1.</summary>
    private static float Ten(uint bits) => (bits & 0x3FF) / 1023f * 2 - 1;

    private static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);

    private static void Need(byte[] buffer, long at, long size)
    {
        if (at < 0 || size < 0 || at + size > buffer.Length) throw new InvalidDataException("The mesh's buffer is shorter than its chunks say.");
    }
}
