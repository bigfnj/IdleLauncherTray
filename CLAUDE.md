# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**IdleLauncherTray** is a portable Windows system tray application that launches a user-selected target executable when the machine becomes both physically idle (keyboard/mouse) and below a CPU usage threshold.

**Key characteristics:**
- Runs entirely from the system tray (no console window)
- Detects physical keyboard/mouse idle time while ignoring injected automation input (e.g., `SendKeys`)
- Monitors system-wide CPU usage
- Launches `.exe`, `.scr`, or `.bat` files when both idle and CPU conditions are met
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
- **CpuUsageMonitor.cs**: Samples system CPU usage via performance counters
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
- **Target executable**: Selectable via tray menu (supports `.exe`, `.scr`, `.bat` with optional arguments)
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
After a failed automatic launch, the app disarms to avoid repeated failed launches in quick succession. Re-arming requires fresh user activity (mouse/keyboard detected).

### Portable Design
No installer, no copies to `%APPDATA%`. Startup registry entries point to the executable's current path. If the executable moves, startup must be re-enabled.

### Deferred Cleanup
Uninstall uses `DeletionHelper` with command-line invocation to work around lock issues during process shutdown (the running process cannot delete itself or its settings directory).

## Testing

No automated test suite is included. Validation is manual:
- Launch the app and verify tray icon appears
- Configure idle/CPU thresholds and target
- Verify launch occurs when conditions are met
- Verify "Run Now" works independently
- Test startup registry toggling
- Verify log output in `%APPDATA%\IdleLauncherTray\IdleLauncherTray.log`

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

Current version: **2.3.0**

v2.3 adds production-readiness improvements (graceful launch failure handling, hook retry logic, better logging, etc.) building on v2.2 hardening work.

<!-- AI_CTX_START -->
AI_CONTEXT={"v":3,"p":"IdleLauncherTray","root":"/home/bigfnj/projects/IdleLauncherTray","t":"init","i":"","n":"","s":{},"b":[],"d":[],"c":[],"f":[],"h":[],"a":[],"e":null}
Use AI_CONTEXT as authoritative session state. Continue from n; preserve b/d/c/f/h; append only meaningful recent work to a; update context through CTX_UPDATE when supported.
After each response, write a single line `CTX_UPDATE:{"v":3,"p":"...","root":"...","t":"...","i":"...","n":"...","s":{},"b":[],"d":[],"c":[],"f":[],"h":[],"a":[],"e":null}` to /home/bigfnj/.ai-context/IdleLauncherTray.json.update — the VS Code extension reads, merges, and deletes it to persist state after every turn.
<!-- AI_CTX_END -->
