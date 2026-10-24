using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Render;
using HelixToolkit.SharpDX.Shaders;
using HelixToolkit.SharpDX.Utilities;
using SharpDX.D3DCompiler;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>How a surface is lit, from the shader its material uses in the game (the shader's own numbering).</summary>
public enum SurfaceKind { Plain = 0, Skin = 1, Hair = 2, Eye = 3, Glass = 4 }

/// <summary>
/// What the view's shader (<see cref="W3Shading"/>) needs of a material beside its colour texture: its kind, its other
/// textures, and its numbers as the game's material graph reads them. Made by <see cref="LookTextures"/>.
/// </summary>
public sealed class MaterialLook
{
    public SurfaceKind Kind { get; init; }

    /// <summary>The normal map, with roughness in its alpha.</summary>
    public BitmapSource? Normal { get; init; }

    /// <summary>The tint mask (red baked ambient occlusion, green the dye's mask, blue where detail goes), or the skin's packed map.</summary>
    public BitmapSource? Mask { get; init; }

    public BitmapSource? Detail1 { get; init; }
    public BitmapSource? Detail2 { get; init; }

    /// <summary>Hair's strand shift texture, or pbr_spec's specular texture.</summary>
    public BitmapSource? Shift { get; init; }

    /// <summary>What glows: an emissive texture, or a mask of where a colour glows.</summary>
    public BitmapSource? Emissive { get; init; }

    /// <summary>An eye's highlight cube map (its BlickCube).</summary>
    public TextureModel? Blick { get; init; }

    /// <summary>The shader's numbers (cbW3), less the view's own (lighting, grading), which the view fills in as it draws.</summary>
    public W3Constants Constants;

    /// <summary>Whether the surface is drawn see-through in the transparent pass (glass).</summary>
    public bool Transparent => Kind == SurfaceKind.Glass;

    /// <summary>A look with only a normal map, for a picture shown as it is (a texture picked to look at in the mesh viewer).</summary>
    public static MaterialLook Plain(BitmapSource? normal) => new() { Normal = normal, Constants = W3Constants.Plain(normal is not null) };
}

/// <summary>The shader's cbW3, as psW3Mesh.hlsl lays it out: each a float4.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct W3Constants
{
    public Vector4 Kind;       // x kind, y alpha cut-off, z light wrap, w warmth
    public Vector4 Spec;       // x RSpecScale, y RSpecBase, z Roughness_min (skin: RoughnessBias), w Roughness_max
    public Vector4 SpecColor;  // rgb SpecularColor (linear), w AOPower
    public Vector4 Detail;     // xy DetailTile, z DetailPower, w DetailRange
    public Vector4 Detail2;    // xy DetailTile2, z DetailAlbedoPower, w detail mode
    public Vector4 Emissive;   // rgb emissive, w opacity
    public Vector4 Flags;      // x has normal map, y has mask, z has strand shift, w strand shift scale
    public Vector4 Eye;        // x specularity, y roughness, z specularity of the corner flesh, w highlight strength
    public Vector4 Env;        // x has environment, y reflection strength, z light all round, w environment mips
    public Vector4 Grade;      // x exposure, y saturation, z contrast, w SSAO strength
    public Vector4 Misc;       // x roughness without a normal map, y translucency, z soft, w has specular texture
    public Vector4 Variance;   // rgb VarianceColor, w VarianceOffset
    public Vector4 Light;      // rgb light all round without an environment, w how much of the environment's colour is kept
    public Vector4 Glow;       // x emissive mode (0 none, 1 colour times the emissive texture, 2 a colour times a mask), y times the vertex colour

    public static int Size => Marshal.SizeOf<W3Constants>();

    /// <summary>A plain surface: a little grey shine, rough, nothing else.</summary>
    public static W3Constants Plain(bool hasNormal = false) => new()
    {
        Spec = new(1, 0, 0, 1),
        SpecColor = new(0.04f, 0.04f, 0.04f, 0),
        Detail = new(1, 1, 0, 5),
        Flags = new(hasNormal ? 1 : 0, 0, 0, 0),
        Misc = new(0.7f, 0, 0, 0),
        Variance = new(1, 1, 1, 1),
    };
}

/// <summary>
/// The light the views are seen in, for every view: the environment's cube map (reflections and the light all round),
/// how bright, and the picture's grade. Read once, from the game's own cube maps.
/// </summary>
public static class W3Environment
{
    /// <summary>The cube map the default environment is lit by: Toussaint's forest, a daylight probe with sky above and ground below.</summary>
    public const string DefaultCube = @"dlc\bob\data\fx\textures\cubemap\bob_forest_cube.w2cube";

    public static TextureModel? Cube { get; private set; }
    public static int CubeMips { get; private set; } = 1;
    public static float Exposure { get; set; } = 1.15f;
    public static float Reflections { get; set; } = 0.9f;
    public static float AllRound { get; set; } = 0.55f;
    public static float Saturation { get; set; } = 0.92f;
    public static float Contrast { get; set; } = 1.05f;
    public static float Occlusion { get; set; } = 0.8f;

    /// <summary>How much of the environment's own colour its light keeps (0 grey, 1 all of it).</summary>
    public static float Colour { get; set; } = 0.35f;

    /// <summary>Raised when the environment changes (its cube map has been read), for the views to draw again.</summary>
    public static event EventHandler? Changed;

    private static int _loading;

    /// <summary>Reads the default environment's cube map in the background, the first time a view asks.</summary>
    public static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _loading, 1) != 0) return;
        Task.Run(() =>
        {
            if (GameLocator.Resolve(null) is not { } game) return;
            var locator = new ResourceLocator([], null, game);
            var cube = GpuTextures.Cube(locator, DefaultCube);
            if (cube is null) return;
            CubeMips = GpuTextures.CubeMips(locator, DefaultCube);
            Cube = cube;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(null, EventArgs.Empty));
        });
    }
}

