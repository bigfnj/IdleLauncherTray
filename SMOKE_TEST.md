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
- Run **Run Now**, let the target exit without touching the keyboard, then wait out the idle timer; confirm the automatic launch still fires. Run Now must not disarm the launcher.
- Hover the tray icon over several states and confirm the tooltip tracks them: `IdleLauncherTray: Idle 0:15/5:00` while counting up, `IdleLauncherTray: Running <name>` while a target is tracked, `IdleLauncherTray: Target missing: <name>` after deleting the selected target, `IdleLauncherTray: Disarmed until you use the PC` after a failed automatic launch.
- With **Allow launching while the PC is locked** OFF, lock the workstation (Win+L) and wait past the idle threshold; confirm nothing launches and the log records `reason=WorkstationLocked` with `sessionOk=False`. Unlock and confirm the launcher does **not** fire on the first tick after sign-in.
- Turn **Allow launching while the PC is locked** ON, repeat the lock test, and confirm the target does launch.
- Confirm the log records both halves of the session subscription: `Subscribed to session switch notifications` at startup and a `Session became unavailable` / `Session became available` pair per lock cycle.
- Sleep the machine, wake it, and confirm the log records `Resumed from sleep; idle clock reset` and that the tooltip restarts its idle count from near zero. Without this the suspended hours count as idle and the target can launch before you have touched anything.
- Corrupt `%APPDATA%\IdleLauncherTray\config.json` (truncate it mid-object) while the app is closed, then start it. Confirm a `config.corrupt-<timestamp>.json` appears beside it holding the original text, that the app starts on defaults, and that the original is **not** left to be overwritten.
- Toggle **Run at startup** on and off; confirm the HKCU Run entry points to the current portable executable path and is removed when disabled.
- Write the `HKCU\...\Run\IdleLauncherTray` value by hand through a differently-cased or 8.3 short path, reopen the tray, and confirm **Run at startup** shows **ticked**. It used to show unticked while the app launched at every logon, with no way to clear it from the menu.
- Set the target to a path containing an environment variable (for example `%WINDIR%\System32\notepad.exe`). Confirm **Run Now** launches it, and that after a restart `config.json` still contains `%WINDIR%` rather than the expanded path.
- Open **Application -> Set arguments...**, then click another application's window. Confirm the prompt stays on top, has a taskbar button and an Alt-Tab entry, and that OK and Cancel both return control to the tray.
- After **Uninstall**, open `%TEMP%\IdleLauncherTray-uninstall.log` and confirm the newest entry records `ParentExited=True`. That value can genuinely be False, which is the point of logging it.
- Confirm the uninstall menu click returns promptly. The tray icon vanishing should be the last visible event, not the start of a multi-second freeze.
- Use **Uninstall (remove settings + startup)**; confirm `%APPDATA%\IdleLauncherTray` settings are removed, startup registration is removed, and the portable executable remains in place.

## Release artifact check

After building the release zip, extract it to a clean folder on Windows and repeat the icon, startup, tray menu, direct `.exe`, and logging checks from the extracted copy.
