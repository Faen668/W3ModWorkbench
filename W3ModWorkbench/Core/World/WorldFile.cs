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

    /// <summary>
    /// Each of the terrain's 31 textures' settings (the clip map's textureParams: val and val2), as the terrain's
    /// shader reads them: val.X how far past its slope threshold a slope fades from background to overlay, val.Y how
    /// much an overlay's normal is flattened before its slope is judged, val.Z the background normal's share, and the
    /// rest the surface's specularity.
    /// </summary>
    public IReadOnlyList<(Vector4 Val, Vector4 Val2)> TextureParams { get; init; } = [];

    /// <summary>A height value (0 to 65535) in metres.</summary>
    public float Metres(ushort value) => Lowest + value / 65535f * (Highest - Lowest);
}

/// <summary>A world's occlusion grid: tiles so big a side, each its middle and its .w3occlusion file.</summary>
public sealed record OcclusionGrid(int Side, float TileSize, IReadOnlyList<(Vector2 Middle, string? File)> Tiles);

/// <summary>
/// A component of a layer's entity: its class and name, the file it draws or uses, and an area's outline in the world
/// with its height (the mean of its points', which for a water shape is the water's level inside it).
/// </summary>
public sealed record LayerComponent(string ClassName, string Name, string? Resource, IReadOnlyList<Vector2>? Outline, float Height = 0)
{
    /// <summary>Where it is in the world: its own transform on its entity's (scale, rotation and position, as the engine applies them).</summary>
    public Matrix4x4 Place { get; init; } = Matrix4x4.Identity;

    /// <summary>How far off it's still drawn, as its component sets it (0 for the mesh's own distance).</summary>
    public float AutoHide { get; init; }

    /// <summary>A stripe's triangles as the game cooked them, and its normal map; null for other components.</summary>
    public StripeData? Stripe { get; init; }

    /// <summary>Its drawable flags as set (DF_IsVisible, DF_CastShadows…), null when it keeps the class's own.</summary>
    public string? DrawableFlags { get; init; }

    /// <summary>
    /// Whether a drawable component is seen, and whether it casts the sun's shadow, by its drawable flags: those it doesn't
    /// set are the class's own (seen, casting shadows); set without DF_IsVisible it's a helper the game doesn't show (a
    /// blockout box); DF_CastShadowsFromLocalLightsOnly casts only from lamps and fires.
    /// </summary>
    public static (bool Visible, bool CastsShadow) Drawable(string? flags)
    {
        if (flags is null) return (true, true);
        var set = flags.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return (set.Contains("DF_IsVisible"), set.Contains("DF_CastShadows"));
    }

    /// <summary>Whether it uses a file of its own (a mesh, a template), for a link to it.</summary>
    public bool HasResource => Resource is { Length: > 0 };
}

/// <summary>
/// A stripe's cooked triangles (CStripeComponent's cookedVertices and cookedIndices): each a byte count, then 64-byte
/// vertices (the position on the entity, a colour whose alpha fades the ends, the segment's number, the texture's
/// across and along coordinates, the way it runs, its middle line's point) and 16-bit indices.
/// </summary>
public sealed record StripeData(byte[] Vertices, byte[] Indices, string? Normal)
{
    /// <summary>The vertices: position, texture coordinates (across, along), alpha, the way the stripe runs.</summary>
    public IEnumerable<(Vector3 Position, Vector2 Uv, float Alpha, Vector3 Along)> Read()
    {
        if (Vertices.Length < 4) yield break;
        int bytes = Math.Min(BitConverter.ToInt32(Vertices, 0), Vertices.Length - 4);
        for (int at = 4; at + 64 <= 4 + bytes; at += 64)
        {
            float F(int o) => BitConverter.ToSingle(Vertices, at + o);
            yield return (new Vector3(F(0), F(4), F(8)), new Vector2(F(20), F(28)), Vertices[at + 15] / 255f, new Vector3(F(36), F(40), F(44)));
        }
    }

    /// <summary>The triangles' indices.</summary>
    public IEnumerable<int> Triangles()
    {
        if (Indices.Length < 4) yield break;
        int bytes = Math.Min(BitConverter.ToInt32(Indices, 0), Indices.Length - 4);
        for (int at = 4; at + 2 <= 4 + bytes; at += 2) yield return BitConverter.ToUInt16(Indices, at);
    }
}

