using System.Numerics;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using W3ModWorkbench.Core.World;
using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFormat = SharpDX.DXGI.Format;

namespace W3ModWorkbench;

// The game's ambient comes from its environment probes: captures of the scene round a point (its albedo, normals and
// depth, relit as the light changes; REDkit's envProbeCachedUnpacked* textures), so what's round a point colours its
// ambient: the ground and the trees below and along the horizon, the sky only where nothing is in the way. Ours: the
// ground and the trees (not the grass) drawn small into the six faces of a cube round the eye, lit as the view is, then
// folded into the ambient cube with the probes' own sky wherever nothing was drawn.
public sealed partial class WorldView3D
{
    private const int ProbeSize = 64;
    // how far the probe sees the trees, and how far the eye moves before it's captured again
    private const float ProbePlantReach = 160, ProbeMove = 4;

    private Texture2D? _probeColour, _probeDepth, _probeWhite;
    private RenderTargetView[] _probeTargets = [];
    private DepthStencilView[] _probeDepths = [];
    private ShaderResourceView? _probeColourView, _probeDepthView, _probeWhiteView;
    private Buffer? _probeFaces;
    private Vector3 _probeAt = new(float.NaN);
    private float _probeHour = float.NaN;
    private int _probeAge;

    /// <summary>Whether the ambient takes what's round the eye (the ground and the trees) from a capture of the scene, as the
    /// game's probes do; off, it's the probes' sky over a ground of an average colour.</summary>
    public bool SceneProbe { get; set; } = true;

