using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Scenes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.World;

/// <summary>The engine's packed counts: six bits in the first byte (the top bit a sign), seven in each byte after while the one before says more follow.</summary>
internal static class Packed
{
    public static bool Count(byte[] bytes, ref int at, out int value)
    {
        value = 0;
        if (at >= bytes.Length) return false;
        int first = bytes[at++];
        value = first & 0x3F;
        if ((first & 0x40) != 0)
        {
            int shift = 6, next;
            do
            {
                if (at >= bytes.Length) return false;
                next = bytes[at++];
                value |= (next & 0x7F) << shift;
                shift += 7;
            } while ((next & 0x80) != 0 && shift < 32);
        }
        return true;
    }
}

/// <summary>What a layer is to its world: its file, its short name, its GUID and what it's built as (outdoor, quest, interior…).</summary>
public sealed record LayerInfo(string Path, string ShortName, Guid Guid, string BuildTag)
{
    /// <summary>
    /// The layer info REDkit keeps at the end of a layer file, after its CR2W data: a small CR2W file of its own holding
    /// one CLayerInfo, then "LYRI" and where that file starts. Saving again adds another, so the last one counts.
    /// Null when the file has none (the game's cooked layers keep theirs in the world instead).
    /// </summary>
    public static LayerInfo? FromTrailer(byte[] data)
    {
        if (data.Length < 16 || BitConverter.ToUInt32(data, data.Length - 8) != 0x4C595249) return null;
        int start = BitConverter.ToInt32(data, data.Length - 4);
        if (start <= 0 || start >= data.Length - 8) return null;
        try
        {
            var file = Cr2wFile.Read(data[start..(data.Length - 8)]);
            return file.First("CLayerInfo") is { } info ? Of(info) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public static LayerInfo Of(RedObject info) => new(info["depotFilePath"] as string ?? "", info["shortName"] as string ?? "",
        info["guid"] is Guid g ? g : Guid.Empty, info["layerBuildTag"]?.ToString() ?? "");
}

/// <summary>A group of layers: a folder of the world, its layers and the groups in it.</summary>
public sealed class LayerGroup
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public List<LayerGroup> Groups { get; } = [];
    public List<LayerInfo> Layers { get; } = [];

    public IEnumerable<LayerInfo> AllLayers() => Layers.Concat(Groups.SelectMany(g => g.AllLayers()));

    /// <summary>
    /// A world's layers grouped by their folders under the world's own, as REDkit groups them: a layer's group is its
    /// folder, each folder a group in its parent's.
    /// </summary>
    public static LayerGroup FromPaths(string worldFolder, IEnumerable<string> layers, Func<string, LayerInfo?>? info = null)
    {
        var root = new LayerGroup { Name = "World", Path = worldFolder };
        string prefix = worldFolder.Length == 0 ? "" : worldFolder.TrimEnd('\\') + "\\";
        foreach (string layer in layers.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!layer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var group = root;
            var parts = layer[prefix.Length..].Split('\\');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var next = group.Groups.FirstOrDefault(g => g.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (next is null)
                    group.Groups.Add(next = new LayerGroup { Name = parts[i], Path = prefix + string.Join('\\', parts[..(i + 1)]) });
                group = next;
            }
            group.Layers.Add(info?.Invoke(layer) ?? new LayerInfo(layer, System.IO.Path.GetFileNameWithoutExtension(layer), Guid.Empty, ""));
        }
        return root;
    }
}

/// <summary>Where a world's terrain is: how many tiles a side, how big, where its corner is, its height range, and each tile's file.</summary>
public sealed record WorldTerrain(int TilesPerEdge, int TileResolution, float Size, Vector2 Corner, float Lowest, float Highest, string?[,] Tiles,
    string? Material, IReadOnlyList<(float Min, float Max)> HeightRanges)
{
    public float TileSize => Size / TilesPerEdge;

    /// <summary>A height value (0 to 65535) in metres.</summary>
    public float Metres(ushort value) => Lowest + value / 65535f * (Highest - Lowest);
}

/// <summary>A world's occlusion grid: tiles so big a side, each its middle and its .w3occlusion file.</summary>
public sealed record OcclusionGrid(int Side, float TileSize, IReadOnlyList<(Vector2 Middle, string? File)> Tiles);

/// <summary>An entity of a layer, for a map: its class and name, where it stands, which way it faces, its template.</summary>
public sealed record LayerEntity(string ClassName, string Name, Vector3 Position, float Yaw, string? Template, RedObject Object);

/// <summary>
/// A world (.w2w, a CGameWorld): its terrain (a clip map of tiles), its occlusion grid, its foliage, its environment, its
/// navigation, and its layers. REDkit's worlds find their layers by their folders; the game's cooked ones keep the
/// whole tree of layer groups and layer infos inside them, which this reads back.
/// </summary>
public sealed class WorldFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject World { get; init; }
    public required string Folder { get; init; }
    public WorldTerrain? Terrain { get; init; }
    public OcclusionGrid? Occlusion { get; init; }
    /// <summary>The layer tree the world keeps (the game's cooked worlds), or null for one that finds its layers by folder.</summary>
    public LayerGroup? EmbeddedLayers { get; init; }

    public static bool IsWorld(string path) => path.EndsWith(".w2w", StringComparison.OrdinalIgnoreCase);

