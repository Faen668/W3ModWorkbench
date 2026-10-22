using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Packing;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>A picture a new mesh's material uses, to go in the project beside it, which Build makes into its texture.</summary>
public sealed record NewMeshPicture(string GamePath, byte[] Tga);

/// <summary>A new mesh made from a glTF: the mesh and its buffer, the pictures its materials use, and how it went.</summary>
public sealed record NewMeshResult(CookedMesh Mesh, List<NewMeshPicture> Pictures, List<string> Lines, List<string> Warnings);

/// <summary>
/// Makes a mesh of the game's own from a glTF, with no game mesh under it: a cooked <c>.w2mesh</c> as the game has them,
/// written by <see cref="MeshWriter"/>, with what a game mesh keeps made for it.
/// <para>
/// Its chunks come from the file's parts as an import's do (<see cref="MeshImport"/>): a LOD from a part's name
/// (<c>name_lod1</c>), else LOD 0; a material slot for each of the file's materials. A mesh whose parts have a skin is
/// skinned, with the skin's bones as the file binds them, and four bones a vertex (eight when the file gives more); one
/// without is static. Each chunk casts its own shadows, so there are no chunks only for shadows. The LODs are drawn from
/// 0, 10, 20, 40… metres, and the mesh hides from 100 metres or twice its last LOD's distance.
/// </para>
/// <para>
/// A material the file made from one of the game's (its <c>w3Source</c> extra, from this app's export) uses that
/// material. Any other becomes a material of the mesh's own on the game's standard shader (pbr_std): its colour picture
/// is the Diffuse, and its normal map, with the roughness the file keeps in green put in its alpha as the game reads it,
/// the Normal. Those pictures go in the project as <c>.tga</c> beside the mesh, named for it and the material
/// (<c>_d01</c> and <c>_n01</c>), and Build makes them into textures.
/// </para>
/// </summary>
public static class NewMesh
{
    private const string StandardShader = @"engine\materials\graphs\pbr_std.w2mg";

