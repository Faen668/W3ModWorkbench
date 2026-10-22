using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using W3ModWorkbench.Core.Animation;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>A picture to put in a glTF, as a PNG, under a name that's the same for the same texture (its path).</summary>
public sealed record GltfPicture(string Name, byte[] Png);

/// <summary>
/// A mesh material as it goes into a glTF: its name, the material it is (for where it came from and its textures'
/// paths), and its pictures: the colour, the normal map with its blue made from red and green, and the roughness the
/// normal map's alpha keeps, in green as glTF reads it. A picture that wasn't found is null.
/// </summary>
public sealed record GltfMaterial(string Name, MeshMaterial? Material, GltfPicture? Colour = null, GltfPicture? Normal = null, GltfPicture? Roughness = null);

/// <summary>
/// Writes a mesh as a binary glTF (.glb), which Blender, 3ds Max, Maya and most other tools read: its chunks as
/// primitives, a node for each LOD, its materials with their textures inside the file, and for a skinned mesh its bones
/// with each vertex's weights. The bones are placed as the mesh was bound to them; a rig, when one is given, says which
/// bone is whose child, else they all hang from the armature.
/// <para>
/// The game is Z-up and glTF Y-up, so everything is turned a quarter about X (x, y, z becomes x, z, −y), which is a turn
/// and not a mirror, so tangents and their handedness hold. The game winds its triangles the other way round from glTF,
/// so each triangle's last two corners are swapped. UVs start at the top left in both.
/// </para>
/// <para>
/// What the game knows that glTF has no place for goes in <c>extras</c>, for reading the file back: the mesh's path and
/// LOD distances, each material's source, shader and texture paths, and each primitive's chunk, material slot, LODs and
/// render mask.
/// </para>
/// </summary>
public static class GltfExport
{
    private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;
    private const int UnsignedByte = 5121, UnsignedShort = 5123, UnsignedInt = 5125, Float = 5126;

    /// <summary>Turns the game's Z-up space to glTF's Y-up, for a row vector: (x, y, z) to (x, z, −y).</summary>
    private static readonly Matrix4x4 ToYUp = new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    private static readonly Matrix4x4 FromYUp = Matrix4x4.Transpose(ToYUp);

    /// <summary>
    /// A name for each of a mesh's materials, as tools list them: the .w2mi's file name for one in a file, else the one
    /// it's based on, else its number. A name two share gets the material's number after it.
    /// </summary>
    public static Dictionary<int, string> MaterialNames(IReadOnlyDictionary<int, MeshMaterial?> materials)
    {
        var names = materials.ToDictionary(m => m.Key, m => m.Value is { } material
            ? material.IsFile ? Path.GetFileNameWithoutExtension(material.Source)
            : material.Bases.FirstOrDefault() is { } b && !material.IsGraph ? Path.GetFileNameWithoutExtension(b)
            : $"material_{m.Key}" // loc-ignore
            : $"material_{m.Key}"); // loc-ignore
        var shared = names.Values.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (int slot in names.Keys.ToList())
            if (shared.Contains(names[slot])) names[slot] += $"_{slot}";
        return names;
    }

