// The view's mesh pixel shader: the game's materials lit as the game lights them, as far as a viewer can. Built on
// Helix Toolkit's shader library (Common.hlsl, CommonBuffers.hlsl, DataStructs.hlsl and psCommon.hlsl, v3.1.2, MIT),
// whose constant buffers, lights, shadow map and SSAO it uses; what the game's materials need beside that comes in
// cbW3 and the textures from t40 on, bound by W3MaterialVariables.
//
// What each kind of material reads was traced from REDkit's material graphs (r4data\engine\materials\graphs):
//   pbr_std and its tint mask / detail variants: roughness in the normal map's alpha, specularity
//     SpecularColor * ((1 - roughness) * RSpecScale + RSpecBase); the tint mask's red is baked ambient occlusion
//     (AOPower), its blue where the detail normal maps go; the colour drawn toward VarianceColor (customColoring).
//   pbr_skin: the "Ambient" texture is packed: red where the detail normal map goes, green roughness, blue specularity.
//   pbr_hair: alpha tested, roughness and specularity as pbr_std, a highlight along the strands (SpecularShiftTexture).
//   pbr_eye: specularity and roughness as numbers (another for the corner flesh), the highlight from its BlickCube.
// Lighting is done in linear light, GGX for the shine, with image-based light from an environment cube map for the
// light all round and for reflections, then tone mapped (ACES) and graded for the screen.
#ifndef PSW3MESH_HLSL
#define PSW3MESH_HLSL

#define CLIPPLANE
#define MESH

#include "Common.hlsl"
#include "DataStructs.hlsl"
#include "psCommon.hlsl"

#define KIND_STD 0
#define KIND_SKIN 1
#define KIND_HAIR 2
#define KIND_EYE 3
#define KIND_GLASS 4

cbuffer cbW3 : register(b10)
{
    float4 w3Kind;      // x kind, y alpha cut-off (0 for none), z light wrap, w warmth of the wrapped light
    float4 w3Spec;      // x RSpecScale, y RSpecBase, z Roughness_min, w Roughness_max
    float4 w3SpecColor; // rgb SpecularColor (linear), w AOPower (the tint mask's red)
    float4 w3Detail;    // xy DetailTile (1), z DetailPower, w DetailRange
    float4 w3Detail2;   // xy DetailTile2, z DetailAlbedoPower, w detail mode (0 none, 1 one masked, 2 two masked, 3 one unmasked, 4 skin)
    float4 w3Emissive;  // rgb emissive (linear), w opacity (glass)
    float4 w3Flags;     // x has normal map, y has mask, z has strand shift, w strand shift scale
    float4 w3Eye;       // x specularity, y roughness, z specularity of the corner flesh, w highlight strength
    float4 w3Env;       // x has environment, y reflection strength, z light-all-round strength, w environment mip levels
    float4 w3Grade;     // x exposure, y saturation, z contrast, w SSAO strength
    float4 w3Misc;      // x roughness without a normal map, y translucency, z soft (blended, not tested), w has specular texture
    float4 w3Variance;  // rgb VarianceColor, w VarianceOffset (1 changes nothing)
    float4 w3Light;     // rgb light all round when there's no environment (linear), w how much of the environment's colour is kept
    float4 w3Glow;      // x emissive mode (0 none, 1 the colour times the emissive texture, 2 a colour times a mask), y times the vertex colour, zw unused
};

Texture2D texW3Normal : register(t40);   // the normal map, with roughness in its alpha
Texture2D texW3Mask : register(t41);     // the tint mask, or the skin's packed map
Texture2D texW3Detail1 : register(t42);
Texture2D texW3Detail2 : register(t43);
Texture2D texW3Shift : register(t44);    // hair's strand shift, or pbr_spec's specular texture
TextureCube texW3Env : register(t45);
TextureCube texW3Blick : register(t46);
Texture2D texW3Emissive : register(t47); // what glows: an emissive texture, or a mask of where
SamplerState samplerW3 : register(s10);
SamplerState samplerW3Clamp : register(s11);

static const float PI_W3 = 3.14159265f;

