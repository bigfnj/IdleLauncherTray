using System;
using System.IO;
using System.Text;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// Every test here runs against a throwaway data directory created by
/// <see cref="TempDataDirectory"/>; none of them can see the real user's config.
/// </summary>
public sealed class ConfigManagerTests
{
    private static readonly int[] AllowedCpuThresholds = [10, 20, 30, 40, 50];

    [Fact]
    public void Load_WithNoConfigFile_ReturnsTheDocumentedDefaults()
    {
        using var data = new TempDataDirectory();

        var config = ConfigManager.Load();

        Assert.Equal(5, config.IdleMinutes);
        Assert.Equal(10, config.CpuThresholdPercent);
        Assert.Equal(string.Empty, config.AppPath);
        Assert.Equal(string.Empty, config.AppArguments);
        Assert.False(config.RunAtStartup);
        Assert.False(config.BlockInjectedWhileRunning);
        Assert.False(config.LockPcOnAppClose);

        // The safe side of the lock gate. False means "do not launch while the PC is locked", and
        // it has to be the default(bool) rather than something a property initialiser supplies.
        Assert.False(config.AllowLaunchWhileLocked);
        Assert.True(config.GamepadCountsAsActivity);
        Assert.True(config.UseSystemIdleFailSafe);
        Assert.Equal(ConfigProxy.MinimumSystemIdleFailSafeWindowMs, config.SystemIdleFailSafeWindowMs);
        Assert.False(config.TrayIconEnabled);
        Assert.Equal(string.Empty, config.TrayIconPath);
        Assert.Equal(string.Empty, config.LastLaunchUtc);
    }

    [Fact]
    public void Load_WithNoConfigFile_DoesNotCreateOne()
    {
        using var data = new TempDataDirectory();

        ConfigManager.Load();

        Assert.False(File.Exists(data.ConfigFile));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        using var data = new TempDataDirectory();

        var saved = new ConfigProxy
        {
            IdleMinutes = 17,
            CpuThresholdPercent = 30,
            AppPath = "C:\\tools\\screensaver.scr",
            AppArguments = "/s --quiet",
            RunAtStartup = true,
            BlockInjectedWhileRunning = true,
            LockPcOnAppClose = true,
            AllowLaunchWhileLocked = true,
            GamepadCountsAsActivity = false,
            UseSystemIdleFailSafe = false,
            SystemIdleFailSafeWindowMs = 9000,
            TrayIconEnabled = true,
            TrayIconPath = "C:\\icons\\tray.ico",
            LastLaunchUtc = "2026-09-11T12:34:56.0000000Z",
        };

        ConfigManager.Save(saved);
        var loaded = ConfigManager.Load();

        Assert.True(File.Exists(data.ConfigFile));
        Assert.Equal(17, loaded.IdleMinutes);
        Assert.Equal(30, loaded.CpuThresholdPercent);
        Assert.Equal("C:\\tools\\screensaver.scr", loaded.AppPath);
        Assert.Equal("/s --quiet", loaded.AppArguments);
        Assert.True(loaded.RunAtStartup);
        Assert.True(loaded.BlockInjectedWhileRunning);
        Assert.True(loaded.LockPcOnAppClose);
        Assert.True(loaded.AllowLaunchWhileLocked);
        Assert.False(loaded.GamepadCountsAsActivity);
        Assert.False(loaded.UseSystemIdleFailSafe);
        Assert.Equal(9000, loaded.SystemIdleFailSafeWindowMs);
        Assert.True(loaded.TrayIconEnabled);
        Assert.Equal("C:\\icons\\tray.ico", loaded.TrayIconPath);
        Assert.Equal("2026-09-11T12:34:56.0000000Z", loaded.LastLaunchUtc);
    }

