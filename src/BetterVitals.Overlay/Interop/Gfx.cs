using System.Runtime.InteropServices;

namespace BetterVitals.Overlay.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct D2DColor(float r, float g, float b, float a)
{
    public float R = r, G = g, B = b, A = a;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2DPoint(float x, float y)
{
    public float X = x, Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2DRect(float l, float t, float r, float b)
{
    public float Left = l, Top = t, Right = r, Bottom = b;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2DRoundedRect
{
    public D2DRect Rect;
    public float RadiusX, RadiusY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2DMatrix
{
    public float M11, M12, M21, M22, Dx, Dy;

    public static D2DMatrix Identity => new() { M11 = 1, M22 = 1 };

    public static D2DMatrix Translation(float x, float y) => new() { M11 = 1, M22 = 1, Dx = x, Dy = y };
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2DGradientStop
{
    public float Position;
    public D2DColor Color;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2DStrokeStyleProps
{
    public int StartCap, EndCap, DashCap, LineJoin;
    public float MiterLimit;
    public int DashStyle;
    public float DashOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D2DRenderTargetProps
{
    public int Type;
    public uint Format;
    public int AlphaMode;
    public float DpiX, DpiY;
    public int Usage;
    public int MinLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct D2DBitmapProps1
{
    public uint Format;
    public int AlphaMode;
    public float DpiX, DpiY;
    public int Options;
    public void* ColorContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteFontMetrics
{
    public ushort DesignUnitsPerEm;
    public ushort Ascent;
    public ushort Descent;
    public short LineGap;
    public ushort CapHeight;
    public ushort XHeight;
    public short UnderlinePosition;
    public ushort UnderlineThickness;
    public short StrikethroughPosition;
    public ushort StrikethroughThickness;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteGlyphMetrics
{
    public int LeftSideBearing;
    public uint AdvanceWidth;
    public int RightSideBearing;
    public int TopSideBearing;
    public uint AdvanceHeight;
    public int BottomSideBearing;
    public int VerticalOriginY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteGlyphOffset
{
    public float AdvanceOffset, AscenderOffset;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DWriteGlyphRun
{
    public void* FontFace;
    public float FontEmSize;
    public uint GlyphCount;
    public ushort* GlyphIndices;
    public float* GlyphAdvances;
    public DWriteGlyphOffset* GlyphOffsets;
    public int IsSideways;
    public uint BidiLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteTextMetrics
{
    public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight;
    public uint MaxBidiReorderingDepth, LineCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWriteLineMetrics
{
    public uint Length, TrailingWhitespaceLength, NewlineLength;
    public float Height, Baseline;
    public int IsTrimmed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DxgiSwapChainDesc1
{
    public uint Width, Height, Format;
    public int Stereo;
    public uint SampleCount, SampleQuality;
    public uint BufferUsage, BufferCount;
    public int Scaling, SwapEffect, AlphaMode;
    public uint Flags;
}

/// <summary>Interface ids used by the overlay.</summary>
internal static class Iid
{
    public static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid DxgiFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    public static readonly Guid DxgiSurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    public static readonly Guid D2DFactory1 = new("bb12d362-daee-4b9a-aa1d-14ba401cfa1f");
    public static readonly Guid DWriteFactory5 = new("958DB99A-BE2A-4F09-AF7D-65189803D1D3");
    public static readonly Guid DCompDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    public static readonly Guid WicPbgra = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
}

/// <summary>HRESULTs that mean "recreate the device".</summary>
internal static class Hr
{
    public const int DxgiDeviceRemoved = unchecked((int)0x887A0005);
    public const int DxgiDeviceHung = unchecked((int)0x887A0006);
    public const int DxgiDeviceReset = unchecked((int)0x887A0007);
    public const int DxgiDriverInternalError = unchecked((int)0x887A0020);
    public const int D2DRecreateTarget = unchecked((int)0x8899000C);

    public static bool IsDeviceLost(int hr) => hr is DxgiDeviceRemoved or DxgiDeviceHung or DxgiDeviceReset
        or DxgiDriverInternalError or D2DRecreateTarget;
}

/// <summary>Exported entry points of the graphics DLLs.</summary>
internal static unsafe partial class GfxApi
{
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D_DRIVER_TYPE_WARP = 5;
    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const uint D3D11_SDK_VERSION = 7;

    [LibraryImport("d3d11.dll")]
    public static partial int D3D11CreateDevice(void* adapter, int driverType, nint software, uint flags,
        int* featureLevels, uint levels, uint sdkVersion, void** device, int* featureLevel, void** context);

    [LibraryImport("d2d1.dll")]
    public static partial int D2D1CreateFactory(int type, Guid* iid, void* options, void** factory);

    [LibraryImport("dwrite.dll")]
    public static partial int DWriteCreateFactory(int type, Guid* iid, void** factory);

    [LibraryImport("dcomp.dll")]
    public static partial int DCompositionCreateDevice(void* dxgiDevice, Guid* iid, void** device);

    [LibraryImport("windowscodecs.dll")]
    public static partial int WICCreateImagingFactory_Proxy(uint sdkVersion, void** factory);
}

/// <summary>ID2D1RenderTarget (slots 0–56) and ID2D1DeviceContext (57+) calls.</summary>
internal static unsafe class RT
{
    public static int CreateSolidColorBrush(void* rt, D2DColor c, out void* brush)
    {
        void* b = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, D2DColor*, void*, void**, int>)Com.Slot(rt, 8))(rt, &c, null, &b);
        brush = b;
        return hr;
    }

    public static int CreateGradientStopCollection(void* rt, D2DGradientStop* stops, uint count, out void* coll)
    {
        void* c = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, D2DGradientStop*, uint, int, int, void**, int>)Com.Slot(rt, 9))(rt, stops, count, 0, 0, &c);
        coll = c;
        return hr;
    }

    public static int CreateLinearGradientBrush(void* rt, D2DPoint start, D2DPoint end, void* stops, out void* brush)
    {
        void* b = null;
        var props = stackalloc D2DPoint[2];
        props[0] = start;
        props[1] = end;
        var hr = ((delegate* unmanaged[Stdcall]<void*, D2DPoint*, void*, void*, void**, int>)Com.Slot(rt, 10))(rt, props, null, stops, &b);
        brush = b;
        return hr;
    }

    public static void DrawLine(void* rt, D2DPoint a, D2DPoint b, void* brush, float width, void* style = null)
        => ((delegate* unmanaged[Stdcall]<void*, D2DPoint, D2DPoint, void*, float, void*, void>)Com.Slot(rt, 15))(rt, a, b, brush, width, style);

    public static void FillRectangle(void* rt, D2DRect r, void* brush)
        => ((delegate* unmanaged[Stdcall]<void*, D2DRect*, void*, void>)Com.Slot(rt, 17))(rt, &r, brush);

    public static void DrawRoundedRectangle(void* rt, D2DRoundedRect r, void* brush, float width, void* style = null)
        => ((delegate* unmanaged[Stdcall]<void*, D2DRoundedRect*, void*, float, void*, void>)Com.Slot(rt, 18))(rt, &r, brush, width, style);

    public static void FillRoundedRectangle(void* rt, D2DRoundedRect r, void* brush)
        => ((delegate* unmanaged[Stdcall]<void*, D2DRoundedRect*, void*, void>)Com.Slot(rt, 19))(rt, &r, brush);

    public static void DrawGeometry(void* rt, void* geometry, void* brush, float width, void* style = null)
        => ((delegate* unmanaged[Stdcall]<void*, void*, void*, float, void*, void>)Com.Slot(rt, 22))(rt, geometry, brush, width, style);

    public static void FillGeometry(void* rt, void* geometry, void* brush)
        => ((delegate* unmanaged[Stdcall]<void*, void*, void*, void*, void>)Com.Slot(rt, 23))(rt, geometry, brush, null);

    public static void DrawTextLayout(void* rt, D2DPoint origin, void* layout, void* brush)
        => ((delegate* unmanaged[Stdcall]<void*, D2DPoint, void*, void*, int, void>)Com.Slot(rt, 28))(rt, origin, layout, brush, 0);

    public static void DrawGlyphRun(void* rt, D2DPoint baseline, DWriteGlyphRun* run, void* brush)
        => ((delegate* unmanaged[Stdcall]<void*, D2DPoint, DWriteGlyphRun*, void*, int, void>)Com.Slot(rt, 29))(rt, baseline, run, brush, 0);

    public static void SetTransform(void* rt, D2DMatrix m)
        => ((delegate* unmanaged[Stdcall]<void*, D2DMatrix*, void>)Com.Slot(rt, 30))(rt, &m);

    /// <summary>D2D1_ANTIALIAS_MODE: 0 = per primitive, 1 = aliased.</summary>
    public static void SetAntialiasMode(void* rt, int mode)
        => ((delegate* unmanaged[Stdcall]<void*, int, void>)Com.Slot(rt, 32))(rt, mode);

    /// <summary>D2D1_TEXT_ANTIALIAS_MODE: 2 = grayscale (required on transparent targets).</summary>
    public static void SetTextAntialiasMode(void* rt, int mode)
        => ((delegate* unmanaged[Stdcall]<void*, int, void>)Com.Slot(rt, 34))(rt, mode);

    public static void PushAxisAlignedClip(void* rt, D2DRect r)
        => ((delegate* unmanaged[Stdcall]<void*, D2DRect*, int, void>)Com.Slot(rt, 45))(rt, &r, 0);

    public static void PopAxisAlignedClip(void* rt)
        => ((delegate* unmanaged[Stdcall]<void*, void>)Com.Slot(rt, 46))(rt);

    public static void Clear(void* rt, D2DColor c)
        => ((delegate* unmanaged[Stdcall]<void*, D2DColor*, void>)Com.Slot(rt, 47))(rt, &c);

    public static void BeginDraw(void* rt)
        => ((delegate* unmanaged[Stdcall]<void*, void>)Com.Slot(rt, 48))(rt);

    public static int EndDraw(void* rt)
        => ((delegate* unmanaged[Stdcall]<void*, ulong*, ulong*, int>)Com.Slot(rt, 49))(rt, null, null);

    public static void SetDpi(void* rt, float x, float y)
        => ((delegate* unmanaged[Stdcall]<void*, float, float, void>)Com.Slot(rt, 51))(rt, x, y);

    // ── ID2D1DeviceContext ──
    public static int CreateBitmapFromDxgiSurface(void* dc, void* surface, D2DBitmapProps1 props, out void* bitmap)
    {
        void* b = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, D2DBitmapProps1*, void**, int>)Com.Slot(dc, 62))(dc, surface, &props, &b);
        bitmap = b;
        return hr;
    }

    public static void SetTarget(void* dc, void* image)
        => ((delegate* unmanaged[Stdcall]<void*, void*, void>)Com.Slot(dc, 74))(dc, image);

    public static int CreateBitmap1(void* dc, uint w, uint h, D2DBitmapProps1 props, out void* bitmap)
    {
        void* b = null;
        var size = ((ulong)h << 32) | w;
        var hr = ((delegate* unmanaged[Stdcall]<void*, ulong, void*, uint, D2DBitmapProps1*, void**, int>)Com.Slot(dc, 57))(dc, size, null, 0, &props, &b);
        bitmap = b;
        return hr;
    }
}

/// <summary>ID2D1Bitmap1 calls (read-back of the swap chain for tests).</summary>
internal static unsafe class Bitmap1
{
    public static int CopyFromBitmap(void* dest, void* src)
        => ((delegate* unmanaged[Stdcall]<void*, void*, void*, void*, int>)Com.Slot(dest, 8))(dest, null, src, null);

    /// <summary>D2D1_MAP_OPTIONS_READ = 1; returns pitch and bits.</summary>
    public static int Map(void* bitmap, out uint pitch, out byte* bits)
    {
        var mapped = stackalloc nint[2];
        var hr = ((delegate* unmanaged[Stdcall]<void*, int, nint*, int>)Com.Slot(bitmap, 14))(bitmap, 1, mapped);
        pitch = (uint)mapped[0];
        bits = (byte*)mapped[1];
        return hr;
    }

    public static int Unmap(void* bitmap) => ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(bitmap, 15))(bitmap);
}

/// <summary>ID2D1Brush / ID2D1SolidColorBrush.</summary>
internal static unsafe class Brush
{
    public static void SetOpacity(void* brush, float opacity)
        => ((delegate* unmanaged[Stdcall]<void*, float, void>)Com.Slot(brush, 4))(brush, opacity);

    public static void SetColor(void* brush, D2DColor c)
        => ((delegate* unmanaged[Stdcall]<void*, D2DColor*, void>)Com.Slot(brush, 8))(brush, &c);
}

/// <summary>ID2D1Factory1, ID2D1Device, geometry calls.</summary>
internal static unsafe class D2D
{
    public static int CreatePathGeometry(void* factory, out void* geometry)
    {
        void* g = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(factory, 10))(factory, &g);
        geometry = g;
        return hr;
    }

    public static int CreateStrokeStyle(void* factory, D2DStrokeStyleProps props, float* dashes, uint dashCount, out void* style)
    {
        void* s = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, D2DStrokeStyleProps*, float*, uint, void**, int>)Com.Slot(factory, 11))(factory, &props, dashes, dashCount, &s);
        style = s;
        return hr;
    }

    public static int CreateWicBitmapRenderTarget(void* factory, void* wicBitmap, D2DRenderTargetProps props, out void* rt)
    {
        void* r = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, D2DRenderTargetProps*, void**, int>)Com.Slot(factory, 13))(factory, wicBitmap, &props, &r);
        rt = r;
        return hr;
    }

    public static int CreateDevice(void* factory1, void* dxgiDevice, out void* device)
    {
        void* d = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Com.Slot(factory1, 17))(factory1, dxgiDevice, &d);
        device = d;
        return hr;
    }

    public static int CreateDeviceContext(void* device, out void* dc)
    {
        void* c = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, int, void**, int>)Com.Slot(device, 4))(device, 0, &c);
        dc = c;
        return hr;
    }

