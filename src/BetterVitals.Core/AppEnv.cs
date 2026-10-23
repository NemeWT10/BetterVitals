using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace BetterVitals.Core;

/// <summary>
/// Process-wide environment: test mode, data folders and the key that separates instances
/// (a test instance never talks to the user's running instance, never touches the user's data).
/// </summary>
public static class AppEnv
{
    public const string ProductName = "BetterVitals";

    public static bool TestMode { get; private set; }

    /// <summary>Settings (roaming, per user): %APPDATA%\BetterVitals.</summary>
    public static string RoamingDir { get; private set; } = "";

    /// <summary>History, logs, caches (local, per machine): %LOCALAPPDATA%\BetterVitals.</summary>
    public static string LocalDir { get; private set; } = "";

    /// <summary>Stable per data-folder key used for the single-instance mutex and pipe.</summary>
    public static string InstanceKey { get; private set; } = "";

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string HistoryDir => Path.Combine(LocalDir, "history");
    public static string LogDir => Path.Combine(LocalDir, "logs");
    public static string CacheDir => Path.Combine(LocalDir, "cache");

    /// <summary>"0.1.0+abc1234.20261023" — version, commit, build date.</summary>
    public static string Version { get; } =
        typeof(AppEnv).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>"0.1.0" — the SemVer part only.</summary>
    public static string ShortVersion => Version.Split('+')[0];

    public static void Init(bool testMode, string? homeOverride)
    {
        TestMode = testMode || Environment.GetEnvironmentVariable("BETTERVITALS_TEST") == "1";
        homeOverride ??= Environment.GetEnvironmentVariable("BETTERVITALS_HOME");

        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            var home = Path.GetFullPath(homeOverride);
            RoamingDir = Path.Combine(home, "roaming");
            LocalDir = Path.Combine(home, "local");
        }
        else
        {
            RoamingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName);
            LocalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);
        }

        Directory.CreateDirectory(RoamingDir);
        Directory.CreateDirectory(LocalDir);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(RoamingDir.ToUpperInvariant() + (TestMode ? "|test" : "")));
        InstanceKey = ProductName + "-" + Convert.ToHexString(hash, 0, 8);
    }
}