    /// <param name="meshGamePath">Where the mesh goes in the game's files, which names its pictures.</param>
    /// <param name="readFile">Reads a game file by its path, to find a material the file names.</param>
    public static NewMeshResult Make(string gltfPath, string meshGamePath, Func<string, byte[]?> readFile)
    {
        var parts = GltfReader.Read(gltfPath);
        var warnings = new List<string>();
        var lines = new List<string>();
        bool named = parts.Any(p => MeshImport.LodOf(p) is not null);
        var used = named ? parts.Where(p => MeshImport.LodOf(p) is not null).ToList() : parts;
        if (used.Count == 0) throw new InvalidDataException(L.T("MeshImport.NoParts"));
        if (named && used.Count < parts.Count) warnings.Add(L.T("MeshImport.LeftOutUnnamed", parts.Count - used.Count));

        // a slot for each of the file's materials the parts use, in the file's order; parts without one share a slot
        var keys = used.Select(p => p.Material ?? -1).Distinct().Order().ToList();
        if (keys.Count > 255) throw new InvalidDataException(L.T("NewMesh.TooManyMaterials", keys.Count));

        // bones: those of the skins, as the file binds them
        bool skinned = used.Any(p => p.Skin is not null);
        var boneNames = new List<string>();
        var binds = new List<Matrix4x4>();
        foreach (var skin in used.Select(p => p.Skin).OfType<GltfSkin>().Distinct())
            for (int j = 0; j < skin.JointNames.Length; j++)
                if (!boneNames.Contains(skin.JointNames[j]))
                {
                    boneNames.Add(skin.JointNames[j]);
                    binds.Add(MeshImport.ToGame(skin.InverseBinds[j]));
                }
        if (boneNames.Count > 255) throw new InvalidDataException(L.T("MeshImport.TooManyBones", boneNames.Count));
        if (skinned && used.Any(p => p.Skin is null)) warnings.Add(L.T("NewMesh.StaticPartsOnFirstBone", boneNames.FirstOrDefault() ?? "-"));
        int perVertex = skinned && used.Any(MoreThanFour) ? 8 : skinned ? 4 : 0;

        // the chunks, LOD by LOD (the file's LODs, numbered from 0 in order)
        bool colours = used.Any(p => p.Colors.Length > 0 || p.W3Colors.Length > 0);
        var like = new MeshChunk
        {
            Index = 0, Material = 0, LodMask = 1, RenderMask = 39, Skinned = skinned, Positions = [], Normals = [], Uvs = [], Indices = [],
            BonesPerVertex = perVertex, CookedInfo = Description(skinned, colours),
        };
        var chunks = new List<MeshChunk>();
        var lodGroups = used.GroupBy(p => MeshImport.LodOf(p) ?? 0).OrderBy(g => g.Key).ToList();
        if (lodGroups.Count > 8) throw new InvalidDataException(L.T("NewMesh.TooManyLods", lodGroups.Count));
        for (int lod = 0; lod < lodGroups.Count; lod++)
        {
            int before = chunks.Count;
            foreach (var group in lodGroups[lod].GroupBy(p => keys.IndexOf(p.Material ?? -1)).OrderBy(g => g.Key))
                foreach (var piece in MeshImport.Split([.. group]))
                    chunks.Add(MeshImport.Build(piece, like, [], group.Key, 1 << lod, boneNames, [], warnings));
            var made = chunks.Skip(before).ToList();
            lines.Add(L.T("MeshImport.Lod", lod, made.Count, made.Sum(c => c.Positions.Length), made.Sum(c => c.Triangles)));
        }

        // the materials, and the pictures of those made here
        string folder = Path.GetDirectoryName(meshGamePath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(meshGamePath);
        var pictures = new List<NewMeshPicture>();
        var materials = new List<JsonObject>();
        var imports = new List<(string Path, string Class)> { (StandardShader, "CMaterialGraph") };
        var handles = new JsonArray();
        bool twoSided = false;
        for (int slot = 0; slot < keys.Count; slot++)
        {
            var part = used.First(p => (p.Material ?? -1) == keys[slot]);
            var info = part.MaterialInfo;
            twoSided |= info?.DoubleSided == true;
            if (part.MaterialExtras?["w3Source"] is JsonNode s && (string?)s is { } source && source.EndsWith(".w2mi", StringComparison.OrdinalIgnoreCase)
                && readFile(source) is not null)
            {
                imports.Add((source, "CMaterialInstance"));
                handles.Add("import:" + source);
                lines.Add(L.T("NewMesh.MaterialFromGame", slot, source));
                continue;
            }
            string name = Safe(info?.Name ?? $"material_{slot}");
            var parameters = new JsonArray();
            if (Colour(info) is { } colourTga)
            {
                string path = Path.Combine(folder, $"{stem}_{name}_d01.xbm");
                pictures.Add(new NewMeshPicture(Path.ChangeExtension(path, ".tga"), colourTga));
                imports.Add((path, "CBitmapTexture"));
                parameters.Add(new JsonObject { ["name"] = "Diffuse", ["type"] = "handle:ITexture", ["value"] = "import:" + path });
            }
            if (Normal(info) is { } normalTga)
            {
                string path = Path.Combine(folder, $"{stem}_{name}_n01.xbm");
                pictures.Add(new NewMeshPicture(Path.ChangeExtension(path, ".tga"), normalTga));
                imports.Add((path, "CBitmapTexture"));
                parameters.Add(new JsonObject { ["name"] = "Normal", ["type"] = "handle:ITexture", ["value"] = "import:" + path });
            }
            if (info?.AlphaMode is "MASK" or "BLEND") warnings.Add(L.T("NewMesh.SeeThrough", info.Name));
            materials.Add(new JsonObject
            {
                ["$class"] = "CMaterialInstance",
                ["$parent"] = 0,
                ["$flags"] = 8192,
                ["baseMaterial"] = "import:" + StandardShader,
                ["$parameters"] = parameters,
            });
            handles.Add($"export:{materials.Count}");
            lines.Add(L.T("NewMesh.MaterialMade", slot, info?.Name ?? "-", parameters.Count));
        }

        // the mesh as the game keeps one, without geometry yet: the writer puts the chunks into it
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in chunks.SelectMany(c => c.Positions))
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var centre = (min + max) / 2;
        float radius = chunks.SelectMany(c => c.Positions).Max(p => Vector3.Distance(p, centre));
        var distances = Enumerable.Range(0, lodGroups.Count).Select(l => l == 0 ? 0f : 10f * (1 << (l - 1))).ToList();
        var cooked = new JsonObject
        {
            ["renderLODs"] = new JsonArray([.. distances.Select(d => (JsonNode?)(double)d)]),
            ["renderChunks"] = "",
            ["renderBuffer"] = 1,
            ["quantizationScale"] = Vector(max - min, 0),
            ["quantizationOffset"] = Vector(min, 1),
            ["vertexBufferSize"] = 0,
            ["indexBufferSize"] = 0,
            ["indexBufferOffset"] = 0,
            ["blasBuffer"] = 0,
        };
        var mesh = new JsonObject { ["$class"] = "CMesh", ["$flags"] = 8192, ["materials"] = handles };
        mesh["boundingBox"] = new JsonObject { ["Min"] = Vector(min, 0), ["Max"] = Vector(max, 1) };
        mesh["autoHideDistance"] = (double)Math.Max(100, distances[^1] * 2);
        if (twoSided) mesh["isTwoSided"] = true;
        if (colours) mesh["useExtraStreams"] = true;
        mesh["generalizedMeshRadius"] = (double)radius;
        mesh["chunks"] = new JsonArray();
        if (!skinned) mesh["isStatic"] = true;
        mesh["cookedData"] = cooked;
        mesh["internalVersion"] = 2;
        mesh["$tail"] = Convert.ToBase64String(Tail(distances));

        var objects = new JsonArray { mesh };
        foreach (var m in materials) objects.Add(m);
        var root = new JsonObject
        {
            ["$format"] = Cr2wJson.FormatName,
            ["$formatVersion"] = Cr2wJson.FormatVersion,
            ["objects"] = objects,
            ["file"] = new JsonObject
            {
                ["version"] = 164,
                ["flags"] = 0,
                ["timestamp"] = 0,
                ["build"] = 0,
                ["chunks"] = 6,
                ["names"] = new JsonArray(""),
                ["imports"] = new JsonArray([.. imports.DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(i => (JsonNode?)new JsonObject { ["path"] = i.Path, ["class"] = i.Class })]),
                ["buffers"] = new JsonArray(new JsonObject { ["index"] = 1, ["flags"] = 0, ["diskSize"] = 0, ["memorySize"] = 0, ["crc"] = 0 }),
            },
        };
        // through text, so every value reads as the JSON the converter takes would have it
        var template = MeshFile.Read(Cr2wJson.FromJsonText(root.ToJsonString()), _ => null);
        var result = MeshWriter.Write(template, chunks, [.. boneNames], [.. binds]);
        if (skinned) lines.Add(L.T("NewMesh.Bones", boneNames.Count, perVertex));
        return new NewMeshResult(result, pictures, lines, warnings);
    }

