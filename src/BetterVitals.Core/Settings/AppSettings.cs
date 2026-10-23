using BetterVitals.Core.Overlay;

namespace BetterVitals.Core.Settings;

/// <summary>
/// Everything the user chooses, persisted as JSON in %APPDATA%\BetterVitals\settings.json. Plain mutable classes:
/// change them only through <see cref="SettingsStore.Mutate"/> so the change is saved and announced.
/// New fields need a sensible default (old files simply lack them); renames / meaning changes need a migration
/// in <see cref="SettingsStore"/> and a bump of <see cref="CurrentSchema"/>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 2: the overlay's flat item list became rows and boxes; 3: rows and boxes became cells of a grid in at most two
    /// windows (see <see cref="OverlayLayout.Normalize"/>).
    /// </summary>
    public const int CurrentSchema = 3;

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>The first-run guide finished (mode, history question, autostart).</summary>
    public bool FirstRunDone { get; set; }

    public GeneralSettings General { get; set; } = new();

    public RecordingSettings Recording { get; set; } = new();

    public OverlayLayout Overlay { get; set; } = new();

    public FlyoutSettings Flyout { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    public DriverSettings Driver { get; set; } = new();
}

public enum TemperatureUnit : byte
{
    Celsius,
    Fahrenheit,
}

public sealed class GeneralSettings
{
    /// <summary>"system", "pl", "en".</summary>
    public string Language { get; set; } = "system";

    /// <summary>"system", "aurora", "graphite", "daylight", "neon", "sunset".</summary>
    public string Theme { get; set; } = "system";

    public bool Animations { get; set; } = true;

    public bool Mica { get; set; } = true;

    public bool Autostart { get; set; } = true;

    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

    /// <summary>Hover bubbles that explain sensor names.</summary>
    public bool Explanations { get; set; } = true;
}

/// <summary>What goes to disk for the charts. Off = live only, nothing written (except settings).</summary>
public sealed class RecordingSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between stored samples: 1, 2 (default, user decision), 5, 10.</summary>
    public int IntervalSeconds { get; set; } = 2;

    /// <summary>Detailed samples kept this many days (default 7, user decision).</summary>
    public int RawRetentionDays { get; set; } = 7;

    /// <summary>1-minute min/avg/max kept this many days.</summary>
    public int MinuteRetentionDays { get; set; } = 365;

    /// <summary>1-hour min/avg/max kept this many days; 0 = forever.</summary>
    public int HourRetentionDays { get; set; }

    /// <summary>Sensor ids being recorded. Filled with the "Key" preset on first run.</summary>
    public HashSet<string> SensorIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The "Key" preset was applied (also adds key sensors of hardware that shows up later, e.g. a new GPU).</summary>
    public bool KeyPresetApplied { get; set; }

    /// <summary>Key-preset sensors the user switched off on purpose: the preset never adds them back.</summary>
    public HashSet<string> RemovedSensorIds { get; set; } = new(StringComparer.Ordinal);
}

public sealed class FlyoutSettings
{
    /// <summary>Sensor ids pinned in the tray flyout (empty = automatic key values).</summary>
    public List<string> PinnedSensorIds { get; set; } = [];
}

public enum UpdateMode : byte
{
    Automatic,
    Ask,
    Off,
}

public sealed class UpdateSettings
{
    public UpdateMode Mode { get; set; } = UpdateMode.Ask;

    public DateTime? LastCheckUtc { get; set; }

    /// <summary>A version the user chose to skip.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>ETag of the last GitHub answer (an unchanged answer then costs ~200 bytes).</summary>
    public string? ETag { get; set; }
}

public enum ReadMode : byte
{
    /// <summary>Administrator + PawnIO: everything (CPU, motherboard, DIMM, RTX 50 hot spot, FPS).</summary>
    Full,

    /// <summary>No administrator rights, no driver: GPU basics, loads, memory, drives, network.</summary>
    Basic,
}

public sealed class DriverSettings
{
    public ReadMode Mode { get; set; } = ReadMode.Full;

    /// <summary>Pause driver-backed sensors while a known kernel anti-cheat runs (off by default, user decision).</summary>
    public bool PauseForAntiCheat { get; set; }

    /// <summary>The PawnIO install was offered (do not nag again).</summary>
    public bool PawnIoOffered { get; set; }
}
