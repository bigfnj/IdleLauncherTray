using System;
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

    public bool TryNextValue(out float percent)
    {
        percent = 0;

        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return false;
        }

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

        // kernel includes idle time. Clamp anomalies conservatively rather than underflowing.
        var busy = idleDelta >= total ? 0UL : total - idleDelta;
        var pct = (busy * 100.0) / total;

        if (pct > 100)
        {
            pct = 100;
        }

        percent = (float)pct;
        return true;
    }

    private static ulong ToUInt64(FILETIME ft)
    {
        return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    }
}
