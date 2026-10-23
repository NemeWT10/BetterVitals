using System.Net.NetworkInformation;

namespace BetterVitals.Platform;

/// <summary>The adapter that carries the internet traffic (default gateway, up, physical) — its name matches LHM's network hardware name.</summary>
public static class NetworkPrimary
{
    public static string? Find()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                            && !LooksVirtual(n.Description)
                            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)))
                .OrderByDescending(n => n.Speed)
                .Select(n => n.Name)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksVirtual(string description)
    {
        string[] marks = ["Hyper-V", "Virtual", "VMware", "VirtualBox", "TAP-", "WireGuard", "Tailscale", "ZeroTier", "Loopback", "Bluetooth", "Kernel Debug"];
        return marks.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
