using System.Runtime.InteropServices;

namespace BetterVitals.Platform;

/// <summary>
/// Is a kernel anti-cheat running? Used only for the optional "pause driver-backed sensors during anti-cheat games"
/// setting. Reads process names from a Toolhelp snapshot — no handles to any process, nothing an anti-cheat could see
/// as probing. Cheap (~1 ms); the app calls it when the foreground app changes, never on a timer.
/// </summary>
public static class AntiCheat
{
    /// <summary>Process names (lowercase) of known anti-cheat services / clients.</summary>
    internal static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "vgc.exe",                              // Riot Vanguard
        "faceitservice.exe", "faceit.exe",      // FACEIT
        "easyanticheat.exe", "easyanticheat_eos.exe", "easyanticheat_eos_setup.exe",
        "beservice.exe", "beservice_x64.exe",   // BattlEye
        "eaanticheat.gameservice.exe", "eaanticheat.gameservicelauncher.exe", // EA Javelin
        "gameguard.des", "gamemon.des",         // nProtect GameGuard
        "xigncode3.xem",
        "esportal.exe", "eseaclient.exe",       // other league clients
    };

    /// <summary>First running anti-cheat process name, or null.</summary>
    public static string? FindRunning()
    {
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return null;
            do
            {
                if (Known.Contains(e.szExeFile)) return e.szExeFile;
            }
            while (Process32NextW(snap, ref e));
            return null;
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
