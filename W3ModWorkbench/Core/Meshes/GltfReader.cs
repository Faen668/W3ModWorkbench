using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>One primitive of a glTF mesh as read: its vertices' attributes, in glTF's own Y-up space, and its triangles.</summary>
public sealed class GltfPrimitive
{
    /// <summary>The node it's drawn by, its mesh, and the material's index and name (null without one).</summary>
    public required string NodeName { get; init; }
    public required string MeshName { get; init; }
    public int? Material { get; init; }
    public string? MaterialName { get; init; }

    /// <summary>The node's and the material's extras, and the primitive's own, where the tool that wrote it kept them.</summary>
    public JsonObject? NodeExtras { get; init; }
    public JsonObject? MaterialExtras { get; init; }
    public JsonObject? Extras { get; init; }

    /// <summary>The material's pictures and settings, or null without a material.</summary>
    public GltfMaterialInfo? MaterialInfo { get; init; }

    public required Vector3[] Positions { get; init; }
    public Vector3[] Normals { get; init; } = [];
    public Vector4[] Tangents { get; init; } = [];
    public Vector2[] Uvs { get; init; } = [];

    /// <summary>TEXCOORD_1, the game's second UV set (in the extra stream), or empty.</summary>
    public Vector2[] Uvs2 { get; init; } = [];

    /// <summary>COLOR_0 as red, green, blue and alpha from 0 to 1, or empty.</summary>
    public Vector4[] Colors { get; init; } = [];

    /// <summary>The game's own extra stream as this app writes it (<c>_W3_COLOR</c>), as bytes, four a vertex; or empty.</summary>
    public byte[] W3Colors { get; init; } = [];

    /// <summary>Each vertex's joints (into the skin's list) and weights, as many sets of four as the file has.</summary>
    public int[] Joints { get; init; } = [];
    public float[] Weights { get; init; } = [];
    public int InfluencesPerVertex { get; init; }

    /// <summary>The skin it's bound to: its joints' names and their inverse bind matrices. Null for a static primitive.</summary>
    public GltfSkin? Skin { get; init; }

    /// <summary>Three a triangle, counter-clockwise as glTF has them.</summary>
    public required int[] Indices { get; init; }
}

public sealed record GltfSkin(string[] JointNames, Matrix4x4[] InverseBinds);

/// <summary>A picture inside or beside a glTF: its bytes and what they are (image/png, image/jpeg).</summary>
public sealed record GltfImage(string Name, byte[] Data, string MimeType);

/// <summary>
/// A glTF material as a new mesh makes its own of: its colour, normal map and metal-roughness pictures (null when it
/// has none), the factors they're multiplied by, and how it's drawn.
/// </summary>
public sealed record GltfMaterialInfo(string Name, GltfImage? Colour, GltfImage? Normal, GltfImage? MetalRoughness,
    Vector4 ColourFactor, float RoughnessFactor, bool DoubleSided, string AlphaMode);

/// <summary>
/// Reads the meshes of a glTF 2.0 file, binary (.glb) or text (.gltf with its .bin beside it or inside as base64): every
/// triangle primitive of every node in the scene, with a static one's node placement applied (a skinned one's isn't, as
/// glTF says), and the skins. Matrices are read as glTF keeps them, by columns, into System.Numerics' row-vector form.
/// </summary>
public static class GltfReader
{
    public static List<GltfPrimitive> Read(string path)
    {
        var data = File.ReadAllBytes(path);
        JsonObject root;
        byte[]? glbBin = null;
        if (data.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x46546C67)
        {
            int at = 12;
            root = null!;
            while (at + 8 <= data.Length)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
                if (at + 8 + (long)length > data.Length) throw new InvalidDataException(L.T("GltfReader.Cut"));
                if (type == 0x4E4F534A) root = JsonNode.Parse(Encoding.UTF8.GetString(data, at + 8, length))!.AsObject();
                else if (type == 0x004E4942) glbBin = data.AsSpan(at + 8, length).ToArray();
                at += 8 + length;
            }
            if (root is null) throw new InvalidDataException(L.T("GltfReader.NoJson"));
        }
        else
        {
            root = JsonNode.Parse(Encoding.UTF8.GetString(data))?.AsObject() ?? throw new InvalidDataException(L.T("GltfReader.NoJson"));
        }

