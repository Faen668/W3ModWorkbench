using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Packing;

/// <summary>
/// Meshes: a REDkit source mesh (<c>.w2mesh</c>, its vertices whole in buffers inside the file) cooked into the game's
/// mesh and its <c>.1.buffer</c>, written by <see cref="MeshWriter"/>.
/// <para>
/// A source vertex is 152 bytes: position (three floats), its bones' numbers (four bytes) and weights (four floats),
/// the normal, a colour (four bytes), two UV sets, the tangent and the binormal (three floats each), then unused space.
/// The source's chunk list says which vertices and triangles belong to each chunk, and the data after its properties
/// lists the chunks of each LOD as a cooked mesh's does.
/// </para>
/// <para>
/// A cooked chunk is drawn in the scene and both shadow cascades (or only the scene, if its source chunk asks for that).
/// A skinned chunk, or one whose material is masked (its alpha cuts holes), draws its own shadows; the other static
/// chunks of a LOD are drawn into its shadows merged, through two chunks only for shadows, one for each cascade.
/// The mesh loses what only REDkit uses (where it was imported from, the names of its materials there) and its
/// collision, which the game keeps in <c>collision.cache</c>, not in the mesh.
/// </para>
/// </summary>
public static partial class ResourceCooker
{
    private const int SourceStride = 152;

    /// <summary>The mesh's properties only REDkit uses.</summary>
    private static readonly string[] EditorMeshFields =
        ["importFile", "importFileTimeStamp", "materialNames", "authorName", "baseResourceFilePath", "rawVertices", "rawIndices", "chunksBuffer",
         "collisionMesh", "navigationObstacle"];