/// <summary>An entity of a layer, for a map: its class and name, where it stands, which way it faces, its template, its components.</summary>
public sealed record LayerEntity(string ClassName, string Name, Vector3 Position, float Yaw, string? Template, RedObject Object,
    IReadOnlyList<LayerComponent> Components);

/// <summary>A pin of a world's hub map: an entity's (a signpost, a place of power, a notice board) or a quest's, where, and how wide for a quest's area.</summary>
public sealed record MapPin(string Name, string Kind, Vector3 Position, float Radius, bool Quest);

/// <summary>
/// A world's hub map pins: the entity pins (.w2em, a CEntityMapPinsResource) and quest pins (.w2qm, a
/// CQuestMapPinsResource) the game shows on its map, kept beside the world under game\hub_pins.
/// </summary>
public static class MapPins
{
    public static List<MapPin> Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var pins = new List<MapPin>();
        if (file.First("CEntityMapPinsResource") is { } entities)
            foreach (var p in entities.Array("mappinsInfo").OfType<RedObject>())
                pins.Add(new MapPin(p["entityName"] as string ?? "", p["entityType"]?.ToString() ?? "", p.Vector("entityPosition"), p.Float("entityRadius"), false));
        if (file.First("CQuestMapPinsResource") is { } quests)
            foreach (var p in quests.Array("mappinsInfo").OfType<RedObject>())
                foreach (var at in p.Array("positions").OfType<RedObject>())
                    pins.Add(new MapPin(p["tag"] as string ?? "", p["type"]?.ToString() ?? "", new Vector3(at.Float("X"), at.Float("Y"), at.Float("Z")), p.Float("radius"), true));
        return pins;
    }
}

