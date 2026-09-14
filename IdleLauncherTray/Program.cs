using System;
using System.Threading;
using System.Windows.Forms;

namespace IdleLauncherTray;

internal static class Program
{
    // Named mutex to prevent multiple tray instances (and multiple timers/hooks) from running.
    private const string SingleInstanceMutexName = "IdleLauncherTray.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        if (DeletionHelper.TryRunCleanupFromCommandLine(args))
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // --------------------------------------------------------------------
        // Single instance guard
        // --------------------------------------------------------------------
        // A second instance is almost always accidental (double-click, startup race, etc.)
        // and leads to multiple tray icons + duplicated keyboard/mouse hooks.
        using var mutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out var isFirst);
        if (!isFirst)
        {
            try
            {
                Logger.Warn("Second instance detected; exiting immediately.");
            }
            catch
            {
                // Never let logging take down startup.
            }

            return;
        }

        // --------------------------------------------------------------------
        // Global exception logging for a GUI/tray app (no console).
        // --------------------------------------------------------------------
        try
        {
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
            Logger.Info(
                $"Starting v{version}. PortableMode=true Exe='{Application.ExecutablePath}' ArgsCount={args.Length}.");
        }
        catch
        {
            // Never let logging take down startup.
        }

        Application.ThreadException += (_, e) =>
        {
            try { Logger.Error("Unhandled UI thread exception.", e.Exception); } catch { /* ignore */ }

            // Clear the tray icon BEFORE Environment.Exit below. That call does not run
            // ApplicationContext teardown, so without this the icon lingers in the notification
            // area as a ghost until the user hovers it.
            TrayAppContext.EmergencyHideTrayIcon();

            try
            {
                MessageBox.Show(
                    $"IdleLauncherTray hit an unexpected error and will exit.\n\nLog file:\n{Logger.LogPath}\n\n{e.Exception.Message}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // ignore
            }

            Environment.Exit(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            try { Logger.Error("Unhandled non-UI exception.", ex); } catch { /* ignore */ }

            TrayAppContext.EmergencyHideTrayIcon();

            // Tell the user, exactly as the UI-thread handler above does. An unhandled
            // exception on a background thread is fatal in .NET, so this path ends the process
            // just as surely -- but without this dialog the tray icon simply disappears with no
            // explanation, which is indistinguishable from the user having closed it. The whole
            // diagnostic strategy here is "it is in the log", and the log's location is only
            // ever revealed by this dialog, so staying silent leaves no route to the evidence.
            try
            {
                MessageBox.Show(
                    $"IdleLauncherTray hit an unexpected error on a background thread and will exit.\n\nLog file:\n{Logger.LogPath}\n\n{ex?.Message ?? "(no exception details available)"}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // ignore
            }
        };

        try
        {
            Application.Run(new TrayAppContext());
        }
        catch (Exception ex)
        {
            try { Logger.Error("Fatal exception while creating/running TrayAppContext.", ex); } catch { /* ignore */ }

            try
            {
                MessageBox.Show(
                    $"IdleLauncherTray crashed during startup.\n\nLog file:\n{Logger.LogPath}\n\n{ex.Message}",
                    AppPaths.AppName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // ignore
            }
        }
    }
}
