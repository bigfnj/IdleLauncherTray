using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

// These directives used to sit INSIDE the namespace declaration, with a
// `using PhysicalIdle = IdleLauncherTray.Tests.Sut.PhysicalIdle;` alias under them. That was
// a workaround for the product type being the assembly's one `public` type: a bare
// `PhysicalIdle` here resolved out through the enclosing IdleLauncherTray namespace, found
// the product, and bound to it directly -- skipping the reflection facade every other test
// file goes through. The product type is `internal` now, so that name is not accessible from
// this assembly at all and the import below is the only `PhysicalIdle` in scope. The header
// matches every other file in the suite again.

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

    // The evidence shape used by the counter tests below: Windows saw input a moment ago and
    // our callback has not run for two minutes. That pair is only possible if input stopped
    // reaching us, and it clears HookSilenceGraceMs by more than a factor of ten.
    private const double EvidenceSystemIdleMs = 0;
    private const long EvidenceCallbackAgeMs = 120_000;

    // How long a thread that MUST be blocked is given to prove it is not finishing. Long
    // enough that a scheduling hiccup cannot be mistaken for exclusion, short enough that
    // three of these cost well under a second.
    private const int BlockedProofMs = 200;

    // How long a thread is given to finish once nothing is in its way.
    private const int UnblockedJoinMs = 5000;

    // How long a hook callback gets to prove it took no lock at all. Generous by an order of
    // magnitude on purpose: a callback that waited on the liveness lock would not be slow
    // here, it would never return, because the test thread holds that lock for the whole
    // window.
    private const int CallbackJoinMs = 1000;

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

    [Fact]
    public void SetHookSilenceExpected_WhenTurnedOn_DropsEvidenceGatheredBeforeTheLockEventArrived()
    {
        using var hooks = new HookStateScope();
        try
        {
            // SystemEvents delivers the lock notification asynchronously, so ticks between the
            // real lock and the notification arriving can already have banked evidence.
            PhysicalIdle.ConsecutiveHookSilenceTicks = PhysicalIdle.HookSilenceTicksRequired;
            PhysicalIdle.DropSuspected = true;

            PhysicalIdle.SetHookSilenceExpected(true);

            Assert.Equal(0, PhysicalIdle.ConsecutiveHookSilenceTicks);
            Assert.False(PhysicalIdle.DropSuspected);
        }
        finally
        {
            PhysicalIdle.SetHookSilenceExpected(false);
        }
    }

    [Fact]
    public void UpdateHookLivenessState_WhileSilenceIsExpected_NeverAccumulatesEvidence()
    {
        using var hooks = new HookStateScope();
        try
        {
            // The exact shape of a locked workstation: the heartbeat is frozen hours in the past
            // because secure-desktop input never reaches a default-desktop hook, while
            // GetLastInputInfo keeps being refreshed by whoever is standing at the lock screen.
            // Without the suspension this is precisely the state that latches
            // "input hooks stopped firing" on a machine whose hooks are perfectly healthy.
            PhysicalIdle.LastHookCallbackMilliseconds = Environment.TickCount64 - (6L * 60 * 60 * 1000);
            PhysicalIdle.SetHookSilenceExpected(true);

            for (var tick = 0; tick < PhysicalIdle.HookSilenceTicksRequired + 3; tick++)
            {
                PhysicalIdle.UpdateHookLivenessState();
                Assert.Equal(0, PhysicalIdle.ConsecutiveHookSilenceTicks);
                Assert.False(PhysicalIdle.DropSuspected);
            }

            Assert.NotEqual(PhysicalIdle.ReasonHooksStoppedFiring, PhysicalIdle.GetHookDegradationReason());
        }
        finally
        {
            PhysicalIdle.SetHookSilenceExpected(false);
        }
    }

    [Fact]
    public void NextHookSilenceTicks_WhileEvidenceKeepsArriving_AdvancesOneTickAtATimeAndStopsAtTheCap()
    {
        // 0 -> 1 -> 2 -> 3 -> 3. One tick of evidence is worth exactly one tick, so a single
        // pathological reading cannot jump the counter to the threshold, and the counter
        // stops at the threshold because ticks past it carry no information and a counter
        // that only ever grows is one that eventually overflows.
        Assert.Equal(1, NextTicks(currentTicks: 0));
        Assert.Equal(2, NextTicks(currentTicks: 1));
        Assert.Equal(3, NextTicks(currentTicks: 2));
        Assert.Equal(3, NextTicks(currentTicks: 3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(99)]
    public void NextHookSilenceTicks_WhenTheEvidenceStops_ResetsToZeroRatherThanDecaying(int currentTicks)
    {
        // A tick that agrees with GetLastInputInfo is proof the hook fired, so the episode is
        // over -- the counter goes back to zero in one step rather than counting down. Three
        // consecutive ticks means consecutive.
        Assert.Equal(0, NextTicks(systemIdleMs: 500, callbackAgeMs: 500, currentTicks));
    }

    [Theory]
    [InlineData(60_000)]
    [InlineData(3_600_000)]
    [InlineData(28_800_000)]
    public void NextHookSilenceTicks_OnACompletelyIdleMachine_StaysAtZeroHoweverLongTheAbsenceLasts(long idleMs)
    {
        // THE case, in counter form. A user who walked away a minute, an hour or a full
        // working day ago moves neither clock, so the two stay in step and no amount of
        // elapsed time turns into evidence. If this ever regresses, the app distrusts a
        // perfectly good hook at precisely the moment it is deciding whether to launch.
        Assert.Equal(0, NextTicks(idleMs, callbackAgeMs: idleMs, currentTicks: 0));
        Assert.Equal(0, NextTicks(idleMs, callbackAgeMs: idleMs, currentTicks: 2));
        Assert.Equal(0, NextTicks(idleMs, callbackAgeMs: idleMs, currentTicks: int.MaxValue));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void NextHookSilenceTicks_WhenTheSystemIdleReadingIsUnusable_ResetsToZero(double systemIdleMs)
    {
        // GetSystemIdleMilliseconds answers PositiveInfinity when GetLastInputInfo fails.
        // Losing the cross-check is not evidence that the hooks failed -- it is evidence of
        // nothing, and a callback age on its own is exactly what a genuinely idle machine
        // looks like. Banking a tick for it would let a broken GetLastInputInfo latch the
        // detector after three ticks on a perfectly healthy machine.
        Assert.Equal(0, NextTicks(systemIdleMs, callbackAgeMs: 3_600_000, currentTicks: 2));
    }

    [Fact]
    public void NextHookSilenceTicks_AtIntMaxValue_SaturatesDownInsteadOfOverflowingNegative()
    {
        // The reason the cap is a comparison and not Math.Min(currentTicks + 1, maxTicks):
        // the ADDITION is what overflows. At int.MaxValue the increment wraps to int.MinValue
        // and Math.Min then faithfully keeps the negative, leaving the counter permanently
        // below any threshold -- a detector that has silently disarmed itself, which is the
        // one failure shape this whole file exists to avoid.
        int capped = NextTicks(currentTicks: int.MaxValue);

        Assert.Equal(3, capped);
        Assert.True(capped >= 0, $"The counter overflowed to {capped}.");

        // And with no cap to saturate against, it still must not step past the top of the
        // range. maxTicks is a parameter, so nothing stops a future caller passing this.
        Assert.Equal(
            int.MaxValue,
            NextTicks(currentTicks: int.MaxValue, maxTicks: int.MaxValue));
        Assert.Equal(
            int.MaxValue,
            NextTicks(currentTicks: int.MaxValue - 1, maxTicks: int.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NextHookSilenceTicks_WithZeroOrNegativeMaxTicks_StillBanksATickInsteadOfDisarmingTheDetector(int maxTicks)
    {
        // The floor IsHookDropSuspected has always put on requiredTicks, which this method was
        // missing. Both are parameters on an internal method the facade calls directly, so
        // neither value is hypothetical.
        //
        // maxTicks: 0 does not bound the counter, it TURNS THE DETECTOR OFF: every tick of
        // evidence is pinned at zero, no threshold is ever met and nothing is ever reported --
        // silently, which is the failure shape this whole file exists to rule out. A negative
        // cap is worse again, because `currentTicks >= maxTicks` is satisfied immediately and
        // the counter comes back NEGATIVE, permanently below any threshold.
        var banked = NextTicks(currentTicks: 0, maxTicks);

        Assert.Equal(1, banked);
        Assert.True(banked >= 0, $"A cap of {maxTicks} produced a counter of {banked}.");

        // Saturating DOWN, not up: a floored cap is still a cap.
        Assert.Equal(1, NextTicks(currentTicks: 1, maxTicks));
        Assert.Equal(1, NextTicks(currentTicks: 99, maxTicks));

        // And the point of all of it: a caller passing the same degenerate value to both halves
        // of the tick still gets a working detector, because one tick of evidence is worth one
        // tick at both ends.
        Assert.True(
            Suspect(EvidenceSystemIdleMs, EvidenceCallbackAgeMs, banked, requiredTicks: maxTicks),
            $"With maxTicks={maxTicks} the detector never reports anything, whatever the evidence.");
    }

    [Fact]
    public void UpdateHookLivenessState_WhileTheLivenessLockIsHeld_WaitsForIt()
    {
        // The tick is the read-modify-write half of the race: it reads the flag, both clocks
        // and the counter, decides, and writes the counter and the latch back. All of that
        // has to be one critical section, or a reset landing in the middle of it is undone by
        // a verdict that was reached before the reset existed.
        using var hooks = new HookStateScope();

        AssertWaitsForTheLivenessLock("The liveness tick", PhysicalIdle.UpdateHookLivenessState);
    }

    [Fact]
    public void NotifyExternalActivity_WhileTheLivenessLockIsHeld_WaitsForIt()
    {
        // The unlock resetter. It runs on the SystemEvents thread, and the heartbeat advance
        // plus the two counter resets are one act: split apart, a tick can read the old
        // heartbeat, watch all three writes go by, and then put the whole lock/unlock gap
        // back as fresh evidence.
        using var hooks = new HookStateScope();

        AssertWaitsForTheLivenessLock(
            "NotifyExternalActivity",
            () => PhysicalIdle.NotifyExternalActivity("hook-liveness exclusion test"));
    }

    [Fact]
    public void SetHookSilenceExpected_WhenTurnedOnWhileTheLivenessLockIsHeld_WaitsForIt()
    {
        // The lock resetter, and the one the tick is blind to: it never touches the
        // heartbeat, so nothing about the clocks reveals that it happened. Only the counter
        // resets are inside the lock here -- the _hookSilenceExpected write itself is
        // deliberately outside it, because it carries an ordering contract with the tray's
        // own locked flag. So the worker below has already published the flag by the time it
        // blocks, which is why the flag is put back in the finally.
        using var hooks = new HookStateScope();
        try
        {
            AssertWaitsForTheLivenessLock(
                "SetHookSilenceExpected(true)",
                () => PhysicalIdle.SetHookSilenceExpected(true));
        }
        finally
        {
            PhysicalIdle.SetHookSilenceExpected(false);
        }
    }

    [Fact]
    public void KeyboardHookCallback_WhileTheLivenessLockIsHeld_StillReturnsImmediately()
    {
        // THE requirement, and the one that outranks every other line in this file. A
        // low-level hook callback runs on the UI thread and blocks ALL system input until it
        // returns. If it ever waited on the liveness lock, a tick that held that lock would
        // freeze every keystroke and every mouse movement on the desktop until it finished --
        // and Windows would then silently drop the hook for overrunning LowLevelHooksTimeout,
        // turning a measurement fix into the exact fault the detector was built to report.
        //
        // It has to be invoked on ANOTHER thread. Monitor is re-entrant, so calling it from
        // the thread that already owns the lock would sail straight through even if the
        // callback did take it, and this test would pass while proving nothing.
        using var hooks = new HookStateScope(keyboardInstalled: false, mouseInstalled: false);
        PhysicalIdle.LastHookCallbackMilliseconds = PhysicalIdle.MonotonicMilliseconds - 3_600_000;
        var beforeMs = PhysicalIdle.MonotonicMilliseconds;

        AssertRunsWithoutTheLivenessLock(
            "The keyboard hook callback",
            PhysicalIdle.InvokeKeyboardHookCallbackWithNegativeCode);

        // Witness: the callback did its real work while the lock was held, rather than
        // returning early down some path that never touches the heartbeat at all.
        Assert.True(
            PhysicalIdle.LastHookCallbackMilliseconds >= beforeMs,
            "The callback returned without moving the heartbeat, so it never reached the code under test.");
    }

    [Fact]
    public void MouseHookCallback_WhileTheLivenessLockIsHeld_StillReturnsImmediately()
    {
        // Same requirement, same reasoning: either callback firing is proof the chain is
        // alive, so both write the heartbeat and both have to stay off every lock.
        using var hooks = new HookStateScope(keyboardInstalled: false, mouseInstalled: false);
        PhysicalIdle.LastHookCallbackMilliseconds = PhysicalIdle.MonotonicMilliseconds - 3_600_000;
        var beforeMs = PhysicalIdle.MonotonicMilliseconds;

        AssertRunsWithoutTheLivenessLock(
            "The mouse hook callback",
            PhysicalIdle.InvokeMouseHookCallbackWithNegativeCode);

        Assert.True(
            PhysicalIdle.LastHookCallbackMilliseconds >= beforeMs,
            "The callback returned without moving the heartbeat, so it never reached the code under test.");
    }

    [Fact]
    public void UpdateHookLivenessState_RacedAgainstSetHookSilenceExpected_LeavesNoStaleEvidenceBehindAReset()
    {
        // Corroboration, and worth being honest about which: a stress test can only go red on
        // a run where the bug is present AND the scheduler happens to land inside the window.
        // Green here is consistent with the lock working and equally consistent with the race
        // never occurring, so it proves nothing on its own. The exclusion tests above are the
        // proof; this is the test that would have caught the old code in the act.
        //
        // SetHookSilenceExpected is the resetter rather than NotifyExternalActivity because it
        // writes no log line: the same loop through NotifyExternalActivity would be 50,000
        // synchronous file appends instead of 50,000 field writes.
        const int Iterations = 50_000;

        using var hooks = new HookStateScope();
        int running = 1;
        Exception? tickFailure = null;

        try
        {
            // Arrange a gap that counts as evidence whatever this machine's real idle time
            // is. Both ages come off the same tick counter, so anchoring the heartbeat to the
            // CURRENT GetLastInputInfo reading fixes the gap at ten minutes on a desktop
            // someone is using and on a CI box that has been idle for hours alike.
            var systemIdleMs = PhysicalIdle.GetSystemIdleMilliseconds();
            Assert.False(double.IsInfinity(systemIdleMs), "GetLastInputInfo failed; there is no gap to arrange.");
            PhysicalIdle.LastHookCallbackMilliseconds =
                PhysicalIdle.MonotonicMilliseconds - (long)systemIdleMs - 600_000;
            PhysicalIdle.ConsecutiveHookSilenceTicks = 0;

            // The arrangement has to be non-degenerate or the race below races nothing at
            // all: prove a tick really does bank evidence from it before starting.
            PhysicalIdle.SetHookSilenceExpected(false);
            PhysicalIdle.UpdateHookLivenessState();
            Assert.Equal(1, PhysicalIdle.ConsecutiveHookSilenceTicks);

            var ticker = new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref running) != 0)
                    {
                        PhysicalIdle.UpdateHookLivenessState();
                    }
                }
                catch (Exception ex)
                {
                    tickFailure = ex;
                }
            })
            {
                IsBackground = true,
                Name = "hook-liveness-tick",
            };

            ticker.Start();

            int staleIteration = -1;
            int staleTicks = 0;

            for (var i = 0; i < Iterations; i++)
            {
                // Re-arm. With silence no longer expected the ticker starts banking evidence
                // again, so every iteration gives the reset below something real to lose.
                PhysicalIdle.SetHookSilenceExpected(false);

                PhysicalIdle.SetHookSilenceExpected(true);

                // The instant that returns, both counters have been zeroed under the lock and
                // every later tick sees _hookSilenceExpected == true, which can only zero them
                // again. So a non-zero reading here can only be a verdict the ticker computed
                // BEFORE the reset and wrote back AFTER it -- the read-modify-write the lock
                // exists to make impossible.
                int ticks = PhysicalIdle.ConsecutiveHookSilenceTicks;
                if (ticks != 0 || PhysicalIdle.DropSuspected)
                {
                    staleIteration = i;
                    staleTicks = ticks;
                    break;
                }
            }

            Volatile.Write(ref running, 0);
            ticker.Join(UnblockedJoinMs);

            if (tickFailure is not null)
            {
                ExceptionDispatchInfo.Capture(tickFailure).Throw();
            }

            Assert.True(
                staleIteration < 0,
                $"A reset at iteration {staleIteration} was overwritten by a tick that had already read the "
                + $"old state: the counter came back as {staleTicks} instead of 0.");
        }
        finally
        {
            Volatile.Write(ref running, 0);
            PhysicalIdle.SetHookSilenceExpected(false);
        }
    }

    // ---------------------------------------------------------------------------------
    // 2026-09-14 incident: nine false alarms in 78 minutes, the first day this detector ran
    // on a real desktop. Everything below this line exists because of that, and every number
    // in it was MEASURED on the machine that produced it, not chosen.
    // ---------------------------------------------------------------------------------

    // The longest false episode carried 120 s of unbroken evidence before a callback finally
    // landed and collapsed the gap. Anything at or under this must not be enough to latch, or
    // that episode ships again.
    private const long LongestObservedFalseEpisodeMs = 120_000;

    [Fact]
    public void ConfirmationWindow_IsLongerThanTheLongestMeasuredFalseAlarm()
    {
        // THE REGRESSION TEST FOR THE INCIDENT. The window is the entire fix: with the system
        // idle clock pinned near zero the rule degenerates into a bare "has the hook fired
        // lately", so how long we wait before believing it is the only lever left.
        //
        // This is deliberately asserted against the SHIPPED constant. Every other test in this
        // file passes requiredTicks as a literal 3, which is why changing the constant from 3
        // to 30 broke nothing and the real window went unguarded through four releases.
        var windowMs = (long)PhysicalIdle.HookSilenceTicksRequired * TrayTickMs;

        Assert.True(
            windowMs > LongestObservedFalseEpisodeMs,
            $"The confirmation window is {windowMs} ms ({PhysicalIdle.HookSilenceTicksRequired} ticks x {TrayTickMs} ms). "
                + $"The longest false alarm measured on a real desktop sustained {LongestObservedFalseEpisodeMs} ms of "
                + "unbroken evidence, so a window this short reports it as a dropped hook again.");
    }

    [Fact]
    public void ConfirmationWindow_StaysFarBelowTheIdleThresholdItProtects()
    {
        // The other side of the same trade. Waiting longer is only free while it stays small
        // against the idle threshold this guard protects; the shortest one the app allows is
        // one minute. Without this, "make the window bigger" is an unbounded answer to every
        // future false alarm, ending at a detector that never fires at all.
        var windowMs = (long)PhysicalIdle.HookSilenceTicksRequired * TrayTickMs;

        Assert.True(
            windowMs <= 300_000,
            $"The confirmation window has grown to {windowMs} ms. Past ~5 minutes it is no longer a "
                + "detector, and the fallback it guards would be wrong for longer than the fault it reports.");
    }

    [Theory]
    [InlineData(24)]
    [InlineData(20)]
    [InlineData(10)]
    public void IsHookDropSuspected_AtTheMeasuredFalseAlarmLength_DoesNotLatch(int ticksOfEvidence)
    {
        // 24 ticks is the 120 s episode, replayed. The evidence is real on every one of these
        // ticks -- Windows saw input, our callback did not run -- and it must still not be
        // enough, because on the machine that produced it the hook was alive the whole time
        // and proved it by firing again.
        Assert.False(
            Suspect(
                systemIdleMs: 0,
                callbackAgeMs: (long)ticksOfEvidence * TrayTickMs,
                consecutiveSuspectTicks: ticksOfEvidence,
                requiredTicks: PhysicalIdle.HookSilenceTicksRequired));
    }

    [Fact]
    public void IsHookDropSuspected_OnceTheFullWindowIsMet_StillLatches()
    {
        // The detector must not have been turned off by widening it. Same shape as above, one
        // tick past the requirement.
        var ticks = PhysicalIdle.HookSilenceTicksRequired;

        Assert.True(
            Suspect(
                systemIdleMs: 0,
                callbackAgeMs: (long)ticks * TrayTickMs,
                consecutiveSuspectTicks: ticks,
                requiredTicks: ticks));
    }

    [Fact]
    public void HookSilenceGapMs_WhenTheCrossCheckIsUnavailable_IsNotANumber()
    {
        // GetSystemIdleMilliseconds returns PositiveInfinity when GetLastInputInfo fails.
        // A missing cross-check must not read as a gap of infinity, which would be evidence.
        Assert.True(double.IsNaN(PhysicalIdle.HookSilenceGapMs(double.PositiveInfinity, NowMs - 60_000, NowMs)));
        Assert.True(double.IsNaN(PhysicalIdle.HookSilenceGapMs(double.NaN, NowMs - 60_000, NowMs)));
        Assert.True(double.IsNaN(PhysicalIdle.HookSilenceGapMs(-1, NowMs - 60_000, NowMs)));
    }

    [Fact]
    public void HookSilenceGapMs_WhenTheHeartbeatIsFresherThanTheLastInput_IsNegative()
    {
        // What a live hook looks like: the callback ran more recently than the input Windows
        // recorded. Negative is a healthy reading, not an error, and the log prints it as-is.
        var gap = PhysicalIdle.HookSilenceGapMs(systemIdleMs: 5_000, lastCallbackMs: NowMs - 100, nowMs: NowMs);

        Assert.True(gap < 0, $"Expected a negative gap for a hook that just fired, got {gap}.");
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(120_000)]
    public void HookSilenceGapMs_WhenTheIdleClockIsPinnedAtZero_CollapsesToTheCallbackAge(long callbackAgeMs)
    {
        // THE DEGENERATION, pinned so nobody has to rediscover it. On a machine whose idle
        // clock never rises -- 239 resets in 150 s on the box that found this -- systemIdleMs
        // is ~0 and the gap IS the callback age. The two-clock cross-check has silently become
        // a one-clock liveness timeout.
        //
        // This is also the proof that "require the gap to be GROWING" cannot rescue it, which
        // is the first idea every reader has. A genuinely dropped hook and a merely quiet one
        // both produce exactly this number, growing at one tick per tick. They are not
        // distinguishable from these two clocks at all; only elapsed time separates them.
        Assert.Equal(callbackAgeMs, PhysicalIdle.HookSilenceGapMs(0, NowMs - callbackAgeMs, NowMs));
    }

    [Theory]
    [InlineData(0L, "0ms")]
    [InlineData(999L, "999ms")]
    [InlineData(-250L, "-250ms")]
    [InlineData(1_000L, "1.0s")]
    [InlineData(120_000L, "120.0s")]
    [InlineData(-10_500L, "-10.5s")]
    [InlineData(long.MinValue, "unavailable")]
    public void FormatMs_RendersEveryCaseTheIncidentLogCanProduce(long valueMs, string expected)
    {
        // long.MinValue is the "no cross-check" sentinel and must become a word: printing
        // "-9223372036854775808ms" in the one line a human reads to decide whether their
        // hooks are broken is worse than printing nothing.
        //
        // Negatives are ordinary and must survive: a negative clock gap is a HEALTHY hook,
        // and rounding it away or rejecting it would hide the most reassuring reading there is.
        Assert.Equal(expected, PhysicalIdle.FormatMs(valueMs));
    }

    [Fact]
    public void FormatMs_UsesInvariantCulture()
    {
        // The same omission in LogCleanupOutcome was a real finding in this repo. On a
        // comma-decimal machine an un-invariant format writes "1,5s", and the diagnostic this
        // whole change exists to add becomes unparseable exactly where it is needed.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5s", PhysicalIdle.FormatMs(1_500));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
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

    // Same arrangement as Suspect, advancing the counter instead of reading the verdict. The
    // defaults are the evidence case, because that is the one most of these tests want.
    private static int NextTicks(
        double systemIdleMs,
        long callbackAgeMs,
        int currentTicks,
        int maxTicks = 3) =>
        PhysicalIdle.NextHookSilenceTicks(
            systemIdleMs,
            NowMs - callbackAgeMs,
            NowMs,
            currentTicks,
            maxTicks);

    private static int NextTicks(int currentTicks, int maxTicks = 3) =>
        NextTicks(EvidenceSystemIdleMs, EvidenceCallbackAgeMs, currentTicks, maxTicks);

    /// <summary>
    /// The mechanical form of the exclusion proof: take the product's liveness lock on this
    /// thread, run <paramref name="lockedCall"/> on another, and require that it is still
    /// running when the window expires and that it finishes once the lock is released.
    /// </summary>
    /// <remarks>
    /// "Still running after <see cref="BlockedProofMs"/>" is weak evidence on its own -- a
    /// thread that was never scheduled looks identical -- so the worker signals before it
    /// enters the call and that signal is asserted too. The two callback tests close the rest
    /// of the gap from the other side: they run product code on a background thread under the
    /// SAME held lock and require it to finish, so "background threads do not get to run in
    /// this test process" cannot explain both results at once.
    /// </remarks>
    private static void AssertWaitsForTheLivenessLock(string what, Action lockedCall)
    {
        using var worker = new BackgroundCall(lockedCall);
        var livenessLock = PhysicalIdle.HookLivenessLock;

        Monitor.Enter(livenessLock);
        try
        {
            worker.Start();

            Assert.True(
                worker.WaitUntilEntered(UnblockedJoinMs),
                $"{what} never reached the product call, so nothing about exclusion was tested.");

            Assert.False(
                worker.WaitUntilFinished(BlockedProofMs),
                $"{what} ran to completion while the hook-liveness lock was held, so it is not taking that lock "
                + "and a reset can still be overwritten by a verdict computed before it.");
        }
        finally
        {
            Monitor.Exit(livenessLock);
        }

        Assert.True(
            worker.WaitUntilFinished(UnblockedJoinMs),
            $"{what} did not finish within {UnblockedJoinMs} ms of the hook-liveness lock being released.");
        worker.RethrowFailure();
    }

    /// <summary>
    /// The opposite assertion, for the code that must never wait on anything: hold the
    /// liveness lock and require <paramref name="call"/> to complete anyway.
    /// </summary>
    private static void AssertRunsWithoutTheLivenessLock(string what, Action call)
    {
        using var worker = new BackgroundCall(call);
        var livenessLock = PhysicalIdle.HookLivenessLock;

        Monitor.Enter(livenessLock);
        try
        {
            worker.Start();

            Assert.True(
                worker.WaitUntilFinished(CallbackJoinMs),
                $"{what} had not returned {CallbackJoinMs} ms into a window where the hook-liveness lock was "
                + "held by another thread, which means it is waiting on a lock it must never touch.");
        }
        finally
        {
            Monitor.Exit(livenessLock);
        }

        worker.RethrowFailure();
    }

    /// <summary>
    /// One product call on a background thread, with the two things an exclusion test needs
    /// from it: a signal that the thread really was scheduled and reached the call, and a
    /// captured exception, because an assertion that throws on a background thread takes the
    /// whole test process down instead of failing one test.
    /// </summary>
    private sealed class BackgroundCall : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _entered = new(initialState: false);
        private Exception? _failure;

        internal BackgroundCall(Action body)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _entered.Set();
                    body();
                }
                catch (Exception ex)
                {
                    _failure = ex;
                }
            })
            {
                IsBackground = true,
                Name = "hook-liveness-exclusion",
            };
        }

        internal void Start() => _thread.Start();

        internal bool WaitUntilEntered(int millisecondsTimeout) => _entered.Wait(millisecondsTimeout);

        internal bool WaitUntilFinished(int millisecondsTimeout) => _thread.Join(millisecondsTimeout);

        /// <summary>Republishes whatever the worker threw, on the thread xunit is watching.</summary>
        internal void RethrowFailure()
        {
            if (_failure is not null)
            {
                ExceptionDispatchInfo.Capture(_failure).Throw();
            }
        }

        public void Dispose() => _entered.Dispose();
    }
}

