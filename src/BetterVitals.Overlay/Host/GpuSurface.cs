using BetterVitals.Core;
using BetterVitals.Overlay.Interop;
using BetterVitals.Overlay.Rendering;

namespace BetterVitals.Overlay.Host;

/// <summary>
/// The window's drawing surface: D3D11 device (BGRA) → DXGI composition swap chain (premultiplied alpha, flip
/// sequential, 2 buffers) → DirectComposition visual on the HWND, drawn with a Direct2D device context.
/// Device loss (TDR, driver update, GPU switch) is reported by <see cref="Render"/>; the owner then calls
/// <see cref="Reset"/> and renders again. Overlay thread only.
/// </summary>
internal sealed unsafe class GpuSurface : IDisposable
{
    private readonly nint _hwnd;
    private void* _d2dFactory;
    private void* _d3d;
    private void* _dxgiDevice;
    private void* _dxgiFactory;
    private void* _swap;
    private void* _d2dDevice;
    private void* _dc;
    private void* _target;
    private void* _dcomp;
    private void* _dcTarget;
    private void* _visual;
    private DeviceResources? _res;

    public GpuSurface(nint hwnd, bool preferWarp)
    {
        _hwnd = hwnd;
        PreferWarp = preferWarp;
    }

    /// <summary>Try the WARP software device before the GPU (default; see <see cref="Create"/>).</summary>
    public bool PreferWarp { get; }

    public bool Ready => _dc != null && _swap != null;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>How many times the device was created (1 + recoveries).</summary>
    public int DeviceCreations { get; private set; }

    /// <summary>The device in use is WARP.</summary>
    public bool IsWarp { get; private set; }

    /// <summary>Creates device, swap chain and composition tree for a <paramref name="w"/> × <paramref name="h"/> surface.</summary>
    public void Create(int w, int h)
    {
        Release();
        w = Math.Max(1, w);
        h = Math.Max(1, h);
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
        var type = first;
        var hr = GfxApi.D3D11CreateDevice(null, first, 0, GfxApi.D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 7, GfxApi.D3D11_SDK_VERSION, &dev, &fl, null);
        if (hr < 0)
        {
            type = second;
            Com.Check(GfxApi.D3D11CreateDevice(null, second, 0, GfxApi.D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 7, GfxApi.D3D11_SDK_VERSION, &dev, &fl, null), "D3D11CreateDevice");
        }
        IsWarp = type == GfxApi.D3D_DRIVER_TYPE_WARP;
        _d3d = dev;
        DeviceCreations++;

        Com.Check(Com.QueryInterface(_d3d, Iid.DxgiDevice, out _dxgiDevice), "QueryInterface(IDXGIDevice)");
        Com.Check(Dxgi.GetAdapter(_dxgiDevice, out var adapter), "IDXGIDevice.GetAdapter");
        hr = Dxgi.GetParent(adapter, Iid.DxgiFactory2, out _dxgiFactory);
        Com.Release(ref adapter);
        Com.Check(hr, "IDXGIAdapter.GetParent(IDXGIFactory2)");

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
        Com.Check(Dxgi.CreateSwapChainForComposition(_dxgiFactory, _d3d, desc, out _swap), "CreateSwapChainForComposition");

        Com.Check(D2D.CreateDevice(_d2dFactory, _dxgiDevice, out _d2dDevice), "ID2D1Factory1.CreateDevice");
        Com.Check(D2D.CreateDeviceContext(_d2dDevice, out _dc), "ID2D1Device.CreateDeviceContext");
        // Keep Direct2D's internal caches small: the overlay draws a few dozen glyphs once a second.
        D2D.SetMaximumTextureMemory(_d2dDevice, 4 * 1024 * 1024);
        BindTarget();
        _res = new DeviceResources(_dc, _d2dFactory);

        void* dcomp = null;
        var dcompIid = Iid.DCompDevice;
        Com.Check(GfxApi.DCompositionCreateDevice(_dxgiDevice, &dcompIid, &dcomp), "DCompositionCreateDevice");
        _dcomp = dcomp;
        Com.Check(DComp.CreateTargetForHwnd(_dcomp, _hwnd, true, out _dcTarget), "CreateTargetForHwnd");
        Com.Check(DComp.CreateVisual(_dcomp, out _visual), "CreateVisual");
        Com.Check(DComp.SetContent(_visual, _swap), "IDCompositionVisual.SetContent");
        Com.Check(DComp.SetRoot(_dcTarget, _visual), "IDCompositionTarget.SetRoot");
        Com.Check(DComp.Commit(_dcomp), "IDCompositionDevice.Commit");
        Width = w;
        Height = h;
        Log.Info($"overlay: surface {w}×{h} on {(IsWarp ? "WARP" : "hardware")} device (feature level 0x{fl:X})");
    }

