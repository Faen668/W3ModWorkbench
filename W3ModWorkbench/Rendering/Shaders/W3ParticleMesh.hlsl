// The view's mesh particles: Helix's instanced meshes, each instance a particle, drawn as the game's particle shaders
// blend (see W3Particle.hlsl). The instance's diffuse colour is the particle's, premultiplied by its alpha (0 for an
// additive one, so it only adds); its emissive colour carries the texture's atlas frame and scroll: the frame's size
// (xy) and corner, scrolled on (zw).

#pragma pack_matrix( row_major )

// Helix's mesh pixel input, as its instanced mesh vertex shader writes it
struct PSInput
{
    float4 p : SV_POSITION;
    float4 vEye : POSITION0;
    float3 n : NORMAL;
    float4 wp : POSITION1;
    float4 sp : TEXCOORD1;
    float2 t : TEXCOORD0;
    float3 t1 : TANGENT;
    float3 t2 : BINORMAL;
    float4 c : COLOR;
    float4 c2 : COLOR1;
    float4 cDiffuse : COLOR2;
};

Texture2D texDiffuseMap : register(t0);
SamplerState samplerSurface : register(s0);

float4 main(PSInput input) : SV_Target
{
    float2 uv = input.t * input.c2.xy + input.c2.zw;
    float4 texel = texDiffuseMap.Sample(samplerSurface, uv);
    float facing = abs(dot(normalize(input.n), normalize(input.vEye.xyz)));
    // one that adds light (fire, glow, shields) takes the texture's brightness and the particle's colour, as the game's
    // effect materials do (their graphs desaturate the texture and colour it, and fade it to the mesh's edges); the
    // brightness is lifted as their graphs lift it
    if (input.cDiffuse.a <= 0)
    {
        float brightness = dot(texel.rgb, float3(0.3, 0.59, 0.11));
        return float4(input.cDiffuse.rgb * brightness * 2.5 * (0.35 + 0.65 * facing), 0);
    }
    // one that covers what's behind it (debris, leaves) keeps the texture's colours, shaded a little by how it faces you
    float4 c = texel * input.cDiffuse;
    c.rgb *= 0.45 + 0.55 * facing;
    return c;
}
