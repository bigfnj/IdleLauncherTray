# Architecture

How IdleLauncherTray is put together: the subsystems, the launch-readiness state machine, and the
design decisions worth knowing before changing anything.

## Project Overview

**IdleLauncherTray** is a portable Windows system tray application that launches a user-selected target executable when the machine becomes both physically idle (keyboard/mouse) and below a CPU usage threshold.

**Key characteristics:**
- Runs entirely from the system tray (no console window)
- Detects physical keyboard/mouse idle time while ignoring injected automation input (e.g., `SendKeys`)
- Monitors system-wide CPU usage
- Launches `.exe`, `.scr`, `.bat`, `.cmd`, `.lnk`, `.msi`, `.ps1`, `.vbs`, `.jar` or `.py`
  targets when both idle and CPU conditions are met (see `TargetFilePolicy.cs`, which is the
  single source of truth for this list)
- Supports optional "Lock PC on close" for automatically locking after an idle-triggered app exits
- Can block injected input while the launched app is running
- Can optionally count XInput gamepad activity as user activity
- Fully portable: no installation required, stores config/logs in `%APPDATA%\IdleLauncherTray\`
- Supports Windows startup integration via registry (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`)

## Architecture

### Core Application Flow

1. **Program.cs**: Entry point enforcing single-instance via named Mutex, handling all unhandled exceptions with logging and user-facing error dialogs
2. **TrayAppContext.cs**: Main application context managing:
   - NotifyIcon (tray icon) and ContextMenuStrip (tray menu)
   - Polling timer (5-second intervals) that evaluates launch readiness
   - Launch decision logic (cooldown, idle, CPU checks)
   - Process tracking for launched apps
   - Dynamic menu updates

### Subsystems

- **PhysicalIdle.cs**: Low-level keyboard/mouse hook integration to track physical user activity independent of automation
- **CpuUsageMonitor.cs**: Samples system-wide CPU usage via `GetSystemTimes` (NOT performance
  counters). It is a **delta** sampler: each reading covers the span since the previous call,
  which is why the tick samples it unconditionally rather than only when a launch is possible
- **AppConfig.cs & ConfigManager.cs**: Configuration model (idle threshold, CPU threshold, target path, startup mode) persisted to `%APPDATA%\IdleLauncherTray\config.json`
- **Logger.cs**: Simple file-based logging to `%APPDATA%\IdleLauncherTray\IdleLauncherTray.log`
- **StartupManager.cs**: Registry read/write for startup entry management
- **WorkstationLock.cs**: Windows API wrapper for workstation locking
- **TargetFilePolicy.cs**: Validates supported file types and path expansion
- **DeletionHelper.cs**: Deferred cleanup execution (called from command-line args) for safe uninstall
- **TextPrompt.cs**: Simple dialog form for user text input (used in menu interactions)
- **AppPaths.cs**: Centralized path constants and environment-variable expansion utilities

### Launch Readiness State Machine

The polling loop in `TrayAppContext` evaluates a `LaunchEvaluation` struct containing:
- Target existence and support status
- Input idle time vs. required threshold
- CPU usage vs. threshold
- Cooldown timer (10-second minimum between launches)
- Armed/disarmed state (disarmed after a failed automatic launch, re-armed on fresh user activity)

The state transitions to "ready-to-launch" only when all conditions pass. On successful idle-triggered launch:
- Process handle is tracked for exit monitoring
- Workstation lock is applied if enabled (only for idle launches, not manual "Run Now")
- Re-arming happens after fresh user activity

## Build & Development

### Prerequisites
- Visual Studio 2022 or later (Community edition works)
- .NET 10 SDK

### Building

```bash
# Visual Studio GUI: Open IdleLauncherTray.sln and Build > Build Solution
# Or via dotnet CLI:
dotnet build IdleLauncherTray.sln
```

### Running

From Visual Studio:
1. Build the solution
2. Debug > Start Debugging (F5)
3. Tray icon appears in system tray

From command line:
```bash
dotnet run --project IdleLauncherTray/IdleLauncherTray.csproj
```

### Publishing

A ready-to-use publish profile is included for single-file framework-dependent builds:

**Visual Studio GUI:**
1. Right-click `IdleLauncherTray` project → Publish
2. Select profile `IdleLauncherTray_v2_3_FrameworkDependent_SingleExe`
3. Publish

**CLI:**
```bash
dotnet publish IdleLauncherTray/IdleLauncherTray.csproj \
  -p:PublishProfile="IdleLauncherTray_v2_3_FrameworkDependent_SingleExe"
```

This produces a single `IdleLauncherTray.exe` targeting `win-x64` (requires .NET 10 runtime on target machine).

### Configuration