float3 ToLinear(float3 c) { return pow(max(c, 0), 2.2f); }
float3 ToScreen(float3 c) { return pow(saturate(c), 1.0f / 2.2f); }

// the environment's light, linear, with only some of its colour kept: a forest probe would turn everything green
float3 EnvironmentLight(float3 c)
{
    c = ToLinear(c);
    float grey = dot(c, float3(0.2126f, 0.7152f, 0.0722f));
    return lerp(grey.xxx, c, w3Light.w);
}

// the view is Y-up (x, z, -y of the game's); the game's cube maps are made in its own Z-up space
float3 GameDirection(float3 v) { return float3(v.x, -v.z, v.y); }

// the graphs' customColoring: the colour drawn toward VarianceColor, as much as VarianceOffset says
float3 CustomColoring(float3 albedo)
{
    float offset = w3Variance.w;
    float a = (albedo.x + albedo.y + albedo.z) * 0.3333f;
    float inverseOffset = 0.5f * (offset - 1.0f);
    float blend = lerp(a, 1.0f - a, saturate(inverseOffset));
    return lerp(albedo, saturate(1.5f * albedo * w3Variance.rgb), saturate(blend * abs(inverseOffset)));
}

float3 Unpack(float3 c) { return c * 2.0f - 1.0f; }

// a normal map's normal: x and y as stored, z worked out (BC5 maps keep only x and y)
float3 UnpackNormal(float4 c)
{
    float2 xy = c.xy * 2.0f - 1.0f;
    return float3(xy, sqrt(saturate(1.0f - dot(xy, xy))));
}

struct Surface
{
    float3 albedo;   // linear
    float3 normal;   // view space
    float roughness;
    float3 f0;       // specularity
    float ao;
    float alpha;
    float translucency;
};

// the graphs' detail blend (normBlend): the detail normal laid on the surface's, as much as amount says
float3 BlendDetail(float3 first, float3 detail, float amount)
{
    float3 r;
    r.x = first.x * detail.z + amount * detail.x * first.z;
    r.y = first.y * detail.z + amount * detail.y * first.z;
    r.z = first.z * detail.z;
    return normalize(r);
}

// the skin graph's reoriented blend
float3 BlendSkinDetail(float3 first, float3 second, float blend)
{
    float3 t = first + float3(0, 0, 1);
    float3 u = normalize(lerp(float3(0, 0, 0.5f), second, blend));
    u.xy *= -1.0f;
    return normalize(t * dot(t, u) - u * t.z);
}

