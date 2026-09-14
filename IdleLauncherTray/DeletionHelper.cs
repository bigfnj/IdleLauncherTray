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
            if (child != null)
            {
                // Note: this runs in the *outgoing* tray process. We do NOT block on
                // WaitForExit() here because the parent is about to ExitThread —
                // doing so would deadlock. Instead the cleanup child waits for our
                // PID to exit (see RunCleanupWorker) before doing the actual delete.
                Logger.Info($"Spawned deferred cleanup helper for '{normalizedFolder}'. ChildPid={child.Id}.");
            }
            else
            {
                Logger.Warn($"Process.Start returned null when spawning cleanup helper for '{normalizedFolder}'. Falling back to in-process deletion.");
                TryDeleteFolderWithRetries(normalizedFolder, initialWaitMs: DefaultInitialWaitMs);
            }
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
            // IsSafeDeleteTarget already logs the rejection reason.
            return;
        }

        WaitForParentExit(parentPid, initialWaitMs);

        // Log the outcome so a silent failure (locked file, permission denied)
        // shows up in the log instead of vanishing — note that this log entry
        // is written *after* the parent has exited, so it lands in the same
        // log file the user will inspect post-uninstall.
        var deleted = TryDeleteFolderWithRetries(normalizedFolder, initialWaitMs: 0);

        // Deliberately NOT Logger. Logger.EnsureDirectoryExists calls Directory.CreateDirectory
        // on AppPaths.BaseDir, and its _dirCreated cache is false in this fresh child process --
        // so logging the successful delete RECREATED the very folder we had just removed and
        // wrote a new log file into it. Uninstall therefore never left a clean state. Write the
        // outcome outside the deleted tree instead, so it is still diagnosable.
        LogCleanupOutcome(deleted
            ? $"Deferred cleanup completed. Folder='{normalizedFolder}'."
            : $"Deferred cleanup did not fully delete the folder after {MaxDeleteAttempts} attempts. Folder='{normalizedFolder}'.");
    }

    // Uninstall outcome goes to %TEMP%, never to AppPaths.BaseDir -- that folder is what we
    // just deleted, and touching Logger would bring it back. Best effort: if this fails there is
    // nowhere sensible left to report it.
    private static void LogCleanupOutcome(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "IdleLauncherTray-uninstall.log");
            File.AppendAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // Nothing left to do; the app is being uninstalled.
        }
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

    // Defence-in-depth against a MISTAKE -- a wrong argument, a bad path, a future caller that
    // passes something unexpected. It confines the delete to the app's own data directory during
    // portable uninstall, and the filesystem-root check is a redundant net under that.
    //
    // It is NOT an attacker boundary, and an earlier version of this comment wrongly claimed it
    // was. The allowed path is AppPaths.BaseDir, which resolves IDLELAUNCHERTRAY_DATA_DIR live on
    // every read -- so anyone who can choose this process's --cleanup-folder argument can also
    // choose its environment, point BaseDir at the same place, and satisfy the check. That buys
    // them nothing they did not already have: a caller who can set a child's environment can call
    // Directory.Delete themselves. The guard's real value is catching our own errors, so describe
    // it as that rather than as a security control someone might rely on.
    //
    // Every rejection logs. A guard that refuses silently is indistinguishable from a guard that
    // was never reached, and ScheduleFolderDelete returns void, so the log is the caller's ONLY
    // evidence that the uninstall it promised did not happen.
    private static bool IsSafeDeleteTarget(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            LogRefusal("the requested path was null, empty or whitespace.");
            return false;
        }

        var root = Path.GetPathRoot(folderPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            LogRefusal($"the requested path has no root, so it is relative. Requested='{folderPath}'.");
            return false;
        }

        var normalizedPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            LogRefusal($"the requested path is a filesystem root. Requested='{normalizedPath}'.");
            return false;
        }

        // Only allow deletion of the app's own base directory — no other paths.
        var expectedBaseDir = NormalizeFolderPath(AppPaths.BaseDir);
        if (string.IsNullOrWhiteSpace(expectedBaseDir))
        {
            LogRefusal("AppPaths.BaseDir did not resolve to a usable directory, so there is nothing safe to compare against.");
            return false;
        }

        if (!string.Equals(normalizedPath, expectedBaseDir, StringComparison.OrdinalIgnoreCase))
        {
            LogRefusal($"the requested path is outside AppPaths.BaseDir. Requested='{normalizedPath}' Expected='{expectedBaseDir}'.");
            return false;
        }

        return true;
    }

    private static void LogRefusal(string because)
    {
        try
        {
            Logger.Warn($"DeletionHelper refused to delete: {because}");
        }
        catch
        {
            // Best effort. Logging must never be the reason a refusal turns into a crash.
        }
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

            // AttributesToSkip must include ReparsePoint. Directory.Delete(recursive: true)
            // unlinks a junction rather than following it, so the DELETE stays inside the tree --
            // but this attribute walk did not. A junction or directory symlink dropped inside the
            // data directory was traversed, and File.SetAttributes stripped ReadOnly/Hidden/System
            // from every file on the far side of it: an unlogged write to an arbitrary tree, from
            // a helper whose entire job is to stay inside one folder.
            //
            // Setting AttributesToSkip explicitly also deliberately drops the Hidden|System skip
            // that this overload defaults to, preserving the legacy SearchOption behaviour of
            // visiting hidden and system files -- they are ours, inside our own directory, and
            // clearing their attributes is the whole point.
            var walkOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true
            };

            foreach (var entry in Directory.EnumerateFileSystemEntries(folderPath, "*", walkOptions))
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
