using System.Runtime.InteropServices;

namespace BetterVitals.Fps.Interop;

/// <summary>Raw ETW controller / consumer API (advapi32). x64 layouts only (the app is win-x64).</summary>
internal static unsafe partial class Etw
{
    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorInvalidParameter = 87;
    public const uint ErrorCancelled = 1223;
    public const uint ErrorAlreadyExists = 183;
    public const uint ErrorWmiInstanceNotFound = 4201;
    public const uint ErrorCtxClosePending = 7007;

    public const uint WnodeFlagTracedGuid = 0x0002_0000;
    public const uint EventTraceRealTimeMode = 0x0000_0100;
    public const uint EventTraceNoPerProcessorBuffering = 0x1000_0000;
    public const uint ClockQpc = 1;

    public const uint ControlQuery = 0;
    public const uint ControlStop = 1;

    public const uint ControlCodeDisableProvider = 0;
    public const uint ControlCodeEnableProvider = 1;

    public const uint EnableTraceParametersVersion2 = 2;
    public const uint EnablePropertyIgnoreKeyword0 = 0x10;
    public const uint FilterTypeEventId = 0x8000_0200;

    public const uint ProcessTraceModeRealTime = 0x0000_0100;
    public const uint ProcessTraceModeRawTimestamp = 0x0000_1000;
    public const uint ProcessTraceModeEventRecord = 0x1000_0000;

    public const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    public const ushort EventHeaderFlag32BitHeader = 0x0020;

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint StartTrace(out ulong sessionHandle, string sessionName, EventTraceProperties* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ControlTrace(ulong sessionHandle, string? sessionName, EventTraceProperties* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    public static partial uint EnableTraceEx2(ulong sessionHandle, Guid* providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, EnableTraceParameters* parameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    public static partial ulong OpenTrace(EventTraceLogfile* logfile);

    [LibraryImport("advapi32.dll")]
    public static partial uint ProcessTrace(ulong* handleArray, uint handleCount, nint startTime, nint endTime);

    [LibraryImport("advapi32.dll")]
    public static partial uint CloseTrace(ulong traceHandle);
}

/// <summary>EVENT_TRACE_PROPERTIES (WNODE_HEADER inlined); followed in memory by the logger name.</summary>
[StructLayout(LayoutKind.Explicit, Size = 120)]
internal struct EventTraceProperties
{
    [FieldOffset(0)] public uint WnodeBufferSize;
    [FieldOffset(24)] public Guid WnodeGuid;
    [FieldOffset(40)] public uint WnodeClientContext;
    [FieldOffset(44)] public uint WnodeFlags;
    [FieldOffset(48)] public uint BufferSize;
    [FieldOffset(52)] public uint MinimumBuffers;
    [FieldOffset(56)] public uint MaximumBuffers;
    [FieldOffset(60)] public uint MaximumFileSize;
    [FieldOffset(64)] public uint LogFileMode;
    [FieldOffset(68)] public uint FlushTimer;
    [FieldOffset(72)] public uint EnableFlags;
    [FieldOffset(80)] public uint NumberOfBuffers;
    [FieldOffset(84)] public uint FreeBuffers;
    [FieldOffset(88)] public uint EventsLost;
    [FieldOffset(92)] public uint BuffersWritten;
    [FieldOffset(96)] public uint LogBuffersLost;
    [FieldOffset(100)] public uint RealTimeBuffersLost;
    [FieldOffset(112)] public uint LogFileNameOffset;
    [FieldOffset(116)] public uint LoggerNameOffset;
}

/// <summary>EVENT_TRACE_LOGFILEW (only the fields we set / read).</summary>
[StructLayout(LayoutKind.Explicit, Size = 448)]
internal unsafe struct EventTraceLogfile
{
    [FieldOffset(0)] public char* LogFileName;
    [FieldOffset(8)] public char* LoggerName;
    [FieldOffset(28)] public uint ProcessTraceMode;
    [FieldOffset(400)] public nint BufferCallback;
    [FieldOffset(416)] public uint EventsLost;
    [FieldOffset(424)] public delegate* unmanaged<EventRecord*, void> EventRecordCallback;
    [FieldOffset(440)] public void* Context;
}

/// <summary>EVENT_RECORD with EVENT_HEADER inlined.</summary>
[StructLayout(LayoutKind.Explicit, Size = 112)]
internal unsafe struct EventRecord
{
    [FieldOffset(4)] public ushort Flags;
    [FieldOffset(8)] public uint ThreadId;
    [FieldOffset(12)] public uint ProcessId;
    [FieldOffset(16)] public long TimeStamp;
    [FieldOffset(24)] public Guid ProviderId;
    [FieldOffset(24)] public ulong ProviderIdLo;
    [FieldOffset(32)] public ulong ProviderIdHi;
    [FieldOffset(40)] public ushort Id;
    [FieldOffset(42)] public byte Version;
    [FieldOffset(86)] public ushort UserDataLength;
    [FieldOffset(96)] public byte* UserData;
    [FieldOffset(104)] public void* UserContext;
}

/// <summary>ENABLE_TRACE_PARAMETERS (version 2).</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal unsafe struct EnableTraceParameters
{
    [FieldOffset(0)] public uint Version;
    [FieldOffset(4)] public uint EnableProperty;
    [FieldOffset(8)] public uint ControlFlags;
    [FieldOffset(12)] public Guid SourceId;
    [FieldOffset(32)] public EventFilterDescriptor* EnableFilterDesc;
    [FieldOffset(40)] public uint FilterDescCount;
}

/// <summary>EVENT_FILTER_DESCRIPTOR.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct EventFilterDescriptor
{
    [FieldOffset(0)] public ulong Ptr;
    [FieldOffset(8)] public uint Size;
    [FieldOffset(12)] public uint Type;
}
