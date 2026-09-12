using System;
using System.IO;
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
    public void Load_KeepsAndNormalisesASupportedAppPath()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile("{ \"AppPath\": \"  \\\"C:/tools/sub/../app.exe\\\"  \" }");

        Assert.Equal("C:\\tools\\app.exe", ConfigManager.Load().AppPath);
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