/// <summary>
/// The view's own pixel shader (<c>Rendering/Shaders/psW3Mesh.hlsl</c>): the game's materials lit in linear light, GGX
/// for the shine, image-based light from the environment, tone mapped. Compiled once when the first view is made and
/// added to Helix's mesh technique as two passes of its own (solid, and the transparent pass for hair's soft ends and
/// glass), which <see cref="W3MaterialCore"/> draws with.
/// </summary>
public static class W3Shading
{
    public const string PassName = "W3Mesh";
    public const string TransparentPassName = "W3MeshOIT";

    /// <summary>What's said about the shader if it couldn't be compiled or added, in which case the view draws as Helix does.</summary>
    public static string? Problem { get; private set; }

    /// <summary>Adds the passes to the mesh technique of a set of shaders, once.</summary>
    public static void Install(IEffectsManager effects)
    {
        try
        {
            var mesh = effects[DefaultRenderTechniqueNames.Mesh];
            if (mesh is not null && !mesh.ShaderPassNames.Contains(PassName))
            {
                AddPass(mesh, DefaultPassNames.Default, PassName, "main");
                AddPass(mesh, DefaultPassNames.OITPass, TransparentPassName, "mainOIT");
            }
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
        InstallParticles(effects);
    }

    /// <summary>What's said about the particle shader if it couldn't be compiled or added, in which case effects aren't drawn.</summary>
    public static string? ParticleProblem { get; private set; }

    // the techniques whose billboards are already the particles'
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IRenderTechnique, object> ParticlesIn = [];

    /// <summary>
    /// Makes Helix's instanced billboards the view's particles (nothing else in the app draws instanced billboards): our
    /// own shaders (see W3Particle.hlsl), blended as premultiplied alpha, tested against the depth but not writing it,
    /// and drawn from both sides.
    /// </summary>
    private static void InstallParticles(IEffectsManager effects)
    {
        try
        {
            var billboards = effects[DefaultRenderTechniqueNames.BillboardInstancing];
            if (billboards is null || ParticlesIn.TryGetValue(billboards, out _)) return;
            var standard = billboards.Description.PassDescriptions?.FirstOrDefault(p => p.Name == DefaultPassNames.Default)
                ?? throw new InvalidOperationException(L.T("W3Shading.HelixsMeshTechniqueHas", DefaultPassNames.Default));
            var blend = new SharpDX.Direct3D11.BlendStateDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blend.RenderTarget[0] = new SharpDX.Direct3D11.RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = SharpDX.Direct3D11.BlendOption.One,
                DestinationBlend = SharpDX.Direct3D11.BlendOption.InverseSourceAlpha,
                BlendOperation = SharpDX.Direct3D11.BlendOperation.Add,
                SourceAlphaBlend = SharpDX.Direct3D11.BlendOption.One,
                DestinationAlphaBlend = SharpDX.Direct3D11.BlendOption.InverseSourceAlpha,
                AlphaBlendOperation = SharpDX.Direct3D11.BlendOperation.Add,
                RenderTargetWriteMask = SharpDX.Direct3D11.ColorWriteMaskFlags.All,
            };
            var depth = new SharpDX.Direct3D11.DepthStencilStateDescription
            {
                IsDepthEnabled = true,
                DepthWriteMask = SharpDX.Direct3D11.DepthWriteMask.Zero,
                DepthComparison = SharpDX.Direct3D11.Comparison.LessEqual,
                IsStencilEnabled = false,
            };
            var raster = new SharpDX.Direct3D11.RasterizerStateDescription
            {
                FillMode = SharpDX.Direct3D11.FillMode.Solid,
                CullMode = SharpDX.Direct3D11.CullMode.None,
                IsDepthClipEnabled = true,
            };
            var pass = new ShaderPassDescription(DefaultPassNames.Default)
            {
                ShaderList =
                [
                    new HelixToolkit.SharpDX.Shaders.ShaderDescription("vsW3Particle", ShaderStage.Vertex, new ShaderReflector(), CompileParticle("vsParticle", "vs_5_0")),
                    new HelixToolkit.SharpDX.Shaders.ShaderDescription("gsW3Particle", ShaderStage.Geometry, new ShaderReflector(), CompileParticle("gsParticle", "gs_5_0")),
                    new HelixToolkit.SharpDX.Shaders.ShaderDescription("psW3Particle", ShaderStage.Pixel, new ShaderReflector(), CompileParticle("psParticle", "ps_5_0")),
                ],
                BlendStateDescription = blend,
                DepthStencilStateDescription = depth,
                RasterStateDescription = raster,
                Topology = standard.Topology,
                InputLayoutDescription = standard.InputLayoutDescription,
            };
            billboards.RemovePass(DefaultPassNames.Default);
            billboards.AddPass(pass);
            ParticlesIn.AddOrUpdate(billboards, true);

            // mesh particles: Helix's instanced meshes with the particle mesh pixel shader, blended and tested the same way
            var meshes = effects[DefaultRenderTechniqueNames.InstancingMesh];
            if (meshes is not null && !meshes.ShaderPassNames.Contains(ParticleMeshPassName))
            {
                var like = meshes.Description.PassDescriptions?.FirstOrDefault(p => p.Name == DefaultPassNames.Default)
                    ?? throw new InvalidOperationException(L.T("W3Shading.HelixsMeshTechniqueHas", DefaultPassNames.Default));
                var pixel = new HelixToolkit.SharpDX.Shaders.ShaderDescription("psW3ParticleMesh", ShaderStage.Pixel, new ShaderReflector(),
                    CompileFile("W3ParticleMesh.hlsl", "main", "ps_5_0"));
                meshes.AddPass(new ShaderPassDescription(ParticleMeshPassName)
                {
                    ShaderList = [.. (like.ShaderList ?? []).Select(s => s.ShaderType == ShaderStage.Pixel ? pixel : s)],
                    BlendStateDescription = blend,
                    DepthStencilStateDescription = depth,
                    RasterStateDescription = raster,
                    Topology = like.Topology,
                    InputLayoutDescription = like.InputLayoutDescription,
                });
            }
            ParticleProblem = null;
        }
        catch (Exception ex)
        {
            ParticleProblem = ex.Message;
        }
    }

