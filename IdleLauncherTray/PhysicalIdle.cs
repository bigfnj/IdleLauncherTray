// This file is a direct port of the embedded C# used inside IdleLauncherTray.ps1.
// It tracks physical input idle time and can optionally suppress injected input events.

// ----------------------------------------------------------------------------
// Overview
// ----------------------------------------------------------------------------
// PhysicalIdle tracks *real* user activity by installing low-level keyboard and
// mouse hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) and ignoring injected events
// (LLKHF_INJECTED/LLMHF_INJECTED). This prevents automation / SendKeys from
// keeping the system "active" when the user is actually away.
//
// Optional features:
//   - Suppress injected input while a launched app/screensaver runs.
//   - Count XInput (gamepad) activity as "user present".
//   - System idle fail-safe: if hooks fail, fall back to GetLastInputInfo.
//
// This code is intentionally defensive: hooks are global and fragile, so most
// failures should degrade gracefully rather than crash the tray app.
// ----------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace IdleLauncherTray;

public static class PhysicalIdle
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int LLKHF_INJECTED = 0x00000010;
    private const int LLKHF_LOWER_IL_INJECTED = 0x00000002;

    private const int LLMHF_INJECTED = 0x00000001;
    private const int LLMHF_LOWER_IL_INJECTED = 0x00000002;

    private const int VK_SCROLL = 0x91;

    // --- Gamepad (XInput) support ---
    // Set to false to ignore gamepad input.
    // The volatile keyword ensures memory barrier semantics on read/write from any thread.
    private static volatile bool _gamepadEnabled = true;
    public static bool GamepadEnabled
    {
        get => _gamepadEnabled;
        set => _gamepadEnabled = value;
    }

    // Polling interval (ms). 250ms is responsive without being chatty.
    public static int GamepadPollMilliseconds { get; set; } = 250;

    // XInput deadzones / thresholds (common defaults from XInput.h)
    private const int XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE = 7849;
    private const int XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE = 8689;
    private const byte XINPUT_GAMEPAD_TRIGGER_THRESHOLD = 30;

    // These properties are written from the UI thread and read from low-level hook callback
    // threads (KeyboardHookCallback, MouseHookCallback) and the gamepad polling thread.
    // volatile is required so that writes on the UI thread are immediately visible to
    // readers on the callback threads without needing a full memory barrier.
    private static volatile bool _suppressInjected;

    // --- Injected-input suppression safety net ---
    //
    // Suppression swallows EVERY event flagged LLKHF_INJECTED / LLMHF_INJECTED. That is the
    // whole point for SendKeys-style anti-idle tools, but the same flag is set by the
    // On-Screen Keyboard, Windows Eye Control / Tobii dwell-click, AutoHotkey remaps and
    // PowerToys Mouse Without Borders. A user whose ONLY input path is one of those cannot
    // dismiss the screensaver at all until they reach a physical keyboard or mouse.
    //
    // THIS CAP IS THE GUARANTEE. After it elapses suppression stops, whatever the caller
    // does -- it does not depend on the allowlist below, which cannot be complete, nor on
    // the caller remembering to switch suppression off. Ten minutes is far longer than a
    // screensaver needs to prove it is running, and short enough that a locked-out user is
    // not stranded.
    private const long InjectedSuppressionMaxDurationMs = 10 * 60 * 1000;

    // Monotonic timestamp of the false -> true transition, or NotSuppressing when off.
    // Re-asserting `SuppressInjected = true` while it is already true deliberately does NOT
    // refresh this, so a caller that re-applies the setting on a timer (the tray app does,
    // once per tick) cannot push the deadline out forever.
    private const long NotSuppressing = long.MinValue;
    private static long _suppressionStartedMilliseconds = NotSuppressing;

    // Latched by the hook callback once the cap is exceeded. Kept separate from
    // _suppressInjected so the public property keeps reporting what the caller ASKED for:
    // clearing the caller's own flag would just make it re-apply the setting on the next
    // tick (and log a state change every time), defeating the guarantee.
    private static int _suppressionAutoReleased;

    public static bool SuppressInjected
    {
        get => _suppressInjected;
        set
        {
            if (!value)
            {
                _suppressInjected = false;
                Interlocked.Exchange(ref _suppressionStartedMilliseconds, NotSuppressing);
                Interlocked.Exchange(ref _suppressionAutoReleased, 0);
                return;
            }

            if (_suppressInjected)
            {
                // Already armed: re-asserting must not restart the clock.
                return;
            }

            // Publish the deadline (and clear the latch) BEFORE the flag, so a callback
            // that observes _suppressInjected == true always sees a valid start time.
            Interlocked.Exchange(ref _suppressionStartedMilliseconds, GetMonotonicMilliseconds());
            Interlocked.Exchange(ref _suppressionAutoReleased, 0);
            Interlocked.Exchange(ref _pendingSuppressionAutoReleaseLog, 0);
            _suppressInjected = true;
        }
    }

    // Best-effort allowlist for injected input that is a real person on an assistive or
    // remote input path rather than an anti-idle script.
    //
    // THIS CANNOT BE COMPLETE, and it is not meant to be. Windows exposes no reliable
    // signature for the On-Screen Keyboard, Eye Control, AutoHotkey or Mouse Without
    // Borders: they all look exactly like SendInput because that is what they call. Only
    // the touch/pen injection signature (MI_WP_SIGNATURE) is documented, so that is the
    // only thing that can be matched here. InjectedSuppressionMaxDurationMs above exists
    // precisely because this list will miss real users.
    private const ulong InjectedSignatureMask = 0xFFFFFF00;
    private const ulong TouchOrPenSignature = 0xFF515700;

    private static bool IsAllowlistedInjectedSource(IntPtr dwExtraInfo)
    {
        // ToInt64 sign-extends on x86, but the mask keeps only bits 8..31, so the
        // comparison is identical on both architectures.
        var extraInfo = unchecked((ulong)dwExtraInfo.ToInt64());
        return (extraInfo & InjectedSignatureMask) == TouchOrPenSignature;
    }

    // Hot path: this runs inside the low-level hook callbacks, which block ALL system input
    // until they return. Ordered cheapest-first, so the common cases (suppression off, or
    // already auto-released) cost a single volatile read and no 64-bit clock arithmetic.
    private static bool ShouldSuppressInjected(IntPtr dwExtraInfo)
    {
        if (!_suppressInjected)
        {
            return false;
        }

        if (Volatile.Read(ref _suppressionAutoReleased) != 0)
        {
            return false;
        }

        var startedMs = Interlocked.Read(ref _suppressionStartedMilliseconds);
        if (startedMs != NotSuppressing
            && GetMonotonicMilliseconds() - startedMs >= InjectedSuppressionMaxDurationMs)
        {
            if (Interlocked.Exchange(ref _suppressionAutoReleased, 1) == 0)
            {
                // Logging here would do synchronous file I/O under a global lock on the
                // input path. Stage it for the tick drain so it is emitted exactly once.
                Interlocked.Exchange(ref _pendingSuppressionAutoReleaseLog, 1);
            }

            return false;
        }

        return !IsAllowlistedInjectedSource(dwExtraInfo);
    }

    private static volatile bool _ignoreScrollLock = true;
    public static bool IgnoreScrollLock
    {
        get => _ignoreScrollLock;
        set => _ignoreScrollLock = value;
    }

    // Fail-safe: if Windows reports recent input (GetLastInputInfo) but our hooks didn't see it,
    // treat it as activity unless it looks like a common "anti-idle" toggle key.
    private static volatile bool _useSystemIdleFailSafe = true;
    public static bool UseSystemIdleFailSafe
    {
        get => _useSystemIdleFailSafe;
        set => _useSystemIdleFailSafe = value;
    }
    public static int SystemIdleFailSafeWindowMs { get; set; } = 2000;

    // Diagnostics: non-zero if hook installation failed.
    public static int LastKeyboardHookError { get; private set; }
    public static int LastMouseHookError { get; private set; }

    // Read from outside _hookInstallLock (tray UI, tick timer), so the reads are volatile to
    // pair with the Volatile.Write publishes in the install/uninstall paths.
    public static bool KeyboardHookInstalled => Volatile.Read(ref _kbHook) != IntPtr.Zero;
    public static bool MouseHookInstalled => Volatile.Read(ref _msHook) != IntPtr.Zero;
    private static bool HooksFullyInstalled => KeyboardHookInstalled && MouseHookInstalled;

    // Store monotonic milliseconds instead of wall-clock DateTime values so idle
    // measurements remain accurate across clock adjustments and still support atomic
    // reads/writes via Interlocked.
    private static long _lastPhysicalInputMilliseconds = GetMonotonicMilliseconds();

    // VK code and timestamp of the last injected keystroke, packed into one 64-bit slot:
    // vkCode in the high 32 bits, the low 32 bits of the monotonic clock in the low 32.
    // These used to be two fields written by two separate Interlocked.Exchange calls and
    // read back as a pair, so a reader could pair a fresh timestamp with the PREVIOUS
    // key's VK code and misclassify a real keystroke as an ignorable anti-idle toggle.
    // One exchange makes the pair atomic.
    private const long NoInjectedKey = long.MinValue;
    private static long _lastInjectedKey = NoInjectedKey;

    private static IntPtr _kbHook = IntPtr.Zero;
    private static IntPtr _msHook = IntPtr.Zero;

    private static readonly LowLevelKeyboardProc _kbProc = KeyboardHookCallback;
    private static readonly LowLevelMouseProc _msProc = MouseHookCallback;

    // Explicit type to avoid ambiguity with System.Windows.Forms.Timer (global usings when WinForms is enabled).
    private static System.Threading.Timer? _gamepadTimer;

    private static readonly object _gamepadLock = new();
    private static readonly object _hookInstallLock = new();
    private const int HookRepairRetryIntervalMs = 30000;

    private static int _gamepadPollInProgress;
    private static int _gamepadStopRequested;
    private static int _keyboardHookExceptionLogged;
    private static int _mouseHookExceptionLogged;
    private static long _lastHookInstallAttemptMilliseconds = long.MinValue;

    // Hook callbacks must never call Logger: it does synchronous File.AppendAllText under a
    // global lock, and the callback blocks all system input until it returns. They stage a
    // message/flag here instead and DrainDeferredHookLogs() emits it from the tray app's
    // tick, which is the only entry point into PhysicalIdle that is called periodically.
    private static string? _pendingKeyboardHookExceptionMessage;
    private static string? _pendingMouseHookExceptionMessage;
    private static int _pendingSuppressionAutoReleaseLog;

    // Cached after the first successful resolution: EnsureHooksStarted runs every 30s while
    // a hook is missing, and Process.MainModule is an expensive way to learn something that
    // cannot change for the lifetime of the process. Only touched under _hookInstallLock.
    private static IntPtr _cachedModuleHandle = IntPtr.Zero;

    private static bool[] _gpConnected = new bool[4];
    private static uint[] _gpLastPacket = new uint[4];
    private static XINPUT_GAMEPAD[] _gpLastState = new XINPUT_GAMEPAD[4];

    // A disconnected XInput slot still costs a full device enumeration on every poll, so at
    // the default 250 ms cadence an empty rig burns 16 enumerations per second forever --
    // and GamepadCountsAsActivity defaults to true, so that IS the default. Back empty slots
    // off; connected slots keep the responsive cadence.
    private const int GamepadDisconnectedRetryMinMs = 2000;
    private const int GamepadDisconnectedRetryMaxMs = 4000;

    private static readonly long[] _gpNextPollMilliseconds = new long[4];
    private static readonly int[] _gpDisconnectedBackoffMs = new int[4];

    // Bounded wait for an in-flight gamepad callback to finish. The gamepad timer is stopped
    // from the UI thread, which is also the hook thread, so an unbounded wait on a hung
    // XInputGetState would stall the message pump and every low-level hook callback with it.
    private const int GamepadTimerDisposeWaitMs = 2000;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate uint XInputGetStateProc(uint dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // Environment.TickCount64 IS GetTickCount64 -- same counter, same resolution -- but the
    // JIT expands it inline, so it costs no P/Invoke transition. That matters because this
    // is called from the low-level hook callbacks, on the system input path.
    private static long GetMonotonicMilliseconds()
    {
        return Environment.TickCount64;
    }

    private static double GetSystemIdleMilliseconds()
    {
        try
        {
            var lii = new LASTINPUTINFO
            {
                cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
            };

            if (!GetLastInputInfo(ref lii))
            {
                return double.PositiveInfinity;
            }

            // The 64-bit tick count avoids the 49-day wraparound of GetTickCount.
            // LASTINPUTINFO.dwTime is still 32-bit, so mask it down to compare correctly.
            var tick64 = GetMonotonicMilliseconds();
            var lastInput32 = lii.dwTime;
            var tick32 = unchecked((uint)tick64);
            var idle = unchecked(tick32 - lastInput32); // handles 32-bit wraparound
            return idle;
        }
        catch
        {
            return double.PositiveInfinity;
        }
    }

    private static int GetEffectiveSystemIdleFailSafeWindowMs()
    {
        var configured = SystemIdleFailSafeWindowMs;
        if (configured < 0)
        {
            configured = 0;
        }

        return Math.Max(configured, AppConfig.MinimumSystemIdleFailSafeWindowMs);
    }

    // Packs the pair written by the keyboard hook. vkCode is masked to 16 bits (its source
    // is the WORD KBDLLHOOKSTRUCT.vkCode) so the packed value can never collide with the
    // NoInjectedKey sentinel, whatever an injector puts in the field.
    private static long PackInjectedKey(uint vkCode, long nowMs)
    {
        return ((long)(vkCode & 0xFFFF) << 32) | unchecked((uint)nowMs);
    }

    private static bool ShouldIgnoreSystemIdleSample(long nowMs, double systemIdleMs, int effectiveWindowMs)
    {
        var lastInjected = Interlocked.Read(ref _lastInjectedKey);
        if (lastInjected == NoInjectedKey)
        {
            return false;
        }

        // Unsigned 32-bit subtraction, so the age stays correct across the ~49.7-day
        // wraparound of the low half of the tick count.
        var ageMs = unchecked((uint)nowMs - (uint)lastInjected);
        var injectedAgeMs = (double)ageMs;
        var idleDeltaMs = Math.Abs(injectedAgeMs - systemIdleMs);
        return injectedAgeMs <= effectiveWindowMs + 250
            && idleDeltaMs <= 750
            && IsIgnoredInjectedKey(unchecked((uint)(lastInjected >> 32)));
    }

    private static bool IsIgnoredInjectedKey(uint vkCode)
    {
        switch (vkCode)
        {
            case 0x91: // ScrollLock
            case 0x14: // CapsLock
            case 0x90: // NumLock
            case 0x7E: // F15
            case 0x10: // Shift
            case 0x11: // Ctrl
            case 0x12: // Alt
            case 0xA0: // LShift
            case 0xA1: // RShift
            case 0xA2: // LCtrl
            case 0xA3: // RCtrl
            case 0xA4: // LAlt
            case 0xA5: // RAlt
                return true;
            default:
                return false;
        }
    }

    // XInput structs
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    // XInputGetState return codes
    private const uint ERROR_SUCCESS = 0;
    private const uint ERROR_DEVICE_NOT_CONNECTED = 1167;

    // Try xinput1_4 first, then fall back (Win7)
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState_1_4(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState_9_1_0(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState_1_3(uint dwUserIndex, out XINPUT_STATE pState);

    private enum XInputDll
    {
        Unknown = 0,
        XInput1_4 = 1,
        XInput9_1_0 = 2,
        XInput1_3 = 3,
        None = 4
    }

    private static volatile XInputDll _xinput = XInputDll.Unknown;

    private static bool TryResolveXInput(XInputGetStateProc probe, XInputDll candidate, string libraryName)
    {
        try
        {
            var probeResult = probe(0, out _);
            _xinput = candidate;

            if (probeResult != ERROR_SUCCESS && probeResult != ERROR_DEVICE_NOT_CONNECTED)
            {
                Logger.Warn($"XInput probe for {libraryName} returned unexpected status code {probeResult}.");
            }

            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            Logger.Warn($"XInput probe for {libraryName} failed because the DLL does not match the current process architecture.");
            return false;
        }
    }

    private static uint XInputGetStateSafe(uint idx, out XINPUT_STATE state)
    {
        state = default;

        if (_xinput == XInputDll.Unknown
            && !TryResolveXInput(XInputGetState_1_4, XInputDll.XInput1_4, "xinput1_4.dll")
            && !TryResolveXInput(XInputGetState_9_1_0, XInputDll.XInput9_1_0, "xinput9_1_0.dll")
            && !TryResolveXInput(XInputGetState_1_3, XInputDll.XInput1_3, "xinput1_3.dll"))
        {
            _xinput = XInputDll.None;
        }

        if (_xinput == XInputDll.None)
        {
            return ERROR_DEVICE_NOT_CONNECTED;
        }

        try
        {
            switch (_xinput)
            {
                case XInputDll.XInput1_4:
                    return XInputGetState_1_4(idx, out state);
                case XInputDll.XInput9_1_0:
                    return XInputGetState_9_1_0(idx, out state);
                case XInputDll.XInput1_3:
                    return XInputGetState_1_3(idx, out state);
                default:
                    return ERROR_DEVICE_NOT_CONNECTED;
            }
        }
        catch
        {
            return ERROR_DEVICE_NOT_CONNECTED;
        }
    }

    // CharSet.Unicode selects SetWindowsHookExW. There are no string parameters, so this only
    // affects which export is bound; the W form is the correct one to target.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    public static void Start()
    {
        // Both hook installation and gamepad timer start must be atomic so Stop() can
        // atomically uninstall hooks + stop the timer without a window in between.
        lock (_hookInstallLock)
        {
            EnsureHooksStarted(forceImmediateRetry: true);

            // Start gamepad polling (XInput) if enabled
            if (GamepadEnabled)
            {
                StartGamepadTimer();
            }
        }
    }

    public static void TryRepairHooksIfNeeded()
    {
        DrainDeferredHookLogs();
        EnsureHooksStarted(forceImmediateRetry: false);
    }

    // Emits whatever the hook callbacks staged instead of logging inline. This is called
    // from TryRepairHooksIfNeeded() because that is the one PhysicalIdle entry point the
    // tray app already calls on every tick: PhysicalIdle owns no timer of its own once
    // gamepad polling is off, so there is nowhere else to drain from without a timer that
    // exists only to log.
    private static void DrainDeferredHookLogs()
    {
        var keyboardMessage = Interlocked.Exchange(ref _pendingKeyboardHookExceptionMessage, null);
        if (keyboardMessage != null)
        {
            Logger.Warn($"Keyboard hook callback failed. Input will be passed through and idle tracking will continue in a degraded state. Error='{keyboardMessage}'.");
        }

        var mouseMessage = Interlocked.Exchange(ref _pendingMouseHookExceptionMessage, null);
        if (mouseMessage != null)
        {
            Logger.Warn($"Mouse hook callback failed. Input will be passed through and idle tracking will continue in a degraded state. Error='{mouseMessage}'.");
        }

        if (Interlocked.Exchange(ref _pendingSuppressionAutoReleaseLog, 0) != 0)
        {
            Logger.Warn($"Injected input suppression auto-released after {InjectedSuppressionMaxDurationMs / 1000} seconds and injected input is being passed through again. This safety net exists so a user whose only input path is injected (On-Screen Keyboard, eye control, AutoHotkey, Mouse Without Borders) cannot be locked out of the desktop. Suppression re-arms the next time it is switched off and back on.");
        }
    }

    private static void EnsureHooksStarted(bool forceImmediateRetry)
    {
        if (HooksFullyInstalled)
        {
            return;
        }

        var nowMs = GetMonotonicMilliseconds();

        lock (_hookInstallLock)
        {
            if (HooksFullyInstalled)
            {
                return;
            }

            var lastAttemptMs = Interlocked.Read(ref _lastHookInstallAttemptMilliseconds);
            if (!forceImmediateRetry
                && lastAttemptMs != long.MinValue
                && nowMs - lastAttemptMs < HookRepairRetryIntervalMs)
            {
                return;
            }

            Interlocked.Exchange(ref _lastHookInstallAttemptMilliseconds, nowMs);

            // MainModule can be null (and/or throw) in some hosting scenarios.
            // Null-safe handling prevents nullable warnings and matches the original intent:
            // try to provide a module handle, but fall back to IntPtr.Zero if unavailable.
            var hMod = TryGetCurrentModuleHandle();

            var keyboardWasInstalled = _kbHook != IntPtr.Zero;
            var mouseWasInstalled = _msHook != IntPtr.Zero;

            if (!keyboardWasInstalled)
            {
                InstallKeyboardHook(hMod);
            }

            if (!mouseWasInstalled)
            {
                InstallMouseHook(hMod);
            }

            // Reset the idle clock ONLY when a hook was newly installed on this attempt.
            //
            // A freshly installed hook has no input history, so treating "now" as the last
            // physical input is the conservative choice -- it avoids claiming the user was idle
            // through a period we were not actually watching.
            //
            // The previous condition was `_kbHook != Zero || _msHook != Zero`, which is satisfied
            // by an ALREADY-installed hook. With one hook installed and the other permanently
            // failing, HooksFullyInstalled never becomes true, so this body ran every 30s and the
            // surviving hook reset the clock every time. Measured idle was pinned below 30s
            // forever and no configured threshold above that was ever reachable: the app looked
            // healthy and silently never launched.
            var newlyInstalledAnyHook =
                (!keyboardWasInstalled && _kbHook != IntPtr.Zero)
                || (!mouseWasInstalled && _msHook != IntPtr.Zero);

            if (newlyInstalledAnyHook)
            {
                Interlocked.Exchange(ref _lastPhysicalInputMilliseconds, nowMs);
            }
        }
    }

    private static IntPtr TryGetCurrentModuleHandle()
    {
        // Only ever called under _hookInstallLock, so a plain field read/write is enough.
        if (_cachedModuleHandle != IntPtr.Zero)
        {
            return _cachedModuleHandle;
        }

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var moduleName = currentProcess.MainModule?.ModuleName;

            if (!string.IsNullOrWhiteSpace(moduleName))
            {
                // Cache successes only; a failure may be transient and is cheap to retry
                // at the 30s repair cadence.
                _cachedModuleHandle = GetModuleHandle(moduleName);
                return _cachedModuleHandle;
            }
        }
        catch
        {
        }

        return IntPtr.Zero;
    }

    private static void InstallKeyboardHook(IntPtr hMod)
    {
        LastKeyboardHookError = 0;
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);

        if (hook == IntPtr.Zero)
        {
            LastKeyboardHookError = Marshal.GetLastWin32Error();

            // Some hosts (in-memory assemblies) behave better with a null module handle.
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, IntPtr.Zero, 0);

            if (hook == IntPtr.Zero)
            {
                LastKeyboardHookError = Marshal.GetLastWin32Error();
            }
            else
            {
                LastKeyboardHookError = 0;
            }
        }

        // Publish with a release write: KeyboardHookInstalled and the hook callback both
        // read _kbHook outside _hookInstallLock. Stop() already writes it this way.
        Volatile.Write(ref _kbHook, hook);
    }

    private static void InstallMouseHook(IntPtr hMod)
    {
        LastMouseHookError = 0;
        var hook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, hMod, 0);

        if (hook == IntPtr.Zero)
        {
            LastMouseHookError = Marshal.GetLastWin32Error();
            hook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, IntPtr.Zero, 0);

            if (hook == IntPtr.Zero)
            {
                LastMouseHookError = Marshal.GetLastWin32Error();
            }
            else
            {
                LastMouseHookError = 0;
            }
        }

        Volatile.Write(ref _msHook, hook);
    }

    public static void Stop()
    {
        // Hold the install lock for the entire stop sequence so Start() cannot
        // reinstall hooks or start the timer while we are tearing down.
        lock (_hookInstallLock)
        {
            StopGamepadTimer();

            if (_kbHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_kbHook);
                Volatile.Write(ref _kbHook, IntPtr.Zero);
            }

            if (_msHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_msHook);
                Volatile.Write(ref _msHook, IntPtr.Zero);
            }
        }
    }

    public static void SetGamepadEnabled(bool enabled)
    {
        // Same lock Start()/Stop() hold, for the same reason: without it a menu toggle can
        // interleave with Stop() and leave a gamepad timer polling after teardown, still
        // writing _lastPhysicalInputMilliseconds. Lock order is _hookInstallLock ->
        // _gamepadLock (Start/StopGamepadTimer take _gamepadLock); do not invert it.
        lock (_hookInstallLock)
        {
            GamepadEnabled = enabled;

            if (enabled)
            {
                StartGamepadTimer();
            }
            else
            {
                StopGamepadTimer();
            }
        }
    }

    public static double GetIdleMilliseconds()
    {
        var nowMs = GetMonotonicMilliseconds();
        var lastPhysMs = Interlocked.Read(ref _lastPhysicalInputMilliseconds);
        var phys = (double)Math.Max(0, nowMs - lastPhysMs);

        if (!UseSystemIdleFailSafe)
        {
            return phys;
        }

        var sys = GetSystemIdleMilliseconds();
        if (double.IsPositiveInfinity(sys))
        {
            return phys;
        }

        var effectiveWindowMs = GetEffectiveSystemIdleFailSafeWindowMs();

        if (!HooksFullyInstalled)
        {
            if (ShouldIgnoreSystemIdleSample(nowMs, sys, effectiveWindowMs))
            {
                return phys;
            }

            if (sys < phys)
            {
                var syncedMs = Math.Max(0, nowMs - (long)sys);
                Interlocked.Exchange(ref _lastPhysicalInputMilliseconds, syncedMs);
                return sys;
            }

            return phys;
        }

        // When hooks are healthy, use the more conservative (smaller) idle estimate.
        // This corrects both downward (hooks overcount) and upward (hooks missed activity).
        if (sys < phys)
        {
            // System idle is smaller → hooks may have overcounted (e.g., missed an input).
            // Only trust this if it's within the fail-safe window (avoids false corrections
            // from recent inputs that the system saw but hooks also saw correctly).
            if (sys <= effectiveWindowMs && !ShouldIgnoreSystemIdleSample(nowMs, sys, effectiveWindowMs))
            {
                var syncedMs = Math.Max(0, nowMs - (long)sys);
                Interlocked.Exchange(ref _lastPhysicalInputMilliseconds, syncedMs);
                return sys;
            }

            return phys;
        }

        // System idle >= physical idle → hooks are working correctly. Trust physical.
        return phys;
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                // Use generic overload to avoid nullable/unboxing warnings.
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                var injected = (data.flags & LLKHF_INJECTED) != 0 || (data.flags & LLKHF_LOWER_IL_INJECTED) != 0;
                var isScroll = data.vkCode == VK_SCROLL;

                if (injected)
                {
                    Interlocked.Exchange(
                        ref _lastInjectedKey,
                        PackInjectedKey(data.vkCode, GetMonotonicMilliseconds()));
                }

                if (!injected && !(IgnoreScrollLock && isScroll))
                {
                    Interlocked.Exchange(ref _lastPhysicalInputMilliseconds, GetMonotonicMilliseconds());
                }

                if (injected && ShouldSuppressInjected(data.dwExtraInfo))
                {
                    return (IntPtr)1; // swallow injected keypress
                }
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _keyboardHookExceptionLogged, 1) == 0)
            {
                try
                {
                    // Stage the message; DrainDeferredHookLogs() writes it from the tick.
                    // Logging here would block all system input on a file write.
                    Interlocked.Exchange(ref _pendingKeyboardHookExceptionMessage, ex.Message);
                }
                catch
                {
                    // Never let diagnostics escape into the input path.
                }
            }
        }

        return CallNextHookEx(Volatile.Read(ref _kbHook), nCode, wParam, lParam);
    }

    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                // Use generic overload to avoid nullable/unboxing warnings.
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                var injected = (data.flags & LLMHF_INJECTED) != 0 || (data.flags & LLMHF_LOWER_IL_INJECTED) != 0;

                if (!injected)
                {
                    Interlocked.Exchange(ref _lastPhysicalInputMilliseconds, GetMonotonicMilliseconds());
                }

                if (injected && ShouldSuppressInjected(data.dwExtraInfo))
                {
                    return (IntPtr)1; // swallow injected mouse event
                }
            }
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _mouseHookExceptionLogged, 1) == 0)
            {
                try
                {
                    // Stage the message; DrainDeferredHookLogs() writes it from the tick.
                    // Logging here would block all system input on a file write.
                    Interlocked.Exchange(ref _pendingMouseHookExceptionMessage, ex.Message);
                }
                catch
                {
                    // Never let diagnostics escape into the input path.
                }
            }
        }

        return CallNextHookEx(Volatile.Read(ref _msHook), nCode, wParam, lParam);
    }

    private static void StartGamepadTimer()
    {
        lock (_gamepadLock)
        {
            if (_gamepadTimer != null)
            {
                return;
            }

            var interval = GamepadPollMilliseconds;

            if (interval < 50)
            {
                interval = 50;
            }

            if (interval > 2000)
            {
                interval = 2000;
            }

            Interlocked.Exchange(ref _gamepadStopRequested, 0);
            Interlocked.Exchange(ref _gamepadPollInProgress, 0);

            // Reset controller tracking inside the lock so PollGamepads never sees partial state.
            for (var i = 0; i < 4; i++)
            {
                _gpConnected[i] = false;
                _gpLastPacket[i] = 0;
                _gpLastState[i] = default;

                // Zero, not "now + backoff": every slot is probed once on the first poll so a
                // controller that is already plugged in is picked up immediately.
                _gpNextPollMilliseconds[i] = 0;
                _gpDisconnectedBackoffMs[i] = 0;
            }

            // Ensure array writes are visible to the timer callback thread before it starts.
            // The timer callback runs on a thread pool thread and may start immediately (dueTime=0).
            Thread.MemoryBarrier();

            _gamepadTimer = new System.Threading.Timer(PollGamepads, null, 0, interval);
        }
    }

    private static void StopGamepadTimer()
    {
        System.Threading.Timer? timerToDispose;

        lock (_gamepadLock)
        {
            if (_gamepadTimer == null)
            {
                return;
            }

            timerToDispose = _gamepadTimer;
            _gamepadTimer = null;
            Interlocked.Exchange(ref _gamepadStopRequested, 1);

            try
            {
                timerToDispose.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Ignore.
            }
        }

        var disposedEvent = new ManualResetEvent(false);
        var disposeEventHere = true;

        try
        {
            // Bounded wait for the in-flight callback. The callback checks
            // _gamepadStopRequested on entry and _gamepadPollInProgress prevents re-entry,
            // so it normally exits at once -- but this runs on the UI thread, which is also
            // the hook thread, and a hung XInputGetState would otherwise stall the message
            // pump and every low-level hook callback with it. Give up and carry on instead.
            if (timerToDispose.Dispose(disposedEvent))
            {
                if (!disposedEvent.WaitOne(GamepadTimerDisposeWaitMs))
                {
                    // The timer still owns this handle and will signal it when the callback
                    // finally returns, so disposing it now would leave the runtime setting a
                    // closed handle. Deliberately leak one event rather than risk that; it
                    // can only happen once per stop, and only when XInput is already hung.
                    disposeEventHere = false;

                    Logger.Warn(
                        $"Gamepad poll did not finish within {GamepadTimerDisposeWaitMs} ms of the timer being stopped; continuing without waiting. XInputGetState is most likely blocked in a driver.");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore.
        }
        finally
        {
            if (disposeEventHere)
            {
                disposedEvent.Dispose();
            }

            Interlocked.Exchange(ref _gamepadPollInProgress, 0);
        }
    }

    private static void PollGamepads(object? stateObj)
    {
        if (!GamepadEnabled || Interlocked.CompareExchange(ref _gamepadStopRequested, 0, 0) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _gamepadPollInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (!GamepadEnabled || Interlocked.CompareExchange(ref _gamepadStopRequested, 0, 0) != 0)
            {
                return;
            }

            var nowMs = GetMonotonicMilliseconds();

            for (uint i = 0; i < 4; i++)
            {
                if (Interlocked.CompareExchange(ref _gamepadStopRequested, 0, 0) != 0)
                {
                    return;
                }

                // Slots that were empty last time are only re-probed when their backoff
                // expires. Connected slots are never skipped, so input latency is unchanged
                // for anyone who actually has a controller.
                if (!_gpConnected[i] && nowMs < _gpNextPollMilliseconds[i])
                {
                    continue;
                }

                var rc = XInputGetStateSafe(i, out var state);

                if (rc == ERROR_SUCCESS)
                {
                    _gpNextPollMilliseconds[i] = 0;
                    _gpDisconnectedBackoffMs[i] = 0;

                    if (!_gpConnected[i])
                    {
                        _gpConnected[i] = true;
                        _gpLastPacket[i] = state.dwPacketNumber;
                        _gpLastState[i] = state.Gamepad;
                        continue;
                    }

                    if (state.dwPacketNumber != _gpLastPacket[i])
                    {
                        if (IsMeaningfulGamepadChange(_gpLastState[i], state.Gamepad))
                        {
                            Interlocked.Exchange(ref _lastPhysicalInputMilliseconds, GetMonotonicMilliseconds());
                        }

                        _gpLastPacket[i] = state.dwPacketNumber;
                        _gpLastState[i] = state.Gamepad;
                    }
                }
                else
                {
                    _gpConnected[i] = false;

                    var backoffMs = _gpDisconnectedBackoffMs[i] == 0
                        ? GamepadDisconnectedRetryMinMs
                        : Math.Min(_gpDisconnectedBackoffMs[i] * 2, GamepadDisconnectedRetryMaxMs);

                    _gpDisconnectedBackoffMs[i] = backoffMs;
                    _gpNextPollMilliseconds[i] = nowMs + backoffMs;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _gamepadPollInProgress, 0);
        }
    }

    private static bool IsThumbActive(short x, short y, int deadzone)
    {
        var ix = x;
        var iy = y;

        var magSq = (long)ix * ix + (long)iy * iy;
        var dzSq = (long)deadzone * deadzone;

        return magSq > dzSq;
    }

    private static bool IsMeaningfulGamepadChange(XINPUT_GAMEPAD prev, XINPUT_GAMEPAD cur)
    {
        // Buttons/D-pad
        if (prev.wButtons != cur.wButtons)
        {
            return true;
        }

        // Triggers (ignore tiny jitter below threshold)
        if (prev.bLeftTrigger >= XINPUT_GAMEPAD_TRIGGER_THRESHOLD
            || cur.bLeftTrigger >= XINPUT_GAMEPAD_TRIGGER_THRESHOLD)
        {
            if (Math.Abs(cur.bLeftTrigger - prev.bLeftTrigger) >= 4)
            {
                return true;
            }
        }

        if (prev.bRightTrigger >= XINPUT_GAMEPAD_TRIGGER_THRESHOLD
            || cur.bRightTrigger >= XINPUT_GAMEPAD_TRIGGER_THRESHOLD)
        {
            if (Math.Abs(cur.bRightTrigger - prev.bRightTrigger) >= 4)
            {
                return true;
            }
        }

        // Sticks: ignore drift inside deadzone; count meaningful motion outside.
        var prevLeftActive = IsThumbActive(prev.sThumbLX, prev.sThumbLY, XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE);
        var curLeftActive = IsThumbActive(cur.sThumbLX, cur.sThumbLY, XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE);

        if (prevLeftActive != curLeftActive)
        {
            return true;
        }

        if (curLeftActive)
        {
            if (Math.Abs(cur.sThumbLX - prev.sThumbLX) >= 500)
            {
                return true;
            }

            if (Math.Abs(cur.sThumbLY - prev.sThumbLY) >= 500)
            {
                return true;
            }
        }

        var prevRightActive = IsThumbActive(prev.sThumbRX, prev.sThumbRY, XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE);
        var curRightActive = IsThumbActive(cur.sThumbRX, cur.sThumbRY, XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE);

        if (prevRightActive != curRightActive)
        {
            return true;
        }

        if (curRightActive)
        {
            if (Math.Abs(cur.sThumbRX - prev.sThumbRX) >= 500)
            {
                return true;
            }

            if (Math.Abs(cur.sThumbRY - prev.sThumbRY) >= 500)
            {
                return true;
            }
        }

        return false;
    }
}
