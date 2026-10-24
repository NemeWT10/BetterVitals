using BetterVitals.Core;
using BetterVitals.Overlay.Interop;
using BetterVitals.Overlay.Rendering;

namespace BetterVitals.Overlay.Host;

/// <summary>
/// The graphics device every overlay window shares: D3D11 device (BGRA), its DXGI factory, one Direct2D device context
/// (it draws into each window's swap chain in turn) with its brushes, and one DirectComposition device (one target per
/// window). Device loss (TDR, driver update, GPU switch) shows up as a failed render or present; the owner then releases
/// every <see cref="SwapSurface"/>, calls <see cref="Create"/> again and recreates them. Overlay thread only.
/// </summary>
internal sealed unsafe class GpuDevice : IDisposable
{
    private void* _d2dFactory;
    private void* _d3d;
    private void* _dxgiDevice;
    private void* _dxgiFactory;
    private void* _d2dDevice;
    private void* _dc;
    private void* _dcomp;
    private DeviceResources? _res;

    public GpuDevice(bool preferWarp) => PreferWarp = preferWarp;

    /// <summary>Try the WARP software device before the GPU (default; see <see cref="Create"/>).</summary>
    public bool PreferWarp { get; }

    public bool Ready => _dc != null && _dcomp != null;

    /// <summary>How many times the device was created (1 + recoveries).</summary>
    public int DeviceCreations { get; private set; }

    /// <summary>The device in use is WARP.</summary>
    public bool IsWarp { get; private set; }

    internal void* D3D => _d3d;

    internal void* DxgiFactory => _dxgiFactory;

    internal void* Dc => _dc;

    internal void* DComp => _dcomp;

    internal DeviceResources? Resources => _res;

    /// <summary>Creates the device, the Direct2D context and the composition device.</summary>
    public void Create()
    {
        Release();
        if (_d2dFactory == null)
        {
            void* f = null;
            var iid = Iid.D2DFactory1;
            Com.Check(GfxApi.D2D1CreateFactory(0, &iid, null, &f), "D2D1CreateFactory");
            _d2dFactory = f;
        }

        var levels = stackalloc int[] { 0xb100, 0xb000, 0xa100, 0xa000, 0x9300, 0x9200, 0x9100 };
        void* dev = null;
        int fl = 0;
        // WARP first by default: for a few hundred pixels redrawn once a second the software rasterizer is cheaper
        // than the GPU driver (measured: +5 MB vs +15 MB private working set, +8 MB vs +60 MB commit, less CPU per
        // frame), never competes with the game for the GPU and is immune to TDRs / driver updates. Hardware is the fallback.
        var first = PreferWarp ? GfxApi.D3D_DRIVER_TYPE_WARP : GfxApi.D3D_DRIVER_TYPE_HARDWARE;
        var second = PreferWarp ? GfxApi.D3D_DRIVER_TYPE_HARDWARE : GfxApi.D3D_DRIVER_TYPE_WARP;
        // WARP on the overlay thread alone: by default it starts a rasterizer worker per core (measured: +12 threads,
        // ~40 wake-ups/s, and 4× the CPU of a frame in hand-offs for a surface of a few hundred pixels). Same pixels.
        static uint Flags(int t) => GfxApi.D3D11_CREATE_DEVICE_BGRA_SUPPORT
                                    | (t == GfxApi.D3D_DRIVER_TYPE_WARP ? GfxApi.D3D11_CREATE_DEVICE_PREVENT_INTERNAL_THREADING_OPTIMIZATIONS : 0);
        var type = first;
        var hr = GfxApi.D3D11CreateDevice(null, first, 0, Flags(first), levels, 7, GfxApi.D3D11_SDK_VERSION, &dev, &fl, null);
        if (hr < 0)
        {
            type = second;
            Com.Check(GfxApi.D3D11CreateDevice(null, second, 0, Flags(second), levels, 7, GfxApi.D3D11_SDK_VERSION, &dev, &fl, null), "D3D11CreateDevice");
        }
        IsWarp = type == GfxApi.D3D_DRIVER_TYPE_WARP;
        _d3d = dev;
        DeviceCreations++;

        Com.Check(Com.QueryInterface(_d3d, Iid.DxgiDevice, out _dxgiDevice), "QueryInterface(IDXGIDevice)");
        Com.Check(Dxgi.GetAdapter(_dxgiDevice, out var adapter), "IDXGIDevice.GetAdapter");
        hr = Dxgi.GetParent(adapter, Iid.DxgiFactory2, out _dxgiFactory);
        Com.Release(ref adapter);
        Com.Check(hr, "IDXGIAdapter.GetParent(IDXGIFactory2)");

        Com.Check(D2D.CreateDevice(_d2dFactory, _dxgiDevice, out _d2dDevice), "ID2D1Factory1.CreateDevice");
        Com.Check(D2D.CreateDeviceContext(_d2dDevice, out _dc), "ID2D1Device.CreateDeviceContext");
        // Keep Direct2D's internal caches small: the overlay draws a few dozen glyphs once a second.
        D2D.SetMaximumTextureMemory(_d2dDevice, 4 * 1024 * 1024);
        _res = new DeviceResources(_dc, _d2dFactory);

        void* dcomp = null;
        var dcompIid = Iid.DCompDevice;
        Com.Check(GfxApi.DCompositionCreateDevice(_dxgiDevice, &dcompIid, &dcomp), "DCompositionCreateDevice");
        _dcomp = dcomp;
        Log.Info($"overlay: {(IsWarp ? "WARP" : "hardware")} device (feature level 0x{fl:X})");
    }

