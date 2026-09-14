# IdleLauncherTray.Tests

Headless unit tests for the pure logic in IdleLauncherTray. xunit, `net10.0-windows`
to match the product.

```
dotnet test IdleLauncherTray.sln -c Release
```

## What is covered

| Area | What is asserted |
| --- | --- |
| `TargetFilePolicy` | every accepted extension and a batch of rejected ones, case-insensitivity, quote/whitespace stripping, environment-variable expansion, relative paths anchored on the executable folder, UNC paths, dot-segment collapsing, the code/message-string consistency, and that a path `Path.GetFullPath` rejects produces an answer instead of an exception |
| `TargetFilePolicy`'s storage/use split | that `PrepareForStorage` keeps a `%VAR%` spelling, a relative path and a dot segment exactly as written, that `ResolveForUse` expands and roots the same string, that `NormalizePath` is still the same function as `ResolveForUse`, and that `ClassifyTarget` calls a malformed path unparseable rather than an unsupported type — with the user-facing message following it, so an `.exe` is never refused over a list containing `.exe` |
| `DeletionHelper` | `IsSafeDeleteTarget` and `NormalizeFolderPath` — it accepts only the app's own data directory (in any letter case, with or without a trailing separator) and refuses a parent, a child, a sibling, a name that merely shares its prefix, a drive root, a UNC share root, a relative path and empty input |
| `ConfigManager` | defaults, full save/load round trip, every normalisation clamp, comment/trailing-comma tolerance, and that a corrupt, empty, wrong-shaped or unreadable file yields defaults rather than an exception |
| `ConfigManager`'s target handling | that a `%APPDATA%` path survives `Save` then `Load` with the variable still present **in the file on disk**, that the support check still runs through an expanded variable, that an unsupported type is still cleared and an unparseable path is deliberately kept, that each writes a log line naming which of the two it was, and that `Save` emits the exact UTF-8 bytes — no BOM — the pre-durability `File.WriteAllText` produced |
| `CpuUsageMonitor` | the FILETIME halves combining without sign extension, the first-sample priming, the divide-by-zero guard when two samples land in one clock tick, the counter-regression re-baseline for each of the three counters independently, and the idle-exceeds-total clamp |
| `AppPaths` | the `IDLELAUNCHERTRAY_DATA_DIR` override, the fall-back to `%APPDATA%`, and the canary described below |
| `PhysicalIdle` hook liveness | the rule that decides a hook was silently dropped — that an idle user is never reported as one at any tick count, that an unusable `GetLastInputInfo` reading is not evidence, the grace and tick thresholds, that both callbacks move the heartbeat even when told to pass the event straight through, the reason strings fitting the tray tooltip, and that an external-activity report advances the idle clock without ever moving it backwards |
| `TrayStatusText` | that every `LaunchReasonCode` renders inside the 63-character cap for `int.MaxValue` durations, 100% CPU, a 300-character target name and a 300-character app name; that each reason code produces a distinct body and only `Unknown` reaches "Status unknown"; the precedence order (degraded beats everything, a setup fault beats "disarmed", "disarmed" beats "ready"); that a cut never splits a surrogate pair; and that the duration and percentage helpers are total |
| `TrayAppContext.LaunchEvaluation` | that `Ready` is false for a locked workstation and true when launching while locked is allowed, that `Ready` ignores `ReasonCode` entirely, and the exact `StateKey` string — the reason codes became an enum, which silently rebinds the `string.Join` overload |
| `TrayAppContext`'s launch-failure classifier | which exceptions earn the one transient retry: `IOException` and `UnauthorizedAccessException` yes, `FileNotFoundException` no despite deriving from `IOException`, and the shell's `Win32Exception` codes split between "busy" and "settled" |

## What is NOT covered

This suite protects the pure functions. It does not protect the app.

- **`TrayAppContext`** — the whole tray lifecycle: the context menu, the idle poll timer,
  launching the target process, the launch cooldown, workstation lock on close, the
  uninstall flow. It needs a window and a message pump. Its pure parts are reached
  individually: the `LaunchEvaluation` record and the two static launch-failure
  classifiers are tested, the instance methods around them are not.