        var buffers = new List<byte[]>();
        foreach (var b in root["buffers"] as JsonArray ?? [])
        {
            string? uri = (string?)b?["uri"];
            if (uri is null) buffers.Add(glbBin ?? []);
            else if (uri.StartsWith("data:", StringComparison.Ordinal)) buffers.Add(Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]));
            else buffers.Add(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path) ?? ".", Uri.UnescapeDataString(uri))));
        }
        var reader = new Accessors(root, buffers);
        var materialInfos = Materials(root, buffers, path);

        var nodes = root["nodes"] as JsonArray ?? [];
        var meshes = root["meshes"] as JsonArray ?? [];
        var materials = root["materials"] as JsonArray ?? [];
        var skins = new List<GltfSkin>();
        foreach (var s in root["skins"] as JsonArray ?? [])
        {
            var joints = (s!["joints"] as JsonArray ?? []).Select(j => (int)j!).ToArray();
            var binds = s["inverseBindMatrices"] is JsonNode ib ? reader.Floats((int)ib) : [];
            skins.Add(new GltfSkin(
                [.. joints.Select(j => (string?)nodes[j]?["name"] ?? $"joint_{j}")],
                [.. joints.Select((_, i) => binds.Length >= (i + 1) * 16 ? Matrix(binds.AsSpan(i * 16, 16)) : Matrix4x4.Identity)]));
        }

        // the nodes in the scene, each with where it's placed in the world
        var result = new List<GltfPrimitive>();
        int scene = (int?)root["scene"] ?? 0;
        var roots = (root["scenes"] as JsonArray)?.ElementAtOrDefault(scene)?["nodes"] as JsonArray;
        var todo = new Stack<(int Node, Matrix4x4 Parent)>();
        if (roots is not null) foreach (var r in roots) todo.Push(((int)r!, Matrix4x4.Identity));
        else for (int i = 0; i < nodes.Count; i++) if (!nodes.Any(n => (n?["children"] as JsonArray)?.Any(c => (int)c! == i) == true)) todo.Push((i, Matrix4x4.Identity));
        var seen = new HashSet<int>();
        while (todo.Count > 0)
        {
            var (index, parent) = todo.Pop();
            if (!seen.Add(index) || nodes[index] is not JsonObject node) continue;
            var world = Local(node) * parent;
            foreach (var c in node["children"] as JsonArray ?? []) todo.Push(((int)c!, world));
            if (node["mesh"] is not JsonNode meshIndex || meshes[(int)meshIndex] is not JsonObject mesh) continue;
            var skin = node["skin"] is JsonNode s ? skins[(int)s] : null;
            foreach (var p in mesh["primitives"] as JsonArray ?? [])
            {
                if (p is not JsonObject primitive || ((int?)primitive["mode"] ?? 4) != 4) continue;
                var attributes = primitive["attributes"] as JsonObject ?? [];
                if (attributes["POSITION"] is not JsonNode position) continue;
                var positions = reader.Vector3s((int)position);
                var normals = attributes["NORMAL"] is JsonNode n ? reader.Vector3s((int)n) : [];
                if (skin is null && !world.IsIdentity)
                {
                    // a static primitive is where its node puts it
                    positions = [.. positions.Select(v => Vector3.Transform(v, world))];
                    normals = [.. normals.Select(v => Vector3.Normalize(Vector3.TransformNormal(v, world)))];
                }
                var tangents = attributes["TANGENT"] is JsonNode t ? reader.Vector4s((int)t) : [];
                if (skin is null && !world.IsIdentity)
                    tangents = [.. tangents.Select(v => new Vector4(Vector3.Normalize(Vector3.TransformNormal(new Vector3(v.X, v.Y, v.Z), world)), v.W))];
                int sets = 0;
                while (attributes[$"JOINTS_{sets}"] is not null && attributes[$"WEIGHTS_{sets}"] is not null) sets++;
                var joints = new int[positions.Length * sets * 4];
                var weights = new float[positions.Length * sets * 4];
                for (int set = 0; set < sets; set++)
                {
                    var j = reader.Ints((int)attributes[$"JOINTS_{set}"]!);
                    var w = reader.Floats((int)attributes[$"WEIGHTS_{set}"]!);
                    for (int v = 0; v < positions.Length; v++)
                        for (int k = 0; k < 4; k++)
                        {
                            joints[(v * sets + set) * 4 + k] = v * 4 + k < j.Length ? j[v * 4 + k] : 0;
                            weights[(v * sets + set) * 4 + k] = v * 4 + k < w.Length ? w[v * 4 + k] : 0;
                        }
                }
                int? material = (int?)primitive["material"];
                var materialNode = material is int m && m < materials.Count ? materials[m] : null;
                result.Add(new GltfPrimitive
                {
                    NodeName = (string?)node["name"] ?? (string?)mesh["name"] ?? $"node_{index}",
                    MeshName = (string?)mesh["name"] ?? "",
                    Material = material,
                    MaterialName = (string?)materialNode?["name"],
                    NodeExtras = node["extras"] as JsonObject,
                    MaterialExtras = materialNode?["extras"] as JsonObject,
                    MaterialInfo = material is int mi && mi < materialInfos.Count ? materialInfos[mi] : null,
                    Extras = primitive["extras"] as JsonObject,
                    Positions = positions,
                    Normals = normals,
                    Tangents = tangents,
                    Uvs = attributes["TEXCOORD_0"] is JsonNode uv ? reader.Vector2s((int)uv) : [],
                    Uvs2 = attributes["TEXCOORD_1"] is JsonNode uv2 ? reader.Vector2s((int)uv2) : [],
                    Colors = attributes["COLOR_0"] is JsonNode col ? reader.Colours((int)col) : [],
                    W3Colors = attributes["_W3_COLOR"] is JsonNode w3 ? reader.Bytes4((int)w3) : [],
                    Joints = sets > 0 && skin is not null ? joints : [],
                    Weights = sets > 0 && skin is not null ? weights : [],
                    InfluencesPerVertex = skin is not null ? sets * 4 : 0,
                    Skin = sets > 0 ? skin : null,
                    Indices = primitive["indices"] is JsonNode ix ? reader.Ints((int)ix) : [.. Enumerable.Range(0, positions.Length)],
                });
            }
        }
        return result;
    }

    /// <summary>Each material's pictures and settings, in the file's order.</summary>
    private static List<GltfMaterialInfo> Materials(JsonObject root, List<byte[]> buffers, string path)
    {
        var images = root["images"] as JsonArray ?? [];
        var textures = root["textures"] as JsonArray ?? [];
        var views = root["bufferViews"] as JsonArray ?? [];
        GltfImage? Image(JsonNode? textureInfo)
        {
            if (textureInfo?["index"] is not JsonNode t || (int)t >= textures.Count || textures[(int)t]?["source"] is not JsonNode source
                || (int)source >= images.Count || images[(int)source] is not JsonObject image) return null;
            string name = (string?)image["name"] ?? $"image_{(int)source}";
            string mime = (string?)image["mimeType"] ?? "";
            byte[]? data = null;
            if (image["bufferView"] is JsonNode v && views[(int)v] is JsonObject view)
            {
                var buffer = buffers[(int?)view["buffer"] ?? 0];
                int offset = (int?)view["byteOffset"] ?? 0, length = (int)view["byteLength"]!;
                if (offset + length <= buffer.Length) data = buffer.AsSpan(offset, length).ToArray();
            }
            else if ((string?)image["uri"] is { } uri)
            {
                if (uri.StartsWith("data:", StringComparison.Ordinal))
                {
                    mime = uri[5..uri.IndexOf(';')];
                    data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
                }
                else
                {
                    string file = Path.Combine(Path.GetDirectoryName(path) ?? ".", Uri.UnescapeDataString(uri));
                    if (File.Exists(file)) data = File.ReadAllBytes(file);
                    if (mime.Length == 0) mime = Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
                }
            }
            return data is null ? null : new GltfImage(name, data, mime);
        }
        var result = new List<GltfMaterialInfo>();
        foreach (var m in root["materials"] as JsonArray ?? [])
        {
            var pbr = m?["pbrMetallicRoughness"];
            var factor = pbr?["baseColorFactor"] is JsonArray f && f.Count == 4 ? new Vector4(F(f[0]), F(f[1]), F(f[2]), F(f[3])) : Vector4.One;
            result.Add(new GltfMaterialInfo((string?)m?["name"] ?? $"material_{result.Count}", Image(pbr?["baseColorTexture"]), Image(m?["normalTexture"]),
                Image(pbr?["metallicRoughnessTexture"]), factor, pbr?["roughnessFactor"] is JsonNode r ? F(r) : 1,
                (bool?)m?["doubleSided"] ?? false, (string?)m?["alphaMode"] ?? "OPAQUE"));
        }
        return result;
    }

    private static Matrix4x4 Local(JsonObject node)
    {
        if (node["matrix"] is JsonArray m && m.Count == 16)
            return Matrix([.. m.Select(x => (float)(double)x!)]);
        var t = node["translation"] is JsonArray ta ? new Vector3(F(ta[0]), F(ta[1]), F(ta[2])) : Vector3.Zero;
        var r = node["rotation"] is JsonArray ra ? new Quaternion(F(ra[0]), F(ra[1]), F(ra[2]), F(ra[3])) : Quaternion.Identity;
        var s = node["scale"] is JsonArray sa ? new Vector3(F(sa[0]), F(sa[1]), F(sa[2])) : Vector3.One;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    private static float F(JsonNode? n) => n is null ? 0 : (float)(double)n;

    /// <summary>A glTF matrix, by columns, as System.Numerics has it for row vectors: the same sixteen numbers in order.</summary>
    private static Matrix4x4 Matrix(ReadOnlySpan<float> f) =>
        new(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);

    /// <summary>Reads accessors' values, whatever their component type, normalised ones as 0 to 1 (or −1 to 1).</summary>
    private sealed class Accessors(JsonObject root, List<byte[]> buffers)
    {
        private readonly JsonArray _accessors = root["accessors"] as JsonArray ?? [];
        private readonly JsonArray _views = root["bufferViews"] as JsonArray ?? [];

        /// <summary>Every component of every element, as floats.</summary>
        private float[] Read(int index, out int width)
        {
            var a = _accessors[index] as JsonObject ?? throw new InvalidDataException(L.T("GltfReader.BadAccessor", index));
            if (a["sparse"] is not null) throw new InvalidDataException(L.T("GltfReader.Sparse"));
            int count = (int)a["count"]!;
            width = (string?)a["type"] switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 1 };
            int type = (int)a["componentType"]!;
            bool normalized = (bool?)a["normalized"] ?? false;
            int size = type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
            var result = new float[count * width];
            if (a["bufferView"] is not JsonNode viewIndex) return result;
            var view = _views[(int)viewIndex]!;
            var buffer = buffers[(int?)view["buffer"] ?? 0];
            long offset = ((long?)view["byteOffset"] ?? 0) + ((long?)a["byteOffset"] ?? 0);
            int stride = (int?)view["byteStride"] ?? width * size;
            if (offset + (long)(count - 1) * stride + width * size > buffer.Length && count > 0)
                throw new InvalidDataException(L.T("GltfReader.BadAccessor", index));
            for (int e = 0; e < count; e++)
                for (int c = 0; c < width; c++)
                {
                    var at = buffer.AsSpan((int)(offset + (long)e * stride + c * size));
                    result[e * width + c] = type switch
                    {
                        5126 => BinaryPrimitives.ReadSingleLittleEndian(at),
                        5121 => normalized ? at[0] / 255f : at[0],
                        5120 => normalized ? Math.Max((sbyte)at[0] / 127f, -1) : (sbyte)at[0],
                        5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(at) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(at),
                        5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(at) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(at),
                        5125 => BinaryPrimitives.ReadUInt32LittleEndian(at),
                        _ => 0,
                    };
                }
            return result;
        }

        public float[] Floats(int index) => Read(index, out _);

        public int[] Ints(int index) => [.. Read(index, out _).Select(f => (int)f)];

        public Vector2[] Vector2s(int index)
        {
            var f = Read(index, out int w);
            return [.. Enumerable.Range(0, f.Length / w).Select(i => new Vector2(f[i * w], f[i * w + 1]))];
        }

        public Vector3[] Vector3s(int index)
        {
            var f = Read(index, out int w);
            return [.. Enumerable.Range(0, f.Length / w).Select(i => new Vector3(f[i * w], f[i * w + 1], f[i * w + 2]))];
        }

        public Vector4[] Vector4s(int index)
        {
            var f = Read(index, out int w);
            return [.. Enumerable.Range(0, f.Length / w).Select(i => new Vector4(f[i * w], f[i * w + 1], f[i * w + 2], w > 3 ? f[i * w + 3] : 1))];
        }

        public Vector4[] Colours(int index) => Vector4s(index);

        /// <summary>Four bytes an element, from a colour of normalised bytes, shorts or floats.</summary>
        public byte[] Bytes4(int index)
        {
            var f = Read(index, out int w);
            int n = f.Length / w;
            var result = new byte[n * 4];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 4; c++)
                    result[i * 4 + c] = c < w ? (byte)Math.Clamp(MathF.Round(f[i * w + c] * 255), 0, 255) : (byte)255;
            return result;
        }
    }
}