/// <summary>
/// Covers who owns <c>_gamepadPollInProgress</c>, the flag that keeps two <c>PollGamepads</c>
/// callbacks out of the four-slot <c>_gpConnected</c> / <c>_gpLastPacket</c> /
/// <c>_gpLastState</c> arrays at the same time.
/// <para>
/// The rule is one line long and the tests below are all about its edges: the callback that
/// takes the flag is the callback that clears it, and nothing on the start/stop path may clear
/// it on that callback's behalf. The path that got this wrong was the one that had just logged
/// that the callback was still running.
/// </para>
/// <para>
/// No controller is needed. XInput answers ERROR_DEVICE_NOT_CONNECTED for empty slots, which is
/// a complete poll as far as the guard is concerned, and the two tests that must not reach a
/// poll at all are arranged so that they cannot.
/// </para>
/// </summary>
public sealed class GamepadPollGuardTests
{
    private const string PollInProgressField = "_gamepadPollInProgress";
    private const string StopRequestedField = "_gamepadStopRequested";
    private const string TimerField = "_gamepadTimer";
    private const string ConnectedField = "_gpConnected";
    private const string NextPollField = "_gpNextPollMilliseconds";
    private const string BackoffField = "_gpDisconnectedBackoffMs";

    [Fact]
    public void StopGamepadTimer_WhileACallbackStillHoldsTheGuard_LeavesTheGuardWithThatCallback()
    {
        // The case worth changing this for: XInputGetState is blocked in a driver, the bounded
        // wait inside StopGamepadTimer expires, and the method LOGS that the callback is still
        // running -- and then used to zero that callback's guard anyway on its way out. The
        // next callback in walks straight into arrays the hung one is still writing.
        using var gamepad = new GamepadPollStateScope(gamepadEnabled: false);

        // A stand-in timer rather than a started one. StopGamepadTimer only calls Change and
        // Dispose on this object, while a real timer would run PollGamepads on a thread pool
        // thread and move the flag below for reasons this test did not arrange.
        using var standInTimer = new System.Threading.Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        PhysicalIdle.WriteField(TimerField, standInTimer);

        // What a hung callback leaves behind: it took the guard and has not given it back.
        PhysicalIdle.WriteField(PollInProgressField, 1);

        StopGamepadTimer();

        // Witnesses first. Both are written past the "no timer, nothing to do" early return, so
        // together they prove the teardown really ran and the assertion below is about the
        // guard rather than about a call that returned on its first line.
        Assert.Null(PhysicalIdle.ReadField<object?>(TimerField));
        Assert.Equal(1, PhysicalIdle.ReadField<int>(StopRequestedField));

        Assert.Equal(
            1,
            PhysicalIdle.ReadField<int>(PollInProgressField));
    }

