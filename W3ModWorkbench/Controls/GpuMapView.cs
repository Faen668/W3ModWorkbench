using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using DxgiFormat = SharpDX.DXGI.Format;
using RawColor4 = SharpDX.Mathematics.Interop.RawColor4;
using Vector2 = System.Numerics.Vector2;

namespace W3ModWorkbench;

/// <summary>
/// A test of drawing the world map with the graphics card (Direct3D 11), every frame, nothing cached: the terrain and
/// the pictures over it as textured squares, every plant as an instanced, turned square from one atlas of their
/// pictures, the dots as instanced squares a few pixels wide, and the outlines as lines. Shown inside WPF through a
/// D3DImage (<see cref="D3DHost"/>), as the 3D viewers are. Fed from a <see cref="WorldMap"/>, whose view it takes on and moves as its own.
/// </summary>
public sealed class GpuMapView : D3DHost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct View
    {
        public System.Numerics.Vector4 Map;    // scale, offset X, offset Y, dot radius in pixels
        public System.Numerics.Vector4 Screen; // width, height
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Plant
    {
        public float X, Y, Yaw, Reach, U0, V0, U1, V1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot
    {
        public float X, Y;
        public uint Colour;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Corner
    {
        public float X, Y, U, V;
    }

    // loc-ignore-start: the shaders' source
    private const string Shaders = @"
cbuffer View : register(b0) { float4 Map; float4 Screen; };
Texture2D Picture : register(t0);
SamplerState Smooth : register(s0);

float4 Place(float2 world, float2 pixels)
{
    float2 at = float2(world.x * Map.x + Map.y, -world.y * Map.x + Map.z) + pixels;
    return float4(at.x / Screen.x * 2 - 1, 1 - at.y / Screen.y * 2, 0, 1);
}

struct Textured { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
struct Coloured { float4 Position : SV_Position; float4 Colour : COLOR0; };

Textured QuadVS(float2 world : POSITION, float2 uv : TEXCOORD0)
{
    Textured o; o.Position = Place(world, 0); o.Uv = uv; return o;
}

Textured PlantVS(float2 corner : POSITION, float2 unused : TEXCOORD0, float4 plant : PLANT, float4 rect : RECT)
{
    float c = cos(plant.z), s = sin(plant.z);
    float2 turned = float2(c * corner.x - s * corner.y, s * corner.x + c * corner.y) * plant.w;
    Textured o;
    o.Position = Place(plant.xy + turned, 0);
    o.Uv = lerp(rect.xy, rect.zw, float2(corner.x * 0.5 + 0.5, 0.5 - corner.y * 0.5));
    return o;
}

Coloured DotVS(float2 corner : POSITION, float2 unused : TEXCOORD0, float2 at : DOT, float4 colour : COLOR0)
{
    Coloured o; o.Position = Place(at, corner * Map.w); o.Colour = colour; return o;
}

Coloured LineVS(float2 world : POSITION, float4 colour : COLOR0)
{
    Coloured o; o.Position = Place(world, 0); o.Colour = colour; return o;
}

float4 TexturedPS(Textured i) : SV_Target { return Picture.Sample(Smooth, i.Uv); }
float4 ColouredPS(Coloured i) : SV_Target { return float4(i.Colour.rgb * i.Colour.a, i.Colour.a); }
";
    // loc-ignore-end

    private VertexShader? _quadVS, _plantVS, _dotVS, _lineVS;
    private PixelShader? _texturedPS, _colouredPS;
    private InputLayout? _quadLayout, _plantLayout, _dotLayout, _lineLayout;
    private Buffer? _view, _corners;
    private SamplerState? _sampler;
    private BlendState? _blend;
    private RasterizerState? _raster;
    private readonly List<(ShaderResourceView Texture, Buffer Quad)> _pictures = [];
    private ShaderResourceView? _atlas;
    private Buffer? _grass, _trees, _dots, _lines;
    private int _grassCount, _treeCount, _dotCount, _lineCount;

    private double _scale = 0.3;
    private System.Windows.Vector _offset;
    private Point _lastMouse;
    private bool _dragging;

    /// <summary>How many plants it draws each frame.</summary>
    public int PlantCount => _grassCount + _treeCount;

    /// <summary>Takes on everything a 2D map shows and where it's looking.</summary>
    public void ShowFrom(WorldMap map)
    {
        EnsureDevice();
        var content = map.Content();
        foreach (var (texture, quad) in _pictures)
        {
            texture.Dispose();
            quad.Dispose();
        }
        _pictures.Clear();
        if (content.Terrain is { } terrain) _pictures.Add(Picture(terrain.Picture, terrain.Area));
        foreach (var (picture, area) in content.Overlays) _pictures.Add(Picture(picture, area));
        Plants(content.Trees, content.Grass);
        Dots(content.Dots);
        Lines(content.Shapes);
        (_scale, _offset) = (content.Scale, content.Offset);
        Invalidate();
    }

    // ---- set up ----

    protected override void Start()
    {
        var vs = (string entry) => ShaderBytecode.Compile(Shaders, entry, "vs_4_0");
        using var quadCode = vs("QuadVS");
        using var plantCode = vs("PlantVS");
        using var dotCode = vs("DotVS");
        using var lineCode = vs("LineVS");
        using var texturedCode = ShaderBytecode.Compile(Shaders, "TexturedPS", "ps_4_0");
        using var colouredCode = ShaderBytecode.Compile(Shaders, "ColouredPS", "ps_4_0");
        _quadVS = new VertexShader(Device, quadCode);
        _plantVS = new VertexShader(Device, plantCode);
        _dotVS = new VertexShader(Device, dotCode);
        _lineVS = new VertexShader(Device, lineCode);
        _texturedPS = new PixelShader(Device, texturedCode);
        _colouredPS = new PixelShader(Device, colouredCode);
        var corner = new[]
        {
            new InputElement("POSITION", 0, DxgiFormat.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElement("TEXCOORD", 0, DxgiFormat.R32G32_Float, 8, 0, InputClassification.PerVertexData, 0),
        };
        _quadLayout = new InputLayout(Device, quadCode, corner);
        _plantLayout = new InputLayout(Device, plantCode, [.. corner,
            new InputElement("PLANT", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElement("RECT", 0, DxgiFormat.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1)]);
        _dotLayout = new InputLayout(Device, dotCode, [.. corner,
            new InputElement("DOT", 0, DxgiFormat.R32G32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElement("COLOR", 0, DxgiFormat.R8G8B8A8_UNorm, 8, 1, InputClassification.PerInstanceData, 1)]);
        _lineLayout = new InputLayout(Device, lineCode, [
            new InputElement("POSITION", 0, DxgiFormat.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElement("COLOR", 0, DxgiFormat.R8G8B8A8_UNorm, 8, 0, InputClassification.PerVertexData, 0)]);
        _view = new Buffer(Device, Utilities.SizeOf<View>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        // a square from -1 to 1, as two triangles
        _corners = Buffer.Create(Device, BindFlags.VertexBuffer, new[]
        {
            new Corner { X = -1, Y = -1 }, new Corner { X = -1, Y = 1 }, new Corner { X = 1, Y = 1 },
            new Corner { X = -1, Y = -1 }, new Corner { X = 1, Y = 1 }, new Corner { X = 1, Y = -1 },
        });
        _sampler = new SamplerState(Device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue,
        });
        var blend = new BlendStateDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription(true, BlendOption.One, BlendOption.InverseSourceAlpha, BlendOperation.Add,
            BlendOption.One, BlendOption.InverseSourceAlpha, BlendOperation.Add, ColorWriteMaskFlags.All);
        _blend = new BlendState(Device, blend);
        _raster = new RasterizerState(Device, new RasterizerStateDescription { CullMode = CullMode.None, FillMode = FillMode.Solid });
    }

    /// <summary>A picture as a texture with its smaller copies, and the square of the world it covers.</summary>
    private (ShaderResourceView, Buffer) Picture(BitmapSource picture, Rect area)
    {
        var texture = Texture(picture);
        float x0 = (float)area.X, y0 = (float)area.Y, x1 = (float)area.Right, y1 = (float)area.Bottom;
        // rows from the north: the picture's top is the area's largest Y
        var quad = Buffer.Create(Device!, BindFlags.VertexBuffer, new[]
        {
            new Corner { X = x0, Y = y0, U = 0, V = 1 }, new Corner { X = x0, Y = y1, U = 0, V = 0 }, new Corner { X = x1, Y = y1, U = 1, V = 0 },
            new Corner { X = x0, Y = y0, U = 0, V = 1 }, new Corner { X = x1, Y = y1, U = 1, V = 0 }, new Corner { X = x1, Y = y0, U = 1, V = 1 },
        });
        return (texture, quad);
    }

    /// <summary>Every plant as an instance: its foot, turn and reach, and where its picture is in one atlas of them all.</summary>
    private void Plants(IReadOnlyList<MapPlant> trees, IReadOnlyList<MapPlant> grass)
    {
        _atlas?.Dispose();
        _grass?.Dispose();
        _trees?.Dispose();
        (_atlas, _grass, _trees, _grassCount, _treeCount) = (null, null, null, 0, 0);
        var pictures = trees.Concat(grass).Select(p => p.Picture).Distinct().ToList();
        if (pictures.Count == 0) return;
        const int slot = 128;
        int perRow = (int)Math.Ceiling(Math.Sqrt(pictures.Count)), side = perRow * slot;
        var atlas = new byte[side * side * 4];
        var rects = new Dictionary<BitmapSource, (float, float, float, float)>();
        for (int i = 0; i < pictures.Count; i++)
        {
            var source = pictures[i].Format == PixelFormats.Pbgra32 ? pictures[i] : new FormatConvertedBitmap(pictures[i], PixelFormats.Pbgra32, null, 0);
            int w = Math.Min(slot, source.PixelWidth), h = Math.Min(slot, source.PixelHeight), ax = i % perRow * slot, ay = i / perRow * slot;
            var pixels = new byte[w * h * 4];
            source.CopyPixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
            for (int y = 0; y < h; y++) Array.Copy(pixels, y * w * 4, atlas, ((ay + y) * side + ax) * 4, w * 4);
            rects[pictures[i]] = ((ax + 0.5f) / side, (ay + 0.5f) / side, (ax + w - 0.5f) / side, (ay + h - 0.5f) / side);
        }
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Pbgra32, null, atlas, side * 4);
        _atlas = Texture(bitmap);
        Buffer? Instances(IReadOnlyList<MapPlant> plants) => plants.Count == 0 ? null : Buffer.Create(Device!, BindFlags.VertexBuffer,
            plants.OrderBy(p => p.Reach).Select(p =>
            {
                var (u0, v0, u1, v1) = rects[p.Picture];
                return new Plant { X = p.At.X, Y = p.At.Y, Yaw = p.Yaw, Reach = p.Reach, U0 = u0, V0 = v0, U1 = u1, V1 = v1 };
            }).ToArray());
        (_grass, _grassCount) = (Instances(grass), grass.Count);
        (_trees, _treeCount) = (Instances(trees), trees.Count);
    }

    private void Dots(IReadOnlyList<(Vector2 At, Color Colour)> dots)
    {
        _dots?.Dispose();
        (_dots, _dotCount) = (null, dots.Count);
        if (dots.Count > 0)
            _dots = Buffer.Create(Device!, BindFlags.VertexBuffer, dots.Select(d => new Dot { X = d.At.X, Y = d.At.Y, Colour = Rgba(d.Colour) }).ToArray());
    }

    private void Lines(IReadOnlyList<MapShape> shapes)
    {
        _lines?.Dispose();
        var vertices = new List<Dot>();
        foreach (var shape in shapes)
            for (int i = 0; i + 1 < shape.Points.Count + (shape.Closed ? 1 : 0); i++)
            {
                var (a, b) = (shape.Points[i], shape.Points[(i + 1) % shape.Points.Count]);
                vertices.Add(new Dot { X = a.X, Y = a.Y, Colour = Rgba(shape.Color) });
                vertices.Add(new Dot { X = b.X, Y = b.Y, Colour = Rgba(shape.Color) });
            }
        (_lines, _lineCount) = (vertices.Count > 0 ? Buffer.Create(Device!, BindFlags.VertexBuffer, vertices.ToArray()) : null, vertices.Count);
    }

    private static uint Rgba(Color c) => (uint)(c.R | c.G << 8 | c.B << 16 | c.A << 24);

    // ---- drawing ----

    protected override void Draw(double seconds)
    {
        var c = Context!;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var view = new View
        {
            Map = new System.Numerics.Vector4((float)(_scale * dpi), (float)(_offset.X * dpi), (float)(_offset.Y * dpi), (float)(1.5 * dpi)),
            Screen = new System.Numerics.Vector4(PixelWidth, PixelHeight, 0, 0),
        };
        c.UpdateSubresource(ref view, _view);
        // flat, drawn in order: no depth test
        c.OutputMerger.SetRenderTargets(TargetView);
        c.OutputMerger.SetBlendState(_blend);
        c.Rasterizer.State = _raster;
        var back = (BackBrush as SolidColorBrush)?.Color ?? Colors.Black;
        c.ClearRenderTargetView(TargetView, new RawColor4(back.R / 255f, back.G / 255f, back.B / 255f, 1));
        c.VertexShader.SetConstantBuffer(0, _view);
        c.PixelShader.SetSampler(0, _sampler);

        c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        c.InputAssembler.InputLayout = _quadLayout;
        c.VertexShader.Set(_quadVS);
        c.PixelShader.Set(_texturedPS);
        foreach (var (texture, quad) in _pictures)
        {
            c.PixelShader.SetShaderResource(0, texture);
            c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(quad, Utilities.SizeOf<Corner>(), 0));
            c.Draw(6, 0);
        }

        if (_atlas is not null)
        {
            c.InputAssembler.InputLayout = _plantLayout;
            c.VertexShader.Set(_plantVS);
            c.PixelShader.SetShaderResource(0, _atlas);
            foreach (var (plants, count) in new[] { (_grass, _grassCount), (_trees, _treeCount) })
            {
                if (plants is null) continue;
                c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_corners, Utilities.SizeOf<Corner>(), 0), new VertexBufferBinding(plants, Utilities.SizeOf<Plant>(), 0));
                c.DrawInstanced(6, count, 0, 0);
            }
        }

        c.PixelShader.Set(_colouredPS);
        if (_lines is not null)
        {
            c.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineList;
            c.InputAssembler.InputLayout = _lineLayout;
            c.VertexShader.Set(_lineVS);
            c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_lines, Utilities.SizeOf<Dot>(), 0));
            c.Draw(_lineCount, 0);
        }
        if (_dots is not null)
        {
            c.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            c.InputAssembler.InputLayout = _dotLayout;
            c.VertexShader.Set(_dotVS);
            c.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_corners, Utilities.SizeOf<Corner>(), 0), new VertexBufferBinding(_dots, Utilities.SizeOf<Dot>(), 0));
            c.DrawInstanced(6, _dotCount, 0, 0);
        }
    }

    /// <summary>The map moved: drawn again on the next frame.</summary>
    private void Moved() => Invalidate();

    // ---- moving ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var at = e.GetPosition(this);
        double scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.25 : 1 / 1.25), 0.01, 80);
        double factor = scale / _scale;
        _offset = new System.Windows.Vector(at.X - (at.X - _offset.X) * factor, at.Y - (at.Y - _offset.Y) * factor);
        _scale = scale;
        Moved();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _lastMouse = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(this);
        _offset += at - _lastMouse;
        _lastMouse = at;
        Moved();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    public override void Dispose()
    {
        foreach (var (texture, quad) in _pictures)
        {
            texture.Dispose();
            quad.Dispose();
        }
        foreach (var d in new IDisposable?[] { _atlas, _grass, _trees, _dots, _lines, _corners, _view, _sampler, _blend, _raster, _quadLayout, _plantLayout,
            _dotLayout, _lineLayout, _quadVS, _plantVS, _dotVS, _lineVS, _texturedPS, _colouredPS })
            d?.Dispose();
        base.Dispose();
    }
}
