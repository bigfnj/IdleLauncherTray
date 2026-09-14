using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IdleLauncherTray;

/// <summary>
/// Lightweight total CPU usage monitor using GetSystemTimes (no PerformanceCounter required).
/// Returns % CPU used (0..100) across all cores.
/// </summary>
internal sealed class CpuUsageMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _initialized;

    /// <summary>
    /// The Win32 error from the last <c>GetSystemTimes</c> call that FAILED, or <c>null</c>
    /// if the last call succeeded.
    /// <para>
    /// Recorded rather than logged, following <c>PhysicalIdle.LastKeyboardHookError</c>:
    /// <see cref="TryNextValue"/> runs once per tick on the UI thread, so logging in here
    /// would put filesystem I/O on the message pump every five seconds and, on a persistent
    /// failure, would write twelve identical lines a minute forever. The caller already
    /// counts consecutive unusable samples and knows when the run of failures is worth one
    /// line.
    /// </para>
    /// <para>
    /// Note that <c>null</c> does NOT mean "no problem". Three of the four ways this method
    /// returns false leave it null, because the P/Invoke succeeded and the sample was
    /// discarded afterwards. <see cref="DescribeLastSampleFailure"/> says which.
    /// </para>
    /// </summary>
    public int? LastSampleError { get; private set; }

    public bool TryNextValue(out float percent)
    {
        percent = 0;

        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            // Read IMMEDIATELY after the failed call, before anything else can run. The
            // last-error value is thread-local and the very next P/Invoke on this thread
            // overwrites it -- including one made by the logging or formatting code that
            // might otherwise look like a natural place to put this.
            LastSampleError = Marshal.GetLastPInvokeError();
            return false;
        }

        LastSampleError = null;

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);

        if (!_initialized)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _initialized = true;
            return false;
        }

        // GetSystemTimes returns FILETIME values (100ns ticks since 1601). They
        // monotonically increase, but a counter wrap, system clock change, or a
        // driver that reports a non-monotonic value could leave one of the new
        // samples smaller than the previous one. With unsigned arithmetic that
        // would silently underflow and produce a wildly inflated delta. Detect
        // the regression, re-baseline, and skip this sample so the caller
        // doesn't see a garbage CPU reading.
        if (idle < _prevIdle || kernel < _prevKernel || user < _prevUser)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            return false;
        }

        var idleDelta = idle - _prevIdle;
        var kernelDelta = kernel - _prevKernel;
        var userDelta = user - _prevUser;

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        var total = kernelDelta + userDelta;
        if (total == 0)
        {
            return false;
        }

        // kernel includes idle time. Clamping busy here is THE bound on the result, and it is
        // why no second clamp follows: `busy` is at most `total`, so `pct` is at most exactly
        // 100.0 (reached when the window contained no idle time at all) and can never exceed it.
        // A trailing `if (pct > 100) pct = 100;` used to sit below this; it could not fire under
        // any input and read as a live safety net while being nothing of the kind. If you ever
        // relax the line below, that is the moment an upper clamp becomes necessary again.
        var busy = idleDelta >= total ? 0UL : total - idleDelta;
        var pct = (busy * 100.0) / total;

        percent = (float)pct;
        return true;
    }

    /// <summary>
    /// A human-readable account of the most recent sample, for the caller to log once it has
    /// decided a run of unusable samples is worth reporting.
    /// </summary>
    public string DescribeLastSampleFailure()
    {
        return DescribeSampleFailure(LastSampleError);
    }

    /// <summary>
    /// Pure: the whole point of splitting it out is that the three cases can be exercised
    /// without needing <c>GetSystemTimes</c> to fail, which is not something a test can
    /// arrange on a healthy machine.
    /// </summary>
    private static string DescribeSampleFailure(int? errorCode)
    {
        if (errorCode is null)
        {
            // NOT "everything is fine". The P/Invoke succeeded, so if the caller is asking
            // this question at all, the samples are being thrown away AFTER the call for one
            // of the three reasons below. Naming them is a real diagnosis: a stuck sampler
            // that never leaves priming looks nothing like a machine whose counters keep
            // regressing, and the difference decides where to look next.
            return "GetSystemTimes succeeded, so the sample was discarded after the call: the "
                + "monitor was priming its first reading, a counter went backwards (clock change, "
                + "wrap, or a driver reporting a non-monotonic value), or the window between two "
                + "samples was shorter than one system clock tick.";
        }

        if (errorCode.Value == 0)
        {
            return "GetSystemTimes returned false (no Win32 error reported).";
        }

        // Translate the bare code into the system's localised message, the same way
        // WorkstationLock does, so the log carries a sentence rather than a number to go
        // and look up. The numeric code stays in the message because the localised text
        // differs per machine language and the number is what a search engine matches.
        string systemMessage;
        try
        {
            systemMessage = new Win32Exception(errorCode.Value).Message;
        }
        catch
        {
            systemMessage = "unknown";
        }

        return $"GetSystemTimes failed: {systemMessage} (Win32 error {errorCode.Value}).";
    }

    private static ulong ToUInt64(FILETIME ft)
    {
        return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    }
}
