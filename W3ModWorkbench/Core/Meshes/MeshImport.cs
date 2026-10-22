using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>What an import made, LOD by LOD, and what it had to work out or leave out, for the person to read.</summary>
public sealed record MeshImportResult(CookedMesh Mesh, List<string> Lines, List<string> Warnings);

/// <summary>
/// Puts a glTF's geometry onto a cooked mesh of the game (the template): its parts become the template's chunks, which
/// keep their materials, how they're drawn and everything else the mesh has, and are written as the game cooks them.
/// <para>
/// A part's LOD comes from its node's name (<c>name_lod1</c>), or the <c>lod</c> extra this app writes, else it's LOD 0.
/// Its material comes from the <c>w3Slot</c> extra, else its name (Blender's <c>.001</c> left off) matched to the names
/// the export gives, else the template material of the same name or number. Parts of the same LOD and material are
/// joined into one chunk, split again where one chunk would have more than 65,535 vertices.
/// </para>
/// <para>
/// The file is Y-up and the game Z-up, so everything is turned back (x, y, z to x, −z, y) and the triangles wound the
/// game's way. What the file hasn't got is worked out or taken from the template's nearest vertex in the same LOD and
/// material: normals from the triangles, tangents from the UVs, the vertex colours, the byte the game keeps beside
/// them and the second UV set (TEXCOORD_1) in its extra stream, and a skinned chunk's bones and weights when the file
/// has no skin.
/// </para>
/// </summary>
public static class MeshImport
{
    /// <param name="template">The cooked mesh the parts go onto.</param>
    /// <param name="materialNames">The names the export gives the template's materials (<see cref="GltfExport.MaterialNames"/>), to find a part's material by.</param>
    /// <param name="missingLods">For a LOD of the template the file has no parts for: true to give it the file's nearest LOD below it, false to keep the template's own.</param>
    public static MeshImportResult Import(MeshFile template, IReadOnlyDictionary<int, string> materialNames, string gltfPath, bool missingLods)
    {
        var parts = GltfReader.Read(gltfPath);
        var warnings = new List<string>();
        var visible = template.Chunks.Where(c => !c.IsShadowOnly).ToList();
        if (visible.Count == 0 || visible.Any(c => c.CookedInfo.Length == 0)) throw new InvalidDataException(L.T("MeshImport.NotCooked"));

        var names = materialNames;
        int slotCount = Math.Max(template.Materials.Count, visible.Max(c => c.Material) + 1);

        // only the parts named for a LOD when some are (others, such as Blender's bone shapes, are left out)
        bool named = parts.Any(p => LodOf(p) is not null);
        var used = named ? parts.Where(p => LodOf(p) is not null).ToList() : parts;
        if (used.Count == 0) throw new InvalidDataException(L.T("MeshImport.NoParts"));
        if (named && used.Count < parts.Count) warnings.Add(L.T("MeshImport.LeftOutUnnamed", parts.Count - used.Count));

        // the mesh's bones: the template's, then any the file's skin adds
        var boneNames = template.BoneNames.ToList();
        var binds = template.BoneInverseBinds.ToList();
        foreach (var skin in used.Select(p => p.Skin).OfType<GltfSkin>().Distinct())
            for (int j = 0; j < skin.JointNames.Length; j++)
                if (!boneNames.Contains(skin.JointNames[j]))
                {
                    boneNames.Add(skin.JointNames[j]);
                    binds.Add(ToGame(skin.InverseBinds[j]));
                    warnings.Add(L.T("MeshImport.NewBone", skin.JointNames[j]));
                }
        if (boneNames.Count > 255) throw new InvalidDataException(L.T("MeshImport.TooManyBones", boneNames.Count));

        // each part's LOD and material slot
        var placed = new List<(GltfPrimitive Part, int Lod, int Slot)>();
        foreach (var part in used)
        {
            int lod = LodOf(part) ?? 0;
            int? slot = SlotOf(part, names, slotCount);
            if (slot is null)
            {
                warnings.Add(L.T("MeshImport.UnknownMaterial", part.MaterialName ?? "-", part.NodeName));
                slot = 0;
            }
            placed.Add((part, lod, slot.Value));
        }

        int templateLods = template.LodCount;
        int fileLods = placed.Max(p => p.Lod) + 1;
        int lodCount = Math.Max(templateLods, fileLods);
        var chunks = new List<MeshChunk>();
        var lines = new List<string>();
        for (int lod = 0; lod < lodCount; lod++)
        {
            var inLod = placed.Where(p => p.Lod == lod).ToList();
            int? from = null;
            if (inLod.Count == 0)
            {
                int below = placed.Where(p => p.Lod < lod).Select(p => p.Lod).DefaultIfEmpty(-1).Max();
                // kept as the template has it when asked, when there's nothing below, or when the template draws nothing in
                // it (the export leaves out what isn't drawn)
                if (lod < templateLods && (!missingLods || below < 0 || !visible.Any(c => IsDrawn(c, visible, lod))))
                {
                    var kept = visible.Where(c => c.InLod(lod)).ToList();
                    foreach (var c in kept) chunks.Add(Copy(c, 1 << lod));
                    lines.Add(L.T("MeshImport.LodKept", lod, kept.Count));
                    continue;
                }
                inLod = [.. placed.Where(p => p.Lod == below).Select(p => (p.Part, lod, p.Slot))];
                from = below;
            }
            int before = chunks.Count;
            // parts go together by material, or by the chunk they were exported from where the file still says
            // (Blender doesn't), in the template's order of chunks
            var groups = inLod.GroupBy(p => (p.Slot, Chunk: ChunkOf(p.Part, visible, lod, p.Slot)))
                .OrderBy(g => g.Key.Chunk >= 0 ? g.Key.Chunk : visible.FindIndex(c => c.InLod(lod) && c.Material == g.Key.Slot) is int i and >= 0 ? i : int.MaxValue)
                .ThenBy(g => g.Key.Slot);
            foreach (var group in groups)
            {
                // the template chunk these take the place of: that one, or the same material in this LOD, else in any, else any
                var like = (group.Key.Chunk >= 0 ? visible[group.Key.Chunk] : null)
                    ?? visible.FirstOrDefault(c => c.InLod(lod) && c.Material == group.Key.Slot)
                    ?? visible.FirstOrDefault(c => c.Material == group.Key.Slot)
                    ?? visible.FirstOrDefault(c => c.InLod(lod)) ?? visible[0];
                var near = visible.Where(c => c.Material == like.Material && c.InLod(Math.Min(lod, templateLods - 1))).DefaultIfEmpty(like).ToList();
                foreach (var piece in Split(group.Select(g => g.Part).ToList()))
                    chunks.Add(Build(piece, like, near, group.Key.Slot, 1 << lod, boneNames, template.BoneNames, warnings));
            }
            // the template's chunks of this LOD that aren't drawn (only cast shadows, say), which the export leaves out, kept
            if (lod < templateLods)
                foreach (var c in visible.Where(c => c.InLod(lod) && !IsDrawn(c, visible, lod)))
                    chunks.Add(Copy(c, 1 << lod));
            var made = chunks.Skip(before).ToList();
            lines.Add(from is int f
                ? L.T("MeshImport.LodFromFile", lod, made.Count, made.Sum(c => c.Positions.Length), made.Sum(c => c.Triangles), f)
                : L.T("MeshImport.Lod", lod, made.Count, made.Sum(c => c.Positions.Length), made.Sum(c => c.Triangles)));
        }

        // a chunk the same in several LODs is written once, drawn in each
        var merged = new List<MeshChunk>();
        foreach (var c in chunks)
        {
            var same = merged.FirstOrDefault(m => ReferenceEquals(m.Positions, c.Positions) && m.Material == c.Material);
            if (same is null) merged.Add(c);
            else merged[merged.IndexOf(same)] = Copy(same, same.LodMask | c.LodMask);
        }
        var cooked = MeshWriter.Write(template, merged, [.. boneNames], [.. binds]);
        return new MeshImportResult(cooked, lines, warnings);
    }