    public static WorldFile Read(byte[] data, string path)
    {
        var file = Cr2wFile.Read(data);
        var world = file.First("CGameWorld") ?? file.First("CWorld") ?? throw new InvalidDataException(L.T("WorldFile.NotWorld"));
        return new WorldFile
        {
            File = file,
            World = world,
            Folder = System.IO.Path.GetDirectoryName(path) ?? "",
            Terrain = TerrainOf(file, world),
            Occlusion = OcclusionOf(file, world),
            EmbeddedLayers = LayersOf(file, world),
        };
    }

    private static List<int> Handles(byte[] tail, ref int at)
    {
        var list = new List<int>();
        if (!Packed.Count(tail, ref at, out int count)) return list;
        for (int i = 0; i < count && at + 4 <= tail.Length; i++, at += 4) list.Add(BitConverter.ToInt32(tail, at));
        return list;
    }

    /// <summary>The terrain, from the clip map: its tail is how many tiles, then the import of each tile's file.</summary>
    private static WorldTerrain? TerrainOf(Cr2wFile file, RedObject world)
    {
        if (world["terrainClipMap"] is not RedHandle h || file.Object(h) is not { } clip) return null;
        int n = (int)clip.Integer("numTilesPerEdge");
        float size = clip.Float("terrainSize");
        if (n <= 0 || size <= 0) return null;
        var tiles = new string?[n, n];
        var tail = clip.Tail;
        int count = tail.Length >= 4 ? BitConverter.ToInt32(tail, 0) : 0;
        for (int i = 0; i < count && 4 + 4 * i + 4 <= tail.Length; i++)
        {
            string? tile = file.PathOf(new RedHandle(BitConverter.ToInt32(tail, 4 + 4 * i), false));
            // tile_ROW_x_COLUMN_resN.w2ter, rows from the south
            if (tile is not null && System.Text.RegularExpressions.Regex.Match(System.IO.Path.GetFileName(tile), @"tile_(\d+)_x_(\d+)") is { Success: true } m
                && int.Parse(m.Groups[1].Value) < n && int.Parse(m.Groups[2].Value) < n)
                tiles[int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)] = tile;
        }
        var corner = clip.Vector("terrainCorner", new Vector3(-size / 2, -size / 2, 0));
        var ranges = clip.Array("tileHeightRanges").OfType<RedObject>().Select(r => (r.Float("X"), r.Float("Y"))).ToList();
        return new WorldTerrain(n, (int)clip.Integer("tileRes", 256), size, new Vector2(corner.X, corner.Y), clip.Float("lowestElevation"),
            clip.Float("highestElevation", 1), tiles, clip["material"] is RedHandle mh ? file.PathOf(mh) : null, ranges);
    }

    /// <summary>The occlusion grid: its tail is how many a side, a tile's size, how many, then each tile's middle and import.</summary>
    private static OcclusionGrid? OcclusionOf(Cr2wFile file, RedObject world)
    {
        if (world["umbraScene"] is not RedHandle h || file.Object(h) is not { } scene || scene.Tail.Length < 12) return null;
        var t = scene.Tail;
        int side = BitConverter.ToInt32(t, 0), count = BitConverter.ToInt32(t, 8);
        float size = BitConverter.ToSingle(t, 4);
        var tiles = new List<(Vector2, string?)>();
        for (int i = 0; i < count && 12 + 20 * i + 20 <= t.Length; i++)
        {
            int at = 12 + 20 * i;
            tiles.Add((new Vector2(BitConverter.ToSingle(t, at), BitConverter.ToSingle(t, at + 4)), file.PathOf(new RedHandle(BitConverter.ToInt32(t, at + 16), false))));
        }
        return new OcclusionGrid(side, size, tiles);
    }

    /// <summary>
    /// The layer tree a cooked world keeps: the world's tail is its root group's handle; a group's tail is the world's
    /// handle, its parent's (0 for the root), then a packed count and its groups' handles, then a packed count and its
    /// layer infos' handles.
    /// </summary>
    private static LayerGroup? LayersOf(Cr2wFile file, RedObject world)
    {
        int root = world.Tail.Length >= 4 ? BitConverter.ToInt32(world.Tail, 0) : 0;
        if (root <= 0 || root > file.Exports.Count) return null;
        LayerGroup? Group(int handle, int depth)
        {
            if (depth > 64 || handle <= 0 || handle > file.Exports.Count) return null;
            var o = file.Object(handle - 1);
            if (o.ClassName != "CLayerGroup") return null;
            var group = new LayerGroup { Name = o["name"] as string ?? "", Path = (o["depotPath"] as string ?? "").TrimEnd('\\') };
            var tail = o.Tail;
            int at = 8;
            foreach (int child in Handles(tail, ref at))
                if (Group(child, depth + 1) is { } g) group.Groups.Add(g);
            foreach (int layer in Handles(tail, ref at))
                if (layer > 0 && layer <= file.Exports.Count && file.Object(layer - 1) is { ClassName: "CLayerInfo" } info) group.Layers.Add(LayerInfo.Of(info));
            return group;
        }
        return Group(root, 0);
    }

    /// <summary>The entities of a layer file, for a map.</summary>
    public static List<LayerEntity> Entities(byte[] layerData)
    {
        var file = Cr2wFile.Read(layerData);
        var result = new List<LayerEntity>();
        if (file.First("CLayer") is not { } layer) return result;
        foreach (var h in layer.Array("entities").OfType<RedHandle>())
        {
            if (file.Object(h) is not { } e) continue;
            var (position, rotation) = SceneStaging.Transform(e["transform"]);
            result.Add(new LayerEntity(e.ClassName, e["name"] as string ?? "", position, rotation.Z * MathF.PI / 180,
                e["template"] is RedHandle t ? file.PathOf(t) : null, e));
        }
        return result;
    }
}
