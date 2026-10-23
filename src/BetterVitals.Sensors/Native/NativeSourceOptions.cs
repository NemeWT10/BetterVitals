namespace BetterVitals.Sensors.Native;

/// <summary>Which gaps <see cref="NativeSource"/> may fill (all on by default; tests and the probe switch areas off).</summary>
public sealed record NativeSourceOptions
{
    /// <summary>Drive temperatures from <c>StorageDeviceTemperatureProperty</c> (no administrator rights).</summary>
    public bool Drives { get; init; } = true;

    /// <summary>RAM speed / type from SMBIOS (and the <see cref="NativeSource.MemoryModules"/> list).</summary>
    public bool Memory { get; init; } = true;

    /// <summary>"Memory Used" / "Memory Available" / "Memory" load when upstream has no memory sensors at all.</summary>
    public bool MemoryUsageFallback { get; init; } = true;

    /// <summary>GPU temperature / fan / clocks (D3DKMT) and load / VRAM (PDH) where upstream has none.</summary>
    public bool Gpu { get; init; } = true;

    /// <summary>CPU load per core and total — only when upstream has no CPU at all.</summary>
    public bool Cpu { get; init; } = true;

    /// <summary>Network throughput — only when upstream has no network adapter at all.</summary>
    public bool Network { get; init; } = true;

    /// <summary>Decides whether a GPU may be touched; default asks the PnP manager (<see cref="CmDevicePowerGate"/>).</summary>
    public IDevicePowerGate? PowerGate { get; init; }
}
