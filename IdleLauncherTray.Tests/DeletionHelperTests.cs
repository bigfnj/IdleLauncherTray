using System;
using System.IO;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// <c>IsSafeDeleteTarget</c> is the last thing standing between
/// <c>Directory.Delete(path, recursive: true)</c> and whatever path arrived on the command
/// line, in a process the user can be tricked into starting
/// (<c>IdleLauncherTray.exe --cleanup-folder &lt;anything&gt;</c>). Every test here is an
/// "it must say no" test except the two that prove it still says yes to its own folder.
/// </summary>
public sealed class DeletionHelperTests
{
    private static string ParentOf(string path) => Directory.GetParent(path)!.FullName;

    [Fact]
    public void IsSafeDeleteTarget_AcceptsTheAppsOwnBaseDirectory()
    {
        using var data = new TempDataDirectory();

        Assert.True(DeletionHelper.IsSafeDeleteTarget(AppPaths.BaseDir));
    }

    [Theory]
    [InlineData("\\")]
    [InlineData("/")]
    [InlineData("\\\\")]
    public void IsSafeDeleteTarget_AcceptsTheBaseDirectoryWithATrailingSeparator(string separator)
    {
        // Deliberate: "C:\...\IdleLauncherTray\" and "C:\...\IdleLauncherTray" are the same
        // directory, and the caller's path can arrive either way.
        using var data = new TempDataDirectory();

        Assert.True(DeletionHelper.IsSafeDeleteTarget(AppPaths.BaseDir + separator));
    }

    [Fact]
    public void IsSafeDeleteTarget_AcceptsTheBaseDirectoryInADifferentCase()
    {
        // Also deliberate: Windows paths are case-insensitive, so an uppercase spelling
        // names the very same directory. Rejecting it would only produce a self-uninstall
        // that silently leaves the folder behind.
        using var data = new TempDataDirectory();

        Assert.True(DeletionHelper.IsSafeDeleteTarget(AppPaths.BaseDir.ToUpperInvariant()));
        Assert.True(DeletionHelper.IsSafeDeleteTarget(AppPaths.BaseDir.ToLowerInvariant()));
    }

    [Fact]
    public void IsSafeDeleteTarget_RejectsTheParentOfTheBaseDirectory()
    {
        using var data = new TempDataDirectory();

        Assert.False(DeletionHelper.IsSafeDeleteTarget(ParentOf(AppPaths.BaseDir)));
    }

    [Fact]
    public void IsSafeDeleteTarget_RejectsTheGrandparentOfTheBaseDirectory()
    {
        using var data = new TempDataDirectory();

        Assert.False(DeletionHelper.IsSafeDeleteTarget(ParentOf(ParentOf(AppPaths.BaseDir))));
    }

    [Fact]
    public void IsSafeDeleteTarget_RejectsASiblingOfTheBaseDirectory()
    {
        using var data = new TempDataDirectory();
        var sibling = Path.Combine(ParentOf(AppPaths.BaseDir), "SomeOtherApp");

        Assert.False(DeletionHelper.IsSafeDeleteTarget(sibling));
    }

    [Fact]
    public void IsSafeDeleteTarget_RejectsAChildOfTheBaseDirectory()
    {
        using var data = new TempDataDirectory();

        Assert.False(DeletionHelper.IsSafeDeleteTarget(Path.Combine(AppPaths.BaseDir, "logs")));
    }

    [Fact]
    public void IsSafeDeleteTarget_RejectsASiblingWhoseNameMerelyStartsWithTheBaseDirectory()
    {
        // The guard must be an equality test, not a prefix test: "...\IdleLauncherTray2"
        // starts with "...\IdleLauncherTray" but is somebody else's folder.
        using var data = new TempDataDirectory();

        Assert.False(DeletionHelper.IsSafeDeleteTarget(AppPaths.BaseDir + "2"));
        Assert.False(DeletionHelper.IsSafeDeleteTarget(AppPaths.BaseDir + "-backup"));
    }

    [Theory]
    [InlineData("C:\\")]
    [InlineData("C:")]
    [InlineData("D:\\")]
    [InlineData("\\\\fileserver\\share")]
    [InlineData("\\\\fileserver\\share\\")]
    public void IsSafeDeleteTarget_RejectsAFilesystemRoot(string root)
    {
        using var data = new TempDataDirectory();

        Assert.False(DeletionHelper.IsSafeDeleteTarget(root));
    }