    /// <summary>
    /// A cooked chunk's description for a new chunk, as the game's are for one like it: its vertex type (2 static, 3
    /// skinned) and layout (static 2, or 3 with the extra stream; skinned 4, or 5), the UV and normal streams asked for
    /// (and the extra one), 29 where every chunk has it, and drawn in the scene and both shadow cascades by itself.
    /// </summary>
    private static byte[] Description(bool skinned, bool extra)
    {
        var d = new byte[37];
        d[0] = (byte)(skinned ? 3 : 2);
        d[5] = 1;
        d[9] = 1;
        if (extra) d[13] = 1;
        d[21] = (byte)(skinned ? extra ? 5 : 4 : extra ? 3 : 2);
        d[26] = 29;
        d[34] = 39;
        d[35] = 39;
        return d;
    }

    private static bool MoreThanFour(GltfPrimitive p)
    {
        if (p.InfluencesPerVertex <= 4) return false;
        for (int v = 0; v < p.Positions.Length; v++)
            if (Enumerable.Range(0, p.InfluencesPerVertex).Count(k => p.Weights[v * p.InfluencesPerVertex + k] > 0) > 4) return true;
        return false;
    }

    /// <summary>The LODs with no chunks and their distances, and empty bone lists; the writer fills the chunks in.</summary>
    private static byte[] Tail(List<float> distances)
    {
        var ms = new MemoryStream();
        ms.WriteByte((byte)distances.Count);
        foreach (float d in distances)
        {
            ms.WriteByte(0x80);
            ms.Write(BitConverter.GetBytes(d));
        }
        for (int i = 0; i < 4; i++) ms.WriteByte(0x80);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- pictures

    /// <summary>The material's colour picture times its colour factor, or a small one of the factor alone; null when it's plain white.</summary>
    private static byte[]? Colour(GltfMaterialInfo? info)
    {
        if (info is null) return null;
        var f = info.ColourFactor;
        bool white = Vector4.DistanceSquared(f, Vector4.One) < 1e-6f;
        if (info.Colour is { } image && Decode(image) is var (rgba, w, h))
        {
            if (!white)
                for (int i = 0; i < rgba.Length; i += 4)
                    for (int c = 0; c < 4; c++) rgba[i + c] = (byte)Math.Clamp(MathF.Round(rgba[i + c] * f[c]), 0, 255);
            return Tga(rgba, w, h);
        }
        if (white) return null;
        var solid = new byte[4 * 4 * 4];
        for (int i = 0; i < solid.Length; i += 4)
            for (int c = 0; c < 4; c++) solid[i + c] = (byte)Math.Clamp(MathF.Round(f[c] * 255), 0, 255);
        return Tga(solid, 4, 4);
    }

    /// <summary>
    /// The game's normal map: the file's normal map's red, green and blue, with the roughness in alpha (the file keeps it
    /// in its metal-roughness picture's green, times its roughness factor). A flat one when there's only roughness; null
    /// when there's neither.
    /// </summary>
    private static byte[]? Normal(GltfMaterialInfo? info)
    {
        if (info is null) return null;
        var normal = info.Normal is { } n ? Decode(n) : null;
        var rough = info.MetalRoughness is { } r ? Decode(r) : null;
        if (normal is null && rough is null) return null;
        int w = normal?.Width ?? rough!.Value.Width, h = normal?.Height ?? rough!.Value.Height;
        var result = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (normal is { } nm) Array.Copy(nm.Rgba, i, result, i, 3);
                else { result[i] = 128; result[i + 1] = 128; result[i + 2] = 255; }
                float roughness = info.RoughnessFactor;
                if (rough is { } rm)
                {
                    int rx = x * rm.Width / w, ry = y * rm.Height / h;
                    roughness *= rm.Rgba[(ry * rm.Width + rx) * 4 + 1] / 255f;
                }
                result[i + 3] = (byte)Math.Clamp(MathF.Round(roughness * 255), 0, 255);
            }
        return Tga(result, w, h);
    }

