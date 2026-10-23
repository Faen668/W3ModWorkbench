using System.IO;
using System.Numerics;
using System.Text;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.World;

/// <summary>
/// One of a tree's materials (a SpeedTree render state): its pictures by name (beside the .srt, as .dds; the game keeps
/// them in its texture cache under those names), what it draws, and its name in the SpeedTree Modeler.
/// </summary>
public sealed record SpeedTreeMaterial(IReadOnlyList<string> Textures, string Description, bool Branches, bool Fronds, bool Leaves,
    bool FacingLeaves, bool RigidMeshes, bool Grass)
{
    /// <summary>The render state's bytes from 64 to 224 (its lighting settings), as stored.</summary>
    public byte[] Lighting { get; init; } = [];

    /// <summary>The specular map (green its specularity, alpha its translucency), or "".</summary>
    public string Specular => Textures.Count > 4 ? Textures[4] : "";

    private float At(int offset) => Lighting.Length >= offset - 64 + 4 ? BitConverter.ToSingle(Lighting, offset - 64) : 0;

    /// <summary>The render state's shininess (its glossiness as the game's shader takes it, over 128).</summary>
    public float Shininess => At(120);

    /// <summary>The render state's alpha scalar (SpeedTree's m_fAlphaScalar): the cutout's alpha is scaled by it, and by the
    /// world's scalar, before the 0.1 cut.</summary>
    public float AlphaScalar => Lighting.Length >= 212 - 64 + 4 ? At(212) : 1;

    /// <summary>The render state's specular colour.</summary>
    public System.Numerics.Vector3 SpecularColour => new(At(124), At(128), At(132));

    /// <summary>The render state's transmission (translucency) colour.</summary>
    public System.Numerics.Vector3 TransmissionColour => new(At(140), At(144), At(148));

    /// <summary>The colour picture (with the leaves' cutout in its alpha), or "" for none.</summary>
    public string Diffuse => Textures[0];

    /// <summary>The normal map, or "".</summary>
    public string Normal => Textures[1];
}

/// <summary>
/// One draw of a LOD: a material and its triangles. A leaf card's corners are its offsets from its middle, facing the
/// camera; normals and ambient occlusion (1 in the open) are there when the material's vertices keep them.
/// </summary>
public sealed record SpeedTreeDraw(int Material, Vector3[] Positions, Vector2[] Uvs, Vector3[]? Corners, int[] Indices,
    Vector3[]? Normals = null, float[]? Occlusion = null);

/// <summary>A side view of the tree in its billboard atlas: the rectangle (left, top, width, height; a negative size flips it) and whether it's turned.</summary>
public readonly record struct SpeedTreeBillboard(Vector4 TexCoords, bool Rotated);

/// <summary>
/// A SpeedTree file (.srt, a CSRTBaseTree): a tree, bush or grass. After its properties it keeps the SpeedTree 7 runtime
/// data ("SRT 07.0.0"): the size box, the LOD distances, the wind settings (a fixed 1,376 bytes), a table of strings,
/// collision capsules, the side-view billboards, then the geometry: each material as a 720-byte render state (main,
/// then shadow, then the billboards' two), and each LOD's draws, their vertices laid out as each material's vertex
/// declaration says (half floats, floats or bytes) and their indices, 16 or 32 bits. Read for all 545 of the game's
/// vegetation files but one (an orange tree's fruit).
/// </summary>
public sealed class SpeedTreeFile
{
    private const string Magic = "SRT 07.0.0";
    private const int WindSize = 1376, RenderStateSize = 720;

    /// <summary>Where the vertex declaration's properties are: SpeedTree's numbering of them.</summary>
    private const int PropertyPosition = 0, PropertyUv = 1, PropertyNormal = 2, PropertyCorner = 5, PropertyOcclusion = 18;

    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    /// <summary>Whether the tree has LODs, and the distances: full detail up to, lowest detail at, billboards from, and billboards only.</summary>
    public required bool LodsPresent { get; init; }
    public required Vector4 LodDistances { get; init; }
    public required IReadOnlyList<string> Strings { get; init; }

