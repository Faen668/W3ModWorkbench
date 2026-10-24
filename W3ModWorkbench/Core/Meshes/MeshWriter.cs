using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>A cooked mesh as files: the .w2mesh and its first buffer (<c>name.w2mesh.1.buffer</c>).</summary>
public sealed record CookedMesh(byte[] File, byte[] Buffer);

/// <summary>
/// Writes a cooked mesh (<c>.w2mesh</c> and its <c>.1.buffer</c>) with new geometry, on a template: a mesh of the game
/// whose every property the new one keeps (its materials, LOD distances, collision, how far away it hides…) but for those
/// that say what its geometry is. Rebuilding a game mesh from what <see cref="MeshFile"/> reads of it gives the game's
/// bytes back.
/// <para>
/// The buffer has every chunk's streams one after another, each starting on 16 bytes: positions (three 16-bit numbers
/// scaled into the mesh's box and FFFF, then for a skinned chunk its bones' numbers and weights as bytes), UVs (two half
/// floats), the normal and tangent (three 10-bit numbers each, the normal's top two bits 01 and the tangent's 11 for a
/// right-handed tangent frame, 00 for a left-handed one) and, when the chunk has it, its extra stream (colour and more).
/// Chunks only for shadows come after the others: one for each LOD that has them, the positions of every chunk of the
/// LOD that casts shadows but isn't drawn in them by itself, merged, which two descriptions (two shadow cascades) share. A stream with the same bytes as one before it
/// isn't written again: the descriptions share it. The triangles follow, 16-bit, from the vertices'
/// end rounded up to 1,024 bytes.
/// </para>
/// <para>
/// After the mesh's properties come its LODs (each one's chunks and distance), its bones' names and the matrices that
/// take the mesh into each bone's space as it was bound, how far each bone's vertices reach from it, and the bones the
/// vertices use in the order they're first used.
/// The engine writes a count there as six bits and more bytes while bit 6 says so, and an empty list as 80.
/// </para>
/// </summary>
public static class MeshWriter
{
    private const int DescriptionSize = 37;