    private static (byte[] Rgba, int Width, int Height)? Decode(GltfImage image)
    {
        try
        {
            return Pictures.Read(image.MimeType == "image/jpeg" ? "picture.jpg" : "picture.png", image.Data);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A 32-bit Targa, top row first: what Build makes a new texture from.</summary>
    private static byte[] Tga(byte[] rgba, int width, int height)
    {
        var tga = new byte[18 + rgba.Length];
        tga[2] = 2;
        BitConverter.TryWriteBytes(tga.AsSpan(12), (ushort)width);
        BitConverter.TryWriteBytes(tga.AsSpan(14), (ushort)height);
        tga[16] = 32;
        tga[17] = 0x28;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            tga[18 + i] = rgba[i + 2];
            tga[18 + i + 1] = rgba[i + 1];
            tga[18 + i + 2] = rgba[i];
            tga[18 + i + 3] = rgba[i + 3];
        }
        return tga;
    }

    /// <summary>A material's name as it can go in a file's name.</summary>
    private static string Safe(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? char.ToLowerInvariant(c) : '_').ToArray();
        string safe = new string(chars).Trim('_');
        return safe.Length == 0 ? "material" : safe.Length > 40 ? safe[..40] : safe;
    }

    private static JsonObject Vector(Vector3 v, double w) => new() { ["X"] = (double)v.X, ["Y"] = (double)v.Y, ["Z"] = (double)v.Z, ["W"] = w };
}
