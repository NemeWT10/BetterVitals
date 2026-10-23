using System.Runtime.InteropServices;
using BetterVitals.Fps.Interop;

namespace BetterVitals.Fps;

/// <summary>Result of creating the session.</summary>
internal enum SessionStartResult
{
    Ok,
    /// <summary>ERROR_ACCESS_DENIED: neither administrator nor in "Performance Log Users".</summary>
    NoAccess,
    /// <summary>Any other failure (session limit, anti-cheat interference, consumer could not attach).</summary>
    Failed,
}

/// <summary>
/// One real-time ETW session plus its consumer thread. Created without providers (EA AntiCheat blocks StartTraceW once
/// it runs, PresentMon issue #573); <see cref="EnableProviders"/> / <see cref="DisableProviders"/> switch the present
/// events on and off later. Not thread-safe: the owner serializes calls.
/// </summary>
internal sealed unsafe class PresentSession : IDisposable
{
    private const int NameChars = 256;
    private static readonly int PropsBytes = sizeof(EventTraceProperties) + NameChars * 2 * 2;

    private readonly string _name;
    private readonly PresentRing _ring;
    private ulong _session;
    private ulong _consumer = Etw.InvalidProcessTraceHandle;
    private EventTraceLogfile* _logfile;
    private char* _loggerName;
    private Thread? _thread;
    private volatile bool _stopping;

    public PresentSession(string name, PresentRing ring)
    {
        _name = name;
        _ring = ring;
    }

    public string Name => _name;

    public bool IsRunning => _session != 0 && _thread is { IsAlive: true };

    public bool ProvidersEnabled { get; private set; }

    /// <summary>Test hook: false = only the DxgKrnl fallback (as for Vulkan / OpenGL apps).</summary>
    internal bool EnableRuntimeProviders { get; set; } = true;

    /// <summary>Win32 error of the last failed call (0 = none).</summary>
    public uint LastError { get; private set; }

    /// <summary>Managed id of the consumer thread (diagnostics / CPU measurement).</summary>
    public int ConsumerManagedThreadId => _thread?.ManagedThreadId ?? 0;

    /// <summary>OS id of the consumer thread, set once it runs.</summary>
    public int ConsumerOsThreadId { get; private set; }

    /// <summary>Raised on the consumer thread when ProcessTrace returns while we did not ask it to (session stopped outside).</summary>
    public event Action? Lost;

    public SessionStartResult Start()
    {
        StopByName(_name); // a stale session of a previous run (sessions outlive their process)

        var props = AllocProps(out var mem);
        try
        {
            props->BufferSize = 64; // KB
            props->MinimumBuffers = 8;
            props->MaximumBuffers = 16;
            props->FlushTimer = 1; // s
            props->LogFileMode = Etw.EventTraceRealTimeMode | Etw.EventTraceNoPerProcessorBuffering;
            var rc = Etw.StartTrace(out _session, _name, props);
            if (rc == Etw.ErrorInvalidParameter)
            {
                ResetProps(props);
                props->BufferSize = 64;
                props->MinimumBuffers = 8;
                props->MaximumBuffers = 16;
                props->FlushTimer = 1;
                props->LogFileMode = Etw.EventTraceRealTimeMode;
                rc = Etw.StartTrace(out _session, _name, props);
            }
            if (rc == Etw.ErrorAlreadyExists)
            {
                StopByName(_name);
                ResetProps(props);
                props->BufferSize = 64;
                props->MinimumBuffers = 8;
                props->MaximumBuffers = 16;
                props->FlushTimer = 1;
                props->LogFileMode = Etw.EventTraceRealTimeMode | Etw.EventTraceNoPerProcessorBuffering;
                rc = Etw.StartTrace(out _session, _name, props);
            }
            if (rc != Etw.ErrorSuccess)
            {
                _session = 0;
                LastError = rc;
                return rc == Etw.ErrorAccessDenied ? SessionStartResult.NoAccess : SessionStartResult.Failed;
            }
        }
        finally
        {
            NativeMemory.Free(mem);
        }

        _loggerName = (char*)NativeMemory.AllocZeroed((nuint)(_name.Length + 1), sizeof(char));
        _name.AsSpan().CopyTo(new Span<char>(_loggerName, _name.Length));
        _logfile = (EventTraceLogfile*)NativeMemory.AllocZeroed((nuint)sizeof(EventTraceLogfile));
        _logfile->LoggerName = _loggerName;
        _logfile->ProcessTraceMode = Etw.ProcessTraceModeRealTime | Etw.ProcessTraceModeEventRecord | Etw.ProcessTraceModeRawTimestamp;
        _logfile->EventRecordCallback = &PresentRing.OnEvent;
        _logfile->Context = _ring.Shared;

        _consumer = Etw.OpenTrace(_logfile);
        if (_consumer == Etw.InvalidProcessTraceHandle)
        {
            LastError = (uint)Marshal.GetLastPInvokeError();
            StopSession();
            FreeLogfile();
            return SessionStartResult.Failed;
        }

        _stopping = false;
        _thread = new Thread(Consume, 256 * 1024) { IsBackground = true, Name = "BetterVitals FPS (ETW)" };
        _thread.Start();
        return SessionStartResult.Ok;
    }

