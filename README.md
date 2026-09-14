# IdleLauncherTray (C# / WinForms)

IdleLauncherTray is a **portable** Windows tray utility that waits for the machine to become genuinely idle and then launches a selected target.

It is a **Windows GUI exe (no console)** that:
- Starts in the **system tray**
- Tracks **physical keyboard/mouse idle time** and ignores injected automation input such as `SendKeys`
- Optionally **blocks injected input** while the launched target is tracked as running
- Optionally counts **XInput gamepad** activity as user activity
- Launches a chosen **.exe**, **.scr**, **.bat**, **.cmd**, **.lnk**, **.msi**, **.ps1**, **.vbs**, **.jar**, or **.py** target once both conditions are met:
  - Input idle is greater than or equal to the configured number of minutes
  - Total CPU usage is less than or equal to the configured threshold (10% to 50%)
- Stores settings in `%APPDATA%\IdleLauncherTray\config.json`
- Writes logs to `%APPDATA%\IdleLauncherTray\IdleLauncherTray.log`
- Supports optional **Run at startup** via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

## Portable behavior

This project is intentionally **portable**:
- The app runs from **wherever you place the executable**
- It does **not** copy itself into `%APPDATA%`
- If **Run at startup** is enabled, the registry entry points to the **current executable path**

That means startup is only valid as long as the executable remains at the same path. If you move, rename, or replace the portable build, toggle **Run at startup** off and back on so the registry entry is refreshed.

## v2.6.1 highlights

A post-release audit of the v2.6.0 code found that the new degraded warning **fired on ordinary
lock cycles**. Input on the lock screen never reaches a low-level hook but does keep refreshing
Windows' own idle clock, so touching the lock screen made the two clocks disagree by the whole lock
duration and the detector concluded the hooks had died. It self-corrected on unlock, but only after
logging a warning and showing a balloon — and a warning that fires every time you unlock your PC is
a warning you learn to ignore, which would have cost the feature the only thing it is for. Hook
silence is now *expected* while the session is locked or disconnected, so no false evidence is
gathered in the first place.

Also fixed: a permanently failing monitor tick logged a full stack trace every 5 seconds, roughly
17,000 entries a day into a 2 MB log that rotates — destroying the very trace it was reporting. It
now logs the first failure of an episode and announces the recovery.

## v2.6 highlights

Version 2.6 finishes what v2.5 started. v2.5 fixed the ways this app could silently stop working;
v2.6 makes it **say so**, and closes the remaining places where it lied about itself or quietly
destroyed something. Every item below was either reported by the app's own log or found by an audit
of code that had no test covering it.

- **The tray tooltip now reports state.** Hover the icon and it says what the launcher is doing:
  `Idle 3:20/5:00`, `CPU 37% > 10%`, `Target missing: app.exe`, `Disarmed until you use the PC`,
  `Running app.exe`. When something is broken rather than merely waiting, the line starts with
  `DEGRADED -` and a balloon appears at most once every five minutes. A monitor loop that throws
  now says `DEGRADED - monitor tick failed` instead of leaving an app that has silently stopped
  launching looking identical to one that is patiently waiting.
- **Launching is blocked while the PC is locked.** The password typed on the secure desktop never
  reaches the low-level hooks, so a locked machine looks fully idle no matter who is standing at
  it. **Options → Allow launching while the PC is locked** opts back in; it is off by default.
- **Run Now no longer disarms the launcher.** Clicking Run Now and then walking away used to
  switch automatic launching off for the entire away period, because the re-arm only happens after
  fresh user activity and there is none. Automatic launches still disarm.
- **Hooks that Windows silently drops are now detected.** Windows unhooks a low-level keyboard or
  mouse hook whose callback overruns its timeout, and it does *not* clear the handle — so the old
  "is the handle non-null" check could never notice, and the app went on reporting a machine as
  fully idle forever. It now compares its own callback heartbeat against the system's input clock,
  reports `input hooks stopped firing`, and stops trusting the dead hook in favour of the Windows
  idle reading. Detection only: it does not reinstall hooks behind your back.
