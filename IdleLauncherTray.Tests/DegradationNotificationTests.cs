using System.Collections.Generic;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// The per-reason rate limit on the degraded-state balloon.
/// </summary>
/// <remarks>
/// The limit used to be a single global timestamp, which made it reason-blind: a fault that had
/// just cleared bought five minutes of silence for a DIFFERENT fault arriving inside its window,
/// so the user was told about the first problem and never about the second. That is exactly the
/// escalation the reason-string dedupe was added to catch, thrown away one step later.
/// </remarks>
public sealed class DegradationNotificationTests
{
    private const int FiveMinutes = 5 * 60 * 1000;

    [Fact]
    public void ShouldNotifyDegradation_ForAReasonNeverSeenBefore_Notifies()
    {
        var history = new Dictionary<string, long>();

        Assert.True(DegradationNotifier.ShouldNotify(history, "keyboard hook not installed", 0, FiveMinutes));
    }

    [Fact]
    public void ShouldNotifyDegradation_ForADifferentFaultInsideTheFirstFaultsWindow_StillNotifies()
    {
        // THE regression test for this bug. Fault A ballooned at t=0; two minutes later a
        // genuinely different fault B appears. Under the old global timestamp B was logged and
        // never shown.
        var history = new Dictionary<string, long> { ["lock detection off"] = 0 };

        Assert.True(DegradationNotifier.ShouldNotify(history, "CPU sampling stuck", 2 * 60 * 1000, FiveMinutes));
    }

    [Fact]
    public void ShouldNotifyDegradation_ForTheSameFaultInsideItsOwnWindow_HoldsItBack()
    {
        // The flap protection surviving: this is why the limit exists at all.
        var history = new Dictionary<string, long> { ["CPU sampling stuck"] = 0 };

        Assert.False(DegradationNotifier.ShouldNotify(history, "CPU sampling stuck", 60 * 1000, FiveMinutes));
    }

    [Theory]
    [InlineData(FiveMinutes - 1, false)]
    [InlineData(FiveMinutes, true)]      // exactly at the boundary the window has elapsed
    [InlineData(FiveMinutes + 1, true)]
    public void ShouldNotifyDegradation_AtTheWindowBoundary_ElapsesInclusively(long elapsedMs, bool expected)
    {
        var history = new Dictionary<string, long> { ["CPU sampling stuck"] = 0 };

        Assert.Equal(expected, DegradationNotifier.ShouldNotify(history, "CPU sampling stuck", elapsedMs, FiveMinutes));
    }

    [Fact]
    public void ShouldNotifyDegradation_ForAFaultFlappingBetweenTwoReasons_NotifiesEachAtMostOncePerWindow()
    {
        // Drives ten minutes of five-second ticks alternating between two faults. The old global
        // limit allowed 1; a limit removed entirely would allow 120. Per-reason allows exactly
        // four: each reason once in each of the two windows.
        var history = new Dictionary<string, long>();
        var reasons = new[] { "lock detection off", "CPU sampling stuck" };
        var notified = 0;

        for (var tick = 0; tick < 120; tick++)
        {
            var nowMs = tick * 5_000L;
            var reason = reasons[tick % 2];

            if (DegradationNotifier.ShouldNotify(history, reason, nowMs, FiveMinutes))
            {
                DegradationNotifier.Record(history, reason, nowMs, 8);
                notified++;
            }
        }

        Assert.Equal(4, notified);
    }

    [Fact]
    public void RecordDegradationNotification_ForMoreDistinctReasonsThanTheCap_StaysBounded()
    {
        // The reasons are compile-time literals today, so the cap is unreachable. It exists so a
        // future caller that interpolates a reason cannot turn a months-long process into a leak.
        var history = new Dictionary<string, long>();

        for (var i = 0; i < 100; i++)
        {
            DegradationNotifier.Record(history, $"synthetic reason {i}", i, 8);
            Assert.True(history.Count <= 8, $"history grew to {history.Count} entries");
        }
    }

    [Fact]
    public void RecordDegradationNotification_ForAReasonAlreadyTracked_OverwritesRatherThanGrowing()
    {
        var history = new Dictionary<string, long>();

        DegradationNotifier.Record(history, "CPU sampling stuck", 1000, 8);
        DegradationNotifier.Record(history, "CPU sampling stuck", 2000, 8);

        Assert.Single(history);
        Assert.Equal(2000, history["CPU sampling stuck"]);
    }
}
