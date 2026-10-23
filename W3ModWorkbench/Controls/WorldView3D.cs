using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using SharpDX;
using W3ModWorkbench.Core.World;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFormat = SharpDX.DXGI.Format;
using RawColor4 = SharpDX.Mathematics.Interop.RawColor4;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace W3ModWorkbench;

/// <summary>
/// A world in 3D, drawn with the graphics card (Direct3D 11, through <see cref="D3DHost"/>): for now its terrain. The
/// heights and the colour map go to the card as two textures the size of the world's grid; the ground is drawn as
/// patches 128 samples a side, each a flat grid lifted into shape on the card, finer near the camera and coarser far
/// off, with skirts hanging from their edges so no gaps show where two sizes meet, and patches out of view skipped.
/// The sky, the light and the fog are the world's environment's at the hour shown (<see cref="WorldLighting"/>), drawn
/// bright and brought to the screen's range by a filmic curve. The water is a level over the whole world (0, the
/// engine's own, but inside each water shape that shape's), drawn wherever it's above the ground: its colour, deeper
/// water darker, the sky mirrored at a glance and the light glinting on ripples. A free camera moves through it as
/// REDkit's does: drag to look, W A S D to move, Q and E down and up.
/// </summary>
public sealed partial class WorldView3D : D3DHost
{
    /// <summary>The shaders' constants for a frame, in the order of the HLSL's Frame (see its comments there).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Frame
    {
        public Matrix4x4 ViewProjection, Unproject;
        public Vector4 Eye, Ground, Heights, Paint, Light, LightColour, Ambient, SunDir, SkyZenith, SkyHorizon, SunGlow,
            FogFront, FogMiddle, FogBack, FogHeight, Water, Camera, AerialFront, AerialMiddle, AerialBack, FogCustom, FogCustomShape, FogLimits;
    }

    /// <summary>The tone mapping and grading constants, in the order of the HLSL's Grading.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Grading
    {
        public Vector4 Exposure, Curve, Slope, Power, Balance, Balance2, Low, Mid, High, Midtones, SkyLuminance, Vignette, VignetteColour;
    }