    private void BindTarget()
    {
        Com.Check(Dxgi.GetBuffer(_swap, 0, Iid.DxgiSurface, out var surface), "IDXGISwapChain.GetBuffer");
        var props = new D2DBitmapProps1 { Format = 87, AlphaMode = 1, DpiX = 96, DpiY = 96, Options = 1 | 2 };
        var hr = RT.CreateBitmapFromDxgiSurface(_dc, surface, props, out _target);
        Com.Release(ref surface);
        Com.Check(hr, "CreateBitmapFromDxgiSurface");
        RT.SetTarget(_dc, _target);
    }

    /// <summary>Resizes the swap chain buffers; returns an HRESULT (device lost → <see cref="Reset"/>).</summary>
    public int Resize(int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (!Ready) return -1;
        if (w == Width && h == Height) return 0;
        RT.SetTarget(_dc, null);
        Com.Release(ref _target);
        var hr = Dxgi.ResizeBuffers(_swap, (uint)w, (uint)h);
        if (hr < 0) return hr;
        BindTarget();
        Width = w;
        Height = h;
        return 0;
    }

    /// <summary>Draws the scene and presents. Returns an HRESULT; device-lost codes mean "call <see cref="Reset"/>".</summary>
    public int Render(Scene scene, float alpha, bool edit, bool present = true)
    {
        if (!Ready || _res is null) return -1;
        RT.BeginDraw(_dc);
        SceneRenderer.Draw(_res, scene, alpha, edit);
        var hr = RT.EndDraw(_dc);
        if (hr < 0 || !present) return hr;
        return Dxgi.Present(_swap, 0, 0);
    }

    /// <summary>DirectComposition still has a valid device (false after a display driver reset).</summary>
    public bool CheckComposition() => _dcomp == null || DComp.CheckDeviceState(_dcomp);

    /// <summary>Drops every device-bound object and recreates them at the current size.</summary>
    public void Reset()
    {
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        Log.Warn("overlay: graphics device lost, recreating");
        Create(w, h);
    }

    /// <summary>Releases device objects (the D2D factory is kept).</summary>
    public void Release()
    {
        _res?.Dispose();
        _res = null;
        if (_dc != null) RT.SetTarget(_dc, null);
        Com.Release(ref _target);
        Com.Release(ref _visual);
        Com.Release(ref _dcTarget);
        if (_dcomp != null) DComp.Commit(_dcomp);
        Com.Release(ref _dcomp);
        Com.Release(ref _dc);
        Com.Release(ref _d2dDevice);
        Com.Release(ref _swap);
        Com.Release(ref _dxgiFactory);
        Com.Release(ref _dxgiDevice);
        Com.Release(ref _d3d);
        Width = Height = 0;
    }

    /// <summary>
    /// Copies the back buffer to memory (tests: what the window shows, without any screen capture). Flip-model
    /// buffers rotate on Present, so draw with <c>present: false</c> first.
    /// </summary>
    public OverlayBitmap? ReadBack()
    {
        if (!Ready) return null;
        var props = new D2DBitmapProps1 { Format = 87, AlphaMode = 1, DpiX = 96, DpiY = 96, Options = 4 | 2 };
        if (RT.CreateBitmap1(_dc, (uint)Width, (uint)Height, props, out var cpu) < 0) return null;
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
