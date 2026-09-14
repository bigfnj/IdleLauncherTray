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
    /// <summary>In declaration order, which is the order <see cref="Enum.GetNames(Type)"/> returns.</summary>
    private static readonly string[] ExpectedTargetPathStatusNames =
        ["Empty", "Unparseable", "UnsupportedType", "Supported"];

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

    [Theory]
    [InlineData("%APPDATA%\\tools\\app.exe")]
    [InlineData("  %APPDATA%\\tools\\app.exe  ")]
    [InlineData("\"%APPDATA%\\tools\\app.exe\"")]
    [InlineData("  \"%APPDATA%\\tools\\app.exe\"  ")]
    public void PrepareForStorage_KeepsEnvironmentVariablesUnexpanded(string asTheUserWroteIt)
    {
        // Trimming and unquoting are lossless; expansion is not. A user who writes %APPDATA% has
        // chosen a spelling that follows the config to another machine, which is the whole point
        // of a portable app -- and a literal path containing %...% has no other way to survive.
        Assert.Equal("%APPDATA%\\tools\\app.exe", TargetFilePolicy.PrepareForStorage(asTheUserWroteIt));
    }

    [Theory]
    [InlineData("payloads\\app.exe")]
    [InlineData("C:/tools/sub/../app.exe")]
    public void PrepareForStorage_DoesNotAnchorOrCollapseThePath(string asTheUserWroteIt)
    {
        // The same rule as expansion, for the same reason: rooting a relative path or collapsing
        // a dot segment bakes in an answer that depends on this machine's layout.
        Assert.Equal(asTheUserWroteIt, TargetFilePolicy.PrepareForStorage($"  \"{asTheUserWroteIt}\"  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("     ")]
    [InlineData("\"\"")]
    [InlineData("  \"  \"  ")]
    public void PrepareForStorage_ReturnsEmptyForEmptyInput(string? path)
    {
        Assert.Equal(string.Empty, TargetFilePolicy.PrepareForStorage(path));
    }

    [Fact]
    public void ResolveForUse_ExpandsWhatPrepareForStorageDeliberatelyKept()
    {
        // The pair is the design: store the portable spelling, resolve it at each point of use.
        // Neither half is worth anything without the other -- storage that cannot be resolved is
        // a dead setting, and resolution that gets stored is the bug this replaced.
        using (new EnvironmentVariableScope("ILT_TEST_TOOLS", "C:\\tools\\bin"))
        {
            var stored = TargetFilePolicy.PrepareForStorage("  \"%ILT_TEST_TOOLS%\\app.exe\"  ");

            Assert.Equal("%ILT_TEST_TOOLS%\\app.exe", stored);
            Assert.Equal("C:\\tools\\bin\\app.exe", TargetFilePolicy.ResolveForUse(stored));
        }
    }

    [Theory]
    [InlineData("C:\\tools\\app.exe")]
    [InlineData("  \"C:/tools/sub/../app.exe\"  ")]
    [InlineData("payloads\\app.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizePath_IsStillTheSameFunctionAsResolveForUse(string? path)
    {
        // NormalizePath survives only because TrayAppContext calls it at its four points of use.
        // If the two ever diverge, half the app resolves a target differently from the other half.
        Assert.Equal(TargetFilePolicy.ResolveForUse(path), TargetFilePolicy.NormalizePath(path));
    }

    [Theory]
    [InlineData("C:\\tools\\app.exe", "Supported")]
    [InlineData("%APPDATA%\\tools\\app.exe", "Supported")]
    [InlineData("C:\\tools\\notes.txt", "UnsupportedType")]
    [InlineData("C:\\tools\\launcher", "UnsupportedType")]
    [InlineData(null, "Empty")]
    [InlineData("   ", "Empty")]
    [InlineData("\"\"", "Empty")]
    public void ClassifyTarget_NamesWhichFaultItIs(string? path, string expectedStatus)
    {
        Assert.Equal(TargetPathStatus.Named(expectedStatus), TargetFilePolicy.ClassifyTarget(path));
    }

    [Fact]
    public void ClassifyTarget_WithAMalformedPath_ReportsAnUnparseablePath_NotAnUnsupportedType()
    {
        // Path.GetFullPath throws on this, so there is no normalised path to read an extension
        // from. Note the string ends in ".exe" -- a *supported* type -- which is exactly why
        // answering "unsupported type" was a diagnosis the user could not act on.
        var malformed = "C:\\" + new string('a', 33_000) + ".exe";

        Assert.Equal(TargetPathStatus.Unparseable, TargetFilePolicy.ClassifyTarget(malformed));
        Assert.NotEqual(TargetPathStatus.UnsupportedType, TargetFilePolicy.ClassifyTarget(malformed));

        // Still refused, so nothing downstream tries to launch it.
        Assert.False(TargetFilePolicy.IsSupportedTarget(malformed));
    }

    [Theory]
    [InlineData("C:\\tools\\app.exe")]
    [InlineData("C:\\tools\\notes.txt")]
    [InlineData("C:\\tools\\launcher")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsSupportedTarget_CannotDisagreeWithClassifyTarget(string? path)
    {
        // IsSupportedTarget is what TrayAppContext still calls. The split into a status must not
        // change a single answer it gives.
        Assert.Equal(
            TargetPathStatus.Supported.Equals(TargetFilePolicy.ClassifyTarget(path)),
            TargetFilePolicy.IsSupportedTarget(path));
    }

    [Fact]
    public void TargetPathStatus_DeclaresExactlyTheFourOutcomesCallersHandle()
    {
        // ConfigManager switches on this enum and treats two of the four as faults with opposite
        // handling. A fifth member added without revisiting that switch would fall into its
        // default and be silently ignored, which is the failure mode this whole codebase has.
        Assert.Equal(ExpectedTargetPathStatusNames, TargetPathStatus.Names);
    }

    [Fact]
    public void GetUnsupportedTargetMessage_WithAMalformedPath_DoesNotBlameTheTargetType()
    {
        var malformed = "C:\\" + new string('a', 33_000) + ".exe";

        var message = TargetFilePolicy.GetUnsupportedTargetMessage(malformed);

        // The old message offered the supported-extension list -- which contains ".exe", the very
        // extension the user had chosen. Telling someone their .exe is not an .exe is worse than
        // saying nothing.
        Assert.DoesNotContain(TargetFilePolicy.SupportedExtensionsDisplay, message, StringComparison.Ordinal);
        Assert.Contains("cannot interpret", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUnsupportedTargetMessage_WithAMalformedPath_ShowsEnoughOfItToRecogniseWithoutDumpingIt()
    {
        var malformed = "C:\\" + new string('a', 33_000) + ".exe";

        var message = TargetFilePolicy.GetUnsupportedTargetMessage(malformed);

        // A 33,000-character MessageBox cannot be read, and the same text reaches a log file that
        // rotates at 2 MB. The count of dropped characters stays, because the length IS the fault
        // being reported.
        Assert.True(message.Length < 1_000, $"the message ran to {message.Length} characters");
        Assert.Contains("aaaa", message, StringComparison.Ordinal);
        Assert.Contains("more characters", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUnsupportedTargetMessage_WithAnUnsupportedType_StillOffersTheSupportedList()
    {
        // The other half of the split: the message that was always right must not have changed.
        var message = TargetFilePolicy.GetUnsupportedTargetMessage("C:\\tools\\notes.txt");

        Assert.Contains(TargetFilePolicy.SupportedExtensionsDisplay, message, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot interpret", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\tools\\app.exe")]
    public void ForDisplay_LeavesAPathAnyoneCouldReadAlone(string path)
    {
        Assert.Equal(path, TargetFilePolicy.ForDisplay(path));
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
