using System;

namespace IdleLauncherTray;

/// <summary>
/// Everything the launcher needs to know for one tick, sampled by the caller.
/// </summary>
/// <remarks>
/// The three target facts are computed by the caller with short-circuit evaluation
/// (<c>supported = hasTarget &amp;&amp; ...</c>, <c>exists = supported &amp;&amp; ...</c>) so that a
/// missing target never triggers a filesystem probe. Passing <c>false</c> for a check that was
/// never reached is safe because the cascade below asks about them in that same order: once
/// <see cref="HasTarget"/> is false the other two cannot change the answer.
/// </remarks>
internal readonly record struct LaunchInputs
{
    public string TargetPath { get; init; }
    public bool HasTarget { get; init; }
    public bool TargetSupported { get; init; }
    public bool TargetExists { get; init; }

    public int IdleSeconds { get; init; }
    public int RequiredIdleSeconds { get; init; }

    public bool CpuSampleValid { get; init; }
    public double CpuPercent { get; init; }
    public int CpuThresholdPercent { get; init; }

    public bool WorkstationLocked { get; init; }
    public bool AllowLaunchWhileLocked { get; init; }

    public DateTime? LastLaunchUtc { get; init; }
    public DateTime NowUtc { get; init; }
    public int MinLaunchCooldownSeconds { get; init; }
}

/// <summary>
/// The result of a decision: the evaluation itself, plus anything the caller must act on.
/// </summary>
/// <remarks>
/// <see cref="RebaselinedLastLaunchUtc"/> is how the clock-warp repair escapes a pure function.
/// The old code wrote <c>_lastLaunchUtc</c> in the middle of the evaluation and logged from there;
/// a pure function can do neither, so it reports the corrected value and the size of the jump and
/// lets the caller persist and log. That is not merely a purity concession -- the repaired value is
/// written to config.json and survives restart, so making it an explicit output rather than a
/// hidden side effect is the honest shape.
/// </remarks>
internal readonly record struct LaunchDecisionResult
{
    public LaunchEvaluation Evaluation { get; init; }

    /// <summary>Non-null when the stored launch timestamp was in the future and was corrected.</summary>
    public DateTime? RebaselinedLastLaunchUtc { get; init; }

    /// <summary>How far into the future the stored timestamp was, in seconds. Zero unless rebaselined.</summary>
    public double ClockWarpSeconds { get; init; }
}

