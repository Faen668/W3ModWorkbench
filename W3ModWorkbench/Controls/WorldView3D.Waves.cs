using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFormat = SharpDX.DXGI.Format;
using Vector4 = System.Numerics.Vector4;

namespace W3ModWorkbench;

// The sea's waves, as the game's renderer simulates them (renderSimWaterFFT, its fft.fx compute shaders, read from the
// static shader cache and editor.exe): a 512 x 512 field of noise, each texel's wave animated through a Phillips spectrum
// of the wind (PHILLIPS_ANIMATION), turned into heights by a 2D FFT (ORDERING and BUTTERFLY), then the slopes, a crest
// measure and the height packed for the water shader (FINALIZE). The water shader samples it at three sizes.
public sealed partial class WorldView3D
{
    private const int WaveSize = 512;

    // loc-ignore-start (HLSL)
    private const string WaveShaders = @"
cbuffer WaveConstants : register(b0)
{
    float4 Phillips0; // towards the wind (sin, cos of its angle), L squared (L = V^2 / g), the amplitude
    float4 Phillips1; // the second way the waves run (wind + 0.4, (1 - wind) x 0.4), the time
    float4 Finalize;  // 1 / 512, how far the crest measure looks (floor(8 x wind + 1) texels)
    float4 FftPass;   // 1 while the FFT runs down the columns
};

Texture2D<float2> WaveNoise : register(t0);
Texture2D<float2> WaveIn : register(t1);
RWTexture2D<float2> WaveField : register(u0);
RWTexture2D<float4> WaveOut : register(u1);

// PHILLIPS_ANIMATION (static shader 0879): the texel's wave vector k = (f - 0.5) x pi, its noise, the Phillips
// spectrum's amplitude A exp(-1 / (k^2 L^2)) / k^4 (k.wind)^2, held back downwind by 1 - |k|, its square root times |k|
// times the noise; animated by the game's own phase (not a dispersion relation), the complex product of the two
[numthreads(16, 16, 1)]
void PhillipsCS(uint3 id : SV_DispatchThreadID)
{
    float2 shifted = (id.xy + 0.5) / 512;
    float2 f = frac(shifted + 0.5);
    float2 noise = WaveNoise.Load(int3(f * 512, 0));
    float2 k = (f - 0.5) * 3.14159274;
    float k2 = dot(k, k), kl = sqrt(k2);
    float2 kn = k / kl;
    float along = dot(kn, Phillips0.xy);
    float p = along * along * exp2(-1.44269502 / (k2 * Phillips0.z)) * Phillips0.w / (k2 * k2);
    float downwind = 1 - (along < 0 ? 0 : 1) * kl;
    float2 h0 = sqrt(p) * kl * downwind * noise;
    float t = Phillips1.z;
    float2 way = normalize(Phillips1.xy);
    float r = sqrt(dot(shifted - 0.5, shifted - 0.5));
    float spin = t * 5 * (1 - r);
    float size = sqrt(dot(h0, h0)) * 1e-4 + 1e-6;
    float2 c = float2(way.y * cos(t * (f.x * 2 - 1)) + sin(spin) * way.x, sin(t * (f.y * 2 - 1)) + cos(spin) * way.x) * size;
    WaveField[id.xy] = float2(c.x * h0.x - c.y * h0.y, c.x * h0.y + c.y * h0.x);
}

// ORDERING and BUTTERFLY (static shader 0083): a 512-point FFT of each row (or column), radix 2, the twiddles e^(-2 pi i)
groupshared float2 WaveLine[512];

[numthreads(256, 1, 1)]
void FftCS(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint row = group.x;
    bool columns = FftPass.x > 0.5;
    [unroll] for (uint n = 0; n < 2; n++)
    {
        uint k = thread.x + n * 256;
        uint from = reversebits(k) >> 23;
        WaveLine[k] = columns ? WaveIn.Load(int3(row, from, 0)) : WaveIn.Load(int3(from, row, 0));
    }
    GroupMemoryBarrierWithGroupSync();
    [loop] for (uint half = 1; half < 512; half <<= 1)
    {
        uint i = thread.x, pos = i % half;
        uint a = (i / half) * half * 2 + pos, b = a + half;
        float angle = -6.28318548 * pos / (half * 2);
        float2 w = float2(cos(angle), sin(angle));
        float2 x = WaveLine[a], y = WaveLine[b];
        float2 wy = float2(w.x * y.x - w.y * y.y, w.x * y.y + w.y * y.x);
        GroupMemoryBarrierWithGroupSync();
        WaveLine[a] = x + wy;
        WaveLine[b] = x - wy;
        GroupMemoryBarrierWithGroupSync();
    }
    [unroll] for (uint m = 0; m < 2; m++)
    {
        uint k = thread.x + m * 256;
        if (columns) WaveField[uint2(row, k)] = WaveLine[k]; else WaveField[uint2(k, row)] = WaveLine[k];
    }
}

// FINALIZE (static shader 0677): the height (the field's real part times 1/512), its slopes across the neighbours
// either side, and a crest measure: how far the height stands over the four texels k away, times 3000, plus 1
[numthreads(16, 16, 1)]
void FinalizeCS(uint3 id : SV_DispatchThreadID)
{
    int2 p = id.xy;
    float s = Finalize.x, k = Finalize.y;
    float h = WaveIn.Load(int3(p, 0)).x * s;
    float xp = WaveIn.Load(int3((p.x + 1) & 511, p.y, 0)).x, xm = WaveIn.Load(int3((p.x + 511) & 511, p.y, 0)).x;
    float yp = WaveIn.Load(int3(p.x, (p.y + 1) & 511, 0)).x, ym = WaveIn.Load(int3(p.x, (p.y + 511) & 511, 0)).x;
    int kk = (int)k;
    float around = WaveIn.Load(int3((p.x + kk) & 511, p.y, 0)).x + WaveIn.Load(int3((p.x - kk + 512) & 511, p.y, 0)).x
        + WaveIn.Load(int3(p.x, (p.y + kk) & 511, 0)).x + WaveIn.Load(int3(p.x, (p.y - kk + 512) & 511, 0)).x;
    WaveOut[p] = float4((xm - xp) * s, (ym - yp) * s, (h - s * 0.25 * around) * 3000 + 1, h);
}
";
    // loc-ignore-end

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveConstants
    {
        public Vector4 Phillips0, Phillips1, Finalize, FftPass;
    }

