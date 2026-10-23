using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using W3ModWorkbench.Core.Meshes;
using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFormat = SharpDX.DXGI.Format;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace W3ModWorkbench;

/// <summary>
/// A mesh the world's layers place, for the 3D view: its file, each material's colour picture and look (as the mesh
/// viewer reads them), how far off it's drawn, and where each copy stands.
/// </summary>
public sealed record MeshKind3D(string Path, MeshFile Mesh, IReadOnlyList<(BitmapSource? Picture, MaterialLook? Look, MeshUse Use)> Materials,
    float AutoHide, IReadOnlyList<Matrix4x4> Places);

/// <summary>
/// What a material's chunks are for in the 3D view: drawn; only casting the sun's shadow (the merged shadow meshes the
/// cooked worlds keep for their buildings); or neither (volumes for weather and interiors, see-through surfaces).
/// </summary>
public enum MeshUse { Drawn, ShadowOnly, Hidden }

// The layers' meshes in 3D (buildings, walls, props): each mesh's LODs as its file keeps them, each copy placed by its
// component on its entity, drawn instanced by mesh and LOD; those out of view or further off than they're drawn left
// out. Lit as the ground is (the sun, its shadows, the probes' ambient, the canopy, the fog); their materials read as
// the mesh viewer reads them (pbr_std: roughness in the normal map's alpha, specularity from it by RSpecScale and
// RSpecBase, the tint mask's red baked occlusion).
public sealed partial class WorldView3D
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MeshVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public System.Numerics.Vector2 Uv;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshMaterialConstants
    {
        public Vector4 Spec;       // RSpecScale, RSpecBase, the least and most roughness
        public Vector4 SpecColour; // the specular colour (linear); how much of the tint mask's red occlusion counts
        public Vector4 Flags;      // whether there's a normal map, a tint mask; the alpha cut (0 for none)
        public Vector4 Spare;
    }

    private sealed record MeshDraw(int Material, int Start, int Count);

    private sealed record MeshLodGpu(Buffer Vertices, Buffer Indices, MeshDraw[] Draws);

    private sealed class MeshKindGpu
    {
        public required MeshKind3D Kind { get; init; }
        public required MeshLodGpu?[] Lods { get; init; }
        public required (ShaderResourceView? Diffuse, ShaderResourceView? Normal, ShaderResourceView? Mask, MeshMaterialConstants Constants, MeshUse Use)[] Materials { get; init; }
        public required float[] LodStarts { get; init; }
        public required float Farthest { get; init; }
        // each copy's middle and reach, for culling
        public required (Vector3 Middle, float Radius)[] Spheres { get; init; }
    }

    private readonly List<MeshKindGpu> _meshes = [];
    private VertexShader? _meshVS, _meshShadowVS;
    private PixelShader? _meshPS;
    private InputLayout? _meshLayout;
    private Buffer? _meshPlaces, _meshMaterial;
    // a mid grey for a material whose colour picture isn't to be found
    private ShaderResourceView? _meshGrey;
    private int _meshPlaceCapacity;

    /// <summary>Meshes drawn in the last frame.</summary>
    public int MeshesDrawn { get; private set; }

    /// <summary>How far meshes are drawn at most, in metres.</summary>
    public float MeshFarthest { get; set; } = 1500;

    private void StartMeshes()
    {
        var source = Shaders;
        using (var code = Compile(source, "MeshVS", "vs_4_0"))
        {
            _meshVS = new VertexShader(Device, code);
            _meshLayout = new InputLayout(Device, code, [
                new InputElement("POSITION", 0, DxgiFormat.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
                new InputElement("NORMAL", 0, DxgiFormat.R32G32B32_Float, 12, 0, InputClassification.PerVertexData, 0),
                new InputElement("TANGENT", 0, DxgiFormat.R32G32B32A32_Float, 24, 0, InputClassification.PerVertexData, 0),
                new InputElement("TEXCOORD", 0, DxgiFormat.R32G32_Float, 40, 0, InputClassification.PerVertexData, 0),
                new InputElement("WORLD", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
                new InputElement("WORLD", 1, DxgiFormat.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
                new InputElement("WORLD", 2, DxgiFormat.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
                new InputElement("WORLD", 3, DxgiFormat.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1)]);
        }
        using (var code = Compile(source, "MeshShadowVS", "vs_4_0")) _meshShadowVS = new VertexShader(Device, code);
        using (var code = Compile(source, "MeshPS", "ps_4_0")) _meshPS = new PixelShader(Device, code);
        _meshGrey = Texture(new byte[] { 128, 128, 128, 255 }, 1, 1, DxgiFormat.B8G8R8A8_UNorm, 4);
        _meshMaterial = new Buffer(Device, Utilities.SizeOf<MeshMaterialConstants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    /// <summary>The layers' meshes, replacing those set before.</summary>
    public void SetMeshes(IReadOnlyList<MeshKind3D> kinds)
    {
        EnsureDevice();
        DisposeMeshes();
        var pictures = new Dictionary<BitmapSource, ShaderResourceView>();
        ShaderResourceView? Picture(BitmapSource? picture, bool straight)
        {
            if (picture is null) return null;
            if (pictures.TryGetValue(picture, out var known)) return known;
            return pictures[picture] = straight ? StraightTexture(picture) : Texture(picture);
        }
        foreach (var kind in kinds)
        {
            var mesh = kind.Mesh;
            int lodCount = mesh.LodCount;
            var lods = new MeshLodGpu?[lodCount];
            for (int lod = 0; lod < lodCount; lod++)
            {
                var vertices = new List<MeshVertex>();
                var indices = new List<int>();
                var draws = new List<MeshDraw>();
                foreach (var chunk in mesh.Chunks)
                {
                    if (!chunk.InLod(lod) || chunk.RenderMask == 0) continue;
                    if (chunk.Material < 0 || chunk.Material >= kind.Materials.Count || kind.Materials[chunk.Material].Use == MeshUse.Hidden) continue;
                    int first = vertices.Count;
                    for (int v = 0; v < chunk.Positions.Length; v++)
                        vertices.Add(new MeshVertex
                        {
                            Position = chunk.Positions[v], Normal = v < chunk.Normals.Length ? chunk.Normals[v] : Vector3.UnitZ,
                            Tangent = v < chunk.Tangents.Length ? chunk.Tangents[v] : new Vector4(1, 0, 0, 1),
                            Uv = v < chunk.Uvs.Length ? chunk.Uvs[v] : default,
                        });
                    draws.Add(new MeshDraw(chunk.Material, indices.Count, chunk.Indices.Length));
                    foreach (int i in chunk.Indices) indices.Add(first + i);
                }
                if (vertices.Count == 0) continue;
                lods[lod] = new MeshLodGpu(Buffer.Create(Device, BindFlags.VertexBuffer, vertices.ToArray()),
                    Buffer.Create(Device, BindFlags.IndexBuffer, indices.ToArray()), draws.ToArray());
            }
            if (lods.All(l => l is null)) continue;
            var materials = kind.Materials.Select(m =>
            {
                var look = m.Look;
                var c = look?.Constants ?? W3Constants.Plain();
                bool normal = look?.Normal is not null, mask = look?.Mask is not null;
                return (Picture(m.Picture, false), normal ? Picture(look!.Normal, true) : null, mask ? Picture(look!.Mask, true) : null,
                    new MeshMaterialConstants
                    {
                        Spec = c.Spec, SpecColour = c.SpecColor,
                        Flags = new Vector4(normal ? 1 : 0, mask ? 1 : 0, c.Kind.Y, 0),
                    }, look?.Transparent == true ? MeshUse.Hidden : m.Use);
            }).ToArray();
            // the LODs start at their distances; the mesh is drawn as far as its component, or else the mesh, says
            var starts = Enumerable.Range(0, lodCount).Select(i => i < mesh.LodDistances.Count ? mesh.LodDistances[i] : float.MaxValue).ToArray();
            float farthest = kind.AutoHide > 0 ? kind.AutoHide : mesh.Mesh["autoHideDistance"] is float hide && hide > 0 ? hide
                : Math.Max(150, (mesh.LodDistances.Count > 0 ? mesh.LodDistances[^1] : 0) * 2);
            var middle = (mesh.BoundsMin + mesh.BoundsMax) / 2;
            float radius = (mesh.BoundsMax - mesh.BoundsMin).Length() / 2;
            var spheres = kind.Places.Select(p =>
            {
                float scale = MathF.Max(new Vector3(p.M11, p.M12, p.M13).Length(), MathF.Max(new Vector3(p.M21, p.M22, p.M23).Length(), new Vector3(p.M31, p.M32, p.M33).Length()));
                return (Vector3.Transform(middle, p), radius * scale);
            }).ToArray();
            _meshes.Add(new MeshKindGpu { Kind = kind, Lods = lods, Materials = materials, LodStarts = starts, Farthest = farthest, Spheres = spheres });
        }
        _sunValid = false;
        Invalidate();
    }

    /// <summary>A picture's texture with its alpha kept straight (a normal map's roughness, a mask's channels), not multiplied in.</summary>
    private ShaderResourceView StraightTexture(BitmapSource picture)
    {
        var source = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return Texture(pixels, source.PixelWidth, source.PixelHeight, DxgiFormat.B8G8R8A8_UNorm, 4);
    }

    /// <summary>Each mesh's copies in view, by LOD, nearer than they're drawn from <paramref name="from"/>.</summary>
    private List<Matrix4x4>[][] GatherMeshes(Vector3 from, float reach, Vector4[]? planes, Vector3? detailFrom = null)
    {
        var eye = detailFrom ?? from;
        var lists = new List<Matrix4x4>[_meshes.Count][];
        for (int k = 0; k < _meshes.Count; k++)
        {
            var kind = _meshes[k];
            float limit = MathF.Min(MathF.Min(kind.Farthest, MeshFarthest), reach);
            for (int i = 0; i < kind.Spheres.Length; i++)
            {
                var (middle, radius) = kind.Spheres[i];
                if (Vector3.Distance(from, middle) - radius > limit) continue;
                if (planes is not null && !InView(planes, middle - new Vector3(radius), middle + new Vector3(radius))) continue;
                float distance = MathF.Max(0, Vector3.Distance(eye, middle) - radius);
                int lod = 0;
                for (int l = 1; l < kind.LodStarts.Length; l++)
                    if (distance >= kind.LodStarts[l]) lod = l;
                // a LOD with nothing in it: the nearest finer one that has
                while (lod > 0 && kind.Lods[lod] is null) lod--;
                if (kind.Lods[lod] is null) continue;
                var slots = lists[k] ??= new List<Matrix4x4>[kind.Lods.Length];
                (slots[lod] ??= []).Add(kind.Kind.Places[i]);
            }
        }
        return lists;
    }

    /// <summary>The gathered copies into the instance buffer, one list after another; each list's first place.</summary>
    private int[][] UploadMeshPlaces(List<Matrix4x4>[][] lists)
    {
        var c = Context!;
        int total = lists.Sum(k => k?.Sum(l => l?.Count ?? 0) ?? 0);
        if (total > _meshPlaceCapacity)
        {
            _meshPlaces?.Dispose();
            _meshPlaceCapacity = Math.Max(total, _meshPlaceCapacity * 2);
            _meshPlaces = new Buffer(Device, new BufferDescription(_meshPlaceCapacity * 64, ResourceUsage.Dynamic, BindFlags.VertexBuffer,
                CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        }
        var starts = new int[lists.Length][];
        if (total == 0 || _meshPlaces is null) return starts;
        c.MapSubresource(_meshPlaces, MapMode.WriteDiscard, MapFlags.None, out var stream);
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
        c.UnmapSubresource(_meshPlaces, 0);
        return starts;
    }

    /// <summary>Draws the gathered meshes with a vertex shader and a pixel shader (null for depth only); how many copies were drawn.</summary>
    private int DrawMeshLists(List<Matrix4x4>[][] lists, VertexShader vs, PixelShader? ps, bool withMaterials)
    {
        if (_meshes.Count == 0) return 0;
        var c = Context!;
        var starts = UploadMeshPlaces(lists);
        if (_meshPlaces is null) return 0;
        int drawn = 0;
        c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        c.InputAssembler.InputLayout = _meshLayout;
        c.VertexShader.Set(vs);
        c.PixelShader.Set(ps);
        c.PixelShader.SetConstantBuffer(9, _meshMaterial);
        int lastMaterial = -1;
        MeshKindGpu? lastKind = null;
        for (int k = 0; k < lists.Length; k++)
        {
            if (lists[k] is not { } slots) continue;
            var kind = _meshes[k];
            for (int s = 0; s < slots.Length; s++)
            {
                if (slots[s] is not { Count: > 0 } list || kind.Lods[s] is not { } lod) continue;
                c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(lod.Vertices, Utilities.SizeOf<MeshVertex>(), 0),
                    new VertexBufferBinding(_meshPlaces, 64, 0));
                c.InputAssembler.SetIndexBuffer(lod.Indices, DxgiFormat.R32_UInt, 0);
                foreach (var draw in lod.Draws)
                {
                    var material = kind.Materials[draw.Material];
                    if (material.Use == MeshUse.Hidden || withMaterials && material.Use == MeshUse.ShadowOnly) continue;
                    if (withMaterials && (lastKind != kind || lastMaterial != draw.Material))
                    {
                        c.UpdateSubresource(ref material.Constants, _meshMaterial);
                        c.PixelShader.SetShaderResources(21, material.Diffuse ?? _meshGrey, material.Normal, material.Mask);
                        lastKind = kind;
                        lastMaterial = draw.Material;
                    }
                    c.DrawIndexedInstanced(draw.Count, list.Count, draw.Start, 0, starts[k][s]);
                }
                drawn += list.Count;
            }
        }
        c.PixelShader.SetShaderResources(21, null, null, null);
        return drawn;
    }

    /// <summary>The meshes in view, lit, after the ground.</summary>
    private void DrawMeshes(Vector4[] planes)
    {
        if (_meshes.Count == 0) return;
        var c = Context!;
        c.OutputMerger.SetDepthStencilState(null);
        c.OutputMerger.SetBlendState(null);
        c.Rasterizer.State = _raster;
        c.PixelShader.SetShaderResource(0, _heightMap);
        MeshesDrawn = DrawMeshLists(GatherMeshes(_eye, MeshFarthest, planes), _meshVS!, _meshPS, true);
    }

    /// <summary>The meshes in view into the depth buffer only, for the ambient occlusion.</summary>
    private void DrawMeshesDepth(Vector4[] planes)
    {
        if (_meshes.Count == 0) return;
        DrawMeshLists(GatherMeshes(_eye, 300, planes), _meshVS!, null, false);
    }

    /// <summary>The meshes round a point into the sun's shadow map, with the LODs they're seen with from the eye.</summary>
    private void DrawMeshesShadow(Vector3 centre, float reach)
    {
        if (_meshes.Count == 0) return;
        DrawMeshLists(GatherMeshes(centre, reach, null, _eye), _meshShadowVS!, null, false);
    }

    private void DisposeMeshes()
    {
        var views = new HashSet<ShaderResourceView>();
        foreach (var kind in _meshes)
        {
            foreach (var lod in kind.Lods)
            {
                lod?.Vertices.Dispose();
                lod?.Indices.Dispose();
            }
            foreach (var m in kind.Materials)
                foreach (var v in new[] { m.Diffuse, m.Normal, m.Mask })
                    if (v is not null) views.Add(v);
        }
        foreach (var v in views) v.Dispose();
        _meshes.Clear();
    }

    private void DisposeMeshDevice()
    {
        DisposeMeshes();
        foreach (var d in new IDisposable?[] { _meshVS, _meshShadowVS, _meshPS, _meshLayout, _meshPlaces, _meshMaterial, _meshGrey }) d?.Dispose();
    }
}
