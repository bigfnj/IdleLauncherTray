using System;
using System.IO;

namespace IdleLauncherTray;

internal static class AppPaths
{
    public const string AppName = "IdleLauncherTray";

    public static readonly string BaseDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");

    // Custom tray icon storage (if user chooses an .ico)
    public static readonly string TrayIconFile = Path.Combine(BaseDir, "tray.ico");

    // The path to the currently-running executable (run from wherever the user launched it).
    public static string CurrentExePath =>
        !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Environment.ProcessPath!
            : Path.Combine(AppContext.BaseDirectory, $"{AppName}.exe");

    // Legacy (pre-change) location used when the app self-copied into %APPDATA%.
    // Kept only to migrate old startup registry entries.
    public static readonly string LegacyInstalledExePath = Path.Combine(BaseDir, $"{AppName}.exe");

    // Startup registry location (HKCU)
    public const string RunRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunRegValueName = AppName;
}
