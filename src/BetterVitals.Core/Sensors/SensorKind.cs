namespace BetterVitals.Core.Sensors;

/// <summary>
/// What a sensor measures. The first block mirrors LibreHardwareMonitor's <c>SensorType</c> 1:1 by name
/// (the LHM source maps with <c>Enum.Parse</c>), followed by BetterVitals' own kinds.
/// Stored in settings and history by NAME, never by number — append new members at the end only.
/// </summary>
public enum SensorKind : byte
{
    Voltage,
    Current,
    Power,
    Clock,
    Temperature,
    Load,
    Frequency,
    Fan,
    Flow,
    Control,
    Level,
    Factor,
    Data,
    SmallData,
    Throughput,
    TimeSpan,
    Timing,
    Energy,
    Noise,
    Conductivity,
    Humidity,

    // ── BetterVitals ──
    Fps,
    FrameTime,
}

/// <summary>Component family a hardware node belongs to (drives icons, default colors, glossary context).</summary>
public enum HardwareKind : byte
{
    Cpu,
    Gpu,
    /// <summary>Mainboard and everything on it: SuperIO, embedded controller.</summary>
    Motherboard,
    Memory,
    Storage,
    Network,
    Battery,
    Psu,
    /// <summary>AIO coolers, pumps.</summary>
    Cooler,
    /// <summary>Fan / RGB hubs and other USB controllers.</summary>
    Controller,
    /// <summary>The foreground game / app (FPS, frame times).</summary>
    Game,
    Other,
}
