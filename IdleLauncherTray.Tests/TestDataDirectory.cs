using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace IdleLauncherTray.Tests;

/// <summary>
/// Redirects the product's per-user data directory into a throwaway temp folder before
/// any test code runs.
/// <para>
/// This is the safety net, not the mechanism. The mechanism is that
/// <c>AppPaths.BaseDir</c> resolves the <c>IDLELAUNCHERTRAY_DATA_DIR</c> environment
/// variable on every read instead of caching it in a <c>static readonly</c> field, so
/// there is no type-initialisation race to lose. The net is that a
/// <see cref="ModuleInitializerAttribute"/> runs before the first method in this assembly
/// executes — earlier than any test, any test-class static constructor, and any xunit
/// fixture — so even a test that forgets to arrange a directory of its own cannot reach
/// the real <c>%APPDATA%\IdleLauncherTray</c>, which holds the user's live config and log
/// and is the directory <c>DeletionHelper</c> exists to delete.
/// </para>
/// <para>
/// The variable name is written out as a literal here on purpose: reading it from the
/// product constant would be circular. <c>AppPathsTests</c> asserts that this literal
/// still matches <c>AppPaths.DataDirOverrideVariable</c>, so a rename fails a test rather
/// than silently disarming the net.
/// </para>
/// </summary>
internal static class TestDataDirectory
{
    internal const string OverrideVariableName = "IDLELAUNCHERTRAY_DATA_DIR";

    /// <summary>Root of every directory this test run is allowed to write to.</summary>
    internal static string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "IdleLauncherTray.Tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>The directory the product sees unless an individual test arranges its own.</summary>
    internal static string DefaultDataDir { get; } = Path.Combine(Root, "default-data-dir");

    [ModuleInitializer]
    internal static void RedirectProductDataDirectory()
    {
        Directory.CreateDirectory(DefaultDataDir);
        Environment.SetEnvironmentVariable(OverrideVariableName, DefaultDataDir);

        // Logger caches its log path in a static readonly field the first time anything
        // touches the type. Forcing that to happen *here*, while the redirect is
        // guaranteed to be on, means the cache can never be taken during one of the
        // AppPathsTests windows that deliberately turn the override off.
        _ = Sut.Logger.LogPath;

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test run over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }

    /// <summary>
    /// Tripwire. Every test that is about to make the product touch the filesystem calls
    /// this first, and it refuses to let the test proceed unless the product's data
    /// directory is inside the temp root.
    /// <para>
    /// This is not theoretical. A mutation run that reintroduced the
    /// <c>static readonly</c> snapshot in <c>AppPaths.BaseDir</c> took its snapshot during
    /// the one window where <c>AppPathsTests</c> deliberately clears the override, froze
    /// the product onto the real %APPDATA%, and the rest of that run wrote the user's
    /// config and log for real. The assertions caught it — after the writes. Checking at
    /// arrange time means a broken build fails before it can touch anything.
    /// </para>
    /// </summary>
    internal static void AssertRedirectIsInEffect()
    {
        var resolved = Sut.AppPaths.BaseDir;

        if (!resolved.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to run: the product resolved its data directory to '{resolved}', which is " +
                $"outside the test root '{Root}'. Something has broken the {OverrideVariableName} " +
                "redirect, and continuing would read and write the real user's config and log.");
        }
    }
}
