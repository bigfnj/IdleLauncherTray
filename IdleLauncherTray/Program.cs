using System;
using System.IO;
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
        //
        // Taking the mutex is a call that can THROW, which is why it is not written inline
        // here any more: see TryAcquireSingleInstanceMutex for what throws, and for why an
        // escape from this exact spot is invisible to the user and to the log alike.
        using var mutex = TryAcquireSingleInstanceMutex(SingleInstanceMutexName, out var isFirst);
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

    /// <summary>
    /// Takes the single-instance mutex, and when it cannot be taken, works out from the failure
    /// whether another instance is already running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>new Mutex(initiallyOwned: true, name, out createdNew)</c> CAN THROW, and the throw is
    /// reachable on an ordinary machine rather than only in theory. A named kernel object
    /// carries an ACL and that constructor asks for full access, so when the first instance was
    /// started elevated -- Task Scheduler's "run with highest privileges" is the usual route --
    /// and the user then double-clicks the exe unelevated, the second process gets
    /// <see cref="UnauthorizedAccessException"/>. A name already taken by a DIFFERENT kind of
    /// kernel object throws <see cref="WaitHandleCannotBeOpenedException"/> for the same reason.
    /// </para>
    /// <para>
    /// Unhandled, either one ends the process at a point where nothing can report it: this runs
    /// before <see cref="Application.ThreadException"/> and
    /// <see cref="AppDomain.UnhandledException"/> are wired, before the tray icon exists and
    /// before any dialog. The user sees Windows Error Reporting or nothing at all, and the log
    /// is empty -- which is indistinguishable from "I double-clicked it and nothing happened",
    /// the exact confusion the "second instance detected" line exists to clear up. So every
    /// outcome below is logged, including the two that let startup continue.
    /// </para>
    /// <para>
    /// Takes the name as a parameter rather than reading the const, so the decision tree can be
    /// exercised against a throwaway name instead of against the one a running copy of this app
    /// may be holding.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The mutex when there is one to hold, or <see langword="null"/> when startup continues
    /// without a guard. The caller owns the lifetime either way.
    /// </returns>
    private static Mutex? TryAcquireSingleInstanceMutex(string mutexName, out bool isFirstInstance)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out var createdNew);
            isFirstInstance = createdNew;
            return mutex;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            LogStartupWarning(
                $"Could not create the single-instance mutex '{mutexName}' ({ex.GetType().Name}: {ex.Message}). "
                + "The likely cause is a first instance that created it while running elevated, leaving an ACL this process cannot open. Checking whether that instance is still there.");
        }

        // The construction failed, so the question the mutex was there to answer -- is a copy of
        // this app already running -- is still open, and has to be answered from the name alone.
        try
        {
            if (Mutex.TryOpenExisting(mutexName, out var existing))
            {
                LogStartupWarning(
                    $"The single-instance mutex '{mutexName}' already exists and could be opened, so another instance is running. Exiting.");
                isFirstInstance = false;
                return existing;
            }

            // Nothing of that name exists, so nothing is holding it and whatever failed above
            // was not another instance. Start anyway: refusing to start is the silent failure
            // this whole method is about, and a tray app that will not start is worth less than
            // one running without a guard it has no way to take.
            LogStartupWarning(
                $"The single-instance mutex '{mutexName}' could not be created and no object of that name exists, so no other instance is holding it. "
                + "Starting WITHOUT the single-instance guard: if a second copy is launched, expect two tray icons and two sets of input hooks.");
            isFirstInstance = true;
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            // The name EXISTS and this process may not touch it at all, which is what an
            // instance running under another identity (in practice, an elevated one) looks
            // like from here. That is a second-instance launch, so behave like one.
            LogStartupWarning(
                $"The single-instance mutex '{mutexName}' exists but access is denied ({ex.Message}), which means another instance owns it -- most likely an elevated one. Exiting.");
            isFirstInstance = false;
            return null;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or IOException)
        {
            // The name is in use by something that is not a mutex, so no instance of this app
            // can be holding it. Same call as above: start, unguarded, and say so.
            LogStartupWarning(
                $"The name '{mutexName}' is held by a kernel object that is not a mutex ({ex.GetType().Name}: {ex.Message}), so no other instance of this app is holding it. "
                + "Starting WITHOUT the single-instance guard.");
            isFirstInstance = true;
            return null;
        }
    }

    /// <summary>
    /// Logging for the window before any exception handler is wired. <c>Logger.Write</c>
    /// swallows its own I/O failures already, so this catch is for the case that would
    /// otherwise be unrecoverable: a diagnostic that throws, from the one part of startup
    /// where a throw produces no diagnostic at all.
    /// </summary>
    private static void LogStartupWarning(string message)
    {
        try
        {
            Logger.Warn(message);
        }
        catch
        {
            // Never let logging take down startup.
        }
    }
}
