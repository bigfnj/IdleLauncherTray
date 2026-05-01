using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace IdleLauncherTray;

internal sealed class TrayAppContext : ApplicationContext
{
    // --------------------------------------------------------------------
    // TrayAppContext is the core of the app:
    //   - Builds the NotifyIcon + context menu
    //   - Polls idle time + CPU usage on a small timer
    //   - Launches the selected app/screensaver when the configured conditions are met
    //   - Persists user settings in %APPDATA%\IdleLauncherTray\config.json
    // --------------------------------------------------------------------

    // Behavior (mirrors PS1 defaults)
    private const int CheckIntervalSeconds = 5;
    private const int MinLaunchCooldownSeconds = 10;
    private const int AutomaticLaunchFailureBalloonTimeoutMs = 10000;

    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu;

    // Explicit type to avoid ambiguity with System.Threading.Timer (implicit global using).
    private readonly System.Windows.Forms.Timer _timer;

    private readonly CpuUsageMonitor _cpu;

    private AppConfig _cfg;

    private bool _armed = true;
    private bool _shutDown;
    private int _shutDownSerialized;
    private int _launchingSerialized;
    private int _consecutiveNonIdleTicks;
    private Process? _runningProcess;
    private DateTime? _lastLaunchUtc;

    private Icon? _trayIconObj;

    // Menu items we need to update dynamically
    private readonly ToolStripMenuItem _miStartup;
    private readonly ToolStripMenuItem _miSelected;
    private readonly ToolStripMenuItem _miArguments;
    private readonly ToolStripMenuItem _miBlockInjected;
    private readonly ToolStripMenuItem _miLockPcOnAppClose;
    private readonly ToolStripMenuItem _miGamepad;
    private readonly ToolStripMenuItem _miTrayIconEnabled;

    private readonly List<ToolStripMenuItem> _idleTimerItems = new();
    private readonly List<ToolStripMenuItem> _cpuThresholdItems = new();

    private string? _lastReadinessStateKey;
    private bool? _lastSuppressionState;
    private bool _trackedProcessWasIdleLaunch;

    private sealed class LaunchEvaluation
    {
        public string TargetPath { get; set; } = string.Empty;
        public bool HasTarget { get; set; }
        public bool TargetSupported { get; set; }
        public bool TargetExists { get; set; }
        public bool CooldownOk { get; set; } = true;
        public double CooldownRemainingSeconds { get; set; }
        public int IdleSeconds { get; set; }
        public int RequiredIdleSeconds { get; set; }
        public bool InputIdleOk { get; set; }
        public double CpuPercent { get; set; }
        public bool CpuSampleValid { get; set; }
        public int CpuThresholdPercent { get; set; }
        public bool CpuOk { get; set; }
        public string ReasonCode { get; set; } = "Unknown";

        public bool Ready => HasTarget && TargetSupported && TargetExists && CooldownOk && InputIdleOk && CpuOk;

        public string StateKey(bool armed)
        {
            return string.Join(
                "|",
                armed ? "armed" : "disarmed",
                ReasonCode,
                HasTarget ? "target" : "no-target",
                TargetSupported ? "target-supported" : "target-unsupported",
                TargetExists ? "target-exists" : "target-missing",
                CooldownOk ? "cooldown-ok" : "cooldown-wait",
                InputIdleOk ? "idle-ok" : "idle-wait",
                CpuSampleValid ? "cpu-valid" : "cpu-invalid",
                CpuOk ? "cpu-ok" : "cpu-blocked");
        }

        public string Describe(bool armed)
        {
            var state = Ready
                ? (armed ? "ready-to-launch" : "ready-but-waiting-for-rearm")
                : "not-ready";

            var cpuText = CpuSampleValid
                ? CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
                : "unknown";

            return
                $"state={state}; reason={ReasonCode}; target='{TargetPath}'; targetSupported={TargetSupported}; targetExists={TargetExists}; idle={IdleSeconds}s/{RequiredIdleSeconds}s; cpu={cpuText}/{CpuThresholdPercent}%; cooldownOk={CooldownOk}; cooldownRemaining={CooldownRemainingSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s; armed={armed}.";
        }
    }