    [Fact]
    public void StartGamepadTimer_WhileAnOrphanedCallbackHoldsTheGuard_DoesNotHandItToTheNewTimer()
    {
        // The same mistake on the way back in, and the pair is what makes it reachable: a user
        // who toggles gamepad support off during a driver hang and straight back on. Clearing
        // the guard here admits a fresh callback beside the orphan, and the orphan's own
        // finally then clears the fresh one's guard and admits a third.
        //
        // Gamepad support is off for the duration so the timer started below cannot take the
        // guard itself. That matters: with the flag cleared, a real callback would re-take it
        // microseconds later and let this assertion pass on timing rather than on behaviour.
        using var gamepad = new GamepadPollStateScope(gamepadEnabled: false);

        PhysicalIdle.WriteField(PollInProgressField, 1);

        StartGamepadTimer();

        // Witness: a timer really was created, so what follows is about the body of the method
        // and not about the "already running" early return at the top of it.
        Assert.NotNull(PhysicalIdle.ReadField<object?>(TimerField));

        Assert.Equal(
            1,
            PhysicalIdle.ReadField<int>(PollInProgressField));
    }

    [Fact]
    public void PollGamepads_WhenAnotherCallbackHoldsTheGuard_ReturnsWithoutClearingIt()
    {
        // Re-entry, from the timer's own callback. A bounced callback returns ABOVE the try, so
        // it never reaches the finally: it cannot clear a guard it does not own, and the
        // callback that does own it is still the one that will give it back.
        using var gamepad = new GamepadPollStateScope(gamepadEnabled: true);
        PhysicalIdle.WriteField(PollInProgressField, 1);
        ClearSlotSchedule();

        PollGamepads();

        Assert.Equal(1, PhysicalIdle.ReadField<int>(PollInProgressField));

        // And it got nowhere near the slots. The companion test below fills this in from an
        // identical arrangement, which is what makes the emptiness here mean something.
        Assert.False(
            EverySlotWasVisited(),
            "A callback that bounced off the guard still touched the controller arrays.");
    }