- **Waking from sleep no longer counts as idle time.** Nothing the user does to wake a suspended
  machine reaches a low-level hook, so a PC that slept overnight used to read as "idle for nine
  hours" the instant it woke, and could launch before the user had touched anything.
- **A corrupt config is moved aside instead of overwritten.** An unreadable `config.json` (a power
  cut mid-write is the realistic cause) used to be replaced with defaults within milliseconds of
  the next launch, taking the target path, arguments, thresholds and custom icon with it. It is now
  preserved as `config.corrupt-<timestamp>.json` so it can be recovered by hand.
- **Absurd config values are clamped at both ends.** An `IdleMinutes` large enough to overflow the
  seconds conversion produced a *negative* threshold, which every tick satisfies — the app launched
  its target continuously. An unbounded fail-safe window did the opposite and stopped it launching
  at all.
- **Uninstall no longer reaches outside its own folder.** The attribute pass before deleting the
  settings directory followed junctions and directory symlinks, clearing read-only, hidden and
  system flags on whatever was on the other side. The delete itself was always confined; the
  attribute write was not.
- **A crash on a background thread now tells you.** It was fatal either way, but only the UI-thread
  path showed a dialog, so the tray icon simply vanished with no explanation and no pointer to the
  log.

## v2.5 highlights

Version 2.5 is a correctness pass over the idle state machine, driven by two read-only audits. The
through-line: **this app used to fail silently, in both directions**, and a tray utility that has
quietly stopped working looks exactly like one that is idle and waiting.

It could silently never launch:
- If one input hook installed and the other failed, the 30s repair loop reset the idle clock on
  every attempt, capping measured idle at 30s so no configured threshold above that was reachable
- A backwards clock change (NTP, VM snapshot, dead CMOS battery) pinned the launch cooldown on for
  the magnitude of the jump, and because it is persisted to `config.json` it survived restart
- Any failure to query the tracked process returned "still running" forever, with no attempt cap

It could silently launch when it should not:
- CPU usage is a *delta* sample but was only taken on the all-checks-passed path, so after a long
  target run the next reading was a multi-hour average — under the threshold almost always
- The launcher re-armed on an idle value that had never been measured, then logged that fresh user
  activity had been observed

Safety and responsiveness:
- **Block injected input while running** now has a hard 10-minute auto-release, so it cannot lock
  out anyone using On-Screen Keyboard, eye-gaze, AutoHotkey or Mouse Without Borders. A touch/pen
  allowlist is included, but the cap is the guarantee — the allowlist cannot be complete
- `File.Exists` no longer runs on the UI thread every tick; against an unreachable UNC target it
  froze the tray menu itself
- Uninstall no longer recreates the folder it just deleted (the logger was resurrecting it)
- Enabling **Lock PC on App Close** no longer locks the workstation on the spot
- A crash in a menu handler no longer leaves a ghost tray icon
- XInput no longer polls four controller slots 4x/second on a machine with no controller

And the project's first automated tests: 184 cases over the pure logic, run by `dotnet test` and
gated in CI. They do **not** cover the tray, the hooks or launching — `SMOKE_TEST.md` is still the
only thing that exercises the app as an app.

## v2.4 highlights

Version 2.4.0 is a production-hardening pass that closes 12 code-review findings with **zero new dependencies** and no behavioral change to the happy path (verified with a clean `dotnet build` and a single-file publish smoke test that still produces exactly one `IdleLauncherTray.exe`). Project version metadata is now `2.4.0`.

Medium-severity fixes:
- The `TrayAppContext` constructor now wraps its entire init body in try/catch and shuts down cleanly on failure, so a throwing init step can no longer leak the tray icon, menu, hooks, or CPU monitor
- Tray-icon swaps now detach `NotifyIcon.Icon` before disposing the previous `Icon`, preventing a repaint from hitting a disposed icon
- `ConfigManager.Save` deletes its `.tmp` staging file in a `finally` block so a crash mid-save can no longer orphan it
- All state-mutating tray-menu handlers are wrapped so an unexpected exception logs cleanly and surfaces a single dialog instead of escaping to the WinForms message pump
- The self-delete safety check now requires the cleanup path to exactly match the app's base directory (defense in depth for the `--cleanup-folder` path)
- Automatic idle-triggered launches now retry once on transient failure (e.g. an AV scanner briefly holding the target file) before disarming

