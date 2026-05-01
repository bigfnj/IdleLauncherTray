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
    public static bool SuppressInjected
    {
        get => _suppressInjected;
        set => _suppressInjected = value;
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

    public static bool KeyboardHookInstalled => _kbHook != IntPtr.Zero;
    public static bool MouseHookInstalled => _msHook != IntPtr.Zero;
    private static bool HooksFullyInstalled => KeyboardHookInstalled && MouseHookInstalled;

    // Store monotonic milliseconds instead of wall-clock DateTime values so idle
    // measurements remain accurate across clock adjustments and still support atomic
    // reads/writes via Interlocked.
    private static long _lastPhysicalInputMilliseconds = GetMonotonicMilliseconds();
    private static long _lastInjectedKeyMilliseconds = long.MinValue;
    private static int _lastInjectedVkCode;

    private static IntPtr _kbHook = IntPtr.Zero;
    private static IntPtr _msHook = IntPtr.Zero;

    private static LowLevelKeyboardProc _kbProc = KeyboardHookCallback;
    private static LowLevelMouseProc _msProc = MouseHookCallback;

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

    private static bool[] _gpConnected = new bool[4];
    private static uint[] _gpLastPacket = new uint[4];
    private static XINPUT_GAMEPAD[] _gpLastState = new XINPUT_GAMEPAD[4];

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

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private static long GetMonotonicMilliseconds()
    {
        return unchecked((long)GetTickCount64());
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

            // GetTickCount64 avoids the 49-day wraparound of GetTickCount.
            // LASTINPUTINFO.dwTime is still 32-bit, so mask GetTickCount64 to compare correctly.
            var tick64 = GetTickCount64();
            var lastInput32 = lii.dwTime;
            var tick32 = (uint)(tick64 & 0xFFFFFFFF);
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

    private static bool ShouldIgnoreSystemIdleSample(long nowMs, double systemIdleMs, int effectiveWindowMs)
    {
        var lastInjMs = Interlocked.Read(ref _lastInjectedKeyMilliseconds);
        if (lastInjMs == long.MinValue)
        {
            return false;
        }

        var injectedAgeMs = (double)Math.Max(0, nowMs - lastInjMs);
        var idleDeltaMs = Math.Abs(injectedAgeMs - systemIdleMs);
        return injectedAgeMs <= effectiveWindowMs + 250
            && idleDeltaMs <= 750
            && IsIgnoredInjectedKey(unchecked((uint)Interlocked.CompareExchange(ref _lastInjectedVkCode, 0, 0)));
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
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
        EnsureHooksStarted(forceImmediateRetry: false);
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

            if (_kbHook == IntPtr.Zero)
            {
                InstallKeyboardHook(hMod);
            }

            if (_msHook == IntPtr.Zero)
            {
                InstallMouseHook(hMod);
            }

            if (_kbHook != IntPtr.Zero || _msHook != IntPtr.Zero)
            {
                Interlocked.Exchange(ref _lastPhysicalInputMilliseconds, nowMs);
            }
        }
    }

    private static IntPtr TryGetCurrentModuleHandle()
    {
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var moduleName = currentProcess.MainModule?.ModuleName;

            if (!string.IsNullOrWhiteSpace(moduleName))
            {
                return GetModuleHandle(moduleName);
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
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);

        if (_kbHook == IntPtr.Zero)
        {
            LastKeyboardHookError = Marshal.GetLastWin32Error();

            // Some hosts (in-memory assemblies) behave better with a null module handle.
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, IntPtr.Zero, 0);

            if (_kbHook == IntPtr.Zero)
            {
                LastKeyboardHookError = Marshal.GetLastWin32Error();
            }
            else
            {
                LastKeyboardHookError = 0;
            }
        }
    }

    private static void InstallMouseHook(IntPtr hMod)
    {
        LastMouseHookError = 0;
        _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, hMod, 0);

        if (_msHook == IntPtr.Zero)
        {
            LastMouseHookError = Marshal.GetLastWin32Error();
            _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, IntPtr.Zero, 0);

            if (_msHook == IntPtr.Zero)
            {
                LastMouseHookError = Marshal.GetLastWin32Error();
            }
            else
            {
                LastMouseHookError = 0;
            }
        }
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
                    Interlocked.Exchange(ref _lastInjectedKeyMilliseconds, GetMonotonicMilliseconds());
                    Interlocked.Exchange(ref _lastInjectedVkCode, unchecked((int)data.vkCode));
                }

                if (!injected && !(IgnoreScrollLock && isScroll))
                {
                    Interlocked.Exchange(ref _lastPhysicalInputMilliseconds, GetMonotonicMilliseconds());
                }

                if (injected && SuppressInjected)
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
                    Logger.Warn($"Keyboard hook callback failed. Input will be passed through and idle tracking will continue in a degraded state. Error='{ex.Message}'.");
                }
                catch
                {
                    // Ignore logging failures inside the hook callback.
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

                if (injected && SuppressInjected)
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
                    Logger.Warn($"Mouse hook callback failed. Input will be passed through and idle tracking will continue in a degraded state. Error='{ex.Message}'.");
                }
                catch
                {
                    // Ignore logging failures inside the hook callback.
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

        try
        {
            using var disposedEvent = new ManualResetEvent(false);
            // Wait indefinitely for the in-flight callback to finish. The callback checks
            // _gamepadStopRequested on entry and _gamepadPollInProgress prevents re-entry,
            // so it should exit promptly unless XInput itself is hung (which we can't fix).
            if (timerToDispose.Dispose(disposedEvent))
            {
                disposedEvent.WaitOne();
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore.
        }
        finally
        {
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

            for (uint i = 0; i < 4; i++)
            {
                if (Interlocked.CompareExchange(ref _gamepadStopRequested, 0, 0) != 0)
                {
                    return;
                }

                var rc = XInputGetStateSafe(i, out var state);

                if (rc == ERROR_SUCCESS)
                {
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