    public static void SetMaximumTextureMemory(void* device, ulong bytes)
        => ((delegate* unmanaged[Stdcall]<void*, ulong, void>)Com.Slot(device, 6))(device, bytes);

    public static void ClearResources(void* device, uint msSinceUse)
        => ((delegate* unmanaged[Stdcall]<void*, uint, void>)Com.Slot(device, 8))(device, msSinceUse);

    public static int OpenGeometry(void* path, out void* sink)
    {
        void* s = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(path, 17))(path, &s);
        sink = s;
        return hr;
    }

    /// <summary>figureBegin: 0 = filled, 1 = hollow.</summary>
    public static void BeginFigure(void* sink, D2DPoint start, int figureBegin)
        => ((delegate* unmanaged[Stdcall]<void*, D2DPoint, int, void>)Com.Slot(sink, 5))(sink, start, figureBegin);

    public static void AddLines(void* sink, D2DPoint* points, uint count)
        => ((delegate* unmanaged[Stdcall]<void*, D2DPoint*, uint, void>)Com.Slot(sink, 6))(sink, points, count);

    /// <summary>figureEnd: 0 = open, 1 = closed.</summary>
    public static void EndFigure(void* sink, int figureEnd)
        => ((delegate* unmanaged[Stdcall]<void*, int, void>)Com.Slot(sink, 8))(sink, figureEnd);

    public static int CloseSink(void* sink)
        => ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(sink, 9))(sink);
}

