using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using W3ModWorkbench.Core.World;
using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFormat = SharpDX.DXGI.Format;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace W3ModWorkbench;

/// <summary>A kind of plant for the 3D view: its SpeedTree file, whether it's grass, its pictures by path, and where it grows.</summary>
public sealed record PlantKind3D(string Path, SpeedTreeFile Tree, bool Grass, IReadOnlyDictionary<string, BitmapSource?> Pictures,
    IReadOnlyList<FoliageInstance> Instances);

// The trees, bushes and grass in 3D, from their SpeedTree files: each kind's LODs as the file keeps them (branches,
// fronds and leaf cards, the facing leaves turned to the camera), its side views (the billboards) further off, grass
// only near; and the sun's shadows of them all round the camera, in a shadow map the ground and the plants read.
public sealed partial class WorldView3D
{
    /// <summary>The shadow and billboard constants, in the order of the HLSL's Plants.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PlantConstants
    {
        public Matrix4x4 SunViewProjection;
        public Vector4 CameraRight, CameraUp, BillboardShape;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public Vector4[] BillboardViews;
        public Vector4 BillboardStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlantVertex
    {
        public Vector3 Position;
        public Vector2 Uv;
        public Vector3 Normal;
        public Vector4 Corner;
        public float Kind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlantPlace
    {
        public Vector4 Place;
        public Vector2 Turn;
    }

    /// <summary>One LOD of a kind on the card: its vertices and indices, and each draw's picture and index range.</summary>
    private sealed record PlantLod(Buffer Vertices, Buffer Indices, (ShaderResourceView? Picture, int Start, int Count, int Base, ShaderResourceView? Specular, Vector4 Material)[] Draws);

    private sealed class PlantKindGpu
    {
        public required PlantKind3D Kind { get; init; }
        public required PlantLod[] Lods { get; init; }
        public ShaderResourceView? Billboard { get; init; }
        public ShaderResourceView? BillboardNormals { get; init; }
        public Vector4[] Views { get; init; } = [];
    }

    private readonly List<PlantKindGpu> _plants = [];
    // the plants by 64 m cell: the kind (its place in _plants) and the instance
    private readonly Dictionary<(int, int), List<(int Kind, FoliageInstance Plant)>> _plantCells = [];
    private readonly Dictionary<string, ShaderResourceView?> _plantPictures = new(StringComparer.OrdinalIgnoreCase);
    private const float PlantCell = 64;
    private VertexShader? _plantVS, _billboardVS, _plantShadowVS, _billboardShadowVS;
    private PixelShader? _plantPS, _plantShadowPS;
    private InputLayout? _plantLayout, _billboardLayout;
    private Buffer? _plantPlaces, _plantConstants, _billboardQuad, _billboardIndices, _plantMaterial;
    private int _plantPlaceCapacity;
    private Texture2D? _plantShadowTexture;
    private DepthStencilView? _plantShadowDepth;
    private ShaderResourceView? _plantShadow;
    private SamplerState? _shadowCompare;
    private RasterizerState? _shadowRaster;
    private const int PlantShadowSize = 4096;
    private Matrix4x4 _sunMatrix;
    private Vector3 _sunCentre, _sunFor;
    private bool _sunValid;
    private const float PlantShadowReach = 160;

    /// <summary>How far the SpeedTree files' LOD distances are stretched, as the game's foliage distance setting does.</summary>
    public float PlantDistanceScale { get; set; } = 2;

    /// <summary>How far trees are drawn at all, as their side views.</summary>
    public float PlantFarthest { get; set; } = 1500;

    /// <summary>Plants drawn in the last frame.</summary>
    public int PlantsDrawn { get; private set; }

    private void StartPlants()
    {
        var source = Shaders;
        using (var code = Compile(source, "PlantVS", "vs_4_0"))
        {
            _plantVS = new VertexShader(Device, code);
            _plantLayout = new InputLayout(Device, code, [
                new InputElement("POSITION", 0, DxgiFormat.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
                new InputElement("TEXCOORD", 0, DxgiFormat.R32G32_Float, 12, 0, InputClassification.PerVertexData, 0),
                new InputElement("NORMAL", 0, DxgiFormat.R32G32B32_Float, 20, 0, InputClassification.PerVertexData, 0),
                new InputElement("TEXCOORD", 1, DxgiFormat.R32G32B32A32_Float, 32, 0, InputClassification.PerVertexData, 0),
                new InputElement("TEXCOORD", 2, DxgiFormat.R32_Float, 48, 0, InputClassification.PerVertexData, 0),
                new InputElement("PLACE", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TURN", 0, DxgiFormat.R32G32_Float, 16, 1, InputClassification.PerInstanceData, 1)]);
        }
        using (var code = Compile(source, "BillboardVS", "vs_4_0"))
        {
            _billboardVS = new VertexShader(Device, code);
            _billboardLayout = new InputLayout(Device, code, [
                new InputElement("POSITION", 0, DxgiFormat.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
                new InputElement("PLACE", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TURN", 0, DxgiFormat.R32G32_Float, 16, 1, InputClassification.PerInstanceData, 1)]);
        }
        using (var code = Compile(source, "PlantShadowVS", "vs_4_0")) _plantShadowVS = new VertexShader(Device, code);
        using (var code = Compile(source, "BillboardShadowVS", "vs_4_0")) _billboardShadowVS = new VertexShader(Device, code);
        using (var code = Compile(source, "PlantPS", "ps_4_0")) _plantPS = new PixelShader(Device, code);
        using (var code = Compile(source, "PlantShadowPS", "ps_4_0")) _plantShadowPS = new PixelShader(Device, code);
        _plantMaterial = new Buffer(Device, 32, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _plantConstants = new Buffer(Device, Utilities.SizeOf<Matrix4x4>() + 16 * (3 + 32 + 1), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _billboardQuad = Buffer.Create(Device, BindFlags.VertexBuffer, new[] { new Vector2(-0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(-0.5f, 1) });
        _billboardIndices = Buffer.Create(Device, BindFlags.IndexBuffer, new ushort[] { 0, 1, 2, 0, 2, 3 });
        _plantShadowTexture = new Texture2D(Device, new Texture2DDescription
        {
            Width = PlantShadowSize, Height = PlantShadowSize, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R32_Typeless,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });
        _plantShadowDepth = new DepthStencilView(Device, _plantShadowTexture, new DepthStencilViewDescription { Format = DxgiFormat.D32_Float, Dimension = DepthStencilViewDimension.Texture2D });
        _plantShadow = new ShaderResourceView(Device, _plantShadowTexture, new ShaderResourceViewDescription
        {
            Format = DxgiFormat.R32_Float, Dimension = ShaderResourceViewDimension.Texture2D, Texture2D = new ShaderResourceViewDescription.Texture2DResource { MipLevels = 1 },
        });
        _shadowCompare = new SamplerState(Device, new SamplerStateDescription
        {
            Filter = Filter.ComparisonMinMagLinearMipPoint, AddressU = TextureAddressMode.Border, AddressV = TextureAddressMode.Border,
            AddressW = TextureAddressMode.Border, BorderColor = new SharpDX.Mathematics.Interop.RawColor4(1, 1, 1, 1),
            ComparisonFunction = Comparison.LessEqual, MaximumLod = float.MaxValue,
        });
        // depth bias against the shadow map seeing its own surface, and both sides of leaves drawn
        _shadowRaster = new RasterizerState(Device, new RasterizerStateDescription
        {
            CullMode = CullMode.None, FillMode = FillMode.Solid, IsDepthClipEnabled = true, DepthBias = 200, SlopeScaledDepthBias = 2.5f,
        });
    }

    /// <summary>
    /// The plants to draw: each kind with its pictures and where it grows. Their LODs and pictures go to the card now;
    /// kinds whose file couldn't be read are left out.
    /// </summary>
    public void SetPlants(IReadOnlyList<PlantKind3D> kinds)
    {
        EnsureDevice();
        DisposePlants();
        foreach (var kind in kinds)
        {
            ShaderResourceView? Picture(string name)
            {
                if (name.Length == 0) return null;
                string path = SpeedTreeFile.TexturePath(kind.Path, name);
                if (_plantPictures.TryGetValue(path, out var known)) return known;
                var view = kind.Pictures.GetValueOrDefault(path) is { } picture ? CutoutTexture(picture) : null;
                _plantPictures[path] = view;
                return view;
            }
            ShaderResourceView? SpecularPicture(string name)
            {
                if (name.Length == 0) return null;
                string path = SpeedTreeFile.TexturePath(kind.Path, name);
                if (_plantPictures.TryGetValue(path + "|s", out var known)) return known;
                var view = kind.Pictures.GetValueOrDefault(path) is { } picture ? StraightTexture(picture) : null;
                _plantPictures[path + "|s"] = view;
                return view;
            }
            var lods = new List<PlantLod>();
            foreach (var lod in kind.Tree.Lods)
            {
                var vertices = new List<PlantVertex>();
                var indices = new List<int>();
                var draws = new List<(ShaderResourceView?, int, int, int, ShaderResourceView?, Vector4)>();
                foreach (var draw in lod)
                {
                    var material = kind.Tree.Materials[draw.Material];
                    int first = vertices.Count;
                    for (int v = 0; v < draw.Positions.Length; v++)
                        vertices.Add(new PlantVertex
                        {
                            Position = draw.Positions[v], Uv = draw.Uvs[v], Normal = draw.Normals?[v] ?? Vector3.UnitZ,
                            Corner = new Vector4(draw.Corners?[v] ?? Vector3.Zero, draw.Occlusion?[v] ?? 1),
                            // the random colours and translucency by kind: grass, branches (bark), else leaves
                            // grass carries how high up the plant the vertex is (0 to 0.45 over 2), for its pigment
                            Kind = kind.Grass || material.Grass ? 2 + 0.45f * Math.Clamp(draw.Positions[v].Z / MathF.Max(kind.Tree.Max.Z, 0.05f), 0, 1)
                                : material.Branches ? 1 : 0,
                        });
                    // its specular map and the render state's settings the game's SpeedTree shader reads with it: the specular
                    // colour's green, the transmission colour's green, the glossiness (shininess over 128)
                    var specular = SpecularPicture(material.Specular);
                    draws.Add((Picture(material.Diffuse), indices.Count, draw.Indices.Length, first, specular,
                        new Vector4(material.SpecularColour.Y, material.TransmissionColour.Y, specular is null ? -1 : MathF.Max(material.Shininess / 128, 1e-3f), material.AlphaScalar)));
                    indices.AddRange(draw.Indices);
                }
                if (vertices.Count == 0) continue;
                lods.Add(new PlantLod(Buffer.Create(Device, BindFlags.VertexBuffer, vertices.ToArray()),
                    Buffer.Create(Device, BindFlags.IndexBuffer, indices.ToArray()), draws.ToArray()));
            }
            if (lods.Count == 0) continue;
            int index = _plants.Count;
            _plants.Add(new PlantKindGpu
            {
                Kind = kind, Lods = lods.ToArray(),
                Billboard = kind.Tree.BillboardMaterial is { } b ? Picture(b.Diffuse) : null,
                BillboardNormals = kind.Tree.BillboardMaterial is { Normal.Length: > 0 } bn ? Picture(bn.Normal) : null,
                Views = kind.Tree.Billboards.Take(32).Select(v => v.TexCoords).ToArray(),
            });
            foreach (var plant in kind.Instances)
            {
                var key = ((int)MathF.Floor(plant.Position.X / PlantCell), (int)MathF.Floor(plant.Position.Y / PlantCell));
                if (!_plantCells.TryGetValue(key, out var cell)) _plantCells[key] = cell = [];
                cell.Add((index, plant));
            }
        }
        BuildCanopy(kinds);
        Invalidate();
    }

    /// <summary>How strongly each crown over a point hides the sky's light from it (set against REDkit's forests).</summary>
    public float CanopyDensity { get; set; } = 1;

    private ShaderResourceView? _canopy;

    /// <summary>
    /// The canopy map over the terrain, a texel every few metres: the trees' crowns over it (each crown a disc of the
    /// tree's width, counted as the share of the texels it covers, smoothed over the neighbours) and the canopy's top.
    /// </summary>
    private void BuildCanopy(IReadOnlyList<PlantKind3D> kinds)
    {
        _canopy?.Dispose();
        _canopy = null;
        if (_terrain is not { } terrain) return;
        int side = Math.Clamp((int)(terrain.Size / 4), 64, 2048);
        float cell = terrain.Size / side;
        var cover = new float[side * side];
        var top = new float[side * side];
        Array.Fill(top, float.MinValue);
        foreach (var kind in kinds)
        {
            if (kind.Grass) continue;
            var tree = kind.Tree;
            float radius = MathF.Max(MathF.Max(-tree.Min.X, tree.Max.X), MathF.Max(-tree.Min.Y, tree.Max.Y));
            // bushes and stumps lower than a person hide little of the sky
            if (tree.Max.Z < 2 || radius <= 0) continue;
            foreach (var plant in kind.Instances)
            {
                float r = radius * plant.Scale, height = plant.Position.Z + tree.Max.Z * plant.Scale;
                float area = MathF.PI * r * r / (cell * cell);
                int x0 = (int)((plant.Position.X - r - terrain.Corner.X) / cell), x1 = (int)((plant.Position.X + r - terrain.Corner.X) / cell);
                int y0 = (int)((plant.Position.Y - r - terrain.Corner.Y) / cell), y1 = (int)((plant.Position.Y + r - terrain.Corner.Y) / cell);
                int count = Math.Max(1, (x1 - x0 + 1) * (y1 - y0 + 1));
                for (int y = Math.Max(0, y0); y <= Math.Min(side - 1, y1); y++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(side - 1, x1); x++)
                    {
                        // rows from the north, as the terrain's textures
                        int at = (side - 1 - y) * side + x;
                        cover[at] += MathF.Min(1, area / count);
                        top[at] = MathF.Max(top[at], height);
                    }
            }
        }
        // smoothed over the neighbours, so a gap of a tree or two still sees little sky
        var data = new float[side * side * 2];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                float sum = 0, high = float.MinValue;
                int n = 0;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= side || yy >= side) continue;
                        sum += cover[yy * side + xx];
                        high = MathF.Max(high, top[yy * side + xx]);
                        n++;
                    }
                data[2 * (y * side + x)] = sum / n;
                data[2 * (y * side + x) + 1] = high;
            }
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            using var texture = new Texture2D(Device, new Texture2DDescription
            {
                Width = side, Height = side, MipLevels = 1, ArraySize = 1, Format = SharpDX.DXGI.Format.R32G32_Float,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new SharpDX.DataRectangle(handle.AddrOfPinnedObject(), side * 8));
            _canopy = new ShaderResourceView(Device, texture);
        }
        finally { handle.Free(); }
    }

    private void DisposePlants()
    {
        _sunValid = false;
        foreach (var kind in _plants)
            foreach (var lod in kind.Lods)
            {
                lod.Vertices.Dispose();
                lod.Indices.Dispose();
            }
        foreach (var view in _plantPictures.Values) view?.Dispose();
        _plantPictures.Clear();
        _plants.Clear();
        _plantCells.Clear();
        _cellGroups.Clear();
        _grassGrown.Clear();
    }

    /// <summary>
    /// The plants to draw, sorted by kind and LOD (the last slot of each kind its billboards): those near enough, as
    /// fine as their distance asks, and in view when <paramref name="planes"/> is given.
    /// </summary>
    private List<PlantPlace>[][] GatherPlants(Vector3 from, float reach, Vector4[]? planes, bool grass = true, Vector3? detailFrom = null)
    {
        // the detail is chosen by the distance from detailFrom (the eye, for the sun's view round a point ahead of it,
        // so a plant casts its shadow with the same LOD it's seen with)
        var eye = detailFrom ?? from;
        var lists = new List<PlantPlace>[_plants.Count][];
        int cells = (int)MathF.Ceiling(reach / PlantCell);
        int cx = (int)MathF.Floor(from.X / PlantCell), cy = (int)MathF.Floor(from.Y / PlantCell);
        var here = new Vector2(from.X, from.Y);
        for (int y = cy - cells; y <= cy + cells; y++)
            for (int x = cx - cells; x <= cx + cells; x++)
            {
                if (!_plantCells.ContainsKey((x, y))) continue;
                var low = new Vector2(x, y) * PlantCell;
                float nearest = Vector2.Distance(Vector2.Clamp(here, low, low + new Vector2(PlantCell)), here);
                if (nearest > reach) continue;
                float farthest = Vector2.Distance(here, new Vector2(here.X < low.X + PlantCell / 2 ? low.X + PlantCell : low.X, here.Y < low.Y + PlantCell / 2 ? low.Y + PlantCell : low.Y));
                if (planes is not null)
                {
                    var min = new Vector3(low, -200);
                    if (!InView(planes, min, min + new Vector3(PlantCell, PlantCell, 600))) continue;
                }
                foreach (var group in CellGroups(x, y))
                {
                    var kind = _plants[group.Kind];
                    if (!grass && kind.Kind.Grass) continue;
                    var tree = kind.Kind.Tree;
                    var d = tree.LodDistances * (kind.Kind.Grass ? 1 : PlantDistanceScale);
                    bool billboards = kind.Billboard is not null && kind.Views.Length > 0;
                    float limit = MathF.Min(billboards ? PlantFarthest : d.W, reach);
                    if (nearest > limit) continue;
                    int Slot(float distance) => billboards && distance > d.Z ? kind.Lods.Length
                        : distance <= d.X || kind.Lods.Length == 1 ? 0
                        : Math.Clamp(1 + (int)((distance - d.X) / Math.Max(d.Y - d.X, 1) * (kind.Lods.Length - 1)), 1, kind.Lods.Length - 1);
                    var slots = lists[group.Kind] ??= new List<PlantPlace>[kind.Lods.Length + 1];
                    // a whole cell in one band of detail goes in at once, its plants not looked at one by one
                    int nearSlot = Slot(nearest);
                    if (detailFrom is null && farthest <= limit && Slot(farthest) == nearSlot)
                    {
                        (slots[nearSlot] ??= []).AddRange(group.Places);
                        continue;
                    }
                    for (int i = 0; i < group.Places.Length; i++)
                    {
                        var place = group.Places[i];
                        var at = new Vector3(place.Place.X, place.Place.Y, place.Place.Z);
                        float distance = Vector3.Distance(from, at);
                        if (distance > limit) continue;
                        (slots[Slot(detailFrom is null ? distance : Vector3.Distance(eye, at))] ??= []).Add(place);
                    }
                }
            }
        return lists;
    }

    /// <summary>A cell's plants by kind, ready to draw, made once and again when the cell gets more (its grass grown).</summary>
    private List<(int Kind, PlantPlace[] Places)> CellGroups(int x, int y)
    {
        var cell = _plantCells[(x, y)];
        if (_cellGroups.TryGetValue((x, y), out var made) && made.Count == cell.Count) return made.Groups;
        var groups = cell.GroupBy(p => p.Kind).Select(g => (g.Key, g.Select(p => new PlantPlace
        {
            Place = new Vector4(p.Plant.Position, p.Plant.Scale), Turn = new Vector2(MathF.Cos(p.Plant.Yaw), MathF.Sin(p.Plant.Yaw)),
        }).ToArray())).ToList();
        _cellGroups[(x, y)] = (cell.Count, groups);
        return groups;
    }

    private readonly Dictionary<(int, int), (int Count, List<(int Kind, PlantPlace[] Places)> Groups)> _cellGroups = [];

    /// <summary>The gathered plants into the instance buffer, all lists one after another; each list's first place returned.</summary>
    private int[][] UploadPlaces(List<PlantPlace>[][] lists)
    {
        var c = Context!;
        int total = lists.Sum(k => k?.Sum(l => l?.Count ?? 0) ?? 0);
        if (total > _plantPlaceCapacity)
        {
            _plantPlaces?.Dispose();
            _plantPlaceCapacity = Math.Max(total, _plantPlaceCapacity * 2);
            _plantPlaces = new Buffer(Device, new BufferDescription(_plantPlaceCapacity * Utilities.SizeOf<PlantPlace>(), ResourceUsage.Dynamic,
                BindFlags.VertexBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        }
        var starts = new int[lists.Length][];
        if (total == 0 || _plantPlaces is null) return starts;
        c.MapSubresource(_plantPlaces, MapMode.WriteDiscard, MapFlags.None, out var stream);
        int at = 0;
        for (int k = 0; k < lists.Length; k++)
        {
            if (lists[k] is not { } slots) continue;
            starts[k] = new int[slots.Length];
            for (int s = 0; s < slots.Length; s++)
            {
                starts[k][s] = at;
                if (slots[s] is not { Count: > 0 } list) continue;
                stream.WriteRange(CollectionsMarshal.AsSpan(list).ToArray());
                at += list.Count;
            }
        }
        c.UnmapSubresource(_plantPlaces, 0);
        return starts;
    }

    /// <summary>The sun's view of the plants round the camera: where it looks from, snapped to its texels so the shadows don't shimmer.</summary>
    private Matrix4x4 SunView(Vector3 toLight)
    {
        var up = MathF.Abs(toLight.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        var look = Matrix4x4.CreateLookAt(Vector3.Zero, -toLight, up);
        var centre = Vector3.Transform(_eye, look);
        float texel = 2 * PlantShadowReach / PlantShadowSize;
        centre = new Vector3(MathF.Round(centre.X / texel) * texel, MathF.Round(centre.Y / texel) * texel, centre.Z);
        var projection = Matrix4x4.CreateOrthographicOffCenter(centre.X - PlantShadowReach, centre.X + PlantShadowReach,
            centre.Y - PlantShadowReach, centre.Y + PlantShadowReach, -centre.Z - 1500, -centre.Z + 1500);
        return look * projection;
    }

    /// <summary>Draws the plants' shadow map and sets the constants and textures the ground, water and plants read it by.</summary>
    private void PreparePlants(Vector3 toLight, Matrix4x4 view)
    {
        var c = Context!;
        bool shadows = (_plants.Count > 0 || _meshes.Count > 0) && toLight.Z > 0.02f;
        // the shadow map is kept until the camera has moved far enough across it or the light has moved
        // round the eye, not ahead of it, so turning the camera doesn't draw it again
        var centre = _eye;
        bool redraw = !_sunValid || Vector3.Distance(centre, _sunCentre) > PlantShadowReach * 0.08f || Vector3.DistanceSquared(toLight, _sunFor) > 1e-7f;
        if (redraw)
        {
            _sunMatrix = SunView(toLight);
            _sunCentre = centre;
            _sunFor = toLight;
            _sunValid = shadows;
        }
        var sun = _sunMatrix;
        Matrix4x4.Invert(view, out var camera);
        var constants = new PlantConstants
        {
            SunViewProjection = sun,
            CameraRight = new Vector4(Vector3.Normalize(new Vector3(camera.M11, camera.M12, camera.M13)), 0),
            CameraUp = new Vector4(Vector3.Normalize(new Vector3(camera.M21, camera.M22, camera.M23)), shadows ? 1 : 0),
            BillboardViews = new Vector4[32],
        };
        c.UpdateSubresource(StructureBytes(constants), _plantConstants);
        c.PixelShader.SetShaderResource(15, null);
        if (shadows && redraw)
        {
            c.ClearDepthStencilView(_plantShadowDepth, DepthStencilClearFlags.Depth, 1, 0);
            c.OutputMerger.SetRenderTargets(_plantShadowDepth, (RenderTargetView?)null);
            c.Rasterizer.SetViewport(0, 0, PlantShadowSize, PlantShadowSize);
            c.Rasterizer.State = _shadowRaster;
            c.OutputMerger.SetDepthStencilState(null);
            c.OutputMerger.SetBlendState(null);
            c.VertexShader.SetConstantBuffer(4, _plantConstants);
            c.PixelShader.SetConstantBuffer(4, _plantConstants);
            c.PixelShader.SetSamplers(0, _sampler, _tiled);
            // the sun's camera: facing leaves turn to it
            Matrix4x4.Invert(Matrix4x4.CreateLookAt(Vector3.Zero, -toLight, MathF.Abs(toLight.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ), out var sunCamera);
            var shadowConstants = constants;
            shadowConstants.CameraRight = new Vector4(sunCamera.M11, sunCamera.M12, sunCamera.M13, 0);
            shadowConstants.CameraUp = new Vector4(sunCamera.M21, sunCamera.M22, sunCamera.M23, 1);
            c.UpdateSubresource(StructureBytes(shadowConstants), _plantConstants);
            var lists = GatherPlants(centre, PlantShadowReach * 1.5f, null, detailFrom: _eye);
            // grass casts no shadows, as SpeedTree grass doesn't in the game
            for (int k = 0; k < lists.Length; k++)
                if (_plants[k].Kind.Grass) lists[k] = null!;
            DrawPlantLists(lists, _plantShadowVS!, _billboardShadowVS!, _plantShadowPS!, constants.BillboardViews);
            DrawMeshesShadow(centre, PlantShadowReach * 1.5f);
            c.UpdateSubresource(StructureBytes(constants), _plantConstants);
            c.Rasterizer.SetViewport(0, 0, PixelWidth, PixelHeight);
            c.Rasterizer.State = _raster;
            c.OutputMerger.SetRenderTargets(DepthView, HdrView);
        }
        c.VertexShader.SetConstantBuffer(4, _plantConstants);
        c.PixelShader.SetConstantBuffer(4, _plantConstants);
        c.PixelShader.SetShaderResource(15, shadows ? _plantShadow : null);
        c.PixelShader.SetSampler(2, _shadowCompare);
    }

    /// <summary>The plants in view into the depth buffer only (their cutouts kept), for the ambient occlusion.</summary>
    private void DrawPlantsDepth(Vector4[] planes)
    {
        if (_plants.Count == 0) return;
        Context!.Rasterizer.State = _shadowRaster;
        DrawPlantLists(GatherPlants(_eye, 200, planes, grass: false), _plantVS!, _billboardVS!, _plantShadowPS!, new Vector4[32]);
        Context.Rasterizer.State = _raster;
    }

    /// <summary>The plants in view, after the ground.</summary>
    private void DrawPlants(Vector4[] planes)
    {
        if (_plants.Count == 0) return;
        var c = Context!;
        c.OutputMerger.SetDepthStencilState(null);
        c.OutputMerger.SetBlendState(null);
        c.Rasterizer.State = _raster;
        // the heights, colour map, control map and textures, for the ground's normal the grass is lit by and its pigment
        c.PixelShader.SetShaderResources(0, _heightMap, _colourMap, _controlMap, _diffuse);
        var lists = GatherPlants(_eye, PlantFarthest, planes);
        PlantsDrawn = DrawPlantLists(lists, _plantVS!, _billboardVS!, _plantPS!, new Vector4[32]);
    }

    private int DrawPlantLists(List<PlantPlace>[][] lists, VertexShader meshVS, VertexShader billboardVS, PixelShader ps, Vector4[] views)
    {
        var c = Context!;
        var starts = UploadPlaces(lists);
        if (_plantPlaces is null) return 0;
        int drawn = 0;
        c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        c.PixelShader.Set(ps);
        c.PixelShader.SetConstantBuffer(10, _plantMaterial);
        for (int k = 0; k < lists.Length; k++)
        {
            if (lists[k] is not { } slots) continue;
            var kind = _plants[k];
            // the tree's own switches: PigmentFloodOff keeps the ground's colour off its grass
            var flags = new Vector4(kind.Kind.Tree.HasTag("PigmentFloodOff") ? 0 : 1, 0, 0, 0);
            for (int s = 0; s < kind.Lods.Length; s++)
            {
                if (slots[s] is not { Count: > 0 } list) continue;
                var lod = kind.Lods[s];
                c.InputAssembler.InputLayout = _plantLayout;
                c.VertexShader.Set(meshVS);
                c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(lod.Vertices, Utilities.SizeOf<PlantVertex>(), 0),
                    new VertexBufferBinding(_plantPlaces, Utilities.SizeOf<PlantPlace>(), 0));
                c.InputAssembler.SetIndexBuffer(lod.Indices, DxgiFormat.R32_UInt, 0);
                foreach (var (picture, start, count, first, specular, material) in lod.Draws)
                {
                    if (picture is null) continue;
                    c.PixelShader.SetShaderResource(14, picture);
                    c.PixelShader.SetShaderResource(24, specular);
                    c.UpdateSubresource(new[] { material, flags }, _plantMaterial);
                    c.DrawIndexedInstanced(count, list.Count, start, first, starts[k][s]);
                }
                drawn += list.Count;
            }
            // the side views: the billboard shape and its views for this kind
            if (slots[kind.Lods.Length] is { Count: > 0 } far && kind.Billboard is { } atlas)
            {
                var tree = kind.Kind.Tree;
                c.UpdateSubresource(new[] { new Vector4(0, 0, -1, tree.BillboardMaterial?.AlphaScalar ?? 1), flags }, _plantMaterial);
                var constants = ReadConstants();
                constants.BillboardShape = new Vector4(tree.BillboardWidth, tree.BillboardTop, tree.BillboardBottom, kind.Views.Length);
                constants.BillboardStyle = new Vector4(tree.BillboardColours, 0, 0, 0);
                for (int v = 0; v < 32; v++) constants.BillboardViews[v] = v < kind.Views.Length ? kind.Views[v] : default;
                c.UpdateSubresource(StructureBytes(constants), _plantConstants);
                c.InputAssembler.InputLayout = _billboardLayout;
                c.VertexShader.Set(billboardVS);
                c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_billboardQuad, 8, 0),
                    new VertexBufferBinding(_plantPlaces, Utilities.SizeOf<PlantPlace>(), 0));
                c.InputAssembler.SetIndexBuffer(_billboardIndices, DxgiFormat.R16_UInt, 0);
                c.PixelShader.SetShaderResource(14, atlas);
                c.PixelShader.SetShaderResource(19, kind.BillboardNormals);
                c.DrawIndexedInstanced(6, far.Count, 0, 0, starts[k][kind.Lods.Length]);
                drawn += far.Count;
            }
        }
        c.PixelShader.SetShaderResource(14, null);
        c.PixelShader.SetShaderResource(19, null);
        return drawn;
    }

    // the constants last written, kept so billboards can change only their own part
    private PlantConstants _lastPlantConstants;

    private PlantConstants ReadConstants()
    {
        var copy = _lastPlantConstants;
        copy.BillboardViews = (Vector4[])(copy.BillboardViews?.Clone() ?? new Vector4[32]);
        return copy;
    }

    private byte[] StructureBytes(PlantConstants constants)
    {
        _lastPlantConstants = constants;
        var bytes = new byte[Utilities.SizeOf<Matrix4x4>() + 16 * (3 + 32 + 1)];
        MemoryMarshal.Write(bytes, in constants.SunViewProjection);
        int at = Utilities.SizeOf<Matrix4x4>();
        foreach (var v in new[] { constants.CameraRight, constants.CameraUp, constants.BillboardShape }.Concat(constants.BillboardViews ?? new Vector4[32]).Append(constants.BillboardStyle))
        {
            MemoryMarshal.Write(bytes.AsSpan(at), in v);
            at += 16;
        }
        return bytes;
    }

    private void DisposePlantDevice()
    {
        DisposePlants();
        foreach (var d in new IDisposable?[] { _canopy, _plantMaterial, _plantVS, _billboardVS, _plantShadowVS, _billboardShadowVS, _plantPS, _plantShadowPS, _plantLayout,
            _billboardLayout, _plantPlaces, _plantConstants, _billboardQuad, _billboardIndices, _plantShadow, _plantShadowDepth, _plantShadowTexture,
            _shadowCompare, _shadowRaster })
            d?.Dispose();
    }
}