- **`PhysicalIdle`'s hooks themselves** — installing them, the injected-input filter, XInput
  gamepad polling, and the 5 s tick that drives the drop detector. Those need real input, a
  message pump and a global hook this suite must not install. What *is* covered is the
  decision the tick makes: `IsHookDropSuspected` is a pure function of two clock readings
  and a tick count, so every case is reachable as an argument.
- **The `SessionSwitch` subscription** — that lock and unlock actually move the flag, and
  that the handler survives being called on the SystemEvents thread. Raising a real
  session switch means locking the machine, so `SMOKE_TEST.md` covers it.
- **The tooltip wiring** — `TrayStatusText` is tested exhaustively, but that `OnTick`
  calls it from all three places, and that `NotifyIcon.Text` accepts the result, needs a
  tray icon.
- **`StartupManager`** — writes to `HKCU\...\Run`. Testable in principle, untested here
  because it mutates real machine state. That now includes the rule that a Run value
  existing under our value name means **enabled**, whatever path it holds — there is no
  seam to fake the registry behind, so `SMOKE_TEST.md` is the only thing that can check
  it. Write a Run value through an 8.3 short name or a junction, reopen the tray, and the
  "Run at startup" tick must be on.
- **`ConfigManager.Save`'s durability** — the tests pin the bytes and that no `.tmp`
  survives, but nothing here proves `Flush(flushToDisk: true)` reached the platter.
  Verifying that needs a power cut, not an assertion.
- **`Logger`** — only its path is asserted, not rotation or its locking.
- **`DeletionHelper`'s actual deletion** — the retry loop, the read-only attribute
  clearing, the deferred child process and its wait-for-parent-exit. Only the guard that
  decides *whether* a delete may proceed is tested.
- **The published artefact** — that an EXE is produced, that it is a single file and that
  it carries its icon resources. `tools/smoke-test.sh` and the CI workflow cover that.

Nothing here starts a process, opens a window or writes outside a temp folder.

## The data-directory redirect

`ConfigManager`, `Logger` and `DeletionHelper` all resolve their paths from
`AppPaths.BaseDir`, which is `%APPDATA%\IdleLauncherTray` — the real user's live config
and log, and the exact directory `DeletionHelper` exists to delete. Testing them in
process without a seam would mean testing against real user data.

Four mechanisms keep that from happening, and they are independent:

1. `AppPaths.BaseDir` resolves `IDLELAUNCHERTRAY_DATA_DIR` on **every read** rather than
   caching it in a `static readonly` field. A cached field is captured whenever the CLR
   first touches the type, which is a race a test can lose silently.
2. `TestDataDirectory` carries a `[ModuleInitializer]` that points that variable at a
   throwaway temp folder before the first method in this assembly executes — earlier than
   any test, any test-class static constructor and any xunit fixture. It then forces
   `Logger`'s type initialiser to run, so the log path `Logger` caches for the life of the
   process is captured while the redirect is known to be on.
3. `TempDataDirectory` calls `TestDataDirectory.AssertRedirectIsInEffect()` in its
   constructor and refuses to let the test proceed unless the product's resolved data
   directory is inside the temp root. Every test that makes the product touch the
   filesystem goes through it, so a build whose redirect is broken fails at arrange time
   instead of after the write.
4. `AppPathsTests` contains the canary: four tests assert that `AppPaths.BaseDir` and
   `Logger.LogPath` are inside the temp root and are not the real user directory.

Mechanism 3 exists because mechanisms 1, 2 and 4 were not enough. A mutation run that
reintroduced the `static readonly` snapshot took that snapshot inside the one window where
`AppPathsTests` deliberately clears the override, froze the product onto the real
`%APPDATA%`, and the rest of that run wrote the user's real config and log. The canary
did fail — but only after the damage. An assertion reports; a tripwire prevents.

Individual tests that need their own directory use `TempDataDirectory`, which creates
one, redirects to it, checks the tripwire and deletes the directory afterwards.

## Why reflection

Every type in IdleLauncherTray is `internal`, and the highest-value functions
(`DeletionHelper.IsSafeDeleteTarget`, `CpuUsageMonitor`'s counter state and its FILETIME
conversion) are `private`. The facades in `Sut/` reach them by reflection, which means no
accessibility modifier in the shipping code had to be widened for tests, and the tests
exercise the assembly that actually ships.

