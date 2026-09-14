using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace IdleLauncherTray;

// Logger uses a single global lock + synchronous File.AppendAllText. For the volume
// this app produces (a few lines per minute, AppData being a local SSD path on every
// supported platform) this is fine and keeps crash diagnostics complete — every log
// line is durably on disk by the time the call returns. A background-queue writer
// would be more scalable but introduces a window where in-flight log lines vanish if
// the process aborts before the queue drains. Keep the simple synchronous design
// unless logging volume becomes a real bottleneck.
//
// KNOWN AND ACCEPTED: the lock is per-process, so two instances of this app do not
// serialise against each other. File.AppendAllText opens with FileShare.Read, so while
// one process holds the file the other's open fails, Write swallows the exception, and
// that line is gone with no trace. The line most likely to be lost is the one a second
// instance writes at startup ("Second instance detected"), because that is precisely the
// moment two processes are both logging.
//
// It is documented rather than fixed, and both available fixes were rejected on purpose:
// a named mutex or a retry with Thread.Sleep would both block, and Logger.Write is called
// from OnTick on the UI thread, so the cost of the fix is a visibly frozen tray icon
// whenever another process happens to hold the file. A dropped startup line is cheaper
// than a stalled message pump. Revisit only if the app ever logs off the UI thread.
internal static class Logger
{
    private static readonly object _gate = new();

    // Cache directory creation state to avoid calling Directory.CreateDirectory on every write.
    // Volatile ensures the check is not cached by the CPU and writes are visible across threads.
    private static volatile bool _dirCreated;

    // Only stat the log file for rotation every N writes to reduce filesystem overhead.
    private static int _writeCount;
    private const int RotationCheckInterval = 10;

    // Class-level rather than a local inside the rotation check: TryRotate takes the limit as
    // a parameter so a test can drive it with a small one, and the production value has to be
    // nameable from outside that method for the seam to mean anything.
    private const long MaxLogBytes = 2_000_000; // ~2MB

    // The first line of every post-rotation log file starts with this. Kept as a const so the
    // test that reads the first line asserts against the PRODUCT's marker rather than a copy
    // of the wording, which would keep passing after the product's text drifted.
    private const string RotationMarkerPrefix = "Log rotated.";

    // The log path is resolved ONCE, deliberately: a log that moved mid-process would split one
    // run's history across two files. Note that AppPaths.BaseDir is NOT fixed -- it is a live
    // property that re-reads IDLELAUNCHERTRAY_DATA_DIR on every access (see AppPaths.cs) -- so
    // this is a snapshot of a moving value, not a cache of a constant.
    private static readonly string _logPath = Path.Combine(AppPaths.BaseDir, $"{AppPaths.AppName}.log");
    public static string LogPath => _logPath;

    // Derived from _logPath, NOT from AppPaths.BaseDir. Those two can disagree: BaseDir is live,
    // _logPath is a snapshot. Creating the directory from the live value while appending to the
    // snapshot path meant EnsureDirectoryExists could succeed on one directory, latch _dirCreated
    // to true forever, and leave every File.AppendAllText failing against a directory that was
    // never created -- with the exception swallowed, so logging died permanently and silently.
    // Deriving both from the same string makes that disagreement unrepresentable.
    private static readonly string _logDir = Path.GetDirectoryName(_logPath) ?? AppPaths.BaseDir;

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Warn(string message)
    {
        Write("WARN", message, null);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", message, ex);
    }

    /// <summary>
    /// The one place a log line's shape is defined. Extracted from <see cref="Write"/> so the
    /// rotation marker goes through it too: a marker assembled by hand would carry a different
    /// timestamp/pid/tid/level prefix, which makes it a stray string sitting in a log file
    /// rather than a log line — invisible to any tooling or eye that scans the left margin.
    /// <para>
    /// Always ends with a newline, so whatever is appended next starts on its own line.
    /// </para>
    /// </summary>
    private static string FormatLine(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append("[pid=");
        sb.Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        sb.Append(" tid=");
        sb.Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture));
        sb.Append("] ");
        sb.Append(level);
        sb.Append(' ');
        sb.Append(message);

        if (ex != null)
        {
            sb.AppendLine();
            sb.Append(ex);
        }

        sb.AppendLine();

        return sb.ToString();
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            EnsureDirectoryExists();

            var line = FormatLine(level, message, ex);

            lock (_gate)
            {
                if (Interlocked.Increment(ref _writeCount) >= RotationCheckInterval)
                {
                    Interlocked.Exchange(ref _writeCount, 0);
                    RotateIfNeeded();
                }

                File.AppendAllText(_logPath, line);
            }
        }
        catch
        {
            // Never let logging crash the app.
        }
    }

    private static void EnsureDirectoryExists()
    {
        // Fast path: already created.
        if (_dirCreated)
        {
            return;
        }

        // Slow path: create directory under lock to avoid redundant CreateDirectory calls.
        lock (_gate)
        {
            if (_dirCreated)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_logDir);
                _dirCreated = true;
            }
            catch
            {
                // Never let logging crash the app.
            }
        }
    }

    // Only ever reached from inside lock (_gate) — see Write. That is what makes TryRotate's
    // direct File.AppendAllText safe without a lock of its own.
    private static void RotateIfNeeded() => TryRotate(_logPath, MaxLogBytes);

    /// <summary>
    /// Archives the log once it passes <paramref name="maxBytes"/> and leaves a marker at the
    /// top of the new one. Returns true only when a rotation actually happened.
    /// <para>
    /// The path and the limit are parameters because that is the ONLY way this is testable:
    /// <c>_logPath</c> is <c>static readonly</c>, and .NET throws <c>FieldAccessException</c>
    /// from <c>FieldInfo.SetValue</c> on an initonly static field, so no amount of reflection
    /// can point the production entry point at a temp file.
    /// </para>
    /// </summary>
    private static bool TryRotate(string logPath, long maxBytes)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return false;
            }

            var fi = new FileInfo(logPath);
            if (fi.Length <= maxBytes)
            {
                return false;
            }

            var archivedBytes = fi.Length;
            var oldPath = logPath + ".old";
            File.Move(logPath, oldPath, overwrite: true);

            // This RECORDS the loss; it does not prevent it. Rotation keeps exactly one
            // archive, so the previous .old was just overwritten and that history is gone.
            // The marker says so in as many words, because the alternative -- a cheerful
            // "rotated" line -- would leave a reader of a truncated log believing nothing had
            // been dropped, which is the same shape of lie as a log line that cannot fail.
            //
            // Appended DIRECTLY rather than through Write: Write re-enters the rotation check,
            // and it would do so with the write counter just reset, so the marker would be
            // checked for rotation against a file that does not exist yet. The File.Move above
            // leaves nothing at logPath, so this call CREATES the file and the marker is
            // guaranteed to be its first line.
            File.AppendAllText(
                logPath,
                FormatLine(
                    "INFO",
                    $"{RotationMarkerPrefix} This file starts mid-stream. "
                        + $"{archivedBytes.ToString(CultureInfo.InvariantCulture)} bytes were archived to '{oldPath}', "
                        + "REPLACING any earlier archive at that path: that older history is gone, not kept.",
                    null));

            return true;
        }
        catch
        {
            // Ignore. A log that cannot rotate must still be a log that does not crash the app.
            return false;
        }
    }
}
