namespace IdleLauncherTray.Tests;

using System;
using IdleLauncherTray.Tests.Sut;

// PhysicalIdle is the one product type that is public, so unlike every other facade in
// Sut/ its name is already visible from this namespace -- and a type in the enclosing
// IdleLauncherTray namespace outranks an alias declared at file scope, which is why these
// directives sit INSIDE the namespace declaration instead of above it. Without the alias
// every call below binds to the product directly, sees only its public surface, and skips
// the reflection the rest of this suite deliberately goes through.
using PhysicalIdle = IdleLauncherTray.Tests.Sut.PhysicalIdle;

/// <summary>
/// Covers the detector for a hook Windows dropped without telling us: the callback stops
/// running, the <c>HHOOK</c> stays non-null, and every "is the hook installed" check in the
/// product keeps answering yes.
/// <para>
/// Installing a real low-level hook here is out of the question -- it is global, it needs a
/// message pump, and its callback blocks all system input until it returns. It does not
/// need to be installed: the rule is a pure function of two clock readings and a tick
/// count, so the interesting cases (including the ones that would take hours of real time)
/// are all reachable as arguments.
/// </para>
/// </summary>
public sealed class HookLivenessTests
{
    // An arbitrary monotonic clock reading. Nothing below depends on its value -- only on
    // the differences -- but a large one keeps the arranged "hours ago" timestamps positive.
    private const long NowMs = 1_000_000_000L;

    // What the tray puts in front of a reason before handing it to NotifyIcon.Text, which
    // WinForms refuses above 63 characters. The prefix is 29 of those.
    private const string TrayPrefix = "IdleLauncherTray: DEGRADED - ";
    private const int TrayTooltipMaxLength = 63;

