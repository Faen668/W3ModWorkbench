using System.Globalization;
using System.IO;
using System.Text;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>
/// Writes chunks of a mesh as a Wavefront .obj with a .mtl beside it, which Blender, 3ds Max and most other tools read.
/// Each material gets a group and a material of its own, with its colour texture as a PNG in the same folder.
/// The mesh is turned to Y-up, as those tools expect, and UVs are flipped to start at the bottom.
/// </summary>
public static class ObjExport
{
    /// <param name="textures">For each material that has one, the PNG's bytes and the name to save it under.</param>
    /// <returns>The files written.</returns>
    public static List<string> Write(string objPath, string meshName, IEnumerable<MeshChunk> chunks, IReadOnlyDictionary<int, (string Name, byte[] Png)> textures)
    {
        var written = new List<string>();
        string folder = Path.GetDirectoryName(objPath) ?? ".";
        string mtlName = Path.GetFileNameWithoutExtension(objPath) + ".mtl";
        var inv = CultureInfo.InvariantCulture;
        var obj = new StringBuilder();
        obj.AppendLine($"# {meshName}, exported by W3 Mod Workbench");
        obj.AppendLine($"mtllib {mtlName}");
        int first = 1;
        var used = new SortedSet<int>();
        foreach (var chunk in chunks)
        {
            used.Add(chunk.Material);
            obj.AppendLine($"o chunk_{chunk.Index}");
            obj.AppendLine($"usemtl material_{chunk.Material}");
            foreach (var p in chunk.Positions) obj.Append(inv, $"v {p.X:R} {p.Z:R} {-p.Y:R}\n");
            foreach (var t in chunk.Uvs) obj.Append(inv, $"vt {t.X:R} {1 - t.Y:R}\n");
            foreach (var n in chunk.Normals) obj.Append(inv, $"vn {n.X:R} {n.Z:R} {-n.Y:R}\n");
            // the game winds its triangles the other way round
            for (int i = 0; i + 2 < chunk.Indices.Length; i += 3)
            {
                int a = chunk.Indices[i] + first, b = chunk.Indices[i + 2] + first, c = chunk.Indices[i + 1] + first;
                obj.Append(inv, $"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}\n");
            }
            first += chunk.Positions.Length;
        }
        File.WriteAllText(objPath, obj.ToString());
        written.Add(objPath);

        var mtl = new StringBuilder();
        foreach (int material in used)
        {
            mtl.AppendLine($"newmtl material_{material}");
            mtl.AppendLine("Kd 1 1 1");
            if (textures.TryGetValue(material, out var texture))
            {
                string png = Path.Combine(folder, texture.Name);
                if (!written.Contains(png, StringComparer.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(png, texture.Png);
                    written.Add(png);
                }
                mtl.AppendLine($"map_Kd {texture.Name}");
            }
            mtl.AppendLine();
        }
        string mtlPath = Path.Combine(folder, mtlName);
        File.WriteAllText(mtlPath, mtl.ToString());
        written.Add(mtlPath);
        return written;
    }
}