Low-severity fixes:
- The CPU monitor detects FILETIME counter regression / system-clock jumps and re-baselines instead of returning a garbage reading from unsigned underflow
- Workstation-lock failures now log the translated Win32 message (e.g. "A required privilege is not held by the client") instead of a bare error code
- Deferred folder cleanup now logs the spawned child PID, and the child logs the eventual delete result, so silent cleanup failures show up in the log
- Magic menu-option arrays and balloon-tip length limits were extracted to named constants/fields
- `LaunchEvaluation` became a sealed `record` for auto equality/hash/ToString
- The logger caches its resolved log path instead of recomputing it on every write

## v2.3 highlights

Version 2.3 carries forward the v2.2 hardening work and adds the following production-readiness changes:
- Automatic idle-triggered launch failures no longer show blocking modal error dialogs
- Failed automatic launches now log, show a tray notification, and disarm until fresh user activity begins a new idle episode
- The system idle fail-safe window now defaults to `6000 ms` so it matches the 5-second monitor cadence
- When keyboard or mouse hook installation is degraded, Windows-reported idle is used conservatively to avoid false idle launches while the user is still active
- Missing low-level hooks are now retried opportunistically during runtime instead of staying in a partial-install state forever
- Hook callback exceptions now fail open and are logged once instead of risking unstable callback behavior
- Target paths are normalized to canonical full paths (including environment-variable expansion) while preserving UNC/network-path compatibility
- Supported launch targets now include `.cmd`, `.lnk`, `.msi`, `.ps1`, `.vbs`, `.jar`, and `.py` in addition to `.exe`, `.scr`, and `.bat`
- Uninstall now uses an internal deferred cleanup helper instead of a shell-built `cmd.exe /c` delete command
- Includes a Visual Studio publish profile for a **framework-dependent single-file** `win-x64` publish
- Updates project version metadata to `2.3.0`

## Tray menu

The tray menu exposes:
- **Run at startup**
- **Idle timer**
- **CPU Threshold**
- **Application**
  - Choose `.exe`, `.scr`, `.bat`, `.cmd`, `.lnk`, `.msi`, `.ps1`, `.vbs`, `.jar`, or `.py`
  - Set optional launch arguments
- **Options**
  - Block injected input while running
  - Lock PC on App Close
  - Count gamepad input as activity
  - Allow launching while the PC is locked (off by default)
  - Choose / enable / reset custom tray icon
- **Run Now** — launches immediately and leaves automatic launching armed
- **Uninstall (remove settings + startup)**
- **Exit**

## Supported target types

IdleLauncherTray starts targets with Windows shell execution, so Windows handles each file the same way it would from Explorer or the Run dialog. This is what allows shortcuts, installers, scripts, Java archives, and Python files to launch through their registered file associations.

Supported target extensions:
- `.exe`
- `.scr`
- `.bat`
- `.cmd`
- `.lnk`
- `.msi`
- `.ps1`
- `.vbs`
- `.jar`
- `.py`

Optional arguments are passed to the selected target. Screensaver targets (`.scr`) automatically receive `/s` before any user-provided arguments so they start full-screen.

## Process tracking limitations

IdleLauncherTray tracks the immediate process handle returned by Windows. Direct long-running `.exe` and `.scr` targets usually provide the most reliable tracking.

Some supported target types may be short-lived or may launch through another host process:
- `.bat` and `.cmd` targets are tracked through the command shell process that Windows starts.
- `.lnk` targets depend on the shortcut target and how Windows resolves it.
- `.msi` targets may hand off to Windows Installer, request elevation, or return before installation UI closes.
- `.ps1`, `.vbs`, `.jar`, and `.py` targets depend on their registered host applications, such as PowerShell, Windows Script Host, Java, or Python.