    /// <summary>Whether the tree's user strings (the engine's tags for it, kept in the string table) name a tag:
    /// BillboardRandomTrees, BillboardRandomBranch, BillboardRandomGrass, BillboardRandomOff, RandomOff, TerrainNormalsOn,
    /// PigmentFloodOff, EnvMaterialSettingsOff, InteractiveOn.</summary>
    public bool HasTag(string tag) => Strings.Contains(tag, StringComparer.Ordinal);

    /// <summary>Which of the environment's random colours the tree's side views take, by its tags: 0 the trees', 1 the
    /// branches', 2 the grass's, 3 none (the picture's own colour, BillboardRandomOff). Untagged trees take the trees' (measured
    /// against REDkit's distant forests, which match that and not the pictures' own colour).</summary>
    public int BillboardColours => HasTag("BillboardRandomOff") ? 3 : HasTag("BillboardRandomBranch") ? 1 : HasTag("BillboardRandomGrass") ? 2 : 0;

    /// <summary>The collision capsules: their two ends and radius.</summary>
    public required IReadOnlyList<(Vector3 From, Vector3 To, float Radius)> Collisions { get; init; }

    /// <summary>The side-view billboards' width, top and bottom, and each view.</summary>
    public required float BillboardWidth { get; init; }
    public required float BillboardTop { get; init; }
    public required float BillboardBottom { get; init; }
    public required IReadOnlyList<SpeedTreeBillboard> Billboards { get; init; }
    public required IReadOnlyList<SpeedTreeMaterial> Materials { get; init; }

    /// <summary>The billboards' material: its diffuse is the atlas of side views, or null when the file has none.</summary>
    public SpeedTreeMaterial? BillboardMaterial { get; init; }
    public required IReadOnlyList<IReadOnlyList<SpeedTreeDraw>> Lods { get; init; }

