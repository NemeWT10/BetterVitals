using BetterVitals.Core;

namespace BetterVitals.Sensors.Power;

/// <summary>
/// Power gate for PnP devices (GPUs): reads the PnP manager's cached <c>DEVPKEY_Device_PowerData</c>
/// (<c>CM_POWER_DATA.PD_MostRecentPowerState</c>) — the GPU itself is never touched. D1–D3 = asleep.
/// </summary>
internal sealed unsafe class PnpPowerGate : PowerGate
{
    private readonly uint _devInst;

    private PnpPowerGate(uint devInst, string instanceId)
    {
        _devInst = devInst;
        InstanceId = instanceId;
    }

    /// <summary>PnP device instance id, e.g. "PCI\VEN_10DE&amp;DEV_2C02&amp;SUBSYS_…\4&amp;…&amp;0&amp;0008".</summary>
    public string InstanceId { get; }

    public override string Target => "PnP " + InstanceId;

    /// <summary>Binds to a device instance id; null when the devnode cannot be located.</summary>
    public static PnpPowerGate? TryCreate(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        try
        {
            uint devInst;
            uint cr;
            fixed (char* p = instanceId)
                cr = PowerNative.CM_Locate_DevNode(out devInst, p, PowerNative.CM_LOCATE_DEVNODE_NORMAL);
            return cr == PowerNative.CR_SUCCESS ? new PnpPowerGate(devInst, instanceId) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: CM_Locate_DevNode failed for {instanceId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Current D-state of the devnode (1 = D0 … 4 = D3), 0 when unknown.</summary>
    public uint ReadDeviceState(out uint configRet)
    {
        // CM_POWER_DATA is 56 bytes; PD_MostRecentPowerState is the second ULONG.
        var buffer = stackalloc byte[64];
        uint size = 64;
        var key = PowerNative.DevPKeyDevicePowerData;
        configRet = PowerNative.CM_Get_DevNode_Property(_devInst, &key, out _, buffer, ref size, 0);
        if (configRet != PowerNative.CR_SUCCESS || size < 8) return 0;
        return *(uint*)(buffer + 4);
    }

    protected override PowerQuery Query()
    {
        var state = ReadDeviceState(out var cr);
        if (cr == PowerNative.CR_NO_SUCH_DEVNODE) return PowerQuery.Gone;
        return PowerGateState.FromDeviceState(state);
    }
}

/// <summary>Pure helpers that turn LHM / D3D / IGCL device ids into PnP device instance ids.</summary>
internal static class PnpIds
{
    /// <summary>
    /// "\\?\PCI#VEN_1002&amp;DEV_731F#6&amp;e539058&amp;0&amp;00000019#{1ca05180-…}" → "PCI\VEN_1002&amp;DEV_731F\6&amp;e539058&amp;0&amp;00000019";
    /// an id that is already an instance id ("PCI\…") is returned as is; anything else → null.
    /// </summary>
    public static string? NormalizeInstanceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var s = id.Trim();
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\\.\", StringComparison.Ordinal)) s = s[4..];
        if (s.EndsWith('}'))
        {
            var brace = s.LastIndexOf('{');
            if (brace > 0) s = s[..(brace - 1)];
        }
        s = s.Replace('#', '\\');
        return s.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) && s.Count(c => c == '\\') >= 2 ? s : null;
    }

    /// <summary>Extracts "VEN_xxxx" / "DEV_xxxx" (4 hex digits each) from any id; "0x56A0" alone gives only the device.</summary>
    public static bool TryParseVenDev(string? id, out string? vendor, out string? device)
    {
        vendor = null;
        device = null;
        if (string.IsNullOrEmpty(id)) return false;
        vendor = Hex4After(id, "VEN_");
        device = Hex4After(id, "DEV_");
        if (device is null && id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && id.Length >= 6 && IsHex(id.AsSpan(2, 4)))
            device = id.Substring(2, 4).ToUpperInvariant();
        return device is not null;
    }

    /// <summary>The single id in <paramref name="ids"/> matching vendor (when given) and device; null when none or ambiguous.</summary>
    public static string? FindUnique(IEnumerable<string> ids, string? vendor, string device)
    {
        string? found = null;
        foreach (var id in ids)
        {
            if (!string.Equals(Hex4After(id, "DEV_"), device, StringComparison.OrdinalIgnoreCase)) continue;
            if (vendor is not null && !string.Equals(Hex4After(id, "VEN_"), vendor, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) return null;
            found = id;
        }
        return found;
    }

    private static string? Hex4After(string s, string tag)
    {
        var i = s.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + tag.Length + 4 > s.Length) return null;
        var span = s.AsSpan(i + tag.Length, 4);
        return IsHex(span) ? span.ToString().ToUpperInvariant() : null;
    }

    private static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }
}
