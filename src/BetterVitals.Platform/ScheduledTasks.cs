using System.Diagnostics;
using System.Security;
using System.Text;

namespace BetterVitals.Platform;

/// <summary>
/// Two Task Scheduler entries per user, in the "\BetterVitals" folder, both "run with highest privileges":
/// <list type="bullet">
/// <item><b>Launcher</b> — no trigger; an unelevated start (Start menu, second launch) runs it, so the app gets its
/// administrator token without a UAC prompt after the one-time install.</item>
/// <item><b>Autostart</b> — logon trigger (15 s delay so it does not slow the boot), enabled / disabled by the setting.</item>
/// </list>
/// Created / changed only from an elevated process; the XML is written next to the installed exe (admin-only folder),
/// never to a user-writable temp folder that another program could swap before schtasks reads it.
/// </summary>
public static class ScheduledTasks
{
    public const string Folder = "BetterVitals";

    public static string LauncherName(string userSid) => $@"{Folder}\Launcher {userSid}";

    public static string AutostartName(string userSid) => $@"{Folder}\Autostart {userSid}";

    /// <summary>Argument the launcher task passes (the app knows it was started elevated on purpose).</summary>
    public const string FromTaskArg = "--from-task";

    /// <summary>Argument the autostart task passes (start straight into the tray).</summary>
    public const string AutostartArg = "--autostart";

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static bool Exists(string name) => Run(["/Query", "/TN", name], out _) == 0;

    /// <summary>Starts a task now (works unelevated for a task this user may run).</summary>
    public static bool Start(string name) => Run(["/Run", "/TN", name], out _) == 0;

    public static bool Delete(string name) => Run(["/Delete", "/TN", name, "/F"], out _) == 0;

    public static bool SetEnabled(string name, bool enabled) => Run(["/Change", "/TN", name, enabled ? "/ENABLE" : "/DISABLE"], out _) == 0;

    /// <summary>Creates or replaces a task from XML (elevated only). <paramref name="xmlDir"/> must be admin-only.</summary>
    public static bool CreateOrReplace(string name, string xml, string xmlDir)
    {
        var path = Path.Combine(xmlDir, $"task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, xml, Encoding.Unicode); // schtasks wants UTF-16 with BOM
            var code = Run(["/Create", "/TN", name, "/XML", path, "/F"], out var output);
            if (code != 0) Core.Log.Warn($"tasks: create '{name}' failed ({code}): {output.Trim()}");
            return code == 0;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>Deletes every BetterVitals task of every user (uninstall).</summary>
    public static void DeleteAll()
    {
        if (Run(["/Query", "/FO", "CSV", "/NH"], out var csv) != 0) return;
        var prefix = $@"\{Folder}\";
        foreach (var line in csv.Split('\n'))
        {
            var name = line.Split(',')[0].Trim().Trim('"');
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Delete(name);
        }
    }

    /// <summary>Task XML for <paramref name="exe"/>; a logon trigger only when <paramref name="atLogon"/>.</summary>
    internal static string BuildXml(string exe, string arguments, string userSid, bool atLogon, bool enabled, string description)
    {
        string X(string s) => SecurityElement.Escape(s);
        var trigger = atLogon
            ? $"""
                <Triggers>
                  <LogonTrigger>
                    <Enabled>true</Enabled>
                    <UserId>{X(userSid)}</UserId>
                    <Delay>PT15S</Delay>
                  </LogonTrigger>
                </Triggers>
              """
            : "  <Triggers />";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>BetterVitals</Author>
                <Description>{X(description)}</Description>
              </RegistrationInfo>
            {trigger}
              <Principals>
                <Principal id="Author">
                  <UserId>{X(userSid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>{(enabled ? "true" : "false")}</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{X(exe)}</Command>
                  <Arguments>{X(arguments)}</Arguments>
                  <WorkingDirectory>{X(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int Run(string[] args, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo(Schtasks)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return -1;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(); } catch { /* gone */ }
                return -1;
            }
            output = stdout.Result + stderr.Result;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Core.Log.Warn("tasks: schtasks failed: " + ex.Message);
            return -1;
        }
    }
}
