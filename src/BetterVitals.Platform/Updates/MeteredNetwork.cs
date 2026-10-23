using System.Runtime.InteropServices;

namespace BetterVitals.Platform.Updates;

/// <summary>Is the current internet connection metered (mobile hotspot, data limit)? Update checks skip it.</summary>
public static class MeteredNetwork
{
    private const uint Unrestricted = 0x1;
    private const uint Fixed = 0x2;
    private const uint Variable = 0x4;
    private const uint OverDataLimit = 0x10000;
    private const uint Roaming = 0x40000;

    /// <summary>True when Windows says the connection costs money or is limited; false when unknown.</summary>
    public static bool IsMetered()
    {
        object? nlm = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return false;
            nlm = Activator.CreateInstance(type);
            if (nlm is not INetworkCostManager costs) return false;
            costs.GetCost(out var cost, IntPtr.Zero);
            if ((cost & (OverDataLimit | Roaming)) != 0) return true;
            return (cost & (Fixed | Variable)) != 0 && (cost & Unrestricted) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (nlm is not null && Marshal.IsComObject(nlm)) Marshal.FinalReleaseComObject(nlm);
        }
    }

    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint pCost, IntPtr pDestIPAddr);
    }
}