    /// <summary>
    /// The template chunk (its place among those drawn) a part was exported from, by its <c>w3Chunk</c> extra, when that
    /// chunk is still one of this LOD with the same material; else −1.
    /// </summary>
    private static int ChunkOf(GltfPrimitive p, List<MeshChunk> visible, int lod, int slot)
    {
        if (p.Extras?["w3Chunk"] is not { } extra || !int.TryParse(extra.ToJsonString(), out int index)) return -1;
        int at = visible.FindIndex(c => c.Index == index);
        return at >= 0 && visible[at].InLod(lod) && visible[at].Material == slot ? at : -1;
    }

    /// <summary>Whether a chunk is drawn in a LOD (so exported): seen in the scene, or anything but hidden when none of the LOD's is.</summary>
    public static bool IsDrawn(MeshChunk chunk, IReadOnlyList<MeshChunk> chunks, int lod)
    {
        if (!chunk.InLod(lod) || chunk.Indices.Length == 0) return false;
        bool anySeen = chunks.Any(c => c.InLod(lod) && c.Indices.Length > 0 && (c.RenderMask & 1) != 0);
        return anySeen ? (chunk.RenderMask & 1) != 0 : chunk.RenderMask != 0;
    }

    private static int? LodOf(GltfPrimitive p)
    {
        if (p.NodeExtras?["lod"] is { } extra && int.TryParse(extra.ToJsonString(), out int fromExtra)) return fromExtra;
        var m = Regex.Match(p.NodeName, @"_lod(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(p.MeshName, @"_lod(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static int? SlotOf(GltfPrimitive p, IReadOnlyDictionary<int, string> names, int slots)
    {
        if (p.MaterialExtras?["w3Slot"] is { } extra && int.TryParse(extra.ToJsonString(), out int fromExtra) && fromExtra < slots) return fromExtra;
        if (p.MaterialName is { } name)
        {
            // Blender numbers a name it has twice: pbr_std_0.001
            string plain = Regex.Replace(name, @"\.\d{3}$", "");
            foreach (var (slot, given) in names)
                if (given.Equals(plain, StringComparison.OrdinalIgnoreCase)) return slot;
            var number = Regex.Match(plain, @"(?:^material_|_)(\d+)$");
            if (number.Success && int.Parse(number.Groups[1].Value) < slots) return int.Parse(number.Groups[1].Value);
        }
        return slots == 1 ? 0 : null;
    }

    /// <summary>Parts joined into chunks of no more than 65,535 vertices, a part bigger than that split by its triangles.</summary>
    private static IEnumerable<List<(GltfPrimitive Part, int[] Triangles)>> Split(List<GltfPrimitive> parts)
    {
        var piece = new List<(GltfPrimitive, int[])>();
        int count = 0;
        foreach (var part in parts)
        {
            if (part.Positions.Length <= ushort.MaxValue)
            {
                if (count + part.Positions.Length > ushort.MaxValue && piece.Count > 0)
                {
                    yield return piece;
                    piece = [];
                    count = 0;
                }
                piece.Add((part, part.Indices));
                count += part.Positions.Length;
                continue;
            }
            // a part too big for one chunk: its triangles in turn, until their vertices fill one
            var used = new HashSet<int>();
            var triangles = new List<int>();
            for (int i = 0; i + 2 < part.Indices.Length; i += 3)
            {
                int added = (used.Contains(part.Indices[i]) ? 0 : 1) + (used.Contains(part.Indices[i + 1]) ? 0 : 1) + (used.Contains(part.Indices[i + 2]) ? 0 : 1);
                if (used.Count + added > ushort.MaxValue)
                {
                    yield return [(part, [.. triangles])];
                    used.Clear();
                    triangles.Clear();
                }
                for (int k = 0; k < 3; k++)
                {
                    used.Add(part.Indices[i + k]);
                    triangles.Add(part.Indices[i + k]);
                }
            }
            if (triangles.Count > 0) yield return [(part, [.. triangles])];
        }
        if (piece.Count > 0) yield return piece;
    }

    /// <summary>One chunk from parts: their vertices turned to the game's space, with whatever the file lacks filled in.</summary>
    private static MeshChunk Build(List<(GltfPrimitive Part, int[] Triangles)> parts, MeshChunk like, List<MeshChunk> near, int slot, int lodMask,
        List<string> boneNames, string[] templateBones, List<string> warnings)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uvs = new List<Vector2>();
        var uvs2 = new List<Vector2>();
        var colours = new List<Vector4>();
        var w3 = new List<byte>();
        var influences = new List<(int Bone, float Weight)[]>();
        var indices = new List<int>();
        bool hasNormals = true, hasTangents = true, hasUvs = true, hasUvs2 = true, hasColours = true, hasW3 = true, hasSkin = true;
        foreach (var (part, triangles) in parts)
        {
            // a whole part keeps its vertices in their order; a piece of one numbers those its triangles use anew
            var map = new Dictionary<int, int>();
            if (ReferenceEquals(triangles, part.Indices))
                for (int i = 0; i < part.Positions.Length; i++) map[i] = i;
            else
                foreach (int i in triangles)
                    if (!map.ContainsKey(i)) map[i] = map.Count;
            var order = map.OrderBy(m => m.Value).Select(m => m.Key).ToArray();
            int first = positions.Count;
            hasNormals &= part.Normals.Length == part.Positions.Length;
            hasTangents &= part.Tangents.Length == part.Positions.Length;
            hasUvs &= part.Uvs.Length == part.Positions.Length;
            hasUvs2 &= part.Uvs2.Length == part.Positions.Length;
            hasColours &= part.Colors.Length == part.Positions.Length;
            hasW3 &= part.W3Colors.Length == part.Positions.Length * 4;
            hasSkin &= part.Skin is not null && part.InfluencesPerVertex > 0;
            foreach (int v in order)
            {
                positions.Add(ToGame(part.Positions[v]));
                normals.Add(v < part.Normals.Length ? ToGame(part.Normals[v]) : Vector3.Zero);
                tangents.Add(v < part.Tangents.Length ? new Vector4(ToGame(new Vector3(part.Tangents[v].X, part.Tangents[v].Y, part.Tangents[v].Z)), part.Tangents[v].W) : Vector4.Zero);
                uvs.Add(v < part.Uvs.Length ? part.Uvs[v] : Vector2.Zero);
                uvs2.Add(v < part.Uvs2.Length ? part.Uvs2[v] : Vector2.Zero);
                colours.Add(v < part.Colors.Length ? part.Colors[v] : Vector4.One);
                for (int k = 0; k < 4; k++) w3.Add(v * 4 + k < part.W3Colors.Length ? part.W3Colors[v * 4 + k] : (byte)255);
                var set = new List<(int, float)>();
                if (part.Skin is { } skin)
                    for (int k = 0; k < part.InfluencesPerVertex; k++)
                    {
                        float w = part.Weights[v * part.InfluencesPerVertex + k];
                        int joint = part.Joints[v * part.InfluencesPerVertex + k];
                        if (w > 0 && joint < skin.JointNames.Length) set.Add((boneNames.IndexOf(skin.JointNames[joint]), w));
                    }
                influences.Add([.. set]);
            }
            // glTF winds its triangles the other way round
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                indices.Add(first + map[triangles[i]]);
                indices.Add(first + map[triangles[i + 2]]);
                indices.Add(first + map[triangles[i + 1]]);
            }
        }

        int n = positions.Count;
        var pos = positions.ToArray();
        var tris = indices.ToArray();
        var normalArray = hasNormals ? Normalised(normals) : SmoothNormals(pos, tris);
        if (!hasNormals) warnings.Add(L.T("MeshImport.MadeNormals", slot));
        var uvArray = uvs.ToArray();
        if (!hasUvs) warnings.Add(L.T("MeshImport.NoUvs", slot));
        var tangentArray = hasTangents ? Tangents(tangents, normalArray) : MakeTangents(pos, normalArray, uvArray, tris);

        // what the file hasn't got, from the template's nearest vertex
        var nearest = new Nearest(near);
        bool layoutHasExtra = BitConverter.ToUInt32(like.CookedInfo, 13) != 0;
        bool skinned = like.CookedInfo[0] == 3;
        byte[] extra = [];
        if (layoutHasExtra)
        {
            extra = new byte[n * 8];
            for (int v = 0; v < n; v++)
            {
                var (chunk, at) = nearest.Find(pos[v]);
                if (chunk is not null && chunk.ExtraStream.Length >= (at + 1) * 8) chunk.ExtraStream.AsSpan(at * 8, 8).CopyTo(extra.AsSpan(v * 8));
                else { extra[v * 8] = extra[v * 8 + 1] = extra[v * 8 + 2] = extra[v * 8 + 3] = 255; }
                // the raw colour and byte as written; then glTF's own colour, which is what a tool paints
                if (hasW3) for (int k = 0; k < 4; k++) extra[v * 8 + k] = w3[v * 4 + k];
                if (hasColours)
                {
                    extra[v * 8] = (byte)Math.Clamp(MathF.Round(colours[v].X * 255), 0, 255);
                    extra[v * 8 + 1] = (byte)Math.Clamp(MathF.Round(colours[v].Y * 255), 0, 255);
                    extra[v * 8 + 2] = (byte)Math.Clamp(MathF.Round(colours[v].Z * 255), 0, 255);
                }
                if (hasUvs2)
                {
                    BitConverter.TryWriteBytes(extra.AsSpan(v * 8 + 4), (Half)uvs2[v].X);
                    BitConverter.TryWriteBytes(extra.AsSpan(v * 8 + 6), (Half)uvs2[v].Y);
                }
            }
            if (!hasW3 && !hasColours) warnings.Add(L.T("MeshImport.ColoursFromNearest", slot));
        }

        int per = skinned ? Math.Max(4, like.BonesPerVertex) : 0;
        var boneIndices = new byte[n * per];
        var boneWeights = new float[n * per];
        if (skinned)
        {
            if (!hasSkin) warnings.Add(L.T("MeshImport.WeightsFromNearest", slot));
            int unweighted = 0;
            for (int v = 0; v < n; v++)
            {
                var set = hasSkin ? influences[v] : [];
                if (set.Length == 0)
                {
                    var (chunk, at) = nearest.Find(pos[v]);
                    if (chunk is not null && chunk.BonesPerVertex > 0)
                        set = [.. Enumerable.Range(0, chunk.BonesPerVertex).Select(b => ((int)chunk.BoneIndices[at * chunk.BonesPerVertex + b], chunk.BoneWeights[at * chunk.BonesPerVertex + b]))
                            .Where(x => x.Item2 > 0 && x.Item1 < templateBones.Length).Select(x => (boneNames.IndexOf(templateBones[x.Item1]), x.Item2)).Where(x => x.Item1 >= 0)];
                    if (hasSkin) unweighted++;
                }
                Quantise(set, per, boneIndices.AsSpan(v * per, per), boneWeights.AsSpan(v * per, per));
            }
            if (unweighted > 0) warnings.Add(L.T("MeshImport.Unweighted", slot, unweighted));
        }

        return new MeshChunk
        {
            Index = like.Index,
            Material = slot,
            LodMask = lodMask,
            RenderMask = like.RenderMask,
            Skinned = skinned,
            Positions = pos,
            Normals = normalArray,
            Tangents = tangentArray,
            Uvs = uvArray,
            Indices = tris,
            BonesPerVertex = per,
            BoneIndices = boneIndices,
            BoneWeights = boneWeights,
            Colors = layoutHasExtra ? [.. Enumerable.Range(0, n).SelectMany(v => new[] { extra[v * 8], extra[v * 8 + 1], extra[v * 8 + 2] })] : [],
            ExtraStream = extra,
            CookedInfo = like.CookedInfo,
        };
    }

    /// <summary>
    /// A vertex's bones, the strongest <paramref name="per"/>, as the game keeps them: weights in 255ths adding up to 255,
    /// the rounding given to the strongest. A vertex nothing moves goes with the first bone.
    /// </summary>
    private static void Quantise((int Bone, float Weight)[] set, int per, Span<byte> bones, Span<float> weights)
    {
        var strongest = set.Where(s => s.Bone >= 0).GroupBy(s => s.Bone).Select(g => (Bone: g.Key, Weight: g.Sum(x => x.Weight)))
            .OrderByDescending(s => s.Weight).Take(per).ToList();
        float total = strongest.Sum(s => s.Weight);
        if (strongest.Count == 0 || total <= 0)
        {
            weights[0] = 1;
            return;
        }
        var bytes = strongest.Select(s => (int)MathF.Round(s.Weight / total * 255)).ToArray();
        bytes[0] += 255 - bytes.Sum();
        for (int i = 0; i < strongest.Count; i++)
        {
            bones[i] = (byte)strongest[i].Bone;
            weights[i] = Math.Clamp(bytes[i], 0, 255) / 255f;
        }
    }

    private static MeshChunk Copy(MeshChunk c, int lodMask) => new()
    {
        Index = c.Index, Material = c.Material, LodMask = lodMask, RenderMask = c.RenderMask, Skinned = c.Skinned,
        Positions = c.Positions, Normals = c.Normals, Tangents = c.Tangents, Uvs = c.Uvs, Indices = c.Indices,
        BonesPerVertex = c.BonesPerVertex, BoneIndices = c.BoneIndices, BoneWeights = c.BoneWeights, Colors = c.Colors,
        ExtraStream = c.ExtraStream, CookedInfo = c.CookedInfo,
    };

    // ---------------------------------------------------------------- geometry

    /// <summary>glTF's Y-up to the game's Z-up: x, y, z becomes x, −z, y.</summary>
    private static Vector3 ToGame(Vector3 v) => new(v.X, -v.Z, v.Y);

    private static readonly Matrix4x4 ToYUp = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    private static readonly Matrix4x4 FromYUp = Matrix4x4.Transpose(ToYUp);

    /// <summary>A matrix in glTF's space as the game has it: the turn the export made, undone.</summary>
    private static Matrix4x4 ToGame(Matrix4x4 m) => ToYUp * m * FromYUp;

    private static Vector3[] Normalised(List<Vector3> normals) =>
        [.. normals.Select(v => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ)];

    private static Vector4[] Tangents(List<Vector4> tangents, Vector3[] normals) =>
        [.. tangents.Select((t, i) =>
        {
            var v = new Vector3(t.X, t.Y, t.Z);
            v -= normals[i] * Vector3.Dot(normals[i], v);
            return new Vector4(v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitX, t.W < 0 ? -1 : 1);
        })];

    /// <summary>Normals made from the triangles, each vertex's the sum of its triangles' weighted by their size.</summary>
    private static Vector3[] SmoothNormals(Vector3[] p, int[] t)
    {
        var n = new Vector3[p.Length];
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            // the game's winding: the face's front is (b − a) × (c − a) the other way round
            var face = Vector3.Cross(p[t[i + 2]] - p[t[i]], p[t[i + 1]] - p[t[i]]);
            n[t[i]] += face;
            n[t[i + 1]] += face;
            n[t[i + 2]] += face;
        }
        return [.. n.Select(v => v.LengthSquared() > 1e-20f ? Vector3.Normalize(v) : Vector3.UnitZ)];
    }