    // The tray ticks every 5 s (TrayAppContext.CheckIntervalSeconds), and the tick is the
    // only thing that advances the detector.
    private const int TrayTickMs = 5000;

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(int.MaxValue, true)]
    public void IsHookDropSuspected_WhenInputArrivesAndNoCallbackFires_IsSuspectedOnlyOnceTheTicksAreMet(
        int consecutiveSuspectTicks,
        bool expected)
    {
        // Windows saw input a moment ago; our callback has not run for two minutes. That
        // pair is only possible if the input stopped reaching us.
        Assert.Equal(
            expected,
            Suspect(systemIdleMs: 0, callbackAgeMs: 120_000, consecutiveSuspectTicks));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    [InlineData(5_000)]
    [InlineData(3_600_000)]
    public void IsHookDropSuspected_WhenTheCallbackKeepsFiringWithTheInput_IsNotSuspected(long agreedIdleMs)
    {
        // The healthy shape: both clocks last moved at the same instant, because the same
        // input moved them. The tick count is deliberately far past the threshold -- a
        // counter alone must never be able to declare a drop.
        Assert.False(Suspect(agreedIdleMs, callbackAgeMs: agreedIdleMs, consecutiveSuspectTicks: 99));
    }

    [Fact]
    public void IsHookDropSuspected_WhenOnlyInjectedInputArrives_IsNotSuspected()
    {
        // An anti-idle tool hammering ScrollLock through SendInput keeps GetLastInputInfo
        // fresh and produces nothing but INJECTED events. The heartbeat is written for those
        // too, precisely so this case reads as healthy: the hook is doing its job, and its
        // job is to recognise that input and refuse to count it as a user. A heartbeat gated
        // on physical input would report a dropped hook here -- on the one tool this file
        // exists to defeat.
        Assert.False(Suspect(systemIdleMs: 500, callbackAgeMs: 500, consecutiveSuspectTicks: 99));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(60_000)]
    [InlineData(3_600_000)]
    [InlineData(28_800_000)]
    public void IsHookDropSuspected_OnACompletelyIdleMachine_IsNotSuspectedAtAnyTickCount(long idleMs)
    {
        // THE case. A user who walked away hours ago moves neither clock, so the two stay in
        // step and the gap stays at zero however long the absence lasts. Reporting a drop
        // here would make the app distrust a perfectly good hook at exactly the moment it is
        // deciding whether to launch.
        Assert.False(Suspect(idleMs, callbackAgeMs: idleMs, consecutiveSuspectTicks: 0));
        Assert.False(Suspect(idleMs, callbackAgeMs: idleMs, consecutiveSuspectTicks: 3));
        Assert.False(Suspect(idleMs, callbackAgeMs: idleMs, consecutiveSuspectTicks: int.MaxValue));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void IsHookDropSuspected_WhenTheSystemIdleReadingIsUnavailable_IsNotSuspected(double systemIdleMs)
    {
        // GetSystemIdleMilliseconds answers PositiveInfinity when GetLastInputInfo fails.
        // Losing the cross-check is not evidence that the hooks failed -- it is evidence of
        // nothing, and the callback age on its own is exactly what a genuinely idle machine
        // looks like.
        Assert.False(Suspect(systemIdleMs, callbackAgeMs: 3_600_000, consecutiveSuspectTicks: int.MaxValue));
    }

    [Fact]
    public void IsHookDropSuspected_WhenTheGapIsInsideTheGrace_IsNotSuspected()
    {
        var graceMs = PhysicalIdle.HookSilenceGraceMs;

        // One millisecond short of the margin is not evidence; the margin itself is. The
        // margin covers the secure desktop, whose keystrokes update this session's
        // GetLastInputInfo but never reach a hook on the default desktop.
        Assert.False(Suspect(systemIdleMs: 1_000, callbackAgeMs: 1_000 + graceMs - 1, consecutiveSuspectTicks: 99));
        Assert.True(Suspect(systemIdleMs: 1_000, callbackAgeMs: 1_000 + graceMs, consecutiveSuspectTicks: 99));
    }

    [Fact]
    public void IsHookDropSuspected_WhenTheHeartbeatIsNewerThanTheClockSample_IsNotSuspected()
    {
        // A callback that landed between the caller's two readings, or a heartbeat just
        // reset by NotifyExternalActivity. A negative age is proof of life, not a huge gap.
        Assert.False(Suspect(systemIdleMs: 0, callbackAgeMs: -5, consecutiveSuspectTicks: 99));
        Assert.False(Suspect(systemIdleMs: 0, callbackAgeMs: 0, consecutiveSuspectTicks: 99));
    }

    [Fact]
    public void IsHookDropSuspected_AfterTheUserStopsTypingFollowingADrop_StaysSuspected()
    {
        // The gap freezes rather than decaying: the callback has been silent for an hour and
        // the system saw input fifty minutes ago, so ten minutes of input went missing and
        // that stays true while the user is away. If the suspicion expired with the input,
        // the app would go back to trusting the dead hook at the moment the machine looks
        // idle -- which is the moment it launches.
        Assert.True(Suspect(systemIdleMs: 3_000_000, callbackAgeMs: 3_600_000, consecutiveSuspectTicks: 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void IsHookDropSuspected_WithZeroOrNegativeRequiredTicks_StillDemandsOneTickOfEvidence(int requiredTicks)
    {
        // The counter is only ever advanced by a tick that produced evidence, so "no ticks
        // required" has to mean one tick, never a suspicion with nothing behind it.
        Assert.False(Suspect(systemIdleMs: 0, callbackAgeMs: 120_000, consecutiveSuspectTicks: 0, requiredTicks));
        Assert.True(Suspect(systemIdleMs: 0, callbackAgeMs: 120_000, consecutiveSuspectTicks: 1, requiredTicks));
    }

    [Fact]
    public void HookSilenceConstants_AreScaledToTheTrayTick()
    {
        // Not a value check -- a shape check. The grace has to outlast more than one tick so
        // a single unlock cannot supply it, and more than one tick has to be required so a
        // one-off disagreement cannot flip the state.
        Assert.True(
            PhysicalIdle.HookSilenceGraceMs > TrayTickMs,
            $"The silence grace ({PhysicalIdle.HookSilenceGraceMs} ms) must outlast a single {TrayTickMs} ms tray tick.");
        Assert.True(
            PhysicalIdle.HookSilenceTicksRequired > 1,
            "A single tick of disagreement must not be enough to declare a dropped hook.");
    }

    [Fact]
    public void KeyboardHookCallback_WithANegativeCode_StillMovesTheHeartbeat()
    {
        // nCode < 0 means "pass this through without inspecting it". The heartbeat is
        // written above that guard on purpose: the callback running at all is the proof the
        // hook is still wired up, whatever it was called about.
        using var hooks = new HookStateScope(keyboardInstalled: false, mouseInstalled: false);
        PhysicalIdle.LastHookCallbackMilliseconds = PhysicalIdle.MonotonicMilliseconds - 3_600_000;

        var beforeMs = PhysicalIdle.MonotonicMilliseconds;
        PhysicalIdle.InvokeKeyboardHookCallbackWithNegativeCode();

        Assert.True(
            PhysicalIdle.LastHookCallbackMilliseconds >= beforeMs,
            "A keyboard callback must move the heartbeat even when it is told to pass the event straight through.");
    }

    [Fact]
    public void MouseHookCallback_WithANegativeCode_StillMovesTheHeartbeat()
    {
        using var hooks = new HookStateScope(keyboardInstalled: false, mouseInstalled: false);
        PhysicalIdle.LastHookCallbackMilliseconds = PhysicalIdle.MonotonicMilliseconds - 3_600_000;

        var beforeMs = PhysicalIdle.MonotonicMilliseconds;
        PhysicalIdle.InvokeMouseHookCallbackWithNegativeCode();

        Assert.True(
            PhysicalIdle.LastHookCallbackMilliseconds >= beforeMs,
            "A mouse callback must move the heartbeat even when it is told to pass the event straight through.");
    }

    [Fact]
    public void GetHookDegradationReason_WhenBothHooksAreInstalledAndFiring_ReturnsNull()
    {
        using var hooks = new HookStateScope(keyboardInstalled: true, mouseInstalled: true, dropSuspected: false);

        Assert.Null(PhysicalIdle.GetHookDegradationReason());
    }

    [Fact]
    public void GetHookDegradationReason_WhenTheKeyboardHookIsMissing_NamesTheKeyboardHook()
    {
        using var hooks = new HookStateScope(keyboardInstalled: false, mouseInstalled: true);

        Assert.Equal(PhysicalIdle.ReasonKeyboardHookMissing, PhysicalIdle.GetHookDegradationReason());
    }

    [Fact]
    public void GetHookDegradationReason_WhenTheMouseHookIsMissing_NamesTheMouseHook()
    {
        using var hooks = new HookStateScope(keyboardInstalled: true, mouseInstalled: false);

        Assert.Equal(PhysicalIdle.ReasonMouseHookMissing, PhysicalIdle.GetHookDegradationReason());
    }

    [Fact]
    public void GetHookDegradationReason_WhenNeitherHookIsInstalled_NamesBothHooks()
    {
        using var hooks = new HookStateScope(keyboardInstalled: false, mouseInstalled: false);

        Assert.Equal(PhysicalIdle.ReasonBothHooksMissing, PhysicalIdle.GetHookDegradationReason());
    }

    [Fact]
    public void GetHookDegradationReason_WhenADropIsSuspected_ReportsThatTheHooksStoppedFiring()
    {
        // Both handles are non-null, which is exactly what makes a silent drop invisible to
        // every other check in the product.
        using var hooks = new HookStateScope(keyboardInstalled: true, mouseInstalled: true, dropSuspected: true);

        Assert.Equal(PhysicalIdle.ReasonHooksStoppedFiring, PhysicalIdle.GetHookDegradationReason());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void GetHookDegradationReason_WhenAHookIsMissingAndADropIsSuspected_ReportsTheMissingHook(
        bool keyboardInstalled,
        bool mouseInstalled)
    {
        // A missing hook is a fact; the drop is inferred from the same silence it causes.
        // Reporting the inference over the fact would send the user looking for the wrong
        // problem.
        using var hooks = new HookStateScope(keyboardInstalled, mouseInstalled, dropSuspected: true);

        Assert.NotEqual(PhysicalIdle.ReasonHooksStoppedFiring, PhysicalIdle.GetHookDegradationReason());
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public void GetHookDegradationReason_ForEveryHookState_FitsTheTrayTooltip(
        bool keyboardInstalled,
        bool mouseInstalled,
        bool dropSuspected)
    {
        // Every combination, not just the ones a reader expects to be reachable: the budget
        // is only enforceable if nothing can return a string nobody measured. NotifyIcon.Text
        // throws above 63 characters, so an over-long reason would take the tray icon out at
        // the moment it had something to say.
        using var hooks = new HookStateScope(keyboardInstalled, mouseInstalled, dropSuspected);

        var reason = PhysicalIdle.GetHookDegradationReason();

        if (reason == null)
        {
            Assert.True(keyboardInstalled && mouseInstalled && !dropSuspected, "Only a healthy state may report no reason.");
            return;
        }

        Assert.True(reason.Length < 30, $"Reason '{reason}' is {reason.Length} characters; the budget is under 30.");
        Assert.True(
            (TrayPrefix + reason).Length <= TrayTooltipMaxLength,
            $"'{TrayPrefix}{reason}' is {(TrayPrefix + reason).Length} characters; NotifyIcon.Text allows {TrayTooltipMaxLength}.");
    }

    [Fact]
    public void GetIdleMilliseconds_WhenADropIsSuspected_StopsTrustingTheHookReading()
    {
        // GetLastInputInfo is a P/Invoke with no seam, so this reads the machine's real
        // answer and arranges the hook's answer relative to it -- ten minutes older, which
        // no rounding or elapsed-time slop between the two calls can close.
        var systemIdleMs = PhysicalIdle.GetSystemIdleMilliseconds();
        Assert.False(double.IsInfinity(systemIdleMs), "GetLastInputInfo failed; there is nothing to cross-check against.");

        using var hooks = new HookStateScope(keyboardInstalled: true, mouseInstalled: true, dropSuspected: true);
        PhysicalIdle.UseSystemIdleFailSafe = true;
        PhysicalIdle.LastPhysicalInputMilliseconds =
            PhysicalIdle.MonotonicMilliseconds - (long)systemIdleMs - 600_000;

        var idleMs = PhysicalIdle.GetIdleMilliseconds();

        // Both hook handles are non-null here, which is exactly what makes a silent drop
        // invisible: without the suspicion feeding into that branch, this returns the hook's
        // ten-minutes-older reading. The gap is only decisive while the machine has been
        // quiet for longer than the fail-safe window -- always true on CI, and true on a
        // developer box unless a key or the mouse moves inside the same
        // GetEffectiveSystemIdleFailSafeWindowMs as the call.
        Assert.True(
            idleMs < systemIdleMs + 300_000,
            $"A suspected drop must fall back to the {systemIdleMs} ms system reading, not the hook's {idleMs} ms one "
            + $"(fail-safe window {PhysicalIdle.GetEffectiveSystemIdleFailSafeWindowMs()} ms).");
    }

    [Fact]
    public void NotifyExternalActivity_WhenADropWasSuspected_ClearsTheSuspicionAndTheCounter()
    {
        using var hooks = new HookStateScope(keyboardInstalled: true, mouseInstalled: true, dropSuspected: true);
        PhysicalIdle.ConsecutiveHookSilenceTicks = 99;

        PhysicalIdle.NotifyExternalActivity("session unlocked");

        Assert.False(PhysicalIdle.DropSuspected);
        Assert.Equal(0, PhysicalIdle.ConsecutiveHookSilenceTicks);
        Assert.Null(PhysicalIdle.GetHookDegradationReason());
    }

    [Fact]
    public void NotifyExternalActivity_AfterALongLock_LeavesNoEvidenceForTheNextTick()
    {
        // The password was typed on the secure desktop, so no callback ran for as long as the
        // machine was locked while GetLastInputInfo saw input seconds ago. Resetting the
        // counters alone would only postpone that gap by a few ticks -- the heartbeat is what
        // carries it, so the heartbeat has to move too.
        using var hooks = new HookStateScope(keyboardInstalled: true, mouseInstalled: true, dropSuspected: true);
        PhysicalIdle.LastHookCallbackMilliseconds = PhysicalIdle.MonotonicMilliseconds - 3_600_000;
        PhysicalIdle.ConsecutiveHookSilenceTicks = 99;

        PhysicalIdle.NotifyExternalActivity("session unlocked");

        Assert.False(
            PhysicalIdle.IsHookDropSuspected(
                systemIdleMs: 0,
                PhysicalIdle.LastHookCallbackMilliseconds,
                PhysicalIdle.MonotonicMilliseconds,
                consecutiveSuspectTicks: 99,
                PhysicalIdle.HookSilenceTicksRequired),
            "An unlock must leave the detector with no gap to count, not merely with a zeroed counter.");
    }

    [Fact]
    public void NotifyExternalActivity_WhenTheIdleClockIsAlreadyNewer_DoesNotMoveItBackwards()
    {
        // A hook callback or the gamepad poll can publish a newer timestamp while this runs.
        // A plain Interlocked.Exchange would overwrite it with our older sample and hand the
        // user back idle time they never accrued.
        using var hooks = new HookStateScope();
        var newerMs = PhysicalIdle.MonotonicMilliseconds + 60_000;
        PhysicalIdle.LastPhysicalInputMilliseconds = newerMs;

        PhysicalIdle.NotifyExternalActivity("session unlocked");

        Assert.Equal(newerMs, PhysicalIdle.LastPhysicalInputMilliseconds);
    }

    [Fact]
    public void NotifyExternalActivity_WhenTheIdleClockIsStale_AdvancesItToNow()
    {
        using var hooks = new HookStateScope();
        PhysicalIdle.LastPhysicalInputMilliseconds = PhysicalIdle.MonotonicMilliseconds - 3_600_000;

        var beforeMs = PhysicalIdle.MonotonicMilliseconds;
        PhysicalIdle.NotifyExternalActivity("session unlocked");

        Assert.True(
            PhysicalIdle.LastPhysicalInputMilliseconds >= beforeMs,
            "Input we could not see is still input: the idle clock has to start again from the report.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NotifyExternalActivity_WithABlankReason_StillResetsTheDetector(string? reason)
    {
        // The reason is only ever logged, so a caller that forgets one must not cost the
        // reset -- and must not throw on the tray's UI thread either.
        using var hooks = new HookStateScope(keyboardInstalled: true, mouseInstalled: true, dropSuspected: true);

        PhysicalIdle.NotifyExternalActivity(reason);

        Assert.False(PhysicalIdle.DropSuspected);
    }

    // Arranges the two clock readings the way a tick would see them: the system last saw
    // input `systemIdleMs` ago, and Windows last called a hook callback `callbackAgeMs` ago.
    private static bool Suspect(
        double systemIdleMs,
        long callbackAgeMs,
        int consecutiveSuspectTicks,
        int requiredTicks = 3) =>
        PhysicalIdle.IsHookDropSuspected(
            systemIdleMs,
            NowMs - callbackAgeMs,
            NowMs,
            consecutiveSuspectTicks,
            requiredTicks);
}
