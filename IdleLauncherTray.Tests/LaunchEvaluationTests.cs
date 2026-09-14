using System;
using System.Collections.Generic;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// <c>TrayAppContext.LaunchEvaluation</c> is the record that decides whether the app starts a
/// program. <c>Ready</c> is the whole decision and it is computed from booleans alone — it never
/// looks at <c>ReasonCode</c> — so a gate that is only labelled is a gate that is not enforced.
/// </summary>
public sealed class LaunchEvaluationTests
{
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
    public void Ready_ForTheFullyReadyArrangement_IsTrue()
    {
        // The control for every test below. Without it, "locked blocks the launch" would pass
        // against an arrangement that was never ready in the first place.
        Assert.True(LaunchEvaluationProxy.FullyReady().Ready);
    }

    [Fact]
    public void Ready_WhenTheWorkstationIsLockedAndLaunchingWhileLockedIsNotAllowed_IsFalse()
    {
        // This is the assertion the whole session-lock change exists for. Labelling the state
        // WorkstationLocked without putting SessionOk into Ready would produce an evaluation that
        // says "locked" in the log and launches into the lock screen anyway.
        var evaluation = LaunchEvaluationProxy.FullyReady();
        evaluation.WorkstationLocked = true;
        evaluation.SessionOk = false;
        evaluation.ReasonCode = LaunchReasonCode.Named("WorkstationLocked");

        Assert.False(evaluation.Ready);
    }

    [Fact]
    public void Ready_WhenTheWorkstationIsLockedAndLaunchingWhileLockedIsAllowed_IsTrue()
    {
        // The opt-in has to actually opt in, otherwise AllowLaunchWhileLocked is a checkbox that
        // does nothing and the two fields might as well be one.
        var evaluation = LaunchEvaluationProxy.FullyReady();
        evaluation.WorkstationLocked = true;
        evaluation.SessionOk = true;

        Assert.True(evaluation.Ready);
    }

    [Fact]
    public void Ready_IgnoresTheReasonCode()
    {
        // Pins the property that makes the test above necessary: setting a blocking reason code
        // and nothing else does NOT stop a launch.
        var evaluation = LaunchEvaluationProxy.FullyReady();
        evaluation.ReasonCode = LaunchReasonCode.Named("WorkstationLocked");

        Assert.True(evaluation.Ready);
    }

    [Fact]
    public void SessionOk_DefaultsToTrue()
    {
        // A default-constructed evaluation must not be session-blocked: every early-return path
        // in EvaluateLaunchReadiness relies on the initialiser, and a default of false would turn
        // "no target configured" into "session blocked" in the state key.
        Assert.True(new LaunchEvaluationProxy().SessionOk);
        Assert.False(new LaunchEvaluationProxy().WorkstationLocked);
    }

    [Fact]
    public void StateKey_ForADefaultEvaluation_IsTheExactExpectedString()
    {
        // Pinned character for character. ReasonCode became an enum, which silently rebinds
        // string.Join from its params string?[] overload to params object?[]. The output is
        // supposed to be identical -- this is the assertion that says it is, rather than leaving
        // the next reader to reason about overload resolution.
        Assert.Equal(
            "armed|Unknown|no-target|target-unsupported|target-missing|unlocked|session-ok|cooldown-ok|idle-wait|cpu-invalid|cpu-blocked",
            new LaunchEvaluationProxy().StateKey(armed: true));
    }

    [Fact]
    public void StateKey_ForAFullyReadyEvaluation_IsTheExactExpectedString()
    {
        Assert.Equal(
            "disarmed|Ready|target|target-supported|target-exists|unlocked|session-ok|cooldown-ok|idle-ok|cpu-valid|cpu-ok",
            LaunchEvaluationProxy.FullyReady().StateKey(armed: false));
    }

