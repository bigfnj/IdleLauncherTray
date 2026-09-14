using System.Globalization;

namespace IdleLauncherTray;

/// <summary>
/// Builds the one-line text shown in the tray icon's tooltip.
/// <para>
/// Deliberately pure: it reads no static state, touches no <c>NotifyIcon</c>, no
/// <c>PhysicalIdle</c> and no file system, and takes primitives only. That is what makes the
/// length guarantee testable without a message pump — the whole "never longer than
/// <see cref="MaxLength"/>" property is decided by these functions and nothing else.
/// </para>
/// <para>
/// The three entry points are distinctly named rather than overloaded, and must stay that way:
/// the test suite reaches this class through <c>Type.GetMethod(name, flags)</c>, which throws
/// <see cref="System.Reflection.AmbiguousMatchException"/> the moment a name has two signatures.
/// One overload here would break every test that uses the reflection facade, not just this one's.
/// </para>
/// </summary>
internal static class TrayStatusText
{
    /// <summary>
    /// Hard cap on the tooltip text.
    /// <para>
    /// Its own constant on purpose. <c>TrayAppContext.BalloonTipTitleMaxLength</c> happens to be
    /// the same number today, but it constrains a balloon-tip title, which is a different Win32
    /// structure with its own history. Sharing the constant would tie two unrelated limits
    /// together and make a future change to either one silently move the other.
    /// </para>
    /// <para>
    /// <c>NotifyIcon.Text</c> throws above a version-dependent limit (64 on the oldest shells it
    /// still has to talk to, 128 on modern ones). 63 plus the implicit terminator is safe on all
    /// of them.
    /// </para>
    /// </summary>
    internal const int MaxLength = 63;

    private const string Separator = ": ";
    private const string FallbackPrefix = AppPaths.AppName;

    // The prefix is data (the app name), so it gets a budget of its own; without one, a long
    // name could eat the entire line and leave no room for the status that is the point of it.
    private const int MaxPrefixLength = 24;

    private const string DegradedPrefix = "DEGRADED - ";
    private const string RunningPrefix = "Running ";
    private const string MissingTargetPrefix = "Target missing: ";

    private const string TickFailureReason = "monitor tick failed";

    /// <summary>Status for a tick that evaluated launch readiness.</summary>
    internal static string ForEvaluation(
        string appName,
        string? degradationReason,
        LaunchReasonCode reasonCode,
        bool armed,
        string? targetFileName,
        int idleSeconds,
        int requiredIdleSeconds,
        double cpuPercent,
        int cpuThresholdPercent)
    {
        var prefix = NormalizePrefix(appName);
        var budget = BodyBudget(prefix);

        // Precedence, first match wins. Every ordering decision below is load-bearing.
        if (!string.IsNullOrWhiteSpace(degradationReason))
        {
            return Compose(prefix, DegradedBody(degradationReason, budget));
        }

        // Setup faults sit ABOVE the disarmed check. Disarmed clears itself the moment the user
        // touches the machine, so it would mask a broken configuration behind a state that looks
        // temporary — and the user would wait out a condition that is never coming.
        switch (reasonCode)
        {
            case LaunchReasonCode.NoTargetConfigured:
                return Compose(prefix, "No target selected");

            case LaunchReasonCode.SelectedTargetUnsupported:
                return Compose(prefix, "Target type not supported");

            case LaunchReasonCode.SelectedTargetMissing:
                return Compose(prefix, MissingTargetBody(targetFileName, budget));

            default:
                break;
        }

        // Disarmed sits above the remaining codes because those codes are computed without
        // reference to the armed flag: a disarmed launcher whose conditions all pass reports
        // `Ready`, and the tooltip would promise a launch that is guaranteed not to happen.
        if (!armed)
        {
            return Compose(prefix, "Disarmed until you use the PC");
        }

        var body = reasonCode switch
        {
            LaunchReasonCode.WorkstationLocked => "Locked - will not launch",
            LaunchReasonCode.LaunchCooldownActive => "Cooldown active",
            LaunchReasonCode.WaitingForInputIdle =>
                "Idle " + FormatDuration(idleSeconds) + "/" + FormatDuration(requiredIdleSeconds),
            LaunchReasonCode.CpuSampleUnavailable => "CPU reading unavailable",
            LaunchReasonCode.CpuAboveThreshold =>
                "CPU " + Percent(cpuPercent) + "% > " + Percent(cpuThresholdPercent) + "%",
            LaunchReasonCode.Ready => "Ready to launch",
            _ => "Status unknown"
        };

        return Compose(prefix, body);
    }

