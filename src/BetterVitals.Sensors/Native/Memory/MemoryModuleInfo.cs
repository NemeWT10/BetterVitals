namespace BetterVitals.Sensors.Native.Memory;

/// <summary>One populated memory slot as the firmware (SMBIOS type 17) describes it. Read once at discovery; never changes.</summary>
/// <param name="DeviceLocator">Slot label on the board ("DIMM_A2", "Controller0-ChannelA-DIMM0").</param>
/// <param name="BankLocator">Bank / channel label ("BANK 0", "P0 CHANNEL A"); may be empty.</param>
/// <param name="Type">"DDR4", "DDR5", "LPDDR5"… or "Unknown".</param>
/// <param name="FormFactor">"DIMM", "SODIMM", "Row of chips"… or "Unknown".</param>
/// <param name="CapacityMb">Module size in MB.</param>
/// <param name="SpeedMTs">Maximum rated speed in MT/s (0 = unknown).</param>
/// <param name="ConfiguredSpeedMTs">Speed the module runs at now in MT/s (0 = unknown) — XMP / EXPO included.</param>
/// <param name="Manufacturer">Manufacturer string as given by the module ("Kingston"); empty when the firmware has none.</param>
/// <param name="PartNumber">Part number ("KF560C36-16"); empty when unknown.</param>
public sealed record MemoryModuleInfo(
    string DeviceLocator,
    string BankLocator,
    string Type,
    string FormFactor,
    int CapacityMb,
    int SpeedMTs,
    int ConfiguredSpeedMTs,
    string Manufacturer,
    string PartNumber)
{
    /// <summary>The speed to show: what the module runs at, else its rated speed.</summary>
    public int EffectiveSpeedMTs => ConfiguredSpeedMTs > 0 ? ConfiguredSpeedMTs : SpeedMTs;
}
