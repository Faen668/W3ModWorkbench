// The view's particles: Helix's instanced billboards, drawn the way the game's particle shaders blend. Each instance
// is a particle; its matrix carries the particle rather than a transform (row 1: width, height, turn in radians;
// row 4: where it is, in the view's space), its colour is premultiplied by its alpha (an additive particle's alpha is
// 0, so it only adds), and its texture coordinates pick its atlas frame. The quad is built facing the camera.

// Helix packs its matrices by row, as its own shaders read them
#pragma pack_matrix( row_major )

cbuffer cbTransforms : register(b0)
{
    float4x4 mView;
    float4x4 mProjection;
    float4x4 mViewProjection;
};

Texture2D billboardTexture : register(t0);
SamplerState samplerBillboard : register(s7);

// Helix's instanced billboard input, as its input layout has it: the billboard's own vertex (slot 0), the instance's
// matrix (slot 1) and its parameters (slot 2)
struct VSInput
{
    float4 p : POSITION;
    float4 foreground : COLOR;
    float4 background : COLOR1;
    float2 t0 : TEXCOORD0;
    float2 t3 : TEXCOORD1;
    float2 offTL : TEXCOORD2;
    float2 offTR : TEXCOORD3;
    float2 offBL : TEXCOORD4;
    float2 offBR : TEXCOORD5;
    float4 mr0 : TEXCOORD6;
    float4 mr1 : TEXCOORD7;
    float4 mr2 : TEXCOORD8;
    float4 mr3 : TEXCOORD9;
    float4 diffuseC : COLOR2;
    float2 tScale : TEXCOORD10;
    float2 tOffset : TEXCOORD11;
};

// the particle on screen, worked out in the vertex stage (Helix gives the camera's matrices to that stage, not to the
// geometry stage): its middle, and how far a metre to the camera's right and up moves on screen. Projection is linear,
// so the geometry stage only adds those up for the quad's corners.
struct GSInput
{
    float4 middle : POSITION;
    float4 right : TEXCOORD2;
    float4 up : TEXCOORD3;
    float4 color : COLOR;
    float4 frame : TEXCOORD0; // the atlas frame's corner (xy) and size (zw)
    float4 shape : TEXCOORD1; // width, height, turn
};

struct PSInput
{
    float4 p : SV_POSITION;
    float4 color : COLOR;
    float2 t : TEXCOORD0;
};

GSInput vsParticle(VSInput input)
{
    GSInput output;
    float4 eye = mul(float4(input.mr3.xyz, 1), mView);
    output.middle = mul(eye, mProjection);
    output.right = mul(float4(1, 0, 0, 0), mProjection);
    output.up = mul(float4(0, 1, 0, 0), mProjection);
    output.color = input.diffuseC;
    output.frame = float4(input.tOffset, input.tScale);
    output.shape = float4(input.mr0.xyz, 0);
    return output;
}

[maxvertexcount(4)]
void gsParticle(point GSInput input[1], inout TriangleStream<PSInput> stream)
{
    GSInput particle = input[0];
    if (particle.color.a <= 0 && dot(particle.color.rgb, float3(1, 1, 1)) <= 0) return;
    float c = cos(particle.shape.z), s = sin(particle.shape.z);
    float2 extent = particle.shape.xy * 0.5;
    const float2 corners[4] = { float2(-1, 1), float2(1, 1), float2(-1, -1), float2(1, -1) };
    const float2 uvs[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float2 o = corners[i] * extent;
        o = float2(o.x * c - o.y * s, o.x * s + o.y * c);
        PSInput output;
        output.p = particle.middle + o.x * particle.right + o.y * particle.up;
        output.color = particle.color;
        output.t = particle.frame.xy + uvs[i] * particle.frame.zw;
        stream.Append(output);
    }
}

// the texture is premultiplied too: smoke covers what's behind it by its alpha, fire adds to it
float4 psParticle(PSInput input) : SV_Target
{
    return billboardTexture.Sample(samplerBillboard, input.t) * input.color;
}
