using System;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// The launch-readiness decision, finally under test.
/// </summary>
/// <remarks>
/// Until v2.7 this logic lived inside a method on a WinForms <c>ApplicationContext</c> whose
/// constructor builds a real <c>NotifyIcon</c>, installs real global input hooks and starts a real
/// timer, so none of it was reachable from a test. Every correctness fix v2.5.0 made here was
/// protected by a comment and nothing else. These are the assertions those comments asked for.
/// </remarks>
public sealed class LaunchDecisionTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---- v2.5.0 regression: the cooldown clock-warp clamp -------------------------------------

    [Fact]
    public void Evaluate_WhenTheStoredLaunchTimestampIsInTheFuture_RebaselinesInsteadOfPinningTheCooldown()
    {
        // An NTP correction, a VM snapshot restore, a dead CMOS battery, or a hand-edited
        // LastLaunchUtc missing its 'Z'. Before v2.5.0 the cooldown stayed active for the full
        // magnitude of the jump -- and because the timestamp is persisted to config.json, it
        // SURVIVED RESTART with nothing in the tray to explain why the app had stopped launching.
        var result = LaunchDecision.Evaluate(
            lastLaunchUtc: Now.AddYears(5),
            nowUtc: Now);

        Assert.Equal(Now, result.RebaselinedLastLaunchUtc);
        Assert.True(result.ClockWarpSeconds > 0);

        // Re-baselined to now, so the cooldown is merely active, not pinned for five years.
        Assert.False(result.Evaluation.CooldownOk);
        Assert.InRange(result.Evaluation.CooldownRemainingSeconds, 0, 10);
    }

    [Fact]
    public void Evaluate_WithASaneLaunchTimestamp_DoesNotRebaseline()
    {
        var result = LaunchDecision.Evaluate(lastLaunchUtc: Now.AddMinutes(-5), nowUtc: Now);

        Assert.Null(result.RebaselinedLastLaunchUtc);
        Assert.Equal(0, result.ClockWarpSeconds);
        Assert.True(result.Evaluation.CooldownOk);
    }

    [Theory]
    [InlineData(0, false)]   // launched this instant: still inside the cooldown
    [InlineData(9, false)]
    [InlineData(10, true)]   // exactly at the boundary: allowed
    [InlineData(600, true)]
    public void Evaluate_AtTheCooldownBoundary_AllowsTheLaunchOnlyOnceItHasElapsed(int secondsAgo, bool expectedOk)
    {
        var result = LaunchDecision.Evaluate(
            lastLaunchUtc: Now.AddSeconds(-secondsAgo),
            nowUtc: Now,
            minLaunchCooldownSeconds: 10);

        Assert.Equal(expectedOk, result.Evaluation.CooldownOk);
    }

    // ---- v2.5.0 regression: IdleMeasured ------------------------------------------------------

    [Theory]
    [InlineData(false, false, false)]  // no target
    [InlineData(true, false, false)]   // unsupported
    [InlineData(true, true, false)]    // missing
    [InlineData(true, true, true)]     // fully evaluated
    public void Evaluate_OnEveryPathIncludingTheEarlyReturns_ReportsThatIdleWasMeasured(
        bool hasTarget, bool supported, bool exists)
    {
        // The early-return paths leave InputIdleOk at its initialiser `false`, which is
        // indistinguishable from a measured "the user is active" -- and the re-arm logic treated it
        // as exactly that, re-arming with no measurement and then logging that fresh user activity
        // had been observed. IdleMeasured is what makes those two cases distinguishable, so it has
        // to be true on every path, not just the one that reaches the bottom.
        var result = LaunchDecision.Evaluate(
            hasTarget: hasTarget, targetSupported: supported, targetExists: exists);

        Assert.True(result.Evaluation.IdleMeasured);
    }

    // ---- Ready requires every gate, not just the reason code ----------------------------------

    [Fact]
    public void Evaluate_WhenEverythingPasses_IsReady()
    {
        var result = LaunchDecision.Evaluate();

        Assert.True(result.Evaluation.Ready);
        Assert.Equal(LaunchReasonCode.Named("Ready"), result.Evaluation.ReasonCode);
    }

    [Fact]
    public void Evaluate_WhenLockedAndNotAllowed_IsNotReadyAndSaysWhy()
    {
        var result = LaunchDecision.Evaluate(workstationLocked: true, allowLaunchWhileLocked: false);

        // Ready is computed from the booleans and ignores ReasonCode entirely. A gate that adds a
        // reason code without adding its boolean to Ready LABELS the blocked state and launches
        // anyway, which is the exact bug shape the session gate was written to avoid.
        Assert.False(result.Evaluation.Ready);
        Assert.False(result.Evaluation.SessionOk);
        Assert.Equal(LaunchReasonCode.Named("WorkstationLocked"), result.Evaluation.ReasonCode);
    }

    [Fact]
    public void Evaluate_WhenLockedButTheUserOptedIn_IsReady()
    {
        var result = LaunchDecision.Evaluate(workstationLocked: true, allowLaunchWhileLocked: true);

        Assert.True(result.Evaluation.Ready);
        Assert.True(result.Evaluation.SessionOk);
        Assert.True(result.Evaluation.WorkstationLocked);
    }

    // ---- The reason cascade, in order ---------------------------------------------------------

    [Fact]
    public void Evaluate_WithNoTarget_ReportsNoTargetAndSkipsTheLaterChecks()
    {
        var result = LaunchDecision.Evaluate(hasTarget: false, targetSupported: false, targetExists: false);

        Assert.Equal(LaunchReasonCode.Named("NoTargetConfigured"), result.Evaluation.ReasonCode);
        Assert.False(result.Evaluation.Ready);
    }

    [Fact]
    public void Evaluate_WithAnUnsupportedTarget_ReportsUnsupported()
    {
        var result = LaunchDecision.Evaluate(targetSupported: false, targetExists: false);

        Assert.Equal(LaunchReasonCode.Named("SelectedTargetUnsupported"), result.Evaluation.ReasonCode);
    }

    [Fact]
    public void Evaluate_WithAMissingTarget_ReportsMissing()
    {
        var result = LaunchDecision.Evaluate(targetExists: false);

        Assert.Equal(LaunchReasonCode.Named("SelectedTargetMissing"), result.Evaluation.ReasonCode);
    }

    [Fact]
    public void Evaluate_WhenTheTargetIsMissingAndTheMachineIsLocked_ReportsTheMissingTarget()
    {
        // A setup fault must not be masked by a transient runtime condition: the user would wait
        // out a lock that was never the problem while the real fault stayed invisible for as long
        // as the machine stayed locked.
        var result = LaunchDecision.Evaluate(targetExists: false, workstationLocked: true);

        Assert.Equal(LaunchReasonCode.Named("SelectedTargetMissing"), result.Evaluation.ReasonCode);
    }

    [Fact]
    public void Evaluate_WhenLockedAndTheCpuIsBusy_ReportsTheLockAsTheGoverningBlocker()
    {
        var result = LaunchDecision.Evaluate(
            workstationLocked: true, cpuPercent: 99, cpuThresholdPercent: 10);

        Assert.Equal(LaunchReasonCode.Named("WorkstationLocked"), result.Evaluation.ReasonCode);
    }

    [Fact]
    public void Evaluate_WhileStillIdling_ReportsWaitingForInputIdle()
    {
        var result = LaunchDecision.Evaluate(idleSeconds: 10, requiredIdleSeconds: 300);

        Assert.Equal(LaunchReasonCode.Named("WaitingForInputIdle"), result.Evaluation.ReasonCode);
        Assert.False(result.Evaluation.InputIdleOk);
    }

    [Fact]
    public void Evaluate_WithAnUnusableCpuReading_ReportsItUnavailableRatherThanAboveThreshold()
    {
        var result = LaunchDecision.Evaluate(cpuSampleValid: false, cpuPercent: 99);

        Assert.Equal(LaunchReasonCode.Named("CpuSampleUnavailable"), result.Evaluation.ReasonCode);

        // An unusable reading carries 0%, never the junk value that came back with it.
        Assert.Equal(0, result.Evaluation.CpuPercent);
        Assert.False(result.Evaluation.CpuOk);
    }

    [Fact]
    public void Evaluate_WithTheCpuAboveTheThreshold_ReportsItBusy()
    {
        var result = LaunchDecision.Evaluate(cpuPercent: 37, cpuThresholdPercent: 10);

        Assert.Equal(LaunchReasonCode.Named("CpuAboveThreshold"), result.Evaluation.ReasonCode);
    }

    [Theory]
    [InlineData(10, 10, true)]   // exactly at the threshold is allowed
    [InlineData(11, 10, false)]
    public void Evaluate_AtTheCpuThresholdBoundary_AllowsEqualButNotAbove(
        double cpu, int threshold, bool expectedOk)
    {
        var result = LaunchDecision.Evaluate(cpuPercent: cpu, cpuThresholdPercent: threshold);

        Assert.Equal(expectedOk, result.Evaluation.CpuOk);
    }

    // ---- Purity -------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_IsPure_ProducesTheSameAnswerForTheSameArguments()
    {
        // Kills any mutation that sneaks a DateTime.UtcNow, a config read or a static back into
        // the decision -- which is the one property that makes every test above meaningful.
        var first = LaunchDecision.Evaluate(lastLaunchUtc: Now.AddSeconds(-3), nowUtc: Now);

        for (var i = 0; i < 25; i++)
        {
            System.Threading.Thread.Sleep(1);
            var again = LaunchDecision.Evaluate(lastLaunchUtc: Now.AddSeconds(-3), nowUtc: Now);

            Assert.Equal(first.Evaluation.ReasonCode, again.Evaluation.ReasonCode);
            Assert.Equal(first.Evaluation.CooldownRemainingSeconds, again.Evaluation.CooldownRemainingSeconds);
            Assert.Equal(first.Evaluation.Ready, again.Evaluation.Ready);
        }
    }

    // ---- The stuck-CPU-sampler counter --------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(int.MaxValue)]
    public void NextConsecutiveInvalidCpuSamples_WhenTheSampleIsValid_ResetsToZero(int current)
    {
        Assert.Equal(0, TickCpuSampling.NextConsecutiveInvalidCpuSamples(current, sampleValid: true, maxSamples: 12));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 6)]
    [InlineData(11, 12)]
    [InlineData(12, 12)]   // saturates at the cap
    public void NextConsecutiveInvalidCpuSamples_WhenTheSampleIsInvalid_AdvancesUpToTheCap(int current, int expected)
    {
        Assert.Equal(expected, TickCpuSampling.NextConsecutiveInvalidCpuSamples(current, sampleValid: false, maxSamples: 12));
    }

    [Fact]
    public void NextConsecutiveInvalidCpuSamples_AtIntMaxValue_DoesNotOverflowIntoANegative()
    {
        // The comment on this counter has always claimed it cannot overflow into a negative that
        // would silently clear the fault. Nothing asserted it until the function became pure.
        var next = TickCpuSampling.NextConsecutiveInvalidCpuSamples(int.MaxValue, sampleValid: false, maxSamples: 12);

        Assert.Equal(12, next);
        Assert.True(next > 0);
    }

    [Fact]
    public void MaxConsecutiveInvalidCpuSamples_AtTheTickCadence_CoversAboutOneMinute()
    {
        // Pins the relationship between two constants in the same file that a future edit to
        // either could silently break.
        Assert.Equal(60, TickCpuSampling.MaxConsecutiveInvalidCpuSamples * TickCpuSampling.CheckIntervalSeconds);
    }

    [Fact]
    public void EvaluateLaunchReadiness_TakesThisTicksCpuReadingAsParameters()
    {
        // The regression test for the fix itself. Reverting to a parameterless
        // EvaluateLaunchReadiness -- the only way to put the sample back below the running-target
        // early return, and so the only way to restore the frozen-delta bug -- fails here.
        var parameters = Product.MethodNamed("TrayAppContext", "EvaluateLaunchReadiness").GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(bool), parameters[0].ParameterType);
        Assert.Equal(typeof(double), parameters[1].ParameterType);
    }
}