    /// <param name="meshPath">The mesh's path in the game, kept in the file and used for its name.</param>
    /// <param name="lods">Each LOD to write, with the chunks to write of it.</param>
    /// <param name="rigs">The rigs the bones are in, best first, for which is whose child. Empty to hang them all from the armature.</param>
    /// <param name="materials">The mesh's materials by their number in it. A chunk whose material isn't given gets a plain one.</param>
    public static void Write(Stream output, string meshPath, MeshFile mesh, IReadOnlyList<(int Lod, IReadOnlyList<MeshChunk> Chunks)> lods,
        IReadOnlyList<(Skeleton Rig, string Path)> rigs, IReadOnlyDictionary<int, GltfMaterial> materials)
    {
        var gltf = new Builder();
        string name = Path.GetFileNameWithoutExtension(meshPath);
        var sceneNodes = new JsonArray();

        int? skin = null;
        if (mesh.BoneNames.Length > 0 && lods.Any(l => l.Chunks.Any(c => c.Skinned)))
        {
            var (armature, skinIndex) = AddSkeleton(gltf, mesh, rigs.Select(r => r.Rig).ToList());
            sceneNodes.Add(armature);
            skin = skinIndex;
        }

        var materialIndex = new Dictionary<int, int>();
        int MaterialOf(int slot)
        {
            if (!materialIndex.TryGetValue(slot, out int index))
                materialIndex[slot] = index = AddMaterial(gltf, slot, materials.GetValueOrDefault(slot));
            return index;
        }

        foreach (var (lod, chunks) in lods)
        {
            string lodName = lods.Count > 1 ? $"{name}_lod{lod}" : name;
            // a skinned mesh's primitives all need bones, so chunks that aren't skinned go in a mesh of their own
            var groups = chunks.Where(c => c.Indices.Length > 0).GroupBy(c => skin is not null && c.Skinned).OrderByDescending(g => g.Key).ToList();
            foreach (var group in groups)
            {
                var primitives = new JsonArray();
                foreach (var chunk in group) primitives.Add(AddPrimitive(gltf, chunk, MaterialOf(chunk.Material), group.Key));
                int meshIndex = gltf.Meshes.Count;
                gltf.Meshes.Add(new JsonObject { ["name"] = lodName, ["primitives"] = primitives });
                var node = new JsonObject
                {
                    ["name"] = !group.Key && groups.Count > 1 ? lodName + "_static" : lodName, // loc-ignore
                    ["mesh"] = meshIndex,
                    ["extras"] = new JsonObject { ["lod"] = lod },
                };
                if (group.Key) node["skin"] = skin;
                sceneNodes.Add(gltf.AddNode(node));
            }
        }

        var lodDistances = new JsonArray();
        foreach (float d in mesh.LodDistances) lodDistances.Add(d);
        var root = new JsonObject
        {
            ["asset"] = new JsonObject
            {
                ["version"] = "2.0",
                ["generator"] = "W3 Mod Workbench", // loc-ignore
                ["extras"] = new JsonObject
                {
                    ["w3Mesh"] = meshPath,
                    ["w3LodDistances"] = lodDistances,
                    ["w3Rigs"] = new JsonArray([.. rigs.Select(r => (JsonNode?)r.Path)]),
                },
            },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["name"] = name, ["nodes"] = sceneNodes }),
        };
        gltf.WriteGlb(output, root);
    }

    /// <summary>
    /// The mesh's bones as nodes under an armature, each placed where the mesh was bound to it, and a skin listing them
    /// in the mesh's order, so the vertices' bone numbers stay as the game keeps them.
    /// </summary>
    private static (int Armature, int Skin) AddSkeleton(Builder gltf, MeshFile mesh, List<Skeleton> rigs)
    {
        int count = mesh.BoneNames.Length;
        var world = new Matrix4x4[count];
        var inverseBinds = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            var bind = i < mesh.BoneInverseBinds.Length ? mesh.BoneInverseBinds[i] : Matrix4x4.Identity;
            // the same matrix seen in Y-up space: undo the turn, apply it, turn back
            inverseBinds[i] = FromYUp * bind * ToYUp;
            world[i] = Matrix4x4.Invert(inverseBinds[i], out var w) ? w : Matrix4x4.Identity;
        }

        var parents = Parents(mesh.BoneNames, rigs);
        int first = gltf.Nodes.Count;
        var children = new List<int>[count];
        for (int i = 0; i < count; i++) children[i] = [];
        var roots = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            if (parents[i] >= 0) children[parents[i]].Add(first + i);
            else roots.Add(first + i);
        }
        var placed = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            var local = parents[i] >= 0 && Matrix4x4.Invert(world[parents[i]], out var up) ? world[i] * up : world[i];
            var node = new JsonObject { ["name"] = mesh.BoneNames[i] };
            placed[i] = Place(node, local);
            if (children[i].Count > 0) node["children"] = new JsonArray([.. children[i].Select(c => (JsonNode?)c)]);
            gltf.AddNode(node);
        }

        // a bone the game binds with a little shear can't be placed exactly by translation, rotation and scale, so the
        // inverse binds are made from where the bones were placed: the mesh then sits exactly where the game has it
        var placedWorld = new Matrix4x4?[count];
        Matrix4x4 WorldOf(int i) => placedWorld[i] ??= parents[i] >= 0 ? placed[i] * WorldOf(parents[i]) : placed[i];
        var binds = new byte[count * 64];
        for (int i = 0; i < count; i++)
            WriteMatrix(binds.AsSpan(i * 64), Matrix4x4.Invert(WorldOf(i), out var inverse) ? inverse : inverseBinds[i]);
        int bindsAccessor = gltf.AddAccessor(binds, Float, count, "MAT4", target: null);
        int skin = gltf.Skins.Count;
        int armature = gltf.AddNode(new JsonObject { ["name"] = "Armature", ["children"] = roots }); // loc-ignore
        gltf.Skins.Add(new JsonObject
        {
            ["name"] = "Armature", // loc-ignore
            ["inverseBindMatrices"] = bindsAccessor,
            ["skeleton"] = armature,
            ["joints"] = new JsonArray([.. Enumerable.Range(first, count).Select(n => (JsonNode?)n)]),
        });
        return (armature, skin);
    }

    /// <summary>
    /// Each of the mesh's bones' parent among the mesh's bones: the nearest bone above it that the mesh is bound to
    /// too, in the first rig that has the bone, or −1 for none (or no rig has it).
    /// </summary>
    private static int[] Parents(string[] names, List<Skeleton> rigs)
    {
        var parents = Enumerable.Repeat(-1, names.Length).ToArray();
        var inMesh = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++) inMesh.TryAdd(names[i], i);
        for (int i = 0; i < names.Length; i++)
            foreach (var rig in rigs)
            {
                int r = Array.FindIndex(rig.Names, n => n.Equals(names[i], StringComparison.OrdinalIgnoreCase));
                if (r < 0) continue;
                for (int up = rig.Parents[r], steps = 0; up >= 0 && steps < rig.Count; up = rig.Parents[up], steps++)
                    if (inMesh.TryGetValue(rig.Names[up], out int parent) && parent != i)
                    {
                        parents[i] = parent;
                        break;
                    }
                break;
            }
        // a rig that lists a bone above its own parent would make a loop; such a bone hangs from the armature instead
        for (int i = 0; i < names.Length; i++)
        {
            int at = parents[i];
            for (int steps = 0; at >= 0 && steps <= names.Length; steps++) at = parents[at];
            if (at >= 0) parents[i] = -1;
        }
        return parents;
    }

    /// <summary>
    /// A node's place as translation, rotation and scale, which tools read best, or its matrix when it won't come apart.
    /// Returns the matrix the node now has, which a little shear in <paramref name="m"/> is left out of.
    /// </summary>
    private static Matrix4x4 Place(JsonObject node, Matrix4x4 m)
    {
        if (Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation))
        {
            rotation = Quaternion.Normalize(rotation);
            if (Vector3.DistanceSquared(scale, Vector3.One) <= 1e-10f) scale = Vector3.One;
            if (translation != Vector3.Zero) node["translation"] = Numbers(translation.X, translation.Y, translation.Z);
            if (rotation != Quaternion.Identity) node["rotation"] = Numbers(rotation.X, rotation.Y, rotation.Z, rotation.W);
            if (scale != Vector3.One) node["scale"] = Numbers(scale.X, scale.Y, scale.Z);
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        }
        if (!m.IsIdentity)
            node["matrix"] = Numbers(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
        return m;
    }

    private static JsonObject AddPrimitive(Builder gltf, MeshChunk chunk, int material, bool skinned)
    {
        int n = chunk.Positions.Length;
        var attributes = new JsonObject();

        var positions = new byte[n * 12];
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int v = 0; v < n; v++)
        {
            var p = YUp(chunk.Positions[v]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            WriteVector(positions.AsSpan(v * 12), p);
        }
        attributes["POSITION"] = gltf.AddAccessor(positions, Float, n, "VEC3", ArrayBuffer,
            min: Numbers(min.X, min.Y, min.Z), max: Numbers(max.X, max.Y, max.Z));

        if (chunk.Normals.Length == n)
        {
            var normals = new byte[n * 12];
            for (int v = 0; v < n; v++) WriteVector(normals.AsSpan(v * 12), Unit(YUp(chunk.Normals[v]), Vector3.UnitY));
            attributes["NORMAL"] = gltf.AddAccessor(normals, Float, n, "VEC3", ArrayBuffer);

            if (chunk.Tangents.Length == n)
            {
                var tangents = new byte[n * 16];
                for (int v = 0; v < n; v++)
                {
                    var t = chunk.Tangents[v];
                    var normal = Unit(YUp(chunk.Normals[v]), Vector3.UnitY);
                    // glTF wants a unit tangent; one the game left empty, or along the normal, gets any at right angles to it
                    var tangent = YUp(new Vector3(t.X, t.Y, t.Z));
                    tangent -= normal * Vector3.Dot(normal, tangent);
                    if (tangent.LengthSquared() < 1e-12f) tangent = Vector3.Cross(normal, MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ);
                    WriteVector(tangents.AsSpan(v * 16), Vector3.Normalize(tangent));
                    BitConverter.TryWriteBytes(tangents.AsSpan(v * 16 + 12), t.W < 0 ? -1f : 1f);
                }
                attributes["TANGENT"] = gltf.AddAccessor(tangents, Float, n, "VEC4", ArrayBuffer);
            }
        }

        if (chunk.Uvs.Length == n)
        {
            var uvs = new byte[n * 8];
            for (int v = 0; v < n; v++)
            {
                BitConverter.TryWriteBytes(uvs.AsSpan(v * 8), chunk.Uvs[v].X);
                BitConverter.TryWriteBytes(uvs.AsSpan(v * 8 + 4), chunk.Uvs[v].Y);
            }
            attributes["TEXCOORD_0"] = gltf.AddAccessor(uvs, Float, n, "VEC2", ArrayBuffer);
        }

        if (chunk.Colors.Length == n * 3)
        {
            // three bytes a vertex is one short of four, which glTF wants each vertex's bytes to start on
            var colours = new byte[n * 4];
            for (int v = 0; v < n; v++)
            {
                chunk.Colors.AsSpan(v * 3, 3).CopyTo(colours.AsSpan(v * 4));
                colours[v * 4 + 3] = 255;
            }
            attributes["COLOR_0"] = gltf.AddAccessor(colours, UnsignedByte, n, "VEC4", ArrayBuffer, normalized: true);
        }

        if (skinned && chunk.BonesPerVertex > 0)
        {
            int per = chunk.BonesPerVertex;
            for (int set = 0; set * 4 < per; set++)
            {
                var joints = new byte[n * 4];
                var weights = new byte[n * 16];
                for (int v = 0; v < n; v++)
                {
                    // glTF wants each vertex's weights to add up to 1: those the game rounded to bytes are scaled to
                    float total = 0;
                    for (int b = 0; b < per; b++) total += chunk.BoneWeights[v * per + b];
                    for (int k = 0; k < 4 && set * 4 + k < per; k++)
                    {
                        int b = set * 4 + k;
                        float w = total > 0 ? chunk.BoneWeights[v * per + b] / total : b == 0 ? 1 : 0;
                        // a bone that doesn't move the vertex is bone 0, so it doesn't name one the skin hasn't got
                        joints[v * 4 + k] = w > 0 ? chunk.BoneIndices[v * per + b] : (byte)0;
                        BitConverter.TryWriteBytes(weights.AsSpan(v * 16 + k * 4), w);
                    }
                }
                attributes[$"JOINTS_{set}"] = gltf.AddAccessor(joints, UnsignedByte, n, "VEC4", ArrayBuffer);
                attributes[$"WEIGHTS_{set}"] = gltf.AddAccessor(weights, Float, n, "VEC4", ArrayBuffer);
            }
        }

        bool wide = n > ushort.MaxValue;
        var indices = new byte[chunk.Indices.Length * (wide ? 4 : 2)];
        for (int i = 0; i + 2 < chunk.Indices.Length; i += 3)
        {
            // the game winds its triangles the other way round
            int a = chunk.Indices[i], b = chunk.Indices[i + 2], c = chunk.Indices[i + 1];
            if (wide)
            {
                BitConverter.TryWriteBytes(indices.AsSpan(i * 4), a);
                BitConverter.TryWriteBytes(indices.AsSpan(i * 4 + 4), b);
                BitConverter.TryWriteBytes(indices.AsSpan(i * 4 + 8), c);
            }
            else
            {
                BitConverter.TryWriteBytes(indices.AsSpan(i * 2), (ushort)a);
                BitConverter.TryWriteBytes(indices.AsSpan(i * 2 + 2), (ushort)b);
                BitConverter.TryWriteBytes(indices.AsSpan(i * 2 + 4), (ushort)c);
            }
        }

        return new JsonObject
        {
            ["attributes"] = attributes,
            ["indices"] = gltf.AddAccessor(indices, wide ? UnsignedInt : UnsignedShort, chunk.Indices.Length, "SCALAR", ElementArrayBuffer),
            ["material"] = material,
            ["extras"] = new JsonObject
            {
                ["w3Chunk"] = chunk.Index,
                ["w3Material"] = chunk.Material,
                ["w3LodMask"] = chunk.LodMask,
                ["w3RenderMask"] = chunk.RenderMask,
            },
        };
    }

    private static int AddMaterial(Builder gltf, int slot, GltfMaterial? info)
    {
        var pbr = new JsonObject { ["metallicFactor"] = 0.0 };
        var material = new JsonObject { ["name"] = info?.Name is { Length: > 0 } n ? n : $"material_{slot}" }; // loc-ignore
        if (info?.Colour is { } colour) pbr["baseColorTexture"] = new JsonObject { ["index"] = gltf.AddTexture(colour) };
        if (info?.Roughness is { } roughness) pbr["metallicRoughnessTexture"] = new JsonObject { ["index"] = gltf.AddTexture(roughness) };
        else pbr["roughnessFactor"] = 0.8;
        material["pbrMetallicRoughness"] = pbr;
        if (info?.Normal is { } normal) material["normalTexture"] = new JsonObject { ["index"] = gltf.AddTexture(normal) };

        var extras = new JsonObject { ["w3Slot"] = slot };
        if (info?.Material is { } m)
        {
            extras["w3Source"] = m.Source;
            if (m.Shader.Length > 0) extras["w3Shader"] = m.Shader;
            if (m.Bases.Count > 0) extras["w3Bases"] = new JsonArray([.. m.Bases.Select(b => (JsonNode?)b)]);
            var textures = new JsonObject();
            foreach (var t in m.Textures) textures[t.Parameter] = t.Path;
            if (textures.Count > 0) extras["w3Textures"] = textures;
            // hair is cards cut out by the colour's alpha, seen from both sides
            if (m.Shader.StartsWith("pbr_hair", StringComparison.OrdinalIgnoreCase) && info.Colour is not null)
            {
                material["alphaMode"] = "MASK";
                material["alphaCutoff"] = 0.5;
                material["doubleSided"] = true;
            }
        }
        material["extras"] = extras;
        int index = gltf.Materials.Count;
        gltf.Materials.Add(material);
        return index;
    }

    private static Vector3 YUp(Vector3 v) => new(v.X, v.Z, -v.Y);

    private static Vector3 Unit(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;

    private static void WriteVector(Span<byte> to, Vector3 v)
    {
        BitConverter.TryWriteBytes(to, v.X);
        BitConverter.TryWriteBytes(to[4..], v.Y);
        BitConverter.TryWriteBytes(to[8..], v.Z);
    }

    /// <summary>
    /// A matrix as glTF keeps one, by columns. System.Numerics multiplies a row vector by its matrix, the other way from
    /// glTF, so its rows in order are glTF's columns.
    /// </summary>
    private static void WriteMatrix(Span<byte> to, Matrix4x4 m)
    {
        float[] values = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        for (int i = 0; i < 16; i++) BitConverter.TryWriteBytes(to[(i * 4)..], values[i]);
    }

    private static JsonArray Numbers(params float[] values) => new([.. values.Select(v => (JsonNode?)(double)v)]);

    /// <summary>The glTF's lists as they're filled, and the binary chunk its accessors and pictures point into.</summary>
    private sealed class Builder
    {
        public readonly JsonArray Nodes = [], Meshes = [], Materials = [], Skins = [];
        private readonly JsonArray _accessors = [], _views = [], _textures = [], _images = [];
        private readonly Dictionary<string, int> _textureByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly MemoryStream _bin = new();

        public int AddNode(JsonObject node)
        {
            Nodes.Add(node);
            return Nodes.Count - 1;
        }

        private int AddView(byte[] data, int? target)
        {
            while (_bin.Length % 4 != 0) _bin.WriteByte(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = _bin.Length, ["byteLength"] = data.Length };
            if (target is { } t) view["target"] = t;
            _bin.Write(data);
            _views.Add(view);
            return _views.Count - 1;
        }

        public int AddAccessor(byte[] data, int componentType, int count, string type, int? target,
            JsonArray? min = null, JsonArray? max = null, bool normalized = false)
        {
            var accessor = new JsonObject
            {
                ["bufferView"] = AddView(data, target),
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            };
            if (normalized) accessor["normalized"] = true;
            if (min is not null) accessor["min"] = min;
            if (max is not null) accessor["max"] = max;
            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }

        /// <summary>A texture for a picture, the same one each time the same picture is asked for.</summary>
        public int AddTexture(GltfPicture picture)
        {
            if (_textureByName.TryGetValue(picture.Name, out int found)) return found;
            _images.Add(new JsonObject { ["name"] = picture.Name, ["bufferView"] = AddView(picture.Png, null), ["mimeType"] = "image/png" });
            _textures.Add(new JsonObject { ["sampler"] = 0, ["source"] = _images.Count - 1 });
            return _textureByName[picture.Name] = _textures.Count - 1;
        }

        public void WriteGlb(Stream output, JsonObject root)
        {
            root["nodes"] = Nodes;
            if (Meshes.Count > 0) root["meshes"] = Meshes;
            if (Materials.Count > 0) root["materials"] = Materials;
            if (Skins.Count > 0) root["skins"] = Skins;
            if (_textures.Count > 0)
            {
                root["textures"] = _textures;
                root["images"] = _images;
                // repeating, smoothed, with mip levels, as the game samples them
                root["samplers"] = new JsonArray(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
            }
            while (_bin.Length % 4 != 0) _bin.WriteByte(0);
            if (_bin.Length > 0)
            {
                root["accessors"] = _accessors;
                root["bufferViews"] = _views;
                root["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Length });
            }

            var json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            int jsonPadded = (json.Length + 3) & ~3;
            long total = 12 + 8 + jsonPadded + (_bin.Length > 0 ? 8 + _bin.Length : 0);
            if (total > uint.MaxValue) throw new IOException(L.T("GltfExport.TooBig"));
            using var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            w.Write(0x46546C67u); // glTF
            w.Write(2u);
            w.Write((uint)total);
            w.Write((uint)jsonPadded);
            w.Write(0x4E4F534Au); // JSON
            w.Write(json);
            for (int i = json.Length; i < jsonPadded; i++) w.Write((byte)' ');
            if (_bin.Length > 0)
            {
                w.Write((uint)_bin.Length);
                w.Write(0x004E4942u); // BIN
                w.Flush();
                _bin.Position = 0;
                _bin.CopyTo(output);
            }
        }
    }
}