    /// <summary>DirectComposition still has a valid device (false after a display driver reset).</summary>
    public bool CheckComposition() => _dcomp == null || Interop.DComp.CheckDeviceState(_dcomp);

    /// <summary>Releases the device objects (the D2D factory is kept). Release the surfaces first.</summary>
    public void Release()
    {
        _res?.Dispose();
        _res = null;
        if (_dc != null) RT.SetTarget(_dc, null);
        if (_dcomp != null) Interop.DComp.Commit(_dcomp);
        Com.Release(ref _dcomp);
        Com.Release(ref _dc);
        Com.Release(ref _d2dDevice);
        Com.Release(ref _dxgiFactory);
        Com.Release(ref _dxgiDevice);
        Com.Release(ref _d3d);
    }

    /// <summary>Lets Direct2D free cached glyph / texture memory not used for a while.</summary>
    public void Trim()
    {
        if (_d2dDevice != null) D2D.ClearResources(_d2dDevice, 0);
    }

    public void Dispose()
    {
        Release();
        Com.Release(ref _d2dFactory);
    }
}

/// <summary>
/// One window's drawing surface on the shared <see cref="GpuDevice"/>: a DXGI composition swap chain (premultiplied
/// alpha, flip sequential, 2 buffers) of exactly the window's size → a DirectComposition visual on the HWND. Overlay
/// thread only.
/// </summary>
internal sealed unsafe class SwapSurface(GpuDevice device, nint hwnd) : IDisposable
{
    private void* _swap;
    private void* _target;
    private void* _dcTarget;
    private void* _visual;
    private void* _backdrop;
    private Scene? _backdropScene;
    private int _backdropVersion, _backdropWidth, _backdropHeight;

    public nint Hwnd { get; } = hwnd;

    public bool Ready => _swap != null && _target != null && device.Ready;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Creates the swap chain and the composition tree for a <paramref name="w"/> × <paramref name="h"/> surface.</summary>
    public void Create(int w, int h)
    {
        Release();
        if (!device.Ready) throw new InvalidOperationException("graphics device not ready");
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        var desc = new DxgiSwapChainDesc1
        {
            Width = (uint)w,
            Height = (uint)h,
            Format = 87, // B8G8R8A8_UNORM
            SampleCount = 1,
            BufferUsage = 0x20, // RENDER_TARGET_OUTPUT
            BufferCount = 2,
            Scaling = 0, // STRETCH
            SwapEffect = 3, // FLIP_SEQUENTIAL
            AlphaMode = 1, // PREMULTIPLIED
        };
        Com.Check(Dxgi.CreateSwapChainForComposition(device.DxgiFactory, device.D3D, desc, out _swap), "CreateSwapChainForComposition");
        BindTarget();
        Com.Check(Interop.DComp.CreateTargetForHwnd(device.DComp, Hwnd, true, out _dcTarget), "CreateTargetForHwnd");
        Com.Check(Interop.DComp.CreateVisual(device.DComp, out _visual), "CreateVisual");
        Com.Check(Interop.DComp.SetContent(_visual, _swap), "IDCompositionVisual.SetContent");
        Com.Check(Interop.DComp.SetRoot(_dcTarget, _visual), "IDCompositionTarget.SetRoot");
        Com.Check(Interop.DComp.Commit(device.DComp), "IDCompositionDevice.Commit");
        Width = w;
        Height = h;
        Log.Info($"overlay: surface {w}×{h}");
    }

    private void BindTarget()
    {
        Com.Check(Dxgi.GetBuffer(_swap, 0, Iid.DxgiSurface, out var surface), "IDXGISwapChain.GetBuffer");
        var props = new D2DBitmapProps1 { Format = 87, AlphaMode = 1, DpiX = 96, DpiY = 96, Options = 1 | 2 };
        var hr = RT.CreateBitmapFromDxgiSurface(device.Dc, surface, props, out _target);
        Com.Release(ref surface);
        Com.Check(hr, "CreateBitmapFromDxgiSurface");
    }

    /// <summary>Resizes the swap chain buffers; returns an HRESULT (device lost → recreate everything).</summary>
    public int Resize(int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (!Ready) return -1;
        if (w == Width && h == Height) return 0;
        RT.SetTarget(device.Dc, null);
        Com.Release(ref _target);
        var hr = Dxgi.ResizeBuffers(_swap, (uint)w, (uint)h);
        if (hr < 0) return hr;
        BindTarget();
        Width = w;
        Height = h;
        return 0;
    }