    private void Consume()
    {
        ConsumerOsThreadId = (int)Win32.GetCurrentThreadId();
        var handle = _consumer;
        var rc = Etw.ProcessTrace(&handle, 1, 0, 0);
        if (!_stopping)
        {
            LastError = rc;
            try { Lost?.Invoke(); } catch { /* never take the thread down */ }
        }
    }

    /// <summary>Turns the present events on. Returns the Win32 error (0 = ok).</summary>
    public uint EnableProviders()
    {
        if (_session == 0) return LastError = Etw.ErrorInvalidParameter;
        if (ProvidersEnabled) return 0;

        // backend first (fewer runtime presents queued before their kernel side is seen), like PresentMon
        var rc = Enable(PresentProviders.DxgKrnl, PresentProviders.DxgKrnlKeywords,
            PresentProviders.DxgKrnlPresentHistoryStart, PresentProviders.DxgKrnlPresentHistoryDetailedStart);
        if (rc == 0 && EnableRuntimeProviders) rc = Enable(PresentProviders.Dxgi, PresentProviders.RuntimeKeywords,
            PresentProviders.DxgiPresentStart, PresentProviders.DxgiPresentMultiplaneOverlayStart);
        if (rc == 0 && EnableRuntimeProviders) rc = Enable(PresentProviders.D3D9, PresentProviders.RuntimeKeywords, PresentProviders.D3D9PresentStart, 0);
        if (rc != 0)
        {
            LastError = rc;
            DisableAll();
            return rc;
        }
        ProvidersEnabled = true;
        return 0;
    }

    /// <summary>Turns the present events off; the session and its consumer thread stay (idle, no events).</summary>
    public void DisableProviders()
    {
        if (!ProvidersEnabled) return;
        DisableAll();
        ProvidersEnabled = false;
    }

    private void DisableAll()
    {
        if (_session == 0) return;
        var g = PresentProviders.D3D9;
        Etw.EnableTraceEx2(_session, &g, Etw.ControlCodeDisableProvider, 0, 0, 0, 0, null);
        g = PresentProviders.Dxgi;
        Etw.EnableTraceEx2(_session, &g, Etw.ControlCodeDisableProvider, 0, 0, 0, 0, null);
        g = PresentProviders.DxgKrnl;
        Etw.EnableTraceEx2(_session, &g, Etw.ControlCodeDisableProvider, 0, 0, 0, 0, null);
    }