    /// <summary>Status for a tick that found the previously launched target still running.</summary>
    internal static string ForRunningTarget(string appName, string? degradationReason, string? targetFileName)
    {
        var prefix = NormalizePrefix(appName);
        var budget = BodyBudget(prefix);

        // Degradation outranks "running" for the same reason it outranks everything else: the
        // target running is the expected case, and a fault the user cannot otherwise see is not.
        return string.IsNullOrWhiteSpace(degradationReason)
            ? Compose(prefix, RunningBody(targetFileName, budget))
            : Compose(prefix, DegradedBody(degradationReason, budget));
    }

    /// <summary>Status for a tick that threw. The monitor loop is the only thing keeping the
    /// launcher honest, so a failing tick has to be visible without opening the log.</summary>
    internal static string ForTickFailure(string appName)
    {
        var prefix = NormalizePrefix(appName);
        return Compose(prefix, DegradedBody(TickFailureReason, BodyBudget(prefix)));
    }

    /// <summary>
    /// Truncates <paramref name="value"/> to at most <paramref name="maxLength"/> UTF-16 units,
    /// marking the cut with a single-unit ellipsis (U+2026).
    /// <para>
    /// Surrogate-safe: a cut that would land between the two halves of a surrogate pair drops the
    /// whole pair. Keeping the high half alone produces an unpaired code unit, which renders as a
    /// replacement box and is not the string anyone measured.
    /// </para>
    /// </summary>
    internal static string Clamp(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength == 1)
        {
            // One unit of room and more text than that: the ellipsis is the whole answer.
            return "…";
        }

        var keep = maxLength - 1;
        if (char.IsHighSurrogate(value[keep - 1]))
        {
            // The low half is at index `keep` and is about to be cut, so drop its partner too.
            keep--;
        }

        return string.Concat(value.AsSpan(0, keep), "…");
    }

    /// <summary>
    /// Renders a duration in at most five characters, for every <see cref="int"/>.
    /// <para>
    /// Total by construction because the input is not as bounded as it looks:
    /// <c>IdleMinutes</c> is only clamped to <c>&gt;= 1</c>, so a hand-edited config can ask for
    /// a required-idle value of hundreds of millions of seconds, and the measured idle value is
    /// whatever the hooks report after a long sleep or a clock change.
    /// </para>
    /// </summary>
    private static string FormatDuration(int seconds)
    {
        if (seconds <= 0)
        {
            return "0:00";
        }

        if (seconds < 3600)
        {
            return (seconds / 60).ToString(CultureInfo.InvariantCulture)
                + ":"
                + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        if (seconds < 86_400)
        {
            return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + "h";
        }

        if (seconds < 8_640_000)
        {
            return (seconds / 86_400).ToString(CultureInfo.InvariantCulture) + "d";
        }

        return "99d+";
    }

    /// <summary>Renders a percentage in at most three characters, for every <see cref="double"/>.</summary>
    private static string Percent(double value)
    {
        // NaN is checked FIRST and not folded into the clamp: Math.Clamp(double.NaN, 0, 100)
        // returns NaN, and NaN.ToString("0") is "NaN" — three characters of non-number that would
        // reach the tooltip as "CPU NaN% > 10%".
        if (double.IsNaN(value))
        {
            return "0";
        }

        var clamped = Math.Clamp(value, 0d, 100d);
        return Math.Round(clamped, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }

    private static string DegradedBody(string? reason, int budget) =>
        DegradedPrefix + Clamp(Normalize(reason), budget - DegradedPrefix.Length);

    private static string RunningBody(string? targetFileName, int budget)
    {
        var name = Normalize(targetFileName);
        return name.Length == 0
            ? "Target is running"
            : RunningPrefix + Clamp(name, budget - RunningPrefix.Length);
    }

    private static string MissingTargetBody(string? targetFileName, int budget)
    {
        var name = Normalize(targetFileName);
        return name.Length == 0
            ? "Target file is missing"
            : MissingTargetPrefix + Clamp(name, budget - MissingTargetPrefix.Length);
    }

    private static int BodyBudget(string prefix) => MaxLength - prefix.Length - Separator.Length;

    private static string NormalizePrefix(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return FallbackPrefix;
        }

        return Clamp(appName.Trim(), MaxPrefixLength);
    }

    private static string Compose(string prefix, string body)
    {
        // Two independent guarantees, on purpose. The per-branch budget above is what keeps the
        // INTERESTING text — it spends the truncation on the file name rather than on the words
        // that explain what is wrong. This Clamp is the unconditional backstop: it makes "never
        // longer than MaxLength" true even for a branch that a future edit budgets incorrectly,
        // and it is the one that a broken budget would trip rather than the shell.
        return Clamp(prefix + Separator + body, MaxLength);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
}