Surface ReadSurface(PSInput input, float3 V)
{
    Surface s;
    float kind = w3Kind.x;
    float2 uv = input.t;
    float distanceBlend = saturate(w3Detail.w - length(vEyePos - input.wp.xyz));

    float4 diffuse = input.cDiffuse;
    if (bHasDiffuseMap)
    {
        diffuse *= texDiffuseMap.Sample(samplerSurface, uv);
    }
    s.alpha = diffuse.a;
    float3 albedo = CustomColoring(diffuse.rgb);

    float4 normalSample = w3Flags.x > 0 ? texW3Normal.Sample(samplerW3, uv) : float4(0.5f, 0.5f, 1, 1);
    float3 n = w3Flags.x > 0 ? UnpackNormal(normalSample) : float3(0, 0, 1);
    float roughness = w3Flags.x > 0 ? normalSample.a : w3Misc.x;
    float4 mask = w3Flags.y > 0 ? texW3Mask.Sample(samplerW3, uv) : float4(1, 0, 0, 1);

    float mode = w3Detail2.w;
    float detailRough = 0;
    float detailWeight = 0;
    if (mode > 0.5f)
    {
        float4 d1 = texW3Detail1.Sample(samplerW3, uv * w3Detail.xy);
        float power = w3Detail.z;
        if (mode < 1.5f || mode > 2.5f && mode < 3.5f)
        {
            // one detail map, where the tint mask's blue says (or everywhere)
            detailWeight = mode < 1.5f ? saturate((mask.b - 0.55f) / 0.45f) : 1;
            n = BlendDetail(n, Unpack(d1.xyz), power * distanceBlend * detailWeight);
            detailRough = 0.5f * (d1.a - 0.5f);
        }
        else if (mode < 2.5f)
        {
            // two detail maps: the first where the mask's blue is over half, the second where it's under
            float4 d2 = texW3Detail2.Sample(samplerW3, uv * w3Detail2.xy);
            float4 d = mask.b > 0.5f ? d1 : d2;
            detailWeight = saturate((abs(mask.b - 0.5f) - 0.05f) / 0.45f);
            n = BlendDetail(n, Unpack(d.xyz), power * distanceBlend * detailWeight);
            detailRough = 0.5f * (d.a - 0.5f);
        }
        else
        {
            // skin: the pores where the packed map's red says
            float3 second = Unpack(d1.xyz);
            second.xy *= power * mask.r;
            n = BlendSkinDetail(n, normalize(second), distanceBlend);
        }
    }

    if (kind == KIND_SKIN)
    {
        // the packed map: green roughness, blue specularity
        roughness = w3Flags.y > 0 ? saturate(mask.g + w3Spec.z) : w3Misc.x;
        float shine = w3Flags.y > 0 ? saturate(mask.b * w3Spec.x + w3Spec.y) : 0.04f;
        s.f0 = w3SpecColor.rgb * shine;
        s.ao = 1;
    }
    else if (kind == KIND_EYE)
    {
        bool flesh = input.t.x < 0;
        roughness = w3Eye.y;
        s.f0 = (flesh ? w3Eye.z : w3Eye.x).xxx;
        s.ao = 1;
    }
    else
    {
        // roughness from the normal map's alpha, the detail map's added where it goes, then into its range
        roughness = saturate(roughness + detailRough * distanceBlend * w3Detail.z * (detailWeight > 0 ? 1 : 0));
        roughness = saturate(lerp(w3Spec.z, w3Spec.w, roughness));
        float3 specColour = w3SpecColor.rgb;
        if (w3Misc.w > 0)
        {
            specColour = ToLinear(texW3Shift.Sample(samplerW3, uv).rgb);
        }
        s.f0 = w3Misc.w > 0 ? specColour : saturate(specColour * ((1.0f - roughness) * w3Spec.x + w3Spec.y));
        // the detail map darkens the colour a little, as the graphs' roughDiffBlend does
        if (w3Detail2.z > 0 && detailWeight > 0)
        {
            albedo *= max(0, 1 - detailRough * detailWeight * distanceBlend * w3Detail2.z);
        }
        // the tint mask's red: ambient occlusion baked in
        s.ao = w3Flags.y > 0 ? lerp(1.0f, mask.r, saturate(w3SpecColor.w)) : 1;
        albedo *= s.ao;
    }

    s.albedo = ToLinear(albedo);
    // hair's colour is kept in its vertices, dark at the roots and light at the tips, encoded as a colour on screen is:
    // taken into linear light first, as the eyebrows and moustache come out as dark as REDkit draws them
    if (w3Glow.y > 0)
    {
        s.albedo *= ToLinear(input.c.rgb);
    }
    // as Helix lays a normal map on: across the tangent, along the bitangent, out along the normal
    float3 N = normalize(input.n);
    if (w3Flags.x > 0)
    {
        N = normalize(n.x * normalize(input.t1) + n.y * normalize(input.t2) + n.z * N);
    }
    // seen from behind (a single-sided card, cloth), its back faces the viewer
    if (dot(N, V) < -0.2f && dot(normalize(input.n), V) < 0)
    {
        N = -N;
    }
    s.normal = N;
    s.roughness = clamp(roughness, 0.03f, 1.0f);
    s.translucency = w3Misc.y;
    return s;
}

float D_GGX(float NdotH, float a)
{
    float a2 = a * a;
    float d = NdotH * NdotH * (a2 - 1) + 1;
    return a2 / (PI_W3 * d * d);
}

float V_Smith(float NdotV, float NdotL, float a)
{
    float k = a * 0.5f;
    return 1.0f / max((NdotV * (1 - k) + k) * (NdotL * (1 - k) + k), 1e-4f);
}

float3 F_Schlick(float3 f0, float VdotH)
{
    return f0 + (1 - f0) * pow(1 - VdotH, 5);
}