    /// <summary>The sky's constants, in the order of the HLSL's Sky.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Sky
    {
        public Vector4 Top, Horizon, SunHorizon, SunBackHorizon, SunSky, MoonHorizon, MoonBackHorizon, MoonSky, SunAt, MoonAt, SkyScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Patch
    {
        public float X, Y, Size, Skirt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GridPoint
    {
        public float U, V, Skirt;
    }

    // loc-ignore-start: the shaders' source
    private const string Shaders = @"
Texture2D HeightMap : register(t0);
Texture2D ColourMap : register(t1);
Texture2D<uint> ControlMap : register(t2);
Texture2DArray Diffuse : register(t3);
Texture2DArray Normals : register(t4);
Texture2D WaterLevel : register(t5);
SamplerState Smooth : register(s0);
SamplerState Tiled : register(s1);

cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 Unproject;
    float4 Eye;        // the camera; seconds, for the water's ripples
    float4 Ground;     // the grid's south-west corner, its size in metres, samples a side
    float4 Heights;    // the lowest height, the range, metres between samples
    float4 Paint;      // x: whether the water reflects the scene; w: whether the painted textures are there
    float4 Light;      // towards the light; exposure
    float4 LightColour;
    float4 Ambient;
    float4 SunDir;     // towards the sun (or the moon); how big its glow is
    float4 SkyZenith;  // and the sky's brightness
    float4 SkyHorizon; // and how much brighter the sky is than the ground
    float4 SunGlow;
    float4 FogFront;   // and the fog's density
    float4 FogMiddle;  // and how far before it appears
    float4 FogBack;    // and its vertical density
    float4 FogHeight;  // its vertical offset; how much day it is (the trajectory's skyDayAmount); vertical density towards and away from the light
    float4 Water;      // the water's colour; how fast it dims what's under it, a metre
    float4 Camera;     // the near and far planes; the view's width and height in pixels
    float4 AerialFront;    // and the aerial perspective's final exponent
    float4 AerialMiddle;   // and the fog's final exponent
    float4 AerialBack;     // and the range the fog appears over
    float4 FogCustom;      // the fog's custom colour; its custom amount scale
    float4 FogCustomShape; // where the custom colour starts and its range; where the custom amount starts and its range
    float4 FogLimits;      // the farthest the fog counts; how thick it is on the sky; how bright its colours are; how wavy the water is
};

cbuffer SkyParameters : register(b3)
{
    float4 SkyTop;            // the sky's colour at the top; the visible sky's brightness scale
    float4 SkyAtHorizon;      // at the horizon
    float4 SunHorizon;        // at the horizon towards the sun
    float4 SunBackHorizon;    // and away from it
    float4 SunSky;            // round the sun
    float4 MoonHorizon;       // at the horizon towards the moon
    float4 MoonBackHorizon;   // and away from it
    float4 MoonSky;           // round the moon; how bright the sky is in reflections (the probes' sky brightness scale)
    float4 SunAt;             // towards the sun; how tight its glow is
    float4 MoonAt;            // towards the moon; how tight its glow is
    float4 SkyScale;          // the sun's and the moon's influence, the brightness, the horizon's vertical attenuation
};


cbuffer Grading : register(b2)
{
    float4 Exposure;     // the exposure scale, the luminance limits' shape, their least and most
    float4 Curve;        // AgX's global contrast, shoulder power, toe power, black point lift
    float4 Slope;        // AgX's slope colour; its saturation
    float4 Power;        // AgX's power colour; its mid grey
    float4 Balance;      // the balance maps' amount (0 when off), their mix; the levels: shadows, midtones
    float4 Balance2;     // the levels' highlights, whether the parametric balance is on, the post brightness, the smallest level of the scene
    float4 Low;          // the shadows' tint; saturation
    float4 Mid;          // the midtones'
    float4 High;         // the highlights'
    float4 Midtones;     // the midtone range's least and most, the margins below and above it
    float4 SkyLuminance; // the sky's luminance for adaptation, and how much of it is that
    float4 Vignette;     // the weights the vignette spares bright pixels by; its opacity
    float4 VignetteColour;
};

// ---- light, sky and fog ----

// The sky as the game's sky shader paints it (its compiled code, read from the shader cache): at the horizon its colour
// runs towards the sun's horizon colour on the side facing the sun and its back horizon colour on the other (by how
// level the view is, and the sun's influence), the same again for the moon; the top colour takes over going up, faster
// the higher the camera (the horizon's vertical attenuation); the colours round the sun and the moon are laid over
// that by their glows; and the whole is scaled by the sky's brightness.
float3 SkyColour(float3 dir)
{
    float2 flat = normalize(dir.xy + float2(0, 1e-6));
    float level = 1 - dir.z * dir.z;
    float sunSide = dot(normalize(SunAt.xy + float2(0, 1e-6)), flat) * 0.5 + 0.5;
    float moonSide = dot(normalize(MoonAt.xy + float2(0, 1e-6)), flat) * 0.5 + 0.5;
    float3 sky = lerp(SkyAtHorizon.rgb, lerp(SunBackHorizon.rgb, SunHorizon.rgb, sunSide), level * saturate(SkyScale.x));
    sky = lerp(sky, lerp(MoonBackHorizon.rgb, MoonHorizon.rgb, moonSide), level * saturate(SkyScale.y));
    float up = 1 - pow(saturate(1 / max(0.0001, SkyScale.w * 0.001 * ((Eye.z + 710) + dir.z * 1000) + 0.1)), 2.8);
    sky = lerp(sky, SkyTop.rgb, up);
    sky = lerp(sky, SunSky.rgb, pow(saturate(dot(SunAt.xyz, dir)), SunAt.w) * saturate(SkyScale.x));
    sky = lerp(sky, MoonSky.rgb, saturate(SkyScale.y) * pow(saturate(dot(MoonAt.xyz, dir)), MoonAt.w));
    sky *= SkyScale.z;
    // the sun's disc (half a degree across), or by night the moon's, smaller and paler
    float day = max(FogHeight.y, 0.02);
    float disc = smoothstep(0.99996, 0.99999, saturate(dot(dir, SunDir.xyz)));
    sky += lerp(float3(0.6, 0.65, 0.7) * 2 * SkyScale.z * 0.02, LightColour.rgb * 40, day) * disc;
    return sky;
}

// The game's global fog, as its shaders compute it (read from the water shader's compiled code, where it's done per
// pixel): the ray from the camera is walked in 16 steps, each dimming by the density over its length, less the higher
// it is (the vertical density, towards or away from the light, over the height above the vertical offset); the fog's
// amount and the aerial perspective's are that raised to their final exponents, faded in over the appear range; the
// colour runs from the middle one to the front (towards the light) or back one, and towards the custom colour as the
// fog thickens.
float2 FogAt(float3 world, float sky, out float3 fog, out float3 aerial)
{
    float3 ray = world - Eye.xyz;
    float reach = length(ray);
    float3 dir = ray / max(reach, 0.001);
    float distance = min(FogLimits.x, reach);
    float stepLength = distance / 16;
    float facing = dot(dir, SunDir.xyz);
    float towards = saturate((facing + 1) * 0.5);
    float vertical = -FogBack.w * lerp(lerp(FogHeight.w, FogHeight.z, towards), lerp(SunBackHorizon.w, SunHorizon.w, towards), sky);
    float start = Eye.z + FogHeight.x;
    float density = FogFront.w * lerp(1, FogLimits.y, sky);
    float through = 1;
    [unroll] for (int k = 1; k <= 16; k++)
        through *= 1 - saturate(stepLength * density / (max(0, vertical * (start + k * stepLength * dir.z)) + 1));
    float amount = saturate(1 - through);
    float appear = saturate((distance - FogMiddle.w) / max(AerialBack.w, 0.001));
    float fogAmount = pow(amount, AerialMiddle.w) * appear;
    float aerialAmount = pow(amount, AerialFront.w) * appear;
    float side = facing * facing * saturate(distance * 0.002 - 0.3);
    fog = lerp(FogMiddle.rgb, facing > 0 ? FogFront.rgb : FogBack.rgb, side);
    aerial = lerp(AerialMiddle.rgb, facing > 0 ? AerialFront.rgb : AerialBack.rgb, side);
    fog = lerp(fog, FogCustom.rgb, saturate((fogAmount - FogCustomShape.x) / max(FogCustomShape.y, 0.001)));
    fogAmount = saturate(lerp(1, FogCustom.w, saturate((fogAmount - FogCustomShape.z) / max(FogCustomShape.w, 0.001))) * fogAmount);
    return float2(fogAmount, aerialAmount);
}

float3 Fogged(float3 colour, float3 world)
{
    float3 fog, aerial;
    float2 amount = FogAt(world, 0, fog, aerial);
    // the aerial perspective: the colour's brightness in the aerial colour, then the fog over it
    colour = lerp(colour, dot(float3(0.333, 0.555, 0.222), colour) * aerial, amount.y);
    return lerp(colour, fog * FogLimits.z, amount.x);
}

// a filmic curve (ACES, fitted), then to the screen's gamma
// The scene is drawn in high dynamic range, in the world's light (the scale to the engine's units in Light.w), with
// each pixel's luminance in those units in alpha for the eye's adaptation; the last pass (PostPS) brings it to the
// screen as the game's tone mapping and grading do.
float4 Screen(float3 hdr, float alpha)
{
    return float4(hdr, dot(hdr, float3(0.2126, 0.7152, 0.0722)) * Light.w);
}

// ---- sky ----

struct SkyOut { float4 Position : SV_Position; float2 Ndc : TEXCOORD0; };

SkyOut SkyVS(uint id : SV_VertexID)
{
    // one triangle over the whole view
    float2 p = float2((id << 1) & 2, id & 2) * 2 - 1;
    SkyOut o;
    o.Position = float4(p, 1, 1);
    o.Ndc = p;
    return o;
}

// the sky as it's seen: fogged as far off as the far plane, by the fog's sky density
float3 SkySeen(float3 dir)
{
    float3 fog, aerial;
    float2 amount = FogAt(Eye.xyz + dir * FogLimits.x, 1, fog, aerial);
    return lerp(SkyColour(dir), fog * FogLimits.z, amount.x);
}

cbuffer TrajectoryDots : register(b5)
{
    float4 TrajectoryPoints[96]; // the sun's (w 1) and the moon's (w 2) directions every half hour; w 0 for none
};

float4 SkyPS(SkyOut i) : SV_Target
{
    float4 far = mul(float4(i.Ndc, 1, 1), Unproject);
    float3 dir = normalize(far.xyz / far.w - Eye.xyz);
    float4 sky = Screen(SkySeen(dir), 1);
    // the sun's and the moon's paths, as REDkit's trajectory display shows them
    [loop] for (int k = 0; k < 96; k++)
        if (TrajectoryPoints[k].w > 0 && dot(dir, TrajectoryPoints[k].xyz) > 0.99996)
            sky.rgb += (TrajectoryPoints[k].w < 1.5 ? float3(1, 0.8, 0.2) : float3(0.4, 0.6, 1)) * SkyScale.z * 0.3;
    sky.a = lerp(sky.a, SkyLuminance.x, SkyLuminance.y);
    return sky;
}

// ---- terrain ----
// As the game's terrain material shader does it (its compiled code, read from the shader cache): each of the four
// control samples round a point gives a background, drawn flat from above at 3 m a repeat, and an overlay, drawn from
// three sides at its own scale; the overlay shows where the slope passes the sample's threshold, fading in over the
// background texture's falloff; and the colour map is laid over the result as an overlay blend.

cbuffer TextureSettings : register(b1)
{
    // for each of the 31 textures: val, then val2 (the clip map's textureParams)
    float4 Slot[64];
};

static const float Scales[8] = { 0.333, 0.166, 0.05, 0.025, 0.0125, 0.0075, 0.00375, 0.00375 };
static const float SlopeThresholds[8] = { 0, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.98 };

// the engine's slope tangent: the angle from straight up as its tangent (a series), stepped between two thresholds
float SlopeTangent(float3 n, float low, float high)
{
    float x = acos(saturate(n.z)), x3 = x * x * x;
    float t = x + x3 / 3 + 2 * x3 * x * x / 15;
    return saturate((saturate(t) - low) / max(high - low, 1e-4));
}

float2 GridUv(float2 world)
{
    // sample i is at the corner plus i spacings, rows counted from the north
    return float2((world.x - Ground.x) / Ground.z + 0.5 / Ground.w, 1 - (world.y - Ground.y) / Ground.z - 0.5 / Ground.w);
}

// the ground's colour at a point, as the grass takes its pigment from it: the four control samples' background
// textures far down their mips (their average colour), blended by the samples' weights, the colour map laid over
float3 TerrainPigment(float2 world)
{
    float2 uv = GridUv(world);
    float3 tint = ColourMap.SampleLevel(Smooth, uv, 0).rgb;
    if (Paint.w <= 0) return tint;
    float2 cell = uv * Ground.w - 0.5;
    int2 at = (int2)floor(cell);
    float2 f = cell - at;
    float3 painted = 0;
    [unroll] for (int k = 0; k < 4; k++)
    {
        int2 o = int2(k & 1, k >> 1);
        float w = (o.x ? f.x : 1 - f.x) * (o.y ? f.y : 1 - f.y);
        uint control = ControlMap.Load(int3(clamp(at + o, 0, (int)Ground.w - 1), 0));
        painted += Diffuse.SampleLevel(Tiled, float3(world * 0.333, (control & 31) - 1), 8).rgb * w;
    }
    return tint < 0.5 ? 2 * painted * tint : 1 - 2 * (1 - painted) * (1 - tint);
}

// the ground's normal at a point, from the heights round it
float3 GroundNormal(float2 world)
{
    float2 uv = GridUv(world);
    float texel = 1 / Ground.w;
    float hw = HeightMap.SampleLevel(Smooth, uv - float2(texel, 0), 0).r, he = HeightMap.SampleLevel(Smooth, uv + float2(texel, 0), 0).r;
    float hn = HeightMap.SampleLevel(Smooth, uv - float2(0, texel), 0).r, hs = HeightMap.SampleLevel(Smooth, uv + float2(0, texel), 0).r;
    float scale = Heights.y / (2 * Heights.z);
    return normalize(float3((hw - he) * scale, (hs - hn) * scale, 1));
}

float GroundHeight(float2 uv) { return HeightMap.SampleLevel(Smooth, uv, 0).r * Heights.y + Heights.x; }

struct Out { float4 Position : SV_Position; float3 World : TEXCOORD0; float2 Uv : TEXCOORD1; };

// ---- the terrain's shadows ----
// For each point of the height map, how much of the sun (or moon) it sees past the terrain towards it: the height map
// walked towards the light in growing steps as far as 14 km, the steepest rise seen from the point (its horizon that
// way) against how high the light stands, softened over a couple of degrees as the edge of a shadow is. A way of our
// own: the game's terrain shadows aren't decoded yet.

Texture2D<float> ShadowMap : register(t13);

float4 ShadowPS(float4 position : SV_Position) : SV_Target
{
    float2 uv = position.xy / Ground.w;
    float2 world = float2(Ground.x + uv.x * Ground.z, Ground.y + (1 - uv.y) * Ground.z);
    float flat = length(Light.xy);
    if (Light.z <= -0.05) return 0;
    float2 toward = Light.xy / max(flat, 1e-4);
    float sun = Light.z / max(flat, 1e-4);
    // from the water's surface where there's water over the ground, so the water isn't shaded by the bed's bumps
    float start = max(GroundHeight(uv), WaterLevel.SampleLevel(Smooth, uv, 0).r) + 0.3;
    float horizon = -1e3, reach = Heights.z;
    [loop] for (int k = 0; k < 120; k++)
    {
        float2 at = world + toward * reach;
        float2 atUv = GridUv(at);
        if (any(atUv < 0) || any(atUv > 1)) break;
        horizon = max(horizon, (GroundHeight(atUv) - start) / reach);
        reach = reach * 1.05 + Heights.z * 0.25;
    }
    return saturate((sun - horizon) / 0.035 + 0.5);
}

cbuffer Plants : register(b4)
{
    row_major float4x4 SunViewProjection; // the sun's view of the plants round the camera
    float4 CameraRight;                   // facing leaves turn to the camera (or, drawing the shadows, the sun)
    float4 CameraUp;                      // w: whether the plants' shadows are there
    float4 BillboardShape;                // a kind's billboard: width, top, bottom, number of views
    float4 BillboardViews[32];            // each view's place in the atlas: left, top, width, height
    float4 BillboardStyle;                // which random colours the side views take: 0 trees, 1 branches, 2 grass, 3 none
};

Texture2D PlantPicture : register(t14);
Texture2D PlantSpecular : register(t24);

cbuffer PlantMaterial : register(b10)
{
    float4 PlantMat; // the specular colour's green, the transmission colour's green, the glossiness (negative without a
                     // specular map), the render state's alpha scalar
    float4 PlantFlags; // whether its grass takes the ground's colour (its SpeedTree file's PigmentFloodOff tag leaves it out)
};
Texture2D PlantNormals : register(t19);
Texture2D<float> PlantShadowMap : register(t15);
SamplerComparisonState ShadowCompare : register(s2);

// how much of the sun the plants let through to a point: the sun's view of them, four taps for a soft edge
float PlantShade(float3 world)
{
    if (CameraUp.w < 0.5) return 1;
    float4 s = mul(float4(world, 1), SunViewProjection);
    float2 uv = s.xy * float2(0.5, -0.5) + 0.5;
    if (any(uv < 0) || any(uv > 1) || s.z > 1) return 1;
    float texel = 1.0 / 4096, lit = 0;
    [unroll] for (int k = 0; k < 4; k++)
        lit += PlantShadowMap.SampleCmpLevelZero(ShadowCompare, uv + float2((k & 1) - 0.5, (k >> 1) - 0.5) * texel * 1.5, s.z);
    return lit / 4;
}

cbuffer GlobalLightColour : register(b7)
{
    float4 LightSide;          // the light's colour far off, up high
    float4 LightOppositeSide;  // and far off, down low
    float4 LightColourParams;  // how it goes from near to far: a scale and bias by distance across, then by height
};

// as the engine's CalcGlobalLightColor: the sun's own colour near the camera, giving way far off to the light side's
// colour high up and the opposite side's low down
float3 GlobalLightColour(float3 world)
{
    float across = length(world.xy - Eye.xy);
    float3 far = lerp(LightOppositeSide.rgb, LightSide.rgb, saturate(world.z * LightColourParams.z + LightColourParams.w));
    return lerp(LightColour.rgb, far, saturate(across * LightColourParams.x + LightColourParams.y));
}

cbuffer Foliage : register(b8)
{
    float4 TreeRandom[3];      // the trees' (leaves') random colours, linear; their saturations in w
    float4 BranchRandom[3];
    float4 GrassRandom[3];
    float4 RandomLuminance[3]; // the luminance weights the saturation works against: trees, branches, grass
    float4 PlantTint;          // the plants' diffuse tint; how much of their ambient occlusion counts
    float4 BillboardTint;      // the billboards' tint; how translucent leaves are
    float4 Translucency;       // view dependency, base flatness, flat brightness, gain brightness
    float4 AlphaScalars;       // 3D plants, grass, billboards: the alpha scale before the 0.1 cut
    float4 AmbientScales;      // the ambient's scale in light and in shadow; the units; the sky's brightest
    float4 AmbientColour;      // the probes' base ambient colour
    float4 SkyIbl;             // the sky's brightness and saturation, the ground's brightness and saturation in the ambient
    float4 ProbeSkyTop;        // the probes' sky at the top; the shape between it and the horizon
    float4 ProbeSkyHorizon;    // and at the horizon; the reflections' scale in light
    float4 SceneAdd;           // the light the scene adds to the probes; the reflections' scale in shadow
    float4 AoSettings;         // the ambient occlusion's radius, bias, power, and how much it dims direct light
    float4 SpecularSettings;   // grass's f90, the other foliage's f90, the trees' specular scale
    float4 WaterSettings;      // the environment's water Fresnel (its reflectance looking straight down); the glint's scale; the AO's power on grass and on trees
    float4 TranslucencyCurve;  // the world's remaster translucency: curve tightness, highlight dimness, normal mask power, scattering saturation
    float4 CanopySettings;     // how strongly the trees' crowns over a point hide the sky from it; whether the canopy map is there; grass's ambient scale (m_ibl grassIBLScale); its albedo multiplier
    float4 Pigment;            // the grass's pigment: brightness, where the flood starts, over how far (m_speedTree pigment*); how much more low on the plant
    float4 GrassSettings;      // the grass's albedo multiplier
    float4 TreeFallback;       // the trees' fallback colour (m_speedTree randomColorsFallback)
    float4 WaterLook;          // the environment's water ambient scale (in shadow), diffuse scale (in light), foam intensity
};
#define GrassAlbedo GrassSettings.x
// which of the light to show, as REDkit's decomposed views do: 0 all of it, 1 the ambient (and the probes' reflection),
// 2 the sun's light (its diffuse, translucency and specular), 3 the albedo (as the G-buffer keeps it, in gamma), 4 the
// ambient occlusion the ambient takes
#define LightingView GrassSettings.y
// the translucency's global scale for leaves and grass (cb0[24].x in the game's SpeedTree shaders: 1, as REDkit's G-buffer
// shows the leaves' translucency at their specular map's alpha times their transmission colour), for billboards
#define TranslucencyGlobal GrassSettings.zw

float3 LightingPart(float3 all, float3 ambient)
{
    return LightingView > 1.5 ? all - ambient : LightingView > 0.5 ? ambient : all;
}

// The trees' crowns over each few metres (how many, the canopy's top). The game's ambient comes from probes it renders
// in the scene, so under a forest they see the forest; ours comes from the sky, dimmed under the crowns by their count.
Texture2D Canopy : register(t20);

float CanopySky(float3 world)
{
    if (CanopySettings.y < 0.5) return 1;
    float2 c = Canopy.SampleLevel(Smooth, GridUv(world.xy), 0).rg;
    return lerp(1, exp(-CanopySettings.x * c.x), saturate((c.y - world.z) / 6));
}

Texture2D<float> AoMap : register(t18);

// the ambient occlusion at a pixel, as the ambient pass made it
float AoAt(float4 position) { return AoMap.Load(int3(int2(position.xy), 0)); }

// the probes' sky, as the engine's probe sky colour: from the top's colour to the horizon's by how far round from the top
// a direction is (0 straight up, 1 at the horizon), bent by the shape
float3 ProbeSky(float3 dir)
{
    float towardsHorizon = 0.5 * length(dir.xy) / max(1 - dir.z, 1e-4);
    return lerp(ProbeSkyTop.rgb, ProbeSkyHorizon.rgb, pow(saturate(towardsHorizon), max(ProbeSkyTop.w, 1e-3)));
}

// the engine's GGX specular for the sun: D (GGX), V (Smith, height-correlated) and Schlick's F towards f90
float3 SpecularAt(float3 n, float3 v, float3 l, float specularity, float roughness, float f90)
{
    float nl = dot(n, l);
    if (nl <= 0) return 0;
    float3 h = normalize(v + l);
    float a = roughness * roughness, a2 = a * a, nh = saturate(dot(n, h)), nv = abs(dot(n, v));
    float d = a2 / (3.14152 * pow(nh * nh * (a2 - 1) + 1, 2));
    float lv = nl * sqrt(nv * nv * (1 - a2) + a2), ll = nv * sqrt(nl * nl * (1 - a2) + a2);
    float vis = 0.5 / (lv + ll + 0.0001);
    float f = specularity + max(0, f90 - specularity) * pow(saturate(1 - abs(dot(h, v))), 5);
    return f * d * vis * saturate(nl);
}

// the reflection of the probes' sky, by the engine's env-probe Fresnel (its grazing gain held back on rough surfaces),
// its strength in light or in shadow; the environment's reflection colours are white at 1
#define GlintScale WaterSettings.y
float3 ReflectionAt(float3 n, float3 v, float specularity, float roughness, float sun)
{
    float3 r = reflect(-v, n);
    r.z = abs(r.z);
    float smooth = 1 - roughness;
    float f = specularity + (1 - specularity) * pow(saturate(1 - abs(dot(n, v))), 5) * smooth * smooth;
    return ProbeSky(r) * f * lerp(SceneAdd.w, ProbeSkyHorizon.w, sun);
}

Texture2D AmbientCube : register(t17);

// the ambient light round a normal from the ambient cube (six directions: +x -x +y -y +z -z), as the probes give it,
// scaled by the environment's ambient scale in light or in shadow
float3 AmbientAt(float3 n, float shadow)
{
    float3 n2 = n * n;
    float3 cube = n2.x * AmbientCube.Load(int3(n.x >= 0 ? 0 : 1, 0, 0)).rgb + n2.y * AmbientCube.Load(int3(n.y >= 0 ? 2 : 3, 0, 0)).rgb
        + n2.z * AmbientCube.Load(int3(n.z >= 0 ? 4 : 5, 0, 0)).rgb;
    return cube * AmbientColour.rgb * lerp(AmbientScales.y, AmbientScales.x, shadow);
}

float SunSeen(float3 world) { return ShadowMap.SampleLevel(Smooth, GridUv(world.xy), 0) * PlantShade(world); }

Out TerrainVS(float2 uv : TEXCOORD0, float skirt : TEXCOORD1, float4 patch : PATCH)
{
    float2 world = patch.xy + uv * patch.z;
    float2 t = GridUv(world);
    // the size of the heights' copy matching the patch's spacing, so far patches don't shimmer
    float level = log2(max(1, patch.z / Heights.z / 128));
    float h = HeightMap.SampleLevel(Smooth, t, level).r * Heights.y + Heights.x - skirt * patch.w;
    Out o;
    o.World = float3(world, h);
    o.Position = mul(float4(o.World, 1), ViewProjection);
    o.Uv = t;
    return o;
}

// a texture seen from three sides, each weighted by how squarely the surface faces it
float4 Triplanar(Texture2DArray tex, float3 world, float scale, float slice, float3 weights)
{
    float4 c = 0;
    if (weights.z > 0) c += tex.Sample(Tiled, float3(world.xy * scale, slice)) * weights.z;
    if (weights.y > 0) c += tex.Sample(Tiled, float3(float2(-world.x, -world.z) * scale, slice)) * weights.y;
    if (weights.x > 0) c += tex.Sample(Tiled, float3(float2(-world.y, -world.z) * scale, slice)) * weights.x;
    return c;
}

float4 TriplanarNormal(float3 world, float scale, float slice, float3 weights, float3 vertexNormal)
{
    float4 xy = 0, xz = 0, yz = 0;
    if (weights.z > 0) xy = Normals.Sample(Tiled, float3(world.xy * scale, slice));
    if (weights.y > 0) xz = Normals.Sample(Tiled, float3(float2(-world.x, -world.z) * scale, slice));
    if (weights.x > 0) yz = Normals.Sample(Tiled, float3(float2(-world.y, -world.z) * scale, slice));
    if (vertexNormal.y < 0) xz.y = 1 - xz.y;
    yz.y = 1 - yz.y;
    return weights.z * xy + weights.y * xz + weights.x * yz;
}

float4 TerrainPS(Out i) : SV_Target
{
    float roughness = 0.8;
    // the material settings (the clip map's textureParams' second half: specularity, its base, its scale by
    // smoothness, the falloff), the horizontal textures' and the vertical ones'
    float4 material = float4(0.45, 0.5, 0, 0);
    float texel = 1 / Ground.w;
    float hw = HeightMap.Sample(Smooth, i.Uv - float2(texel, 0)).r, he = HeightMap.Sample(Smooth, i.Uv + float2(texel, 0)).r;
    float hn = HeightMap.Sample(Smooth, i.Uv - float2(0, texel)).r, hs = HeightMap.Sample(Smooth, i.Uv + float2(0, texel)).r;
    float scale = Heights.y / (2 * Heights.z);
    float3 normal = normalize(float3((hw - he) * scale, (hs - hn) * scale, 1));
    float3 tint = ColourMap.Sample(Smooth, i.Uv).rgb;
    float3 colour = tint;
    if (Paint.w > 0)
    {
        float2 cell = i.Uv * Ground.w - 0.5;
        int2 at = (int2)floor(cell);
        float2 f = cell - at;
        // a hole where the nearest sample has no texture at all (a cave's mouth, a cellar)
        clip((float)ControlMap.Load(int3(clamp(at, 0, (int)Ground.w - 1), 0)) - 0.5);
        // as the engine's terrain material (REDkit's terrainMaterialDX11.fx, EvaluateMaterial): the four control
        // samples' horizontal textures flat at 0.333 a metre and vertical ones from three sides at their own scales,
        // blended by the samples' weights; one slope tangent from the blended normals decides between them
        float3 sides = max(abs(normal) - 0.576, 0);
        sides /= max(sides.x + sides.y + sides.z, 0.0001);
        float3 tangent = normalize(float3(1, 0, 0) - normal * normal.x), binormal = cross(normal, tangent);
        float4 horizontal = 0, vertical = 0, horizontalN = 0, verticalN = 0;
        float threshold = 0, sharpness = 0, damp = 0, normalDamp = 0;
        float4 horizontalM = 0, verticalM = 0;
        [unroll] for (int k = 0; k < 4; k++)
        {
            int2 o = int2(k & 1, k >> 1);
            float w = (o.x ? f.x : 1 - f.x) * (o.y ? f.y : 1 - f.y);
            uint control = ControlMap.Load(int3(clamp(at + o, 0, (int)Ground.w - 1), 0));
            uint back = (control & 31) - 1, over = ((control >> 5) & 31) - 1;
            float overScale = Scales[(control >> 13) & 7];
            threshold += SlopeThresholds[(control >> 10) & 7] * w;
            sharpness += Slot[min(back, 31) * 2].x * w;
            damp += Slot[min(over, 31) * 2].y * w;
            normalDamp += Slot[min(over, 31) * 2].z * w;
            horizontalM += Slot[min(back, 31) * 2 + 1] * w;
            verticalM += Slot[min(over, 31) * 2 + 1] * w;
            float2 flat = i.World.xy * 0.333;
            horizontal += Diffuse.Sample(Tiled, float3(flat, back)) * w;
            horizontalN += Normals.Sample(Tiled, float3(flat, back)) * w;
            vertical += Triplanar(Diffuse, i.World, overScale, over, sides) * w;
            verticalN += TriplanarNormal(i.World, overScale, over, sides, normal) * w;
        }
        // the normal maps at full strength, in the surface's tangent space
        float3 h = normalize(horizontalN.xyz - 0.5), v = normalize(verticalN.xyz - 0.5);
        float3 combinedVertical = v.x * tangent + v.y * binormal + v.z * normal;
        float3 combinedHorizontal = h.x * tangent + h.y * binormal + h.z * normal;
        float3 flattened = lerp(combinedVertical, float3(0, 0, 1), normal.z);
        float3 judged = normalize(lerp(combinedVertical, flattened, damp));
        float amount = SlopeTangent(judged, threshold, saturate(threshold + sharpness));
        // the engine's CombineNormalsDerivatives: the two normals' slopes added, the horizontal one's by the damping
        float2 dv = -combinedVertical.xy / combinedVertical.z, dh = -combinedHorizontal.xy / combinedHorizontal.z;
        float3 both = normalize(float3(-(1 - normalDamp) * dv - normalDamp * dh, 1));
        float3 painted = lerp(horizontal.rgb, vertical.rgb, amount);
        // the colour map laid over it: darker below mid grey, lighter above (the engine's ApplyColorMap)
        colour = tint < 0.5 ? 2 * painted * tint : 1 - 2 * (1 - painted) * (1 - tint);
        normal = normalize(lerp(both, combinedVertical, amount));
        roughness = lerp(horizontalN.a, verticalN.a, amount);
        material = lerp(horizontalM, verticalM, amount);
    }
    float3 v = normalize(Eye.xyz - i.World);
    // the world's terrain material graph (levels\prolog_village	errain_material.w2mg, as REDkit has it): the
    // diffuse brightened at grazing angles by the falloff (its Fresnel block at power 16), and the specularity the
    // texture's own (gamma) times its smoothness by the scale plus three times the base past a half
    colour += saturate(pow(1 - saturate(dot(normal, v)), 16)) * material.w;
    float specularity = saturate(pow(saturate(material.x), 2.2) * ((1 - roughness) * material.z + (material.y - 0.5) * 3));
    float3 albedo = pow(saturate(colour), 2.2);
    // the sun's f90 on the ground, as the remaster's lookdev defaults have it (read from REDkit's editor.exe):
    // TerrainF90ForGlobalLightStartValue 0.35 at 5 m from the eye, SceneF90EndValue 0.25 from 200 m
    float f90 = lerp(max(0.35, specularity), max(0.25, specularity), saturate((length(Eye.xyz - i.World) - 5) / 195));
    float sun = SunSeen(i.World), ao = AoAt(i.Position);
    float3 sunLight = GlobalLightColour(i.World) * sun;
    // the engine's lighting: Lambert over pi and GGX specular from the sun, dimmed by the AO's share for direct light;
    // the probes' ambient and reflection, dimmed by the AO
    float direct = lerp(1, ao, AoSettings.w);
    float sky = CanopySky(i.World);
    float3 ambientPart = albedo * AmbientAt(normal, sun) * ao * sky + ReflectionAt(normal, v, specularity, clamp(roughness, 0.05, 1), sun) * ao * sky;
    float3 lit = albedo * sunLight * saturate(dot(normal, Light.xyz)) / 3.14159265 * direct
        + sunLight * SpecularAt(normal, v, Light.xyz, specularity, clamp(roughness, 0.05, 1), f90) * direct + ambientPart;
    if (LightingView > 3.5) return float4(ao.xxx, 1);
    if (LightingView > 2.5) return float4(pow(saturate(albedo), 1 / 2.2), 1);
    return Screen(Fogged(LightingPart(lit, ambientPart), i.World), 1);
}

// ---- water ----
// As the game's water shaders do it (read from its static shader cache), where the data is there: the waves damped in
// shallow water ((|depth| - 0.1) x 0.204 and x 0.526) and gone past 250 m from the camera; what's under the water is
// the scene already drawn, seen through it bent by the surface and dimmed by how much water the eye looks through;
// the sky mirrored at a glance; and the game's foam loop (32 frames, frac(time x 0.024) x 32, at 0.04 a metre) in the
// shallows. The game's waves come from a wave simulation it runs each frame; these are a few crossing waves instead.

Texture2D<float> DepthMap : register(t6);
Texture2D SceneMap : register(t7);
Texture2DArray FoamLoop : register(t8);
Texture2D WaveMap : register(t9);

float ViewDepth(float stored) { return Camera.x * Camera.y / (Camera.y - stored * (Camera.y - Camera.x)); }

Out WaterVS(float2 uv : TEXCOORD0, float skirt : TEXCOORD1, float4 patch : PATCH)
{
    float2 world = patch.xy + uv * patch.z;
    float2 t = GridUv(world);
    Out o;
    o.World = float3(world, WaterLevel.SampleLevel(Smooth, t, 0).r);
    o.Position = mul(float4(o.World, 1), ViewProjection);
    o.Uv = t;
    return o;
}

// the waves' slope: the wave map (a tileable sea made once from a spectrum of waves, as the game's simulation makes
// its own each frame) at three sizes, as the game's large, medium and small layers, each drifting its own way; the
// long layers damped in shallower water than the short one, as the game damps them
float2 Ripples(float2 p, float shallowLong, float shallowShort)
{
    float time = Eye.w;
    float2 large = WaveMap.Sample(Tiled, p / 34 + time * float2(0.011, 0.006)).rg * 2 - 1;
    float2 medium = WaveMap.Sample(Tiled, p / 11 + time * float2(-0.017, 0.021)).rg * 2 - 1;
    float2 small = WaveMap.Sample(Tiled, p / 3.3 + time * float2(0.043, -0.031)).rg * 2 - 1;
    return (large * 0.55 + medium * 0.35) * shallowLong + small * 0.22 * shallowShort;
}

// what the water mirrors: the reflected ray walked through the scene already drawn until it passes behind something
// there (as the game's screen-space reflections do), or the sky when it leaves the view without meeting anything
float3 Reflection(float3 start, float3 dir, float3 sky)
{
    float stepLength = 0.6;
    float3 p = start;
    [loop] for (int k = 0; k < 56; k++)
    {
        p += dir * stepLength;
        stepLength *= 1.1;
        float4 clip = mul(float4(p, 1), ViewProjection);
        if (clip.w <= Camera.x) break;
        float2 uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
        if (uv.x < 0 || uv.y < 0 || uv.x > 1 || uv.y > 1) break;
        float stored = DepthMap.SampleLevel(Smooth, uv, 0);
        if (stored >= 1) continue;
        float scene = ViewDepth(stored);
        if (clip.w > scene + 0.05 && clip.w < scene + stepLength * 3 + 1)
        {
            float edge = saturate(min(min(uv.x, 1 - uv.x), min(uv.y, 1 - uv.y)) * 12);
            return lerp(sky, SceneMap.SampleLevel(Smooth, uv, 0).rgb, edge);
        }
    }
    return sky;
}

float4 WaterPS(Out i) : SV_Target
{
    float depth = i.World.z - GroundHeight(i.Uv);
    clip(depth + 0.05);
    // none over the ground's holes (a cave's or a crypt's mouth), which the ground leaves out
    if (Paint.w > 0) clip((float)ControlMap.Load(int3(clamp((int2)floor(i.Uv * Ground.w), 0, (int)Ground.w - 1), 0)) - 0.5);
    float distance = length(i.World - Eye.xyz);
    float near = saturate((250 - distance) * 0.004);
    float shallowLong = saturate((abs(depth) - 0.1) * 0.2040816) * 0.7 + 0.3;
    float shallowShort = saturate((abs(depth) - 0.1) * 0.5263158) * 0.4 + 0.6;
    float3 n = normalize(float3(-Ripples(i.World.xy, shallowLong, shallowShort) * lerp(0.3, 1, near) * FogLimits.w, 1));

    // how much water the eye looks through to what's behind
    int2 pixel = int2(i.Position.xy);
    float behind = ViewDepth(DepthMap.Load(int3(pixel, 0)));
    float thickness = max(behind - i.Position.w, 0);
    float2 screen = i.Position.xy / Camera.zw;
    float2 bent = screen + n.xy * 0.03 * saturate(thickness * 2);
    // keep it straight where the bent view would find something in front of the water
    if (ViewDepth(DepthMap.SampleLevel(Smooth, bent, 0)) < i.Position.w) bent = screen;
    float3 under = SceneMap.SampleLevel(Smooth, bent, 0).rgb;

    float3 view = normalize(i.World - Eye.xyz);
    float3 reflected = reflect(view, n);
    reflected.z = abs(reflected.z);
    float seen = SunSeen(i.World);
    // how much water the eye looks through (towards its vertical depth further off, over 200 metres), to the power 0.6
    float looked = pow(max(lerp(thickness, max(depth, 0), saturate(distance * 0.005)), 0), 0.6);
    // how much the water mirrors, as the game's water shader writes it for the reflections drawn after it: its own
    // Fresnel, ((1 - n.v)^4 / 4 + 0.05) to the power of the environment's waterFresnel + 0.5, times its diffuse scale in
    // light and its ambient scale in shadow, times the water looked through (0.33 + 0.17 x a light's w we haven't placed,
    // taken at a half), so shallow water mirrors less; the wave height's share of it (0.4 to 1) taken at its least; and
    // the square root of all that, as the shader writes it
    float nv0 = saturate(dot(n, -view));
    float lookScale = lerp(WaterLook.x, WaterLook.y, seen);
    float fresnel = sqrt(saturate(lookScale * pow(pow(1 - nv0, 4) * 0.25 + 0.05, clamp(WaterSettings.x + 0.5, 0.01, 6)) * 0.4
        * saturate(0.415 * looked)));
    float3 body = Screen(Water.rgb * (GlobalLightColour(i.World) * saturate(Light.z) * seen / 3.14159265 + AmbientAt(float3(0, 0, 1), seen)), 1).rgb;
    // the sky as it's seen: measured against REDkit's lake, the water mirrors it at its own brightness
    float3 sky = Screen(SkySeen(reflected), 1).rgb;
    if (Paint.x > 0.5) sky = Reflection(i.World + float3(0, 0, 0.05), reflected, sky);
    // the sun's (or moon's) highlight as the game's water shader has it: GGX with alpha squared 1e-4 (its 3.183e-05 is
    // that over pi), the visibility term, held to 1, times the light
    float3 halfway = normalize(Light.xyz - view);
    float nh = saturate(dot(n, halfway)), nl = saturate(dot(n, Light.xyz)), nv = saturate(dot(n, -view));
    float a2 = 1e-4, dd = nh * nh * (a2 - 1) + 1;
    // (its Fresnel its own: 0.85, the per-water constant at its least, up to 0.9 by (1 - h.v)^4)
    float sunFresnel = 0.85 + 0.05 * pow(1 - saturate(dot(halfway, -view)), 4);
    float lobe = saturate(a2 / (3.14159 * dd * dd) * nl / (4 * nl * nv + 1e-4) * sunFresnel);
    // the game takes the sun's colour to the power 1/2.2 for it, a colour, not the sun's whole strength
    float3 sunTint = pow(saturate(LightColour.rgb / max(max(LightColour.r, LightColour.g), max(LightColour.b, 1e-4))), 0.4545);
    float3 glint = Screen(seen * sunTint * lobe * GlintScale * saturate(Light.z * 4) * lerp(0.04, 1, FogHeight.y), 1).rgb;
    // the foam as the game's water shader makes it, over all the water near the eye, not only the shore: its loop (32
    // frames, played at 0.024 a second) at 0.04 a metre, nudged by the waves, and again 15 times finer; the coarse loop
    // where the waves slope, fading out to 350 m, times the fine one to the power 2.2, plus the coarse one fading out
    // sooner, to the power 1.6; brightened by (2 + 18) x the environment's foam intensity + 0.5 and its diffuse scale in
    // light (ambient in shadow), and added to the water's own colour (a per-water constant we haven't placed is taken at
    // its least, 0.05)
    float frame = frac(Eye.w * 0.024) * 32;
    float2 foamUv = n.xy * 0.002 + i.World.xy * 0.04;
    float coarse = lerp(FoamLoop.Sample(Tiled, float3(foamUv, floor(frame))).r, FoamLoop.Sample(Tiled, float3(foamUv, floor(frame + 1) % 32)).r, frac(frame));
    float fine = lerp(FoamLoop.Sample(Tiled, float3(foamUv * 15, floor(frame))).r, FoamLoop.Sample(Tiled, float3(foamUv * 15, floor(frame + 1) % 32)).r, frac(frame));
    float reach = saturate((350 - length(i.World.xy - Eye.xy)) / 350);
    float foamA = coarse * 1.3 * saturate(length(n.xy) * 1.5) * pow(reach, 12) * pow(fine, 2.2)
        + pow(saturate(pow(reach, 1.2) * coarse), 2.6) * 0.4;
    float foamB = pow(foamA, 1.6);
    const float perWater = 0.05;
    float foam = (1 - 0.9 * perWater) * 20 * max(foamB, saturate(foamA) * (perWater * 0.7 + 0.3)) * lookScale * clamp(WaterLook.z + 0.5, 0, 20);
    body += Screen(foam.xxx, 1).rgb;

    // the water's colour taking over from what's under it, as the game's water shader has it: what's under it dimmed
    // to 0.6 of itself through none, then each channel moved to the water's colour by the water looked through, the red
    // first (0.5 of the way a metre, the green 0.32, the blue 0.34; the shader's red and blue take a constant we haven't
    // placed at its most)
    float lookedFog = looked * Water.w;
    under *= 0.6 - 0.267 * saturate(lookedFog);
    float3 colour = lerp(under, body, saturate(lookedFog * float3(0.5, 0.32, 0.34)));
    colour = lerp(colour, sky, fresnel) + glint;
    float3 fog, aerial;
    float2 amount = FogAt(i.World, 0, fog, aerial);
    colour = lerp(colour, Screen(fog * FogLimits.z, 1).rgb, amount.x);
    return Screen(colour, 1);
}

// ---- tone mapping and grading ----
// As the game's post-process shaders do it (its static shader cache): the exposure from the eye's adapted luminance,
// kept between the environment's limits and bent by its shape; then AgX: the colour into AgX's working space, its log
// between 10 stops under and 6.5 over the mid grey, through the curve (a toe and a shoulder of their own contrast
// powers either side of the mid grey's point, the toe lifted by the black point lift), the slope, power and saturation,
// and back out; then the balance maps (64 x 64 x 64 colour lookups as 8 x 8 tiles), mixed in by their amount; then the
// final colour balance's levels and its saturation and tint in the shadows, midtones and highlights.

Texture2D HdrScene : register(t10);
Texture2D<float> AdaptedLuminance : register(t16);

cbuffer Adaptation : register(b6)
{
    float4 AdaptRate; // x: how much of the way to the scene's luminance the eye goes this frame
};

// the eye's luminance moved towards the scene's average (the scene's smallest level, its alpha the luminance)
float AdaptPS(SkyOut i) : SV_Target
{
    float scene = HdrScene.Load(int3(0, 0, Balance2.w)).a;
    float before = AdaptedLuminance.Load(int3(0, 0, 0));
    return lerp(before, scene, saturate(AdaptRate.x));
}
Texture2D BalanceA : register(t11);
Texture2D BalanceB : register(t12);


float SignedPow(float x, float p) { return x == 0 ? 0 : sign(x) * pow(abs(x), p); }

float AgxCurve(float x)
{
    float pivot = 10 / 16.5, contrast = Curve.x;
    if (x >= pivot)
    {
        float shoulder = Curve.y, reach = (1 - pivot) * contrast;
        float scale = SignedPow(SignedPow(reach, -shoulder) * (SignedPow(2 * reach, shoulder) - 1), -1 / shoulder);
        float u = (x - pivot) * contrast / scale;
        return u / SignedPow(SignedPow(u, shoulder) + 1, 1 / shoulder) * scale + 0.5;
    }
    float toe = Curve.z, reach = contrast * pivot;
    float scale = SignedPow(SignedPow(reach, -toe) * (SignedPow(2 * reach, toe) - 1), -1 / toe) * (1 - Curve.w);
    float u = (x - pivot) * contrast / -scale;
    return -scale * (u / SignedPow(SignedPow(u, toe) + 1, 1 / toe)) + 0.5;
}

float3 Lookup(Texture2D map, float3 g)
{
    // the blue picks two neighbouring tiles, red and green the place in each, as the game's balance pass reads them
    float slices = g.b * 63.75, below = floor(slices), between = slices - below;
    float upper = min(0.99999, saturate(g.b * 0.9960938 + 0.015625)), lower = min(0.99999, saturate(below * 0.015625));
    float2 rg = clamp((saturate(g.rg) + 0.0078125) * 0.9960938, 0.015625, 0.984375);
    float upperRow = floor(upper * 8), lowerRow = floor(lower * 8);
    float2 a = float2(floor(upper * 64 - upperRow * 8) + rg.x, upperRow + rg.y) * 0.125;
    float2 b = float2(floor(lower * 64 - lowerRow * 8) + rg.x, lowerRow + rg.y) * 0.125;
    float3 high = map.SampleLevel(Smooth, a, 0).rgb, low = map.SampleLevel(Smooth, b, 0).rgb;
    return pow(abs(lerp(low, high, between)), 2.2);
}

float4 PostPS(SkyOut i) : SV_Target
{
    // the albedo view: as it was drawn, untouched by the exposure and the grading
    if (LightingView > 2.5) return float4(HdrScene.Load(int3(int2(i.Position.xy), 0)).rgb, 1);
    // the exposure from the eye's adaptation: the scene's average luminance (the smallest level), held between the limits
    float adapted = clamp(AdaptedLuminance.Load(int3(0, 0, 0)), Exposure.z, Exposure.w);
    float k = Exposure.x * 11.2;
    float exposure = Exposure.x / (pow(max(adapted, 0.0001) / k, Exposure.y) * k);
    float3 c = HdrScene.Load(int3(int2(i.Position.xy), 0)).rgb * Light.w * exposure;

    // AgX
    float low = log2(Power.w * 0.0009765625), high = log2(Power.w * 90.50967);
    float3 inset = float3(dot(c, float3(0.8424791, 0.0784336, 0.07922374)), dot(c, float3(0.04232824, 0.8784686, 0.07916613)),
        dot(c, float3(0.04237565, 0.0784336, 0.879143)));
    float3 x = (clamp(log2(max(inset, 1e-10)), low, high) - low) / (high - low);
    float3 y = pow(max(float3(AgxCurve(x.r), AgxCurve(x.g), AgxCurve(x.b)) * Slope.rgb, 0), Power.rgb);
    float grey = dot(y, float3(0.2126729, 0.7151522, 0.072175));
    y = max(lerp(grey, y, Slope.w), 0);
    float3 linear_ = pow(max(float3(dot(y, float3(1.196879, -0.09802088, -0.09902974)), dot(y, float3(-0.05289685, 1.151903, -0.09896117)),
        dot(y, float3(-0.05297164, -0.09804345, 1.151074))), 0.0001), 2.2);

    // the balance maps
    if (Balance.x > 0)
    {
        float3 g = pow(saturate(linear_), 0.4545455);
        float3 mapped = lerp(Lookup(BalanceA, g), Lookup(BalanceB, g), Balance.y);
        linear_ += (mapped - linear_) * Balance.x;
    }

    // the final colour balance: levels, then saturation and tint by how dark each pixel is
    float3 g = pow(saturate(linear_), 0.4545455);
    if (Balance2.y > 0)
    {
        g = pow(saturate((g - Balance.z) / max(Balance2.x - Balance.z, 0.001)), 1 / max(Balance.w, 0.001));
        float luma = dot(float3(0.299, 0.587, 0.114), g);
        float toMid = saturate((luma - (Midtones.x - Midtones.z)) / max(Midtones.z, 0.001));
        float toHigh = saturate((luma - Midtones.y) / max(Midtones.w, 0.001));
        float4 part = lerp(lerp(Low, Mid, toMid), High, toHigh);
        float3 lin = pow(g, 2.2);
        float l = dot(float3(0.2126, 0.7152, 0.0722), lin);
        g = pow(saturate((part.w * (lin - l) + l) * part.rgb), 0.4545455);
    }
    g = saturate(g * Balance2.z);
    // the vignette, as the game's final pass: darker towards the corners, sparing what's bright by the weights
    float2 fromMiddle = i.Position.xy / Camera.zw - 0.5;
    float ring = saturate((length(fromMiddle) * 2 - 0.55) * 1.219512), ring2 = ring * ring;
    float vignette = Vignette.w * min(dot(float4(-0.1, -0.105, 1.12, 0.09), float4(ring2 * ring2, ring2 * ring, ring2, ring)), 0.94)
        * saturate(1 - dot(pow(g, 2.2), Vignette.rgb));
    return float4(lerp(g, VignetteColour.rgb, saturate(vignette)), 1);
}

// ---- plants ----
// The SpeedTree files' LODs, each plant turned by its yaw and scaled, the facing leaves' cards spread across the camera
// (their first two corner values); far off, a side view from the billboard atlas, the one nearest the way the camera
// looks at the plant. Lit by the sun through both sides of a leaf, its ambient light dimmed by the vertex's occlusion.

struct PlantIn
{
    float3 Position : POSITION; float2 Uv : TEXCOORD0; float3 Normal : NORMAL; float4 Corner : TEXCOORD1; float Kind : TEXCOORD2;
    float4 Place : PLACE; float2 Turn : TURN;
};
// the kind: 0 leaves, 1 branches, 2 grass, 3 a billboard
struct PlantOut
{
    float4 Position : SV_Position; float3 World : TEXCOORD0; float2 Uv : TEXCOORD1; float3 Normal : TEXCOORD2; float Occlusion : TEXCOORD3;
    float Kind : TEXCOORD4; float3 Weights : TEXCOORD5; float3 Across : TEXCOORD6; float3 Upward : TEXCOORD7;
};

float3 Turned(float3 p, float2 turn) { return float3(turn.x * p.x - turn.y * p.y, turn.y * p.x + turn.x * p.y, p.z); }

// which of the three random colours a plant takes, from the way it faces, as the engine's SpeedTreeCalcRandomColorData
float3 RandomWeights(float2 turn)
{
    float f = turn.x * 0.5 + 0.5;
    return float3(1 - saturate(f * 3) * saturate((1 - f) * 3), saturate(1 - abs(f - 0.33333) * 3), saturate(1 - abs(f - 0.66666) * 3));
}

PlantOut PlantAt(PlantIn v, row_major float4x4 projection)
{
    PlantOut o;
    float3 local = Turned(v.Position, v.Turn) + CameraRight.xyz * v.Corner.x + CameraUp.xyz * v.Corner.y;
    o.World = v.Place.xyz + local * v.Place.w;
    o.Position = mul(float4(o.World, 1), projection);
    o.Uv = v.Uv;
    o.Normal = Turned(v.Normal, v.Turn);
    o.Occlusion = v.Corner.w;
    o.Kind = v.Kind;
    o.Weights = RandomWeights(v.Turn);
    o.Across = 0;
    o.Upward = 0;
    return o;
}

PlantOut PlantVS(PlantIn v) { return PlantAt(v, ViewProjection); }
PlantOut PlantShadowVS(PlantIn v) { return PlantAt(v, SunViewProjection); }

struct BillboardIn { float2 Corner : POSITION; float4 Place : PLACE; float2 Turn : TURN; };

PlantOut BillboardAt(BillboardIn v, float3 eye, row_major float4x4 projection)
{
    PlantOut o;
    // the view nearest the way the camera looks at the plant, counted round from the plant's own facing
    float2 toEye = eye.xy - v.Place.xy;
    float around = atan2(toEye.y, toEye.x) - atan2(v.Turn.y, v.Turn.x);
    float views = max(BillboardShape.w, 1);
    int view = (int)round(frac(around / 6.2831853) * views) % (int)views;
    float4 at = BillboardViews[view];
    float2 side = normalize(float2(-toEye.y, toEye.x) + float2(1e-5, 0));
    float height = lerp(BillboardShape.z, BillboardShape.y, v.Corner.y);
    o.World = v.Place.xyz + (float3(side * v.Corner.x * BillboardShape.x, height)) * v.Place.w;
    o.Position = mul(float4(o.World, 1), projection);
    // each view's rectangle starts at the plant's foot, its height negative going up the atlas; a turned view (both
    // sizes negative) runs the plant's height along the atlas's width
    o.Uv = at.z < 0 ? at.xy + float2(v.Corner.y * at.z, (v.Corner.x + 0.5) * at.w) : at.xy + float2((v.Corner.x + 0.5) * at.z, v.Corner.y * at.w);
    o.Normal = normalize(float3(normalize(toEye + float2(1e-5, 0)), 1));
    o.Occlusion = 1;
    o.Kind = 3;
    o.Weights = RandomWeights(v.Turn);
    // the world's ways along the atlas picture (its width, and up the picture), for the views' normal map; a turned
    // view runs the plant's height along the width
    float3 up = float3(0, 0, 1), across = float3(side, 0);
    o.Across = at.z < 0 ? -up : across * sign(at.z);
    o.Upward = at.z < 0 ? across * -sign(at.w) : up * -sign(at.w);
    return o;
}

PlantOut BillboardVS(BillboardIn v) { return BillboardAt(v, Eye.xyz, ViewProjection); }
PlantOut BillboardShadowVS(BillboardIn v) { return BillboardAt(v, v.Place.xyz + Light.xyz * 100, SunViewProjection); }

// the game's alpha cut: the picture's alpha times the kind's alpha scalar, kept from 0.1
float PlantAlphaScalar(float kind) { return kind > 2.5 ? AlphaScalars.z : kind > 1.5 ? AlphaScalars.y : AlphaScalars.x; }

// the engine's SpeedTreeApplyRandomColorAlbedo: towards the colour's luminance by the saturation, then times the colour
float3 RandomColour(float3 gamma, int k, float3 w)
{
    float4 c0 = k == 2 ? GrassRandom[0] : k == 1 ? BranchRandom[0] : TreeRandom[0];
    float4 c1 = k == 2 ? GrassRandom[1] : k == 1 ? BranchRandom[1] : TreeRandom[1];
    float4 c2 = k == 2 ? GrassRandom[2] : k == 1 ? BranchRandom[2] : TreeRandom[2];
    float4 merged = w.x * c0 + w.y * c1 + w.z * c2;
    if (abs(SpecularSettings.w - 1) < 0.5) merged = float4(1, 1, 1, 1);
    float lum = dot(RandomLuminance[k].rgb, gamma);
    float3 lin = pow(max(0, gamma), 2.2);
    return max(0, lum + (lin - lum) * merged.w) * merged.rgb;
}

float4 PlantPS(PlantOut i, bool front : SV_IsFrontFace) : SV_Target
{
    float4 c = PlantPicture.Sample(Tiled, i.Uv);
    // the cut as the game's SpeedTree shaders make it: the alpha times the render state's alpha scalar and the world's
    clip(c.a * PlantMat.w * PlantAlphaScalar(i.Kind) - 0.1);
    // the vertex normal on both sides of a card, as the game's SpeedTree shaders keep it (they never ask which side is
    // seen): a crown's leaves face out from it, so its sunny side is lit through and through
    float3 n = normalize(i.Normal);
    bool billboard = i.Kind > 2.5;
    if (billboard)
    {
        // the side views' own normal map, in the picture's ways and towards the camera
        float3 m = PlantNormals.Sample(Tiled, i.Uv).xyz;
        if (dot(m, m) > 0.01)
        {
            m = m * 2 - 1;
            float3 facing = normalize(float3(normalize(Eye.xy - i.World.xy + float2(1e-5, 0)), 0));
            n = normalize(m.x * i.Across + m.y * i.Upward + m.z * facing);
        }
    }
    // the trees' own leaves and branches take the fallback colour (the engine's SpeedTreeApplyRandomColorFallback), the
    // grass and the side views the random ones; the environment's SpeedTree diffuse then on the colour in gamma, as the
    // game's SpeedTree shaders apply it
    // (the side views by the tree's tag, as the game's billboard shader picks them, times the billboards' colour and not
    // the diffuse)
    int set = (int)BillboardStyle.x;
    float3 albedo = i.Kind < 1.5 ? pow(saturate(c.rgb), 2.2) * TreeFallback.rgb * pow(PlantTint.rgb, 2.2)
        : i.Kind < 2.5 ? RandomColour(saturate(c.rgb), 2, i.Weights) * pow(PlantTint.rgb, 2.2)
        : (set > 2 ? pow(saturate(c.rgb), 2.2) : RandomColour(saturate(c.rgb), set, i.Weights)) * BillboardTint.rgb;
    albedo *= saturate(PlantTint.w * (i.Occlusion - 1) + 1);
    if (i.Kind > 1.5 && i.Kind < 2.5 && PlantFlags.x > 0.5)
    {
        // the grass's pigment, as the game's grass shader has it: its colour drawn towards the ground's under it (its
        // brightness times the pigment's brightness), the more the lower on the plant and the further off, past the
        // flood's start over its range (in gamma, as the shader works)
        float up = saturate((i.Kind - 2) / 0.45);
        float amount = saturate((1 - up) * Pigment.w + saturate((length(Eye.xyz - i.World) - Pigment.y) / max(Pigment.z, 1e-4)));
        // the world's SpeedTree parameters' grass albedo multiplier, on the colour in gamma as the shader has it
        float3 g = pow(saturate(albedo), 1 / 2.2) * GrassAlbedo;
        float3 pigmented = saturate(dot(g, float3(0.3, 0.5, 0.2))) * Pigment.x * TerrainPigment(i.World.xy);
        albedo = pow(saturate(lerp(g, pigmented, amount)), 2.2);
    }
    float3 V = normalize(Eye.xyz - i.World), L = Light.xyz;
    float sun = SunSeen(i.World);
    bool leafy = !(i.Kind > 0.5 && i.Kind < 1.5);
    bool grass = i.Kind > 1.5 && i.Kind < 2.5;
    // grass is lit by the ground's normal under it (the remaster's UseTerrainNormalDiffuseGrass, at full strength), its
    // specular by the card's normal three quarters of the way to the ground's (TerrainNormalSpecGrassSlider 0.75)
    float3 ground = GroundNormal(i.World.xy);
    float3 specularNormal = grass ? normalize(lerp(n, ground, 0.75)) : n;
    if (grass) n = ground;
    float LoV = dot(L, V);
    // the engine's diffuse (include_constants.fx, DiffuseLightingPBRPipeline): grass by its approximated foliage model,
    // |L.N| to the power 0.3 (ApproximatedDiffuseCurvePower) dimmed as the eye looks towards the sun; the rest Lambert;
    // a tree's leaf cards keep their own facing, so a canopy's side away from the sun is lit only through the leaves
    float3 diffuse = (grass ? pow(abs(dot(L, n)), 0.3) * saturate(LoV + 0.5) * albedo : saturate(dot(n, L))) / 3.14159265;
    // the translucency as the game's SpeedTree shader writes it: the specular map's alpha times the transmission colour,
    // times the environment's translucency scale; the specularity its green times the specular colour (none without a map)
    float4 specMap = PlantMat.z >= 0 && i.Kind < 2.5 ? PlantSpecular.Sample(Tiled, i.Uv) : float4(0, 0, 0, 1);
    // (the side views by the environment's billboards translucency)
    // (grass's by how high up the blade, as the game's grass shader has it: 0.5 + 0.5 saturate(height x 0.5 x the world's
    // grass translucency multiplier, taken as 1))
    float grassHeight = 0.5 + 0.5 * saturate(saturate((i.Kind - 2) / 0.45) * 0.5);
    float translucent = leafy ? (i.Kind < 2.5 ? TranslucencyGlobal.x * specMap.a * PlantMat.y * (i.Kind > 1.5 ? grassHeight : 1) : TranslucencyGlobal.y) : 0;
    // (the grass and the bushes its brushes grow take no specular at all: measured against REDkit, the sheen, even
    // the grazing one, is what made them pale)
    float specularity = i.Kind < 1.5 ? specMap.g * PlantMat.x : 0;
    // the shininess over 128 goes where the game's lighting reads the roughness
    float glossiness = PlantMat.z >= 0 ? PlantMat.z : 0.234375;
    float3 transl = 0;
    if (translucent > 0)
    {
        // the remaster's translucency (UseNewTranslucency, on by default): the light through the leaf, strongest looking
        // into the sun, masked by how squarely the light meets the card; coloured by the albedo by the saturation
        float mask = pow(abs(dot(L, n)), TranslucencyCurve.z);
        float scattering = exp(-(LoV + TranslucencyCurve.x)) + exp(-3 * (LoV + TranslucencyCurve.y));
        float3 tone = grass ? saturate(lerp(dot(albedo, float3(0.2125, 0.7154, 0.0721)), albedo, TranslucencyCurve.w)) : 1;
        diffuse += mask * scattering * translucent * tone / 3.14159265;
    }
    float ao = pow(AoAt(i.Position), grass ? WaterSettings.z : WaterSettings.w), directAo = lerp(1, ao, AoSettings.w);
    float f90 = i.Kind > 1.5 && i.Kind < 2.5 ? SpecularSettings.x : SpecularSettings.y;
    float3 sunLight = GlobalLightColour(i.World) * sun;
    // the ambient by the leaf's own facing (the side seen), grass's by the ground under it
    float3 an = grass ? ground : n;
    // (the approximated model has the albedo in it already)
    float sky = CanopySky(i.World);
    float3 lit = (grass ? sunLight * diffuse * directAo : albedo * sunLight * diffuse * directAo) + albedo * AmbientAt(an, sun) * ao * sky * (grass ? CanopySettings.z : 1)
        + (grass ? 0 : sunLight * SpecularAt(specularNormal, V, L, specularity, clamp(glossiness, 0.05, 1), f90) * SpecularSettings.z * directAo);
    float3 ambientPart = albedo * AmbientAt(an, sun) * ao * sky * (grass ? CanopySettings.z : 1);
    if (LightingView > 3.5) return float4(ao.xxx, 1);
    if (LightingView > 2.5) return float4(pow(saturate(albedo), 1 / 2.2), 1);
    return Screen(Fogged(LightingPart(lit, ambientPart), i.World), 1);
}

void PlantShadowPS(PlantOut i)
{
    clip(PlantPicture.Sample(Tiled, i.Uv).a * PlantMat.w * PlantAlphaScalar(i.Kind) - 0.1);
}

// ---- the ambient cube ----
// The game lights with environment probes: cube maps of the scene round where each was taken, relit as the light
// changes. Ours: each direction's half of the sphere averaged by the cosine, from the world's own cooked probe where
// there is one (else our capture round the eye), the probes' sky (the environment's probe sky colours) wherever it
// saw no scene; the scene by the IBL settings' ground brightness and saturation, plus the scene add.
Texture2DArray ProbeColour : register(t25);
Texture2DArray<float> ProbeDepth : register(t26);

cbuffer ProbeFaces : register(b11)
{
    row_major float4x4 ProbeFace[7]; // the capture's six faces' views; the seventh's first row where it was taken (w 1 once it was)
};

// what the probe's capture saw along a direction: its colour, and whether anything was there (0 for the sky)
float4 ProbeSeen(float3 dir)
{
    float3 a = abs(dir);
    int f = a.x >= a.y && a.x >= a.z ? (dir.x >= 0 ? 0 : 1) : a.y >= a.z ? (dir.y >= 0 ? 2 : 3) : (dir.z >= 0 ? 4 : 5);
    float4 clip = mul(float4(ProbeFace[6][0].xyz + dir, 1), ProbeFace[f]);
    float2 uv = saturate(float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5));
    float depth = ProbeDepth.SampleLevel(Smooth, float3(uv, f), 0);
    return float4(ProbeColour.SampleLevel(Smooth, float3(uv, f), 0).rgb, depth < 1 ? 1 : 0);
}

// the world's own environment probe, as cooking left it in the texture cache: six 128 x 128 faces of the scene round
// where it was taken, its albedo (two-channel YCbCr, the rows taking turns at blue and red), normals, the sun's shadow
// and depth (1 - 0.5 / the distance along the face's axis)
Texture2DArray<float4> CookedAlbedo : register(t27);
Texture2DArray<float4> CookedNormals : register(t28);
Texture2DArray<float> CookedDepth : register(t29);

cbuffer CookedProbe : register(b12)
{
    float4 CookedAt;    // where it was taken; w 1 when there's one
    float4 CookedGen;   // its generation parameters' ambient colour times its ambient intensity; the intensity
};

// the face and texel a direction falls on, as the engine's probe unpacking finds them
int3 CookedTexel(float3 d)
{
    float3 a = abs(d);
    int face;
    float2 uv;
    if (a.x > max(a.y, a.z)) { face = d.x < 0 ? 0 : 1; uv = float2(d.x < 0 ? d.z / a.x * 0.5 + 0.5 : 0.5 - d.z / a.x * 0.5, d.y / a.x * 0.5 + 0.5); }
    else if (a.y >= max(a.x, a.z)) { face = d.y > 0 ? 3 : 2; uv = float2(0.5 - d.x / a.y * 0.5, d.y > 0 ? d.z / a.y * 0.5 + 0.5 : 0.5 - d.z / a.y * 0.5); }
    else { face = d.z > 0 ? 5 : 4; uv = float2(d.z > 0 ? d.x / a.z * 0.5 + 0.5 : 0.5 - d.x / a.z * 0.5, d.y / a.z * 0.5 + 0.5); }
    return int3(clamp((int2)(uv * 128), 0, 127), face);
}

// what the world's probe saw along a direction, lit for now: the scene's albedo (decoded as the engine does, then
// linear) under the sun where it reaches that point and the ambient, as the probes' relighting does; w 0 for the sky
float4 CookedSeen(float3 dir)
{
    int3 t = CookedTexel(dir);
    float depth = CookedDepth.Load(int4(t, 0));
    if (depth >= 1) return 0;
    float4 here = CookedAlbedo.Load(int4(t, 0));
    // the chroma: blue on even rows, red on odd, each texel taking the other from its neighbour
    float other = CookedAlbedo.Load(int4(t.x, (t.y & 1) == 0 ? t.y + 1 : t.y - 1, t.z, 0)).y;
    float cb = (t.y & 1) == 0 ? here.y : other, cr = (t.y & 1) == 0 ? other : here.y;
    float3 albedo = saturate(float3(here.x + 1.402 * (cr - 0.5), here.x - 0.344 * (cb - 0.5) - 0.714 * (cr - 0.5), here.x + 1.772 * (cb - 0.5)));
    albedo = pow(albedo, 2.2);
    float3 n = normalize(CookedNormals.Load(int4(t, 0)).xyz * 2 - 1);
    float3 a = abs(dir);
    float3 world = CookedAt.xyz + dir / max(a.x, max(a.y, a.z)) * (0.5 / max(1 - depth, 1e-5));
    float sun = SunSeen(world);
    // as the engine's probe relighting (static shader 0017) has it: the albedo under the sun and the local lights only,
    // no ambient
    float3 lit = albedo * GlobalLightColour(world) * saturate(dot(n, Light.xyz)) * sun / 3.14159265;
    return float4(lit, 1);
}

float4 AmbientPS(float4 position : SV_Position) : SV_Target
{
    bool cooked = CookedAt.w > 0.5;
    bool captured = !cooked && ProbeFace[6][0].w > 0.5;
    int face = (int)position.x;
    float3 axis = face == 0 ? float3(1, 0, 0) : face == 1 ? float3(-1, 0, 0) : face == 2 ? float3(0, 1, 0)
        : face == 3 ? float3(0, -1, 0) : face == 4 ? float3(0, 0, 1) : float3(0, 0, -1);
    float3 up = abs(axis.z) > 0.9 ? float3(1, 0, 0) : float3(0, 0, 1);
    float3 across = normalize(cross(up, axis)), along = cross(axis, across);
    // the ground's light: the sun on ground of an average colour, as seen from above
    float3 ground = 0.18 * (LightColour.rgb * saturate(Light.z) / 3.14159265) * SkyIbl.z;
    float3 sum = 0;
    float weights = 0;
    [loop] for (int a = 0; a < 16; a++)
        [loop] for (int b = 0; b < 16; b++)
        {
            // a cosine-weighted spread of directions round the axis
            float u = (a + 0.5) / 16, v = (b + 0.5) / 16;
            float r = sqrt(u), phi = v * 6.2831853;
            float3 dir = normalize(axis * sqrt(1 - u) + (across * cos(phi) + along * sin(phi)) * r);
            float3 c;
            float4 seen = cooked ? CookedSeen(dir) : captured ? ProbeSeen(dir) : 0;
            if (seen.a > 0.5)
            {
                // the scene the capture saw there (the ground, the trees), by the ground's brightness and saturation
                float l = dot(seen.rgb, float3(0.2126, 0.7152, 0.0722));
                c = lerp(l, seen.rgb, SkyIbl.w) * SkyIbl.z + SceneAdd.rgb;
            }
            else if (dir.z >= 0 || captured || cooked)
            {
                // the probes see the sky at its own brightness, not the visible sky's extra scale
                // the probes are made from the environment's own probe sky
                c = ProbeSky(dir);
            }
            else
            {
                float l = dot(ground, float3(0.2126, 0.7152, 0.0722));
                c = lerp(l, ground, SkyIbl.w) + SceneAdd.rgb;
            }
            sum += c;
            weights += 1;
        }
    return float4(sum / weights, 1);
}

// ---- ambient occlusion ----
// As the engine's HBAO+ (the environment's m_ssaoNV settings): round each pixel, from the depth already drawn, the
// points within the radius in eight directions, how far each rises over the surface (less the bias), faded with
// distance; the light left raised to the power.
float3 WorldAtPixel(int2 pixel)
{
    float stored = DepthMap.Load(int3(pixel, 0));
    float2 ndc = float2((pixel.x + 0.5) / Camera.z * 2 - 1, 1 - (pixel.y + 0.5) / Camera.w * 2);
    float4 w = mul(float4(ndc, stored, 1), Unproject);
    return w.xyz / w.w;
}

float AoPS(float4 position : SV_Position) : SV_Target
{
    int2 pixel = int2(position.xy);
    float stored = DepthMap.Load(int3(pixel, 0));
    if (stored >= 1) return 1;
    float3 p = WorldAtPixel(pixel);
    float3 dx1 = WorldAtPixel(pixel + int2(1, 0)) - p, dx2 = p - WorldAtPixel(pixel - int2(1, 0));
    float3 dy1 = WorldAtPixel(pixel + int2(0, 1)) - p, dy2 = p - WorldAtPixel(pixel - int2(0, 1));
    float3 dx = dot(dx1, dx1) < dot(dx2, dx2) ? dx1 : dx2, dy = dot(dy1, dy1) < dot(dy2, dy2) ? dy1 : dy2;
    float3 n = normalize(cross(dy, dx));
    if (dot(n, Eye.xyz - p) < 0) n = -n;
    float depth = ViewDepth(stored);
    // the radius in pixels at this depth (a 60 degree view)
    float radius = AoSettings.x, pixels = radius * Camera.w * 0.5 / (0.57735 * depth);
    if (pixels < 1) return 1;
    float occlusion = 0;
    [unroll] for (int d = 0; d < 8; d++)
    {
        float angle = d * 0.785398 + 0.39;
        float2 step = float2(cos(angle), sin(angle)) * pixels / 4;
        [unroll] for (int k = 1; k <= 4; k++)
        {
            int2 at = pixel + int2(step * k);
            if (any(at < 0) || at.x >= (int)Camera.z || at.y >= (int)Camera.w) continue;
            float3 v = WorldAtPixel(at) - p;
            float dist2 = dot(v, v);
            occlusion += saturate(dot(n, v) * rsqrt(max(dist2, 1e-6)) - AoSettings.y) * saturate(1 - dist2 / (radius * radius));
        }
    }
    return pow(saturate(1 - occlusion / 32 * 2), AoSettings.z * 0.5);
}

// ---- meshes ----
// The layers' meshes, each copy placed by its matrix (four rows, as instance data), lit as the ground: the sun with its
// shadows, the probes' ambient under the canopy, GGX specular, the sky's reflection, the fog. Materials as the mesh
// viewer reads them: roughness in the normal map's alpha between the least and most, specularity by RSpecScale and
// RSpecBase from it, the tint mask's red baked occlusion.

cbuffer MeshMaterial : register(b9)
{
    float4 MeshSpec;       // RSpecScale, RSpecBase, the least and most roughness
    float4 MeshSpecColour; // the specular colour (linear); how much of the tint mask's occlusion counts
    float4 MeshFlags;      // a normal map, a tint mask; the alpha cut (0 for none)
    float4 MeshSpare;
};

Texture2D MeshDiffuse : register(t21);
Texture2D MeshNormal : register(t22);
Texture2D MeshMask : register(t23);

struct MeshIn
{
    float3 Position : POSITION; float3 Normal : NORMAL; float4 Tangent : TANGENT; float2 Uv : TEXCOORD0;
    float4 Row0 : WORLD0; float4 Row1 : WORLD1; float4 Row2 : WORLD2; float4 Row3 : WORLD3;
};
struct MeshOut { float4 Position : SV_Position; float3 World : TEXCOORD0; float2 Uv : TEXCOORD1; float3 Normal : TEXCOORD2; float4 Tangent : TEXCOORD3; };

MeshOut MeshAt(MeshIn v, row_major float4x4 projection)
{
    float4x4 place = float4x4(v.Row0, v.Row1, v.Row2, v.Row3);
    MeshOut o;
    o.World = mul(float4(v.Position, 1), place).xyz;
    o.Position = mul(float4(o.World, 1), projection);
    o.Uv = v.Uv;
    o.Normal = mul(v.Normal, (float3x3)place);
    o.Tangent = float4(mul(v.Tangent.xyz, (float3x3)place), v.Tangent.w);
    return o;
}

MeshOut MeshVS(MeshIn v) { return MeshAt(v, ViewProjection); }
MeshOut MeshShadowVS(MeshIn v) { return MeshAt(v, SunViewProjection); }

float4 MeshPS(MeshOut i, bool front : SV_IsFrontFace) : SV_Target
{
    float4 c = MeshDiffuse.Sample(Tiled, i.Uv);
    if (MeshFlags.z > 0) clip(c.a - MeshFlags.z);
    float3 albedo = pow(saturate(c.rgb), 2.2);
    float3 n = normalize(front ? i.Normal : -i.Normal);
    float roughness = 0.7;
    if (MeshFlags.x > 0)
    {
        // the normal map's x and y, z worked out, in the surface's tangent space (the bitangent by the tangent's handedness)
        float4 m = MeshNormal.Sample(Tiled, i.Uv);
        float2 xy = m.xy * 2 - 1;
        float3 t = normalize(i.Tangent.xyz - n * dot(n, i.Tangent.xyz) + 1e-5), b = cross(n, t) * (i.Tangent.w < 0 ? -1 : 1);
        n = normalize(xy.x * t + xy.y * b + sqrt(saturate(1 - dot(xy, xy))) * n);
        roughness = m.a;
    }
    roughness = clamp(lerp(MeshSpec.z, MeshSpec.w, roughness), 0.05, 1);
    float3 specularity = saturate(MeshSpecColour.rgb * ((1 - roughness) * MeshSpec.x + MeshSpec.y));
    float baked = MeshFlags.y > 0 ? lerp(1, MeshMask.Sample(Tiled, i.Uv).r, saturate(MeshSpecColour.w)) : 1;
    float sun = SunSeen(i.World), ao = AoAt(i.Position) * baked, sky = CanopySky(i.World);
    float3 v = normalize(Eye.xyz - i.World), sunLight = GlobalLightColour(i.World) * sun;
    float direct = lerp(1, ao, AoSettings.w);
    float3 lit = albedo * (sunLight * saturate(dot(n, Light.xyz)) / 3.14159265 * direct + AmbientAt(n, sun) * ao * sky)
        + sunLight * SpecularAt(n, v, Light.xyz, dot(specularity, 0.3333), roughness, 1) * direct
        + ReflectionAt(n, v, dot(specularity, 0.3333), roughness, sun) * ao * sky;
    return Screen(Fogged(lit, i.World), 1);
}

// ---- stripes ----
// The layers' stripes (paths and roads over the ground): the game's cooked triangles, lifted a little off the ground so
// they're drawn over it, lit as the ground under them (its normal, bent by the stripe's normal map along the way it
// runs), blended in by their texture's alpha and their vertices' (clear at the ends).

struct StripeIn { float3 Position : POSITION; float2 Uv : TEXCOORD0; float Alpha : TEXCOORD1; float3 Along : TEXCOORD2; };
struct StripeOut { float4 Position : SV_Position; float3 World : TEXCOORD0; float2 Uv : TEXCOORD1; float Alpha : TEXCOORD2; float3 Along : TEXCOORD3; };

StripeOut StripeVS(StripeIn v)
{
    StripeOut o;
    o.World = v.Position + float3(0, 0, 0.04);
    o.Position = mul(float4(o.World, 1), ViewProjection);
    o.Uv = v.Uv;
    o.Alpha = v.Alpha;
    o.Along = v.Along;
    return o;
}

float4 StripePS(StripeOut i) : SV_Target
{
    float4 c = MeshDiffuse.Sample(Tiled, i.Uv);
    float alpha = saturate(c.a * i.Alpha);
    clip(alpha - 0.01);
    float3 albedo = pow(saturate(c.rgb), 2.2);
    float3 n = GroundNormal(i.World.xy);
    float4 m = MeshNormal.Sample(Tiled, i.Uv);
    if (dot(m.rgb, m.rgb) > 0.01)
    {
        float3 b = normalize(i.Along - n * dot(n, i.Along) + 1e-5), t = cross(b, n);
        float2 xy = m.xy * 2 - 1;
        n = normalize(xy.x * t + xy.y * b + sqrt(saturate(1 - dot(xy, xy))) * n);
    }
    float sun = SunSeen(i.World), ao = AoAt(i.Position), sky = CanopySky(i.World);
    float3 v = normalize(Eye.xyz - i.World), sunLight = GlobalLightColour(i.World) * sun;
    float direct = lerp(1, ao, AoSettings.w);
    float3 lit = albedo * (sunLight * saturate(dot(n, Light.xyz)) / 3.14159265 * direct + AmbientAt(n, sun) * ao * sky)
        + sunLight * SpecularAt(n, v, Light.xyz, 0.02, 0.8, 0.3) * direct;
    return float4(Screen(Fogged(lit, i.World), 1).rgb, alpha);
}
";
    // loc-ignore-end