- **Idle threshold**: Configurable in minutes (default from config)
- **CPU threshold**: Configurable 10-50% (default from config)
- **Target executable**: Selectable via tray menu, with optional arguments. `TargetFilePolicy.cs`
  is the single source of truth for the supported list; see "Supported target types" above rather
  than repeating it here, because a second copy of that list has drifted before
- **Startup mode**: Enabled/disabled via tray menu (writes to Windows registry)
- **Block injected input**: Optional checkbox in tray menu
- **Lock on close**: Optional checkbox in tray menu
- **Gamepad activity**: Optional checkbox in tray menu
- **Custom tray icon**: Optional custom .ico file in `%APPDATA%\IdleLauncherTray\`

Settings persist in `%APPDATA%\IdleLauncherTray\config.json` (JSON format).

## Key Design Decisions

### Single-Instance Guard
Named Mutex prevents multiple tray instances (which would create duplicate hooks and tray icons). Second instance detections are logged and exit immediately.

### Physical Idle Detection via Hook
The app uses low-level keyboard/mouse hooks to distinguish genuine user activity from automation (SendKeys, etc.). This is essential for the use case (detecting when a user has stopped using the machine).

### 5-Second Polling Interval
The main evaluation loop runs every 5 seconds—a balance between responsiveness and CPU efficiency. Idle timeout values are compared against this cadence.

### Armed/Disarmed Pattern
After a failed automatic launch, the app disarms to avoid repeated failed launches in quick
succession. Re-arming requires fresh user activity (mouse/keyboard detected), confirmed over two
consecutive ticks to avoid flapping at the idle boundary.

That guarantee is enforced by `LaunchEvaluation.IdleMeasured`, and it is load-bearing: the
evaluation returns early when no target is configured, the target type is unsupported, or the
target file is missing, and on those paths idle has not been sampled. Re-arming on `!InputIdleOk`
alone would therefore re-arm on an *unmeasured* value, which is what happened before v2.5.

### Portable Design
No installer, no copies to `%APPDATA%`. Startup registry entries point to the executable's current path. If the executable moves, startup must be re-enabled.

### Deferred Cleanup
Uninstall uses `DeletionHelper` with command-line invocation to work around lock issues during process shutdown (the running process cannot delete itself or its settings directory).

## Testing

`IdleLauncherTray.Tests` (xunit) runs with `dotnet test IdleLauncherTray.sln -c Release`. CI fails
the run if fewer than 150 tests execute, because `dotnet test` exits 0 when zero tests match a
filter, so a broken or unreferenced test project would otherwise turn CI green by testing nothing.

Every product type is `internal` and there is no `InternalsVisibleTo`: the tests reach product code
by reflection through the facades in `IdleLauncherTray.Tests/Sut/`. The shipping DLL is therefore
identical to what would ship without a test project.

**A green `dotnet test` does not mean the app works.** The suite covers pure logic only:
`TargetFilePolicy`, `ConfigManager`, `CpuUsageMonitor`'s FILETIME arithmetic, `DeletionHelper`'s
self-delete guard, the tray status formatter and the launch-readiness record. Not covered, and
needing a real desktop session: the tray icon and menu, hook installation, launching, cooldown,
lock-on-close, the startup registry entry and the uninstall flow.

`SMOKE_TEST.md` is the manual checklist that exercises the app as an app, and `tools/smoke-test.sh`
runs the automated half (build, test, publish, single-file and icon-resource checks).

## Logging

All diagnostic output goes to `%APPDATA%\IdleLauncherTray\IdleLauncherTray.log` (plain text, appended on each run).

Key logged events:
- Application startup (version, path, args)
- Configuration loads/saves
- Hook installation status
- Idle state transitions
- Launch evaluations
- Process exits
- Startup registry changes
- Uninstall operations

Log file is useful for diagnosing why a launch did or did not occur at a given time.

## Version

Current version: **2.5.0**

The authoritative version is `<Version>` in `IdleLauncherTray/IdleLauncherTray.csproj`. The release
workflow derives the shipped assembly version from the git tag instead, so a tagged build always
matches its tag regardless of what the csproj says.

- **v2.5** — correctness pass over the idle state machine. Fixes several ways the app could
  silently stop working: an idle clock that could never exceed 30s when one input hook failed, a
  CPU guard that stopped guarding after any long gap, re-arming without an idle measurement, and a
  launch cooldown that a backwards clock change could pin on for years *across restarts*. Adds a
  hard auto-release cap to injected-input blocking so it cannot lock out assistive-technology
  users, and the project's first automated tests.
- **v2.4** — production-hardening pass closing 12 code-review findings (disposal ordering, atomic
  config save, wrapped menu handlers, transient launch retry).
- **v2.3** — production-readiness improvements: graceful launch failure handling, hook retry logic,
  better logging, the expanded target-type list.

