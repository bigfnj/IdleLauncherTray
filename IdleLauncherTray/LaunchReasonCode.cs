namespace IdleLauncherTray;

/// <summary>
/// Why the launcher is (or is not) willing to start the target right now.
/// <para>
/// These were free-form strings until the tray tooltip started rendering them. A tooltip has a
/// hard length budget, and "every rendered status fits" is only provable if the set of inputs is
/// closed: with an enum the proof enumerates <see cref="System.Enum.GetValues(System.Type)"/> and
/// cannot drift, whereas a hand-maintained list of string literals silently stops covering the
/// newest one. The member names are byte-identical to the strings they replaced, so the log lines
/// produced by <c>StateKey</c> and <c>Describe</c> are unchanged.
/// </para>
/// </summary>
internal enum LaunchReasonCode
{
    Unknown = 0,
    NoTargetConfigured,
    SelectedTargetUnsupported,
    SelectedTargetMissing,
    WorkstationLocked,
    LaunchCooldownActive,
    WaitingForInputIdle,
    CpuSampleUnavailable,
    CpuAboveThreshold,
    Ready
}
