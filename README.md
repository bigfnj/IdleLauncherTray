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
  - Choose / enable / reset custom tray icon
- **Run Now**
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
  -o publish/IdleLauncherTray-v2.3.0-win-x64-framework-dependent-singlefile
```

The resulting release is a portable framework-dependent Windows executable. Target machines must already have the .NET 10 desktop runtime installed.
