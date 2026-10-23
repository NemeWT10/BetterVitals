using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace BetterVitals.Platform;

public enum PawnIoInstallOutcome
{
    Installed,
    /// <summary>Installed; Windows needs a restart before the driver works.</summary>
    RestartRequired,
    Failed,
    /// <summary>The bundled installer did not match its pinned hash (never run).</summary>
    Corrupted,
}

/// <summary>
/// The PawnIO kernel driver used by LibreHardwareMonitor for CPU / motherboard / DIMM sensors and the RTX 50 hot spot.
/// BetterVitals carries the official, unmodified, signed installer (redistribution allowed by its author) so the app
/// stays offline. Installing needs administrator rights and the user's explicit opt-in; it is never done silently on
/// our own, never in tests.
/// </summary>
public static class PawnIo
{
    /// <summary>Version of the bundled installer.</summary>
    public static Version BundledVersion { get; } = new(2, 2, 0);

    /// <summary>FACEIT blocked builds signed with the old certificate; 2.1.0 is the first re-signed release.</summary>
    public static Version MinimumGoodVersion { get; } = new(2, 1, 0);

    /// <summary>SHA-256 of the bundled PawnIO_setup.exe 2.2.0 (matches winget namazso.PawnIO 2.2.0).</summary>
    internal const string BundledSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>Installed driver version (the same registry value LibreHardwareMonitor checks), or null.</summary>
    public static Version? InstalledVersion
    {
        get
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(UninstallKey);
                return key?.GetValue("DisplayVersion") is string s && Version.TryParse(s.Trim(), out var v) ? v : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool IsInstalled => InstalledVersion is not null;

    /// <summary>Installed, but older than the re-signed 2.1 (offer the update).</summary>
    public static bool IsOutdated => InstalledVersion is { } v && v < MinimumGoodVersion;

    /// <summary>
    /// Runs the bundled installer silently. Elevated only. <paramref name="workDir"/> must be admin-only (the install
    /// folder): the file is verified right before it runs, so nothing can swap it in between.
    /// </summary>
    public static PawnIoInstallOutcome Install(string workDir)
    {
        if (!Elevation.IsElevated) return PawnIoInstallOutcome.Failed;
        var path = Path.Combine(workDir, "PawnIO_setup.exe");
        try
        {
            using (var res = typeof(PawnIo).Assembly.GetManifestResourceStream("BetterVitals.PawnIO_setup.exe"))
            {
                if (res is null) return PawnIoInstallOutcome.Corrupted;
                Directory.CreateDirectory(workDir);
                using var file = File.Create(path);
                res.CopyTo(file);
            }
            using (var check = File.OpenRead(path))
            {
                if (!Convert.ToHexString(SHA256.HashData(check)).Equals(BundledSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Core.Log.Error("pawnio: bundled installer hash mismatch — not run");
                    return PawnIoInstallOutcome.Corrupted;
                }
            }
            using var p = Process.Start(new ProcessStartInfo(path, "-install -silent") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir });
            if (p is null) return PawnIoInstallOutcome.Failed;
            if (!p.WaitForExit(TimeSpan.FromMinutes(3))) return PawnIoInstallOutcome.Failed;
            Core.Log.Info($"pawnio: installer exit code {p.ExitCode}, installed {InstalledVersion}");
            return p.ExitCode switch
            {
                0 => PawnIoInstallOutcome.Installed,
                3010 => PawnIoInstallOutcome.RestartRequired,
                _ => IsInstalled ? PawnIoInstallOutcome.Installed : PawnIoInstallOutcome.Failed,
            };
        }
        catch (Exception ex)
        {
            Core.Log.Error("pawnio: install failed", ex);
            return PawnIoInstallOutcome.Failed;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>Hash of the embedded installer (tests: it must match <see cref="BundledSha256"/>).</summary>
    internal static string EmbeddedSha256()
    {
        using var res = typeof(PawnIo).Assembly.GetManifestResourceStream("BetterVitals.PawnIO_setup.exe");
        return res is null ? "" : Convert.ToHexString(SHA256.HashData(res));
    }
}
