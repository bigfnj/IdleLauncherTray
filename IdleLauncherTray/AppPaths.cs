using System;
using System.IO;

namespace IdleLauncherTray;

internal static class AppPaths
{
    public const string AppName = "IdleLauncherTray";

    /// <summary>
    /// Opt-in environment override for the per-user data directory (config.json, the log
    /// file and the stored custom tray icon). When unset or blank the app behaves exactly
    /// as it always has and uses %APPDATA%\IdleLauncherTray.
    /// <para>
    /// It exists for two reasons:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>Testability.</b> Without it there is no seam at all, so any
    /// in-process test of <see cref="ConfigManager"/>, <see cref="Logger"/> or
    /// <see cref="DeletionHelper"/> would read and write the real user's config and log —
    /// and <see cref="DeletionHelper"/> exists to <i>delete</i> that directory.</description></item>
    /// <item><description><b>Genuine portable operation.</b> The app already runs from
    /// wherever it is dropped; this lets its state live next to the executable (on a USB
    /// stick, say) instead of in the roaming profile.</description></item>
    /// </list>
    /// </summary>
    public const string DataDirOverrideVariable = "IDLELAUNCHERTRAY_DATA_DIR";

    /// <summary>
    /// Resolved on every access rather than cached in a <c>static readonly</c> field.
    /// A cached field is captured at type-initialisation time, which is whenever the CLR
    /// first happens to touch this type — so a test that sets
    /// <see cref="DataDirOverrideVariable"/> a moment too late would silently write to the
    /// real user's %APPDATA%. Reading it live removes the ordering hazard entirely. The
    /// cost is an environment lookup plus a Path.Combine on a path that is touched a
    /// handful of times per minute, which is not a hot path.
    /// </summary>
    public static string BaseDir => ResolveBaseDir();

    public static string ConfigPath => Path.Combine(BaseDir, "config.json");

    // Custom tray icon storage (if user chooses an .ico)
    public static string TrayIconFile => Path.Combine(BaseDir, "tray.ico");

    // The path to the currently-running executable (run from wherever the user launched it).
    public static string CurrentExePath =>
        !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Environment.ProcessPath!
            : Path.Combine(AppContext.BaseDirectory, $"{AppName}.exe");

    // Legacy (pre-change) location used when the app self-copied into %APPDATA%.
    // Kept only to migrate old startup registry entries.
    public static string LegacyInstalledExePath => Path.Combine(BaseDir, $"{AppName}.exe");

    // Startup registry location (HKCU)
    public const string RunRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunRegValueName = AppName;

    private static string DefaultBaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    private static string ResolveBaseDir()
    {
        var configured = Environment.GetEnvironmentVariable(DataDirOverrideVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultBaseDir;
        }

        try
        {
            // Rooting the override matters for more than tidiness: DeletionHelper compares
            // a caller-supplied folder against this value to decide whether a recursive
            // delete is allowed, and comparing a full path against a relative one would
            // never match.
            return Path.GetFullPath(configured.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // A malformed override must not take the app down; fall back to the default.
            return DefaultBaseDir;
        }
    }
}
