namespace IdleLauncherTray;

internal sealed class AppConfig
{
    internal const int MinimumSystemIdleFailSafeWindowMs = 6000;

    public int IdleMinutes { get; set; } = 5;

    /// <summary>
    /// Max allowed total CPU usage (%) before we consider the machine "idle-ready".
    /// Valid values: 10, 20, 30, 40, 50. Default: 10.
    /// </summary>
    public int CpuThresholdPercent { get; set; } = 10;

    public string AppPath { get; set; } = string.Empty;

    // Optional arguments passed to the selected application/batch file.
    // (For .scr we still prepend "/s" to start full-screen.)
    public string AppArguments { get; set; } = string.Empty;

    public bool RunAtStartup { get; set; }

    public bool BlockInjectedWhileRunning { get; set; }

    // When enabled, Windows is locked after an idle-triggered tracked app exits.
    public bool LockPcOnAppClose { get; set; }

    // Count XInput gamepad activity as user activity
    public bool GamepadCountsAsActivity { get; set; } = true;

    // Fail-safe: when enabled, the effective window is clamped to a minimum that matches
    // the 5-second monitor cadence so recent system input is not missed between polls.
    public bool UseSystemIdleFailSafe { get; set; } = true;

    public int SystemIdleFailSafeWindowMs { get; set; } = MinimumSystemIdleFailSafeWindowMs;

    // Custom tray icon support
    public bool TrayIconEnabled { get; set; }

    public string TrayIconPath { get; set; } = string.Empty;

    // ISO-8601 UTC timestamp (round-trip) for last launch
    public string LastLaunchUtc { get; set; } = string.Empty;

    /// <summary>
    /// Normalizes a CPU threshold percent into the allowed set (10, 20, 30, 40, 50).
    /// Values are rounded to the nearest 10 and clamped into range.
    /// </summary>
    internal static int NormalizeCpuThresholdPercent(int percent)
    {
        // Round to nearest 10 (e.g., 15 -> 20, 14 -> 10)
        var rounded = ((percent + 5) / 10) * 10;

        if (rounded < 10) return 10;
        if (rounded > 50) return 50;
        return rounded;
    }
}