/// <summary>
/// The launch-readiness decision, as a pure function.
/// </summary>
/// <remarks>
/// No statics, no clock, no filesystem, no config, no logging. Every input is a parameter and the
/// only output is the returned record, which is what finally makes the v2.5.0 correctness fixes
/// testable: the cooldown clock-warp clamp, the <c>IdleMeasured</c> gate and the reason-code
/// cascade were all previously reachable only by constructing a WinForms tray application.
/// </remarks>
internal static class LaunchDecision
{
    // Taken by value, not by `in`. The struct is small, this runs once every five seconds, and a
    // by-value parameter is reachable through the reflection facade the test suite has to use --
    // which is the entire reason this function exists.
    internal static LaunchDecisionResult Evaluate(LaunchInputs inputs)
    {
        var evaluation = new LaunchEvaluation
        {
            TargetPath = inputs.TargetPath ?? string.Empty,
            HasTarget = inputs.HasTarget,

            // No Math.Max clamps here. ConfigManager.NormalizeInPlace runs on BOTH Load and Save
            // and is the single enforcement point for these bounds; the menu handlers only ever
            // assign values from fixed sets. Clamping again could not change any reachable value
            // and would disguise where the invariant is actually kept.
            RequiredIdleSeconds = inputs.RequiredIdleSeconds,
            CpuThresholdPercent = inputs.CpuThresholdPercent,

            // Idle and CPU are sampled by the caller BEFORE any of the early returns below, so
            // these are always real measurements. IdleMeasured records that fact for the re-arm
            // logic, which must never infer "the user is active" from InputIdleOk alone.
            IdleSeconds = inputs.IdleSeconds,
            IdleMeasured = true,
            CpuSampleValid = inputs.CpuSampleValid,
            CpuPercent = inputs.CpuSampleValid ? inputs.CpuPercent : 0
        };

        evaluation.InputIdleOk = evaluation.IdleSeconds >= evaluation.RequiredIdleSeconds;
        evaluation.CpuOk = evaluation.CpuSampleValid && evaluation.CpuPercent <= evaluation.CpuThresholdPercent;

        evaluation.WorkstationLocked = inputs.WorkstationLocked;
        evaluation.SessionOk = !evaluation.WorkstationLocked || inputs.AllowLaunchWhileLocked;

        if (!evaluation.HasTarget)
        {
            evaluation.ReasonCode = LaunchReasonCode.NoTargetConfigured;
            return new LaunchDecisionResult { Evaluation = evaluation };
        }

        evaluation.TargetSupported = inputs.TargetSupported;
        if (!evaluation.TargetSupported)
        {
            evaluation.ReasonCode = LaunchReasonCode.SelectedTargetUnsupported;
            return new LaunchDecisionResult { Evaluation = evaluation };
        }

        evaluation.TargetExists = inputs.TargetExists;
        if (!evaluation.TargetExists)
        {
            evaluation.ReasonCode = LaunchReasonCode.SelectedTargetMissing;
            return new LaunchDecisionResult { Evaluation = evaluation };
        }

        DateTime? rebaselined = null;
        var clockWarpSeconds = 0d;

        if (inputs.LastLaunchUtc.HasValue)
        {
            var delta = (inputs.NowUtc - inputs.LastLaunchUtc.Value).TotalSeconds;

            // A NEGATIVE delta means the wall clock moved backwards relative to the stored
            // timestamp: an NTP correction, a VM snapshot restore, a dead CMOS battery, or a
            // hand-edited LastLaunchUtc missing its 'Z' (which ToUniversalTime() then shifts
            // forward by the local offset). Without this clamp the cooldown stayed active for the
            // full magnitude of the jump -- hours or years -- and because the timestamp is
            // persisted to config.json it SURVIVED RESTART, with nothing in the tray to show why
            // the app had stopped launching. Re-baseline and carry on.
            if (delta < 0)
            {
                clockWarpSeconds = -delta;
                rebaselined = inputs.NowUtc;
                delta = 0;
            }

            if (delta < inputs.MinLaunchCooldownSeconds)
            {
                evaluation.CooldownOk = false;
                evaluation.CooldownRemainingSeconds = inputs.MinLaunchCooldownSeconds - delta;
            }
        }

        // The lock label goes HERE -- first in this cascade -- and in neither of the two places it
        // might look like it belongs.
        //
        // Not before the target checks above: that would report "Locked" for a target that does not
        // exist, and the user would wait out a lock state that was never the problem while the real
        // fault stayed invisible for as long as the machine stayed locked.
        //
        // Not before the cooldown block either: that block self-heals a launch timestamp that has
        // moved into the future, and that value is PERSISTED. Skipping the repair while locked
        // would let a clock jump strand the cooldown across restarts.
        if (!evaluation.SessionOk)
        {
            evaluation.ReasonCode = LaunchReasonCode.WorkstationLocked;
        }
        else if (!evaluation.CooldownOk)
        {
            evaluation.ReasonCode = LaunchReasonCode.LaunchCooldownActive;
        }
        else if (!evaluation.InputIdleOk)
        {
            evaluation.ReasonCode = LaunchReasonCode.WaitingForInputIdle;
        }
        else if (!evaluation.CpuSampleValid)
        {
            evaluation.ReasonCode = LaunchReasonCode.CpuSampleUnavailable;
        }
        else if (!evaluation.CpuOk)
        {
            evaluation.ReasonCode = LaunchReasonCode.CpuAboveThreshold;
        }
        else
        {
            evaluation.ReasonCode = LaunchReasonCode.Ready;
        }

        return new LaunchDecisionResult
        {
            Evaluation = evaluation,
            RebaselinedLastLaunchUtc = rebaselined,
            ClockWarpSeconds = clockWarpSeconds
        };
    }
}