    [Fact]
    public void PollGamepads_WhenNothingHoldsTheGuard_TakesItAndGivesItBack()
    {
        // The other half of the rule, and the non-degeneracy witness for the test above: with
        // the guard free the same call really does run a full poll, so "the flag survived" up
        // there is a fact about the guard and not about some earlier return this suite cannot
        // see. It also pins the only clear of the flag that is allowed to exist.
        using var gamepad = new GamepadPollStateScope(gamepadEnabled: true);
        PhysicalIdle.WriteField(PollInProgressField, 0);
        ClearSlotSchedule();

        PollGamepads();

        Assert.True(
            EverySlotWasVisited(),
            "The poll never reached the slots, so nothing about taking the guard was exercised.");
        Assert.Equal(0, PhysicalIdle.ReadField<int>(PollInProgressField));
    }

    private static void StartGamepadTimer() => Product.CallStatic(PhysicalIdle.TypeName, "StartGamepadTimer");

    private static void StopGamepadTimer() => Product.CallStatic(PhysicalIdle.TypeName, "StopGamepadTimer");

    /// <summary>Invokes the timer callback directly, with the <c>null</c> state the timer passes it.</summary>
    private static void PollGamepads() =>
        Product.CallStatic(PhysicalIdle.TypeName, "PollGamepads", new object?[] { null });

