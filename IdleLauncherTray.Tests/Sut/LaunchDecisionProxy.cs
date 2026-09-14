using System;
using System.Reflection;

namespace IdleLauncherTray.Tests.Sut;

/// <summary>
/// Mirror of the product's <c>LaunchDecision.Evaluate</c> and its <c>LaunchInputs</c> struct.
/// </summary>
/// <remarks>
/// Both types are <c>internal</c>, so the suite reaches them the same way it reaches everything
/// else: reflection through <see cref="Product"/>, with no accessibility change in the shipping
/// assembly. <c>LaunchInputs</c> has init-only properties, which <see cref="PropertyInfo.SetValue"/>
/// can still write — <c>init</c> is a compile-time modreq on the setter, not a runtime one — so a
/// boxed instance can be populated field by field and handed straight to the method.
/// </remarks>
internal static class LaunchDecision
{
    private static readonly Type InputsType = Product.TypeNamed("LaunchInputs");
    private static readonly Type ResultType = Product.TypeNamed("LaunchDecisionResult");

    private static readonly MethodInfo EvaluateMethod =
        Product.MethodNamed("LaunchDecision", "Evaluate");

    /// <summary>
    /// Evaluates one tick. Defaults describe a healthy, armed, ready-to-launch machine so a test
    /// only has to name the one thing it is varying.
    /// </summary>
    internal static LaunchDecisionResultProxy Evaluate(
        string targetPath = "C:\\tools\\app.exe",
        bool hasTarget = true,
        bool targetSupported = true,
        bool targetExists = true,
        int idleSeconds = 600,
        int requiredIdleSeconds = 300,
        bool cpuSampleValid = true,
        double cpuPercent = 1,
        int cpuThresholdPercent = 10,
        bool workstationLocked = false,
        bool allowLaunchWhileLocked = false,
        DateTime? lastLaunchUtc = null,
        DateTime? nowUtc = null,
        int minLaunchCooldownSeconds = 10)
    {
        var inputs = Activator.CreateInstance(InputsType)!;

        Set(inputs, "TargetPath", targetPath);
        Set(inputs, "HasTarget", hasTarget);
        Set(inputs, "TargetSupported", targetSupported);
        Set(inputs, "TargetExists", targetExists);
        Set(inputs, "IdleSeconds", idleSeconds);
        Set(inputs, "RequiredIdleSeconds", requiredIdleSeconds);
        Set(inputs, "CpuSampleValid", cpuSampleValid);
        Set(inputs, "CpuPercent", cpuPercent);
        Set(inputs, "CpuThresholdPercent", cpuThresholdPercent);
        Set(inputs, "WorkstationLocked", workstationLocked);
        Set(inputs, "AllowLaunchWhileLocked", allowLaunchWhileLocked);
        Set(inputs, "LastLaunchUtc", lastLaunchUtc);
        Set(inputs, "NowUtc", nowUtc ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        Set(inputs, "MinLaunchCooldownSeconds", minLaunchCooldownSeconds);

        var result = EvaluateMethod.Invoke(null, new[] { inputs })!;
        return new LaunchDecisionResultProxy(result, ResultType);
    }

    private static void Set(object inputs, string property, object? value) =>
        InputsType.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(inputs, value);
}

/// <summary>
/// The tick's CPU-sampling bookkeeping on <c>TrayAppContext</c>: the pure counter advance and the
/// two constants whose relationship the comment on it depends on.
/// </summary>
internal static class TickCpuSampling
{
    private const string TypeName = "TrayAppContext";

    internal static int MaxConsecutiveInvalidCpuSamples =>
        Product.ReadConst<int>(TypeName, nameof(MaxConsecutiveInvalidCpuSamples));

    internal static int CheckIntervalSeconds =>
        Product.ReadConst<int>(TypeName, nameof(CheckIntervalSeconds));

    internal static int NextConsecutiveInvalidCpuSamples(int current, bool sampleValid, int maxSamples) =>
        (int)Product.CallStatic(TypeName, nameof(NextConsecutiveInvalidCpuSamples), current, sampleValid, maxSamples)!;
}

/// <summary>Mirror of the product's <c>IdleLauncherTray.AppInfo</c>.</summary>
internal static class AppInfo
{
    private const string TypeName = "AppInfo";

    internal static string VersionDisplay =>
        Product.ReadStaticProperty<string>(TypeName, nameof(VersionDisplay));
}

/// <summary>
/// Mirror of the degraded-state balloon's per-reason rate limit on <c>TrayAppContext</c>.
/// </summary>
internal static class DegradationNotifier
{
    private const string TypeName = "TrayAppContext";

    internal static int DegradationBalloonMinIntervalMs =>
        Product.ReadConst<int>(TypeName, nameof(DegradationBalloonMinIntervalMs));

    internal static int MaxTrackedDegradationReasons =>
        Product.ReadConst<int>(TypeName, nameof(MaxTrackedDegradationReasons));

    internal static bool ShouldNotify(
        System.Collections.Generic.Dictionary<string, long> history, string reason, long nowMs, int minIntervalMs) =>
        (bool)Product.CallStatic(TypeName, "ShouldNotifyDegradation", history, reason, nowMs, minIntervalMs)!;

    internal static void Record(
        System.Collections.Generic.Dictionary<string, long> history, string reason, long nowMs, int maxTrackedReasons) =>
        Product.CallStatic(TypeName, "RecordDegradationNotification", history, reason, nowMs, maxTrackedReasons);
}

internal sealed class LaunchDecisionResultProxy
{
    private readonly object _instance;
    private readonly Type _type;

    internal LaunchDecisionResultProxy(object instance, Type type)
    {
        _instance = instance;
        _type = type;
    }

    internal LaunchEvaluationProxy Evaluation =>
        new(Read<object>(nameof(Evaluation))!);

    internal DateTime? RebaselinedLastLaunchUtc => Read<DateTime?>(nameof(RebaselinedLastLaunchUtc));

    internal double ClockWarpSeconds => Read<double>(nameof(ClockWarpSeconds));

    private T? Read<T>(string property) =>
        (T?)_type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_instance);
}
