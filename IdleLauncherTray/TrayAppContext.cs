// System, System.Collections.Generic, System.Drawing, System.IO and System.Windows.Forms are
// supplied by ImplicitUsings + UseWindowsForms in the csproj, so they are not repeated here.
// Only these two need declaring. (IDE0005 is not enabled at build, so the analyzers do not
// report the redundant ones.)
using System.Diagnostics;
using System.Globalization;

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
    private const int BalloonTipTitleMaxLength = 63;
    private const int BalloonTipTextMaxLength = 255;
    private const int TransientLaunchRetryCount = 1;
    private const int TransientLaunchRetryDelayMs = 250;

    private static readonly int[] IdleTimerOptionMinutes = { 1, 3, 5, 10, 15, 20, 30 };
    private static readonly int[] CpuThresholdOptionPercents = { 10, 20, 30, 40, 50 };

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

    // Consecutive failures of the tracked-process state query. Bounded so one bad handle cannot
    // latch the launcher off for the life of the process (see UpdateTrackedProcessState).
    private int _consecutiveProcessQueryFailures;
    private const int MaxConsecutiveProcessQueryFailures = 3;

    // Cached existence of the configured target. File.Exists ran on the UI thread on every tick;
    // against an unreachable UNC path (a documented supported target) it blocks for the SMB
    // timeout and freezes the message pump, so the tray menu itself stops responding.
    private string _targetExistsCachedPath = string.Empty;
    private bool _targetExistsCachedResult;
    private long _targetExistsCheckedAtMs = long.MinValue;
    private const int TargetExistsCacheTtlMs = 30_000;

    // Mutable record (init-style construction via object initializer, then field
    // updates as the evaluation progresses). Record gives us auto-equality and
    // ToString() for free; we keep the explicit Describe() for human-readable logs.
    private sealed record LaunchEvaluation
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

        // Whether idle was actually SAMPLED this evaluation. The early-return paths (no target,
        // unsupported, missing) leave InputIdleOk at its initialiser `false`, which is
        // indistinguishable from a measured "the user is active" -- and the re-arm logic treated
        // it as exactly that, re-arming with no measurement and then logging that fresh user
        // activity had been observed. Never infer activity from InputIdleOk alone.
        public bool IdleMeasured { get; set; }
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
        // Wrap the entire initialisation in try/catch so that, if any step throws
        // (corrupt config, hooks rejected, NotifyIcon creation fails, etc.), we
        // tear down anything that's already been allocated instead of leaving
        // tray icons / menus / hooks / processes dangling. ShutdownForExit is
        // idempotent and null-safe via its per-step try/catch blocks, so it's
        // safe to call here regardless of how far construction progressed.
        try
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

        // Publish immediately after the icon exists so the fatal handlers in Program.cs can clear
        // it. Single-instance is enforced by a named mutex in Main, so there is only ever one.
        _live = this;

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

        foreach (var minutes in IdleTimerOptionMinutes)
        {
            var item = new ToolStripMenuItem($"{minutes} minute{(minutes == 1 ? string.Empty : "s")}")
            {
                Tag = minutes,
                Checked = _cfg.IdleMinutes == minutes
            };

            item.Click += (_, _) => RunMenuAction("Set idle timer", () =>
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
            });

            _idleTimerItems.Add(item);
            miIdle.DropDownItems.Add(item);
        }

        _menu.Items.Add(miIdle);

        // CPU Threshold submenu
        var miCpu = new ToolStripMenuItem("CPU Threshold");

        foreach (var pct in CpuThresholdOptionPercents)
        {
            var item = new ToolStripMenuItem($"{pct}%")
            {
                Tag = pct,
                Checked = _cfg.CpuThresholdPercent == pct,
                ToolTipText = "Only launch when total CPU usage is at or below this threshold."
            };

            item.Click += (_, _) => RunMenuAction("Set CPU threshold", () =>
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
            });

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

        miChoose.Click += (_, _) => RunMenuAction("Choose target application", () =>
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
        });

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

        _miBlockInjected.Click += (_, _) => RunMenuAction("Toggle 'Block injected input'", () =>
        {
            _cfg.BlockInjectedWhileRunning = _miBlockInjected.Checked;
            ConfigManager.Save(_cfg);
            SetInjectedSuppression(
                _cfg.BlockInjectedWhileRunning && UpdateTrackedProcessState(logStateChange: false),
                _cfg.BlockInjectedWhileRunning
                    ? "blocking while running was enabled"
                    : "blocking while running was disabled");
            Logger.Info($"Block injected input while running set to {_cfg.BlockInjectedWhileRunning}.");
        });

        miOptions.DropDownItems.Add(_miBlockInjected);

        _miLockPcOnAppClose = new ToolStripMenuItem("Lock PC on App Close")
        {
            CheckOnClick = true,
            Checked = _cfg.LockPcOnAppClose,
            ToolTipText =
                "When enabled, Windows will lock after an application or screensaver that was launched automatically by idle detection closes. Manual Run Now launches do not trigger this."
        };

        _miLockPcOnAppClose.Click += (_, _) => RunMenuAction("Toggle 'Lock PC on App Close'", () =>
        {
            _cfg.LockPcOnAppClose = _miLockPcOnAppClose.Checked;
            ConfigManager.Save(_cfg);

            // allowWorkstationLock: false -- see UpdateTrackedProcessState. Without it, ticking
            // this checkbox locks the workstation immediately whenever the tracked idle-launched
            // process exited within the last tick.
            var running = UpdateTrackedProcessState(logStateChange: false, allowWorkstationLock: false);
            if (running && _trackedProcessWasIdleLaunch)
            {
                Logger.Info(
                    $"Lock PC on App Close set to {_cfg.LockPcOnAppClose}. The currently tracked process was launched automatically from idle, so workstation lock on close is now {(_cfg.LockPcOnAppClose ? "enabled" : "disabled")} for this run.");
            }
            else
            {
                Logger.Info($"Lock PC on App Close set to {_cfg.LockPcOnAppClose}.");
            }
        });

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

        _miTrayIconEnabled.Click += (_, _) => RunMenuAction("Toggle 'Use custom tray icon'", () =>
        {
            _cfg.TrayIconEnabled = _miTrayIconEnabled.Checked;
            ConfigManager.Save(_cfg);
            Logger.Info($"Use custom tray icon set to {_cfg.TrayIconEnabled}.");
            ApplyTrayIcon();
        });

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

        miRunNow.Click += (_, _) => RunMenuAction("Run Now", () =>
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

            if (TryLaunchSelectedApp("manual Run Now", launchedFromIdle: false, showErrorDialog: true, out _, out _))
            {
                DisarmAfterLaunch("manual Run Now");
            }
        });

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
        catch (Exception ex)
        {
            try { Logger.Error("TrayAppContext construction failed; cleaning up partial state.", ex); }
            catch { /* logger may itself be impaired */ }

            ShutdownForExit();
            throw;
        }
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

    // Always clear NotifyIcon.Icon first so the tray never holds a reference to a
    // disposed Icon object — otherwise a tray repaint between Dispose() and the new
    // assignment can hit ObjectDisposedException.
    private void DetachAndDisposeCurrentTrayIcon()
    {
        try
        {
            if (_notify != null) _notify.Icon = null;
        }
        catch
        {
            // Ignore — best effort.
        }

        try
        {
            _trayIconObj?.Dispose();
        }
        catch
        {
            // Ignore disposal failures.
        }

        _trayIconObj = null;
    }

    private void ApplyTrayIcon()
    {
        DetachAndDisposeCurrentTrayIcon();

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

    // Centralised exception handling for tray menu Click handlers. Any handler that
    // mutates settings / writes config / touches the registry / spawns a process
    // should run through this so an unexpected exception logs cleanly and surfaces
    // a single MessageBox instead of escaping to the WinForms message pump.
    private static void RunMenuAction(string actionDescription, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Error($"Menu action failed: {actionDescription}.", ex);
            try
            {
                MessageBox.Show(
                    $"{actionDescription} failed:\n{ex.Message}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // If even the MessageBox blows up, we've at least logged the
                // original exception — don't let the dialog failure mask it.
            }
        }
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
                    if (TryLaunchWithTransientRetry("automatic idle trigger", out var failureMessage, out var launchAlreadyInProgress))
                    {
                        DisarmAfterLaunch("automatic idle trigger");
                    }
                    else if (!launchAlreadyInProgress)
                    {
                        HandleAutomaticLaunchFailure("automatic idle trigger", evaluation.TargetPath, failureMessage);
                    }

                    // else: a launch is already in flight under a nested pump. Skip this tick
                    // silently and leave the launcher armed -- the in-flight launch will disarm
                    // it on success.
                }

                return;
            }

            // `evaluation.IdleMeasured &&` is load-bearing: without it the three early-return
            // paths (no target / unsupported / missing) satisfy `!InputIdleOk` by never having
            // computed it, so a target on a flaky share re-armed the launcher every other tick
            // while the user was away, and logged that fresh user activity had been observed.
            if (!_armed && evaluation.IdleMeasured && !evaluation.InputIdleOk)
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
            // No Math.Max clamps here. ConfigManager.NormalizeInPlace runs on BOTH Load and
            // Save and is the single enforcement point for these bounds (IdleMinutes >= 1, CPU
            // threshold snapped to the allowed steps); the menu handlers only ever assign values
            // from those same fixed sets. Clamping again here could not change any reachable
            // value and disguised where the invariant is actually kept.
            RequiredIdleSeconds = _cfg.IdleMinutes * 60,
            CpuThresholdPercent = _cfg.CpuThresholdPercent,
            InputIdleOk = false,
            CpuOk = false
        };

        // Sample idle and CPU FIRST, before any early return.
        //
        // CpuUsageMonitor is a DELTA sampler: each reading covers the span since the previous
        // call. Sampling only on the all-checks-passed path meant that after a four-hour target
        // run, or hours with a target on an unreachable share, the next "CPU usage" reading was a
        // four-hour average rather than a five-second one. A multi-hour average sits under the
        // 10-50% gate almost always, so the CPU guard quietly stopped guarding at exactly the
        // moment it mattered: the first evaluation after a gap.
        //
        // Measuring idle here too is what lets the re-arm logic below tell "the user is active"
        // apart from "we never looked".
        evaluation.IdleSeconds = GetIdleSeconds();
        evaluation.IdleMeasured = true;
        evaluation.CpuSampleValid = _cpu.TryNextValue(out var cpuPercent);
        evaluation.CpuPercent = evaluation.CpuSampleValid ? cpuPercent : 0;
        evaluation.InputIdleOk = evaluation.IdleSeconds >= evaluation.RequiredIdleSeconds;
        evaluation.CpuOk = evaluation.CpuSampleValid && evaluation.CpuPercent <= evaluation.CpuThresholdPercent;

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

        evaluation.TargetExists = TargetExistsCached(targetPath);
        if (!evaluation.TargetExists)
        {
            evaluation.ReasonCode = "SelectedTargetMissing";
            return evaluation;
        }

        if (_lastLaunchUtc.HasValue)
        {
            var delta = (DateTime.UtcNow - _lastLaunchUtc.Value).TotalSeconds;

            // A NEGATIVE delta means the wall clock moved backwards relative to the stored
            // timestamp: an NTP correction, a VM snapshot restore, a dead CMOS battery, or a
            // hand-edited LastLaunchUtc missing its 'Z' (which ToUniversalTime() then shifts
            // forward by the local offset). Without this clamp the cooldown stayed active for the
            // full magnitude of the jump -- hours or years -- and because _lastLaunchUtc is
            // persisted to config.json it SURVIVED RESTART, with nothing in the tray to show why
            // the app had stopped launching. Re-baseline and carry on.
            if (delta < 0)
            {
                Logger.Warn(
                    $"Launch cooldown timestamp is in the future by {(-delta):F0}s (clock change or edited config). Re-baselining to now.");
                _lastLaunchUtc = DateTime.UtcNow;
                delta = 0;
            }

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

    // Existence of the target, cached for TargetExistsCacheTtlMs.
    //
    // The uncached call sat on the UI thread on every 5s tick: 120,960 File.Exists calls per week.
    // For a local path that is cheap, but the README advertises UNC targets, and File.Exists
    // against an unreachable SMB host blocks for the connection timeout with the message pump
    // held -- the tray menu stops opening and the app appears hung.
    //
    // The cache is keyed on the path, so choosing a different target invalidates it immediately
    // rather than waiting out the TTL. A 30s staleness window is harmless here: the worst case is
    // one tick that evaluates against a target which appeared or vanished moments ago, and the
    // launch itself still fails safely if the file is gone.
    private bool TargetExistsCached(string targetPath)
    {
        var nowMs = Environment.TickCount64;

        if (_targetExistsCheckedAtMs != long.MinValue
            && string.Equals(_targetExistsCachedPath, targetPath, StringComparison.OrdinalIgnoreCase)
            && nowMs - _targetExistsCheckedAtMs < TargetExistsCacheTtlMs)
        {
            return _targetExistsCachedResult;
        }

        bool exists;
        try
        {
            exists = File.Exists(targetPath);
        }
        catch (Exception ex)
        {
            // File.Exists swallows most errors, but a malformed or unreachable path can still
            // throw. Treat it as missing rather than letting the tick die.
            Logger.Warn($"Failed to probe target existence. Path='{targetPath}' Error='{ex.Message}'.");
            exists = false;
        }

        _targetExistsCachedPath = targetPath;
        _targetExistsCachedResult = exists;
        _targetExistsCheckedAtMs = nowMs;
        return exists;
    }

    // Retry wrapper for automatic idle-triggered launches. A transient failure -- an antivirus
    // scanner briefly holding the target's file handle, say -- should not disarm the launcher and
    // force the user to wiggle the mouse.
    //
    // NOTE the honest limitation: TryLaunchSelectedApp returns false for EVERY failure, including
    // permanent ones (no target, unsupported type, file not found). This wrapper therefore retries
    // those too and pays one extra UI-thread sleep for them. The earlier comment here claimed the
    // retry fired "only on genuinely transient errors (IO / unauthorized access)"; no such
    // classification existed then or now. Classifying the exception inside the launch path is
    // recorded in BACKLOG.md.
    //
    // What IS now distinguished is the re-entrancy rejection, via alreadyInProgress -- see below.
    private bool TryLaunchWithTransientRetry(string trigger, out string failureMessage, out bool alreadyInProgress)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (TryLaunchSelectedApp(trigger, launchedFromIdle: true, showErrorDialog: false, out failureMessage, out alreadyInProgress))
            {
                return true;
            }

            // A launch is already running under a nested message pump (a modal dialog, or
            // ShellExecuteEx pumping). Retrying cannot help and the caller must not treat it as a
            // failure: doing so raised a false "Automatic launch failed" balloon and disarmed the
            // launcher for a launch that was actually in flight and about to succeed.
            if (alreadyInProgress)
            {
                return false;
            }

            if (attempt >= TransientLaunchRetryCount)
            {
                return false;
            }

            Logger.Warn($"Transient launch failure on attempt {attempt + 1}. Retrying after {TransientLaunchRetryDelayMs}ms. Trigger='{trigger}' Failure='{failureMessage}'.");
            try { Thread.Sleep(TransientLaunchRetryDelayMs); } catch { /* ignore */ }
        }
    }

    private bool TryLaunchSelectedApp(string trigger, bool launchedFromIdle, bool showErrorDialog, out string failureMessage, out bool alreadyInProgress)
    {
        failureMessage = string.Empty;
        alreadyInProgress = false;

        // Guard against concurrent launches (e.g., rapid Run Now clicks or overlapping idle triggers).
        if (Interlocked.Exchange(ref _launchingSerialized, 1) != 0)
        {
            alreadyInProgress = true;
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
            var safeTitle = TruncateForBalloonTip(title, BalloonTipTitleMaxLength);
            var safeText = TruncateForBalloonTip(text, BalloonTipTextMaxLength);
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

    // allowWorkstationLock exists because discovering an exit here has a SIDE EFFECT: it can
    // lock the workstation. That is right from the timer tick, and wrong from a menu handler --
    // enabling "Lock PC on App Close" used to lock the screen on the spot if the tracked
    // idle-launched target happened to have exited since the last tick. A checkbox should not
    // lock your machine.
    private bool UpdateTrackedProcessState(bool logStateChange, bool allowWorkstationLock = true)
    {
        if (_runningProcess == null)
        {
            _trackedProcessWasIdleLaunch = false;
            return false;
        }

        // Captured EAGERLY, on purpose. This value is still used further down by
        // MaybeLockWorkstationAfterTrackedProcessExit, which runs after _runningProcess.Dispose(),
        // and reading a disposed Process's properties throws. Deferring this to a lazy closure
        // would save two allocations per tick and buy an exception on the exit path.
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
            // Treat as not exited and retry -- but NOT forever. Returning "still running"
            // unconditionally meant one persistently throwing handle made OnTick return early on
            // every tick for the life of the process: readiness never re-evaluated, the handle
            // never disposed, and injected-input suppression latched ON system-wide, with nothing
            // in the UI to show it. Give up after a few consecutive failures and fall through to
            // the normal exit path so the app recovers.
            _consecutiveProcessQueryFailures++;
            if (_consecutiveProcessQueryFailures < MaxConsecutiveProcessQueryFailures)
            {
                if (logStateChange)
                {
                    Logger.Warn(
                        $"Failed to query tracked process state (attempt {_consecutiveProcessQueryFailures}/{MaxConsecutiveProcessQueryFailures}). Process={processDescription} Error='{ex.Message}'.");
                }

                return true;
            }

            Logger.Error(
                $"Giving up on the tracked process after {_consecutiveProcessQueryFailures} failed state queries; treating it as exited so the launcher can recover. Process={processDescription}.",
                ex);
            exited = true;
        }

        _consecutiveProcessQueryFailures = 0;

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

        if (allowWorkstationLock)
        {
            MaybeLockWorkstationAfterTrackedProcessExit(processDescription);
        }

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

    // Set once the tray icon exists, so the process-wide fatal handlers in Program.cs can clear
    // it before Environment.Exit. Without this a crash in any menu handler leaves a ghost icon in
    // the notification area until the user happens to hover over it.
    private static TrayAppContext? _live;

    // Best-effort, callable from a fatal exception handler on any thread. Deliberately does the
    // minimum -- just hide the icon -- because the process is about to die and a full
    // ShutdownForExit could itself throw from the very state that is already broken.
    internal static void EmergencyHideTrayIcon()
    {
        try
        {
            var live = _live;
            if (live?._notify is { } icon)
            {
                icon.Visible = false;
            }
        }
        catch
        {
            // Nothing useful left to do; we are on the way out.
        }
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
