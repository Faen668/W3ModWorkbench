using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D9 = SharpDX.Direct3D9;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using DxgiFormat = SharpDX.DXGI.Format;
using RawColor4 = SharpDX.Mathematics.Interop.RawColor4;
using Vector2 = System.Numerics.Vector2;

namespace W3ModWorkbench;

/// <summary>
/// A test of drawing the world map with the graphics card (Direct3D 11), every frame, nothing cached: the terrain and
/// the pictures over it as textured squares, every plant as an instanced, turned square from one atlas of their
/// pictures, the dots as instanced squares a few pixels wide, and the outlines as lines. Shown inside WPF through a
/// D3DImage, as the 3D viewers are. Fed from a <see cref="WorldMap"/>, whose view it takes on and moves as its own.
/// </summary>
public sealed class GpuMapView : FrameworkElement, IDisposable
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

    private readonly D3DImage _image = new();
    private Device? _device;
    private DeviceContext? _context;
    private Texture2D? _target;
    private RenderTargetView? _targetView;
    private D3D9.Direct3DEx? _d3d9;
    private D3D9.DeviceEx? _device9;
    private D3D9.Texture? _shared9;
    private D3D9.Surface? _surface9;
    private Query? _done;
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
    private int _width, _height;
    private bool _dirty = true;

    private double _scale = 0.3;
    private System.Windows.Vector _offset;
    private Point _lastMouse;
    private bool _dragging;

    /// <summary>How long the last frame took the graphics card, in milliseconds, waited for.</summary>
    public double LastFrameMilliseconds { get; private set; }

    /// <summary>How many plants it draws each frame.</summary>
    public int PlantCount => _grassCount + _treeCount;

    public GpuMapView()
    {
        Focusable = true;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        _image.IsFrontBufferAvailableChanged += (_, _) => _dirty = true;
    }

    /// <summary>Takes on everything a 2D map shows and where it's looking.</summary>
    public void ShowFrom(WorldMap map)
    {
        Start();
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
        _dirty = true;
    }

    // ---- set up ----

    private void Start()
    {
        if (_device is not null) return;
        _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        _context = _device.ImmediateContext;
        _d3d9 = new D3D9.Direct3DEx();
        var parameters = new D3D9.PresentParameters
        {
            Windowed = true, SwapEffect = D3D9.SwapEffect.Discard, DeviceWindowHandle = GetDesktopWindow(),
            PresentationInterval = D3D9.PresentInterval.Default,
        };
        _device9 = new D3D9.DeviceEx(_d3d9, 0, D3D9.DeviceType.Hardware, IntPtr.Zero,
            D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded | D3D9.CreateFlags.FpuPreserve, parameters);
        _done = new Query(_device, new QueryDescription { Type = QueryType.Event });

        var vs = (string entry) => ShaderBytecode.Compile(Shaders, entry, "vs_4_0");
        using var quadCode = vs("QuadVS");
        using var plantCode = vs("PlantVS");
        using var dotCode = vs("DotVS");
        using var lineCode = vs("LineVS");
        using var texturedCode = ShaderBytecode.Compile(Shaders, "TexturedPS", "ps_4_0");
        using var colouredCode = ShaderBytecode.Compile(Shaders, "ColouredPS", "ps_4_0");
        _quadVS = new VertexShader(_device, quadCode);
        _plantVS = new VertexShader(_device, plantCode);
        _dotVS = new VertexShader(_device, dotCode);
        _lineVS = new VertexShader(_device, lineCode);
        _texturedPS = new PixelShader(_device, texturedCode);
        _colouredPS = new PixelShader(_device, colouredCode);
        var corner = new[]
        {
            new InputElement("POSITION", 0, DxgiFormat.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElement("TEXCOORD", 0, DxgiFormat.R32G32_Float, 8, 0, InputClassification.PerVertexData, 0),
        };
        _quadLayout = new InputLayout(_device, quadCode, corner);
        _plantLayout = new InputLayout(_device, plantCode, [.. corner,
            new InputElement("PLANT", 0, DxgiFormat.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElement("RECT", 0, DxgiFormat.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1)]);
        _dotLayout = new InputLayout(_device, dotCode, [.. corner,
            new InputElement("DOT", 0, DxgiFormat.R32G32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElement("COLOR", 0, DxgiFormat.R8G8B8A8_UNorm, 8, 1, InputClassification.PerInstanceData, 1)]);
        _lineLayout = new InputLayout(_device, lineCode, [
            new InputElement("POSITION", 0, DxgiFormat.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElement("COLOR", 0, DxgiFormat.R8G8B8A8_UNorm, 8, 0, InputClassification.PerVertexData, 0)]);
        _view = new Buffer(_device, Utilities.SizeOf<View>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        // a square from -1 to 1, as two triangles
        _corners = Buffer.Create(_device, BindFlags.VertexBuffer, new[]
        {
            new Corner { X = -1, Y = -1 }, new Corner { X = -1, Y = 1 }, new Corner { X = 1, Y = 1 },
            new Corner { X = -1, Y = -1 }, new Corner { X = 1, Y = 1 }, new Corner { X = 1, Y = -1 },
        });
        _sampler = new SamplerState(_device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaximumLod = float.MaxValue,
        });
        var blend = new BlendStateDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription(true, BlendOption.One, BlendOption.InverseSourceAlpha, BlendOperation.Add,
            BlendOption.One, BlendOption.InverseSourceAlpha, BlendOperation.Add, ColorWriteMaskFlags.All);
        _blend = new BlendState(_device, blend);
        _raster = new RasterizerState(_device, new RasterizerStateDescription { CullMode = CullMode.None, FillMode = FillMode.Solid });
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    /// <summary>The render target, the size of the view in pixels, shared with Direct3D 9 for the D3DImage.</summary>
    private void Resize()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int width = Math.Max(1, (int)(ActualWidth * dpi)), height = Math.Max(1, (int)(ActualHeight * dpi));
        if (_device is null || (width == _width && height == _height && _target is not null)) return;
        (_width, _height) = (width, height);
        _surface9?.Dispose();
        _shared9?.Dispose();
        _targetView?.Dispose();
        _target?.Dispose();
        _target = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, OptionFlags = ResourceOptionFlags.Shared,
        });
        _targetView = new RenderTargetView(_device, _target);
        IntPtr handle;
        using (var resource = _target.QueryInterface<SharpDX.DXGI.Resource>()) handle = resource.SharedHandle;
        _shared9 = new D3D9.Texture(_device9, width, height, 1, D3D9.Usage.RenderTarget, D3D9.Format.A8R8G8B8, D3D9.Pool.Default, ref handle);
        _surface9 = _shared9.GetSurfaceLevel(0);
        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer);
        _image.Unlock();
        _dirty = true;
    }

    /// <summary>A picture as a texture with its smaller copies, and the square of the world it covers.</summary>
    private (ShaderResourceView, Buffer) Picture(BitmapSource picture, Rect area)
    {
        var texture = Texture(picture);
        float x0 = (float)area.X, y0 = (float)area.Y, x1 = (float)area.Right, y1 = (float)area.Bottom;
        // rows from the north: the picture's top is the area's largest Y
        var quad = Buffer.Create(_device!, BindFlags.VertexBuffer, new[]
        {
            new Corner { X = x0, Y = y0, U = 0, V = 1 }, new Corner { X = x0, Y = y1, U = 0, V = 0 }, new Corner { X = x1, Y = y1, U = 1, V = 0 },
            new Corner { X = x0, Y = y0, U = 0, V = 1 }, new Corner { X = x1, Y = y1, U = 1, V = 0 }, new Corner { X = x1, Y = y0, U = 1, V = 1 },
        });
        return (texture, quad);
    }

    private ShaderResourceView Texture(BitmapSource picture)
    {
        var source = picture.Format == PixelFormats.Pbgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Pbgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        var texture = new Texture2D(_device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 0, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, OptionFlags = ResourceOptionFlags.GenerateMipMaps,
        });
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { _context!.UpdateSubresource(texture, 0, null, handle.AddrOfPinnedObject(), width * 4, 0); }
        finally { handle.Free(); }
        var view = new ShaderResourceView(_device, texture);
        _context!.GenerateMips(view);
        texture.Dispose();
        return view;
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
        Buffer? Instances(IReadOnlyList<MapPlant> plants) => plants.Count == 0 ? null : Buffer.Create(_device!, BindFlags.VertexBuffer,
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
            _dots = Buffer.Create(_device!, BindFlags.VertexBuffer, dots.Select(d => new Dot { X = d.At.X, Y = d.At.Y, Colour = Rgba(d.Colour) }).ToArray());
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
        (_lines, _lineCount) = (vertices.Count > 0 ? Buffer.Create(_device!, BindFlags.VertexBuffer, vertices.ToArray()) : null, vertices.Count);
    }

    private static uint Rgba(Color c) => (uint)(c.R | c.G << 8 | c.B << 16 | c.A << 24);

    // ---- drawing ----

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_dirty || _device is null || !_image.IsFrontBufferAvailable || ActualWidth <= 0) return;
        Resize();
        _dirty = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Draw();
        // wait for the card, so the D3DImage shows a finished frame
        _context!.End(_done);
        _context.Flush();
        while (!_context.IsDataAvailable(_done) && clock.ElapsedMilliseconds < 250) Thread.Yield();
        LastFrameMilliseconds = clock.Elapsed.TotalMilliseconds;
        _image.Lock();
        _image.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
        _image.Unlock();
    }

    private void Draw()
    {
        var c = _context!;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var view = new View
        {
            Map = new System.Numerics.Vector4((float)(_scale * dpi), (float)(_offset.X * dpi), (float)(_offset.Y * dpi), (float)(1.5 * dpi)),
            Screen = new System.Numerics.Vector4(_width, _height, 0, 0),
        };
        c.UpdateSubresource(ref view, _view);
        c.OutputMerger.SetRenderTargets(_targetView);
        c.OutputMerger.SetBlendState(_blend);
        c.Rasterizer.SetViewport(0, 0, _width, _height);
        c.Rasterizer.State = _raster;
        var back = (BackBrush as SolidColorBrush)?.Color ?? Colors.Black;
        c.ClearRenderTargetView(_targetView, new RawColor4(back.R / 255f, back.G / 255f, back.B / 255f, 1));
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

    public Brush BackBrush { get; set; } = Brushes.Black;

    // the Direct3D picture stretched over the whole element; it shows each new frame without being drawn again here
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackBrush, null, new Rect(RenderSize));
        dc.DrawImage(_image, new Rect(RenderSize));
    }

    /// <summary>The map moved: drawn again on the next frame.</summary>
    private void Moved() => _dirty = true;

    // ---- moving ----

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _dirty = true;
    }

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

    public void Dispose()
    {
        CompositionTarget.Rendering -= OnFrame;
        foreach (var (texture, quad) in _pictures)
        {
            texture.Dispose();
            quad.Dispose();
        }
        foreach (var d in new IDisposable?[] { _atlas, _grass, _trees, _dots, _lines, _corners, _view, _sampler, _blend, _raster, _quadLayout, _plantLayout,
            _dotLayout, _lineLayout, _quadVS, _plantVS, _dotVS, _lineVS, _texturedPS, _colouredPS, _done, _surface9, _shared9, _targetView, _target,
            _device9, _d3d9, _device })
            d?.Dispose();
    }
}