    [Fact]
    public void Save_WritesIntoTheConfiguredDataDirectory_AndLeavesNoStagingFile()
    {
        using var data = new TempDataDirectory();

        ConfigManager.Save(new ConfigProxy { IdleMinutes = 3 });

        Assert.True(File.Exists(data.ConfigFile));
        Assert.False(File.Exists(data.ConfigFile + ".tmp"));
        Assert.Contains("\"IdleMinutes\": 3", File.ReadAllText(data.ConfigFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_CreatesTheDataDirectoryWhenItIsMissing()
    {
        using var data = new TempDataDirectory();
        Directory.Delete(data.Path, recursive: true);
        Assert.False(Directory.Exists(data.Path));

        ConfigManager.Save(new ConfigProxy());

        Assert.True(File.Exists(data.ConfigFile));
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(1440, 1440)]
    public void Load_ClampsIdleMinutesToAtLeastOne(int stored, int expected)
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile($"{{ \"IdleMinutes\": {stored} }}");

        Assert.Equal(expected, ConfigManager.Load().IdleMinutes);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(40_000_000)]
    [InlineData(35_791_395)]
    public void Load_ClampsIdleMinutesBelowTheValueThatOverflowsTheSecondsConversion(int stored)
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile($"{{ \"IdleMinutes\": {stored} }}");

        var minutes = ConfigManager.Load().IdleMinutes;

        // The bound that matters is not the constant, it is that TrayAppContext can still compute
        // `IdleMinutes * 60` into an int without wrapping. An unclamped 40,000,000 becomes about
        // -1.89 billion seconds, and readiness tests `IdleSeconds >= RequiredIdleSeconds`, so a
        // negative threshold is satisfied on every tick and the app launches its target forever.
        // Assert the real property rather than the clamp value, so this test still means something
        // if the constant is ever retuned.
        Assert.Equal(stored > ConfigManager.MaximumIdleMinutes ? ConfigManager.MaximumIdleMinutes : stored, minutes);
        Assert.True(
            (long)minutes * 60 <= int.MaxValue,
            $"IdleMinutes={minutes} still overflows when converted to seconds.");
        Assert.True(minutes * 60 > 0, $"IdleMinutes={minutes} produced a non-positive second count.");
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(2_000_000_000)]
    public void Load_ClampsAnAbsurdFailSafeWindow(int stored)
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile($"{{ \"SystemIdleFailSafeWindowMs\": {stored} }}");

        // Unclamped this fails the opposite way to the idle threshold: the window decides when a
        // smaller GetLastInputInfo reading is trusted over the hook clock, so an enormous value
        // means it is always trusted and measured idle can never accumulate. The app stops
        // launching entirely and reports nothing.
        Assert.Equal(ConfigManager.MaximumSystemIdleFailSafeWindowMs, ConfigManager.Load().SystemIdleFailSafeWindowMs);
    }

    [Fact]
    public void Load_WithAnUnreadableFile_MovesItAsideInsteadOfLettingItBeOverwritten()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile("{ \"IdleMinutes\": 42, \"AppPath\": \"C:\\\\tools\\\\real.exe\"");  // truncated on purpose

        var loaded = ConfigManager.Load();

        // Defaults are returned so the app still starts.
        Assert.Equal(5, loaded.IdleMinutes);