    /// <summary>Samples a patch spans, and the finest it's drawn with; coarser ones halve it, as far as 8.</summary>
    private const int PatchSamples = 128;
    private static readonly int[] Steps = [1, 2, 4, 8, 16];

    private VertexShader? _vs, _skyVS, _waterVS;
    private PixelShader? _ps, _skyPS, _waterPS;
    private BlendState? _waterBlend;
    private DepthStencilState? _noDepth, _depthTestOnly;
    private ShaderResourceView? _waterLevel, _foam, _waves;
    // REDkit's camera: 0.2 m to 7 km, a vertical field of view of 60 degrees
    private const float NearPlane = 0.2f, FarPlane = 7000;
    private bool _hasWater;
    private float _waterTop;
    private WorldLighting? _lighting;
    private bool _reflections = true;
    private float _hour = 12;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private InputLayout? _layout;
    private Buffer? _frame, _textureSettings, _grading, _sky, _lightColour, _foliage;
    private Texture2D? _ambientCube;
    private RenderTargetView? _ambientTarget;
    private ShaderResourceView? _ambientView;
    private PixelShader? _ambientPS, _aoPS;
    private Texture2D? _ao;
    private RenderTargetView? _aoTarget;
    private ShaderResourceView? _aoView;

    /// <summary>The ambient occlusion's texture, the size of the view.</summary>
    private void EnsureAo()
    {
        if (_ao is { } ao && ao.Description.Width == PixelWidth && ao.Description.Height == PixelHeight) return;
        _aoView?.Dispose();
        _aoTarget?.Dispose();
        _ao?.Dispose();
        _ao = new Texture2D(Device, new Texture2DDescription
        {
            Width = Math.Max(1, PixelWidth), Height = Math.Max(1, PixelHeight), MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _aoTarget = new RenderTargetView(Device, _ao);
        _aoView = new ShaderResourceView(Device, _ao);
    }
    private PixelShader? _postPS, _shadowPS;
    private Texture2D? _shadowTexture;
    private RenderTargetView? _shadowTarget;
    private ShaderResourceView? _shadow;
    private Vector3 _shadowFor;
    private bool _shadowMade;
    private ShaderResourceView? _balanceA, _balanceB;
    private SamplerState? _sampler;
    private RasterizerState? _raster;
    private DepthStencilState? _depthState;
    private readonly (Buffer Vertices, Buffer Indices, int Count)?[] _grids = new (Buffer, Buffer, int)?[Steps.Length];
    private Buffer? _patches;
    private ShaderResourceView? _heightMap, _colourMap, _controlMap, _diffuse, _normals;
    private SamplerState? _tiled;

    private WorldTerrain? _terrain;
    private ushort[] _heights = [];
    private int _side;
    private float[,] _patchLow = new float[0, 0], _patchHigh = new float[0, 0];

    private Vector3 _eye;
    private float _yaw, _pitch = -0.35f;
    // the flying speed as REDkit shows it: a multiplier, 1 being a slow walk (SpeedUnit metres a second)
    private float _speed = 1;
    private const float SpeedUnit = 2;
    private readonly HashSet<Key> _held = [];
    private Point _lastMouse;
    private bool _looking;

    /// <summary>Raised as the camera moves: where it is and which way it looks (degrees from north, clockwise).</summary>
    public event EventHandler<(Vector3 Eye, float Heading, float Speed)>? CameraMoved;

    /// <summary>How many patches the last frame drew.</summary>
    public int PatchesDrawn { get; private set; }

    /// <summary>Whether it has terrain to show.</summary>
    public bool HasTerrain => _terrain is not null;

    public WorldView3D()
    {
        BackBrush = System.Windows.Media.Brushes.Black;
        HighDynamicRange = true;
    }

    /// <summary>
    /// The terrain to show: the whole world's heights, control map and colour map, rows from the north, as the 2D map
    /// read them, and the textures painted with (the terrain material's diffuse and normal arrays) when they were found.
    /// </summary>
    public void ShowTerrain(WorldTerrainPictures pictures, (Core.Textures.TextureInfo Info, byte[] Pixels)? diffuse,
        (Core.Textures.TextureInfo Info, byte[] Pixels)? normals)
    {
        EnsureDevice();
        _terrain = pictures.Terrain;
        _heights = pictures.Heights;
        _control = pictures.Control;
        _side = pictures.Side;
        _heightMap?.Dispose();
        _colourMap?.Dispose();
        _heightMap = Texture(_heights, _side, _side, DxgiFormat.R16_UNorm, 2);
        // the shadows, a sample of the height map each, made when first drawn
        _shadow?.Dispose();
        _shadowTarget?.Dispose();
        _shadowTexture?.Dispose();
        _shadowTexture = new Texture2D(Device, new Texture2DDescription
        {
            Width = _side, Height = _side, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _shadowTarget = new RenderTargetView(Device, _shadowTexture);
        _shadow = new ShaderResourceView(Device, _shadowTexture);
        _shadowMade = false;
        _colourMap = Texture(pictures.Colour, _side, _side, DxgiFormat.B8G8R8A8_UNorm, 4);
        _controlMap?.Dispose();
        _diffuse?.Dispose();
        _normals?.Dispose();
        _controlMap = Integers(pictures.Control, _side);
        // each texture's settings, val then val2, as the terrain shader's TextureSettings
        var settings = new Vector4[64];
        for (int i = 0; i < Math.Min(31, _terrain.TextureParams.Count); i++)
            (settings[2 * i], settings[2 * i + 1]) = _terrain.TextureParams[i];
        Context!.UpdateSubresource(settings, _textureSettings);
        _diffuse = diffuse is { } d ? TextureArray(d.Info, d.Pixels) : null;
        _normals = normals is { } n ? TextureArray(n.Info, n.Pixels) : null;
        // each patch's lowest and highest point, to know when it's in view
        int patches = Math.Max(1, _side / PatchSamples);
        _patches?.Dispose();
        _patches = new Buffer(Device, Utilities.SizeOf<Patch>() * patches * patches, ResourceUsage.Dynamic, BindFlags.VertexBuffer, CpuAccessFlags.Write,
            ResourceOptionFlags.None, 0);
        (_patchLow, _patchHigh) = (new float[patches, patches], new float[patches, patches]);
        for (int py = 0; py < patches; py++)
            for (int px = 0; px < patches; px++)
            {
                ushort low = ushort.MaxValue, high = 0;
                for (int y = py * PatchSamples; y <= Math.Min(_side - 1, (py + 1) * PatchSamples); y++)
                    for (int x = px * PatchSamples; x <= Math.Min(_side - 1, (px + 1) * PatchSamples); x++)
                    {
                        ushort h = _heights[y * _side + x];
                        if (h < low) low = h;
                        if (h > high) high = h;
                    }
                // patches counted from the south, as the world
                _patchLow[patches - 1 - py, px] = _terrain.Metres(low);
                _patchHigh[patches - 1 - py, px] = _terrain.Metres(high);
            }
        Invalidate();
    }

    /// <summary>The world's sky and light through the day.</summary>
    public void SetLighting(WorldLighting? lighting)
    {
        _lighting = lighting;
        RefreshAmbient();
        Invalidate();
    }

    /// <summary>The hour shown, 0 to 24.</summary>
    public float Hour
    {
        get => _hour;
        set
        {
            _hour = ((value % 24) + 24) % 24;
            Invalidate();
        }
    }

    /// <summary>Whether the day goes by on its own, an hour every <see cref="SecondsAnHour"/> seconds.</summary>
    public bool PlayingDay
    {
        get => _playing;
        set
        {
            _playing = value;
            Animating = value || _hasWater;
        }
    }
    private bool _playing;

    public float SecondsAnHour { get; set; } = 2;

    /// <summary>Raised as the day goes by on its own, with the hour.</summary>
    public event EventHandler<float>? HourChanged;

    /// <summary>
    /// The water: a level over the whole terrain, the engine's 0 except inside each water shape (a CWaterComponent's
    /// outline, at its height), kept as a texture the water's grid reads its height from.
    /// </summary>
    public void SetWater(IEnumerable<(IReadOnlyList<Vector2> Outline, float Level)> shapes, IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)>? surfaces = null)
    {
        if (_terrain is not { } terrain) return;
        EnsureDevice();
        const int side = 1024;
        float cell = terrain.Size / side;
        var levels = new float[side * side];
        foreach (var (outline, level) in shapes)
        {
            if (outline.Count < 3) continue;
            float minX = outline.Min(p => p.X), maxX = outline.Max(p => p.X), minY = outline.Min(p => p.Y), maxY = outline.Max(p => p.Y);
            int x0 = Math.Max(0, (int)((minX - terrain.Corner.X) / cell)), x1 = Math.Min(side - 1, (int)((maxX - terrain.Corner.X) / cell));
            int y0 = Math.Max(0, (int)((minY - terrain.Corner.Y) / cell)), y1 = Math.Min(side - 1, (int)((maxY - terrain.Corner.Y) / cell));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (Inside(outline, new Vector2(terrain.Corner.X + (x + 0.5f) * cell, terrain.Corner.Y + (y + 0.5f) * cell)))
                        // rows from the north, as the terrain's textures
                        levels[(side - 1 - y) * side + x] = level;
        }
        // water meshes (a river's surface, drawn with the game's water shaders): their triangles' heights, the highest
        // where they overlap
        foreach (var (a, b, c) in surfaces ?? [])
        {
            float minX = MathF.Min(a.X, MathF.Min(b.X, c.X)), maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
            float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            int x0 = Math.Max(0, (int)((minX - terrain.Corner.X) / cell)), x1 = Math.Min(side - 1, (int)((maxX - terrain.Corner.X) / cell));
            int y0 = Math.Max(0, (int)((minY - terrain.Corner.Y) / cell)), y1 = Math.Min(side - 1, (int)((maxY - terrain.Corner.Y) / cell));
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-6f) continue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = terrain.Corner.X + (x + 0.5f) * cell, py = terrain.Corner.Y + (y + 0.5f) * cell;
                    float u = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / d, v = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / d;
                    if (u < -0.01f || v < -0.01f || u + v > 1.01f) continue;
                    float z = u * a.Z + v * b.Z + (1 - u - v) * c.Z;
                    int at = (side - 1 - y) * side + x;
                    // (never below the level already there: the global water at 0 stays under a river lower than it)
                    levels[at] = MathF.Max(levels[at], z);
                }
        }
        _waterLevel?.Dispose();
        var handle = GCHandle.Alloc(levels, GCHandleType.Pinned);
        try
        {
            using var texture = new Texture2D(Device, new Texture2DDescription
            {
                Width = side, Height = side, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R32_Float,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new DataRectangle(handle.AddrOfPinnedObject(), side * 4));
            _waterLevel = new ShaderResourceView(Device, texture);
        }
        finally { handle.Free(); }
        _hasWater = true;
        _waterTop = levels.Max();
        // the shadows start from the water's surface, so they're made again
        _shadowMade = false;
        Animating = true;
        Invalidate();
    }

    private static bool Inside(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            if ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)
                && point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>The game's foam loop (environment\water\global_ocean\ocean_control.texarray: 32 frames), as the texture cache keeps it.</summary>
    public void SetFoam((Core.Textures.TextureInfo Info, byte[] Pixels)? foam)
    {
        EnsureDevice();
        _foam?.Dispose();
        _foam = foam is { } f ? TextureArray(f.Info, f.Pixels) : null;
        Invalidate();
    }

    /// <summary>Whether the painted textures are shown (else the colour map alone).</summary>
    public bool Painted => _diffuse is not null && _normals is not null;

    /// <summary>The control map as whole numbers, read a sample at a time (no smoothing between two textures' numbers).</summary>
    private ShaderResourceView Integers(ushort[] values, int side)
    {
        var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            using var texture = new Texture2D(Device, new Texture2DDescription
            {
                Width = side, Height = side, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R16_UInt,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, new DataRectangle(handle.AddrOfPinnedObject(), side * 2));
            return new ShaderResourceView(Device, texture);
        }
        finally { handle.Free(); }
    }

    /// <summary>Puts the camera over a point of the world, looking north and down.</summary>
    public void LookAt(Vector2 point)
    {
        float ground = GroundAt(point);
        _eye = new Vector3(point.X, point.Y - 120, ground + 90);
        (_yaw, _pitch) = (0, -0.5f);
        Moved();
    }

    /// <summary>The ground's height at a point, from the heights read.</summary>
    public float GroundAt(Vector2 point)
    {
        if (_terrain is not { } t || _side == 0) return 0;
        float spacing = t.Size / _side;
        int x = Math.Clamp((int)((point.X - t.Corner.X) / spacing), 0, _side - 1);
        int y = Math.Clamp(_side - 1 - (int)((point.Y - t.Corner.Y) / spacing), 0, _side - 1);
        return t.Metres(_heights[y * _side + x]);
    }

    private void Moved()
    {
        Invalidate();
        CameraMoved?.Invoke(this, (_eye, (_yaw * 180 / MathF.PI % 360 + 360) % 360, _speed));
    }

    // ---- set up ----

    protected override void Start()
    {
        using var vsCode = Compile(Shaders, "TerrainVS", "vs_4_0");
        using var psCode = Compile(Shaders, "TerrainPS", "ps_4_0");
        _vs = new VertexShader(Device, vsCode);
        _ps = new PixelShader(Device, psCode);
        using (var code = Compile(Shaders, "SkyVS", "vs_4_0")) _skyVS = new VertexShader(Device, code);
        using (var code = Compile(Shaders, "SkyPS", "ps_4_0")) _skyPS = new PixelShader(Device, code);
        using (var code = Compile(Shaders, "WaterVS", "vs_4_0")) _waterVS = new VertexShader(Device, code);
        using (var code = Compile(Shaders, "WaterPS", "ps_4_0")) _waterPS = new PixelShader(Device, code);
        using (var code = Compile(Shaders, "PostPS", "ps_4_0")) _postPS = new PixelShader(Device, code);
        using (var code = Compile(Shaders, "ShadowPS", "ps_4_0")) _shadowPS = new PixelShader(Device, code);
        StartPlants();
        StartMeshes();
        StartStripes();
        StartEnvironment();
        _adaptBuffer = new Buffer(Device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        var blend = new BlendStateDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription(true, BlendOption.SourceAlpha, BlendOption.InverseSourceAlpha, BlendOperation.Add,
            BlendOption.Zero, BlendOption.One, BlendOperation.Add, ColorWriteMaskFlags.All);
        _waterBlend = new BlendState(Device, blend);
        _noDepth = new DepthStencilState(Device, new DepthStencilStateDescription { IsDepthEnabled = false, DepthWriteMask = DepthWriteMask.Zero });
        _depthTestOnly = new DepthStencilState(Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.Less,
        });
        _layout = new InputLayout(Device, vsCode, [
            new InputElement("TEXCOORD", 0, DxgiFormat.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElement("TEXCOORD", 1, DxgiFormat.R32_Float, 8, 0, InputClassification.PerVertexData, 0),
            new InputElement("PATCH", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1)]);
        _textureSettings = new Buffer(Device, 64 * 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _frame = new Buffer(Device, Utilities.SizeOf<Frame>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _grading = new Buffer(Device, Utilities.SizeOf<Grading>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _sky = new Buffer(Device, Utilities.SizeOf<Sky>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _lightColour = new Buffer(Device, 48, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _foliage = new Buffer(Device, 16 * 31, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _ambientCube = new Texture2D(Device, new Texture2DDescription
        {
            Width = 6, Height = 1, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R16G16B16A16_Float,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _ambientTarget = new RenderTargetView(Device, _ambientCube);
        _ambientView = new ShaderResourceView(Device, _ambientCube);
        using (var code = Compile(Shaders, "AmbientPS", "ps_4_0")) _ambientPS = new PixelShader(Device, code);
        using (var code = Compile(Shaders, "AoPS", "ps_4_0")) _aoPS = new PixelShader(Device, code);
        _sampler = new SamplerState(Device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue,
        });
        _tiled = new SamplerState(Device, new SamplerStateDescription
        {
            Filter = Filter.Anisotropic, MaximumAnisotropy = 8, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue,
        });
        _raster = new RasterizerState(Device, new RasterizerStateDescription { CullMode = CullMode.None, FillMode = FillMode.Solid, IsDepthClipEnabled = true });
        _depthState = new DepthStencilState(Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.All, DepthComparison = Comparison.Less,
        });
        for (int i = 0; i < Steps.Length; i++) _grids[i] = Grid(PatchSamples / Steps[i]);
        _waves = Texture(WaveMap(256), 256, 256, DxgiFormat.R8G8B8A8_UNorm, 4);
    }

    /// <summary>
    /// A tileable sea's slopes (R, G: the slope across and up, 0.5 flat), made from a few hundred waves whose
    /// wavelengths fit the tile, each as tall as a wind-driven sea's spectrum gives it (Phillips', as the game's own wave
    /// simulation uses), with random phases.
    /// </summary>
    private static byte[] WaveMap(int size)
    {
        var random = new Random(1764);
        var waves = new List<(float Kx, float Ky, float Amplitude, float Phase)>();
        var wind = Vector2.Normalize(new Vector2(0.8f, 0.6f));
        for (int i = -20; i <= 20; i++)
            for (int j = -20; j <= 20; j++)
            {
                float k = MathF.Sqrt(i * i + j * j);
                if (k < 1 || k > 20) continue;
                float along = Vector2.Dot(new Vector2(i, j) / k, wind);
                // long waves along the wind, few short ones, none against it
                float energy = MathF.Exp(-1 / (k * k * 0.02f)) / (k * k * k * k) * (along * along * 0.8f + 0.2f);
                waves.Add((i * MathF.Tau, j * MathF.Tau, MathF.Sqrt(energy), (float)random.NextDouble() * MathF.Tau));
            }
        var slopes = new float[size * size * 2];
        Parallel.For(0, size, y =>
        {
            for (int x = 0; x < size; x++)
            {
                float u = (float)x / size, v = (float)y / size, sx = 0, sy = 0;
                foreach (var (kx, ky, amplitude, phase) in waves)
                {
                    float c = MathF.Cos(kx * u + ky * v + phase) * amplitude;
                    sx += kx * c;
                    sy += ky * c;
                }
                slopes[(y * size + x) * 2] = sx;
                slopes[(y * size + x) * 2 + 1] = sy;
            }
        });
        float most = slopes.Max(Math.Abs);
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            pixels[i * 4] = (byte)Math.Clamp(128 + slopes[i * 2] / most * 127, 0, 255);
            pixels[i * 4 + 1] = (byte)Math.Clamp(128 + slopes[i * 2 + 1] / most * 127, 0, 255);
            pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }

    /// <summary>
    /// A flat grid of <paramref name="cells"/> squares a side, from 0 to 1, with a skirt round it: a ring of points
    /// the same as the edge's but hanging down, so a coarser neighbour's edge can't leave a gap to see through.
    /// </summary>
    private (Buffer, Buffer, int) Grid(int cells)
    {
        int n = cells + 1;
        var points = new List<GridPoint>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                points.Add(new GridPoint { U = (float)x / cells, V = (float)y / cells });
        var indices = new List<int>();
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                int a = y * n + x;
                indices.AddRange([a, a + 1, a + n, a + 1, a + n + 1, a + n]);
            }
        // the skirt: each edge's points again, hanging, joined to the edge
        void Edge(Func<int, int> at)
        {
            int first = points.Count;
            for (int i = 0; i < n; i++)
            {
                var p = points[at(i)];
                points.Add(p with { Skirt = 1 });
            }
            for (int i = 0; i < cells; i++)
                indices.AddRange([at(i), at(i + 1), first + i, at(i + 1), first + i + 1, first + i]);
        }
        Edge(i => i);
        Edge(i => (n - 1) * n + i);
        Edge(i => i * n);
        Edge(i => i * n + n - 1);
        return (Buffer.Create(Device, BindFlags.VertexBuffer, points.ToArray()), Buffer.Create(Device, BindFlags.IndexBuffer, indices.ToArray()), indices.Count);
    }

    // ---- drawing ----

    /// <summary>How bright the sky is drawn against the ground, at the same exposure: the environment's sky colours are kept far dimmer than its light.</summary>

    /// <summary>How wavy the water is for a weather's wind (its windScale): nearly a mirror when it's still, full waves from 0.3 up.</summary>
    private static float Waviness(float wind) => 0.12f + 0.88f * Math.Clamp(wind / 0.3f, 0, 1);

    /// <summary>The fog colours' scale against the light, measured against REDkit (the sky at the horizon at 09:10 over White Orchard).</summary>
    public float FogScale { get; set; } = 0.7f;

    /// <summary>
    /// The world's remaster translucency settings (its environmentParameters' translucencyRemasterSettings): the curve's
    /// tightness, the highlights' dimness, the normal mask's power and the scattering's saturation. A setting the world
    /// doesn't keep is 1 (the engine's own default for it isn't in REDkit's data).
    /// </summary>
    public Vector4 TranslucencyCurve { get; set; } = Vector4.One;

    /// <summary>
    /// The world's GTAO settings (its render settings' gtaoSettings): the radius, the final power, and the extra power on
    /// grass and on trees; null to use the environment's HBAO settings.
    /// </summary>
    public Vector4? Gtao { get; set; }

    /// <summary>
    /// The world's speedTreeParameters GrassAlbedoMultiplier (m_GrassAlbedoMultiplier): the cooked worlds keep it at its
    /// default, which is in the engine's code; 1 matches REDkit's grass.
    /// </summary>
    public float GrassAlbedoMultiplier { get; set; } = 1;

    /// <summary>Which of the light the view shows, as REDkit's decomposed views: 0 all of it, 1 the ambient alone, 2 the sun's alone.</summary>
    public float LightingView { get; set; }


    /// <summary>The camera's vertical field of view, in degrees.</summary>
    public float FieldOfView { get; set; } = 60;

    /// <summary>How much more pigment grass takes low on the plant than at its top (1, the whole of the shader's height term, matches REDkit's grass).</summary>
    public float PigmentHeight { get; set; } = 1;




    /// <summary>A scale on the sky's brightness, for matching against REDkit (1 is the environment's own).</summary>
    public float SkyFactor { get; set; } = 1;

    /// <summary>A test switch for matching against REDkit: 1 draws plants without their random colours.</summary>
    public float PlantTest { get; set; }

    /// <summary>The sun's glint on water, in the engine's units: set against REDkit's lake at 09:10.</summary>
    public float GlintScale { get; set; } = 4;

    /// <summary>
    /// The scale from the light as this view adds it up (the environment's colours made linear, times their strengths)
    /// to the engine's units, which the tone mapping's exposure and luminance limits are in.
    /// </summary>
    public float Units { get; set; } = 0.5f;

    protected override void Draw(double seconds)
    {
        Fly((float)seconds);
        AdvanceEnvironment((float)seconds);
        if (_playing)
        {
            Hour = _hour + (float)seconds / Math.Max(0.05f, SecondsAnHour);
            HourChanged?.Invoke(this, _hour);
        }
        var c = Context!;
        c.ClearRenderTargetView(TargetView, new RawColor4(0, 0, 0, 1));
        if (HdrView is not null) c.ClearRenderTargetView(HdrView, new RawColor4(0, 0, 0, 1));
        c.ClearDepthStencilView(DepthView, DepthStencilClearFlags.Depth, 1, 0);
        if (_terrain is not { } terrain || _heightMap is null) return;

        float aspect = PixelWidth / (float)Math.Max(1, PixelHeight);
        var view = Matrix4x4.CreateLookAt(_eye, _eye + Forward(), Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView * MathF.PI / 180, aspect, NearPlane, FarPlane);
        var viewProjection = view * projection;
        Matrix4x4.Invert(viewProjection, out var unproject);
        float spacing = terrain.Size / _side;
        var light = (_lighting ?? Fallback).At(_hour);
        static Vector4 V(Vector3 v, float w = 0) => new(v, w);
        var frame = new Frame
        {
            ViewProjection = viewProjection,
            Unproject = unproject,
            Eye = new Vector4(_eye, (float)_clock.Elapsed.TotalSeconds),
            Ground = new Vector4(terrain.Corner.X, terrain.Corner.Y, terrain.Size, _side),
            Heights = new Vector4(terrain.Lowest, terrain.Highest - terrain.Lowest, spacing, 0),
            Paint = new Vector4(_reflections ? 1 : 0, 0, 0, Painted ? 1 : 0),
            Light = V(light.LightDirection, Units),
            LightColour = V(light.LightColour),
            Ambient = V(light.Ambient),
            SunDir = V(light.SunDirection, light.SunGlowSize),
            SkyZenith = V(light.SkyZenith, light.SkyBrightness),
            SkyHorizon = V(light.SkyHorizon),
            SunGlow = V(light.SunGlow),
            FogFront = V(light.FogFront, AllowGlobalFog ? light.FogDensity : 0),
            FogMiddle = V(light.FogMiddle, light.FogAppear),
            FogBack = V(light.FogBack, light.FogVerticalDensity),
            FogHeight = new Vector4(light.FogVerticalOffset, light.DayAmount, light.FogVerticalFront, light.FogVerticalBack),
            Water = V(light.WaterColour, light.WaterFog),
            Camera = new Vector4(NearPlane, FarPlane, PixelWidth, PixelHeight),
            AerialFront = V(light.AerialFront, light.AerialExponent),
            AerialMiddle = V(light.AerialMiddle, light.FogExponent),
            AerialBack = V(light.AerialBack, light.FogAppearRange),
            FogCustom = V(light.FogCustom, light.FogCustomAmount),
            FogCustomShape = new Vector4(light.FogCustomColourStart, light.FogCustomColourRange, light.FogCustomAmountStart, light.FogCustomAmountRange),
            FogLimits = new Vector4(light.FogDistanceLimit, light.FogSkyDensity, FogScale, Waviness((_lighting ?? Fallback).Wind)),
        };
        c.UpdateSubresource(ref frame, _frame);
        c.VertexShader.SetConstantBuffer(0, _frame);
        var grade = (_lighting ?? Fallback).Grading(_hour);
        bool maps = grade.BalanceMaps && _balanceA is not null && _balanceB is not null;
        var grading = new Grading
        {
            Exposure = new Vector4(grade.ExposureScale, grade.LuminanceShape, grade.LuminanceMin, grade.LuminanceMax),
            Curve = new Vector4(grade.GlobalContrast, grade.ShoulderPower, grade.ToePower, grade.BlackLift),
            Slope = V(grade.Slope, grade.Saturation),
            Power = V(grade.Power, grade.MidGray),
            Balance = new Vector4(maps && AllowColorBalance ? grade.BalanceMapAmount : 0, Math.Clamp(grade.BalanceMapLerp, 0, 1), grade.LevelsShadows, grade.LevelsMidtones),
            Balance2 = new Vector4(grade.LevelsHighlights, grade.ParametricBalance && AllowColorBalance ? 1 : 0, grade.PostBrightness,
                MathF.Floor(MathF.Log2(Math.Max(PixelWidth, PixelHeight)))),
            Low = grade.Low, Mid = grade.Mid, High = grade.High,
            Midtones = new Vector4(grade.MidtoneRangeMin, grade.MidtoneRangeMax, grade.MidtoneMarginMin, grade.MidtoneMarginMax),
            SkyLuminance = new Vector4(grade.SkyLuminance, grade.SkyLuminanceAmount, 0, 0),
            Vignette = new Vector4(grade.VignetteWeights, AllowVignette ? grade.VignetteOpacity : 0),
            VignetteColour = new Vector4(grade.VignetteColour, 0),
        };
        c.UpdateSubresource(ref grading, _grading);
        var skyAt = (_lighting ?? Fallback).Sky(_hour);
        var sky = new Sky
        {
            Top = V(skyAt.Top, skyAt.VisibleScale), Horizon = V(skyAt.Horizon),
            // the sky's own vertical fog density towards and away from the light ride in the horizon colours' w
            SunHorizon = V(skyAt.SunHorizon, skyAt.FogVerticalFront), SunBackHorizon = V(skyAt.SunBackHorizon, skyAt.FogVerticalBack),
            SunSky = V(skyAt.SunSky), MoonHorizon = V(skyAt.MoonHorizon), MoonBackHorizon = V(skyAt.MoonBackHorizon), MoonSky = V(skyAt.MoonSky, skyAt.ReflectedSky),
            SunAt = V(skyAt.SunDirection, skyAt.SunSize), MoonAt = V(skyAt.MoonDirection, skyAt.MoonSize),
            // the sky at the environment's global sky brightness alone: measured against REDkit's (its sky's top at 09:10 over
            // White Orchard), the visible sky brightness scale isn't on the sky we see
            SkyScale = new Vector4(skyAt.SunInfluence, skyAt.MoonInfluence, skyAt.Brightness * SkyFactor, skyAt.HorizonAttenuation),
        };
        c.UpdateSubresource(ref sky, _sky);
        c.PixelShader.SetConstantBuffers(0, _frame, _textureSettings, _grading, _sky);
        var near = (_lighting ?? Fallback).GlobalLightColour(_hour);
        c.UpdateSubresource(new[] { V(near.Side), V(near.OppositeSide), near.Parameters }, _lightColour);
        c.PixelShader.SetConstantBuffer(7, _lightColour);
        var foliage = (_lighting ?? Fallback).Foliage(_hour);
        var alpha = (_lighting ?? Fallback).AlphaScalars;
        var foliageData = new Vector4[31];
        for (int k = 0; k < 3; k++)
        {
            foliageData[k] = foliage.Trees[k];
            foliageData[3 + k] = foliage.Branches[k];
            foliageData[6 + k] = foliage.Grass[k];
        }
        foliageData[9] = V(foliage.TreesLuminance);
        foliageData[10] = V(foliage.BranchesLuminance);
        foliageData[11] = V(foliage.GrassLuminance);
        foliageData[12] = V(foliage.Diffuse, foliage.OcclusionScale);
        foliageData[13] = V(foliage.BillboardColour, foliage.TranslucencyScale);
        foliageData[14] = foliage.Translucency;
        foliageData[15] = new Vector4(alpha, 0);
        foliageData[16] = new Vector4(foliage.AmbientLight, foliage.AmbientShadow, Units, foliage.MaxSkyBrightness);
        foliageData[17] = V(foliage.AmbientColour);
        foliageData[18] = new Vector4(foliage.SkyBrightness, foliage.SkySaturation, foliage.GroundBrightness, foliage.GroundSaturation);
        var probes = (_lighting ?? Fallback).Probes(_hour);
        foliageData[19] = V(probes.SkyTop, probes.SkyShape);
        foliageData[20] = V(probes.SkyHorizon, probes.ReflectionLight);
        foliageData[21] = V(probes.SceneAdd, probes.ReflectionShadow);
        // the remaster's GTAO (the world's render settings) in place of the environment's HBAO radius and power: our
        // pass's power is half the setting
        foliageData[22] = Gtao is { } gtao ? new Vector4(gtao.X, probes.AoBias, gtao.Y * 2, AllowSsao ? probes.AoNonAmbient : 0)
            : new Vector4(probes.AoRadius, probes.AoBias, probes.AoPower, AllowSsao ? probes.AoNonAmbient : 0);
        foliageData[23] = new Vector4(probes.F90Grass, probes.F90Trees, probes.TreeSpecular, PlantTest);
        foliageData[24] = new Vector4(probes.WaterFresnel, GlintScale, Gtao?.Z ?? 1, Gtao?.W ?? 1);
        foliageData[25] = TranslucencyCurve;
        foliageData[26] = new Vector4(CanopyDensity, _canopy is null ? 0 : 1, foliage.GrassAmbient, 0);
        foliageData[29] = V(foliage.Fallback);
        foliageData[30] = new Vector4(probes.WaterAmbientScale, probes.WaterDiffuseScale, probes.WaterFoam, 0);
        foliageData[28] = new Vector4(GrassAlbedoMultiplier, LightingView, 1, foliage.BillboardTranslucency);
        foliageData[27] = new Vector4(foliage.Pigment, PigmentHeight);

        c.UpdateSubresource(foliageData, _foliage);
        c.PixelShader.SetConstantBuffer(8, _foliage);
        // the ambient cube for this light, before anything reads it: made again when the light or the eye has moved (it
        // folds a probe of hundreds of samples a direction, too much for every frame)
        if (AmbientDue(light.LightDirection))
        {
            c.PixelShader.SetShaderResource(17, null);
            c.OutputMerger.SetRenderTargets((DepthStencilView?)null, _ambientTarget);
            c.Rasterizer.SetViewport(0, 0, 6, 1);
            c.Rasterizer.State = _raster;
            c.OutputMerger.SetDepthStencilState(_noDepth);
            c.OutputMerger.SetBlendState(null);
            c.InputAssembler.InputLayout = null;
            c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            c.VertexShader.Set(_skyVS);
            c.PixelShader.Set(_ambientPS);
            EnsureProbe();
            c.PixelShader.SetShaderResources(25, _probeColourView, _probeDepthView);
            c.PixelShader.SetConstantBuffer(11, _probeFaces);
            BindCookedProbe(c);
            c.Draw(3, 0);
            c.PixelShader.SetShaderResources(25, null, null);
    c.Rasterizer.SetViewport(0, 0, PixelWidth, PixelHeight);
            c.OutputMerger.SetRenderTargets(DepthView, HdrView);
        }
        c.PixelShader.SetShaderResource(17, _ambientView);
        // the terrain's shadows for where the light is now, made again only when it has moved
        if (_shadowTarget is not null && (!_shadowMade || Vector3.DistanceSquared(_shadowFor, light.LightDirection) > 1e-8f))
        {
            _shadowFor = light.LightDirection;
            _shadowMade = true;
            c.PixelShader.SetShaderResource(13, null);
            c.OutputMerger.SetRenderTargets((DepthStencilView?)null, _shadowTarget);
            c.Rasterizer.SetViewport(0, 0, _side, _side);
            c.Rasterizer.State = _raster;
            c.OutputMerger.SetDepthStencilState(_noDepth);
            c.OutputMerger.SetBlendState(null);
            c.InputAssembler.InputLayout = null;
            c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            c.PixelShader.SetShaderResources(0, _heightMap, null, null, null, null, _waterLevel);
            c.PixelShader.SetSamplers(0, _sampler, _tiled);
            c.VertexShader.Set(_skyVS);
            c.PixelShader.Set(_shadowPS);
            c.Draw(3, 0);
            c.Rasterizer.SetViewport(0, 0, PixelWidth, PixelHeight);
            c.OutputMerger.SetRenderTargets(DepthView, HdrView);
        }
        c.PixelShader.SetShaderResource(13, _shadow);
        GrowGrass();
        PreparePlants(light.LightDirection, view);
        SetTrajectory();
        c.VertexShader.SetShaderResources(0, _heightMap, null, null, null, null, _waterLevel);
        c.VertexShader.SetSampler(0, _sampler);
        c.PixelShader.SetShaderResources(0, _heightMap, _colourMap, _controlMap, _diffuse, _normals, _waterLevel);
        c.PixelShader.SetShaderResource(20, _canopy);
        c.PixelShader.SetSamplers(0, _sampler, _tiled);
        c.Rasterizer.State = _raster;
        // what's round the eye, for the next ambient
        CaptureProbe(frame, terrain, spacing);

        // the sky behind everything
        c.OutputMerger.SetDepthStencilState(_noDepth);
        c.OutputMerger.SetBlendState(null);
        c.InputAssembler.InputLayout = null;
        c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        c.VertexShader.Set(_skyVS);
        c.PixelShader.Set(_skyPS);
        c.Draw(3, 0);

        // the patches in view, each as fine as its distance asks
        var planes = Frustum(viewProjection);
        int count = _patchLow.GetLength(0);
        float size = PatchSamples * spacing;
        var byStep = new List<Patch>[Steps.Length];
        for (int i = 0; i < Steps.Length; i++) byStep[i] = [];
        var watery = new List<Patch>();
        for (int py = 0; py < count; py++)
            for (int px = 0; px < count; px++)
            {
                var min = new Vector3(terrain.Corner.X + px * size, terrain.Corner.Y + py * size, _patchLow[py, px] - 8);
                var max = new Vector3(min.X + size, min.Y + size, Math.Max(_patchHigh[py, px], _hasWater ? _waterTop : float.MinValue));
                if (!InView(planes, min, max)) continue;
                float distance = Vector3.Distance(_eye, Vector3.Clamp(_eye, min, max));
                int step = distance < 120 ? 0 : distance < 260 ? 1 : distance < 600 ? 2 : distance < 1400 ? 3 : 4;
                // skirts a little deeper for coarser patches, whose edges stray further from their neighbours'
                byStep[step].Add(new Patch { X = min.X, Y = min.Y, Size = size, Skirt = 2 + 4 * step });
                // water only where the ground dips below the highest water level
                if (_hasWater && _patchLow[py, px] < _waterTop) watery.Add(new Patch { X = min.X, Y = min.Y, Size = size });
            }

        c.OutputMerger.SetDepthStencilState(_depthState);
        // the depth of what's in view first (the ground and the plants), for the ambient occlusion, then cleared
        c.PixelShader.SetShaderResource(18, null);
        EnsureAo();
        if (AllowSsao && _aoTarget is not null)
        {
            c.OutputMerger.SetRenderTargets(DepthView, (RenderTargetView?)null);
            c.OutputMerger.SetDepthStencilState(null);
            c.InputAssembler.InputLayout = _layout;
            c.VertexShader.Set(_vs);
            c.PixelShader.Set(null);
            for (int i = 0; i < Steps.Length; i++)
                if (byStep[i].Count > 0 && _grids[i] is { } depthGrid) DrawPatches(depthGrid, byStep[i]);
            DrawPlantsDepth(planes);
            DrawMeshesDepth(planes);
            c.OutputMerger.SetRenderTargets((DepthStencilView?)null, _aoTarget);
            c.OutputMerger.SetDepthStencilState(_noDepth);
            c.InputAssembler.InputLayout = null;
            c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            c.VertexShader.Set(_skyVS);
            c.PixelShader.Set(_aoPS);
            c.PixelShader.SetShaderResource(6, DepthTexture);
            c.Draw(3, 0);
            c.PixelShader.SetShaderResource(6, null);
            c.ClearDepthStencilView(DepthView, DepthStencilClearFlags.Depth, 1, 0);
            c.OutputMerger.SetRenderTargets(DepthView, HdrView);
            c.OutputMerger.SetDepthStencilState(null);
        }
        else if (_aoTarget is not null) c.ClearRenderTargetView(_aoTarget, new RawColor4(1, 1, 1, 1));
        c.PixelShader.SetShaderResource(18, _aoView);
        c.InputAssembler.InputLayout = _layout;
        c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        c.VertexShader.Set(_vs);
        c.PixelShader.Set(_ps);
        PatchesDrawn = 0;
        for (int i = 0; i < Steps.Length; i++)
            if (byStep[i].Count > 0 && _grids[i] is { } grid)
            {
                DrawPatches(grid, byStep[i]);
                PatchesDrawn += byStep[i].Count;
            }

        // the paths laid over the ground, then the trees, bushes and grass, and the layers' meshes
        DrawStripes();
        DrawPlants(planes);
        DrawMeshes(planes);
        c.InputAssembler.InputLayout = _layout;
        c.Rasterizer.State = _raster;

        // the water over the ground, seen through where it's shallow
        if (AllowWaterShader && watery.Count > 0 && _grids[2] is { } waterGrid)
        {
            CopyScene();
            c.OutputMerger.SetRenderTargets(ReadOnlyDepthView, HdrView);
            c.OutputMerger.SetDepthStencilState(_depthTestOnly);
            c.OutputMerger.SetBlendState(null);
            c.PixelShader.SetShaderResources(6, DepthTexture, SceneTexture, _foam, _waves);
            c.VertexShader.Set(_waterVS);
            c.PixelShader.Set(_waterPS);
            DrawPatches(waterGrid, watery);
            // let go of the depth buffer as a texture, so the next frame can draw into it
            c.PixelShader.SetShaderResources(6, null, null, null, null);
        }

        // the scene brought to the screen: its average for the eye, then the tone mapping and grading
        c.OutputMerger.SetRenderTargets((DepthStencilView?)null, TargetView);
        AverageHdr();
        c.OutputMerger.SetDepthStencilState(_noDepth);
        c.OutputMerger.SetBlendState(null);
        c.InputAssembler.InputLayout = null;
        c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        var adapted = Adapt((float)seconds, !_adaptedOnce);
        _adaptedOnce = true;
        c.VertexShader.Set(_skyVS);
        c.PixelShader.Set(_postPS);
        c.PixelShader.SetShaderResources(10, HdrTexture, _balanceA, _balanceB);
        c.PixelShader.SetShaderResource(16, adapted);
        c.Draw(3, 0);
        c.PixelShader.SetShaderResources(10, null, null, null);
        c.PixelShader.SetShaderResource(16, null);
        // keep drawing while the eye adapts, the day runs or the weather changes
        Animating = _playing || _hasWater || !FakeDayCycle || !WeatherPaused || !InstantAdaptation || _lighting?.PreviousWeather is not null;
    }

    /// <summary>The environment's two balance maps (colour lookup pictures, 8 x 8 tiles of 64 x 64), or null for none.</summary>
    public void SetBalanceMaps((Core.Textures.TextureInfo Info, byte[] Pixels)? first, (Core.Textures.TextureInfo Info, byte[] Pixels)? second)
    {
        EnsureDevice();
        _balanceA?.Dispose();
        _balanceB?.Dispose();
        _balanceA = first is { } a ? TextureArray(a.Info, a.Pixels) : null;
        // with one map named, it's both ends of the mix
        _balanceB = (second ?? first) is { } b ? TextureArray(b.Info, b.Pixels) : null;
        Invalidate();
    }

    private void DrawPatches((Buffer Vertices, Buffer Indices, int Count) grid, List<Patch> patches)
    {
        var c = Context!;
        c.MapSubresource(_patches, MapMode.WriteDiscard, MapFlags.None, out var stream);
        stream.WriteRange(patches.ToArray());
        c.UnmapSubresource(_patches, 0);
        c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(grid.Vertices, Utilities.SizeOf<GridPoint>(), 0),
            new VertexBufferBinding(_patches, Utilities.SizeOf<Patch>(), 0));
        c.InputAssembler.SetIndexBuffer(grid.Indices, DxgiFormat.R32_UInt, 0);
        c.DrawIndexedInstanced(grid.Count, patches.Count, 0, 0, 0);
    }

    /// <summary>The light when the world's lighting isn't known: the engine's defaults, a day like any other.</summary>
    private static readonly WorldLighting Fallback = WorldLighting.Default;

    private Vector3 Forward() => new(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch));

    /// <summary>The six planes of what the camera sees, pointing in.</summary>
    private static Vector4[] Frustum(Matrix4x4 m) =>
    [
        Vector4.Normalize(new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41)),
        Vector4.Normalize(new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41)),
        Vector4.Normalize(new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42)),
        Vector4.Normalize(new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42)),
        Vector4.Normalize(new Vector4(m.M13, m.M23, m.M33, m.M43)),
        Vector4.Normalize(new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)),
    ];

    private static bool InView(Vector4[] planes, Vector3 min, Vector3 max)
    {
        foreach (var p in planes)
        {
            // the box's corner furthest along the plane's normal
            var far = new Vector3(p.X >= 0 ? max.X : min.X, p.Y >= 0 ? max.Y : min.Y, p.Z >= 0 ? max.Z : min.Z);
            if (p.X * far.X + p.Y * far.Y + p.Z * far.Z + p.W < 0) return false;
        }
        return true;
    }

    // ---- moving ----

    /// <summary>Moves the camera by the keys held, never below the ground.</summary>
    private void Fly(float seconds)
    {
        if (_held.Count == 0) return;
        float speed = _speed * SpeedUnit * (_held.Contains(Key.LeftShift) || _held.Contains(Key.RightShift) ? 5 : 1)
            * (_held.Contains(Key.LeftCtrl) || _held.Contains(Key.RightCtrl) ? 0.25f : 1);
        var forward = Forward();
        var right = Vector3.Normalize(new Vector3(MathF.Cos(_yaw), -MathF.Sin(_yaw), 0));
        var move = Vector3.Zero;
        if (_held.Contains(Key.W) || _held.Contains(Key.Up)) move += forward;
        if (_held.Contains(Key.S) || _held.Contains(Key.Down)) move -= forward;
        if (_held.Contains(Key.D) || _held.Contains(Key.Right)) move += right;
        if (_held.Contains(Key.A) || _held.Contains(Key.Left)) move -= right;
        if (_held.Contains(Key.E) || _held.Contains(Key.Space)) move += Vector3.UnitZ;
        if (_held.Contains(Key.Q) || _held.Contains(Key.C)) move -= Vector3.UnitZ;
        if (move == Vector3.Zero) return;
        _eye += Vector3.Normalize(move) * speed * seconds;
        KeepAboveGround();
        Moved();
    }

    private void KeepAboveGround()
    {
        float ground = GroundAt(new Vector2(_eye.X, _eye.Y)) + 1.7f;
        if (_eye.Z < ground) _eye.Z = ground;
    }

    /// <summary>
    /// The camera as REDkit's Copy Camera View writes it: [[x y z w|pitch yaw roll]], degrees, REDkit's yaw turning
    /// anticlockwise from north where this view's turns clockwise.
    /// </summary>
    public string CameraText => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"[[{_eye.X:0.###} {_eye.Y:0.###} {_eye.Z:0.###} 1|{_pitch * 180 / MathF.PI:0.####} {-_yaw * 180 / MathF.PI:0.####} 0]]");

