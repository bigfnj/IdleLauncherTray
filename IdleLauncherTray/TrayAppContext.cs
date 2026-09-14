// System, System.Collections.Generic, System.Drawing, System.IO and System.Windows.Forms are
// supplied by ImplicitUsings + UseWindowsForms in the csproj, so they are not repeated here.
// Only these need declaring. (IDE0005 is not enabled at build, so the analyzers do not
// report the redundant ones.)
//
// Microsoft.Win32 is reachable transitively through the Windows Desktop framework reference, so
// SystemEvents costs no PackageReference.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

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

    // Win32 status codes that describe a resource which is momentarily unavailable rather than a
    // settled answer. See IsRetryableWin32Error.
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorNetnameDeleted = 64;
    private const int ErrorNoSystemResources = 1450;

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

    // Workstation lock state, maintained by OnSessionSwitch.
    //
    // `volatile` and nothing heavier is the right tool here: it is written ONLY on the SystemEvents
    // notification thread and read ONLY on the UI thread, one field, no compound invariant to hold
    // across the two. There is no read-modify-write to make atomic and no second field that has to
    // agree with it, so a lock would add a lock-ordering hazard between the UI thread and a thread
    // we do not own, and buy nothing. What volatile does buy is the guarantee the code actually
    // needs: the UI thread must not read a cached copy and keep launching into a locked desktop.
    private volatile bool _workstationLocked;

    // False when the SessionSwitch subscription failed. Lock detection is then off, which the
    // tooltip reports as a degradation rather than letting the gate quietly not enforce anything.
    private bool _sessionSwitchSubscribed;

    // Type+message of the tick failure currently being suppressed, or null when the tick is
    // healthy. UI thread only.
    private string? _lastTickFailureSignature;

    // Menu items we need to update dynamically
    private readonly ToolStripMenuItem _miStartup;
    private readonly ToolStripMenuItem _miSelected;
    private readonly ToolStripMenuItem _miArguments;
    private readonly ToolStripMenuItem _miBlockInjected;
    private readonly ToolStripMenuItem _miLockPcOnAppClose;
    private readonly ToolStripMenuItem _miAllowLaunchWhileLocked;
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

    // Last text successfully applied to NotifyIcon.Text, seeded with the value the NotifyIcon is
    // constructed with so the first genuine status change is the first syscall.
    private string _lastTrayStatusText = string.Empty;
    private bool _trayStatusFailureLogged;

    // Degradation notification state. The reason STRING is remembered rather than a bool: see
    // UpdateDegradationNotification.
    private string? _lastDegradationReason;
    // When each DISTINCT reason was last ballooned. Deliberately not cleared on recovery: an
    // A -> clear -> A cycle every six seconds would otherwise balloon every six seconds, which is
    // the flap this interval exists to stop wearing a different hat. A reason that returns after
    // its own window has expired balloons again, which is what anyone would expect.
    //
    // Bounded by construction -- ComposeDegradationReason can only return one of a handful of
    // compile-time literals -- but the cap is defence in depth against a future caller that
    // interpolates a reason. This process runs for months, and an unbounded dictionary keyed on a
    // string is how that becomes a leak.
    private readonly Dictionary<string, long> _degradationNotifiedAtMs = new(StringComparer.Ordinal);
    private const int MaxTrackedDegradationReasons = 8;
    private const int DegradationBalloonMinIntervalMs = 5 * 60 * 1000;

    // Consecutive unusable CPU samples. A stuck sampler is invisible otherwise: CpuOk is simply
    // false forever, which reads as a condition that is merely not met yet. One minute of dead
    // samples at the 5s cadence is twelve.
    private int _consecutiveInvalidCpuSamples;
    private const int MaxConsecutiveInvalidCpuSamples = 12;

    // Re-entrancy latch for the tick. See OnTick: Process.Start pumps messages, so a WM_TIMER can
    // re-enter the tick while the outer call is still inside the launch path.
    private int _tickInProgress;


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
                // PrepareForStorage, because this value is about to be WRITTEN to config.json.
                // A file dialog cannot hand back a "%VAR%" path, so the two calls agree here
                // today; naming the storage form anyway is what stops the next person restoring
                // the expand-then-persist behaviour that baked a portable config to one machine.
                var selectedPath = TargetFilePolicy.PrepareForStorage(dlg.FileName);
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
                // allowWorkstationLock: false, for the same reason the Lock-PC checkbox passes it.
                // UpdateTrackedProcessState locks the workstation when it DISCOVERS a tracked exit,
                // so without this, ticking this box within the five seconds before the tick reaps
                // an idle-launched target that has just closed locks the machine from inside a
                // checkbox handler. A checkbox should not lock your PC.
                _cfg.BlockInjectedWhileRunning
                    && UpdateTrackedProcessState(logStateChange: false, allowWorkstationLock: false),
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

        _miAllowLaunchWhileLocked = new ToolStripMenuItem("Allow launching while the PC is locked")
        {
            CheckOnClick = true,
            Checked = _cfg.AllowLaunchWhileLocked,
            ToolTipText =
                "When enabled, the idle trigger may launch the target while the workstation is locked. Off by default, because a target started behind the lock screen is invisible until you sign back in."
        };

        _miAllowLaunchWhileLocked.Click += (_, _) => RunMenuAction("Toggle 'Allow launching while the PC is locked'", () =>
        {
            _cfg.AllowLaunchWhileLocked = _miAllowLaunchWhileLocked.Checked;
            ConfigManager.Save(_cfg);

            // The readiness snapshot is keyed on the evaluated state, and this toggle changes how
            // the very same session state evaluates. Without the invalidation the next tick would
            // compare equal to the pre-toggle key and log nothing at all.
            InvalidateReadinessSnapshot();
            Logger.Info($"Allow launching while the PC is locked set to {_cfg.AllowLaunchWhileLocked}.");
        });

        miOptions.DropDownItems.Add(_miAllowLaunchWhileLocked);
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

            // ResolveForUse, not the stored value. The config now keeps the path exactly as the
            // user wrote it, environment variables and all, so File.Exists against the raw string
            // would report a perfectly good "%APPDATA%\tools\app.exe" as missing and refuse to run
            // it. The message still shows the stored spelling, because that is the one the user
            // typed and the one they would go and fix.
            var runNowPath = TargetFilePolicy.ResolveForUse(_cfg.AppPath);
            if (!File.Exists(runNowPath))
            {
                Logger.Warn(
                    $"Run Now aborted because the selected file does not exist. Stored='{_cfg.AppPath}' Resolved='{runNowPath}'.");

                MessageBox.Show(
                    $"Selected file not found:\n{_cfg.AppPath}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Run Now deliberately does NOT disarm, and used to. Disarming here switched automatic
            // launching off until fresh user activity -- so clicking Run Now and then walking away
            // was the worst possible sequence: when the target exited, InputIdleOk was still true,
            // the re-arm branch in OnTick never ran, and the launcher stayed dead for the entire
            // away period, which is exactly the period it exists for.
            //
            // Nothing is lost by leaving it armed. A second launch on the same tick is already
            // blocked by the tracked-process handle and by the 10s cooldown, and the automatic
            // path keeps its own disarm so a failing target still cannot loop.
            if (!TryLaunchSelectedApp(
                    "manual Run Now",
                    launchedFromIdle: false,
                    showErrorDialog: true,
                    out _,
                    out var alreadyInProgress,
                    out _)
                && alreadyInProgress)
            {
                // The one failure path that does NOT honour showErrorDialog: it returns before
                // every MessageBox in the launch method. Without this, clicking Run Now while an
                // automatic launch is still inside ShellExecuteEx -- which pumps messages, so the
                // menu still opens and still dispatches this click -- did nothing at all. No
                // launch, no dialog, no tooltip change, one Warn in a log nobody is reading. A
                // menu item that silently does nothing is this app's oldest failure shape with a
                // mouse attached.
                MessageBox.Show(
                    "A launch is already in progress. Wait for it to finish and try again.",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
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

        // Session-lock detection. Subscribing can throw (a broken SystemEvents window pump, a
        // session with no desktop), and when it does the app is still perfectly useful -- so this
        // must NOT fail construction. The flag instead feeds ComposeDegradationReason, which puts
        // "lock detection off" in the tooltip: the failure of a guard is reported rather than
        // leaving a guard that silently enforces nothing.
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _sessionSwitchSubscribed = true;
            Logger.Info("Subscribed to session switch notifications; automatic launching will be blocked while the workstation is locked.");
        }
        catch (Exception ex)
        {
            _sessionSwitchSubscribed = false;
            Logger.Warn(
                $"Failed to subscribe to session switch notifications. Lock detection is off and the tray tooltip will report the degradation. Error='{ex.Message}'.");
        }

        // Resume from sleep or hibernate has the same shape as an unlock: the machine was not
        // running our hooks while it was suspended, and whatever the user did to wake it never
        // reached WH_KEYBOARD_LL. Without this, the idle clock carries the entire suspend across
        // the resume -- so a machine that slept overnight is "idle for nine hours" the instant it
        // wakes and launches the target before the user has touched anything. It also clears the
        // hook-drop suspicion the gap would otherwise create.
        //
        // Best-effort and separate from the block above: losing resume handling is a smaller
        // failure than losing lock detection, and it should not flip the tooltip to degraded.
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to subscribe to power mode notifications; resume-from-sleep will not reset the idle clock. Error='{ex.Message}'.");
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

    // RUNS ON THE SystemEvents DEDICATED NOTIFICATION THREAD, NOT THE UI THREAD.
    //
    // Everything touched here has to be safe from an arbitrary thread: the volatile bool below,
    // Logger (a single global lock around a synchronous append) and PhysicalIdle (Interlocked /
    // volatile state throughout). NO WinForms object may be touched -- _notify, _menu and _timer
    // all belong to the thread that created them, and the failure mode of getting that wrong is
    // an intermittent crash on a machine nobody is watching, because it happens at the lock
    // screen by definition.
    //
    // The whole body is wrapped because an exception escaping here does not merely get logged:
    // Program.cs's AppDomain.UnhandledException handler logs and hides the tray icon, but it
    // cannot cancel the termination -- the CLR still kills the process. A session switch, which
    // is a routine event on any machine with a lock timeout, must not be able to do that.
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        try
        {
            switch (e.Reason)
            {
                // Locked: our low-level hooks no longer see the user's input, so every idle
                // measurement from here on is "the user has been idle for the whole lock".
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect:
                    // Excuse hook silence BEFORE raising our own flag, so no tick can observe
                    // "locked" while the drop detector is still treating silence as evidence.
                    // Without this an ordinary lock produces a false "input hooks stopped
                    // firing": secure-desktop input never reaches our hooks but does keep
                    // refreshing GetLastInputInfo, so touching the lock screen jumps the gap
                    // between the two clocks to the full lock duration.
                    PhysicalIdle.SetHookSilenceExpected(true);
                    _workstationLocked = true;
                    Logger.Info($"Session became unavailable; automatic launching is now blocked. Reason={e.Reason}.");
                    break;

                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteConnect:
                    // THE ORDER BELOW IS LOAD-BEARING: the idle clock must be reset BEFORE
                    // _workstationLocked is cleared, never after. The password typed on the secure
                    // desktop never reaches WH_KEYBOARD_LL, so at this instant the hooks still
                    // report the entire lock duration as idle time. A tick landing in the gap
                    // between a cleared flag and a not-yet-reset clock would see "unlocked and
                    // hours idle" and launch the target one tick after the user signed back in.
                    //
                    // This also clears the hook-drop suspicion the lock necessarily created:
                    // to the drop detector, "GetLastInputInfo advanced but no callback fired"
                    // is exactly what a secure-desktop sign-in looks like, and without this the
                    // app would report a degraded hook after every unlock.
                    PhysicalIdle.NotifyExternalActivity($"session became available ({e.Reason})");
                    _workstationLocked = false;

                    // Stop excusing hook silence only AFTER the clock and the flag are correct.
                    // Reversed, a tick could land while silence was still excused but the
                    // launcher was already unblocked, and a genuinely dead hook would go
                    // unreported for that window.
                    PhysicalIdle.SetHookSilenceExpected(false);
                    Logger.Info($"Session became available; automatic launching is allowed again. Reason={e.Reason}.");
                    break;

                default:
                    // Deliberately no state change for SessionLogon / SessionLogoff: those fire
                    // for OTHER sessions as well (fast user switching), so reacting to them would
                    // let a second user's logon clear a flag that describes OUR session. And
                    // SessionRemoteControl says nothing about whether input reaches our hooks.
                    Logger.Info(
                        $"Session switch ignored because it does not change input availability for this session. Reason={e.Reason}.");
                    break;
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Warn($"Failed to handle a session switch notification. Reason={e?.Reason} Error='{ex.Message}'.");
            }
            catch
            {
                // The logger is the last thing left and it is failing too. Swallowing is the
                // whole point of this handler: see the note above about process termination.
            }
        }
    }

    // Raised on the SystemEvents notification thread, exactly like OnSessionSwitch, and bound by
    // the same rules: no WinForms object, and nothing may be allowed to escape, because an
    // unhandled exception on a non-UI thread terminates the process.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e?.Mode != PowerModes.Resume)
            {
                // Suspend and StatusChange say nothing about input reaching our hooks. Notably we
                // do NOT reset on Suspend: the machine is about to stop running our timer anyway,
                // and claiming activity at that moment would be a claim we cannot support.
                return;
            }

            PhysicalIdle.NotifyExternalActivity("resumed from sleep or hibernate");
            Logger.Info("Resumed from sleep; idle clock reset so the suspended time does not count as idle.");
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Warn($"Failed to handle a power mode notification. Error='{ex.Message}'.");
            }
            catch
            {
                // Same reasoning as the session handler above.
            }
        }
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
        // Reject a re-entrant tick outright.
        //
        // Process.Start with UseShellExecute = true calls ShellExecuteEx, which PUMPS MESSAGES, so
        // a WM_TIMER can be dispatched and re-enter this method while the outer call is still
        // inside the launch path. That was already survivable -- the nested tick hits
        // _launchingSerialized and skips the launch -- but it is not survivable now that the CPU
        // delta sampler is advanced at the top: two samples inside one timer period halve the
        // window the guard is measured over and hand the outer tick a sub-second reading.
        //
        // Re-entry is always on the SAME thread (a nested pump), so a plain bool would do; the
        // Interlocked matches _launchingSerialized and _shutDownSerialized and costs nothing.
        // Suppressed ticks cannot pile up: WM_TIMER is synthesised on demand, not queued.
        if (Interlocked.Exchange(ref _tickInProgress, 1) != 0)
        {
            return;
        }

        // `finally` rather than a statement at the end of the try: two of the three exits from
        // this method are early returns, and a finally still runs for those.
        var tickThrew = false;
        try
        {
            // Sample the CPU FIRST -- above the running-target early return, and above
            // TryRepairHooksIfNeeded too.
            //
            // CpuUsageMonitor is a DELTA sampler: every reading covers the span since the previous
            // call, so the only thing keeping the window at five seconds is being called every five
            // seconds. A tick that skips the sample does not miss one, it silently WIDENS the next.
            // v2.5.0 fixed exactly this one level down, by hoisting the sample above the early
            // returns inside EvaluateLaunchReadiness -- but `if (running) return;` is outside that
            // method, so for the entire run of a launched target the sampler was never advanced.
            // After a four-hour screensaver the next reading was a four-hour average, which sits
            // under any threshold, so the CPU guard stopped guarding at precisely the tick that
            // decides whether to relaunch.
            //
            // Above TryRepairHooksIfNeeded specifically because that drains deferred hook logs and
            // can therefore throw out of Logger. A failing-tick episode must still advance the
            // sampler, or it reproduces the very gap this move exists to close.
            //
            // The invalid-sample counter moves with it for the same reason: left below the return,
            // a sampler that died during a long run stayed invisible until the target exited, and
            // one that recovered during a run kept reporting stuck.
            var cpuSampleValid = _cpu.TryNextValue(out var cpuPercent);
            var previousInvalidCpuSamples = _consecutiveInvalidCpuSamples;
            _consecutiveInvalidCpuSamples = NextConsecutiveInvalidCpuSamples(
                _consecutiveInvalidCpuSamples, cpuSampleValid, MaxConsecutiveInvalidCpuSamples);

            // Say WHY, exactly once per episode of failures.
            //
            // The tooltip reports THAT sampling is stuck; only this can report the Win32 error
            // behind it, and a stuck sampler means the app never launches its target at all. The
            // counter saturates at the cap, so this pair of conditions is true only on the tick
            // that crosses it -- which is what keeps a permanently broken sampler from writing
            // twelve identical lines a minute forever, and is why CpuUsageMonitor records the
            // error instead of logging it from inside the sampler.
            if (_consecutiveInvalidCpuSamples == MaxConsecutiveInvalidCpuSamples
                && previousInvalidCpuSamples < MaxConsecutiveInvalidCpuSamples)
            {
                Logger.Warn(
                    $"CPU sampling has produced {MaxConsecutiveInvalidCpuSamples} unusable samples in a row ({MaxConsecutiveInvalidCpuSamples * CheckIntervalSeconds}s). {_cpu.DescribeLastSampleFailure()}");
            }

            PhysicalIdle.TryRepairHooksIfNeeded();

            var running = UpdateTrackedProcessState(logStateChange: true);

            SetInjectedSuppression(
                _cfg.BlockInjectedWhileRunning && running,
                running && _cfg.BlockInjectedWhileRunning
                    ? "tracked process is running and blocking is enabled"
                    : "the tracked process is not running or blocking is disabled");

            if (running)
            {
                // Tooltip call site 1 of 3. This early return is exactly why there are three: a
                // single update at the tail of the method would leave the tooltip frozen on the
                // last pre-launch status for the whole run of the target, which may be hours.
                var runningDegradation = ComposeDegradationReason();
                UpdateDegradationNotification(runningDegradation);
                SetTrayStatusText(TrayStatusText.ForRunningTarget(
                    runningDegradation,
                    TargetFileNameOrNull(TargetFilePolicy.NormalizePath(_cfg.AppPath)),
                    _cfg.CpuThresholdPercent,
                    AppInfo.VersionDisplay));

                return;
            }

            var evaluation = EvaluateLaunchReadiness(cpuSampleValid, cpuPercent);
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
            }

            // `else if`, not `if`. The Ready branch used to `return` here, which skipped the
            // re-arm cascade entirely; `else if` skips it identically, so this restructure is
            // behaviour-preserving. A plain `if` would NOT be: Ready implies InputIdleOk, so the
            // condition would evaluate false on a ready tick, the `else` below would run, and
            // _consecutiveNonIdleTicks would be reset on a tick that never used to touch it.
            //
            // `evaluation.IdleMeasured &&` is a belt-and-braces guard, and is currently always
            // true -- EvaluateLaunchReadiness sets it above every early return. Read that as the
            // reason to keep it, not to drop it: the v2.5.0 bug was that the three early-return
            // paths (no target / unsupported / missing) satisfied `!InputIdleOk` by never having
            // computed it, so a target on a flaky share re-armed the launcher every other tick
            // while the user was away and logged that fresh user activity had been observed. The
            // actual fix was hoisting the idle sample above those returns; this flag is what
            // makes re-introducing an early return above that sample fail closed instead of
            // silently restoring the bug.
            else if (!_armed && evaluation.IdleMeasured && !evaluation.InputIdleOk)
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

            // Tooltip call site 2 of 3, reached by every non-running tick -- including the ready
            // one, whose `return` was removed above precisely so it lands here. `_armed` is read
            // after the launch attempt on purpose: a tick that just launched is disarmed, and the
            // tooltip should say so rather than still advertising "Ready to launch".
            var degradation = ComposeDegradationReason();
            UpdateDegradationNotification(degradation);
            SetTrayStatusText(TrayStatusText.ForEvaluation(
                degradation,
                evaluation.ReasonCode,
                _armed,
                TargetFileNameOrNull(evaluation.TargetPath),
                evaluation.IdleSeconds,
                evaluation.RequiredIdleSeconds,
                evaluation.CpuPercent,
                evaluation.CpuThresholdPercent,
                AppInfo.VersionDisplay));
        }
        catch (Exception ex)
        {
            // Log the first failure of an episode with its stack trace, then go quiet until the
            // tick recovers. A permanently failing tick fires every 5 s, and logging each one
            // unconditionally writes ~17,000 entries a day into a 2 MB file that rotates -- which
            // would destroy the very stack trace being reported. The recovery is announced too,
            // so a reader can tell "it stopped failing" from "it stopped logging".
            tickThrew = true;
            var signature = ex.GetType().FullName + "|" + ex.Message;
            if (!string.Equals(_lastTickFailureSignature, signature, StringComparison.Ordinal))
            {
                _lastTickFailureSignature = signature;
                Logger.Error("Unhandled exception in monitor tick. Further identical failures will not be logged until it recovers.", ex);
            }

            // Tooltip call site 3 of 3. A tick that throws is the one failure the user has no
            // other way to notice: the launcher simply stops launching. Its own try/catch because
            // we are already inside a catch on the WinForms pump, where an escape becomes an
            // unhandled UI-thread exception and takes the process down via Program.cs.
            try
            {
                SetTrayStatusText(TrayStatusText.ForTickFailure(_cfg.CpuThresholdPercent, AppInfo.VersionDisplay));
            }
            catch
            {
                // The tick has already failed and the tooltip is cosmetic; the original error is
                // logged above, which is the part that matters.
            }
        }
        finally
        {
            // Released FIRST, before the recovery log below. If Logger.Info threw, a latch released
            // after it would stay set for the life of the process and the tick would never run
            // again -- a silent stop, which is the one failure shape this app keeps having.
            Interlocked.Exchange(ref _tickInProgress, 0);

            if (!tickThrew && _lastTickFailureSignature != null)
            {
                _lastTickFailureSignature = null;
                Logger.Info("Monitor tick recovered; readiness evaluation is running normally again.");
            }
        }
    }

    // Path.GetFileName over a target path, reduced to null when there is nothing worth showing.
    // The formatter treats null as "no name" and falls back to a generic body, so the tooltip
    // never renders "Target missing: " with an empty tail.
    private static string? TargetFileNameOrNull(string targetPath)
    {
        try
        {
            var fileName = Path.GetFileName(targetPath);
            return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        }
        catch
        {
            // A malformed path must not be able to break the tooltip; the status is still useful
            // without the file name.
            return null;
        }
    }

    // Applies text to the tray tooltip, skipping the writes that would change nothing.
    private void SetTrayStatusText(string text)
    {
        // Each assignment to NotifyIcon.Text is a Shell_NotifyIcon(NIM_MODIFY) call into the
        // shell. The status only moves on a state transition, so the early return removes twelve
        // syscalls a minute that all write the same string.
        if (string.Equals(_lastTrayStatusText, text, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _notify.Text = text;

            // Latched AFTER the assignment succeeds, never before. Recording a value the shell
            // rejected would make every later call compare equal and return early -- the tooltip
            // would freeze at whatever it last displayed, permanently, with no retry path.
            _lastTrayStatusText = text;
            _trayStatusFailureLogged = false;
        }
        catch (Exception ex)
        {
            // At most one warning per failure episode. The tick runs every five seconds, so an
            // unconditional log here would rotate the log file out from under the real diagnosis.
            if (!_trayStatusFailureLogged)
            {
                _trayStatusFailureLogged = true;
                Logger.Warn($"Failed to update the tray tooltip text. Text='{text}' Error='{ex.Message}'.");
            }
        }
    }

    // The one reason, if any, that this app is currently running in a degraded state -- ordered
    // most-dangerous-first, because the tooltip has room for exactly one. "Most dangerous" means
    // "most changes what the user should expect from the app", not "rarest".
    private string? ComposeDegradationReason()
    {
        // Hooks first: they are the measurement everything else is derived from. A dropped or
        // missing hook makes the idle number itself untrustworthy, which is strictly worse than
        // a gate that is merely not being enforced.
        //
        // One call, not a flag plus a string. Two reads could be observed from either side of a
        // tick and paint "DEGRADED - " with an empty reason, or a stale reason with no flag.
        string? hookReason;
        try
        {
            hookReason = PhysicalIdle.GetHookDegradationReason();
        }
        catch (Exception ex)
        {
            // A diagnostic that throws is itself a degradation, and silently treating it as
            // "healthy" would be the exact failure this whole feature exists to end.
            Logger.Warn($"Failed to read the hook degradation reason. Error='{ex.Message}'.");
            return "hook status unreadable";
        }

        if (!string.IsNullOrWhiteSpace(hookReason))
        {
            return hookReason.Trim();
        }

        if (!_sessionSwitchSubscribed)
        {
            // We cannot tell locked from unlocked, so the lock gate below is not enforcing
            // anything -- and a gate that reports nothing when it stops working is worse than no
            // gate, because the user believes it is holding.
            return "lock detection off";
        }

        if (_consecutiveInvalidCpuSamples >= MaxConsecutiveInvalidCpuSamples)
        {
            // CpuOk is false while this lasts, so the launcher never fires. Without this the
            // tooltip would report "CPU reading unavailable" indefinitely, which reads like a
            // condition that is about to clear.
            return "CPU sampling stuck";
        }

        return null;
    }

    private void UpdateDegradationNotification(string? reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        // Deduped on the REASON STRING and not on an "already degraded" bool. An escalation from
        // one fault to another is a different problem with a different remedy, and a bool would
        // swallow it: the user would be told about the first fault and never about the second.
        if (string.Equals(_lastDegradationReason, normalized, StringComparison.Ordinal))
        {
            return;
        }

        var previous = _lastDegradationReason;
        _lastDegradationReason = normalized;

        if (normalized == null)
        {
            Logger.Info($"Degraded operation cleared. PreviousReason='{previous}'.");
            return;
        }

        Logger.Warn($"Operating in a degraded state. Reason='{normalized}' PreviousReason='{previous ?? "(none)"}'.");

        // Rate-limit the balloon but never the log, and rate-limit it PER REASON.
        //
        // A single global timestamp made the interval reason-blind, so a fault that had just
        // cleared bought five minutes of silence for a DIFFERENT fault arriving inside its window:
        // the user was told about the first problem and never about the second. That is exactly
        // the escalation the reason-string dedupe above exists to catch, thrown away one step
        // later. Keyed per reason, a new fault always gets its own notification while a fault
        // flapping between two reasons still balloons at most twice per window instead of
        // every five seconds.
        //
        // Absence from the map means "never notified", which also retires the old long.MinValue
        // sentinel and the TickCount64 overflow footnote that came with it.
        var nowMs = Environment.TickCount64;
        if (!ShouldNotifyDegradation(_degradationNotifiedAtMs, normalized, nowMs, DegradationBalloonMinIntervalMs))
        {
            return;
        }

        RecordDegradationNotification(_degradationNotifiedAtMs, normalized, nowMs, MaxTrackedDegradationReasons);

        ShowTrayNotification(
            $"{AppPaths.AppName} is running degraded",
            $"{normalized}. Automatic launching may not behave as configured. See the log for details.",
            ToolTipIcon.Warning);
    }

    // Whether this exact reason may be ballooned now. Pure, so the per-reason rule is testable
    // without a NotifyIcon: a reason never notified before always passes, and a reason inside its
    // OWN window is held back without holding back any other reason.
    internal static bool ShouldNotifyDegradation(
        IReadOnlyDictionary<string, long> history, string reason, long nowMs, int minIntervalMs)
    {
        if (!history.TryGetValue(reason, out var lastNotifiedMs))
        {
            return true;
        }

        return nowMs - lastNotifiedMs >= minIntervalMs;
    }

    // Records that a reason was just notified, keeping the history bounded.
    //
    // Pure apart from the dictionary it is handed, which is what makes the cap testable without
    // constructing a tray application. On overflow the whole map is dropped rather than evicting
    // one entry: with a bounded set of literal reasons this is unreachable, and if it ever is
    // reached the cost is one extra balloon per reason, which is the right way for this to fail.
    internal static void RecordDegradationNotification(
        Dictionary<string, long> history, string reason, long nowMs, int maxTrackedReasons)
    {
        if (!history.ContainsKey(reason) && history.Count >= maxTrackedReasons)
        {
            history.Clear();
        }

        history[reason] = nowMs;
    }

    private LaunchEvaluation EvaluateLaunchReadiness(bool cpuSampleValid, double cpuPercent)
    {
        var targetPath = TargetFilePolicy.NormalizePath(_cfg.AppPath);

        // Short-circuit deliberately: targetExists guards a filesystem probe behind
        // targetSupported, which guards it behind hasTarget, so a missing or unsupported target
        // never touches the disk. LaunchDecision asks about the three in that same order, so
        // passing false for a check that was never reached cannot change the answer.
        var hasTarget = !string.IsNullOrWhiteSpace(targetPath);
        var targetSupported = hasTarget && TargetFilePolicy.IsSupportedTarget(targetPath);
        var targetExists = targetSupported && TargetExistsCached(targetPath);

        // THE SESSION FLAG IS READ BEFORE THE IDLE CLOCK, AND THE ORDER IS LOAD-BEARING.
        //
        // OnSessionSwitch resets the idle clock and THEN clears _workstationLocked, so that a tick
        // can never see "unlocked" alongside an idle measurement taken during the lock. That
        // writer order only protects a reader that reads the FLAG first and the CLOCK second.
        //
        // Read the other way round -- which is what an object initializer does, since C# evaluates
        // its members in source order -- the guarantee inverts: the tick samples nine hours of
        // lock-screen idle, is preempted, the SystemEvents thread runs the entire unlock handler,
        // and the tick resumes to read `false`. The result is InputIdleOk AND SessionOk together,
        // Ready is true, and the target launches onto the desktop the user just signed back into.
        // The log line would be perfectly self-consistent and describe a state the machine was
        // never in.
        //
        // Both are read into locals here so the order is explicit and cannot be changed by
        // reordering the initializer below. One read each: reading _workstationLocked twice would
        // let a transition land between the two and produce "locked" in the log with "session ok"
        // in the decision, or the reverse.
        var workstationLocked = _workstationLocked;
        var idleSeconds = GetIdleSeconds();

        var result = LaunchDecision.Evaluate(new LaunchInputs
        {
            TargetPath = targetPath,
            HasTarget = hasTarget,
            TargetSupported = targetSupported,
            TargetExists = targetExists,
            IdleSeconds = idleSeconds,
            RequiredIdleSeconds = _cfg.IdleMinutes * 60,
            CpuSampleValid = cpuSampleValid,
            CpuPercent = cpuPercent,
            CpuThresholdPercent = _cfg.CpuThresholdPercent,
            WorkstationLocked = workstationLocked,
            AllowLaunchWhileLocked = _cfg.AllowLaunchWhileLocked,
            LastLaunchUtc = _lastLaunchUtc,
            NowUtc = DateTime.UtcNow,
            MinLaunchCooldownSeconds = MinLaunchCooldownSeconds
        });

        // The clock-warp repair is REPORTED by the pure decision and applied here, so the
        // persisted value and the log line that explains it stay together at the one place that
        // owns them. _lastLaunchUtc is written to config.json and survives restart, so this is a
        // side effect worth keeping visible rather than burying inside the evaluation.
        if (result.RebaselinedLastLaunchUtc.HasValue)
        {
            // PERSIST the repair, not just the in-memory copy.
            //
            // Repairing only _lastLaunchUtc left config.json holding the future timestamp, so the
            // constructor parsed it back in on the next start and the cooldown was re-stranded on
            // every single launch of the app until some later successful launch happened to
            // overwrite it. That is the same "survived restart with nothing to show why" failure
            // the repair exists to end, reduced in magnitude rather than removed -- and until now
            // the comment on LaunchDecision.RebaselinedLastLaunchUtc claimed this write already
            // happened.
            _cfg.LastLaunchUtc = result.RebaselinedLastLaunchUtc.Value.ToString("o", CultureInfo.InvariantCulture);
            ConfigManager.Save(_cfg);

            Logger.Warn(
                $"Launch cooldown timestamp is in the future by {result.ClockWarpSeconds:F0}s (clock change or edited config). Re-baselining to now.");
            _lastLaunchUtc = result.RebaselinedLastLaunchUtc;
        }

        return result.Evaluation;
    }

    // Advances the stuck-sampler counter by one tick. Pure, so the saturation is testable without
    // constructing a tray application.
    //
    // Saturating rather than free-running: the only question ever asked of this counter is
    // ">= MaxConsecutiveInvalidCpuSamples", and a counter that keeps climbing is one that can
    // overflow into a negative and silently clear the fault it exists to report. The comparison is
    // written ">= maxSamples" rather than "< maxSamples then increment" so that int.MaxValue
    // saturates DOWN to the cap instead of wrapping -- unreachable today, but this is now a public
    // function of its arguments and the test says so.
    internal static int NextConsecutiveInvalidCpuSamples(int current, bool sampleValid, int maxSamples)
    {
        if (sampleValid)
        {
            return 0;
        }

        return current >= maxSamples ? maxSamples : current + 1;
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
    // TryLaunchSelectedApp used to return a bare false for EVERY failure, so this wrapper retried
    // the permanent ones too -- "no target", "unsupported type", "file not found" -- and paid a
    // 250ms Thread.Sleep on the UI thread for each, which is a visibly frozen tray menu in
    // exchange for an outcome that could not change. It now reports whether the failure was
    // transient (see IsTransientLaunchFailure) and only those are retried.
    //
    // The re-entrancy rejection is distinguished separately, via alreadyInProgress -- see below.
    private bool TryLaunchWithTransientRetry(string trigger, out string failureMessage, out bool alreadyInProgress)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (TryLaunchSelectedApp(trigger, launchedFromIdle: true, showErrorDialog: false, out failureMessage, out alreadyInProgress, out var failureIsTransient))
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

            if (!failureIsTransient)
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

    // failureIsTransient answers the one question the retry wrapper needs: could a second attempt
    // plausibly succeed? It stays false on every path that fails for a settled reason, so those
    // failures cost no sleep and no second Process.Start.
    private bool TryLaunchSelectedApp(string trigger, bool launchedFromIdle, bool showErrorDialog, out string failureMessage, out bool alreadyInProgress, out bool failureIsTransient)
    {
        failureMessage = string.Empty;
        alreadyInProgress = false;

        // Default false: only the exception path below can promote a failure to transient. Every
        // early return here -- no target, unsupported type, file missing -- is permanent by
        // construction, and leaving the default in place is what makes that true without four
        // separate assignments that a later edit could forget.
        failureIsTransient = false;

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
            failureIsTransient = IsTransientLaunchFailure(ex);
            Logger.Error($"Failed to launch target via {trigger}. Path='{path}' Transient={failureIsTransient}.", ex);

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

    // Which launch failures are worth a second attempt. The retry costs a Thread.Sleep on the UI
    // thread with the message pump held, so it has to be spent only where a retry can plausibly
    // win: a file handle an antivirus scanner is holding for a moment, a share that blinked, an
    // ACL check that lost a race. "No such file" and "not a valid application" will answer the
    // same way every time.
    private static bool IsTransientLaunchFailure(Exception ex)
    {
        return ex switch
        {
            // These derive from IOException and are permanent, so they MUST be matched before the
            // IOException arm below, which would otherwise swallow them. Verified by breaking it:
            // swapping the two arms is a compile ERROR (CS8510, unreachable pattern), so this
            // particular ordering cannot regress silently.
            FileNotFoundException or DirectoryNotFoundException => false,
            IOException => true,
            UnauthorizedAccessException => true,
            Win32Exception win32 => IsRetryableWin32Error(win32.NativeErrorCode),
            _ => false
        };
    }

    // Process.Start with UseShellExecute reports shell failures as Win32Exception, so the native
    // code is the only thing that separates "busy" from "no".
    //
    // Retried: ACCESS_DENIED (5, which the shell also returns for a transient ACL/AV block),
    // SHARING_VIOLATION (32), LOCK_VIOLATION (33), NETNAME_DELETED (64, a share that dropped) and
    // NO_SYSTEM_RESOURCES (1450).
    //
    // NOT retried, and deliberately absent: FILE_NOT_FOUND (2) and BAD_EXE_FORMAT (193) are
    // settled answers, and CANCELLED (1223) means the user dismissed the elevation prompt --
    // retrying that one re-prompts a person who just said no.
    private static bool IsRetryableWin32Error(int nativeErrorCode)
    {
        return nativeErrorCode is ErrorAccessDenied
            or ErrorSharingViolation
            or ErrorLockViolation
            or ErrorNetnameDeleted
            or ErrorNoSystemResources;
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

    // Delegates to TrayStatusText.Clamp rather than keeping a second copy of the same algorithm.
    // The copy that used to live here had the same latent flaw the tooltip one was written to
    // avoid: `value[..(maxLength - 1)]` can cut between the halves of a surrogate pair, leaving an
    // unpaired code unit in a balloon title. One truncator, fixed once.
    private static string TruncateForBalloonTip(string value, int maxLength) =>
        TrayStatusText.Clamp(value, maxLength);

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
            $"Configuration loaded. PortableMode=true AppPath='{_cfg.AppPath}' IdleMinutes={_cfg.IdleMinutes} CpuThresholdPercent={_cfg.CpuThresholdPercent} RunAtStartup={_cfg.RunAtStartup} BlockInjectedWhileRunning={_cfg.BlockInjectedWhileRunning} LockPcOnAppClose={_cfg.LockPcOnAppClose} AllowLaunchWhileLocked={_cfg.AllowLaunchWhileLocked} GamepadCountsAsActivity={_cfg.GamepadCountsAsActivity} UseSystemIdleFailSafe={_cfg.UseSystemIdleFailSafe} SystemIdleFailSafeWindowMs={_cfg.SystemIdleFailSafeWindowMs} TrayIconEnabled={_cfg.TrayIconEnabled} TrayIconPath='{_cfg.TrayIconPath}'.");
    }

    private static void LogHookStatus()
    {
        // Snapshot each volatile ONCE. Reading them again inside the message would let a hook
        // install between the decision and the description, producing a "degraded" warning that
        // then reports both hooks as installed -- a log line that contradicts itself is worse
        // than no log line, because it costs the next reader time before they distrust it.
        var keyboardInstalled = PhysicalIdle.KeyboardHookInstalled;
        var mouseInstalled = PhysicalIdle.MouseHookInstalled;

        if (keyboardInstalled && mouseInstalled)
        {
            Logger.Info("Physical idle hooks installed successfully (keyboard and mouse).");
            return;
        }

        Logger.Warn(
            $"Physical idle hook installation is degraded. KeyboardHookInstalled={keyboardInstalled} KeyboardHookError={PhysicalIdle.LastKeyboardHookError} MouseHookInstalled={mouseInstalled} MouseHookError={PhysicalIdle.LastMouseHookError} UseSystemIdleFailSafe={PhysicalIdle.UseSystemIdleFailSafe}.");
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

        // FIRST, and mandatory rather than tidy. SystemEvents.SessionSwitch is a STATIC event, so
        // the subscription is a GC root: miss this and the whole TrayAppContext stays alive for
        // the life of the process, and with it _notify, _menu, _cfg and the tracked Process
        // handle. That is a real leak, and it also means a torn-down instance keeps waking on
        // every lock and unlock.
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { /* ignore */ }

        // PowerModeChanged is the same kind of static event and leaks the same way.
        try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* ignore */ }

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

        // Clear the static LAST. It is the same category of GC root as the SystemEvents handlers
        // released at the top of this method: while it is set, a torn-down context and everything
        // it owns stay reachable for the life of the process. Benign today (single instance, and
        // the process exits immediately afterwards) but it is the one root this method was
        // otherwise careful about and then left behind.
        //
        // Last, not first: Program.cs's fatal handlers call EmergencyHideTrayIcon through this
        // field, and clearing it early would take away the tray-icon cleanup during the window
        // where the rest of the teardown can still throw.
        _live = null;
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