        // The original is preserved. Without this the caller's very next Save -- which fires
        // whenever RunAtStartup disagrees with the registry, the normal case for this app --
        // writes those defaults straight over the user's real settings, and the only trace is one
        // line in a log nobody opens.
        var quarantined = Directory.GetFiles(Path.GetDirectoryName(data.ConfigFile)!, "config.corrupt-*.json");
        Assert.True(quarantined.Length == 1, $"expected exactly one quarantined config, found {quarantined.Length}");
        Assert.Contains("C:\\\\tools\\\\real.exe", File.ReadAllText(quarantined[0]), StringComparison.Ordinal);
        Assert.False(File.Exists(data.ConfigFile), "the unreadable config should have been moved, not copied");
    }

    [Theory]
    [InlineData(int.MinValue, 10)]
    [InlineData(-40, 10)]
    [InlineData(0, 10)]
    [InlineData(4, 10)]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    [InlineData(14, 10)]
    [InlineData(15, 20)]
    [InlineData(24, 20)]
    [InlineData(25, 30)]
    [InlineData(30, 30)]
    [InlineData(44, 40)]
    [InlineData(45, 50)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(100, 50)]
    public void Load_SnapsCpuThresholdToTheAllowedSteps(int stored, int expected)
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile($"{{ \"CpuThresholdPercent\": {stored} }}");

        var loaded = ConfigManager.Load().CpuThresholdPercent;

        Assert.Equal(expected, loaded);
        Assert.Equal(ConfigProxy.NormalizeCpuThresholdPercent(stored), loaded);
        Assert.Contains(loaded, AllowedCpuThresholds);
    }

    [Theory]
    [InlineData(true, -5000, 6000)]
    [InlineData(true, 0, 6000)]
    [InlineData(true, 5999, 6000)]
    [InlineData(true, 6000, 6000)]
    [InlineData(true, 9000, 9000)]
    [InlineData(false, -5000, 0)]
    [InlineData(false, 0, 0)]
    [InlineData(false, 2000, 2000)]
    public void Load_ClampsTheFailSafeWindow(bool failSafeEnabled, int stored, int expected)
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile(
            $"{{ \"UseSystemIdleFailSafe\": {(failSafeEnabled ? "true" : "false")}, \"SystemIdleFailSafeWindowMs\": {stored} }}");

        Assert.Equal(expected, ConfigManager.Load().SystemIdleFailSafeWindowMs);
    }

    [Theory]
    [InlineData("C:\\\\tools\\\\notes.txt")]
    [InlineData("C:\\\\tools\\\\payload.dll")]
    [InlineData("C:\\\\tools\\\\payload.com")]
    [InlineData("C:\\\\tools\\\\payload")]
    public void Load_ClearsAnAppPathWithAnUnsupportedTargetType(string storedJsonPath)
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile($"{{ \"AppPath\": \"{storedJsonPath}\" }}");

        Assert.Equal(string.Empty, ConfigManager.Load().AppPath);
    }

    [Fact]
    public void Load_KeepsASupportedAppPathAsTheUserWroteIt()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile("{ \"AppPath\": \"  \\\"C:/tools/sub/../app.exe\\\"  \" }");

        var loaded = ConfigManager.Load();

        // Trimmed and unquoted -- both lossless -- and nothing else. Collapsing the dot segment
        // would be harmless on its own, but it is the same step that expands %APPDATA%, and
        // normalisation runs on Save too, so whatever this method returns is what lands on disk.
        Assert.Equal("C:/tools/sub/../app.exe", loaded.AppPath);

        // ...and it still resolves to the real file at the point of use.
        Assert.Equal("C:\\tools\\app.exe", TargetFilePolicy.ResolveForUse(loaded.AppPath));
    }

    [Fact]
    public void SaveThenLoad_LeavesAnEnvironmentVariablePathUnexpandedInTheFile()
    {
        using var data = new TempDataDirectory();

        ConfigManager.Save(new ConfigProxy { AppPath = "%APPDATA%\\tools\\app.exe" });

        // Assert on the FILE, not on the object. The object could hold the right string while Save
        // wrote the expanded one, and the file is the half that travels to the next machine.
        var onDisk = File.ReadAllText(data.ConfigFile);
        Assert.Contains("\"AppPath\": \"%APPDATA%\\\\tools\\\\app.exe\"", onDisk, StringComparison.Ordinal);

        // JSON doubles every backslash, so the expanded form has to be doubled before looking for
        // it -- searching for the raw spelling would find nothing here even when the bug is back.
        var expanded = Environment.ExpandEnvironmentVariables("%APPDATA%\\tools\\app.exe");
        Assert.NotEqual("%APPDATA%\\tools\\app.exe", expanded); // the check is only real if %APPDATA% resolves
        Assert.DoesNotContain(
            expanded.Replace("\\", "\\\\", StringComparison.Ordinal),
            onDisk,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal("%APPDATA%\\tools\\app.exe", ConfigManager.Load().AppPath);
    }

    [Fact]
    public void Load_WithAnEnvironmentVariablePath_StillResolvesToAUsableAbsolutePath()
    {
        using var data = new TempDataDirectory();
        using var tools = new EnvironmentVariableScope("ILT_TEST_TOOLS", "C:\\tools\\bin");
        data.WriteConfigFile("{ \"AppPath\": \"%ILT_TEST_TOOLS%\\\\app.exe\" }");

        var loaded = ConfigManager.Load();

        Assert.Equal("%ILT_TEST_TOOLS%\\app.exe", loaded.AppPath);
        Assert.Equal("C:\\tools\\bin\\app.exe", TargetFilePolicy.ResolveForUse(loaded.AppPath));
    }

    [Fact]
    public void Load_ChecksTheTargetTypeThroughAnEnvironmentVariable_AndKeepsTheVariable()
    {
        // The support check has to keep working on the RAW string now that the raw string is what
        // is stored, which it does because ClassifyTarget expands internally. Otherwise keeping the
        // user's spelling would have quietly disabled the only validation the app performs.
        //
        // The variable supplies the file NAME, so the unexpanded path has no extension at all and
        // would be cleared as an unsupported type. Asserting the .exe is KEPT is therefore an
        // assertion that expansion really happened -- an unsupported variable would be cleared
        // either way and would prove nothing, which a mutation run demonstrated.
        using var data = new TempDataDirectory();
        using var target = new EnvironmentVariableScope("ILT_TEST_TARGET", "app.exe");
        data.WriteConfigFile("{ \"IdleMinutes\": 9, \"AppPath\": \"C:\\\\tools\\\\%ILT_TEST_TARGET%\" }");

        var loaded = ConfigManager.Load();

        // IdleMinutes pins that the file was really read: a swallowed parse failure hands back
        // defaults, and those have nothing to say about expansion either way.
        Assert.Equal(9, loaded.IdleMinutes);
        Assert.Equal("C:\\tools\\%ILT_TEST_TARGET%", loaded.AppPath);
        Assert.Equal("C:\\tools\\app.exe", TargetFilePolicy.ResolveForUse(loaded.AppPath));
    }

    [Fact]
    public void Load_WithAnUnsupportedExtension_ClearsItAndRecordsThatTheTypeWasTheFault()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile("{ \"AppPath\": \"C:\\\\tools\\\\notes.txt\" }");

        var log = new LogCapture();
        var loaded = ConfigManager.Load();

        Assert.Equal(string.Empty, loaded.AppPath);

        // The wording is the deliverable, not decoration: clearing a setting silently is how this
        // app loses user data, so the log has to name what went and why.
        Assert.Contains("unsupported target TYPE", log.Text, StringComparison.Ordinal);
        Assert.Contains("cleared", log.Text, StringComparison.Ordinal);
        Assert.Contains("C:\\tools\\notes.txt", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithAMalformedAppPath_KeepsItAndRecordsThatThePathWasTheFault()
    {
        using var data = new TempDataDirectory();
        var malformed = "C:\\" + new string('a', 33_000) + ".exe";
        data.WriteConfigFile($"{{ \"AppPath\": \"C:\\\\{new string('a', 33_000)}.exe\" }}");

        var log = new LogCapture();
        var loaded = ConfigManager.Load();

        // Kept, not cleared. The stored value is no longer environment-expanded, so whether it
        // parses is a fact about THIS machine: clearing would destroy a setting made on another
        // one, and because normalisation runs on Save the loss would reach disk immediately.
        Assert.Equal(malformed, loaded.AppPath);

        // Nothing will launch it here, which is what makes keeping it safe rather than a trap.
        Assert.False(TargetFilePolicy.IsSupportedTarget(loaded.AppPath));

        // And the log says which fault it was, in the opposite direction to the test above.
        Assert.Contains("cannot interpret", log.Text, StringComparison.Ordinal);
        Assert.Contains("KEPT", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported target TYPE", log.Text, StringComparison.Ordinal);

        // The log rotates at 2 MB. A 33,000-character path per Load would be a real cost, and the
        // truncation is the only thing keeping one malformed setting from filling it.
        Assert.True(log.Text.Length < 2_000, $"one Load wrote {log.Text.Length} characters of log");
    }

    [Fact]
    public void Save_WritesTheSameBytesTheNonDurableImplementationDid()
    {
        using var data = new TempDataDirectory();

        ConfigManager.Save(new ConfigProxy
        {
            IdleMinutes = 17,
            CpuThresholdPercent = 30,
            AppPath = "%APPDATA%\\tools\\app.exe",
            AppArguments = "/s --quiet",
            RunAtStartup = true,
            BlockInjectedWhileRunning = true,
            LockPcOnAppClose = true,
            AllowLaunchWhileLocked = true,
            GamepadCountsAsActivity = false,
            UseSystemIdleFailSafe = false,
            SystemIdleFailSafeWindowMs = 9000,
            TrayIconEnabled = true,
            TrayIconPath = "C:\\icons\\tray.ico",
            LastLaunchUtc = "2026-09-11T12:34:56.0000000Z",
        });

        // Joined on an explicit "\r\n" rather than written as a multi-line literal: this source
        // file is LF (.editorconfig) while Utf8JsonWriter indents with Environment.NewLine, so a
        // verbatim literal would pin the wrong bytes and match the file only by accident.
        var expected = string.Join(
            "\r\n",
            "{",
            "  \"IdleMinutes\": 17,",
            "  \"CpuThresholdPercent\": 30,",
            "  \"AppPath\": \"%APPDATA%\\\\tools\\\\app.exe\",",
            "  \"AppArguments\": \"/s --quiet\",",
            "  \"RunAtStartup\": true,",
            "  \"BlockInjectedWhileRunning\": true,",
            "  \"LockPcOnAppClose\": true,",
            "  \"AllowLaunchWhileLocked\": true,",
            "  \"GamepadCountsAsActivity\": false,",
            "  \"UseSystemIdleFailSafe\": false,",
            "  \"SystemIdleFailSafeWindowMs\": 9000,",
            "  \"TrayIconEnabled\": true,",
            "  \"TrayIconPath\": \"C:\\\\icons\\\\tray.ico\",",
            "  \"LastLaunchUtc\": \"2026-09-11T12:34:56.0000000Z\"",
            "}");

        var actual = File.ReadAllBytes(data.ConfigFile);

        // Bytes, not text. Swapping File.WriteAllText for a FileStream was supposed to change WHEN
        // the file reaches the disk and nothing about what is in it -- and the one difference a
        // string comparison would miss is a BOM, which this app would read back happily while
        // every other reader of config.json saw a changed file.
        Assert.Equal(Encoding.UTF8.GetBytes(expected), actual);
        Assert.False(
            actual.Length >= 3 && actual[0] == 0xEF && actual[1] == 0xBB && actual[2] == 0xBF,
            "config.json was written with a UTF-8 BOM");
    }

    [Fact]
    public void Load_TrimsTheFreeTextFields()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile(
            "{ \"AppArguments\": \"  /s  \", \"TrayIconPath\": \"  C:\\\\icons\\\\t.ico  \", \"LastLaunchUtc\": \"  2026-01-01T00:00:00Z  \" }");

        var config = ConfigManager.Load();

        Assert.Equal("/s", config.AppArguments);
        Assert.Equal("C:\\icons\\t.ico", config.TrayIconPath);
        Assert.Equal("2026-01-01T00:00:00Z", config.LastLaunchUtc);
    }

    [Fact]
    public void Load_TurnsExplicitJsonNullsIntoEmptyStrings()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile(
            "{ \"IdleMinutes\": 7, \"AppPath\": null, \"AppArguments\": null, \"TrayIconPath\": null, \"LastLaunchUtc\": null }");

        var config = ConfigManager.Load();

        // IdleMinutes pins down that the file was really read: if normalisation threw on
        // one of the nulls, Load would swallow it and hand back defaults, whose strings
        // are empty too -- the assertions below would pass on a broken code path.
        Assert.Equal(7, config.IdleMinutes);
        Assert.Equal(string.Empty, config.AppPath);
        Assert.Equal(string.Empty, config.AppArguments);
        Assert.Equal(string.Empty, config.TrayIconPath);
        Assert.Equal(string.Empty, config.LastLaunchUtc);
    }

    [Fact]
    public void Save_NormalisesBeforeWriting_SoTheFileOnDiskIsAlreadyClamped()
    {
        using var data = new TempDataDirectory();

        var config = new ConfigProxy
        {
            IdleMinutes = 0,
            CpuThresholdPercent = 37,
            AppPath = "C:\\tools\\notes.txt",
            AppArguments = "   trimmed   ",
        };

        ConfigManager.Save(config);
        var onDisk = File.ReadAllText(data.ConfigFile);

        Assert.Contains("\"IdleMinutes\": 1", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"CpuThresholdPercent\": 40", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"AppPath\": \"\"", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"AppArguments\": \"trimmed\"", onDisk, StringComparison.Ordinal);

        // Save normalises the caller's instance in place, not a copy.
        Assert.Equal(1, config.IdleMinutes);
        Assert.Equal(40, config.CpuThresholdPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ this is not json")]
    [InlineData("{ \"IdleMinutes\": }")]
    [InlineData("{ \"IdleMinutes\": \"not a number\" }")]
    [InlineData("[1, 2, 3]")]
    [InlineData("<?xml version=\"1.0\"?><config/>")]
    [InlineData("\0\0\0\0")]
    public void Load_WithAnUnparseableFile_ReturnsDefaultsInsteadOfThrowing(string contents)
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile(contents);

        var exception = Record.Exception(() => ConfigManager.Load());

        Assert.Null(exception);
        Assert.Equal(5, ConfigManager.Load().IdleMinutes);
        Assert.Equal(10, ConfigManager.Load().CpuThresholdPercent);
    }

    [Fact]
    public void Load_WithAJsonNullDocument_ReturnsDefaults()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile("null");

        Assert.Equal(5, ConfigManager.Load().IdleMinutes);
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingCommas()
    {
        // The deserialiser is configured to tolerate a hand-edited file; if that ever
        // regresses the user's settings silently revert to defaults.
        using var data = new TempDataDirectory();
        data.WriteConfigFile("{\n  // how long to wait\n  \"IdleMinutes\": 12,\n  \"RunAtStartup\": true,\n}");

        var config = ConfigManager.Load();

        Assert.Equal(12, config.IdleMinutes);
        Assert.True(config.RunAtStartup);
    }

    [Fact]
    public void Load_WithTheLockKeyAbsent_DefaultsToNotLaunchingWhileLocked()
    {
        // The upgrade path, and the reason the setting is spelled "Allow..." rather than
        // "Block...". Every config.json written before this version is missing the key entirely,
        // so the default has to be the safe answer without any migration step -- and IdleMinutes
        // pins that the file really was read, since a swallowed parse failure would hand back
        // defaults whose AllowLaunchWhileLocked is false too.
        using var data = new TempDataDirectory();
        data.WriteConfigFile("{ \"IdleMinutes\": 9, \"LockPcOnAppClose\": true }");

        var config = ConfigManager.Load();

        Assert.Equal(9, config.IdleMinutes);
        Assert.True(config.LockPcOnAppClose);
        Assert.False(config.AllowLaunchWhileLocked);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Load_ReadsTheStoredLockSetting(string storedJson, bool expected)
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile($"{{ \"AllowLaunchWhileLocked\": {storedJson} }}");

        Assert.Equal(expected, ConfigManager.Load().AllowLaunchWhileLocked);
    }

    [Fact]
    public void Load_IgnoresUnknownProperties()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile("{ \"IdleMinutes\": 8, \"SomeSettingFromAFutureVersion\": 42 }");

        Assert.Equal(8, ConfigManager.Load().IdleMinutes);
    }

    [Fact]
    public void Save_WithAnUnwritableDataDirectory_DoesNotThrow()
    {
        // Config persistence is best-effort by design: a failed write must not take down
        // a tray app the user cannot see.
        using var data = new TempDataDirectory();
        using var blocked = new EnvironmentVariableScope(
            TestDataDirectory.OverrideVariableName,
            Path.Combine(data.Path, "a-file-not-a-folder"));

        File.WriteAllText(Path.Combine(data.Path, "a-file-not-a-folder"), "in the way");

        Assert.Null(Record.Exception(() => ConfigManager.Save(new ConfigProxy())));
    }

    [Fact]
    public void Load_WithAnUnreadableDataDirectory_ReturnsDefaults()
    {
        using var data = new TempDataDirectory();
        using var blocked = new EnvironmentVariableScope(
            TestDataDirectory.OverrideVariableName,
            Path.Combine(data.Path, "a-file-not-a-folder"));

        File.WriteAllText(Path.Combine(data.Path, "a-file-not-a-folder"), "in the way");

        var config = ConfigManager.Load();

        Assert.Equal(5, config.IdleMinutes);
        Assert.Equal(10, config.CpuThresholdPercent);
    }
}