    /// <summary>The pass mesh particles are drawn with, in Helix's instanced mesh technique (see <see cref="ParticleMeshMaterialCore"/>).</summary>
    public const string ParticleMeshPassName = "W3ParticleMesh";

    private static byte[] CompileParticle(string entry, string profile) => CompileFile("W3Particle.hlsl", entry, profile);

    private static byte[] CompileFile(string file, string entry, string profile)
    {
        using var result = ShaderBytecode.Compile(Read(file), entry, profile, ShaderFlags.OptimizationLevel3, EffectFlags.None,
            null, new Includes(), file);
        if (result.HasErrors || result.Bytecode is null) throw new InvalidOperationException(L.T("W3Shading.ViewsShaderDidntCompile", result.Message));
        return result.Bytecode.Data;
    }

    /// <summary>A pass like one of Helix's, with the view's pixel shader in place of its own.</summary>
    private static void AddPass(IRenderTechnique technique, string like, string name, string entry)
    {
        var standard = technique.Description.PassDescriptions?.FirstOrDefault(p => p.Name == like)
            ?? throw new InvalidOperationException(L.T("W3Shading.HelixsMeshTechniqueHas", like));
        var pixel = new HelixToolkit.SharpDX.Shaders.ShaderDescription("psW3Mesh_" + entry, ShaderStage.Pixel, new ShaderReflector(), Compile(entry));
        technique.AddPass(new ShaderPassDescription(name)
        {
            ShaderList = [.. (standard.ShaderList ?? []).Select(s => s.ShaderType == ShaderStage.Pixel ? pixel : s)],
            BlendStateDescription = standard.BlendStateDescription,
            DepthStencilStateDescription = standard.DepthStencilStateDescription,
            RasterStateDescription = standard.RasterStateDescription,
            Topology = standard.Topology,
            InputLayoutDescription = standard.InputLayoutDescription,
            BlendFactor = standard.BlendFactor,
            SampleMask = standard.SampleMask,
            StencilRef = standard.StencilRef,
        });
    }