If the immediate process exits quickly after launching a child process, IdleLauncherTray treats the target as closed. That affects features tied to process lifetime, including **Block injected input while running** and **Lock PC on App Close**.

## Lock on close behavior

When **Options -> Lock PC on App Close** is enabled, IdleLauncherTray calls the Windows workstation lock API after the **tracked process** for an idle-triggered launch exits.

This is intentionally limited to targets launched automatically by the idle trigger. A manual **Run Now** launch does not lock the PC on close.

Because the app tracks the immediate process returned by Windows, launcher stubs, shortcuts, scripts, installers, or batch files that spawn a child process and exit immediately may not behave exactly like a long-running direct `.exe` or `.scr`. In those cases the workstation lock is based on the tracked process handle that was actually returned.

## Launching while the PC is locked

By default IdleLauncherTray will not launch while the workstation is locked. The reason is that it cannot tell the difference: the password typed on the secure desktop never reaches a `WH_KEYBOARD_LL` hook, so a locked machine reports the full lock duration as idle time whether nobody is there or somebody is signing in right now.

Lock and unlock are observed through `SystemEvents.SessionSwitch`, which also covers RDP connect/disconnect and console connect/disconnect (fast user switching). Sign-in and sign-out of *other* sessions are deliberately ignored, because they say nothing about this one.

**Options → Allow launching while the PC is locked** turns the gate off for anyone who wants a screensaver or a batch job to start behind the lock screen. If the subscription itself fails at startup, the tray tooltip reports `DEGRADED - lock detection off` rather than leaving a gate that silently enforces nothing.

## Logging

The log file is stored at:

`%APPDATA%\IdleLauncherTray\IdleLauncherTray.log`

The log captures operational details that are useful when diagnosing why a launch did or did not happen, including idle readiness transitions, hook health, automatic-launch failure handling, and workstation lock attempts after idle-triggered app exits.

## Build / Run (Visual Studio)

1. Open `IdleLauncherTray.sln` in Visual Studio.
2. Build the solution.
3. Run the executable.
4. You should see the tray icon.

## Uninstall

The tray menu entry **Uninstall (remove settings + startup)** removes:
- the startup registry value, if present
- `%APPDATA%\IdleLauncherTray\` contents such as config, logs, and optional custom tray icon

Because the application is portable, **Uninstall does not delete the portable executable itself**.

## Publish (Visual Studio)

This source package includes a ready-to-use Visual Studio publish profile:

`IdleLauncherTray\Properties\PublishProfiles\IdleLauncherTray_v2_3_FrameworkDependent_SingleExe.pubxml`

That profile publishes a:
- single-file Windows executable
- framework-dependent deployment (`.NET 10` must already be installed on the target machine)
- `win-x64` build
- with single-file compression intentionally disabled, because .NET only supports bundle compression for self-contained publishes

In Visual Studio:
1. Right click the project.
2. Choose **Publish**.
3. Select the included `IdleLauncherTray_v2_3_FrameworkDependent_SingleExe` profile.
4. Publish the project.

## Publish (CLI)

From the repository root:

```bash
dotnet publish IdleLauncherTray/IdleLauncherTray.csproj \
  -c Release \
  -r win-x64 \
  --self-contained false \
  -p:PublishSingleFile=true \
  -p:UseAppHost=true \
  -p:EnableCompressionInSingleFile=false \
  -p:PublishReadyToRun=false \
  -p:PublishTrimmed=false \
  -p:DebugType=embedded \
  -p:DebugSymbols=true \
  -o publish/IdleLauncherTray-win-x64-framework-dependent-singlefile
```

The output folder name is deliberately version-free here. `tools/smoke-test.sh` derives the
version from `<Version>` in the csproj and names its own output accordingly; hardcoding a
version into this example is how it ended up two releases out of date.

The resulting release is a portable framework-dependent Windows executable. Target machines must already have the .NET 10 desktop runtime installed.
