using System;
using System.Diagnostics;
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

    // ---- Argument parsing -------------------------------------------------------------
    //
    // This is the surface an attacker reaches: IdleLauncherTray.exe is on disk and anyone can
    // run it with any arguments they like. The parser therefore has to be TOTAL -- defined for
    // every string[] that can exist -- and the tests below cover the shapes that used to be
    // short-circuited by an `args.Length < 2` guard, which was removed because it could not
    // change the result for any input at all.

    [Fact]
    public void TryParseCleanupArgs_WithNoArguments_ReportsNothingToDo()
    {
        // One of the two inputs the deleted length guard used to catch. It returns false here
        // for the reason it always really returned false: no --cleanup-folder value was found.
        Assert.False(DeletionHelper.TryParseCleanupArgs([], out var folder, out var parentPid));
        Assert.Equal(string.Empty, folder);
        Assert.Equal(0, parentPid);
    }

    [Theory]
    [InlineData("--cleanup-folder")]
    [InlineData("--cleanup-parent-pid")]
    [InlineData("C:\\Windows")]
    [InlineData("")]
    public void TryParseCleanupArgs_WithASingleArgument_ReportsNothingToDo(string only)
    {
        // The other input the deleted guard caught. A lone flag has no value after it, and every
        // branch in the loop independently checks `i + 1 < args.Length` before reading one --
        // which is why removing the guard was a provable no-op rather than a judgement call.
        Assert.False(DeletionHelper.TryParseCleanupArgs([only], out var folder, out var parentPid));
        Assert.Equal(string.Empty, folder);
        Assert.Equal(0, parentPid);
    }

    [Fact]
    public void TryParseCleanupArgs_WithAFlagAsTheLastArgument_IgnoresItInsteadOfReadingPastTheEnd()
    {
        string[] args = [DeletionHelper.CleanupFolderArg, "C:\\data\\app", DeletionHelper.CleanupParentPidArg];

        Assert.True(DeletionHelper.TryParseCleanupArgs(args, out var folder, out var parentPid));
        Assert.Equal("C:\\data\\app", folder);
        Assert.Equal(0, parentPid);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99999999999999999999")]
    [InlineData("")]
    [InlineData("12.5")]
    public void TryParseCleanupArgs_WithAnUnusableParentPid_KeepsParsingAndLeavesThePidAtZero(string unusable)
    {
        // A junk pid must not sink the whole command line: the folder is the part that matters,
        // and a zero pid simply means "we have nothing to wait for", which WaitForParentExit
        // already handles.
        string[] args =
        [
            DeletionHelper.CleanupParentPidArg,
            unusable,
            DeletionHelper.CleanupFolderArg,
            "C:\\data\\app"
        ];

        Assert.True(DeletionHelper.TryParseCleanupArgs(args, out var folder, out var parentPid));
        Assert.Equal("C:\\data\\app", folder);
        Assert.Equal(0, parentPid);
    }

    [Fact]
    public void TryParseCleanupArgs_MatchesFlagNamesWithoutRegardToCase()
    {
        // Windows command lines are routinely retyped by hand and by scripts; the parser has
        // always been OrdinalIgnoreCase, and this pins it so a switch to a case-sensitive
        // comparison cannot pass as a tidy-up.
        string[] args =
        [
            DeletionHelper.CleanupFolderArg.ToUpperInvariant(),
            "C:\\data\\app",
            DeletionHelper.CleanupParentPidArg.ToUpperInvariant(),
            "4321"
        ];

        Assert.True(DeletionHelper.TryParseCleanupArgs(args, out var folder, out var parentPid));
        Assert.Equal("C:\\data\\app", folder);
        Assert.Equal(4321, parentPid);
    }

    [Fact]
    public void TryParseCleanupArgs_WithAValidPid_ReadsIt()
    {
        string[] args = [DeletionHelper.CleanupFolderArg, "C:\\data\\app", DeletionHelper.CleanupParentPidArg, "1234"];

        Assert.True(DeletionHelper.TryParseCleanupArgs(args, out var folder, out var parentPid));
        Assert.Equal("C:\\data\\app", folder);
        Assert.Equal(1234, parentPid);
    }

    [Fact]
    public void TryParseCleanupArgs_WithTheRetiredWaitFlagAlone_FindsNothingToDelete()
    {
        // --cleanup-wait-ms is gone. Its only producer always sent the same compile-time
        // constant and its only consumer floored the value at that same constant, so across
        // every possible input the flag could only ever make uninstall slower. What matters now
        // is that the retired tokens are INERT on a command line the tests already treat as
        // attacker-reachable -- above all that neither the flag nor its value can drift into
        // the folder slot, which is the argument that authorises a recursive delete.
        Assert.False(DeletionHelper.TryParseCleanupArgs(["--cleanup-wait-ms", "5000"], out var folder, out var parentPid));
        Assert.Equal(string.Empty, folder);
        Assert.Equal(0, parentPid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryParseCleanupArgs_WithTheRetiredWaitFlagAlongsideRealArguments_IgnoresIt(bool waitFlagFirst)
    {
        string[] waitFlagBeforeTheFolder = ["--cleanup-wait-ms", "5000", DeletionHelper.CleanupFolderArg, "C:\\data\\app"];
        string[] waitFlagAfterTheFolder = [DeletionHelper.CleanupFolderArg, "C:\\data\\app", "--cleanup-wait-ms", "5000"];

        var args = waitFlagFirst ? waitFlagBeforeTheFolder : waitFlagAfterTheFolder;

        Assert.True(DeletionHelper.TryParseCleanupArgs(args, out var folder, out var parentPid));
        Assert.Equal("C:\\data\\app", folder);
        Assert.Equal(0, parentPid);
    }

    [Fact]
    public void DeletionHelper_NoLongerDeclaresTheRetiredWaitArgument()
    {
        // Pins the removal itself. Without this the constant could come back tomorrow with a
        // parse branch behind it, and every test above would still pass -- they assert that the
        // TOKENS are inert, which a reinstated flag would quietly stop being true.
        Assert.False(DeletionHelper.DeclaresField("CleanupWaitMsArg"));
        Assert.False(DeletionHelper.DeclaresField("DefaultInitialWaitMs"));
    }

    // ---- The delete itself ------------------------------------------------------------
    //
    // These are the only tests in this file that touch the disk. Every path is built under
    // TempDataDirectory.Path, which lives inside the run's temp root, so the worst a mistake
    // here can do is delete a folder this test created seconds earlier.

    [Fact]
    public void TryDeleteFolderWithRetries_WhenTheFolderIsAlreadyGone_ReportsSuccessImmediately()
    {
        using var data = new TempDataDirectory();
        var missing = Path.Combine(data.Path, "never-existed");

        Assert.False(Directory.Exists(missing));
        Assert.True(DeletionHelper.TryDeleteFolderWithRetries(missing, DeletionHelper.MaxInProcessDeleteAttempts));
    }

    [Fact]
    public void TryDeleteFolderWithRetries_DeletesReadOnlyFilesAndReadOnlySubdirectories()
    {
        // The regression test for hoisting ClearReadOnlyAttributes out of the retry loop. The
        // attribute walk now runs exactly once, before the first attempt, so if the hoist ever
        // lands above the wrong line -- or the call is dropped as "the loop already did it" --
        // this is the test that stops it. A ReadOnly bit on either a file or a directory is
        // enough to make Directory.Delete(recursive: true) fail.
        using var data = new TempDataDirectory();
        var target = Path.Combine(data.Path, "read-only-tree");
        var subDirectory = Path.Combine(target, "sub");
        Directory.CreateDirectory(subDirectory);

        var readOnlyFile = Path.Combine(subDirectory, "pinned.txt");
        File.WriteAllText(readOnlyFile, "content");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        var subDirectoryInfo = new DirectoryInfo(subDirectory);
        subDirectoryInfo.Attributes |= FileAttributes.ReadOnly;

        Assert.True(DeletionHelper.TryDeleteFolderWithRetries(target, DeletionHelper.MaxInProcessDeleteAttempts));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void TryDeleteFolderWithRetries_OnItsFinalAttempt_ReturnsWithoutSleepingFirst()
    {
        // The regression test for skipping the sleep after the last attempt. It runs with
        // maxAttempts: 1 rather than 3 on purpose -- one attempt means the ONLY sleep that could
        // occur is the retired one, so the margin is a full DeleteRetryDelayMs and the assertion
        // cannot flake under load the way a tighter budget would.
        //
        // This matters because the in-process paths run synchronously on the UI thread during
        // uninstall, after the tray icon has already been hidden: every millisecond spent here
        // is a millisecond the user spends looking at a tray where the icon vanished and nothing
        // happened.
        using var data = new TempDataDirectory();
        var target = Path.Combine(data.Path, "held-open");
        Directory.CreateDirectory(target);

        using var handle = new FileStream(
            Path.Combine(target, "held.bin"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        handle.WriteByte(0x2A);
        handle.Flush();

        var stopwatch = Stopwatch.StartNew();
        var deleted = DeletionHelper.TryDeleteFolderWithRetries(target, maxAttempts: 1);
        stopwatch.Stop();

        Assert.False(deleted, "A folder holding a file opened with FileShare.None cannot be deleted.");
        Assert.True(Directory.Exists(target));
        Assert.True(
            stopwatch.ElapsedMilliseconds < DeletionHelper.DeleteRetryDelayMs,
            $"A single attempt took {stopwatch.ElapsedMilliseconds}ms, which means it slept the "
            + $"{DeletionHelper.DeleteRetryDelayMs}ms retry delay after the last attempt.");
    }

    [Fact]
    public void TryDeleteFolderWithRetries_BetweenTwoAttempts_StillWaitsForWhateverHoldsTheFolder()
    {
        // The other half of the pair. The change was "skip the sleep after the LAST attempt",
        // not "stop sleeping" -- and without this test, deleting the delay outright would pass
        // the test above and silently turn the retry loop into three instant attempts that give
        // a transient lock no time at all to clear.
        using var data = new TempDataDirectory();
        var target = Path.Combine(data.Path, "held-open-twice");
        Directory.CreateDirectory(target);

        using var handle = new FileStream(
            Path.Combine(target, "held.bin"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        handle.WriteByte(0x2A);
        handle.Flush();

        var stopwatch = Stopwatch.StartNew();
        var deleted = DeletionHelper.TryDeleteFolderWithRetries(target, maxAttempts: 2);
        stopwatch.Stop();

        Assert.False(deleted);

        // Only a lower bound: the machine can always be slower, never faster than a sleep it
        // actually performed. The small allowance absorbs the difference between the OS timer
        // that schedules Thread.Sleep and the QPC clock Stopwatch reads.
        Assert.True(
            stopwatch.ElapsedMilliseconds >= DeletionHelper.DeleteRetryDelayMs - 50,
            $"Two attempts took only {stopwatch.ElapsedMilliseconds}ms, so the "
            + $"{DeletionHelper.DeleteRetryDelayMs}ms delay between them never happened.");
    }

    [Fact]
    public void TryDeleteFolderWithRetries_DeletesAPopulatedFolderOutright()
    {
        using var data = new TempDataDirectory();
        var target = Path.Combine(data.Path, "ordinary-tree");
        Directory.CreateDirectory(Path.Combine(target, "nested", "deeper"));
        File.WriteAllText(Path.Combine(target, "config.json"), "{}");
        File.WriteAllText(Path.Combine(target, "nested", "deeper", "tray.ico"), "not really an icon");

        Assert.True(DeletionHelper.TryDeleteFolderWithRetries(target, DeletionHelper.MaxInProcessDeleteAttempts));
        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(data.Path), "The delete must stay inside the folder it was given.");
    }

    [Fact]
    public void InProcessAttempts_AreFewerThanTheDeferredChildsAttempts()
    {
        // The asymmetry is the decision: the child is unobserved and can afford to keep trying,
        // while the in-process fallbacks block the UI thread with the tray icon already hidden.
        Assert.True(DeletionHelper.MaxInProcessDeleteAttempts < DeletionHelper.MaxDeleteAttempts);
        Assert.Equal(3, DeletionHelper.MaxInProcessDeleteAttempts);
    }
}
