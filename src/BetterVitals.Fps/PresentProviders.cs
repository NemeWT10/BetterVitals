namespace BetterVitals.Fps;

/// <summary>
/// The minimal set of ETW present events that count frames per process.
/// GUIDs, event ids, levels and keywords come from Intel PresentMon (MIT, github.com/GameTechDev/PresentMon:
/// PresentData/PresentMonTraceSession.cpp, PresentData/ETW/*.h). Like PresentMon without display tracking, a frame is
/// a runtime Present_Start (DXGI: D3D10 / 11 / 12; D3D9), and for apps without a runtime event (Vulkan, OpenGL) the
/// DxgKrnl PresentHistory(Detailed)_Start emitted while submitting every windowed present. DxgKrnl writes exactly one
/// of the two per present: the Detailed one when its keywords are on (verified on Windows 11 24H2 for flip-model
/// presents), the plain one otherwise.
/// </summary>
internal static class PresentProviders
{
    /// <summary>Microsoft-Windows-DXGI.</summary>
    public static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    /// <summary>Microsoft-Windows-D3D9.</summary>
    public static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    /// <summary>Microsoft-Windows-DxgKrnl.</summary>
    public static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    public const ushort DxgiPresentStart = 42;
    public const ushort DxgiPresentMultiplaneOverlayStart = 55;
    public const ushort D3D9PresentStart = 1;
    public const ushort DxgKrnlPresentHistoryStart = 171;
    public const ushort DxgKrnlPresentHistoryDetailedStart = 215;

    /// <summary>Microsoft_Windows_DXGI_Analytic | Events (DXGI) and Microsoft_Windows_Direct3D9_Analytic | Events (D3D9).</summary>
    public const ulong RuntimeKeywords = 0x8000_0000_0000_0002;

    /// <summary>
    /// Base | Present. PresentMon never filters DxgKrnl on its Performance keyword (0x4000000000000000): side effects
    /// with a performance cost on some Windows versions.
    /// </summary>
    public const ulong DxgKrnlKeywords = 0x0000_0000_0800_0001;

    /// <summary>All the events above are level 0 (LogAlways); enabling at level 0 keeps every other event off.</summary>
    public const byte Level = 0;

    /// <summary>DXGI_PRESENT_TEST: checks occlusion, presents nothing.</summary>
    public const uint DxgiPresentTest = 0x1;

    /// <summary>PresentHistory models that are not app frames (PresentMon ignores them too).</summary>
    public const uint ModelRedirectedGdi = 1;
    public const uint ModelRedirectedComposition = 7;

    /// <summary>
    /// Byte offset of <c>Model</c> in PresentHistory_Start / PresentHistoryDetailed_Start v2 (hAdapter: pointer,
    /// Token: UInt64, Model: UInt32; checked against the provider manifest with tracerpt).
    /// </summary>
    public const int PresentHistoryModelOffset64 = 16;
}

/// <summary>Where a present was seen; lower value = preferred (a lower class suppresses higher ones for the same process).</summary>
internal enum PresentClass
{
    Dxgi = 0,
    D3D9 = 1,
    Kernel = 2,
}