    [Theory]
    [InlineData("C:\\")]
    [InlineData("D:\\")]
    [InlineData("\\\\fileserver\\share")]
    public void IsSafeDeleteTarget_RejectsAFilesystemRoot_EvenWhenItIsTheConfiguredDataDirectory(string root)
    {
        // The root check reads as belt-and-braces next to the "must equal BaseDir" test,
        // and it used to be unreachable. It is not any more: the data directory is
        // configurable, so pointing IDLELAUNCHERTRAY_DATA_DIR at a drive root would
        // otherwise make "Uninstall" mean "recursively delete the drive".
        // No filesystem access happens here; this is pure string logic.
        using var redirect = new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, root);

        Assert.Equal(root, AppPaths.BaseDir);
        Assert.False(DeletionHelper.IsSafeDeleteTarget(root));
        Assert.False(DeletionHelper.IsSafeDeleteTarget(DeletionHelper.NormalizeFolderPath(root)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsSafeDeleteTarget_RejectsEmptyInput(string? path)
    {
        using var data = new TempDataDirectory();

        Assert.False(DeletionHelper.IsSafeDeleteTarget(path));
    }

    [Theory]
    [InlineData("IdleLauncherTray")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("sub\\folder")]
    public void IsSafeDeleteTarget_RejectsARelativePath(string path)
    {
        // A relative path has no root, and the guard compares against a rooted BaseDir.
        using var data = new TempDataDirectory();

        Assert.False(DeletionHelper.IsSafeDeleteTarget(path));
    }

    [Theory]
    [InlineData("C:\\Windows")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("C:\\Users")]
    [InlineData("C:\\Program Files")]
    public void IsSafeDeleteTarget_RejectsUnrelatedSystemDirectories(string path)
    {
        using var data = new TempDataDirectory();

        Assert.False(DeletionHelper.IsSafeDeleteTarget(path));
    }

    [Fact]
    public void IsSafeDeleteTarget_FollowsTheOverride_SoItGuardsWhicheverDirectoryIsInUse()
    {
        // Proves the guard is anchored on AppPaths.BaseDir rather than on a path baked in
        // at type-initialisation time: the folder that was safe a moment ago is not safe
        // once the app's data directory moves.
        string firstDir;

        using (var first = new TempDataDirectory("first"))
        {
            firstDir = AppPaths.BaseDir;
            Assert.True(DeletionHelper.IsSafeDeleteTarget(firstDir));
        }

        using (new TempDataDirectory("second"))
        {
            Assert.NotEqual(firstDir, AppPaths.BaseDir);
            Assert.True(DeletionHelper.IsSafeDeleteTarget(AppPaths.BaseDir));
            Assert.False(DeletionHelper.IsSafeDeleteTarget(firstDir));
        }
    }

    [Theory]
    [InlineData("IdleLauncherTray")]
    [InlineData("sub\\folder")]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeThenGuard_RejectsWhatTheRealEntryPointWouldPassIn(string requested)
    {
        // ScheduleFolderDelete and the --cleanup-folder worker both normalise first, so
        // this is the composition that actually runs. A relative path becomes rooted at
        // the working directory, which is never the app's data directory.
        using var data = new TempDataDirectory();

        var normalized = DeletionHelper.NormalizeFolderPath(requested);

        Assert.False(DeletionHelper.IsSafeDeleteTarget(normalized));
    }

    [Fact]
    public void NormalizeThenGuard_AcceptsTheBaseDirectoryHoweverItIsSpelled()
    {
        using var data = new TempDataDirectory();

        Assert.True(DeletionHelper.IsSafeDeleteTarget(DeletionHelper.NormalizeFolderPath(AppPaths.BaseDir + "\\")));
        Assert.True(DeletionHelper.IsSafeDeleteTarget(DeletionHelper.NormalizeFolderPath($"  {AppPaths.BaseDir}  ")));
        Assert.True(DeletionHelper.IsSafeDeleteTarget(DeletionHelper.NormalizeFolderPath(
            Path.Combine(AppPaths.BaseDir, "sub", ".."))));
    }

    [Fact]
    public void NormalizeFolderPath_TrimsATrailingSeparatorButKeepsARootIntact()
    {
        Assert.Equal("C:\\tools\\app", DeletionHelper.NormalizeFolderPath("C:\\tools\\app\\"));
        Assert.Equal("C:\\tools\\app", DeletionHelper.NormalizeFolderPath("  C:\\tools\\app  "));
        Assert.Equal("C:\\", DeletionHelper.NormalizeFolderPath("C:\\"));
    }

    [Fact]
    public void NormalizeFolderPath_ReturnsEmptyForInputItCannotResolve()
    {
        Assert.Equal(string.Empty, DeletionHelper.NormalizeFolderPath(null));
        Assert.Equal(string.Empty, DeletionHelper.NormalizeFolderPath("   "));
        Assert.Equal(string.Empty, DeletionHelper.NormalizeFolderPath("C:\\tools\0\\app"));
    }
}