/// <summary>DirectWrite calls (IDWriteFactory5 and friends).</summary>
internal static unsafe class DW
{
    public static int GetSystemFontCollection(void* factory, out void* collection)
    {
        void* c = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int, int>)Com.Slot(factory, 3))(factory, &c, 0);
        collection = c;
        return hr;
    }

    public static int RegisterFontFileLoader(void* factory, void* loader)
        => ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(factory, 13))(factory, loader);

    public static int CreateTextFormat(void* factory, string family, void* collection, int weight, float size, out void* format)
    {
        void* f = null;
        int hr;
        fixed (char* fam = family)
        fixed (char* loc = "en-us")
            hr = ((delegate* unmanaged[Stdcall]<void*, char*, void*, int, int, int, float, char*, void**, int>)Com.Slot(factory, 15))(
                factory, fam, collection, weight, 0, 5, size, loc, &f);
        format = f;
        return hr;
    }

    public static int CreateTextLayout(void* factory, char* text, int length, void* format, out void* layout)
    {
        void* l = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, char*, uint, void*, float, float, void**, int>)Com.Slot(factory, 18))(
            factory, text, (uint)length, format, 8192f, 2048f, &l);
        layout = l;
        return hr;
    }

    public static int CreateFontSetBuilder1(void* factory5, out void* builder)
    {
        void* b = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(factory5, 43))(factory5, &b);
        builder = b;
        return hr;
    }

    public static int CreateInMemoryFontFileLoader(void* factory5, out void* loader)
    {
        void* l = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(factory5, 44))(factory5, &l);
        loader = l;
        return hr;
    }

    public static int CreateFontCollectionFromFontSet(void* factory3, void* fontSet, out void* collection)
    {
        void* c = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)Com.Slot(factory3, 37))(factory3, fontSet, &c);
        collection = c;
        return hr;
    }

    public static int CreateInMemoryFontFileReference(void* loader, void* factory, void* data, uint size, void* owner, out void* file)
    {
        void* f = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, void*, uint, void*, void**, int>)Com.Slot(loader, 4))(loader, factory, data, size, owner, &f);
        file = f;
        return hr;
    }

    public static int BuilderAddFontFile(void* builder1, void* file)
        => ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(builder1, 7))(builder1, file);

    public static int BuilderCreateFontSet(void* builder, out void* set)
    {
        void* s = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(builder, 6))(builder, &s);
        set = s;
        return hr;
    }

    public static int GetFontFamily(void* collection, uint index, out void* family)
    {
        void* f = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Com.Slot(collection, 4))(collection, index, &f);
        family = f;
        return hr;
    }

    public static bool FindFamilyName(void* collection, string name, out uint index)
    {
        uint i = 0;
        int exists = 0;
        int hr;
        fixed (char* n = name) hr = ((delegate* unmanaged[Stdcall]<void*, char*, uint*, int*, int>)Com.Slot(collection, 5))(collection, n, &i, &exists);
        index = i;
        return hr >= 0 && exists != 0;
    }

    public static int GetFirstMatchingFont(void* family, int weight, out void* font)
    {
        void* f = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, int, int, int, void**, int>)Com.Slot(family, 7))(family, weight, 5, 0, &f);
        font = f;
        return hr;
    }

    public static int GetWeight(void* font) => ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(font, 4))(font);

    public static int CreateFontFace(void* font, out void* face)
    {
        void* f = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(font, 13))(font, &f);
        face = f;
        return hr;
    }

    public static void GetFaceMetrics(void* face, out DWriteFontMetrics m)
    {
        DWriteFontMetrics r;
        ((delegate* unmanaged[Stdcall]<void*, DWriteFontMetrics*, void>)Com.Slot(face, 8))(face, &r);
        m = r;
    }

    public static int GetDesignGlyphMetrics(void* face, ushort* glyphs, uint count, DWriteGlyphMetrics* metrics)
        => ((delegate* unmanaged[Stdcall]<void*, ushort*, uint, DWriteGlyphMetrics*, int, int>)Com.Slot(face, 10))(face, glyphs, count, metrics, 0);

    public static int GetGlyphIndices(void* face, uint* codePoints, uint count, ushort* glyphs)
        => ((delegate* unmanaged[Stdcall]<void*, uint*, uint, ushort*, int>)Com.Slot(face, 11))(face, codePoints, count, glyphs);

    /// <summary>DWRITE_WORD_WRAPPING_NO_WRAP = 1.</summary>
    public static int SetWordWrapping(void* format, int mode)
        => ((delegate* unmanaged[Stdcall]<void*, int, int>)Com.Slot(format, 5))(format, mode);

    public static int GetLineMetrics(void* layout, DWriteLineMetrics* metrics, uint max, out uint actual)
    {
        uint a = 0;
        var hr = ((delegate* unmanaged[Stdcall]<void*, DWriteLineMetrics*, uint, uint*, int>)Com.Slot(layout, 59))(layout, metrics, max, &a);
        actual = a;
        return hr;
    }

    public static int GetMetrics(void* layout, out DWriteTextMetrics m)
    {
        DWriteTextMetrics r;
        var hr = ((delegate* unmanaged[Stdcall]<void*, DWriteTextMetrics*, int>)Com.Slot(layout, 60))(layout, &r);
        m = r;
        return hr;
    }
}

