using System.Globalization;

namespace IdleLauncherTray;

/// <summary>
/// One tick's answer to "should the launcher fire, and if not, why not".
/// </summary>
/// <remarks>
/// Lifted out of <c>TrayAppContext</c> so that <see cref="LaunchDecision"/> can produce it without
/// a WinForms context in scope. Constructing <c>TrayAppContext</c> builds a real
/// <c>NotifyIcon</c>, installs real input hooks and starts a real timer, so while this record lived
/// inside it the launch decision could not be tested at all -- the v2.5.0 correctness fixes were
/// protected by comments rather than by assertions.
/// <para>
/// Mutable record rather than a positional one: the evaluation is built up in stages and there are
/// seventeen members, so an object initializer followed by field updates reads far better than a
/// seventeen-argument constructor. The record gives auto-equality and <c>ToString()</c> for free;
/// <see cref="Describe"/> stays for human-readable logs.
/// </para>
/// </remarks>
internal sealed record LaunchEvaluation
{
    public string TargetPath { get; set; } = string.Empty;
    public bool HasTarget { get; set; }
    public bool TargetSupported { get; set; }
    public bool TargetExists { get; set; }
    public bool CooldownOk { get; set; } = true;
    public double CooldownRemainingSeconds { get; set; }
    public int IdleSeconds { get; set; }
    public int RequiredIdleSeconds { get; set; }
    public bool InputIdleOk { get; set; }

    // Whether idle was actually SAMPLED this evaluation. The early-return paths (no target,
    // unsupported, missing) leave InputIdleOk at its initialiser `false`, which is
    // indistinguishable from a measured "the user is active" -- and the re-arm logic treated
    // it as exactly that, re-arming with no measurement and then logging that fresh user
    // activity had been observed. Never infer activity from InputIdleOk alone.
    public bool IdleMeasured { get; set; }
    public double CpuPercent { get; set; }
    public bool CpuSampleValid { get; set; }
    public int CpuThresholdPercent { get; set; }
    public bool CpuOk { get; set; }

    // Whether the workstation was locked at the moment this evaluation was taken.
    public bool WorkstationLocked { get; set; }

    // Whether the session state permits launching. Separate from WorkstationLocked because
    // the user can opt into launching anyway (AppConfig.AllowLaunchWhileLocked), so "locked"
    // and "blocked" are genuinely different facts and the log has to be able to say which.
    public bool SessionOk { get; set; } = true;

    public LaunchReasonCode ReasonCode { get; set; } = LaunchReasonCode.Unknown;

    // Ready is computed from the booleans and ignores ReasonCode entirely, which is why
    // SessionOk has to appear HERE and not only in the labelling cascade. Adding the
    // reason code without adding the boolean would have produced an evaluation that says
    // "WorkstationLocked" and launches anyway.
    public bool Ready =>
        HasTarget && TargetSupported && TargetExists && SessionOk && CooldownOk && InputIdleOk && CpuOk;

    public string StateKey(bool armed)
    {
        // NOTE: ReasonCode is an enum, so this binds string.Join(string, params object?[])
        // rather than the params string?[] overload it used to. The rendered output is
        // identical because Enum.ToString() yields the member name, which is byte-identical
        // to the string literal it replaced -- and a test pins the exact string so the next
        // person to touch this does not have to take that on trust.
        return string.Join(
            "|",
            armed ? "armed" : "disarmed",
            ReasonCode,
            HasTarget ? "target" : "no-target",
            TargetSupported ? "target-supported" : "target-unsupported",
            TargetExists ? "target-exists" : "target-missing",
            WorkstationLocked ? "locked" : "unlocked",
            SessionOk ? "session-ok" : "session-blocked",
            CooldownOk ? "cooldown-ok" : "cooldown-wait",
            InputIdleOk ? "idle-ok" : "idle-wait",
            CpuSampleValid ? "cpu-valid" : "cpu-invalid",
            CpuOk ? "cpu-ok" : "cpu-blocked");
    }

    public string Describe(bool armed)
    {
        var state = Ready
            ? (armed ? "ready-to-launch" : "ready-but-waiting-for-rearm")
            : "not-ready";

        var cpuText = CpuSampleValid
            ? CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "unknown";

        return
            $"state={state}; reason={ReasonCode}; target='{TargetPath}'; targetSupported={TargetSupported}; targetExists={TargetExists}; workstationLocked={WorkstationLocked}; sessionOk={SessionOk}; idle={IdleSeconds}s/{RequiredIdleSeconds}s; cpu={cpuText}/{CpuThresholdPercent}%; cooldownOk={CooldownOk}; cooldownRemaining={CooldownRemainingSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s; armed={armed}.";
    }
}
