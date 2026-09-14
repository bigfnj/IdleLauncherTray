using System;
using System.Collections.Generic;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// The tray tooltip is the only place this app reports its own state without the user opening a
/// log file, and <c>NotifyIcon.Text</c> throws above a shell-version-dependent limit. So the
/// length guarantee is not cosmetic: exceeding it turns the status update into an exception on
/// the WinForms pump.
/// <para>
/// <c>TrayStatusText</c> is pure by construction, which is what lets every one of these run with
/// no tray icon, no hooks and no timer.
/// </para>
/// </summary>
public sealed class TrayStatusTextTests
{
    // The tooltip now ends with the configured CPU threshold and the running version, and
    // carries no app name at all. Every expectation below is "<status>" + Suffix.
    private const string Suffix = " - CPU 10% - v2.7.0";

    // For the tests that deliberately drive the threshold away from the default.
    private static string SuffixFor(int cpuThresholdPercent) =>
        $" - CPU {cpuThresholdPercent}% - v2.7.0";

    // Long enough that no branch can render it whole, and built from a character that is trivial
    // to spot in a failure message.
    private const int OversizedNameLength = 300;

    /// <summary>Every reason code, paired with both armed states.</summary>
    public static TheoryData<string, bool> EveryReasonCodeAndArmedState()
    {
        var data = new TheoryData<string, bool>();

        foreach (var name in LaunchReasonCode.Names)
        {
            data.Add(name, true);
            data.Add(name, false);
        }

        return data;
    }

