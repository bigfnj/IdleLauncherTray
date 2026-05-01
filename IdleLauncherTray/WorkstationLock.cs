using System;
using System.Runtime.InteropServices;

namespace IdleLauncherTray;

internal static class WorkstationLock
{
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public static bool TryLock(out string errorMessage)
    {
        try
        {
            if (LockWorkStation())
            {
                errorMessage = string.Empty;
                return true;
            }

            var errorCode = Marshal.GetLastWin32Error();
            errorMessage = errorCode == 0
                ? "LockWorkStation returned false."
                : $"LockWorkStation failed with Win32 error {errorCode}.";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
