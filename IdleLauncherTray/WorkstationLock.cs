using System;
using System.ComponentModel;
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
            if (errorCode == 0)
            {
                errorMessage = "LockWorkStation returned false (no Win32 error reported).";
                return false;
            }

            // Translate the bare error code into the system's localised message
            // (e.g. 1314 -> "A required privilege is not held by the client.")
            // so logs and any future user-facing UI show something useful.
            string systemMessage;
            try
            {
                systemMessage = new Win32Exception(errorCode).Message;
            }
            catch
            {
                systemMessage = "unknown";
            }

            errorMessage = $"LockWorkStation failed: {systemMessage} (Win32 error {errorCode}).";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