// how much of the environment's reflection comes back at an angle, for a roughness (Karis's fit)
float3 EnvironmentBRDF(float3 f0, float roughness, float NdotV)
{
    const float4 c0 = float4(-1, -0.0275f, -0.572f, 0.022f);
    const float4 c1 = float4(1, 0.0425f, 1.04f, -0.04f);
    float4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28f * NdotV)) * r.x + r.y;
    float2 ab = float2(-1.04f, 1.04f) * a004 + r.zw;
    return f0 * ab.x + ab.y;
}

// one light on the surface: diffuse (wrapped for skin, warm where it wraps), GGX shine, a highlight along hair strands
float3 LightOne(Surface s, PSInput input, float3 V, float3 L, float3 radiance)
{
    float3 N = s.normal;
    float NdotL = dot(N, L);
    float wrap = w3Kind.z;
    float lit = saturate((NdotL + wrap) / (1 + wrap));
    float3 diffuse = s.albedo * lit;
    float scatter = saturate(lit - saturate(NdotL));
    diffuse += w3Kind.w * scatter * s.albedo * float3(1.0f, 0.42f, 0.28f);
    // light through thin things (leaves, cloth, ears) from behind
    diffuse += s.translucency * saturate(-NdotL) * s.albedo * 0.5f;

    float3 H = normalize(V + L);
    float NdotV = saturate(abs(dot(N, V)) + 1e-4f);
    float NdotLs = saturate(NdotL);
    float a = s.roughness * s.roughness;
    float3 shine = D_GGX(saturate(dot(N, H)), a) * V_Smith(NdotV, NdotLs, a) * F_Schlick(s.f0, saturate(dot(V, H))) * 0.25f * NdotLs;

    if (w3Kind.x == KIND_HAIR)
    {
        // Kajiya-Kay: the highlight runs along the strands, shifted along them by the shift texture
        float shift = w3Flags.z > 0 ? (texW3Shift.Sample(samplerW3, input.t).r - 0.5f) * w3Flags.w : 0;
        float3 T = normalize(normalize(input.t2) + shift * N);
        float TdotH = dot(T, H);
        float strand = sqrt(saturate(1 - TdotH * TdotH));
        shine = s.f0 * pow(strand, max(2.0f / (a * a + 1e-3f), 8.0f)) * saturate(NdotL + 0.3f) * 0.5f;
    }
    return (diffuse + shine) * radiance;
}

float3 LightColor(float4 c) { return ToLinear(c.rgb) * 1.6f; }