`Sut/Product.cs` rethrows the original exception rather than the
`TargetInvocationException` reflection wraps it in, so a test asserting "this input must
not throw" is asserting something about the product and not about reflection plumbing.

## Parallelism

Assembly-wide parallelisation is disabled (`AssemblyInfo.cs`). Several tests move
process-global state — the environment override and the working directory — and the whole
suite runs in about a second, so serialising it removes a class of scheduling-dependent
failures for no real cost.

## Mutation testing

Every assertion here was verified by breaking the code it guards and confirming the
matching test went red. A test that passes no matter what the code does is worse than no
test. 69 mutations were applied one at a time to a throwaway copy of the repo; 63 were
killed, and 79 of the 80 test methods have been observed failing under at least one of
them.

Six mutations survived. None of them is missing coverage — each one is code whose
behaviour is already determined by something else:

- `AppPaths`' blank-override branch — a blank value reaches `Path.GetFullPath("")`, which
  throws, and the surrounding `catch` returns the same default.
- `TargetFilePolicy`'s `!string.IsNullOrWhiteSpace(extension)` guard — the set lookup
  already answers `false` for an empty extension.
- `CpuUsageMonitor`'s `if (pct > 100)` clamp — `busy` is clamped to at most `total` two
  lines earlier, so `pct` cannot exceed 100. The clamp is unreachable.
- `ConfigManager`'s `?? new AppConfig()` after deserialisation — the surrounding
  `catch` already turns the resulting failure into the same defaults.
- `DeletionHelper.IsSafeDeleteTarget`'s empty-input guard and its no-root guard — each is
  individually redundant, because `Path.GetPathRoot` returns null for empty input and a
  rootless path still fails the must-equal-BaseDir test. Removing *both* plus the equality
  test is killed by 30 tests. Three overlapping guards on a recursive delete is the right
  amount; this records which ones are load-bearing alone.

The hook-liveness tests were added the same way: 9 mutations, each breaking one decision the
new tests claim to guard, applied one at a time with the product DLL's timestamp checked so
a green run could not come from a stale build. All 9 were killed by the test that names the
behaviour — including the two that matter most, the naive "the hook has not fired lately"
detector (killed by the idle-machine theory) and a `GetIdleMilliseconds` that keeps trusting
a hook it suspects is dead.

One half-mutation is over-determined and worth recording: deleting the `double.IsInfinity`
guard from the evidence rule is killed for a negative or `NegativeInfinity` reading but not
for `PositiveInfinity`, because `callbackAge - PositiveInfinity` is already negative and no
gap can exceed the grace. The guard stays, for the two cases that do need it and because the
intent should not depend on that arithmetic.

The config/path round added six more, each applied to the real source and rebuilt — the harness
prints the product DLL's timestamp before and after, so a green run cannot come from a stale
build. Persisting the expanded path again was killed by 4 tests; calling an unparseable path an
unsupported type by 3; writing `config.json` with a UTF-8 BOM by 1; clearing an unparseable stored
path instead of keeping it by 1; dropping the display truncation by 2.

The sixth is the one worth recording. Removing the expansion from `ClassifyTarget` was killed by
the existing `IsSupportedTarget_ExpandsEnvironmentVariablesBeforeDeciding` but **not** by the new
`ConfigManager` test written to cover the same ground: its variable named a `.txt`, so the path was
cleared whether or not expansion happened and the test proved nothing. It now uses a variable
naming an `.exe`, where an unexpanded path has no extension and is cleared while an expanded one is
kept — so the assertion can only pass if expansion really ran. A test whose axis is degenerate
passes for a reason that has nothing to do with the behaviour it names.

The one test method no mutation kills is
`AppPathsTests.TestRun_NeverPointsBaseDir_AtTheRealUserDataDirectory`. The only mutation
that makes it fail is one that points the product at the real `%APPDATA%`, which is the
thing it exists to catch — running it would write to the user's live config. It is kept as
a canary; the tripwire in mechanism 3 above is the mutation-tested control.