/// <summary>DXGI calls.</summary>
internal static unsafe class Dxgi
{
    public static int GetParent(void* obj, in Guid iid, out void* parent)
    {
        void* p = null;
        int hr;
        fixed (Guid* g = &iid) hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Com.Slot(obj, 6))(obj, g, &p);
        parent = p;
        return hr;
    }

    public static int GetAdapter(void* dxgiDevice, out void* adapter)
    {
        void* a = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(dxgiDevice, 7))(dxgiDevice, &a);
        adapter = a;
        return hr;
    }

    public static int CreateSwapChainForComposition(void* factory2, void* device, DxgiSwapChainDesc1 desc, out void* swapChain)
    {
        void* s = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void*, DxgiSwapChainDesc1*, void*, void**, int>)Com.Slot(factory2, 24))(factory2, device, &desc, null, &s);
        swapChain = s;
        return hr;
    }

    public static int Present(void* swapChain, uint sync, uint flags)
        => ((delegate* unmanaged[Stdcall]<void*, uint, uint, int>)Com.Slot(swapChain, 8))(swapChain, sync, flags);

    public static int GetBuffer(void* swapChain, uint index, in Guid iid, out void* surface)
    {
        void* s = null;
        int hr;
        fixed (Guid* g = &iid) hr = ((delegate* unmanaged[Stdcall]<void*, uint, Guid*, void**, int>)Com.Slot(swapChain, 9))(swapChain, index, g, &s);
        surface = s;
        return hr;
    }

    public static int ResizeBuffers(void* swapChain, uint width, uint height)
        => ((delegate* unmanaged[Stdcall]<void*, uint, uint, uint, uint, uint, int>)Com.Slot(swapChain, 13))(swapChain, 0, width, height, 0, 0);
}

