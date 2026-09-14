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

    // How long the cleanup CHILD waits for the tray process that spawned it to exit before
    // trying the delete. Exactly one meaning now that the in-process paths no longer sleep:
    // there is no "initial wait" any more, only this.
    private const int ParentExitWaitMs = 2000;

    // The child process gets the generous budget: it has already outlived the tray, nothing
    // is waiting on it, and its whole job is to keep trying.
    private const int MaxDeleteAttempts = 12;

    // The three in-process fallback paths get three. They run SYNCHRONOUSLY ON THE UI THREAD
    // from the uninstall handler, after ShutdownForExit has hidden the tray icon -- so every
    // retry is time the user spends staring at a tray where the icon just vanished and nothing
    // else happened. Twelve attempts x 500ms is eight seconds of that.
    //
    // Three is not a guess about how long a lock lasts. At that moment the tray holds NOTHING
    // inside BaseDir: the log is written with File.AppendAllText, which opens and closes per
    // line, and the custom tray icon was read into a MemoryStream at load. So the only thing a
    // retry can outlast is a THIRD-PARTY transient -- antivirus, the search indexer, a shell
    // preview handler -- and if one of those is still holding a handle a second later, the
    // deferred child (which is not on any UI thread and has the full twelve) is the right
    // place for that fight, not here.
    private const int MaxInProcessDeleteAttempts = 3;

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
            TryDeleteFolderWithRetries(normalizedFolder, MaxInProcessDeleteAttempts);
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
                TryDeleteFolderWithRetries(normalizedFolder, MaxInProcessDeleteAttempts);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to start deferred cleanup helper for '{normalizedFolder}'. Falling back to an in-process deletion attempt. Error='{ex.Message}'.");
            TryDeleteFolderWithRetries(normalizedFolder, MaxInProcessDeleteAttempts);
        }
    }

    public static bool TryRunCleanupFromCommandLine(string[] args)
    {
        if (!TryParseCleanupArgs(args, out var folderToDelete, out var parentPid))
        {
            return false;
        }

        RunCleanupWorker(folderToDelete, parentPid);
        return true;
    }

    private static void RunCleanupWorker(string folderToDelete, int parentPid)
    {
        var normalizedFolder = NormalizeFolderPath(folderToDelete);
        if (!IsSafeDeleteTarget(normalizedFolder))
        {
            // IsSafeDeleteTarget already logs the rejection reason.
            return;
        }

        var parentExited = WaitForParentExit(parentPid);

        // Log the outcome so a silent failure (locked file, permission denied)
        // shows up in the log instead of vanishing — note that this log entry
        // is written *after* the parent has exited, so it lands in the same
        // log file the user will inspect post-uninstall.
        var deleted = TryDeleteFolderWithRetries(normalizedFolder, MaxDeleteAttempts);

        // Deliberately NOT Logger. Logger.EnsureDirectoryExists calls Directory.CreateDirectory
        // on AppPaths.BaseDir, and its _dirCreated cache is false in this fresh child process --
        // so logging the successful delete RECREATED the very folder we had just removed and
        // wrote a new log file into it. Uninstall therefore never left a clean state. Write the
        // outcome outside the deleted tree instead, so it is still diagnosable.
        if (deleted)
        {
            LogCleanupOutcome($"Deferred cleanup completed. Folder='{normalizedFolder}' ParentExited={parentExited}.");
            return;
        }

        // The parent-exit outcome is carried down to here because this file is the ONLY thing a
        // user has after an uninstall, and "it failed" is not a diagnosis. If the parent never
        // exited, the tray process still had the folder open and no number of retries was ever
        // going to win -- a completely different problem, with a completely different fix, from
        // a third party holding a handle.
        var cause = parentExited
            ? "the parent process had already exited, so something else is holding files in the folder (antivirus, the search indexer, a shell preview handler, or an open Explorer window)."
            : $"the parent process was NOT observed to exit within {ParentExitWaitMs.ToString(CultureInfo.InvariantCulture)}ms, so it most likely still had the folder open and no number of retries would have succeeded.";

        LogCleanupOutcome(
            $"Deferred cleanup did not fully delete the folder after {MaxDeleteAttempts.ToString(CultureInfo.InvariantCulture)} attempts. "
            + $"Folder='{normalizedFolder}' ParentExited={parentExited}. Cause: {cause}");
    }

    // Uninstall outcome goes to %TEMP%, never to AppPaths.BaseDir -- that folder is what we
    // just deleted, and touching Logger would bring it back. Best effort: if this fails there is
    // nowhere sensible left to report it.
    private static void LogCleanupOutcome(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "IdleLauncherTray-uninstall.log");

            // InvariantCulture, like every other timestamp this app writes (see Logger.FormatLine).
            // Without it the format string is interpreted against the current culture, and under
            // ja-JP or ar-SA both the calendar era-year and the digit glyphs change -- in the ONE
            // file a user has left to read after uninstalling.
            File.AppendAllText(
                path,
                $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}");
        }
        catch
        {
            // Nothing left to do; the app is being uninstalled.
        }
    }

    /// <summary>
    /// Returns whether the parent was actually OBSERVED to exit. That distinction is the whole
    /// value of the return: the caller writes it into the uninstall log, where "we waited and it
    /// was still running" and "it was gone" point at entirely different causes for a failed
    /// delete.
    /// <para>
    /// PID reuse is a known and deliberately unhandled hazard: between the parent recording its
    /// own id and this lookup, Windows could in principle have recycled that id onto an
    /// unrelated process, and we would then wait on a stranger. A handshake (an inherited event
    /// handle, or a start-time check passed down) would close it. It is not worth it. The window
    /// is milliseconds wide, the worst case is that we wait up to <see cref="ParentExitWaitMs"/>
    /// longer and then delete anyway, the delete target is independently guarded by
    /// <see cref="IsSafeDeleteTarget"/> regardless of what we waited on, and the fix would add a
    /// third argument to the very command-line surface this change just shrank.
    /// </para>
    /// </summary>
    private static bool WaitForParentExit(int parentPid)
    {
        if (parentPid > 0)
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                return parent.HasExited || parent.WaitForExit(ParentExitWaitMs);
            }
            catch (ArgumentException)
            {
                // GetProcessById throws this when nothing is running under that id, which is
                // precisely the state we were waiting for. It exited; we just missed it.
                return true;
            }
            catch
            {
                // Anything else (access denied on HasExited, for instance) tells us nothing
                // about the parent. Fall through to the bounded sleep below.
            }
        }

        // We did NOT observe an exit here, so say so. Reporting true because we slept for the
        // same duration would be a log line that cannot fail -- it would read identically
        // whether the parent had gone or was still holding every file in the folder.
        Thread.Sleep(ParentExitWaitMs);
        return false;
    }

    /// <summary>
    /// Total over every possible <paramref name="args"/>: each branch independently checks that
    /// a value follows its flag, all out-params are assigned up front, and the final check is
    /// what decides success.
    /// <para>
    /// There is deliberately no <c>args.Length &lt; 2</c> fast path. It was removed because it
    /// was a provable no-op across the entire input domain -- the loop cannot read past the end
    /// with or without it, and the closing
    /// <c>!string.IsNullOrWhiteSpace(folderToDelete)</c> already returns false for every input
    /// too short to contain a flag and its value. Keeping it did active harm: it implied the
    /// loop below depends on a minimum length, which it does not. (This is not the same as the
    /// guards that keep a function TOTAL and must stay even when no current caller reaches
    /// them; this one changed nothing for any input at all.)
    /// </para>
    /// </summary>
    private static bool TryParseCleanupArgs(string[] args, out string folderToDelete, out int parentPid)
    {
        folderToDelete = string.Empty;
        parentPid = 0;

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

    /// <summary>
    /// Deletes <paramref name="folderPath"/>, retrying up to <paramref name="maxAttempts"/>
    /// times with <see cref="DeleteRetryDelayMs"/> between attempts.
    /// <para>
    /// There is no "wait before the first attempt" parameter any more. It existed so the
    /// deferred CHILD could let the tray process exit first, and the only caller that ever
    /// passed a non-zero value was an in-process fallback -- where we ARE the parent, so
    /// sleeping to wait for ourselves cannot help by construction and the two seconds bought
    /// nothing but an emptier tray. The child's wait now lives in <see cref="WaitForParentExit"/>,
    /// which is the only place it ever meant anything.
    /// </para>
    /// </summary>
    private static bool TryDeleteFolderWithRetries(string folderPath, int maxAttempts)
    {
        if (!Directory.Exists(folderPath))
        {
            return true;
        }

        // Hoisted OUT of the retry loop. Nothing inside the loop can put a ReadOnly bit back --
        // we are the only writer, and a failed Directory.Delete does not restore attributes --
        // so re-running it per attempt re-walked the entire tree and re-issued one
        // File.SetAttributes per entry for a result that the first walk had already settled. On
        // a contended folder that is twelve full recursive enumerations plus 12xN syscalls, all
        // to discover the same thing twelve times.
        ClearReadOnlyAttributes(folderPath);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                // Still checked inside the loop, and it is NOT a duplicate of the early return
                // above: this one is the SUCCESS test. Between two attempts the folder can go
                // away -- a previous Directory.Delete that reported an error may have completed,
                // or the deferred child may have won the race -- and that is the outcome we
                // want to report as a success rather than retry against.
                if (!Directory.Exists(folderPath))
                {
                    return true;
                }

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

            // No sleep after the LAST attempt. The delay exists to give whatever holds the
            // folder time to let go before we try again; once there is no "again", it is pure
            // dead time -- 500ms of it on the UI thread on every failed in-process uninstall.
            if (attempt < maxAttempts - 1)
            {
                Thread.Sleep(DeleteRetryDelayMs);
            }
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