    /// <param name="template">The mesh to keep everything else of. It must be cooked.</param>
    /// <param name="chunks">
    /// The chunks to draw, in order, each with its LODs, material, render mask and the template chunk's
    /// <see cref="MeshChunk.CookedInfo"/> it's written as (copied from the chunk it replaces). The template's chunks only
    /// for shadows are made again from them.
    /// </param>
    /// <param name="boneNames">The bones the chunks' vertices name, in order; the template's for the same bones.</param>
    /// <param name="inverseBinds">For each bone, the matrix that takes the mesh into the bone's space as it was bound.</param>
    public static CookedMesh Write(MeshFile template, IReadOnlyList<MeshChunk> chunks, string[] boneNames, Matrix4x4[] inverseBinds)
    {
        var visible = chunks.Where(c => c.Positions.Length > 0 && c.Indices.Length > 0).ToList();
        if (visible.Count == 0) throw new InvalidDataException(L.T("MeshWriter.NoChunks"));
        if (visible.FirstOrDefault(c => c.Positions.Length > ushort.MaxValue) is { } big)
            throw new InvalidDataException(L.T("MeshWriter.TooManyVertices", big.Index, big.Positions.Length));
        if (visible.FirstOrDefault(c => c.CookedInfo.Length != DescriptionSize) is { } bare)
            throw new InvalidDataException(L.T("MeshWriter.NoDescription", bare.Index));

        var json = Cr2wJson.ToJson(template.File, out _);
        var mesh = (JsonObject)json["objects"]![0]!;
        var cooked = (JsonObject)mesh["cookedData"]!;

        // the box everything is scaled into
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var chunk in visible)
            foreach (var p in chunk.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        var scale = max - min;

        // the template's chunks only for shadows, made again from the new chunks of their LODs
        var shadows = template.Chunks.Where(c => c.IsShadowOnly).ToList();

        // a stream with the same bytes as one already written is written once, and both descriptions point at it
        var buffer = new MemoryStream();
        var written = new Dictionary<byte[], uint>(ByteComparer.Instance);
        uint Stream(byte[] data)
        {
            if (written.TryGetValue(data, out uint at)) return at;
            while (buffer.Length % 16 != 0) buffer.WriteByte(0);
            at = (uint)buffer.Length;
            buffer.Write(data);
            return written[data] = at;
        }
        var descriptions = new List<byte[]>();
        var triangles = new List<byte[]>();
        foreach (var chunk in visible)
        {
            var d = (byte[])chunk.CookedInfo.Clone();
            int bones = d[0] == 3 ? Math.Max(4, chunk.BonesPerVertex) : 0;
            Put(d, 1, Stream(Positions(chunk, min, scale, bones)));
            Put(d, 5, U32(d, 5) != 0 ? Stream(Uvs(chunk)) : 0);
            Put(d, 9, U32(d, 9) != 0 ? Stream(NormalsAndTangents(chunk)) : 0);
            Put(d, 13, U32(d, 13) != 0 ? Stream(Extra(chunk)) : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(27), (ushort)chunk.Positions.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(29), (uint)chunk.Indices.Length);
            d[33] = (byte)chunk.Material;
            d[34] = (byte)chunk.RenderMask;
            d[36] = (byte)chunk.LodMask;
            descriptions.Add(d);
            triangles.Add(Indices(chunk.Indices));
        }
        // shadows: for each LOD, the chunks that cast shadows but not by themselves merged into one, which two
        // descriptions share. Byte 34 of a description is the passes a chunk asks to be drawn in (1 the scene, 2 and 4
        // the two shadow cascades), byte 35 those it's drawn in by itself once cooked. A LOD of one static chunk's
        // merged positions are that chunk's own, so they're shared too
        foreach (var group in shadows.GroupBy(s => s.LodMask))
        {
            var merged = visible.Where(c => (c.LodMask & group.Key) != 0 && (c.RenderMask & 6) != 0 && (c.CookedInfo[35] & 6) == 0).ToList();
            // more vertices than a chunk can number are split, between chunks, into more shadow chunks
            var pieces = new List<List<MeshChunk>>();
            foreach (var c in merged)
            {
                if (pieces.Count == 0 || pieces[^1].Sum(p => p.Positions.Length) + c.Positions.Length > ushort.MaxValue) pieces.Add([]);
                pieces[^1].Add(c);
            }
            foreach (var piece in pieces)
            {
                var indices = new List<int>();
                int first = 0;
                foreach (var c in piece)
                {
                    indices.AddRange(c.Indices.Select(i => i + first));
                    first += c.Positions.Length;
                }
                var shadow = new MeshChunk
                {
                    Index = -1, Material = 0, LodMask = group.Key, RenderMask = 0, Skinned = false,
                    Positions = [.. piece.SelectMany(c => c.Positions)], Normals = [], Uvs = [], Indices = [.. indices],
                };
                if (shadow.Positions.Length > ushort.MaxValue) continue;
                uint at = Stream(Positions(shadow, min, scale, 0));
                foreach (var s in group.Take(2))
                {
                    var d = (byte[])s.CookedInfo.Clone();
                    Put(d, 1, at);
                    BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(27), (ushort)shadow.Positions.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(29), (uint)indices.Count);
                    descriptions.Add(d);
                    triangles.Add(Indices(shadow.Indices));
                }
            }
        }

        // the triangles, from the vertices' end rounded up to 1,024 bytes, also written once each
        long vertexBytes = buffer.Length;
        long indexBase = (vertexBytes + 1023) & ~1023L;
        buffer.Write(new byte[indexBase - vertexBytes]);
        var placed = new Dictionary<byte[], uint>(ByteComparer.Instance);
        for (int i = 0; i < descriptions.Count; i++)
        {
            if (!placed.TryGetValue(triangles[i], out uint at))
            {
                placed[triangles[i]] = at = (uint)(buffer.Length - indexBase);
                buffer.Write(triangles[i]);
            }
            Put(descriptions[i], 22, at);
        }
        long indexBytes = buffer.Length - indexBase;
        var bytes = buffer.ToArray();

        // the chunk table
        var table = new MemoryStream();
        WriteCount(table, descriptions.Count);
        foreach (var d in descriptions) table.Write(d);

        cooked["renderChunks"] = Convert.ToBase64String(table.ToArray());
        // the W of each of these vectors, which the engine leaves as it comes, is kept as the template has it
        cooked["quantizationScale"] = Vector(scale, W(cooked["quantizationScale"], 0));
        cooked["quantizationOffset"] = Vector(min, W(cooked["quantizationOffset"], 1));
        cooked["vertexBufferSize"] = vertexBytes;
        cooked["indexBufferSize"] = indexBytes;
        cooked["indexBufferOffset"] = indexBase;
        // where each bone is: the template's own for a bone it has bound the same, which the engine kept from the rig
        // rather than working out from the bind as here
        if (boneNames.Length > 0)
        {
            var kept = cooked["bonePositions"] as JsonArray;
            var positions = new JsonArray();
            for (int i = 0; i < boneNames.Length; i++)
            {
                int same = Array.FindIndex(template.BoneNames, n => n.Equals(boneNames[i], StringComparison.Ordinal));
                positions.Add(same >= 0 && same < template.BoneInverseBinds.Length && template.BoneInverseBinds[same] == inverseBinds[i] && kept is not null && same < kept.Count
                    ? kept[same]!.DeepClone()
                    : Vector(Matrix4x4.Invert(inverseBinds[i], out var bound) ? bound.Translation : Vector3.Zero, 1));
            }
            cooked["bonePositions"] = positions;
        }
        else cooked.Remove("bonePositions");