    /// <summary>Puts the camera where a REDkit camera view says ([[x y z w|pitch yaw roll]]); false when the text isn't one.</summary>
    public bool PasteCamera(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\[\[\s*([-\d.eE+]+)\s+([-\d.eE+]+)\s+([-\d.eE+]+)(?:\s+[-\d.eE+]+)?\s*\|\s*([-\d.eE+]+)\s+([-\d.eE+]+)");
        if (!m.Success) return false;
        float F(int i) => float.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
        _eye = new Vector3(F(1), F(2), F(3));
        // REDkit keeps turning its angles past a full turn as the mouse goes round (a pitch of -3993.81 is -33.81)
        static float Wrapped(float degrees) => degrees - 360 * MathF.Round(degrees / 360);
        _pitch = Math.Clamp(Wrapped(F(4)) * MathF.PI / 180, -1.5f, 1.5f);
        _yaw = -Wrapped(F(5)) * MathF.PI / 180;
        Moved();
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // REDkit's own keys for copying a camera view and going to one
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key is Key.C or Key.V)
        {
            try
            {
                if (key == Key.C) Clipboard.SetText(CameraText);
                else if (Clipboard.GetText() is { } text) PasteCamera(text);
            }
            catch (System.Runtime.InteropServices.COMException) { }
            e.Handled = true;
            return;
        }
        if (key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.C or Key.Space or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)
        {
            _held.Add(key);
            Animating = true;
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _held.Remove(e.Key == Key.System ? e.SystemKey : e.Key);
        Animating = _held.Count > 0;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _held.Clear();
        Animating = false;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        _lastMouse = e.GetPosition(this);
        _looking = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_looking) return;
        var at = e.GetPosition(this);
        var moved = at - _lastMouse;
        _lastMouse = at;
        _yaw += (float)moved.X * 0.0045f;
        _pitch = Math.Clamp(_pitch - (float)moved.Y * 0.0045f, -1.5f, 1.5f);
        Moved();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
        {
            _looking = false;
            ReleaseMouseCapture();
        }
    }

    /// <summary>
    /// As REDkit's camera: the wheel with Shift held sets how fast the camera flies; the wheel alone moves it forward or
    /// back, a step that shrinks the nearer the ground it is.
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int notches = Math.Sign(e.Delta);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            _speed = Math.Clamp(_speed * (notches > 0 ? 1.25f : 0.8f), 0.05f, 100);
        else
        {
            float above = MathF.Max(0, _eye.Z - GroundAt(new Vector2(_eye.X, _eye.Y)));
            _eye += Forward() * notches * _speed * Math.Clamp(above * 0.2f, 0.2f, 5);
            KeepAboveGround();
        }
        Moved();
        e.Handled = true;
    }

