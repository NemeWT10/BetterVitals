using BetterVitals.Core;

namespace BetterVitals.History;

/// <summary>Test / tuning hooks for <see cref="HistoryService"/>. Defaults are what the app uses.</summary>
internal sealed class HistoryOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Free bytes on the volume holding the folder (null result = unknown, keep writing).</summary>
    public Func<string, long?> FreeSpace { get; init; } = DefaultFreeSpace;

    /// <summary>Below this much free space nothing is written (the live ring keeps working).</summary>
    public long MinFreeBytes { get; init; } = 256L << 20;

    /// <summary>Run the once-a-minute flush timer (tests drive <see cref="HistoryService.Flush"/> by hand).</summary>
    public bool AutoFlush { get; init; } = true;

    /// <summary>Delay after each minute boundary before the timer flush (lets the engine seal the minute).</summary>
    public TimeSpan FlushOffset { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Run recovery / retention on a worker thread after enabling (tests do it inline).</summary>
    public bool BackgroundStart { get; init; } = true;

    public Action<string> Warn { get; init; } = Log.Warn;

    private static long? DefaultFreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