        var box = mesh["boundingBox"] as JsonObject;
        mesh["boundingBox"] = new JsonObject { ["Min"] = Vector(min, W(box?["Min"], 0)), ["Max"] = Vector(max, W(box?["Max"], 1)) };
        mesh["chunks"] = PackedChunks(mesh["chunks"] as JsonArray, visible);

        var names = (JsonArray)json["file"]!["names"]!;
        mesh["$tail"] = Convert.ToBase64String(Tail(template, visible, boneNames, inverseBinds, names));

        var bufferEntry = ((JsonArray)json["file"]!["buffers"]!).OfType<JsonObject>().First(b => (int?)b["index"] == 1);
        bufferEntry["diskSize"] = bytes.Length;
        bufferEntry["memorySize"] = bytes.Length;
        bufferEntry["crc"] = Checksums.Crc32(bytes);
        return new CookedMesh(Cr2wJson.FromJson(json), bytes);
    }

    /// <summary>
    /// The uncooked list of chunks the mesh keeps beside the cooked one: each one's vertex type, material, bones per
    /// vertex, and where its vertices and triangles would start counting all the chunks before it. A chunk keeps the
    /// template's entry at its place for what it doesn't change, as the engine leaves out values it has as defaults.
    /// </summary>
    private static JsonArray PackedChunks(JsonArray? old, List<MeshChunk> visible)
    {
        var result = new JsonArray();
        long firstVertex = 0, firstIndex = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            var chunk = visible[i];
            var entry = old is not null && i < old.Count && old[i] is JsonObject kept ? (JsonObject)kept.DeepClone() : new JsonObject();
            if (old is null || i >= old.Count)
            {
                if (chunk.Skinned) entry["vertexType"] = "MVT_SkinnedMesh";
                if (chunk.Skinned && chunk.BonesPerVertex > 0) entry["numBonesPerVertex"] = chunk.BonesPerVertex;
            }
            Set(entry, "materialID", chunk.Material);
            Set(entry, "numVertices", chunk.Positions.Length);
            Set(entry, "numIndices", chunk.Indices.Length);
            Set(entry, "firstVertex", firstVertex);
            Set(entry, "firstIndex", firstIndex);
            firstVertex += chunk.Positions.Length;
            firstIndex += chunk.Indices.Length;
            result.Add(entry);
        }
        return result;

        static void Set(JsonObject o, string name, long value)
        {
            if (value == 0) o.Remove(name);
            else o[name] = value;
        }
    }

    private static byte[] Tail(MeshFile template, List<MeshChunk> visible, string[] boneNames, Matrix4x4[] inverseBinds, JsonArray names)
    {
        var old = template.Mesh.Tail;
        var ms = new MemoryStream();
        // the LODs: the template's distances, with the chunks of each
        var (lodCount, at) = ReadCount(old, 0);
        var distances = new List<float>();
        for (int l = 0; l < lodCount; l++)
        {
            var (chunks, next) = ReadCount(old, at);
            at = next + chunks * 2;
            distances.Add(BinaryPrimitives.ReadSingleLittleEndian(old.AsSpan(at)));
            at += 4;
        }
        int lods = Math.Max(distances.Count, visible.Count == 0 ? 1 : 32 - BitOperations.LeadingZeroCount((uint)visible.Max(c => c.LodMask)));
        WriteCount(ms, lods);
        for (int l = 0; l < lods; l++)
        {
            var inLod = visible.Select((c, i) => (c, i)).Where(x => x.c.InLod(l)).Select(x => x.i).ToList();
            WriteCount(ms, inLod.Count);
            foreach (int i in inLod) ms.Write(BitConverter.GetBytes((ushort)i));
            ms.Write(BitConverter.GetBytes(l < distances.Count ? distances[l] : l == 0 ? 0f : distances.LastOrDefault() + 10 * (l - distances.Count + 1)));
        }

        // the bones, by name: a name the file hasn't got goes on the end of its list
        WriteCount(ms, boneNames.Length);
        foreach (string name in boneNames)
        {
            int index = -1;
            for (int i = 0; i < names.Count && index < 0; i++)
                if ((string?)names[i] == name) index = i;
            if (index < 0)
            {
                names.Add(name);
                index = names.Count - 1;
            }
            ms.Write(BitConverter.GetBytes((ushort)index));
        }
        WriteCount(ms, inverseBinds.Length);
        foreach (var m in inverseBinds)
            foreach (float f in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
                ms.Write(BitConverter.GetBytes(f));

        // how far each bone's vertices reach from it, then as many zeros. The game's are 1.2 times the farthest vertex
        // that names the bone, in most meshes; others were worked out from the mesh before it was cooked, so the
        // template's own is kept for a bone it has when that reaches further (it can only make the mesh's bounds bigger)
        var (oldNames, afterNames) = ReadCount(old, at);
        var (oldMatrices, afterMatrices) = ReadCount(old, afterNames + oldNames * 2);
        var (oldReaches, reachAt) = ReadCount(old, afterMatrices + oldMatrices * 64);
        var reach = new float[boneNames.Length];
        var positions = inverseBinds.Select(b => Matrix4x4.Invert(b, out var bound) ? bound.Translation : Vector3.Zero).ToArray();
        foreach (var chunk in visible)
        {
            int per = chunk.BonesPerVertex;
            if (per == 0) continue;
            for (int v = 0; v < chunk.Positions.Length; v++)
                for (int b = 0; b < per; b++)
                {
                    int bone = chunk.BoneIndices[v * per + b];
                    if (bone < reach.Length) reach[bone] = Math.Max(reach[bone], Vector3.Distance(chunk.Positions[v], positions[bone]) * 1.2f);
                }
        }
        for (int i = 0; i < reach.Length; i++)
        {
            int same = Array.FindIndex(template.BoneNames, n => n.Equals(boneNames[i], StringComparison.Ordinal));
            if (same >= 0 && same < oldReaches && reachAt + same * 4 + 4 <= old.Length)
                reach[i] = Math.Max(reach[i], BinaryPrimitives.ReadSingleLittleEndian(old.AsSpan(reachAt + same * 4)));
        }
        WriteCount(ms, reach.Length);
        foreach (float r in reach) ms.Write(BitConverter.GetBytes(r));

        // the bones the vertices use (with some weight), in the order they're first used, as numbers into the bone list
        // (WolvenKit names it BoneIndecesMappingBoneIndex). The cooker made it from the mesh before it reordered its
        // vertices, so the template's is kept, bone by bone, while the mesh still uses the same bones; else it's made anew
        var used = UsedBones(visible, boneNames.Length);
        var (oldMapCount, mapAt) = ReadCount(old, reachAt + oldReaches * 4);
        var oldMap = Enumerable.Range(0, oldMapCount).Where(i => mapAt + i * 4 + 4 <= old.Length)
            .Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(old.AsSpan(mapAt + i * 4))).ToList();
        var templateUsed = UsedBones(template.Chunks.Where(c => !c.IsShadowOnly).ToList(), template.BoneNames.Length)
            .Select(b => template.BoneNames[b]).ToHashSet(StringComparer.Ordinal);
        var nowUsed = used.Select(b => boneNames[b]).ToHashSet(StringComparer.Ordinal);
        var map = used;
        if (templateUsed.SetEquals(nowUsed) && oldMap.All(b => b < template.BoneNames.Length))
            map = [.. oldMap.Select(b => Array.IndexOf(boneNames, template.BoneNames[b])).Select(b => Math.Max(b, 0))];
        WriteCount(ms, map.Count);
        foreach (int b in map) ms.Write(BitConverter.GetBytes((uint)b));
        return ms.ToArray();
    }

    /// <summary>The bones the chunks' vertices use with some weight, in the order they're first used.</summary>
    private static List<int> UsedBones(List<MeshChunk> chunks, int bones)
    {
        var used = new List<int>();
        var seen = new HashSet<int>();
        foreach (var chunk in chunks)
        {
            int per = chunk.BonesPerVertex;
            for (int v = 0; v < chunk.Positions.Length && per > 0; v++)
                for (int b = 0; b < per; b++)
                {
                    int bone = chunk.BoneIndices[v * per + b];
                    if (bone < bones && chunk.BoneWeights[v * per + b] > 0 && seen.Add(bone)) used.Add(bone);
                }
        }
        return used;
    }

    // ---------------------------------------------------------------- streams

    private static byte[] Positions(MeshChunk chunk, Vector3 min, Vector3 scale, int bones)
    {
        int n = chunk.Positions.Length, stride = 8 + bones * 2;
        var s = new byte[n * stride];
        for (int v = 0; v < n; v++)
        {
            var p = chunk.Positions[v];
            var at = s.AsSpan(v * stride);
            BinaryPrimitives.WriteUInt16LittleEndian(at, Quantize(p.X, min.X, scale.X));
            BinaryPrimitives.WriteUInt16LittleEndian(at[2..], Quantize(p.Y, min.Y, scale.Y));
            BinaryPrimitives.WriteUInt16LittleEndian(at[4..], Quantize(p.Z, min.Z, scale.Z));
            BinaryPrimitives.WriteUInt16LittleEndian(at[6..], 0xFFFF);
            int per = chunk.BonesPerVertex;
            for (int b = 0; b < bones && per > 0; b++)
            {
                if (b >= per) continue;
                at[8 + b] = chunk.BoneIndices[v * per + b];
                at[8 + bones + b] = (byte)Math.Clamp(MathF.Round(chunk.BoneWeights[v * per + b] * 255), 0, 255);
            }
        }
        return s;
    }

    private static ushort Quantize(float value, float min, float scale) =>
        scale > 0 ? (ushort)Math.Clamp(MathF.Round((value - min) / scale * 65535), 0, 65535) : (ushort)0;

    private static byte[] Uvs(MeshChunk chunk)
    {
        int n = chunk.Positions.Length;
        var s = new byte[n * 4];
        for (int v = 0; v < n; v++)
        {
            var uv = v < chunk.Uvs.Length ? chunk.Uvs[v] : Vector2.Zero;
            BinaryPrimitives.WriteHalfLittleEndian(s.AsSpan(v * 4), (Half)uv.X);
            BinaryPrimitives.WriteHalfLittleEndian(s.AsSpan(v * 4 + 2), (Half)uv.Y);
        }
        return s;
    }

    private static byte[] NormalsAndTangents(MeshChunk chunk)
    {
        int n = chunk.Positions.Length;
        var s = new byte[n * 8];
        for (int v = 0; v < n; v++)
        {
            var normal = v < chunk.Normals.Length ? chunk.Normals[v] : Vector3.UnitZ;
            var tangent = v < chunk.Tangents.Length ? chunk.Tangents[v] : new Vector4(1, 0, 0, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(v * 8), Ten(normal) | 1u << 30);
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(v * 8 + 4), Ten(new Vector3(tangent.X, tangent.Y, tangent.Z)) | (tangent.W >= 0 ? 3u << 30 : 0));
        }
        return s;
    }

    /// <summary>A vector as three 10-bit numbers, −1 to 1 as 0 to 1023.</summary>
    private static uint Ten(Vector3 v)
    {
        static uint C(float f) => (uint)Math.Clamp(MathF.Round((f + 1) / 2 * 1023), 0, 1023);
        return C(v.X) | C(v.Y) << 10 | C(v.Z) << 20;
    }

    private static byte[] Extra(MeshChunk chunk)
    {
        int n = chunk.Positions.Length;
        if (chunk.ExtraStream.Length == n * 8) return chunk.ExtraStream;
        var s = new byte[n * 8];
        for (int v = 0; v < n; v++)
        {
            if (chunk.Colors.Length == n * 3) chunk.Colors.AsSpan(v * 3, 3).CopyTo(s.AsSpan(v * 8));
            else s[v * 8] = s[v * 8 + 1] = s[v * 8 + 2] = 255;
            s[v * 8 + 3] = 255;
        }
        return s;
    }

    private static byte[] Indices(int[] indices)
    {
        var s = new byte[indices.Length * 2];
        for (int i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(i * 2), (ushort)indices[i]);
        return s;
    }

    // ---------------------------------------------------------------- small things

    /// <summary>Compares byte arrays by what's in them, so the same stream is found again.</summary>
    private sealed class ByteComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] data)
        {
            var hash = new HashCode();
            hash.AddBytes(data);
            return hash.ToHashCode();
        }
    }

    /// <summary>A vector's W as a JSON node of the template has it, or the fallback.</summary>
    private static double W(JsonNode? vector, double fallback) =>
        vector?["W"] is JsonNode w ? double.Parse(w.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : fallback;

    private static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));

    private static void Put(byte[] d, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), value);

    private static JsonObject Vector(Vector3 v, double w) => new() { ["X"] = (double)v.X, ["Y"] = (double)v.Y, ["Z"] = (double)v.Z, ["W"] = w };

    /// <summary>A count as the engine writes one: six bits, and seven more a byte while there are more; nothing as 80.</summary>
    private static void WriteCount(Stream s, int count) => s.Write(PackedInt.CountBytes(count));

    private static (int Count, int Next) ReadCount(byte[] data, int at)
    {
        PackedInt.TryRead(data, ref at, out int count, out _);
        return (count, at);
    }
}