    /// <summary>How fast the camera flies, as REDkit shows it (1 a slow walk).</summary>
    public float Speed
    {
        get => _speed;
        set { _speed = Math.Clamp(value, 0.05f, 100); Moved(); }
    }

    public override void Dispose()
    {
        foreach (var grid in _grids)
            if (grid is { } g)
            {
                g.Vertices.Dispose();
                g.Indices.Dispose();
            }
        DisposePlantDevice();
        DisposeMeshDevice();
        DisposeStripeDevice();
        DisposeEnvironment();
        DisposeProbe();
        foreach (var d in new IDisposable?[] { _aoPS, _ao, _aoTarget, _aoView, _foliage, _ambientCube, _ambientTarget, _ambientView, _ambientPS, _lightColour, _shadowPS, _shadow, _shadowTarget, _shadowTexture, _sky, _grading, _postPS, _balanceA, _balanceB, _waves, _foam, _textureSettings, _skyVS, _skyPS, _waterVS, _waterPS, _waterBlend, _noDepth, _depthTestOnly, _waterLevel, _patches, _heightMap, _colourMap, _controlMap, _diffuse, _normals, _tiled, _depthState, _raster, _sampler, _frame, _layout, _ps, _vs })
            d?.Dispose();
        base.Dispose();
    }
}
