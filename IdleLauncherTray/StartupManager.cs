using System;
using Microsoft.Win32;

namespace IdleLauncherTray;

internal static class StartupManager
{
    private static string Quote(string path) => $"\"{path}\"";

    private static string CurrentStartupCommand() => Quote(AppPaths.CurrentExePath);

    private static string LegacyStartupCommand() => Quote(AppPaths.LegacyInstalledExePath);

    public static bool GetStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppPaths.RunRegSubKey, writable: false);
            var val = key?.GetValue(AppPaths.RunRegValueName) as string;

            if (string.IsNullOrWhiteSpace(val))
            {
                return false;
            }

            // Portable mode: startup points directly at the currently-running EXE.
            if (string.Equals(val, CurrentStartupCommand(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Legacy (pre-portable): startup pointed at the self-installed copy in %APPDATA%.
            // Treat this as "enabled", but migrate it to the current EXE location so it keeps working.
            if (string.Equals(val, LegacyStartupCommand(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var w = Registry.CurrentUser.CreateSubKey(AppPaths.RunRegSubKey, writable: true);
                    w?.SetValue(AppPaths.RunRegValueName, CurrentStartupCommand(), RegistryValueKind.String);
                    Logger.Info(
                        $"Migrated legacy startup command to portable mode. Old='{LegacyStartupCommand()}' New='{CurrentStartupCommand()}'.");
                }
                catch (Exception ex)
                {
                    Logger.Warn(
                        $"Startup registry still points at legacy AppData copy and migration failed. Value='{val}'. Error='{ex.Message}'.");
                }

                return true;
            }

            Logger.Warn(
                $"Startup registry value exists but does not match the current portable executable. Value='{val}'. Startup will be treated as disabled in the tray UI.");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to read startup registration. Error='{ex.Message}'.");
            return false;
        }
    }

    public static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AppPaths.RunRegSubKey, writable: true);
        if (key == null)
        {
            Logger.Warn("Failed to open or create the startup registry key.");
            throw new InvalidOperationException("Failed to open or create the startup registry key.");
        }

        if (enabled)
        {
            key.SetValue(AppPaths.RunRegValueName, CurrentStartupCommand(), RegistryValueKind.String);
            Logger.Info($"Startup enabled for portable executable. Command='{CurrentStartupCommand()}'.");
            return;
        }

        try
        {
            key.DeleteValue(AppPaths.RunRegValueName, throwOnMissingValue: false);
            Logger.Info("Startup disabled.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to remove startup registration. Error='{ex.Message}'.");
        }
    }
}
