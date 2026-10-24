using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D9 = SharpDX.Direct3D9;
using Device = SharpDX.Direct3D11.Device;
using DxgiFormat = SharpDX.DXGI.Format;

namespace W3ModWorkbench;

/// <summary>
/// An element drawn with the graphics card (Direct3D 11) and shown inside WPF through a D3DImage: a render target and
/// depth buffer the size of the element in pixels, shared with Direct3D 9 for the D3DImage, and a frame drawn whenever
/// one is asked for (or every frame while <see cref="Animating"/>), waited for so WPF shows a finished one. Subclasses
/// set themselves up in <see cref="Start"/> and draw in <see cref="Draw"/>.
/// </summary>
public abstract class D3DHost : FrameworkElement, IDisposable
{
    private readonly D3DImage _image = new();
    private D3D9.Direct3DEx? _d3d9;
    private D3D9.DeviceEx? _device9;
    private D3D9.Texture? _shared9;
    private D3D9.Surface? _surface9;
    // _work is drawn into; _target, shared with Direct3D 9, is what WPF shows, each finished frame copied into it
    private Texture2D? _target, _work, _depth;
    private Query? _done;
    private bool _dirty = true, _hooked;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrame;
    private readonly List<IDisposable> _owned = [];

    protected Device? Device { get; private set; }
    protected DeviceContext? Context { get; private set; }
    protected RenderTargetView? TargetView { get; private set; }
    protected DepthStencilView? DepthView { get; private set; }

    /// <summary>The depth buffer for testing without writing, so it can be read as a texture at the same time.</summary>
    protected DepthStencilView? ReadOnlyDepthView { get; private set; }

    /// <summary>The depth buffer as a texture (0 near to 1 far), for water and the like to see what's behind them.</summary>
    protected ShaderResourceView? DepthTexture { get; private set; }

    /// <summary>A copy of the frame drawn so far, made by <see cref="CopyScene"/>, for refraction.</summary>
    protected ShaderResourceView? SceneTexture { get; private set; }
    private Texture2D? _sceneCopy;

    /// <summary>
    /// Draw the scene in high dynamic range (16-bit float colour, <see cref="HdrView"/>) and leave it to the subclass to
    /// bring it down to <see cref="TargetView"/> at the end, as a game's tone mapping does; set before the first frame.
    /// </summary>
    protected bool HighDynamicRange { get; init; }

    /// <summary>The high dynamic range target the scene is drawn into when <see cref="HighDynamicRange"/> (its top level).</summary>
    protected RenderTargetView? HdrView { get; private set; }

    /// <summary>The high dynamic range scene as a texture, with all its smaller levels (made by <see cref="AverageHdr"/>) down to one pixel.</summary>
    protected ShaderResourceView? HdrTexture { get; private set; }
    private Texture2D? _hdr;

    /// <summary>The render target's size in pixels.</summary>
    protected int PixelWidth { get; private set; }
    protected int PixelHeight { get; private set; }

    /// <summary>Draw every frame, not only when asked (while something moves).</summary>
    protected bool Animating { get; set; }

    /// <summary>How long the last frame took the graphics card, in milliseconds, waited for.</summary>
    public double LastFrameMilliseconds { get; private set; }

    public Brush BackBrush { get; set; } = Brushes.Black;