    /// <summary>Whether a file is a mesh this cooks.</summary>
    public static bool IsMesh(string gamePath) => gamePath.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A REDkit source mesh cooked (the mesh and its first buffer), or null when it's cooked already.
    /// <paramref name="readFile"/> reads another file by its game path, the project's first (the materials, to tell
    /// which are masked). <paramref name="note"/> says what the cooked mesh hasn't got that the source had.
    /// </summary>
    /// <exception cref="InvalidDataException">It can't be cooked, or can't be read.</exception>
    public static CookedMesh? CookMesh(string gamePath, byte[] source, Func<string, byte[]?> readFile, out string? note)
    {
        note = null;
        var file = Cr2wFile.Read(source);
        if (TextureCooker.IsCooked(file)) return null;
        if (file.Exports.Count == 0 || file.Exports[0].ClassName != "CMesh") throw new InvalidDataException(L.T("ResourceCooker.NotPlainMesh"));
        var mesh = file.First("CMesh")!;
        var packed = mesh.Array("chunks").OfType<RedObject>().ToList();
        var vertexData = mesh["rawVertices"] is RedBufferRef v && v.Index > 0 ? file.Buffer(v.Index, _ => null) : null;
        var indexData = mesh["rawIndices"] is RedBufferRef i && i.Index > 0 ? file.Buffer(i.Index, _ => null) : null;
        if (vertexData is null || indexData is null || packed.Count == 0) throw new InvalidDataException(L.T("ResourceCooker.MeshHasNoVertices"));
        if (vertexData.Length != packed.Sum(c => c.Integer("numVertices")) * SourceStride) throw new InvalidDataException(L.T("ResourceCooker.MeshVerticesUnknown"));
        var bones = MeshFile.Read(source, _ => null);

        var json = Upgraded(file);
        var objects = (JsonArray)json["objects"]!;
        var meshNode = (JsonObject)objects[0]!;
        if (meshNode["collisionMesh"] is not null) note = L.T("ResourceCooker.MeshCollisionLeftOut");
        bool extra = mesh["useExtraStreams"] is true;
        var lodMasks = LodMasks(mesh.Tail, packed.Count);
        var masked = MaskedMaterials(meshNode, objects, json, readFile);

        // the chunks as the writer takes them
        var chunks = new List<MeshChunk>();
        for (int c = 0; c < packed.Count; c++)
            chunks.Add(SourceChunk(packed[c], c, vertexData, indexData, lodMasks[c], extra, masked));
        // positions are stored in the box the source keeps
        var srcBox = mesh.Struct("boundingBox");
        var boxMin = srcBox?.Vector("Min") is { } bmin ? new Vector3(bmin.X, bmin.Y, bmin.Z) : Vector3.Zero;
        var boxMax = srcBox?.Vector("Max") is { } bmax ? new Vector3(bmax.X, bmax.Y, bmax.Z) : Vector3.Zero;
        var quantization = (boxMin, boxMax - boxMin);
        chunks = Snapped(chunks, boxMin, boxMax - boxMin);
        var shadows = new List<MeshChunk>();
        var merged = chunks.Where(c => c.Positions.Length > 0 && c.Indices.Length > 0 && (c.RenderMask & 6) != 0 && (c.CookedInfo[35] & 6) == 0).ToList();
        foreach (int lod in merged.Select(c => c.LodMask).Distinct().Order())
            foreach (byte cascade in new byte[] { 2, 4 })
            {
                var d = new byte[37];
                // the material of the first chunk merged into it
                d[33] = (byte)merged.First(c => c.LodMask == lod).Material;
                d[0] = 2;
                d[21] = 1;
                d[26] = 0x1D;
                d[35] = cascade;
                d[36] = (byte)lod;
                shadows.Add(new MeshChunk { Index = -1, Material = 0, LodMask = lod, RenderMask = 0, Skinned = false, Positions = [], Normals = [], Uvs = [], Indices = [], CookedInfo = d });
            }

        // the mesh without its geometry, as the game keeps one: the writer fills it in
        var packedChunks = meshNode["chunks"]?.DeepClone() as JsonArray;
        int collision = ExportIndex(meshNode["collisionMesh"]);
        foreach (string field in EditorMeshFields) meshNode.Remove(field);
        if (meshNode["isOccluder"] is JsonValue occluder && occluder.TryGetValue<bool>(out bool o) && !o) meshNode.Remove("isOccluder");
        meshNode["chunks"] = new JsonArray();
        var cooked = new JsonObject();
        // where the collision starts: nothing here, as for a mesh without collision (its collision isn't cooked)
        cooked["collisionInitPositionOffset"] = Vector4Json(Vector4.Zero);
        if (bones.BoneNames.Length > 0) cooked["bonePositions"] = new JsonArray();
        cooked["renderLODs"] = new JsonArray([.. LodDistances(mesh.Tail).Select(d => (JsonNode?)(double)d)]);
        cooked["renderChunks"] = "";
        cooked["renderBuffer"] = 1;
        cooked["quantizationScale"] = Vector4Json(Vector4.Zero);
        cooked["quantizationOffset"] = Vector4Json(new Vector4(0, 0, 0, 1));
        cooked["vertexBufferSize"] = 0;
        cooked["indexBufferSize"] = 0;
        cooked["indexBufferOffset"] = 0;
        cooked["blasBuffer"] = 0;
        meshNode["cookedData"] = cooked;
        if (meshNode["boundingBox"]?["Min"] is JsonObject min) min["W"] = 1;
        if (collision > 0) RemoveObjects(json, collision);

        var fileBlock = (JsonObject)json["file"]!;
        fileBlock["buffers"] = new JsonArray(new JsonObject { ["index"] = 1, ["flags"] = 0, ["diskSize"] = 0, ["memorySize"] = 0, ["crc"] = 0 });
        // through text, so every value reads as the JSON the converter takes would have it
        json = (JsonObject)JsonNode.Parse(json.ToJsonString())!;
        Finish(json, cooked: true);
        var template = MeshFile.Read(Cr2wJson.FromJson(json), _ => null);
        var result = MeshWriter.Write(template, chunks, bones.BoneNames, bones.BoneInverseBinds, shadows, packedChunks, quantization);
        var written = (JsonObject)JsonNode.Parse(Cr2wJson.ToJson(Cr2wFile.Read(result.File), out _).ToJsonString())!;
        var writtenMesh = (JsonObject)((JsonArray)written["objects"]!)[0]!;
        // the source's own data after the properties (its LODs, bones, how far each reaches and which the vertices use),
        // which the cook keeps as it is, its bones' names numbered into this file's names
        var writtenNames = ((JsonArray)written["file"]!["names"]!).Select(n => (string?)n ?? "").ToList();
        byte[] tail = (byte[])mesh.Tail.Clone();
        var (boneAt, boneCount) = BoneNamesIn(tail);
        for (int b = 0; b < boneCount; b++)
        {
            string name = file.Names[BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(boneAt + b * 2))];
            int index = writtenNames.IndexOf(name);
            if (index < 0) throw new InvalidDataException(L.T("ResourceCooker.MeshVerticesUnknown"));
            BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(boneAt + b * 2), (ushort)index);
        }
        writtenMesh["$tail"] = Convert.ToBase64String(tail);
        // where each bone is, from its bind as a rigid move (minus the move turned back), as the cook works it out
        if (bones.BoneInverseBinds.Length > 0 && writtenMesh["cookedData"] is JsonObject writtenCooked)
            writtenCooked["bonePositions"] = new JsonArray([.. bones.BoneInverseBinds.Select(m => (JsonNode?)Vector4Json(new Vector4(
                -(m.M41 * m.M11 + m.M42 * m.M12 + m.M43 * m.M13),
                -(m.M41 * m.M21 + m.M42 * m.M22 + m.M43 * m.M23),
                -(m.M41 * m.M31 + m.M42 * m.M32 + m.M43 * m.M33), 1)))]);
        return result with { File = WriteMesh(written) };
    }

    /// <summary>A source chunk's vertices and triangles, and the cooked chunk's description.</summary>
    private static MeshChunk SourceChunk(RedObject info, int index, byte[] vertexData, byte[] indexData, int lodMask, bool extra, HashSet<int> masked)
    {
        int count = (int)info.Integer("numVertices"), first = (int)info.Integer("firstVertex");
        int indices = (int)info.Integer("numIndices"), firstIndex = (int)info.Integer("firstIndex");
        int material = (int)info.Integer("materialID");
        bool skinned = info["vertexType"] is RedEnum t && t.Values.Contains("MVT_SkinnedMesh");
        int per = skinned ? Math.Max(1, (int)info.Integer("numBonesPerVertex", 4)) : 0;
        if ((long)(first + count) * SourceStride > vertexData.Length || (firstIndex + (long)indices) * 2 > indexData.Length)
            throw new InvalidDataException(L.T("ResourceCooker.MeshVerticesUnknown"));
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        var tangents = new Vector4[count];
        var boneIndices = new byte[count * per];
        var boneWeights = new float[count * per];
        var extraStream = extra ? new byte[count * 8] : [];
        for (int v = 0; v < count; v++)
        {
            int start = (first + v) * SourceStride;
            var s = vertexData.AsSpan(start, SourceStride);
            float F(int at) => BinaryPrimitives.ReadSingleLittleEndian(vertexData.AsSpan(start + at));
            positions[v] = new Vector3(F(0), F(4), F(8));
            for (int b = 0; b < per && b < 4; b++)
            {
                boneIndices[v * per + b] = s[12 + b];
                boneWeights[v * per + b] = F(16 + b * 4);
            }
            normals[v] = new Vector3(F(32), F(36), F(40));
            uvs[v] = new Vector2(F(48), F(52));
            var tangent = new Vector3(F(64), F(68), F(72));
            var binormal = new Vector3(F(76), F(80), F(84));
            // left-handed only when the binormal points against normal × tangent (a missing tangent, NaN, isn't)
            tangents[v] = new Vector4(tangent, Vector3.Dot(Vector3.Cross(normals[v], tangent), binormal) < 0 ? -1 : 1);
            if (extra)
            {
                // the colour, then the second UV set as two half floats
                s.Slice(44, 4).CopyTo(extraStream.AsSpan(v * 8));
                BinaryPrimitives.WriteUInt16LittleEndian(extraStream.AsSpan(v * 8 + 4), HalfBits(F(56)));
                BinaryPrimitives.WriteUInt16LittleEndian(extraStream.AsSpan(v * 8 + 6), HalfBits(F(60)));
            }
        }
        var triangles = new int[indices - indices % 3];
        for (int k = 0; k < triangles.Length; k++) triangles[k] = BinaryPrimitives.ReadUInt16LittleEndian(indexData.AsSpan((firstIndex + k) * 2));

        bool sceneOnly = info["renderMask"] is RedEnum r && r.Values.Count == 1 && r.Values[0] == "MCR_Scene";
        var d = new byte[37];
        d[0] = (byte)(skinned ? 3 : 2);
        d[5] = 1;
        d[9] = 1;
        if (extra) d[13] = 1;
        d[21] = (byte)(skinned ? extra ? 5 : 4 : extra ? 3 : 2);
        d[26] = 0x1D;
        d[35] = (byte)(sceneOnly ? 1 : skinned || masked.Contains(material) ? 0x27 : 0x21);
        return new MeshChunk
        {
            Index = index, Material = material, LodMask = lodMask, RenderMask = sceneOnly ? 1 : 0x27, Skinned = skinned,
            Positions = positions, Normals = normals, Uvs = uvs, Tangents = tangents, Indices = triangles,
            BonesPerVertex = per, BoneIndices = boneIndices, BoneWeights = boneWeights, ExtraStream = extraStream, CookedInfo = d,
        };
    }

    /// <summary>
    /// The chunks with every value put on the step the game's cook stores it as, which cuts rather than rounds:
    /// a position's 16-bit step in the box it's stored in (its offset from the box's least, times 1 over the box's
    /// size, times 65535), a normal's or tangent's 10-bit step ((value + 1) / 2 × 1023), a bone weight's byte (× 255)
    /// and a UV's half float with its extra bits cut off. The writer, which rounds, then stores exactly those steps,
    /// and the mesh's bounding box is the one round the positions as they're stored.
    /// </summary>
    private static List<MeshChunk> Snapped(List<MeshChunk> chunks, Vector3 min, Vector3 size)
    {
        static float Step(float value, float least, float size)
        {
            if (size <= 0) return least;
            float step = Math.Clamp(MathF.Floor((value - least) * (1f / size) * 65535f), 0, 65535);
            return least + step / 65535f * size;
        }
        static float Ten(float f) => float.IsNaN(f) ? -1 : Math.Clamp((int)((f + 1) / 2 * 1023), 0, 1023) / 1023f * 2 - 1;
        var result = new List<MeshChunk>(chunks.Count);
        foreach (var c in chunks)
            result.Add(new MeshChunk
            {
                Index = c.Index, Material = c.Material, LodMask = c.LodMask, RenderMask = c.RenderMask, Skinned = c.Skinned,
                Positions = [.. c.Positions.Select(p => new Vector3(Step(p.X, min.X, size.X), Step(p.Y, min.Y, size.Y), Step(p.Z, min.Z, size.Z)))],
                Normals = [.. c.Normals.Select(n => new Vector3(Ten(n.X), Ten(n.Y), Ten(n.Z)))],
                Tangents = [.. c.Tangents.Select(t => new Vector4(Ten(t.X), Ten(t.Y), Ten(t.Z), t.W))],
                Uvs = [.. c.Uvs.Select(uv => new Vector2(HalfCut(uv.X), HalfCut(uv.Y)))],
                Indices = c.Indices, BonesPerVertex = c.BonesPerVertex, BoneIndices = c.BoneIndices,
                BoneWeights = [.. c.BoneWeights.Select(w => (int)(w * 255) / 255f)],
                ExtraStream = c.ExtraStream, CookedInfo = c.CookedInfo,
            });
        return result;
    }

    /// <summary>A float as a half float with the bits that don't fit cut off (not rounded), as the game's cook makes UVs.</summary>
    private static float HalfCut(float f) => (float)BitConverter.UInt16BitsToHalf(HalfBits(f));

    private static ushort HalfBits(float f)
    {
        uint bits = BitConverter.SingleToUInt32Bits(f);
        uint sign = (bits >> 16) & 0x8000;
        int rawExponent = (int)((bits >> 23) & 0xFF);
        int exponent = rawExponent - 127 + 15;
        uint mantissa = bits & 0x7FFFFF;
        if (rawExponent == 0) return (ushort)sign;
        if (exponent >= 31) return (ushort)(sign | 0x7C00);
        if (exponent <= 0) return exponent < -10 ? (ushort)sign : (ushort)(sign | ((mantissa | 0x800000) >> (14 - exponent)));
        return (ushort)(sign | (uint)(exponent << 10) | (mantissa >> 13));
    }

    /// <summary>Each chunk's LODs (a bit each), from the LOD list after the mesh's properties; a chunk no LOD lists is in the first.</summary>
    private static int[] LodMasks(byte[] tail, int chunks)
    {
        var masks = new int[chunks];
        int at = 0;
        int lods = PackedInt.Read(tail, ref at);
        for (int l = 0; l < lods && l < 31; l++)
        {
            int count = PackedInt.Read(tail, ref at);
            for (int k = 0; k < count; k++, at += 2)
                if (BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(at)) is var c && c < chunks) masks[c] |= 1 << l;
            at += 4;
        }
        for (int c = 0; c < chunks; c++) if (masks[c] == 0) masks[c] = 1;
        return masks;
    }

    private static List<float> LodDistances(byte[] tail)
    {
        var distances = new List<float>();
        int at = 0;
        int lods = PackedInt.Read(tail, ref at);
        for (int l = 0; l < lods; l++)
        {
            int count = PackedInt.Read(tail, ref at);
            at += count * 2;
            distances.Add(BinaryPrimitives.ReadSingleLittleEndian(tail.AsSpan(at)));
            at += 4;
        }
        if (distances.Count == 0) distances.Add(0);
        return distances;
    }

    /// <summary>
    /// The mesh's material slots whose material is masked or blended (it draws its own shadows): a material instance
    /// that turns its mask on, or one whose graph is masked and won't let an instance say otherwise, or blends; a graph
    /// used straight that is masked or blends.
    /// </summary>
    private static HashSet<int> MaskedMaterials(JsonObject mesh, JsonArray objects, JsonObject json, Func<string, byte[]?> readFile)
    {
        var masked = new HashSet<int>();
        var slots = mesh["materials"] as JsonArray ?? [];
        for (int i = 0; i < slots.Count; i++)
        {
            bool? Of(JsonNode? reference, int depth)
            {
                if (depth > 8 || reference is not JsonValue value || !value.TryGetValue<string>(out var r)) return null;
                if (r.StartsWith("export:", StringComparison.Ordinal) && int.TryParse(r.AsSpan(7), out int e) && e < objects.Count && objects[e] is JsonObject o)
                    return Material(o, depth);
                if (!r.StartsWith("import:", StringComparison.Ordinal) || readFile(r[7..]) is not { } bytes) return null;
                try
                {
                    var file = Cr2wJson.ToJson(Cr2wFile.Read(bytes), out _);
                    return (file["objects"] as JsonArray)?.FirstOrDefault() is JsonObject root ? Material(root, depth) : null;
                }
                catch (InvalidDataException) { return null; }
            }
            bool? Material(JsonObject o, int depth)
            {
                if ((string?)o["$class"] == "CMaterialInstance")
                {
                    if (o["enableMask"] is JsonValue m && m.TryGetValue<bool>(out bool on) && on) return true;
                    return Of(o["baseMaterial"], depth + 1) is true && (!Overridable(o["baseMaterial"], depth + 1) || Blended(o["baseMaterial"]));
                }
                return o["isMasked"] is JsonValue g && g.TryGetValue<bool>(out bool isMasked) && isMasked
                    || o["blendMode"] is JsonValue b && b.TryGetValue<string>(out var mode) && mode != "RBM_None";
            }
            bool Blended(JsonNode? reference) =>
                reference is JsonValue value && value.TryGetValue<string>(out var r) && r.StartsWith("import:", StringComparison.Ordinal)
                && readFile(r[7..]) is { } bytes && Cr2wFile.Read(bytes).First("CMaterialGraph")?["blendMode"] is RedEnum { Values: [var mode, ..] } && mode != "RBM_None";
            bool Overridable(JsonNode? reference, int depth)
            {
                if (reference is not JsonValue value || !value.TryGetValue<string>(out var r) || !r.StartsWith("import:", StringComparison.Ordinal) || readFile(r[7..]) is not { } bytes)
                    return false;
                try { return Cr2wFile.Read(bytes).Exports.Count > 0 && Cr2wFile.Read(bytes).First("CMaterialGraph")?["canOverrideMasked"] is true; }
                catch (InvalidDataException) { return false; }
            }
            if (Of(slots[i], 0) is true) masked.Add(i);
        }
        return masked;
    }

    /// <summary>Removes an object and those under it, renumbering the references to those after.</summary>
    private static void RemoveObjects(JsonObject json, int first)
    {
        var objects = (JsonArray)json["objects"]!;
        var gone = new HashSet<int> { first };
        for (int i = first + 1; i < objects.Count; i++)
            if (objects[i] is JsonObject o && o["$parent"] is { } p && gone.Contains((int)p!)) gone.Add(i);
        var renumber = new Dictionary<int, int>();
        for (int i = 0, at = 0; i < objects.Count; i++)
            if (!gone.Contains(i)) renumber[i] = at++;
        var kept = new JsonArray();
        for (int i = 0; i < objects.Count; i++)
        {
            if (gone.Contains(i) || objects[i] is not JsonObject o) continue;
            var copy = (JsonObject)o.DeepClone();
            if (copy["$parent"] is { } p) copy["$parent"] = renumber[(int)p!];
            Renumber(copy, renumber);
            kept.Add(copy);
        }
        json["objects"] = kept;
    }

    private static JsonObject Vector4Json(Vector4 v) => new() { ["X"] = v.X, ["Y"] = v.Y, ["Z"] = v.Z, ["W"] = v.W };

    /// <summary>
    /// A cooked mesh written with its names in the order the game's cook writes them (<see cref="FirstUse"/>), its
    /// bones' names, which it keeps after its properties as numbers into the names, coming after the mesh's own.
    /// </summary>
    private static byte[] WriteMesh(JsonObject json)
    {
        var meshNode = (JsonObject)((JsonArray)json["objects"]!)[0]!;
        var file = (JsonObject)json["file"]!;
        var oldNames = ((JsonArray)file["names"]!).Select(n => (string?)n ?? "").ToList();
        byte[] tail = Convert.FromBase64String((string)meshNode["$tail"]!);
        var (boneAt, boneCount) = BoneNamesIn(tail);
        var boneNames = Enumerable.Range(0, boneCount).Select(b => oldNames[BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(boneAt + b * 2))]).ToList();
        byte[] written = Write(json, boneNames);
        // the bones' names as numbers into the names as they're now
        var names = ((JsonArray)Cr2wJson.ToJson(Cr2wFile.Read(written), out _)["file"]!["names"]!).Select(n => (string)n!).ToList();
        for (int b = 0; b < boneCount; b++) BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(boneAt + b * 2), (ushort)names.IndexOf(boneNames[b]));
        meshNode["$tail"] = Convert.ToBase64String(tail);
        file["names"] = new JsonArray([.. names.Select(n => (JsonNode?)n)]);
        return Cr2wJson.FromJson(json);
    }

    /// <summary>Where a mesh's bone names start in the data after its properties (after its LODs), and how many there are.</summary>
    private static (int At, int Count) BoneNamesIn(byte[] tail)
    {
        int at = 0;
        int lods = PackedInt.Read(tail, ref at);
        for (int l = 0; l < lods; l++)
        {
            int count = PackedInt.Read(tail, ref at);
            at += count * 2 + 4;
        }
        int bones = PackedInt.Read(tail, ref at);
        return (at, bones);
    }
}
