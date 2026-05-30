using System;
using System.IO;
using System.Text.Json;

namespace IdleLauncherTray;

internal static class ConfigManager
{
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
        if (cfg.IdleMinutes < 1) cfg.IdleMinutes = 1;

        cfg.CpuThresholdPercent = AppConfig.NormalizeCpuThresholdPercent(cfg.CpuThresholdPercent);

        if (cfg.SystemIdleFailSafeWindowMs < 0) cfg.SystemIdleFailSafeWindowMs = 0;

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

            var cfgFallback = new AppConfig();
            NormalizeInPlace(cfgFallback);
            return cfgFallback;
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
