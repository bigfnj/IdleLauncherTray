using System;
using System.Reflection;

namespace IdleLauncherTray.Tests.Sut;

/// <summary>
/// Mirror of the product's <c>IdleLauncherTray.CpuUsageMonitor</c>.
/// <para>
/// The counter source is <c>GetSystemTimes</c>, a static P/Invoke with no seam, so the
/// samples themselves cannot be faked. What the monitor *remembers* between samples is
/// private instance state, and that is reachable: writing <c>_prevIdle</c> /
/// <c>_prevKernel</c> / <c>_prevUser</c> puts the object into the exact state a counter
/// regression or a clock jump would produce, and the next real sample then has to be
/// handled by the branch under test.
/// </para>
/// </summary>
internal sealed class CpuUsageMonitorProxy
{
    private static readonly Type MonitorType = Product.TypeNamed("CpuUsageMonitor");
    private static readonly MethodInfo TryNextValueMethod = Product.MethodNamed("CpuUsageMonitor", "TryNextValue");
    private static readonly MethodInfo ToUInt64Method = Product.MethodNamed("CpuUsageMonitor", "ToUInt64");

    private static readonly MethodInfo DescribeLastSampleFailureMethod =
        Product.MethodNamed("CpuUsageMonitor", "DescribeLastSampleFailure");

    private static readonly Type FileTimeType =
        MonitorType.GetNestedType("FILETIME", BindingFlags.NonPublic)
        ?? throw new MissingMemberException(MonitorType.FullName, "FILETIME");

    private readonly object _instance = Activator.CreateInstance(MonitorType, nonPublic: true)!;

    internal bool TryNextValue(out float percent)
    {
        var arguments = new object?[] { 0f };
        var produced = (bool)Product.Call(TryNextValueMethod, _instance, arguments)!;
        percent = (float)arguments[0]!;
        return produced;
    }

    internal bool Initialized
    {
        get => (bool)Product.FieldNamed(MonitorType, "_initialized").GetValue(_instance)!;
        set => Product.FieldNamed(MonitorType, "_initialized").SetValue(_instance, value);
    }

    /// <summary>
    /// The recorded Win32 error from the last <c>GetSystemTimes</c> call, or null when it
    /// succeeded. Boxed as a plain <c>int</c> by reflection when it has a value, so the cast
    /// goes through <c>int?</c> rather than a direct unbox.
    /// </summary>
    internal int? LastSampleError =>
        (int?)Product.PropertyNamed("CpuUsageMonitor", nameof(LastSampleError)).GetValue(_instance);

    internal string DescribeLastSampleFailure() =>
        (string)Product.Call(DescribeLastSampleFailureMethod, _instance)!;

    /// <summary>
    /// The product's pure classifier. Reached directly because the failure it describes --
    /// <c>GetSystemTimes</c> itself returning false -- cannot be provoked on a working machine,
    /// so driving it through an instance would leave two of its three branches untested.
    /// </summary>
    internal static string DescribeSampleFailure(int? errorCode) =>
        (string)Product.CallStatic("CpuUsageMonitor", nameof(DescribeSampleFailure), errorCode)!;

    internal ulong PreviousIdle
    {
        get => ReadCounter("_prevIdle");
        set => WriteCounter("_prevIdle", value);
    }

    internal ulong PreviousKernel
    {
        get => ReadCounter("_prevKernel");
        set => WriteCounter("_prevKernel", value);
    }

    internal ulong PreviousUser
    {
        get => ReadCounter("_prevUser");
        set => WriteCounter("_prevUser", value);
    }

    /// <summary>Calls the product's private FILETIME-to-ulong conversion.</summary>
    internal static ulong ToUInt64(uint high, uint low)
    {
        var fileTime = Activator.CreateInstance(FileTimeType)!;
        Product.FieldNamed(FileTimeType, "dwHighDateTime").SetValue(fileTime, high);
        Product.FieldNamed(FileTimeType, "dwLowDateTime").SetValue(fileTime, low);
        return (ulong)Product.Call(ToUInt64Method, null, fileTime)!;
    }

    private ulong ReadCounter(string fieldName) =>
        (ulong)Product.FieldNamed(MonitorType, fieldName).GetValue(_instance)!;

    private void WriteCounter(string fieldName, ulong value) =>
        Product.FieldNamed(MonitorType, fieldName).SetValue(_instance, value);
}