    /// <summary>Whether the passes are there to draw with.</summary>
    public static bool Installed(IRenderTechnique technique) => Problem is null && technique.ShaderPassNames.Contains(PassName);

    private static byte[] Compile(string entry)
    {
        using var result = ShaderBytecode.Compile(Read("psW3Mesh.hlsl"), entry, "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None,
            null, new Includes(), "psW3Mesh.hlsl");
        if (result.HasErrors || result.Bytecode is null) throw new InvalidOperationException(L.T("W3Shading.ViewsShaderDidntCompile", result.Message));
        return result.Bytecode.Data;
    }

    private static string Read(string name)
    {
        using var stream = typeof(W3Shading).Assembly.GetManifestResourceStream("W3ModWorkbench.Shaders." + name)
            ?? throw new FileNotFoundException(L.T("W3Shading.ViewsShaderFileIsnt", name));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The shader's includes, from the program's own copies, by file name whatever folder they're named in.</summary>
    private sealed class Includes : SharpDX.CallbackBase, Include
    {
        public Stream Open(IncludeType type, string fileName, Stream parentStream) =>
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Read(Path.GetFileName(fileName.Replace('\\', '/')))));

        public void Close(Stream stream) => stream.Dispose();
    }
}

/// <summary>A mesh particle's material: its texture, drawn by the view's particle mesh pass (see W3ParticleMesh.hlsl).</summary>
public sealed class ParticleMeshMaterialCore : PhongMaterialCore
{
    public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique) =>
        new PhongMaterialVariables(manager, technique, this, W3Shading.ParticleMeshPassName);
}

/// <summary>A Phong material drawn with the view's own passes, with what the game's material says beside its textures.</summary>
public sealed class W3MaterialCore : PhongMaterialCore
{
    /// <summary>The material as the game has it; null for a plain grey surface.</summary>
    public MaterialLook? Look { get; init; }

    /// <summary>Hair's second pass: blended by its alpha, not tested.</summary>
    public bool Soft { get; init; }

    public override MaterialVariable CreateMaterialVariables(IEffectsManager manager, IRenderTechnique technique) =>
        W3Shading.Installed(technique)
            ? new W3MaterialVariables(manager, technique, this)
            : new PhongMaterialVariables(manager, technique, this, DefaultPassNames.Default);
}

/// <summary>
/// Binds what the view's shader needs beside Helix's own: its constant buffer (cbW3, b10) and its textures (t40 on),
/// after Helix has bound the pass and the material's colour texture, just before each draw.
/// </summary>
public sealed class W3MaterialVariables : PhongMaterialVariables
{
    private readonly W3MaterialCore _core;
    private readonly IRenderTechnique _technique;
    private readonly SharpDX.Direct3D11.Buffer _constants;
    private readonly List<ShaderResourceViewProxy> _views = [];
    private readonly SharpDX.Direct3D11.ShaderResourceView?[] _textures = new SharpDX.Direct3D11.ShaderResourceView?[8];
    private readonly SamplerStateProxy _wrap, _clamp;
    private ShaderResourceViewProxy? _environment;
    private TextureModel? _environmentModel;

