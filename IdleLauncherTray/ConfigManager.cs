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

        // Store what the user wrote, not what it resolves to on this machine today.
        // PrepareForStorage only trims and unquotes, both of which are lossless; it deliberately
        // does NOT expand environment variables. This method runs on every Load AND every Save, so
        // the previous NormalizePath call baked "%APPDATA%\tools\app.exe" down to one machine's
        // absolute path within milliseconds of the app starting -- destroying the portable spelling
        // the user chose, and any literal path containing %...%. Expansion now happens in
        // TargetFilePolicy.ResolveForUse, at each point of use.
        cfg.AppPath = TargetFilePolicy.PrepareForStorage(cfg.AppPath);
        ApplyTargetPolicyTo(cfg);

        cfg.AppArguments = (cfg.AppArguments ?? string.Empty).Trim();
        cfg.TrayIconPath = (cfg.TrayIconPath ?? string.Empty).Trim();
        cfg.LastLaunchUtc = (cfg.LastLaunchUtc ?? string.Empty).Trim();
    }

    /// <summary>
    /// Acts on whatever is wrong with the stored target, and records which fault it was.
    /// </summary>
    /// <remarks>
    /// The two faults get opposite treatment, and the difference is the point of this method.
    /// <para>
    /// <b>An unsupported type is cleared.</b> The extension is a property of the string itself:
    /// a ".txt" can never become launchable, on this machine or any other, so keeping it would only
    /// preserve a setting that is guaranteed never to work.
    /// </para>
    /// <para>
    /// <b>An unparseable path is kept.</b> Now that the stored value keeps its environment
    /// variables, whether it parses is a property of the machine reading it — "%TOOLS%\app.exe"
    /// can fail here and be perfectly valid on the box the config came from. Clearing would
    /// destroy the user's setting merely for opening the app on the wrong machine, and because
    /// normalisation runs on Save too, the loss is written to disk immediately and is not
    /// recoverable. Nothing dangerous is kept: ClassifyTarget still refuses it, so the launch
    /// path will not run it, and the fault is now in the log with both spellings of the path.
    /// </para>
    /// </remarks>
    private static void ApplyTargetPolicyTo(AppConfig cfg)
    {
        switch (TargetFilePolicy.ClassifyTarget(cfg.AppPath))
        {
            case TargetPathStatus.UnsupportedType:
                TryWarn(
                    "Configuration contained an unsupported target TYPE, so AppPath was cleared. " +
                    $"Supported types are {TargetFilePolicy.SupportedExtensionsDisplay}. " +
                    $"Path='{TargetFilePolicy.ForDisplay(cfg.AppPath)}'.");
                cfg.AppPath = string.Empty;
                break;

            case TargetPathStatus.Unparseable:
                TryWarn(
                    "Configuration contained a target PATH that Windows cannot interpret -- it is too long, or holds " +
                    "characters a path cannot. This is not an unsupported target type; the type was never reached. " +
                    "AppPath has been KEPT rather than cleared, because the stored value is no longer environment-expanded " +
                    "and can be valid on another machine -- but nothing will launch it here until it parses. " +
                    $"Stored='{TargetFilePolicy.ForDisplay(cfg.AppPath)}' " +
                    $"Expanded='{TargetFilePolicy.ForDisplay(TargetFilePolicy.ResolveForUse(cfg.AppPath))}'.");
                break;

            default:
                // Supported, or nothing stored at all. Nothing to say and nothing to do.
                break;
        }
    }

    /// <summary>
    /// Logger already swallows its own failures, so this guard is belt and braces — but
    /// normalisation runs inside Load's catch-all, where a throw would be reported as "failed to
    /// load config" and quarantine a file that was perfectly readable.
    /// </summary>
    private static void TryWarn(string message)
    {
        try
        {
            Logger.Warn(message);
        }
        catch
        {
            // Ignore.
        }
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

            // Stage, make durable, then publish with a rename — in that order, because the order
            // is the whole guarantee.
            //
            // What this protects against: File.Move over an existing file is atomic on NTFS, so a
            // reader sees either the entire old config or the entire new one and never a mixture;
            // and Flush(flushToDisk: true) issues FlushFileBuffers on the staging file, so its
            // bytes are on the platter before the rename is even attempted. The previous
            // File.WriteAllText left those bytes in the OS write cache, which is precisely how a
            // power cut could publish a complete rename over a file of zeros — the one failure the
            // comment here used to claim protection from.
            //
            // What it still does NOT protect against: the directory entry the rename creates is
            // not itself flushed, and Windows offers no supported way to fsync a directory. A power
            // cut in the window between the rename and NTFS committing that metadata can therefore
            // still lose the save outright. That is a survivable outcome — the previous config is
            // intact and complete — whereas publishing a torn one was not, which is why the flush
            // is the half worth having.
            //
            // The finally below still has to remove the staging file: a successful Move renames it
            // away, but a Move that throws leaves orphaned junk in %APPDATA% that survives runs.
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(cfg, Options);

            // FileMode.Create matches what File.WriteAllText did: truncate an orphaned .tmp from an
            // earlier failure rather than append to it. SerializeToUtf8Bytes with the same Options
            // emits the same UTF-8, BOM-free bytes WriteAllText produced, so nothing about the file
            // on disk changes — only when it reaches the disk.
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(jsonBytes);
                stream.Flush(flushToDisk: true);
            }

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
            // Clean up the staging file if anything went wrong between the flush and the
            // Move. The Move succeeds by renaming so the .tmp normally vanishes, but if
            // Move threw we still need to remove the orphaned .tmp.
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
