using System.Runtime.CompilerServices;

namespace BetterVitals.Overlay.Interop;

/// <summary>
/// Minimal COM helpers: raw interface pointers, calls through the vtable with function pointers (no RCWs, no
/// marshaling stubs, no allocations). Slot numbers are verified against the Windows SDK headers.
/// </summary>
internal static unsafe class Com
{
    public const int S_OK = 0;

    /// <summary>Vtable slot <paramref name="slot"/> of interface pointer <paramref name="p"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* Slot(void* p, int slot) => (*(void***)p)[slot];

    public static uint AddRef(void* p) => p == null ? 0 : ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(p, 1))(p);

    public static void Release<T>(ref T* p) where T : unmanaged
    {
        if (p == null) return;
        var q = p;
        p = null;
        ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(q, 2))(q);
    }

    public static void Release(ref void* p)
    {
        if (p == null) return;
        var q = p;
        p = null;
        ((delegate* unmanaged[Stdcall]<void*, uint>)Slot(q, 2))(q);
    }

    public static int QueryInterface(void* p, in Guid iid, out void* result)
    {
        void* r = null;
        int hr;
        fixed (Guid* g = &iid) hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)Slot(p, 0))(p, g, &r);
        result = r;
        return hr;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new OverlayGraphicsException(what, hr);
    }
}

/// <summary>A failed native graphics call (HRESULT kept for device-lost handling).</summary>
internal sealed class OverlayGraphicsException(string what, int hr)
    : Exception($"{what} failed: 0x{hr:X8}")
{
    public int HResult2 { get; } = hr;
}
