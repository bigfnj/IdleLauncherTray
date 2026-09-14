using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace IdleLauncherTray;

internal static class ConfigManager
{
    // 30 days. Chosen to be far beyond any plausible idle threshold rather than to express a
    // policy, so it constrains nobody in practice while keeping IdleMinutes * 60 nowhere near
    // int overflow.
    internal const int MaximumIdleMinutes = 30 * 24 * 60;

    // 10 minutes, against a 6-second default. Same reasoning: generous enough that a real
    // setting never hits it.
    internal const int MaximumSystemIdleFailSafeWindowMs = 10 * 60 * 1000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };


    private static void NormalizeInPlace(AppConfig cfg)
    {
        // Keep values within sensible bounds even if the config was hand-edited
        // or comes from an older version.
        //
        // Both bounds matter, not just the lower one. TrayAppContext computes
        // RequiredIdleSeconds as `IdleMinutes * 60` into an int, unchecked: an IdleMinutes
        // above int.MaxValue/60 (~35.8 million) wraps NEGATIVE, and the readiness test is
        // `IdleSeconds >= RequiredIdleSeconds`, so a negative threshold is satisfied on every
        // tick. The app would fire its target every cooldown, forever, from a config value
        // that merely looked absurd rather than dangerous. This method is documented as the
        // single enforcement point for these bounds, so the cap belongs here.
        if (cfg.IdleMinutes < 1) cfg.IdleMinutes = 1;
        if (cfg.IdleMinutes > MaximumIdleMinutes) cfg.IdleMinutes = MaximumIdleMinutes;

        cfg.CpuThresholdPercent = AppConfig.NormalizeCpuThresholdPercent(cfg.CpuThresholdPercent);

        if (cfg.SystemIdleFailSafeWindowMs < 0) cfg.SystemIdleFailSafeWindowMs = 0;

        // An unbounded fail-safe window fails in the opposite direction: the window decides
        // when a smaller GetLastInputInfo reading is trusted over the hook clock, so a huge
        // value means it is ALWAYS trusted and the app can effectively never accumulate idle
        // time. It stops launching and reports nothing.
        if (cfg.SystemIdleFailSafeWindowMs > MaximumSystemIdleFailSafeWindowMs)
        {
            cfg.SystemIdleFailSafeWindowMs = MaximumSystemIdleFailSafeWindowMs;
        }

        if (cfg.UseSystemIdleFailSafe && cfg.SystemIdleFailSafeWindowMs < AppConfig.MinimumSystemIdleFailSafeWindowMs)
        {
            cfg.SystemIdleFailSafeWindowMs = AppConfig.MinimumSystemIdleFailSafeWindowMs;
        }

        cfg.AppPath = TargetFilePolicy.NormalizePath(cfg.AppPath);
        if (!string.IsNullOrWhiteSpace(cfg.AppPath) && !TargetFilePolicy.IsSupportedTarget(cfg.AppPath))
        {
            try
            {
                Logger.Warn($"Configuration contained an unsupported target type. Clearing AppPath. Path='{cfg.AppPath}'.");
            }
            catch
            {
                // Ignore.
            }

            cfg.AppPath = string.Empty;
        }

        cfg.AppArguments = (cfg.AppArguments ?? string.Empty).Trim();
        cfg.TrayIconPath = (cfg.TrayIconPath ?? string.Empty).Trim();
        cfg.LastLaunchUtc = (cfg.LastLaunchUtc ?? string.Empty).Trim();
    }


    public static AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.BaseDir);

            if (!File.Exists(AppPaths.ConfigPath))
            {
                var cfgNew = new AppConfig();
                NormalizeInPlace(cfgNew);
                return cfgNew;
            }

            var json = File.ReadAllText(AppPaths.ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
            NormalizeInPlace(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Error("Failed to load config; using defaults.", ex);
            }
            catch
            {
                // Ignore.
            }

            // Move the unreadable file aside before handing back defaults.
            //
            // Returning defaults is not, by itself, a recoverable outcome here: the caller
            // immediately reconciles RunAtStartup against the registry and, when they differ,
            // calls Save -- which writes those defaults straight over config.json. So a single
            // truncated byte (a power cut mid-write is the realistic cause) silently destroyed
            // the user's target path, arguments, thresholds and custom icon within milliseconds
            // of the next launch, and the tray then displayed the defaults as though the user
            // had chosen them. The only notice was one line in a log nobody has reason to open.
            //
            // Quarantining costs nothing when the file is fine and makes the bad case
            // recoverable by hand. If the move fails we still return defaults -- refusing to
            // start because we could not rename a file would be a worse trade.
            TryQuarantineUnreadableConfig();

            var cfgFallback = new AppConfig();
            NormalizeInPlace(cfgFallback);
            return cfgFallback;
        }
    }

    private static void TryQuarantineUnreadableConfig()
    {
        try
        {
            var configPath = AppPaths.ConfigPath;
            if (!File.Exists(configPath))
            {
                return;
            }

            // Invariant culture: this filename is read by a human comparing it against log
            // timestamps, and a non-Gregorian ambient culture would render a different era's
            // year here than Logger writes.
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var quarantinePath = Path.Combine(AppPaths.BaseDir, $"config.corrupt-{stamp}.json");

            File.Move(configPath, quarantinePath, overwrite: true);
            Logger.Warn(
                $"The existing config could not be read, so it was moved aside instead of being overwritten with defaults. Saved as '{quarantinePath}'.");
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Error("Could not move the unreadable config aside; it may be overwritten by the next save.", ex);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    public static void Save(AppConfig cfg)
    {
        var tmpPath = AppPaths.ConfigPath + ".tmp";

        try
        {
            NormalizeInPlace(cfg);
            Directory.CreateDirectory(AppPaths.BaseDir);

            // Write atomically to reduce the chance of a partially-written config file
            // (e.g. power loss / crash mid-write). On failure we still need to remove
            // the stale .tmp file in the finally below — otherwise a successful
            // WriteAllText followed by a failed Move would leave orphaned junk in
            // %APPDATA% that survives across runs.
            var json = JsonSerializer.Serialize(cfg, Options);

            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, AppPaths.ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Don't crash the app due to config write failures.
            try
            {
                Logger.Error("Failed to save config.", ex);
            }
            catch
            {
                // Ignore.
            }
        }
        finally
        {
            // Clean up the staging file if anything went wrong between WriteAllText
            // and Move. The Move succeeds by renaming so the .tmp normally vanishes,
            // but if Move threw we still need to remove the orphaned .tmp.
            try
            {
                if (File.Exists(tmpPath))
                {
                    File.Delete(tmpPath);
                }
            }
            catch
            {
                // Best effort; we already logged the original failure above.
            }
        }
    }
}
