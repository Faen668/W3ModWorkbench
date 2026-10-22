using System.IO;
using System.Numerics;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.World;

/// <summary>What a cell of a navigation tile says of the ground there.</summary>
public enum NavCell : byte
{
    Walkable = 0,
    /// <summary>Water, cliffs and what collides: no one walks there.</summary>
    Blocked = 1,
    /// <summary>Ground a navmesh instance takes over (a village, a castle), whose own mesh says where to walk.</summary>
    Instance = 2,
    Other = 3,
}

/// <summary>
/// A navigation tile (.navtile, PathLib's, not a CR2W file): which parts of one terrain tile can be walked, a grid of
/// cells over it (256 a side for a tile of 256 samples), rows from the south. The file is a u16 version (6), a u16
/// resolution, the cells two bits each (the lowest first), then a search tree of the tile's areas the engine walks
/// paths through, which this doesn't read. Named terrain_COLUMNxROW.navtile.
/// </summary>
public sealed class NavTileFile
{
    public required int Resolution { get; init; }
    public required byte[] Packed { get; init; }

    public static bool IsNavTile(string path) => path.EndsWith(".navtile", StringComparison.OrdinalIgnoreCase);

    public static NavTileFile Read(byte[] data)
    {
        if (data.Length < 4 || BitConverter.ToUInt16(data, 0) != 6) throw new InvalidDataException(L.T("NavigationFiles.NotNavTile"));
        int resolution = BitConverter.ToUInt16(data, 2);
        int bytes = resolution * resolution / 4;
        if (resolution <= 0 || 4 + bytes > data.Length) throw new InvalidDataException(L.T("NavigationFiles.NotNavTile"));
        return new NavTileFile { Resolution = resolution, Packed = data[4..(4 + bytes)] };
    }

    /// <summary>A cell, by its column and its row from the south.</summary>
    public NavCell Cell(int x, int y)
    {
        int i = y * Resolution + x;
        return (NavCell)((Packed[i >> 2] >> ((i & 3) * 2)) & 3);
    }
}

/// <summary>
/// A navmesh (.navmesh, PathLib's, not a CR2W file): the walkable surface of a place the terrain's grid can't describe
/// (a village's streets and stairs, a castle's floors), as triangles. The file is a u16 version (1), u16 counts of
/// vertices, triangles and something more, its middle (a Vector4), its radius, its box (two Vector4s), the size of
/// the rest, then the vertices (three floats each), the triangles (three u16 vertex numbers), each triangle's
/// neighbours (three u16, 0xFFFF for none, the top bit marking an edge on the mesh's border), each triangle's normal,
/// and a KD-tree to find triangles by place, which this doesn't read.
/// </summary>
public sealed class NavMeshFile
{
    public required Vector3 Middle { get; init; }
    public required float Radius { get; init; }
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }
    public required IReadOnlyList<Vector3> Vertices { get; init; }
    public required IReadOnlyList<(int A, int B, int C)> Triangles { get; init; }
    public required IReadOnlyList<(int A, int B, int C)> Neighbours { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }

    public static bool IsNavMesh(string path) => path.EndsWith(".navmesh", StringComparison.OrdinalIgnoreCase);

    public static NavMeshFile Read(byte[] data)
    {
        if (data.Length < 64 || BitConverter.ToUInt16(data, 0) != 1) throw new InvalidDataException(L.T("NavigationFiles.NotNavMesh"));
        int vertices = BitConverter.ToUInt16(data, 2), triangles = BitConverter.ToUInt16(data, 4);
        int at = 64;
        if (at + 12L * vertices + 12L * triangles + 12L * triangles > data.Length) throw new InvalidDataException(L.T("NavigationFiles.NotNavMesh"));
        Vector3 V(int o) => new(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8));
        var v = new Vector3[vertices];
        for (int i = 0; i < vertices; i++, at += 12) v[i] = V(at);
        var t = new (int, int, int)[triangles];
        for (int i = 0; i < triangles; i++, at += 6) t[i] = (BitConverter.ToUInt16(data, at), BitConverter.ToUInt16(data, at + 2), BitConverter.ToUInt16(data, at + 4));
        var n = new (int, int, int)[triangles];
        for (int i = 0; i < triangles; i++, at += 6) n[i] = (BitConverter.ToUInt16(data, at), BitConverter.ToUInt16(data, at + 2), BitConverter.ToUInt16(data, at + 4));
        var normals = new Vector3[triangles];
        for (int i = 0; i < triangles; i++, at += 12) normals[i] = V(at);
        return new NavMeshFile
        {
            Middle = V(8),
            Radius = BitConverter.ToSingle(data, 24),
            Min = V(28),
            Max = V(44),
            Vertices = v,
            Triangles = t,
            Neighbours = n,
            Normals = normals,
        };
    }
}
