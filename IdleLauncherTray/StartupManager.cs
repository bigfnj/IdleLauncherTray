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
    /// <para>
    /// Which is why GetStartupEnabled no longer decides enabled-versus-disabled from this
    /// comparison at all. It is used for the two things it can still answer honestly: spotting
    /// the legacy %APPDATA% path that is worth migrating, and reporting a mismatch to the log.
    /// </para>
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

    /// <summary>
    /// Reports whether Windows will start this app at logon.
    /// </summary>
    /// <remarks>
    /// The question this answers is "will the app start", and the only fact Windows acts on is
    /// that a value exists under our value name. So that is what is reported. The canonical
    /// comparison below is only our guess at whether a value is <i>ours</i>, and
    /// <see cref="TryGetCanonicalExePath"/> cannot see through 8.3 short names, junctions or
    /// symlinks — a Run entry written through any of those failed the comparison, and the tray
    /// then showed "Run at startup" unchecked while the app really did launch at every logon. A
    /// user who wanted it off saw it already off and did nothing. A wrong guess about whose value
    /// it is must not turn into a wrong statement about whether the app starts.
    /// </remarks>
    public static bool GetStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppPaths.RunRegSubKey, writable: false);

            // `as string` is deliberate rather than lax. A Run value that is not REG_SZ or
            // REG_EXPAND_SZ is not something Windows launches, so reading it as "no registration"
            // is the true answer and not a gap in the check.
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

            // A value exists under our value name and canonicalises to neither path we know. Still
            // ENABLED: Windows runs whatever is there, so that is what the tray must show.
            //
            // The mismatch remains worth knowing, because there are two very different stories
            // behind it -- a path we simply cannot canonicalise (a short name, a junction, a
            // symlink), or a different copy of this app that has claimed the same value name. Both
            // paths go in the log, every time, so the one that matters is reconstructible.
            Logger.Warn(
                $"Startup registry value exists but does not canonicalise to the current executable. Reporting startup as ENABLED anyway, because Windows will run the stored command at logon regardless. Stored='{val}' Current='{CurrentStartupCommand()}'.");
            return true;
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
