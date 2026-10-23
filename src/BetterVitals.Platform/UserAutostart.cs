using Microsoft.Win32;

namespace BetterVitals.Platform;

/// <summary>
/// "Start with Windows" in basic mode (and for a copy that is not installed): one value under HKCU\…\Run that starts
/// the exe unelevated — no administrator rights, no UAC. Full mode uses the elevated autostart task instead; the two
/// are never active together.
/// </summary>
public static class UserAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BetterVitals";

    public static string Command(string exe) => $"\"{exe}\" {ScheduledTasks.AutostartArg}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the Run value. Returns false when the registry refused.</summary>
    public static bool Set(bool enabled, string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, Command(exe));
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Warn("autostart (user): " + ex.Message);
            return false;
        }
    }
}
