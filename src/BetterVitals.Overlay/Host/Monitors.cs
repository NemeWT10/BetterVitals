using System.Runtime.InteropServices;
using BetterVitals.Overlay.Interop;

namespace BetterVitals.Overlay.Host;

/// <summary>Enumerates monitors in physical pixels (call from a per-monitor-DPI-aware V2 thread).</summary>
internal static unsafe partial class Monitors
{
    [ThreadStatic] private static List<nint>? _handles;

    public static List<OverlayMonitor> Enumerate()
    {
        _handles = [];
        Win32.EnumDisplayMonitors(0, null, &OnMonitor, 0);
        var handles = _handles;
        _handles = null;
        var names = DisplayConfigNames();
        var list = new List<OverlayMonitor>(handles.Count);
        foreach (var h in handles)
            if (Describe(h, names) is { } m) list.Add(m);
        return list;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data)
    {
        _handles?.Add(monitor);
        return 1;
    }

    /// <summary>Monitor info for a handle (also used for the foreground window's monitor).</summary>
    public static OverlayMonitor? Describe(nint monitor, Dictionary<string, (string Path, string Name)>? names = null)
    {
        var mi = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (!Win32.GetMonitorInfo(monitor, &mi)) return null;
        var device = new string(mi.szDevice);
        var nul = device.IndexOf('\0');
        if (nul >= 0) device = device[..nul];
        names ??= DisplayConfigNames();
        string key, friendly;
        if (names.TryGetValue(device, out var n))
        {
            key = n.Path;
            friendly = n.Name;
        }
        else
        {
            key = InterfacePath(device) ?? device;
            friendly = "";
        }
        if (friendly.Length == 0) friendly = $"Display {device.TrimStart('\\', '.').Replace("DISPLAY", "", StringComparison.OrdinalIgnoreCase)}";
        return new OverlayMonitor(key, device, friendly,
            new PixelRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
            new PixelRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
            (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0);
    }

    private static string? InterfacePath(string device)
    {
        var dd = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
        fixed (char* d = device)
            if (!Win32.EnumDisplayDevices(d, 0, &dd, Win32.EDD_GET_DEVICE_INTERFACE_NAME)) return null;
        var id = new string(dd.DeviceID);
        var nul = id.IndexOf('\0');
        if (nul >= 0) id = id[..nul];
        return id.Length > 0 ? id : null;
    }

    // ── DisplayConfig: EDID friendly names + monitor device paths, keyed by GDI device name ──

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public ulong SourceAdapter;
        public uint SourceId, SourceModeIdx, SourceStatus;
        public ulong TargetAdapter;
        public uint TargetId, TargetModeIdx, Tech, Rotation, Scaling, RefreshNum, RefreshDen, ScanLine, Available, TargetStatus;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Type;
        public uint Size;
        public ulong Adapter;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceName
    {
        public Header Header;
        public fixed char GdiName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetName
    {
        public Header Header;
        public uint Flags, Tech;
        public ushort Manufacturer, Product;
        public uint Connector;
        public fixed char FriendlyName[64];
        public fixed char DevicePath[128];
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, uint* paths, uint* modes);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, uint* paths, PathInfo* pathArray, uint* modes, void* modeArray, void* topology);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(Header* packet);

    private static Dictionary<string, (string Path, string Name)> DisplayConfigNames()
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            uint np = 0, nm = 0;
            if (GetDisplayConfigBufferSizes(2, &np, &nm) != 0 || np == 0) return result;
            var paths = new PathInfo[np];
            var modes = new byte[nm * 64];
            fixed (PathInfo* pp = paths)
            fixed (byte* mp = modes)
                if (QueryDisplayConfig(2, &np, pp, &nm, mp, null) != 0) return result;
            for (var i = 0; i < np; i++)
            {
                var src = new SourceName { Header = { Type = 1, Size = (uint)sizeof(SourceName), Adapter = paths[i].SourceAdapter, Id = paths[i].SourceId } };
                if (DisplayConfigGetDeviceInfo(&src.Header) != 0) continue;
                var tgt = new TargetName { Header = { Type = 2, Size = (uint)sizeof(TargetName), Adapter = paths[i].TargetAdapter, Id = paths[i].TargetId } };
                if (DisplayConfigGetDeviceInfo(&tgt.Header) != 0) continue;
                var gdi = Str(src.GdiName, 32);
                var path = Str(tgt.DevicePath, 128);
                if (gdi.Length > 0 && path.Length > 0 && !result.ContainsKey(gdi)) result[gdi] = (path, Str(tgt.FriendlyName, 64));
            }
        }
        catch
        {
            // names are cosmetic; keys fall back to EnumDisplayDevices
        }
        return result;
    }

    private static string Str(char* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != '\0') n++;
        return new string(p, 0, n);
    }
}