    public static TheoryData<string> EveryReasonCode()
    {
        var data = new TheoryData<string>();

        foreach (var name in LaunchReasonCode.Names)
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void MaxLength_IsSixtyThree()
    {
        // Pinned because the number is a Win32 fact, not a preference: NotifyIcon.Text is copied
        // into a fixed buffer whose size depends on the shell version the app is talking to.
        Assert.Equal(63, TrayStatusText.MaxLength);
    }

    [Fact]
    public void EveryReasonCode_IsCoveredByTheEnum()
    {
        // Guards the guard. If the product's reason codes ever revert to strings, or a member is
        // added without a rendering, the exhaustive theories below would quietly shrink instead
        // of failing -- an empty theory is a green theory.
        Assert.True(LaunchReasonCode.Names.Count >= 10, "The reason-code enum lost members.");
        Assert.Contains("Unknown", LaunchReasonCode.Names);
        Assert.Contains("Ready", LaunchReasonCode.Names);
        Assert.Contains("WorkstationLocked", LaunchReasonCode.Names);
    }

    [Theory]
    [MemberData(nameof(EveryReasonCodeAndArmedState))]
    public void ForEvaluation_WithEveryExtremeInput_StaysWithinMaxLength(string reasonCodeName, bool armed)
    {
        // int.MaxValue durations are reachable: IdleMinutes is only clamped to >= 1, so a
        // hand-edited config multiplies into the hundreds of millions of seconds.
        var text = TrayStatusText.ForEvaluation(
            degradationReason: null,
            LaunchReasonCode.Named(reasonCodeName),
            armed,
            new string('W', OversizedNameLength),
            idleSeconds: int.MaxValue,
            requiredIdleSeconds: int.MaxValue,
            cpuPercent: 100d,
            cpuThresholdPercent: 100);

        Assert.True(
            text.Length <= TrayStatusText.MaxLength,
            $"'{text}' is {text.Length} characters for reason {reasonCodeName} (armed={armed}).");

        // The suffix survives even when the body is truncated: it is appended whole, because it is
        // the section nobody would notice quietly disappearing.
        Assert.EndsWith(SuffixFor(100), text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryReasonCodeAndArmedState))]
    public void ForEvaluation_WithNonsenseNumbers_StaysWithinMaxLength(string reasonCodeName, bool armed)
    {
        var text = TrayStatusText.ForEvaluation(
            degradationReason: null,
            LaunchReasonCode.Named(reasonCodeName),
            armed,
            targetFileName: null,
            idleSeconds: int.MinValue,
            requiredIdleSeconds: -1,
            cpuPercent: double.NaN,
            cpuThresholdPercent: int.MaxValue);

        Assert.True(text.Length <= TrayStatusText.MaxLength, $"'{text}' is {text.Length} characters.");
        Assert.DoesNotContain("NaN", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryReasonCodeAndArmedState))]
    public void ForEvaluation_WithAnOversizedVersion_StaysWithinMaxLength(string reasonCodeName, bool armed)
    {
        // The version is data too. It comes from an assembly attribute, which a SourceLink-style
        // build can stuff with a commit hash, so without its own budget the suffix would eat the
        // whole line and leave the status -- the entire point of the tooltip -- nowhere to go.
        var text = TrayStatusText.ForEvaluation(
            degradationReason: null,
            LaunchReasonCode.Named(reasonCodeName),
            armed,
            new string('W', OversizedNameLength),
            idleSeconds: int.MaxValue,
            requiredIdleSeconds: int.MaxValue,
            cpuPercent: 100d,
            cpuThresholdPercent: 100,
            versionDisplay: new string('9', OversizedNameLength));

        Assert.True(text.Length <= TrayStatusText.MaxLength, $"'{text}' is {text.Length} characters.");
    }

    [Theory]
    [MemberData(nameof(EveryReasonCode))]
    public void ForEvaluation_WithADegradationReason_ReportsTheDegradationRegardlessOfTheReasonCode(string reasonCodeName)
    {
        // Degraded outranks everything, including Ready and including a setup fault: a fault the
        // user cannot otherwise see is worth more tooltip than a fault that is already obvious
        // from the app not doing anything.
        var text = TrayStatusText.ForEvaluation(
            "lock detection off",
            LaunchReasonCode.Named(reasonCodeName),
            armed: true,
            "app.exe",
            idleSeconds: 200,
            requiredIdleSeconds: 300,
            cpuPercent: 37d,
            cpuThresholdPercent: 10);

        Assert.Equal("DEGRADED - lock detection off" + Suffix, text);
    }

    [Fact]
    public void ForEvaluation_WithA500CharacterDegradationReason_TruncatesToExactlyMaxLength()
    {
        var text = TrayStatusText.ForEvaluation(
            new string('x', 500),
            LaunchReasonCode.Named("Ready"),
            armed: true,
            "app.exe",
            idleSeconds: 200,
            requiredIdleSeconds: 300,
            cpuPercent: 1d,
            cpuThresholdPercent: 10);

        // Exactly MaxLength, not merely under it: the budget is supposed to SPEND the whole line
        // on a reason this long, and a short answer would mean a branch is over-reserving.
        Assert.Equal(TrayStatusText.MaxLength, text.Length);

        // And the words that say what happened survive the truncation -- truncating the label
        // away would leave the user with 63 characters of a message they cannot classify.
        Assert.StartsWith("DEGRADED - ", text, StringComparison.Ordinal);

        // The truncation lands on the BODY, immediately before the suffix: the CPU threshold and
        // the version are appended whole and are never what gets spent. An implementation that
        // clamped the combined string instead would eat the version off the end, which is the one
        // section nobody would notice going missing.
        Assert.EndsWith("…" + Suffix, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unknown", true, 200, 300, 37d, 10, "Status unknown" + Suffix)]
    [InlineData("NoTargetConfigured", true, 200, 300, 37d, 10, "No target selected" + Suffix)]
    [InlineData("SelectedTargetUnsupported", true, 200, 300, 37d, 10, "Target type not supported" + Suffix)]
    [InlineData("SelectedTargetMissing", true, 200, 300, 37d, 10, "Target missing: app.exe" + Suffix)]
    [InlineData("WorkstationLocked", true, 200, 300, 37d, 10, "Locked - will not launch" + Suffix)]
    [InlineData("LaunchCooldownActive", true, 200, 300, 37d, 10, "Cooldown active" + Suffix)]
    [InlineData("WaitingForInputIdle", true, 200, 300, 37d, 10, "Idle 3:20/5:00" + Suffix)]
    [InlineData("CpuSampleUnavailable", true, 200, 300, 37d, 10, "CPU reading unavailable" + Suffix)]
    [InlineData("CpuAboveThreshold", true, 200, 300, 37d, 10, "CPU busy 37%" + Suffix)]
    [InlineData("Ready", true, 200, 300, 37d, 10, "Ready to launch" + Suffix)]
    public void ForEvaluation_RendersTheDocumentedLineForEachReasonCode(
        string reasonCodeName,
        bool armed,
        int idleSeconds,
        int requiredIdleSeconds,
        double cpuPercent,
        int cpuThresholdPercent,
        string expected)
    {
        var text = TrayStatusText.ForEvaluation(
            degradationReason: null,
            LaunchReasonCode.Named(reasonCodeName),
            armed,
            "app.exe",
            idleSeconds,
            requiredIdleSeconds,
            cpuPercent,
            cpuThresholdPercent);

        Assert.Equal(expected, text);
        Assert.True(text.Length <= TrayStatusText.MaxLength);
    }

    [Fact]
    public void ForEvaluation_ForEveryReasonCode_ProducesADistinctBody()
    {
        // Two codes rendering the same words makes the tooltip a worse diagnostic than the log it
        // is meant to replace: the user reads a status that cannot tell them which gate is shut.
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in LaunchReasonCode.Names)
        {
            var body = BodyOf(TrayStatusText.ForEvaluation(
                degradationReason: null,
                LaunchReasonCode.Named(name),
                armed: true,
                "app.exe",
                idleSeconds: 200,
                requiredIdleSeconds: 300,
                cpuPercent: 37d,
                cpuThresholdPercent: 10));

            Assert.DoesNotContain(body, bodies.Values);
            bodies.Add(name, body);
        }

        Assert.Equal(LaunchReasonCode.Names.Count, bodies.Count);
    }

    [Fact]
    public void ForEvaluation_ForEveryReasonCodeExceptUnknown_DoesNotFallThroughToStatusUnknown()
    {
        // "Status unknown" is the default arm. A new member that nobody renders lands there
        // silently and looks like a working tooltip, so the fall-through is asserted directly
        // rather than being left to the distinctness test to imply.
        foreach (var name in LaunchReasonCode.Names)
        {
            var body = BodyOf(TrayStatusText.ForEvaluation(
                degradationReason: null,
                LaunchReasonCode.Named(name),
                armed: true,
                "app.exe",
                idleSeconds: 200,
                requiredIdleSeconds: 300,
                cpuPercent: 37d,
                cpuThresholdPercent: 10));

            if (string.Equals(name, "Unknown", StringComparison.Ordinal))
            {
                Assert.Equal("Status unknown", body);
            }
            else
            {
                Assert.NotEqual("Status unknown", body);
            }
        }
    }

    [Fact]
    public void ForEvaluation_WhenDisarmedAndOtherwiseReady_ReportsDisarmedRatherThanReady()
    {
        // The bug this fixes: a disarmed launcher whose conditions all pass reported "Ready",
        // which is a promise the app was guaranteed not to keep.
        var text = TrayStatusText.ForEvaluation(
            degradationReason: null,
            LaunchReasonCode.Named("Ready"),
            armed: false,
            "app.exe",
            idleSeconds: 300,
            requiredIdleSeconds: 300,
            cpuPercent: 1d,
            cpuThresholdPercent: 10);

        Assert.Equal("Disarmed until you use the PC" + Suffix, text);
    }

    [Theory]
    [InlineData("NoTargetConfigured", "No target selected")]
    [InlineData("SelectedTargetUnsupported", "Target type not supported")]
    [InlineData("SelectedTargetMissing", "Target missing: app.exe")]
    public void ForEvaluation_WhenDisarmedWithASetupFault_ReportsTheSetupFault(string reasonCodeName, string expectedBody)
    {
        // Setup faults outrank "disarmed" because disarmed clears itself the moment the user
        // touches the mouse. Masking a broken target behind it sends the user off to wait for a
        // state change that fixes nothing.
        var text = TrayStatusText.ForEvaluation(
            degradationReason: null,
            LaunchReasonCode.Named(reasonCodeName),
            armed: false,
            "app.exe",
            idleSeconds: 300,
            requiredIdleSeconds: 300,
            cpuPercent: 1d,
            cpuThresholdPercent: 10);

        Assert.Equal(expectedBody + Suffix, text);
    }

    [Fact]
    public void ForEvaluation_WithAMissingTargetAndNoFileName_FallsBackToAGenericBody()
    {
        var text = TrayStatusText.ForEvaluation(
            degradationReason: null,
            LaunchReasonCode.Named("SelectedTargetMissing"),
            armed: true,
            targetFileName: "   ",
            idleSeconds: 0,
            requiredIdleSeconds: 300,
            cpuPercent: 0d,
            cpuThresholdPercent: 10);

        Assert.Equal("Target file is missing" + Suffix, text);
    }

    [Fact]
    public void ForEvaluation_WithAnEmptyVersion_StillShowsAVersionSection()
    {
        // A tooltip that silently drops the version on the one build where the lookup failed is a
        // tooltip you cannot trust to say what is running, which is the only reason it carries one.
        var text = TrayStatusText.ForEvaluation(
            degradationReason: null,
            LaunchReasonCode.Named("Ready"),
            armed: true,
            "app.exe",
            idleSeconds: 300,
            requiredIdleSeconds: 300,
            cpuPercent: 1d,
            cpuThresholdPercent: 10,
            versionDisplay: "   ");

        Assert.Equal("Ready to launch - CPU 10% - v?", text);
    }

    [Fact]
    public void ForRunningTarget_NamesTheRunningTarget()
    {
        Assert.Equal(
            "Running app.exe" + Suffix,
            TrayStatusText.ForRunningTarget(degradationReason: null, "app.exe"));
    }

    [Fact]
    public void ForRunningTarget_WithNoFileName_FallsBackToAGenericBody()
    {
        Assert.Equal(
            "Target is running" + Suffix,
            TrayStatusText.ForRunningTarget(degradationReason: null, targetFileName: null));
    }

    [Fact]
    public void ForRunningTarget_WithADegradationReason_ReportsTheDegradation()
    {
        Assert.Equal(
            "DEGRADED - CPU sampling stuck" + Suffix,
            TrayStatusText.ForRunningTarget("CPU sampling stuck", "app.exe"));
    }

    [Fact]
    public void ForRunningTarget_WithAnOversizedFileName_StaysWithinMaxLength()
    {
        var text = TrayStatusText.ForRunningTarget(null, new string('W', OversizedNameLength));

        Assert.True(text.Length <= TrayStatusText.MaxLength, $"'{text}' is {text.Length} characters.");
        Assert.StartsWith("Running ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ForTickFailure_ReportsTheFailingMonitorLoop()
    {
        // The monitor tick is the only thing that launches anything. When it throws, the app
        // simply stops working and says nothing -- this is the line that says it.
        var text = TrayStatusText.ForTickFailure();

        Assert.Equal("DEGRADED - monitor tick failed" + Suffix, text);
        Assert.True(text.Length <= TrayStatusText.MaxLength);
    }

    [Fact]
    public void Clamp_WhenTheCutWouldSplitASurrogatePair_DropsTheWholePair()
    {
        // U+1F600, two UTF-16 units. Cutting between them leaves an unpaired high surrogate: a
        // replacement box on screen, and a string whose length no longer means what the caller
        // measured.
        const string emoji = "😀";
        var value = "ab" + emoji + "cd";

        // maxLength 4 => keep 3 units + the ellipsis, and unit 3 is the high surrogate.
        var clamped = TrayStatusText.Clamp(value, 4);

        Assert.Equal("ab…", clamped);
        Assert.DoesNotContain(clamped, c => char.IsSurrogate(c));
    }

    [Fact]
    public void Clamp_WhenThePairFitsWhole_KeepsIt()
    {
        // The counterpart of the test above. Without it, a Clamp that dropped the pair
        // unconditionally would still be green.
        const string emoji = "😀";

        Assert.Equal("ab" + emoji + "…", TrayStatusText.Clamp("ab" + emoji + "cd", 5));
    }

    [Theory]
    [InlineData("", 10, "")]
    [InlineData("short", 10, "short")]
    [InlineData("exactly-10", 10, "exactly-10")]
    [InlineData("abcdefghijk", 10, "abcdefghi…")]
    [InlineData("abc", 1, "…")]
    [InlineData("abc", 0, "")]
    [InlineData("abc", -5, "")]
    public void Clamp_NeverExceedsTheRequestedLength(string value, int maxLength, string expected)
    {
        var clamped = TrayStatusText.Clamp(value, maxLength);

        Assert.Equal(expected, clamped);
        Assert.True(clamped.Length <= Math.Max(0, maxLength));
    }

    [Theory]
    [InlineData(int.MinValue, "0:00")]
    [InlineData(-1, "0:00")]
    [InlineData(0, "0:00")]
    [InlineData(1, "0:01")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(200, "3:20")]
    [InlineData(300, "5:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1h")]
    [InlineData(86_399, "23h")]
    [InlineData(86_400, "1d")]
    [InlineData(8_639_999, "99d")]
    [InlineData(8_640_000, "99d+")]
    [InlineData(int.MaxValue, "99d+")]
    public void FormatDuration_IsTotalAndAtMostFiveCharacters(int seconds, string expected)
    {
        var formatted = TrayStatusText.FormatDuration(seconds);

        Assert.Equal(expected, formatted);
        Assert.True(formatted.Length <= 5, $"'{formatted}' is {formatted.Length} characters.");
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(0.4d, "0")]
    [InlineData(0.5d, "1")]
    [InlineData(37d, "37")]
    [InlineData(99.6d, "100")]
    [InlineData(100d, "100")]
    [InlineData(1000d, "100")]
    [InlineData(-1d, "0")]
    [InlineData(double.NegativeInfinity, "0")]
    [InlineData(double.PositiveInfinity, "100")]
    public void Percent_IsTotalAndAtMostThreeCharacters(double value, string expected)
    {
        var formatted = TrayStatusText.Percent(value);

        Assert.Equal(expected, formatted);
        Assert.True(formatted.Length <= 3, $"'{formatted}' is {formatted.Length} characters.");
    }

    [Fact]
    public void Percent_WithNaN_DoesNotRenderTheWordNaN()
    {
        // Math.Clamp(double.NaN, 0, 100) returns NaN, so a clamp-then-format implementation
        // produces "CPU NaN% > 10%". The NaN check has to come first, and this is what says so.
        Assert.Equal("0", TrayStatusText.Percent(double.NaN));
    }

    private static string BodyOf(string text)
    {
        Assert.EndsWith(Suffix, text, StringComparison.Ordinal);
        return text[..^Suffix.Length];
    }
}