    private void EnsureProbe()
    {
        if (_probeColour is not null) return;
        _probeColour = new Texture2D(Device, new Texture2DDescription
        {
            Width = ProbeSize, Height = ProbeSize, MipLevels = 1, ArraySize = 6, Format = DxgiFormat.R16G16B16A16_Float,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _probeDepth = new Texture2D(Device, new Texture2DDescription
        {
            Width = ProbeSize, Height = ProbeSize, MipLevels = 1, ArraySize = 6, Format = DxgiFormat.R32_Typeless,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });
        _probeTargets = new RenderTargetView[6];
        _probeDepths = new DepthStencilView[6];
        for (int f = 0; f < 6; f++)
        {
            _probeTargets[f] = new RenderTargetView(Device, _probeColour, new RenderTargetViewDescription
            {
                Format = DxgiFormat.R16G16B16A16_Float, Dimension = RenderTargetViewDimension.Texture2DArray,
                Texture2DArray = new RenderTargetViewDescription.Texture2DArrayResource { FirstArraySlice = f, ArraySize = 1 },
            });
            _probeDepths[f] = new DepthStencilView(Device, _probeDepth, new DepthStencilViewDescription
            {
                Format = DxgiFormat.D32_Float, Dimension = DepthStencilViewDimension.Texture2DArray,
                Texture2DArray = new DepthStencilViewDescription.Texture2DArrayResource { FirstArraySlice = f, ArraySize = 1 },
            });
        }
        _probeColourView = new ShaderResourceView(Device, _probeColour);
        _probeDepthView = new ShaderResourceView(Device, _probeDepth, new ShaderResourceViewDescription
        {
            Format = DxgiFormat.R32_Float, Dimension = ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new ShaderResourceViewDescription.Texture2DArrayResource { MipLevels = 1, ArraySize = 6 },
        });
        // the ambient occlusion while capturing: none (the capture's pixels aren't the view's)
        _probeWhite = new Texture2D(Device, new Texture2DDescription
        {
            Width = ProbeSize, Height = ProbeSize, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        using (var white = new RenderTargetView(Device, _probeWhite)) Context!.ClearRenderTargetView(white, new RawColor4(1, 1, 1, 1));
        _probeWhiteView = new ShaderResourceView(Device, _probeWhite);
        _probeFaces = new Buffer(Device, 6 * 64 + 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        // nothing captured yet: the ambient pass takes the old ground until there is
        Context!.UpdateSubresource(new Matrix4x4[7], _probeFaces);
    }

    private static readonly (Vector3 Axis, Vector3 Up)[] ProbeAxes =
    [
        (Vector3.UnitX, Vector3.UnitZ), (-Vector3.UnitX, Vector3.UnitZ), (Vector3.UnitY, Vector3.UnitZ),
        (-Vector3.UnitY, Vector3.UnitZ), (Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY),
    ];

    /// <summary>Captures the scene round the eye into the probe's faces, when the eye has moved or the hour changed.</summary>
    private void CaptureProbe(Frame frame, WorldTerrain terrain, float spacing)
    {
        var c = Context!;
        EnsureProbe();
        _probeAge++;
        if (!SceneProbe || _cookedAlbedo is not null)
        {
            if (!float.IsNaN(_probeHour)) c.UpdateSubresource(new Matrix4x4[7], _probeFaces);
            _probeHour = float.NaN;
            return;
        }
        if (Vector3.Distance(_eye, _probeAt) < ProbeMove && MathF.Abs(_hour - _probeHour) < 0.01f && _probeAge < 90) return;
        _probeAt = _eye;
        _probeHour = _hour;
        _probeAge = 0;
        var faces = new Matrix4x4[7];
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.3f, FarPlane);
        c.PixelShader.SetShaderResource(18, _probeWhiteView);
        c.PixelShader.SetShaderResources(25, null, null);
        c.Rasterizer.SetViewport(0, 0, ProbeSize, ProbeSize);
        int count = _patchLow.GetLength(0);
        float size = PatchSamples * spacing;
        for (int f = 0; f < 6; f++)
        {
            var (axis, up) = ProbeAxes[f];
            var viewProjection = Matrix4x4.CreateLookAt(_eye, _eye + axis, up) * projection;
            Matrix4x4.Invert(viewProjection, out var unproject);
            faces[f] = viewProjection;
            var face = frame;
            face.ViewProjection = viewProjection;
            face.Unproject = unproject;
            face.Camera = new Vector4(0.3f, FarPlane, ProbeSize, ProbeSize);
            c.UpdateSubresource(ref face, _frame);
            c.ClearRenderTargetView(_probeTargets[f], new RawColor4(0, 0, 0, 0));
            c.ClearDepthStencilView(_probeDepths[f], DepthStencilClearFlags.Depth, 1, 0);
            c.OutputMerger.SetRenderTargets(_probeDepths[f], _probeTargets[f]);
            c.OutputMerger.SetDepthStencilState(_depthState);
            c.OutputMerger.SetBlendState(null);
            c.Rasterizer.State = _raster;
            var planes = Frustum(viewProjection);
            // the ground, coarse (a texel of the capture is a few metres off)
            var byStep = new List<Patch>[Steps.Length];
            for (int i = 0; i < Steps.Length; i++) byStep[i] = [];
            for (int py = 0; py < count; py++)
                for (int px = 0; px < count; px++)
                {
                    var min = new Vector3(terrain.Corner.X + px * size, terrain.Corner.Y + py * size, _patchLow[py, px] - 8);
                    var max = new Vector3(min.X + size, min.Y + size, _patchHigh[py, px]);
                    if (!InView(planes, min, max)) continue;
                    float distance = Vector3.Distance(_eye, Vector3.Clamp(_eye, min, max));
                    int step = Math.Min(Steps.Length - 1, distance < 200 ? 2 : distance < 600 ? 3 : 4);
                    byStep[step].Add(new Patch { X = min.X, Y = min.Y, Size = size, Skirt = 2 + 4 * step });
                }
            c.InputAssembler.InputLayout = _layout;
            c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            c.VertexShader.Set(_vs);
            c.PixelShader.Set(_ps);
            for (int i = 0; i < Steps.Length; i++)
                if (byStep[i].Count > 0 && _grids[i] is { } grid) DrawPatches(grid, byStep[i]);
            // the trees and bushes round it
            if (_plants.Count > 0)
            {
                c.PixelShader.SetShaderResources(0, _heightMap, _colourMap, _controlMap, _diffuse);
                DrawPlantLists(GatherPlants(_eye, ProbePlantReach, planes, grass: false), _plantVS!, _billboardVS!, _plantPS!, new Vector4[32]);
                c.PixelShader.SetShaderResources(0, _heightMap, _colourMap, _controlMap, _diffuse, _normals, _waterLevel);
            }
        }
        faces[6] = new Matrix4x4 { M11 = _eye.X, M12 = _eye.Y, M13 = _eye.Z, M14 = 1 };
        c.UpdateSubresource(faces, _probeFaces);
        RefreshAmbient();
        c.UpdateSubresource(ref frame, _frame);
        c.Rasterizer.SetViewport(0, 0, PixelWidth, PixelHeight);
        c.Rasterizer.State = _raster;
        c.OutputMerger.SetRenderTargets(DepthView, HdrView);
        c.PixelShader.SetShaderResource(18, _aoView);
    }

    private Texture2D? _cookedAlbedo, _cookedNormals, _cookedDepth;
    private ShaderResourceView? _cookedAlbedoView, _cookedNormalsView, _cookedDepthView;
    private Buffer? _cookedConstants;

    /// <summary>
    /// The world's environment probe as cooking left it (see <see cref="Core.World.EnvProbeData"/>): its four cube maps'
    /// pixels (six 128 x 128 faces each: albedo and normals as RGBA8, the sun's shadows, the depth as 16-bit floats), where
    /// it was taken, its ambient colour and its intensity; null for none, when the ambient falls back to a capture
    /// of our own.
    /// </summary>
    public void SetCookedProbe(Vector3 origin, Vector3 ambient, float intensity, byte[] albedo, byte[] normals, byte[] depth)
    {
        EnsureDevice();
        DisposeCooked();
        _cookedAlbedo = FaceArray(albedo, DxgiFormat.R8G8B8A8_UNorm, 4);
        _cookedNormals = FaceArray(normals, DxgiFormat.R8G8B8A8_UNorm, 4);
        _cookedDepth = FaceArray(depth, DxgiFormat.R16_Float, 2);
        _cookedAlbedoView = new ShaderResourceView(Device, _cookedAlbedo);
        _cookedNormalsView = new ShaderResourceView(Device, _cookedNormals);
        _cookedDepthView = new ShaderResourceView(Device, _cookedDepth);
        _cookedConstants ??= new Buffer(Device, 32, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        Context!.UpdateSubresource(new[] { new Vector4(origin, 1), new Vector4(ambient * intensity, intensity) }, _cookedConstants);
        RefreshAmbient();
        Invalidate();
    }

    /// <summary>No probe of the world's own: the ambient takes what's round the eye from our capture again.</summary>
    public void ClearCookedProbe()
    {
        DisposeCooked();
        RefreshAmbient();
        Invalidate();
    }

    private Texture2D FaceArray(byte[] pixels, DxgiFormat format, int bytes)
    {
        const int Side = 128;
        var boxes = new SharpDX.DataBox[6];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            for (int f = 0; f < 6; f++) boxes[f] = new SharpDX.DataBox(handle.AddrOfPinnedObject() + f * Side * Side * bytes, Side * bytes, Side * Side * bytes);
            return new Texture2D(Device, new Texture2DDescription
            {
                Width = Side, Height = Side, MipLevels = 1, ArraySize = 6, Format = format,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, boxes);
        }
        finally { handle.Free(); }
    }

    /// <summary>The cooked probe's textures and constants for the ambient pass (nothing bound when there's none).</summary>
    private void BindCookedProbe(DeviceContext c)
    {
        _cookedConstants ??= new Buffer(Device, 32, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        if (_cookedAlbedo is null) c.UpdateSubresource(new Vector4[2], _cookedConstants);
        c.PixelShader.SetShaderResources(27, _cookedAlbedoView, _cookedNormalsView, _cookedDepthView);
        c.PixelShader.SetConstantBuffer(12, _cookedConstants);
    }

    private void DisposeCooked()
    {
        foreach (var d in new IDisposable?[] { _cookedAlbedoView, _cookedNormalsView, _cookedDepthView, _cookedAlbedo, _cookedNormals, _cookedDepth })
            d?.Dispose();
        (_cookedAlbedo, _cookedNormals, _cookedDepth, _cookedAlbedoView, _cookedNormalsView, _cookedDepthView) = (null, null, null, null, null, null);
    }

    private Vector3 _ambientEye = new(float.NaN), _ambientLight;
    private int _ambientAge, _ambientRepeats;

    /// <summary>
    /// Whether the ambient cube is to be made again this frame: when the light has turned, the eye has moved a couple of
    /// metres or a probe changed, then for a few frames after (each folds in the last, as light bounces), and once a
    /// second anyway for the weather's changes.
    /// </summary>
    private bool AmbientDue(Vector3 toLight)
    {
        _ambientAge++;
        if (Vector3.Distance(_eye, _ambientEye) > 2 || Vector3.DistanceSquared(toLight, _ambientLight) > 1e-7f || _ambientAge >= 60)
        {
            _ambientEye = _eye;
            _ambientLight = toLight;
            _ambientRepeats = 4;
        }
        if (_ambientRepeats <= 0) return false;
        _ambientRepeats--;
        _ambientAge = 0;
        return true;
    }

    /// <summary>The ambient cube made again over the next frames (a probe or the lighting changed).</summary>
    private void RefreshAmbient() => _ambientRepeats = 4;

    private void DisposeProbe()
    {
        DisposeCooked();
        _cookedConstants?.Dispose();
        _cookedConstants = null;
        foreach (var t in _probeTargets) t.Dispose();
        foreach (var d in _probeDepths) d.Dispose();
        foreach (var d in new IDisposable?[] { _probeColourView, _probeDepthView, _probeWhiteView, _probeColour, _probeDepth, _probeWhite, _probeFaces })
            d?.Dispose();
        _probeTargets = [];
        _probeDepths = [];
        _probeColour = null;
    }
}