    protected D3DHost()
    {
        Focusable = true;
        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
        IsVisibleChanged += (_, _) => Hook(IsVisible && IsLoaded);
        _image.IsFrontBufferAvailableChanged += (_, _) => _dirty = true;
    }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
        if (on) FrameStats.EnabledChanged += ShowStats;
        else FrameStats.EnabledChanged -= ShowStats;
        ShowStats();
        _dirty = true;
    }

    // the frame rate panel, when Settings has it on: its frames noted as they're drawn, the panel redrawn twice a second
    private readonly FrameStats _stats = new();
    private System.Windows.Threading.DispatcherTimer? _statsTimer;

    private void ShowStats()
    {
        bool show = _hooked && FrameStats.Enabled;
        if (show && _statsTimer is null)
        {
            _statsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (_, _) => InvalidateVisual();
        }
        if (show) _statsTimer!.Start();
        else _statsTimer?.Stop();
        InvalidateVisual();
    }

    /// <summary>Ask for a frame to be drawn.</summary>
    protected void Invalidate() => _dirty = true;

    /// <summary>Makes the devices the first time; then <see cref="Start"/> sets up what the subclass draws with.</summary>
    protected void EnsureDevice()
    {
        if (Device is not null) return;
        Device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        Context = Device.ImmediateContext;
        _d3d9 = new D3D9.Direct3DEx();
        var parameters = new D3D9.PresentParameters
        {
            Windowed = true, SwapEffect = D3D9.SwapEffect.Discard, DeviceWindowHandle = GetDesktopWindow(),
            PresentationInterval = D3D9.PresentInterval.Default,
        };
        _device9 = new D3D9.DeviceEx(_d3d9, 0, D3D9.DeviceType.Hardware, IntPtr.Zero,
            D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded | D3D9.CreateFlags.FpuPreserve, parameters);
        _done = new Query(Device, new QueryDescription { Type = QueryType.Event });
        Start();
    }

    /// <summary>Sets up the shaders, states and buffers the subclass draws with.</summary>
    protected abstract void Start();

    /// <summary>Draws a frame into <see cref="TargetView"/>; <paramref name="seconds"/> since the last one.</summary>
    protected abstract void Draw(double seconds);

    /// <summary>Keeps something to dispose of with the element.</summary>
    protected T Own<T>(T thing) where T : IDisposable
    {
        _owned.Add(thing);
        return thing;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    /// <summary>A shader compiled from HLSL source.</summary>
    protected static ShaderBytecode Compile(string source, string entry, string profile) => ShaderBytecode.Compile(source, entry, profile);

    /// <summary>
    /// A cutout picture's texture (leaves, grass): its alpha kept straight, not multiplied into the colour, and the
    /// opaque parts' colour carried into the clear ones, so neither the cutout's edge nor its smaller copies turn dark.
    /// </summary>
    protected ShaderResourceView CutoutTexture(BitmapSource picture)
    {
        if (StoredTexture(picture) is { } stored) return stored;
        var source = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight;
        var pixels = new byte[w * h * 4];
        source.CopyPixels(pixels, w * 4, 0);
        // the opaque texels' average colour into every clear texel, then each clear texel from its nearest opaque
        // neighbours a few times, so the edges keep the leaves' own colours
        long r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] >= 128) { b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; n++; }
        if (n > 0)
        {
            var filled = new bool[w * h];
            for (int i = 0; i < w * h; i++) filled[i] = pixels[i * 4 + 3] >= 128;
            for (int pass = 0; pass < 8; pass++)
            {
                var next = (bool[])filled.Clone();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (filled[i]) continue;
                        int sb = 0, sg = 0, sr = 0, c = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= w || yy >= h || !filled[yy * w + xx]) continue;
                                int j = (yy * w + xx) * 4;
                                sb += pixels[j]; sg += pixels[j + 1]; sr += pixels[j + 2]; c++;
                            }
                        if (c == 0) continue;
                        pixels[i * 4] = (byte)(sb / c); pixels[i * 4 + 1] = (byte)(sg / c); pixels[i * 4 + 2] = (byte)(sr / c);
                        next[i] = true;
                    }
                filled = next;
            }
            for (int i = 0; i < w * h; i++)
                if (!filled[i]) { pixels[i * 4] = (byte)(b / n); pixels[i * 4 + 1] = (byte)(g / n); pixels[i * 4 + 2] = (byte)(r / n); }
        }
        return Texture(pixels, w, h, DxgiFormat.B8G8R8A8_UNorm, 4);
    }

    /// <summary>
    /// The texture's own blocks a picture was decoded from, at full size with the game's mip levels and its alpha
    /// straight; null when there are none. The picture kept in memory is a small copy (GpuTextures.KeptSize), so a
    /// texture made from it was a quarter of the game's size or less: grass atlases' plumes turned into broad blobs.
    /// </summary>
    protected ShaderResourceView? StoredTexture(BitmapSource picture) =>
        GpuTextures.StoredBlocks(picture) is { } dds && Core.Textures.Dds.TryRead(dds, out var info, out var pixels) && info.Slices == 1 && !info.IsCube
            ? TextureArray(info, pixels) : null;

    /// <summary>A texture made from a picture, with its smaller copies.</summary>
    protected ShaderResourceView Texture(BitmapSource picture)
    {
        var source = picture.Format == PixelFormats.Pbgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return Texture(pixels, source.PixelWidth, source.PixelHeight, DxgiFormat.B8G8R8A8_UNorm, 4);
    }

    /// <summary>A texture made from raw pixels in a format, with its smaller copies made on the card.</summary>
    protected ShaderResourceView Texture<T>(T[] pixels, int width, int height, DxgiFormat format, int bytesPerPixel) where T : struct
    {
        var texture = new Texture2D(Device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 0, ArraySize = 1, Format = format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, OptionFlags = ResourceOptionFlags.GenerateMipMaps,
        });
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { Context!.UpdateSubresource(texture, 0, null, handle.AddrOfPinnedObject(), width * bytesPerPixel, 0); }
        finally { handle.Free(); }
        var view = new ShaderResourceView(Device, texture);
        Context!.GenerateMips(view);
        texture.Dispose();
        return view;
    }

    /// <summary>
    /// A texture array from the game's texture cache as it's stored: its block-compressed slices with their own mip
    /// levels (the cache keeps each level of every slice before the next level). Null for a format the card can't take.
    /// </summary>
    protected ShaderResourceView? TextureArray(Core.Textures.TextureInfo info, byte[] pixels)
    {
        var (format, blockBytes) = info.Format switch
        {
            Core.Textures.TextureFormat.Bc1 => (DxgiFormat.BC1_UNorm, 8),
            Core.Textures.TextureFormat.Bc3 => (DxgiFormat.BC3_UNorm, 16),
            Core.Textures.TextureFormat.Bc4 => (DxgiFormat.BC4_UNorm, 8),
            Core.Textures.TextureFormat.Bc5 => (DxgiFormat.BC5_UNorm, 16),
            Core.Textures.TextureFormat.Bc7 => (DxgiFormat.BC7_UNorm, 16),
            Core.Textures.TextureFormat.Rgba8 => (DxgiFormat.R8G8B8A8_UNorm, 0),
            _ => (DxgiFormat.Unknown, 0),
        };
        if (format == DxgiFormat.Unknown) return null;
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var boxes = new SharpDX.DataBox[info.Slices * info.Mips];
            for (int slice = 0; slice < info.Slices; slice++)
                for (int level = 0; level < info.Mips; level++)
                {
                    int width = Math.Max(1, info.Width >> level);
                    int pitch = blockBytes == 0 ? width * 4 : Math.Max(1, (width + 3) / 4) * blockBytes;
                    int offset = Core.Textures.TextureDecoder.LevelOffset(info, level, slice);
                    if (offset + pitch > pixels.Length) return null;
                    boxes[slice * info.Mips + level] = new SharpDX.DataBox(handle.AddrOfPinnedObject() + offset, pitch, 0);
                }
            using var texture = new Texture2D(Device, new Texture2DDescription
            {
                Width = info.Width, Height = info.Height, MipLevels = info.Mips, ArraySize = info.Slices, Format = format,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, boxes);
            return new ShaderResourceView(Device, texture);
        }
        finally { handle.Free(); }
    }

    /// <summary>The render target and depth buffer, the size of the element in pixels, shared with Direct3D 9 for the D3DImage.</summary>
    private void Resize()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int width = Math.Max(1, (int)(ActualWidth * dpi)), height = Math.Max(1, (int)(ActualHeight * dpi));
        if (Device is null || (width == PixelWidth && height == PixelHeight && _target is not null)) return;
        (PixelWidth, PixelHeight) = (width, height);
        _surface9?.Dispose();
        _shared9?.Dispose();
        TargetView?.Dispose();
        _target?.Dispose();
        _work?.Dispose();
        DepthView?.Dispose();
        ReadOnlyDepthView?.Dispose();
        DepthTexture?.Dispose();
        _depth?.Dispose();
        SceneTexture?.Dispose();
        _sceneCopy?.Dispose();
        HdrView?.Dispose();
        HdrTexture?.Dispose();
        _hdr?.Dispose();
        var sceneFormat = HighDynamicRange ? DxgiFormat.R16G16B16A16_Float : DxgiFormat.B8G8R8A8_UNorm;
        if (HighDynamicRange)
        {
            _hdr = new Texture2D(Device, new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = 0, ArraySize = 1, Format = sceneFormat,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, OptionFlags = ResourceOptionFlags.GenerateMipMaps,
            });
            HdrView = new RenderTargetView(Device, _hdr, new RenderTargetViewDescription
            {
                Format = sceneFormat, Dimension = RenderTargetViewDimension.Texture2D, Texture2D = new RenderTargetViewDescription.Texture2DResource { MipSlice = 0 },
            });
            HdrTexture = new ShaderResourceView(Device, _hdr);
        }
        _target = new Texture2D(Device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, OptionFlags = ResourceOptionFlags.Shared,
        });
        _work = new Texture2D(Device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        TargetView = new RenderTargetView(Device, _work);
        _depth = new Texture2D(Device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.R32_Typeless,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
        });
        DepthView = new DepthStencilView(Device, _depth, new DepthStencilViewDescription { Format = DxgiFormat.D32_Float, Dimension = DepthStencilViewDimension.Texture2D });
        ReadOnlyDepthView = new DepthStencilView(Device, _depth, new DepthStencilViewDescription
        {
            Format = DxgiFormat.D32_Float, Dimension = DepthStencilViewDimension.Texture2D, Flags = DepthStencilViewFlags.ReadOnlyDepth,
        });
        DepthTexture = new ShaderResourceView(Device, _depth, new ShaderResourceViewDescription
        {
            Format = DxgiFormat.R32_Float, Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D, Texture2D = new ShaderResourceViewDescription.Texture2DResource { MipLevels = 1 },
        });
        _sceneCopy = new Texture2D(Device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = sceneFormat,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        SceneTexture = new ShaderResourceView(Device, _sceneCopy);
        IntPtr handle;
        using (var resource = _target.QueryInterface<SharpDX.DXGI.Resource>()) handle = resource.SharedHandle;
        _shared9 = new D3D9.Texture(_device9, width, height, 1, D3D9.Usage.RenderTarget, D3D9.Format.A8R8G8B8, D3D9.Pool.Default, ref handle);
        _surface9 = _shared9.GetSurfaceLevel(0);
        _image.Lock();
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9.NativePointer);
        _image.Unlock();
        _dirty = true;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds, seconds = Math.Min(0.1, now - _lastFrame);
        _lastFrame = now;
        if ((!_dirty && !Animating) || !_image.IsFrontBufferAvailable || ActualWidth <= 0 || ActualHeight <= 0) return;
        EnsureDevice();
        Resize();
        _dirty = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Context!.Rasterizer.SetViewport(0, 0, PixelWidth, PixelHeight);
        Context.OutputMerger.SetRenderTargets(DepthView, HighDynamicRange ? HdrView : TargetView);
        Draw(seconds);
        // the finished frame into the picture WPF shows, while WPF isn't reading it, waited for so it's whole
        _image.Lock();
        try
        {
            Context.CopyResource(_work, _target);
            Context.End(_done);
            Context.Flush();
            while (!Context.IsDataAvailable(_done) && clock.ElapsedMilliseconds < 250) Thread.Yield();
            _image.AddDirtyRect(new Int32Rect(0, 0, PixelWidth, PixelHeight));
        }
        finally { _image.Unlock(); }
        LastFrameMilliseconds = clock.Elapsed.TotalMilliseconds;
        if (FrameStats.Enabled) _stats.Frame(LastFrameMilliseconds);
    }

    /// <summary>Copies what's been drawn so far into <see cref="SceneTexture"/>.</summary>
    protected void CopyScene()
    {
        if (HighDynamicRange) { if (_hdr is not null && _sceneCopy is not null) Context!.CopySubresourceRegion(_hdr, 0, null, _sceneCopy, 0); }
        else if (_work is not null && _sceneCopy is not null) Context!.CopyResource(_work, _sceneCopy);
    }

    /// <summary>Makes the high dynamic range scene's smaller levels, down to its average colour in one pixel; unbind it as a target first.</summary>
    protected void AverageHdr()
    {
        if (HdrTexture is not null) Context!.GenerateMips(HdrTexture);
    }

    // the Direct3D picture stretched over the whole element; it shows each new frame without being drawn again here
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackBrush, null, new Rect(RenderSize));
        dc.DrawImage(_image, new Rect(RenderSize));
        if (_hooked && FrameStats.Enabled) _stats.Draw(dc, this);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _dirty = true;
    }

    public virtual void Dispose()
    {
        Hook(false);
        foreach (var thing in _owned) thing.Dispose();
        _owned.Clear();
        foreach (var d in new IDisposable?[] { _done, _surface9, _shared9, TargetView, _target, _work, DepthView, ReadOnlyDepthView, DepthTexture, _depth,
            SceneTexture, _sceneCopy, HdrView, HdrTexture, _hdr, _device9, _d3d9, Device })
            d?.Dispose();
        GC.SuppressFinalize(this);
    }
}