    [Fact]
    public void StateKey_DistinguishesLockedFromUnlocked()
    {
        // The state key is what suppresses duplicate log lines. If locking did not change it, the
        // transition into a locked desktop would be invisible in the log for as long as nothing
        // else changed -- which, while locked, is the normal case.
        var unlocked = LaunchEvaluationProxy.FullyReady();
        var locked = LaunchEvaluationProxy.FullyReady();
        locked.WorkstationLocked = true;

        Assert.NotEqual(unlocked.StateKey(armed: true), locked.StateKey(armed: true));
        Assert.Contains("|unlocked|", unlocked.StateKey(armed: true), StringComparison.Ordinal);
        Assert.Contains("|locked|", locked.StateKey(armed: true), StringComparison.Ordinal);
    }

    [Fact]
    public void StateKey_DistinguishesSessionOkFromSessionBlocked()
    {
        var allowed = LaunchEvaluationProxy.FullyReady();
        var blocked = LaunchEvaluationProxy.FullyReady();
        blocked.SessionOk = false;

        Assert.NotEqual(allowed.StateKey(armed: true), blocked.StateKey(armed: true));
        Assert.Contains("|session-ok|", allowed.StateKey(armed: true), StringComparison.Ordinal);
        Assert.Contains("|session-blocked|", blocked.StateKey(armed: true), StringComparison.Ordinal);
    }

    [Fact]
    public void StateKey_WithLockedAndAllowed_DiffersFromLockedAndBlocked()
    {
        // Both facts are carried because they are genuinely different: locked-but-permitted and
        // locked-and-blocked are two different reasons for the same screen state, and collapsing
        // them would hide the effect of the AllowLaunchWhileLocked setting from the log.
        var allowed = LaunchEvaluationProxy.FullyReady();
        allowed.WorkstationLocked = true;
        allowed.SessionOk = true;

        var blocked = LaunchEvaluationProxy.FullyReady();
        blocked.WorkstationLocked = true;
        blocked.SessionOk = false;

        Assert.NotEqual(allowed.StateKey(armed: true), blocked.StateKey(armed: true));
    }

    [Fact]
    public void StateKey_ForEveryReasonCode_IsDistinct()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in LaunchReasonCode.Names)
        {
            var evaluation = LaunchEvaluationProxy.FullyReady();
            evaluation.ReasonCode = LaunchReasonCode.Named(name);

            Assert.True(keys.Add(evaluation.StateKey(armed: true)), $"Reason code {name} produced a duplicate state key.");
        }

        Assert.Equal(LaunchReasonCode.Names.Count, keys.Count);
    }

    [Theory]
    [MemberData(nameof(EveryReasonCode))]
    public void StateKey_RendersTheReasonCodeByName(string reasonCodeName)
    {
        // The enum member names were chosen to be byte-identical to the strings they replaced, so
        // the log format did not change. This is what keeps that true.
        var evaluation = LaunchEvaluationProxy.FullyReady();
        evaluation.ReasonCode = LaunchReasonCode.Named(reasonCodeName);

        Assert.Contains("|" + reasonCodeName + "|", evaluation.StateKey(armed: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ReportsTheSessionState()
    {
        var evaluation = LaunchEvaluationProxy.FullyReady();
        evaluation.WorkstationLocked = true;
        evaluation.SessionOk = false;
        evaluation.ReasonCode = LaunchReasonCode.Named("WorkstationLocked");

        var described = evaluation.Describe(armed: true);

        Assert.Contains("workstationLocked=True", described, StringComparison.Ordinal);
        Assert.Contains("sessionOk=False", described, StringComparison.Ordinal);
        Assert.Contains("reason=WorkstationLocked", described, StringComparison.Ordinal);

        // A blocked session is not ready, and the human-readable line has to agree with Ready or
        // the log and the behaviour tell different stories.
        Assert.Contains("state=not-ready", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ForAReadyEvaluation_SaysReady()
    {
        Assert.Contains(
            "state=ready-to-launch",
            LaunchEvaluationProxy.FullyReady().Describe(armed: true),
            StringComparison.Ordinal);
    }
}