    public TrayAppContext()
    {
        _cfg = ConfigManager.Load();
        LogConfigurationSummary();

        // Configure/Start physical-idle tracking hooks
        PhysicalIdle.GamepadEnabled = _cfg.GamepadCountsAsActivity;
        PhysicalIdle.UseSystemIdleFailSafe = _cfg.UseSystemIdleFailSafe;
        PhysicalIdle.SystemIdleFailSafeWindowMs = _cfg.SystemIdleFailSafeWindowMs;
        PhysicalIdle.IgnoreScrollLock = true;
        PhysicalIdle.Start();
        LogHookStatus();

        _lastSuppressionState = PhysicalIdle.SuppressInjected;
        Logger.Info($"Injected input suppression initial state: {PhysicalIdle.SuppressInjected}.");

        // Keep config startup flag consistent with registry reality
        var oldStartup = _cfg.RunAtStartup;
        _cfg.RunAtStartup = StartupManager.GetStartupEnabled();
        if (_cfg.RunAtStartup != oldStartup)
        {
            ConfigManager.Save(_cfg);
            Logger.Info(
                $"Startup setting synchronized from registry. PreviousConfigValue={oldStartup}; CurrentRegistryValue={_cfg.RunAtStartup}.");
        }

        _notify = new NotifyIcon
        {
            Text = AppPaths.AppName,
            Visible = true
        };

        // Tray menu
        _menu = new ContextMenuStrip
        {
            ShowItemToolTips = true
        };

        // Run at startup
        _miStartup = new ToolStripMenuItem("Run at startup")
        {
            CheckOnClick = true,
            Checked = _cfg.RunAtStartup
        };

        _miStartup.Click += (_, _) =>
        {
            try
            {
                StartupManager.SetStartupEnabled(_miStartup.Checked);
                _cfg.RunAtStartup = StartupManager.GetStartupEnabled();
                _miStartup.Checked = _cfg.RunAtStartup;
                ConfigManager.Save(_cfg);
                Logger.Info($"Run at startup set to {_cfg.RunAtStartup}. CurrentExe='{AppPaths.CurrentExePath}'.");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to change startup setting.", ex);

                MessageBox.Show(
                    $"Failed to change startup setting:\n{ex.Message}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                _miStartup.Checked = StartupManager.GetStartupEnabled();
            }
        };

        _menu.Items.Add(_miStartup);
        _menu.Items.Add(new ToolStripSeparator());

        // Idle timer submenu
        var miIdle = new ToolStripMenuItem("Idle timer");
        int[] idleOptions = { 1, 3, 5, 10, 15, 20, 30 };

        foreach (var minutes in idleOptions)
        {
            var item = new ToolStripMenuItem($"{minutes} minute{(minutes == 1 ? string.Empty : "s")}")
            {
                Tag = minutes,
                Checked = _cfg.IdleMinutes == minutes
            };

            item.Click += (_, _) =>
            {
                var chosen = (int)item.Tag!;
                _cfg.IdleMinutes = chosen;

                foreach (var ti in _idleTimerItems)
                {
                    ti.Checked = (int)ti.Tag! == chosen;
                }

                ConfigManager.Save(_cfg);
                InvalidateReadinessSnapshot();
                Logger.Info($"Idle timer set to {chosen} minute(s).");
            };

            _idleTimerItems.Add(item);
            miIdle.DropDownItems.Add(item);
        }

        _menu.Items.Add(miIdle);

        // CPU Threshold submenu
        var miCpu = new ToolStripMenuItem("CPU Threshold");
        int[] cpuOptions = { 10, 20, 30, 40, 50 };

        foreach (var pct in cpuOptions)
        {
            var item = new ToolStripMenuItem($"{pct}%")
            {
                Tag = pct,
                Checked = _cfg.CpuThresholdPercent == pct,
                ToolTipText = "Only launch when total CPU usage is at or below this threshold."
            };

            item.Click += (_, _) =>
            {
                var chosen = (int)item.Tag!;
                chosen = AppConfig.NormalizeCpuThresholdPercent(chosen);
                _cfg.CpuThresholdPercent = chosen;

                foreach (var ti in _cpuThresholdItems)
                {
                    ti.Checked = (int)ti.Tag! == chosen;
                }

                ConfigManager.Save(_cfg);
                InvalidateReadinessSnapshot();
                Logger.Info($"CPU threshold set to {chosen}%.");
            };

            _cpuThresholdItems.Add(item);
            miCpu.DropDownItems.Add(item);
        }

        _menu.Items.Add(miCpu);

        // Application submenu
        var miApp = new ToolStripMenuItem("Application");

        var miChoose = new ToolStripMenuItem("Choose target application...")
        {
            ToolTipText = "Select an application, script, or shortcut to launch when idle."
        };

        miChoose.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter =
                    "Supported files (*.exe;*.scr;*.bat;*.cmd;*.lnk;*.msi;*.ps1;*.vbs;*.jar;*.py)|*.exe;*.scr;*.bat;*.cmd;*.lnk;*.msi;*.ps1;*.vbs;*.jar;*.py|Applications (*.exe;*.scr)|*.exe;*.scr|Scripts & Shortcuts (*.bat;*.cmd;*.ps1;*.vbs;*.py;*.lnk;*.jar;*.msi)|*.bat;*.cmd;*.ps1;*.vbs;*.py;*.lnk;*.jar;*.msi|All files (*.*)|*.*",
                Title = "Select a target application",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                var selectedPath = TargetFilePolicy.NormalizePath(dlg.FileName);
                if (!TargetFilePolicy.IsSupportedTarget(selectedPath))
                {
                    Logger.Warn($"Application selection rejected because the file type is unsupported. Path='{selectedPath}'.");

                    MessageBox.Show(
                        TargetFilePolicy.GetUnsupportedTargetMessage(selectedPath),
                        AppPaths.AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                _cfg.AppPath = selectedPath;
                ConfigManager.Save(_cfg);
                UpdateSelectedAppMenuText();
                InvalidateReadinessSnapshot();
                Logger.Info($"Selected application changed to '{_cfg.AppPath}'.");
            }
        };

        var miSetArgs = new ToolStripMenuItem("Set arguments...")
        {
            ToolTipText = "Optional command-line arguments passed to the selected file (e.g. -fullscreen -s -w)."
        };

        miSetArgs.Click += (_, _) =>
        {
            try
            {
                var val = _cfg.AppArguments ?? string.Empty;
                var previous = val;

                if (TextPrompt.Show(AppPaths.AppName, "Arguments to pass (leave blank for none):", ref val))
                {
                    _cfg.AppArguments = (val ?? string.Empty).Trim();
                    ConfigManager.Save(_cfg);
                    UpdateArgumentsMenuText();
                    UpdateSelectedAppMenuText();
                    Logger.Info(
                        $"Launch arguments updated. Previous={SummarizeArgumentsForLog(previous)} Current={SummarizeArgumentsForLog(_cfg.AppArguments)}.");
                    MaybeWarnAboutSensitiveArguments(_cfg.AppArguments);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to set arguments.", ex);

                MessageBox.Show(
                    $"Failed to set arguments:\n{ex.Message}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };

        _miSelected = new ToolStripMenuItem("Selected: (none)")
        {
            Enabled = false
        };

        _miArguments = new ToolStripMenuItem("Arguments: (none)")
        {
            Enabled = false
        };

        UpdateSelectedAppMenuText();
        UpdateArgumentsMenuText();

        miApp.DropDownItems.Add(miChoose);
        miApp.DropDownItems.Add(miSetArgs);
        miApp.DropDownItems.Add(new ToolStripSeparator());
        miApp.DropDownItems.Add(_miSelected);
        miApp.DropDownItems.Add(_miArguments);

        _menu.Items.Add(miApp);

        // Options submenu
        var miOptions = new ToolStripMenuItem("Options");

        _miBlockInjected = new ToolStripMenuItem("Block injected input while running")
        {
            CheckOnClick = true,
            Checked = _cfg.BlockInjectedWhileRunning,
            ToolTipText =
                "When enabled, injected/virtual input (e.g., SendKeys) is blocked while the launched app/screensaver is running."
        };

        _miBlockInjected.Click += (_, _) =>
        {
            _cfg.BlockInjectedWhileRunning = _miBlockInjected.Checked;
            ConfigManager.Save(_cfg);
            SetInjectedSuppression(
                _cfg.BlockInjectedWhileRunning && UpdateTrackedProcessState(logStateChange: false),
                _cfg.BlockInjectedWhileRunning
                    ? "blocking while running was enabled"
                    : "blocking while running was disabled");
            Logger.Info($"Block injected input while running set to {_cfg.BlockInjectedWhileRunning}.");
        };

        miOptions.DropDownItems.Add(_miBlockInjected);

        _miLockPcOnAppClose = new ToolStripMenuItem("Lock PC on App Close")
        {
            CheckOnClick = true,
            Checked = _cfg.LockPcOnAppClose,
            ToolTipText =
                "When enabled, Windows will lock after an application or screensaver that was launched automatically by idle detection closes. Manual Run Now launches do not trigger this."
        };

        _miLockPcOnAppClose.Click += (_, _) =>
        {
            _cfg.LockPcOnAppClose = _miLockPcOnAppClose.Checked;
            ConfigManager.Save(_cfg);

            var running = UpdateTrackedProcessState(logStateChange: false);
            if (running && _trackedProcessWasIdleLaunch)
            {
                Logger.Info(
                    $"Lock PC on App Close set to {_cfg.LockPcOnAppClose}. The currently tracked process was launched automatically from idle, so workstation lock on close is now {(_cfg.LockPcOnAppClose ? "enabled" : "disabled")} for this run.");
            }
            else
            {
                Logger.Info($"Lock PC on App Close set to {_cfg.LockPcOnAppClose}.");
            }
        };

        miOptions.DropDownItems.Add(_miLockPcOnAppClose);

        _miGamepad = new ToolStripMenuItem("Count gamepad input as activity")
        {
            CheckOnClick = true,
            Checked = _cfg.GamepadCountsAsActivity,
            ToolTipText = "When enabled, XInput controller input (Xbox/most gamepads) will reset the idle timer."
        };

        _miGamepad.Click += (_, _) =>
        {
            _cfg.GamepadCountsAsActivity = _miGamepad.Checked;
            ConfigManager.Save(_cfg);

            try
            {
                PhysicalIdle.SetGamepadEnabled(_miGamepad.Checked);
                Logger.Info($"Count gamepad input as activity set to {_miGamepad.Checked}.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to apply gamepad activity setting immediately. Error='{ex.Message}'.");
            }
        };

        miOptions.DropDownItems.Add(_miGamepad);
        miOptions.DropDownItems.Add(new ToolStripSeparator());

        // Enable/disable custom tray icon (when disabled, we use the EXE icon)
        _miTrayIconEnabled = new ToolStripMenuItem("Use custom tray icon")
        {
            CheckOnClick = true,
            Checked = _cfg.TrayIconEnabled
        };

        _miTrayIconEnabled.Click += (_, _) =>
        {
            _cfg.TrayIconEnabled = _miTrayIconEnabled.Checked;
            ConfigManager.Save(_cfg);
            Logger.Info($"Use custom tray icon set to {_cfg.TrayIconEnabled}.");
            ApplyTrayIcon();
        };

        // Choose tray icon
        var miChooseIcon = new ToolStripMenuItem("Choose tray icon (.ico)...")
        {
            ToolTipText = "Pick a custom tray icon. The selected icon is copied to %APPDATA%\\IdleLauncherTray\\tray.ico"
        };

        miChooseIcon.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Icon files (*.ico)|*.ico|All files (*.*)|*.*",
                Title = "Select a tray icon (.ico)",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.BaseDir);
                    File.Copy(dlg.FileName, AppPaths.TrayIconFile, overwrite: true);

                    _cfg.TrayIconPath = AppPaths.TrayIconFile;
                    _cfg.TrayIconEnabled = true;
                    _miTrayIconEnabled.Checked = true;

                    ConfigManager.Save(_cfg);
                    Logger.Info(
                        $"Custom tray icon selected. Source='{dlg.FileName}' StoredPath='{AppPaths.TrayIconFile}'.");
                    ApplyTrayIcon();
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to set tray icon.", ex);

                    MessageBox.Show(
                        $"Failed to set tray icon:\n{ex.Message}",
                        AppPaths.AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        };

        miOptions.DropDownItems.Add(miChooseIcon);
        miOptions.DropDownItems.Add(_miTrayIconEnabled);

        // Reset to default icon (EXE icon)
        var miResetIcon = new ToolStripMenuItem("Reset tray icon to default");

        miResetIcon.Click += (_, _) =>
        {
            try
            {
                _cfg.TrayIconEnabled = false;
                _cfg.TrayIconPath = string.Empty;
                _miTrayIconEnabled.Checked = false;

                try
                {
                    if (File.Exists(AppPaths.TrayIconFile))
                    {
                        File.Delete(AppPaths.TrayIconFile);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to delete the stored custom tray icon during reset. Error='{ex.Message}'.");
                }

                ConfigManager.Save(_cfg);
                Logger.Info("Custom tray icon reset to default executable icon. Stored custom icon state was cleared.");
                ApplyTrayIcon();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to reset tray icon to default. Error='{ex.Message}'.");
            }
        };

        miOptions.DropDownItems.Add(miResetIcon);

        _menu.Items.Add(miOptions);
        _menu.Items.Add(new ToolStripSeparator());

        // Run Now
        var miRunNow = new ToolStripMenuItem("Run Now")
        {
            ToolTipText = "Launch the selected application immediately (bypasses idle timer and CPU check)."
        };

        miRunNow.Click += (_, _) =>
        {
            Logger.Info("Run Now requested from tray menu.");

            if (string.IsNullOrWhiteSpace(_cfg.AppPath))
            {
                Logger.Warn("Run Now aborted because no application is selected.");

                MessageBox.Show(
                    "No application selected.\nUse Application → Choose target application… first.",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (!TargetFilePolicy.IsSupportedTarget(_cfg.AppPath))
            {
                Logger.Warn($"Run Now aborted because the selected target type is unsupported. Path='{_cfg.AppPath}'.");

                MessageBox.Show(
                    TargetFilePolicy.GetUnsupportedTargetMessage(_cfg.AppPath),
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(_cfg.AppPath))
            {
                Logger.Warn($"Run Now aborted because the selected file does not exist. Path='{_cfg.AppPath}'.");

                MessageBox.Show(
                    $"Selected file not found:\n{_cfg.AppPath}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (TryLaunchSelectedApp("manual Run Now", launchedFromIdle: false, showErrorDialog: true, out _))
            {
                DisarmAfterLaunch("manual Run Now");
            }
        };

        _menu.Items.Add(miRunNow);
        _menu.Items.Add(new ToolStripSeparator());

        // Uninstall
        var miUninstall = new ToolStripMenuItem("Uninstall (remove settings + startup)");

        miUninstall.Click += (_, _) =>
        {
            var res = MessageBox.Show(
                $"This removes startup registration and deletes settings stored in:\n{AppPaths.BaseDir}\n\nThe portable executable itself is not deleted.\n\nContinue?",
                AppPaths.AppName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (res == DialogResult.Yes)
            {
                Logger.Info("Portable uninstall requested. Startup registration and AppData settings will be removed; the portable executable will be left in place.");

                try
                {
                    StartupManager.SetStartupEnabled(false);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to remove startup registration during uninstall. Error='{ex.Message}'.");
                }

                ShutdownForExit();

                // Schedule deletion of the whole appdata folder (includes settings/logs)
                DeletionHelper.ScheduleFolderDelete(AppPaths.BaseDir);

                ExitThread();
            }
            else
            {
                Logger.Info("Portable uninstall canceled by user.");
            }
        };

        _menu.Items.Add(miUninstall);

        // Exit
        var miExit = new ToolStripMenuItem("Exit");

        miExit.Click += (_, _) =>
        {
            Logger.Info("Exit requested from tray menu.");
            ShutdownForExit();
            ExitThread();
        };

        _menu.Items.Add(miExit);

        _notify.ContextMenuStrip = _menu;

        ApplyTrayIcon();

        // CPU sampler
        _cpu = new CpuUsageMonitor();
        Logger.Info("CPU usage monitor initialized.");

        if (!string.IsNullOrWhiteSpace(_cfg.LastLaunchUtc)
            && DateTime.TryParse(
                _cfg.LastLaunchUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            _lastLaunchUtc = parsed.ToUniversalTime();
            Logger.Info($"Restored last launch timestamp: {_lastLaunchUtc.Value.ToString("o", CultureInfo.InvariantCulture)} UTC.");
        }
        else if (!string.IsNullOrWhiteSpace(_cfg.LastLaunchUtc))
        {
            Logger.Warn($"Could not parse saved LastLaunchUtc value. Value='{_cfg.LastLaunchUtc}'.");
        }

        // Monitor loop
        _timer = new System.Windows.Forms.Timer { Interval = CheckIntervalSeconds * 1000 };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        Logger.Info(
            $"Monitoring started. CheckIntervalSeconds={CheckIntervalSeconds}; MinLaunchCooldownSeconds={MinLaunchCooldownSeconds}; PortableExecutable='{AppPaths.CurrentExePath}'.");
    }

    private void UpdateSelectedAppMenuText()
    {
        if (string.IsNullOrWhiteSpace(_cfg.AppPath))
        {
            _miSelected.Text = "Selected: (none)";
            _miSelected.ToolTipText = string.Empty;
            return;
        }

        _miSelected.Text = "Selected: " + Path.GetFileName(_cfg.AppPath);

        // Tooltip shows full path + args (if any).
        var tip = _cfg.AppPath;
        if (!string.IsNullOrWhiteSpace(_cfg.AppArguments))
        {
            tip += Environment.NewLine + "Args: " + _cfg.AppArguments.Trim();
        }

        _miSelected.ToolTipText = tip;
    }

    private void UpdateArgumentsMenuText()
    {
        var args = (_cfg.AppArguments ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(args))
        {
            _miArguments.Text = "Arguments: (none)";
            _miArguments.ToolTipText = "No arguments will be passed.";
            return;
        }

        const int maxPreview = 60;
        var preview = args.Length <= maxPreview ? args : args[..maxPreview] + "…";
        _miArguments.Text = "Arguments: " + preview;
        _miArguments.ToolTipText = args;
    }

    private void ApplyTrayIcon()
    {
        try
        {
            _trayIconObj?.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _trayIconObj = null;

        // 1) Custom icon (if enabled)
        try
        {
            if (_cfg.TrayIconEnabled)
            {
                var path = _cfg.TrayIconPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = AppPaths.TrayIconFile;
                }

                if (File.Exists(path))
                {
                    // Load icon without locking file: read bytes -> Icon -> Clone
                    Icon? loaded = null;
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        using var ms = new MemoryStream(bytes);
                        using var ico = new Icon(ms);
                        loaded = (Icon)ico.Clone();
                    }
                    catch
                    {
                        // Fall through to default icon.
                    }

                    if (loaded != null)
                    {
                        try
                        {
                            _trayIconObj?.Dispose();
                        }
                        catch
                        {
                            // Ignore disposal failure of the previous icon.
                        }

                        _trayIconObj = loaded;
                        _notify.Icon = _trayIconObj;
                        Logger.Info($"Applied custom tray icon from '{path}'.");
                        return;
                    }
                }

                Logger.Warn($"Custom tray icon is enabled but the icon file was not found. Falling back to default icon. Path='{path}'.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to apply custom tray icon. Falling back to default icon. Error='{ex.Message}'.");
        }

        // 2) Default: use the EXE's own icon (set via matrix.ico in the project), then System default.
        Icon? extractedExeIcon = null;

        try
        {
            extractedExeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to extract executable icon. Error='{ex.Message}'.");
            extractedExeIcon = null;
        }

        try
        {
            // Dispose any previously-held custom icon before replacing it.
            try
            {
                _trayIconObj?.Dispose();
            }
            catch
            {
                // Ignore disposal failures.
            }

            _trayIconObj = (Icon)(extractedExeIcon?.Clone() ?? SystemIcons.Application.Clone());
            _notify.Icon = _trayIconObj;
            Logger.Info("Applied default tray icon.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to apply default icon. Falling back to SystemIcons.Application. Error='{ex.Message}'.");
            _notify.Icon = SystemIcons.Application;
        }
        finally
        {
            try
            {
                extractedExeIcon?.Dispose();
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static int GetIdleSeconds()
    {
        return (int)Math.Floor(PhysicalIdle.GetIdleMilliseconds() / 1000.0);
    }

    private void OnTick()
    {
        try
        {
            PhysicalIdle.TryRepairHooksIfNeeded();

            var running = UpdateTrackedProcessState(logStateChange: true);

            SetInjectedSuppression(
                _cfg.BlockInjectedWhileRunning && running,
                running && _cfg.BlockInjectedWhileRunning
                    ? "tracked process is running and blocking is enabled"
                    : "the tracked process is not running or blocking is disabled");

            if (running)
            {
                return;
            }

            var evaluation = EvaluateLaunchReadiness();
            LogLaunchReadinessIfNeeded(evaluation);

            if (evaluation.Ready)
            {
                if (_armed)
                {
                    if (TryLaunchSelectedApp("automatic idle trigger", launchedFromIdle: true, showErrorDialog: false, out var failureMessage))
                    {
                        DisarmAfterLaunch("automatic idle trigger");
                    }
                    else
                    {
                        HandleAutomaticLaunchFailure("automatic idle trigger", evaluation.TargetPath, failureMessage);
                    }
                }

                return;
            }

            if (!_armed && !evaluation.InputIdleOk)
            {
                // Require at least 2 consecutive non-idle ticks to confirm genuine user activity
                // before re-arming. This prevents rapid arm/disarm flapping when the user is
                // at the boundary of the idle threshold or jiggles a mouse briefly.
                _consecutiveNonIdleTicks++;
                if (_consecutiveNonIdleTicks >= 2)
                {
                    _armed = true;
                    InvalidateReadinessSnapshot();
                    Logger.Info(
                        $"Launcher re-armed because fresh user activity ended the previous idle episode. Idle={evaluation.IdleSeconds}s/{evaluation.RequiredIdleSeconds}s.");
                }
            }
            else
            {
                _consecutiveNonIdleTicks = 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Unhandled exception in monitor tick.", ex);
        }
    }

    private LaunchEvaluation EvaluateLaunchReadiness()
    {
        var targetPath = TargetFilePolicy.NormalizePath(_cfg.AppPath);

        var evaluation = new LaunchEvaluation
        {
            TargetPath = targetPath,
            HasTarget = !string.IsNullOrWhiteSpace(targetPath),
            RequiredIdleSeconds = Math.Max(1, _cfg.IdleMinutes) * 60,
            CpuThresholdPercent = Math.Max(0, _cfg.CpuThresholdPercent),
            CooldownOk = true,
            InputIdleOk = false,
            CpuOk = false
        };

        if (!evaluation.HasTarget)
        {
            evaluation.ReasonCode = "NoTargetConfigured";
            return evaluation;
        }

        evaluation.TargetSupported = TargetFilePolicy.IsSupportedTarget(targetPath);
        if (!evaluation.TargetSupported)
        {
            evaluation.ReasonCode = "SelectedTargetUnsupported";
            return evaluation;
        }

        evaluation.TargetExists = File.Exists(targetPath);
        if (!evaluation.TargetExists)
        {
            evaluation.ReasonCode = "SelectedTargetMissing";
            return evaluation;
        }

        evaluation.IdleSeconds = GetIdleSeconds();
        evaluation.CpuSampleValid = _cpu.TryNextValue(out var cpuPercent);
        evaluation.CpuPercent = evaluation.CpuSampleValid ? cpuPercent : 0;
        evaluation.InputIdleOk = evaluation.IdleSeconds >= evaluation.RequiredIdleSeconds;
        evaluation.CpuOk = evaluation.CpuSampleValid && evaluation.CpuPercent <= evaluation.CpuThresholdPercent;

        if (_lastLaunchUtc.HasValue)
        {
            var delta = (DateTime.UtcNow - _lastLaunchUtc.Value).TotalSeconds;
            if (delta < MinLaunchCooldownSeconds)
            {
                evaluation.CooldownOk = false;
                evaluation.CooldownRemainingSeconds = MinLaunchCooldownSeconds - delta;
            }
        }

        if (!evaluation.CooldownOk)
        {
            evaluation.ReasonCode = "LaunchCooldownActive";
        }
        else if (!evaluation.InputIdleOk)
        {
            evaluation.ReasonCode = "WaitingForInputIdle";
        }
        else if (!evaluation.CpuSampleValid)
        {
            evaluation.ReasonCode = "CpuSampleUnavailable";
        }
        else if (!evaluation.CpuOk)
        {
            evaluation.ReasonCode = "CpuAboveThreshold";
        }
        else
        {
            evaluation.ReasonCode = "Ready";
        }

        return evaluation;
    }

    private bool TryLaunchSelectedApp(string trigger, bool launchedFromIdle, bool showErrorDialog, out string failureMessage)
    {
        failureMessage = string.Empty;

        // Guard against concurrent launches (e.g., rapid Run Now clicks or overlapping idle triggers).
        if (Interlocked.Exchange(ref _launchingSerialized, 1) != 0)
        {
            failureMessage = "A launch is already in progress.";
            Logger.Warn($"Launch skipped ({trigger}) because another launch is already in progress.");
            return false;
        }

        string path = TargetFilePolicy.NormalizePath(_cfg.AppPath);
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                failureMessage = "No application is selected.";
                Logger.Warn($"Launch skipped ({trigger}) because no application is selected.");

                if (showErrorDialog)
                {
                    MessageBox.Show(
                        "No application selected.\nUse Application → Choose target application… first.",
                        AppPaths.AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return false;
            }

            if (!TargetFilePolicy.IsSupportedTarget(path))
            {
                failureMessage = TargetFilePolicy.GetUnsupportedTargetMessage(path);
                Logger.Warn($"Launch skipped ({trigger}) because the selected target type is unsupported. Path='{path}'.");

                if (showErrorDialog)
                {
                    MessageBox.Show(
                        failureMessage,
                        AppPaths.AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                return false;
            }

            if (!File.Exists(path))
            {
                failureMessage = $"Selected file not found: {path}";
                Logger.Warn($"Launch skipped ({trigger}) because the selected file does not exist. Path='{path}'.");

                if (showErrorDialog)
                {
                    MessageBox.Show(
                        $"Selected file not found:\n{path}",
                        AppPaths.AppName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                return false;
            }

            var previousProcess = _runningProcess;
            if (previousProcess != null)
            {
                Logger.Warn(
                    $"A new launch is being requested while a tracked process handle already exists. Trigger='{trigger}' ExistingProcess={DescribeProcess(previousProcess)}. The existing tracking session will be replaced.");
            }

            var ext = Path.GetExtension(path).ToLowerInvariant();

            var userArgs = (_cfg.AppArguments ?? string.Empty).Trim();
            var finalArgs = userArgs;

            if (ext == ".scr")
            {
                // Preserve old behavior: screensavers launch full-screen via /s.
                finalArgs = string.IsNullOrWhiteSpace(userArgs) ? "/s" : $"/s {userArgs}";
            }

            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = finalArgs,
                UseShellExecute = true
            };

            // Helpful for batch files and apps that expect relative paths.
            try
            {
                var wd = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(wd))
                {
                    psi.WorkingDirectory = wd;
                }
            }
            catch
            {
                // Ignore.
            }

            var startedProcess = Process.Start(psi);
            _runningProcess = startedProcess;
            _trackedProcessWasIdleLaunch = launchedFromIdle && startedProcess != null;
            _lastLaunchUtc = DateTime.UtcNow;

            _cfg.LastLaunchUtc = _lastLaunchUtc.Value.ToString("o", CultureInfo.InvariantCulture);
            ConfigManager.Save(_cfg);

            if (previousProcess != null && !ReferenceEquals(previousProcess, startedProcess))
            {
                try
                {
                    previousProcess.Dispose();
                }
                catch
                {
                    // Ignore.
                }
            }

            var running = startedProcess != null && UpdateTrackedProcessState(logStateChange: false);
            var trackingState = startedProcess == null
                ? "StartedButUntracked"
                : running
                    ? "Tracked"
                    : "TrackedProcessExitedImmediately";
            var lockOnCloseEligible = launchedFromIdle && startedProcess != null;

            Logger.Info(
                $"Launch succeeded. Trigger='{trigger}' Path='{path}' UserArguments={SummarizeArgumentsForLog(userArgs)} FinalArguments={SummarizeArgumentsForLog(finalArgs)} WorkingDirectory='{(string.IsNullOrWhiteSpace(psi.WorkingDirectory) ? "(default)" : psi.WorkingDirectory)}' TrackingState={trackingState} TrackedProcess={DescribeProcess(_runningProcess)} LastLaunchUtc='{_cfg.LastLaunchUtc}' LaunchedFromIdle={launchedFromIdle} LockOnCloseEligible={lockOnCloseEligible}.");

            if (launchedFromIdle && _cfg.LockPcOnAppClose && startedProcess == null)
            {
                Logger.Warn(
                    "Lock PC on App Close is enabled, but the idle-triggered launch did not yield a tracked process handle. The workstation cannot be locked automatically when that target closes.");
            }

            SetInjectedSuppression(
                _cfg.BlockInjectedWhileRunning && running,
                running
                    ? $"target launched via {trigger}"
                    : $"launch via {trigger} is not currently tracked");

            InvalidateReadinessSnapshot();
            return true;
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            Logger.Error($"Failed to launch target via {trigger}. Path='{path}'.", ex);

            if (showErrorDialog)
            {
                MessageBox.Show(
                    $"Failed to launch:\n{ex.Message}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _launchingSerialized, 0);
        }
    }

    private void DisarmAfterLaunch(string trigger)
    {
        var wasArmed = _armed;
        _armed = false;
        _consecutiveNonIdleTicks = 0;
        InvalidateReadinessSnapshot();

        Logger.Info(
            wasArmed
                ? $"Launcher disarmed after {trigger}. Another automatic launch will not occur until fresh user activity starts a new idle episode."
                : $"Launcher remained disarmed after {trigger}."
        );
    }

    private void HandleAutomaticLaunchFailure(string trigger, string targetPath, string? failureMessage)
    {
        var wasArmed = _armed;
        _armed = false;
        _consecutiveNonIdleTicks = 0;
        InvalidateReadinessSnapshot();

        var normalizedFailure = string.IsNullOrWhiteSpace(failureMessage) ? "unknown error" : failureMessage.Trim();

        Logger.Warn(
            wasArmed
                ? $"Automatic launch attempt failed and the launcher was disarmed until fresh user activity occurs. Trigger='{trigger}' Target='{targetPath}' Failure='{normalizedFailure}'."
                : $"Automatic launch attempt failed while the launcher was already disarmed. Trigger='{trigger}' Target='{targetPath}' Failure='{normalizedFailure}'."
        );

        ShowTrayNotification(
            "Automatic launch failed",
            BuildAutomaticLaunchFailureNotificationText(targetPath),
            ToolTipIcon.Warning);
    }

    private void ShowTrayNotification(string title, string text, ToolTipIcon icon)
    {
        try
        {
            var safeTitle = TruncateForBalloonTip(title, 63);
            var safeText = TruncateForBalloonTip(text, 255);
            _notify.ShowBalloonTip(AutomaticLaunchFailureBalloonTimeoutMs, safeTitle, safeText, icon);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show tray notification. Title='{title}' Error='{ex.Message}'.");
        }
    }

    private static string BuildAutomaticLaunchFailureNotificationText(string targetPath)
    {
        var fileName = Path.GetFileName(targetPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "the selected target";
        }

        return $"{fileName} could not be launched automatically. IdleLauncherTray will wait for new user activity before trying again. See the log for details.";
    }

    private static string TruncateForBalloonTip(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 1)
        {
            return value[..maxLength];
        }

        return value[..(maxLength - 1)] + "…";
    }

    private bool UpdateTrackedProcessState(bool logStateChange)
    {
        if (_runningProcess == null)
        {
            _trackedProcessWasIdleLaunch = false;
            return false;
        }

        var processDescription = DescribeProcess(_runningProcess);
        var exited = false;

        // Use WaitForExit(0) as the definitive exit check. It returns true if the process
        // has already exited, false if it is still running or if we cannot determine the state.
        // This avoids the TOCTOU window between HasExited and Dispose.
        try
        {
            exited = _runningProcess.WaitForExit(0);
        }
        catch (Exception ex)
        {
            if (logStateChange)
            {
                Logger.Warn($"Failed to query tracked process state. Process={processDescription} Error='{ex.Message}'.");
            }

            // Treat as not exited — we will retry on the next tick.
            return true;
        }

        if (!exited)
        {
            // Process is still running.
            return true;
        }

        // Process has exited. Log the exit.
        if (logStateChange)
        {
            try
            {
                Logger.Info($"Tracked process exited. Process={processDescription} ExitCode={_runningProcess.ExitCode}.");
            }
            catch
            {
                Logger.Info($"Tracked process exited. Process={processDescription}.");
            }
        }

        // Dispose the handle. Failure here is non-fatal — the handle is already invalid.
        try
        {
            _runningProcess.Dispose();
        }
        catch
        {
            // Ignore disposal failures on already-exited processes.
        }

        MaybeLockWorkstationAfterTrackedProcessExit(processDescription);

        _runningProcess = null;
        _trackedProcessWasIdleLaunch = false;
        InvalidateReadinessSnapshot();
        return false;
    }

    private void MaybeLockWorkstationAfterTrackedProcessExit(string processDescription)
    {
        if (!_trackedProcessWasIdleLaunch || !_cfg.LockPcOnAppClose)
        {
            return;
        }

        if (_shutDown)
        {
            Logger.Info(
                $"Tracked idle-launched process closed while the tray application was shutting down. Workstation lock was skipped. Process={processDescription}.");
            return;
        }

        Logger.Info(
            $"Tracked idle-launched process closed. Lock PC on App Close is enabled; attempting to lock the workstation. Process={processDescription}.");

        if (WorkstationLock.TryLock(out var errorMessage))
        {
            Logger.Info($"Workstation lock request succeeded after tracked process exit. Process={processDescription}.");
            return;
        }

        Logger.Error(
            $"Workstation lock request failed after tracked process exit. Process={processDescription}. Error='{errorMessage}'.");
    }

    private void SetInjectedSuppression(bool enabled, string reason)
    {
        // Read PhysicalIdle.SuppressInjected exactly once to avoid a race where another
        // thread modifies it between our read and our write to _lastSuppressionState.
        var currentPhysical = PhysicalIdle.SuppressInjected;
        if (_lastSuppressionState.HasValue && _lastSuppressionState.Value == enabled && currentPhysical == enabled)
        {
            return;
        }

        PhysicalIdle.SuppressInjected = enabled;
        _lastSuppressionState = enabled;

        Logger.Info($"Injected input suppression {(enabled ? "enabled" : "disabled")} because {reason}.");
    }

    private void LogLaunchReadinessIfNeeded(LaunchEvaluation evaluation)
    {
        var key = evaluation.StateKey(_armed);
        if (string.Equals(_lastReadinessStateKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastReadinessStateKey = key;
        Logger.Info("Launch readiness changed. " + evaluation.Describe(_armed));
    }

    private void InvalidateReadinessSnapshot()
    {
        _lastReadinessStateKey = null;
    }

    private static string SummarizeArgumentsForLog(string? args)
    {
        var normalized = (args ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "(none)";
        }

        var sensitiveHint = ArgumentsMayContainSensitiveData(normalized) ? "true" : "false";
        return $"(redacted; length={normalized.Length}; sensitiveHint={sensitiveHint})";
    }

    private static bool ArgumentsMayContainSensitiveData(string? args)
    {
        var normalized = (args ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        string[] indicators =
        {
            "password",
            "passwd",
            "pwd",
            "secret",
            "token",
            "apikey",
            "api-key",
            "api_key",
            "clientsecret",
            "client_secret",
            "bearer",
            "authorization",
            "auth=",
            "sig=",
            "sas="
        };

        foreach (var indicator in indicators)
        {
            if (normalized.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void MaybeWarnAboutSensitiveArguments(string? args)
    {
        if (!ArgumentsMayContainSensitiveData(args))
        {
            return;
        }

        MessageBox.Show(
            "These launch arguments appear to contain a secret or credential. IdleLauncherTray stores launch arguments in the local config file in plain text.",
            AppPaths.AppName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void LogConfigurationSummary()
    {
        Logger.Info(
            $"Configuration loaded. PortableMode=true AppPath='{_cfg.AppPath}' IdleMinutes={_cfg.IdleMinutes} CpuThresholdPercent={_cfg.CpuThresholdPercent} RunAtStartup={_cfg.RunAtStartup} BlockInjectedWhileRunning={_cfg.BlockInjectedWhileRunning} LockPcOnAppClose={_cfg.LockPcOnAppClose} GamepadCountsAsActivity={_cfg.GamepadCountsAsActivity} UseSystemIdleFailSafe={_cfg.UseSystemIdleFailSafe} SystemIdleFailSafeWindowMs={_cfg.SystemIdleFailSafeWindowMs} TrayIconEnabled={_cfg.TrayIconEnabled} TrayIconPath='{_cfg.TrayIconPath}'.");
    }

    private static void LogHookStatus()
    {
        if (PhysicalIdle.KeyboardHookInstalled && PhysicalIdle.MouseHookInstalled)
        {
            Logger.Info("Physical idle hooks installed successfully (keyboard and mouse).");
            return;
        }

        Logger.Warn(
            $"Physical idle hook installation is degraded. KeyboardHookInstalled={PhysicalIdle.KeyboardHookInstalled} KeyboardHookError={PhysicalIdle.LastKeyboardHookError} MouseHookInstalled={PhysicalIdle.MouseHookInstalled} MouseHookError={PhysicalIdle.LastMouseHookError} UseSystemIdleFailSafe={PhysicalIdle.UseSystemIdleFailSafe}.");
    }

    private static string DescribeProcess(Process? process)
    {
        if (process == null)
        {
            return "(none)";
        }

        var pid = "unknown";
        var name = string.Empty;

        try
        {
            pid = process.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            // Ignore.
        }

        try
        {
            name = process.ProcessName;
        }
        catch
        {
            // Ignore.
        }

        return string.IsNullOrWhiteSpace(name)
            ? $"pid={pid}"
            : $"pid={pid} name='{name}'";
    }

    private void ShutdownForExit()
    {
        // Use Interlocked so this is safe even if called from multiple threads or races with OnTick.
        if (Interlocked.Exchange(ref _shutDownSerialized, 1) != 0) return;
        _shutDown = true;

        Logger.Info("Shutting down tray application.");

        try { _timer.Stop(); } catch { /* ignore */ }
        try { _timer.Dispose(); } catch { /* ignore */ }
        try { PhysicalIdle.Stop(); } catch { /* ignore */ }
        try { SetInjectedSuppression(false, "application shutdown"); } catch { /* ignore */ }

        try
        {
            _runningProcess?.Dispose();
            _runningProcess = null;
            _trackedProcessWasIdleLaunch = false;
        }
        catch { /* ignore */ }

        try
        {
            _notify.Visible = false;
            _notify.Dispose();
        }
        catch { /* ignore */ }

        try { _trayIconObj?.Dispose(); } catch { /* ignore */ }
        _trayIconObj = null;

        try { _menu.Dispose(); } catch { /* ignore */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ShutdownForExit();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Ensures hooks/timers/icon resources are released even if the app exits without going through our menu.
    /// </summary>
    protected override void ExitThreadCore()
    {
        ShutdownForExit();
        base.ExitThreadCore();
    }
}
