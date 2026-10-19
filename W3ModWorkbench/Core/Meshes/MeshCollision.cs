using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace W3ModWorkbench.Core.Meshes;

/// <summary>One of a mesh's collision shapes: what kind it is, its physical material, and its vertices and triangles in the mesh's space.</summary>
public sealed record CollisionShape(string Kind, string Material, Vector3[] Vertices, int[] Triangles);

/// <summary>
/// A cooked mesh's collision, as the game's collision.cache keeps it under the mesh's path: a count of shapes, then for
/// each its physical material's name and its place as a 4×4 matrix, then the shape as NVIDIA PhysX cooked it. A convex
/// shape ("NXS" "CVXM") is a hull ("ICE" "CVHL"): its vertices, then its polygons (each a plane, where its corners start
/// in the list of corners and how many there are), then that list of corners. A triangle mesh ("NXS" "MESH") is its
/// vertices and its triangles' corners, 8, 16 or 32 bits each as its flags say. Box, sphere and capsule shapes have no
/// PhysX data and are counted but not drawn.
/// </summary>
public static class MeshCollision
{
    /// <summary>The shapes that can be drawn, and how many shapes there are in all.</summary>
    public static (List<CollisionShape> Shapes, int Count) Read(byte[] data)
    {
        var shapes = new List<CollisionShape>();
        int count = data.Length >= 16 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)) : 0;
        int searchFrom = 16;
        for (int at = Find(data, "NXS\u0001", searchFrom); at >= 0; at = Find(data, "NXS\u0001", at + 8))
        {
            string kind = Encoding.ASCII.GetString(data, at + 4, 4);
            var place = Placement(data, searchFrom, at);
            string material = MaterialName(data, searchFrom, at);
            CollisionShape? shape = kind switch
            {
                "CVXM" => Convex(data, at, material),
                "MESH" => TriangleMesh(data, at, material),
                _ => null,
            };
            if (shape is not null)
            {
                if (!place.IsIdentity)
                    for (int v = 0; v < shape.Vertices.Length; v++) shape.Vertices[v] = Vector3.Transform(shape.Vertices[v], place);
                shapes.Add(shape);
            }
            searchFrom = at + 8;
        }
        return (shapes, Math.Max(count, shapes.Count));
    }

    private static int Find(byte[] data, string tag, int from)
    {
        var bytes = Encoding.Latin1.GetBytes(tag);
        int i = from < data.Length ? data.AsSpan(from).IndexOf(bytes) : -1;
        return i < 0 ? -1 : from + i;
    }

    private static uint U32(byte[] d, int at) => at + 4 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) : 0;

    private static float F32(byte[] d, int at) => at + 4 <= d.Length ? BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at)) : 0;

    private static Vector3 Vector(byte[] d, int at) => new(F32(d, at), F32(d, at + 4), F32(d, at + 8));

    /// <summary>
    /// A shape's place: the last 4×4 matrix before its PhysX data that looks like one (a rotation and scale, a
    /// translation, and 0, 0, 0, 1 down its last column). Identity when none does.
    /// </summary>
    private static Matrix4x4 Placement(byte[] d, int from, int to)
    {
        for (int at = to - 64; at >= from; at--)
        {
            float M(int k) => F32(d, at + k * 4);
            if (M(3) != 0 || M(7) != 0 || M(11) != 0 || M(15) != 1) continue;
            var m = new Matrix4x4(M(0), M(1), M(2), M(3), M(4), M(5), M(6), M(7), M(8), M(9), M(10), M(11), M(12), M(13), M(14), M(15));
            float scale = new Vector3(m.M11, m.M12, m.M13).Length();
            if (scale is > 0.001f and < 1000 && float.IsFinite(m.M41 + m.M42 + m.M43)) return m;
        }
        return Matrix4x4.Identity;
    }

    /// <summary>A shape's physical material: the last name before its matrix, such as "stone_solid" or "default".</summary>
    private static string MaterialName(byte[] d, int from, int to)
    {
        string found = "default";
        for (int at = from; at < to; at++)
        {
            int length = d[at] & 0x7F;
            if (length < 3 || at + 1 + length > to) continue;
            var text = d.AsSpan(at + 1, length);
            bool name = true;
            foreach (byte c in text) name &= c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';
            if (!name) continue;
            string s = Encoding.ASCII.GetString(text);
            if (s != "None" && s != "NXS") found = s;
            at += length;
        }
        return found;
    }

    private static CollisionShape? Convex(byte[] d, int at, string material)
    {
        int hull = Find(d, "ICE\u0001CVHL", at);
        if (hull < 0) return null;
        int counts = hull + 12;
        int vertices = (int)U32(d, counts), polygons = (int)U32(d, counts + 8), refs = (int)U32(d, counts + 12);
        int verticesAt = counts + 16, polygonsAt = verticesAt + vertices * 12, refsAt = polygonsAt + polygons * 20;
        if (vertices is <= 0 or > 256 || polygons is <= 0 or > 1024 || refsAt + refs > d.Length) return null;
        var points = new Vector3[vertices];
        for (int v = 0; v < vertices; v++) points[v] = Vector(d, verticesAt + v * 12);
        var triangles = new List<int>();
        for (int p = 0; p < polygons; p++)
        {
            int polygon = polygonsAt + p * 20;
            int first = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(polygon + 16));
            int corners = d[polygon + 18];
            // each polygon is flat and convex, so it's a fan of triangles from its first corner
            for (int k = 1; k + 1 < corners && first + k + 1 < refs; k++)
            {
                triangles.Add(d[refsAt + first]);
                triangles.Add(d[refsAt + first + k]);
                triangles.Add(d[refsAt + first + k + 1]);
            }
        }
        return new CollisionShape("Convex", material, points, [.. triangles]);
    }

    private static CollisionShape? TriangleMesh(byte[] d, int at, string material)
    {
        uint flags = U32(d, at + 12);
        int vertices = (int)U32(d, at + 20), triangles = (int)U32(d, at + 24);
        int verticesAt = at + 28, trianglesAt = verticesAt + vertices * 12;
        int size = (flags & 4) != 0 ? 1 : (flags & 8) != 0 ? 2 : 4;
        if (vertices <= 0 || triangles <= 0 || vertices > 4_000_000 || trianglesAt + (long)triangles * 3 * size > d.Length) return null;
        var points = new Vector3[vertices];
        for (int v = 0; v < vertices; v++) points[v] = Vector(d, verticesAt + v * 12);
        var corners = new int[triangles * 3];
        for (int i = 0; i < corners.Length; i++)
        {
            int c = trianglesAt + i * size;
            corners[i] = size switch
            {
                1 => d[c],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(c)),
                _ => (int)U32(d, c),
            };
            if (corners[i] >= vertices) corners[i] = 0;
        }
        return new CollisionShape("Triangle mesh", material, points, corners);
    }
}
