using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using W3ModWorkbench.Core.World;
using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFormat = SharpDX.DXGI.Format;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace W3ModWorkbench;

// What REDkit's World Environment tool sets, here: its editor switches (what's drawn, the eye's adaptation, the sun's
// path), the weather system (a condition blending in over its blend time, stabilised at once, or chosen by the
// weather table's odds when not paused), and the fake day cycle (time held where the slider is, or running at the
// game's pace).
public sealed partial class WorldView3D
{
    /// <summary>The eye adapts at once (REDkit's Instant adaptation), else over a moment as the game's does.</summary>
    public bool InstantAdaptation { get; set; }

    /// <summary>The sun's and the moon's paths across the sky drawn as dots, one an half hour (REDkit's Trajectory display).</summary>
    public bool ShowTrajectory { get; set; }

    /// <summary>The environment probes follow the light (REDkit's Allow envProbe update); off, the reflected sky stays as it was.</summary>
    public bool AllowEnvProbeUpdate { get; set; } = true;

    /// <summary>REDkit's EnvProbe instant update: the probes are taken at once rather than over frames. Ours are always at once.</summary>
    public bool EnvProbeInstantUpdate { get; set; }

    public bool AllowGlobalFog { get; set; } = true;
    public bool AllowColorBalance { get; set; } = true;
    public bool AllowVignette { get; set; } = true;
    public bool AllowWaterShader { get; set; } = true;

    // switches REDkit has for effects this view doesn't draw yet: kept so the World Environment window shows them
    public bool AllowBloom { get; set; } = true;
    public bool AllowAntialiasing { get; set; } = true;
    public bool AllowDepthOfField { get; set; } = true;
    public bool AllowSsao { get; set; } = true;
    public bool AllowCloudsShadow { get; set; } = true;
    public bool ForceCutsceneDepthOfField { get; set; }

    /// <summary>The time is held where it's set (REDkit's Fake daycycle); off, it runs at the game's pace.</summary>
    public bool FakeDayCycle { get; set; } = true;

    /// <summary>
    /// Game hours a real second when the day runs at the game's pace: a whole day in 96 minutes, The Witcher 3's clock
    /// (15 game minutes a real minute).
    /// </summary>
    private const float GameHoursPerSecond = 24f / (96 * 60);

    /// <summary>The weather system holds its condition (REDkit's Pause); unpaused, the next comes by the weather table's odds.</summary>
    public bool WeatherPaused { get; set; } = true;

    /// <summary>Draws again with the switches as they are now.</summary>
    public void Redraw() => Invalidate();

    /// <summary>The lighting now: the world's environments, the weather and its change, and any forced preset.</summary>
    public WorldLighting? Lighting => _lighting;

    /// <summary>Raised when the lighting changes on its own: a weather change starting, going on or finishing.</summary>
    public event EventHandler? LightingChanged;

    private float _weatherSeconds, _sinceWeather;
    private readonly Random _weatherOdds = new();