    private uint Enable(Guid provider, ulong keywords, ushort id1, ushort id2)
    {
        // EVENT_FILTER_EVENT_ID: FilterIn (BOOLEAN), Reserved (UCHAR), Count (USHORT), Events[Count] (USHORT)
        var count = id2 == 0 ? 1 : 2;
        var filter = stackalloc byte[4 + 2 * 2];
        filter[0] = 1;
        filter[1] = 0;
        *(ushort*)(filter + 2) = (ushort)count;
        ((ushort*)(filter + 4))[0] = id1;
        ((ushort*)(filter + 4))[1] = id2;

        EventFilterDescriptor desc;
        desc.Ptr = (ulong)filter;
        desc.Size = (uint)(4 + 2 * count);
        desc.Type = Etw.FilterTypeEventId;

        EnableTraceParameters p = default;
        p.Version = Etw.EnableTraceParametersVersion2;
        p.EnableProperty = Etw.EnablePropertyIgnoreKeyword0;
        p.EnableFilterDesc = &desc;
        p.FilterDescCount = 1;

        return Etw.EnableTraceEx2(_session, &provider, Etw.ControlCodeEnableProvider, PresentProviders.Level,
            keywords, keywords, 0, &p);
    }

    /// <summary>Session buffers as reported by ETW (diagnostics): count, size in KB, events lost.</summary>
    public (uint Buffers, uint BufferSizeKb, uint EventsLost, uint RealTimeBuffersLost)? Query()
    {
        if (_session == 0) return null;
        var props = AllocProps(out var mem);
        try
        {
            var rc = Etw.ControlTrace(_session, null, props, Etw.ControlQuery);
            if (rc != 0) return null;
            return (props->NumberOfBuffers, props->BufferSize, props->EventsLost, props->RealTimeBuffersLost);
        }
        finally
        {
            NativeMemory.Free(mem);
        }
    }

    /// <summary>Stops a session by name (ours from a crashed run). Returns the Win32 error.</summary>
    internal static uint StopByName(string name)
    {
        var props = AllocProps(out var mem);
        try
        {
            return Etw.ControlTrace(0, name, props, Etw.ControlStop);
        }
        finally
        {
            NativeMemory.Free(mem);
        }
    }

    private static EventTraceProperties* AllocProps(out void* mem)
    {
        mem = NativeMemory.AllocZeroed((nuint)PropsBytes);
        var props = (EventTraceProperties*)mem;
        ResetProps(props);
        return props;
    }

    private static void ResetProps(EventTraceProperties* props)
    {
        new Span<byte>(props, PropsBytes).Clear();
        props->WnodeBufferSize = (uint)PropsBytes;
        props->WnodeFlags = Etw.WnodeFlagTracedGuid;
        props->WnodeClientContext = Etw.ClockQpc;
        props->LoggerNameOffset = (uint)sizeof(EventTraceProperties);
        props->LogFileNameOffset = (uint)(sizeof(EventTraceProperties) + NameChars * 2);
    }

    private void StopSession()
    {
        if (_session == 0) return;
        var props = AllocProps(out var mem);
        try
        {
            Etw.ControlTrace(_session, null, props, Etw.ControlStop);
        }
        finally
        {
            NativeMemory.Free(mem);
        }
        _session = 0;
        ProvidersEnabled = false;
    }

    private void FreeLogfile()
    {
        if (_logfile != null) NativeMemory.Free(_logfile);
        if (_loggerName != null) NativeMemory.Free(_loggerName);
        _logfile = null;
        _loggerName = null;
    }

    /// <summary>Stops the session and the consumer thread. Returns false when the thread did not exit (memory is then kept).</summary>
    public bool Stop()
    {
        _stopping = true;
        if (ProvidersEnabled) DisableProviders();
        StopSession();
        if (_consumer != Etw.InvalidProcessTraceHandle)
        {
            Etw.CloseTrace(_consumer);
            _consumer = Etw.InvalidProcessTraceHandle;
        }
        var exited = _thread is null || _thread.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        if (exited) FreeLogfile();
        return exited;
    }

    public void Dispose() => Stop();
}
