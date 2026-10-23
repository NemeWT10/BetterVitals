using Microsoft.Win32;

namespace BetterVitals.Platform;

/// <summary>
/// Windows security features that matter for the PawnIO driver and some anti-cheats (Vanguard may require Memory
/// Integrity): read from the registry only, null when unknown.
/// </summary>
public static class SecurityState
{
    /// <summary>Memory Integrity (HVCI) enabled.</summary>
    public static bool? MemoryIntegrity => ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");

    /// <summary>UEFI Secure Boot enabled.</summary>
    public static bool? SecureBoot => ReadDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");

    /// <summary>Virtualization-based security configured.</summary>
    public static bool? Vbs => ReadDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");

    private static bool? ReadDword(string path, string name)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(path);
            return key?.GetValue(name) is int v ? v != 0 : null;
        }
        catch
        {
            return null;
        }
    }
}