    /// <summary>
    /// Draws the scene and presents. Returns an HRESULT; device-lost codes mean "recreate the device".
    /// Fully opaque (not fading), the backdrop comes from its cached bitmap (<see cref="Backdrop"/>): the same pixels as
    /// drawing it, for a fraction of the cost on WARP.
    /// </summary>
    public int Render(Scene scene, float alpha, bool edit, float editEmphasis = 1, bool present = true)
    {
        if (!Ready || device.Resources is not { } res) return -1;
        var dc = device.Dc;
        var hr = 0;
        var backdrop = alpha >= 1 ? Backdrop(scene, res, out hr) : null;
        if (hr < 0) return hr;
        if (backdrop != null)
        {
            // a plain copy over every pixel of the back buffer: what Clear + the backdrop's drawing would leave there
            hr = Bitmap1.CopyFromBitmap(_target, backdrop);
            if (hr < 0) return hr;
        }
        RT.SetTarget(dc, _target);
        RT.BeginDraw(dc);
        if (backdrop != null) SceneRenderer.DrawForeground(res, scene, alpha, edit, editEmphasis);
        else SceneRenderer.Draw(res, scene, alpha, edit, editEmphasis);
        hr = RT.EndDraw(dc);
        RT.SetTarget(dc, null);
        if (hr < 0 || !present) return hr;
        return Dxgi.Present(_swap, 0, 0);
    }

    /// <summary>
    /// The scene's backdrop (<see cref="SceneRenderer.DrawBackdrop"/> over a cleared bitmap of the surface's size, fully
    /// opaque), drawn again only when the scene, its arrangement or the surface size changed. Null when it cannot be had
    /// (<paramref name="hr"/> &lt; 0 for a device error).
    /// </summary>
    private void* Backdrop(Scene scene, DeviceResources res, out int hr)
    {
        hr = 0;
        if (_backdrop != null && ReferenceEquals(_backdropScene, scene) && _backdropVersion == scene.LayoutVersion
            && _backdropWidth == Width && _backdropHeight == Height)
            return _backdrop;
        var dc = device.Dc;
        if (_backdrop == null || _backdropWidth != Width || _backdropHeight != Height)
        {
            Com.Release(ref _backdrop);
            _backdropScene = null;
            var props = new D2DBitmapProps1 { Format = 87, AlphaMode = 1, DpiX = 96, DpiY = 96, Options = 1 }; // TARGET
            hr = RT.CreateBitmap1(dc, (uint)Width, (uint)Height, props, out _backdrop);
            if (hr < 0)
            {
                _backdrop = null;
                return null;
            }
            _backdropWidth = Width;
            _backdropHeight = Height;
        }
        RT.SetTarget(dc, _backdrop);
        RT.BeginDraw(dc);
        RT.Clear(dc, new D2DColor(0, 0, 0, 0));
        SceneRenderer.DrawBackdrop(res, scene, 1);
        hr = RT.EndDraw(dc);
        RT.SetTarget(dc, null);
        if (hr < 0)
        {
            _backdropScene = null;
            return null;
        }
        _backdropScene = scene;
        _backdropVersion = scene.LayoutVersion;
        return _backdrop;
    }

    /// <summary>
    /// Copies the back buffer to memory (tests: what the window shows, without any screen capture). Flip-model
    /// buffers rotate on Present, so draw with <c>present: false</c> first.
    /// </summary>
    public OverlayBitmap? ReadBack()
    {
        if (!Ready) return null;
        var dc = device.Dc;
        var props = new D2DBitmapProps1 { Format = 87, AlphaMode = 1, DpiX = 96, DpiY = 96, Options = 4 | 2 };
        if (RT.CreateBitmap1(dc, (uint)Width, (uint)Height, props, out var cpu) < 0) return null;
        try
        {
            if (Bitmap1.CopyFromBitmap(cpu, _target) < 0) return null;
            if (Bitmap1.Map(cpu, out var pitch, out var bits) < 0) return null;
            var stride = Width * 4;
            var pixels = new byte[stride * Height];
            for (var y = 0; y < Height; y++)
                new ReadOnlySpan<byte>(bits + y * pitch, stride).CopyTo(pixels.AsSpan(y * stride));
            Bitmap1.Unmap(cpu);
            return new OverlayBitmap(Width, Height, stride, pixels);
        }
        finally
        {
            Com.Release(ref cpu);
        }
    }

    /// <summary>Releases the swap chain and the composition target (the device stays).</summary>
    public void Release()
    {
        var had = _dcTarget != null;
        Com.Release(ref _backdrop);
        _backdropScene = null;
        Com.Release(ref _target);
        Com.Release(ref _visual);
        Com.Release(ref _dcTarget);
        Com.Release(ref _swap);
        if (had && device.Ready) Interop.DComp.Commit(device.DComp);
        Width = Height = 0;
    }

    public void Dispose() => Release();
}