    /// <summary>Blends towards a weather condition over its blend time, or at once.</summary>
    public void ChangeWeather(string? name, bool instantly)
    {
        if (_lighting is not { } lighting) return;
        if (instantly || name == lighting.Weather)
        {
            _lighting = lighting.WithWeather(name);
            _weatherSeconds = 0;
        }
        else
        {
            // a change already going on starts from where it's got to
            if (lighting.PreviousWeather is not null) lighting = lighting.WithTransition(1);
            _lighting = lighting.TowardsWeather(name, 0);
            _weatherSeconds = Math.Max(0.01f, lighting.Weathers.FirstOrDefault(w => w.Name == name)?.BlendTime ?? 120);
        }
        _sinceWeather = 0;
        _shadowMade = false;
        LightingChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>Finishes a weather change at once (REDkit's Stabilize).</summary>
    public void StabiliseWeather()
    {
        if (_lighting is not { PreviousWeather: not null } lighting) return;
        _lighting = lighting.WithTransition(1);
        LightingChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>Forces an environment over everything (REDkit's Force preset), or none.</summary>
    public void ForcePreset(string? path)
    {
        if (_lighting is null) return;
        _lighting = _lighting.WithPreset(path);
        LightingChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>Uses each environment's outdoor, interior or cave parameters (REDkit's Force Interior and Force Cave Parameters).</summary>
    public void UseParameters(EnvironmentParameters parameters)
    {
        if (_lighting is null) return;
        _lighting = _lighting.WithParameters(parameters);
        LightingChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>The weather and the day moving on by <paramref name="seconds"/>; true when anything changed.</summary>
    private bool AdvanceEnvironment(float seconds)
    {
        bool changed = false;
        if (!FakeDayCycle)
        {
            Hour = _hour + seconds * GameHoursPerSecond;
            HourChanged?.Invoke(this, _hour);
            changed = true;
        }
        if (_lighting is not { } lighting) return changed;
        if (lighting.PreviousWeather is not null && _weatherSeconds > 0)
        {
            float t = lighting.Transition + seconds / _weatherSeconds;
            _lighting = lighting.WithTransition(t);
            LightingChanged?.Invoke(this, EventArgs.Empty);
            changed = true;
        }
        else if (!WeatherPaused && lighting.Weathers.Count > 0)
        {
            // the condition lasts its occurrence time, then the next is drawn by the table's odds
            _sinceWeather += seconds;
            float lasts = lighting.Weathers.FirstOrDefault(w => w.Name == lighting.Weather)?.OccurrenceTime ?? 600;
            if (_sinceWeather >= lasts)
            {
                float total = lighting.Weathers.Sum(w => w.Probability), pick = (float)_weatherOdds.NextDouble() * total;
                var next = lighting.Weathers.FirstOrDefault(w => (pick -= w.Probability) < 0) ?? lighting.Weathers[^1];
                ChangeWeather(next.Name, false);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>The sun's (w 1) and the moon's (w 2) directions every half hour, for the trajectory display.</summary>
    private Vector4[] Trajectory()
    {
        var points = new Vector4[96];
        if (!ShowTrajectory) return points;
        var lighting = _lighting ?? Fallback;
        for (int i = 0; i < 48; i++)
        {
            var sky = lighting.Sky(i * 0.5f);
            points[i] = new Vector4(sky.SunDirection, 1);
            points[48 + i] = new Vector4(sky.MoonDirection, 2);
        }
        return points;
    }

    // the eye's adapted luminance, kept from frame to frame in two one-pixel textures written in turn
    private Texture2D?[] _adapted = new Texture2D?[2];
    private RenderTargetView?[] _adaptedTargets = new RenderTargetView?[2];
    private ShaderResourceView?[] _adaptedViews = new ShaderResourceView?[2];
    private int _adaptedNow;
    private PixelShader? _adaptPS;
    private Buffer? _trajectory;

    private void StartEnvironment()
    {
        for (int i = 0; i < 2; i++)
        {
            _adapted[i] = new Texture2D(Device, new Texture2DDescription
            {
                Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R32_Float,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _adaptedTargets[i] = new RenderTargetView(Device, _adapted[i]);
            _adaptedViews[i] = new ShaderResourceView(Device, _adapted[i]);
        }
        using (var code = Compile(Shaders, "AdaptPS", "ps_4_0")) _adaptPS = new PixelShader(Device, code);
        _trajectory = new Buffer(Device, 96 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    /// <summary>The trajectory dots for the sky, set each frame.</summary>
    private void SetTrajectory()
    {
        Context!.UpdateSubresource(Trajectory(), _trajectory);
        Context.PixelShader.SetConstantBuffer(5, _trajectory);
    }

    /// <summary>
    /// The eye's adapted luminance moved towards the scene's average: at once with <see cref="InstantAdaptation"/>,
    /// else by a share each frame as time goes; the post pass reads the result.
    /// </summary>
    private ShaderResourceView? Adapt(float seconds, bool first)
    {
        var c = Context!;
        int previous = _adaptedNow, next = 1 - _adaptedNow;
        _adaptedNow = next;
        float rate = InstantAdaptation || first ? 1 : 1 - MathF.Exp(-seconds * AdaptationSpeed);
        c.OutputMerger.SetRenderTargets((DepthStencilView?)null, _adaptedTargets[next]);
        c.Rasterizer.SetViewport(0, 0, 1, 1);
        c.PixelShader.SetShaderResources(10, HdrTexture);
        c.PixelShader.SetShaderResource(16, _adaptedViews[previous]);
        _adaptRate[0] = new Vector4(rate, 0, 0, 0);
        c.UpdateSubresource(_adaptRate, _adaptBuffer);
        c.PixelShader.SetConstantBuffer(6, _adaptBuffer);
        c.VertexShader.Set(_skyVS);
        c.PixelShader.Set(_adaptPS);
        c.Draw(3, 0);
        c.PixelShader.SetShaderResource(16, null);
        c.Rasterizer.SetViewport(0, 0, PixelWidth, PixelHeight);
        c.OutputMerger.SetRenderTargets((DepthStencilView?)null, TargetView);
        return _adaptedViews[next];
    }

    /// <summary>How fast the eye adapts when not at once, a share of the gap a second (by eye: the game's rate isn't decoded).</summary>
    public float AdaptationSpeed { get; set; } = 1.5f;

    private readonly Vector4[] _adaptRate = new Vector4[1];
    private Buffer? _adaptBuffer;
    private bool _adaptedOnce;

    private void DisposeEnvironment()
    {
        foreach (var d in new IDisposable?[] { _adaptPS, _trajectory, _adaptBuffer }.Concat(_adapted).Concat(_adaptedTargets).Concat(_adaptedViews))
            d?.Dispose();
    }
}
