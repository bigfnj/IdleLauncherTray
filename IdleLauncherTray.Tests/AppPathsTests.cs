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
    /// <summary>
    /// Must use the SAME overload the product uses. The plain
    /// <c>GetFolderPath(ApplicationData)</c> returns <see cref="string.Empty"/> when the
    /// folder does not physically exist; the product asks for
    /// <see cref="Environment.SpecialFolderOption.DoNotVerify"/> because it creates the
    /// directory itself. Calling the other overload here would make the three tests below
    /// compare two different APIs, and they would pass or fail on whether the CI account
    /// happens to have a materialised roaming profile rather than on the product's logic.
    /// </summary>
    private static string RealUserDataDir =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            AppPaths.AppName);

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

    /// <summary>
    /// Every value the product could conceivably compose a default directory from: a real
    /// path, the documented empty-string result, and the four shapes that look rooted enough
    /// to fool a careless check.
    /// </summary>
    public static TheoryData<string?> EveryApplicationDataInput() => new()
    {
        null,
        string.Empty,
        "   ",
        "\t",
        "AppData",
        "C:relative",
        "\\rooted",
        ".",
        "C:\\Users\\someone\\AppData\\Roaming"
    };

    [Fact]
    public void ComposeDefaultBaseDir_WithARealRoamingPath_CombinesTheAppNameOntoIt()
    {
        var roaming = Path.Combine(TestDataDirectory.Root, "roaming");

        Assert.Equal(Path.Combine(roaming, AppPaths.AppName), AppPaths.ComposeDefaultBaseDir(roaming));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ComposeDefaultBaseDir_WithNothingUsable_FallsBackBesideTheExecutable(string? nothing)
    {
        // Environment.GetFolderPath is DOCUMENTED to return string.Empty when the folder does
        // not physically exist, and Path.Combine("", "IdleLauncherTray") is the bare RELATIVE
        // string "IdleLauncherTray". That is not a cosmetic defect: DeletionHelper authorises a
        // recursive delete by rooting BaseDir against the current directory and comparing it to
        // a caller path rooted the same way an instant earlier. They match, and the guard then
        // says yes to deleting <cwd>\IdleLauncherTray.
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, AppPaths.AppName), AppPaths.ComposeDefaultBaseDir(nothing));
    }

    [Theory]
    [InlineData("AppData")]
    [InlineData(".")]
    public void ComposeDefaultBaseDir_WithARelativePath_FallsBackBesideTheExecutable(string relative)
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, AppPaths.AppName), AppPaths.ComposeDefaultBaseDir(relative));
    }

    [Theory]
    [InlineData("C:relative")]
    [InlineData("\\rooted")]
    public void ComposeDefaultBaseDir_WithAPathThatIsRootedButNotFullyQualified_StillFallsBack(string rootedButNot)
    {
        // The witness for Path.IsPathFullyQualified over Path.IsPathRooted. Both of these
        // answer TRUE to IsPathRooted -- and both still resolve against ambient process state:
        // "C:relative" against the current directory ON DRIVE C:, "\rooted" against the current
        // drive. A check written with IsPathRooted waves them straight through, and this test
        // is what fails if anyone swaps the two.
        Assert.True(Path.IsPathRooted(rootedButNot), "Precondition: this input must look rooted.");
        Assert.False(Path.IsPathFullyQualified(rootedButNot));

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, AppPaths.AppName), AppPaths.ComposeDefaultBaseDir(rootedButNot));
    }

    [Theory]
    [MemberData(nameof(EveryApplicationDataInput))]
    public void ComposeDefaultBaseDir_ForEveryInput_ReturnsAFullyQualifiedPath(string? applicationDataPath)
    {
        // The invariant the rest of the app rests on, asserted across the whole input domain
        // rather than per case. DeletionHelper's safety guard compares normalised absolute
        // paths; the moment BaseDir can be anything else, that comparison is against a value
        // that means something different on every working directory.
        var composed = AppPaths.ComposeDefaultBaseDir(applicationDataPath);

        Assert.True(
            Path.IsPathFullyQualified(composed),
            $"ComposeDefaultBaseDir('{applicationDataPath ?? "<null>"}') returned '{composed}', which is not fully qualified.");
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