    private ComputeShader? _phillipsCS, _fftCS, _finalizeCS;
    private Buffer? _waveConstants;
    private ShaderResourceView? _waveNoise, _waveFieldA, _waveFieldB;
    private UnorderedAccessView? _waveFieldAU, _waveFieldBU, _waveOutU;

    private void StartWaves()
    {
        using (var code = Compile(WaveShaders, "PhillipsCS", "cs_5_0")) _phillipsCS = new ComputeShader(Device, code);
        using (var code = Compile(WaveShaders, "FftCS", "cs_5_0")) _fftCS = new ComputeShader(Device, code);
        using (var code = Compile(WaveShaders, "FinalizeCS", "cs_5_0")) _finalizeCS = new ComputeShader(Device, code);
        _waveConstants = new Buffer(Device, Utilities.SizeOf<WaveConstants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        // the game's input: each texel two numbers drawn evenly between -1 and 1 (its own generator's sequence isn't kept)
        var random = new System.Random(20263);
        var noise = new float[WaveSize * WaveSize * 2];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(random.NextDouble() * 2 - 1);
        _waveNoise = Texture(noise, WaveSize, WaveSize, DxgiFormat.R32G32_Float, 8);
        (_waveFieldA, _waveFieldAU) = WaveTexture(DxgiFormat.R32G32_Float);
        (_waveFieldB, _waveFieldBU) = WaveTexture(DxgiFormat.R32G32_Float);
        (_waves, _waveOutU) = WaveTexture(DxgiFormat.R16G16B16A16_Float);
    }

    private (ShaderResourceView, UnorderedAccessView) WaveTexture(DxgiFormat format)
    {
        using var texture = new Texture2D(Device, new Texture2DDescription
        {
            Width = WaveSize, Height = WaveSize, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
        });
        return (new ShaderResourceView(Device, texture), new UnorderedAccessView(Device, texture));
    }

    /// <summary>
    /// The waves at a time (seconds), for a wind strength (0 to 1, the weather's): the engine turns it into a wind speed
    /// of 5.4w^3 - 4.45w^4 + 6.3w^2 + 6.2w + 4.05 m/s and the Phillips length L = V^2 / g; its direction (the wind
    /// manager's at run time) is taken as north.
    /// </summary>
    private void UpdateWaves(float seconds, float wind)
    {
        var c = Context!;
        float w = Math.Clamp(wind, 0, 1);
        float speed = 5.4f * w * w * w - 4.45f * w * w * w * w + 6.3f * w * w + 6.2f * w + 4.05f;
        float length = speed * speed / 9.81f;
        var constants = new WaveConstants
        {
            Phillips0 = new Vector4(0, 1, length * length, 1),
            Phillips1 = new Vector4(w + 0.4f, (1 - w) * 0.4f, seconds % 100000, 2.3e-4f),
            Finalize = new Vector4(1f / WaveSize, MathF.Floor(w * 8 + 1), 0, 0),
        };
        void Upload() => c.UpdateSubresource(ref constants, _waveConstants);
        void Unbind()
        {
            c.ComputeShader.SetShaderResources(0, new ShaderResourceView[] { null!, null! });
            c.ComputeShader.SetUnorderedAccessViews(0, new UnorderedAccessView[] { null!, null! });
        }
        Upload();
        c.ComputeShader.SetConstantBuffer(0, _waveConstants);
        c.ComputeShader.Set(_phillipsCS);
        c.ComputeShader.SetShaderResource(0, _waveNoise);
        c.ComputeShader.SetUnorderedAccessView(0, _waveFieldAU);
        c.Dispatch(WaveSize / 16, WaveSize / 16, 1);
        Unbind();
        // down the rows into B, then down the columns back into A
        c.ComputeShader.Set(_fftCS);
        c.ComputeShader.SetShaderResource(1, _waveFieldA);
        c.ComputeShader.SetUnorderedAccessView(0, _waveFieldBU);
        c.Dispatch(WaveSize, 1, 1);
        Unbind();
        constants.FftPass = new Vector4(1, 0, 0, 0);
        Upload();
        c.ComputeShader.SetShaderResource(1, _waveFieldB);
        c.ComputeShader.SetUnorderedAccessView(0, _waveFieldAU);
        c.Dispatch(WaveSize, 1, 1);
        Unbind();
        c.ComputeShader.Set(_finalizeCS);
        c.ComputeShader.SetShaderResource(1, _waveFieldA);
        c.ComputeShader.SetUnorderedAccessView(1, _waveOutU);
        c.Dispatch(WaveSize / 16, WaveSize / 16, 1);
        c.ComputeShader.SetShaderResources(0, new ShaderResourceView[] { null!, null! });
        c.ComputeShader.SetUnorderedAccessViews(0, new UnorderedAccessView[] { null!, null! });
        c.ComputeShader.Set(null);
    }

    private void DisposeWaves()
    {
        foreach (var d in new IDisposable?[] { _phillipsCS, _fftCS, _finalizeCS, _waveConstants, _waveNoise, _waveFieldA, _waveFieldB, _waveFieldAU, _waveFieldBU, _waveOutU })
            d?.Dispose();
    }
}