float4 Shade(PSInput input, bool soft)
{
    float3 V = normalize(vEyePos - input.wp.xyz);
    Surface s = ReadSurface(input, V);

    // hair, eyelashes, beards: alpha tested as the game tests them; the soft pass blends their thin ends instead
    if (!soft && w3Kind.y > 0)
    {
        clip(s.alpha - w3Kind.y);
        s.alpha = 1;
    }
    else if (!soft && w3Kind.x != KIND_GLASS)
    {
        s.alpha = 1;
    }

    // the shadow, on the first directional light (the one the shadow map is made from)
    float shadow = 1;
    if (bHasShadowMap && bRenderShadowMap && dot(getLookDir(vLightView), s.normal) > 0)
    {
        shadow = shadowStrength(input.sp);
    }
    float occlusion = 1;
    if (SSAOEnabled)
    {
        float2 quadTex = input.p.xy * vResolution.zw;
        occlusion = lerp(1.0f, texSSAOMap.SampleLevel(samplerSurface, quadTex, 0).r, w3Grade.w);
    }

    float3 colour = 0;
    bool shadowed = false;
    for (int i = 0; i < NumLights; ++i)
    {
        float3 radiance = LightColor(Lights[i].vLightColor);
        if (Lights[i].iLightType == 1)
        {
            float through = shadowed ? 1 : shadow;
            shadowed = true;
            colour += through * LightOne(s, input, V, normalize(Lights[i].vLightDir.xyz), radiance);
        }
        else if (Lights[i].iLightType == 2 || Lights[i].iLightType == 3)
        {
            float3 d = Lights[i].vLightPos.xyz - input.wp.xyz;
            float dl = length(d);
            if (Lights[i].vLightAtt.w < dl)
            {
                continue;
            }
            d /= dl;
            float att = 1.0f / (Lights[i].vLightAtt.x + Lights[i].vLightAtt.y * dl + Lights[i].vLightAtt.z * dl * dl);
            if (Lights[i].iLightType == 3)
            {
                float rho = dot(-d, normalize(Lights[i].vLightDir.xyz));
                att *= pow(saturate((rho - Lights[i].vLightSpot.x) / (Lights[i].vLightSpot.y - Lights[i].vLightSpot.x)), Lights[i].vLightSpot.z);
            }
            colour += att * LightOne(s, input, V, d, radiance);
        }
    }
    // an ambient light a scene sets: light all round, as much again as it says
    colour += s.albedo * ToLinear(vLightAmbient.rgb) * s.ao * occlusion;

    // the light all round, and reflections, from the environment's cube map (or a plain sky without one)
    float3 N = s.normal;
    float NdotV = saturate(abs(dot(N, V)) + 1e-4f);
    float3 ambient;
    float3 reflected;
    if (w3Env.x > 0)
    {
        float top = max(w3Env.w - 1, 0);
        ambient = EnvironmentLight(texW3Env.SampleLevel(samplerW3Clamp, GameDirection(N), top).rgb);
        float3 R = reflect(-V, N);
        reflected = EnvironmentLight(texW3Env.SampleLevel(samplerW3Clamp, GameDirection(R), s.roughness * top).rgb);
    }
    else
    {
        ambient = lerp(w3Light.rgb * 0.45f, w3Light.rgb, saturate(N.y * 0.5f + 0.5f));
        reflected = ambient;
    }
    colour += s.albedo * ambient * w3Env.z * s.ao * occlusion;
    colour += reflected * EnvironmentBRDF(s.f0, s.roughness, NdotV) * w3Env.y * s.ao * occlusion;

    if (w3Kind.x == KIND_EYE && w3Eye.w > 0)
    {
        // the eye's wet highlight, from its own cube map, turned as the game turns it
        float3 R = reflect(-V, N);
        colour += ToLinear(texW3Blick.Sample(samplerW3Clamp, GameDirection(R)).rgb) * w3Eye.w * (input.t.x < 0 ? 0.2f : 1.0f);
    }

    // what glows: pbr_std_tint_mask_det_emissive (the colour times its emissive texture times emissive_multiplier),
    // or a colour where a mask says (emissive_color_mask)
    if (w3Glow.x > 0.5f)
    {
        float3 glowing = texW3Emissive.Sample(samplerW3, input.t).rgb;
        colour += w3Glow.x < 1.5f ? s.albedo * ToLinear(glowing) * w3Emissive.rgb : w3Emissive.rgb * dot(glowing, 0.3333f);
    }

    // tone mapped (ACES, Narkowicz's fit), then graded: saturation and contrast round mid-grey
    colour *= w3Grade.x;
    colour = saturate((colour * (2.51f * colour + 0.03f)) / (colour * (2.43f * colour + 0.59f) + 0.14f));
    float grey = dot(colour, float3(0.2126f, 0.7152f, 0.0722f));
    colour = lerp(grey.xxx, colour, w3Grade.y);
    colour = saturate((colour - 0.18f) * w3Grade.z + 0.18f);
    float3 screen = ToScreen(colour);

    // the glow of a highlighted part, over everything
    screen += input.c2.rgb;

    float alpha = s.alpha;
    if (w3Kind.x == KIND_GLASS)
    {
        alpha = saturate(min(s.alpha, w3Emissive.w) + dot(reflected * EnvironmentBRDF(s.f0, s.roughness, NdotV), 0.33f));
    }
    return float4(saturate(screen), alpha);
}

float4 main(PSInput input) : SV_Target
{
    return Shade(input, false);
}

// the transparent pass (Helix's weighted blended order-independent transparency): hair's soft ends, glass
PSOITOutput mainOIT(PSInput input)
{
    float4 colour = Shade(input, true);
    return calculateOIT(colour, input.vEye.w, input.p.z);
}

#endif
