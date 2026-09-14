using System;
using System.IO;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// A target path naming an environment variable this machine does not define.
/// </summary>
/// <remarks>
/// v2.7 stopped expanding the stored path so a portable config survives being moved between
/// machines. That made "does this have a supported extension" a property of the CURRENT MACHINE'S
/// ENVIRONMENT rather than of the string, because <c>ExpandEnvironmentVariables</c> leaves an
/// undefined <c>%NAME%</c> untouched and the result then has no extension at all.
/// <para>
/// Classified as an unsupported TYPE, such a target was CLEARED — and because
/// <c>NormalizeInPlace</c> also runs on save, the loss reached disk and was unrecoverable. That is
/// precisely the misdiagnosis the <c>Unparseable</c> status was introduced to end, reappearing one
/// enum member over. Reachable in a single launch: a Run-key entry racing the logon script that
/// defines the variable.
/// </para>
/// </remarks>
public sealed class UnresolvedVariableTargetTests
{
    [Theory]
    [InlineData("C:\\tools\\%ILT_DOES_NOT_EXIST%")]
    [InlineData("%ILT_DOES_NOT_EXIST%\\app.exe")]
    [InlineData("C:\\tools\\%ILT_DOES_NOT_EXIST%.exe")]
    public void ClassifyTarget_WithAnUndefinedEnvironmentVariable_IsUnparseableRatherThanUnsupportedType(string path)
    {
        // Unparseable is KEPT by ConfigManager; UnsupportedType is CLEARED. Getting this wrong
        // deletes a setting that is valid on the machine the config came from.
        Assert.Equal(TargetPathStatus.Unparseable, TargetFilePolicy.ClassifyTarget(path));
        Assert.NotEqual(TargetPathStatus.UnsupportedType, TargetFilePolicy.ClassifyTarget(path));
    }

    [Fact]
    public void Load_WithAnUndefinedEnvironmentVariableInTheTarget_KeepsTheStoredPath()
    {
        using var data = new TempDataDirectory();
        data.WriteConfigFile("{ \"AppPath\": \"C:\\\\tools\\\\%ILT_DOES_NOT_EXIST%\\\\app.exe\" }");

        var loaded = ConfigManager.Load();

        // The whole point: it must still be there for the machine where the variable IS defined.
        Assert.Contains("%ILT_DOES_NOT_EXIST%", loaded.AppPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyTarget_WithADefinedEnvironmentVariable_IsStillJudgedOnTheResolvedExtension()
    {
        // The variable resolves, so the extension is knowable and the normal rules apply. Uses
        // WINDIR because it is defined on every Windows machine this can run on.
        Assert.Equal(TargetPathStatus.Supported, TargetFilePolicy.ClassifyTarget("%WINDIR%\\System32\\notepad.exe"));
        Assert.Equal(TargetPathStatus.UnsupportedType, TargetFilePolicy.ClassifyTarget("%WINDIR%\\System32\\drivers\\etc\\hosts.txt"));
    }

    [Theory]
    [InlineData("%ILT_NOPE%", true)]
    [InlineData("C:\\a\\%ILT_NOPE%\\b.exe", true)]
    [InlineData("C:\\tools\\100%.exe", false)]      // a lone % is a legal Windows filename character
    [InlineData("C:\\tools\\50%%.exe", false)]      // "%%" names no variable; expansion leaves it alone
    [InlineData("C:\\tools\\app.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsUnresolvedVariable_RequiresAPairOfPercentSignsAroundAName(string? path, bool expected)
    {
        // Windows treats an unmatched '%' as a literal, so matching that rule is what keeps this
        // from clearing a file the user can genuinely launch.
        Assert.Equal(expected, TargetFilePolicy.ContainsUnresolvedVariable(path));
    }

    [Fact]
    public void ClassifyTarget_ForAFileNameContainingALonePercentSign_IsStillSupported()
    {
        // The regression guard for the fix above: over-eager detection would classify a real,
        // launchable file as unparseable and quietly stop launching it.
        Assert.Equal(TargetPathStatus.Supported, TargetFilePolicy.ClassifyTarget("C:\\tools\\100%.exe"));
    }
}
