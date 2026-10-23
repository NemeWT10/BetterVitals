using System.Runtime.CompilerServices;
using BetterVitals.Services;

namespace BetterVitals.UI.Panel;

/// <summary>The Settings sections after General (10) and before About (1000).</summary>
internal static class SettingsRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (!PlatformCommands.IsHelperProcess) RegisterNow();
    }

    // never even compiled in a helper process (--install…): it must not load WPF
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterNow()
    {
        SettingsSections.Register(new SettingsSection(SettingsPageParameter.Recording, "settings.recording", 20, ctx => new RecordingSection(ctx)));
        SettingsSections.Register(new SettingsSection(SettingsPageParameter.Overlay, "settings.overlay", 30, ctx => new OverlayBehaviorSection(ctx)));
        SettingsSections.Register(new SettingsSection(SettingsPageParameter.Startup, "settings.startup", 40, ctx => new StartupSection(ctx)));
        SettingsSections.Register(new SettingsSection(SettingsPageParameter.Updates, "settings.updates", 50, ctx => new UpdatesSection(ctx)));
        SettingsSections.Register(new SettingsSection(SettingsPageParameter.Permissions, "settings.permissions", 60, ctx => new PermissionsSection(ctx)));
    }
}
