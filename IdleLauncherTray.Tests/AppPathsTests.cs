using System;
using System.IO;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// Two jobs. The first four tests are the canary: they assert that this test run is
/// pointed away from the real user's config and log, and they are the tests that fail if
/// the redirect mechanism ever regresses. The rest cover the override itself.
/// <para>
/// Note that nothing in this class writes a file. The tests that clear the override read
/// <c>AppPaths.BaseDir</c> as a string and never hand it to an API that creates anything,
/// so even while the redirect is deliberately off, the real directory is untouched.
/// </para>
/// </summary>
public sealed class AppPathsTests
{
    private static string RealUserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppName);

    [Fact]
    public void TestRun_RedirectsBaseDir_IntoTheTempRoot()
    {
        Assert.StartsWith(TestDataDirectory.Root, AppPaths.BaseDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestRun_NeverPointsBaseDir_AtTheRealUserDataDirectory()
    {
        Assert.NotEqual(RealUserDataDir, AppPaths.BaseDir, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestRun_RedirectsTheLogFile_BeforeLoggerCapturesItsPath()
    {
        // Logger caches its log path in a static readonly field at type-initialisation
        // time. If the module initializer did not win that race, this is where it shows.
        Assert.StartsWith(TestDataDirectory.Root, Logger.LogPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverrideVariableName_UsedByTheHarness_MatchesTheProductConstant()
    {
        Assert.Equal(TestDataDirectory.OverrideVariableName, AppPaths.DataDirOverrideVariable);
    }

    [Fact]
    public void BaseDir_WithNoOverride_IsRoamingAppDataSubfolder()
    {
        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, null))
        {
            Assert.Equal(RealUserDataDir, AppPaths.BaseDir);
        }

        Assert.StartsWith(TestDataDirectory.Root, AppPaths.BaseDir, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void BaseDir_WithBlankOverride_FallsBackToTheDefault(string blank)
    {
        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, blank))
        {
            Assert.Equal(RealUserDataDir, AppPaths.BaseDir);
        }
    }

    [Fact]
    public void BaseDir_WithOverrideSet_UsesTheOverride()
    {
        var expected = Path.Combine(TestDataDirectory.Root, "explicit-override");

        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, expected))
        {
            Assert.Equal(expected, AppPaths.BaseDir);
        }
    }

    [Fact]
    public void BaseDir_WithSurroundingWhitespaceInOverride_IsTrimmed()
    {
        var expected = Path.Combine(TestDataDirectory.Root, "padded-override");

        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, $"  {expected}  "))
        {
            Assert.Equal(expected, AppPaths.BaseDir);
        }
    }

    [Fact]
    public void BaseDir_WithRelativeOverride_IsRooted()
    {
        // DeletionHelper compares a caller-supplied folder against BaseDir to decide
        // whether a recursive delete may proceed. A relative BaseDir could never match a
        // normalised absolute path, so the guard would reject its own directory.
        using (new CurrentDirectoryScope(TestDataDirectory.Root))
        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, "relative-data"))
        {
            var baseDir = AppPaths.BaseDir;

            Assert.True(Path.IsPathRooted(baseDir), $"Expected a rooted path but got '{baseDir}'.");
            Assert.Equal(Path.Combine(TestDataDirectory.Root, "relative-data"), baseDir);
        }
    }

    [Fact]
    public void BaseDir_WithAnUnusableOverride_FallsBackToTheDefaultInsteadOfThrowing()
    {
        // Long enough that Path.GetFullPath throws PathTooLongException. A bad value in
        // the environment must not stop the app from starting.
        var tooLong = "C:\\" + new string('a', 40_000);

        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, tooLong))
        {
            Assert.Equal(RealUserDataDir, AppPaths.BaseDir);
        }
    }

    [Fact]
    public void DerivedPaths_FollowBaseDir()
    {
        var root = Path.Combine(TestDataDirectory.Root, "derived-paths");

        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, root))
        {
            Assert.Equal(Path.Combine(root, "config.json"), AppPaths.ConfigPath);
            Assert.Equal(Path.Combine(root, "tray.ico"), AppPaths.TrayIconFile);
            Assert.Equal(Path.Combine(root, "IdleLauncherTray.exe"), AppPaths.LegacyInstalledExePath);
        }
    }

    [Fact]
    public void DerivedPaths_TrackAChangeToTheOverride_RatherThanSnapshottingIt()
    {
        // The whole point of resolving on each read: a cached static readonly field would
        // freeze whichever directory happened to be current when the type first loaded.
        var first = Path.Combine(TestDataDirectory.Root, "snapshot-a");
        var second = Path.Combine(TestDataDirectory.Root, "snapshot-b");

        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, first))
        {
            Assert.Equal(Path.Combine(first, "config.json"), AppPaths.ConfigPath);
        }

        using (new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, second))
        {
            Assert.Equal(Path.Combine(second, "config.json"), AppPaths.ConfigPath);
        }
    }
}