    /// <summary>Tangents made from the UVs: the way U runs across each triangle, summed at its corners, and V's side for the handedness.</summary>
    private static Vector4[] MakeTangents(Vector3[] p, Vector3[] normals, Vector2[] uv, int[] t)
    {
        var tan = new Vector3[p.Length];
        var bit = new Vector3[p.Length];
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];
            var e1 = p[b] - p[a];
            var e2 = p[c] - p[a];
            var d1 = uv[b] - uv[a];
            var d2 = uv[c] - uv[a];
            float r = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(r) < 1e-12f) continue;
            r = 1 / r;
            var sdir = (e1 * d2.Y - e2 * d1.Y) * r;
            var tdir = (e2 * d1.X - e1 * d2.X) * r;
            foreach (int v in new[] { a, b, c })
            {
                tan[v] += sdir;
                bit[v] += tdir;
            }
        }
        var result = new Vector4[p.Length];
        for (int v = 0; v < p.Length; v++)
        {
            var n = normals[v];
            var x = tan[v] - n * Vector3.Dot(n, tan[v]);
            if (x.LengthSquared() < 1e-12f) x = Vector3.Cross(n, MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ);
            x = Vector3.Normalize(x);
            // the green of a normal map runs along normal × tangent times W, as the game reads it
            result[v] = new Vector4(x, Vector3.Dot(Vector3.Cross(n, x), bit[v]) < 0 ? -1 : 1);
        }
        return result;
    }

    /// <summary>The nearest vertex among some chunks, looked up through a grid of cells.</summary>
    private sealed class Nearest
    {
        private readonly List<(MeshChunk Chunk, int Vertex, Vector3 At)> _all = [];
        private readonly Dictionary<(int, int, int), List<int>> _cells = [];
        private readonly float _cell;

        public Nearest(List<MeshChunk> chunks)
        {
            foreach (var c in chunks)
                for (int v = 0; v < c.Positions.Length; v++) _all.Add((c, v, c.Positions[v]));
            if (_all.Count == 0) { _cell = 1; return; }
            var min = _all.Aggregate(new Vector3(float.MaxValue), (m, x) => Vector3.Min(m, x.At));
            var max = _all.Aggregate(new Vector3(float.MinValue), (m, x) => Vector3.Max(m, x.At));
            var size = max - min;
            _cell = MathF.Max(MathF.Cbrt(MathF.Max(size.X * size.Y * size.Z, 1e-9f) / Math.Max(_all.Count / 4f, 1)), 1e-4f);
            for (int i = 0; i < _all.Count; i++)
            {
                var key = Key(_all[i].At);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
                list.Add(i);
            }
        }

        private (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));

        public (MeshChunk? Chunk, int Vertex) Find(Vector3 p)
        {
            if (_all.Count == 0) return (null, 0);
            var (x, y, z) = Key(p);
            int best = -1;
            float bestDistance = float.MaxValue;
            // rings of cells outwards until a vertex is found and the next ring can't be nearer
            for (int ring = 0; ring < 64; ring++)
            {
                for (int i = -ring; i <= ring; i++)
                    for (int j = -ring; j <= ring; j++)
                        for (int k = -ring; k <= ring; k++)
                        {
                            if (Math.Max(Math.Abs(i), Math.Max(Math.Abs(j), Math.Abs(k))) != ring) continue;
                            if (!_cells.TryGetValue((x + i, y + j, z + k), out var list)) continue;
                            foreach (int index in list)
                            {
                                float d = Vector3.DistanceSquared(p, _all[index].At);
                                if (d < bestDistance) (bestDistance, best) = (d, index);
                            }
                        }
                if (best >= 0 && MathF.Sqrt(bestDistance) <= ring * _cell) break;
            }
            if (best < 0)
                for (int i = 0; i < _all.Count; i++)
                {
                    float d = Vector3.DistanceSquared(p, _all[i].At);
                    if (d < bestDistance) (bestDistance, best) = (d, i);
                }
            return (_all[best].Chunk, _all[best].Vertex);
        }
    }
}
