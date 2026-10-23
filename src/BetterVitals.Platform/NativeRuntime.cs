using System.Reflection;
using System.Runtime.InteropServices;

namespace BetterVitals.Platform;

/// <summary>
/// WPF's native libraries. The exe is published single-file WITHOUT self-extracting them (that would unpack them to
/// the user's %TEMP% and an elevated BetterVitals would load DLLs any ordinary program can replace). Instead they ride
/// inside the exe as resources and are written:
/// <list type="bullet">
/// <item>next to the installed exe in %ProgramFiles%\BetterVitals by the elevated installer (admin-only folder);</item>
/// <item>for an elevated process that is not the installed copy, to %ProgramFiles%\BetterVitals\runtime\&lt;version&gt;;</item>
/// <item>for an unelevated process (first run from Downloads, basic mode), to %LOCALAPPDATA%\BetterVitals\runtime\&lt;version&gt;.</item>
/// </list>
/// <see cref="Prepare"/> runs first thing in Main, before any WPF type is touched.
/// </summary>
public static class NativeRuntime
{
    /// <summary>Resource name prefix in the app assembly ("BetterVitals.Native.wpfgfx_cor3.dll").</summary>
    public const string ResourcePrefix = "BetterVitals.Native.";

    /// <summary>Directory the libraries were loaded from after <see cref="Prepare"/> (diagnostics).</summary>
    public static string? Directory { get; private set; }

    /// <summary>Makes WPF's native libraries loadable from a safe place. Never throws; logs and lets WPF fail loudly otherwise.</summary>
    public static void Prepare(Assembly source, string version)
    {
        try
        {
            var names = source.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();
            var exeDir = AppContext.BaseDirectory;
            // developer builds / installed copy: the files are already beside the exe
            if (names.Length == 0 || names.All(n => File.Exists(Path.Combine(exeDir, n[ResourcePrefix.Length..]))))
            {
                Directory = exeDir;
                return;
            }

            var dir = Elevation.IsElevated
                ? (Installer.IsRunningInstalled ? Installer.InstallDir : Path.Combine(Installer.InstallDir, "runtime", version))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Core.AppEnv.ProductName, "runtime", version);
            WriteAll(source, names, dir);
            AddSearchDirectory(dir);
            Directory = dir;
        }
        catch (Exception ex)
        {
            Core.Log.Error("native runtime: prepare failed", ex);
        }
    }

    /// <summary>Installer: writes the libraries next to the installed exe.</summary>
    public static void WriteBeside(Assembly source, string installDir)
    {
        var names = source.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();
        WriteAll(source, names, installDir);
    }

    internal static void WriteAll(Assembly source, string[] resourceNames, string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        foreach (var res in resourceNames)
        {
            var path = Path.Combine(dir, res[ResourcePrefix.Length..]);
            using var stream = source.GetManifestResourceStream(res);
            if (stream is null) continue;
            // already written by an earlier start of this version (same size = complete)
            if (File.Exists(path) && new FileInfo(path).Length == stream.Length) continue;
            var tmp = path + ".tmp";
            using (var file = File.Create(tmp)) stream.CopyTo(file);
            File.Move(tmp, path, overwrite: true);
        }
    }

    private static void AddSearchDirectory(string dir)
    {
        // both: AddDllDirectory serves LoadLibraryEx with LOAD_LIBRARY_SEARCH_USER_DIRS (DllImport with safe search
        // paths), SetDllDirectory the classic search order used by native-to-native loads (wpfgfx → D3DCompiler).
        AddDllDirectory(dir);
        SetDllDirectoryW(dir);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string pathName);
}
