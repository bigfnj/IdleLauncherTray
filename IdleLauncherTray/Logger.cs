using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace IdleLauncherTray;

internal static class Logger
{
    private static readonly object _gate = new();

    // Cache directory creation state to avoid calling Directory.CreateDirectory on every write.
    // Volatile ensures the check is not cached by the CPU and writes are visible across threads.
    private static volatile bool _dirCreated;

    // Only stat the log file for rotation every N writes to reduce filesystem overhead.
    private static int _writeCount;
    private const int RotationCheckInterval = 10;

    public static string LogPath => Path.Combine(AppPaths.BaseDir, $"{AppPaths.AppName}.log");

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

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            EnsureDirectoryExists();

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

            lock (_gate)
            {
                if (Interlocked.Increment(ref _writeCount) >= RotationCheckInterval)
                {
                    Interlocked.Exchange(ref _writeCount, 0);
                    RotateIfNeeded();
                }

                File.AppendAllText(LogPath, sb.ToString());
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
                Directory.CreateDirectory(AppPaths.BaseDir);
                _dirCreated = true;
            }
            catch
            {
                // Never let logging crash the app.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return;
            }

            var fi = new FileInfo(LogPath);

            const long maxBytes = 2_000_000; // ~2MB
            if (fi.Length <= maxBytes)
            {
                return;
            }

            var oldPath = LogPath + ".old";
            File.Move(LogPath, oldPath, overwrite: true);
        }
        catch
        {
            // Ignore.
        }
    }
}