    private static bool GamepadEnabled
    {
        get => (bool)Product.PropertyNamed(PhysicalIdle.TypeName, nameof(GamepadEnabled)).GetValue(null)!;
        set => Product.PropertyNamed(PhysicalIdle.TypeName, nameof(GamepadEnabled)).SetValue(null, value);
    }

    /// <summary>
    /// Puts the four slots in the state a fresh <c>StartGamepadTimer</c> leaves them in: nothing
    /// connected and nothing scheduled, so the next poll has to visit every slot instead of
    /// skipping one on a backoff an earlier test left behind.
    /// </summary>
    private static void ClearSlotSchedule()
    {
        Array.Clear(PhysicalIdle.ReadField<Array>(ConnectedField));
        Array.Clear(PhysicalIdle.ReadField<Array>(NextPollField));
        Array.Clear(PhysicalIdle.ReadField<Array>(BackoffField));
    }

    /// <summary>
    /// True once a poll has looked at every slot. A connected pad leaves <c>_gpConnected</c>
    /// set; an empty one leaves a retry scheduled. A slot nothing looked at has neither, which
    /// is exactly what <see cref="ClearSlotSchedule"/> arranges.
    /// </summary>
    private static bool EverySlotWasVisited()
    {
        var connected = (bool[])PhysicalIdle.ReadField<Array>(ConnectedField);
        var nextPollMs = (long[])PhysicalIdle.ReadField<Array>(NextPollField);

        for (var i = 0; i < connected.Length; i++)
        {
            if (!connected[i] && nextPollMs[i] == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Saves every piece of gamepad state these tests move and puts it back, including when the
    /// body throws — and stops any timer a test started, because a stray poll running on into
    /// the next test would write the idle clock from nowhere.
    /// </summary>
    private sealed class GamepadPollStateScope : IDisposable
    {
        // Restored by copying elements back rather than by reassigning the fields: two of the
        // five are `static readonly`, and FieldInfo.SetValue throws FieldAccessException on an
        // initonly static. The contents are what the product mutates in any case.
        private static readonly string[] SlotArrayFields =
        {
            ConnectedField,
            "_gpLastPacket",
            "_gpLastState",
            NextPollField,
            BackoffField,
        };

        private readonly int _previousPollInProgress;
        private readonly int _previousStopRequested;
        private readonly bool _previousGamepadEnabled;
        private readonly Array[] _previousSlots;

        internal GamepadPollStateScope(bool gamepadEnabled)
        {
            // Nothing else in this suite starts gamepad polling, so a live timer here would mean
            // an earlier test left one running -- and every arrangement below would then be
            // racing a real callback instead of describing one.
            Assert.Null(PhysicalIdle.ReadField<object?>(TimerField));

            _previousPollInProgress = PhysicalIdle.ReadField<int>(PollInProgressField);
            _previousStopRequested = PhysicalIdle.ReadField<int>(StopRequestedField);
            _previousGamepadEnabled = GamepadEnabled;
            _previousSlots = Array.ConvertAll(
                SlotArrayFields,
                fieldName => (Array)PhysicalIdle.ReadField<Array>(fieldName).Clone());

            GamepadEnabled = gamepadEnabled;
            PhysicalIdle.WriteField(StopRequestedField, 0);
        }

        public void Dispose()
        {
            // First, before anything is restored: a timer still ticking would poll against
            // half-restored state.
            StopGamepadTimer();

            for (var i = 0; i < SlotArrayFields.Length; i++)
            {
                Array.Copy(
                    _previousSlots[i],
                    PhysicalIdle.ReadField<Array>(SlotArrayFields[i]),
                    _previousSlots[i].Length);
            }

            GamepadEnabled = _previousGamepadEnabled;
            PhysicalIdle.WriteField(StopRequestedField, _previousStopRequested);
            PhysicalIdle.WriteField(PollInProgressField, _previousPollInProgress);
        }
    }
}
