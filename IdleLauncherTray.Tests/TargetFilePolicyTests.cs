using System;
using System.IO;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// <c>TargetFilePolicy</c> decides what the tray app is willing to launch, so a hole here
/// is a hole in the only input validation the app performs on a user-chosen target.
/// </summary>
public sealed class TargetFilePolicyTests
{
    [Theory]
    [InlineData(".exe")]
    [InlineData(".scr")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".lnk")]
    [InlineData(".msi")]
    [InlineData(".ps1")]
    [InlineData(".vbs")]
    [InlineData(".jar")]
    [InlineData(".py")]
    public void IsSupportedTarget_AcceptsEverySupportedExtension(string extension)
    {
        Assert.True(TargetFilePolicy.IsSupportedTarget($"C:\\tools\\launcher{extension}"));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".dll")]
    [InlineData(".com")]
    [InlineData(".pif")]
    [InlineData(".js")]
    [InlineData(".jse")]
    [InlineData(".wsf")]
    [InlineData(".msp")]
    [InlineData(".reg")]
    [InlineData(".docx")]
    [InlineData(".png")]
    [InlineData(".exe.txt")]
    [InlineData(".sh")]
    public void IsSupportedTarget_RejectsUnsupportedExtensions(string extension)
    {
        Assert.False(TargetFilePolicy.IsSupportedTarget($"C:\\tools\\launcher{extension}"));
    }

    [Theory]
    [InlineData("C:\\tools\\LAUNCHER.EXE")]
    [InlineData("C:\\tools\\launcher.Exe")]
    [InlineData("C:\\tools\\launcher.BaT")]
    [InlineData("C:\\tools\\launcher.PS1")]
    [InlineData("C:\\tools\\launcher.Py")]
    public void IsSupportedTarget_IgnoresExtensionCase(string path)
    {
        Assert.True(TargetFilePolicy.IsSupportedTarget(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("\"\"")]
    [InlineData("  \"  \"  ")]
    public void IsSupportedTarget_RejectsEmptyInput(string? path)
    {
        Assert.False(TargetFilePolicy.IsSupportedTarget(path));
    }

    [Theory]
    [InlineData("C:\\tools\\launcher")]
    [InlineData("C:\\tools\\launcher.")]
    [InlineData("C:\\tools\\launcher.exe\\")]
    [InlineData("C:\\")]
    public void IsSupportedTarget_RejectsPathsWithoutAUsableExtension(string path)
    {
        Assert.False(TargetFilePolicy.IsSupportedTarget(path));
    }

    [Fact]
    public void IsSupportedTarget_AcceptsAQuotedPath()
    {
        Assert.True(TargetFilePolicy.IsSupportedTarget("\"C:\\Program Files\\App\\app.exe\""));
    }

    [Fact]
    public void IsSupportedTarget_AcceptsAUncPath()
    {
        Assert.True(TargetFilePolicy.IsSupportedTarget("\\\\fileserver\\share\\tools\\app.exe"));
    }

    [Fact]
    public void IsSupportedTarget_AcceptsARelativePath()
    {
        Assert.True(TargetFilePolicy.IsSupportedTarget("payloads\\app.exe"));
    }

    [Fact]
    public void IsSupportedTarget_DoesNotThrowOnAPathGetFullPathRejects()
    {
        // Longer than Windows will accept, so Path.GetFullPath throws
        // PathTooLongException. The policy has to answer "no", not take the process down
        // while the user is picking a file. Note the string still ends in ".exe": the
        // answer must come from the normalised path, and there isn't one.
        var malformed = "C:\\" + new string('a', 33_000) + ".exe";

        var exception = Record.Exception(() => TargetFilePolicy.IsSupportedTarget(malformed));

        Assert.Null(exception);
        Assert.False(TargetFilePolicy.IsSupportedTarget(malformed));
    }

    [Fact]
    public void IsSupportedTarget_WithAnEmbeddedNul_AnswersOnTheTruncatedPath()
    {
        // Environment.ExpandEnvironmentVariables truncates at an embedded NUL, so the
        // policy never sees the tail. "app\0.exe" becomes "app", which has no extension.
        var withNul = "C:\\tools\\app\0.exe";

        Assert.Null(Record.Exception(() => TargetFilePolicy.IsSupportedTarget(withNul)));
        Assert.False(TargetFilePolicy.IsSupportedTarget(withNul));
        Assert.Equal("C:\\tools\\app", TargetFilePolicy.NormalizePath(withNul));
    }

    [Fact]
    public void IsSupportedTarget_ExpandsEnvironmentVariablesBeforeDeciding()
    {
        // The variable supplies the file name, not the folder: an unexpanded
        // "C:\tools\%ILT_TEST_TARGET%" has no extension at all, so this fails if
        // expansion is dropped. Putting the variable in the directory part would not --
        // "%VAR%\app.exe" still ends in .exe whether it was expanded or not.
        using (new EnvironmentVariableScope("ILT_TEST_TARGET", "app.exe"))
        using (new EnvironmentVariableScope("ILT_TEST_DOC", "notes.txt"))
        {
            Assert.True(TargetFilePolicy.IsSupportedTarget("C:\\tools\\%ILT_TEST_TARGET%"));
            Assert.False(TargetFilePolicy.IsSupportedTarget("C:\\tools\\%ILT_TEST_DOC%"));
            Assert.Equal("C:\\tools\\app.exe", TargetFilePolicy.NormalizePath("C:\\tools\\%ILT_TEST_TARGET%"));
        }
    }

    [Fact]
    public void NormalizePath_StripsSurroundingQuotesAndWhitespace()
    {
        Assert.Equal("C:\\Program Files\\App\\app.exe", TargetFilePolicy.NormalizePath("  \"C:\\Program Files\\App\\app.exe\"  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("     ")]
    [InlineData("\"\"")]
    public void NormalizePath_ReturnsEmptyForEmptyInput(string? path)
    {
        Assert.Equal(string.Empty, TargetFilePolicy.NormalizePath(path));
    }

    [Theory]
    [InlineData("C:/tools/app.exe", "C:\\tools\\app.exe")]
    [InlineData("C:\\tools\\sub\\..\\app.exe", "C:\\tools\\app.exe")]
    [InlineData("C:\\tools\\.\\app.exe", "C:\\tools\\app.exe")]
    public void NormalizePath_CanonicalisesSeparatorsAndDotSegments(string input, string expected)
    {
        Assert.Equal(expected, TargetFilePolicy.NormalizePath(input));
    }

    [Fact]
    public void NormalizePath_PreservesAUncPath()
    {
        Assert.Equal(
            "\\\\fileserver\\share\\tools\\app.exe",
            TargetFilePolicy.NormalizePath("\\\\fileserver\\share\\tools\\app.exe"));
    }

    [Fact]
    public void NormalizePath_ExpandsEnvironmentVariables()
    {
        using (new EnvironmentVariableScope("ILT_TEST_TOOLS", "C:\\tools\\bin"))
        {
            Assert.Equal("C:\\tools\\bin\\app.exe", TargetFilePolicy.NormalizePath("%ILT_TEST_TOOLS%\\app.exe"));
        }
    }

    [Fact]
    public void NormalizePath_ResolvesRelativePathsAgainstTheExecutableFolder_NotTheWorkingDirectory()
    {
        // The tray app is portable and can be started with any working directory (the
        // Run registry key sets none). Anchoring on AppContext.BaseDirectory is what makes
        // a stored relative target mean the same thing on the next launch.
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "payloads", "app.exe"));

        using (new CurrentDirectoryScope(TestDataDirectory.Root))
        {
            var normalized = TargetFilePolicy.NormalizePath("payloads\\app.exe");

            Assert.Equal(expected, normalized);
            Assert.DoesNotContain(TestDataDirectory.Root, normalized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NormalizePath_OnAPathGetFullPathRejects_ReturnsThePreparedCandidateInsteadOfThrowing()
    {
        var tooLong = "C:\\" + new string('a', 33_000) + ".exe";

        var exception = Record.Exception(() => TargetFilePolicy.NormalizePath($"  \"{tooLong}\"  "));

        Assert.Null(exception);
        Assert.Equal(tooLong, TargetFilePolicy.NormalizePath($"  \"{tooLong}\"  "));
    }

    [Fact]
    public void SupportedExtensionsDisplay_ListsEverySupportedExtension()
    {
        // The list is duplicated: a HashSet decides, a const string tells the user. This
        // is the only thing keeping the two halves from drifting apart.
        var display = TargetFilePolicy.SupportedExtensionsDisplay;

        foreach (var extension in new[] { ".exe", ".scr", ".bat", ".cmd", ".lnk", ".msi", ".ps1", ".vbs", ".jar", ".py" })
        {
            Assert.Contains(extension, display, StringComparison.Ordinal);
            Assert.True(
                TargetFilePolicy.IsSupportedTarget($"C:\\tools\\app{extension}"),
                $"'{extension}' is advertised in SupportedExtensionsDisplay but IsSupportedTarget rejects it.");
        }
    }

    [Fact]
    public void GetUnsupportedTargetMessage_WithNoPath_AsksForASupportedType()
    {
        var message = TargetFilePolicy.GetUnsupportedTargetMessage("   ");

        Assert.Equal($"Select a supported target type: {TargetFilePolicy.SupportedExtensionsDisplay}.", message);
    }

    [Fact]
    public void GetUnsupportedTargetMessage_WithAPath_ShowsTheNormalisedPath()
    {
        var message = TargetFilePolicy.GetUnsupportedTargetMessage("  \"C:/tools/readme.txt\"  ");

        Assert.Contains("C:\\tools\\readme.txt", message, StringComparison.Ordinal);
        Assert.Contains(TargetFilePolicy.SupportedExtensionsDisplay, message, StringComparison.Ordinal);
    }
}