    public static bool IsSpeedTree(string path) => path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);

    /// <summary>A material's picture as the game keeps it: in the texture cache beside the tree, under its .dds name.</summary>
    public static string TexturePath(string treePath, string name) => Path.Combine(Path.GetDirectoryName(treePath) ?? "", name);

    public static SpeedTreeFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var tree = file.First("CSRTBaseTree") ?? throw new InvalidDataException(L.T("SpeedTreeFile.NotTree"));
        return ReadSrt(tree.Tail);
    }

    /// <summary>The SpeedTree data itself, found by its "SRT 07.0.0" heading.</summary>
    public static SpeedTreeFile ReadSrt(byte[] data)
    {
        int start = data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Magic));
        if (start < 0) throw new InvalidDataException(L.T("SpeedTreeFile.NotTree"));
        var r = new Reader(data, start);
        try
        {
            return Parse(r);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException(L.T("SpeedTreeFile.Damaged", r.At - start));
        }
    }

    private static SpeedTreeFile Parse(Reader r)
    {
        r.At += 16 + 4;
        var min = r.Vector3();
        var max = r.Vector3();
        bool lods = r.U32() != 0;
        var distances = new Vector4(r.F32(), r.F32(), r.F32(), r.F32());
        r.At += WindSize;

        // the strings: a count, each length (8 bytes), then the strings themselves, the first's length overlapping the
        // last length's spare half
        int count = r.U32();
        r.At += 4;
        var lengths = new int[count];
        for (int i = 0; i < count; i++) lengths[i] = BitConverter.ToInt32(r.Data, r.At + 8 * i);
        r.At += 8 * count - 4;
        var strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            var bytes = r.Data.AsSpan(r.At, lengths[i]);
            int end = bytes.IndexOf((byte)0);
            strings[i] = Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
            r.At += lengths[i];
        }
        r.Align();
        string Name(int index) => index >= 0 && index < strings.Length ? strings[index] : "";

        var collisions = new List<(Vector3, Vector3, float)>();
        int capsules = r.U32();
        for (int i = 0; i < capsules; i++)
        {
            r.At += 8;
            collisions.Add((r.Vector3(), r.Vector3(), r.F32()));
        }

        float width = r.F32(), top = r.F32(), bottom = r.F32();
        int views = r.U32();
        var coords = new Vector4[views];
        for (int i = 0; i < views; i++) coords[i] = new Vector4(r.F32(), r.F32(), r.F32(), r.F32());
        var billboards = new SpeedTreeBillboard[views];
        for (int i = 0; i < views; i++) billboards[i] = new SpeedTreeBillboard(coords[i], r.Data[r.At + i] != 0);
        r.At += views;
        r.Align();
        int cutoutVertices = r.U32(), cutoutIndices = r.U32();
        r.At += cutoutVertices * 8 + cutoutIndices * 2;
        r.Align();
        // the horizontal billboard (none of the game's trees has one): a flag, its texture coordinates and corners
        r.At += 4 + 80;
        // five user strings, the number of materials, then the shader path
        r.At += 20;
        int materials = r.U32();
        r.At += 12;

        var states = new List<(SpeedTreeMaterial Material, Declaration Layout)>();
        for (int i = 0; i < materials; i++) states.Add(RenderState(r.Data, r.At + i * RenderStateSize, Name));
        var billboardState = RenderState(r.Data, r.At + 2 * materials * RenderStateSize, Name).Item1;
        // the main render states, the shadow ones, and the billboards' main and shadow
        r.At += RenderStateSize * (2 * materials + 2);

        int lodCount = r.U32();
        var drawCounts = new int[lodCount];
        for (int i = 0; i < lodCount; i++, r.At += 24) drawCounts[i] = BitConverter.ToInt32(r.Data, r.At);
        var calls = new List<(int State, int Vertices, int Indices, bool Wide)>();
        for (int i = 0; i < drawCounts.Sum(); i++, r.At += 40)
            calls.Add((BitConverter.ToInt32(r.Data, r.At + 8), BitConverter.ToInt32(r.Data, r.At + 12),
                BitConverter.ToInt32(r.Data, r.At + 24), r.Data[r.At + 28] != 0));
        var draws = new List<SpeedTreeDraw>();
        foreach (var (state, vertices, indexCount, wide) in calls)
        {
            var layout = states[state].Layout;
            int vertexData = r.At;
            r.At += vertices * layout.Size;
            r.Align();
            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
                indices[i] = wide ? BitConverter.ToInt32(r.Data, r.At + 4 * i) : BitConverter.ToUInt16(r.Data, r.At + 2 * i);
            r.At += indexCount * (wide ? 4 : 2);
            r.Align();
            if (indices.Any(i => i >= vertices)) throw new InvalidDataException(L.T("SpeedTreeFile.Damaged", r.At));
            var positions = new Vector3[vertices];
            var uvs = new Vector2[vertices];
            Vector3[]? corners = layout.Has(PropertyCorner) ? new Vector3[vertices] : null;
            Vector3[]? normals = layout.Has(PropertyNormal) ? new Vector3[vertices] : null;
            float[]? occlusion = layout.Has(PropertyOcclusion) ? new float[vertices] : null;
            for (int v = 0; v < vertices; v++)
            {
                int at = vertexData + v * layout.Size;
                positions[v] = new Vector3(layout.Get(r.Data, at, PropertyPosition, 0), layout.Get(r.Data, at, PropertyPosition, 1), layout.Get(r.Data, at, PropertyPosition, 2));
                uvs[v] = new Vector2(layout.Get(r.Data, at, PropertyUv, 0), layout.Get(r.Data, at, PropertyUv, 1));
                if (corners is not null)
                    corners[v] = new Vector3(layout.Get(r.Data, at, PropertyCorner, 0), layout.Get(r.Data, at, PropertyCorner, 1), layout.Get(r.Data, at, PropertyCorner, 2));
                if (normals is not null)
                {
                    // bytes hold -1 to 1 as 0 to 255
                    var n = new Vector3(layout.Get(r.Data, at, PropertyNormal, 0), layout.Get(r.Data, at, PropertyNormal, 1), layout.Get(r.Data, at, PropertyNormal, 2));
                    if (layout.IsBytes(PropertyNormal)) n = n * 2 - Vector3.One;
                    normals[v] = n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitZ;
                }
                if (occlusion is not null) occlusion[v] = layout.Get(r.Data, at, PropertyOcclusion, 0);
            }
            draws.Add(new SpeedTreeDraw(state, positions, uvs, corners, indices, normals, occlusion));
        }
        var lodList = new List<IReadOnlyList<SpeedTreeDraw>>();
        int taken = 0;
        foreach (int n in drawCounts)
        {
            lodList.Add(draws.GetRange(taken, n));
            taken += n;
        }
        return new SpeedTreeFile
        {
            Min = min, Max = max, LodsPresent = lods, LodDistances = distances, Strings = strings, Collisions = collisions,
            BillboardWidth = width, BillboardTop = top, BillboardBottom = bottom, Billboards = billboards,
            Materials = states.Select(s => s.Material).ToList(), Lods = lodList,
            BillboardMaterial = views > 0 ? billboardState : null,
        };
    }

    /// <summary>
    /// A render state: eight pictures (string numbers, 8 bytes each), its lighting settings, five bytes saying what it
    /// draws (branches, fronds, leaves, facing leaves, rigid meshes), then its vertex declaration: 16 attributes of 14
    /// bytes (a stream, a format, then four properties, four components and four byte offsets), the vertex size at 700
    /// and its name at 704.
    /// </summary>
    private static (SpeedTreeMaterial, Declaration) RenderState(byte[] data, int at, Func<int, string> name)
    {
        var textures = new string[8];
        for (int i = 0; i < 8; i++) textures[i] = name(BitConverter.ToInt32(data, at + 8 * i));
        var layout = new Declaration(BitConverter.ToInt32(data, at + 700));
        for (int i = 0; i < 16; i++)
        {
            int e = at + 229 + 14 * i;
            byte format = data[e + 1];
            if (format > 2) continue;
            for (int c = 0; c < 4; c++)
                if (data[e + 2 + c] < Declaration.Properties && data[e + 6 + c] < 4)
                    layout.Set(data[e + 2 + c], data[e + 6 + c], format, data[e + 10 + c]);
        }
        var material = new SpeedTreeMaterial(textures, name(BitConverter.ToInt32(data, at + 704)), data[at + 224] != 0, data[at + 225] != 0,
            data[at + 226] != 0, data[at + 227] != 0, data[at + 228] != 0, data[at + 180] != 0) { Lighting = data.AsSpan(at + 64, 160).ToArray() };
        return (material, layout);
    }

    /// <summary>Where each property's components sit in a vertex: format (0 float, 1 half, 2 byte) and offset, or none.</summary>
    private sealed class Declaration(int size)
    {
        public const int Properties = 19;
        private readonly (int Format, int Offset)?[,] _where = new (int, int)?[Properties, 4];

        public int Size { get; } = size;

        public void Set(int property, int component, int format, int offset) => _where[property, component] = (format, offset);

        public bool Has(int property) => _where[property, 0] is not null;

        public bool IsBytes(int property) => _where[property, 0] is (2, _);

        public float Get(byte[] data, int vertex, int property, int component) => _where[property, component] switch
        {
            (0, int o) => BitConverter.ToSingle(data, vertex + o),
            (1, int o) => (float)BitConverter.ToHalf(data, vertex + o),
            (2, int o) => data[vertex + o] / 255f,
            _ => 0,
        };
    }

    private sealed class Reader(byte[] data, int start)
    {
        public byte[] Data { get; } = data;
        public int At { get; set; } = start;

        public int U32()
        {
            int v = BitConverter.ToInt32(Data, At);
            At += 4;
            return v;
        }

        public float F32()
        {
            float v = BitConverter.ToSingle(Data, At);
            At += 4;
            return v;
        }

        public Vector3 Vector3() => new(F32(), F32(), F32());

        /// <summary>To the next 4 bytes, counted from the start of the SpeedTree data.</summary>
        public void Align() => At = start + (At - start + 3) / 4 * 4;
    }
}
