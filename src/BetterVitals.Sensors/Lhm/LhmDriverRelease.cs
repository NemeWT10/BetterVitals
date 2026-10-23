using System.Reflection;
using BetterVitals.Core;
using LibreHardwareMonitor.Hardware;
using RAMSPDToolkit.I2CSMBus.Interop.PawnIO;
using RAMSPDToolkit.Windows.Driver;
using RAMSPDToolkit.Windows.Driver.Interfaces;

namespace BetterVitals.Sensors.Lhm;

/// <summary>
/// Releases every PawnIO handle LibreHardwareMonitor holds outside the CPU / mainboard / DIMM groups (those are closed by
/// disabling their groups): RAMSPDToolkit's SMBus modules (a static driver that survives <c>MemoryGroup.Close</c>) and
/// per-GPU modules (NVIDIA hot spot MMIO, Intel iGPU MSR) — found by reflection, because LHM has no API for it.
/// </summary>
internal static class LhmDriverRelease
{
    private const string PawnIoNamespace = "LibreHardwareMonitor.PawnIo";

    /// <summary>Closes RAMSPDToolkit's SMBus PawnIO modules and forgets its driver (the next MemoryGroup installs a fresh one).</summary>
    public static void UnloadSpdDriver()
    {
        try
        {
            DriverManager.UnloadDriver();
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: RAMSPDToolkit unload failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Installs a driver that refuses to load, so a MemoryGroup created while paused still gives "Total / Virtual Memory"
    /// but never opens SMBus modules.
    /// </summary>
    public static void BlockSpdDriver()
    {
        UnloadSpdDriver();
        DriverManager.Driver = new NoSpdDriver();
    }

    /// <summary>Closes PawnIO module fields on the given hardware (and its sub-hardware). Returns the number closed.</summary>
    public static int ClosePawnIoModules(IEnumerable<IHardware> hardware)
    {
        var closed = 0;
        foreach (var hw in hardware) closed += CloseOn(hw);
        return closed;
    }

    private static int CloseOn(IHardware hw)
    {
        var closed = 0;
        try
        {
            for (var t = hw.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.Namespace != PawnIoNamespace || f.GetValue(hw) is not { } module) continue;
                    var close = module.GetType().GetMethod("Close", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
                    if (close is null) continue;
                    close.Invoke(module, null);
                    closed++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"lhm: closing PawnIO modules of '{hw.Name}' failed: {ex.Message}");
        }
        foreach (var sub in hw.SubHardware) closed += CloseOn(sub);
        return closed;
    }

    /// <summary>RAMSPDToolkit driver stand-in that never loads (no PawnIO, no SMBus access).</summary>
    private sealed class NoSpdDriver : IPawnIODriver
    {
        public bool IsOpen => true;

        public bool Load() => false;

        public void Unload()
        {
        }

        public IPawnIOModule LoadModule(PawnIOSMBusIdentifier pawnIOSMBusIdentifier) => null!;
    }
}
