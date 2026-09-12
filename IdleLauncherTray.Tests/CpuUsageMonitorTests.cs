using System;
using System.Threading;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// The sample source is <c>GetSystemTimes</c>, a static P/Invoke with no injection point,
/// so these tests use the machine's real CPU counters. What they *can* control is the
/// monitor's own memory of the previous sample, which is where all the interesting
/// arithmetic lives: writing a previous-counter value reproduces a counter regression, a
/// clock jump, or an idle-time anomaly exactly as the running app would see it.
/// </summary>
public sealed class CpuUsageMonitorTests
{
    [Fact]
    public void TryNextValue_OnTheFirstCall_PrimesAndReportsNoValue()
    {
        var monitor = new CpuUsageMonitorProxy();

        Assert.False(monitor.Initialized);

        var produced = monitor.TryNextValue(out var percent);

        Assert.False(produced);
        Assert.Equal(0f, percent);
        Assert.True(monitor.Initialized);
        Assert.True(monitor.PreviousKernel > 0, "Priming should have captured the current kernel time.");
    }

    [Fact]
    public void TryNextValue_AfterPriming_ProducesAPercentageInRange()
    {
        var monitor = new CpuUsageMonitorProxy();
        monitor.TryNextValue(out _);

        Thread.Sleep(120);

        Assert.True(monitor.TryNextValue(out var percent), "A sample taken 120ms later should produce a reading.");
        Assert.False(float.IsNaN(percent), "CPU usage came back as NaN.");
        Assert.InRange(percent, 0f, 100f);
    }

    [Fact]
    public void TryNextValue_WithinASingleClockTick_ReportsNoValueRatherThanDividingByZero()
    {
        // GetSystemTimes only advances on the system clock tick (~15.6ms), so back-to-back
        // calls routinely see identical counters and a zero total. Without the guard that
        // is a 0/0 double division, which yields NaN and is reported as a real reading.
        var monitor = new CpuUsageMonitorProxy();
        monitor.TryNextValue(out _);

        var suppressed = 0;

        for (var i = 0; i < 2000; i++)
        {
            if (monitor.TryNextValue(out var percent))
            {
                Assert.False(float.IsNaN(percent), "CPU usage came back as NaN.");
                Assert.InRange(percent, 0f, 100f);
            }
            else
            {
                suppressed++;
                Assert.Equal(0f, percent);
            }
        }

        Assert.True(suppressed > 0, "Expected at least one pair of samples inside the same clock tick.");
    }

    [Fact]
    public void TryNextValue_WhenTheIdleCounterGoesBackwards_RebaselinesAndSkipsTheSample()
    {
        AssertRegressionIsAbsorbed(monitor => monitor.PreviousIdle = ulong.MaxValue, monitor => monitor.PreviousIdle);
    }

    [Fact]
    public void TryNextValue_WhenTheKernelCounterGoesBackwards_RebaselinesAndSkipsTheSample()
    {
        AssertRegressionIsAbsorbed(monitor => monitor.PreviousKernel = ulong.MaxValue, monitor => monitor.PreviousKernel);
    }

    [Fact]
    public void TryNextValue_WhenTheUserCounterGoesBackwards_RebaselinesAndSkipsTheSample()
    {
        AssertRegressionIsAbsorbed(monitor => monitor.PreviousUser = ulong.MaxValue, monitor => monitor.PreviousUser);
    }

    private static void AssertRegressionIsAbsorbed(
        Action<CpuUsageMonitorProxy> regressTheCounter,
        Func<CpuUsageMonitorProxy, ulong> readTheCounter)
    {
        var monitor = new CpuUsageMonitorProxy();
        monitor.TryNextValue(out _);

        // A previous value above anything the counter can report is what a wrap, a clock
        // change or a misbehaving driver looks like from in here. Unguarded, the unsigned
        // subtraction underflows and the delta becomes astronomically large.
        regressTheCounter(monitor);
        Thread.Sleep(30);

        var produced = monitor.TryNextValue(out var percent);

        Assert.False(produced, "A sample taken after a counter regression must be discarded, not reported.");
        Assert.Equal(0f, percent);
        Assert.NotEqual(ulong.MaxValue, readTheCounter(monitor));
        Assert.True(readTheCounter(monitor) > 0, "The regressed counter should have been re-baselined onto the fresh sample.");
    }

    [Fact]
    public void TryNextValue_AfterARegression_StartsReportingAgainOnTheNextSample()
    {
        var monitor = new CpuUsageMonitorProxy();
        monitor.TryNextValue(out _);
        monitor.PreviousKernel = ulong.MaxValue;
        Thread.Sleep(30);
        Assert.False(monitor.TryNextValue(out _));

        Thread.Sleep(120);

        Assert.True(monitor.TryNextValue(out var percent), "The monitor should recover on the sample after a re-baseline.");
        Assert.InRange(percent, 0f, 100f);
    }

    [Fact]
    public void TryNextValue_WhenIdleTimeExceedsTheBusyWindow_ReportsZeroRatherThanUnderflowing()
    {
        // kernel time already includes idle time, so busy = total - idle. If a sample ever
        // shows more idle than total, the unsigned subtraction underflows into a colossal
        // "busy" figure and the app reports a pegged CPU on a machine doing nothing.
        // Zeroing the previous idle reading reproduces that: the idle delta becomes the
        // machine's entire accumulated idle time, which dwarfs a 60ms busy window.
        var monitor = new CpuUsageMonitorProxy();
        monitor.TryNextValue(out _);

        monitor.PreviousIdle = 0;
        Thread.Sleep(60);

        Assert.True(monitor.TryNextValue(out var percent));
        Assert.Equal(0f, percent);
    }

    [Theory]
    [InlineData(0u, 0u, 0UL)]
    [InlineData(0u, 1u, 1UL)]
    [InlineData(1u, 0u, 4294967296UL)]
    [InlineData(0u, uint.MaxValue, 4294967295UL)]
    [InlineData(1u, uint.MaxValue, 8589934591UL)]
    [InlineData(uint.MaxValue, uint.MaxValue, ulong.MaxValue)]
    [InlineData(0x01234567u, 0x89ABCDEFu, 0x0123456789ABCDEFUL)]
    public void ToUInt64_CombinesTheTwoHalvesOfAFileTime(uint high, uint low, ulong expected)
    {
        Assert.Equal(expected, CpuUsageMonitorProxy.ToUInt64(high, low));
    }

    [Fact]
    public void ToUInt64_DoesNotSignExtendTheLowHalf()
    {
        // dwLowDateTime is a uint; widening it through a signed type would turn any value
        // with the top bit set into 0xFFFFFFFF________ and wreck every delta after it.
        Assert.Equal(0x80000000UL, CpuUsageMonitorProxy.ToUInt64(0u, 0x80000000u));
        Assert.Equal(0xFFFFFFFFUL, CpuUsageMonitorProxy.ToUInt64(0u, 0xFFFFFFFFu));
    }
}
