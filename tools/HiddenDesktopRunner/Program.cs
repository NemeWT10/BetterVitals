using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HiddenDesktopRunner;

/// <summary>
/// HiddenDesktopRunner [--timeout seconds] [--cwd dir] -- command line
/// Exit codes: the child's own code; 124 = timeout (process tree killed); 125 = runner error / bad usage.
/// The user's input desktop is never switched: the child lives on its own desktop and cannot receive their input.
/// </summary>
internal static class Program
{
    private const int ExitTimeout = 124;
    private const int ExitRunnerError = 125;

    private static int Main(string[] args)
    {
        var timeout = TimeSpan.FromSeconds(120);
        string? cwd = null;
        var i = 0;
        for (; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                i++;
                break;
            }
            if (args[i] == "--timeout" && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var s))
            {
                timeout = TimeSpan.FromSeconds(s);
                i++;
            }
            else if (args[i] == "--cwd" && i + 1 < args.Length)
            {
                cwd = args[++i];
            }
            else
            {
                break; // first non-option token starts the command line (no explicit "--")
            }
        }
        if (i >= args.Length)
        {
            Console.Error.WriteLine("usage: HiddenDesktopRunner [--timeout <seconds>] [--cwd <dir>] -- <command line>");
            return ExitRunnerError;
        }
        var commandLine = string.Join(" ", args.Skip(i).Select(Quote));

        try
        {
            return Run(commandLine, cwd, timeout);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("HiddenDesktopRunner: " + ex.Message);
            return ExitRunnerError;
        }
    }

    /// <summary>Re-quotes an argument for CreateProcess when it contains whitespace and is not quoted yet.</summary>
    private static string Quote(string a)
    {
        if (a.Length == 0) return "\"\"";
        if (!a.Any(char.IsWhiteSpace) || (a.StartsWith('"') && a.EndsWith('"'))) return a;
        return "\"" + a.Replace("\"", "\\\"") + "\"";
    }

    private static int Run(string commandLine, string? cwd, TimeSpan timeout)
    {
        var station = StationName();
        var desktopName = $"BetterVitalsTest-{Environment.ProcessId}";
        var desktop = NativeMethods.CreateDesktopW(desktopName, null, 0, 0, NativeMethods.GENERIC_ALL, 0);
        if (desktop == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop failed");

        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, inRead = null, inWrite = null;
        nint job = 0;
        var pi = new NativeMethods.PROCESS_INFORMATION();
        try
        {
            var sa = new NativeMethods.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
            CreatePipe(out outRead, out outWrite, ref sa);
            CreatePipe(out errRead, out errWrite, ref sa);
            CreatePipe(out inRead, out inWrite, ref sa);
            // our ends must not be inherited by the child
            NativeMethods.SetHandleInformation(outRead, NativeMethods.HANDLE_FLAG_INHERIT, 0);
            NativeMethods.SetHandleInformation(errRead, NativeMethods.HANDLE_FLAG_INHERIT, 0);
            NativeMethods.SetHandleInformation(inWrite, NativeMethods.HANDLE_FLAG_INHERIT, 0);

            // the whole process tree dies with this runner (or on timeout)
            job = NativeMethods.CreateJobObjectW(0, null);
            var limits = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf(limits));

            var si = new NativeMethods.STARTUPINFO
            {
                lpDesktop = station + "\\" + desktopName,
                dwFlags = NativeMethods.STARTF_USESTDHANDLES,
                hStdOutput = outWrite.DangerousGetHandle(),
                hStdError = errWrite.DangerousGetHandle(),
                hStdInput = inRead.DangerousGetHandle(),
            };
            si.cb = Marshal.SizeOf(si);

            var cmd = new StringBuilder(commandLine);
            if (!NativeMethods.CreateProcessW(null, cmd, 0, 0, true,
                    NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_NEW_PROCESS_GROUP | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                    0, cwd, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for: " + commandLine);

            NativeMethods.AssignProcessToJobObject(job, pi.hProcess);
            NativeMethods.ResumeThread(pi.hThread);

            // the child owns these ends now
            outWrite.Dispose();
            errWrite.Dispose();
            inRead.Dispose();
            inWrite.Dispose(); // the child gets an immediately closed stdin

            var stdout = Pump(outRead, Console.OpenStandardOutput());
            var stderr = Pump(errRead, Console.OpenStandardError());

            var wait = NativeMethods.WaitForSingleObject(pi.hProcess, (uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1));
            int exit;
            if (wait == NativeMethods.WAIT_TIMEOUT)
            {
                Console.Error.WriteLine($"HiddenDesktopRunner: timeout after {timeout.TotalSeconds:0.#} s, killing the process tree");
                NativeMethods.TerminateJobObject(job, ExitTimeout);
                NativeMethods.WaitForSingleObject(pi.hProcess, 5000);
                exit = ExitTimeout;
            }
            else
            {
                NativeMethods.GetExitCodeProcess(pi.hProcess, out var code);
                exit = unchecked((int)code);
            }
            stdout.Join(2000);
            stderr.Join(2000);
            return exit;
        }
        finally
        {
            if (pi.hProcess != 0) NativeMethods.CloseHandle(pi.hProcess);
            if (pi.hThread != 0) NativeMethods.CloseHandle(pi.hThread);
            if (job != 0) NativeMethods.CloseHandle(job); // KILL_ON_JOB_CLOSE: nothing is left on the desktop
            outRead?.Dispose();
            outWrite?.Dispose();
            errRead?.Dispose();
            errWrite?.Dispose();
            inRead?.Dispose();
            inWrite?.Dispose();
            NativeMethods.CloseDesktop(desktop);
        }
    }

    private static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref NativeMethods.SECURITY_ATTRIBUTES sa)
    {
        if (!NativeMethods.CreatePipe(out read, out write, ref sa, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
    }

    private static Thread Pump(SafeFileHandle read, Stream target)
    {
        var t = new Thread(() =>
        {
            try
            {
                using var src = new FileStream(read, FileAccess.Read, 4096, false);
                var buf = new byte[4096];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    target.Write(buf, 0, n);
                    target.Flush();
                }
            }
            catch (IOException)
            {
                // pipe closed
            }
        })
        { IsBackground = true };
        t.Start();
        return t;
    }

    private static string StationName()
    {
        var ws = NativeMethods.GetProcessWindowStation();
        var buf = new char[256];
        if (ws != 0 && NativeMethods.GetUserObjectInformationW(ws, NativeMethods.UOI_NAME, buf, buf.Length * 2, out var needed))
            return new string(buf, 0, Math.Max(0, (int)needed / 2 - 1));
        return "WinSta0";
    }
}

internal static class NativeMethods
{
    public const uint GENERIC_ALL = 0x10000000;
    public const uint STARTF_USESTDHANDLES = 0x00000100;
    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint HANDLE_FLAG_INHERIT = 1;
    public const int UOI_NAME = 2;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    public const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateDesktopW(string lpszDesktop, string? lpszDevice, nint pDevmode, uint dwFlags, uint dwDesiredAccess, nint lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(nint hDesktop);

    [DllImport("user32.dll")]
    public static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetUserObjectInformationW(nint hObj, int nIndex, [Out] char[] pvInfo, int nLength, out uint lpnLengthNeeded);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, nint lpProcessAttributes, nint lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    public static extern uint ResumeThread(nint hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(nint hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, int cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(nint hJob, uint uExitCode);
}
