# Smoke Test Checklist

Use this checklist before publishing or tagging a release.

## Automated checks

Run from the repository root:

```bash
./tools/smoke-test.sh
```

The automated smoke test verifies:
- supported target extensions are documented in `README.md`
- supported target extensions are present in `TargetFilePolicy.cs`
- the Visual Studio publish profile uses the expected single-file, framework-dependent, embedded-symbol release settings
- `dotnet build IdleLauncherTray.sln` succeeds
- `dotnet publish` creates the framework-dependent single-file `win-x64` executable
- the publish output contains `IdleLauncherTray.exe`
- the published executable contains embedded Windows icon resources

## Manual Windows checks

These require a Windows desktop session and user interaction:

- Start the published `IdleLauncherTray.exe`; confirm it launches with no console window and appears in the system tray.
- Copy or extract the release to a normal Windows folder such as `%USERPROFILE%\Downloads\IdleLauncherTray-test`; confirm File Explorer shows the embedded executable icon there.
- Open the tray menu and confirm **Application -> Choose target application...** allows `.exe`, `.scr`, `.bat`, `.cmd`, `.lnk`, `.msi`, `.ps1`, `.vbs`, `.jar`, and `.py`.
- Select a direct long-running `.exe`, run **Run Now**, and confirm the log records `TrackingState=Tracked`.
- Select a `.scr`, run **Run Now**, and confirm it starts with `/s`.
- Select representative `.bat` and `.cmd` files that keep their console alive; confirm tracking remains active until the shell exits.
- Select a `.lnk` shortcut; confirm launch behavior and note whether the log reports tracked, exited immediately, or untracked behavior.
- Select a safe test `.msi`; confirm launch behavior, including any UAC/elevation handoff, and note process-tracking behavior.
- Select safe `.ps1`, `.vbs`, `.jar`, and `.py` test files; confirm each launches through the expected registered host.
- Enable **Block injected input while running** with a reliably tracked long-running target; confirm synthetic input is blocked while the tracked process is running and restored after it exits.
- Enable **Lock PC on App Close** with an idle-triggered long-running target; confirm Windows locks only after the tracked idle-launched process exits.
- Confirm **Run Now** launches do not trigger lock-on-close.
- Toggle **Run at startup** on and off; confirm the HKCU Run entry points to the current portable executable path and is removed when disabled.
- Use **Uninstall (remove settings + startup)**; confirm `%APPDATA%\IdleLauncherTray` settings are removed, startup registration is removed, and the portable executable remains in place.

## Release artifact check

After building the release zip, extract it to a clean folder on Windows and repeat the icon, startup, tray menu, direct `.exe`, and logging checks from the extracted copy.