    public W3MaterialVariables(IEffectsManager manager, IRenderTechnique technique, W3MaterialCore core)
        : base(manager, technique, core, W3Shading.PassName)
    {
        _core = core;
        _technique = technique;
        var effects = (EffectsManager)manager;
        var device = effects.Device ?? throw new InvalidOperationException(L.T("W3Shading.ViewHasNoGraphics"));
        _constants = new SharpDX.Direct3D11.Buffer(device, W3Constants.Size, SharpDX.Direct3D11.ResourceUsage.Default,
            SharpDX.Direct3D11.BindFlags.ConstantBuffer, SharpDX.Direct3D11.CpuAccessFlags.None, SharpDX.Direct3D11.ResourceOptionFlags.None, 0);
        _wrap = effects.StateManager!.Register(DefaultSamplers.LinearSamplerWrapAni4) ?? throw new InvalidOperationException(L.T("W3Shading.NoSamplerViewsTextures"));
        _clamp = effects.StateManager!.Register(DefaultSamplers.IBLSampler) ?? throw new InvalidOperationException(L.T("W3Shading.NoSamplerViewsCube"));
        if (core.Look is { } look)
        {
            _textures[0] = View(effects, look.Normal is null ? null : GpuTextures.Of(look.Normal));
            _textures[1] = View(effects, look.Mask is null ? null : GpuTextures.Of(look.Mask));
            _textures[2] = View(effects, look.Detail1 is null ? null : GpuTextures.Of(look.Detail1));
            _textures[3] = View(effects, look.Detail2 is null ? null : GpuTextures.Of(look.Detail2));
            _textures[4] = View(effects, look.Shift is null ? null : GpuTextures.Of(look.Shift));
            _textures[6] = View(effects, look.Blick);
            _textures[7] = View(effects, look.Emissive is null ? null : GpuTextures.Of(look.Emissive));
        }
    }

    private SharpDX.Direct3D11.ShaderResourceView? View(EffectsManager effects, TextureModel? texture)
    {
        if (texture is null) return null;
        var view = effects.MaterialTextureManager?.Register(texture);
        if (view is null) return null;
        _views.Add(view);
        return view.TextureView;
    }

    public override ShaderPass GetPass(RenderType renderType, RenderContext context) =>
        renderType == RenderType.Transparent && context.OITRenderStage == OITRenderStage.SinglePassWeighted
            ? _technique[W3Shading.TransparentPassName] ?? base.GetPass(renderType, context)
            : base.GetPass(renderType, context);

    public override bool BindMaterialResources(RenderContext context, DeviceContextProxy deviceContext, ShaderPass shaderPass)
    {
        if (!base.BindMaterialResources(context, deviceContext, shaderPass)) return false;
        if ((SharpDX.Direct3D11.DeviceContext1?)deviceContext is not { } dc) return true;
        var values = _core.Look?.Constants ?? W3Constants.Plain();
        if (_core.Soft) values.Kind.Y = 0;
        values.Misc.Z = _core.Soft ? 1 : 0;
        // the environment, the same for every material
        if (!ReferenceEquals(_environmentModel, W3Environment.Cube) && W3Environment.Cube is { } cube)
        {
            _environmentModel = cube;
            _environment = (EffectsManager as EffectsManager)?.MaterialTextureManager?.Register(cube);
            _textures[5] = _environment?.TextureView;
        }
        values.Env = new(_textures[5] is null ? 0 : 1, W3Environment.Reflections, W3Environment.AllRound, W3Environment.CubeMips);
        values.Grade = new(W3Environment.Exposure, W3Environment.Saturation, W3Environment.Contrast, W3Environment.Occlusion);
        values.Light = new(0.32f, 0.33f, 0.36f, W3Environment.Colour);
        dc.UpdateSubresource(ref values, _constants);
        dc.PixelShader.SetConstantBuffer(10, _constants);
        for (int i = 0; i < _textures.Length; i++) dc.PixelShader.SetShaderResource(40 + i, _textures[i]);
        dc.PixelShader.SetSampler(10, _wrap.State);
        dc.PixelShader.SetSampler(11, _clamp.State);
        return true;
    }

    protected override void OnDispose(bool disposeManagedResources)
    {
        foreach (var view in _views) view.Dispose();
        _environment?.Dispose();
        _wrap.Dispose();
        _clamp.Dispose();
        _constants.Dispose();
        base.OnDispose(disposeManagedResources);
    }
}
