using System;
using System.IO;
using Microsoft.Win32;

namespace IdleLauncherTray;

internal static class StartupManager
{
    private static string Quote(string path) => $"\"{path}\"";

    private static string CurrentStartupCommand() => Quote(AppPaths.CurrentExePath);

    private static string LegacyStartupCommand() => Quote(AppPaths.LegacyInstalledExePath);

    /// <summary>
    /// Extracts the executable path from a Run-key command line and canonicalises it.
    /// Returns null when the value has no usable path.
    /// </summary>
    /// <remarks>
    /// The enabled check used to be an exact OrdinalIgnoreCase match against the string we
    /// write. Any equivalent spelling of the same file -- a trailing separator, a relative
    /// segment, a different-cased drive letter -- then read back as "not ours", so the tray
    /// reported startup as off while a live Run entry kept launching the app.
    /// Path.GetFullPath does NOT resolve 8.3 short names, junctions or symlinks, so a value
    /// written through one of those still reads as a mismatch; that needs
    /// GetFinalPathNameByHandle and an open handle to a file that may no longer exist.
    /// </remarks>
    private static string? TryGetCanonicalExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var path = command.Trim();

        // We always write the path quoted, and that is correct -- it is what makes a path
        // containing spaces unambiguous. Strip the quotes (and anything after them, which
        // would be arguments) before canonicalising.
        if (path.Length > 0 && path[0] == '"')
        {
            var closingQuote = path.IndexOf('"', 1);
            path = closingQuote > 1
                ? path.Substring(1, closingQuote - 1)
                : path.Substring(1);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsSameExecutable(string? registryValue, string expectedCommand)
    {
        var actual = TryGetCanonicalExePath(registryValue);
        if (actual == null)
        {
            return false;
        }

        var expected = TryGetCanonicalExePath(expectedCommand);
        return expected != null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

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
            if (IsSameExecutable(val, CurrentStartupCommand()))
            {
                return true;
            }

            // Legacy (pre-portable): startup pointed at the self-installed copy in %APPDATA%.
            // Treat this as "enabled", but migrate it to the current EXE location so it keeps working.
            if (IsSameExecutable(val, LegacyStartupCommand()))
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
