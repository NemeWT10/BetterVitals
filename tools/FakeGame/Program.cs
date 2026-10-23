using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FakeGame;

/// <summary>
/// A fake game for FPS tests: a D3D11 flip-model swap chain that clears and presents at a fixed rate.
/// The window lives on a private hidden desktop (default) or far off screen as a never-activated tool window, so it
/// never appears on the user's screen. Usage:
/// <code>FakeGame --fps 60 --seconds 15 [--vsync off] [--jitter] [--seed 1] [--desktop hidden|offscreen]</code>
/// Prints "READY &lt;pid&gt;" after the first present and "DONE presents=… elapsed=… fps=…" at the end.
/// </summary>
internal static unsafe class Program
{
    private sealed record Options(double Fps, double Seconds, bool Vsync, bool Jitter, int Seed, bool HiddenDesktop);

    private static int Main(string[] args)
    {
        Options opt;
        try
        {
            opt = Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var exit = 1;
        // a fresh thread has no windows / hooks, so SetThreadDesktop works on it
        var t = new Thread(() => exit = Run(opt), 1024 * 1024) { Name = "render" };
        t.Start();
        t.Join();
        return exit;
    }

    private static Options Parse(string[] args)
    {
        double fps = 60, seconds = 15;
        bool vsync = true, jitter = false, hidden = true;
        var seed = 1;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {args[i]}");
            switch (args[i])
            {
                case "--fps": fps = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seconds": seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--vsync": vsync = Next() is not ("off" or "0" or "false"); break;
                case "--jitter": jitter = true; break;
                case "--seed": seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--desktop": hidden = Next() != "offscreen"; break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (fps is <= 0 or > 5000) throw new ArgumentException("--fps must be in (0, 5000]");
        return new Options(fps, seconds, vsync, jitter, seed, hidden);
    }

    private static int Run(Options opt)
    {
        nint desktop = 0, hwnd = 0, timer = 0;
        nint swapChain = 0, device = 0, context = 0, rtv = 0;
        try
        {
            if (opt.HiddenDesktop)
            {
                desktop = CreateDesktopW($"BetterVitals-FakeGame-{Environment.ProcessId}", null, null, 0, DesktopAccess, null);
                if (desktop == 0) return Fail("CreateDesktop", Marshal.GetLastPInvokeError());
                if (!SetThreadDesktop(desktop)) return Fail("SetThreadDesktop", Marshal.GetLastPInvokeError());
            }

            hwnd = CreateWindow(opt.HiddenDesktop);
            if (hwnd == 0) return Fail("CreateWindowEx", Marshal.GetLastPInvokeError());

            var desc = new SwapChainDesc
            {
                Width = 256,
                Height = 144,
                Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
                SampleCount = 1,
                BufferUsage = 0x20, // DXGI_USAGE_RENDER_TARGET_OUTPUT
                BufferCount = 2,
                OutputWindow = hwnd,
                Windowed = 1,
                SwapEffect = 4, // DXGI_SWAP_EFFECT_FLIP_DISCARD
            };
            int featureLevel;
            var hr = D3D11CreateDeviceAndSwapChain(0, 1 /* HARDWARE */, 0, 0, null, 0, 7, &desc, &swapChain, &device, &featureLevel, &context);
            if (hr < 0) return Fail("D3D11CreateDeviceAndSwapChain", hr);

            nint backBuffer;
            var iidTexture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            hr = ((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Vtbl(swapChain)[9])(swapChain, 0, &iidTexture2D, &backBuffer); // IDXGISwapChain::GetBuffer
            if (hr < 0) return Fail("GetBuffer", hr);
            hr = ((delegate* unmanaged<nint, nint, void*, nint*, int>)Vtbl(device)[9])(device, backBuffer, null, &rtv); // ID3D11Device::CreateRenderTargetView
            Release(backBuffer);
            if (hr < 0) return Fail("CreateRenderTargetView", hr);

            timer = CreateWaitableTimerExW(0, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);
            if (timer == 0) timer = CreateWaitableTimerExW(0, null, 0, 0x1F0003);

            var clear = (delegate* unmanaged<nint, nint, float*, void>)Vtbl(context)[50]; // ID3D11DeviceContext::ClearRenderTargetView
            var present = (delegate* unmanaged<nint, uint, uint, int>)Vtbl(swapChain)[8]; // IDXGISwapChain::Present
            var sync = opt.Vsync ? 1u : 0u;
            var rng = new Random(opt.Seed);
            var freq = (double)Stopwatch.Frequency;
            var period = freq / opt.Fps;
            var total = (long)Math.Round(opt.Fps * opt.Seconds);
            long occluded = 0, failed = 0;
            var color = stackalloc float[4];

            var start = Stopwatch.GetTimestamp();
            long frame = 0;
            for (; frame < total; frame++)
            {
                // absolute schedule: the average rate is exact whatever the per-frame wake-up error
                var offset = opt.Jitter && frame > 0 ? (rng.NextDouble() - 0.5) * 0.5 * period : 0;
                WaitUntil(timer, start + (long)(frame * period + offset));
                Pump();

                var phase = (float)(frame % 240) / 240f;
                color[0] = phase;
                color[1] = 0.2f;
                color[2] = 1f - phase;
                color[3] = 1f;
                clear(context, rtv, color);
                hr = present(swapChain, sync, 0);
                if (hr == 0x087A0001) occluded++; // DXGI_STATUS_OCCLUDED
                else if (hr < 0) failed++;
                if (frame == 0)
                {
                    Console.Out.WriteLine($"READY {Environment.ProcessId}");
                    Console.Out.Flush();
                }
            }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"DONE presents={frame} elapsed={elapsed:F3} fps={(frame > 1 ? (frame - 1) / elapsed : 0):F2} occluded={occluded} failed={failed}"));
            return failed > 0 ? 3 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Release(rtv);
            Release(context);
            Release(swapChain);
            Release(device);
            if (timer != 0) CloseHandle(timer);
            if (hwnd != 0) DestroyWindow(hwnd);
            if (desktop != 0) CloseDesktop(desktop);
        }
    }

    private static int Fail(string what, int code)
    {
        Console.Error.WriteLine($"{what} failed: 0x{code:X8}");
        return 1;
    }

    private static void WaitUntil(nint timer, long deadline)
    {
        // sleep on the high-resolution timer until ~1 ms before, then spin
        var spinFrom = deadline - Stopwatch.Frequency / 1000;
        var now = Stopwatch.GetTimestamp();
        if (timer != 0 && now < spinFrom)
        {
            var due = -(long)((spinFrom - now) * 10_000_000.0 / Stopwatch.Frequency); // relative, 100 ns units
            if (SetWaitableTimer(timer, &due, 0, 0, 0, false)) WaitForSingleObject(timer, 0xFFFFFFFF);
        }
        while (Stopwatch.GetTimestamp() < deadline) Thread.SpinWait(20);
    }

    private static void Pump()
    {
        Msg msg;
        while (PeekMessageW(&msg, 0, 0, 0, 1 /* PM_REMOVE */))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    private static nint CreateWindow(bool hiddenDesktop)
    {
        var module = GetModuleHandleW(null);
        fixed (char* name = "BetterVitalsFakeGame")
        {
            var wc = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                Instance = module,
                ClassName = name,
            };
            RegisterClassExW(&wc);
            const uint exStyle = 0x80 /* TOOLWINDOW */ | 0x0800_0000 /* NOACTIVATE */;
            const uint style = 0x8000_0000; // WS_POPUP
            // on the hidden desktop nobody sees it; off screen it is a never-activated tool window far outside any monitor
            int x = hiddenDesktop ? 0 : -32000, y = hiddenDesktop ? 0 : -32000;
            var hwnd = CreateWindowExW(exStyle, name, name, style, x, y, 256, 144, 0, 0, module, null);
            if (hwnd != 0) ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
            return hwnd;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint w, nint l)
        => msg == 0x0021 /* WM_MOUSEACTIVATE */ ? 3 /* MA_NOACTIVATE */ : DefWindowProcW(hwnd, msg, w, l);

    private static void** Vtbl(nint obj) => *(void***)obj;

    private static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged<nint, uint>)Vtbl(obj)[2])(obj);
    }

    private const uint DesktopAccess = 0x01 | 0x02 | 0x04 | 0x08 | 0x40 | 0x80; // read, create window / menu, hooks, enumerate, write

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapChainDesc
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling;
        public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
        public nint OutputWindow;
        public int Windowed;
        public uint SwapEffect, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public nint IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y, Private;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDeviceAndSwapChain(nint adapter, int driverType, nint software, uint flags, int* featureLevels,
        uint featureLevelCount, uint sdkVersion, SwapChainDesc* desc, nint* swapChain, nint* device, int* featureLevel, nint* context);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateDesktopW(string name, string? device, void* devmode, uint flags, uint access, void* sa);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern ushort RegisterClassExW(WndClassEx* wc);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, void* param);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PeekMessageW(Msg* msg, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TranslateMessage(Msg* msg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DispatchMessageW(Msg* msg);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool SetWaitableTimer(nint timer, long* due, int period, nint completion, nint arg, bool resume);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(nint handle, uint ms);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool CloseHandle(nint handle);
}