/// <summary>DirectComposition calls (IDCompositionDevice / Target / Visual).</summary>
internal static unsafe class DComp
{
    public static int Commit(void* device) => ((delegate* unmanaged[Stdcall]<void*, int>)Com.Slot(device, 3))(device);

    public static int CreateTargetForHwnd(void* device, nint hwnd, bool topmost, out void* target)
    {
        void* t = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, nint, int, void**, int>)Com.Slot(device, 6))(device, hwnd, topmost ? 1 : 0, &t);
        target = t;
        return hr;
    }

    public static int CreateVisual(void* device, out void* visual)
    {
        void* v = null;
        var hr = ((delegate* unmanaged[Stdcall]<void*, void**, int>)Com.Slot(device, 7))(device, &v);
        visual = v;
        return hr;
    }

    public static bool CheckDeviceState(void* device)
    {
        int valid = 0;
        var hr = ((delegate* unmanaged[Stdcall]<void*, int*, int>)Com.Slot(device, 26))(device, &valid);
        return hr >= 0 && valid != 0;
    }

    public static int SetRoot(void* target, void* visual)
        => ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(target, 3))(target, visual);

    public static int SetContent(void* visual, void* content)
        => ((delegate* unmanaged[Stdcall]<void*, void*, int>)Com.Slot(visual, 15))(visual, content);
}

/// <summary>WIC calls.</summary>
internal static unsafe class Wic
{
    public static int CreateBitmap(void* factory, uint w, uint h, out void* bitmap)
    {
        void* b = null;
        var fmt = Iid.WicPbgra;
        var hr = ((delegate* unmanaged[Stdcall]<void*, uint, uint, Guid*, int, void**, int>)Com.Slot(factory, 17))(factory, w, h, &fmt, 2, &b);
        bitmap = b;
        return hr;
    }

    public static int CopyPixels(void* bitmap, uint stride, uint size, byte* buffer)
        => ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, byte*, int>)Com.Slot(bitmap, 7))(bitmap, null, stride, size, buffer);
}
