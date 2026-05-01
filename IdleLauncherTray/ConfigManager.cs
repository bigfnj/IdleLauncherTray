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
        try
        {
            NormalizeInPlace(cfg);
            Directory.CreateDirectory(AppPaths.BaseDir);

            // Write atomically to reduce the chance of a partially-written config file
            // (e.g. power loss / crash mid-write).
            var json = JsonSerializer.Serialize(cfg, Options);
            var tmpPath = AppPaths.ConfigPath + ".tmp";

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
    }
}
