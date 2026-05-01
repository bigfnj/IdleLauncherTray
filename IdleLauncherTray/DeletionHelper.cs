using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace IdleLauncherTray;

internal static class DeletionHelper
{
    private const string CleanupFolderArg = "--cleanup-folder";
    private const string CleanupParentPidArg = "--cleanup-parent-pid";
    private const string CleanupWaitMsArg = "--cleanup-wait-ms";

    private const int DefaultInitialWaitMs = 2000;
    private const int MaxDeleteAttempts = 12;
    private const int DeleteRetryDelayMs = 500;

    public static void ScheduleFolderDelete(string folderToDelete)
    {
        var normalizedFolder = NormalizeFolderPath(folderToDelete);
        if (!IsSafeDeleteTarget(normalizedFolder))
        {
            return;
        }

        var helperExePath = AppPaths.CurrentExePath;
        if (string.IsNullOrWhiteSpace(helperExePath) || !File.Exists(helperExePath))
        {
            TryDeleteFolderWithRetries(normalizedFolder, initialWaitMs: DefaultInitialWaitMs);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = helperExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        psi.ArgumentList.Add(CleanupFolderArg);
        psi.ArgumentList.Add(normalizedFolder);
        psi.ArgumentList.Add(CleanupParentPidArg);
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(CleanupWaitMsArg);
        psi.ArgumentList.Add(DefaultInitialWaitMs.ToString(CultureInfo.InvariantCulture));

        try
        {
            using var child = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to start deferred cleanup helper for '{normalizedFolder}'. Falling back to an in-process deletion attempt. Error='{ex.Message}'.");
            TryDeleteFolderWithRetries(normalizedFolder, initialWaitMs: DefaultInitialWaitMs);
        }
    }

    public static bool TryRunCleanupFromCommandLine(string[] args)
    {
        if (!TryParseCleanupArgs(args, out var folderToDelete, out var parentPid, out var initialWaitMs))
        {
            return false;
        }

        RunCleanupWorker(folderToDelete, parentPid, initialWaitMs);
        return true;
    }

    private static void RunCleanupWorker(string folderToDelete, int parentPid, int initialWaitMs)
    {
        var normalizedFolder = NormalizeFolderPath(folderToDelete);
        if (!IsSafeDeleteTarget(normalizedFolder))
        {
            return;
        }

        WaitForParentExit(parentPid, initialWaitMs);
        TryDeleteFolderWithRetries(normalizedFolder, initialWaitMs: 0);
    }

    private static void WaitForParentExit(int parentPid, int fallbackWaitMs)
    {
        if (parentPid > 0)
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                if (!parent.HasExited)
                {
                    var waitMs = Math.Max(fallbackWaitMs, DefaultInitialWaitMs);
                    parent.WaitForExit(waitMs);
                }

                return;
            }
            catch (ArgumentException)
            {
                return;
            }
            catch
            {
                // Fall back to a bounded sleep below.
            }
        }

        if (fallbackWaitMs > 0)
        {
            Thread.Sleep(fallbackWaitMs);
        }
    }

    private static bool TryParseCleanupArgs(string[] args, out string folderToDelete, out int parentPid, out int initialWaitMs)
    {
        folderToDelete = string.Empty;
        parentPid = 0;
        initialWaitMs = DefaultInitialWaitMs;

        if (args.Length < 2)
        {
            return false;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, CleanupFolderArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                folderToDelete = args[++i];
                continue;
            }

            if (string.Equals(arg, CleanupParentPidArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid) && parsedPid > 0)
                {
                    parentPid = parsedPid;
                }

                continue;
            }

            if (string.Equals(arg, CleanupWaitMsArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWaitMs) && parsedWaitMs >= 0)
                {
                    initialWaitMs = parsedWaitMs;
                }
            }
        }

        return !string.IsNullOrWhiteSpace(folderToDelete);
    }

    private static string NormalizeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return string.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(folderPath.Trim());
            var root = Path.GetPathRoot(fullPath);

            if (!string.IsNullOrWhiteSpace(root) && fullPath.Length > root.Length)
            {
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return fullPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsSafeDeleteTarget(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        var root = Path.GetPathRoot(folderPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return !string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDeleteFolderWithRetries(string folderPath, int initialWaitMs)
    {
        if (initialWaitMs > 0)
        {
            Thread.Sleep(initialWaitMs);
        }

        for (var attempt = 0; attempt < MaxDeleteAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    return true;
                }

                ClearReadOnlyAttributes(folderPath);
                Directory.Delete(folderPath, recursive: true);

                if (!Directory.Exists(folderPath))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Retry below.
            }
            catch (UnauthorizedAccessException)
            {
                // Retry below.
            }
            catch
            {
                // Give other transient errors one more chance via the retry loop.
            }

            Thread.Sleep(DeleteRetryDelayMs);
        }

        return !Directory.Exists(folderPath);
    }

    private static void ClearReadOnlyAttributes(string folderPath)
    {
        try
        {
            var rootDirectory = new DirectoryInfo(folderPath);
            if (rootDirectory.Exists)
            {
                rootDirectory.Attributes = FileAttributes.Normal;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(folderPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                catch
                {
                    // Ignore individual entries and let the delete attempt decide.
                }
            }
        }
        catch
        {
            // Ignore best-effort cleanup failures.
        }
    }
}