/// <summary>
/// A world's waypoints (.redwpset, a CWayPointsCollectionsSet of CWayPointsCollection): the points NPCs walk to and
/// stand at, cooked from the layers' waypoint components. Each collection keeps, after its properties, a size and its
/// waypoints, 20 bytes each: where (three floats), which way it faces and the component it came from.
/// </summary>
public static class Waypoints
{
    public static List<(Vector3 Position, float Yaw)> Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var points = new List<(Vector3, float)>();
        foreach (var export in file.Exports.Where(x => x.ClassName == "CWayPointsCollection"))
        {
            var collection = file.Object(export.Index);
            int count = (int)collection.Integer("waypointsCount");
            var tail = collection.Tail;
            for (int i = 0; i < count && 4 + 20 * i + 20 <= tail.Length; i++)
            {
                int at = 4 + 20 * i;
                points.Add((new Vector3(BitConverter.ToSingle(tail, at), BitConverter.ToSingle(tail, at + 4), BitConverter.ToSingle(tail, at + 8)),
                    BitConverter.ToSingle(tail, at + 12)));
            }
        }
        return points;
    }
}

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
        static Vector4 V4(RedObject? v) => v is null ? Vector4.Zero : new Vector4(v.Float("X"), v.Float("Y"), v.Float("Z"), v.Float("W"));
        var parameters = clip.Array("textureParams").OfType<RedObject>().Select(p => (V4(p["val"] as RedObject), V4(p["val2"] as RedObject))).ToList();
        return new WorldTerrain(n, (int)clip.Integer("tileRes", 256), size, new Vector2(corner.X, corner.Y), clip.Float("lowestElevation"),
            clip.Float("highestElevation", 1), tiles, clip["material"] is RedHandle mh ? file.PathOf(mh) : null, ranges) { TextureParams = parameters };
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
    public static List<LayerEntity> Entities(byte[] layerData) => Entities(Cr2wFile.Read(layerData));

    /// <summary>A layer file's entities and what its sector data places (the meshes, decals and lights cooking took out of them).</summary>
    public static (List<LayerEntity> Entities, List<SectorObject> Sector) Layer(byte[] layerData)
    {
        var file = Cr2wFile.Read(layerData);
        var sector = file.First("CSectorData") is { Tail.Length: > 8 } data ? SectorData.Read(data.Tail) : [];
        return (Entities(file), sector);
    }

    private static List<LayerEntity> Entities(Cr2wFile file)
    {
        var result = new List<LayerEntity>();
        if (file.First("CLayer") is not { } layer) return result;
        // the exports under each entity: cooked layers keep the areas only as the entity's children, not in its list
        var children = file.Exports.Where(x => x.Parent >= 0).ToLookup(x => x.Parent, x => x.Index);
        foreach (var h in layer.Array("entities").OfType<RedHandle>())
        {
            if (file.Object(h) is not { } e) continue;
            var (position, rotation) = SceneStaging.Transform(e["transform"]);
            var place = e["transform"] is byte[] raw ? Core.Entities.EntityFile.Transform(raw) : Matrix4x4.CreateTranslation(position);
            result.Add(new LayerEntity(e.ClassName, e["name"] as string ?? "", position, rotation.Z * MathF.PI / 180,
                e["template"] is RedHandle t ? file.PathOf(t) : null, e, Components(file, e, h.Export ?? -1, children, position, rotation.Z * MathF.PI / 180, place)));
        }
        return result;
    }

    /// <summary>
    /// An entity's components: those the layer keeps (areas, waypoints, triggers: the entity's components list and its
    /// child exports), and
    /// those streamed in near the player, kept in a CR2W file of their own in its streamingDataBuffer (meshes, lights).
    /// </summary>
    private static List<LayerComponent> Components(Cr2wFile file, RedObject entity, int index, ILookup<int, int> children, Vector3 at, float yaw, Matrix4x4 place)
    {
        var components = new List<LayerComponent>();
        var seen = new HashSet<int>();
        foreach (int i in entity.Array("components").OfType<RedHandle>().Select(h => h.Export ?? -1).Concat(children[index]))
            if (i >= 0 && i < file.Exports.Count && seen.Add(i) && file.Exports[i].ClassName.EndsWith("Component", StringComparison.Ordinal))
                components.Add(Component(file, file.Object(i), at, yaw, place));
        if (entity.Bytes("streamingDataBuffer") is { Length: > 8 } streamed)
        {
            // the CR2W file, straight away or after its size
            int start = Cr2wFile.IsCr2w(streamed) ? 0 : Cr2wFile.IsCr2w(streamed.AsSpan(4)) ? 4 : -1;
            if (start >= 0)
                try
                {
                    var inner = Cr2wFile.Read(streamed[start..]);
                    for (int i = 0; i < inner.Exports.Count; i++)
                        if (inner.Exports[i].ClassName.EndsWith("Component", StringComparison.Ordinal)) components.Add(Component(inner, inner.Object(i), at, yaw, place));
                }
                catch (InvalidDataException) { }
        }
        return components;
    }

    private static LayerComponent Component(Cr2wFile file, RedObject c, Vector3 at, float yaw, Matrix4x4 entityPlace)
    {
        string? resource = null;
        foreach (string name in new[] { "mesh", "resource", "template", "particleSystem", "spawnTree", "dialogset", "cloth", "furMesh" })
            if (c[name] is RedHandle h && file.PathOf(h) is { Length: > 0 } path)
            {
                resource = path;
                break;
            }
        resource ??= c.Properties.Select(p => p.Value).OfType<RedHandle>().Select(file.PathOf).FirstOrDefault(p => p is { Length: > 0 });
        var world = c.Array("worldPoints").OfType<RedObject>().ToList();
        var points = world.Select(p => new Vector2(p.Float("X"), p.Float("Y"))).ToList();
        float height = world.Count > 0 ? world.Average(p => p.Float("Z")) : at.Z;
        if (points.Count < 2 && c.Array("localPoints").Count >= 2)
        {
            // the game's cooked areas keep only their own points: placed by the component on the entity, then the entity in the world
            var (offset, turn) = SceneStaging.Transform(c["transform"]);
            var place = Matrix4x4.CreateRotationZ(turn.Z * MathF.PI / 180) * Matrix4x4.CreateTranslation(offset)
                * Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateTranslation(at);
            var placed = c.Array("localPoints").OfType<RedObject>().Select(p => Vector3.Transform(new Vector3(p.Float("X"), p.Float("Y"), 0), place)).ToList();
            points = placed.Select(p => new Vector2(p.X, p.Y)).ToList();
            height = placed.Average(p => p.Z);
        }
        var own = c["transform"] is byte[] raw ? Core.Entities.EntityFile.Transform(raw) : Matrix4x4.Identity;
        return new LayerComponent(c.ClassName, c["name"] as string ?? "", resource, points.Count >= 2 ? points : null, height) { Place = own * entityPlace, AutoHide = c["autoHideDistance"] is float hide ? hide : 0, DrawableFlags = c["drawableFlags"]?.ToString(),
            Stripe = c.ClassName == "CStripeComponent" && c["cookedVertices"] is byte[] sv && c["cookedIndices"] is byte[] si
                ? new StripeData(sv, si, c["normalTexture"] is RedHandle nh ? file.PathOf(nh) : null) : null };
    }
}
