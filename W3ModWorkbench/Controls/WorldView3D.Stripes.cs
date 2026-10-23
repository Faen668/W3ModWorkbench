using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFormat = SharpDX.DXGI.Format;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace W3ModWorkbench;

/// <summary>A stripe's vertex in the world: where, its texture coordinates (across, along), how opaque, which way the stripe runs.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StripeVertex3D
{
    public Vector3 Position;
    public Vector2 Uv;
    public float Alpha;
    public Vector3 Along;
}

/// <summary>The layers' stripes that share a colour and normal texture, their triangles in the world.</summary>
public sealed record StripeBatch3D(BitmapSource? Diffuse, BitmapSource? Normal, StripeVertex3D[] Vertices, int[] Indices);

// The layers' stripes (paths and roads laid over the ground, CStripeComponent): the triangles the game cooked for each,
// laid over the drawn ground, lit as it is, see-through as their texture and their ends say.
public sealed partial class WorldView3D
{
    private sealed record StripeGpu(Buffer Vertices, Buffer Indices, int Count, ShaderResourceView? Diffuse, ShaderResourceView? Normal);

    private readonly List<StripeGpu> _stripes = [];
    private VertexShader? _stripeVS;
    private PixelShader? _stripePS;
    private InputLayout? _stripeLayout;
    private DepthStencilState? _stripeDepth;

    private void StartStripes()
    {
        using (var code = Compile(Shaders, "StripeVS", "vs_4_0"))
        {
            _stripeVS = new VertexShader(Device, code);
            _stripeLayout = new InputLayout(Device, code, [
                new InputElement("POSITION", 0, DxgiFormat.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
                new InputElement("TEXCOORD", 0, DxgiFormat.R32G32_Float, 12, 0, InputClassification.PerVertexData, 0),
                new InputElement("TEXCOORD", 1, DxgiFormat.R32_Float, 20, 0, InputClassification.PerVertexData, 0),
                new InputElement("TEXCOORD", 2, DxgiFormat.R32G32B32_Float, 24, 0, InputClassification.PerVertexData, 0)]);
        }
        using (var code = Compile(Shaders, "StripePS", "ps_4_0")) _stripePS = new PixelShader(Device, code);
        _stripeDepth = new DepthStencilState(Device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.Zero, DepthComparison = Comparison.LessEqual,
        });
    }

    /// <summary>The layers' stripes, replacing those set before.</summary>
    public void SetStripes(IReadOnlyList<StripeBatch3D> batches)
    {
        EnsureDevice();
        DisposeStripes();
        var pictures = new Dictionary<BitmapSource, ShaderResourceView>();
        ShaderResourceView? Picture(BitmapSource? picture, bool straight)
        {
            if (picture is null) return null;
            if (pictures.TryGetValue(picture, out var known)) return known;
            return pictures[picture] = straight ? StraightTexture(picture) : CutoutTexture(picture);
        }
        foreach (var batch in batches)
        {
            if (batch.Vertices.Length == 0 || batch.Indices.Length == 0) continue;
            _stripes.Add(new StripeGpu(Buffer.Create(Device, BindFlags.VertexBuffer, batch.Vertices), Buffer.Create(Device, BindFlags.IndexBuffer, batch.Indices),
                batch.Indices.Length, Picture(batch.Diffuse, false), Picture(batch.Normal, true)));
        }
        Invalidate();
    }

    /// <summary>The stripes over the ground just drawn, blended in by their alpha.</summary>
    private void DrawStripes()
    {
        if (_stripes.Count == 0) return;
        var c = Context!;
        c.OutputMerger.SetDepthStencilState(_stripeDepth);
        c.OutputMerger.SetBlendState(_waterBlend);
        c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        c.InputAssembler.InputLayout = _stripeLayout;
        c.VertexShader.Set(_stripeVS);
        c.PixelShader.Set(_stripePS);
        foreach (var stripe in _stripes)
        {
            c.PixelShader.SetShaderResources(21, stripe.Diffuse ?? _meshGrey, stripe.Normal);
            c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(stripe.Vertices, Utilities.SizeOf<StripeVertex3D>(), 0));
            c.InputAssembler.SetIndexBuffer(stripe.Indices, DxgiFormat.R32_UInt, 0);
            c.DrawIndexed(stripe.Count, 0, 0);
        }
        c.PixelShader.SetShaderResources(21, null, null);
        c.OutputMerger.SetBlendState(null);
        c.OutputMerger.SetDepthStencilState(null);
    }

    private void DisposeStripes()
    {
        var views = new HashSet<ShaderResourceView>();
        foreach (var s in _stripes)
        {
            s.Vertices.Dispose();
            s.Indices.Dispose();
            if (s.Diffuse is not null) views.Add(s.Diffuse);
            if (s.Normal is not null) views.Add(s.Normal);
        }
        foreach (var v in views) v.Dispose();
        _stripes.Clear();
    }

    private void DisposeStripeDevice()
    {
        DisposeStripes();
        foreach (var d in new IDisposable?[] { _stripeVS, _stripePS, _stripeLayout, _stripeDepth }) d?.Dispose();
    }
}
