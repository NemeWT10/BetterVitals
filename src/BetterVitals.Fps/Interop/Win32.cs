using System.Runtime.InteropServices;

namespace BetterVitals.Fps.Interop;

/// <summary>Window / Toolhelp APIs used to find the foreground process. Never opens a process handle.</summary>
internal static unsafe partial class Win32
{
    public const uint Th32csSnapProcess = 0x2;

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint hwnd, char* className, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32First(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32Next(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}

/// <summary>PROCESSENTRY32W.</summary>
[StructLayout(LayoutKind.Explicit, Size = 568)]
internal unsafe struct ProcessEntry32
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(8)] public uint ProcessId;
    [FieldOffset(44)] public fixed char ExeFile[260];
}
