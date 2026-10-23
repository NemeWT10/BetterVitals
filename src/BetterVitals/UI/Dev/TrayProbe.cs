using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using BetterVitals.Core;
using BetterVitals.UI.Panel;
using BetterVitals.UI.Tray;

namespace BetterVitals.UI.Dev;

/// <summary>
/// <c>--tray-probe</c> (test mode, run it on the hidden desktop): measures the private working set at start, with the
/// panel open (every page visited) and the flyout open, and again after everything closed and the tray rest settled.
/// Writes tray-probe.txt next to the logs and to stdout, then exits.
/// </summary>
internal static class TrayProbe
{
    public static void Start() => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Run);

    private static async void Run()
    {
        var lines = new List<string>();
        void Mark(string what)
        {
            var (priv, ws, bytes) = Native.MemoryUse();
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{what,-26} private WS {priv / 1048576.0,7:0.0} MB | WS {ws / 1048576.0,7:0.0} MB | private bytes {bytes / 1048576.0,7:0.0} MB | managed {GC.GetTotalMemory(false) / 1048576.0,6:0.0} MB | GC committed {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0,6:0.0} MB");
            lines.Add(line);
            Log.Info("probe: " + line);
        }
        try
        {
            Mark("start");
            TrayRest.Settle(); // what a normal start does
            await Task.Delay(6000); // the start-up trims ran (idle + 4 s)
            Mark("start, trimmed");

            AppShell.I.OpenPanel(PageIds.Overview);
            await Task.Delay(400);
            foreach (var page in PageRegistry.All)
            {
                AppShell.I.OpenPanel(page.Id);
                await Task.Delay(300);
            }
            Mark("panel open, all pages");
            AppShell.I.ShowFlyout();
            await Task.Delay(1500);
            Mark("panel + flyout");

            AppShell.I.CloseAll();
            await Task.Delay(30000); // idle trim, +4 s with GC, +25 s working set only
            Mark($"tray rest ({TrayRest.Trims} trims)");
            await Task.Delay(60000);
            Mark("tray rest +60 s (hub ticking)");
        }
        catch (Exception ex)
        {
            lines.Add("probe failed: " + ex);
            Log.Error("probe failed", ex);
        }
        try
        {
            File.WriteAllLines(Path.Combine(AppEnv.LogDir, "tray-probe.txt"), lines);
            foreach (var l in lines) Console.WriteLine(l);
        }
        catch
        {
            // the log has it
        }
        AppShell.I.Quit();
    }
}
